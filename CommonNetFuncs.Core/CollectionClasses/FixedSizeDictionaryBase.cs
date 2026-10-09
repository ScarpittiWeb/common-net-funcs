using System.Collections;

namespace CommonNetFuncs.Core.CollectionClasses;

/// <summary>
/// Shared thread-safe storage and plumbing for fixed-capacity dictionaries that evict their oldest entry when full.
/// </summary>
/// <remarks>Uses <see cref="System.Collections.Generic.OrderedDictionary{TKey, TValue}"/> on net9.0+, falling back to a
/// <see cref="Dictionary{TKey, TValue}"/> + <see cref="LinkedList{T}"/> implementation on older target frameworks where OrderedDictionary doesn't exist.
/// Index 0 / <see cref="LinkedList{T}.First"/> is always the next entry to be evicted.</remarks>
/// <typeparam name="TKey">The type of the keys in the dictionary. Keys must be non-null.</typeparam>
/// <typeparam name="TValue">The type of the values in the dictionary.</typeparam>
public abstract class FixedSizeDictionaryBase<TKey, TValue> : IDictionary<TKey, TValue?> where TKey : notnull
{
	private protected readonly ReaderWriterLockSlim readWriteLock = new();
	private protected readonly int capacity;

#if NET9_0_OR_GREATER
	private protected readonly OrderedDictionary<TKey, TValue?> dictionary;
#else
	private protected readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue?>>> lookup;

	private protected readonly LinkedList<KeyValuePair<TKey, TValue?>> order = new();
#endif

	private protected FixedSizeDictionaryBase(int capacity, IDictionary<TKey, TValue?>? sourceDictionary)
	{
		if (capacity <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be greater than zero.");
		}

		if (sourceDictionary != null && sourceDictionary.Count > capacity)
		{
			throw new ArgumentException("Source dictionary exceeds the specified capacity.", nameof(sourceDictionary));
		}

		this.capacity = capacity;
#if NET9_0_OR_GREATER
		dictionary = new OrderedDictionary<TKey, TValue?>(capacity);
#else
		lookup = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue?>>>(capacity);
#endif

		if (sourceDictionary != null)
		{
			foreach (KeyValuePair<TKey, TValue?> kvp in sourceDictionary)
			{
				AddNewest(kvp.Key, kvp.Value);
			}
		}
	}

	/// <inheritdoc />
	public ICollection<TKey> Keys
	{
		get
		{
			readWriteLock.EnterReadLock();
			try
			{
#if NET9_0_OR_GREATER
				return dictionary.Keys;
#else
				return order.Select(static x => x.Key).ToList();
#endif
			}
			finally
			{
				readWriteLock.ExitReadLock();
			}
		}
	}

	/// <inheritdoc />
	public ICollection<TValue?> Values
	{
		get
		{
			readWriteLock.EnterReadLock();
			try
			{
#if NET9_0_OR_GREATER
				return dictionary.Values;
#else
				return order.Select(static x => x.Value).ToList();
#endif
			}
			finally
			{
				readWriteLock.ExitReadLock();
			}
		}
	}

	/// <inheritdoc />
	public int Count
	{
		get
		{
			readWriteLock.EnterReadLock();
			try
			{
#if NET9_0_OR_GREATER
				return dictionary.Count;
#else
				return lookup.Count;
#endif
			}
			finally
			{
				readWriteLock.ExitReadLock();
			}
		}
	}

	/// <inheritdoc />
	public bool IsReadOnly => false;

	/// <inheritdoc />
	public abstract TValue? this[TKey key] { get; set; }

	/// <inheritdoc />
	public abstract bool TryGetValue(TKey key, out TValue? value);

	/// <inheritdoc />
	public abstract void Add(TKey key, TValue? value);

	/// <inheritdoc />
	public abstract void Add(KeyValuePair<TKey, TValue?> item);

	/// <summary>
	/// Gets the value associated with the specified key, or adds a new key/value pair to the dictionary if the key does not exist.
	/// </summary>
	/// <param name="key">The key to locate in the dictionary.</param>
	/// <param name="valueFactory">A function to generate a value for the key if it does not exist.</param>
	/// <returns>The value associated with the specified key.</returns>
	public abstract TValue GetOrAdd(TKey key, Func<TKey, TValue> valueFactory);

	/// <inheritdoc />
	public bool ContainsKey(TKey key)
	{
		readWriteLock.EnterReadLock();
		try
		{
#if NET9_0_OR_GREATER
			return dictionary.ContainsKey(key);
#else
			return lookup.ContainsKey(key);
#endif
		}
		finally
		{
			readWriteLock.ExitReadLock();
		}
	}

	/// <inheritdoc />
	public void Clear()
	{
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			dictionary.Clear();
#else
			lookup.Clear();
			order.Clear();
#endif
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	public void TrimExcess()
	{
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			dictionary.TrimExcess();
#else
			lookup.TrimExcess();
#endif
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	/// <summary>
	/// Attempts to add the specified key and value to the dictionary.
	/// </summary>
	/// <param name="key">Key of the value to add.</param>
	/// <param name="value">Value to add.</param>
	/// <returns><see langword="true"/> if the key/value pair was added successfully, <see langword="false"/> otherwise.</returns>
	public bool TryAdd(TKey key, TValue? value)
	{
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			if (dictionary.ContainsKey(key))
			{
				return false;
			}
#else
			if (lookup.ContainsKey(key))
			{
				return false;
			}
#endif
			AddNewest(key, value);
			return true;
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	/// <inheritdoc />
	public bool Remove(TKey key)
	{
		readWriteLock.EnterWriteLock();
		try
		{
			return RemoveInternal(key);
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	/// <inheritdoc />
	public bool Contains(KeyValuePair<TKey, TValue?> item)
	{
		readWriteLock.EnterReadLock();
		try
		{
#if NET9_0_OR_GREATER
			return dictionary.TryGetValue(item.Key, out TValue? value) && EqualityComparer<TValue?>.Default.Equals(value, item.Value);
#else
			return lookup.TryGetValue(item.Key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node) && EqualityComparer<TValue?>.Default.Equals(node.Value.Value, item.Value);
#endif
		}
		finally
		{
			readWriteLock.ExitReadLock();
		}
	}

	/// <inheritdoc />
	public void CopyTo(KeyValuePair<TKey, TValue?>[] array, int arrayIndex)
	{
		readWriteLock.EnterReadLock();
		try
		{
#if NET9_0_OR_GREATER
			((ICollection<KeyValuePair<TKey, TValue?>>)dictionary).CopyTo(array, arrayIndex);
#else
			order.ToList().CopyTo(array, arrayIndex);
#endif
		}
		finally
		{
			readWriteLock.ExitReadLock();
		}
	}

	/// <inheritdoc />
	public bool Remove(KeyValuePair<TKey, TValue?> item)
	{
		readWriteLock.EnterWriteLock();
		try
		{
#if NET9_0_OR_GREATER
			return dictionary.TryGetValue(item.Key, out TValue? value) && EqualityComparer<TValue?>.Default.Equals(value, item.Value) && RemoveInternal(item.Key);
#else
			return lookup.TryGetValue(item.Key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node)
				&& EqualityComparer<TValue?>.Default.Equals(node.Value.Value, item.Value)
				&& RemoveInternal(item.Key);
#endif
		}
		finally
		{
			readWriteLock.ExitWriteLock();
		}
	}

	/// <inheritdoc />
	public IEnumerator<KeyValuePair<TKey, TValue?>> GetEnumerator()
	{
		readWriteLock.EnterReadLock();
		try
		{
#if NET9_0_OR_GREATER
			return dictionary.ToList().GetEnumerator();
#else
			return order.ToList().GetEnumerator();
#endif
		}
		finally
		{
			readWriteLock.ExitReadLock();
		}
	}

	/// <inheritdoc />
	IEnumerator IEnumerable.GetEnumerator()
	{
		return GetEnumerator();
	}

	// Callers must already hold the write lock.
	private protected void AddNewest(TKey key, TValue? value)
	{
#if NET9_0_OR_GREATER
		if (dictionary.Count >= capacity)
		{
			dictionary.RemoveAt(0);
		}
		dictionary.Add(key, value);
#else
		if (lookup.Count >= capacity)
		{
			RemoveInternal(order.First!.Value.Key);
		}

		LinkedListNode<KeyValuePair<TKey, TValue?>> node = order.AddLast(new KeyValuePair<TKey, TValue?>(key, value));
		lookup[key] = node;
#endif
	}

	// Callers must already hold the write lock.
	private protected bool RemoveInternal(TKey key)
	{
#if NET9_0_OR_GREATER
		return dictionary.Remove(key);
#else
		if (!lookup.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue?>>? node))
		{
			return false;
		}

		order.Remove(node);
		lookup.Remove(key);
		return true;
#endif
	}
}
