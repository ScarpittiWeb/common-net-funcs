using System.Runtime.CompilerServices;
using System.Text;
using CommonNetFuncs.Web.Requests.Rest.Options;
using Microsoft.AspNetCore.JsonPatch;
using NLog;
using static CommonNetFuncs.Web.Common.ContentTypes;
using static CommonNetFuncs.Web.Requests.Rest.RestHelperWrapper.WrapperHelpers;
using static Newtonsoft.Json.JsonConvert;

namespace CommonNetFuncs.Web.Requests.Rest.RestHelperWrapper;

public sealed class RestHelpersWrapper
{

	public RestHelpersWrapper(IRestClientFactory restClientFactory)
	{
		this.restClientFactory = restClientFactory;
	}

	public RestHelpersWrapper(IRestClientFactory restClientFactory, RestHelperOptionsDefaultConfig defaultOptions)
	{
		this.restClientFactory = restClientFactory;
		this.defaultOptions = defaultOptions;
	}

	private readonly IRestClientFactory restClientFactory;
	private readonly RestHelperOptionsDefaultConfig? defaultOptions;

	private readonly Logger logger = LogManager.GetCurrentClassLogger();

	private void FillDefaultOptions(RestHelperOptions options)
	{
		if (defaultOptions == null)
		{
			return;
		}

		options.ResilienceOptions ??= new();
		options.ResilienceOptions.GetBearerTokenFunc ??= defaultOptions.ResilienceOptions?.GetBearerTokenFunc;
		options.UseBearerToken = defaultOptions.UseBearerToken ?? options.UseBearerToken;

		options.JsonSerializerOptions ??= defaultOptions.JsonSerializerOptions;
		options.MessagePackSerializerOptions ??= defaultOptions.MessagePackSerializerOptions;
		options.CompressionOptions ??= defaultOptions.CompressionOptions;
	}

	private Task<RestObject<TResponse>?> SendRestObjectWithRetryAsync<TResponse, TBody>(RestHelperOptions options, string requestLabel, string errorLabel, HttpMethod httpMethod, TBody? requestBody,
		CancellationToken cancellationToken, Func<HttpContent>? createPatchDocument = null)
	{
		return SendWithRetryAsync<RestObject<TResponse>, TBody>(options, requestLabel, errorLabel, false, httpMethod, requestBody, createPatchDocument,
			static (client, requestOptions, token) => client.RestObjectRequest<TResponse, TBody>(requestOptions, token), cancellationToken);
	}

	private Task<StreamingRestObject<TResponse>?> SendStreamingWithRetryAsync<TResponse, TBody>(RestHelperOptions options, string requestLabel, string errorLabel, HttpMethod httpMethod,
		TBody? requestBody, CancellationToken cancellationToken)
	{
		return SendWithRetryAsync<StreamingRestObject<TResponse>, TBody>(options, requestLabel, errorLabel, true, httpMethod, requestBody, null,
			static (client, requestOptions, token) => client.StreamingRestObjectRequest<TResponse, TBody>(requestOptions, token), cancellationToken);
	}

	// Shared retry/bearer-token loop. Returns the last result received, or null if no attempt was made.
	private async Task<TResult?> SendWithRetryAsync<TResult, TBody>(RestHelperOptions options, string requestLabel, string errorLabel, bool isStreaming, HttpMethod httpMethod, TBody? requestBody,
		Func<HttpContent>? createPatchDocument, Func<IRestClient, RequestOptions<TBody>, CancellationToken, Task<TResult>> sendRequest, CancellationToken cancellationToken) where TResult : class, IRestResponse
	{
		TResult? result = null;
		int attempts = 0;
		string? bearerToken = null;
		HttpResponseMessage? lastResponse = null;

		FillDefaultOptions(options);
		if (isStreaming)
		{
			UpdateStreamingHeaders(options); // Ensure application/json or application/x-msgpack is used for streaming
		}

		IRestClient client = restClientFactory.CreateClient(options.ApiName);
		options.ResilienceOptions ??= new();

		Dictionary<string, string> requestHeaders = new();
		try
		{
			PopulateHeaders(requestHeaders, options, isStreaming);
			while ((result?.Response == null || !result.Response.IsSuccessStatusCode) && attempts < options.ResilienceOptions.MaxRetry)
			{
				if (attempts > 0)
				{
					logger.Info("{RequestLabel} {Url} Attempt {AttemptNumber}", requestLabel, options.Url, attempts + 1);
				}

				if (options.UseBearerToken)
				{
					bearerToken = await PopulateBearerToken(options, attempts, lastResponse, bearerToken).ConfigureAwait(false);
				}

				RequestOptions<TBody> baseRequestOptions = GetRequestOptions(options, client.BaseAddress, requestHeaders, httpMethod, bearerToken, requestBody, createPatchDocument?.Invoke());

				result = await sendRequest(client, baseRequestOptions, cancellationToken).ConfigureAwait(false);

				if (!ShouldRetry(result.Response, options.ResilienceOptions))
				{
					break;
				}

				attempts++;
				lastResponse = result.Response;

				if (attempts >= options.ResilienceOptions.MaxRetry)
				{
					logger.Warn("{RequestLabel} {Url} still failing after max allowed attempts ({MaxRetry}).", requestLabel, options.Url, options.ResilienceOptions.MaxRetry);
					break;
				}
				await Task.Delay(GetWaitTime(options.ResilienceOptions, attempts), cancellationToken).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			logger.Error(ex, "Exception occurred during {RequestType} request to {Url}: {Message}", errorLabel, options.Url, ex.Message);
			throw new HttpRequestException($"Error occurred during {errorLabel} request", ex);
		}

		return result;
	}

	#region GET Methods

	/// <summary>
	/// Sends a GET request to the specified URL and returns the response deserialized into the specified type.
	/// </summary>
	/// <typeparam name="TResponse">The object type to be returned by the request.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The deserialized response object, or <see cref="null"/> if the request failed.</returns>
	public async Task<TResponse?> Get<TResponse>(RestHelperOptions options, CancellationToken cancellationToken = default)
	{
		RestObject<TResponse>? result = await SendRestObjectWithRetryAsync<TResponse, TResponse>(options, "GET", "GET", HttpMethod.Get, default, cancellationToken).ConfigureAwait(false);
		return result == null ? default : result.Result;
	}

	/// <summary>
	/// Sends a GET request to the specified URL and returns the response as an asynchronous stream of the specified type.
	/// </summary>
	/// <typeparam name="TResponse">The object type to be returned by the request.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The deserialized response object, or <see cref="null"/> if the request failed.</returns>
	public async IAsyncEnumerable<TResponse?> GetStreaming<TResponse>(RestHelperOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		StreamingRestObject<TResponse>? result = await SendStreamingWithRetryAsync<TResponse, TResponse>(options, "GET (Streaming)", "GET Streaming", HttpMethod.Get, default, cancellationToken).ConfigureAwait(false);

		if (result?.Result != null)
		{
			await foreach (TResponse? item in result.Result.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				cancellationToken.ThrowIfCancellationRequested();
				yield return item;
			}
		}
	}

	#endregion

	#region POST Methods

	/// <summary>
	/// Sends a POST request to the specified URL with the provided object and returns the response deserialized into the specified type.
	/// </summary>
	/// <typeparam name="TBody">The object type to populate the request body with and / or be returned by the request.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="postObject">The object to be sent in the request body.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The deserialized response object, or <see cref="null"/> if the request failed.</returns>
	public async Task<TBody?> PostRequest<TBody>(RestHelperOptions options, TBody postObject, CancellationToken cancellationToken = default)
	{
		RestObject<TBody>? result = await SendRestObjectWithRetryAsync<TBody, TBody>(options, "POST", "POST", HttpMethod.Post, postObject, cancellationToken).ConfigureAwait(false);
		return result == null ? default : result.Result;
	}

	/// <summary>
	/// Sends a POST request to the specified URL with the provided object and returns the response as an asynchronous stream of the specified type.
	/// </summary>
	/// <typeparam name="TBody">The object type to populate the request body with and / or be returned by the request stream.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="postObject">The object to be sent in the request body.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>An asynchronous stream of the response objects.</returns>
	public async IAsyncEnumerable<TBody?> PostRequestStreaming<TBody>(RestHelperOptions options, TBody postObject, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		StreamingRestObject<TBody>? result = await SendStreamingWithRetryAsync<TBody, TBody>(options, "POST (Streaming)", "POST Streaming", HttpMethod.Post, postObject, cancellationToken).ConfigureAwait(false);

		if (result?.Result != null)
		{
			await foreach (TBody? item in result.Result.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				yield return item;
			}
		}
	}

	/// <summary>
	/// Sends a POST request to the specified URL with the provided object and returns the response deserialized into the specified type.
	/// </summary>
	/// <typeparam name="TResponse">The object type to be returned in the response.</typeparam>
	/// <typeparam name="TBody">The object type to populate the request body with.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="postObject">The object to be sent in the request body.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The deserialized response object, or <see cref="null"/> if the request failed.</returns>
	public async Task<TResponse?> GenericPostRequest<TResponse, TBody>(RestHelperOptions options, TBody postObject, CancellationToken cancellationToken = default)
	{
		RestObject<TResponse>? result = await SendRestObjectWithRetryAsync<TResponse, TBody>(options, "POST (Generic)", "generic POST", HttpMethod.Post, postObject, cancellationToken).ConfigureAwait(false);
		return result == null ? default : result.Result;
	}

	/// <summary>
	/// Sends a POST request to the specified URL with the provided object and returns the response as an asynchronous stream of the specified type.
	/// </summary>
	/// <typeparam name="TResponse">The object type to be returned in the stream.</typeparam>
	/// <typeparam name="TBody">The object type to populate the request body with.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="postObject">The object to be sent in the request body.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>An asynchronous stream of the response objects.</returns>
	public async IAsyncEnumerable<TResponse?> GenericPostRequestStreaming<TResponse, TBody>(RestHelperOptions options, TBody postObject, [EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		StreamingRestObject<TResponse>? result = await SendStreamingWithRetryAsync<TResponse, TBody>(options, "POST (Generic Streaming)", "generic POST streaming", HttpMethod.Post, postObject, cancellationToken).ConfigureAwait(false);

		if (result?.Result != null)
		{
			await foreach (TResponse? item in result.Result.WithCancellation(cancellationToken).ConfigureAwait(false))
			{
				yield return item;
			}
		}
		else
		{
			logger.Warn("POST (Generic Streaming) {Url} failed with response: [{StatusCode}]:{Response}", options.Url, result?.Response?.StatusCode, result?.Response?.ReasonPhrase);
		}
	}

	/// <summary>
	/// Sends a POST request to the specified URL with the provided object and returns the response as a string.
	/// </summary>
	/// <typeparam name="TBody">The object type to populate the request body with.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="postObject">The object to be sent in the request body.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The response body as a string, or <see cref="null"/> if the request failed.</returns>
	public async Task<string?> StringPostRequest<TBody>(RestHelperOptions options, TBody postObject, CancellationToken cancellationToken = default)
	{
		RestObject<string?>? result = await SendRestObjectWithRetryAsync<string?, TBody>(options, "POST (String)", "string POST", HttpMethod.Post, postObject, cancellationToken).ConfigureAwait(false);
		return result?.Result;
	}

	#endregion

	#region Update Methods

	/// <summary>
	/// Sends a PATCH request to the specified URL with the provided model, comparing it to the old model to create a JSON Patch document.
	/// </summary>
	/// <typeparam name="TEntity">Type of the object to be patched.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="model">The updated model to be sent in the request.</param>
	/// <param name="oldModel">The original model to compare against.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The returned value from the request or <see cref="null"/> if the request failed.</returns>
	public async Task<TEntity?> PatchRequest<TEntity>(RestHelperOptions options, TEntity model, TEntity oldModel, CancellationToken cancellationToken = default) where TEntity : class
	{
		JsonPatchDocument patchDocument = PatchCreator.CreatePatch(oldModel, model);

		if (patchDocument.Operations.Count == 0)
		{
			logger.Debug("No changes detected in the model; skipping PATCH request.");
			return model; // No changes detected, return the original model
		}

		// System.Text.Json has issues producing JsonPatchDocument in the correct format, so Newtonsoft is used here
		RestObject<TEntity>? result = await SendRestObjectWithRetryAsync<TEntity, TEntity>(options, "PATCH", "PATCH", HttpMethod.Patch, default, cancellationToken,
			() => new StringContent(SerializeObject(patchDocument), Encoding.UTF8, Json)).ConfigureAwait(false);
		return result?.Result;
	}

	/// <summary>
	/// Sends a PUT request to the specified URL with the provided replacement model and returns the response deserialized into the specified type.
	/// </summary>
	/// <typeparam name="TEntity">Type of the object to be replaced and returned.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="replacementModel">The model to replace the existing resource with.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The returned value from the request or <see cref="null"/> if the request failed.</returns>
	public async Task<TEntity?> PutRequest<TEntity>(RestHelperOptions options, TEntity replacementModel, CancellationToken cancellationToken = default) where TEntity : class
	{
		RestObject<TEntity>? result = await SendRestObjectWithRetryAsync<TEntity, TEntity>(options, "PUT", "PUT", HttpMethod.Put, replacementModel, cancellationToken).ConfigureAwait(false);
		return result?.Result;
	}

	/// <summary>
	/// Sends a DELETE request to the specified URL and returns the response deserialized into the specified type.
	/// </summary>
	/// <typeparam name="TEntity">Type of the expected return value.</typeparam>
	/// <param name="options">Options specifying the request details.</param>
	/// <param name="cancellationToken">Optional: Cancellation token for this operation.</param>
	/// <returns>The returned value from the request or <see cref="null"/> if the request failed.</returns>
	public async Task<TEntity?> DeleteRequest<TEntity>(RestHelperOptions options, CancellationToken cancellationToken = default) where TEntity : class
	{
		RestObject<TEntity>? result = await SendRestObjectWithRetryAsync<TEntity, TEntity>(options, "DELETE", "DELETE", HttpMethod.Delete, default, cancellationToken).ConfigureAwait(false);
		return result?.Result;
	}

	#endregion
}
