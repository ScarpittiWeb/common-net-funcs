using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CommonNetFuncs.EFCore;
using FastExpressionCompiler;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BenchmarkSuite;

// Data-layer (DeepClone / FastMap / Sql.* / EFCore) performance candidates.
// Each class pits a verbatim private copy of the current library implementation ("Original", baseline) against the proposed replacement(s).
// PdVerify.Run() asserts that Original and Optimized produce identical results on edge-case inputs.

#region DeepClone

/// <summary>DeepClone per-object compiled-function lookup: wrapper alloc + read lock + LRU write lock per object vs single GetOrAdd vs call-scoped type memo.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdDeepCloneCacheLookupBenchmarks
{
	private object target = null!;

	[Params("Complex", "Nested50", "Dict50")]
	public string Scenario { get; set; } = "Complex";

	[GlobalSetup]
	public void Setup()
	{
		// Populate each variant's cache with ~50 other types so LRU move-to-most-recently-used cost is realistic
		foreach (object warm in PdDeepCloneData.CreateWarmupObjects())
		{
			PdDeepCloneOriginal.DeepClone(warm);
			PdDeepCloneOptimized.DeepClone(warm);
			PdDeepCloneOptimizedMemo.DeepClone(warm);
		}
		target = PdDeepCloneData.Create(Scenario);
		Original();
		Optimized_SingleLockLookup();
		Optimized_CallScopedMemo();
	}

	// All variants use the original comparer so only the lookup path differs
	[Benchmark(Baseline = true)]
	public object? Original()
	{
		return PdDeepCloneOriginal.DeepClone(target, new Dictionary<object, object>(new PdOriginalReferenceEqualityComparer()));
	}

	[Benchmark]
	public object? Optimized_SingleLockLookup()
	{
		return PdDeepCloneOptimized.DeepClone(target, new Dictionary<object, object>(new PdOriginalReferenceEqualityComparer()));
	}

	[Benchmark]
	public object? Optimized_CallScopedMemo()
	{
		return PdDeepCloneOptimizedMemo.DeepClone(target, new Dictionary<object, object>(new PdOriginalReferenceEqualityComparer()));
	}
}

/// <summary>DeepClone reference-tracking comparer: virtual obj.GetHashCode() (deep for records / ValueType hashing for boxed structs) vs RuntimeHelpers.GetHashCode.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdDeepCloneReferenceComparerBenchmarks
{
	private object target = null!;

	[Params("Nested50", "Dict50", "RecordChain40")]
	public string Scenario { get; set; } = "Nested50";

	[GlobalSetup]
	public void Setup()
	{
		target = PdDeepCloneData.Create(Scenario);
		Original();
		Optimized_RuntimeHelpersHash();
	}

	[Benchmark(Baseline = true)]
	public object? Original()
	{
		return PdDeepCloneOriginal.DeepClone(target);
	}

	[Benchmark]
	public object? Optimized_RuntimeHelpersHash()
	{
		return PdDeepCloneOriginal.DeepClone(target, new Dictionary<object, object>(PdFastReferenceEqualityComparer.Instance));
	}
}

public sealed record PdCloneRecord(int Id, string Name, PdCloneRecord? Next);

public sealed class PdWithDelegate
{
	public Action? Callback;
	public string? Name;
	public SimpleClass? Item;
}

internal static class PdDeepCloneData
{
	public static object Create(string scenario)
	{
		return scenario switch
		{
			"Complex" => CreateComplex(),
			"Nested50" => CreateNested(50),
			"Dict50" => CreateDictionary(50),
			"RecordChain40" => CreateRecordChain(40),
			_ => throw new ArgumentOutOfRangeException(nameof(scenario))
		};
	}

	public static SimpleClass CreateSimple(int i)
	{
		return new SimpleClass { Id = i, Name = "Item" + i, Value = i * 1.5, IsActive = i % 2 == 0, CreatedDate = new DateTime(2026, 1, 1).AddDays(i) };
	}

	public static ComplexClass CreateComplex()
	{
		return new ComplexClass
		{
			Id = 100,
			Name = "Complex",
			Title = "Title",
			Description = "Description",
			Count = 5,
			Price = 12.5m,
			IsEnabled = true,
			Numbers = Enumerable.Range(0, 10).ToList(),
			Tags = Enumerable.Range(0, 10).Select(i => "tag" + i).ToList(),
			Metadata = Enumerable.Range(0, 10).ToDictionary(i => "key" + i, i => "value" + i),
			Nested = CreateSimple(1)
		};
	}

	public static NestedClass CreateNested(int children)
	{
		NestedClass leaf = new() { Level = 3, Id = 3, Name = "Leaf", Child = CreateSimple(-3) };
		NestedClass middle = new() { Level = 2, Id = 2, Name = "Middle", Child = CreateSimple(-2), Child2 = leaf };
		return new NestedClass { Level = 1, Id = 1, Name = "Root", Child = CreateSimple(-1), Child2 = middle, Children = Enumerable.Range(0, children).Select(i => CreateSimple(i)).ToList() };
	}

	public static Dictionary<string, SimpleClass> CreateDictionary(int count)
	{
		return Enumerable.Range(0, count).ToDictionary(i => "key" + i, i => CreateSimple(i));
	}

	public static PdCloneRecord CreateRecordChain(int length)
	{
		PdCloneRecord? node = null;
		for (int i = length; i > 0; i--)
		{
			node = new PdCloneRecord(i, "Node" + i, node);
		}
		return node!;
	}

	public static IEnumerable<object> CreateWarmupObjects()
	{
		Type[] elementTypes = [typeof(int), typeof(long), typeof(short), typeof(byte), typeof(char), typeof(bool), typeof(double), typeof(float), typeof(decimal),
			typeof(DateTime), typeof(Guid), typeof(TimeSpan), typeof(string), typeof(SimpleClassDto), typeof(ExportTestRow), typeof(Version)];
		foreach (Type elementType in elementTypes)
		{
			yield return Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
			yield return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), elementType))!;
			yield return Array.CreateInstance(elementType, 1);
		}
	}

	public static IEnumerable<(string Name, object? Input)> CreateVerificationInputs()
	{
		yield return ("null", null);
		yield return ("string", "plain string");
		yield return ("boxed int", 5);
		yield return ("object", new object());
		yield return ("Complex", CreateComplex());
		yield return ("Nested50", CreateNested(50));
		yield return ("Dict50", CreateDictionary(50));
		yield return ("RecordChain40", CreateRecordChain(40));
		NestedClass cyclic = new() { Level = 1, Name = "Cycle", Child = CreateSimple(9) };
		cyclic.Child2 = cyclic;
		cyclic.Children = [cyclic.Child, cyclic.Child];
		yield return ("Cyclic", cyclic);
		yield return ("int[]", new[] { 1, 2, 3 });
		yield return ("List<string>", new List<string> { "a", "b", null! });
		SimpleClass shared = CreateSimple(77);
		yield return ("object[]", new object?[] { 1, "a", shared, null, shared, new[] { 4, 5 } });
		SimpleClass[,] matrix = new SimpleClass[2, 2];
		matrix[0, 0] = CreateSimple(1);
		matrix[1, 1] = matrix[0, 0];
		yield return ("SimpleClass[,]", matrix);
		yield return ("boxed KeyValuePair", new KeyValuePair<string, SimpleClass>("k", CreateSimple(3)));
		yield return ("delegate field", new PdWithDelegate { Callback = () => { }, Name = "n", Item = CreateSimple(4) });
	}
}

/// <summary>Verbatim copy of CommonNetFuncs.DeepClone.ReferenceEqualityComparer.</summary>
internal sealed class PdOriginalReferenceEqualityComparer : EqualityComparer<object>
{
	public override bool Equals(object? obj1, object? obj2)
	{
		return ReferenceEquals(obj1, obj2);
	}

	public override int GetHashCode(object obj)
	{
		if (obj == null)
		{
			return 0;
		}

		return obj.GetHashCode();
	}
}

/// <summary>Proposed comparer: identity hash (never runs user GetHashCode, no ValueType reflection hashing) + shared singleton.</summary>
internal sealed class PdFastReferenceEqualityComparer : EqualityComparer<object>
{
	public static readonly PdFastReferenceEqualityComparer Instance = new();

	public override bool Equals(object? obj1, object? obj2)
	{
		return ReferenceEquals(obj1, obj2);
	}

	public override int GetHashCode(object obj)
	{
		return RuntimeHelpers.GetHashCode(obj);
	}
}

/// <summary>
/// Verbatim copy of the expression-building part of CommonNetFuncs.DeepClone.ExpressionTrees.
/// Only change: the recursive entry point MethodInfo is passed in so several clone variants can share the builder.
/// </summary>
internal static class PdDeepCloneExpressionBuilder
{
	private static readonly ConcurrentDictionary<Type, bool> IsStructTypeToDeepCopyDictionary = [];
	private static readonly Type ObjectType = typeof(object);
	private static readonly Type ObjectDictionaryType = typeof(Dictionary<object, object>);
	private static readonly ParameterExpression[] EmptyParameterExpressions = [];
	private static readonly Type FieldInfoType = typeof(FieldInfo);
	private static readonly MethodInfo? SetValueMethod = FieldInfoType.GetMethod("SetValue", [ObjectType, ObjectType]);

	internal static Expression<Func<object, Dictionary<object, object>, object>> CreateCompiledLambdaCopyFunctionForType(Type type, bool useCache, MethodInfo deepCopyMethod)
	{
		InitializeExpressions(type, out ParameterExpression inputParameter, out ParameterExpression inputDictionary, out ParameterExpression outputVariable, out ParameterExpression boxingVariable,
			out LabelTarget endLabel, out List<ParameterExpression> variables, out List<Expression> expressions);

		IfNullThenReturnNullExpression(inputParameter, endLabel, expressions);

		MemberwiseCloneInputToOutputExpression(type, inputParameter, outputVariable, expressions);

		if (!type.IsValueType && type != typeof(string))
		{
			StoreReferencesIntoDictionaryExpression(inputParameter, inputDictionary, outputVariable, expressions);
		}

		FieldsCopyExpressions(type, inputParameter, inputDictionary, outputVariable, boxingVariable, expressions, useCache, deepCopyMethod);

		if (type.IsArray && IsTypeToDeepCopy(type.GetElementType()))
		{
			CreateArrayCopyLoopExpression(type, inputParameter, inputDictionary, outputVariable, variables, expressions, useCache, deepCopyMethod);
		}

		return CombineAllIntoLambdaFunctionExpression(inputParameter, inputDictionary, outputVariable, endLabel, variables, expressions);
	}

	private static void InitializeExpressions(Type type, out ParameterExpression inputParameter, out ParameterExpression inputDictionary, out ParameterExpression outputVariable,
			out ParameterExpression boxingVariable, out LabelTarget endLabel, out List<ParameterExpression> variables, out List<Expression> expressions)
	{
		inputParameter = Expression.Parameter(ObjectType);
		inputDictionary = Expression.Parameter(ObjectDictionaryType);
		outputVariable = Expression.Variable(type);
		boxingVariable = Expression.Variable(ObjectType);
		endLabel = Expression.Label();
		variables = [];
		expressions = [];

		variables.Add(outputVariable);
		variables.Add(boxingVariable);
	}

	private static void IfNullThenReturnNullExpression(ParameterExpression inputParameter, LabelTarget endLabel, List<Expression> expressions)
	{
		ConditionalExpression ifNullThenReturnNullExpression = Expression.IfThen(Expression.Equal(inputParameter, Expression.Constant(null, ObjectType)), Expression.Return(endLabel));
		expressions.Add(ifNullThenReturnNullExpression);
	}

	private static void MemberwiseCloneInputToOutputExpression(Type type, ParameterExpression inputParameter, ParameterExpression outputVariable, List<Expression> expressions)
	{
		MethodInfo memberwiseCloneMethod = ObjectType.GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance)!;
		BinaryExpression memberwiseCloneInputExpression = Expression.Assign(outputVariable, Expression.Convert(Expression.Call(inputParameter, memberwiseCloneMethod), type));
		expressions.Add(memberwiseCloneInputExpression);
	}

	private static void StoreReferencesIntoDictionaryExpression(ParameterExpression inputParameter, ParameterExpression inputDictionary, ParameterExpression outputVariable, List<Expression> expressions)
	{
		BinaryExpression storeReferencesExpression = Expression.Assign(Expression.Property(inputDictionary, ObjectDictionaryType.GetProperty("Item")!, inputParameter), Expression.Convert(outputVariable, ObjectType));
		expressions.Add(storeReferencesExpression);
	}

	private static Expression<Func<object, Dictionary<object, object>, object>> CombineAllIntoLambdaFunctionExpression(ParameterExpression inputParameter, ParameterExpression inputDictionary, ParameterExpression outputVariable,
			LabelTarget endLabel, List<ParameterExpression> variables, List<Expression> expressions)
	{
		expressions.Add(Expression.Label(endLabel));
		expressions.Add(Expression.Convert(outputVariable, ObjectType));
		BlockExpression finalBody = Expression.Block(variables, expressions);
		return Expression.Lambda<Func<object, Dictionary<object, object>, object>>(finalBody, inputParameter, inputDictionary);
	}

	private static void CreateArrayCopyLoopExpression(Type type, ParameterExpression inputParameter, ParameterExpression inputDictionary, ParameterExpression outputVariable,
			List<ParameterExpression> variables, List<Expression> expressions, bool useCache, MethodInfo deepCopyMethod)
	{
		int rank = type.GetArrayRank();
		ParameterExpression[] indices = GenerateIndices(rank);

		variables.AddRange(indices);

		Type? elementType = type.GetElementType();
		Expression forExpression = ArrayFieldToArrayFieldAssignExpression(inputParameter, inputDictionary, outputVariable, elementType, type, indices, useCache, deepCopyMethod);

		for (int dimension = 0; dimension < rank; dimension++)
		{
			ParameterExpression indexVariable = indices[dimension];
			forExpression = LoopIntoLoopExpression(inputParameter, indexVariable, forExpression, dimension);
		}

		expressions.Add(forExpression);
	}

	private static ParameterExpression[] GenerateIndices(int arrayRank)
	{
		ParameterExpression[] indices = new ParameterExpression[arrayRank];
		for (int i = 0; i < arrayRank; i++)
		{
			indices[i] = Expression.Variable(typeof(int));
		}

		return indices;
	}

	private static BinaryExpression ArrayFieldToArrayFieldAssignExpression(ParameterExpression inputParameter, ParameterExpression inputDictionary, ParameterExpression outputVariable, Type? elementType, Type arrayType,
		ParameterExpression[] indices, bool useCache, MethodInfo deepCopyMethod)
	{
		IndexExpression indexTo = Expression.ArrayAccess(outputVariable, indices);
		MethodCallExpression indexFrom = Expression.ArrayIndex(Expression.Convert(inputParameter, arrayType), indices);

		bool forceDeepCopy = elementType != ObjectType;
		UnaryExpression rightSide = Expression.Convert(Expression.Call(deepCopyMethod, Expression.Convert(indexFrom, ObjectType), Expression.Constant(forceDeepCopy, typeof(bool)), inputDictionary, Expression.Constant(useCache, typeof(bool))), elementType!);
		return Expression.Assign(indexTo, rightSide);
	}

	private static BlockExpression LoopIntoLoopExpression(ParameterExpression inputParameter, ParameterExpression indexVariable, Expression loopToEncapsulate, int dimension)
	{
		ParameterExpression lengthVariable = Expression.Variable(typeof(int));

		LabelTarget endLabelForThisLoop = Expression.Label();

		Expression[] loopBody =
		[
			Expression.IfThen(Expression.GreaterThanOrEqual(indexVariable, lengthVariable), Expression.Break(endLabelForThisLoop)),
			loopToEncapsulate,
			Expression.PostIncrementAssign(indexVariable)
		];

		LoopExpression newLoop = Expression.Loop(Expression.Block(EmptyParameterExpressions, loopBody), endLabelForThisLoop);

		BinaryExpression lengthAssignment = GetLengthForDimensionExpression(lengthVariable, inputParameter, dimension);
		BinaryExpression indexAssignment = Expression.Assign(indexVariable, Expression.Constant(0));
		Expression[] outerBody = [lengthAssignment, indexAssignment, newLoop];
		return Expression.Block(new[] { lengthVariable }, outerBody);
	}

	private static BinaryExpression GetLengthForDimensionExpression(ParameterExpression lengthVariable, ParameterExpression inputParameter, int i)
	{
		MethodInfo? getLengthMethod = typeof(Array).GetMethod("GetLength", BindingFlags.Public | BindingFlags.Instance);
		ConstantExpression dimensionConstant = Expression.Constant(i);
		return Expression.Assign(lengthVariable, Expression.Call(Expression.Convert(inputParameter, typeof(Array)), getLengthMethod!, dimensionConstant));
	}

	private static void FieldsCopyExpressions(Type type, ParameterExpression inputParameter, ParameterExpression inputDictionary, ParameterExpression outputVariable,
			ParameterExpression boxingVariable, List<Expression> expressions, bool useCache, MethodInfo deepCopyMethod)
	{
		FieldInfo[] fields = GetAllRelevantFields(type);
		FieldInfo[] readonlyFields = Array.FindAll(fields, f => f.IsInitOnly);
		FieldInfo[] writableFields = Array.FindAll(fields, f => !f.IsInitOnly);

		bool shouldUseBoxing = readonlyFields.Length > 0;
		if (shouldUseBoxing)
		{
			BinaryExpression boxingExpression = Expression.Assign(boxingVariable, Expression.Convert(outputVariable, ObjectType));
			expressions.Add(boxingExpression);
		}

		foreach (FieldInfo field in readonlyFields)
		{
			if (typeof(Delegate).IsAssignableFrom(field.FieldType))
			{
				ReadonlyFieldToNullExpression(field, boxingVariable, expressions);
			}
			else
			{
				ReadonlyFieldCopyExpression(type, field, inputParameter, inputDictionary, boxingVariable, expressions, useCache, deepCopyMethod);
			}
		}

		if (shouldUseBoxing)
		{
			BinaryExpression unboxingExpression = Expression.Assign(outputVariable, Expression.Convert(boxingVariable, type));
			expressions.Add(unboxingExpression);
		}

		foreach (FieldInfo field in writableFields)
		{
			if (typeof(Delegate).IsAssignableFrom(field.FieldType))
			{
				WritableFieldToNullExpression(field, outputVariable, expressions);
			}
			else
			{
				WritableFieldCopyExpression(type, field, inputParameter, inputDictionary, outputVariable, expressions, useCache, deepCopyMethod);
			}
		}
	}

	private static FieldInfo[] GetAllRelevantFields(Type? type, bool forceAllFields = false)
	{
		List<FieldInfo> fieldsList = [];
		Type? typeCache = type;
		while (typeCache != null)
		{
			fieldsList.AddRange(typeCache.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy)
				.Where(field => forceAllFields || IsTypeToDeepCopy(field.FieldType)));
			typeCache = typeCache.BaseType;
		}
		return fieldsList.ToArray();
	}

	private static FieldInfo[] GetAllFields(Type type)
	{
		return GetAllRelevantFields(type, forceAllFields: true);
	}

	private static void ReadonlyFieldToNullExpression(FieldInfo field, ParameterExpression boxingVariable, List<Expression> expressions)
	{
		MethodCallExpression fieldToNullExpression = Expression.Call(Expression.Constant(field), SetValueMethod!, boxingVariable, Expression.Constant(null, field.FieldType));
		expressions.Add(fieldToNullExpression);
	}

	private static void ReadonlyFieldCopyExpression(Type type, FieldInfo field, ParameterExpression inputParameter, ParameterExpression inputDictionary, ParameterExpression boxingVariable, List<Expression> expressions,
		bool useCache, MethodInfo deepCopyMethod)
	{
		MemberExpression fieldFrom = Expression.Field(Expression.Convert(inputParameter, type), field);
		bool forceDeepCopy = field.FieldType != ObjectType;
		MethodCallExpression fieldDeepCopyExpression =
			Expression.Call
			(
				Expression.Constant(field, FieldInfoType),
				SetValueMethod!,
				boxingVariable,
				Expression.Call(deepCopyMethod, Expression.Convert(fieldFrom, ObjectType), Expression.Constant(forceDeepCopy, typeof(bool)), inputDictionary, Expression.Constant(useCache, typeof(bool)))
			);

		expressions.Add(fieldDeepCopyExpression);
	}

	private static void WritableFieldToNullExpression(FieldInfo field, ParameterExpression outputVariable, List<Expression> expressions)
	{
		MemberExpression fieldTo = Expression.Field(outputVariable, field);
		BinaryExpression fieldToNullExpression = Expression.Assign(fieldTo, Expression.Constant(null, field.FieldType));
		expressions.Add(fieldToNullExpression);
	}

	private static void WritableFieldCopyExpression(Type type, FieldInfo field, ParameterExpression inputParameter, ParameterExpression inputDictionary, ParameterExpression outputVariable, List<Expression> expressions,
		bool useCache, MethodInfo deepCopyMethod)
	{
		MemberExpression fieldFrom = Expression.Field(Expression.Convert(inputParameter, type), field);
		Type fieldType = field.FieldType;
		MemberExpression fieldTo = Expression.Field(outputVariable, field);

		bool forceDeepCopy = field.FieldType != ObjectType;

		BinaryExpression fieldDeepCopyExpression =
			Expression.Assign
			(
				fieldTo,
				Expression.Convert
				(
					Expression.Call(deepCopyMethod, Expression.Convert(fieldFrom, ObjectType), Expression.Constant(forceDeepCopy, typeof(bool)), inputDictionary, Expression.Constant(useCache, typeof(bool))),
					fieldType
				)
			);

		expressions.Add(fieldDeepCopyExpression);
	}

	internal static bool IsTypeToDeepCopy(Type? type)
	{
		return type == null || (!type.IsValueType && type != typeof(string)) || IsStructWhichNeedsDeepCopy(type);
	}

	private static bool IsStructWhichNeedsDeepCopy(Type? type)
	{
		if (!IsStructTypeToDeepCopyDictionary.TryGetValue(type!, out bool isStructTypeToDeepCopy))
		{
			isStructTypeToDeepCopy = IsStructWhichNeedsDeepCopy_NoDictionaryUsed(type!);
			IsStructTypeToDeepCopyDictionary.TryAdd(type!, isStructTypeToDeepCopy);
		}

		return isStructTypeToDeepCopy;
	}

	private static bool IsStructWhichNeedsDeepCopy_NoDictionaryUsed(Type type)
	{
		return IsStructOtherThanBasicValueTypes(type) && HasInItsHierarchyFieldsWithClasses(type);
	}

	private static bool IsStructOtherThanBasicValueTypes(Type type)
	{
		return type.IsValueType && !type.IsPrimitive && !type.IsEnum && type != typeof(decimal);
	}

	private static bool HasInItsHierarchyFieldsWithClasses(Type type, HashSet<Type>? alreadyCheckedTypes = null)
	{
		alreadyCheckedTypes ??= [];
		alreadyCheckedTypes.Add(type);

		FieldInfo[] allFields = GetAllFields(type);

		HashSet<Type> distinctFieldTypes = new(allFields.Length);

		foreach (Type fieldType in allFields.Select(x => x.FieldType))
		{
			if (!fieldType.IsValueType && fieldType != typeof(string))
			{
				return true;
			}
			distinctFieldTypes.Add(fieldType);
		}

		foreach (Type fieldType in distinctFieldTypes)
		{
			if (IsStructOtherThanBasicValueTypes(fieldType) && !alreadyCheckedTypes.Contains(fieldType) && HasInItsHierarchyFieldsWithClasses(fieldType, alreadyCheckedTypes))
			{
				return true;
			}
		}

		return false;
	}
}

/// <summary>Verbatim copy of the current ExpressionTrees.DeepClone / DeepCopyByExpressionTreeObj / cache lookup (own CacheManager instance).</summary>
internal static class PdDeepCloneOriginal
{
	private static readonly Type ObjectType = typeof(object);
	private static readonly CommonNetFuncs.Core.CacheManager<Type, Func<object, Dictionary<object, object>, object>> DeepCloneCache = new();
	private static readonly MethodInfo DeepCopyByExpressionTreeObjMethod = typeof(PdDeepCloneOriginal).GetMethod(nameof(DeepCopyByExpressionTreeObj), BindingFlags.NonPublic | BindingFlags.Static)!;

	public static CommonNetFuncs.Core.ICacheManagerApi<Type, Func<object, Dictionary<object, object>, object>> CacheManager => DeepCloneCache;

	private static Func<object, Dictionary<object, object>, object> GetOrAddPropertiesFromCompiledFunctionsCache(Type key)
	{
		bool isLimitedCache = CacheManager.IsUsingLimitedCache();
		if (isLimitedCache ? CacheManager.GetLimitedCache().TryGetValue(key, out Func<object, Dictionary<object, object>, object>? compiledFunction) : CacheManager.GetCache().TryGetValue(key, out compiledFunction))
		{
			return compiledFunction!;
		}

		compiledFunction = PdDeepCloneExpressionBuilder.CreateCompiledLambdaCopyFunctionForType(key, true, DeepCopyByExpressionTreeObjMethod).Compile();
		if (isLimitedCache)
		{
			CacheManager.TryAddLimitedCache(key, compiledFunction);
		}
		else
		{
			CacheManager.TryAddCache(key, compiledFunction);
		}
		return compiledFunction;
	}

	public static T? DeepClone<T>(T original, Dictionary<object, object>? copiedReferencesDict = null, bool useCache = true)
	{
		return (T?)DeepCopyByExpressionTreeObj(original, false, copiedReferencesDict ?? new Dictionary<object, object>(new PdOriginalReferenceEqualityComparer()), useCache);
	}

	private static object? DeepCopyByExpressionTreeObj(object? original, bool forceDeepCopy, Dictionary<object, object> copiedReferencesDict, bool useCache)
	{
		if (original == null)
		{
			return null;
		}

		Type type = original.GetType();

		if (typeof(Delegate).IsAssignableFrom(type))
		{
			throw new ArgumentException($"Type {type.FullName} is a delegate type which is unsupported.", nameof(original));
		}

		if (!forceDeepCopy && !PdDeepCloneExpressionBuilder.IsTypeToDeepCopy(type))
		{
			return original;
		}

		if (copiedReferencesDict.TryGetValue(original, out object? alreadyCopiedObject))
		{
			return alreadyCopiedObject;
		}

		if (type == ObjectType)
		{
			return new();
		}

		Func<object, Dictionary<object, object>, object> compiledCopyFunction = useCache ?
			GetOrAddPropertiesFromCompiledFunctionsCache(type) :
			PdDeepCloneExpressionBuilder.CreateCompiledLambdaCopyFunctionForType(type, useCache, DeepCopyByExpressionTreeObjMethod).Compile();
		return compiledCopyFunction(original, copiedReferencesDict);
	}
}

/// <summary>Proposed: `is Delegate` type test, one GetOrAdd (single lock, no ReadOnlyDictionary wrapper) per lookup, singleton identity-hash comparer.</summary>
internal static class PdDeepCloneOptimized
{
	private static readonly Type ObjectType = typeof(object);
	private static readonly CommonNetFuncs.Core.CacheManager<Type, Func<object, Dictionary<object, object>, object>> DeepCloneCache = new();
	private static readonly MethodInfo DeepCopyByExpressionTreeObjMethod = typeof(PdDeepCloneOptimized).GetMethod(nameof(DeepCopyByExpressionTreeObj), BindingFlags.NonPublic | BindingFlags.Static)!;
	private static readonly Func<Type, Func<object, Dictionary<object, object>, object>> CompileForCache =
		static t => PdDeepCloneExpressionBuilder.CreateCompiledLambdaCopyFunctionForType(t, true, DeepCopyByExpressionTreeObjMethod).Compile();

	public static CommonNetFuncs.Core.ICacheManagerApi<Type, Func<object, Dictionary<object, object>, object>> CacheManager => DeepCloneCache;

	internal static Func<object, Dictionary<object, object>, object> GetOrAddCompiledFunction(Type key)
	{
		return CacheManager.IsUsingLimitedCache() ? CacheManager.GetOrAddLimitedCache(key, CompileForCache) : CacheManager.GetOrAddCache(key, CompileForCache);
	}

	public static T? DeepClone<T>(T original, Dictionary<object, object>? copiedReferencesDict = null, bool useCache = true)
	{
		return (T?)DeepCopyByExpressionTreeObj(original, false, copiedReferencesDict ?? new Dictionary<object, object>(PdFastReferenceEqualityComparer.Instance), useCache);
	}

	private static object? DeepCopyByExpressionTreeObj(object? original, bool forceDeepCopy, Dictionary<object, object> copiedReferencesDict, bool useCache)
	{
		if (original == null)
		{
			return null;
		}

		// Equivalent to typeof(Delegate).IsAssignableFrom(original.GetType()) but a single type-handle check
		if (original is Delegate)
		{
			throw new ArgumentException($"Type {original.GetType().FullName} is a delegate type which is unsupported.", nameof(original));
		}

		Type type = original.GetType();

		if (!forceDeepCopy && !PdDeepCloneExpressionBuilder.IsTypeToDeepCopy(type))
		{
			return original;
		}

		if (copiedReferencesDict.TryGetValue(original, out object? alreadyCopiedObject))
		{
			return alreadyCopiedObject;
		}

		if (type == ObjectType)
		{
			return new();
		}

		Func<object, Dictionary<object, object>, object> compiledCopyFunction = useCache ?
			GetOrAddCompiledFunction(type) :
			PdDeepCloneExpressionBuilder.CreateCompiledLambdaCopyFunctionForType(type, useCache, DeepCopyByExpressionTreeObjMethod).Compile();
		return compiledCopyFunction(original, copiedReferencesDict);
	}
}

/// <summary>Small fixed-size Type -> compiled function memo, scoped to one top-level DeepClone call.</summary>
internal sealed class PdTypeMemo
{
	private const int Size = 8;
	private readonly Type?[] types = new Type?[Size];
	private readonly Func<object, Dictionary<object, object>, object>?[] functions = new Func<object, Dictionary<object, object>, object>?[Size];
	private int next;

	public Func<object, Dictionary<object, object>, object> GetOrFetch(Type type, Func<Type, Func<object, Dictionary<object, object>, object>> fetch)
	{
		Type?[] localTypes = types;
		for (int i = 0; i < localTypes.Length; i++)
		{
			if (ReferenceEquals(localTypes[i], type))
			{
				return functions[i]!;
			}
		}

		Func<object, Dictionary<object, object>, object> function = fetch(type);
		types[next] = type;
		functions[next] = function;
		next = (next + 1) & (Size - 1);
		return function;
	}

	public void Reset()
	{
		Array.Clear(types, 0, types.Length);
		Array.Clear(functions, 0, functions.Length);
		next = 0;
	}
}

/// <summary>Proposed (stronger): PdDeepCloneOptimized + a [ThreadStatic] call-scoped memo so repeated types inside one graph skip the CacheManager lock entirely.</summary>
internal static class PdDeepCloneOptimizedMemo
{
	private static readonly Type ObjectType = typeof(object);
	private static readonly CommonNetFuncs.Core.CacheManager<Type, Func<object, Dictionary<object, object>, object>> DeepCloneCache = new();
	private static readonly MethodInfo DeepCopyByExpressionTreeObjMethod = typeof(PdDeepCloneOptimizedMemo).GetMethod(nameof(DeepCopyByExpressionTreeObj), BindingFlags.NonPublic | BindingFlags.Static)!;
	private static readonly Func<Type, Func<object, Dictionary<object, object>, object>> CompileForCache =
		static t => PdDeepCloneExpressionBuilder.CreateCompiledLambdaCopyFunctionForType(t, true, DeepCopyByExpressionTreeObjMethod).Compile();
	private static readonly Func<Type, Func<object, Dictionary<object, object>, object>> FetchFromCache = GetOrAddCompiledFunction;

	[ThreadStatic]
	private static PdTypeMemo? reusableMemo;

	[ThreadStatic]
	private static PdTypeMemo? activeMemo;

	public static CommonNetFuncs.Core.ICacheManagerApi<Type, Func<object, Dictionary<object, object>, object>> CacheManager => DeepCloneCache;

	private static Func<object, Dictionary<object, object>, object> GetOrAddCompiledFunction(Type key)
	{
		return CacheManager.IsUsingLimitedCache() ? CacheManager.GetOrAddLimitedCache(key, CompileForCache) : CacheManager.GetOrAddCache(key, CompileForCache);
	}

	public static T? DeepClone<T>(T original, Dictionary<object, object>? copiedReferencesDict = null, bool useCache = true)
	{
		Dictionary<object, object> references = copiedReferencesDict ?? new Dictionary<object, object>(PdFastReferenceEqualityComparer.Instance);
		if (!useCache)
		{
			return (T?)DeepCopyByExpressionTreeObj(original, false, references, false);
		}

		// Re-entrant top-level calls (only possible via a user-supplied comparer) get their own memo
		PdTypeMemo? outer = activeMemo;
		PdTypeMemo memo = outer == null ? (reusableMemo ??= new PdTypeMemo()) : new PdTypeMemo();
		activeMemo = memo;
		try
		{
			return (T?)DeepCopyByExpressionTreeObj(original, false, references, true);
		}
		finally
		{
			memo.Reset();
			activeMemo = outer;
		}
	}

	private static object? DeepCopyByExpressionTreeObj(object? original, bool forceDeepCopy, Dictionary<object, object> copiedReferencesDict, bool useCache)
	{
		if (original == null)
		{
			return null;
		}

		if (original is Delegate)
		{
			throw new ArgumentException($"Type {original.GetType().FullName} is a delegate type which is unsupported.", nameof(original));
		}

		Type type = original.GetType();

		if (!forceDeepCopy && !PdDeepCloneExpressionBuilder.IsTypeToDeepCopy(type))
		{
			return original;
		}

		if (copiedReferencesDict.TryGetValue(original, out object? alreadyCopiedObject))
		{
			return alreadyCopiedObject;
		}

		if (type == ObjectType)
		{
			return new();
		}

		Func<object, Dictionary<object, object>, object> compiledCopyFunction;
		if (useCache)
		{
			PdTypeMemo? memo = activeMemo;
			compiledCopyFunction = memo != null ? memo.GetOrFetch(type, FetchFromCache) : GetOrAddCompiledFunction(type);
		}
		else
		{
			compiledCopyFunction = PdDeepCloneExpressionBuilder.CreateCompiledLambdaCopyFunctionForType(type, useCache, DeepCopyByExpressionTreeObjMethod).Compile();
		}
		return compiledCopyFunction(original, copiedReferencesDict);
	}
}

/// <summary>Structural graph comparison used by PdVerify (cycle/alias aware).</summary>
internal static class PdGraph
{
	public static bool Equal(object? x, object? y)
	{
		return Equal(x, y, new Dictionary<object, object>(PdFastReferenceEqualityComparer.Instance));
	}

	private static bool Equal(object? x, object? y, Dictionary<object, object> visited)
	{
		if (x is null || y is null)
		{
			return x is null && y is null;
		}

		Type type = x.GetType();
		if (type != y.GetType())
		{
			return false;
		}

		if (type.IsPrimitive || type.IsEnum || x is string || x is decimal || x is DateTime || x is DateTimeOffset || x is Guid || x is TimeSpan || x is Type || x is Delegate)
		{
			return x.Equals(y);
		}

		if (!type.IsValueType)
		{
			if (visited.TryGetValue(x, out object? mapped))
			{
				return ReferenceEquals(mapped, y);
			}
			visited[x] = y;
		}

		if (x is Array xArray)
		{
			Array yArray = (Array)y;
			if (xArray.Rank != yArray.Rank || xArray.Length != yArray.Length)
			{
				return false;
			}

			IEnumerator xEnumerator = xArray.GetEnumerator();
			IEnumerator yEnumerator = yArray.GetEnumerator();
			while (xEnumerator.MoveNext() && yEnumerator.MoveNext())
			{
				if (!Equal(xEnumerator.Current, yEnumerator.Current, visited))
				{
					return false;
				}
			}
			return true;
		}

		for (Type? current = type; current != null; current = current.BaseType)
		{
			foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (!Equal(field.GetValue(x), field.GetValue(y), visited))
				{
					return false;
				}
			}
		}
		return true;
	}
}

#endregion

#region Sql.Common

/// <summary>DirectQuery row mapper: reader.GetOrdinal(name) per property per row vs ordinals resolved once per result set (lazily on the first row).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdSqlOrdinalResolutionBenchmarks
{
	private DataTable table = null!;
	private Func<IDataReader, PdSqlRow> originalMapper = null!;
	private PdOrdinalMapper<PdSqlRow> ordinalMapper = null!;

	[GlobalSetup]
	public void Setup()
	{
		table = PdSqlData.CreateTable(1000, includeNulls: true);
		originalMapper = PdSqlMappers.CreateMapperDelegate<PdSqlRow>(typeof(PdSqlRow));
		ordinalMapper = PdSqlMappers.CreateOrdinalMapper<PdSqlRow>(typeof(PdSqlRow));
	}

	[Benchmark(Baseline = true)]
	public long Original_GetOrdinalPerRow()
	{
		long sum = 0;
		using DataTableReader reader = table.CreateDataReader();
		while (reader.Read())
		{
			sum += originalMapper(reader).Count;
		}
		return sum;
	}

	[Benchmark]
	public long Optimized_OrdinalsOncePerReader()
	{
		long sum = 0;
		// One small per-query wrapper keeps the Func<IDataReader, T> shape used by GetDataStream / GetDataDirectAsync
		Func<IDataReader, PdSqlRow> mapper = new PdOrdinalReaderMapper<PdSqlRow>(ordinalMapper).Map;
		using DataTableReader reader = table.CreateDataReader();
		while (reader.Read())
		{
			sum += mapper(reader).Count;
		}
		return sum;
	}
}

/// <summary>QueryParameters.CleanQueryParam(IList): Parallel.ForEach + ConcurrentBag + LINQ vs a sequential pre-sized loop.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdCleanQueryParamListBenchmarks
{
	private List<string> values = null!;

	[Params(8, 1000)]
	public int Count { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		values = Enumerable.Range(0, Count).Select(i => (i % 5) switch
		{
			0 => " value" + i + "\n",
			1 => "null",
			2 => "  Clean\nThis" + i + "  ",
			3 => "plain" + i,
			_ => "NULL"
		}).ToList();
	}

	[Benchmark(Baseline = true)]
	public int Original_ParallelConcurrentBag()
	{
		return PdQueryParameters.CleanQueryParamOriginal(values)!.Count;
	}

	[Benchmark]
	public int Optimized_SequentialList()
	{
		return PdQueryParameters.CleanQueryParamOptimized(values)!.Count;
	}
}

/// <summary>QueryParameters.IsClean / SanitizeSqlParameter: ten Contains scans + three Replace scans vs one character pass + one IndexOfAny.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdSanitizeSqlParameterBenchmarks
{
	private static readonly string?[] Inputs =
	[
		"12345", "John O'Brien", "abc%def*", "normal value", "a;b", "SELECT * FROM", "x", "xp_cmdshell", "", null, "50%", "comment -- here", "/* hi */",
		"Robert'); DROP TABLE Students;--", "simple", "ABCdef123", "customer.name@example.com", "Some reasonably long free-text search value"
	];

	[Benchmark(Baseline = true)]
	public int Original()
	{
		int total = 0;
		foreach (string? input in Inputs)
		{
			total += PdQueryParameters.SanitizeSqlParameterOriginal(input)!.Length;
			total += PdQueryParameters.SanitizeSqlParameterOriginal(input, onlyAlphanumeric: true, maxLength: 40)!.Length;
			total += PdQueryParameters.IsCleanOriginal(input) ? 1 : 0;
		}
		return total;
	}

	[Benchmark]
	public int Optimized_SinglePass()
	{
		int total = 0;
		foreach (string? input in Inputs)
		{
			total += PdQueryParameters.SanitizeSqlParameterOptimized(input)!.Length;
			total += PdQueryParameters.SanitizeSqlParameterOptimized(input, onlyAlphanumeric: true, maxLength: 40)!.Length;
			total += PdQueryParameters.IsCleanOptimized(input) ? 1 : 0;
		}
		return total;
	}
}

public sealed class PdSqlRow
{
	public int Id { get; set; }
	public string? Name { get; set; }
	public string? Email { get; set; }
	public decimal Amount { get; set; }
	public DateTime Created { get; set; }
	public bool Active { get; set; }
	public long Count { get; set; }
	public double Ratio { get; set; }
	public string? Notes { get; set; }
	public Guid Key { get; set; }
}

internal static class PdSqlData
{
	public static DataTable CreateTable(int rows, bool includeNulls, bool reverseColumns = false, bool omitNotes = false, bool amountAsString = false)
	{
		(string Name, Type Type)[] columns =
		[
			("Id", typeof(long)), ("Name", typeof(string)), ("Email", typeof(string)), ("Amount", amountAsString ? typeof(string) : typeof(decimal)), ("Created", typeof(DateTime)),
			("Active", typeof(bool)), ("Count", typeof(long)), ("Ratio", typeof(double)), ("Notes", typeof(string)), ("Key", typeof(Guid))
		];

		DataTable table = new();
		IEnumerable<(string Name, Type Type)> ordered = reverseColumns ? Enumerable.Reverse(columns) : columns;
		foreach ((string name, Type type) in ordered)
		{
			if (!(omitNotes && name == "Notes"))
			{
				table.Columns.Add(name, type);
			}
		}

		for (int i = 0; i < rows; i++)
		{
			bool nullRow = includeNulls && i % 7 == 3;
			DataRow row = table.NewRow();
			SetValue(row, "Id", nullRow ? DBNull.Value : (object)(long)i);
			SetValue(row, "Name", nullRow ? DBNull.Value : (object)("Name" + i));
			SetValue(row, "Email", "user" + i + "@test.com");
			SetValue(row, "Amount", nullRow ? DBNull.Value : amountAsString ? (object)"1.5" : (object)(i * 1.5m));
			SetValue(row, "Created", new DateTime(2026, 1, 1).AddMinutes(i));
			SetValue(row, "Active", nullRow ? DBNull.Value : (object)(i % 2 == 0));
			SetValue(row, "Count", nullRow ? DBNull.Value : (object)((long)i * 7));
			SetValue(row, "Ratio", i / 3.0);
			SetValue(row, "Notes", i % 5 == 0 ? DBNull.Value : (object)"note");
			SetValue(row, "Key", nullRow ? DBNull.Value : (object)new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10));
			table.Rows.Add(row);
		}
		return table;
	}

	private static void SetValue(DataRow row, string column, object value)
	{
		if (row.Table.Columns.Contains(column))
		{
			row[column] = value;
		}
	}
}

/// <summary>Compiled mapper that reads values by pre-resolved ordinals; ColumnNames[i] is the column read through ordinals[i].</summary>
internal sealed class PdOrdinalMapper<T>(string[] columnNames, Func<IDataReader, int[], T> map)
{
	public string[] ColumnNames { get; } = columnNames;

	public Func<IDataReader, int[], T> Map { get; } = map;
}

/// <summary>Per-query wrapper: resolves ordinals on the first mapped row (same point the original first calls GetOrdinal), then reuses them.</summary>
internal sealed class PdOrdinalReaderMapper<T>(PdOrdinalMapper<T> compiled)
{
	private int[]? ordinals;

	public T Map(IDataReader reader)
	{
		int[] resolved = ordinals ??= Resolve(reader);
		return compiled.Map(reader, resolved);
	}

	private int[] Resolve(IDataReader reader)
	{
		string[] names = compiled.ColumnNames;
		int[] result = new int[names.Length];
		for (int i = 0; i < names.Length; i++)
		{
			result[i] = reader.GetOrdinal(names[i]);
		}
		return result;
	}
}

internal static class PdSqlMappers
{
	// Verbatim copy of CommonNetFuncs.Sql.Common.DirectQuery.CreateMapperDelegate (current: GetOrdinal once per property per row)
	public static Func<IDataReader, T> CreateMapperDelegate<T>(Type type) where T : class, new()
	{
		ParameterExpression readerParam = Expression.Parameter(typeof(IDataReader), "reader");
		MethodInfo getValueMethod = typeof(IDataRecord).GetMethod("GetValue")!;
		MethodInfo getOrdinalMethod = typeof(IDataRecord).GetMethod("GetOrdinal")!;
		MethodInfo isDBNullMethod = typeof(IDataRecord).GetMethod("IsDBNull")!;
		MethodInfo toInt32Method = typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(object) })!;

		NewExpression instanceExp = Expression.New(type);

		IEnumerable<PropertyInfo> properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite);

		List<MemberBinding> assignments = [];

		foreach (PropertyInfo prop in properties)
		{
			ParameterExpression ordinal = Expression.Variable(typeof(int), "ordinal");
			MethodCallExpression getOrdinalCall = Expression.Call(readerParam, getOrdinalMethod, Expression.Constant(prop.Name));

			MethodCallExpression isDbNullCall = Expression.Call(readerParam, isDBNullMethod, ordinal);

			MethodCallExpression getValue = Expression.Call(readerParam, getValueMethod, ordinal);

			Expression convertedValue;
			if (prop.PropertyType == typeof(int))
			{
				convertedValue = Expression.Convert(Expression.Call(toInt32Method, getValue), typeof(int));
			}
			else
			{
				convertedValue = Expression.Convert(getValue, prop.PropertyType);
			}

			BlockExpression assignValue = Expression.Block(prop.PropertyType, new[] { ordinal },
				Expression.Assign(ordinal, getOrdinalCall),
				Expression.Condition(isDbNullCall, Expression.Default(prop.PropertyType), convertedValue));

			assignments.Add(Expression.Bind(prop, assignValue));
		}

		MemberInitExpression memberInit = Expression.MemberInit(instanceExp, assignments);

		Expression<Func<IDataReader, T>> lambda = Expression.Lambda<Func<IDataReader, T>>(memberInit, readerParam);
		return lambda.CompileFast();
	}

	// Proposed: same per-property IsDBNull / GetValue / conversion, but the ordinal comes from an array resolved once per reader
	public static PdOrdinalMapper<T> CreateOrdinalMapper<T>(Type type) where T : class, new()
	{
		ParameterExpression readerParam = Expression.Parameter(typeof(IDataReader), "reader");
		ParameterExpression ordinalsParam = Expression.Parameter(typeof(int[]), "ordinals");
		MethodInfo getValueMethod = typeof(IDataRecord).GetMethod("GetValue")!;
		MethodInfo isDBNullMethod = typeof(IDataRecord).GetMethod("IsDBNull")!;
		MethodInfo toInt32Method = typeof(Convert).GetMethod(nameof(Convert.ToInt32), new[] { typeof(object) })!;

		NewExpression instanceExp = Expression.New(type);
		List<string> columnNames = [];
		List<MemberBinding> assignments = [];

		foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite))
		{
			BinaryExpression ordinal = Expression.ArrayIndex(ordinalsParam, Expression.Constant(columnNames.Count));
			columnNames.Add(prop.Name);

			MethodCallExpression isDbNullCall = Expression.Call(readerParam, isDBNullMethod, ordinal);
			MethodCallExpression getValue = Expression.Call(readerParam, getValueMethod, ordinal);

			Expression convertedValue = prop.PropertyType == typeof(int)
				? Expression.Convert(Expression.Call(toInt32Method, getValue), typeof(int))
				: Expression.Convert(getValue, prop.PropertyType);

			assignments.Add(Expression.Bind(prop, Expression.Condition(isDbNullCall, Expression.Default(prop.PropertyType), convertedValue)));
		}

		MemberInitExpression memberInit = Expression.MemberInit(instanceExp, assignments);
		Func<IDataReader, int[], T> map = Expression.Lambda<Func<IDataReader, int[], T>>(memberInit, readerParam, ordinalsParam).CompileFast();
		return new PdOrdinalMapper<T>(columnNames.ToArray(), map);
	}
}

internal static class PdQueryParameters
{
	// Verbatim copy of QueryParameters.CleanQueryParam(this IList<string>?)
	public static List<string>? CleanQueryParamOriginal(IList<string>? values)
	{
		if (values == null)
		{
			return null;
		}

		ConcurrentBag<string?> cleanValues = [];
		if (values.Count > 0)
		{
			Parallel.ForEach(values, value => cleanValues.Add(CommonNetFuncs.Core.Strings.MakeNullNull(value)?.Replace("\n", string.Empty).Trim()));
		}

		return (cleanValues ?? []).Where(x => x != null).ToList()!;
	}

	// Proposed: same per-item transformation, sequential, pre-sized. Output order becomes input order (the original's order is non-deterministic).
	public static List<string>? CleanQueryParamOptimized(IList<string>? values)
	{
		if (values == null)
		{
			return null;
		}

		List<string> cleanValues = new(values.Count);
		for (int i = 0; i < values.Count; i++)
		{
			string? cleanedValue = CommonNetFuncs.Core.Strings.MakeNullNull(values[i])?.Replace("\n", string.Empty).Trim();
			if (cleanedValue != null)
			{
				cleanValues.Add(cleanedValue);
			}
		}
		return cleanValues;
	}

	// Verbatim copy of QueryParameters.IsClean
	public static bool IsCleanOriginal(string? parameter, bool excludeSingleQuote = false)
	{
		return string.IsNullOrWhiteSpace(parameter) || (!parameter.Contains(';') && (excludeSingleQuote || !parameter.Contains('\'')) && !parameter.Contains('[') && !parameter.Contains(']') &&
			!parameter.Contains('"') && !parameter.Contains('`') && !parameter.Contains("/*") && !parameter.Contains("*/") && !parameter.Contains("xp_") && !parameter.Contains("--"));
	}

	// Proposed: one ordinal pass; two/three-character tokens are checked at their first character
	public static bool IsCleanOptimized(string? parameter, bool excludeSingleQuote = false)
	{
		if (string.IsNullOrWhiteSpace(parameter))
		{
			return true;
		}

		ReadOnlySpan<char> span = parameter.AsSpan();
		int last = span.Length - 1;
		for (int i = 0; i < span.Length; i++)
		{
			switch (span[i])
			{
				case ';':
				case '[':
				case ']':
				case '"':
				case '`':
					return false;
				case '\'':
					if (!excludeSingleQuote)
					{
						return false;
					}
					break;
				case '/':
					if (i < last && span[i + 1] == '*')
					{
						return false;
					}
					break;
				case '*':
					if (i < last && span[i + 1] == '/')
					{
						return false;
					}
					break;
				case '-':
					if (i < last && span[i + 1] == '-')
					{
						return false;
					}
					break;
				case 'x':
					if (i < last - 1 && span[i + 1] == 'p' && span[i + 2] == '_')
					{
						return false;
					}
					break;
			}
		}
		return true;
	}

	// Verbatim copy of QueryParameters.SanitizeSqlParameter
	public static string? SanitizeSqlParameterOriginal(string? parameter, bool onlyAlphanumeric = false, bool onlyAlphaChars = false, bool onlyNumberChars = false, int? maxLength = null, int? minLength = null, string? defaultValue = "")
	{
		string? result = null;
		if (IsCleanOriginal(parameter, true) && (maxLength == null || (parameter?.Length ?? 0) <= maxLength) && (minLength == null || (parameter?.Length ?? 0) >= minLength))
		{
			if (onlyAlphanumeric || onlyAlphaChars || onlyNumberChars)
			{
				if ((onlyAlphanumeric && CommonNetFuncs.Core.Strings.IsAlphanumeric(parameter)) || (onlyAlphaChars && CommonNetFuncs.Core.Strings.IsAlphaOnly(parameter)) ||
					(onlyNumberChars && CommonNetFuncs.Core.Strings.IsNumericOnly(parameter)))
				{
					result = parameter;
				}
			}
			else
			{
				result = parameter;
			}
		}

		return result?.Replace("'", "''").Replace("%", string.Empty).Replace("*", string.Empty) ?? defaultValue;
	}

	// Proposed: single-pass IsClean + skip the Replace chain when none of ' % * is present
	public static string? SanitizeSqlParameterOptimized(string? parameter, bool onlyAlphanumeric = false, bool onlyAlphaChars = false, bool onlyNumberChars = false, int? maxLength = null, int? minLength = null, string? defaultValue = "")
	{
		string? result = null;
		if (IsCleanOptimized(parameter, true) && (maxLength == null || (parameter?.Length ?? 0) <= maxLength) && (minLength == null || (parameter?.Length ?? 0) >= minLength))
		{
			if (onlyAlphanumeric || onlyAlphaChars || onlyNumberChars)
			{
				if ((onlyAlphanumeric && CommonNetFuncs.Core.Strings.IsAlphanumeric(parameter)) || (onlyAlphaChars && CommonNetFuncs.Core.Strings.IsAlphaOnly(parameter)) ||
					(onlyNumberChars && CommonNetFuncs.Core.Strings.IsNumericOnly(parameter)))
				{
					result = parameter;
				}
			}
			else
			{
				result = parameter;
			}
		}

		if (result == null)
		{
			return defaultValue;
		}

		return result.AsSpan().IndexOfAny('\'', '%', '*') < 0 ? result : result.Replace("'", "''").Replace("%", string.Empty).Replace("*", string.Empty);
	}
}

#endregion

#region EFCore

/// <summary>GetByKey/DeleteByKey with disabled filters: key inlined as ConstantExpression (EF query-cache miss + recompile per distinct key) vs captured-value member access (EF parameter).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdKeyQueryParameterizationBenchmarks
{
	private const int LookupsPerInvocation = 10;
	private const int KeySpace = 1_000_000;
	private DbContextOptions<PdEfContext> options = null!;
	private int nextKey;

	[GlobalSetup]
	public void Setup()
	{
		options = PdEf.CreateSeededOptions(1000);
	}

	// Distinct keys per call, as in real traffic (a small repeating key set would hide the recompilation in EF's query cache)
	private int NextKey()
	{
		nextKey = (nextKey % KeySpace) + 1;
		return nextKey;
	}

	[Benchmark(Baseline = true)]
	public int Original_ConstantKey()
	{
		using PdEfContext context = new(options);
		int found = 0;
		for (int i = 0; i < LookupsPerInvocation; i++)
		{
			if (context.Set<PdKeyEntity>().Where(PdKeyBuilders.Original(context, NextKey())).IgnoreQueryFilters().FirstOrDefault() != null)
			{
				found++;
			}
		}
		return found;
	}

	[Benchmark]
	public int Optimized_ConstantKeyNoClosure()
	{
		using PdEfContext context = new(options);
		int found = 0;
		for (int i = 0; i < LookupsPerInvocation; i++)
		{
			if (context.Set<PdKeyEntity>().Where(PdKeyBuilders.OptimizedConstant(context, NextKey())).IgnoreQueryFilters().FirstOrDefault() != null)
			{
				found++;
			}
		}
		return found;
	}

	[Benchmark]
	public int Optimized_ParameterizedKey()
	{
		using PdEfContext context = new(options);
		int found = 0;
		for (int i = 0; i < LookupsPerInvocation; i++)
		{
			if (context.Set<PdKeyEntity>().Where(PdKeyBuilders.OptimizedParameterized(context, NextKey())).IgnoreQueryFilters().FirstOrDefault() != null)
			{
				found++;
			}
		}
		return found;
	}
}

/// <summary>GetByKey/DeleteByKey expression builder: capturing GetOrAdd lambda (closure + delegate per call) + Expression.Property(name) reflection per call vs static factory + cached PropertyInfo.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdKeyExpressionBuilderBenchmarks
{
	private const int KeysPerInvocation = 100;
	private PdEfContext context = null!;

	[GlobalSetup]
	public void Setup()
	{
		context = new PdEfContext(PdEf.CreateOptions());
		Original();
		Optimized_StaticFactoryCachedProperty();
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		context.Dispose();
	}

	[Benchmark(Baseline = true)]
	public int Original()
	{
		int total = 0;
		for (int i = 1; i <= KeysPerInvocation; i++)
		{
			total += PdKeyBuilders.Original(context, i).Parameters.Count;
		}
		return total;
	}

	[Benchmark]
	public int Optimized_StaticFactoryCachedProperty()
	{
		int total = 0;
		for (int i = 1; i <= KeysPerInvocation; i++)
		{
			total += PdKeyBuilders.OptimizedConstant(context, i).Parameters.Count;
		}
		return total;
	}
}

/// <summary>NavigationProperties.RemoveNavigationProperties per entity: capturing GetOrAdd lambda (closure + delegate per call) vs static lambda with factory argument.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdRemoveNavigationPropertiesBenchmarks
{
	private PdEfContext context = null!;
	private List<PdNavParent> entities = null!;

	[GlobalSetup]
	public void Setup()
	{
		context = new PdEfContext(PdEf.CreateOptions());
		entities = PdEf.CreateNavParents(1000);
		PdNavigationRemoval.Original(entities[0], context);
		PdNavigationRemoval.Optimized(entities[0], context);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		context.Dispose();
	}

	[Benchmark(Baseline = true)]
	public int Original_CapturingFactory()
	{
		foreach (PdNavParent entity in entities)
		{
			PdNavigationRemoval.Original(entity, context);
		}
		return entities.Count;
	}

	[Benchmark]
	public int Optimized_StaticFactory()
	{
		foreach (PdNavParent entity in entities)
		{
			PdNavigationRemoval.Optimized(entity, context);
		}
		return entities.Count;
	}
}

/// <summary>Collections.GetObjectByPartial expression building: GetProperties + Activator.CreateInstance per value-type property per call vs per-type cached properties and boxed defaults.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdObjectByPartialBenchmarks
{
	private PdEfContext context = null!;
	private readonly PdPartialEntity partial = new() { Name = "Alice", Created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local), Amount = 12.5m, Count = 3, Active = true };

	[GlobalSetup]
	public void Setup()
	{
		context = new PdEfContext(PdEf.CreateOptions());
		Original();
		Optimized_CachedPropertiesAndDefaults();
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		context.Dispose();
	}

	[Benchmark(Baseline = true)]
	public Expression? Original()
	{
		return PdObjectByPartial.Original(context, partial).conditions;
	}

	[Benchmark]
	public Expression? Optimized_CachedPropertiesAndDefaults()
	{
		return PdObjectByPartial.Optimized(context, partial).conditions;
	}
}

/// <summary>Navigation include-path cache hit (every "Full" query): ignore-string built twice + LINQ/closure filter vs reuse key + allocation-light filter.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdNavigationsCacheHitBenchmarks
{
	private PdEfContext context = null!;
	private NavigationPropertiesOptions options = null!;

	[Params(false, true)]
	public bool WithIgnoreList { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		context = new PdEfContext(PdEf.CreateOptions());
		options = WithIgnoreList
			? new NavigationPropertiesOptions(navPropAttributesToIgnore: [typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), typeof(System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute)])
			: new NavigationPropertiesOptions();
		PdNavigationsCache.OriginalGetNavigations<PdNavParent>(context, options);
		PdNavigationsCache.OptimizedGetNavigations<PdNavParent>(context, options);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		context.Dispose();
	}

	[Benchmark(Baseline = true)]
	public int Original()
	{
		return PdNavigationsCache.OriginalGetNavigations<PdNavParent>(context, options).Count;
	}

	[Benchmark]
	public int Optimized_ReuseKeyManualFilter()
	{
		return PdNavigationsCache.OptimizedGetNavigations<PdNavParent>(context, options).Count;
	}
}

public sealed class PdKeyEntity
{
	public int Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public bool IsDeleted { get; set; }
}

public sealed class PdPartialEntity
{
	public int Id { get; set; }
	public string? Name { get; set; }
	public string? Email { get; set; }
	public DateTime Created { get; set; }
	public decimal Amount { get; set; }
	public bool Active { get; set; }
	public int? Count { get; set; }
	public Guid Key { get; set; }
	public long Big { get; set; }
	public double Ratio { get; set; }
}

public sealed class PdNavParent
{
	public int Id { get; set; }
	public string? Name { get; set; }
	public List<PdNavChild>? Children { get; set; }
	public int? FavoriteId { get; set; }
	public PdNavChild? Favorite { get; set; }
}

public sealed class PdNavChild
{
	public int Id { get; set; }
	public int ParentId { get; set; }
	public PdNavParent? Parent { get; set; }
	public string? Name { get; set; }
	public List<PdNavItem>? Items { get; set; }
}

public sealed class PdNavItem
{
	public int Id { get; set; }
	public int ChildId { get; set; }
	public PdNavChild? Child { get; set; }
	public string? Label { get; set; }
}

public sealed class PdEfContext(DbContextOptions<PdEfContext> options) : DbContext(options)
{
	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<PdKeyEntity>().HasKey(e => e.Id);
		modelBuilder.Entity<PdKeyEntity>().HasQueryFilter(e => !e.IsDeleted);
		modelBuilder.Entity<PdPartialEntity>().HasKey(e => e.Id);
		modelBuilder.Entity<PdNavParent>(builder =>
		{
			builder.HasKey(p => p.Id);
			builder.HasMany(p => p.Children).WithOne(c => c.Parent).HasForeignKey(c => c.ParentId);
			builder.HasOne(p => p.Favorite).WithMany().HasForeignKey(p => p.FavoriteId);
		});
		modelBuilder.Entity<PdNavChild>(builder =>
		{
			builder.HasKey(c => c.Id);
			builder.HasMany(c => c.Items).WithOne(i => i.Child).HasForeignKey(i => i.ChildId);
		});
		modelBuilder.Entity<PdNavItem>().HasKey(i => i.Id);
	}
}

internal static class PdEf
{
	public static DbContextOptions<PdEfContext> CreateOptions()
	{
		return new DbContextOptionsBuilder<PdEfContext>().UseInMemoryDatabase("Pd_" + Guid.NewGuid().ToString("N")).Options;
	}

	public static DbContextOptions<PdEfContext> CreateSeededOptions(int keyRows)
	{
		DbContextOptions<PdEfContext> options = CreateOptions();
		using PdEfContext context = new(options);
		for (int i = 1; i <= keyRows; i++)
		{
			context.Add(new PdKeyEntity { Id = i, Name = "Key" + i, IsDeleted = i % 10 == 0 });
		}
		context.SaveChanges();
		return options;
	}

	public static List<PdNavParent> CreateNavParents(int count)
	{
		return Enumerable.Range(1, count).Select(i => new PdNavParent
		{
			Id = i,
			Name = "P" + i,
			Children = [new PdNavChild { Id = i, Name = "C" + i, Items = [new PdNavItem { Id = i, Label = "I" + i }] }],
			Favorite = new PdNavChild { Id = -i, Name = "F" + i }
		}).ToList();
	}
}

/// <summary>Copy of internal EFCore EntityKeyMetadata.</summary>
internal sealed class PdEntityKeyMetadata
{
	public string? KeyPropertyName { get; set; }

	public Type? KeyPropertyType { get; set; }
}

internal static class PdKeyBuilders
{
	private static readonly ConcurrentDictionary<Type, PdEntityKeyMetadata> originalEntityKeyCache = new();
	private static readonly ConcurrentDictionary<Type, Func<object, Expression<Func<PdKeyEntity, bool>>>> originalSingleKeyExpressionBuilder = new();

	private static readonly ConcurrentDictionary<Type, PdEntityKeyMetadata> optimizedEntityKeyCache = new();
	private static readonly ConcurrentDictionary<Type, Func<object, Expression<Func<PdKeyEntity, bool>>>> constantKeyBuilders = new();
	private static readonly ConcurrentDictionary<Type, Func<object, Expression<Func<PdKeyEntity, bool>>>> parameterizedKeyBuilders = new();
	private static readonly MethodInfo CreateKeyAccessMethod = typeof(PdKeyBuilders).GetMethod(nameof(CreateKeyAccess), BindingFlags.NonPublic | BindingFlags.Static)!;

	// Verbatim copy of the single-key expression branch of BaseDbContextActions.GetByKey(object) / DeleteByKey (TEntity = PdKeyEntity)
	public static Expression<Func<PdKeyEntity, bool>> Original(DbContext context, object primaryKey)
	{
		Func<object, Expression<Func<PdKeyEntity, bool>>> expressionBuilder = originalSingleKeyExpressionBuilder.GetOrAdd(typeof(PdKeyEntity), type =>
		{
			PdEntityKeyMetadata keyMetadata = originalEntityKeyCache.GetOrAdd(type, t =>
			{
				IEntityType? entityType = context.Model.FindEntityType(t);
				IKey? primaryKey_Property = entityType?.FindPrimaryKey();

				if (primaryKey_Property == null || primaryKey_Property.Properties.Count != 1)
				{
					throw new InvalidOperationException($"Entity {t.Name} does not have a single-field primary key. Use the array overload instead.");
				}

				return new PdEntityKeyMetadata
				{
					KeyPropertyName = primaryKey_Property.Properties[0].Name,
					KeyPropertyType = primaryKey_Property.Properties[0].ClrType
				};
			});

			return (keyValue) =>
			{
				ParameterExpression parameter = Expression.Parameter(typeof(PdKeyEntity), "x");
				MemberExpression property = Expression.Property(parameter, keyMetadata.KeyPropertyName!);
				ConstantExpression constant = Expression.Constant(keyValue, keyMetadata.KeyPropertyType!);
				BinaryExpression equality = Expression.Equal(property, constant);
				return Expression.Lambda<Func<PdKeyEntity, bool>>(equality, parameter);
			};
		});

		return expressionBuilder(primaryKey);
	}

	// Proposed (closure fix only): static factory with factory argument (no per-call closure/delegate) + PropertyInfo resolved once. Same ConstantExpression output.
	public static Expression<Func<PdKeyEntity, bool>> OptimizedConstant(DbContext context, object primaryKey)
	{
		return constantKeyBuilders.GetOrAdd(typeof(PdKeyEntity), static (type, ctx) => CreateSingleKeyBuilder(type, ctx, parameterize: false), context)(primaryKey);
	}

	// Proposed (full): as above, but the key is read from a StrongBox field so EF funcletizes it into a query parameter
	public static Expression<Func<PdKeyEntity, bool>> OptimizedParameterized(DbContext context, object primaryKey)
	{
		return parameterizedKeyBuilders.GetOrAdd(typeof(PdKeyEntity), static (type, ctx) => CreateSingleKeyBuilder(type, ctx, parameterize: true), context)(primaryKey);
	}

	private static Func<object, Expression<Func<PdKeyEntity, bool>>> CreateSingleKeyBuilder(Type type, DbContext context, bool parameterize)
	{
		PdEntityKeyMetadata keyMetadata = optimizedEntityKeyCache.GetOrAdd(type, static (t, ctx) =>
		{
			IEntityType? entityType = ctx.Model.FindEntityType(t);
			IKey? primaryKey_Property = entityType?.FindPrimaryKey();

			if (primaryKey_Property == null || primaryKey_Property.Properties.Count != 1)
			{
				throw new InvalidOperationException($"Entity {t.Name} does not have a single-field primary key. Use the array overload instead.");
			}

			return new PdEntityKeyMetadata
			{
				KeyPropertyName = primaryKey_Property.Properties[0].Name,
				KeyPropertyType = primaryKey_Property.Properties[0].ClrType
			};
		}, context);

		Type keyType = keyMetadata.KeyPropertyType!;
		// Resolved through the same Expression.Property(Expression, string) lookup the original performs on every call
		PropertyInfo keyProperty = (PropertyInfo)Expression.Property(Expression.Parameter(typeof(PdKeyEntity), "x"), keyMetadata.KeyPropertyName!).Member;

		if (!parameterize)
		{
			return keyValue =>
			{
				ParameterExpression parameter = Expression.Parameter(typeof(PdKeyEntity), "x");
				BinaryExpression equality = Expression.Equal(Expression.Property(parameter, keyProperty), Expression.Constant(keyValue, keyType));
				return Expression.Lambda<Func<PdKeyEntity, bool>>(equality, parameter);
			};
		}

		Func<object?, Expression> createKeyAccess = (Func<object?, Expression>)CreateKeyAccessMethod.MakeGenericMethod(keyType).CreateDelegate(typeof(Func<object, Expression>));
		return keyValue =>
		{
			ParameterExpression parameter = Expression.Parameter(typeof(PdKeyEntity), "x");
			_ = Expression.Constant(keyValue, keyType); // Keeps the original's argument validation (same ArgumentException for a mismatched key type)
			BinaryExpression equality = Expression.Equal(Expression.Property(parameter, keyProperty), createKeyAccess(keyValue));
			return Expression.Lambda<Func<PdKeyEntity, bool>>(equality, parameter);
		};
	}

	private static MemberExpression CreateKeyAccess<TKey>(object? keyValue)
	{
		return Expression.Field(Expression.Constant(new StrongBox<TKey>((TKey)keyValue!)), nameof(StrongBox<TKey>.Value));
	}
}

internal static class PdNavigationRemoval
{
	private static readonly ConcurrentDictionary<Type, Action<object>> OriginalNavigationSetterCache = new();
	private static readonly ConcurrentDictionary<Type, Action<object>> OptimizedNavigationSetterCache = new();

	// Verbatim copy of NavigationProperties.RemoveNavigationProperties (own setter cache)
	public static void Original<T>(T? obj, DbContext context) where T : class
	{
		if (obj == null)
		{
			return;
		}

		Action<T> setter = OriginalNavigationSetterCache.GetOrAdd(typeof(T), type =>
		{
			List<string> navigations = NavigationProperties.GetTopLevelNavigations<T>(context);

			ParameterExpression parameter = Expression.Parameter(typeof(object), "entity");
			UnaryExpression convertedParameter = Expression.Convert(parameter, type);

			List<BinaryExpression> assignments = [];
			foreach (string navProp in navigations)
			{
				PropertyInfo? property = type.GetProperty(navProp);
				if (property?.CanWrite == true)
				{
					assignments.Add(Expression.Assign(Expression.Property(convertedParameter, property), Expression.Constant(null, property.PropertyType)));
				}
			}

			if (!CommonNetFuncs.Core.Collections.AnyFast(assignments))
			{
				return new Action<object>(_ => { });
			}

			if (!CommonNetFuncs.Core.Collections.AnyFast(assignments))
			{
				return new Action<object>(_ => { });
			}

			BlockExpression block = Expression.Block(assignments);

			return Expression.Lambda<Action<object>>(block, parameter).CompileFast();
		});

		setter(obj);
	}

	// Proposed: static lambda + factory argument -> no closure/delegate allocation per entity (the display class is otherwise allocated even for null obj)
	public static void Optimized<T>(T? obj, DbContext context) where T : class
	{
		if (obj == null)
		{
			return;
		}

		Action<T> setter = OptimizedNavigationSetterCache.GetOrAdd(typeof(T), static (type, ctx) => CreateSetter<T>(type, ctx), context);
		setter(obj);
	}

	private static Action<object> CreateSetter<T>(Type type, DbContext context) where T : class
	{
		List<string> navigations = NavigationProperties.GetTopLevelNavigations<T>(context);

		ParameterExpression parameter = Expression.Parameter(typeof(object), "entity");
		UnaryExpression convertedParameter = Expression.Convert(parameter, type);

		List<BinaryExpression> assignments = [];
		foreach (string navProp in navigations)
		{
			PropertyInfo? property = type.GetProperty(navProp);
			if (property?.CanWrite == true)
			{
				assignments.Add(Expression.Assign(Expression.Property(convertedParameter, property), Expression.Constant(null, property.PropertyType)));
			}
		}

		if (assignments.Count == 0)
		{
			return new Action<object>(_ => { });
		}

		return Expression.Lambda<Action<object>>(Expression.Block(assignments), parameter).CompileFast();
	}
}

/// <summary>Per-type cache of public instance properties and lazily computed boxed defaults (same Activator.CreateInstance call as the original, made once).</summary>
internal static class PdPartialPropertyCache<T>
{
	private static readonly object Unset = new();
	internal static readonly PropertyInfo[] Properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
	private static readonly object?[] Defaults = CreateUnsetDefaults();

	private static object?[] CreateUnsetDefaults()
	{
		object?[] defaults = new object?[Properties.Length];
		Array.Fill(defaults, Unset);
		return defaults;
	}

	internal static object? GetDefault(int index)
	{
		object? value = Defaults[index];
		if (ReferenceEquals(value, Unset))
		{
			Type propertyType = Properties[index].PropertyType;
			value = propertyType.IsValueType ? Activator.CreateInstance(propertyType) : null;
			Defaults[index] = value;
		}
		return value;
	}
}

internal static class PdObjectByPartial
{
	// Verbatim copy of EFCore Collections.GetObjectByPartialExpression
	public static (Expression? conditions, ParameterExpression parameter) Original<T>(DbContext context, T partialObject, bool ignoreDefaultValues = true, CancellationToken cancellationToken = default)
	{
		Type entityType = typeof(T);
		PropertyInfo[] properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
		ParameterExpression parameter = Expression.Parameter(entityType, "$x");
		Expression? conditions = null;

		IEntityType? entityTypeMetadata = context.Model.FindEntityType(entityType);

		foreach (PropertyInfo property in properties)
		{
			cancellationToken.ThrowIfCancellationRequested();

			object? partialValue = property.GetValue(partialObject);

			if (partialValue is null)
			{
				continue;
			}

			if (ignoreDefaultValues)
			{
				object? defaultValue = property.PropertyType.IsValueType ? Activator.CreateInstance(property.PropertyType) : null;
				if (Equals(partialValue, defaultValue))
				{
					continue;
				}
			}

			if (partialValue is DateTime dateTimeValue)
			{
				partialValue = NormalizeDateTimeForDatabase(dateTimeValue, property, entityTypeMetadata, context);
			}

			BinaryExpression condition = Expression.Equal(Expression.Property(parameter, property), Expression.Constant(partialValue, property.PropertyType));

			conditions = conditions == null ? condition : Expression.AndAlso(conditions, condition);
		}

		return (conditions, parameter);
	}

	// Proposed: identical loop, but properties and boxed defaults come from a per-T static cache
	public static (Expression? conditions, ParameterExpression parameter) Optimized<T>(DbContext context, T partialObject, bool ignoreDefaultValues = true, CancellationToken cancellationToken = default)
	{
		Type entityType = typeof(T);
		PropertyInfo[] properties = PdPartialPropertyCache<T>.Properties;
		ParameterExpression parameter = Expression.Parameter(entityType, "$x");
		Expression? conditions = null;

		IEntityType? entityTypeMetadata = context.Model.FindEntityType(entityType);

		for (int i = 0; i < properties.Length; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();

			PropertyInfo property = properties[i];
			object? partialValue = property.GetValue(partialObject);

			if (partialValue is null)
			{
				continue;
			}

			if (ignoreDefaultValues && Equals(partialValue, PdPartialPropertyCache<T>.GetDefault(i)))
			{
				continue;
			}

			if (partialValue is DateTime dateTimeValue)
			{
				partialValue = NormalizeDateTimeForDatabase(dateTimeValue, property, entityTypeMetadata, context);
			}

			BinaryExpression condition = Expression.Equal(Expression.Property(parameter, property), Expression.Constant(partialValue, property.PropertyType));

			conditions = conditions == null ? condition : Expression.AndAlso(conditions, condition);
		}

		return (conditions, parameter);
	}

	// Verbatim copy of EFCore Collections.NormalizeDateTimeForDatabase
	private static DateTime NormalizeDateTimeForDatabase(DateTime dateTimeValue, PropertyInfo property, IEntityType? entityTypeMetadata, DbContext context)
	{
		IProperty? efProperty = entityTypeMetadata?.FindProperty(property.Name);

		if (efProperty == null)
		{
			return dateTimeValue.ToUniversalTime();
		}

		string? providerName = context.Database.ProviderName?.ToLowerInvariant();

		if (providerName?.Contains("inmemory") == true)
		{
			return dateTimeValue.Kind == DateTimeKind.Local ? dateTimeValue.ToUniversalTime() : dateTimeValue;
		}

		string? storeType;
		try
		{
			storeType = efProperty.GetColumnType()?.ToLowerInvariant();
		}
		catch (InvalidCastException)
		{
			return dateTimeValue.ToUniversalTime();
		}

		if (string.IsNullOrEmpty(storeType))
		{
			return dateTimeValue.ToUniversalTime();
		}

		if (providerName?.Contains("postgres") == true || providerName?.Contains("npgsql") == true)
		{
			return dateTimeValue.ToUniversalTime();
		}
		else if (providerName?.Contains("sqlserver") == true)
		{
			bool isTimezoneAware = storeType.Contains("datetimeoffset");
			return isTimezoneAware ? dateTimeValue.ToUniversalTime() : DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Unspecified);
		}
		else if (providerName?.Contains("mysql") == true)
		{
			return DateTime.SpecifyKind(dateTimeValue, DateTimeKind.Unspecified);
		}
		else
		{
			return dateTimeValue.ToUniversalTime();
		}
	}
}

internal static class PdNavigationsCache
{
	private static readonly CommonNetFuncs.Core.CacheManager<NavigationProperties.NavigationPropertiesCacheKey, NavigationProperties.NavigationPropertiesCacheValue> OriginalNavigationCache = new();
	private static readonly CommonNetFuncs.Core.CacheManager<NavigationProperties.NavigationPropertiesCacheKey, NavigationProperties.NavigationPropertiesCacheValue> OptimizedNavigationCache = new();

	private static CommonNetFuncs.Core.ICacheManagerApi<NavigationProperties.NavigationPropertiesCacheKey, NavigationProperties.NavigationPropertiesCacheValue> OriginalNavigationCacheManager => OriginalNavigationCache;

	private static CommonNetFuncs.Core.ICacheManagerApi<NavigationProperties.NavigationPropertiesCacheKey, NavigationProperties.NavigationPropertiesCacheValue> OptimizedNavigationCacheManager => OptimizedNavigationCache;

	// Verbatim copy of NavigationProperties.GetNavigations<T> (own cache)
	public static HashSet<string> OriginalGetNavigations<T>(DbContext context, NavigationPropertiesOptions? navigationPropertiesOptions = null) where T : class
	{
		navigationPropertiesOptions ??= new();

		return navigationPropertiesOptions.UseCaching ?
			OriginalGetOrAddPropertiesFromEntityNavigationsCache<T>(new(typeof(T), CreateNavPropsIgnoreString(navigationPropertiesOptions.NavPropAttributesToIgnore)), context, navigationPropertiesOptions) :
			GetNewNavigations<T>(context, navigationPropertiesOptions);
	}

	// Verbatim copy of NavigationProperties.GetOrAddPropertiesFromEntityNavigationsCache<T> (key recomputed inside, LINQ depth filter)
	private static HashSet<string> OriginalGetOrAddPropertiesFromEntityNavigationsCache<T>(NavigationProperties.NavigationPropertiesCacheKey key, DbContext context, NavigationPropertiesOptions navigationPropertiesOptions) where T : class
	{
		if ((OriginalNavigationCacheManager.IsUsingLimitedCache() ?
					OriginalNavigationCacheManager.GetLimitedCache().TryGetValue(new(typeof(T), CreateNavPropsIgnoreString(navigationPropertiesOptions.NavPropAttributesToIgnore)), out NavigationProperties.NavigationPropertiesCacheValue cachedValue) :
					OriginalNavigationCacheManager.GetCache().TryGetValue(new(typeof(T), CreateNavPropsIgnoreString(navigationPropertiesOptions.NavPropAttributesToIgnore)), out cachedValue)) &&
				(cachedValue.MaxDepth < 0 || cachedValue.MaxDepth >= navigationPropertiesOptions.MaxNavigationDepth))
		{
			return cachedValue.GetNavigationsToDepth(navigationPropertiesOptions.MaxNavigationDepth);
		}

		HashSet<string> navigations = GetNewNavigations<T>(context, navigationPropertiesOptions);
		if (OriginalNavigationCacheManager.IsUsingLimitedCache())
		{
			OriginalNavigationCacheManager.TryAddLimitedCache(key, new(navigations, navigationPropertiesOptions.MaxNavigationDepth));
		}
		else
		{
			OriginalNavigationCacheManager.TryAddCache(key, new(navigations, navigationPropertiesOptions.MaxNavigationDepth));
		}
		return navigations;
	}

	public static HashSet<string> OptimizedGetNavigations<T>(DbContext context, NavigationPropertiesOptions? navigationPropertiesOptions = null) where T : class
	{
		navigationPropertiesOptions ??= new();

		return navigationPropertiesOptions.UseCaching ?
			OptimizedGetOrAddPropertiesFromEntityNavigationsCache<T>(new(typeof(T), CreateNavPropsIgnoreString(navigationPropertiesOptions.NavPropAttributesToIgnore)), context, navigationPropertiesOptions) :
			GetNewNavigations<T>(context, navigationPropertiesOptions);
	}

	// Proposed: look up with the key the caller already built, and filter with a plain loop
	private static HashSet<string> OptimizedGetOrAddPropertiesFromEntityNavigationsCache<T>(NavigationProperties.NavigationPropertiesCacheKey key, DbContext context, NavigationPropertiesOptions navigationPropertiesOptions) where T : class
	{
		if ((OptimizedNavigationCacheManager.IsUsingLimitedCache() ?
					OptimizedNavigationCacheManager.GetLimitedCache().TryGetValue(key, out NavigationProperties.NavigationPropertiesCacheValue cachedValue) :
					OptimizedNavigationCacheManager.GetCache().TryGetValue(key, out cachedValue)) &&
				(cachedValue.MaxDepth < 0 || cachedValue.MaxDepth >= navigationPropertiesOptions.MaxNavigationDepth))
		{
			return GetNavigationsToDepth(cachedValue.NavigationProperties, navigationPropertiesOptions.MaxNavigationDepth);
		}

		HashSet<string> navigations = GetNewNavigations<T>(context, navigationPropertiesOptions);
		if (OptimizedNavigationCacheManager.IsUsingLimitedCache())
		{
			OptimizedNavigationCacheManager.TryAddLimitedCache(key, new(navigations, navigationPropertiesOptions.MaxNavigationDepth));
		}
		else
		{
			OptimizedNavigationCacheManager.TryAddCache(key, new(navigations, navigationPropertiesOptions.MaxNavigationDepth));
		}
		return navigations;
	}

	// Proposed body for NavigationPropertiesCacheValue.GetNavigationsToDepth: same predicate as HasNoMoreThanNumberOfChars('.', depth), no closure/iterator, pre-sized
	internal static HashSet<string> GetNavigationsToDepth(HashSet<string> navigationProperties, int depth)
	{
		if (depth < 0)
		{
			return navigationProperties;
		}

		HashSet<string> result = new(navigationProperties.Count);
		foreach (string navigation in navigationProperties)
		{
			if (HasNoMoreThanDots(navigation, depth))
			{
				result.Add(navigation);
			}
		}
		return result;
	}

	private static bool HasNoMoreThanDots(string? value, int maxDots)
	{
		if (string.IsNullOrEmpty(value))
		{
			return true;
		}

		int count = 0;
		foreach (char c in value)
		{
			if (c == '.' && ++count > maxDots)
			{
				return false;
			}
		}
		return true;
	}

	// Equivalent to the private NavigationProperties.GetNewNavigations<T> (only runs on a cache miss)
	private static HashSet<string> GetNewNavigations<T>(DbContext context, NavigationPropertiesOptions navigationPropertiesOptions) where T : class
	{
		return NavigationProperties.GetNavigations<T>(context, new NavigationPropertiesOptions(navigationPropertiesOptions.MaxNavigationDepth, navigationPropertiesOptions.NavPropAttributesToIgnore, false));
	}

	// Verbatim copy of NavigationProperties.CreateNavPropsIgnoreString
	private static string? CreateNavPropsIgnoreString(List<Type>? navPropAttributesToIgnore)
	{
		return navPropAttributesToIgnore != null ? string.Join("|", navPropAttributesToIgnore.OrderBy(x => x.Name)) : null;
	}
}

/// <summary>Deterministic textual dump of an expression tree (includes DateTime.Kind and constant types) used by PdVerify.</summary>
internal sealed class PdExpressionDescriber : ExpressionVisitor
{
	private readonly StringBuilder builder = new();

	public static string Describe(Expression? expression)
	{
		if (expression == null)
		{
			return "<null>";
		}

		PdExpressionDescriber describer = new();
		describer.Visit(expression);
		return describer.builder.ToString();
	}

	public override Expression? Visit(Expression? node)
	{
		if (node != null)
		{
			builder.Append('[').Append(node.NodeType).Append(':').Append(node.Type.FullName).Append(']');
		}
		return base.Visit(node);
	}

	protected override Expression VisitConstant(ConstantExpression node)
	{
		builder.Append("C(").Append(FormatValue(node.Value)).Append(')');
		return base.VisitConstant(node);
	}

	protected override Expression VisitMember(MemberExpression node)
	{
		builder.Append("M(").Append(node.Member.DeclaringType?.FullName).Append('.').Append(node.Member.Name).Append(')');
		return base.VisitMember(node);
	}

	protected override Expression VisitParameter(ParameterExpression node)
	{
		builder.Append("P(").Append(node.Name).Append(')');
		return base.VisitParameter(node);
	}

	protected override Expression VisitBinary(BinaryExpression node)
	{
		builder.Append("B(").Append(node.Method?.Name).Append(')');
		return base.VisitBinary(node);
	}

	private static string FormatValue(object? value)
	{
		return value switch
		{
			null => "null",
			DateTime dateTime => dateTime.Ticks.ToString(CultureInfo.InvariantCulture) + "/" + dateTime.Kind,
			IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
			_ => value.ToString() ?? string.Empty
		};
	}
}

#endregion

#region FastMap

/// <summary>FastMapper dictionary value mapping: boxed IEnumerator&lt;KVP&gt; + unsized destination vs Dictionary struct enumerator + capacity-presized destination.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PdFastMapDictionaryBenchmarks
{
	private PdMapSource source = null!;
	private Func<PdMapSource, PdMapDest> originalMapper = null!;
	private Func<PdMapSource, PdMapDest> optimizedMapper = null!;

	[Params(8, 100)]
	public int Entries { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		source = PdFastMapData.CreateSource(Entries);
		originalMapper = new PdFastMapBuilder().CreateMapper<PdMapSource, PdMapDest>();
		optimizedMapper = new PdFastMapBuilderOptimized().CreateMapper<PdMapSource, PdMapDest>();
	}

	[Benchmark(Baseline = true)]
	public PdMapDest Original_InterfaceEnumerator()
	{
		return originalMapper(source);
	}

	[Benchmark]
	public PdMapDest Optimized_StructEnumeratorPresized()
	{
		return optimizedMapper(source);
	}
}

public sealed class PdMapItem
{
	public int A { get; set; }
	public string? B { get; set; }
	public double C { get; set; }
}

public sealed class PdMapItemDto
{
	public int A { get; set; }
	public string? B { get; set; }
	public double C { get; set; }
}

public sealed class PdMapSource
{
	public int Id { get; set; }
	public Dictionary<string, PdMapItem>? Items { get; set; }
}

public sealed class PdMapDest
{
	public int Id { get; set; }
	public Dictionary<string, PdMapItemDto>? Items { get; set; }
}

internal static class PdFastMapData
{
	public static PdMapSource CreateSource(int entries)
	{
		return new PdMapSource
		{
			Id = entries,
			Items = Enumerable.Range(0, entries).ToDictionary(i => "k" + i, i => i % 10 == 9 ? null! : new PdMapItem { A = i, B = "b" + i, C = i * 0.5 })
		};
	}
}

/// <summary>Verbatim copy of the FastMapper expression builder (instance methods so the dictionary path can be overridden).</summary>
internal class PdFastMapBuilder
{
	public Func<TSource, TDest> CreateMapper<TSource, TDest>()
	{
		Type sourceType = typeof(TSource);
		Type destType = typeof(TDest);

		ParameterExpression sourceParam = Expression.Parameter(sourceType, "src");
		Expression body = CreateMappingExpression(sourceParam, sourceType, destType);

		Expression<Func<TSource, TDest>> lambda = Expression.Lambda<Func<TSource, TDest>>(body, sourceParam);
		return lambda.CompileFast();
	}

	protected Expression CreateMappingExpression(Expression source, Type sourceType, Type destType)
	{
		if (IsCollection(sourceType) && IsCollection(destType))
		{
			return CreateCollectionMappingExpression(source, sourceType, destType);
		}

		if (IsDictionary(sourceType) && IsDictionary(destType))
		{
			return CreateDictionaryMappingExpression(source, sourceType, destType);
		}

		return CreateObjectMappingExpression(source, sourceType, destType);
	}

	private MemberInitExpression CreateObjectMappingExpression(Expression source, Type sourceType, Type destType)
	{
		PropertyInfo[] sourceProps = GetProperties(sourceType);
		PropertyInfo[] destProps = GetProperties(destType);

		List<MemberBinding> bindings = [];

		foreach (PropertyInfo destProp in destProps)
		{
			if (!destProp.CanWrite)
			{
				continue;
			}

			PropertyInfo? sourceProp = Array.Find(sourceProps, p => p.CanRead && p.Name == destProp.Name);
			if (sourceProp is null)
			{
				continue;
			}

			MemberExpression sourceAccess = Expression.Property(source, sourceProp);
			Expression valueExpr;

			if (sourceProp.PropertyType == destProp.PropertyType)
			{
				valueExpr = sourceAccess;
			}
			else if (IsCollection(sourceProp.PropertyType) && IsCollection(destProp.PropertyType))
			{
				valueExpr = CreateCollectionMappingExpression(sourceAccess, sourceProp.PropertyType, destProp.PropertyType);

				if (!sourceProp.PropertyType.IsValueType)
				{
					valueExpr = Expression.Condition(
							Expression.Equal(sourceAccess, Expression.Constant(null, sourceProp.PropertyType)),
							Expression.Default(destProp.PropertyType),
							valueExpr
						);
				}
			}
			else if (IsDictionary(sourceProp.PropertyType) && IsDictionary(destProp.PropertyType))
			{
				valueExpr = CreateDictionaryMappingExpression(sourceAccess, sourceProp.PropertyType, destProp.PropertyType);

				if (!sourceProp.PropertyType.IsValueType)
				{
					valueExpr = Expression.Condition(Expression.Equal(sourceAccess, Expression.Constant(null, sourceProp.PropertyType)),
							Expression.Default(destProp.PropertyType),
							valueExpr
						);
				}
			}
			else if (!sourceProp.PropertyType.IsValueType && !destProp.PropertyType.IsValueType)
			{
				valueExpr = CreateObjectMappingExpression(sourceAccess, sourceProp.PropertyType, destProp.PropertyType);

				valueExpr = Expression.Condition(
					Expression.Equal(sourceAccess, Expression.Constant(null, sourceProp.PropertyType)),
					Expression.Default(destProp.PropertyType),
					valueExpr);
			}
			else
			{
				continue;
			}

			bindings.Add(Expression.Bind(destProp, valueExpr));
		}

		NewExpression newExpr = Expression.New(destType);
		return Expression.MemberInit(newExpr, bindings);
	}

	private Expression CreateCollectionMappingExpression(Expression source, Type sourceType, Type destType)
	{
		Type sourceElemType = GetElementType(sourceType);
		Type destElemType = GetElementType(destType);

		if (sourceElemType == destElemType)
		{
			return CreateSameTypeCollectionCopy(source, destType, sourceElemType);
		}

		return CreateMappedCollectionExpression(source, destType, sourceElemType, destElemType);
	}

	private static Expression CreateSameTypeCollectionCopy(Expression source, Type destType, Type elemType)
	{
		if (destType.IsArray)
		{
			MethodInfo toArrayMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(elemType);
			return Expression.Call(null, toArrayMethod, source);
		}

		if (destType.IsGenericType && destType.GetGenericTypeDefinition() == typeof(List<>))
		{
			ConstructorInfo listCtor = destType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elemType)])!;
			return Expression.New(listCtor, source);
		}

		if (destType.IsGenericType && destType.GetGenericTypeDefinition() == typeof(HashSet<>))
		{
			ConstructorInfo hashSetCtor = destType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elemType)])!;
			return Expression.New(hashSetCtor, source);
		}

		MethodInfo toListMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(elemType);
		return Expression.Call(null, toListMethod, source);
	}

	private MethodCallExpression CreateMappedCollectionExpression(Expression source, Type destType, Type sourceElemType, Type destElemType)
	{
		ParameterExpression itemParam = Expression.Parameter(sourceElemType, "item");
		Expression mappedItem = CreateMappingExpression(itemParam, sourceElemType, destElemType);
		LambdaExpression selectLambda = Expression.Lambda(mappedItem, itemParam);

		MethodInfo selectMethod = typeof(Enumerable).GetMethods()
			.First(m => m.Name == nameof(Enumerable.Select) && m.GetParameters().Length == 2)
			.MakeGenericMethod(sourceElemType, destElemType);

		MethodCallExpression selectCall = Expression.Call(null, selectMethod, source, selectLambda);

		if (destType.IsArray)
		{
			MethodInfo toArrayMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray))!.MakeGenericMethod(destElemType);
			return Expression.Call(null, toArrayMethod, selectCall);
		}

		if (destType.IsGenericType)
		{
			Type genericDef = destType.GetGenericTypeDefinition();

			if (genericDef == typeof(List<>))
			{
				MethodInfo toListMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(destElemType);
				return Expression.Call(null, toListMethod, selectCall);
			}

			if (genericDef == typeof(HashSet<>))
			{
				MethodInfo toHashSetMethod = typeof(Enumerable).GetMethods()
					.First(m => m.Name == nameof(Enumerable.ToHashSet) && m.GetParameters().Length == 1)
					.MakeGenericMethod(destElemType);
				return Expression.Call(null, toHashSetMethod, selectCall);
			}
		}

		MethodInfo defaultToListMethod = typeof(Enumerable).GetMethod(nameof(Enumerable.ToList))!.MakeGenericMethod(destElemType);
		return Expression.Call(null, defaultToListMethod, selectCall);
	}

	protected virtual Expression CreateDictionaryMappingExpression(Expression source, Type sourceType, Type destType)
	{
		Type[] sourceArgs = sourceType.GetGenericArguments();
		Type[] destArgs = destType.GetGenericArguments();

		Type sourceKeyType = sourceArgs[0];
		Type sourceValueType = sourceArgs[1];
		Type destKeyType = destArgs[0];
		Type destValueType = destArgs[1];

		if (sourceKeyType != destKeyType)
		{
			throw new InvalidOperationException("Dictionary key types must match");
		}

		if (sourceValueType == destValueType)
		{
			ConstructorInfo dictCtor = destType.GetConstructor([typeof(IDictionary<,>).MakeGenericType(sourceKeyType, sourceValueType)])!;
			return Expression.New(dictCtor, source);
		}

		List<ParameterExpression> variables = [];
		List<Expression> bodyExpressions = [];

		ParameterExpression destDict = Expression.Variable(destType, "destDict");
		variables.Add(destDict);
		bodyExpressions.Add(Expression.Assign(destDict, Expression.New(destType)));

		Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(sourceKeyType, sourceValueType);
		Type enumerableType = typeof(IEnumerable<>).MakeGenericType(kvpType);
		Type enumeratorType = typeof(IEnumerator<>).MakeGenericType(kvpType);

		MethodInfo getEnumeratorMethod = enumerableType.GetMethod("GetEnumerator")!;
		ParameterExpression enumerator = Expression.Variable(enumeratorType, "enumerator");
		variables.Add(enumerator);
		bodyExpressions.Add(Expression.Assign(enumerator, Expression.Call(source, getEnumeratorMethod)));

		LabelTarget loopBreak = Expression.Label("break");
		MethodInfo moveNextMethod = typeof(IEnumerator).GetMethod("MoveNext")!;
		PropertyInfo currentProp = enumeratorType.GetProperty("Current")!;

		ParameterExpression kvpVar = Expression.Variable(kvpType, "kvp");
		variables.Add(kvpVar);

		MemberExpression keyAccess = Expression.Property(kvpVar, "Key");
		MemberExpression valueAccess = Expression.Property(kvpVar, "Value");

		Expression mappedValue = CreateMappingExpression(valueAccess, sourceValueType, destValueType);

		if (!sourceValueType.IsValueType)
		{
			mappedValue = Expression.Condition(
				Expression.Equal(valueAccess, Expression.Constant(null, sourceValueType)),
				Expression.Default(destValueType),
				mappedValue);
		}

		MethodInfo addMethod = destType.GetMethod("Add", [destKeyType, destValueType])!;

		BlockExpression loopBody = Expression.Block(
			Expression.IfThen(Expression.Not(Expression.Call(enumerator, moveNextMethod)), Expression.Break(loopBreak)),
			Expression.Assign(kvpVar, Expression.Property(enumerator, currentProp)),
			Expression.Call(destDict, addMethod, keyAccess, mappedValue));

		bodyExpressions.Add(Expression.Loop(loopBody, loopBreak));
		bodyExpressions.Add(destDict);

		return Expression.Block(variables, bodyExpressions);
	}

	protected static PropertyInfo[] GetProperties(Type type)
	{
		return CommonNetFuncs.Core.ReflectionCaches.GetOrAddPropertiesFromReflectionCache(type);
	}

	protected static bool IsCollection(Type type)
	{
		return type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type) && !IsDictionary(type);
	}

	protected static bool IsDictionary(Type type)
	{
		return typeof(IDictionary).IsAssignableFrom(type);
	}

	protected static Type GetElementType(Type collectionType)
	{
		if (collectionType.IsArray)
		{
			return collectionType.GetElementType()!;
		}

		if (collectionType.IsGenericType)
		{
			Type[] genArgs = collectionType.GetGenericArguments();
			if (genArgs.Length > 0)
			{
				return genArgs[0];
			}
		}

		Type? enumInterface = collectionType.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

		return enumInterface?.GetGenericArguments()[0] ?? typeof(object);
	}
}

/// <summary>Proposed: for Dictionary&lt;K,V1&gt; -> Dictionary&lt;K,V2&gt; use the struct enumerator (no boxing, no interface dispatch) and pre-size the destination.</summary>
internal sealed class PdFastMapBuilderOptimized : PdFastMapBuilder
{
	protected override Expression CreateDictionaryMappingExpression(Expression source, Type sourceType, Type destType)
	{
		if (!sourceType.IsGenericType || sourceType.GetGenericTypeDefinition() != typeof(Dictionary<,>) || !destType.IsGenericType || destType.GetGenericTypeDefinition() != typeof(Dictionary<,>))
		{
			return base.CreateDictionaryMappingExpression(source, sourceType, destType);
		}

		Type[] sourceArgs = sourceType.GetGenericArguments();
		Type[] destArgs = destType.GetGenericArguments();
		Type sourceKeyType = sourceArgs[0];
		Type sourceValueType = sourceArgs[1];
		Type destKeyType = destArgs[0];
		Type destValueType = destArgs[1];

		// Mismatched keys (same exception) and same-value-type copies keep the original path
		if (sourceKeyType != destKeyType || sourceValueType == destValueType)
		{
			return base.CreateDictionaryMappingExpression(source, sourceType, destType);
		}

		ParameterExpression sourceDict = Expression.Variable(sourceType, "sourceDict");
		ParameterExpression destDict = Expression.Variable(destType, "destDict");

		MethodInfo getEnumeratorMethod = sourceType.GetMethod("GetEnumerator", Type.EmptyTypes)!;
		Type enumeratorType = getEnumeratorMethod.ReturnType;
		ParameterExpression enumerator = Expression.Variable(enumeratorType, "enumerator");

		Type kvpType = typeof(KeyValuePair<,>).MakeGenericType(sourceKeyType, sourceValueType);
		ParameterExpression kvpVar = Expression.Variable(kvpType, "kvp");
		MemberExpression keyAccess = Expression.Property(kvpVar, "Key");
		MemberExpression valueAccess = Expression.Property(kvpVar, "Value");

		Expression mappedValue = CreateMappingExpression(valueAccess, sourceValueType, destValueType);
		if (!sourceValueType.IsValueType)
		{
			mappedValue = Expression.Condition(
				Expression.Equal(valueAccess, Expression.Constant(null, sourceValueType)),
				Expression.Default(destValueType),
				mappedValue);
		}

		MethodInfo addMethod = destType.GetMethod("Add", [destKeyType, destValueType])!;
		ConstructorInfo capacityCtor = destType.GetConstructor([typeof(int)])!;

		LabelTarget loopBreak = Expression.Label("break");
		BlockExpression loopBody = Expression.Block(
			Expression.IfThen(Expression.Not(Expression.Call(enumerator, enumeratorType.GetMethod("MoveNext", Type.EmptyTypes)!)), Expression.Break(loopBreak)),
			Expression.Assign(kvpVar, Expression.Property(enumerator, enumeratorType.GetProperty("Current")!)),
			Expression.Call(destDict, addMethod, keyAccess, mappedValue));

		return Expression.Block(
			new[] { sourceDict, destDict, enumerator, kvpVar },
			Expression.Assign(sourceDict, source), // source evaluated once (original evaluates it once for GetEnumerator)
			Expression.Assign(destDict, Expression.New(capacityCtor, Expression.Property(sourceDict, "Count"))),
			Expression.Assign(enumerator, Expression.Call(sourceDict, getEnumeratorMethod)),
			Expression.Loop(loopBody, loopBreak),
			destDict);
	}
}

#endregion

#region Verification

/// <summary>Comparer that re-enters DeepClone from GetHashCode once per top-level call (exercises the call-scoped memo save/restore).</summary>
internal sealed class PdReentrantComparer(Func<object?, object?> nestedClone) : EqualityComparer<object>
{
	private bool inside;

	public int NestedCalls { get; private set; }

	public override bool Equals(object? obj1, object? obj2)
	{
		return ReferenceEquals(obj1, obj2);
	}

	public override int GetHashCode(object obj)
	{
		if (!inside)
		{
			inside = true;
			try
			{
				nestedClone(PdDeepCloneData.CreateNested(3));
				NestedCalls++;
			}
			finally
			{
				inside = false;
			}
		}
		return RuntimeHelpers.GetHashCode(obj);
	}
}

/// <summary>Asserts that Original and Optimized variants of every Pd* candidate produce identical results on edge-case inputs.</summary>
public static class PdVerify
{
	public static void Run()
	{
		VerifyDeepClone();
		VerifySqlMapper();
		VerifyCleanQueryParam();
		VerifySanitize();
		VerifyKeyBuilders();
		VerifyKeyQueries();
		VerifyRemoveNavigationProperties();
		VerifyObjectByPartial();
		VerifyNavigationsCache();
		VerifyFastMapDictionary();
	}

	private static void Check(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException("PdVerify failed: " + message);
		}
	}

	private static Exception? Capture(Action action)
	{
		try
		{
			action();
			return null;
		}
		catch (Exception ex)
		{
			return ex;
		}
	}

	private static void CheckSameException(Exception? expected, Exception? actual, string message)
	{
		Check((expected == null) == (actual == null), message + " (only one variant threw)");
		if (expected != null)
		{
			Check(expected.GetType() == actual!.GetType(), $"{message} (exception type {expected.GetType().Name} vs {actual.GetType().Name})");
			Check(expected.Message == actual.Message, message + " (exception message)");
		}
	}

	private static void VerifyDeepClone()
	{
		foreach ((string name, object? input) in PdDeepCloneData.CreateVerificationInputs())
		{
			object? original = PdDeepCloneOriginal.DeepClone(input);
			object? originalNoCache = PdDeepCloneOriginal.DeepClone(input, useCache: false);
			(string Variant, object? Clone)[] variants =
			[
				("SingleLock", PdDeepCloneOptimized.DeepClone(input)),
				("SingleLockNoCache", PdDeepCloneOptimized.DeepClone(input, useCache: false)),
				("Memo", PdDeepCloneOptimizedMemo.DeepClone(input)),
				("MemoNoCache", PdDeepCloneOptimizedMemo.DeepClone(input, useCache: false)),
				("MemoOriginalComparer", PdDeepCloneOptimizedMemo.DeepClone(input, new Dictionary<object, object>(new PdOriginalReferenceEqualityComparer()))),
				("FastComparer", PdDeepCloneOriginal.DeepClone(input, new Dictionary<object, object>(PdFastReferenceEqualityComparer.Instance))),
				("OriginalNoCache", originalNoCache)
			];

			foreach ((string variant, object? clone) in variants)
			{
				Check(PdGraph.Equal(original, clone), $"DeepClone {variant} differs from Original for {name}");
				Check(ReferenceEquals(input, original) == ReferenceEquals(input, clone), $"DeepClone {variant} root identity differs for {name}");
			}
		}

		// Delegates (root and nested in an object[] element) must throw the same ArgumentException
		Action action = () => { };
		foreach (object delegateInput in new object[] { action, new object[] { 1, action } })
		{
			Exception? expected = Capture(() => PdDeepCloneOriginal.DeepClone(delegateInput));
			Check(expected is ArgumentException, "DeepClone delegate input must throw ArgumentException");
			CheckSameException(expected, Capture(() => PdDeepCloneOptimized.DeepClone(delegateInput)), "DeepClone SingleLock delegate");
			CheckSameException(expected, Capture(() => PdDeepCloneOptimizedMemo.DeepClone(delegateInput)), "DeepClone Memo delegate");
		}

		// A caller-supplied, pre-populated reference dictionary short-circuits identically
		SimpleClass known = PdDeepCloneData.CreateSimple(5);
		object sentinel = new();
		Func<Dictionary<object, object>> seeded = () => new Dictionary<object, object>(new PdOriginalReferenceEqualityComparer()) { [known] = sentinel };
		Check(ReferenceEquals(PdDeepCloneOriginal.DeepClone<object>(known, seeded()), sentinel), "DeepClone Original seeded dictionary");
		Check(ReferenceEquals(PdDeepCloneOptimized.DeepClone<object>(known, seeded()), sentinel), "DeepClone SingleLock seeded dictionary");
		Check(ReferenceEquals(PdDeepCloneOptimizedMemo.DeepClone<object>(known, seeded()), sentinel), "DeepClone Memo seeded dictionary");

		// Re-entrant DeepClone from a user comparer: outer memo must be restored after the nested call
		ComplexClass complex = PdDeepCloneData.CreateComplex();
		PdReentrantComparer originalComparer = new(o => PdDeepCloneOriginal.DeepClone(o));
		PdReentrantComparer memoComparer = new(o => PdDeepCloneOptimizedMemo.DeepClone(o));
		object? originalReentrant = PdDeepCloneOriginal.DeepClone<object>(complex, new Dictionary<object, object>(originalComparer));
		object? memoReentrant = PdDeepCloneOptimizedMemo.DeepClone<object>(complex, new Dictionary<object, object>(memoComparer));
		Check(memoComparer.NestedCalls > 0 && memoComparer.NestedCalls == originalComparer.NestedCalls, "DeepClone re-entrant call count");
		Check(PdGraph.Equal(originalReentrant, memoReentrant), "DeepClone re-entrant result");
	}

	private static void VerifySqlMapper()
	{
		Func<IDataReader, PdSqlRow> original = PdSqlMappers.CreateMapperDelegate<PdSqlRow>(typeof(PdSqlRow));
		PdOrdinalMapper<PdSqlRow> ordinal = PdSqlMappers.CreateOrdinalMapper<PdSqlRow>(typeof(PdSqlRow));

		DataTable[] tables = [PdSqlData.CreateTable(60, includeNulls: true), PdSqlData.CreateTable(60, includeNulls: true, reverseColumns: true), PdSqlData.CreateTable(0, includeNulls: false),
			PdSqlData.CreateTable(0, includeNulls: false, omitNotes: true)];
		foreach (DataTable table in tables)
		{
			List<PdSqlRow> expected = ReadAll(table, original);
			List<PdSqlRow> actual = ReadAll(table, new PdOrdinalReaderMapper<PdSqlRow>(ordinal).Map);
			Check(expected.Count == actual.Count, "Sql mapper row count");
			for (int i = 0; i < expected.Count; i++)
			{
				Check(SameRow(expected[i], actual[i]), $"Sql mapper row {i} differs");
			}
		}

		// Missing column and failed conversion surface the same exception from the first mapped row
		foreach (DataTable bad in new[] { PdSqlData.CreateTable(3, includeNulls: false, omitNotes: true), PdSqlData.CreateTable(3, includeNulls: false, amountAsString: true) })
		{
			Exception? expected = Capture(() => ReadAll(bad, original));
			Check(expected != null, "Sql mapper bad table must throw");
			CheckSameException(expected, Capture(() => ReadAll(bad, new PdOrdinalReaderMapper<PdSqlRow>(ordinal).Map)), "Sql mapper failure");
		}
	}

	private static List<PdSqlRow> ReadAll(DataTable table, Func<IDataReader, PdSqlRow> mapper)
	{
		List<PdSqlRow> rows = [];
		using DataTableReader reader = table.CreateDataReader();
		while (reader.Read())
		{
			rows.Add(mapper(reader));
		}
		return rows;
	}

	private static bool SameRow(PdSqlRow a, PdSqlRow b)
	{
		return a.Id == b.Id && a.Name == b.Name && a.Email == b.Email && a.Amount == b.Amount && a.Created == b.Created && a.Created.Kind == b.Created.Kind &&
			a.Active == b.Active && a.Count == b.Count && a.Ratio.Equals(b.Ratio) && a.Notes == b.Notes && a.Key == b.Key;
	}

	private static void VerifyCleanQueryParam()
	{
		IList<string>?[] inputs =
		[
			null,
			new List<string>(),
			new List<string> { "test\n", " value ", "null", "  Clean\nThis  " },
			new List<string> { "item1\n", "  item2  ", "item3\n\n", " \nitem4 ", "null", "item5", "NULL", "nullnull", "", "   ", null!, "dup", "dup", "\n" },
			Enumerable.Range(0, 500).Select(i => i % 3 == 0 ? "null" : " v" + i + "\n").ToArray()
		];

		foreach (IList<string>? input in inputs)
		{
			List<string>? expected = PdQueryParameters.CleanQueryParamOriginal(input);
			List<string>? actual = PdQueryParameters.CleanQueryParamOptimized(input);
			Check((expected == null) == (actual == null), "CleanQueryParam null-ness");
			if (expected != null)
			{
				// The original's element order is non-deterministic (Parallel.ForEach + ConcurrentBag), so compare as multisets
				Check(expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(actual!.OrderBy(x => x, StringComparer.Ordinal)), "CleanQueryParam contents");
			}
		}
	}

	private static void VerifySanitize()
	{
		string?[] inputs =
		[
			null, "", " ", "  \t", "abc", "a;b", "a'b", "O'Brien", "a[b", "]", "\"q\"", "`", "/*", "*/", "/ *", "* /", "a/", "a*", "a-", "a--b", "-", "--", "x", "xp", "xp_",
			"axp_b", "XP_cmd", "xP_", "%", "50%", "a*b", "ab12", "12", "abc def", "\u00fcn\u00efc\u00f6d\u00e9", "abc/", "/", "*", "-x", "x-", "xx", "xpx_", "p_", "*/*", "/*/", "x\np_"
		];
		bool[] flags = [false, true];
		int?[] maxLengths = [null, 3, 50];
		int?[] minLengths = [null, 2];
		string?[] defaults = ["", null, "D"];

		foreach (string? input in inputs)
		{
			foreach (bool excludeSingleQuote in flags)
			{
				Check(PdQueryParameters.IsCleanOriginal(input, excludeSingleQuote) == PdQueryParameters.IsCleanOptimized(input, excludeSingleQuote), $"IsClean differs for '{input}' ({excludeSingleQuote})");
			}

			foreach (bool alphanumeric in flags)
			{
				foreach (bool alpha in flags)
				{
					foreach (bool numeric in flags)
					{
						foreach (int? maxLength in maxLengths)
						{
							foreach (int? minLength in minLengths)
							{
								foreach (string? defaultValue in defaults)
								{
									string? expected = PdQueryParameters.SanitizeSqlParameterOriginal(input, alphanumeric, alpha, numeric, maxLength, minLength, defaultValue);
									string? actual = PdQueryParameters.SanitizeSqlParameterOptimized(input, alphanumeric, alpha, numeric, maxLength, minLength, defaultValue);
									Check(string.Equals(expected, actual, StringComparison.Ordinal), $"SanitizeSqlParameter differs for '{input}'");
								}
							}
						}
					}
				}
			}
		}
	}

	private static void VerifyKeyBuilders()
	{
		using PdEfContext context = new(PdEf.CreateOptions());
		foreach (object key in new object[] { 1, 0, -5, int.MaxValue })
		{
			Check(PdExpressionDescriber.Describe(PdKeyBuilders.Original(context, key)) == PdExpressionDescriber.Describe(PdKeyBuilders.OptimizedConstant(context, key)), $"Key expression differs for {key}");
		}

		foreach (object? badKey in new object?[] { 5L, "abc", null, 1.5 })
		{
			Exception? expected = Capture(() => PdKeyBuilders.Original(context, badKey!));
			Check(expected != null, "Key builder must reject mismatched key type");
			CheckSameException(expected, Capture(() => PdKeyBuilders.OptimizedConstant(context, badKey!)), "Key builder (constant) bad key");
			CheckSameException(expected, Capture(() => PdKeyBuilders.OptimizedParameterized(context, badKey!)), "Key builder (parameterized) bad key");
		}
	}

	private static void VerifyKeyQueries()
	{
		DbContextOptions<PdEfContext> options = PdEf.CreateSeededOptions(100);
		foreach (int key in new[] { 1, 10, 55, 100, 101, 5000 })
		{
			foreach (bool ignoreFilters in new[] { true, false })
			{
				PdKeyEntity? expected = QueryByKey(options, ctx => PdKeyBuilders.Original(ctx, key), ignoreFilters);
				PdKeyEntity? constant = QueryByKey(options, ctx => PdKeyBuilders.OptimizedConstant(ctx, key), ignoreFilters);
				PdKeyEntity? parameterized = QueryByKey(options, ctx => PdKeyBuilders.OptimizedParameterized(ctx, key), ignoreFilters);
				Check(SameEntity(expected, constant) && SameEntity(expected, parameterized), $"Key query differs for {key} (ignoreFilters: {ignoreFilters})");
			}
		}
	}

	private static PdKeyEntity? QueryByKey(DbContextOptions<PdEfContext> options, Func<DbContext, Expression<Func<PdKeyEntity, bool>>> build, bool ignoreFilters)
	{
		using PdEfContext context = new(options);
		IQueryable<PdKeyEntity> query = context.Set<PdKeyEntity>().Where(build(context));
		if (ignoreFilters)
		{
			query = query.IgnoreQueryFilters();
		}
		return query.FirstOrDefault();
	}

	private static bool SameEntity(PdKeyEntity? a, PdKeyEntity? b)
	{
		return (a == null && b == null) || (a != null && b != null && a.Id == b.Id && a.Name == b.Name && a.IsDeleted == b.IsDeleted);
	}

	private static void VerifyRemoveNavigationProperties()
	{
		using PdEfContext context = new(PdEf.CreateOptions());
		List<PdNavParent> expected = PdEf.CreateNavParents(5);
		List<PdNavParent> actual = PdEf.CreateNavParents(5);
		for (int i = 0; i < expected.Count; i++)
		{
			PdNavigationRemoval.Original(expected[i], context);
			PdNavigationRemoval.Optimized(actual[i], context);
			Check(expected[i].Children == null && actual[i].Children == null && expected[i].Favorite == null && actual[i].Favorite == null, "RemoveNavigationProperties parent navigations");
			Check(expected[i].Id == actual[i].Id && expected[i].Name == actual[i].Name && expected[i].FavoriteId == actual[i].FavoriteId, "RemoveNavigationProperties parent scalars");
		}

		PdNavChild expectedChild = new() { Id = 1, ParentId = 2, Parent = new PdNavParent(), Items = [new PdNavItem()] };
		PdNavChild actualChild = new() { Id = 1, ParentId = 2, Parent = new PdNavParent(), Items = [new PdNavItem()] };
		PdNavigationRemoval.Original(expectedChild, context);
		PdNavigationRemoval.Optimized(actualChild, context);
		Check(expectedChild.Parent == null && actualChild.Parent == null && expectedChild.Items == null && actualChild.Items == null && expectedChild.ParentId == actualChild.ParentId, "RemoveNavigationProperties child");

		PdKeyEntity expectedNoNavigations = new() { Id = 3, Name = "n" };
		PdKeyEntity actualNoNavigations = new() { Id = 3, Name = "n" };
		PdNavigationRemoval.Original(expectedNoNavigations, context);
		PdNavigationRemoval.Optimized(actualNoNavigations, context);
		Check(SameEntity(expectedNoNavigations, actualNoNavigations), "RemoveNavigationProperties entity without navigations");

		CheckSameException(Capture(() => PdNavigationRemoval.Original<PdNavParent>(null, context)), Capture(() => PdNavigationRemoval.Optimized<PdNavParent>(null, context)), "RemoveNavigationProperties null");
	}

	private static void VerifyObjectByPartial()
	{
		using PdEfContext context = new(PdEf.CreateOptions());
		PdPartialEntity[] partials =
		[
			new(),
			new() { Name = "Alice" },
			new() { Name = "Bob", Created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local), Amount = 12.5m, Count = 0, Active = true, Key = Guid.Empty, Big = 0 },
			new() { Created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), Count = 7, Key = new Guid(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11), Ratio = 0.5, Big = long.MaxValue, Id = 9 },
			new() { Created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified), Email = string.Empty, Ratio = -0.0 }
		];

		foreach (PdPartialEntity partial in partials)
		{
			foreach (bool ignoreDefaultValues in new[] { true, false })
			{
				(Expression? expectedConditions, ParameterExpression expectedParameter) = PdObjectByPartial.Original(context, partial, ignoreDefaultValues);
				(Expression? actualConditions, ParameterExpression actualParameter) = PdObjectByPartial.Optimized(context, partial, ignoreDefaultValues);
				Check(PdExpressionDescriber.Describe(expectedConditions) == PdExpressionDescriber.Describe(actualConditions), "GetObjectByPartial conditions differ");
				Check(expectedParameter.Name == actualParameter.Name && expectedParameter.Type == actualParameter.Type, "GetObjectByPartial parameter differs");
			}
		}

		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();
		CheckSameException(Capture(() => PdObjectByPartial.Original(context, partials[1], true, cancelled.Token)), Capture(() => PdObjectByPartial.Optimized(context, partials[1], true, cancelled.Token)),
			"GetObjectByPartial cancellation");
	}

	private static void VerifyNavigationsCache()
	{
		using PdEfContext context = new(PdEf.CreateOptions());
		List<Type> ignore = [typeof(System.Text.Json.Serialization.JsonIgnoreAttribute)];
		NavigationPropertiesOptions[] sequence =
		[
			new(), new(), new(maxNavigationDepth: 1), new(maxNavigationDepth: 0), new(maxNavigationDepth: 3, navPropAttributesToIgnore: ignore),
			new(maxNavigationDepth: 1, navPropAttributesToIgnore: ignore), new(maxNavigationDepth: -1), new(useCaching: false), new(maxNavigationDepth: 200), new(maxNavigationDepth: 2)
		];

		foreach (NavigationPropertiesOptions options in sequence)
		{
			HashSet<string> expected = PdNavigationsCache.OriginalGetNavigations<PdNavParent>(context, options);
			HashSet<string> actual = PdNavigationsCache.OptimizedGetNavigations<PdNavParent>(context, options);
			Check(expected.SequenceEqual(actual), $"GetNavigations differs (depth {options.MaxNavigationDepth})");

			HashSet<string> expectedChild = PdNavigationsCache.OriginalGetNavigations<PdNavChild>(context, options);
			HashSet<string> actualChild = PdNavigationsCache.OptimizedGetNavigations<PdNavChild>(context, options);
			Check(expectedChild.SequenceEqual(actualChild), $"GetNavigations (child) differs (depth {options.MaxNavigationDepth})");
		}

		HashSet<string> set = ["A", "A.B", "A.B.C", string.Empty, null!, "X.Y.Z.W", "..", "Q"];
		NavigationProperties.NavigationPropertiesCacheValue value = new(set, 5);
		for (int depth = -1; depth <= 4; depth++)
		{
			HashSet<string> expected = value.GetNavigationsToDepth(depth);
			HashSet<string> actual = PdNavigationsCache.GetNavigationsToDepth(set, depth);
			Check(expected.SequenceEqual(actual), $"GetNavigationsToDepth differs (depth {depth})");
			Check(ReferenceEquals(expected, set) == ReferenceEquals(actual, set), $"GetNavigationsToDepth identity differs (depth {depth})");
			Check(expected.Comparer.Equals(actual.Comparer), $"GetNavigationsToDepth comparer differs (depth {depth})");
		}
	}

	private static void VerifyFastMapDictionary()
	{
		Func<PdMapSource, PdMapDest> original = new PdFastMapBuilder().CreateMapper<PdMapSource, PdMapDest>();
		Func<PdMapSource, PdMapDest> optimized = new PdFastMapBuilderOptimized().CreateMapper<PdMapSource, PdMapDest>();

		Dictionary<string, PdMapItem> withHoles = PdFastMapData.CreateSource(20).Items!;
		withHoles.Remove("k3");
		withHoles.Remove("k7");
		withHoles["new"] = new PdMapItem { A = 99, B = null, C = double.NaN };

		PdMapSource[] sources =
		[
			new() { Id = 1, Items = null },
			new() { Id = 2, Items = new Dictionary<string, PdMapItem>() },
			PdFastMapData.CreateSource(25),
			new() { Id = 4, Items = new Dictionary<string, PdMapItem>(StringComparer.OrdinalIgnoreCase) { ["a"] = new PdMapItem { A = 1 }, ["B"] = null! } },
			new() { Id = 5, Items = withHoles }
		];

		foreach (PdMapSource source in sources)
		{
			Check(SameMap(original(source), optimized(source)), $"FastMap dictionary differs for source {source.Id}");
		}
	}

	private static bool SameMap(PdMapDest a, PdMapDest b)
	{
		if (a.Id != b.Id || (a.Items == null) != (b.Items == null))
		{
			return false;
		}

		if (a.Items == null)
		{
			return true;
		}

		if (a.Items.Count != b.Items!.Count || !a.Items.Keys.SequenceEqual(b.Items.Keys) || a.Items.Comparer.GetType() != b.Items.Comparer.GetType())
		{
			return false;
		}

		foreach (KeyValuePair<string, PdMapItemDto> pair in a.Items)
		{
			PdMapItemDto? other = b.Items[pair.Key];
			if ((pair.Value == null) != (other == null))
			{
				return false;
			}

			if (pair.Value != null && (pair.Value.A != other!.A || pair.Value.B != other.B || !pair.Value.C.Equals(other.C)))
			{
				return false;
			}
		}
		return true;
	}
}

#endregion
