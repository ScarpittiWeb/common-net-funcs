using System.Buffers;
using System.Text.Json;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace CommonNetFuncs.Web.Requests.MessagePack;

/// <summary>
/// Streams IAsyncEnumerable data with content negotiation support (MessagePack or JSON).
/// Supports MessagePack binary streaming and newline-delimited JSON (NDJSON) streaming based on the Accept header.
/// Each item is serialized individually and written to the stream for memory-efficient streaming.
/// </summary>
/// <typeparam name="T">Type of items to stream</typeparam>
/// <param name="data">The async enumerable data to stream</param>
/// <param name="messagePackOptions">
/// Optional MessagePack serializer options. When omitted, registered MVC MessagePack output formatter options are used when available;
/// otherwise <see cref="MessagePackSerializerOptions.Standard"/> is used.
/// </param>
/// <param name="jsonOptions">Optional JSON serializer options</param>
/// <param name="successStatusCode">Status code to return when data is present (default: 200 OK)</param>
/// <param name="emptyStatusCode">Status code to return when no data is present (default: 204 No Content)</param>
/// <remarks>
/// Creates a new MessagePackStreamingResult with content negotiation.
/// Supports: application/json (NDJSON), application/x-msgpack, and */* (defaults to JSON).
/// Returns 400 Bad Request for unsupported Accept headers.
/// </remarks>
public class MessagePackStreamingResult<T>(
	IAsyncEnumerable<T> data,
	MessagePackSerializerOptions? messagePackOptions = null,
	JsonSerializerOptions? jsonOptions = null,
	int successStatusCode = StatusCodes.Status200OK,
	int emptyStatusCode = StatusCodes.Status204NoContent) : IActionResult, IResult
{
	private static readonly byte[] NewLine = [(byte)'\n'];

	private readonly IAsyncEnumerable<T> data = data ?? throw new ArgumentNullException(nameof(data));
	private readonly MessagePackSerializerOptions? messagePackOptions = messagePackOptions;
	private readonly JsonSerializerOptions? jsonOptions = jsonOptions;
	private readonly int successStatusCode = successStatusCode;
	private readonly int emptyStatusCode = emptyStatusCode;

	public async Task ExecuteResultAsync(ActionContext context)
	{
		ArgumentNullException.ThrowIfNull(context);
		await ExecuteAsync(context.HttpContext).ConfigureAwait(false);
	}

	public async Task ExecuteAsync(HttpContext httpContext)
	{
		ArgumentNullException.ThrowIfNull(httpContext);

		HttpRequest request = httpContext.Request;
		HttpResponse response = httpContext.Response;
		CancellationToken cancellationToken = httpContext.RequestAborted;

		// Content negotiation: determine format from Accept header
		string acceptHeader = request.Headers[HeaderNames.Accept].ToString() ?? string.Empty;
		StreamingFormat format = DetermineFormat(acceptHeader);

		if (format == StreamingFormat.Unsupported)
		{
			response.StatusCode = StatusCodes.Status400BadRequest;
			response.ContentType = "text/plain";
			await response.WriteAsync($"Unsupported Accept header: {acceptHeader}. Supported formats: application/json, application/x-msgpack", cancellationToken).ConfigureAwait(false);
			return;
		}

		// Set content type based on negotiated format
		response.ContentType = format == StreamingFormat.MessagePack ? "application/x-msgpack" : "application/x-ndjson";
		response.StatusCode = successStatusCode;

		bool hasData = false;
		IOutputFormatter? mvcMessagePackFormatter = format == StreamingFormat.MessagePack && messagePackOptions is null
			? GetMvcMessagePackFormatter(httpContext)
			: null;

		// One serialization buffer per response (never shared between concurrent executions), reused for every MessagePack item
		ArrayBufferWriter<byte>? messagePackBuffer = null;

		try
		{
			await foreach (T item in data.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				hasData = true;

				if (format == StreamingFormat.MessagePack)
				{
					if (mvcMessagePackFormatter is not null)
					{
						await WriteMvcMessagePackItemAsync(httpContext, mvcMessagePackFormatter, item).ConfigureAwait(false);
					}
					else
					{
						messagePackBuffer ??= new ArrayBufferWriter<byte>();
						await WriteMessagePackItemAsync(response, messagePackBuffer, item, cancellationToken).ConfigureAwait(false);
					}
				}
				else // JSON
				{
					await WriteJsonItemAsync(response, item, cancellationToken).ConfigureAwait(false);
				}
			}

			if (!hasData)
			{
				response.StatusCode = emptyStatusCode;
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Client disconnected or request was aborted; not a server error.
			response.StatusCode = StatusCodes.Status499ClientClosedRequest; // Non-standard status code for client closed request
		}
		catch (Exception)
		{
			if (!response.HasStarted)
			{
				response.StatusCode = StatusCodes.Status500InternalServerError;
			}
			throw;
		}
	}

	private static StreamingFormat DetermineFormat(string acceptHeader)
	{
		if (string.IsNullOrWhiteSpace(acceptHeader))
		{
			return StreamingFormat.Json; // Default to JSON when no Accept header
		}

		string normalized = acceptHeader.ToLowerInvariant();

		// Check for MessagePack
		if (normalized.Contains("application/x-msgpack") || normalized.Contains("application/msgpack"))
		{
			return StreamingFormat.MessagePack;
		}

		// Check for JSON (including wildcard)
		if (normalized.Contains("application/json") ||
				normalized.Contains("application/x-ndjson") ||
				normalized.Contains("*/*") ||
				normalized.Contains("application/*"))
		{
			return StreamingFormat.Json;
		}

		// Unsupported format
		return StreamingFormat.Unsupported;
	}

	private static IOutputFormatter? GetMvcMessagePackFormatter(HttpContext httpContext)
	{
		IServiceProvidersFeature? serviceProviders = httpContext.Features.Get<IServiceProvidersFeature>();
		if (serviceProviders?.RequestServices is not { } requestServices ||
			requestServices.GetService(typeof(IOptions<MvcOptions>)) is not IOptions<MvcOptions> mvcOptions)
		{
			return null;
		}

		return mvcOptions.Value.OutputFormatters.FirstOrDefault(
			formatter => formatter.GetType().FullName == "MessagePack.AspNetCoreMvcFormatter.MessagePackOutputFormatter");
	}

	private async Task WriteMessagePackItemAsync(HttpResponse response, T item, CancellationToken cancellationToken)
	{
		IServiceProvidersFeature? serviceProviders = httpContext.Features.Get<IServiceProvidersFeature>();
		if (serviceProviders?.RequestServices is not { } requestServices ||
			requestServices.GetService(typeof(IOptions<MvcOptions>)) is not IOptions<MvcOptions> mvcOptions)
		{
			return null;
		}

		return mvcOptions.Value.OutputFormatters.FirstOrDefault(
			formatter => formatter.GetType().FullName == "MessagePack.AspNetCoreMvcFormatter.MessagePackOutputFormatter");
	}

	private async Task WriteMessagePackItemAsync(HttpResponse response, ArrayBufferWriter<byte> bufferWriter, T item, CancellationToken cancellationToken)
	{
		// Serialize each item into the reused buffer
		bufferWriter.ResetWrittenCount();
		MessagePackSerializer.Serialize(bufferWriter, item, messagePackOptions ?? MessagePackSerializerOptions.Standard, cancellationToken);

		// Write the serialized bytes to the response stream
		await response.Body.WriteAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
		await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task WriteMvcMessagePackItemAsync(HttpContext httpContext, IOutputFormatter formatter, T item)
	{
		OutputFormatterWriteContext context = new(
			httpContext,
			static (stream, encoding) => new StreamWriter(stream, encoding, leaveOpen: true),
			typeof(T),
			item);

		await formatter.WriteAsync(context).ConfigureAwait(false);
		await httpContext.Response.Body.FlushAsync(httpContext.RequestAborted).ConfigureAwait(false);
	}

	private async Task WriteJsonItemAsync(HttpResponse response, T item, CancellationToken cancellationToken)
	{
		// Serialize item as JSON
		byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(item, jsonOptions);

		// Write JSON followed by newline (NDJSON format)
		await response.Body.WriteAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
		await response.Body.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
		await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private enum StreamingFormat
	{
		Json,
		MessagePack,
		Unsupported
	}
}
