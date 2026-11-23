using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using CommonNetFuncs.Core.CollectionClasses;
using FastExpressionCompiler;
using ZLinq;
using EHashAlgorithm = CommonNetFuncs.Core.EHashAlgorithm;

namespace BenchmarkSuite;

// Performance candidates for CommonNetFuncs.Core (everything except Strings.cs).
// Each class pits a verbatim copy of the current library implementation ("Original", baseline) against the proposed replacement(s).
// "Shared" variants compile on both net11.0 and netstandard2.1, "Net11" variants rely on APIs that only exist on modern .NET.
// PcVerify.Run() asserts that every Original/Optimized pair produces identical results on edge-case inputs.

#region 1. FixedLruDictionary.GetOrAdd hit path

/// <summary>FixedLruDictionary.GetOrAdd hit (net11): OrderedDictionary Remove+Add (extra hash lookups + O(n) shift) vs IndexOf+RemoveAt vs O(1) LinkedList LRU.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcLruGetOrAddHitBenchmarks
{
	private const int Lookups = 1000;
	private static readonly Func<int, string> Factory = static key => key.ToString(CultureInfo.InvariantCulture);

	private PcLruOriginal<int, string> original = null!;
	private PcLruIndexed<int, string> indexed = null!;
	private PcLruLinked<int, string> linked = null!;
	private int[] accessOrder = null!;

	[Params(100, 1000)]
	public int Capacity { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		original = new(Capacity);
		indexed = new(Capacity);
		linked = new(Capacity);
		for (int i = 0; i < Capacity; i++)
		{
			original.GetOrAdd(i, Factory);
			indexed.GetOrAdd(i, Factory);
			linked.GetOrAdd(i, Factory);
		}

		System.Random random = new(42);
		accessOrder = new int[Lookups];
		for (int i = 0; i < Lookups; i++)
		{
			accessOrder[i] = random.Next(Capacity);
		}
	}

	[Benchmark(Baseline = true)]
	public int Original_RemoveAdd()
	{
		int total = 0;
		foreach (int key in accessOrder)
		{
			total += original.GetOrAdd(key, Factory).Length;
		}
		return total;
	}

	[Benchmark]
	public int Net11_IndexOfRemoveAt()
	{
		int total = 0;
		foreach (int key in accessOrder)
		{
			total += indexed.GetOrAdd(key, Factory).Length;
		}
		return total;
	}

	[Benchmark]
	public int Shared_LinkedListLru()
	{
		int total = 0;
		foreach (int key in accessOrder)
		{
			total += linked.GetOrAdd(key, Factory).Length;
		}
		return total;
	}
}

public sealed class PcLruOriginal<TKey, TValue> where TKey : notnull
{
	private readonly ReaderWriterLockSlim readWriteLock = new();
	private readonly int capacity;
	private readonly OrderedDictionary<TKey, TValue?> dictionary;

	public PcLruOriginal(int capacity)
	{
		this.capacity = capacity;
		dictionary = new OrderedDictionary<TKey, TValue?>(capacity);
	}

	// Verbatim copy of FixedLruDictionary.GetOrAdd (net11 path)
	public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
	{
		readWriteLock.EnterWriteLock();
		try
		{
			if (dictionary.TryGetValue(key, out TValue? existing))
			{
				MoveToMostRecentlyUsed(key, existing);
				return existing!;
			}

			TValue value = valueFactory(key);
			AddNewest(key, value);
			return value;
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	public List<KeyValuePair<TKey, TValue?>> Snapshot()
	{
		return dictionary.ToList();
	}

	private void MoveToMostRecentlyUsed(TKey key, TValue? value)
	{
		if (dictionary.Count > 0 && !EqualityComparer<TKey>.Default.Equals(dictionary.GetAt(dictionary.Count - 1).Key, key))
		{
			dictionary.Remove(key);
			dictionary.Add(key, value);
		}
	}

	private void AddNewest(TKey key, TValue? value)
	{
		if (dictionary.Count >= capacity)
		{
			dictionary.RemoveAt(0);
		}
		dictionary.Add(key, value);
	}
}

public sealed class PcLruIndexed<TKey, TValue> where TKey : notnull
{
	private readonly ReaderWriterLockSlim readWriteLock = new();
	private readonly int capacity;
	private readonly OrderedDictionary<TKey, TValue?> dictionary;

	public PcLruIndexed(int capacity)
	{
		this.capacity = capacity;
		dictionary = new OrderedDictionary<TKey, TValue?>(capacity);
	}

	// One hash lookup (IndexOf) instead of TryGetValue + GetAt(last).Key equality + Remove(key) + Add
	public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
	{
		readWriteLock.EnterWriteLock();
		try
		{
			int index = dictionary.IndexOf(key);
			if (index >= 0)
			{
				TValue? existing = dictionary.GetAt(index).Value;
				if (index != dictionary.Count - 1)
				{
					dictionary.RemoveAt(index);
					dictionary.Add(key, existing);
				}
				return existing!;
			}

			TValue value = valueFactory(key);
			AddNewest(key, value);
			return value;
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	public List<KeyValuePair<TKey, TValue?>> Snapshot()
	{
		return dictionary.ToList();
	}

	private void AddNewest(TKey key, TValue? value)
	{
		if (dictionary.Count >= capacity)
		{
			dictionary.RemoveAt(0);
		}
		dictionary.Add(key, value);
	}
}

public sealed class PcLruLinked<TKey, TValue> where TKey : notnull
{
	private readonly ReaderWriterLockSlim readWriteLock = new();
	private readonly int capacity;
	private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue?>>> lookup;
	private readonly LinkedList<KeyValuePair<TKey, TValue?>> order = new();

	public PcLruLinked(int capacity)
	{
		this.capacity = capacity;
		lookup = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue?>>>(capacity);
	}

	// Verbatim copy of the netstandard2.1 FixedLruDictionary.GetOrAdd path, proposed for every TFM (O(1) move, node reuse => no allocation on hit)
	public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
	{
		readWriteLock.EnterWriteLock();
		try
		{
			if (lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
			{
				MoveToMostRecentlyUsed(node);
				return node.Value.Value!;
			}

			TValue value = valueFactory(key);
			AddNewest(key, value);
			return value;
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	public List<KeyValuePair<TKey, TValue?>> Snapshot()
	{
		return order.ToList();
	}

	private void MoveToMostRecentlyUsed(LinkedListNode<KeyValuePair<TKey, TValue?>> node)
	{
		if (node != order.Last)
		{
			order.Remove(node);
			order.AddLast(node);
		}
	}

	private void AddNewest(TKey key, TValue? value)
	{
		if (lookup.Count >= capacity)
		{
			RemoveInternal(order.First!.Value.Key);
		}

		LinkedListNode<KeyValuePair<TKey, TValue?>> node = order.AddLast(new KeyValuePair<TKey, TValue?>(key, value));
		lookup[key] = node;
	}

	private bool RemoveInternal(TKey key)
	{
		if (!lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
		{
			return false;
		}

		order.Remove(node);
		lookup.Remove(key);
		return true;
	}
}

#endregion

#region 2. Copy cache hit path (GetLimitedCache()/GetCache() wrappers)

/// <summary>Copy cache hit: GetLimitedCache()/GetCache() ReadOnlyDictionary wrapper (+ extra RWLS read lock) vs direct TryGetValue on the underlying cache.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcCopyCacheLookupBenchmarks
{
	private const int Lookups = 300;
	private readonly (Type Source, Type Dest)[] keys = [(typeof(NestedClass), typeof(NestedClass)), (typeof(SimpleClass), typeof(SimpleClass)), (typeof(ComplexClass), typeof(ComplexClass))];
	private PcCacheManagerModel<(Type Source, Type Dest), Func<object, object?, int, int, object?>> manager = null!;

	[Params(true, false)]
	public bool UseLimitedCache { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		manager = new(100, UseLimitedCache);
		foreach ((Type Source, Type Dest) key in keys)
		{
			PcCopyCacheImpl.OriginalGetOrAdd(manager, key);
		}
	}

	[Benchmark(Baseline = true)]
	public int Original_ReadOnlyWrapper()
	{
		int hits = 0;
		for (int i = 0; i < Lookups; i++)
		{
			if (PcCopyCacheImpl.OriginalGetOrAdd(manager, keys[i % keys.Length]) != null)
			{
				hits++;
			}
		}
		return hits;
	}

	[Benchmark]
	public int Shared_DirectTryGetValue()
	{
		int hits = 0;
		for (int i = 0; i < Lookups; i++)
		{
			if (PcCopyCacheImpl.OptimizedGetOrAdd(manager, keys[i % keys.Length]) != null)
			{
				hits++;
			}
		}
		return hits;
	}
}

/// <summary>Self-contained model of the relevant CacheManagerBase members (same lock + same FixedLruDictionary/ConcurrentDictionary storage).</summary>
public sealed class PcCacheManagerModel<TKey, TValue> where TKey : notnull
{
	private readonly ReaderWriterLockSlim readWriteLock = new();
	private readonly ConcurrentDictionary<TKey, TValue> cache = new();
	private readonly FixedLruDictionary<TKey, TValue> limitedCache;
	private readonly bool useLimitedCache;

	public PcCacheManagerModel(int limitedCacheSize, bool useLimitedCache)
	{
		limitedCache = new FixedLruDictionary<TKey, TValue>(limitedCacheSize);
		this.useLimitedCache = useLimitedCache;
	}

	public bool IsUsingLimitedCache()
	{
		return useLimitedCache;
	}

	// Verbatim copy of CacheManagerBase.GetCache
	public IReadOnlyDictionary<TKey, TValue> GetCache()
	{
		return cache.AsReadOnly();
	}

	// Verbatim copy of CacheManagerBase.GetLimitedCache
	public IReadOnlyDictionary<TKey, TValue?> GetLimitedCache()
	{
		readWriteLock.EnterReadLock();
		try
		{
			return limitedCache.AsReadOnly();
		}
		finally
		{
			readWriteLock.ExitReadLock();
		}
	}

	public bool TryAddLimitedCache(TKey key, TValue value)
	{
		return limitedCache.TryAdd(key, value);
	}

	public bool TryAddCache(TKey key, TValue value)
	{
		return cache.TryAdd(key, value);
	}

	// Proposed CacheManagerBase additions: no wrapper allocation, no extra lock (same lock-free LimitedCache reference read as GetOrAddLimitedCache)
	public bool TryGetCacheValue(TKey key, [MaybeNullWhen(false)] out TValue value)
	{
		return cache.TryGetValue(key, out value);
	}

	public bool TryGetLimitedCacheValue(TKey key, out TValue? value)
	{
		return limitedCache.TryGetValue(key, out value);
	}
}

public static class PcCopyCacheImpl
{
	private static readonly Func<object, object?, int, int, object?> CopyStub = static (source, _, _, _) => source;

	// Stand-in for Copy.CreateCopyFunction (miss path only)
	private static Func<object, object?, int, int, object?> CreateCopyFunction(Type sourceType, Type destType)
	{
		return CopyStub;
	}

	// Verbatim copy of Copy.GetOrAddFunctionFromDeepCopyCache (GetOrAddFunctionFromCopyCache / GetOrCreatePropertyMaps use the same lookup)
	public static Func<object, object?, int, int, object?> OriginalGetOrAdd(PcCacheManagerModel<(Type Source, Type Dest), Func<object, object?, int, int, object?>> DeepCopyCacheManager, (Type sourceType, Type destType) key)
	{
		if (DeepCopyCacheManager.IsUsingLimitedCache() ? DeepCopyCacheManager.GetLimitedCache().TryGetValue(key, out Func<object, object?, int, int, object?>? function) :
					DeepCopyCacheManager.GetCache().TryGetValue(key, out function))
		{
			return function!;
		}

		function = CreateCopyFunction(key.sourceType, key.destType);
		if (DeepCopyCacheManager.IsUsingLimitedCache())
		{
			DeepCopyCacheManager.TryAddLimitedCache(key, function);
		}
		else
		{
			DeepCopyCacheManager.TryAddCache(key, function);
		}

		return function;
	}

	public static Func<object, object?, int, int, object?> OptimizedGetOrAdd(PcCacheManagerModel<(Type Source, Type Dest), Func<object, object?, int, int, object?>> DeepCopyCacheManager, (Type sourceType, Type destType) key)
	{
		if (DeepCopyCacheManager.IsUsingLimitedCache() ? DeepCopyCacheManager.TryGetLimitedCacheValue(key, out Func<object, object?, int, int, object?>? function) :
					DeepCopyCacheManager.TryGetCacheValue(key, out function))
		{
			return function!;
		}

		function = CreateCopyFunction(key.sourceType, key.destType);
		if (DeepCopyCacheManager.IsUsingLimitedCache())
		{
			DeepCopyCacheManager.TryAddLimitedCache(key, function);
		}
		else
		{
			DeepCopyCacheManager.TryAddCache(key, function);
		}

		return function;
	}
}

#endregion

#region 3. FixedFifoDictionary.GetOrAdd under contention

/// <summary>FixedFifoDictionary.GetOrAdd hit: upgradeable read lock (exclusive between callers) vs shared read lock + double-checked write lock.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcFifoGetOrAddContentionBenchmarks
{
	private const int LookupsPerThread = 10_000;
	private static readonly Func<int, string> Factory = static key => key.ToString(CultureInfo.InvariantCulture);

	private PcFifoOriginal<int, string> original = null!;
	private PcFifoReadLock<int, string> readLock = null!;

	[Params(1, 8)]
	public int Threads { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		original = new(100);
		readLock = new(100);
		for (int i = 0; i < 64; i++)
		{
			original.GetOrAdd(i, Factory);
			readLock.GetOrAdd(i, Factory);
		}
	}

	[Benchmark(Baseline = true)]
	public long Original_UpgradeableReadLock()
	{
		long total = 0;
		Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, worker =>
		{
			long local = 0;
			for (int i = 0; i < LookupsPerThread; i++)
			{
				local += original.GetOrAdd((i + worker) & 63, Factory).Length;
			}
			Interlocked.Add(ref total, local);
		});
		return total;
	}

	[Benchmark]
	public long Shared_ReadLockDoubleCheck()
	{
		long total = 0;
		Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, worker =>
		{
			long local = 0;
			for (int i = 0; i < LookupsPerThread; i++)
			{
				local += readLock.GetOrAdd((i + worker) & 63, Factory).Length;
			}
			Interlocked.Add(ref total, local);
		});
		return total;
	}
}

public sealed class PcFifoOriginal<TKey, TValue> where TKey : notnull
{
	private readonly ReaderWriterLockSlim readWriteLock = new();
	private readonly int capacity;
	private readonly OrderedDictionary<TKey, TValue?> dictionary;

	public PcFifoOriginal(int capacity)
	{
		this.capacity = capacity;
		dictionary = new OrderedDictionary<TKey, TValue?>(capacity);
	}

	// Verbatim copy of FixedFifoDictionary.GetOrAdd (net11 path)
	public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
	{
		readWriteLock.EnterUpgradeableReadLock();
		try
		{
			if (dictionary.TryGetValue(key, out TValue? existing))
			{
				return existing!;
			}

			readWriteLock.EnterWriteLock();
			try
			{
				TValue value = valueFactory(key);
				AddNewest(key, value);
				return value;
			}
			finally
			{
				readWriteLock.ExitWriteLock();
			}
		}
		finally
		{
			readWriteLock.ExitUpgradeableReadLock();
		}
	}

	public List<KeyValuePair<TKey, TValue?>> Snapshot()
	{
		return dictionary.ToList();
	}

	private void AddNewest(TKey key, TValue? value)
	{
		if (dictionary.Count >= capacity)
		{
			dictionary.RemoveAt(0);
		}
		dictionary.Add(key, value);
	}
}

public sealed class PcFifoReadLock<TKey, TValue> where TKey : notnull
{
	private readonly ReaderWriterLockSlim readWriteLock = new();
	private readonly int capacity;
	private readonly OrderedDictionary<TKey, TValue?> dictionary;

	public PcFifoReadLock(int capacity)
	{
		this.capacity = capacity;
		dictionary = new OrderedDictionary<TKey, TValue?>(capacity);
	}

	public TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
	{
		readWriteLock.EnterReadLock();
		try
		{
			if (dictionary.TryGetValue(key, out TValue? existing))
			{
				return existing!;
			}
		}
		finally
		{
			readWriteLock.ExitReadLock();
		}

		readWriteLock.EnterWriteLock();
		try
		{
			// Another thread may have added the key between releasing the read lock and acquiring the write lock, so the factory still runs at most once per key
			if (dictionary.TryGetValue(key, out TValue? added))
			{
				return added!;
			}

			TValue value = valueFactory(key);
			AddNewest(key, value);
			return value;
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	public List<KeyValuePair<TKey, TValue?>> Snapshot()
	{
		return dictionary.ToList();
	}

	private void AddNewest(TKey key, TValue? value)
	{
		if (dictionary.Count >= capacity)
		{
			dictionary.RemoveAt(0);
		}
		dictionary.Add(key, value);
	}
}

#endregion

#region 4. Inspect.IsEqual AsyncLocal context

/// <summary>Inspect.IsEqual top-level call: AsyncLocal set/reset + ComparisonContext + HashSet per call vs skipping the context when the type has no recursive properties.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcIsEqualBenchmarks
{
	private SimpleClass flatA = null!;
	private SimpleClass flatB = null!;
	private NestedClass nestedA = null!;
	private NestedClass nestedB = null!;

	[Params(false, true)]
	public bool Nested { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		flatA = PcData.CreateSimple(1);
		flatB = PcData.CreateSimple(1);
		nestedA = PcData.CreateNested();
		nestedB = PcData.CreateNested();
	}

	[Benchmark(Baseline = true)]
	public bool Original_AsyncLocalContext()
	{
		return Nested ? PcIsEqualImpl.OriginalIsEqual(nestedA, nestedB) : PcIsEqualImpl.OriginalIsEqual(flatA, flatB);
	}

	[Benchmark]
	public bool Shared_SkipContextForFlatTypes()
	{
		return Nested ? PcIsEqualImpl.OptimizedIsEqual(nestedA, nestedB) : PcIsEqualImpl.OptimizedIsEqual(flatA, flatB);
	}
}

public static class PcIsEqualImpl
{
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

		public bool HasRecursion { get; } = hasRecursion;
	}

	private static readonly AsyncLocal<ComparisonContext?> OriginalContext = new();
	private static readonly ConcurrentDictionary<(Type Type, bool IgnoreStringCase, bool Recursive), Func<object, object, IEnumerable<string>, bool>> OriginalDelegates = new();

	private static readonly AsyncLocal<ComparisonContext?> OptimizedContext = new();
	private static readonly ConcurrentDictionary<(Type Type, bool IgnoreStringCase, bool Recursive), CompareEntry> OptimizedDelegates = new();

	// Verbatim copy of Inspect.IsEqual
	public static bool OriginalIsEqual(object? obj1, object? obj2, IEnumerable<string>? exemptProps = null, bool ignoreStringCase = false, bool recursive = true)
	{
		bool isTopLevel = OriginalContext.Value == null;
		if (isTopLevel)
		{
			OriginalContext.Value = new ComparisonContext();
		}

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

			if (!OriginalContext.Value!.TryAddPair(obj1, obj2))
			{
				return true;
			}

			exemptProps ??= [];
			Func<object, object, IEnumerable<string>, bool> compareDelegate = OriginalDelegates.GetOrAdd((type, ignoreStringCase, recursive),
				static key => BuildCompareDelegate(key.Type, key.IgnoreStringCase, key.Recursive, nameof(OriginalIsEqual), out _));

			return compareDelegate(obj1, obj2, exemptProps);
		}
		finally
		{
			if ((obj1 != null) && (obj2 != null))
			{
				OriginalContext.Value?.RemovePair(obj1, obj2);
			}

			if (isTopLevel)
			{
				OriginalContext.Value = null;
			}
		}
	}

	public static bool OptimizedIsEqual(object? obj1, object? obj2, IEnumerable<string>? exemptProps = null, bool ignoreStringCase = false, bool recursive = true)
	{
		if (OptimizedContext.Value == null)
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

			CompareEntry entry = OptimizedDelegates.GetOrAdd((topLevelType, ignoreStringCase, recursive), static key => CreateEntry(key.Type, key.IgnoreStringCase, key.Recursive));
			if (!entry.HasRecursion)
			{
				// The compiled comparer can never re-enter IsEqual, so the cycle-tracking context would never be read
				return entry.Compare(obj1, obj2, exemptProps ?? Array.Empty<string>());
			}

			ComparisonContext context = new();
			OptimizedContext.Value = context;
			try
			{
				context.TryAddPair(obj1, obj2);
				return entry.Compare(obj1, obj2, exemptProps ?? Array.Empty<string>());
			}
			finally
			{
				OptimizedContext.Value?.RemovePair(obj1, obj2);
				OptimizedContext.Value = null;
			}
		}

		// Nested (recursive) call: identical to the original non-top-level path
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

			if (!OptimizedContext.Value!.TryAddPair(obj1, obj2))
			{
				return true;
			}

			exemptProps ??= [];
			CompareEntry entry = OptimizedDelegates.GetOrAdd((type, ignoreStringCase, recursive), static key => CreateEntry(key.Type, key.IgnoreStringCase, key.Recursive));
			return entry.Compare(obj1, obj2, exemptProps);
		}
		finally
		{
			if ((obj1 != null) && (obj2 != null))
			{
				OptimizedContext.Value?.RemovePair(obj1, obj2);
			}
		}
	}

	private static CompareEntry CreateEntry(Type type, bool ignoreStringCase, bool recursive)
	{
		Func<object, object, IEnumerable<string>, bool> compare = BuildCompareDelegate(type, ignoreStringCase, recursive, nameof(OptimizedIsEqual), out bool hasRecursion);
		return new CompareEntry(compare, hasRecursion);
	}

	// Verbatim copy of Inspect.CreateCompareDelegate, parameterized only by the IsEqual method used for recursion and reporting whether that branch was emitted
	private static Func<object, object, IEnumerable<string>, bool> BuildCompareDelegate(Type type, bool ignoreStringCase, bool recursive, string isEqualMethodName, out bool hasRecursion)
	{
		hasRecursion = false;
		ParameterExpression obj1Param = Expression.Parameter(typeof(object), "obj1");
		ParameterExpression obj2Param = Expression.Parameter(typeof(object), "obj2");
		ParameterExpression exemptPropsParam = Expression.Parameter(typeof(IEnumerable<string>), "exemptProps");

		UnaryExpression typedObj1 = Expression.Convert(obj1Param, type);
		UnaryExpression typedObj2 = Expression.Convert(obj2Param, type);

		IEnumerable<PropertyInfo> properties = CommonNetFuncs.Core.ReflectionCaches.GetOrAddPropertiesFromReflectionCache(type).Where(x => x.CanRead && (x.GetIndexParameters().Length == 0));

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
				MethodInfo? isEqualMethod = typeof(PcIsEqualImpl).GetMethod(isEqualMethodName, [typeof(object), typeof(object), typeof(IEnumerable<string>), typeof(bool), typeof(bool)])!;
				comparison = Expression.Call(isEqualMethod, value1, value2, Expression.Constant(null, typeof(IEnumerable<string>)), Expression.Constant(ignoreStringCase), Expression.Constant(recursive));
				hasRecursion = true;
			}
			else
			{
				comparison = Expression.Constant(true);
			}

			comparisons.Add(Expression.Condition(propExemptCheck, Expression.Constant(true), comparison));
		}

		Expression andAlsoExpression = comparisons.Aggregate(Expression.AndAlso);

		Expression<Func<object, object, IEnumerable<string>, bool>> lambda = Expression.Lambda<Func<object, object, IEnumerable<string>, bool>>(andAlsoExpression, obj1Param, obj2Param, exemptPropsParam);

		return lambda.CompileFast();
	}
}

#endregion

#region 5. Inspect.GetHashForObject

/// <summary>Inspect.GetHashForObject: Where+OrderBy re-sorted per object/nested object + ms.ToArray() + undisposed HashAlgorithm vs cached ordering + GetBuffer (+ one-shot HashData on net11).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcGetHashForObjectBenchmarks
{
	private readonly PcHashOrder order = PcData.CreateHashOrder();

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return PcHashImpl.Original(order);
	}

	[Benchmark]
	public string Shared_CachedOrderGetBuffer()
	{
		return PcHashImpl.Shared(order);
	}

	[Benchmark]
	public string Net11_CachedOrderHashData()
	{
		return PcHashImpl.Net11(order);
	}
}

public sealed class PcHashChild
{
	public int Value { get; set; }

	public string? Label { get; set; }
}

public sealed class PcHashOrder
{
	public int Id { get; set; }

	public string? Name { get; set; }

	public decimal Price { get; set; }

	public bool IsActive { get; set; }

	public List<int>? Numbers { get; set; }

	public List<string>? Tags { get; set; }

	public Dictionary<string, int>? Counts { get; set; }

	public PcHashChild? Child { get; set; }
}

public static class PcHashImpl
{
	private static readonly ConcurrentDictionary<(Type Type, CultureInfo Culture), PropertyInfo[]> OrderedPropertiesCache = new();

	// Verbatim copy of Inspect.GetHashForObject (net11 build: HashCompat => Convert.ToHexStringLower / MD5.HashData)
	public static string Original<T>(T obj, EHashAlgorithm hashAlgorithm = EHashAlgorithm.MD5) where T : class?
	{
		if (obj == null)
		{
			return "null";
		}

		HashAlgorithm algorithm = CreateHashAlgorithm(hashAlgorithm);
		IOrderedEnumerable<PropertyInfo> properties = GetReadablePropertiesOrderedByName(typeof(T));

		using MemoryStream ms = new();
		using BinaryWriter writer = new(ms);
		foreach (PropertyInfo property in properties)
		{
			object? value = property.GetValue(obj);
			if (value != null)
			{
				OriginalWriteValue(writer, value);
			}
		}

		return Convert.ToHexStringLower(algorithm.ComputeHash(ms.ToArray()));
	}

	public static string Shared<T>(T obj, EHashAlgorithm hashAlgorithm = EHashAlgorithm.MD5) where T : class?
	{
		if (obj == null)
		{
			return "null";
		}

		using HashAlgorithm algorithm = CreateHashAlgorithm(hashAlgorithm);
		using MemoryStream ms = new();
		using BinaryWriter writer = new(ms);
		foreach (PropertyInfo property in GetReadablePropertiesOrderedByNameCached(typeof(T)))
		{
			object? value = property.GetValue(obj);
			if (value != null)
			{
				OptimizedWriteValue(writer, value, false);
			}
		}

		return Convert.ToHexStringLower(algorithm.ComputeHash(ms.GetBuffer(), 0, (int)ms.Length));
	}

	public static string Net11<T>(T obj, EHashAlgorithm hashAlgorithm = EHashAlgorithm.MD5) where T : class?
	{
		if (obj == null)
		{
			return "null";
		}

		using MemoryStream ms = new();
		using BinaryWriter writer = new(ms);
		foreach (PropertyInfo property in GetReadablePropertiesOrderedByNameCached(typeof(T)))
		{
			object? value = property.GetValue(obj);
			if (value != null)
			{
				OptimizedWriteValue(writer, value, true);
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
		return CommonNetFuncs.Core.ReflectionCaches.GetOrAddPropertiesFromReflectionCache(type).Where(x => x.CanRead).OrderBy(x => x.Name);
	}

	private static PropertyInfo[] GetReadablePropertiesOrderedByNameCached(Type type)
	{
		// OrderBy(Name) uses the current culture's string comparer, so the culture is part of the key to keep the ordering identical
		return OrderedPropertiesCache.GetOrAdd((type, CultureInfo.CurrentCulture), static key => GetReadablePropertiesOrderedByName(key.Type).ToArray());
	}

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

	// Verbatim copy of Inspect.WriteValue
	private static void OriginalWriteValue(BinaryWriter writer, object value)
	{
		if (value == null)
		{
			writer.Write("null");
			return;
		}

		Type type = value.GetType();

		if (value is System.Collections.IEnumerable enumerable and not string)
		{
			List<string> itemHashes = [];
			foreach (object item in enumerable)
			{
				using MemoryStream itemMs = new();
				using BinaryWriter itemWriter = new(itemMs);
				OriginalWriteValue(itemWriter, item);
				itemHashes.Add(BitConverter.ToString(MD5.HashData(itemMs.ToArray())));
			}

			WriteSortedItemHashes(writer, itemHashes);
			return;
		}

		if (IsSimpleValue(type, value))
		{
			writer.Write(value.ToString()!);
			return;
		}

		IOrderedEnumerable<PropertyInfo> properties = GetReadablePropertiesOrderedByName(type);

		writer.Write("{");
		foreach (PropertyInfo property in properties)
		{
			writer.Write(property.Name);
			writer.Write(":");
			OriginalWriteValue(writer, property.GetValue(value)!);
			writer.Write(",");
		}
		writer.Write("}");
	}

	private static void OptimizedWriteValue(BinaryWriter writer, object value, bool spanItemHash)
	{
		if (value == null)
		{
			writer.Write("null");
			return;
		}

		Type type = value.GetType();

		if (value is System.Collections.IEnumerable enumerable and not string)
		{
			List<string> itemHashes = [];
			foreach (object item in enumerable)
			{
				using MemoryStream itemMs = new();
				using BinaryWriter itemWriter = new(itemMs);
				OptimizedWriteValue(itemWriter, item, spanItemHash);
				byte[] itemHash = spanItemHash ? MD5.HashData(itemMs.GetBuffer().AsSpan(0, (int)itemMs.Length)) : MD5.HashData(itemMs.ToArray());
				itemHashes.Add(BitConverter.ToString(itemHash));
			}

			WriteSortedItemHashes(writer, itemHashes);
			return;
		}

		if (IsSimpleValue(type, value))
		{
			writer.Write(value.ToString()!);
			return;
		}

		writer.Write("{");
		foreach (PropertyInfo property in GetReadablePropertiesOrderedByNameCached(type))
		{
			writer.Write(property.Name);
			writer.Write(":");
			OptimizedWriteValue(writer, property.GetValue(value)!, spanItemHash);
			writer.Write(",");
		}
		writer.Write("}");
	}
}

#endregion

#region 6. GetBusinessDays (DateTime / DateOnly)

/// <summary>DateTimeHelpers.GetBusinessDays: decimal multiply/divide chain vs exact integer math for whole-day spans (decimal fallback for partial days).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcBusinessDaysDateTimeBenchmarks
{
	private const int Pairs = 512;
	private readonly DateTime[] starts = new DateTime[Pairs];
	private readonly DateTime[] ends = new DateTime[Pairs];
	private readonly List<DateTime> holidays = PcData.CreateHolidays();

	[GlobalSetup]
	public void Setup()
	{
		System.Random random = new(7);
		for (int i = 0; i < Pairs; i++)
		{
			starts[i] = new DateTime(2020, 1, 1).AddDays(random.Next(0, 2000));
			ends[i] = starts[i].AddDays(random.Next(0, 400));
		}
	}

	[Benchmark(Baseline = true)]
	public int Original_Decimal()
	{
		int total = 0;
		for (int i = 0; i < Pairs; i++)
		{
			total += PcBusinessDaysImpl.OriginalDateTime(starts[i], ends[i], holidays);
		}
		return total;
	}

	[Benchmark]
	public int Shared_IntegerMath()
	{
		int total = 0;
		for (int i = 0; i < Pairs; i++)
		{
			total += PcBusinessDaysImpl.OptimizedDateTime(starts[i], ends[i], holidays);
		}
		return total;
	}
}

/// <summary>DateOnlyHelpers.GetBusinessDays: decimal multiply/divide chain vs exact integer math (5*days - 2*dowDiff is always a multiple of 7).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcBusinessDaysDateOnlyBenchmarks
{
	private const int Pairs = 512;
	private readonly DateOnly[] starts = new DateOnly[Pairs];
	private readonly DateOnly[] ends = new DateOnly[Pairs];
	private readonly List<DateOnly> holidays = PcData.CreateHolidays().ConvertAll(DateOnly.FromDateTime);

	[GlobalSetup]
	public void Setup()
	{
		System.Random random = new(7);
		for (int i = 0; i < Pairs; i++)
		{
			starts[i] = new DateOnly(2020, 1, 1).AddDays(random.Next(0, 2000));
			ends[i] = starts[i].AddDays(random.Next(0, 400));
		}
	}

	[Benchmark(Baseline = true)]
	public int Original_Decimal()
	{
		int total = 0;
		for (int i = 0; i < Pairs; i++)
		{
			total += PcBusinessDaysImpl.OriginalDateOnly(starts[i], ends[i], holidays);
		}
		return total;
	}

	[Benchmark]
	public int Shared_IntegerMath()
	{
		int total = 0;
		for (int i = 0; i < Pairs; i++)
		{
			total += PcBusinessDaysImpl.OptimizedDateOnly(starts[i], ends[i], holidays);
		}
		return total;
	}
}

public static class PcBusinessDaysImpl
{
	// Verbatim copy of DateTimeHelpers.GetBusinessDays
	public static int OriginalDateTime(DateTime? startDate, DateTime? endDate, List<DateTime>? exceptionDates = null)
	{
		if ((startDate == null) || (endDate == null))
		{
			return 0;
		}

		DateTime sDate = (DateTime)startDate;
		DateTime eDate = (DateTime)endDate;

		decimal calcBusinessDays = 1 + (((((decimal)(eDate - sDate).TotalDays) * 5m) - ((sDate.DayOfWeek - eDate.DayOfWeek) * 2m)) / 7m);

		if (eDate.DayOfWeek == DayOfWeek.Saturday)
		{
			calcBusinessDays--;
		}

		if (sDate.DayOfWeek == DayOfWeek.Sunday)
		{
			calcBusinessDays--;
		}

		if (exceptionDates?.Count > 0)
		{
			int exceptionDays = 0;
			foreach (DateTime exceptionDate in exceptionDates)
			{
				if (exceptionDate >= sDate && exceptionDate <= eDate &&
					exceptionDate.DayOfWeek != DayOfWeek.Saturday &&
					exceptionDate.DayOfWeek != DayOfWeek.Sunday)
				{
					exceptionDays++;
				}
			}
			calcBusinessDays -= exceptionDays;
		}

		return (int)calcBusinessDays;
	}

	public static int OptimizedDateTime(DateTime? startDate, DateTime? endDate, List<DateTime>? exceptionDates = null)
	{
		if ((startDate == null) || (endDate == null))
		{
			return 0;
		}

		DateTime sDate = startDate.Value;
		DateTime eDate = endDate.Value;
		long tickDiff = eDate.Ticks - sDate.Ticks;
		if (tickDiff % TimeSpan.TicksPerDay != 0)
		{
			// Partial days make the decimal intermediate fractional, keep the exact original arithmetic for them
			return OriginalDateTime(startDate, endDate, exceptionDates);
		}

		DayOfWeek startDayOfWeek = sDate.DayOfWeek;
		DayOfWeek endDayOfWeek = eDate.DayOfWeek;

		// For whole-day spans endDow - startDow == days (mod 7), so 5 * days - 2 * (startDow - endDow) is a multiple of 7 and the division is exact
		int calcBusinessDays = 1 + ((((int)(tickDiff / TimeSpan.TicksPerDay) * 5) - ((startDayOfWeek - endDayOfWeek) * 2)) / 7);

		if (endDayOfWeek == DayOfWeek.Saturday)
		{
			calcBusinessDays--;
		}

		if (startDayOfWeek == DayOfWeek.Sunday)
		{
			calcBusinessDays--;
		}

		if (exceptionDates?.Count > 0)
		{
			int exceptionDays = 0;
			foreach (DateTime exceptionDate in exceptionDates)
			{
				if (exceptionDate >= sDate && exceptionDate <= eDate &&
					exceptionDate.DayOfWeek != DayOfWeek.Saturday &&
					exceptionDate.DayOfWeek != DayOfWeek.Sunday)
				{
					exceptionDays++;
				}
			}
			calcBusinessDays -= exceptionDays;
		}

		return calcBusinessDays;
	}

	// Verbatim copy of DateOnlyHelpers.GetBusinessDays
	public static int OriginalDateOnly(DateOnly? startDate, DateOnly? endDate, List<DateOnly>? exceptionDates = null)
	{
		if ((startDate == null) || (endDate == null))
		{
			return 0;
		}

		DateOnly sDate = (DateOnly)startDate;
		DateOnly eDate = (DateOnly)endDate;

		decimal calcBusinessDays = 1 + ((((eDate.DayNumber - sDate.DayNumber) * 5m) - ((sDate.DayOfWeek - eDate.DayOfWeek) * 2m)) / 7m);

		if (eDate.DayOfWeek == DayOfWeek.Saturday)
		{
			calcBusinessDays--;
		}

		if (sDate.DayOfWeek == DayOfWeek.Sunday)
		{
			calcBusinessDays--;
		}

		if (exceptionDates != null)
		{
			int exceptionDays = 0;
			foreach (DateOnly exceptionDate in exceptionDates)
			{
				if ((exceptionDate >= sDate) && (exceptionDate <= eDate) &&
						(exceptionDate.DayOfWeek != DayOfWeek.Saturday) &&
						(exceptionDate.DayOfWeek != DayOfWeek.Sunday))
				{
					exceptionDays++;
				}
			}
			calcBusinessDays -= exceptionDays;
		}

		return (int)calcBusinessDays;
	}

	public static int OptimizedDateOnly(DateOnly? startDate, DateOnly? endDate, List<DateOnly>? exceptionDates = null)
	{
		if ((startDate == null) || (endDate == null))
		{
			return 0;
		}

		DateOnly sDate = startDate.Value;
		DateOnly eDate = endDate.Value;
		DayOfWeek startDayOfWeek = sDate.DayOfWeek;
		DayOfWeek endDayOfWeek = eDate.DayOfWeek;

		// endDow - startDow == days (mod 7), so 5 * days - 2 * (startDow - endDow) is always a multiple of 7 and the division is exact
		int calcBusinessDays = 1 + ((((eDate.DayNumber - sDate.DayNumber) * 5) - ((startDayOfWeek - endDayOfWeek) * 2)) / 7);

		if (endDayOfWeek == DayOfWeek.Saturday)
		{
			calcBusinessDays--;
		}

		if (startDayOfWeek == DayOfWeek.Sunday)
		{
			calcBusinessDays--;
		}

		if (exceptionDates != null)
		{
			int exceptionDays = 0;
			foreach (DateOnly exceptionDate in exceptionDates)
			{
				if ((exceptionDate >= sDate) && (exceptionDate <= eDate) &&
						(exceptionDate.DayOfWeek != DayOfWeek.Saturday) &&
						(exceptionDate.DayOfWeek != DayOfWeek.Sunday))
				{
					exceptionDays++;
				}
			}
			calcBusinessDays -= exceptionDays;
		}

		return calcBusinessDays;
	}
}

#endregion

#region 7. Collections.IndexOf

/// <summary>Collections.IndexOf: ZLinq Select((a, i) => new { a, i }) (one heap object per scanned element + closure) vs a plain indexed/foreach scan.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcIndexOfBenchmarks
{
	private IEnumerable<string> source = null!;

	[Params("List", "HashSet")]
	public string SourceKind { get; set; } = "List";

	[GlobalSetup]
	public void Setup()
	{
		List<string> items = Enumerable.Range(0, 1000).Select(static i => $"Item_{i}").ToList();
		source = SourceKind == "List" ? items : new HashSet<string>(items);
	}

	[Benchmark(Baseline = true)]
	public int Original_AnonymousPairs()
	{
		return PcIndexOfImpl.Original(source, "Item_750", null);
	}

	[Benchmark]
	public int Shared_Loop()
	{
		return PcIndexOfImpl.Optimized(source, "Item_750", null);
	}
}

public static class PcIndexOfImpl
{
	// Verbatim copy of Collections.IndexOf(IEnumerable<T>, T, IEqualityComparer<T>?)
	public static int Original<T>(IEnumerable<T> collection, T value, IEqualityComparer<T>? comparer)
	{
		comparer ??= EqualityComparer<T>.Default;
		var found = collection.AsValueEnumerable().Select((a, i) => new { a, i }).FirstOrDefault(x => comparer.Equals(x.a, value));
		return (found == null) ? (-1) : found.i;
	}

	public static int Optimized<T>(IEnumerable<T> collection, T value, IEqualityComparer<T>? comparer)
	{
		if (collection is null)
		{
			// Defer to the original so a null source fails in exactly the same way
			return Original(collection!, value, comparer);
		}

		comparer ??= EqualityComparer<T>.Default;
		if (collection is T[] array)
		{
			for (int i = 0; i < array.Length; i++)
			{
				if (comparer.Equals(array[i], value))
				{
					return i;
				}
			}
			return -1;
		}

		if (collection is List<T> list)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (comparer.Equals(list[i], value))
				{
					return i;
				}
			}
			return -1;
		}

		int index = 0;
		foreach (T item in collection)
		{
			if (comparer.Equals(item, value))
			{
				return index;
			}
			index++;
		}
		return -1;
	}
}

#endregion

#region 8-9. Collections.GetCombinations / GetRandomCombinations

/// <summary>Collections.GetCombinations: new List&lt;string&gt; copy per partial combination per level + final string.Join vs building the joined prefix strings directly.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcGetCombinationsBenchmarks
{
	private readonly List<List<string?>> sources = PcData.CreateCombinationSources();

	[Benchmark(Baseline = true)]
	public int Original_ListPerCombination()
	{
		return PcCombinationsImpl.OriginalGetCombinations(sources).Count;
	}

	[Benchmark]
	public int Shared_IncrementalStrings()
	{
		return PcCombinationsImpl.OptimizedGetCombinations(sources).Count;
	}
}

/// <summary>Collections.GetRandomCombinations: recomputes the full combination set a second time when maxCombinations is smaller vs reusing the first set.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcGetRandomCombinationsBenchmarks
{
	private readonly List<List<string?>> sources = PcData.CreateCombinationSources();

	[Benchmark(Baseline = true)]
	public int Original_ComputeTwice()
	{
		return PcCombinationsImpl.OriginalGetRandomCombinations(sources, 10).Count;
	}

	[Benchmark]
	public int Shared_ReuseSet()
	{
		return PcCombinationsImpl.OptimizedGetRandomCombinations(sources, 10).Count;
	}
}

public static class PcCombinationsImpl
{
	// Verbatim copy of Collections.GetCombinations (inner lambda parameter renamed only)
	public static HashSet<string> OriginalGetCombinations(IEnumerable<IEnumerable<string?>> sources, int? maxCombinations = null, string separator = "|", string? nullReplacement = default)
	{
		if (maxCombinations.HasValue && maxCombinations < 1)
		{
			throw new ArgumentException("Max combinations must be either null, or greater than 0", nameof(maxCombinations));
		}

		string?[][] sourcesArray = sources.Select(x => x.Any() ? x.Distinct().Select(v => v ?? nullReplacement).ToArray() : [nullReplacement]).ToArray();

		if (!CommonNetFuncs.Core.Collections.AnyFast(sourcesArray))
		{
			return [];
		}

		int length = sourcesArray.Length;

		List<List<string>> current = sourcesArray[0].Select(x => (List<string>)[x?.ToString() ?? string.Empty]).ToList();

		for (int i = 1; i < length; i++)
		{
			current = current.AsValueEnumerable().SelectMany(existingCombo => sourcesArray[i].Select(x => new List<string>(existingCombo) { x?.ToString() ?? string.Empty })).ToList();
			if (maxCombinations.HasValue && current.Count >= maxCombinations)
			{
				break;
			}
		}

		return maxCombinations == null || !maxCombinations.HasValue || current.Count < maxCombinations
			? new HashSet<string>(current.Select(x => string.Join(separator, x)))
			: current.Select(x => string.Join(separator, x)).Take((int)maxCombinations).ToHashSet();
	}

	public static HashSet<string> OptimizedGetCombinations(IEnumerable<IEnumerable<string?>> sources, int? maxCombinations = null, string separator = "|", string? nullReplacement = default)
	{
		if (maxCombinations.HasValue && maxCombinations < 1)
		{
			throw new ArgumentException("Max combinations must be either null, or greater than 0", nameof(maxCombinations));
		}

		string?[][] sourcesArray = sources.Select(x => x.Any() ? x.Distinct().Select(v => v ?? nullReplacement).ToArray() : [nullReplacement]).ToArray();

		if (!CommonNetFuncs.Core.Collections.AnyFast(sourcesArray))
		{
			return [];
		}

		string?[] first = sourcesArray[0];
		List<string> current = new(first.Length);
		foreach (string? value in first)
		{
			current.Add(value ?? string.Empty);
		}

		for (int i = 1; i < sourcesArray.Length; i++)
		{
			string?[] next = sourcesArray[i];
			List<string> combined = new((int)Math.Min((long)current.Count * next.Length, 1 << 20));
			foreach (string prefix in current)
			{
				foreach (string? value in next)
				{
					// Equivalent to string.Join(separator, parts): Concat treats a null separator as empty just like Join does
					combined.Add(string.Concat(prefix, separator, value ?? string.Empty));
				}
			}

			current = combined;
			if (maxCombinations.HasValue && current.Count >= maxCombinations)
			{
				break;
			}
		}

		return maxCombinations == null || current.Count < maxCombinations
			? new HashSet<string>(current)
			: current.Take((int)maxCombinations).ToHashSet();
	}

	// Verbatim copy of Collections.GetRandomCombinations
	public static HashSet<string> OriginalGetRandomCombinations(IEnumerable<IEnumerable<string?>> sources, int? maxCombinations = null, string separator = "|", string? nullReplacement = default)
	{
		if (maxCombinations is < 1)
		{
			throw new ArgumentException("Max combinations must be either null, or greater than 0", nameof(maxCombinations));
		}

		HashSet<string> allCombinations = OriginalGetCombinations(sources, null, separator, nullReplacement);
		if (maxCombinations != null && maxCombinations < allCombinations.Count)
		{
			return CommonNetFuncs.Core.Random.GetUniqueRandomElements(OriginalGetCombinations(sources, null, separator, nullReplacement), (int)maxCombinations).ToHashSet();
		}
		return CommonNetFuncs.Core.Random.Shuffle(allCombinations).ToHashSet();
	}

	public static HashSet<string> OptimizedGetRandomCombinations(IEnumerable<IEnumerable<string?>> sources, int? maxCombinations = null, string separator = "|", string? nullReplacement = default)
	{
		if (maxCombinations is < 1)
		{
			throw new ArgumentException("Max combinations must be either null, or greater than 0", nameof(maxCombinations));
		}

		HashSet<string> allCombinations = OriginalGetCombinations(sources, null, separator, nullReplacement);
		if (maxCombinations != null && maxCombinations < allCombinations.Count)
		{
			// Same content and insertion order as the recomputed set, so the sampling distribution is unchanged
			return CommonNetFuncs.Core.Random.GetUniqueRandomElements(allCombinations, (int)maxCombinations).ToHashSet();
		}
		return CommonNetFuncs.Core.Random.Shuffle(allCombinations).ToHashSet();
	}
}

#endregion

#region 10. Random.ShuffleListInPlace

/// <summary>Random.ShuffleListInPlace: one RandomNumberGenerator.GetBytes(4) call per element vs filling a 256-byte stack buffer and consuming 4 bytes per element (same distribution/bias).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcShuffleListInPlaceBenchmarks
{
	private List<int> list = null!;

	[Params(100, 10_000)]
	public int Size { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		list = Enumerable.Range(0, Size).ToList();
	}

	[Benchmark(Baseline = true)]
	public int Original_RngCallPerElement()
	{
		return PcShuffleImpl.Original(list)[0];
	}

	[Benchmark]
	public int Shared_BatchedRandomBytes()
	{
		return PcShuffleImpl.Optimized(list)[0];
	}
}

public static class PcShuffleImpl
{
	private const int RandomBufferSize = 256;
	private const string MaxValueError = "Max value must be greater than 0.";
	private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

	// Verbatim copy of Random.GetRandomInt(int, int)
	private static int GetRandomInt(int minValue, int maxValue)
	{
		if (maxValue <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxValue), MaxValueError);
		}

		if (minValue > maxValue)
		{
			throw new ArgumentException($"{nameof(minValue)} must be less than or equal to {nameof(maxValue)}", nameof(minValue));
		}

		if (minValue == maxValue)
		{
			return minValue;
		}

		Span<byte> randomNumber = stackalloc byte[4];
		Rng.GetBytes(randomNumber);
		int result = BitConverter.ToInt32(randomNumber) & 0x7FFFFFFF;
		return minValue + (result % (maxValue - minValue));
	}

	// Verbatim copy of Random.ShuffleListInPlace
	public static IList<T> Original<T>(IList<T> list, CancellationToken cancellationToken = default)
	{
		int n = list.Count;
		while (n > 1)
		{
			cancellationToken.ThrowIfCancellationRequested();
			n--;
			int k = GetRandomInt(0, n + 1);
			(list[n], list[k]) = (list[k], list[n]);
		}
		return list;
	}

	public static IList<T> Optimized<T>(IList<T> list, CancellationToken cancellationToken = default)
	{
		int n = list.Count;
		if (n <= 1)
		{
			return list;
		}

		Span<byte> randomBytes = stackalloc byte[RandomBufferSize];
		int offset = RandomBufferSize;
		while (n > 1)
		{
			cancellationToken.ThrowIfCancellationRequested();
			n--;
			if (offset == RandomBufferSize)
			{
				Rng.GetBytes(randomBytes);
				offset = 0;
			}

			// Same transform as GetRandomInt(0, n + 1): non-negative int32 modulo the range
			int result = BitConverter.ToInt32(randomBytes.Slice(offset, 4)) & 0x7FFFFFFF;
			offset += 4;
			int k = result % (n + 1);
			(list[n], list[k]) = (list[k], list[n]);
		}
		return list;
	}
}

#endregion

#region 11. CountingStream synchronous Write/Read

/// <summary>CountingStream sync Write: WriteAsync(...).GetAwaiter().GetResult() (async method + Task per call) vs inner ValueTask consumed directly when it completed synchronously.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcCountingStreamSyncWriteBenchmarks
{
	private const int Writes = 1000;
	private readonly byte[] chunk = new byte[64];

	[Params(false, true)]
	public bool SpanWrites { get; set; }

	[Benchmark(Baseline = true)]
	public long Original_SyncOverAsyncMethod()
	{
		using MemoryStream inner = new(Writes * 64);
		using PcOriginalCountingStream counting = new(inner);
		for (int i = 0; i < Writes; i++)
		{
			if (SpanWrites)
			{
				counting.Write(chunk.AsSpan());
			}
			else
			{
				counting.Write(chunk, 0, chunk.Length);
			}
		}
		return counting.BytesWritten;
	}

	[Benchmark]
	public long Shared_ValueTaskFastPath()
	{
		using MemoryStream inner = new(Writes * 64);
		using PcOptimizedCountingStream counting = new(inner);
		for (int i = 0; i < Writes; i++)
		{
			if (SpanWrites)
			{
				counting.Write(chunk.AsSpan());
			}
			else
			{
				counting.Write(chunk, 0, chunk.Length);
			}
		}
		return counting.BytesWritten;
	}
}

// Verbatim copy of CountingStream (CopyToAsync override omitted, it is not exercised here)
public sealed class PcOriginalCountingStream(Stream innerStream) : Stream
{
	private readonly Stream innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
	private long bytesWritten;
	private bool disposed;

	public long BytesWritten => Interlocked.Read(ref bytesWritten);

	public override bool CanRead => innerStream.CanRead;

	public override bool CanSeek => innerStream.CanSeek;

	public override bool CanWrite => innerStream.CanWrite;

	public override long Length => innerStream.Length;

	public override long Position { get => innerStream.Position; set => innerStream.Position = value; }

	public override async Task FlushAsync(CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		await innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	public override void Flush()
	{
		ThrowIfDisposed();
		FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		ThrowIfDisposed();
		return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		return await innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		return innerStream.ReadAsync(buffer, cancellationToken);
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		ThrowIfDisposed();
		return innerStream.Seek(offset, origin);
	}

	public override void SetLength(long value)
	{
		ThrowIfDisposed();
		innerStream.SetLength(value);
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		ThrowIfDisposed();
		WriteAsync(buffer, offset, count).GetAwaiter().GetResult();
	}

	public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		await innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
		Interlocked.Add(ref bytesWritten, count);
	}

	public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		await innerStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
		Interlocked.Add(ref bytesWritten, buffer.Length);
	}

	protected override void Dispose(bool disposing)
	{
		if (!disposed)
		{
			if (disposing)
			{
				disposed = true;
			}
			base.Dispose(disposing);
		}
	}

	public override async ValueTask DisposeAsync()
	{
		if (!disposed)
		{
			disposed = true;
			await base.DisposeAsync().ConfigureAwait(false);
			GC.SuppressFinalize(this);
		}
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
	}

	~PcOriginalCountingStream()
	{
		Dispose(false);
	}
}

// Same as the original except the synchronous Write/Read/Flush. They still go through the inner stream's async API (so inner streams that forbid
// synchronous I/O, e.g. Kestrel response bodies, keep working) but skip the extra async method + Task when the inner operation completed synchronously.
public sealed class PcOptimizedCountingStream(Stream innerStream) : Stream
{
	private readonly Stream innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
	private long bytesWritten;
	private bool disposed;

	public long BytesWritten => Interlocked.Read(ref bytesWritten);

	public override bool CanRead => innerStream.CanRead;

	public override bool CanSeek => innerStream.CanSeek;

	public override bool CanWrite => innerStream.CanWrite;

	public override long Length => innerStream.Length;

	public override long Position { get => innerStream.Position; set => innerStream.Position = value; }

	public override async Task FlushAsync(CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		await innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	public override void Flush()
	{
		ThrowIfDisposed();
		innerStream.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		ThrowIfDisposed();
		ValueTask<int> read = innerStream.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None);
		return read.IsCompletedSuccessfully ? read.Result : read.AsTask().GetAwaiter().GetResult();
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		return await innerStream.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
	}

	public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		return innerStream.ReadAsync(buffer, cancellationToken);
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		ThrowIfDisposed();
		return innerStream.Seek(offset, origin);
	}

	public override void SetLength(long value)
	{
		ThrowIfDisposed();
		innerStream.SetLength(value);
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		ThrowIfDisposed();
		ValueTask write = innerStream.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None);
		if (write.IsCompletedSuccessfully)
		{
			write.GetAwaiter().GetResult();
		}
		else
		{
			write.AsTask().GetAwaiter().GetResult();
		}
		Interlocked.Add(ref bytesWritten, count);
	}

	public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		ThrowIfDisposed();
		await innerStream.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
		Interlocked.Add(ref bytesWritten, count);
	}

	public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		await innerStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
		Interlocked.Add(ref bytesWritten, buffer.Length);
	}

	protected override void Dispose(bool disposing)
	{
		if (!disposed)
		{
			if (disposing)
			{
				disposed = true;
			}
			base.Dispose(disposing);
		}
	}

	public override async ValueTask DisposeAsync()
	{
		if (!disposed)
		{
			disposed = true;
			await base.DisposeAsync().ConfigureAwait(false);
			GC.SuppressFinalize(this);
		}
	}

	private void ThrowIfDisposed()
	{
		ObjectDisposedException.ThrowIf(disposed, this);
	}

	~PcOptimizedCountingStream()
	{
		Dispose(false);
	}
}

#endregion

#region 12. Streams.ReadStreamAsync

/// <summary>Streams.ReadStreamAsync: always ms.ToArray() (second full-size copy) vs returning the MemoryStream's buffer when it is exactly the data length.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcReadStreamAsyncBenchmarks
{
	private byte[] payload = null!;

	[Params(16 * 1024, 1024 * 1024)]
	public int PayloadSize { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		payload = new byte[PayloadSize];
		new System.Random(3).NextBytes(payload);
	}

	[Benchmark(Baseline = true)]
	public async Task<int> Original_ToArray()
	{
		using MemoryStream source = new(payload, writable: false);
		byte[] result = await PcReadStreamImpl.OriginalReadStreamAsync(source).ConfigureAwait(false);
		return result.Length;
	}

	[Benchmark]
	public async Task<int> Shared_ExactBuffer()
	{
		using MemoryStream source = new(payload, writable: false);
		byte[] result = await PcReadStreamImpl.OptimizedReadStreamAsync(source).ConfigureAwait(false);
		return result.Length;
	}
}

public static class PcReadStreamImpl
{
	// Verbatim copy of Streams.ReadStreamAsync
	public static async ValueTask<byte[]> OriginalReadStreamAsync(Stream stream, int bufferSize = 4096, CancellationToken cancellationToken = default)
	{
		MemoryStream ms = stream.CanSeek ? new MemoryStream(capacity: (int)stream.Length) : new MemoryStream();

		try
		{
			await stream.CopyToAsync(ms, bufferSize, cancellationToken).ConfigureAwait(false);
			return ms.ToArray();
		}
		finally
		{
			await ms.DisposeAsync().ConfigureAwait(false);
		}
	}

	public static async ValueTask<byte[]> OptimizedReadStreamAsync(Stream stream, int bufferSize = 4096, CancellationToken cancellationToken = default)
	{
		MemoryStream ms = stream.CanSeek ? new MemoryStream(capacity: (int)stream.Length) : new MemoryStream();

		try
		{
			await stream.CopyToAsync(ms, bufferSize, cancellationToken).ConfigureAwait(false);

			// The MemoryStream is private to this method, so its buffer can be handed out when it holds exactly the data (pre-sized seekable source at position 0)
			if (ms.TryGetBuffer(out ArraySegment<byte> segment) && (segment.Offset == 0) && (segment.Count == segment.Array!.Length))
			{
				return segment.Array;
			}
			return ms.ToArray();
		}
		finally
		{
			await ms.DisposeAsync().ConfigureAwait(false);
		}
	}
}

#endregion

#region 13. MathHelpers.GetPrecision(decimal)

/// <summary>MathHelpers.GetPrecision(decimal): decimal.GetBits (new int[4] per call) vs decimal.Scale (net7+).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcGetPrecisionBenchmarks
{
	private readonly decimal[] values = PcData.CreateDecimals(1024);

	[Benchmark(Baseline = true)]
	public int Original_GetBits()
	{
		int total = 0;
		foreach (decimal value in values)
		{
			total += PcPrecisionImpl.Original(value);
		}
		return total;
	}

	[Benchmark]
	public int Net11_Scale()
	{
		int total = 0;
		foreach (decimal value in values)
		{
			total += PcPrecisionImpl.Net11(value);
		}
		return total;
	}
}

public static class PcPrecisionImpl
{
	// Verbatim copy of MathHelpers.GetPrecision(decimal)
	public static int Original(decimal value)
	{
		int[] bits = decimal.GetBits(value);
		return (bits[3] >> 16) & 0xFF;
	}

	public static int Net11(decimal value)
	{
		return value.Scale;
	}
}

#endregion

#region 14. MathHelpers.GetMedian(IEnumerable<int>)

/// <summary>MathHelpers.GetMedian(int/long): OrderBy(x => x).ToArray() (key array + index map + delegate compares) vs ToArray + Array.Sort.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcGetMedianBenchmarks
{
	private readonly int[] numbers = PcData.CreateInts(1001, 11);

	[Benchmark(Baseline = true)]
	public int Original_OrderBy()
	{
		return PcMedianImpl.Original(numbers);
	}

	[Benchmark]
	public int Shared_ArraySort()
	{
		return PcMedianImpl.Optimized(numbers);
	}
}

public static class PcMedianImpl
{
	private const string NumbersParameterNullOrEmptyError = "numbers parameter cannot be null or empty.";

	// Verbatim copy of MathHelpers.GetMedian(IEnumerable<int>)
	public static int Original(IEnumerable<int> numbers)
	{
		if (numbers?.Any() != true)
		{
			throw new ArgumentException(NumbersParameterNullOrEmptyError);
		}

		int[] sorted = numbers.OrderBy(static x => x).ToArray();

		int mid = sorted.Length / 2;
		return sorted.Length % 2 != 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
	}

	public static int Optimized(IEnumerable<int> numbers)
	{
		if (numbers?.Any() != true)
		{
			throw new ArgumentException(NumbersParameterNullOrEmptyError);
		}

		// ToArray always copies, so the caller's collection is never reordered; equal ints are indistinguishable so stability is irrelevant
		int[] sorted = numbers.ToArray();
		Array.Sort(sorted);

		int mid = sorted.Length / 2;
		return sorted.Length % 2 != 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
	}
}

#endregion

#region 15. Collections.AddRange(HashSet) via SelectNonNull

/// <summary>Collections.AddRange(HashSet/ConcurrentBag): SelectNonNull (Where+Select iterator + 2 delegate calls per item) vs inline null check.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PcHashSetAddRangeBenchmarks
{
	private readonly List<string?> items = PcData.CreateNullableStrings(1000);

	[Benchmark(Baseline = true)]
	public int Original_SelectNonNull()
	{
		HashSet<string> set = [];
		PcAddRangeImpl.Original(set, items);
		return set.Count;
	}

	[Benchmark]
	public int Shared_InlineNullCheck()
	{
		HashSet<string> set = [];
		PcAddRangeImpl.Optimized(set, items);
		return set.Count;
	}
}

public static class PcAddRangeImpl
{
	// Verbatim copy of Collections.SelectNonNull
	private static IEnumerable<T>? SelectNonNull<T>(IEnumerable<T?>? items)
	{
		return items?.Where(x => x != null!).Select(x => x!);
	}

	// Verbatim copy of Collections.AddRange(HashSet<T>, IEnumerable<T?>, CancellationToken)
	public static void Original<T>(HashSet<T> hashSet, IEnumerable<T?> toAdd, CancellationToken cancellationToken = default)
	{
		foreach (T? item in SelectNonNull(toAdd)!)
		{
			cancellationToken.ThrowIfCancellationRequested();
			hashSet.Add(item);
		}
	}

	public static void Optimized<T>(HashSet<T> hashSet, IEnumerable<T?> toAdd, CancellationToken cancellationToken = default)
	{
		foreach (T? item in toAdd)
		{
			if (item == null)
			{
				continue;
			}

			cancellationToken.ThrowIfCancellationRequested();
			hashSet.Add(item);
		}
	}
}

#endregion

#region Shared test data

public static class PcData
{
	public static SimpleClass CreateSimple(int id)
	{
		return new SimpleClass { Id = id, Name = $"Name_{id}", Value = id * 1.5, IsActive = (id % 2) == 0, CreatedDate = new DateTime(2026, 1, 1).AddDays(id) };
	}

	public static NestedClass CreateNested()
	{
		return new NestedClass
		{
			Level = 1,
			Id = 10,
			Name = "Parent",
			Child = CreateSimple(2),
			Child2 = null,
			Children = [CreateSimple(3), CreateSimple(4), CreateSimple(5)]
		};
	}

	public static PcHashOrder CreateHashOrder()
	{
		return new PcHashOrder
		{
			Id = 42,
			Name = "Order 42",
			Price = 1234.56m,
			IsActive = true,
			Numbers = [5, 3, 8, 1, 9, 2, 7, 4, 6, 0],
			Tags = ["red", "green", "blue", "cyan", "magenta"],
			Counts = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2, ["c"] = 3 },
			Child = new PcHashChild { Value = 7, Label = "child" }
		};
	}

	public static List<DateTime> CreateHolidays()
	{
		return
		[
			new DateTime(2020, 1, 1), new DateTime(2020, 7, 4), new DateTime(2020, 12, 25), new DateTime(2021, 5, 31), new DateTime(2021, 11, 25),
			new DateTime(2022, 9, 5), new DateTime(2023, 1, 16), new DateTime(2024, 7, 4), new DateTime(2025, 12, 25), new DateTime(2026, 1, 3)
		];
	}

	public static List<List<string?>> CreateCombinationSources()
	{
		return
		[
			["A", "B", "C", "D", "E", "F"],
			["Small", "Medium", "Large", "XL", "XXL", "XXXL"],
			["Red", "Green", "Blue", "Black", "White", "Grey"],
			["2024", "2025", "2026", "2027", "2028", "2029"]
		];
	}

	public static decimal[] CreateDecimals(int count)
	{
		System.Random random = new(5);
		decimal[] values = new decimal[count];
		for (int i = 0; i < count; i++)
		{
			values[i] = new decimal(random.Next(), random.Next(), 0, (i % 3) == 0, (byte)random.Next(0, 29));
		}
		return values;
	}

	public static int[] CreateInts(int count, int seed)
	{
		System.Random random = new(seed);
		int[] values = new int[count];
		for (int i = 0; i < count; i++)
		{
			values[i] = random.Next(-1_000_000, 1_000_000);
		}
		return values;
	}

	public static List<string?> CreateNullableStrings(int count)
	{
		List<string?> values = new(count);
		for (int i = 0; i < count; i++)
		{
			values.Add((i % 10) == 0 ? null : $"Item_{i % 500}");
		}
		return values;
	}
}

public sealed class PcNonSeekableStream(Stream inner) : Stream
{
	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => throw new NotSupportedException();

	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override void Flush()
	{
	}

	public override int Read(byte[] buffer, int offset, int count)
	{
		return inner.Read(buffer, offset, count);
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}
}

#endregion

#region Verification

/// <summary>Asserts that every Original/Optimized pair in this file produces identical results on edge-case inputs.</summary>
public static class PcVerify
{
	public static void Run()
	{
		VerifyLru();
		VerifyCopyCacheLookup();
		VerifyFifo();
		VerifyIsEqual();
		VerifyGetHashForObject();
		VerifyBusinessDays();
		VerifyIndexOf();
		VerifyGetCombinations();
		VerifyGetRandomCombinations();
		VerifyShuffle();
		VerifyCountingStream();
		VerifyReadStreamAsync();
		VerifyGetPrecision();
		VerifyGetMedian();
		VerifyAddRange();
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException($"PcVerify mismatch: {message}");
		}
	}

	private static Type? CaptureException(Action action)
	{
		try
		{
			action();
			return null;
		}
		catch (Exception ex)
		{
			return ex.GetType();
		}
	}

	private static void AssertSameException(Action original, Action optimized, string name)
	{
		Type? originalException = CaptureException(original);
		Type? optimizedException = CaptureException(optimized);
		Assert(originalException == optimizedException, $"{name}: exception {originalException?.Name ?? "none"} vs {optimizedException?.Name ?? "none"}");
	}

	private static bool SameSnapshot<TKey, TValue>(List<KeyValuePair<TKey, TValue?>> a, List<KeyValuePair<TKey, TValue?>> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}

		for (int i = 0; i < a.Count; i++)
		{
			if (!EqualityComparer<TKey>.Default.Equals(a[i].Key, b[i].Key) || !EqualityComparer<TValue?>.Default.Equals(a[i].Value, b[i].Value))
			{
				return false;
			}
		}
		return true;
	}

	private static void VerifyLru()
	{
		foreach (int capacity in new[] { 1, 2, 5 })
		{
			PcLruOriginal<int, string> original = new(capacity);
			PcLruIndexed<int, string> indexed = new(capacity);
			PcLruLinked<int, string> linked = new(capacity);
			System.Random random = new(capacity);
			int factoryCalls = 0;
			string Factory(int key)
			{
				factoryCalls++;
				return $"{key}_{factoryCalls}";
			}

			for (int i = 0; i < 2000; i++)
			{
				int key = random.Next(capacity * 2);
				// Each implementation gets its own factory invocation so values only match if hit/miss decisions match
				int callsBefore = factoryCalls;
				string a = original.GetOrAdd(key, Factory);
				factoryCalls = callsBefore;
				string b = indexed.GetOrAdd(key, Factory);
				factoryCalls = callsBefore;
				string c = linked.GetOrAdd(key, Factory);
				Assert(a == b && a == c, $"LRU GetOrAdd value (capacity {capacity}, step {i})");
				Assert(SameSnapshot(original.Snapshot(), indexed.Snapshot()) && SameSnapshot(original.Snapshot(), linked.Snapshot()), $"LRU order (capacity {capacity}, step {i})");
			}
		}
	}

	private static void VerifyCopyCacheLookup()
	{
		foreach (bool useLimited in new[] { true, false })
		{
			PcCacheManagerModel<(Type Source, Type Dest), Func<object, object?, int, int, object?>> originalManager = new(2, useLimited);
			PcCacheManagerModel<(Type Source, Type Dest), Func<object, object?, int, int, object?>> optimizedManager = new(2, useLimited);
			(Type, Type)[] keys = [(typeof(int), typeof(int)), (typeof(string), typeof(string)), (typeof(SimpleClass), typeof(SimpleClassDto)), (typeof(int), typeof(int))];
			Func<object, object?, int, int, object?> preset = static (_, dest, _, _) => dest;
			originalManager.TryAddCache((typeof(long), typeof(long)), preset);
			optimizedManager.TryAddCache((typeof(long), typeof(long)), preset);
			originalManager.TryAddLimitedCache((typeof(long), typeof(long)), preset);
			optimizedManager.TryAddLimitedCache((typeof(long), typeof(long)), preset);

			Assert(ReferenceEquals(PcCopyCacheImpl.OriginalGetOrAdd(originalManager, (typeof(long), typeof(long))), PcCopyCacheImpl.OptimizedGetOrAdd(optimizedManager, (typeof(long), typeof(long)))), "Copy cache preset hit");
			foreach ((Type, Type) key in keys)
			{
				Func<object, object?, int, int, object?> a = PcCopyCacheImpl.OriginalGetOrAdd(originalManager, key);
				Func<object, object?, int, int, object?> b = PcCopyCacheImpl.OptimizedGetOrAdd(optimizedManager, key);
				Assert(ReferenceEquals(a, b), $"Copy cache lookup (limited {useLimited})");
			}

			Assert(originalManager.GetCache().Count == optimizedManager.GetCache().Count && originalManager.GetLimitedCache().Count == optimizedManager.GetLimitedCache().Count, $"Copy cache contents (limited {useLimited})");
		}
	}

	private static void VerifyFifo()
	{
		foreach (int capacity in new[] { 1, 3, 5 })
		{
			PcFifoOriginal<int, string> original = new(capacity);
			PcFifoReadLock<int, string> readLock = new(capacity);
			System.Random random = new(capacity);
			for (int i = 0; i < 2000; i++)
			{
				int key = random.Next(capacity * 2);
				int step = i;
				string a = original.GetOrAdd(key, k => $"{k}_{step}");
				string b = readLock.GetOrAdd(key, k => $"{k}_{step}");
				Assert(a == b, $"FIFO GetOrAdd value (capacity {capacity}, step {i})");
				Assert(SameSnapshot(original.Snapshot(), readLock.Snapshot()), $"FIFO order (capacity {capacity}, step {i})");
			}
		}

		PcFifoOriginal<int, string> threadedOriginal = new(32);
		PcFifoReadLock<int, string> threadedReadLock = new(32);
		int originalFactoryCalls = 0;
		int optimizedFactoryCalls = 0;
		Parallel.For(0, 8, _ =>
		{
			for (int i = 0; i < 5000; i++)
			{
				threadedOriginal.GetOrAdd(i & 15, k => { Interlocked.Increment(ref originalFactoryCalls); return k.ToString(CultureInfo.InvariantCulture); });
				threadedReadLock.GetOrAdd(i & 15, k => { Interlocked.Increment(ref optimizedFactoryCalls); return k.ToString(CultureInfo.InvariantCulture); });
			}
		});
		Assert(originalFactoryCalls == 16 && optimizedFactoryCalls == 16, "FIFO factory runs once per key under contention");
	}

	private static void VerifyIsEqual()
	{
		SimpleClass a = PcData.CreateSimple(1);
		SimpleClass b = PcData.CreateSimple(1);
		SimpleClass differentName = PcData.CreateSimple(1);
		differentName.Name = "Other";
		SimpleClass differentCase = PcData.CreateSimple(1);
		differentCase.Name = a.Name!.ToUpperInvariant();
		SimpleClass differentDate = PcData.CreateSimple(1);
		differentDate.CreatedDate = differentDate.CreatedDate.AddTicks(1);

		NestedClass nestedA = PcData.CreateNested();
		NestedClass nestedB = PcData.CreateNested();
		NestedClass nestedDifferentChild = PcData.CreateNested();
		nestedDifferentChild.Child!.Value = -1;
		NestedClass cyclicA = PcData.CreateNested();
		cyclicA.Child2 = cyclicA;
		NestedClass cyclicB = PcData.CreateNested();
		cyclicB.Child2 = cyclicB;

		(object? Left, object? Right)[] pairs =
		[
			(a, b), (a, differentName), (a, differentCase), (a, differentDate), (null, null), (a, null), (null, a), (a, PcData.CreateNested()),
			(nestedA, nestedB), (nestedA, nestedDifferentChild), (cyclicA, cyclicB), (cyclicA, nestedA), (a, a), ("x", "x"), ("x", "y"), (1, 1), (1, 2)
		];

		IEnumerable<string>?[] exemptions = [null, [], [nameof(SimpleClass.Name)], [nameof(NestedClass.Child)]];
		foreach ((object? left, object? right) in pairs)
		{
			foreach (IEnumerable<string>? exempt in exemptions)
			{
				foreach (bool ignoreCase in new[] { false, true })
				{
					foreach (bool recursive in new[] { true, false })
					{
						Type? originalException = CaptureException(() => PcIsEqualImpl.OriginalIsEqual(left, right, exempt, ignoreCase, recursive));
						Type? optimizedException = CaptureException(() => PcIsEqualImpl.OptimizedIsEqual(left, right, exempt, ignoreCase, recursive));
						Assert(originalException == optimizedException, $"IsEqual exception ({left?.GetType().Name} vs {right?.GetType().Name})");
						if (originalException == null)
						{
							bool original = PcIsEqualImpl.OriginalIsEqual(left, right, exempt, ignoreCase, recursive);
							bool optimized = PcIsEqualImpl.OptimizedIsEqual(left, right, exempt, ignoreCase, recursive);
							Assert(original == optimized, $"IsEqual ({left?.GetType().Name} vs {right?.GetType().Name}, ignoreCase {ignoreCase}, recursive {recursive})");
						}
					}
				}
			}
		}
	}

	private static void VerifyGetHashForObject()
	{
		PcHashOrder full = PcData.CreateHashOrder();
		PcHashOrder sparse = new() { Id = 1, Name = null, Numbers = [], Tags = null, Child = null };
		PcHashOrder reordered = PcData.CreateHashOrder();
		reordered.Numbers!.Reverse();
		foreach (EHashAlgorithm algorithm in Enum.GetValues<EHashAlgorithm>())
		{
			foreach (PcHashOrder order in new[] { full, sparse, reordered })
			{
				string original = PcHashImpl.Original(order, algorithm);
				Assert(original == PcHashImpl.Shared(order, algorithm), $"GetHashForObject Shared ({algorithm})");
				Assert(original == PcHashImpl.Net11(order, algorithm), $"GetHashForObject Net11 ({algorithm})");
			}

			PcHashChild child = new() { Value = -3, Label = "x" };
			Assert(PcHashImpl.Original(child, algorithm) == PcHashImpl.Net11(child, algorithm), $"GetHashForObject child ({algorithm})");
		}

		Assert(PcHashImpl.Original((PcHashOrder?)null) == PcHashImpl.Shared((PcHashOrder?)null) && PcHashImpl.Original((PcHashOrder?)null) == PcHashImpl.Net11((PcHashOrder?)null), "GetHashForObject null");
	}

	private static void VerifyBusinessDays()
	{
		List<DateTime> holidays = PcData.CreateHolidays();
		holidays.Add(new DateTime(2021, 1, 2));
		holidays.Add(new DateTime(2021, 1, 3));
		holidays.Add(new DateTime(1999, 12, 31));
		List<DateOnly> dateOnlyHolidays = holidays.ConvertAll(DateOnly.FromDateTime);

		System.Random random = new(123);
		for (int i = 0; i < 20_000; i++)
		{
			DateTime start = new DateTime(2000, 1, 1).AddDays(random.Next(0, 12_000));
			DateTime end = start.AddDays(random.Next(-400, 800));
			if ((i % 3) == 0)
			{
				end = end.AddHours(random.Next(1, 23));
			}
			if ((i % 5) == 0)
			{
				start = start.AddMinutes(37);
				end = end.AddMinutes(37);
			}

			List<DateTime>? exceptions = (i % 4) switch { 0 => null, 1 => [], _ => holidays };
			List<DateOnly>? dateOnlyExceptions = (i % 4) switch { 0 => null, 1 => [], _ => dateOnlyHolidays };
			Assert(PcBusinessDaysImpl.OriginalDateTime(start, end, exceptions) == PcBusinessDaysImpl.OptimizedDateTime(start, end, exceptions), $"GetBusinessDays DateTime {start:O} -> {end:O}");

			DateOnly startDate = DateOnly.FromDateTime(start);
			DateOnly endDate = DateOnly.FromDateTime(end);
			Assert(PcBusinessDaysImpl.OriginalDateOnly(startDate, endDate, dateOnlyExceptions) == PcBusinessDaysImpl.OptimizedDateOnly(startDate, endDate, dateOnlyExceptions), $"GetBusinessDays DateOnly {startDate} -> {endDate}");
		}

		DateTime[] extremes = [DateTime.MinValue, DateTime.MaxValue, DateTime.MaxValue.Date, new DateTime(2026, 11, 14), new DateTime(2026, 11, 15)];
		foreach (DateTime start in extremes)
		{
			foreach (DateTime end in extremes)
			{
				Assert(PcBusinessDaysImpl.OriginalDateTime(start, end, holidays) == PcBusinessDaysImpl.OptimizedDateTime(start, end, holidays), $"GetBusinessDays DateTime extremes {start:O} -> {end:O}");
				Assert(PcBusinessDaysImpl.OriginalDateOnly(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), dateOnlyHolidays) == PcBusinessDaysImpl.OptimizedDateOnly(DateOnly.FromDateTime(start), DateOnly.FromDateTime(end), dateOnlyHolidays), "GetBusinessDays DateOnly extremes");
			}
		}

		Assert(PcBusinessDaysImpl.OriginalDateTime(null, DateTime.Today) == PcBusinessDaysImpl.OptimizedDateTime(null, DateTime.Today), "GetBusinessDays DateTime null start");
		Assert(PcBusinessDaysImpl.OriginalDateTime(DateTime.Today, null) == PcBusinessDaysImpl.OptimizedDateTime(DateTime.Today, null), "GetBusinessDays DateTime null end");
		Assert(PcBusinessDaysImpl.OriginalDateOnly(null, null) == PcBusinessDaysImpl.OptimizedDateOnly(null, null), "GetBusinessDays DateOnly null");
	}

	private static void VerifyIndexOf()
	{
		List<string?> list = ["a", "b", null, "B", "c", "b"];
		string?[] array = [.. list];
		HashSet<string?> set = ["a", "b", null, "c"];
		IEnumerable<string?> lazy = list.Where(static _ => true);
		IEnumerable<object> covariant = new[] { "x", "y", "z" };
		string?[] values = ["a", "b", "B", null, "c", "missing"];
		IEqualityComparer<string?>?[] comparers = [null, StringComparer.OrdinalIgnoreCase];

		foreach (IEnumerable<string?> source in new IEnumerable<string?>[] { list, array, set, lazy, [] })
		{
			foreach (string? value in values)
			{
				foreach (IEqualityComparer<string?>? comparer in comparers)
				{
					Assert(PcIndexOfImpl.Original(source, value, comparer) == PcIndexOfImpl.Optimized(source, value, comparer), $"IndexOf {source.GetType().Name} '{value}'");
				}
			}
		}

		Assert(PcIndexOfImpl.Original(covariant, "z", null) == PcIndexOfImpl.Optimized(covariant, "z", null), "IndexOf covariant array");
		Assert(PcIndexOfImpl.Original(Enumerable.Range(0, 100), 99, null) == PcIndexOfImpl.Optimized(Enumerable.Range(0, 100), 99, null), "IndexOf range");
		AssertSameException(() => PcIndexOfImpl.Original<string>(null!, "a", null), () => PcIndexOfImpl.Optimized<string>(null!, "a", null), "IndexOf null source");
	}

	private static void VerifyGetCombinations()
	{
		List<List<List<string?>>> sourceSets =
		[
			PcData.CreateCombinationSources(),
			[["a", "b", "a", null], ["1", null, "2"], []],
			[["only"]],
			[[null]],
			[["x", "y"], ["x", "y"]],
			[]
		];
		int?[] maxima = [null, 1, 2, 5, 36, 1296, 100_000];
		string?[] separators = ["|", string.Empty, null, ", "];
		string?[] nullReplacements = [null, "N/A"];

		foreach (List<List<string?>> sources in sourceSets)
		{
			foreach (int? max in maxima)
			{
				foreach (string? separator in separators)
				{
					foreach (string? nullReplacement in nullReplacements)
					{
						HashSet<string> original = PcCombinationsImpl.OriginalGetCombinations(sources, max, separator!, nullReplacement);
						HashSet<string> optimized = PcCombinationsImpl.OptimizedGetCombinations(sources, max, separator!, nullReplacement);
						Assert(original.SequenceEqual(optimized), $"GetCombinations (max {max}, separator '{separator}', nullReplacement '{nullReplacement}')");
					}
				}
			}
		}

		AssertSameException(() => PcCombinationsImpl.OriginalGetCombinations(PcData.CreateCombinationSources(), 0), () => PcCombinationsImpl.OptimizedGetCombinations(PcData.CreateCombinationSources(), 0), "GetCombinations max 0");
	}

	private static void VerifyGetRandomCombinations()
	{
		List<List<string?>> sources = PcData.CreateCombinationSources();
		HashSet<string> all = PcCombinationsImpl.OriginalGetCombinations(sources);
		foreach (int? max in new int?[] { null, 1, 10, all.Count - 1, all.Count, all.Count + 5 })
		{
			HashSet<string> original = PcCombinationsImpl.OriginalGetRandomCombinations(sources, max);
			HashSet<string> optimized = PcCombinationsImpl.OptimizedGetRandomCombinations(sources, max);
			Assert(original.Count == optimized.Count, $"GetRandomCombinations count (max {max})");
			Assert(original.IsSubsetOf(all) && optimized.IsSubsetOf(all), $"GetRandomCombinations subset (max {max})");
		}

		AssertSameException(() => PcCombinationsImpl.OriginalGetRandomCombinations(sources, 0), () => PcCombinationsImpl.OptimizedGetRandomCombinations(sources, 0), "GetRandomCombinations max 0");
	}

	private static void VerifyShuffle()
	{
		foreach (int size in new[] { 0, 1, 2, 3, 63, 64, 65, 1000 })
		{
			List<int> original = Enumerable.Range(0, size).ToList();
			List<int> optimized = Enumerable.Range(0, size).ToList();
			Assert(ReferenceEquals(PcShuffleImpl.Original(original), original) && ReferenceEquals(PcShuffleImpl.Optimized(optimized), optimized), $"Shuffle returns same instance ({size})");
			Assert(original.Order().SequenceEqual(Enumerable.Range(0, size)) && optimized.Order().SequenceEqual(Enumerable.Range(0, size)), $"Shuffle is a permutation ({size})");
		}

		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();
		AssertSameException(() => PcShuffleImpl.Original(new List<int> { 1, 2, 3 }, cancelled.Token), () => PcShuffleImpl.Optimized(new List<int> { 1, 2, 3 }, cancelled.Token), "Shuffle cancelled");
		AssertSameException(() => PcShuffleImpl.Original(new List<int> { 1 }, cancelled.Token), () => PcShuffleImpl.Optimized(new List<int> { 1 }, cancelled.Token), "Shuffle cancelled single item");
	}

	private static void VerifyCountingStream()
	{
		byte[] data = new byte[300];
		new System.Random(9).NextBytes(data);

		using MemoryStream originalInner = new();
		using MemoryStream optimizedInner = new();
		PcOriginalCountingStream original = new(originalInner);
		PcOptimizedCountingStream optimized = new(optimizedInner);

		original.Write(data, 0, 100);
		optimized.Write(data, 0, 100);
		original.Write(data.AsSpan(100, 50));
		optimized.Write(data.AsSpan(100, 50));
		original.Write(data, 150, 0);
		optimized.Write(data, 150, 0);
		original.Write(data, 150, 150);
		optimized.Write(data, 150, 150);
		original.Flush();
		optimized.Flush();
		Assert(original.BytesWritten == optimized.BytesWritten && original.BytesWritten == 300, "CountingStream BytesWritten");
		Assert(originalInner.ToArray().SequenceEqual(optimizedInner.ToArray()), "CountingStream written bytes");

		original.Position = 0;
		optimized.Position = 0;
		byte[] originalRead = new byte[128];
		byte[] optimizedRead = new byte[128];
		int originalCount = original.Read(originalRead, 3, 120);
		int optimizedCount = optimized.Read(optimizedRead, 3, 120);
		Assert(originalCount == optimizedCount && originalRead.SequenceEqual(optimizedRead), "CountingStream Read");

		AssertSameException(() => original.Write(data, 0, 400), () => optimized.Write(data, 0, 400), "CountingStream Write out of range");
		Assert(original.BytesWritten == optimized.BytesWritten, "CountingStream BytesWritten after failed write");

		original.Dispose();
		optimized.Dispose();
		AssertSameException(() => original.Write(data, 0, 1), () => optimized.Write(data, 0, 1), "CountingStream Write after dispose");
#pragma warning disable CA2022 // Only the exception thrown after dispose is compared; the byte count is irrelevant
		AssertSameException(() => original.Read(originalRead, 0, 1), () => optimized.Read(optimizedRead, 0, 1), "CountingStream Read after dispose");
#pragma warning restore CA2022
		AssertSameException(() => original.Flush(), () => optimized.Flush(), "CountingStream Flush after dispose");

		using MemoryStream readOnlyInner = new(data, writable: false);
		using PcOriginalCountingStream originalReadOnly = new(readOnlyInner);
		using PcOptimizedCountingStream optimizedReadOnly = new(readOnlyInner);
		AssertSameException(() => originalReadOnly.Write(data, 0, 1), () => optimizedReadOnly.Write(data, 0, 1), "CountingStream Write to read-only inner");
	}

	private static void VerifyReadStreamAsync()
	{
		byte[] payload = new byte[10_000];
		new System.Random(1).NextBytes(payload);

		Func<Stream>[] factories =
		[
			() => new MemoryStream(payload, writable: false),
			() => new MemoryStream(payload, writable: false) { Position = 1234 },
			() => new MemoryStream(payload, writable: false) { Position = payload.Length },
			() => new PcNonSeekableStream(new MemoryStream(payload, writable: false)),
			() => new MemoryStream(),
			() => new PcNonSeekableStream(new MemoryStream())
		];

		foreach (Func<Stream> factory in factories)
		{
			foreach (int bufferSize in new[] { 1, 4096 })
			{
				using Stream originalSource = factory();
				using Stream optimizedSource = factory();
				byte[] original = PcReadStreamImpl.OriginalReadStreamAsync(originalSource, bufferSize).AsTask().GetAwaiter().GetResult();
				byte[] optimized = PcReadStreamImpl.OptimizedReadStreamAsync(optimizedSource, bufferSize).AsTask().GetAwaiter().GetResult();
				Assert(original.SequenceEqual(optimized), $"ReadStreamAsync content ({originalSource.GetType().Name}, buffer {bufferSize})");
			}
		}

		AssertSameException(() => PcReadStreamImpl.OriginalReadStreamAsync(new MemoryStream(payload), 0).AsTask().GetAwaiter().GetResult(), () => PcReadStreamImpl.OptimizedReadStreamAsync(new MemoryStream(payload), 0).AsTask().GetAwaiter().GetResult(), "ReadStreamAsync buffer 0");
	}

	private static void VerifyGetPrecision()
	{
		decimal[] values = [0m, 1m, 1.0m, 1.000m, -0.5m, 123.456m, decimal.MaxValue, decimal.MinValue, 0.0000000000000000000000000001m, -0.00m, 1e-28m * 3, 79228162514264337593543950335m / 10m];
		foreach (decimal value in values.Concat(PcData.CreateDecimals(5000)))
		{
			Assert(PcPrecisionImpl.Original(value) == PcPrecisionImpl.Net11(value), $"GetPrecision {value}");
		}
	}

	private static void VerifyGetMedian()
	{
		int[][] inputs =
		[
			[1], [1, 2], [3, 1, 2], [-5, 10, 10, 2], [int.MaxValue, int.MaxValue - 2], [int.MinValue, -1, 0],
			PcData.CreateInts(1001, 1), PcData.CreateInts(1000, 2)
		];

		foreach (int[] input in inputs)
		{
			int[] copy = [.. input];
			Assert(PcMedianImpl.Original(input) == PcMedianImpl.Optimized(input), $"GetMedian ({input.Length} items)");
			Assert(copy.SequenceEqual(input), "GetMedian must not reorder the input");
			Assert(PcMedianImpl.Original(input.Select(static x => x)) == PcMedianImpl.Optimized(input.Select(static x => x)), "GetMedian lazy source");
		}

		AssertSameException(() => PcMedianImpl.Original([]), () => PcMedianImpl.Optimized([]), "GetMedian empty");
		AssertSameException(() => PcMedianImpl.Original(null!), () => PcMedianImpl.Optimized(null!), "GetMedian null");
	}

	private static void VerifyAddRange()
	{
		List<string?> items = PcData.CreateNullableStrings(1000);
		HashSet<string> original = ["preexisting", "Item_1"];
		HashSet<string> optimized = ["preexisting", "Item_1"];
		PcAddRangeImpl.Original(original, items);
		PcAddRangeImpl.Optimized(optimized, items);
		Assert(original.SequenceEqual(optimized), "AddRange contents and insertion order");

		HashSet<string> originalEmpty = [];
		HashSet<string> optimizedEmpty = [];
		PcAddRangeImpl.Original(originalEmpty, new string?[] { null, null });
		PcAddRangeImpl.Optimized(optimizedEmpty, new string?[] { null, null });
		Assert(originalEmpty.Count == 0 && optimizedEmpty.Count == 0, "AddRange all nulls");

		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();
		AssertSameException(() => PcAddRangeImpl.Original(new HashSet<string>(), items, cancelled.Token), () => PcAddRangeImpl.Optimized(new HashSet<string>(), items, cancelled.Token), "AddRange cancelled");
		AssertSameException(() => PcAddRangeImpl.Original(new HashSet<string>(), new string?[] { null }, cancelled.Token), () => PcAddRangeImpl.Optimized(new HashSet<string>(), new string?[] { null }, cancelled.Token), "AddRange cancelled with only nulls");
		AssertSameException(() => PcAddRangeImpl.Original(new HashSet<string>(), null!), () => PcAddRangeImpl.Optimized(new HashSet<string>(), null!), "AddRange null source");
	}
}

#endregion
