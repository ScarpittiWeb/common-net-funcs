namespace CommonNetFuncs.Core.CollectionClasses;

/// <summary>
/// Represents a fixed-capacity, thread-safe dictionary that maintains the most recently used (MRU) items. When the capacity is exceeded, the least recently used (LRU) item is removed.
/// </summary>
/// <remarks>This dictionary enforces a maximum capacity. When the capacity is exceeded, the least recently used
/// item is automatically removed to make room for new entries. Accessing or adding an item updates its usage order,
/// moving it to the most recently used position. This implementation is thread-safe and uses a <see cref="ReaderWriterLockSlim"/> to synchronize access.
/// Uses <see cref="System.Collections.Generic.OrderedDictionary{TKey, TValue}"/> on net9.0+ (the fastest option available there), falling back to a
/// <see cref="Dictionary{TKey, TValue}"/> + <see cref="LinkedList{T}"/> implementation on older target frameworks where OrderedDictionary doesn't exist.</remarks>
/// <typeparam name="TKey">The type of the keys in the dictionary. Keys must be non-null.</typeparam>
/// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
public class FixedLruDictionary<TKey, TValue> : FixedSizeDictionaryBase<TKey, TValue> where TKey : notnull
{
	/// <summary>
	/// Initializes a new instance of the <see cref="FixedLruDictionary{TKey, TValue}"/> class with the specified capacity and an optional source dictionary.
	/// </summary>
	/// <param name="capacity">The maximum number of items the dictionary can hold.</param>
	/// <param name="sourceDictionary">Optional: A dictionary to initialize the contents of the new dictionary.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when the capacity is less than or equal to zero.</exception>
	/// <exception cref="ArgumentException">Thrown when the source dictionary exceeds the specified capacity.</exception>
	public FixedLruDictionary(int capacity, IDictionary<TKey, TValue?>? sourceDictionary = null) : base(capacity, sourceDictionary)
	{
	}

	/// <inheritdoc />
	public override TValue? this[TKey key]
	{
		get
		{
			readWriteLock.EnterWriteLock();
			try
			{
#if NET9_0_OR_GREATER
				if (!dictionary.TryGetValue(key, out TValue? value))
				{
					throw new KeyNotFoundException();
				}
				MoveToMostRecentlyUsed(key, value);
				return value;
#else
				if (!lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
				{
					throw new KeyNotFoundException();
				}

				MoveToMostRecentlyUsed(node);
				return node.Value.Value;
#endif
			}
			finally
			{
				readWriteLock.ExitWriteLock();
			}
		}
		set
		{
			readWriteLock.EnterWriteLock();
			try
			{
				RemoveInternal(key);
				AddNewest(key, value);
			}
			finally
			{
				readWriteLock.ExitWriteLock();
			}
		}
	}

	/// <inheritdoc />
	public override bool TryGetValue(TKey key, out TValue? value)
	{
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			if (!dictionary.TryGetValue(key, out value))
			{
				return false;
			}
			MoveToMostRecentlyUsed(key, value);
			return true;
#else
			if (!lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
			{
				value = default;
				return false;
			}

			MoveToMostRecentlyUsed(node);
			value = node.Value.Value;
			return true;
#endif
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	/// <inheritdoc />
	public override void Add(TKey key, TValue? value)
	{
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			bool exists = dictionary.ContainsKey(key);
#else
			bool exists = lookup.ContainsKey(key);
#endif
			if (exists)
			{
				throw new ArgumentException("An item with the same key has already been added.", nameof(key));
			}

			AddNewest(key, value);
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
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			if (dictionary.TryGetValue(key, out TValue? existing))
			{
				MoveToMostRecentlyUsed(key, existing);
				return existing!;
			}
#else
			if (lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
			{
				MoveToMostRecentlyUsed(node);
				return node.Value.Value!;
			}
#endif

			TValue value = valueFactory(key);
			AddNewest(key, value);
			return value;
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

#if NET9_0_OR_GREATER
	// Callers must already hold the write lock.
	private void MoveToMostRecentlyUsed(TKey key, TValue? value)
	{
		if (dictionary.Count > 0 && !EqualityComparer<TKey>.Default.Equals(dictionary.GetAt(dictionary.Count - 1).Key, key))
		{
			dictionary.Remove(key);
			dictionary.Add(key, value);
		}
	}
#else
	// Callers must already hold the write lock.
	private void MoveToMostRecentlyUsed(LinkedListNode<KeyValuePair<TKey, TValue?>> node)
	{
		if (node != order.Last)
		{
			order.Remove(node);
			order.AddLast(node);
		}
	}
#endif
}
