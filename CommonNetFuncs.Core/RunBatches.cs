namespace CommonNetFuncs.Core;

using CommonNetFuncs.Core.Internal;

/// <summary>
/// Run batches of operations on a collection of items.
/// </summary>
public static class RunBatches
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	/// <summary>
	/// Takes a collection of items and processes them in batches using the provided async processor.
	/// </summary>
	public static async Task<bool> RunBatchedProcessAsync<T>(this IEnumerable<T> itemsToProcess, Func<IEnumerable<T>, Task<bool>> processor, int batchSize = 10000, bool breakOnFail = true, bool logProgress = true, CancellationToken cancellationToken = default)
	{
		ThrowHelper.ThrowIfNull(itemsToProcess, nameof(itemsToProcess));
		ThrowHelper.ThrowIfNull(processor, nameof(processor));
		List<T> distinctItems = PrepareBatches(itemsToProcess, batchSize, out int totalBatches);
		bool success = true;

		for (int i = 0; i < totalBatches; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<T> batch = GetBatch(distinctItems, i, batchSize);

			success &= await processor(batch).ConfigureAwait(false);

			if (ShouldStopAfterBatch(success, breakOnFail, logProgress, i, totalBatches))
			{
				break;
			}
		}

		return success;
	}

	public static Task<bool> RunBatchedProcessAsync<T>(this IEnumerable<T> itemsToProcess, Func<List<T>, Task<bool>> listProcessor, int batchSize = 10000, bool breakOnFail = true,
			bool logProgress = true, CancellationToken cancellationToken = default)
	{
		ThrowHelper.ThrowIfNull(listProcessor, nameof(listProcessor));
		// Adapt the List processor to work with IEnumerable - batch from GetRange is always List<TNumber>
		return RunBatchedProcessAsync(itemsToProcess, async batch => await listProcessor((List<T>)batch).ConfigureAwait(false), batchSize, breakOnFail, logProgress, cancellationToken);
	}

	/// <summary>
	/// Takes a collection of items and processes them in batches using the provided sync processor.
	/// </summary>
	//public static bool RunBatchedProcess<TObj>(this IEnumerable<TObj> itemsToProcess, SyncBatchProcessor<TObj> processor, int batchSize = 10000, bool breakOnFail = true, bool logProgress = true)
	public static bool RunBatchedProcess<T>(this IEnumerable<T> itemsToProcess, Func<IEnumerable<T>, bool> processor, int batchSize = 10000, bool breakOnFail = true, bool logProgress = true, CancellationToken cancellationToken = default)
	{
		ThrowHelper.ThrowIfNull(itemsToProcess, nameof(itemsToProcess));
		ThrowHelper.ThrowIfNull(processor, nameof(processor));
		List<T> distinctItems = PrepareBatches(itemsToProcess, batchSize, out int totalBatches);
		bool success = true;

		for (int i = 0; i < totalBatches; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			IReadOnlyList<T> batch = GetBatch(distinctItems, i, batchSize);

			success &= processor(batch);

			if (ShouldStopAfterBatch(success, breakOnFail, logProgress, i, totalBatches))
			{
				break;
			}
		}

		return success;
	}

	// Extension methods for List-specific processors
	public static bool RunBatchedProcess<T>(this IEnumerable<T> itemsToProcess, Func<List<T>, bool> listProcessor, int batchSize = 10000, bool breakOnFail = true, bool logProgress = true,
			CancellationToken cancellationToken = default)
	{
		ThrowHelper.ThrowIfNull(listProcessor, nameof(listProcessor));
		// Adapt the List processor to work with IEnumerable - batch from GetRange is always List<TNumber>
		return RunBatchedProcess(itemsToProcess, batch => listProcessor((List<T>)batch), batchSize, breakOnFail, logProgress, cancellationToken);
	}

	private static List<T> PrepareBatches<T>(IEnumerable<T> itemsToProcess, int batchSize, out int totalBatches)
	{
		ThrowHelper.ThrowIfNegativeOrZero(batchSize, nameof(batchSize));

		// Materialize distinct items once - use HashSet directly to avoid double materialization
		List<T> distinctItems = new(new HashSet<T>(itemsToProcess));
		totalBatches = (int)MathHelpers.Ceiling((decimal)distinctItems.Count / batchSize, 1);
		return distinctItems;
	}

	private static List<T> GetBatch<T>(List<T> items, int batchIndex, int batchSize)
	{
		int start = batchIndex * batchSize;
		return items.GetRange(start, Math.Min(batchSize, items.Count - start));
	}

	// Returns true when processing should stop because a batch failed and breakOnFail is set.
	private static bool ShouldStopAfterBatch(bool success, bool breakOnFail, bool logProgress, int batchIndex, int totalBatches)
	{
		if (logProgress)
		{
			logger.Info("Process {CurrentBatch}/{TotalBatches} complete", batchIndex + 1, totalBatches);
		}

		return !success && breakOnFail;
	}
}
