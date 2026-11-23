using CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue;
using Microsoft.Extensions.Hosting;
using System.Diagnostics;
using System.Threading.Channels;

namespace CommonNetFuncs.Web.Api.TaskQueuing.ApiQueue;

/// <summary>
/// Processes sequential work submitted with an ordering priority.
/// </summary>
/// <remarks>
/// Disposal stops accepting work and is non-blocking. Work already executing is allowed to finish,
/// while work still waiting to run is canceled.
/// </remarks>
public class PrioritizedSequentialTaskProcessor : BackgroundService
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	private readonly ChannelWriter<PrioritizedQueuedTask> writer;
	private readonly ChannelReader<PrioritizedQueuedTask> reader;
	private readonly PriorityQueue<PrioritizedQueuedTask, int> priorityQueue = new();
	private readonly SemaphoreSlim semaphore = new(1, 1);
	private readonly Lock statsLock = new();
	private readonly PrioritizedQueueStats stats;
	private readonly Dictionary<TaskPriority, Queue<TimeSpan>> processingTimesByPriority = new();
	private readonly int processTimeWindow = 1000;
	private volatile bool disposed;
	private Task? disposalTask;

	public PrioritizedSequentialTaskProcessor(BoundedChannelOptions boundedChannelOptions, int processTimeWindow = 1000)
	{
		Channel<PrioritizedQueuedTask> queue = Channel.CreateBounded<PrioritizedQueuedTask>(boundedChannelOptions);

		this.processTimeWindow = processTimeWindow;
		stats = new PrioritizedQueueStats("All");

		foreach (TaskPriority priority in Enum.GetValues<TaskPriority>())
		{
			stats.PriorityBreakdown[priority] = new PriorityStats();
			processingTimesByPriority[priority] = new Queue<TimeSpan>();
		}

		writer = queue.Writer;
		reader = queue.Reader;
	}

	public PrioritizedSequentialTaskProcessor(UnboundedChannelOptions unboundedChannelOptions)
	{
		Channel<PrioritizedQueuedTask> queue = Channel.CreateUnbounded<PrioritizedQueuedTask>(unboundedChannelOptions);

		stats = new PrioritizedQueueStats("All");
		foreach (TaskPriority priority in Enum.GetValues<TaskPriority>())
		{
			stats.PriorityBreakdown[priority] = new PriorityStats();
			processingTimesByPriority[priority] = new Queue<TimeSpan>();
		}

		writer = queue.Writer;
		reader = queue.Reader;
	}

	public virtual async Task<T?> EnqueueWithPriorityAsync<T>(Func<CancellationToken, Task<T?>> taskFunction, int priority = (int)TaskPriority.Normal, TaskPriority priorityLevel = TaskPriority.Normal,
				TimeSpan? timeout = null, CancellationToken cancellationToken = default)
	{
		PrioritizedQueuedTask queuedTask = new(async ct => await taskFunction(ct).ConfigureAwait(false))
		{
			Priority = priority,
			Timeout = timeout
		};

		await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			priorityQueue.Enqueue(queuedTask, -priority); // Negative for max-heap behavior


			lock (statsLock)
			{
				stats.TotalQueuedTasks++;
				stats.PriorityBreakdown[priorityLevel].QueuedTasks++;
				stats.CurrentQueueDepth = priorityQueue.Count;
			}

			// Transfer highest priority item from priority queue to channel

			if (priorityQueue.TryDequeue(out PrioritizedQueuedTask? highestPriorityTask, out _))
			{
				try
				{
					await writer.WriteAsync(highestPriorityTask, cancellationToken).ConfigureAwait(false);
				}
				catch (ChannelClosedException) when (disposed)
				{
					highestPriorityTask.CompletionSource.TrySetCanceled();
					queuedTask.CompletionSource.TrySetCanceled();
					throw new TaskCanceledException("The task processor is shutting down.");
				}
			}
		}
		finally
		{
			semaphore.Release();
		}

		return (T?)await queuedTask.CompletionSource.Task.ConfigureAwait(false);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken) // Stopping token used here to match the BackgroundService base class

	{
		await foreach (PrioritizedQueuedTask currentTask in reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
		{
			if (disposed)
			{
				currentTask.CompletionSource.TrySetCanceled();
				continue;
			}

			try
			{
				if (currentTask.IsCancelled)
				{
					logger.Debug("Skipping cancelled task {TaskId}", currentTask.Id);
					continue;
				}

				logger.Debug("Processing task {TaskId}", currentTask.Id);

				// Process the task

				Stopwatch stopwatch = Stopwatch.StartNew();

				lock (statsLock)
				{
					stats.CurrentQueueDepth = priorityQueue.Count;
					stats.CurrentProcessingPriority = currentTask.PriorityLevel;
				}

				object? result = await currentTask.TaskFunction(CancellationToken.None).ConfigureAwait(false);
				currentTask.CompletionSource.SetResult(result);

				stopwatch.Stop();

				PrioritizedQueueStatsRecorder.RecordProcessedTask(stats, statsLock, processingTimesByPriority, processTimeWindow, currentTask.PriorityLevel, stopwatch.Elapsed);

				logger.Debug("Completed task {TaskId}", currentTask.Id);
			}
			catch (OperationCanceledException oce) when (currentTask.IsCancelled)
			{
				logger.Debug(oce, "Task {TaskId} was cancelled", currentTask.Id);
				// Task was already marked as cancelled, no need to update stats

			}
			catch (Exception ex)
			{
				logger.Error(ex, "Error processing task {TaskId}", currentTask.Id);
				currentTask.CompletionSource.SetException(ex);
			}
			finally
			{
				lock (statsLock)
				{
					stats.CurrentProcessingPriority = null;
				}
			}
		}
	}

	public virtual Task<PrioritizedQueueStats> GetAllQueueStatsAsync()
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
		while (reader.TryRead(out PrioritizedQueuedTask? pendingTask))
		{
			pendingTask.CompletionSource.TrySetCanceled();
			pendingTask.CancellationTokenSource.Cancel();
			pendingTask.CancellationTokenSource.Dispose();
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
			await CancelPriorityQueueAsync().ConfigureAwait(false);
			CompleteDispose();
		}
	}

	private async Task CancelPriorityQueueAsync()
	{
		await semaphore.WaitAsync().ConfigureAwait(false);
		try
		{
			while (priorityQueue.TryDequeue(out PrioritizedQueuedTask? pendingTask, out _))
			{
				pendingTask.CompletionSource.TrySetCanceled();
				pendingTask.CancellationTokenSource.Cancel();
				pendingTask.CancellationTokenSource.Dispose();
			}
		}
		finally
		{
			semaphore.Release();
		}
	}

	private void CompleteDispose()
	{
		semaphore.Dispose();
		base.Dispose();
	}
}
