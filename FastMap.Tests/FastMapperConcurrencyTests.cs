using System.Collections.Concurrent;
using CommonNetFuncs.FastMap;

namespace FastMap.Tests;

// Define a collection to ensure tests don't run in parallel with other test classes
[CollectionDefinition("FasterMapperConcurrency", DisableParallelization = true)]
public class FasterMapperConcurrencyCollection;

[Collection("FasterMapperConcurrency")] // Run these tests serially
public sealed class FasterMapperConcurrencyTests
{
	public sealed class SimpleSource
	{
		public required string StringProp { get; set; }
		public int IntProp { get; set; }
		public DateTime DateProp { get; set; }
	}

	public sealed class SimpleDestination
	{
		public required string StringProp { get; set; }
		public int IntProp { get; set; }
		public DateTime DateProp { get; set; }
	}

	public sealed class ComplexSource
	{
		public required string Name { get; set; }
		public required List<string> Items { get; set; }
	}

	public sealed class ComplexDestination
	{
		public required string Name { get; set; }
		public required List<string> Items { get; set; }
	}

	[Fact]
	public void ConcurrentMapping_MultipleMappings_NoExceptions()
	{
		// Arrange
		const int threadCount = 10;
		const int operationsPerThread = 1000;
		ConcurrentBag<Exception> exceptions = [];
		List<Thread> threads = [];

		// Act - Create multiple threads that perform mapping simultaneously
		for (int i = 0; i < threadCount; i++)
		{
			int threadId = i;
			Thread thread = new(() =>
			{
				try
				{
					for (int j = 0; j < operationsPerThread; j++)
					{
						SimpleSource source = new()
						{
							StringProp = $"Thread{threadId}-Op{j}",
							IntProp = (threadId * 1000) + j,
							DateProp = DateTime.Now
						};

						SimpleDestination result = source.FastMap<SimpleSource, SimpleDestination>();

						if (result.StringProp != source.StringProp || result.IntProp != source.IntProp)
						{
							throw new InvalidOperationException($"Mapping produced incorrect results on thread {threadId}");
						}
					}
				}
				catch (Exception ex)
				{
					exceptions.Add(ex);
				}
			});
			threads.Add(thread);
			thread.Start();
		}

		// Wait for all threads to complete
		foreach (Thread thread in threads)
		{
			thread.Join();
		}

		// Assert
		exceptions.ShouldBeEmpty();
	}

	private static void RunConcurrently(int threadCount, int operationsPerThread, Action<int, int> operation)
	{
		ConcurrentBag<Exception> exceptions = [];
		List<Thread> threads = [];

		for (int i = 0; i < threadCount; i++)
		{
			int threadId = i;
			Thread thread = new(() =>
			{
				try
				{
					for (int j = 0; j < operationsPerThread; j++)
					{
						operation(threadId, j);
					}
				}
				catch (Exception ex)
				{
					exceptions.Add(ex);
				}
			});
			threads.Add(thread);
			thread.Start();
		}

		foreach (Thread thread in threads)
		{
			thread.Join();
		}

		exceptions.ShouldBeEmpty();
	}

	[Fact]
	public void ConcurrentMapping_DifferentTypes_NoExceptions()
		=> RunConcurrently(8, 500, MapSimpleOrComplex);

	private static void MapSimpleOrComplex(int threadId, int j)
	{
		// Alternate between simple and complex mappings
		if ((threadId + j) % 2 == 0)
		{
			MapSimple(threadId, j);
		}
		else
		{
			MapComplex(threadId, j);
		}
	}

	private static void MapSimple(int threadId, int j)
	{
		SimpleSource source = new()
		{
			StringProp = $"Simple{threadId}-{j}",
			IntProp = j,
			DateProp = DateTime.Now
		};

		SimpleDestination result = source.FastMap<SimpleSource, SimpleDestination>();
		if (result.StringProp != source.StringProp)
		{
			throw new InvalidOperationException("Simple mapping failed");
		}
	}

	private static void MapComplex(int threadId, int j)
	{
		ComplexSource source = new()
		{
			Name = $"Complex{threadId}-{j}",
			Items = [$"item{j}"]
		};

		ComplexDestination result = source.FastMap<ComplexSource, ComplexDestination>();
		if (result.Name != source.Name || result.Items.Count != source.Items.Count)
		{
			throw new InvalidOperationException("Complex mapping failed");
		}
	}

	[Fact]
	public void ConcurrentMapping_ListMappings_NoExceptions()
		=> RunConcurrently(6, 200, MapList);

	private static void MapList(int threadId, int j)
	{
		List<SimpleSource> source =
		[
			new() { StringProp = $"T{threadId}-{j}-A", IntProp = j, DateProp = DateTime.Now },
			new() { StringProp = $"T{threadId}-{j}-B", IntProp = j + 1, DateProp = DateTime.Now.AddDays(1) },
			new() { StringProp = $"T{threadId}-{j}-C", IntProp = j + 2, DateProp = DateTime.Now.AddDays(2) }
		];

		List<SimpleDestination> result = source.FastMap<List<SimpleSource>, List<SimpleDestination>>();

		if (result.Count != 3)
		{
			throw new InvalidOperationException($"Expected 3 items, got {result.Count}");
		}

		for (int k = 0; k < source.Count; k++)
		{
			AssertSameValues(source[k], result[k], "List");
		}
	}

	private static void AssertSameValues(SimpleSource source, SimpleDestination result, string kind)
	{
		if (result.StringProp != source.StringProp || result.IntProp != source.IntProp)
		{
			throw new InvalidOperationException($"{kind} mapping produced incorrect results");
		}
	}

	[Fact]
	public async Task ConcurrentMapping_ParallelAsyncOperations_NoExceptions()
	{
		// Arrange
		const int parallelOperations = 100;
		ConcurrentBag<Exception> exceptions = [];

		// Act
		await Parallel.ForEachAsync(Enumerable.Range(0, parallelOperations), async (i, _) =>
		{
			try
			{
				await Task.Run(() =>
				{
					SimpleSource source = new()
					{
						StringProp = $"Async-{i}",
						IntProp = i,
						DateProp = DateTime.Now
					};

					SimpleDestination result = source.FastMap<SimpleSource, SimpleDestination>();

					if (result.StringProp != source.StringProp || result.IntProp != source.IntProp)
					{
						throw new InvalidOperationException("Async mapping failed");
					}
				}, _);
			}
			catch (Exception ex)
			{
				exceptions.Add(ex);
			}
		});

		// Assert
		exceptions.ShouldBeEmpty();
	}

	[Fact]
	public void RapidMapping_HighThroughput_Succeeds()
	{
		// Arrange
		const int iterations = 10000;
		SimpleSource source = new()
		{
			StringProp = "HighThroughput",
			IntProp = 42,
			DateProp = DateTime.Now
		};

		// Act & Assert - Should complete without exception
		for (int i = 0; i < iterations; i++)
		{
			SimpleDestination result = source.FastMap<SimpleSource, SimpleDestination>();
			result.StringProp.ShouldBe(source.StringProp);
		}
	}

	[Fact]
	public void ConcurrentMapping_ArrayMappings_NoExceptions()
		=> RunConcurrently(4, 300, MapArray);

	private static void MapArray(int threadId, int j)
	{
		SimpleSource[] source =
		[
			new() { StringProp = $"T{threadId}-{j}-X", IntProp = j, DateProp = DateTime.Now },
			new() { StringProp = $"T{threadId}-{j}-Y", IntProp = j + 100, DateProp = DateTime.Now.AddHours(1) }
		];

		SimpleDestination[] result = source.FastMap<SimpleSource[], SimpleDestination[]>();

		if (result.Length != 2)
		{
			throw new InvalidOperationException($"Expected 2 items, got {result.Length}");
		}

		for (int k = 0; k < source.Length; k++)
		{
			AssertSameValues(source[k], result[k], "Array");
		}
	}

	[Fact]
	public void ConcurrentMapping_DictionaryMappings_NoExceptions()
		=> RunConcurrently(4, 200, MapDictionary);

	private static void MapDictionary(int threadId, int j)
	{
		Dictionary<string, int> source = new()
		{
			[$"key{threadId}-{j}-A"] = j,
			[$"key{threadId}-{j}-B"] = j + 1
		};

		Dictionary<string, int> result = source.FastMap<Dictionary<string, int>, Dictionary<string, int>>();

		if (result.Count != source.Count)
		{
			throw new InvalidOperationException($"Expected {source.Count} items, got {result.Count}");
		}

		foreach (KeyValuePair<string, int> kvp in source)
		{
			if (!result.TryGetValue(kvp.Key, out int value) || value != kvp.Value)
			{
				throw new InvalidOperationException("Dictionary mapping produced incorrect results");
			}
		}
	}
}
