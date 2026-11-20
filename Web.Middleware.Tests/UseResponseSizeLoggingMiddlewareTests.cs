using CommonNetFuncs.Web.Middleware;
using FakeItEasy;
using FakeItEasy.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using xRetry.v3;

namespace Web.Middleware.Tests;

public sealed class UseResponseSizeLoggingMiddlewareTests
{
	private readonly ILogger<UseResponseSizeLoggingMiddleware> logger;
	private readonly RequestDelegate next;

	public UseResponseSizeLoggingMiddlewareTests()
	{
		logger = A.Fake<ILogger<UseResponseSizeLoggingMiddleware>>();
		next = A.Fake<RequestDelegate>();
	}

	[RetryFact(3)]
	public void Constructor_ValidParameters_CreatesInstance()
	{
		// Act
		UseResponseSizeLoggingMiddleware middleware = new(next, logger, 1024 * 100);

		// Assert
		middleware.ShouldNotBeNull();
	}

	[RetryTheory(3)]
	[InlineData(0)]    // Empty response
	[InlineData(100)]  // Small response
	[InlineData(1024)] // 1KB response
	public async Task InvokeAsync_LogsResponseSize_ForVariousContentSizes(int contentSize)
	{
		// Arrange
		UseResponseSizeLoggingMiddleware middleware = new(next, logger, -1);
		DefaultHttpContext context = new();
		await using MemoryStream responseBody = new();
		context.Response.Body = responseBody;

		byte[] content = new byte[contentSize];
		A.CallTo(() => next(context)).Returns(Task.Run(() => context.Response.Body.WriteAsync(content, 0, content.Length)));
		A.CallTo(() => logger.IsEnabled(LogLevel.Warning)).Returns(true);

		// Set up request headers
		context.Request.Method = "GET";
		context.Request.Path = "/test-path";
		context.Request.Headers.Accept = "application/json";
		context.Request.Headers.AcceptEncoding = "gzip";

		// Act
		await middleware.InvokeAsync(context);

		// Assert - Get the actual call and verify the formatted message
		IFakeObjectCall call = Fake.GetCalls(logger).Single(c => c.Method.Name == "Log");
		call.Arguments[0].ShouldBe(LogLevel.Warning);

		object? state = call.Arguments[2];
		string message = state?.ToString() ?? string.Empty;
		message.ShouldContain("Response to");
		message.ShouldContain("/test-path");
		message.ShouldContain("[GET]");
		message.ShouldContain("Size:");
	}

	[RetryFact(3)]
	public async Task InvokeAsync_RestoresOriginalBodyStream()
	{
		// Arrange
		UseResponseSizeLoggingMiddleware middleware = new(next, logger, 1024 * 100);
		DefaultHttpContext context = new();
		await using MemoryStream originalBody = new();
		context.Response.Body = originalBody;

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		context.Response.Body.ShouldBe(originalBody);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_HandlesNextDelegateException()
	{
		// Arrange
		UseResponseSizeLoggingMiddleware middleware = new(next, logger, 1024 * 100);
		DefaultHttpContext context = new();
		Exception exception = new("Test exception");

		A.CallTo(() => next(context)).ThrowsAsync(exception);

		// Act & Assert
		Exception thrownException = await Should.ThrowAsync<Exception>(async () => await middleware.InvokeAsync(context));

		thrownException.ShouldBe(exception);
		context.Response.Body.ShouldBe(context.Response.Body);
	}

	[RetryFact(3)]
	public async Task InvokeAsync_HandlesEmptyHeaders()
	{
		// Arrange
		A.CallTo(() => logger.IsEnabled(LogLevel.Warning)).Returns(true);
		UseResponseSizeLoggingMiddleware middleware = new(next, logger, -1);
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();

		// Don't set any headers to test null handling
		context.Request.Headers.Accept = string.Empty;
		context.Request.Headers.AcceptEncoding = string.Empty;

		// Act
		await middleware.InvokeAsync(context);

		// Assert - Get the actual call and verify the formatted message
		IFakeObjectCall call = Fake.GetCalls(logger).Single(c => c.Method.Name == "Log");
		call.Arguments[0].ShouldBe(LogLevel.Warning);

		object? state = call.Arguments[2];
		string message = state?.ToString() ?? string.Empty;
		message.ShouldContain("Response to");
		message.ShouldNotContain("+");
	}
}

public sealed class ResponseSizeLoggingBehaviorTests
{




	private sealed class ThrowingDisposeMemoryStream(Exception failure) : MemoryStream
	{
		public override ValueTask DisposeAsync()
		{
			return ValueTask.FromException(failure);
		}
	}

	private sealed class GatedDisposeMemoryStream(TaskCompletionSource started, TaskCompletionSource allowDisposal) : MemoryStream
	{
		public bool WasDisposed { get; private set; }

		public override async ValueTask DisposeAsync()
		{
			started.SetResult();
			await allowDisposal.Task;
			WasDisposed = true;
			await base.DisposeAsync();
		}
	}

	private sealed class GatedFailingDisposeMemoryStream(
		TaskCompletionSource started,
		TaskCompletionSource allowDisposal,
		Exception failure) : MemoryStream
	{
		public override async ValueTask DisposeAsync()
		{
			started.SetResult();
			await allowDisposal.Task;
			throw failure;
		}
	}

	[Fact]
	public async Task InvokeAsync_DisposeFailure_RestoresOriginalStreamAndPropagatesExactException()
	{
		// Arrange
		InvalidOperationException failure = new("dispose failed");
		ThrowingDisposeMemoryStream originalBody = new(failure);
		DefaultHttpContext context = new();
		context.Response.Body = originalBody;
		ILogger<UseResponseSizeLoggingMiddleware> logger = A.Fake<ILogger<UseResponseSizeLoggingMiddleware>>();
		UseResponseSizeLoggingMiddleware middleware = new(_ => Task.CompletedTask, logger, 0);

		// Act
		InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

		// Assert
		thrown.ShouldBeSameAs(failure);
		context.Response.Body.ShouldBeSameAs(originalBody);
	}

	[Fact]
	public async Task InvokeAsync_AsynchronousOriginalStreamDisposal_WaitsAndRestoresStream()
	{
		// Arrange
		TaskCompletionSource disposalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		GatedDisposeMemoryStream originalBody = new(disposalStarted, allowDisposal);
		DefaultHttpContext context = new();
		context.Response.Body = originalBody;
		UseResponseSizeLoggingMiddleware middleware = new(
			_ => Task.CompletedTask,
			A.Fake<ILogger<UseResponseSizeLoggingMiddleware>>(),
			long.MaxValue);

		// Act
		Task invocation = middleware.InvokeAsync(context);
		await disposalStarted.Task;
		invocation.IsCompleted.ShouldBeFalse();
		allowDisposal.SetResult();
		await invocation;

		// Assert
		context.Response.Body.ShouldBeSameAs(originalBody);
		originalBody.WasDisposed.ShouldBeTrue();
	}

	[Fact]
	public async Task InvokeAsync_AsynchronousDisposeFailure_RestoresOriginalStreamAndPropagatesExactException()
	{
		// Arrange
		InvalidOperationException failure = new("asynchronous dispose failed");
		TaskCompletionSource disposalStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource allowDisposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		GatedFailingDisposeMemoryStream originalBody = new(disposalStarted, allowDisposal, failure);
		DefaultHttpContext context = new();
		context.Response.Body = originalBody;
		UseResponseSizeLoggingMiddleware middleware = new(
			_ => Task.CompletedTask,
			A.Fake<ILogger<UseResponseSizeLoggingMiddleware>>(),
			0);

		// Act
		Task invocation = middleware.InvokeAsync(context);
		await disposalStarted.Task;
		invocation.IsCompleted.ShouldBeFalse();
		allowDisposal.SetResult();
		InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => invocation);

		// Assert
		thrown.ShouldBeSameAs(failure);
		context.Response.Body.ShouldBeSameAs(originalBody);
	}

	[Fact]
	public async Task InvokeAsync_DisabledWarningLog_DoesNotLogAboveThreshold()
	{
		// Arrange
		DefaultHttpContext context = new();
		context.Request.Method = HttpMethods.Get;
		context.Request.Path = "/large";
		context.Response.Body = new MemoryStream();
		ILogger<UseResponseSizeLoggingMiddleware> logger = A.Fake<ILogger<UseResponseSizeLoggingMiddleware>>();
		A.CallTo(() => logger.IsEnabled(LogLevel.Warning)).Returns(false);
		UseResponseSizeLoggingMiddleware middleware = new(
			invocationContext => invocationContext.Response.Body.WriteAsync(new byte[8]).AsTask(),
			logger,
			1);

		// Act
		await middleware.InvokeAsync(context);

		// Assert
		Fake.GetCalls(logger).Count(call => call.Method.Name == "Log").ShouldBe(0);
	}

}
