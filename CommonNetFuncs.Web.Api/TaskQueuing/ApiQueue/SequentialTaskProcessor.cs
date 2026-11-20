using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace CommonNetFuncs.Web.Api.TaskQueuing.ApiQueue;

/// <summary>
/// Processes queued work sequentially in the order supplied by the channel.
/// </summary>
/// <remarks>
/// Disposal stops accepting work and is non-blocking. Work already executing is allowed to finish,
/// while work still waiting in the channel is canceled. Enqueued callers are completed as shutdown progresses.
/// </remarks>
public class SequentialTaskProcessor : BackgroundService
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();
	private readonly CancellationTokenSource cancellationTokenSource;
	private readonly ChannelWriter<QueuedTask> writer;
	private readonly ChannelReader<QueuedTask> reader;
	private readonly QueueStats stats;
	private readonly Queue<TimeSpan> processingTimes = new();
	private readonly Lock statsLock = new();
	private readonly int processTimeWindow;
	private volatile bool disposed;
	private Task? disposalTask;

	public SequentialTaskProcessor(BoundedChannelOptions boundedChannelOptions, int processTimeWindow = 1000)
	{
		cancellationTokenSource = new CancellationTokenSource();

		Channel<QueuedTask> queue = Channel.CreateBounded<QueuedTask>(boundedChannelOptions);
		writer = queue.Writer;
		reader = queue.Reader;

		this.processTimeWindow = processTimeWindow;
		stats = new QueueStats("All");
	}

	public SequentialTaskProcessor(UnboundedChannelOptions unboundedChannelOptions, int processTimeWindow = 1000)
	{
		cancellationTokenSource = new CancellationTokenSource();

		Channel<QueuedTask> queue = Channel.CreateUnbounded<QueuedTask>(unboundedChannelOptions);
		writer = queue.Writer;
		reader = queue.Reader;

		this.processTimeWindow = processTimeWindow;
		stats = new QueueStats("All");
	}

	public QueueStats Stats => GetCurrentStats();

	public virtual async Task<T?> EnqueueAsync<T>(Func<CancellationToken, Task<T?>> taskFunction, CancellationToken cancellationToken = default)
	{
		QueuedTask queuedTask = new(async ct => await taskFunction(ct).ConfigureAwait(false));
		try
		{
			await writer.WriteAsync(queuedTask, cancellationToken).ConfigureAwait(false);
		}
		catch (ChannelClosedException) when (disposed)
		{
			queuedTask.CompletionSource.TrySetCanceled();
			throw new TaskCanceledException("The task processor is shutting down.");
		}

		lock (statsLock)
		{
			stats.QueuedTasks++;
		}

		object? result = await queuedTask.CompletionSource.Task.ConfigureAwait(false);
		return (T?)result;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		await foreach (QueuedTask task in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
		{
			if (disposed)
			{
				task.CompletionSource.TrySetCanceled();
				continue;
			}

			Stopwatch stopwatch = Stopwatch.StartNew();

			try
			{
				logger.Debug("Processing task {TaskId}", task.Id);

				object? result = await task.TaskFunction(cancellationTokenSource.Token).ConfigureAwait(false);
				task.CompletionSource.SetResult(result);

				lock (statsLock)
				{
					stats.ProcessedTasks++;
					stats.LastProcessedAt = DateTime.UtcNow;
					processingTimes.Enqueue(stopwatch.Elapsed);

					if (processingTimes.Count > processTimeWindow)
					{
						processingTimes.Dequeue();
					}
				}

				logger.Debug("Completed task {TaskId}", task.Id);
			}
			catch (Exception ex)
			{
				stopwatch.Stop();

				lock (statsLock)
				{
					stats.FailedTasks++;
				}

				logger.Error(ex, "Error processing task {TaskId}", task.Id);
				task.CompletionSource.SetException(ex);
			}
		}
	}

	private QueueStats GetCurrentStats()
	{
		lock (statsLock)
		{
			QueueStats currentStats = new(stats.EndpointKey)
			{
				QueuedTasks = stats.QueuedTasks,
				ProcessedTasks = stats.ProcessedTasks,
				FailedTasks = stats.FailedTasks,
				LastProcessedAt = stats.LastProcessedAt
			};

			// Queue<T> enumerates oldest -> newest, so the average is summed in the same order as before
			if (processingTimes.Count > 0)
			{
				currentStats.AverageProcessingTime = TimeSpan.FromMilliseconds(processingTimes.Average(t => t.TotalMilliseconds));
			}

			return currentStats;
		}
	}

	public virtual Task<QueueStats> GetAllQueueStatsAsync()
	{
		return Task.FromResult(stats);
	}

	/// <summary>
	/// Initiates a non-blocking shutdown. The active task is allowed to complete and pending tasks are canceled.
	/// </summary>
	public override void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;

		try
		{
			writer.TryComplete();
		}
		catch (Exception ex)
		{
			logger.Warn(ex, "Error completing writer channel");
		}

		try
		{
			Task stopTask = StopAsync(CancellationToken.None);
			CancelPendingTasks();
			disposalTask = CompleteDisposeAsync(stopTask);
		}
		catch (Exception ex)
		{
			logger.Warn(ex, "Error stopping background service");
			CompleteDispose();
		}

		GC.SuppressFinalize(this);
	}

	private void CancelPendingTasks()
	{
		while (reader.TryRead(out QueuedTask? pendingTask))
		{
			pendingTask.CompletionSource.TrySetCanceled();
		}
	}

	private async Task CompleteDisposeAsync(Task stopTask)
	{
		try
		{
			await stopTask.ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			logger.Warn(ex, "Error waiting for background service shutdown");
		}
		finally
		{
			CancelPendingTasks();
			CompleteDispose();
		}
	}

	private void CompleteDispose()
	{
		try
		{
			cancellationTokenSource.Cancel();
		}
		catch (ObjectDisposedException)
		{
			// Already disposed.
		}

		cancellationTokenSource.Dispose();
		base.Dispose();
	}
}
