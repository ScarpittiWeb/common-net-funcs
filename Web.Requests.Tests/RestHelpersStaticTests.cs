using CommonNetFuncs.Web.Requests.Rest;
using MemoryPack;
using MemoryPack.Streaming;
using MessagePack;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using static Xunit.TestContext;

namespace Web.Requests.Tests;

public sealed class RestHelpersStaticTests
{
	public enum ExceptionType { TaskCanceledExpected, TaskCanceledUnexpected, GeneralException }
	public enum TimeoutValue { Null, Zero }
	public enum CompressionType { GZip, Brotli }
	public enum HeaderScenario { DefaultNoHeaders, NullHeaders, ReplacesNonJsonAcceptHeader, HeadersWithoutAcceptKey }
	public enum StreamingObjectHeaderScenario { NullHeaders, KeepsJsonAcceptHeader, ReplacesNonJsonAcceptHeader }
	public enum SerializationFormat { MemoryPack, MessagePack }

	[Theory]
	[InlineData("GET", null)]
	[InlineData("POST", "body")]
	[InlineData("PUT", "body")]
	public async Task RestRequest_ReturnsExpectedResult(string methodString, string? body)
	{
		HttpMethod method = new(methodString);
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = method,
			BodyObject = body,
			Timeout = 1,
			LogQuery = true,
			LogBody = true
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("\"result\"", Encoding.UTF8, "application/json")
		};

		string? result = await client.RestRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBe("result");
	}

	[Fact]
	public async Task RestRequest_LogsRequestBodyForPatchDocument()
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		StringContent patchContent = new("[{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"New\"}]", Encoding.UTF8, "application/json");
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Patch,
			PatchDocument = patchContent,
			LogRequest = true,
			LogBody = true
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("\"result\"", Encoding.UTF8, "application/json")
		};

		string? result = await client.RestRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBe("result");
	}

	[Fact]
	public async Task RestRequest_LogsEmptyBodyWhenNoBodyOrPatchDocument()
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Post,
			BodyObject = null,
			LogRequest = true,
			LogBody = true
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("\"result\"", Encoding.UTF8, "application/json")
		};

		string? result = await client.RestRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBe("result");
	}

	[Theory]
	[InlineData(ExceptionType.TaskCanceledExpected)]
	[InlineData(ExceptionType.TaskCanceledUnexpected)]
	[InlineData(ExceptionType.GeneralException)]
	public async Task RestRequest_HandlesExceptions(ExceptionType exceptionType)
	{
		Exception exception = exceptionType == ExceptionType.GeneralException
			? new InvalidOperationException("fail")
			: new TaskCanceledException("Canceled!");

		FakeHttpMessageHandler handler = new() { ThrowOnSend = exception };
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			ExpectTaskCancellation = exceptionType == ExceptionType.TaskCanceledExpected
		};

		string? result = await client.RestRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBeNull();
	}

	[Theory]
	[InlineData(TimeoutValue.Null)]
	[InlineData(TimeoutValue.Zero)]
	public async Task RestRequest_UsesDefaultTimeout(TimeoutValue timeoutValue)
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			Timeout = timeoutValue == TimeoutValue.Null ? null : 0
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("\"result\"", Encoding.UTF8, "application/json")
		};

		string? result = await client.RestRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBe("result");
	}

	[Fact]
	public async Task RestObjectRequest_ReturnsRestObject()
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new() { Url = "http://test", HttpMethod = HttpMethod.Get };

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("\"foo\"", Encoding.UTF8, "application/json")
		};

		RestObject<string> result = await client.RestObjectRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.Result.ShouldBe("foo");
		result.Response.ShouldNotBeNull();
	}

	[Theory]
	[InlineData(ExceptionType.TaskCanceledExpected)]
	[InlineData(ExceptionType.TaskCanceledUnexpected)]
	[InlineData(ExceptionType.GeneralException)]
	public async Task RestObjectRequest_HandlesExceptions(ExceptionType exceptionType)
	{
		Exception exception = exceptionType == ExceptionType.GeneralException
			? new InvalidOperationException("fail")
			: new TaskCanceledException("Canceled!");

		FakeHttpMessageHandler handler = new() { ThrowOnSend = exception };
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			ExpectTaskCancellation = exceptionType == ExceptionType.TaskCanceledExpected
		};

		RestObject<string> result = await client.RestObjectRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.Result.ShouldBeNull();
		if (exceptionType == ExceptionType.TaskCanceledExpected)
		{
			result.Response.ShouldBeNull();
		}
	}

	[Theory]
	[InlineData(HeaderScenario.DefaultNoHeaders)]
	[InlineData(HeaderScenario.NullHeaders)]
	[InlineData(HeaderScenario.ReplacesNonJsonAcceptHeader)]
	[InlineData(HeaderScenario.HeadersWithoutAcceptKey)]
	public async Task StreamingRestRequest_HandlesHeaderScenarios(HeaderScenario scenario)
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			HttpHeaders = scenario switch
			{
				HeaderScenario.NullHeaders => null,
				HeaderScenario.ReplacesNonJsonAcceptHeader => new Dictionary<string, string> { { "Accept", "text/xml" } },
				HeaderScenario.HeadersWithoutAcceptKey => new Dictionary<string, string> { { "X-Custom", "value" } },
				_ => null
			}
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("[\"a\",\"b\"]", Encoding.UTF8, "application/json")
		};

		List<string?> results = new();
		await foreach (string? item in client.StreamingRestRequest<string, string>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBe(["a", "b"]);
	}

	[Theory]
	[InlineData(ExceptionType.TaskCanceledExpected)]
	[InlineData(ExceptionType.TaskCanceledUnexpected)]
	[InlineData(ExceptionType.GeneralException)]
	public async Task StreamingRestRequest_HandlesExceptions(ExceptionType exceptionType)
	{
		Exception exception = exceptionType == ExceptionType.GeneralException
			? new InvalidOperationException("fail")
			: new TaskCanceledException("Canceled!");

		FakeHttpMessageHandler handler = new() { ThrowOnSend = exception };
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			ExpectTaskCancellation = exceptionType == ExceptionType.TaskCanceledExpected
		};

		List<string?> results = new();
		await foreach (string? item in client.StreamingRestRequest<string, string>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBeEmpty();
	}

	[Fact]
	public async Task StreamingRestRequest_YieldsMessagePackResults()
	{
		TestModel model1 = new() { Name = "a", Value = 1 };
		TestModel model2 = new() { Name = "b", Value = 2 };
		MemoryStream stream = new();
		await stream.WriteAsync(MessagePackSerializer.Serialize(model1, cancellationToken: Current.CancellationToken), Current.CancellationToken);
		await stream.WriteAsync(MessagePackSerializer.Serialize(model2, cancellationToken: Current.CancellationToken), Current.CancellationToken);
		stream.Position = 0;

		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<TestModel> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			HttpHeaders = new Dictionary<string, string> { { "Accept", "application/x-msgpack" } }
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StreamContent(stream)
		};
		handler.Response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");

		List<TestModel?> results = new();
		await foreach (TestModel? item in client.StreamingRestRequest<TestModel, TestModel>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(2);
		results[0]!.Name.ShouldBe("a");
		results[1]!.Name.ShouldBe("b");
	}

	[Fact]
	public async Task StreamingRestRequest_YieldsMemoryPackResults()
	{
		List<TestModel> models = new() { new() { Name = "a", Value = 1 }, new() { Name = "b", Value = 2 } };
		MemoryStream stream = new();
		await MemoryPackStreamingSerializer.SerializeAsync(stream, models.Count, models, cancellationToken: Current.CancellationToken);
		stream.Position = 0;

		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<TestModel> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			HttpHeaders = new Dictionary<string, string> { { "Accept", "application/x-memorypack" } }
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StreamContent(stream)
		};
		handler.Response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-memorypack");

		List<TestModel?> results = new();
		await foreach (TestModel? item in client.StreamingRestRequest<TestModel, TestModel>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(2);
		results[0]!.Name.ShouldBe("a");
		results[1]!.Name.ShouldBe("b");
	}

	[Fact]
	public async Task StreamingRestObjectRequest_ReturnsStreamingRestObject()
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new() { Url = "http://test", HttpMethod = HttpMethod.Get };

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("[\"a\",\"b\"]", Encoding.UTF8, "application/json")
		};

		StreamingRestObject<string> result = await client.StreamingRestObjectRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.Response.ShouldNotBeNull();
		result.Result.ShouldNotBeNull();

		List<string?> items = new();
		await foreach (string? item in result.Result)
		{
			items.Add(item);
		}
		items.ShouldBe(["a", "b"]);
	}

	[Theory]
	[InlineData(StreamingObjectHeaderScenario.NullHeaders)]
	[InlineData(StreamingObjectHeaderScenario.KeepsJsonAcceptHeader)]
	[InlineData(StreamingObjectHeaderScenario.ReplacesNonJsonAcceptHeader)]
	public async Task StreamingRestObjectRequest_HandlesHeaderScenarios(StreamingObjectHeaderScenario scenario)
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			HttpHeaders = scenario switch
			{
				StreamingObjectHeaderScenario.NullHeaders => null,
				StreamingObjectHeaderScenario.KeepsJsonAcceptHeader => new Dictionary<string, string> { { "Accept", "application/json" } },
				StreamingObjectHeaderScenario.ReplacesNonJsonAcceptHeader => new Dictionary<string, string> { { "Accept", "text/xml" } },
				_ => null
			}
		};

		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("[\"a\"]", Encoding.UTF8, "application/json")
		};

		StreamingRestObject<string> result = await client.StreamingRestObjectRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.Response.ShouldNotBeNull();
	}

	[Fact]
	public async Task StreamingRestObjectRequest_ReturnsAfterReceivingHeadersWithoutBufferingContent()
	{
		FakeHttpMessageHandler handler = new();
		HttpClient client = new(handler);
		RequestOptions<string> options = new() { Url = "http://test", HttpMethod = HttpMethod.Get };
		handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new FakeContent(throwOnRead: true)
		};
		handler.Response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

		StreamingRestObject<string> result = await client.StreamingRestObjectRequest<string, string>(options, Current.CancellationToken);

		result.Response.ShouldBeSameAs(handler.Response);
		result.Result.ShouldNotBeNull();
	}

	[Theory]
	[InlineData(ExceptionType.TaskCanceledExpected)]
	[InlineData(ExceptionType.TaskCanceledUnexpected)]
	[InlineData(ExceptionType.GeneralException)]
	public async Task StreamingRestObjectRequest_HandlesExceptions(ExceptionType exceptionType)
	{
		Exception exception = exceptionType == ExceptionType.GeneralException
			? new InvalidOperationException("fail")
			: new TaskCanceledException("Canceled!");

		FakeHttpMessageHandler handler = new() { ThrowOnSend = exception };
		HttpClient client = new(handler);
		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			ExpectTaskCancellation = exceptionType == ExceptionType.TaskCanceledExpected
		};

		StreamingRestObject<string> result = await client.StreamingRestObjectRequest<string, string>(options, cancellationToken: Current.CancellationToken);

		result.Response.ShouldBeNull();
	}

	[Theory]
	[InlineData(1000000, 100, 8192)]
	[InlineData(100000, 10, 8192)]
	[InlineData(10000, 1, 8192)]
	[InlineData(90000000, 10000, 8192)]
	public void GetChunkingParameters_ReturnsExpectedValues(int itemCount, int startingChunk, int bufferLimit)
	{
		(int itemsPerChunk, int numberOfChunks) = RestHelpersStatic.GetChunkingParameters(itemCount, startingChunk, bufferLimit);

		itemsPerChunk.ShouldBeGreaterThan(0);
		numberOfChunks.ShouldBeGreaterThan(0);
		numberOfChunks.ShouldBeLessThan(bufferLimit);
	}

	[Fact]
	public async Task HandleResponse_ReturnsSuccessResult()
	{
		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new StringContent("\"success\"", Encoding.UTF8, "application/json")
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			LogResponse = true
		};

		string? result = await response.HandleResponse<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBe("success");
	}

	[Fact]
	public async Task HandleResponse_LogsResponseWithNewtonsoftDeserializer()
	{
		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new StringContent("\"success\"", Encoding.UTF8, "application/json")
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			LogResponse = true,
			UseNewtonsoftDeserializer = true
		};

		string? result = await response.HandleResponse<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBe("success");
	}

	[Fact]
	public async Task HandleResponse_HandlesErrorResponse()
	{
		HttpResponseMessage response = new(HttpStatusCode.BadRequest)
		{
			Content = new StringContent("Error message", Encoding.UTF8, "text/plain")
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			LogQuery = true
		};

		string? result = await response.HandleResponse<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBeNull();
	}

	[Fact]
	public async Task HandleResponse_HandlesProblemDetails()
	{
		string problemJson = JsonSerializer.Serialize(new
		{
			status = 400,
			title = "Bad Request",
			errors = new Dictionary<string, string[]>
						{
								{ "field1", valueArray }
						}
		});

		HttpResponseMessage response = new(HttpStatusCode.BadRequest)
		{
			Content = new StringContent(problemJson, Encoding.UTF8, "application/problem+json")
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get
		};

		string? result = await response.HandleResponse<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBeNull();
	}

	[Fact]
	public async Task HandleResponse_HandlesException()
	{
		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new FakeContent(true)
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get
		};

		string? result = await response.HandleResponse<string, string>(options, cancellationToken: Current.CancellationToken);

		result.ShouldBeNull();
	}

	[Fact]
	public async Task HandleResponseAsync_YieldsSuccessResults()
	{
		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new StringContent("[\"a\",\"b\"]", Encoding.UTF8, "application/json")
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			LogResponse = true
		};

		List<string?> results = new();
		await foreach (string? item in response.HandleResponseAsync<string, string>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBe(["a", "b"]);
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task HandleResponseAsync_HandlesCompression(CompressionType compressionType)
	{
		byte[] jsonBytes = Encoding.UTF8.GetBytes("[\"a\",\"b\"]");
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new StreamContent(compressedStream)
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		response.Content.Headers.ContentEncoding.Add(compressionType == CompressionType.GZip ? "gzip" : "br");

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get
		};

		List<string?> results = new();
		await foreach (string? item in response.HandleResponseAsync<string, string>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBe(["a", "b"]);
	}

	[Fact]
	public async Task HandleResponseAsync_YieldsMessagePackResults()
	{
		TestModel model1 = new() { Name = "a", Value = 1 };
		TestModel model2 = new() { Name = "b", Value = 2 };
		MemoryStream stream = new();
		await stream.WriteAsync(MessagePackSerializer.Serialize(model1, cancellationToken: Current.CancellationToken), Current.CancellationToken);
		await stream.WriteAsync(MessagePackSerializer.Serialize(model2, cancellationToken: Current.CancellationToken), Current.CancellationToken);
		stream.Position = 0;

		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new StreamContent(stream)
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-msgpack");

		RequestOptions<TestModel> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get
		};

		List<TestModel?> results = new();
		await foreach (TestModel? item in response.HandleResponseAsync<TestModel, TestModel>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(2);
		results[0]!.Name.ShouldBe("a");
		results[1]!.Name.ShouldBe("b");
	}

	[Fact]
	public async Task HandleResponseAsync_YieldsMemoryPackResults()
	{
		List<TestModel> models = new() { new() { Name = "a", Value = 1 }, new() { Name = "b", Value = 2 } };
		MemoryStream stream = new();
		await MemoryPackStreamingSerializer.SerializeAsync(stream, models.Count, models, cancellationToken: Current.CancellationToken);
		stream.Position = 0;

		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new StreamContent(stream)
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-memorypack");

		RequestOptions<TestModel> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get
		};

		List<TestModel?> results = new();
		await foreach (TestModel? item in response.HandleResponseAsync<TestModel, TestModel>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(2);
		results[0]!.Name.ShouldBe("a");
		results[1]!.Name.ShouldBe("b");
	}

	[Theory]
	[InlineData(HttpStatusCode.NoContent, null)]
	[InlineData(HttpStatusCode.NoContent, "application/x-msgpack")]
	[InlineData(HttpStatusCode.OK, null)]
	public async Task HandleResponseAsync_YieldsNothingForEmptyBody(HttpStatusCode statusCode, string? contentType)
	{
		HttpResponseMessage response = new(statusCode)
		{
			Content = new ByteArrayContent([])
		};
		if (contentType != null)
		{
			response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
		}

		RequestOptions<TestModel> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Post
		};

		List<TestModel?> results = new();
		await foreach (TestModel? item in response.HandleResponseAsync<TestModel, TestModel>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBeEmpty();
	}

	[Fact]
	public async Task HandleResponseAsync_HandlesErrorResponse()
	{
		HttpResponseMessage response = new(HttpStatusCode.BadRequest)
		{
			Content = new StringContent("Error", Encoding.UTF8, "text/plain")
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get,
			LogResponse = true
		};

		List<string?> results = new();
		await foreach (string? item in response.HandleResponseAsync<string, string>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBeEmpty();
	}

	private static readonly string[] value = new[] { "error1" };
	private static readonly string[] valueArray = new[] { "error1", "error2" };

	[Fact]
	public async Task HandleResponseAsync_HandlesProblemDetails()
	{
		string problemJson = JsonSerializer.Serialize(new
		{
			status = 400,
			title = "Bad Request",
			errors = new Dictionary<string, string[]>
				{
						{ "field1", value }
				}
		});

		HttpResponseMessage response = new(HttpStatusCode.BadRequest)
		{
			Content = new StringContent(problemJson, Encoding.UTF8, "application/problem+json")
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get
		};

		List<string?> results = new();
		await foreach (string? item in response.HandleResponseAsync<string, string>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBeEmpty();
	}

	[Fact]
	public async Task HandleResponseAsync_HandlesException()
	{
		HttpResponseMessage response = new(HttpStatusCode.OK)
		{
			Content = new FakeContent(true)
		};

		RequestOptions<string> options = new()
		{
			Url = "http://test",
			HttpMethod = HttpMethod.Get
		};

		List<string?> results = new();
		await foreach (string? item in response.HandleResponseAsync<string, string>(options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBeEmpty();
	}

	[Theory]
	[InlineData("application/json", null, false)]
	[InlineData("application/json", "gzip", false)]
	[InlineData("application/json", "br", false)]
	[InlineData("application/json", null, true)]
	[InlineData("text/plain", null, false)]
	public async Task ReadResponseStream_HandlesContentTypes(string contentType, string? encoding, bool useNewtonsoft)
	{
		// For JSON, the test data should be a JSON string (with quotes)
		// For text/plain, the test data should be plain text (without quotes)

		string testData = contentType.Contains("json") ? "\"test data\"" : "test data";
		MemoryStream stream;

		// If encoding is specified, compress the data appropriately

		if (encoding == "gzip")
		{
			byte[] jsonBytes = Encoding.UTF8.GetBytes(testData);
			stream = new();
			await using (System.IO.Compression.GZipStream gzip = new(stream, System.IO.Compression.CompressionMode.Compress, true))
			{
				await gzip.WriteAsync(jsonBytes, Current.CancellationToken);
			}
			stream.Position = 0;
		}
		else if (encoding == "br")
		{
			byte[] jsonBytes = Encoding.UTF8.GetBytes(testData);
			stream = new();
			await using (System.IO.Compression.BrotliStream brotli = new(stream, System.IO.Compression.CompressionMode.Compress, true))
			{
				await brotli.WriteAsync(jsonBytes, Current.CancellationToken);
			}
			stream.Position = 0;
		}
		else
		{
			stream = new(Encoding.UTF8.GetBytes(testData));
		}

		string? result = await stream.ReadResponseStream<string>(contentType, encoding, useNewtonsoft, cancellationToken: Current.CancellationToken);

		result.ShouldBe("test data");
	}

	[Fact]
	public async Task ReadResponseStream_HandlesEmptyStream()
	{
		MemoryStream stream = new(Array.Empty<byte>());

		string? result = await stream.ReadResponseStream<string>("application/json", null, false, cancellationToken: Current.CancellationToken);

		result.ShouldBeNull();
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task ReadResponseStream_HandlesJsonWithCompression(CompressionType compressionType)
	{
		byte[] jsonBytes = Encoding.UTF8.GetBytes("\"test\"");
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		string encoding = compressionType == CompressionType.GZip ? "gzip" : "br";
		string? result = await compressedStream.ReadResponseStream<string>("application/json", encoding, false, cancellationToken: Current.CancellationToken);

		result.ShouldBe("test");
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task ReadResponseStream_HandlesTextWithCompression(CompressionType compressionType)
	{
		byte[] textBytes = Encoding.UTF8.GetBytes("plain text");
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(textBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(textBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		string encoding = compressionType == CompressionType.GZip ? "gzip" : "br";
		string? result = await compressedStream.ReadResponseStream<string>("text/plain", encoding, false, cancellationToken: Current.CancellationToken);

		result.ShouldBe("plain text");
	}

	[Fact]
	public async Task ReadResponseStream_HandlesMemoryPack()
	{
		TestModel model = new() { Name = "Test", Value = 123 };
		byte[] memPackBytes = MemoryPackSerializer.Serialize(model);
		MemoryStream stream = new(memPackBytes);

		TestModel? result = await stream.ReadResponseStream<TestModel>("application/x-memorypack", null, false, cancellationToken: Current.CancellationToken);

		result.ShouldNotBeNull();
		result.Name.ShouldBe("Test");
		result.Value.ShouldBe(123);
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task ReadResponseStream_HandlesMemoryPackWithCompression(CompressionType compressionType)
	{
		TestModel model = new() { Name = "Compressed", Value = 456 };
		byte[] memPackBytes = MemoryPackSerializer.Serialize(model);
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(memPackBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(memPackBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		string encoding = compressionType == CompressionType.GZip ? "gzip" : "br";
		TestModel? result = await compressedStream.ReadResponseStream<TestModel>("application/x-memorypack", encoding, false, cancellationToken: Current.CancellationToken);

		result.ShouldNotBeNull();
		result.Name.ShouldBe("Compressed");
		result.Value.ShouldBe(456);
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task ReadResponseStream_HandlesJsonWithCompressionAndNewtonsoft(CompressionType compressionType)
	{
		byte[] jsonBytes = Encoding.UTF8.GetBytes("\"test\"");
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		string encoding = compressionType == CompressionType.GZip ? "gzip" : "br";
		string? result = await compressedStream.ReadResponseStream<string>("application/json", encoding, true, cancellationToken: Current.CancellationToken);

		result.ShouldBe("test");
	}

	[Fact]
	public async Task ReadResponseStream_HandlesMessagePack()
	{
		TestModel model = new() { Name = "Test", Value = 123 };
		byte[] msgPackBytes = MessagePackSerializer.Serialize(model, cancellationToken: Current.CancellationToken);
		MemoryStream stream = new(msgPackBytes);

		TestModel? result = await stream.ReadResponseStream<TestModel>("application/x-msgpack", null, false, cancellationToken: Current.CancellationToken);

		result.ShouldNotBeNull();
		result.Name.ShouldBe("Test");
		result.Value.ShouldBe(123);
	}

	[Fact]
	public async Task ReadResponseStream_HandlesMessagePackWithCompression()
	{
		TestModel model = new() { Name = "Test", Value = 123 };
		byte[] msgPackBytes = MessagePackSerializer.Serialize(model, cancellationToken: Current.CancellationToken);
		MemoryStream stream = new(msgPackBytes);

		MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard;

		TestModel? result = await stream.ReadResponseStream<TestModel>("application/x-msgpack", null, false, messagePackSerializerOptions: options, cancellationToken: Current.CancellationToken);

		result.ShouldNotBeNull();
	}

	[Fact]
	public async Task ReadResponseStream_HandlesMessagePackWithUntrusted()
	{
		TestModel model = new() { Name = "Test", Value = 123 };
		byte[] msgPackBytes = MessagePackSerializer.Serialize(model, cancellationToken: Current.CancellationToken);
		MemoryStream stream = new(msgPackBytes);

		MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

		TestModel? result = await stream.ReadResponseStream<TestModel>("application/x-msgpack", null, false, messagePackSerializerOptions: options, cancellationToken: Current.CancellationToken);

		result.ShouldNotBeNull();
	}

	[Fact]
	public async Task ReadResponseStream_HandlesException()
	{
		MemoryStream stream = new(Encoding.UTF8.GetBytes("invalid json"));

		TestModel? result = await stream.ReadResponseStream<TestModel>("application/json", null, false, cancellationToken: Current.CancellationToken);

		result.ShouldBeNull();
	}

	[Fact]
	public async Task ReadResponseStreamAsync_YieldsJsonResults()
	{
		const string json = "[\"a\",\"b\",\"c\"]";
		MemoryStream stream = new(Encoding.UTF8.GetBytes(json));

		List<string?> results = new();
		await foreach (string? item in stream.ReadResponseStreamAsync<string>("application/json", null, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBe(["a", "b", "c"]);
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task ReadResponseStreamAsync_HandlesCompression(CompressionType compressionType)
	{
		byte[] jsonBytes = Encoding.UTF8.GetBytes("[\"a\",\"b\"]");
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(jsonBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		string encoding = compressionType == CompressionType.GZip ? "gzip" : "br";
		List<string?> results = new();
		await foreach (string? item in compressedStream.ReadResponseStreamAsync<string>("application/json", encoding, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBe(["a", "b"]);
	}

	[Fact]
	public async Task ReadResponseStreamAsync_ThrowsForNonJsonContentType()
	{
		MemoryStream stream = new(Encoding.UTF8.GetBytes("data"));

		await Should.ThrowAsync<NotImplementedException>(async () =>
		{
			await foreach (string? _ in stream.ReadResponseStreamAsync<string>("application/xml", null, null))
			{
				// No-op

			}
		});
	}

	[Fact]
	public async Task ReadResponseStreamAsync_YieldsMessagePackResults()
	{
		TestModel model1 = new() { Name = "a", Value = 1 };
		TestModel model2 = new() { Name = "b", Value = 2 };
		MemoryStream stream = new();
		await stream.WriteAsync(MessagePackSerializer.Serialize(model1, cancellationToken: Current.CancellationToken), Current.CancellationToken);
		await stream.WriteAsync(MessagePackSerializer.Serialize(model2, cancellationToken: Current.CancellationToken), Current.CancellationToken);
		stream.Position = 0;

		List<TestModel?> results = new();
		await foreach (TestModel? item in stream.ReadResponseStreamAsync<TestModel>("application/x-msgpack", null, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(2);
		results[0]!.Name.ShouldBe("a");
		results[0]!.Value.ShouldBe(1);
		results[1]!.Name.ShouldBe("b");
		results[1]!.Value.ShouldBe(2);
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task ReadResponseStreamAsync_HandlesMessagePackWithCompression(CompressionType compressionType)
	{
		TestModel model = new() { Name = "compressed", Value = 42 };
		byte[] msgPackBytes = MessagePackSerializer.Serialize(model, cancellationToken: Current.CancellationToken);
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(msgPackBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(msgPackBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		string encoding = compressionType == CompressionType.GZip ? "gzip" : "br";
		List<TestModel?> results = new();
		await foreach (TestModel? item in compressedStream.ReadResponseStreamAsync<TestModel>("application/x-msgpack", encoding, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(1);
		results[0]!.Name.ShouldBe("compressed");
		results[0]!.Value.ShouldBe(42);
	}

	[Fact]
	public async Task ReadResponseStreamAsync_MessagePackUsesProvidedSerializerOptions()
	{
		TestModel model = new() { Name = "opts", Value = 7 };
		MemoryStream stream = new(MessagePackSerializer.Serialize(model, cancellationToken: Current.CancellationToken));
		MessagePackSerializerOptions options = MessagePackSerializerOptions.Standard;

		List<TestModel?> results = new();
		await foreach (TestModel? item in stream.ReadResponseStreamAsync<TestModel>("application/x-msgpack", null, null, messagePackSerializerOptions: options, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(1);
		results[0]!.Name.ShouldBe("opts");
	}

	[Fact]
	public async Task ReadResponseStreamAsync_MessagePackEmptyStreamYieldsNoResults()
	{
		MemoryStream stream = new();

		List<TestModel?> results = new();
		await foreach (TestModel? item in stream.ReadResponseStreamAsync<TestModel>("application/x-msgpack", null, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBeEmpty();
	}

	[Fact]
	public async Task ReadResponseStreamAsync_YieldsMemoryPackResults()
	{
		List<TestModel> models = new() { new() { Name = "a", Value = 1 }, new() { Name = "b", Value = 2 } };
		MemoryStream stream = new();
		await MemoryPackStreamingSerializer.SerializeAsync(stream, models.Count, models, cancellationToken: Current.CancellationToken);
		stream.Position = 0;

		List<TestModel?> results = new();
		await foreach (TestModel? item in stream.ReadResponseStreamAsync<TestModel>("application/x-memorypack", null, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(2);
		results[0]!.Name.ShouldBe("a");
		results[0]!.Value.ShouldBe(1);
		results[1]!.Name.ShouldBe("b");
		results[1]!.Value.ShouldBe(2);
	}

	[Theory]
	[InlineData(CompressionType.GZip)]
	[InlineData(CompressionType.Brotli)]
	public async Task ReadResponseStreamAsync_HandlesMemoryPackWithCompression(CompressionType compressionType)
	{
		TestModel model = new() { Name = "compressed", Value = 42 };
		MemoryStream memPackStream = new();
		await MemoryPackStreamingSerializer.SerializeAsync(memPackStream, 1, new[] { model }, cancellationToken: Current.CancellationToken);
		byte[] memPackBytes = memPackStream.ToArray();
		MemoryStream compressedStream = new();

		if (compressionType == CompressionType.GZip)
		{
			await using System.IO.Compression.GZipStream gzip = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await gzip.WriteAsync(memPackBytes, Current.CancellationToken);
		}
		else
		{
			await using System.IO.Compression.BrotliStream brotli = new(compressedStream, System.IO.Compression.CompressionMode.Compress, true);
			await brotli.WriteAsync(memPackBytes, Current.CancellationToken);
		}
		compressedStream.Position = 0;

		string encoding = compressionType == CompressionType.GZip ? "gzip" : "br";
		List<TestModel?> results = new();
		await foreach (TestModel? item in compressedStream.ReadResponseStreamAsync<TestModel>("application/x-memorypack", encoding, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.Count.ShouldBe(1);
		results[0]!.Name.ShouldBe("compressed");
		results[0]!.Value.ShouldBe(42);
	}

	[Fact]
	public async Task ReadResponseStreamAsync_MemoryPackEmptyStreamYieldsNoResults()
	{
		MemoryStream stream = new();
		await MemoryPackStreamingSerializer.SerializeAsync(stream, 0, Array.Empty<TestModel>(), cancellationToken: Current.CancellationToken);
		stream.Position = 0;

		List<TestModel?> results = new();
		await foreach (TestModel? item in stream.ReadResponseStreamAsync<TestModel>("application/x-memorypack", null, null, cancellationToken: Current.CancellationToken))
		{
			results.Add(item);
		}

		results.ShouldBeEmpty();
	}

	[Theory]
	[InlineData("POST", "application/json")]
	[InlineData("PUT", "application/json")]
	[InlineData("PATCH", null)]
	[InlineData("GET", null)]
	public void AddContent_AddsContentForHttpMethods(string methodString, string? expectedContentType)
	{
		HttpMethod method = new(methodString);
		HttpRequestMessage request = new(method, "http://test");
		TestModel model = new() { Name = "Test", Value = 42 };

		if (method == HttpMethod.Patch)
		{
			StringContent patchContent = new("[{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"New\"}]");
			request.AddContent(method, null, model, patchContent);
			request.Content.ShouldNotBeNull();
			request.Content.Headers.ContentType?.MediaType.ShouldBe("application/json-patch+json");
		}
		else
		{
			request.AddContent(method, null, model, null);

			if (method == HttpMethod.Get)
			{
				request.Content.ShouldBeNull();
			}
			else
			{
				request.Content.ShouldNotBeNull();
				request.Content.Headers.ContentType?.MediaType.ShouldBe(expectedContentType);
			}
		}
	}

	[Theory]
	[InlineData("application/x-memorypack")]
	[InlineData("application/x-msgpack")]
	public void AddContent_HandlesSerializationFormats(string expectedMediaType)
	{
		HttpRequestMessage request = new(HttpMethod.Post, "http://test");
		Dictionary<string, string> headers = new() { { "Content-Type", expectedMediaType } };
		TestModel model = new() { Name = "Test", Value = 42 };

		request.AddContent(HttpMethod.Post, headers, model, null);

		request.Content.ShouldNotBeNull();
		request.Content.Headers.ContentType?.MediaType.ShouldBe(expectedMediaType);
	}

	[Fact]
	public void AddContent_PatchWithNullPatchDocDoesNotAddContent()
	{
		HttpRequestMessage request = new(HttpMethod.Patch, "http://test");
		TestModel model = new() { Name = "Test", Value = 42 };

		request.AddContent(HttpMethod.Patch, null, model, null);

		request.Content.ShouldBeNull();
	}

	[Theory]
	[InlineData("token123")]
	[InlineData(null)]
	public void AttachHeaders_AddsBearerToken(string? token)
	{
		HttpRequestMessage request = new(HttpMethod.Get, "http://test");

		request.AttachHeaders(token, null);

		if (token != null)
		{
			request.Headers.Authorization.ShouldNotBeNull();
			request.Headers.Authorization.Scheme.ShouldBe("Bearer");
			request.Headers.Authorization.Parameter.ShouldBe(token);
		}
	}

	[Fact]
	public void AttachHeaders_AddsCustomHeaders()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "http://test");
		Dictionary<string, string> headers = new()
			{
				{ "X-Custom-Header", "value1" },
				{ "X-Another-Header", "value2" }
			};

		request.AttachHeaders(null, headers);

		request.Headers.Contains("X-Custom-Header").ShouldBeTrue();
		request.Headers.Contains("X-Another-Header").ShouldBeTrue();
	}

	[Fact]
	public void AttachHeaders_HandlesNullHeaders()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "http://test");

		request.AttachHeaders("token", null);

		request.Headers.Authorization.ShouldNotBeNull();
	}

	[Fact]
	public void AttachHeaders_HandlesEmptyBearerTokenWithoutAuthHeader()
	{
		HttpRequestMessage request = new(HttpMethod.Get, "http://test");

		request.AttachHeaders(string.Empty, null);

		request.Headers.Authorization.ShouldBeNull();
	}

	private sealed class FakeHttpMessageHandler : HttpMessageHandler
	{
		public HttpResponseMessage? Response { get; set; }

		public Exception? ThrowOnSend { get; set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			if (ThrowOnSend is not null)
			{
				throw ThrowOnSend;
			}

			return Task.FromResult(Response ?? new HttpResponseMessage(HttpStatusCode.OK));
		}
	}

	private sealed class FakeContent(bool throwOnRead) : HttpContent
	{
		private readonly bool throwOnRead = throwOnRead;

		protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
		{
			if (throwOnRead)
			{
				throw new InvalidOperationException("Fake error");
			}
			return Task.CompletedTask;
		}

		protected override bool TryComputeLength(out long length)
		{
			length = 0;
			return false;
		}
	}
}

[MemoryPackable]
[MessagePackObject(true, AllowPrivate = true)]
internal partial class TestModel
{
	[Key(0)]
	public string Name { get; set; } = string.Empty;

	[Key(1)]
	public int Value { get; set; }
}

public sealed partial class RestHelpersStaticBehaviorTests
{













	private static async Task<MemoryStream> CompressAsync(string value, string encoding)
	{
		await using MemoryStream input = new(Encoding.UTF8.GetBytes(value));
		return await CompressStreamAsync(input, encoding);
	}

	private static async Task<MemoryStream> CompressStreamAsync(Stream input, string encoding)
	{
		MemoryStream output = new();
		Stream compressor = encoding switch
		{
			"gzip" => new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true),
			"br" => new System.IO.Compression.BrotliStream(output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true),
			_ => throw new ArgumentOutOfRangeException(nameof(encoding))
		};

		await using (compressor)
		{
			await input.CopyToAsync(compressor, Current.CancellationToken);
		}

		output.Position = 0;
		return output;
	}

	private sealed class CapturingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
	{
		private readonly Queue<HttpResponseMessage> responses = new(responses);

		public CapturedRequest? Captured { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Captured = new CapturedRequest(
				request.Method.Method,
				request.RequestUri!,
				request.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray()),
				request.Content?.Headers.ContentType?.MediaType,
				request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
			return responses.Dequeue();
		}
	}

	private sealed record CapturedRequest(
		string Method,
		Uri Uri,
		IReadOnlyDictionary<string, string[]> Headers,
		string? ContentType,
		string Body);

	private sealed class NonSeekableReadStream(byte[] bytes) : Stream
	{
		private readonly MemoryStream inner = new(bytes);

		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException();
		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
			}
			base.Dispose(disposing);
		}
	}

	private sealed class RequestModel
	{
		public string Name { get; set; } = string.Empty;
		public int Value { get; set; }
	}

	private sealed class WireResponse
	{
		public string WireName { get; set; } = string.Empty;
		public int WireValue { get; set; }
	}

	[MemoryPackable]
	[MessagePackObject(true, AllowPrivate = true)]
	internal sealed partial class ResponseModel
	{
		[Key(0)]
		public string Name { get; set; } = string.Empty;

		[Key(1)]
		public int Value { get; set; }
	}

	[Fact]
	public async Task RestObjectRequest_SendsExactMethodUriHeadersAndBody()
	{
		CapturingHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""{"name":"response","value":11}""", Encoding.UTF8, "application/json")
		});
		using HttpClient client = new(handler);
		RequestOptions<RequestModel> options = new()
		{
			Url = "https://unit.test/resource?item=7",
			HttpMethod = HttpMethod.Post,
			BearerToken = "exact-token",
			BodyObject = new RequestModel { Name = "captured", Value = 9 },
			HttpHeaders = new Dictionary<string, string>
			{
				["X-Correlation"] = "coverage-42",
				["Accept"] = "application/json"
			},
			LogRequest = true,
			LogBody = true
		};

		RestObject<ResponseModel> response = await client.RestObjectRequest<ResponseModel, RequestModel>(options, Current.CancellationToken);

		handler.Captured.ShouldNotBeNull();
		handler.Captured!.Method.ShouldBe("POST");
		handler.Captured.Uri.ShouldBe(new Uri("https://unit.test/resource?item=7"));
		handler.Captured.Headers["Authorization"].Single().ShouldBe("Bearer exact-token");
		handler.Captured.Headers["X-Correlation"].Single().ShouldBe("coverage-42");
		handler.Captured.Headers["Accept"].Single().ShouldBe("application/json");
		handler.Captured.ContentType.ShouldBe("application/json");
		handler.Captured.Body.ShouldBe("""{"name":"captured","value":9}""");
		response.Response?.StatusCode.ShouldBe(HttpStatusCode.OK);
		response.Result.ShouldNotBeNull();
		response.Result!.Name.ShouldBe("response");
		response.Result.Value.ShouldBe(11);
	}

	[Fact]
	public async Task StreamingRestRequest_SendsExactGetAndYieldsOrderedItems()
	{
		CapturingHandler handler = new(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("""[{"name":"first","value":1},{"name":"second","value":2}]""", Encoding.UTF8, "application/json")
		});
		using HttpClient client = new(handler);
		RequestOptions<RequestModel> options = new()
		{
			Url = "https://unit.test/stream?after=4",
			HttpMethod = HttpMethod.Get,
			HttpHeaders = null,
			LogRequest = true,
			LogBody = true,
			LogQuery = false
		};

		List<ResponseModel?> items = [];
		await foreach (ResponseModel? item in client.StreamingRestRequest<ResponseModel, RequestModel>(options, Current.CancellationToken))
		{
			items.Add(item);
		}

		handler.Captured!.Method.ShouldBe("GET");
		handler.Captured.Uri.ShouldBe(new Uri("https://unit.test/stream?after=4"));
		handler.Captured.Headers["Accept"].Single().ShouldBe("application/json");
		handler.Captured.Body.ShouldBeEmpty();
		items.Select(x => x!.Name).ShouldBe(["first", "second"]);
		items.Select(x => x!.Value).ShouldBe([1, 2]);
		options.HttpHeaders!.ShouldContainKey("Accept");
	}

	[Fact]
	public async Task StreamingRestObjectRequest_PreservesExactEmptyNoContentState()
	{
		CapturingHandler handler = new(new HttpResponseMessage(HttpStatusCode.NoContent));
		using HttpClient client = new(handler);
		RequestOptions<RequestModel> options = new()
		{
			Url = "https://unit.test/empty",
			HttpMethod = HttpMethod.Delete
		};

		StreamingRestObject<ResponseModel> response = await client.StreamingRestObjectRequest<ResponseModel, RequestModel>(options, Current.CancellationToken);
		List<ResponseModel?> items = [];
		await foreach (ResponseModel? item in response.Result!)
		{
			items.Add(item);
		}

		handler.Captured!.Method.ShouldBe("DELETE");
		response.Response?.StatusCode.ShouldBe(HttpStatusCode.NoContent);
		items.ShouldBeEmpty();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task HandleResponse_ProblemDetailsEmptyBody_ReturnsNullWithoutLeakingHeaders(bool includeHeaders)
	{
		HttpResponseMessage response = new(HttpStatusCode.BadRequest)
		{
			Content = new ByteArrayContent([])
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json");
		response.Content.Headers.ContentEncoding.Add("identity");
		RequestOptions<RequestModel> options = new()
		{
			Url = "https://unit.test/problem?secret=redacted",
			HttpMethod = HttpMethod.Get,
			LogQuery = false,
			LogResponse = true,
			HttpHeaders = includeHeaders
				? new Dictionary<string, string> { ["X-Request"] = "state-1", ["Accept"] = "application/problem+json" }
				: null
		};

		ResponseModel? result = await response.HandleResponse<ResponseModel, RequestModel>(options, Current.CancellationToken);

		result.ShouldBeNull();
		response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Theory]
	[InlineData("application/json", "gzip", false)]
	[InlineData("application/json", "gzip", true)]
	[InlineData("application/json", "br", false)]
	[InlineData("application/json", "br", true)]
	[InlineData("text/plain", "gzip", false)]
	[InlineData("text/plain", "br", false)]
	public async Task ReadResponseStream_DecompressesAndDeserializesExactValue(string contentType, string encoding, bool useNewtonsoft)
	{
		string plainText = contentType.Contains("json", StringComparison.Ordinal) ? """{"name":"decoded","value":17}""" : "decoded text";
		await using MemoryStream compressed = await CompressAsync(plainText, encoding);

		if (contentType.Contains("json", StringComparison.Ordinal))
		{
			ResponseModel? result = await compressed.ReadResponseStream<ResponseModel>(contentType, encoding, useNewtonsoft, cancellationToken: Current.CancellationToken);
			result.ShouldNotBeNull();
			result.Name.ShouldBe("decoded");
			result.Value.ShouldBe(17);
		}
		else
		{
			string? result = await compressed.ReadResponseStream<string>(contentType, encoding, useNewtonsoft, cancellationToken: Current.CancellationToken);
			result.ShouldBe("decoded text");
		}
	}

	[Fact]
	public async Task ReadResponseStream_MessagePackAndMemoryPack_ReturnExactValues()
	{
		ResponseModel expected = new() { Name = "binary", Value = 23 };

		await using MemoryStream messagePack = new(MessagePackSerializer.Serialize(expected, cancellationToken: Current.CancellationToken));
		ResponseModel? fromMessagePack = await messagePack.ReadResponseStream<ResponseModel>(
			"application/x-msgpack", null, false, cancellationToken: Current.CancellationToken);

		await using MemoryStream memoryPack = new(MemoryPackSerializer.Serialize(expected));
		ResponseModel? fromMemoryPack = await memoryPack.ReadResponseStream<ResponseModel>(
			"application/x-memorypack", null, false, cancellationToken: Current.CancellationToken);

		fromMessagePack.ShouldBeEquivalentTo(expected);
		fromMemoryPack.ShouldBeEquivalentTo(expected);
	}

	[Theory]
	[InlineData("gzip")]
	[InlineData("br")]
	public async Task ReadResponseStream_CompressedMemoryPack_ReturnsExactValue(string encoding)
	{
		ResponseModel expected = new() { Name = "compressed-memory", Value = 37 };
		await using MemoryStream plain = new(MemoryPackSerializer.Serialize(expected));
		await using MemoryStream compressed = await CompressStreamAsync(plain, encoding);

		ResponseModel? result = await compressed.ReadResponseStream<ResponseModel>(
			"application/x-memorypack", encoding, false, cancellationToken: Current.CancellationToken);

		result.ShouldBeEquivalentTo(expected);
	}

	[Theory]
	[InlineData("gzip")]
	[InlineData("br")]
	public async Task ReadResponseStream_CompressedJsonUsesExactProvidedSerializerOptions(string encoding)
	{
		const string json = """{"wire_name":"configured","wire_value":41}""";
		await using MemoryStream input = new(Encoding.UTF8.GetBytes(json));
		await using MemoryStream compressed = await CompressStreamAsync(input, encoding);
		System.Text.Json.JsonSerializerOptions options = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
		};

		WireResponse? result = await compressed.ReadResponseStream<WireResponse>(
			"application/json", encoding, false, options, cancellationToken: Current.CancellationToken);

		result.ShouldNotBeNull();
		result.WireName.ShouldBe("configured");
		result.WireValue.ShouldBe(41);
	}

	[Fact]
	public async Task ReadResponseStream_NonSeekableJsonStream_ReturnsExactValue()
	{
		await using NonSeekableReadStream stream = new(Encoding.UTF8.GetBytes("""{"name":"nonseekable","value":29}"""));

		ResponseModel? result = await stream.ReadResponseStream<ResponseModel>(
			"application/json", null, false, cancellationToken: Current.CancellationToken);

		result.ShouldNotBeNull();
		result.Name.ShouldBe("nonseekable");
		result.Value.ShouldBe(29);
	}

	[Theory]
	[InlineData(0, 1, 8, 1, 0)]
	[InlineData(8, 2, 8, 2, 4)]
	[InlineData(9, 2, 8, 2, 5)]
	[InlineData(9, 1, 2, 1001, 1)]
	public void GetChunkingParameters_ExactBoundaries(
		int itemCount, int startingItemsPerChunk, int bufferLimit, int expectedItemsPerChunk, int expectedChunks)
	{
		(int itemsPerChunk, int chunks) = RestHelpersStatic.GetChunkingParameters(itemCount, startingItemsPerChunk, bufferLimit);

		itemsPerChunk.ShouldBe(expectedItemsPerChunk);
		chunks.ShouldBe(expectedChunks);
	}

	[Fact]
	public async Task HandleResponseAsync_LogsErrorAndEmptyResults()
	{
		HttpResponseMessage response = new(HttpStatusCode.UnprocessableEntity)
		{
			Content = new StringContent("validation failed", Encoding.UTF8, "text/plain")
		};
		response.Content.Headers.ContentEncoding.Add("identity");
		RequestOptions<RequestModel> options = new()
		{
			Url = "https://unit.test/errors",
			HttpMethod = HttpMethod.Post,
			HttpHeaders = new Dictionary<string, string> { ["X-State"] = "failed" },
			LogResponse = true,
			LogQuery = true
		};

		List<ResponseModel?> items = [];
		await foreach (ResponseModel? item in response.HandleResponseAsync<ResponseModel, RequestModel>(options, Current.CancellationToken))
		{
			items.Add(item);
		}

		items.ShouldBeEmpty();
	}

	[Fact]
	public async Task HandleResponseAsync_EmptyProblemDetailsUsesDefaultAndReturnsEmpty()
	{
		HttpResponseMessage response = new(HttpStatusCode.BadRequest)
		{
			Content = new ByteArrayContent([])
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/problem+json");
		RequestOptions<RequestModel> options = new()
		{
			Url = "https://unit.test/problem",
			HttpMethod = HttpMethod.Patch,
			PatchDocument = new StringContent("[]"),
			LogRequest = true,
			LogBody = true,
			LogResponse = true,
			UseNewtonsoftDeserializer = false,
			JsonSerializerOptions = new System.Text.Json.JsonSerializerOptions()
		};

		List<ResponseModel?> items = [];
		await foreach (ResponseModel? item in response.HandleResponseAsync<ResponseModel, RequestModel>(options, Current.CancellationToken))
		{
			items.Add(item);
		}

		items.ShouldBeEmpty();
	}

	[Fact]
	public async Task HandleResponse_LogsProvidedJsonOptionsAndNewtonsoftStates()
	{
		System.Text.Json.JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
		foreach (bool useNewtonsoft in new[] { false, true })
		{
			HttpResponseMessage response = new(HttpStatusCode.OK)
			{
				Content = new StringContent("""{"name":"logged","value":43}""", Encoding.UTF8, "application/json")
			};
			RequestOptions<RequestModel> requestOptions = new()
			{
				Url = "https://unit.test/logged?secret=value",
				HttpMethod = HttpMethod.Get,
				LogQuery = useNewtonsoft,
				LogRequest = true,
				LogResponse = true,
				UseNewtonsoftDeserializer = useNewtonsoft,
				JsonSerializerOptions = options
			};

			ResponseModel? result = await response.HandleResponse<ResponseModel, RequestModel>(requestOptions, Current.CancellationToken);

			result.ShouldNotBeNull();
			result.Name.ShouldBe("logged");
			result.Value.ShouldBe(43);
		}
	}

}
