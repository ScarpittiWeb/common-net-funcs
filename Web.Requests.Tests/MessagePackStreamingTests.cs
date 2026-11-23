using CommonNetFuncs.Web.Requests;
using CommonNetFuncs.Web.Requests.MessagePack;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static Xunit.TestContext;

namespace Web.Requests.Tests;

public sealed class MessagePackStreamingTests
{
	#region Test Models

	[MessagePackObject]
	public class TestModel
	{
		[Key(0)]
		public int Id { get; set; }
		[Key(1)]
		public string Name { get; set; } = string.Empty;
	}

	#endregion

	#region MessagePackStreamingResult Tests - Controller Scenario

	[Fact]
	public async Task MessagePackStreamingResult_Controller_WithData_ReturnsSuccess()
	{
		// Arrange
		List<TestModel> testData =
		[
			new TestModel { Id = 1, Name = "Test1" },
			new TestModel { Id = 2, Name = "Test2" }
		];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-msgpack");

		// Verify the stream contains MessagePack data
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Position = 0;

		using MessagePackStreamReader messagePackStreamReader = new(ms, leaveOpen: true);
		List<TestModel> readItems = [];

		while (await messagePackStreamReader.ReadAsync(Current.CancellationToken) is ReadOnlySequence<byte> msgPackData)
		{
			TestModel? item = MessagePackSerializer.Deserialize<TestModel>(msgPackData, MessagePackSerializerOptions.Standard, Current.CancellationToken);
			if (item != null)
			{
				readItems.Add(item);
			}
		}

		readItems.Count.ShouldBe(2);
		readItems[0].Id.ShouldBe(1);
		readItems[0].Name.ShouldBe("Test1");
		readItems[1].Id.ShouldBe(2);
		readItems[1].Name.ShouldBe("Test2");
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithNoData_ReturnsNoContent()
	{
		// Arrange
		List<TestModel> emptyData = [];
		MessagePackStreamingResult<TestModel> result = new(emptyData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithCustomSuccessStatusCode_ReturnsCustomCode()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable(), successStatusCode: StatusCodes.Status202Accepted);
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithCustomEmptyStatusCode_ReturnsCustomCode()
	{
		// Arrange
		List<TestModel> emptyData = [];
		MessagePackStreamingResult<TestModel> result = new(emptyData.ToAsyncEnumerable(), emptyStatusCode: StatusCodes.Status404NotFound);
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithCompressedOptions_SerializesWithCompression()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test" }];
		MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable(), options);
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Length.ShouldBeGreaterThan(0);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithNullData_ThrowsArgumentNullException()
	{
		// Act & Assert
		Should.Throw<ArgumentNullException>(() => new MessagePackStreamingResult<TestModel>(null!));
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithNullContext_ThrowsArgumentNullException()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());

		// Act & Assert
		await Should.ThrowAsync<ArgumentNullException>(async () => await result.ExecuteResultAsync(null!));
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithExceptionDuringIteration_Returns500IfNotStarted()
	{
		// Arrange
		async IAsyncEnumerable<TestModel> GetDataWithError()
		{
			yield return new TestModel { Id = 1, Name = "Test1" };
			await Task.Delay(1);
			throw new InvalidOperationException("Test exception");
		}

		MessagePackStreamingResult<TestModel> result = new(GetDataWithError());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act & Assert
		await Should.ThrowAsync<InvalidOperationException>(async () => await result.ExecuteResultAsync(actionContext));

		// Response should be 500 if not started
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithCancellation_StopsStreamingWithoutThrowingAndSetsClientClosedStatus()
	{
		// Arrange
		using CancellationTokenSource cts = new();
		int itemsYielded = 0;
		async IAsyncEnumerable<TestModel> GetDataSlowly()
		{
			for (int i = 0; i < 10; i++)
			{
				await Task.Delay(10, Current.CancellationToken);
				itemsYielded++;
				yield return new TestModel { Id = i, Name = $"Test{i}" };
				if (i == 2)
				{
					await cts.CancelAsync();
				}
			}
		}

		MessagePackStreamingResult<TestModel> result = new(GetDataSlowly());
		ActionContext actionContext = CreateActionContext(cts.Token, "application/x-msgpack");

		// Act - cancellation should be handled internally, not thrown
		await result.ExecuteResultAsync(actionContext);

		// Assert - streaming stopped shortly after cancellation instead of running through all 10 items
		itemsYielded.ShouldBeLessThan(10);
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithCancellation_PropagatesTokenToAsyncEnumerable()
	{
		// Arrange
		using CancellationTokenSource cts = new();
		int itemsProduced = 0;

		// Only completes early if the cancellation token passed to the enumerator is signaled (via WithCancellation).
		async IAsyncEnumerable<TestModel> GetDataObservingCancellation([EnumeratorCancellation] CancellationToken cancellationToken = default)
		{
			for (int i = 0; i < 5; i++)
			{
				itemsProduced++;
				yield return new TestModel { Id = i, Name = $"Test{i}" };
				await Task.Delay(Timeout.Infinite, cancellationToken);
			}
		}

		MessagePackStreamingResult<TestModel> result = new(GetDataObservingCancellation(Current.CancellationToken));
		ActionContext actionContext = CreateActionContext(cts.Token, "application/x-msgpack");

		Task executeTask = result.ExecuteResultAsync(actionContext);
		await Task.Delay(50, Current.CancellationToken);
		await cts.CancelAsync();

		// Act - if the token isn't propagated to the enumerable, this will hang until the test times out
		Task completedTask = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(5), Current.CancellationToken));

		// Assert
		completedTask.ShouldBe(executeTask, "ExecuteResultAsync did not complete; the cancellation token was not propagated to the IAsyncEnumerable");
		await executeTask;
		itemsProduced.ShouldBe(1);
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithAlreadyCancelledToken_DoesNotThrowAndSetsClientClosedStatus()
	{
		// Arrange
		List<TestModel> testData =
		[
			new TestModel { Id = 1, Name = "Test1" },
			new TestModel { Id = 2, Name = "Test2" }
		];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(new CancellationToken(canceled: true), "application/x-msgpack");

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status499ClientClosedRequest);
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithUnrelatedOperationCanceledException_Returns500AndRethrows()
	{
		// Arrange - throws OperationCanceledException that is unrelated to the request's own RequestAborted token
		async IAsyncEnumerable<TestModel> GetDataWithUnrelatedCancellation()
		{
			yield return new TestModel { Id = 1, Name = "Test1" };
			await Task.Delay(1, Current.CancellationToken);
			throw new OperationCanceledException("Unrelated cancellation");
		}

		MessagePackStreamingResult<TestModel> result = new(GetDataWithUnrelatedCancellation());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act & Assert - since actionContext's RequestAborted token was never cancelled, this should not be treated as a client disconnect
		await Should.ThrowAsync<OperationCanceledException>(async () => await result.ExecuteResultAsync(actionContext));
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status500InternalServerError);
	}

	#endregion

	#region MessagePackStreamingExtensions Tests - Controller Extension Methods

	[Fact]
	public void StreamMessagePack_ControllerExtension_WithData_ReturnsMessagePackStreamingResult()
	{
		// Arrange
		FakeController controller = new();
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test" }];

		// Act
		MessagePackStreamingResult<TestModel> result = controller.StreamMessagePack(testData.ToAsyncEnumerable());

		// Assert
		result.ShouldNotBeNull();
		result.ShouldBeOfType<MessagePackStreamingResult<TestModel>>();
	}

	[Fact]
	public void StreamMessagePack_ControllerExtension_WithOptions_PassesOptionsToResult()
	{
		// Arrange
		FakeController controller = new();
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test" }];
		MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);

		// Act
		MessagePackStreamingResult<TestModel> result = controller.StreamMessagePack(testData.ToAsyncEnumerable(), options);

		// Assert
		result.ShouldNotBeNull();
	}

	[Fact]
	public void StreamMessagePack_ControllerExtension_WithCustomStatusCodes_PassesCodesToResult()
	{
		// Arrange
		FakeController controller = new();
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test" }];

		// Act
		MessagePackStreamingResult<TestModel> result = controller.StreamMessagePack(
			testData.ToAsyncEnumerable(),
			successStatusCode: StatusCodes.Status202Accepted,
			emptyStatusCode: StatusCodes.Status404NotFound);

		// Assert
		result.ShouldNotBeNull();
	}

	[Fact]
	public void StreamMessagePack_ControllerExtension_WithNullData_ThrowsArgumentNullException()
	{
		// Arrange
		FakeController controller = new();

		// Act & Assert
		Should.Throw<ArgumentNullException>(() => controller.StreamMessagePack<TestModel>(null!));
	}

	#endregion

	#region MessagePackStreaming Tests - Minimal API Static Methods

	[Fact]
	public async Task MessagePackStreaming_MinimalApi_WithData_ReturnsSuccess()
	{
		// Arrange
		List<TestModel> testData =
		[
			new TestModel { Id = 1, Name = "MinimalApi1" },
			new TestModel { Id = 2, Name = "MinimalApi2" },
			new TestModel { Id = 3, Name = "MinimalApi3" }
		];

		// Act - Using static helper as you would in a Minimal API
		MessagePackStreamingResult<TestModel> result = MessagePackStreaming.Stream(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-msgpack");

		// Verify the stream contains MessagePack data
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Position = 0;

		using MessagePackStreamReader messagePackStreamReader = new(ms, leaveOpen: true);
		List<TestModel> readItems = [];

		while (await messagePackStreamReader.ReadAsync(Current.CancellationToken) is ReadOnlySequence<byte> msgPackData)
		{
			TestModel? item = MessagePackSerializer.Deserialize<TestModel>(msgPackData, MessagePackSerializerOptions.Standard, Current.CancellationToken);
			if (item != null)
			{
				readItems.Add(item);
			}
		}

		readItems.Count.ShouldBe(3);
		readItems[0].Name.ShouldBe("MinimalApi1");
		readItems[1].Name.ShouldBe("MinimalApi2");
		readItems[2].Name.ShouldBe("MinimalApi3");
	}

	[Fact]
	public async Task MessagePackStreaming_MinimalApi_WithEmptyData_ReturnsNoContent()
	{
		// Arrange
		List<TestModel> emptyData = [];

		// Act - Using static helper as you would in a Minimal API
		MessagePackStreamingResult<TestModel> result = MessagePackStreaming.Stream(emptyData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
	}

	[Fact]
	public async Task MessagePackStreaming_MinimalApi_WithCompression_SerializesCorrectly()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 100, Name = "CompressedMinimalApi" }];
		MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray);

		// Act - Using static helper with compression as you would in a Minimal API
		MessagePackStreamingResult<TestModel> result = MessagePackStreaming.Stream(testData.ToAsyncEnumerable(), options);
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Length.ShouldBeGreaterThan(0);
	}

	[Fact]
	public async Task MessagePackStreaming_MinimalApi_WithCustomStatusCodes_ReturnsCustomCodes()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test" }];

		// Act - Using static helper with custom status codes
		MessagePackStreamingResult<TestModel> result = MessagePackStreaming.Stream(testData.ToAsyncEnumerable(), successStatusCode: StatusCodes.Status202Accepted, emptyStatusCode: StatusCodes.Status404NotFound);

		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status202Accepted);
	}

	[Fact]
	public async Task MessagePackStreaming_MinimalApi_WithLargeDataset_StreamsEfficiently()
	{
		// Arrange - Simulate a large dataset
		async IAsyncEnumerable<TestModel> GetLargeDataset()
		{
			for (int i = 0; i < 1000; i++)
			{
				await Task.Yield();
				yield return new TestModel { Id = i, Name = $"Item{i}" };
			}
		}

		// Act - Using static helper for large dataset streaming
		MessagePackStreamingResult<TestModel> result = MessagePackStreaming.Stream(GetLargeDataset());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);

		// Verify we can read all items back
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Position = 0;

		using MessagePackStreamReader messagePackStreamReader = new(ms, leaveOpen: true);
		int count = 0;

		while (await messagePackStreamReader.ReadAsync(Current.CancellationToken) is ReadOnlySequence<byte> msgPackData)
		{
			TestModel? item = MessagePackSerializer.Deserialize<TestModel>(msgPackData, MessagePackSerializerOptions.Standard, Current.CancellationToken);
			if (item != null)
			{
				count++;
			}
		}

		count.ShouldBe(1000);
	}

	[Fact]
	public void MessagePackStreaming_MinimalApi_WithNullData_ThrowsArgumentNullException()
	{
		// Act & Assert
		Should.Throw<ArgumentNullException>(() => MessagePackStreaming.Stream<TestModel>(null!));
	}

	#endregion

	#region Integration Tests - Both Controller and Minimal API Patterns

	[Theory]
	[InlineData(true)]  // Controller pattern
	[InlineData(false)] // Minimal API pattern
	public async Task MessagePackStreaming_BothPatterns_ProduceSameOutput(bool useControllerPattern)
	{
		// Arrange
		List<TestModel> testData =
		[
			new TestModel { Id = 1, Name = "Test1" },
			new TestModel { Id = 2, Name = "Test2" }
		];

		MessagePackStreamingResult<TestModel> result;
		if (useControllerPattern)
		{
			// Controller pattern
			FakeController controller = new();
			result = controller.StreamMessagePack(testData.ToAsyncEnumerable());
		}
		else
		{
			// Minimal API pattern
			result = MessagePackStreaming.Stream(testData.ToAsyncEnumerable());
		}

		ActionContext actionContext = CreateActionContext(Current.CancellationToken, "application/x-msgpack");

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert - Both patterns should produce identical results
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-msgpack");

		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Position = 0;

		using MessagePackStreamReader messagePackStreamReader = new(ms, leaveOpen: true);
		List<TestModel> readItems = [];

		while (await messagePackStreamReader.ReadAsync(Current.CancellationToken) is ReadOnlySequence<byte> msgPackData)
		{
			TestModel? item = MessagePackSerializer.Deserialize<TestModel>(msgPackData, MessagePackSerializerOptions.Standard, Current.CancellationToken);
			if (item != null)
			{
				readItems.Add(item);
			}
		}

		readItems.Count.ShouldBe(2);
		readItems[0].Id.ShouldBe(1);
		readItems[1].Id.ShouldBe(2);
	}

	#endregion

	#region Content Negotiation Tests

	[Fact]
	public async Task MessagePackStreamingResult_WithJsonAcceptHeader_ReturnsNDJSON()
	{
		// Arrange
		List<TestModel> testData =
		[
			new TestModel { Id = 1, Name = "Test1" },
			new TestModel { Id = 2, Name = "Test2" }
		];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers.Accept = "application/json";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-ndjson");

		// Verify the stream contains NDJSON data
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Position = 0;
		StreamReader reader = new(ms);
		string content = await reader.ReadToEndAsync(Current.CancellationToken);

		string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		lines.Length.ShouldBe(2);

		TestModel? item1 = JsonSerializer.Deserialize<TestModel>(lines[0]);
		item1.ShouldNotBeNull();
		item1.Id.ShouldBe(1);
		item1.Name.ShouldBe("Test1");

		TestModel? item2 = JsonSerializer.Deserialize<TestModel>(lines[1]);
		item2.ShouldNotBeNull();
		item2.Id.ShouldBe(2);
		item2.Name.ShouldBe("Test2");
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithNoAcceptHeader_DefaultsToJSON()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		// No Accept header set

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-ndjson");
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithWildcardAccept_DefaultsToJSON()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers.Accept = "*/*";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-ndjson");
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithMessagePackAcceptHeader_ReturnsMessagePack()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers.Accept = "application/x-msgpack";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-msgpack");
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithUnsupportedAcceptHeader_ReturnsBadRequest()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers.Accept = "application/xml";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
		actionContext.HttpContext.Response.ContentType.ShouldBe("text/plain");

		// Verify error message
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Position = 0;
		StreamReader reader = new(ms);
		string content = await reader.ReadToEndAsync(Current.CancellationToken);
		content.ShouldContain("Unsupported Accept header");
		content.ShouldContain("application/xml");
	}

	[Fact]
	public async Task MessagePackStreamingResult_WithCustomJsonOptions_UsesProvidedOptions()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		JsonSerializerOptions jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable(), jsonOptions: jsonOptions);
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers["Accept"] = "application/json";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		MemoryStream ms = (MemoryStream)actionContext.HttpContext.Response.Body;
		ms.Position = 0;
		StreamReader reader = new(ms);
		string content = await reader.ReadToEndAsync(Current.CancellationToken);

		// Camel case formatting should be applied
		content.ShouldContain("\"id\":");
		content.ShouldContain("\"name\":");
	}

	[Fact]
	public async Task MessagePackStreamingResult_EmptyDataWithJsonAccept_ReturnsNoContent()
	{
		// Arrange
		List<TestModel> emptyData = [];
		MessagePackStreamingResult<TestModel> result = new(emptyData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers["Accept"] = "application/json";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status204NoContent);
	}

	[Fact]
	public async Task StreamMessagePack_Extension_WithJsonAccept_ReturnsNDJSON()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		FakeController controller = new();
		IActionResult result = controller.StreamMessagePack(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers["Accept"] = "application/json";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-ndjson");
	}

	[Fact]
	public async Task MessagePackStreaming_Static_WithJsonAccept_ReturnsNDJSON()
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		IActionResult result = MessagePackStreaming.Stream(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers["Accept"] = "application/json";

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-ndjson");
	}

	[Theory]
	[InlineData("application/json")]
	[InlineData("application/x-ndjson")]
	[InlineData("*/*")]
	[InlineData("application/*")]
	[InlineData("")]
	public async Task MessagePackStreamingResult_VariousJsonAcceptHeaders_ReturnsJSON(string acceptHeader)
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		if (!string.IsNullOrEmpty(acceptHeader))
		{
			actionContext.HttpContext.Request.Headers["Accept"] = acceptHeader;
		}

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-ndjson");
	}

	[Theory]
	[InlineData("application/x-msgpack")]
	[InlineData("application/msgpack")]
	public async Task MessagePackStreamingResult_MessagePackAcceptHeaders_ReturnsMessagePack(string acceptHeader)
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers["Accept"] = acceptHeader;

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		actionContext.HttpContext.Response.ContentType.ShouldBe("application/x-msgpack");
	}

	[Theory]
	[InlineData("application/xml")]
	[InlineData("text/html")]
	[InlineData("text/plain")]
	[InlineData("application/protobuf")]
	public async Task MessagePackStreamingResult_UnsupportedAcceptHeaders_ReturnsBadRequest(string acceptHeader)
	{
		// Arrange
		List<TestModel> testData = [new TestModel { Id = 1, Name = "Test1" }];
		MessagePackStreamingResult<TestModel> result = new(testData.ToAsyncEnumerable());
		ActionContext actionContext = CreateActionContext(Current.CancellationToken);
		actionContext.HttpContext.Request.Headers.Accept = acceptHeader;

		// Act
		await result.ExecuteResultAsync(actionContext);

		// Assert
		actionContext.HttpContext.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
	}

	#endregion

	#region Helper Methods

	private static ActionContext CreateActionContext(CancellationToken cancellationToken = default, string acceptHeader = "")
	{
		DefaultHttpContext httpContext = new();
		httpContext.Response.Body = new MemoryStream();
		httpContext.RequestAborted = cancellationToken;

		if (!string.IsNullOrEmpty(acceptHeader))
		{
			httpContext.Request.Headers.Accept = acceptHeader;
		}

		return new ActionContext
		{
			HttpContext = httpContext,
			RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
			ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()
		};
	}

	private class FakeController : ControllerBase
	{
	}

	#endregion
}

public static class AsyncEnumerableExtensions
{
	public static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IEnumerable<T> source)
	{
		foreach (T item in source)
		{
			await Task.Yield();
			yield return item;
		}
	}
}

public sealed class MessagePackStreamingResultBehaviorTests
{
	private static readonly FieldInfo JValueTypeField = typeof(JValue).GetField("_valueType", BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static readonly FieldInfo JValueValueField = typeof(JValue).GetField("_value", BindingFlags.Instance | BindingFlags.NonPublic)!;
	private static readonly MethodInfo FillPatchForObjectMethod = typeof(PatchCreator).GetMethod("FillPatchForObject", BindingFlags.Static | BindingFlags.NonPublic)!;








	private static ServiceProvider CreateServices(MessagePackSerializerOptions options, bool addSupportedFormatter)
	{
		ServiceCollection services = new();
		MvcOptions mvcOptions = new();
		mvcOptions.OutputFormatters.Add(addSupportedFormatter
			? new FormatterWithOptions(options)
			: new FormatterWithOptions(options, supportsMessagePack: false));
		services.AddSingleton<IOptions<MvcOptions>>(Options.Create(mvcOptions));
		return services.BuildServiceProvider();
	}

	private static DefaultHttpContext CreateContext(string accept)
	{
		DefaultHttpContext context = new();
		context.Response.Body = new MemoryStream();
		context.Request.Headers.Accept = accept;
		context.Features.Set<IServiceProvidersFeature>(new RequestServicesFeature());
		return context;
	}

	private static JValue CreateArtificialDateValue(object value)
	{
		JValue result = new("seed");
		JValueTypeField.SetValue(result, JTokenType.Date);
		JValueValueField.SetValue(result, value);
		return result;
	}

	private static async IAsyncEnumerable<StreamingModel> AsyncItems(params StreamingModel[] items)
	{
		foreach (StreamingModel item in items)
		{
			await Task.Yield();
			yield return item;
		}
	}

	[MessagePackObject(AllowPrivate = true)]
	internal sealed class StreamingModel
	{
		[Key(0)]
		public int Id { get; set; }

		[Key(1)]
		public string Name { get; set; } = string.Empty;
	}

	private sealed class FormatterWithOptions : OutputFormatter
	{
		private readonly MessagePackSerializerOptions options;

		public FormatterWithOptions(MessagePackSerializerOptions options, bool supportsMessagePack = true)
		{
			this.options = options;
			SupportedMediaTypes.Add(supportsMessagePack ? "application/x-msgpack" : "application/xml");
		}

		public override Task WriteResponseBodyAsync(OutputFormatterWriteContext context) => Task.CompletedTask;
	}

	private sealed class FormatterWithoutOptions : OutputFormatter
	{
		public FormatterWithoutOptions()
		{
			SupportedMediaTypes.Add("application/x-msgpack");
		}

		public override Task WriteResponseBodyAsync(OutputFormatterWriteContext context) => Task.CompletedTask;
	}

	private sealed class RequestServicesFeature : IServiceProvidersFeature
	{
		public IServiceProvider RequestServices { get; set; } = null!;
	}

	private sealed class ConvertibleDateValue(DateTime value) : IConvertible
	{
		public TypeCode GetTypeCode() => TypeCode.Object;
		public bool ToBoolean(IFormatProvider? provider) => Convert.ToBoolean(value, provider);
		public byte ToByte(IFormatProvider? provider) => Convert.ToByte(value, provider);
		public char ToChar(IFormatProvider? provider) => Convert.ToChar(value, provider);
		public DateTime ToDateTime(IFormatProvider? provider) => value;
		public decimal ToDecimal(IFormatProvider? provider) => Convert.ToDecimal(value, provider);
		public double ToDouble(IFormatProvider? provider) => Convert.ToDouble(value, provider);
		public short ToInt16(IFormatProvider? provider) => Convert.ToInt16(value, provider);
		public int ToInt32(IFormatProvider? provider) => Convert.ToInt32(value, provider);
		public long ToInt64(IFormatProvider? provider) => Convert.ToInt64(value, provider);
		public sbyte ToSByte(IFormatProvider? provider) => Convert.ToSByte(value, provider);
		public float ToSingle(IFormatProvider? provider) => Convert.ToSingle(value, provider);
		public string ToString(IFormatProvider? provider) => value.ToString(provider);
		public object ToType(Type conversionType, IFormatProvider? provider) => Convert.ChangeType(value, conversionType, provider);
		public ushort ToUInt16(IFormatProvider? provider) => Convert.ToUInt16(value, provider);
		public uint ToUInt32(IFormatProvider? provider) => Convert.ToUInt32(value, provider);
		public ulong ToUInt64(IFormatProvider? provider) => Convert.ToUInt64(value, provider);
	}

	[Fact]
	public async Task MessagePackStreamingResult_UsesMvcFormatterOptions_ForExactPayload()
	{
		StreamingModel expected = new() { Id = 7, Name = "configured" };
		MessagePackSerializerOptions formatterOptions = MessagePackSerializerOptions.Standard
			.WithCompression(MessagePackCompression.Lz4Block);
		DefaultHttpContext context = CreateContext("application/x-msgpack");
		context.RequestServices = CreateServices(formatterOptions, addSupportedFormatter: true);
		MessagePackStreamingResult<StreamingModel> result = new(AsyncItems(expected));

		await result.ExecuteAsync(context);

		context.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
		context.Response.ContentType.ShouldBe("application/x-msgpack");
		byte[] payload = ((MemoryStream)context.Response.Body).ToArray();
		payload.ShouldBe(MessagePackSerializer.Serialize(expected, formatterOptions, Current.CancellationToken));
		MessagePackSerializer.Deserialize<StreamingModel>(payload, formatterOptions, Current.CancellationToken).ShouldBeEquivalentTo(expected);
	}

	[Fact]
	public async Task MessagePackStreamingResult_FallsBackToStandardOptions_WhenMvcOptionsAreAbsent()
	{
		StreamingModel expected = new() { Id = 3, Name = "standard" };
		DefaultHttpContext context = CreateContext("application/x-msgpack");
		context.RequestServices = new ServiceCollection().BuildServiceProvider();
		MessagePackStreamingResult<StreamingModel> result = new(AsyncItems(expected));

		await result.ExecuteAsync(context);

		((MemoryStream)context.Response.Body).ToArray()
			.ShouldBe(MessagePackSerializer.Serialize(expected, MessagePackSerializerOptions.Standard, Current.CancellationToken));
	}

	[Fact]
	public async Task MessagePackStreamingResult_FallsBackToStandardOptions_WhenFormatterIsUnsupported()
	{
		StreamingModel expected = new() { Id = 4, Name = "unsupported-formatter" };
		DefaultHttpContext context = CreateContext("application/msgpack");
		context.RequestServices = CreateServices(MessagePackSerializerOptions.Standard, addSupportedFormatter: false);
		MessagePackStreamingResult<StreamingModel> result = new(AsyncItems(expected));

		await result.ExecuteAsync(context);

		((MemoryStream)context.Response.Body).ToArray()
			.ShouldBe(MessagePackSerializer.Serialize(expected, MessagePackSerializerOptions.Standard, Current.CancellationToken));
	}

	[Fact]
	public async Task MessagePackStreamingResult_FallsBackToStandardOptions_WhenFormatterHasNoOptionsField()
	{
		StreamingModel expected = new() { Id = 5, Name = "no-options-field" };
		DefaultHttpContext context = CreateContext("application/x-msgpack");
		ServiceCollection services = new();
		MvcOptions mvcOptions = new();
		mvcOptions.OutputFormatters.Add(new FormatterWithoutOptions());
		services.AddSingleton<IOptions<MvcOptions>>(Options.Create(mvcOptions));
		context.RequestServices = services.BuildServiceProvider();
		MessagePackStreamingResult<StreamingModel> result = new(AsyncItems(expected));

		await result.ExecuteAsync(context);

		((MemoryStream)context.Response.Body).ToArray()
			.ShouldBe(MessagePackSerializer.Serialize(expected, MessagePackSerializerOptions.Standard, Current.CancellationToken));
	}

}
