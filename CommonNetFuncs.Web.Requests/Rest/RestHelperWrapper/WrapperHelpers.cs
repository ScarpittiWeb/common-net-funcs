using CommonNetFuncs.Core;
using CommonNetFuncs.Web.Requests.Rest.Options;
using System.Collections.Immutable;
using System.Net;
using static CommonNetFuncs.Compression.Streams;
using static CommonNetFuncs.Core.Random;
using static CommonNetFuncs.Web.Common.ContentTypes;
using static CommonNetFuncs.Web.Requests.Rest.RestHelperConstants;
using static System.Net.HttpStatusCode;

namespace CommonNetFuncs.Web.Requests.Rest.RestHelperWrapper;

internal static class WrapperHelpers
{
	private static readonly ImmutableDictionary<string, string> MemPackHeadersWithGzip = ImmutableDictionary.CreateRange([MemPackContentHeader, MemPackAcceptHeader, GzipEncodingHeader]);
	private static readonly ImmutableDictionary<string, string> MemPackHeadersWithBrotli = ImmutableDictionary.CreateRange([MemPackContentHeader, MemPackAcceptHeader, BrotliEncodingHeader]);

	private static readonly ImmutableDictionary<string, string> MsgPackHeadersWithGzip = ImmutableDictionary.CreateRange([MsgPackContentHeader, MsgPackAcceptHeader, GzipEncodingHeader]);
	private static readonly ImmutableDictionary<string, string> MsgPackHeadersWithBrotli = ImmutableDictionary.CreateRange([MsgPackContentHeader, MsgPackAcceptHeader, BrotliEncodingHeader]);

	private static readonly ImmutableDictionary<string, string> JsonHeadersWithGzip = ImmutableDictionary.CreateRange([JsonContentHeader, JsonAcceptHeader, GzipEncodingHeader]);
	private static readonly ImmutableDictionary<string, string> JsonHeadersWithBrotli = ImmutableDictionary.CreateRange([JsonContentHeader, JsonAcceptHeader, BrotliEncodingHeader]);

	// Snapshots taken from the immutable dictionaries above, so iteration order (and therefore header insertion order) is unchanged
	private static readonly KeyValuePair<string, string>[] MemPackHeadersWithGzipArray = [.. MemPackHeadersWithGzip];
	private static readonly KeyValuePair<string, string>[] MemPackHeadersWithBrotliArray = [.. MemPackHeadersWithBrotli];
	private static readonly KeyValuePair<string, string>[] MsgPackHeadersWithGzipArray = [.. MsgPackHeadersWithGzip];
	private static readonly KeyValuePair<string, string>[] MsgPackHeadersWithBrotliArray = [.. MsgPackHeadersWithBrotli];
	private static readonly KeyValuePair<string, string>[] JsonHeadersWithGzipArray = [.. JsonHeadersWithGzip];
	private static readonly KeyValuePair<string, string>[] JsonHeadersWithBrotliArray = [.. JsonHeadersWithBrotli];
	private static readonly KeyValuePair<string, string>[] MemPackHeadersArray = [.. MemPackHeaders];
	private static readonly KeyValuePair<string, string>[] MsgPackHeadersArray = [.. MsgPackHeaders];
	private static readonly KeyValuePair<string, string>[] JsonHeadersArray = [.. JsonHeaders];
	private static readonly KeyValuePair<string, string>[] JsonNoEncodeHeadersArray = [.. JsonNoEncodeHeaders];

	/// <summary>
	/// Populates a dictionary with custom and compression headers.
	/// </summary>
	internal static void PopulateHeaders(Dictionary<string, string> headers, RestHelperOptions options, bool isStreaming)
	{
		// Add custom headers from options
		if (options.HttpHeaders?.Count > 0)
		{
			foreach (KeyValuePair<string, string> header in options.HttpHeaders)
			{
				headers[header.Key] = header.Value;
			}
		}

		// Add compression headers
		SetCompressionHttpHeadersInPlace(headers, options.CompressionOptions, isStreaming);
	}

	/// <summary>
	/// Populates existing dictionary with compression headers instead of creating a new one.
	/// </summary>
	private static void SetCompressionHttpHeadersInPlace(Dictionary<string, string> headers, CompressionOptions? compressionOptions = null, bool isStreaming = false)
	{
		KeyValuePair<string, string>[]? compressionHeaders = SelectCompressionHeaders(compressionOptions, isStreaming);

		// Add compression headers to the existing dictionary if they don't already exist
		if (compressionHeaders != null)
		{
			foreach (KeyValuePair<string, string> header in compressionHeaders)
			{
				headers.TryAdd(header.Key, header.Value);
			}
		}
	}

	private static KeyValuePair<string, string>[]? SelectCompressionHeaders(CompressionOptions? compressionOptions, bool isStreaming)
	{
		if (compressionOptions?.UseCompression == true)
		{
			if (compressionOptions.CompressionType == null)
			{
				return compressionOptions.UseMemPack ? MemPackHeadersArray : compressionOptions.UseMsgPack ? MsgPackHeadersArray : JsonHeadersArray;
			}

			// Any type other than Brotli (including Gzip) maps to the Gzip header sets
			if (compressionOptions.CompressionType == ECompressionType.Brotli)
			{
				return compressionOptions.UseMemPack ? MemPackHeadersWithBrotliArray : compressionOptions.UseMsgPack ? MsgPackHeadersWithBrotliArray : JsonHeadersWithBrotliArray;
			}
			return compressionOptions.UseMemPack ? MemPackHeadersWithGzipArray : compressionOptions.UseMsgPack ? MsgPackHeadersWithGzipArray : JsonHeadersWithGzipArray;
		}

		if (isStreaming)
		{
			// When streaming without compression, use appropriate headers based on serialization type, defaulting to JSON with no compression
			if (compressionOptions?.UseMemPack == true)
			{
				return MemPackHeadersArray;
			}
			return compressionOptions?.UseMsgPack == true ? MsgPackHeadersArray : JsonNoEncodeHeadersArray;
		}
		return null;
	}

	internal static TimeSpan GetWaitTime(ResilienceOptions resilienceOptions, int attempts)
	{
		TimeSpan waitTime = resilienceOptions.DelayBackoffType switch
		{
			EDelayBackoffType.Constant => resilienceOptions.RetryDelay + (resilienceOptions.UseJitter ? TimeSpan.FromMilliseconds(resilienceOptions.RetryDelay.TotalMilliseconds * (GetRandomInt(0, 51) - 25) / 100f) : TimeSpan.FromMilliseconds(0)),
			EDelayBackoffType.Linear => resilienceOptions.RetryDelay * attempts,
			EDelayBackoffType.Exponential => TimeSpan.FromMilliseconds(Math.Pow(resilienceOptions.RetryDelay.TotalMilliseconds, attempts)),
			_ => resilienceOptions.RetryDelay,
		};

		if (resilienceOptions.UseJitter)
		{
			waitTime += GetJitter(waitTime);
		}

		return waitTime;
	}

	private static TimeSpan GetJitter(TimeSpan baseRetryDelay)
	{
		return TimeSpan.FromMilliseconds(baseRetryDelay.TotalMilliseconds * (GetRandomInt(0, 51) - 25) / 100f);
	}

	internal static async Task<string?> PopulateBearerToken(RestHelperOptions options, int attempts, HttpResponseMessage? lastResponse, string? currentBearerToken)
	{
		if (!options.BearerToken.IsNullOrWhiteSpace() && attempts == 0)
		{
			return options.BearerToken;
		}
		else if (options.ResilienceOptions?.GetBearerTokenFunc != null && ((attempts == 0 && options.BearerToken.IsNullOrWhiteSpace()) || (lastResponse?.StatusCode is Unauthorized or Forbidden))) // Only refresh token if we got a 401 or 403
		{
			return await options.ResilienceOptions.GetBearerTokenFunc(options.ApiName, attempts > 0).ConfigureAwait(false);
		}

		return currentBearerToken;
	}

	internal static void UpdateStreamingHeaders(RestHelperOptions options)
	{
		options.HttpHeaders ??= new Dictionary<string, string>();

		// Convert to mutable dictionary if read-only
		if (options.HttpHeaders.IsReadOnly)
		{
			options.HttpHeaders = new Dictionary<string, string>(options.HttpHeaders);
		}

		// Leave the accept header to PopulateHeaders when a MessagePack or MemoryPack format is requested via CompressionOptions
		bool usesExplicitSerializationFormat = (options.CompressionOptions?.UseMsgPack == true) || (options.CompressionOptions?.UseMemPack == true);
		if (!usesExplicitSerializationFormat && !options.HttpHeaders.ContainsKey(AcceptHeader))
		{
			options.HttpHeaders[AcceptHeader] = Json;
		}
	}

	internal static bool ShouldRetry(HttpResponseMessage? response, ResilienceOptions options)
	{
		if (options.ShouldRetryFunc != null && options.ShouldRetryFunc(response, options))
		{
			return false;
		}
		else
		{
			if ((options.RunOnce && response?.StatusCode != Unauthorized) || ((options.NullOk || response != null) && response?.IsSuccessStatusCode == true))
			{
				return false;
			}

			// If custom retry function is provided, use it
			if (options.ShouldRetryByStatusFunc != null && response != null)
			{
				return options.ShouldRetryByStatusFunc(response.StatusCode);
			}

			// Default retry behavior when no custom function is provided
			if (response != null)
			{
				return ShouldRetryBasedOnStatusCode(response.StatusCode);
			}

			return false;
		}
	}

	private static bool ShouldRetryBasedOnStatusCode(HttpStatusCode statusCode)
	{
		return statusCode switch
		{
			// Auth errors - retry for token refresh
			Unauthorized => true,
			Forbidden => true,

			// 5xx server errors - should retry
			InternalServerError => true,
			BadGateway => true,
			ServiceUnavailable => true,
			GatewayTimeout => true,
			InsufficientStorage => true,
			NetworkAuthenticationRequired => true,

			// 4xx client errors that might be transient
			RequestTimeout => true,
			TooManyRequests => true,

			// Don't retry these 4xx errors
			BadRequest => false,
			NotFound => false,
			MethodNotAllowed => false,
			NotAcceptable => false,
			Conflict => false,
			Gone => false,
			UnprocessableEntity => false,

			// For any other status codes, don't retry by default
			_ => false
		};
	}

	private const char UrlSeparator = (char)47;

	internal static RequestOptions<T> GetRequestOptions<T>(RestHelperOptions options, Uri? baseAddress, IDictionary<string, string> headers, HttpMethod httpMethod,
		string? bearerToken, T? postObject = default, HttpContent? patchDocument = null)
	{
		RequestOptions<T> baseRequestOptions = new()
		{
			Url = baseAddress != null
				? new Uri(new Uri(baseAddress.ToString().TrimEnd(UrlSeparator) + UrlSeparator), options.Url.TrimStart(UrlSeparator)).ToString()
				: options.Url,
			HttpMethod = httpMethod,
			BearerToken = bearerToken,
			Timeout = options.ResilienceOptions?.TimeoutValue?.TotalSeconds,
			HttpHeaders = headers,
			JsonSerializerOptions = options.JsonSerializerOptions,
			UseNewtonsoftDeserializer = options.UseNewtonsoftDeserializer,
			ExpectTaskCancellation = options.ResilienceOptions?.RunOnce ?? false,
			LogQuery = options.LogQuery,
			LogBody = options.LogBody,
			MessagePackSerializerOptions = options.MessagePackSerializerOptions
		};

		if (httpMethod == HttpMethod.Post || httpMethod == HttpMethod.Put)
		{
			baseRequestOptions.BodyObject = postObject;
		}

		if (httpMethod == HttpMethod.Patch)
		{
			baseRequestOptions.PatchDocument = patchDocument;
		}

		return baseRequestOptions;
	}
}
