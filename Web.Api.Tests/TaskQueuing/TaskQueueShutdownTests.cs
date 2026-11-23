using CommonNetFuncs.Web.Api.TaskQueuing.ApiQueue;
using CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue;
using System.Diagnostics;
using System.Threading.Channels;
using static Xunit.TestContext;

namespace Web.Api.Tests.TaskQueuing;

public sealed class TaskQueueShutdownTests
{
	[Fact]
	public async Task SequentialProcessor_Dispose_IsPromptAndCompletesActiveButCancelsPending()
	{
		SequentialTaskProcessor processor = new(new UnboundedChannelOptions());
		await processor.StartAsync(Current.CancellationToken);
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task<int?> active = processor.EnqueueAsync<int?>(async _ =>
		{
			started.SetResult();
			await release.Task;
			return 1;
		}, Current.CancellationToken);
		await started.Task;
		Task<int?> pending = processor.EnqueueAsync(_ => Task.FromResult((int?)2), Current.CancellationToken);

		Stopwatch stopwatch = Stopwatch.StartNew();
		processor.Dispose();
		stopwatch.Stop();

		stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
		await Should.ThrowAsync<TaskCanceledException>(async () => await pending);

		release.SetResult();
		(await active).ShouldBe(1);
	}

	[Fact]
	public async Task PrioritizedProcessor_Dispose_IsPromptAndCompletesActiveButCancelsPending()
	{
		PrioritizedSequentialTaskProcessor processor = new(new UnboundedChannelOptions());
		await processor.StartAsync(Current.CancellationToken);
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task<int?> active = processor.EnqueueWithPriorityAsync<int?>(async _ =>
		{
			started.SetResult();
			await release.Task;
			return 1;
		}, cancellationToken: Current.CancellationToken);
		await started.Task;
		Task<int?> pending = processor.EnqueueWithPriorityAsync(_ => Task.FromResult((int?)2), cancellationToken: Current.CancellationToken);

		Stopwatch stopwatch = Stopwatch.StartNew();
		processor.Dispose();
		stopwatch.Stop();

		stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
		await Should.ThrowAsync<TaskCanceledException>(async () => await pending);

		release.SetResult();
		(await active).ShouldBe(1);
	}

	[Fact]
	public async Task EndpointQueue_Dispose_IsPromptAndCancelsActiveAndPending()
	{
		CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue.EndpointQueue queue = new("shutdown", new UnboundedChannelOptions());
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task<int> active = queue.EnqueueAsync<int>(async cancellationToken =>
		{
			started.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 1;
		}, Current.CancellationToken);
		await started.Task;
		Task<int> pending = queue.EnqueueAsync(_ => Task.FromResult(2), Current.CancellationToken);

		Stopwatch stopwatch = Stopwatch.StartNew();
		queue.Dispose();
		stopwatch.Stop();

		stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
		await Should.ThrowAsync<TaskCanceledException>(async () => await active);
		await Should.ThrowAsync<TaskCanceledException>(async () => await pending);
	}

	[Fact]
	public async Task PrioritizedEndpointQueue_Dispose_IsPromptAndCancelsActiveAndPending()
	{
		PrioritizedEndpointQueue queue = new("shutdown");
		TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);

		Task<int> active = queue.EnqueueAsync<int>(async cancellationToken =>
		{
			started.SetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return 1;
		}, 1, TaskPriority.Normal, Current.CancellationToken);
		await started.Task;
		Task<int> pending = queue.EnqueueAsync(_ => Task.FromResult(2), 1, TaskPriority.Normal, Current.CancellationToken);

		Stopwatch stopwatch = Stopwatch.StartNew();
		queue.Dispose();
		stopwatch.Stop();

		stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
		await Should.ThrowAsync<TaskCanceledException>(async () => await active);
		await Should.ThrowAsync<TaskCanceledException>(async () => await pending);
	}
}
