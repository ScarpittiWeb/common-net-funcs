namespace CommonNetFuncs.Core.CollectionClasses;

/// <summary>
/// Fixed size dictionary that maintains insertion order and evicts the oldest item when capacity is exceeded.
/// </summary>
/// <remarks>This dictionary enforces a maximum capacity. When the capacity is exceeded, the oldest item is automatically removed to make room for new entries.
/// This implementation is thread-safe and uses a <see cref="ReaderWriterLockSlim"/> to synchronize access.
/// Uses <see cref="System.Collections.Generic.OrderedDictionary{TKey, TValue}"/> on net9.0+ (the fastest option available there), falling back to a
/// <see cref="Dictionary{TKey, TValue}"/> + <see cref="LinkedList{T}"/> implementation on older target frameworks where OrderedDictionary doesn't exist.</remarks>
/// <typeparam name="TKey">The type of the keys in the dictionary. Keys must be non-null.</typeparam>
/// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
public class FixedFifoDictionary<TKey, TValue> : FixedSizeDictionaryBase<TKey, TValue> where TKey : notnull
{
	/// <summary>
	/// Initializes a new instance of the <see cref="FixedFifoDictionary{TKey,TValue}"/> class with the specified capacity and an optional source dictionary.
	/// </summary>
	/// <param name="capacity">The maximum number of items the dictionary can hold.</param>
	/// <param name="sourceDictionary">Optional: A dictionary to initialize the contents of the new dictionary.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when the capacity is less than or equal to zero.</exception>
	/// <exception cref="ArgumentException">Thrown when the source dictionary exceeds the specified capacity.</exception>
	public FixedFifoDictionary(int capacity, IDictionary<TKey, TValue?>? sourceDictionary = null) : base(capacity, sourceDictionary)
	{
	}

	/// <inheritdoc />
	public override TValue? this[TKey key]
	{
		get
		{
			readWriteLock.EnterReadLock();
			try
			{
#if NET9_0_OR_GREATER
				return dictionary[key];
#else
				return lookup[key].Value.Value;
#endif
			}
			finally
			{
				readWriteLock.ExitReadLock();
			}
		}
		set => Add(key, value);
	}

	/// <inheritdoc />
	public override bool TryGetValue(TKey key, out TValue? value)
	{
		readWriteLock.EnterReadLock();
		try
		{
#if NET9_0_OR_GREATER
			return dictionary.TryGetValue(key, out value);
#else
			if (lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
			{
				value = node.Value.Value;
				return true;
			}
			value = default;
			return false;
#endif
		}
		finally
		{
			readWriteLock.ExitReadLock();
		}
	}

	/// <inheritdoc />
	public override void Add(TKey key, TValue? value)
	{
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			if (dictionary.ContainsKey(key))
			{
				// Update existing item - not changing its position in the queue
				dictionary[key] = value;
			}
			else
			{
				AddNewest(key, value);
			}
#else
			if (lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
			{
				// Update existing item - not changing its position in the queue
				node.Value = new(key, value);
			}
			else
			{
				AddNewest(key, value);
			}
#endif
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	/// <inheritdoc />
	public override void Add(KeyValuePair<TKey, TValue?> item)
	{
		Add(item.Key, item.Value);
	}

	/// <inheritdoc />
	public override TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory)
	{
		readWriteLock.EnterUpgradeableReadLock();
		try
		{
#if NET9_0_OR_GREATER
			if (dictionary.TryGetValue(key, out TValue? existing))
			{
				return existing!;
			}
#else
			if (lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
			{
				return node.Value.Value!;
			}
#endif

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
}
