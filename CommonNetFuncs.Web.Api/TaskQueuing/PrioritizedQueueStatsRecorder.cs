using CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue;

namespace CommonNetFuncs.Web.Api.TaskQueuing;

internal static class PrioritizedQueueStatsRecorder
{
	internal static void RecordProcessedTask(PrioritizedQueueStats stats, Lock statsLock, Dictionary<TaskPriority, List<TimeSpan>> processingTimesByPriority, int processTimeWindow,
		TaskPriority priority, TimeSpan elapsed)
	{
		lock (statsLock)
		{
			stats.TotalProcessedTasks++;
			stats.LastProcessedAt = DateTime.UtcNow;

			PriorityStats priorityStats = stats.PriorityBreakdown[priority];
			priorityStats.ProcessedTasks++;
			priorityStats.LastProcessedAt = DateTime.UtcNow;

			List<TimeSpan> processingTimes = processingTimesByPriority[priority];
			processingTimes.Add(elapsed);

			if (processingTimes.Count > processTimeWindow)
			{
				processingTimes.RemoveAt(0);
			}
		}
	}
}
