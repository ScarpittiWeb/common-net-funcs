# CommonNetFuncs.Web.Common

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Common)](https://www.nuget.org/packages/CommonNetFuncs.Web.Common/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Common)](https://www.nuget.org/packages/CommonNetFuncs.Web.Common/)

Shared ASP.NET Core MIME, URI, JSON, security-header, validation, and response-cache support types. The package targets **.NET 11.0**.

These helpers do not authenticate or authorize requests, validate uploaded file contents, or establish application-specific security policy.

## Contents

- [Installation](#installation)
- [Content types and encodings](#content-types-and-encodings)
- [URI helpers](#uri-helpers)
- [JSON naming](#json-naming)
- [Security headers](#security-headers)
- [Validation attributes](#validation-attributes)
- [Caching support types](#caching-support-types)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Common
```

## Content types and encodings

`ContentTypes` provides the following public constants:

| Area | Members |
| --- | --- |
| JSON and form data | `Json`, `JsonProblem`, `MemPack`, `MsgPack`, `UrlEncodedFormData`, `MultiPartFormData`, `FormDataTypes` |
| Images | `Png`, `Jpeg`, `Gif`, `Bmp`, `Tiff`, `Svg` |
| Office/document/archive | `Xlsx`, `Xls`, `Docx`, `Doc`, `Pptx`, `Ppt`, `Pdf`, `Csv`, `Zip`, `MsZipUpload` |
| Media | `TransportStream`, `Mpeg`, `Mp4`, `Webm`, `Flv`, `Avi`, `Mp3`, `Aac` |
| Web/general | `Html`, `Text`, `Js`, `AppJs`, `Css`, `Xhtml`, `Xml`, `BinaryStream` |

`EncodingTypes` provides `Identity`, `Brotli`, `GZip`, and `Deflate`.

Both lookup methods are case-insensitive for known extensions. `GetContentType` throws `ArgumentException` for a null/empty file name or a file name without an extension. `GetContentTypeByExtension` accepts an extension with or without the leading dot and throws `ArgumentException` for null/empty input. Unknown extensions return `application/octet-stream`; MIME lookup is not content validation.

```csharp
using CommonNetFuncs.Web.Common;

string fromFile = "REPORT.XLSX".GetContentType();
string fromExtension = ContentTypes.GetContentTypeByExtension("mp4");
string json = ContentTypes.Json;
string gzip = EncodingTypes.GZip;

// Some fallback matches are prefix-based and culture-sensitive:
string html = ContentTypes.GetContentTypeByExtension(".html-page");
```

## URI helpers

`UriHelpers` supplies extension methods for query construction, date conversion, query-value redaction, and request URI reconstruction.

> The query-string builders append keys and values directly. They do not URL-encode keys or values. Encode untrusted or reserved characters before using these methods. Redaction replaces **all** query values, but does not authorize the destination; `Uri` parsing can throw for an invalid URL.

### Query parameters

```csharp
using CommonNetFuncs.Web.Common;

// IEnumerable<T> + shared parameter name
string repeated = new[] { 1, 2, 3 }.ListToQueryParameters("id");
// id=1&id=2&id=3

// IEnumerable<KeyValuePair<string, T>>
string pairs = new KeyValuePair<string, int>[]
{
    new("limit", 20),
    new("page", 2)
}.ListToQueryParameters();
// limit=20&page=2

string none = Array.Empty<int>().ListToQueryParameters("id"); // ""
```

### URI-safe dates

The default formats are `yyyy-MM-ddTHH:mm:ss` (`TimestampUrlFormat`) and `yyyy-MM-dd` (`DateOnlyUrlFormat`) from the Core package. Formatting uses the input overload's normal `ToString`/nullable-helper behavior; parsing uses `InvariantCulture` and `DateTimeStyles.None`.

```csharp
using CommonNetFuncs.Web.Common;

DateTime instant = new(2026, 11, 20, 10, 30, 0);
DateTime? nullableInstant = instant;
DateOnly date = new(2026, 11, 20);
DateOnly? nullableDate = date;

// Non-nullable and nullable DateTime overloads
string timestamp = instant.ToUriSafeString();
string? nullableTimestamp = nullableInstant.ToUriSafeString();
string customTimestamp = instant.ToUriSafeString("yyyyMMddHHmmss");

// Non-nullable and nullable DateOnly overloads
string day = date.ToUriSafeString();
string? nullableDay = nullableDate.ToUriSafeString("yyyyMMdd");

DateTime? parsedInstant = timestamp.ParseUriSafeDateTime();
DateTime? parsedCustomInstant = customTimestamp.ParseUriSafeDateTime("yyyyMMddHHmmss");
DateOnly? parsedDay = day.ParseUriSafeDateOnly();
DateOnly? parsedCustomDay = "20261120".ParseUriSafeDateOnly("yyyyMMdd");

string? missingTimestamp = ((DateTime?)null).ToUriSafeString();
DateTime? invalidTimestamp = "not-a-date".ParseUriSafeDateTime(); // null
```

### Redaction and request URI

`GetRedactedUri` returns the original URL unchanged when it has no query string, returns `null` for null/empty/whitespace input, removes a trailing slash from the path, and replaces every query value with the supplied marker (which defaults to `<REDACTED>`).

```csharp
using CommonNetFuncs.Web.Common;
using Microsoft.AspNetCore.Http;

string? redacted = "https://example.test/items?token=secret&page=2"
    .GetRedactedUri();
string? customRedaction = "https://example.test/items?token=secret"
    .GetRedactedUri("***");

DefaultHttpContext context = new();
context.Request.Scheme = "https";
context.Request.Host = new HostString("example.test");
context.Request.PathBase = "/app";
context.Request.Path = "/items";
context.Request.QueryString = new QueryString("?page=2");

string requestUri = UriHelpers.GetUriFromRequest(context.Request);
// https://example.test/app/items?page=2
```

## JSON naming

`PascalCaseJsonNamingPolicy.ConvertName` uppercases only the first character and only when that character is lowercase. It does not convert snake case, kebab case, or the rest of a camel-case name.

```csharp
using System.Text.Json;
using CommonNetFuncs.Web.Common;

PascalCaseJsonNamingPolicy policy = new();
string personName = policy.ConvertName("personName"); // "PersonName"
string unchanged = policy.ConvertName("alreadyPascal"); // "AlreadyPascal"

JsonSerializerOptions options = new()
{
    PropertyNamingPolicy = policy
};
string json = JsonSerializer.Serialize(new { first_name = "Ada" }, options);
// {"First_name":"Ada"}
```

## Security headers

`SecurityHeadersStore.SecurityHeaders` is a `FrozenDictionary<string,string>` and `HeadersToRemove` is a `List<string>`. It also exposes `XrfHeader` (`X-XSRF-TOKEN`).

The bundled headers include:

- `X-Xss-Protection`
- `X-Frame-Options`
- `Referrer-Policy`
- `X-Content-Type-Options`
- `X-Permitted-Cross-Domain-Policies`
- `Content-Security-Policy`

`HeadersToRemove` contains `Server` and `X-Powered-By`.

The bundled CSP permits `cdnjs.cloudflare.com` and `cdn.jsdelivr.net` over HTTP, enables `upgrade-insecure-requests`, and is **not a production-ready policy**. Review every value for the application's browser, CDN, framing, XSS, and proxy requirements.

```csharp
using CommonNetFuncs.Web.Common;

IReadOnlyDictionary<string, string> headers = SecurityHeadersStore.SecurityHeaders;
List<string> remove = SecurityHeadersStore.HeadersToRemove;
string xsrfHeader = SecurityHeadersStore.XrfHeader;

// Apply through your application's response-header middleware:
// app.UseCustomHeaders(SecurityHeadersStore.SecurityHeaders,
//                       SecurityHeadersStore.HeadersToRemove);
```

## Validation attributes

All attributes target properties, fields, and parameters. Use `Validator.TryValidateObject`/`ValidationContext` (or ASP.NET Core model validation) to invoke them. A valid `ErrorMessage` can be customized through the inherited `ValidationAttribute.ErrorMessage`.

Unless stated otherwise, null and empty values pass string/list regex and deny-character validation.

### Allowed values, including nullable values

`AllowedNullableValuesAttribute` has overloads for an explicit value array and for an enum type. Both allow a nullable value by default; pass `allowNull: false` to reject null.

```csharp
using System.ComponentModel.DataAnnotations;
using CommonNetFuncs.Web.Common.ValidationAttributes;

enum PublishState { Draft, Active, Archived }

sealed class Publication
{
    [AllowedNullableValues("draft", "live")]
    public string? Slug { get; set; }

    [AllowedNullableValues(false, "draft", "live")]
    public string? RequiredSlug { get; set; } = "draft";

    [AllowedNullableValues(typeof(PublishState))]
    public PublishState? State { get; set; }

    [AllowedNullableValues(false, typeof(PublishState))]
    public PublishState? RequiredState { get; set; } = PublishState.Active;
}

Publication model = new();
List<ValidationResult> errors = [];
bool valid = Validator.TryValidateObject(model, new ValidationContext(model), errors, true);
```

### Denied characters

```csharp
using CommonNetFuncs.Web.Common.ValidationAttributes;

sealed class SearchInput
{
    [DenyCharacters("<>")]
    public string? Text { get; set; }
}

var fromString = new DenyCharactersAttribute("<>");
var fromArray = new DenyCharactersAttribute(['<', '>']);
string message = fromString.FormatErrorMessage("Text");

var perItemFromString = new ListDenyCharactersAttribute("<>");
var perItemFromArray = new ListDenyCharactersAttribute(['<', '>']);
string listMessage = perItemFromString.FormatErrorMessage("Tags");
```

### Denied regular expressions

`DenyRegularExpressionAttribute` and `ListDenyRegularExpressionAttribute` default to denying any substring match. Set `denyOnlyFullMatch: true` to deny only a complete regex match. Their `MatchTimeoutInMilliseconds` property defaults to 2000 and is exposed as `MatchTimeout`.

```csharp
using CommonNetFuncs.Web.Common.ValidationAttributes;

sealed class Comment
{
    [DenyRegularExpression(@"<script", false,
        MatchTimeoutInMilliseconds = 500)]
    public string? Body { get; set; }

    [ListDenyRegularExpression(@"\d+", true,
        MatchTimeoutInMilliseconds = 500)]
    public List<string?> Labels { get; set; } = [];
}

var scalar = new DenyRegularExpressionAttribute(@"\d+", true)
{
    MatchTimeoutInMilliseconds = 100
};
string scalarMessage = scalar.FormatErrorMessage("Body");
TimeSpan timeout = scalar.MatchTimeout;

var list = new ListDenyRegularExpressionAttribute(@"\d+", true)
{
    MatchTimeoutInMilliseconds = 100
};
string listRegexMessage = list.FormatErrorMessage("Labels");
```

### Collection length and range

`ListMaxLengthAttribute` supports `ListMaxLength(length)` and parameterless `ListMaxLength()` (the latter uses `-1`, meaning no limit). It requires an `ICollection`; it validates the length of each string item. `ListStringLengthAttribute` validates every string in an `ICollection` between `MinimumLength` and `MaximumLength`; null items pass.

`ListRangeAttribute` supports inclusive `int`, inclusive `double`, and arbitrary `IComparable` operands. `MinimumIsExclusive`, `MaximumIsExclusive`, `ParseLimitsInInvariantCulture`, and `ConvertValueInInvariantCulture` modify its behavior.

```csharp
using System.ComponentModel.DataAnnotations;
using CommonNetFuncs.Web.Common.ValidationAttributes;

sealed class Filters
{
    [ListMaxLength(20)]
    public List<string> Names { get; set; } = [];

    [ListMaxLength] // no configured maximum
    public List<string> UnboundedNames { get; set; } = [];

    [ListStringLength(12, MinimumLength = 2)]
    public List<string?> Codes { get; set; } = [];

    [ListRange(1, 100)]
    public int[] Integers { get; set; } = [];

    [ListRange(0.0, 1.0, MinimumIsExclusive = true)]
    public double[] Fractions { get; set; } = [];

    [ListRange(typeof(decimal), "0.01", "999.99",
        ParseLimitsInInvariantCulture = true,
        ConvertValueInInvariantCulture = true)]
    public decimal[] Prices { get; set; } = [];
}

ListRangeAttribute range = new(typeof(decimal), "0.01", "999.99");
string rangeMessage = range.FormatErrorMessage("Prices");
```

### Regular expressions for every collection item

```csharp
using CommonNetFuncs.Web.Common.ValidationAttributes;

sealed class Identifiers
{
    [ListRegularExpression(@"[A-Z]{2}\d{4}",
        MatchTimeoutInMilliseconds = 250)]
    public List<string?> Values { get; set; } = [];
}

ListRegularExpressionAttribute regex = new(@"[A-Z]{2}\d{4}");
string regexMessage = regex.FormatErrorMessage("Values");
```

List regex and deny-regex helpers apply the 2-second default timeout unless changed. For untrusted regular expressions or unusually long inputs, set a suitable positive timeout. `-1` creates the implementation's built-in 5-second no-timeout-style fallback rather than an infinite `Regex` timeout.

## Caching support types

These types are shared contracts/support data for cache middleware; they do not implement a cache by themselves.

### `CacheEntry`

```csharp
using CommonNetFuncs.Web.Common.CachingSupportClasses;

CacheEntry entry = new()
{
    Data = [1, 2, 3],
    Tags = ["products"],
    Headers = new Dictionary<string, string>
    {
        ["Content-Type"] = "application/json"
    },
    CompressionType = 1
};
```

`Data` starts as an empty byte array, `Tags` as an empty set, and `Headers` as an empty dictionary.

### `CacheOptions` and `CacheOptionDefaults`

```csharp
using System.Collections.Immutable;
using static CommonNetFuncs.Compression.Streams;
using CommonNetFuncs.Web.Common.CachingSupportClasses;

CacheOptions options = new()
{
    EvictionQueryParam = "evict",
    UseCacheQueryParam = "cached",
    CacheSecondsQueryParam = "seconds",
    CacheMinutesQueryParam = "minutes",
    CacheHoursQueryParam = "hours",
    CacheTagHeader = "X-App-Cache-Tags",
    EvictTagQueryParam = "evictTags",
    DefaultCacheDuration = TimeSpan.FromMinutes(10),
    MaxCacheSizeInBytes = 200 * 1024 * 1024,
    HeadersToCache = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Content-Type"),
    UseCompression = true,
    CompressionType = ECompressionType.Gzip,
    SuppressLogs = false
};

string cacheQuery = CacheOptionDefaults.DefaultCacheQueryParam;
long maxSize = CacheOptionDefaults.DefaultCacheSize;
bool cachesVary = CacheOptionDefaults.DefaultHeadersToCache.Contains("vary");
```

Defaults are:

| Setting | Default |
| --- | --- |
| Query/header names | `DefaultEvictionQueryParam = evictCacheEntry`, `DefaultCacheQueryParam = useCache`, `DefaultCacheSecondsQueryParam = cacheSeconds`, `DefaultCacheMinutesQueryParam = cacheMinutes`, `DefaultCacheHoursQueryParam = cacheHours`, `DefaultCacheTagHeader = X-Cache-Tags`, `DefaultEvictTagQueryParam = evictTags` |
| Duration | 5 minutes |
| Maximum size | 100 MiB |
| Cached headers | `Content-Type`, `Content-Language`, `Content-Encoding`, `Cache-Control`, `Vary` (case-insensitive) |
| Compression | enabled, GZip |
| `SuppressLogs` | `true` |

### `CacheMetrics`

All counters are protected by locks. `AddToSize` uses the absolute value of its argument. `SubtractFromSize` does nothing while the size is zero; otherwise a positive value reduces the size and a negative value moves it in the opposite direction, with no lower-bound clamp. `IncrementEviction` only counts `EvictionReason.Removed` and `EvictionReason.Capacity`.

```csharp
using System.Collections.Concurrent;
using CommonNetFuncs.Web.Common.CachingSupportClasses;
using Microsoft.Extensions.Caching.Memory;

ConcurrentDictionary<string, HashSet<string>> tags = new();
CacheMetrics metrics = new(tags); // constructor accepts an optional existing dictionary

metrics.IncrementHits();
metrics.IncrementMisses();
metrics.IncrementCacheEntryCount();
metrics.AddToSize(256);
metrics.IncrementEviction(EvictionReason.Capacity);
metrics.IncrementEviction(EvictionReason.Removed);
metrics.IncrementSkippedDueToSize();

long hits = metrics.CacheHits();
long misses = metrics.CacheMisses();
long entries = metrics.CurrentCacheEntryCount();
long bytes = metrics.CurrentCacheSize();
long capacityEvictions = metrics.EvictedDueToCapacity();
long removedEvictions = metrics.EvictedDueToRemoved();
long skipped = metrics.SkippedDueToSize();

metrics.SubtractFromSize(100);
metrics.DecrementCacheEntryCount();
metrics.CacheTags["products"] = ["item:42"];
metrics.Clear();
```

Validation attribute configuration properties are public:

| Attribute | Public configuration |
| --- | --- |
| `DenyCharactersAttribute` | `DenyCharacters`; inherited `ErrorMessage` |
| `DenyRegularExpressionAttribute` | `Pattern`, `DenyOnlyFullMatch`, `MatchTimeoutInMilliseconds`, read-only `MatchTimeout` |
| `ListDenyCharactersAttribute` | `DenyCharacters`; inherited `ErrorMessage` |
| `ListDenyRegularExpressionAttribute` | `Pattern`, `DenyOnlyFullMatch`, `MatchTimeoutInMilliseconds`, read-only `MatchTimeout` |
| `ListMaxLengthAttribute` | `Length` |
| `ListRangeAttribute` | `Minimum`, `Maximum`, `OperandType`, `MinimumIsExclusive`, `MaximumIsExclusive`, `ParseLimitsInInvariantCulture`, `ConvertValueInInvariantCulture` |
| `ListRegularExpressionAttribute` | `Pattern`, `MatchTimeoutInMilliseconds`, read-only `MatchTimeout` |
| `ListStringLengthAttribute` | `MaximumLength`, `MinimumLength` |

## License

This project is licensed under the MIT License. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
