# CommonNetFuncs.Web.Api

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Api)](https://www.nuget.org/packages/CommonNetFuncs.Web.Api/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Api)](https://www.nuget.org/packages/CommonNetFuncs.Web.Api/)

Generic MVC/minimal-API CRUD helpers, minimal-API validation, MessagePack request/response components, OpenAPI transformers, and sequential/priority task queues. The package targets **.NET 11.0** and integrates with `CommonNetFuncs.EFCore`.

## Contents

- [Installation](#installation)
- [Generic EF endpoint helpers](#generic-ef-endpoint-helpers)
- [Minimal-API validation](#minimal-api-validation)
- [MessagePack](#messagepack)
- [OpenAPI transformers](#openapi-transformers)
- [Task queues](#task-queues)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Api
```

## Generic EF endpoint helpers

The four endpoint classes expose the same operation families with different response shapes:

| Class | Usage | Result shape |
| --- | --- | --- |
| `GenericEndpoints` | MVC instance, entity in/out | `ActionResult<T>` |
| `GenericDtoEndpoints` | MVC instance, DTO in/entity persisted/DTO out | `ActionResult<T>` |
| `GenericMinimalEndpoints` | Minimal API static methods, entity in/out | `Results<...>` typed HTTP results |
| `GenericMinimalDtoEndpoints` | Minimal API static methods, DTO in/entity persisted/DTO out | `Results<...>` typed HTTP results |

The actions require `IBaseDbContextActions<TModel,TContext>` and EF Core. They do not configure authentication, authorization, tenancy, permitted HTTP fields, transaction boundaries, or DTO contracts for the application.

> Verify that the `IBaseDbContextActions` implementation keeps its `DbContext` alive through `SaveChanges`. The default EF action implementation used by this repository has tracked-write/context-lifetime behavior that must be checked before relying on create/update persistence. The endpoint helper calling `Update`, `CreateMany`, or `Delete` and then `SaveChanges` is not itself a guarantee that the same tracked unit of work is saved.

### Result semantics

- A successful `SaveChanges` returns 200 `Ok` with the input/mapped model or an affected-row count.
- A failed save, empty collection where applicable, null count, not-found key, or caught exception generally returns 204 `NoContent`.
- Expression-based `DeleteMany` and `UpdateMany` return 200 even for an affected count of zero when the action returns `0`.
- A thrown exception inside the endpoint helper is logged; these helpers generally convert it to 204.
- A 204 does not establish that a write failed for no other reason—it is also the helper's failure/caught-exception result. Use an explicit wrapper with distinct statuses if clients must distinguish failure from success.

For key-based patch/update calls, the initial `GetByKey` happens before the catch/update helper in the implementation. Lookup exceptions from that operation are therefore not converted to 204 by these overloads; after a model is loaded, patch/update exceptions are caught by the update helper. The MVC direct patch overload taking `TEntity? dbModel` catches its own validation/apply/save operations.

Patch/update methods deep-clone the entity, apply the change, validate the clone with DataAnnotations, copy its properties to the database model, call `Update`, and save. An empty patch returns 200 with the current value. A missing model returns 204. Minimal API methods return a `ValidationProblem` carrying grouped DataAnnotations errors. The MVC variants return a 400 `ValidationProblem`, but currently build it from controller `ModelState` rather than copying the helper's local validation list; therefore detailed patch errors may be absent unless the controller's `ModelState` is populated separately.

Global filter options are available only on the entity `GenericEndpoints` and `GenericMinimalEndpoints` operations shown below; DTO methods do not expose that parameter. `removeNavigationProps` is passed to the EF action and is not a security boundary.

### MVC entity endpoints: every overload

```csharp
using CommonNetFuncs.EFCore;
using CommonNetFuncs.Web.Api;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

public sealed class ItemsController(
    GenericEndpoints endpoints,
    IBaseDbContextActions<Item, AppDbContext> db) : ControllerBase
{
    [HttpPost("many")]
    public Task<ActionResult<IEnumerable<Item>>> CreateMany(
        IEnumerable<Item> models) =>
        endpoints.CreateMany(models, db, removeNavigationProps: true);

    [HttpDelete("one")]
    public Task<ActionResult<Item>> Delete(Item model) =>
        endpoints.Delete(model, db, removeNavigationProps: true,
            globalFilterOptions: tenantFilter);

    [HttpDelete("many")]
    public Task<ActionResult<IEnumerable<Item>>> DeleteMany(
        IEnumerable<Item> models) =>
        endpoints.DeleteMany(models, db,
            removeNavigationProps: false,
            globalFilterOptions: tenantFilter);

    [HttpDelete("matching")]
    public Task<ActionResult<int>> DeleteManyMatching() =>
        endpoints.DeleteMany<Item, AppDbContext>(
            item => item.Archived,
            db,
            tenantFilter,
            HttpContext.RequestAborted);

    [HttpDelete("keys")]
    public Task<ActionResult<IEnumerable<Item>>> DeleteManyByKeys(
        IEnumerable<object> keys) =>
        endpoints.DeleteManyByKeys(keys, db, tenantFilter);

    [HttpPut("matching")]
    public Task<ActionResult<int>> UpdateMany() =>
        endpoints.UpdateMany<Item, AppDbContext>(
            item => !item.Archived,
            setters => setters
                .SetProperty(x => x.Archived, true)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow),
            db,
            tenantFilter,
            HttpContext.RequestAborted);

    [HttpPatch("{id:int}")]
    public Task<ActionResult<Item>> Patch(
        int id, JsonPatchDocument<Item> patch) =>
        endpoints.Patch<Item, AppDbContext>(
            id, patch, db, tenantFilter);

    [HttpPatch("{tenantId:int}/{id:int}")]
    public Task<ActionResult<Item>> PatchComposite(
        int tenantId, int id, JsonPatchDocument<Item> patch) =>
        endpoints.Patch<Item, AppDbContext>(
            new object[] { tenantId, id }, patch, db, tenantFilter);

    [NonAction]
    public Task<ActionResult<Item>> PatchLoaded(
        Item? loaded, JsonPatchDocument<Item> patch) =>
        endpoints.Patch(loaded, patch, db);

    private readonly GlobalFilterOptions tenantFilter = new();
}
```

### Minimal entity endpoints: every overload

The overloads map one-for-one to the MVC methods, but return typed `Ok`/`NoContent` (and `ValidationProblem` for patch).

```csharp
app.MapPost("/items/bulk", (
    IEnumerable<Item> models,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalEndpoints.CreateMany(
        models, db, removeNavigationProps: true));

app.MapDelete("/items/one", (
    Item model, IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalEndpoints.Delete(
        model, db, removeNavigationProps: false, tenantFilter));

app.MapDelete("/items/many", (
    IEnumerable<Item> models, IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalEndpoints.DeleteMany(models, db, false, tenantFilter));

app.MapDelete("/items/archived", (
    IBaseDbContextActions<Item, AppDbContext> db, CancellationToken token) =>
    GenericMinimalEndpoints.DeleteMany<Item, AppDbContext>(
        item => item.Archived, db, tenantFilter, token));

app.MapDelete("/items/keys", (
    IEnumerable<object> values,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalEndpoints.DeleteManyByKeys<Item, AppDbContext>(
        values, db, tenantFilter));

app.MapPut("/items/archive", (
    IBaseDbContextActions<Item, AppDbContext> db, CancellationToken token) =>
    GenericMinimalEndpoints.UpdateMany<Item, AppDbContext>(
        item => !item.Archived,
        setters => setters.SetProperty(x => x.Archived, true),
        db, tenantFilter, token));

app.MapPatch("/items/{id:int}", (
    int id, JsonPatchDocument<Item> patch,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalEndpoints.Patch<Item, AppDbContext>(
        id, patch, db, tenantFilter));

app.MapPatch("/items/{tenantId:int}/{id:int}", (
    int tenantId, int id, JsonPatchDocument<Item> patch,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalEndpoints.Patch<Item, AppDbContext>(
        new object[] { tenantId, id }, patch, db, tenantFilter));

// The loaded-model overload is useful after application-specific lookup:
Item? loaded = await db.GetByKey(1, globalFilterOptions: tenantFilter);
var loadedPatchResult =
    await GenericMinimalEndpoints.Patch(loaded, patch, db);
```

> `GenericMinimalEndpoints.DeleteManyByKeys` currently attempts `models.Cast<TEntity>()` before producing its `Ok<IEnumerable<TEntity>>`; despite accepting `IEnumerable<object>`, each non-null element must therefore actually be compatible with `TEntity`. Composite keys and provider limitations should be tested with the chosen EF provider; `DeleteManyByKeys` is documented in source as not working with PostgreSQL.

### MVC DTO endpoints: every overload

DTO methods map with `FastMap`. `CreateMany`, `Delete`, and `DeleteMany` map input DTOs to entities, persist, and then return output DTOs; they do not use the persisted server-generated entity values when building the response. `Update` copies DTO properties onto a cloned entity and validates the result. `DeleteManyByKeys` maps each key object directly to `TOutDto`, which is usually only useful when the objects already carry the output shape.

```csharp
public sealed class ItemDtosController(
    GenericDtoEndpoints endpoints,
    IBaseDbContextActions<Item, AppDbContext> db) : ControllerBase
{
    [HttpPost("many")]
    public Task<ActionResult<IEnumerable<ItemDto>>> CreateMany(
        IEnumerable<ItemWriteDto> models) =>
        endpoints.CreateMany<Item, AppDbContext, ItemWriteDto, ItemDto>(
            models, db, removeNavigationProps: true);

    [HttpDelete("one")]
    public Task<ActionResult<ItemDto>> Delete(ItemWriteDto model) =>
        endpoints.Delete<Item, AppDbContext, ItemWriteDto, ItemDto>(
            model, db, removeNavigationProps: false);

    [HttpDelete("many")]
    public Task<ActionResult<IEnumerable<ItemDto>>> DeleteMany(
        IEnumerable<ItemWriteDto> models) =>
        endpoints.DeleteMany<Item, AppDbContext, ItemWriteDto, ItemDto>(
            models, db, false);

    [HttpDelete("keys")]
    public Task<ActionResult<IEnumerable<ItemDto>>> DeleteManyByKeys(
        IEnumerable<object> keys) =>
        endpoints.DeleteManyByKeys<Item, AppDbContext, ItemDto>(keys, db);

    [HttpPatch("{id:int}")]
    public Task<ActionResult<ItemDto>> Patch(
        int id, JsonPatchDocument<Item> patch) =>
        endpoints.Patch<Item, AppDbContext, ItemDto>(id, patch, db);

    [HttpPatch("{tenantId:int}/{id:int}")]
    public Task<ActionResult<ItemDto>> PatchComposite(
        int tenantId, int id, JsonPatchDocument<Item> patch) =>
        endpoints.Patch<Item, AppDbContext, ItemDto>(
            new object[] { tenantId, id }, patch, db);

    [HttpPut("{id:int}")]
    public Task<ActionResult<ItemDto>> Update(
        int id, ItemWriteDto dto) =>
        endpoints.Update<Item, AppDbContext, ItemWriteDto, ItemDto>(
            id, dto, db);

    [HttpPut("{tenantId:int}/{id:int}")]
    public Task<ActionResult<ItemDto>> UpdateComposite(
        int tenantId, int id, ItemWriteDto dto) =>
        endpoints.Update<Item, AppDbContext, ItemWriteDto, ItemDto>(
            new object[] { tenantId, id }, dto, db);
}
```

### Minimal DTO endpoints: every overload

```csharp
app.MapPost("/dto/items", (
    IEnumerable<ItemWriteDto> models,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.CreateMany<
        Item, AppDbContext, ItemWriteDto, ItemDto>(
            models, db, removeNavigationProps: true));

app.MapDelete("/dto/items/one", (
    ItemWriteDto model,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.Delete<
        Item, AppDbContext, ItemWriteDto, ItemDto>(model, db, false));

app.MapDelete("/dto/items/many", (
    IEnumerable<ItemWriteDto> models,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.DeleteMany<
        Item, AppDbContext, ItemWriteDto, ItemDto>(models, db, false));

app.MapDelete("/dto/items/keys", (
    IEnumerable<object> keys,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.DeleteManyByKeys<
        Item, AppDbContext, ItemDto>(keys, db));

app.MapPatch("/dto/items/{id:int}", (
    int id, JsonPatchDocument<Item> patch,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.Patch<Item, AppDbContext, ItemDto>(
        id, patch, db));

app.MapPatch("/dto/items/{tenantId:int}/{id:int}", (
    int tenantId, int id, JsonPatchDocument<Item> patch,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.Patch<Item, AppDbContext, ItemDto>(
        new object[] { tenantId, id }, patch, db));

app.MapPut("/dto/items/{id:int}", (
    int id, ItemWriteDto dto,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.Update<
        Item, AppDbContext, ItemWriteDto, ItemDto>(id, dto, db));

app.MapPut("/dto/items/{tenantId:int}/{id:int}", (
    int tenantId, int id, ItemWriteDto dto,
    IBaseDbContextActions<Item, AppDbContext> db) =>
    GenericMinimalDtoEndpoints.Update<
        Item, AppDbContext, ItemWriteDto, ItemDto>(
            new object[] { tenantId, id }, dto, db));
```

DTO mapping does not implement an allowed writable-field policy. Use distinct input DTOs and explicitly authorize keys/filters.

## Minimal-API validation

`ValidationEndpointFilter` validates every endpoint argument that is not:

- null;
- primitive or enum;
- `string`, `decimal`, `Guid`, or a common date/time struct;
- `CancellationToken`, `HttpContext`, `HttpRequest`, `HttpResponse`, `ClaimsPrincipal`, `IServiceProvider`, or `LinkGenerator`;
- assignable to `HttpContext`/`ClaimsPrincipal`;
- registered as a service in `IServiceProviderIsService`.

It uses `Validator.TryValidateObject(..., validateAllProperties: true)`. Validation failures are returned immediately as a 400 `ValidationProblem`, grouped by the first member name; object-level errors use an empty key.

`ValidationEndpointFilterExtensions` applies it to either a route group or an individual route:

```csharp
using CommonNetFuncs.Web.Api;

RouteGroupBuilder itemRoutes = app.MapGroup("/items").WithValidation();
itemRoutes.MapPost("/", (ItemWriteDto dto) => Results.Ok(dto));

app.MapPost("/special-item", (ItemWriteDto dto) => Results.Ok(dto))
   .WithValidation();
```

The filter's constructor requires DI's `IServiceProviderIsService`; `AddEndpointFilter<ValidationEndpointFilter>` obtains it through the service provider. It can also be instantiated directly to show the filter method:

```csharp
IServiceProviderIsService serviceChecker =
    provider.GetRequiredService<IServiceProviderIsService>();
ValidationEndpointFilter filter = new(serviceChecker);
object? result = await filter.InvokeAsync(invocationContext, next);
```

This is DataAnnotations validation, not a substitute for authorization, database constraints, or validation inside services.

## MessagePack

The package splits MessagePack request conversion, minimal-endpoint output filtering, formatters/resolvers, and JSON date handling.

### Request middleware

`UseMsgPackRequestBody` should run before endpoints are mapped/executed. For a request whose `Content-Type` contains `application/x-msgpack`, the middleware reads the body, converts it to JSON with `MessagePackSerializer.ConvertToJson`, and replaces the body with UTF-8 JSON plus `Content-Length`. An empty MessagePack body is left unchanged. Other content types continue directly.

```csharp
using CommonNetFuncs.Web.Api.MsgPack;
using System.Buffers;
using MessagePack;

app.UseMsgPackRequestBody();
app.UseMsgPackRequestBody(MessagePackSerializerOptions.Standard);

app.MapPost("/items", (ItemWriteDto dto) => Results.Ok(dto));
```

Direct middleware construction and invocation are also public:

```csharp
MsgPackRequestMiddleware middleware =
    new(async context => await next(context), MessagePackSerializerOptions.Standard);
await middleware.InvokeAsync(httpContext);
```

MessagePack options and JSON contract must agree between producer and consumer. The middleware allocates/copies the body into memory, so apply request size limits and timeout/cancellation policy externally.

### Minimal-endpoint output filter

`WithMsgPackOutput` applies to either a `RouteHandlerBuilder` or `RouteGroupBuilder`. The filter acts only when `Accept` contains `application/x-msgpack`. It converts results implementing `IValueHttpResult`; no-body results and content types outside the `application/json` prefix—including `application/problem+json`—pass through unchanged.

```csharp
app.MapGet("/items/{id:int}", (int id) => Results.Ok(new Item(id, "one")))
   .WithMsgPackOutput();

RouteGroupBuilder group = app.MapGroup("/items")
    .WithMsgPackOutput(MsgPackSerializerConfig.DateTimesAsStrings);
group.MapGet("/", () => Results.Ok(items));
```

The filter constructor and `InvokeAsync` remain available for direct use:

```csharp
MsgPackOutputFilter filter =
    new(MessagePackSerializerOptions.Standard);
object? filtered = await filter.InvokeAsync(invocationContext, next);
```

The filter writes the concrete runtime type directly to `Response.Body` and preserves the wrapped status code. A null value sets only the status code. Serialization uses `RequestAborted`, which cancels on disconnect.

### Flexible decimal

`DecimalFormatter` serializes `decimal` as an invariant string and accepts a MessagePack string, signed integer, or float when reading. `NullableDecimalFormatter` additionally accepts nil. Float input is converted through its shortest round-trip string before parsing as `decimal`.

```csharp
using CommonNetFuncs.Web.Api.MsgPack;
using MessagePack;

MessagePackSerializerOptions options =
    MessagePackSerializerOptions.Standard.WithResolver(
        CompositeResolver.Create(
            FlexibleDecimalResolver.Instance,
            StandardResolver.Instance));

decimal money = MessagePackSerializer.Deserialize<decimal>(
    MessagePackSerializer.Serialize(12.345m, options), options);

IMessagePackFormatter<decimal>? decimalFormatter =
    FlexibleDecimalResolver.Instance.GetFormatter<decimal>();
IMessagePackFormatter<decimal?>? nullableFormatter =
    FlexibleDecimalResolver.Instance.GetFormatter<decimal?>();

decimal decimalRoundTrip = RoundTrip(
    DecimalFormatter.Instance, 12.345m, options);
decimal? nullableRoundTrip = RoundTrip(
    NullableDecimalFormatter.Instance, (decimal?)4.25m, options);

static T RoundTrip<T>(
    IMessagePackFormatter<T> formatter,
    T value,
    MessagePackSerializerOptions formatterOptions)
{
    ArrayBufferWriter<byte> buffer = new();
    MessagePackWriter writer = new(buffer);
    formatter.Serialize(ref writer, value, formatterOptions);
    writer.Flush();

    MessagePackReader reader = new(buffer.WrittenMemory);
    return formatter.Deserialize(ref reader, formatterOptions);
}
```

The concrete formatter constructors are private; use their singleton `Instance`. Register `FlexibleDecimalResolver` before `StandardResolver` so its decimal registrations take precedence.

### Date/time formatters and resolver

The resolver provides:

- `DateTimeAsStringFormatter`: ISO round-trip (`O`) MessagePack string; `Unspecified` becomes UTC before serialization.
- `TimeSpanAsStringFormatter`: invariant constant (`c`) string.
- `DateTimeOffsetAsStringFormatter`: ISO round-trip string.
- `DateOnlyAsStringFormatter`: `yyyy-MM-dd` output and flexible input.

`DateOnlyAsStringFormatter` additionally accepts standard integer day-number values, MessagePack timestamp extensions, ISO date/date-time prefixes, ISO week/ordinal dates, common numeric dates, and invariant `DateTimeOffset`-parseable strings. A nil or invalid value throws rather than returning a default date.

```csharp
using CommonNetFuncs.Web.Api.MsgPack;

MessagePackSerializerOptions dates =
    MessagePackSerializerOptions.Standard.WithResolver(
        CompositeResolver.Create(
            DateTimeStringResolver.Instance,
            StandardResolver.Instance));

DateTime dt = MessagePackSerializer.Deserialize<DateTime>(
    MessagePackSerializer.Serialize(DateTime.UtcNow, dates), dates);
TimeSpan ts = MessagePackSerializer.Deserialize<TimeSpan>(
    MessagePackSerializer.Serialize(TimeSpan.FromHours(2), dates), dates);
DateTimeOffset dto = MessagePackSerializer.Deserialize<DateTimeOffset>(
    MessagePackSerializer.Serialize(DateTimeOffset.Now, dates), dates);
DateOnly day = MessagePackSerializer.Deserialize<DateOnly>(
    MessagePackSerializer.Serialize(DateOnly.Today, dates), dates);

IMessagePackFormatter<DateTime>? dateFormatter =
    DateTimeStringResolver.Instance.GetFormatter<DateTime>();
```

Direct formatter calls show each public operation:

```csharp
using System.Buffers;

void RoundTripDateTime()
{
    ArrayBufferWriter<byte> buffer = new();
    MessagePackWriter writer = new(buffer);
    DateTimeAsStringFormatter.Instance.Serialize(
        ref writer, DateTime.UtcNow, dates);
    writer.Flush();
    MessagePackReader reader = new(buffer.WrittenMemory);
    DateTime read = DateTimeAsStringFormatter.Instance.Deserialize(
        ref reader, dates);
}

void RoundTripOtherValues()
{
    RoundTrip(TimeSpanAsStringFormatter.Instance, TimeSpan.FromMinutes(5), dates);
    RoundTrip(DateTimeOffsetAsStringFormatter.Instance, DateTimeOffset.UtcNow, dates);
    RoundTrip(DateOnlyAsStringFormatter.Instance, DateOnly.Today, dates);
}

static void RoundTrip<T>(
    IMessagePackFormatter<T> formatter,
    T value,
    MessagePackSerializerOptions formatterOptions)
{
    ArrayBufferWriter<byte> buffer = new();
    MessagePackWriter writer = new(buffer);
    formatter.Serialize(ref writer, value, formatterOptions);
    writer.Flush();
    MessagePackReader reader = new(buffer.WrittenMemory);
    T _ = formatter.Deserialize(ref reader, formatterOptions);
}
```

`DateTimeUtcJsonConverter` supplies matching System.Text.Json behavior: `Unspecified` values are serialized as UTC with a `Z`, and read with `RoundtripKind`.

```csharp
JsonSerializerOptions json = new();
json.Converters.Add(DateTimeUtcJsonConverter.Instance);
string jsonDate = JsonSerializer.Serialize(DateTime.UtcNow, json);
DateTime readDate = JsonSerializer.Deserialize<DateTime>(jsonDate, json);
```

`MsgPackSerializerConfig.DateTimesAsStrings` is a prebuilt `MessagePackSerializerOptions` using untrusted-data security and `DateTimeStringResolver` before `StandardResolver`:

```csharp
MessagePackSerializerOptions preconfigured =
    MsgPackSerializerConfig.DateTimesAsStrings;
```

## OpenAPI transformers

`HeaderTransformer` adds a required `Accept` header parameter to every transformed operation, with schema type string and default `application/json`.

```csharp
using CommonNetFuncs.Web.Api.OpenApiTransformers;
using Microsoft.OpenApi;

builder.Services.AddOpenApi(options =>
{
    options.AddOperationTransformer<HeaderTransformer>();
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

HeaderTransformer headerTransformer = new();
await headerTransformer.TransformAsync(
    operation, operationContext, cancellationToken);
```

`BearerSecuritySchemeOptions.AuthenticationSchemeName` defaults to `Bearer`. `BearerSecuritySchemeTransformer` replaces/sets `document.Components.SecuritySchemes` with a `Bearer` HTTP scheme only when:

- no scheme name is configured (`null`), or
- ASP.NET Core has an authentication scheme with the configured name.

It does not add a security requirement to individual operations; use endpoint/OpenAPI metadata separately if the operation is protected.

```csharp
builder.Services.Configure<BearerSecuritySchemeOptions>(
    options => options.AuthenticationSchemeName = "ApiBearer");

BearerSecuritySchemeTransformer bearer = new(
    authenticationSchemeProvider,
    options: bearerOptions);
await bearer.TransformAsync(document, documentContext, cancellationToken);

BearerSecuritySchemeOptions bearerConfig = new()
{
    AuthenticationSchemeName = "ApiBearer"
};
```

## Task queues

The package has two separate queue designs:

1. **API queue processors**: one application-wide background-service processor (`SequentialTaskProcessor` or `PrioritizedSequentialTaskProcessor`) and a service wrapper.
2. **Endpoint queues**: one queue per endpoint key, managed by `EndpointQueueService` or `PrioritizedEndpointQueueService`.

Nothing is automatically registered in DI. Register only the processor/services used by the application.

### Common task/stat contracts

`QueuedTask` stores the `TaskFunction` delegate, generated `Id`, `CompletionSource`, and UTC `QueuedAt`. `QueueStats`, `PriorityStats`, and `PrioritizedQueueStats` are mutable public DTOs. Prioritized stats expose `TotalQueuedTasks`, `TotalProcessedTasks`, `TotalFailedTasks`, `TotalCancelledTasks`, and the per-priority breakdown. `TaskPriority` values are `Low=0`, `Normal=1`, `High=2`, `Critical=3`, and `Emergency=4`.

```csharp
using CommonNetFuncs.Web.Api.TaskQueuing;
using CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue;

QueuedTask queued = new(token => Task.FromResult<object?>("done"));
queued.CompletionSource.TrySetResult("manual result");

QueueStats totals = new("Items.Index")
{
    QueuedTasks = 10,
    ProcessedTasks = 9,
    FailedTasks = 1,
    LastProcessedAt = DateTime.UtcNow,
    AverageProcessingTime = TimeSpan.FromMilliseconds(20)
};

PriorityStats priorityStats = new()
{
    QueuedTasks = 4,
    ProcessedTasks = 3,
    CancelledTasks = 1
};

PrioritizedQueueStats prioritized = new("Items.Index")
{
    TotalQueuedTasks = 10,
    CurrentQueueDepth = 2,
    CurrentProcessingPriority = TaskPriority.High,
    PriorityBreakdown =
    {
        [TaskPriority.High] = priorityStats
    }
};
```

`PrioritizedQueuedTask` has its own cancellation token source, timeout, numeric priority, priority level, and `IsCancelled`.

```csharp
PrioritizedQueuedTask first = new(_ => Task.FromResult<object?>("first"))
{
    Priority = 10,
    PriorityLevel = TaskPriority.Emergency,
    Timeout = TimeSpan.FromSeconds(10)
};
PrioritizedQueuedTask second = new(_ => Task.FromResult<object?>("second"));
CancellationTokenSource perTaskCancellation = first.CancellationTokenSource;

int ordering = first.CompareTo(second);
bool less = first < second;
bool lessOrEqual = first <= second;
bool greater = first > second;
bool greaterOrEqual = first >= second;
bool sameReference = first == first;
bool equalReference = first.Equals(first);

try
{
    bool differentReference = first != second;
}
catch (NotImplementedException)
{
    // Equality between distinct instances is not implemented.
}

try
{
    int hash = first.GetHashCode();
}
catch (NotImplementedException)
{
    // Hashing is not implemented.
}
```

`CompareTo` orders higher numeric priority first, then older `QueuedAt` first. `==`/`!=` delegate to `Equals`; `Equals` returns true only for the same reference and throws `NotImplementedException` for any distinct non-null object. `GetHashCode` also throws. Do not use distinct `PrioritizedQueuedTask` values as dictionary/hash-set keys or equality-check them.

### Sequential API processor

Both bounded and unbounded constructors are available; `processTimeWindow` bounds the number of recent durations retained for the average.

```csharp
using System.Threading.Channels;
using CommonNetFuncs.Web.Api.TaskQueuing.ApiQueue;

SequentialTaskProcessor bounded = new(
    new BoundedChannelOptions(100)
    {
        FullMode = BoundedChannelFullMode.Wait
    },
    processTimeWindow: 500);

SequentialTaskProcessor unbounded = new(
    new UnboundedChannelOptions(), processTimeWindow: 500);

int? result = await bounded.EnqueueAsync(
    async token =>
    {
        await Task.Delay(10, token);
        return 42;
    },
    cancellationToken);

QueueStats stats = bounded.Stats;
QueueStats allStats = await bounded.GetAllQueueStatsAsync();
bounded.Dispose();
```

Register the processor as hosted service and the wrapper as a service in the application's DI setup:

```csharp
builder.Services.AddSingleton<SequentialTaskProcessor>(_ =>
    new SequentialTaskProcessor(
        new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        }));
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<SequentialTaskProcessor>());
builder.Services.AddSingleton<ISequentialTaskService, SequentialTaskService>();
```

The service delegates to `EnqueueAsync`:

```csharp
ISequentialTaskService service =
    provider.GetRequiredService<ISequentialTaskService>();
int? queuedResult = await service.ExecuteAsync(
    token => Task.FromResult<int?>(7), cancellationToken);
```

Disposal completes the writer, stops the background service non-blockingly, lets the active task finish, and cancels pending completion sources. Exceptions from queued functions reach the caller through the completion task and increment failed stats.

The sequential metrics extension maps `GET /api/sequential-api-tasks-metrics` and injects the concrete `SequentialTaskProcessor`:

```csharp
using CommonNetFuncs.Web.Api.TaskQueuing.ApiQueue;
app.EndpointQueueMetrics();
```

### Prioritized API processor

```csharp
PrioritizedSequentialTaskProcessor prioritizedBounded = new(
    new BoundedChannelOptions(100), processTimeWindow: 500);
PrioritizedSequentialTaskProcessor prioritizedUnbounded =
    new(new UnboundedChannelOptions());

int? priorityResult =
    await prioritizedBounded.EnqueueWithPriorityAsync(
        _ => Task.FromResult<int?>(42),
        priority: 10,
        priorityLevel: TaskPriority.Emergency,
        timeout: TimeSpan.FromSeconds(5),
        cancellationToken);

PrioritizedQueueStats priorityStats =
    await prioritizedBounded.GetAllQueueStatsAsync();
prioritizedBounded.Dispose();
```

Important current limitations:

- `timeout` is stored on a `PrioritizedQueuedTask`, but this processor never applies it.
- the queued work receives `CancellationToken.None`, so the active delegate does not receive caller/shutdown cancellation through this processor;
- the `priorityLevel` argument is not currently copied into the queued task, so stats use the enum's default value instead of the supplied level;
- only one highest item is transferred to the channel on each enqueue, so priority behavior should be tested for the application's submission rate.

`IPrioritizedSequentialTaskService` exposes the same parameters as its `PrioritizedSequentialTaskService` implementation:

```csharp
IPrioritizedSequentialTaskService priorityService =
    new PrioritizedSequentialTaskService(prioritizedUnbounded);
int? serviceResult = await priorityService.ExecuteAsync(
    _ => Task.FromResult<int?>(9),
    priority: 3,
    taskPriority: TaskPriority.Critical,
    timeout: TimeSpan.FromSeconds(5),
    cancellationToken);
```

Its metrics endpoint is `GET /api/prioritized-sequential-api-tasks-metrics`:

```csharp
using CommonNetFuncs.Web.Api.TaskQueuing.ApiQueue;
CommonNetFuncs.Web.Api.TaskQueuing.ApiQueue
    .PrioritizedSequentialTaskExtensions.EndpointQueueMetrics(app);
```

### Per-endpoint queues

`EndpointQueue` creates its processing task in each constructor and supports bounded or unbounded channels. `EnqueueAsync` returns only after that queue item completes. The active task receives the queue cancellation token, which is canceled on disposal.

```csharp
EndpointQueue boundedQueue = new(
    "Items.Index", new BoundedChannelOptions(50), 1000);
EndpointQueue unboundedQueue = new(
    "Items.Export", new UnboundedChannelOptions(), 1000);

int? value = await boundedQueue.EnqueueAsync(
    async token =>
    {
        await Task.Delay(5, token);
        return 3;
    }, cancellationToken);

string key = boundedQueue.EndpointKey;
QueueStats snapshot = boundedQueue.Stats;
boundedQueue.Dispose();
```

The service supports three constructors and both channel option overloads:

```csharp
EndpointQueueService defaultService = new();
EndpointQueueService intervalService = new(TimeSpan.FromMinutes(10));
EndpointQueueService configuredService = new(
    cleanupInterval: TimeSpan.FromMinutes(2),
    cutoffTimeMinutes: 15);

int? boundedResult = await configuredService.ExecuteAsync(
    "Items.Index", token => Task.FromResult<int?>(1),
    new BoundedChannelOptions(10), cancellationToken);
int? unboundedResult = await configuredService.ExecuteAsync(
    "Items.Export", token => Task.FromResult<int?>(2),
    new UnboundedChannelOptions(), cancellationToken);

QueueStats oneStats =
    await configuredService.GetQueueStatsAsync("Items.Index");
Dictionary<string, QueueStats> allEndpointStats =
    await configuredService.GetAllQueueStatsAsync();
configuredService.Dispose();
```

The cleanup timer removes processed queues whose `LastProcessedAt` is older than the configured cutoff. Register `EndpointQueueService`/`IEndpointQueueService` as a singleton and dispose it at application shutdown.

The controller extensions derive a key as `ControllerName.ActionName` or accept `customKey`. Both bounded and unbounded overloads use `HttpContext.RequestAborted`:

```csharp
int? controllerValue = await this.ExecuteQueuedAsync(
    queueService,
    token => Task.FromResult<int?>(1),
    new BoundedChannelOptions(10),
    customKey: "Items.Index");

int? otherValue = await this.ExecuteQueuedAsync(
    queueService,
    token => Task.FromResult<int?>(2),
    new UnboundedChannelOptions(),
    customKey: "Items.Export");
```

The metrics extension maps:

- `GET /api/endpoint-queue-metrics`
- `GET /api/endpoint-queue-metrics/{endpointKey}`

and injects the concrete `EndpointQueueService`.

### Prioritized per-endpoint queues

The priority queue starts lazily on first enqueue. Higher numeric priority wins; equal priority uses queue time. `CancelTasksByPriorityAsync` cancels pending tasks with the selected priority level and updates cancellation stats.

```csharp
PrioritizedEndpointQueue endpointPriority =
    new("Items.Index", processTimeWindow: 500);

int? queuedValue = await endpointPriority.EnqueueAsync(
    async token =>
    {
        await Task.Delay(5, token);
        return 1;
    },
    priority: 100,
    priorityLevel: TaskPriority.Emergency,
    cancellationToken);

bool cancelled = await endpointPriority.CancelTasksByPriorityAsync(
    TaskPriority.Low);
PrioritizedQueueStats endpointPriorityStats = endpointPriority.Stats;
string endpointPriorityKey = endpointPriority.EndpointKey;
endpointPriority.Dispose();
```

`PrioritizedEndpointQueueService` supports three constructors and enum/custom numeric priority overloads. Numeric values map `>=4` to `Emergency`, 3 to `Critical`, 2 to `High`, 1 to `Normal`, and lower to `Low`.

`IPrioritizedEndpointQueueService` declares the execution, metrics, and cancellation operations implemented by the service.

```csharp
PrioritizedEndpointQueueService p1 = new(provider);
PrioritizedEndpointQueueService p2 =
    new(provider, TimeSpan.FromMinutes(10));
PrioritizedEndpointQueueService p3 =
    new(provider, TimeSpan.FromMinutes(2), cutoffTimeMinutes: 15);

int? byLevel = await p3.ExecuteAsync(
    "Items.Index", token => Task.FromResult<int?>(1),
    TaskPriority.Critical, cancellationToken);

int? byNumber = await p3.ExecuteAsync(
    "Items.Index", token => Task.FromResult<int?>(2),
    customPriority: 25, cancellationToken);

PrioritizedQueueStats onePriorityStats =
    await p3.GetQueueStatsAsync("Items.Index");
Dictionary<string, PrioritizedQueueStats> everyPriorityStats =
    await p3.GetAllQueueStatsAsync();
bool cancelledLow =
    await p3.CancelTasksAsync("Items.Index", TaskPriority.Low);
p3.Dispose();
```

Both controller priority overloads derive/use an endpoint key and propagate `RequestAborted`:

```csharp
int? levelQueued = await this.ExecutePrioritizedAsync(
    prioritizedQueueService,
    token => Task.FromResult<int?>(1),
    priority: TaskPriority.High,
    customKey: "Items.Index");

int? numericQueued = await this.ExecutePrioritizedAsync(
    prioritizedQueueService,
    token => Task.FromResult<int?>(2),
    customPriority: 100,
    customKey: "Items.Index");
```

The prioritized metrics extension uses the same `/api/endpoint-queue-metrics` paths as the non-prioritized extension but injects `PrioritizedEndpointQueueService`. Do not map both endpoint-queue metrics variants into the same route table unless the application differentiates them.

```csharp
using CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue;
CommonNetFuncs.Web.Api.TaskQueuing.EndpointQueue
    .PrioritizedEndpointQueueExtensions.EndpointQueueMetrics(app);
```

Endpoint and API queues use different shutdown behavior:

- sequential API processors allow the active operation to finish and cancel items still waiting;
- endpoint queues request cancellation for the active and pending operations;
- disposal is non-blocking from the disposing caller; resources are released after the processing task exits.

Queued delegates should observe their provided token and treat `OperationCanceledException` as normal shutdown.

## License

This project is licensed under the MIT License. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
