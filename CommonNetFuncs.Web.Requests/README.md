# CommonNetFuncs.Web.Requests

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Requests)](https://www.nuget.org/packages/CommonNetFuncs.Web.Requests/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Requests)](https://www.nuget.org/packages/CommonNetFuncs.Web.Requests/)

HTTP request, response, retry, streaming, JSON Patch, cache, and MessagePack helpers for ASP.NET Core applications. The package targets **.NET 11.0**.

## Contents

- [Installation](#installation)
- [Direct REST helpers](#direct-rest-helpers)
- [Request and response contracts](#request-and-response-contracts)
- [Dependency-injected REST wrapper](#dependency-injected-rest-wrapper)
- [Response stream readers and chunking](#response-stream-readers-and-chunking)
- [MessagePack/NDJSON server streaming](#messagepackndjson-server-streaming)
- [JSON Patch](#json-patch)
- [Distributed cache extensions](#distributed-cache-extensions)
- [Other public models](#other-public-models)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Requests
```

## Direct REST helpers

The direct APIs are:

- `RestHelpersStatic` extension methods on `HttpClient`
- `IRestHelpersCommon`, `RestHelpersCommon`, and `RestHelpersCommonFactory`
- `RestHelpers`, a convenience instance backed by one shared `SocketsHttpHandler`

`RestHelpers` shares one static `HttpClient` for all instances. Its handler has `MaxConnectionsPerServer = 100`, keep-alive pings every 15 seconds, and a 60-second ping timeout. That connection count is not an application-level concurrency or rate limit. The client's own timeout is infinite; every helper request is guarded by the linked per-request timeout in `RequestOptions.Timeout`.

The public `RestHelpersStatic.defaultJsonSerializerOptions` field ignores reference cycles and matches JSON property names case-insensitively.

For `Timeout is null` or `<= 0`, the helper uses **100 seconds**. A caller cancellation token is linked to that timeout. The convenience `RestHelpers` methods catch/log request and cancellation failures and generally return `default`, an empty object, or an empty stream instead of propagating them.

For a complete request, that timer covers reading the response body because it uses `ResponseContentRead`. Streaming methods use `ResponseHeadersRead`; the linked timeout source is disposed after headers arrive, so the body enumeration itself is governed by the caller's cancellation token rather than by the original `RequestOptions.Timeout`.

### Call all direct helper overloads

```csharp
using CommonNetFuncs.Web.Requests.Rest;

HttpClient client = new();
RestHelpers rest = new();
using CancellationTokenSource cts = new();

RequestOptions<CreateItem> create = new()
{
    Url = "https://api.example.test/items",
    HttpMethod = HttpMethod.Post,
    BodyObject = new CreateItem("new item"),
    Timeout = 30,
    BearerToken = "access-token"
};

// HttpClient extension
Item? extensionResult =
    await client.RestRequest<Item, CreateItem>(create, cts.Token);

// Shared-client convenience methods, through RestHelpersCommon
Item? sharedResult =
    await rest.RestRequest<Item, CreateItem>(create, cts.Token);

await foreach (Item? item in client.StreamingRestRequest<Item, CreateItem>(
    new()
    {
        Url = "https://api.example.test/items/stream",
        HttpMethod = HttpMethod.Get
    }, cts.Token))
{
    if (item is not null)
    {
        Console.WriteLine(item.Name);
    }
}

await foreach (Item? item in rest.StreamingRestRequest<Item, CreateItem>(
    new()
    {
        Url = "https://api.example.test/items/stream",
        HttpMethod = HttpMethod.Get
    }, cts.Token))
{
    Console.WriteLine(item?.Name);
}
```

`RestRequest` uses `ResponseContentRead`. POST and PUT serialize `BodyObject`; PATCH uses `PatchDocument`; other methods do not add a body. JSON is the default body format. Explicit `Content-Type: application/x-memorypack` or `application/x-msgpack` switches body serialization. A PATCH content's media type is forced to `application/json-patch+json`.

### Inspect status, headers, and result

Use the object APIs when the HTTP response matters. `RestObject<T>` contains `Result`, `Response`, and `Error`; the current REST implementation populates `Result` and `Response` and does not assign `Error` from HTTP failures.

```csharp
using CommonNetFuncs.Web.Requests.Rest;

RequestOptions<object?> getOne = new()
{
    Url = "https://api.example.test/items/1",
    HttpMethod = HttpMethod.Get,
    Timeout = 30
};

RestObject<Item> raw = await client.RestObjectRequest<Item, object?>(
    getOne, cancellationToken);
try
{
    if (raw.Response?.IsSuccessStatusCode == true)
    {
        Item? item = raw.Result;
    }
    else
    {
        Console.WriteLine(raw.Response?.StatusCode);
    }
}
finally
{
    raw.Response?.Dispose();
}

RestObject<Item> convenience = await rest.RestRequestObject<Item, object?>(
    getOne, cancellationToken);
convenience.Response?.Dispose();

StreamingRestObject<Item> streaming =
    await client.StreamingRestObjectRequest<Item, object?>(
        new()
        {
            Url = "https://api.example.test/items/stream",
            HttpMethod = HttpMethod.Get
        }, cancellationToken);
try
{
    if (streaming.Result is not null)
    {
        await foreach (Item? item in streaming.Result.WithCancellation(cancellationToken))
        {
            Console.WriteLine(item?.Name);
        }
    }
}
finally
{
    // Streaming object requests transfer response lifetime to the caller.
    streaming.Response?.Dispose();
}

StreamingRestObject<Item> convenienceStreaming =
    await rest.StreamingRestRequestObject<Item, object?>(
        new()
        {
            Url = "https://api.example.test/items/stream",
            HttpMethod = HttpMethod.Get
        }, cancellationToken);
try
{
    // consume convenienceStreaming.Result
}
finally
{
    convenienceStreaming.Response?.Dispose();
}
```

`RestHelpersCommonFactory` creates either the factory's default `HttpClient` or a named client and delegates all four common methods. It implements `IDisposable` and disposes that created client; dispose the factory wrapper when it is no longer needed. Do not dispose factory-managed shared clients per request.

```csharp
using CommonNetFuncs.Web.Requests.Rest;
using Microsoft.Extensions.DependencyInjection;

ServiceCollection services = new();
services.AddHttpClient("catalog");
using ServiceProvider provider = services.BuildServiceProvider();
IHttpClientFactory factory = provider.GetRequiredService<IHttpClientFactory>();

using RestHelpersCommonFactory catalog = new(factory, "catalog");
Item? item = await catalog.RestRequest<Item, object?>(
    new() { Url = "items/1" }, cancellationToken);

await foreach (Item? streamed in catalog.StreamingRestRequest<Item, object?>(
    new() { Url = "items/stream" }, cancellationToken))
{
    Console.WriteLine(streamed?.Name);
}

RestObject<Item> factoryObject =
    await catalog.RestRequestObject<Item, object?>(
        new() { Url = "items/1" }, cancellationToken);
factoryObject.Response?.Dispose();

StreamingRestObject<Item> factoryStream =
    await catalog.StreamingRestRequestObject<Item, object?>(
        new() { Url = "items/stream" }, cancellationToken);
factoryStream.Response?.Dispose();
```

## Request and response contracts

### `RequestOptions<TBody>`

| Property | Meaning |
| --- | --- |
| `Url` | Absolute or relative request URL; nullability is suppressed, so validate it before sending. |
| `RedactedUrl` | Read-only URL with every query value replaced by `<REDACTED>`; malformed URLs produce `<Error Redacting URL>`. |
| `HttpMethod` | Defaults to GET. |
| `BodyObject` | POST/PUT body. |
| `PatchDocument` | PATCH `HttpContent`. |
| `BearerToken` | Adds an `Authorization: Bearer ...` header. |
| `HttpHeaders` | Custom request headers. |
| `Timeout` | Per-request seconds; null/nonpositive uses 100 seconds. |
| `JsonSerializerOptions` | System.Text.Json response/options and JSON request-body configuration where supported. |
| `UseNewtonsoftDeserializer` | Uses Newtonsoft.Json for JSON responses instead of System.Text.Json. |
| `MessagePackSerializerOptions` | MessagePack request/response options. |
| `LogRequest`, `LogBody`, `LogQuery`, `LogResponse` | Opt-in logging controls. Query redaction is used unless `LogQuery` is true. |
| `ExpectTaskCancellation` | Logs cancellation as expected instead of as an error. |

Logging a body, query, response, or custom header can expose secrets. Keep these flags disabled in production unless the logged data is known to be safe.

```csharp
using System.Text.Json;
using CommonNetFuncs.Web.Requests.Rest;

RequestOptions<CreateItem> options = new()
{
    Url = "https://api.example.test/items?tenant=42",
    HttpMethod = HttpMethod.Post,
    BodyObject = new("new item"),
    BearerToken = token,
    Timeout = 15,
    HttpHeaders = new Dictionary<string, string>
    {
        ["Content-Type"] = "application/json",
        ["Accept"] = "application/json"
    },
    JsonSerializerOptions = new(JsonSerializerDefaults.Web),
    UseNewtonsoftDeserializer = false,
    ExpectTaskCancellation = false,
    LogRequest = true,
    LogBody = false,
    LogQuery = false,
    LogResponse = false
};

string displayUrl = options.RedactedUrl;
```

### Response types

`RestObject<T>` is mutable and carries `Result`, `Response`, and `Error`. `StreamingRestObject<T>` carries `Result` and `Response`. A caller must keep the response alive while consuming a stream and dispose it afterward. The direct convenience `StreamingRestRequest` manages/disposes its own response; object-returning calls do not.

```csharp
RestObject<Item> one = new()
{
    Result = new Item("cached"),
    Response = null,
    Error = null
};

StreamingRestObject<Item> many = new()
{
    Result = GetItemsAsync(),
    Response = null
};
```

### Header constants

`RestHelperConstants` exposes:

- `EDelayBackoffType`: `Constant`, `Linear`, `Exponential`
- header names `ContentTypeHeader`, `AcceptEncodingHeader`, `AcceptHeader`
- individual `KeyValuePair` headers `NoEncodingHeader`, `BrotliEncodingHeader`, `GzipEncodingHeader`, `MemPackContentHeader`, `MsgPackContentHeader`, `JsonContentHeader`, `MemPackAcceptHeader`, `MsgPackAcceptHeader`, and `JsonAcceptHeader`
- immutable dictionaries `JsonHeaders`, `JsonNoEncodeHeaders`, `MemPackHeaders`, and `MsgPackHeaders`

```csharp
using static CommonNetFuncs.Web.Requests.Rest.RestHelperConstants;

RequestOptions<object?> binaryRequest = new()
{
    Url = "https://api.example.test/items/stream",
    HttpHeaders = MsgPackHeaders,
    MessagePackSerializerOptions = MessagePackSerializerOptions.Standard
};

EDelayBackoffType backoff = EDelayBackoffType.Exponential;
```

## Dependency-injected REST wrapper

`RestHelpersWrapper` provides convenience methods plus response-based retry and bearer-token refresh. It requires an `IRestClientFactory`; the normal implementation wraps `IHttpClientFactory`.

The public constructors are:

```csharp
RestHelpersWrapper basic = new(restClientFactory);
RestHelpersWrapper configured = new(restClientFactory, defaultConfig);
```

### Register services

All three `AddRestClient` overloads in `ServiceCollectionExtensions` also call `AddRestClientFactory` and `AddRestHelpersWrapper`; `AddRestClientFactory` and `AddRestHelpersWrapper` can be called independently when configuring the named client elsewhere.

```csharp
using CommonNetFuncs.Web.Requests.Rest.RestHelperWrapper;

builder.Services.AddRestClientFactory();
builder.Services.AddRestHelpersWrapper();

builder.Services.AddRestClient("catalog");
builder.Services.AddRestClient("with-base",
    client => client.BaseAddress = new Uri("https://api.example.test/"));
builder.Services.AddRestClient("with-services", (sp, client) =>
{
    client.BaseAddress = sp.GetRequiredService<ApiConfiguration>().BaseUri;
});
```

`AddRestClientFactory` and `AddRestHelpersWrapper` use `TryAddSingleton`, so they do not replace an existing registration. The wrapper is mutable only in that it may fill missing fields on a passed `RestHelperOptions`; register it at an appropriate lifetime for the app.

The lower-level interfaces can be implemented for tests:

```csharp
IRestClient namedClient = restClientFactory.CreateClient("catalog");
Uri? baseAddress = namedClient.BaseAddress;
RestObject<Item> objectResult = await namedClient.RestObjectRequest<Item, object?>(
    new() { Url = "items/1" }, cancellationToken);
StreamingRestObject<Item> streamResult =
    await namedClient.StreamingRestObjectRequest<Item, object?>(
        new() { Url = "items" }, cancellationToken);
```

The package's concrete implementations are `RestClient`, which wraps one `HttpClient`, and `RestClientFactory`, which creates one `RestClient` from a named `IHttpClientFactory` client:

```csharp
using CommonNetFuncs.Web.Requests.Rest.RestHelperWrapper;

RestClient directClient = new(client);
RestClientFactory concreteFactory = new(httpClientFactory);
IRestClient created = concreteFactory.CreateClient("catalog");
```

### Wrapper options

`RestHelperOptions` validates `ApiName` and `Url` as nonempty. If `UseBearerToken` is true, the constructor also requires either a token or `ResilienceOptions.GetBearerTokenFunc`.

```csharp
using CommonNetFuncs.Web.Requests.Rest.Options;
using static CommonNetFuncs.Compression.Streams;

RestHelperOptions getOptions = new(
    Url: "items/1",
    ApiName: "catalog",
    HttpHeaders: new Dictionary<string, string> { ["X-Correlation-Id"] = "abc" },
    UseBearerToken: true,
    BearerToken: "access-token",
    UseNewtonsoftDeserializer: false,
    LogQuery: false,
    LogBody: false,
    CompressionOptions: new(UseCompression: true, CompressionType: ECompressionType.Gzip),
    MessagePackSerializerOptions: null,
    JsonSerializerOptions: new JsonSerializerOptions(JsonSerializerDefaults.Web),
    ResilienceOptions: new(MaxRetry: 3));
```

`RestHelperOptionsDefaultConfig` has `UseBearerToken`, `CompressionOptions`, `MessagePackSerializerOptions`, `JsonSerializerOptions`, and `ResilienceOptions`. The wrapper:

- always overrides per-call `UseBearerToken` when the default is non-null;
- fills null JSON, MessagePack, and compression options;
- when it creates or receives `ResilienceOptions`, fills only a missing `GetBearerTokenFunc` from the default.

It does **not** merge default retry count, delay, timeout, backoff, or retry predicates into a per-call resilience object.

```csharp
RestHelperOptionsDefaultConfig defaults = new()
{
    UseBearerToken = true,
    JsonSerializerOptions = new(JsonSerializerDefaults.Web),
    ResilienceOptions = new(
        GetBearerTokenFunc: (api, forceRefresh) =>
            ValueTask.FromResult(tokenProvider.Get(api, forceRefresh)))
};
builder.Services.AddSingleton(defaults);
```

`CompressionOptions` has `UseCompression`, nullable `CompressionType`, `UseMemPack`, and `UseMsgPack`. It controls generated `Content-Type`, `Accept`, and `Accept-Encoding` headers. Compression request bodies are **not** compressed by these options: the headers select response/media behavior, while `ReadResponseStream` handles GZip/Brotli responses.

```csharp
using static CommonNetFuncs.Compression.Streams;

CompressionOptions ordinary = new();
CompressionOptions msgpackBrotli = new(
    UseCompression: true,
    CompressionType: ECompressionType.Brotli,
    UseMemPack: false,
    UseMsgPack: true);
```

### Retry configuration and exact semantics

`ResilienceOptions` defaults:

| Setting | Default |
| --- | --- |
| `MaxRetry` | 10 (implemented as maximum total attempts) |
| `RetryDelay` | 1000 ms |
| `TimeoutValue` | 100 seconds; null/negative also becomes 100 seconds; zero remains zero |
| `DelayBackoffType` | Constant |
| `UseJitter` | true |
| `RunOnce`, `NullOk` | false |

`ShouldRetryByStatusFunc` can explicitly decide by status. Despite its name, `ShouldRetryFunc` is used as a stop condition: a `true` result stops retries. Default retries apply to 401, 403, 408, 429, and selected 5xx/network-authentication statuses. 400/404/405/406/409/410/422 and other statuses do not retry. A response with no status because the underlying helper failed is not retried by the wrapper.

Constant, linear, and exponential delays are calculated from `RetryDelay`; jitter can adjust each delay by about ±25% (the current helper also applies jitter while constructing the constant delay, so constant+jitter has two jitter adjustments).

The exponential formula is `RetryDelay.TotalMilliseconds ^ attemptNumber`, rather than a conventional doubling sequence, so verify the resulting durations before enabling it. The wrapper converts exceptions it catches—including cancellation during its retry delay—to `HttpRequestException`.

Bearer token retrieval receives `(ApiName, forceRefresh)`. Force refresh becomes true on a retry. The current token is reused unless a 401/403 was received.

```csharp
ResilienceOptions retries = new(
    MaxRetry: 3,
    RetryDelay: 250,
    TimeoutValue: 20,
    RunOnce: false,
    NullOk: false,
    ShouldRetryByStatusFunc: status =>
        status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable,
    DelayBackoffType: EDelayBackoffType.Exponential,
    UseJitter: true,
    ShouldRetryFunc: (response, options) =>
        response?.StatusCode == HttpStatusCode.NotImplemented,
    GetBearerTokenFunc: async (api, refresh) =>
        await tokenStore.GetAsync(api, refresh));
```

Retries can duplicate non-idempotent POST requests. Retry only idempotent operations or operations protected by an idempotency key/strategy.

### Every wrapper method

`PostRequest<TBody>` expects the response to have the same type as the body. Use `GenericPostRequest<TResponse,TBody>` for different types. Patch first creates a diff and returns the new model without sending a request when there are no operations.

```csharp
RestHelpersWrapper wrapper = new(restClientFactory, defaults);
RestHelperOptions items = new("items", "catalog");
RestHelperOptions one = new("items/1", "catalog");

Item? get = await wrapper.Get<Item>(one, cancellationToken);

await foreach (Item? row in wrapper.GetStreaming<Item>(
    new("items/stream", "catalog"), cancellationToken))
{
    Console.WriteLine(row?.Name);
}

Item? createdSameType = await wrapper.PostRequest(items, new Item("one"), cancellationToken);

await foreach (Item? row in wrapper.PostRequestStreaming(
    new("items/search-stream", "catalog"), new Item("filter"), cancellationToken))
{
    Console.WriteLine(row?.Name);
}

CreateResult? result = await wrapper.GenericPostRequest<CreateResult, CreateItem>(
    items, new CreateItem("one"), cancellationToken);

await foreach (Item? row in wrapper.GenericPostRequestStreaming<Item, CreateItem>(
    new("items/search-stream", "catalog"), new CreateItem("filter"), cancellationToken))
{
    Console.WriteLine(row?.Name);
}

string? text = await wrapper.StringPostRequest(
    new("items/echo", "catalog"), new CreateItem("one"), cancellationToken);

Item? patched = await wrapper.PatchRequest(
    one, currentItem, originalItem, cancellationToken);

Item? replaced = await wrapper.PutRequest(
    one, replacementItem, cancellationToken);

Item? deleted = await wrapper.DeleteRequest<Item>(
    one, cancellationToken);
```

The wrapper methods return only `Result`, so callers do not receive status or headers; choose `IRestClient` or `HttpClient`/`RestObject` when those are required. Streaming wrapper methods do not expose their `HttpResponseMessage` to the caller, so consume the enumerator promptly and do not retain it past the request operation. The retry loop also does not explicitly dispose the `HttpResponseMessage` objects held by `RestObject`/`StreamingRestObject`, including failed-attempt responses; this matters for long-lived callers and is a current ownership limitation of the wrapper.

## Response stream readers and chunking

### Read a complete response

`ReadResponseStream<TResponse>` recognizes:

- exact MessagePack content type `application/x-msgpack`;
- exact MemoryPack `application/x-memorypack`;
- content types containing `json`;
- content types containing `text`;

GZip and Brotli response encodings are decompressed for JSON, MemoryPack, and text. MessagePack is assumed to have its own serializer compression. Deserialization/parsing exceptions are logged and result in `default`; the method does not throw most content errors. A seekable stream with length at most one returns default.

```csharp
await using Stream complete = File.OpenRead("item.bin");
Item? decoded = await complete.ReadResponseStream<Item>(
    contentType: "application/x-msgpack",
    contentEncoding: null,
    useNewtonsoftDeserializer: false,
    jsonSerializerOptions: null,
    messagePackSerializerOptions: MessagePackSerializerOptions.Standard,
    cancellationToken: cancellationToken);
```

For JSON this can instead use Newtonsoft:

```csharp
Item? fromJson = await jsonStream.ReadResponseStream<Item>(
    "application/json; charset=utf-8",
    "gzip",
    useNewtonsoftDeserializer: true,
    cancellationToken: cancellationToken);
```

The complete-reader caller retains ownership of the input stream except where a `StreamReader` is used for text (that reader disposes its source); do not reuse the stream after calling it with text. Dispose decompressed/request streams when the helper no longer needs them.

### Read an asynchronous stream

`ReadResponseStreamAsync` supports MessagePack as a concatenated sequence of top-level values, MemoryPack streaming, and System.Text.Json async-enumerable JSON. It yields non-null items and disposes its active input/decompression stream in `finally`; the caller owns the original response/HTTP transport, and must enumerate or dispose the async enumerator promptly.

```csharp
await using Stream messagePackSequence = File.OpenRead("items.msgpack");
await foreach (Item? item in messagePackSequence.ReadResponseStreamAsync<Item>(
    "application/x-msgpack",
    contentEncoding: null,
    jsonSerializerOptions: null,
    messagePackSerializerOptions: MessagePackSerializerOptions.Standard,
    cancellationToken))
{
    Console.WriteLine(item?.Name);
}
```

JSON call example:

```csharp
await foreach (Item? item in jsonArrayStream.ReadResponseStreamAsync<Item>(
    "application/json",
    contentEncoding: "br",
    jsonSerializerOptions: new(JsonSerializerDefaults.Web),
    cancellationToken: cancellationToken))
{
    Console.WriteLine(item?.Name);
}
```

### Compute chunk sizes

`GetChunkingParameters` increases `itemsPerChunk` in steps of 1000 until the number of chunks is below `bufferLimit`.

```csharp
(int perChunk, int chunks) =
    RestHelpersStatic.GetChunkingParameters(
        itemCount: 1_000_000,
        startingItemsPerChunk: 10_000,
        bufferLimit: 8192);
```

## MessagePack/NDJSON server streaming

`MessagePackStreaming.Stream` and the `StreamMessagePack` extension supplied by `MessagePackStreamingExtensions` for `ControllerBase` both create the same public `MessagePackStreamingResult<T>` for an `IAsyncEnumerable<T>`.

Content negotiation is:

- `application/x-msgpack` or `application/msgpack` → concatenated MessagePack values;
- `application/json`, `application/x-ndjson`, `application/*`, or `*/*` → NDJSON;
- missing `Accept` → NDJSON;
- anything else → 400 text response.

JSON responses use `application/x-ndjson`, with each serialized item followed by `\n`. MessagePack responses use `application/x-msgpack`, and one complete MessagePack value is written/flushed per item. The result sets the success status before iteration, and uses the empty status when iteration produces no item. Client disconnect cancellation maps to status 499 when the response permits; other exceptions remain exceptions and become 500 only if the response has not started.

```csharp
using CommonNetFuncs.Web.Requests.MessagePack;
using MessagePack;

IAsyncEnumerable<Item> source = LoadItemsAsync(cancellationToken);

MessagePackStreamingResult<Item> minimal =
    MessagePackStreaming.Stream(
        source,
        MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray),
        new JsonSerializerOptions(JsonSerializerDefaults.Web),
        successStatusCode: StatusCodes.Status202Accepted,
        emptyStatusCode: StatusCodes.Status204NoContent);

MessagePackStreamingResult<Item> controllerResult =
    this.StreamMessagePack(
        source,
        messagePackOptions: null,
        jsonOptions: null,
        successStatusCode: StatusCodes.Status200OK,
        emptyStatusCode: StatusCodes.Status404NotFound);
```

Both execution entry points are public:

```csharp
MessagePackStreamingResult<Item> result =
    MessagePackStreaming.Stream(source);

await result.ExecuteAsync(httpContext);
await result.ExecuteResultAsync(actionContext);
```

When MessagePack options are omitted, the implementation looks for an MVC `MessagePackOutputFormatter`, reflects an options field, and otherwise uses `MessagePackSerializerOptions.Standard`. Explicit options take precedence. This reflection-based lookup depends on the formatter having a field of exactly `MessagePackSerializerOptions`.

Consume a MessagePack stream with the client stream API:

```csharp
RequestOptions<object?> request = new()
{
    Url = "https://api.example.test/items/stream",
    HttpMethod = HttpMethod.Get,
    HttpHeaders = RestHelperConstants.MsgPackHeaders
};

await foreach (Item? item in client.StreamingRestRequest<Item, object?>(
    request, cancellationToken))
{
    Console.WriteLine(item?.Name);
}
```

The helper does not buffer a complete stream, but MessagePack streaming and NDJSON streaming use different wire framing. Ensure the client protocol matches `application/x-msgpack` or `application/x-ndjson`; do not assume every arbitrary JSON stream is a top-level JSON array.

## JSON Patch

### Generate a patch

`PatchCreator.CreatePatch` serializes both objects with Newtonsoft.Json, recursively compares object properties, and emits add/remove/replace operations. It accepts an optional serializer; its default settings ignore reference loops.

```csharp
using CommonNetFuncs.Web.Requests;
using Microsoft.AspNetCore.JsonPatch;
using Newtonsoft.Json;

Item original = new("before") { Count = 1 };
Item modified = original with { Name = "after", Count = 2 };

JsonPatchDocument defaultPatch =
    PatchCreator.CreatePatch(original, modified);

JsonSerializer customSerializer = JsonSerializer.Create(
    new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Include
    });
JsonPatchDocument customPatch =
    PatchCreator.CreatePatch(original, modified, customSerializer);
```

Nested `JObject` properties are compared recursively. Arrays are compared as serialized token values and therefore generally become one `replace` operation, not element-level JSON Patch operations. Paths use serializer property names and are not JSON Pointer-escaped here; validate unusual property names and resulting paths if user-supplied serializer mappings contain `/` or `~`.

### Install the MVC input formatter

```csharp
using CommonNetFuncs.Web.Requests;

builder.Services.AddControllers(options =>
    options.InputFormatters.Insert(
        0, JsonPatchFormatter.JsonPatchInputFormatter()));
```

The formatter is backed by a newly created service provider and uses Newtonsoft's `DefaultContractResolver`, ignores reference loops, and ignores null values. Insert it ahead of competing patch formatters.

## Distributed cache extensions

`DistributedCacheExtensions` serializes with fixed System.Text.Json settings: Pascal/property names are unchanged, JSON is indented, trailing commas are allowed on read, and null properties are omitted when written.

```csharp
using CommonNetFuncs.Web.Requests;
using Microsoft.Extensions.Caching.Distributed;

Item cached = new("cached");

await cache.SetAsync("item:1", cached);
await cache.SetAsync(
    "item:2",
    cached,
    new DistributedCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
    });
bool syncHit = cache.TryGetValue("item:1", out Item? syncValue);
Item? asyncValue = await cache.TryGetValueAsync<Item>("item:2");
```

These extension signatures do not expose cancellation tokens, despite the underlying `IDistributedCache` API.

`TryGetValueAsync` strips one leading UTF-8 BOM before deserialization. The synchronous method does not perform that explicit strip. Both return `false`/null for a missing key; malformed JSON propagates a JSON deserialization exception.

## Other public models

### MessagePack/MemoryPack data sample

`CommonNetFuncs.Web.Requests.AsyncIntString` inherits `AsyncIntString` from CommonNetFuncs.Core and adds MemoryPack and MessagePack metadata. It exposes `AsyncInt`, `AsyncDecimal`, `AsyncFloat`, and `AsyncString`.

```csharp
using CommonNetFuncs.Web.Requests;
using MessagePack;
using MemoryPack;

AsyncIntString value = new()
{
    AsyncInt = 1,
    AsyncDecimal = 2.5m,
    AsyncFloat = 3.5f,
    AsyncString = "value"
};

byte[] messagePack = MessagePackSerializer.Serialize(value);
byte[] memoryPack = MemoryPackSerializer.Serialize(value);
```

### Problem details with errors

```csharp
using CommonNetFuncs.Web.Requests;

ProblemDetailsWithErrors problem = new()
{
    Title = "Validation failed",
    Status = StatusCodes.Status400BadRequest,
    Errors =
    {
        ["Name"] = ["Name is required."]
    }
};
```

## License

This project is licensed under the MIT License. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
