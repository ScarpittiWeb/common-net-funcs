# CommonNetFuncs.Web.Middleware

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Middleware)](https://www.nuget.org/packages/CommonNetFuncs.Web.Middleware/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Middleware)](https://www.nuget.org/packages/CommonNetFuncs.Web.Middleware/)

ASP.NET Core middleware, filters, and route helpers for JSON Patch binding, opt-in in-memory response caching, CORS/`OPTIONS` handling, response headers, slow/large-response logging, and publishing antiforgery request tokens. The components do not provide authentication, authorization, or complete security policies.

## Contents

- [Target framework](#target-framework)
- [Installation](#installation)
- [Pipeline order and trust boundaries](#pipeline-order-and-trust-boundaries)
- [JSON Patch support](#json-patch-support)
- [In-memory response caching](#in-memory-response-caching)
  - [Registration and middleware](#registration-and-middleware)
  - [Cache key and controls](#cache-key-and-controls)
  - [Metrics and administrative endpoints](#metrics-and-administrative-endpoints)
- [OPTIONS and CORS headers](#options-and-cors-headers)
- [Custom response headers](#custom-response-headers)
- [Slow-response logging](#slow-response-logging)
  - [Minimal API endpoint filter](#minimal-api-endpoint-filter)
  - [MVC action filter](#mvc-action-filter)
- [Response-size logging](#response-size-logging)
- [XSRF token cookie](#xsrf-token-cookie)
- [Public API summary](#public-api-summary)
- [License](#license)

---

## Target framework

The current package targets **.NET 11.0**.

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Middleware
```

## Pipeline order and trust boundaries

Middleware order matters:

- Put `UseJsonPatchRequestBody()` before endpoint/model binding.
- Put `UseMemoryValueCaching()` in the desired caching scope. It can bypass all downstream middleware and endpoint execution on a cache hit.
- Put `UseOptions()` before components that should not run for preflight `OPTIONS` requests; the middleware terminates those requests.
- `UseCustomHeaders()` removes and adds configured headers before calling the next middleware. Later middleware or endpoint code can still add or replace a header, so place it and scope it according to the desired behavior.
- `UseResponseSizeLogging()` must wrap the components whose response bytes it should count. It replaces `Response.Body` for the duration of downstream execution.
- `UseXsrfToken()` must run while antiforgery services are available. It publishes a token cookie; it does not validate unsafe requests.

Never cache personalized, tenant-specific, credential-bearing, or otherwise sensitive responses unless the cache key and deployment model prove that sharing is safe. The current cache key does **not** include the request method, host, authenticated user, `Authorization`, or cookies.

## JSON Patch support

`JsonPatchRequestMiddleware` changes an incoming `Content-Type` containing `application/json-patch+json` to `application/json; charset=utf-8`. `JsonPatchDocumentConverterFactory` and `JsonPatchDocumentConverter<T>` bridge System.Text.Json to `Microsoft.AspNetCore.JsonPatch`'s Newtonsoft.Json wire format. JSON Patch is a data-mutation format, not authorization: authorize the endpoint, validate ownership, constrain allowed patch paths, and validate the resulting model.

Register the converter in every System.Text.Json options instance that binds patches. Minimal APIs use `ConfigureHttpJsonOptions`; MVC controllers use `AddJsonOptions`.

```csharp
using CommonNetFuncs.Web.Middleware;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonPatchDocumentConverterFactory());
});

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonPatchDocumentConverterFactory());
});

WebApplication app = builder.Build();

// Before endpoint/model binding.
app.UseJsonPatchRequestBody();

app.MapPatch("/items/{id:int}", (int id, JsonPatchDocument<Item> patch) =>
{
    Item item = FindItem(id);       // Application authorization and lookup.
    patch.ApplyTo(item);            // Validate ModelState/a resulting model in production.
    return Results.Ok(item);
});

app.MapControllers();
app.Run();

public sealed class Item
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

static Item FindItem(int id) => new() { Id = id, Name = "old" };
```

The public converter and middleware types can also be used directly:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using CommonNetFuncs.Web.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.JsonPatch;

var factory = new JsonPatchDocumentConverterFactory();
bool supported = factory.CanConvert(typeof(JsonPatchDocument<Item>));
JsonConverter? genericConverter = factory.CreateConverter(
    typeof(JsonPatchDocument<Item>),
    new JsonSerializerOptions());

var options = new JsonSerializerOptions();
options.Converters.Add(factory);

JsonPatchDocument<Item> patch = new();
patch.Replace(x => x.Name, "new");
string json = JsonSerializer.Serialize(patch, options); // Calls converter Write(...).
JsonPatchDocument<Item>? read =
    JsonSerializer.Deserialize<JsonPatchDocument<Item>>(json, options); // Calls Read(...).

RequestDelegate next = context =>
{
    Console.WriteLine(context.Request.ContentType);
    return Task.CompletedTask;
};
var middleware = new JsonPatchRequestMiddleware(next);
var context = new DefaultHttpContext();
context.Request.ContentType = "application/json-patch+json";
await middleware.InvokeAsync(context);
```

## In-memory response caching

The cache is process-local, opt-in, and backed by `IMemoryCache`. It caches only HTTP 200 responses. A cache hit writes cached bytes and configured response headers and skips the downstream pipeline.

### Registration and middleware

Use `MemoryValueCaching()` to register `IMemoryCache` (only when absent), internal cache tracking, and `CacheMetrics`. Pass `trackMetrics: true` to `UseMemoryValueCaching()` in the current implementation: its soft-size/capacity path relies on `CacheMetrics` to decide whether space is available. With `trackMetrics: false`, the current size calculation cannot establish available space and responses may be skipped rather than cached.

```csharp
using System.Collections.Immutable;
using CommonNetFuncs.Web.Common.CachingSupportClasses;
using CommonNetFuncs.Web.Middleware.CachingMiddleware;
using Microsoft.Extensions.Caching.Memory;
using static CommonNetFuncs.Compression.Streams;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.MemoryValueCaching();
builder.Services.Configure<MemoryCacheOptions>(options =>
    options.SizeLimit = 512 * 1024 * 1024);

WebApplication app = builder.Build();

CacheOptions options = new()
{
    EvictionQueryParam = "evictCacheEntry",
    UseCacheQueryParam = "useCache",
    CacheSecondsQueryParam = "cacheSeconds",
    CacheMinutesQueryParam = "cacheMinutes",
    CacheHoursQueryParam = "cacheHours",
    CacheTagHeader = "X-Cache-Tags",
    EvictTagQueryParam = "evictTags",
    DefaultCacheDuration = TimeSpan.FromMinutes(5),
    MaxCacheSizeInBytes = 100 * 1024 * 1024,
    HeadersToCache = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Content-Type", "Content-Encoding", "Cache-Control", "Vary"),
    UseCompression = true,
    CompressionType = ECompressionType.Gzip,
    SuppressLogs = true
};

app.UseMemoryValueCaching(options, trackMetrics: true);
app.MapGet("/catalog", () => Results.Json(GetCatalog()));
app.Run();

static object GetCatalog() => new { Updated = DateTimeOffset.UtcNow };
```

If the application needs different query/header names or behavior, every settable `CacheOptions` property shown above is available. Defaults are 5 minutes, 100 MiB soft cache size, GZip compression, `SuppressLogs = true`, and the displayed control names/header.

### Cache key and controls

The default request controls are:

| Control | Behavior |
| --- | --- |
| `useCache=true` | Look up a response and populate the cache after a 200 response. |
| `evictCacheEntry=true` | Evict the matching key before processing. The eviction control itself is excluded from the generated key. |
| `evictTags=tag-a,tag-b` plus `evictCacheEntry=true` | Evict all tracked entries having any listed tag instead of only the matching key. |
| `cacheSeconds=<n>` | Override expiration by the specified seconds. |
| `cacheMinutes=<n>` | Override expiration by the specified minutes. |
| `cacheHours=<n>` | Override expiration by the specified hours. |
| `X-Cache-Tags: tag-a, tag-b` | Associate tags with a newly cached entry. |

The three duration values are added together; invalid values are ignored. A combined value of zero uses `DefaultCacheDuration`. Query keys are sorted for key generation. The key contains the path and selected query values, plus an SHA-256 hash of the request body for `POST`, and the `Accept` header. It does not contain the method, host, user, authorization, cookies, other headers, or request body for non-`POST` methods. Do not expose cache-control or eviction query parameters to untrusted callers without an appropriate policy.

```http
GET /catalog?useCache=true&cacheMinutes=10
X-Cache-Tags: catalog,public

GET /catalog?useCache=true&evictCacheEntry=true
POST /search?useCache=true&cacheSeconds=30
Accept: application/json
```

Only the 200 status is retained; non-200 responses still pass through. Compression stores a compressed byte array in process and decompresses it on cache hits. `IMemoryCache` size accounting, on the other hand, is configured with the original response length. If `MaxCacheSizeInBytes` is exceeded and tracked entries cannot be freed, the new response is not cached.

The middleware suppresses `TaskCanceledException` only on the branch where no cache operation was requested and `SuppressLogs` controls only whether that swallowed cancellation is logged. Cancellation during cache population or lookup is not generally swallowed.

### Metrics and administrative endpoints

`MapCacheMetrics()` adds:

```text
GET /api/memory-cache-metrics
```

The response includes hits, misses, hit ratio, skipped entries, current byte/human-readable size, entry count, tag counts, and manual/capacity eviction counts. This endpoint has no built-in authorization; protect it at an outer scope when it exposes operational data.

`MapEvictionEndpoints()` adds:

```text
POST /api/memorycache/evict/key/{key}
POST /api/memorycache/evict/tag/{tag}
POST /api/memorycache/evict/all
```

Key/tag endpoints return the number evicted, `0` when not found, and 400 for whitespace route values. The all endpoint works only with the concrete `MemoryCache` implementation and returns the pre-clear count. Supplying an authorization policy applies it to all three endpoints:

```csharp
builder.Services.AddAuthorization(options =>
    options.AddPolicy("cache-admin", policy =>
        policy.RequireRole("CacheAdministrator")));

WebApplication app = builder.Build();
app.UseMemoryValueCaching(trackMetrics: true);
app.UseAuthentication();
app.UseAuthorization();

RouteGroupBuilder cacheAdmin = app.MapGroup(string.Empty);
cacheAdmin.RequireAuthorization("cache-admin");
cacheAdmin.MapCacheMetrics();
cacheAdmin.MapEvictionEndpoints("cache-admin");
```

## OPTIONS and CORS headers

`UseOptions()` is a small CORS helper, not ASP.NET Core's full CORS service. It does not validate an origin against an allow-list: for `OPTIONS`, it echoes the request `Origin`, or uses `defaultAllowedOrigin` when the request has no `Origin`; for other requests with an `Origin`, it reflects that origin and the configured credentials flag. It terminates every `OPTIONS` request without calling downstream middleware.

All `allowedHeaders` and `allowedMethods` items are joined with `", "`; methods are uppercased. Defaults are origin `*`, no headers/methods, credentials `true`, max-age 3600, and status 200. Reflecting arbitrary origins together with credentials can be unsafe. A literal `*` is also incompatible with credentialed browser CORS. Configure a real allow-list with ASP.NET Core CORS when that is required.

Although the `maxAge` parameter is nullable in the extension signature, the concrete middleware requires an integer, so callers should provide a value rather than `null`.

```csharp
using System.Net;
using CommonNetFuncs.Web.Middleware;
using Microsoft.AspNetCore.Http;

WebApplication app = builder.Build();

// Extension overload (only one overload; all arguments are optional).
app.UseOptions(
    defaultAllowedOrigin: "https://example.com",
    allowedHeaders: ["Content-Type", "Authorization", "X-XSRF-TOKEN"],
    allowedMethods: ["GET", "POST", "PATCH"],
    allowCredentials: true,
    maxAge: 3600,
    defaultStatusCode: HttpStatusCode.NoContent);

// The public middleware itself can be constructed and invoked directly.
RequestDelegate next = _ => Task.CompletedTask;
var optionsMiddleware = new OptionsMiddleware(
    next, "https://example.com", ["Content-Type"], ["GET", "OPTIONS"],
    allowCredentials: true, maxAge: 3600, HttpStatusCode.OK);
var context = new DefaultHttpContext();
context.Request.Method = HttpMethods.Options;
context.Request.Headers.Origin = "https://example.com";
await optionsMiddleware.InvokeAsync(context);
```

## Custom response headers

`UseCustomHeaders()` removes each listed header and then uses `TryAdd` for each configured addition before invoking downstream middleware. An existing header is not overwritten, and a downstream component can still modify response headers. Some server-level headers may be controlled by the host and may not be removable here.

```csharp
using CommonNetFuncs.Web.Middleware;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http;

WebApplication app = builder.Build();
app.UseCustomHeaders(
    addHeaders: new Dictionary<string, string>
    {
        ["X-Content-Type-Options"] = "nosniff",
        ["Referrer-Policy"] = "no-referrer"
    },
    removeHeaders: ["X-Powered-By"]);

RequestDelegate next = _ => Task.CompletedTask;
var middleware = new UseCustomHeadersMiddleware(
    next,
    addHeaders: new Dictionary<string, string> { ["X-Frame-Options"] = "DENY" },
    removeHeaders: ["Server"]);
var context = new DefaultHttpContext();
await middleware.InvokeAsync(context);
```

## Slow-response logging

`IResponseLoggingConfig.ThresholdInSeconds` is read-only; `ResponseLoggingConfig` provides the settable implementation. A warning is emitted at `LogLevel.Warning` when elapsed time is greater than or equal to the threshold and warning logging is enabled.

### Minimal API endpoint filter

`UseResponseLoggingEndpointFilter` measures the call to the next endpoint filter. Its public `InvokeAsync` returns the downstream result and propagates downstream exceptions (so no warning is written if the call throws).

```csharp
using CommonNetFuncs.Web.Middleware;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IResponseLoggingConfig>(
    new ResponseLoggingConfig { ThresholdInSeconds = 2 });
builder.Services.AddSingleton<UseResponseLoggingEndpointFilter>();

WebApplication app = builder.Build();
app.MapGet("/slow", async () =>
{
    await Task.Delay(TimeSpan.FromSeconds(3));
    return Results.Ok();
}).AddEndpointFilter<UseResponseLoggingEndpointFilter>();
```

### MVC action filter

`UseResponseLoggingFilter` starts a shared `Stopwatch` in `OnActionExecuting` and checks it in `OnActionExecuted`. The filter's normal execution model is one MVC action invocation at a time. Exceptions before the executed callback can prevent the log.

```csharp
using CommonNetFuncs.Web.Middleware;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IResponseLoggingConfig>(
    new ResponseLoggingConfig { ThresholdInSeconds = 1 });
builder.Services.AddControllers(options =>
    options.Filters.Add<UseResponseLoggingFilter>());
```

## Response-size logging

`UseResponseSizeLogging()` wraps the downstream response body in a counting stream and writes a warning after downstream execution when bytes written exceed `logThreshold`. The message includes path, method, `Accept`, encoded `Accept-Encoding`, and a human-readable size. The default threshold is `-1`, so any completed response with zero or more bytes is eligible for a warning; pass an explicit positive threshold in normal use.

The component is documented by its own source as development-oriented. It changes `Response.Body` and disposes the counting stream and the original response body in `finally`. Do not add it blindly to production pipelines.

```csharp
using CommonNetFuncs.Web.Middleware;
using Microsoft.AspNetCore.Http;

WebApplication app = builder.Build();
app.UseResponseSizeLogging(logThreshold: 1024 * 1024);

RequestDelegate next = async context =>
    await context.Response.WriteAsync("response");
ILogger<UseResponseSizeLoggingMiddleware> logger =
    app.Services.GetRequiredService<ILogger<UseResponseSizeLoggingMiddleware>>();
var middleware = new UseResponseSizeLoggingMiddleware(next, logger, 1024);
var context = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
await middleware.InvokeAsync(context);
```

## XSRF token cookie

`UseXsrfToken()` calls `IAntiforgery.GetAndStoreTokens` for every request and appends a response cookie named `XSRF-TOKEN` containing the antiforgery request token. Default cookie flags are `HttpOnly = false` and `Secure = true`.

This middleware only publishes a token. It does not validate the token on unsafe requests. Configure antiforgery validation (for example, MVC antiforgery validation or an appropriate minimal-API endpoint filter), use HTTPS, and understand that `HttpOnly = false` is required only when JavaScript must read the token and therefore exposes it to script in the origin. The package does not set a custom `SameSite` policy.

```csharp
using CommonNetFuncs.Web.Middleware;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddAntiforgery();

WebApplication app = builder.Build();
app.UseXsrfToken(httpOnly: false, secure: true);

var directMiddleware = new UseXsrfTokenMiddleware(
    next: _ => Task.CompletedTask,
    antiforgery: app.Services.GetRequiredService<IAntiforgery>(),
    httpOnly: false,
    secure: true);
await directMiddleware.InvokeAsync(new DefaultHttpContext
{
    RequestServices = app.Services
});
```

## Public API summary

| Type | Public members |
| --- | --- |
| `JsonPatchRequestMiddleware` | Constructor; `InvokeAsync(HttpContext)` |
| `JsonPatchDocumentConverterFactory` | `CanConvert(Type)`; `CreateConverter(Type, JsonSerializerOptions)` |
| `JsonPatchDocumentConverter<T>` | `Read(...)`; `Write(...)` |
| `JsonPatchEndpointSupportExtensions` | `UseJsonPatchRequestBody()` |
| `MemoryCacheEvictionMiddlewareExtensions` | `MemoryValueCaching()`; `UseMemoryValueCaching(CacheOptions?, bool)`; `MapCacheMetrics()`; `MapEvictionEndpoints(string?)` |
| `OptionsMiddlewareExtensions` | `UseOptions(...)` with optional CORS arguments |
| `OptionsMiddleware` | Constructor; `InvokeAsync(HttpContext)` |
| `UseCustomHeadersMiddlewareExtension` | `UseCustomHeaders(IDictionary<string,string>?, IEnumerable<string>?)` |
| `UseCustomHeadersMiddleware` | Constructor; `InvokeAsync(HttpContext)` |
| `UseResponseLoggingEndpointFilter` | Constructor; `InvokeAsync(...)` |
| `UseResponseLoggingFilter` | Constructor; `OnActionExecuting(...)`; `OnActionExecuted(...)` |
| `ResponseSizeLoggingMiddlewareExtension` | `UseResponseSizeLogging(long)` |
| `UseResponseSizeLoggingMiddleware` | Constructor; `InvokeAsync(HttpContext)` |
| `UseXsrfTokenMiddlewareExtension` | `UseXsrfToken(bool, bool)` |
| `UseXsrfTokenMiddleware` | Constructor; `InvokeAsync(HttpContext)` |
| `IResponseLoggingConfig` | `ThresholdInSeconds` |
| `ResponseLoggingConfig` | Settable `ThresholdInSeconds` |

The cache options, cache entry, and metrics types used by the caching extensions are public types from the package dependency `CommonNetFuncs.Web.Common`.

## License

Licensed under MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
