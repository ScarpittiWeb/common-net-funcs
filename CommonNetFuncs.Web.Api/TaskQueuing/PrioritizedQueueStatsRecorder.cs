using CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue;

namespace CommonNetFuncs.Web.Api.TaskQueuing;

internal static class PrioritizedQueueStatsRecorder
{
	internal static void RecordProcessedTask(PrioritizedQueueStats stats, Lock statsLock, Dictionary<TaskPriority, Queue<TimeSpan>> processingTimesByPriority, int processTimeWindow,
		TaskPriority priority, TimeSpan elapsed)
	{
		lock (statsLock)
		{
			stats.TotalProcessedTasks++;
			stats.LastProcessedAt = DateTime.UtcNow;

			PriorityStats priorityStats = stats.PriorityBreakdown[priority];
			priorityStats.ProcessedTasks++;
			priorityStats.LastProcessedAt = DateTime.UtcNow;

			Queue<TimeSpan> processingTimes = processingTimesByPriority[priority];
			processingTimes.Enqueue(elapsed);

			if (processingTimes.Count > processTimeWindow)
			{
				processingTimes.Dequeue();
			}
		}
	}
}
