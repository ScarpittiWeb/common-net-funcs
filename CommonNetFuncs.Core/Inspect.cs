using CommonNetFuncs.Core.Internal;
using FastExpressionCompiler;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using static CommonNetFuncs.Core.ReflectionCaches;

namespace CommonNetFuncs.Core;

public static class Inspect
{
	/// <summary>
	/// Gets the default value of the provided type
	/// </summary>
	/// <param name="type">Type to get the default value of</param>
	/// <returns>The default value of the provided type</returns>
	public static object? GetDefaultValue(this Type type)
	{
		return type.IsValueType ? RuntimeHelpers.GetUninitializedObject(type) : null;
	}

	/// <summary>
	/// Get the number of properties in a class that are set to their default value
	/// </summary>
	/// <param name="obj">Object to count default properties in</param>
	/// <returns>Number of properties in a class that are set to their default value</returns>
	public static int CountDefaultProps<T>(this T obj) where T : class
	{
		int count = 0;
		foreach (PropertyInfo property in GetOrAddPropertiesFromReflectionCache(typeof(T)))
		{
			if (property.CanWrite && Equals(property.GetValue(obj), DefaultValueCache.GetOrAdd(property.PropertyType, static type => type.GetDefaultValue())))
			{
				count++;
			}
		}
		return count;
	}

	// Boxed defaults are only ever compared with Equals, so sharing one instance per type avoids allocating a new box per property per call
	private static readonly ConcurrentDictionary<Type, object?> DefaultValueCache = new();

	/// <summary>
	/// Returns whether a Type has the specified attribute or not
	/// </summary>
	/// <param name="type">The type to check for the specified attribute</param>
	/// <param name="attributeName">The name of the attribute you are checking the provided type for</param>
	/// <returns><see langword="true"/> if the object has the specified attribute, otherwise false</returns>
	public static bool ObjectHasAttribute(this Type type, string attributeName)
	{
		bool hasAttribute = false;
		foreach (object item in type.GetCustomAttributes(true))
		{
			object? typeIdObject = item.GetType().GetProperty("TypeId")?.GetValue(item, null);

			if (typeIdObject != null)
			{
				string? attrName = typeIdObject.GetType().GetProperty("Name")?.GetValue(typeIdObject, null)?.ToString();
				if (attrName?.StrEq(attributeName) == true)
				{
					hasAttribute = true;
					break;
				}
			}
		}
		return hasAttribute;
	}

	// This class is used to track object pairs being compared
	private sealed class ComparisonContext
	{
		private readonly HashSet<(object, object)> comparingPairs = [];

		public bool TryAddPair(object obj1, object obj2)
		{
			return comparingPairs.Add((obj1, obj2));
		}

		public void RemovePair(object obj1, object obj2)
		{
			comparingPairs.Remove((obj1, obj2));
		}
	}

	private sealed class CompareEntry(Func<object, object, IEnumerable<string>, bool> compare, bool hasRecursion)
	{
		public Func<object, object, IEnumerable<string>, bool> Compare { get; } = compare;

		// Whether the compiled comparer can call back into IsEqual (and therefore needs cycle tracking)
		public bool HasRecursion { get; } = hasRecursion;
	}

	private static readonly AsyncLocal<ComparisonContext?> CurrentContext = new();
	private static readonly ConcurrentDictionary<(Type Type, bool IgnoreStringCase, bool Recursive), CompareEntry> CompareDelegates = [];

	/// <summary>
	/// Compare two class objects for value equality
	/// </summary>
	/// <param name="obj1">First object to compare for value equality</param>
	/// <param name="obj2">Second object to compare for value equality</param>
	/// <param name="exemptProps">Optional: Names of properties to not include in the matching check</param>
	/// <param name="ignoreStringCase">Optional: If <see langword="true"/>, will ignore case when comparing string properties between obj1 and obj2, otherwise will use case sensitive comparison.</param>
	/// <param name="recursive">Optional: If <see langword="true"/>, will recursively compare </param>
	/// <returns><see langword="true"/> if the two objects are equal by values, otherwise false</returns>
	public static bool IsEqual(this object? obj1, object? obj2, IEnumerable<string>? exemptProps = null, bool ignoreStringCase = false, bool recursive = true)
	{
		// Top-level call
		if (CurrentContext.Value == null)
		{
			if ((obj1 == null) && (obj2 == null))
			{
				return true;
			}

			if ((obj1 == null) || (obj2 == null))
			{
				return false;
			}

			Type topLevelType = obj1.GetType();
			if (topLevelType != obj2.GetType())
			{
				return false;
			}

			CompareEntry entry = CompareDelegates.GetOrAdd((topLevelType, ignoreStringCase, recursive), static key => CreateCompareEntry(key.Type, key.IgnoreStringCase, key.Recursive));
			if (!entry.HasRecursion)
			{
				// The compiled comparer can never re-enter IsEqual, so the cycle-tracking context would never be read
				return entry.Compare(obj1, obj2, exemptProps ?? Array.Empty<string>());
			}

			ComparisonContext context = new();
			CurrentContext.Value = context;
			try
			{
				context.TryAddPair(obj1, obj2);
				return entry.Compare(obj1, obj2, exemptProps ?? Array.Empty<string>());
			}
			finally
			{
				// Clear context since this was the top-level call
				CurrentContext.Value?.RemovePair(obj1, obj2);
				CurrentContext.Value = null;
			}
		}

		// Nested (recursive) call
		try
		{
			if ((obj1 == null) && (obj2 == null))
			{
				return true;
			}

			if ((obj1 == null) || (obj2 == null))
			{
				return false;
			}

			Type type = obj1.GetType();

			if (type != obj2.GetType())
			{
				return false;
			}

			// If we're already comparing these objects, return true to break the cycle
			if (!CurrentContext.Value!.TryAddPair(obj1, obj2))
			{
				return true;
			}

			exemptProps ??= [];
			CompareEntry entry = CompareDelegates.GetOrAdd((type, ignoreStringCase, recursive), static key => CreateCompareEntry(key.Type, key.IgnoreStringCase, key.Recursive));
			return entry.Compare(obj1, obj2, exemptProps);
		}
		finally
		{
			// Clean up
			if ((obj1 != null) && (obj2 != null))
			{
				CurrentContext.Value?.RemovePair(obj1, obj2);
			}
		}
	}

	private static CompareEntry CreateCompareEntry(Type type, bool ignoreStringCase, bool recursive)
	{
		Func<object, object, IEnumerable<string>, bool> compare = CreateCompareDelegate(type, ignoreStringCase, recursive, out bool hasRecursion);
		return new CompareEntry(compare, hasRecursion);
	}

	/// <summary>
	/// Creates a delegate for comparing two objects of the specified type for value equality.
	/// </summary>
	/// <param name="type">Type of the object to be compared</param>
	/// <param name="ignoreStringCase">If <see langword="true"/>, will ignore case when comparing string properties for value equality</param>
	/// <param name="recursive">If <see langword="true"/>, will recursively compare properties of complex types</param>
	/// <param name="hasRecursion">Set to <see langword="true"/> if the delegate calls back into <see cref="IsEqual"/> for any property</param>
	/// <returns>A delegate for comparing two objects of the specified type for value equality</returns>
	private static Func<object, object, IEnumerable<string>, bool> CreateCompareDelegate(Type type, bool ignoreStringCase, bool recursive, out bool hasRecursion)
	{
		hasRecursion = false;
		ParameterExpression obj1Param = Expression.Parameter(typeof(object), "obj1");
		ParameterExpression obj2Param = Expression.Parameter(typeof(object), "obj2");
		ParameterExpression exemptPropsParam = Expression.Parameter(typeof(IEnumerable<string>), "exemptProps");

		UnaryExpression typedObj1 = Expression.Convert(obj1Param, type);
		UnaryExpression typedObj2 = Expression.Convert(obj2Param, type);

		IEnumerable<PropertyInfo> properties = GetOrAddPropertiesFromReflectionCache(type).Where(x => x.CanRead && (x.GetIndexParameters().Length == 0));

		List<Expression> comparisons = [];

		foreach (PropertyInfo prop in properties)
		{
			MethodCallExpression propExemptCheck = Expression.Call(typeof(Enumerable), nameof(Enumerable.Contains), [typeof(string)], exemptPropsParam, Expression.Constant(prop.Name));

			MemberExpression value1 = Expression.Property(typedObj1, prop);
			MemberExpression value2 = Expression.Property(typedObj2, prop);

			Expression comparison;

			if (prop.PropertyType == typeof(string))
			{
				if (ignoreStringCase)
				{
					MethodInfo? equalsMethod = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string), typeof(StringComparison)])!;
					comparison = Expression.Call(equalsMethod, value1, value2, Expression.Constant(StringComparison.OrdinalIgnoreCase));
				}
				else
				{
					MethodInfo? equalsMethod = typeof(string).GetMethod(nameof(string.Equals), [typeof(string), typeof(string)])!;
					comparison = Expression.Call(equalsMethod, value1, value2);
				}
			}
			else if (prop.PropertyType.IsValueType)
			{
				comparison = Expression.Equal(value1, value2);
			}
			else if (typeof(IComparable).IsAssignableFrom(prop.PropertyType))
			{
				comparison = Expression.Equal(Expression.Call(value1, "CompareTo", null, value2), Expression.Constant(0));
			}
			else if (recursive && !prop.PropertyType.IsValueType && (prop.PropertyType != typeof(string)))
			{
				MethodInfo? isEqualMethod = typeof(Inspect).GetMethod(nameof(IsEqual), [typeof(object), typeof(object), typeof(IEnumerable<string>), typeof(bool), typeof(bool)])!;
				comparison = Expression.Call(isEqualMethod, value1, value2, Expression.Constant(null, typeof(IEnumerable<string>)), Expression.Constant(ignoreStringCase), Expression.Constant(recursive));
				hasRecursion = true;
			}
			else
			{
				comparison = Expression.Constant(true);
			}

			comparisons.Add(Expression.Condition(propExemptCheck, Expression.Constant(true), comparison));
		}

		// AndAlso short-circuits, so the remaining (possibly recursive) property comparisons are skipped after the first mismatch
		Expression andAlsoExpression = comparisons.Aggregate(Expression.AndAlso);

		Expression<Func<object, object, IEnumerable<string>, bool>> lambda = Expression.Lambda<Func<object, object, IEnumerable<string>, bool>>(andAlsoExpression, obj1Param, obj2Param, exemptPropsParam);

		return lambda.CompileFast();
	}

	/// <summary>
	/// Gets a hash string representing the object's value, using the specified algorithm (default MD5). Order of collection elements does not affect the hash.
	/// </summary>
	/// <param name="obj">Object to get hash value from</param>
	/// <param name="hashAlgorithm">Optional: Hash algorithm to use. Default is MD5.</param>
	/// <returns>Hash string representing the object's value</returns>
	public static string GetHashForObject<T>(this T obj, EHashAlgorithm hashAlgorithm = EHashAlgorithm.MD5) where T : class?
	{
		if (obj == null)
		{
			return "null";
		}

#if NET
		using MemoryStream ms = new();
		using BinaryWriter writer = new(ms);
		foreach (PropertyInfo property in GetReadablePropertiesOrderedByNameCached(typeof(T)))
		{
			object? value = property.GetValue(obj);
			if (value != null)
			{
				WriteValue(writer, value);
			}
		}

		ReadOnlySpan<byte> data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
		byte[] hash = hashAlgorithm switch
		{
			EHashAlgorithm.SHA1 => SHA1.HashData(data),
			EHashAlgorithm.MD5 => MD5.HashData(data),
			EHashAlgorithm.SHA256 => SHA256.HashData(data),
			EHashAlgorithm.SHA384 => SHA384.HashData(data),
			_ => SHA512.HashData(data)
		};
		return Convert.ToHexStringLower(hash);
#else
		using HashAlgorithm algorithm = CreateHashAlgorithm(hashAlgorithm);
		IOrderedEnumerable<PropertyInfo> properties = GetReadablePropertiesOrderedByName(typeof(T));

		using MemoryStream ms = new();
		using BinaryWriter writer = new(ms);
		foreach (PropertyInfo property in properties)
		{
			object? value = property.GetValue(obj);
			if (value != null)
			{
				WriteValue(writer, value);
			}
		}

		return HashCompat.ToHexStringLower(algorithm.ComputeHash(ms.ToArray()));
#endif
	}

	/// <summary>
	/// Gets a hash string representing the object's value asynchronously, using the specified algorithm (default MD5). Order of collection elements does not affect the hash.
	/// </summary>
	/// <param name="obj">Object to get hash value from</param>
	/// <param name="hashAlgorithm">Optional: Hash algorithm to use. Default is MD5.</param>
	/// <returns>Hash string representing the object's value</returns>
	public static async Task<string> GetHashForObjectAsync<T>(this T obj, EHashAlgorithm hashAlgorithm = EHashAlgorithm.MD5) where T : class
	{
		if (obj == null)
		{
			return "null";
		}

		HashAlgorithm algorithm = CreateHashAlgorithm(hashAlgorithm);
		IOrderedEnumerable<PropertyInfo> properties = GetReadablePropertiesOrderedByName(typeof(T));

		await using MemoryStream ms = new();
		await using BinaryWriter writer = new(ms);
		foreach (PropertyInfo property in properties)
		{
			object? value = property.GetValue(obj);
			if (value != null)
			{
				await WriteValueAsync(writer, value).ConfigureAwait(false);
			}
		}
		await ms.FlushAsync().ConfigureAwait(false);
		ms.Position = 0; // Reset stream position for reading
		return HashCompat.ToHexStringLower(await algorithm.ComputeHashAsync(ms).ConfigureAwait(false));
	}

	private static HashAlgorithm CreateHashAlgorithm(EHashAlgorithm hashAlgorithm)
	{
		return hashAlgorithm switch
		{
			EHashAlgorithm.SHA1 => SHA1.Create(),
			EHashAlgorithm.MD5 => MD5.Create(),
			EHashAlgorithm.SHA256 => SHA256.Create(),
			EHashAlgorithm.SHA384 => SHA384.Create(),
			_ => SHA512.Create()
		};
	}

	private static IOrderedEnumerable<PropertyInfo> GetReadablePropertiesOrderedByName(Type type)
	{
		return GetOrAddPropertiesFromReflectionCache(type).Where(x => x.CanRead).OrderBy(x => x.Name);
	}

#if NET
	private static readonly ConcurrentDictionary<(Type Type, System.Globalization.CultureInfo Culture), PropertyInfo[]> OrderedPropertiesCache = new();

	private static PropertyInfo[] GetReadablePropertiesOrderedByNameCached(Type type)
	{
		// OrderBy(Name) uses the current culture's string comparer, so the culture is part of the key to keep the ordering identical
		return OrderedPropertiesCache.GetOrAdd((type, System.Globalization.CultureInfo.CurrentCulture), static key => GetReadablePropertiesOrderedByName(key.Type).ToArray());
	}
#endif

	// Sorts the hashes so the collection's hash doesn't depend on element order.
	private static void WriteSortedItemHashes(BinaryWriter writer, List<string> itemHashes)
	{
		itemHashes.Sort();

		writer.Write("[");
		foreach (string itemHash in itemHashes)
		{
			writer.Write(itemHash);
			writer.Write(",");
		}
		writer.Write("]");
	}

	private static bool IsSimpleValue(Type type, object value)
	{
		return type.IsPrimitive || (value is string) || (value is decimal);
	}

	/// <summary>
	/// Writes the value to the binary writer.
	/// </summary>
	/// <param name="writer">Binary writer to write the value to</param>
	/// <param name="value">Value to write to the binary writer</param>
	private static void WriteValue(BinaryWriter writer, object value)
	{
		if (value == null)
		{
			writer.Write("null");
			return;
		}

		Type type = value.GetType();

		// Handle collections
		if (value is IEnumerable enumerable and not string)
		{
			// Convert collection to list of sorted hashes
			List<string> itemHashes = [];
			foreach (object item in enumerable)
			{
				using MemoryStream itemMs = new();
				using BinaryWriter itemWriter = new(itemMs);
				WriteValue(itemWriter, item);
#if NET
				itemHashes.Add(BitConverter.ToString(MD5.HashData(itemMs.GetBuffer().AsSpan(0, (int)itemMs.Length))));
#else
				itemHashes.Add(BitConverter.ToString(HashCompat.Md5HashData(itemMs.ToArray())));
#endif
			}

			WriteSortedItemHashes(writer, itemHashes);
			return;
		}

		// Handle primitive types and strings
		if (IsSimpleValue(type, value))
		{
			writer.Write(value.ToString()!);
			return;
		}

		// Handle complex objects recursively
#if NET
		PropertyInfo[] properties = GetReadablePropertiesOrderedByNameCached(type);
#else
		IOrderedEnumerable<PropertyInfo> properties = GetReadablePropertiesOrderedByName(type);
#endif

		writer.Write("{");
		foreach (PropertyInfo property in properties)
		{
			writer.Write(property.Name);
			writer.Write(":");
			WriteValue(writer, property.GetValue(value)!);
			writer.Write(",");
		}
		writer.Write("}");
	}

	/// <summary>
	/// Writes the value to the binary writer.
	/// </summary>
	/// <param name="writer">Binary writer to write the value to</param>
	/// <param name="value">Value to write to the binary writer</param>
	private static async Task WriteValueAsync(BinaryWriter writer, object value)
	{
		if (value == null)
		{
			writer.Write("null");
			return;
		}

		Type type = value.GetType();

		// Handle collections
		if (value is IEnumerable enumerable and not string)
		{
			// Convert collection to list of sorted hashes
			List<string> itemHashes = [];
			foreach (object item in enumerable)
			{
				await using MemoryStream itemMs = new();
				await using BinaryWriter itemWriter = new(itemMs);
				await WriteValueAsync(itemWriter, item).ConfigureAwait(false);
				itemHashes.Add(BitConverter.ToString(await HashCompat.Md5HashDataAsync(itemMs).ConfigureAwait(false)));
			}

			WriteSortedItemHashes(writer, itemHashes);
			return;
		}

		// Handle primitive types and strings
		if (IsSimpleValue(type, value))
		{
			writer.Write(value.ToString()!);
			return;
		}

		// Handle complex objects recursively
		IOrderedEnumerable<PropertyInfo> properties = GetReadablePropertiesOrderedByName(type);

		writer.Write("{");
		foreach (PropertyInfo property in properties)
		{
			writer.Write(property.Name);
			writer.Write(":");
			await WriteValueAsync(writer, property.GetValue(value)!).ConfigureAwait(false);
			writer.Write(",");
		}
		writer.Write("}");
	}
}
