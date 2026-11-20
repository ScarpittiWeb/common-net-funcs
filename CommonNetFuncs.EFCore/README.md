# CommonNetFuncs.EFCore

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.EFCore)](https://www.nuget.org/packages/CommonNetFuncs.EFCore/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.EFCore)](https://www.nuget.org/packages/CommonNetFuncs.EFCore/)

EF Core query, bulk-command, navigation-property, query-rendering, and logging helpers. This package currently targets `net11.0`.

## Contents

- [Critical lifetime and behavior notes](#critical-lifetime-and-behavior-notes)
- [Setup](#setup)
- [`BaseDbContextActions<TEntity, TContext>`](#basedbcontextactionstentity-tcontext)
  - [Options and result types](#options-and-result-types)
  - [Primary-key methods](#primary-key-methods)
  - [All-entity methods](#all-entity-methods)
  - [Filtered methods](#filtered-methods)
  - [Navigation-source methods](#navigation-source-methods)
  - [Paging methods](#paging-methods)
  - [Single/min/max/count methods](#singleminmaxcount-methods)
  - [Write methods](#write-methods)
- [`Collections`](#collections)
- [`QueryExtensions`](#queryextensions)
- [`NavigationProperties`](#navigationproperties)
- [Query rendering](#query-rendering)
- [Filtered logging](#filtered-logging)
- [Installation](#installation)
- [License](#license)

## Critical lifetime and behavior notes

Read these notes before using the helpers.

### `DbContext` lifetime

`BaseDbContextActions` resolves `TContext` from the supplied `IServiceProvider` and many methods dispose that resolved context.

- `GetQueryAll`, `GetQueryWithFilter`, all `GetQuery*Full`, and projection/query overloads return `IQueryable` only after the context has been disposed. Do not rely on deferred execution of those queryables. Compose and materialize an EF query on a context whose lifetime you control instead.
- Materialization and streaming convenience methods built on those query-producing methods are affected by the same issue. A passing in-memory or pooled-context test does not guarantee behavior with a production DI scope/provider.
- Do not expect a tracked entity returned by these helpers to remain attached to a context that later `SaveChanges()` can use.
- `CreateMany`, `Update`, and `UpdateMany(List<TEntity>)` change tracker state on a context that is then disposed. `SaveChanges()` resolves a different use/context, so that sequence cannot reliably persist those changes.
- `Create`, `DeleteByObject`, `DeleteByKey`, `DeleteMany(IEnumerable<TEntity>)`, and `SaveChanges()` have the same scoped-context requirement: the operations only work as a combined unit when DI returns and keeps alive the same context for the entire unit of work. The current implementation disposes it, so prefer one explicitly scoped `DbContext`.
- `DeleteMany(predicate)` and predicate-based `UpdateMany` call EF Core's `ExecuteDeleteAsync`/`ExecuteUpdateAsync` and execute immediately. They do not require `SaveChanges()`, but require a provider that supports bulk execute APIs.

For reliable writes, use ordinary EF Core code on one context:

```cs
await using AsyncServiceScope scope =
    serviceProvider.CreateAsyncScope();
TestDbContext context =
    scope.ServiceProvider.GetRequiredService<TestDbContext>();

context.Add(entity);
context.Update(existing);
context.Remove(toDelete);
await context.SaveChangesAsync(cancellationToken);
```

### Tracking and return values

- Most collection/full-query defaults use `trackEntities: false` (`AsNoTracking`).
- The plain `GetByKey(object)` methods use `FindAsync` and therefore track the returned entity until their resolved context is disposed. The `full` dispatcher ignores its `trackEntities` argument when `full` is false.
- Single-result convenience methods such as `GetOneWithFilter`, `GetMaxByOrder`, `GetMinByOrder`, `GetMax`, and `GetMin` default `trackEntities` to `true` on their non-`full`/non-projection entry points. The boolean-`full` dispatcher defaults its own `trackEntities` to `false`; check the exact overload.
- Projections are generally not entity-tracked.
- Many caught read failures return `null`, streams return no items, `GetMax`/`GetMin`/`GetCount` can return `0`, and write helpers often log and return `false`/`null`. A `false` result from `SaveChanges` can mean no rows or an error.
- Cancellation is passed to operations where the signature includes a token, but several broad exception handlers catch cancellation and return fallback results. Streaming enumeration can stop or propagate cancellation when EF Core performs enumeration.

### Full queries, filters, and safety

- `full`/`Full` means navigation properties discovered from EF metadata are included, subject to depth, ignore attributes, and cycle handling. It is not a guarantee that every CLR graph object is loaded.
- `FullQueryOptions.SplitQueryOverride` selects provider single/split-query behavior only; `null` leaves the database/provider default.
- `GlobalFilterOptions` can disable all EF query filters or selected named filters. Treat bypassing tenant/soft-delete/security filters as trusted privileged behavior.
- The `GlobalFilterOptions` parameters are not forwarded consistently by all convenience/materialization/streaming methods in the current implementation. In particular, verify projection/full/navigation wrappers; some call a lower-level method without the supplied options. Use a correctly scoped EF query directly when filter correctness is security-sensitive.
- Query rendering includes actual parameter values. Errors logged while serializing write models may also contain entity data. Restrict those logs.

## Setup

Register EF Core normally, then construct the action class inside the desired scope:

```cs
using CommonNetFuncs.EFCore;
using Microsoft.Extensions.DependencyInjection;

ServiceProvider provider = new ServiceCollection()
    .AddDbContext<TestDbContext>(options => options.UseSqlite(connectionString))
    .BuildServiceProvider();

using IServiceScope scope = provider.CreateScope();
BaseDbContextActions<TestEntity, TestDbContext> actions =
    new(scope.ServiceProvider);
BaseDbContextActions<TestEntityWithCompoundKey, TestDbContext> compoundActions =
    new(scope.ServiceProvider);

IBaseDbContextActions<TestEntity, TestDbContext> contract = actions;
```

The public `ServiceProvider` property can be replaced:

```cs
IServiceProvider original = actions.ServiceProvider;
actions.ServiceProvider = scope.ServiceProvider;
```

`IBaseDbContextActions<TEntity, TContext>` exposes the same query/write contract documented below and is useful for injection:

```cs
public sealed class CustomerService(
    IBaseDbContextActions<Customer, AppDbContext> queries)
{
    private readonly IBaseDbContextActions<Customer, AppDbContext> queries =
        queries;
}
```

The examples use these model shapes:

```cs
public sealed class TestEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
    public List<TestEntityDetail> Details { get; set; } = [];
}

public sealed class TestEntityDetail
{
    public int Id { get; set; }
    public int TestEntityId { get; set; }
    public TestEntity? TestEntity { get; set; }
}

public sealed class TestEntityWithCompoundKey
{
    public int TenantId { get; set; }
    public int EntityId { get; set; }
    public required string Name { get; set; }
}

public sealed class CustomerDto
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

public sealed class TestDbContext(
    DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<TestEntity> TestEntities => Set<TestEntity>();
    public DbSet<TestEntityDetail> TestEntityDetails =>
        Set<TestEntityDetail>();
    public DbSet<TestEntityWithCompoundKey> CompoundEntities =>
        Set<TestEntityWithCompoundKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TestEntityWithCompoundKey>()
            .HasKey(x => new { x.TenantId, x.EntityId });
    }
}
```

## `BaseDbContextActions<TEntity, TContext>`

### Options and result types

#### `FullQueryOptions`

Its constructor and inherited `NavigationPropertiesOptions` properties are public:

```cs
FullQueryOptions defaultFull = new();
FullQueryOptions splitFull = new(splitQueryOverride: true)
{
    MaxNavigationDepth = 2,
    NavPropAttributesToIgnore =
        [typeof(JsonIgnoreAttribute), typeof(Newtonsoft.Json.JsonIgnoreAttribute)],
    UseCaching = true
};

bool? split = splitFull.SplitQueryOverride;
```

`true` requests `AsSplitQuery`, `false` requests `AsSingleQuery`, and `null` uses the provider/default behavior.

#### `GlobalFilterOptions`

```cs
GlobalFilterOptions keepFilters = new()
{
    DisableAllFilters = false,
    FilterNamesToDisable = null
};

GlobalFilterOptions privileged = new()
{
    DisableAllFilters = true
};

GlobalFilterOptions named = new()
{
    FilterNamesToDisable = ["TenantFilter", "SoftDeleteFilter"]
};
```

When `FilterNamesToDisable` is nonempty it takes precedence over `DisableAllFilters` in the current helper logic.

#### `GenericPagingModel<T>`

```cs
GenericPagingModel<CustomerDto> page = new()
{
    Entities =
    [
        new CustomerDto { Id = 1, Name = "Ada" }
    ],
    TotalRecords = 57
};
```

A new model has an empty `Entities` list and `TotalRecords == 0`.

### Primary-key methods

There are six overloads. Single-key examples and ordered compound-key arrays are shown explicitly; a compound-key anonymous object is also accepted by the object overload when its property names match the model key names.

```cs
CancellationToken ct = cancellationToken;
FullQueryOptions fullOptions = new(splitQueryOverride: true);
GlobalFilterOptions filters = new() { DisableAllFilters = false };

// GetByKey(bool, object)
TestEntity? a = await actions.GetByKey(
    full: false, primaryKey: 42, queryTimeout: TimeSpan.FromSeconds(15),
    trackEntities: false, fullQueryOptions: fullOptions,
    globalFilterOptions: filters, cancellationToken: ct);

// GetByKey(object)
TestEntity? b = await actions.GetByKey(
    primaryKey: 42, queryTimeout: TimeSpan.FromSeconds(15),
    globalFilterOptions: filters, cancellationToken: ct);

// GetByKey(bool, object[])
TestEntityWithCompoundKey? c = await compoundActions.GetByKey(
    full: false, primaryKey: [tenantId, entityId],
    queryTimeout: TimeSpan.FromSeconds(15), trackEntities: false,
    fullQueryOptions: fullOptions, globalFilterOptions: filters,
    cancellationToken: ct);

// GetByKey(object[])
TestEntityWithCompoundKey? d = await compoundActions.GetByKey(
    primaryKey: [tenantId, entityId],
    queryTimeout: TimeSpan.FromSeconds(15),
    globalFilterOptions: filters, cancellationToken: ct);

// GetByKeyFull(object)
TestEntity? e = await actions.GetByKeyFull(
    primaryKey: 42, queryTimeout: TimeSpan.FromSeconds(15),
    trackEntities: false, fullQueryOptions: fullOptions,
    globalFilterOptions: filters, cancellationToken: ct);

// GetByKeyFull(object[])
TestEntityWithCompoundKey? f = await compoundActions.GetByKeyFull(
    primaryKey: [tenantId, entityId],
    queryTimeout: TimeSpan.FromSeconds(15), trackEntities: false,
    fullQueryOptions: fullOptions, globalFilterOptions: filters,
    cancellationToken: ct);
```

For a compound-key entity, array values must follow EF model key declaration order. The object overload instead recognizes an anonymous object and reorders values by key-property name:

```cs
var anonymousKey = new { EntityId = entityId, TenantId = tenantId };
TestEntityWithCompoundKey? found =
    await compoundActions.GetByKey(anonymousKey, cancellationToken: ct);
```

Without filter bypass, `FindAsync` is used; EF global filters still apply, as demonstrated by the focused filter tests.

### All-entity methods

The examples below cover every materialized, streaming, query, projection, boolean-`full`, and explicit-`Full` overload.

```cs
// Materialized, entities: GetAll(bool) and GetAll()
List<TestEntity>? a1 = await actions.GetAll(
    full: false, queryTimeout: timeout, trackEntities: false,
    fullQueryOptions: fullOptions, globalFilterOptions: filters,
    cancellationToken: ct);
List<TestEntity>? a2 = await actions.GetAll(
    queryTimeout: timeout, trackEntities: false,
    globalFilterOptions: filters, cancellationToken: ct);

// Materialized, projections: GetAll<TOutput>(bool, select) and GetAll<TOutput>(select)
List<CustomerDto>? a3 = await actions.GetAll(
    full: false, x => new CustomerDto { Id = x.Id, Name = x.Name },
    queryTimeout: timeout, trackEntities: false,
    fullQueryOptions: fullOptions, globalFilterOptions: filters,
    cancellationToken: ct);
List<string>? a4 = await actions.GetAll(
    x => x.Name, queryTimeout: timeout, trackEntities: false,
    globalFilterOptions: filters, cancellationToken: ct);

// Materialized Full overloads
List<TestEntity>? a5 = await actions.GetAllFull(
    timeout, trackEntities: false, fullOptions, filters, ct);
List<CustomerDto>? a6 = await actions.GetAllFull(
    x => new CustomerDto { Id = x.Id, Name = x.Name },
    timeout, trackEntities: false, fullOptions, filters, ct);

// Streaming entities: boolean-full and plain
IAsyncEnumerable<TestEntity>? s1 = actions.GetAllStreaming(
    full: false, timeout, false, fullOptions, filters, ct);
IAsyncEnumerable<TestEntity>? s2 = actions.GetAllStreaming(
    timeout, false, filters, ct);

// Streaming projections: boolean-full and plain
IAsyncEnumerable<CustomerDto>? s3 = actions.GetAllStreaming(
    full: false,
    x => new CustomerDto { Id = x.Id, Name = x.Name },
    timeout, false, fullOptions, filters, ct);
IAsyncEnumerable<string>? s4 = actions.GetAllStreaming(
    x => x.Name, timeout, false, filters, ct);

// Streaming explicit Full overloads
IAsyncEnumerable<TestEntity>? s5 =
    actions.GetAllFullStreaming(timeout, false, fullOptions, filters, ct);
IAsyncEnumerable<CustomerDto>? s6 =
    actions.GetAllFullStreaming(
        x => new CustomerDto { Id = x.Id, Name = x.Name },
        timeout, false, fullOptions, filters, ct);

await foreach (TestEntity item in s1 ?? AsyncEnumerable.Empty<TestEntity>())
{
    Process(item);
}
```

All four `IQueryable` overloads are present but subject to the disposed-context warning:

```cs
IQueryable<TestEntity> q1 =
    actions.GetQueryAll(timeout, trackEntities: false, filters);
IQueryable<string> q2 =
    actions.GetQueryAll(x => x.Name, timeout, false, filters);
IQueryable<TestEntity> q3 =
    actions.GetQueryAllFull(timeout, handlingCircularRefException: false,
        trackEntities: false, fullOptions, filters);
IQueryable<CustomerDto> q4 =
    actions.GetQueryAllFull(
        x => new CustomerDto { Id = x.Id, Name = x.Name },
        timeout, handlingCircularRefException: false,
        trackEntities: false, fullOptions, filters);
```

These assignments are API demonstrations only. Do not enumerate the returned query against the context the helper already disposed; build the same query on a live context.

### Filtered methods

`whereExpression` is provider-translated LINQ. The boolean `full` forms dispatch to ordinary or `Full` behavior. Every overload is shown:

```cs
Expression<Func<TestEntity, bool>> where = x => x.Id >= minimumId;
Expression<Func<TestEntity, CustomerDto>> selectDto =
    x => new CustomerDto { Id = x.Id, Name = x.Name };

// GetWithFilter entities
List<TestEntity>? f1 = await actions.GetWithFilter(
    full: false, where, timeout, false, fullOptions, filters, ct);
List<TestEntity>? f2 = await actions.GetWithFilter(
    where, timeout, false, filters, ct);

// GetWithFilter projections
List<CustomerDto>? f3 = await actions.GetWithFilter(
    full: false, where, selectDto, timeout, false, fullOptions, filters, ct);
List<CustomerDto>? f4 = await actions.GetWithFilter(
    where, selectDto, timeout, false, filters, ct);

// Explicit Full
List<TestEntity>? f5 =
    await actions.GetWithFilterFull(where, timeout, false, fullOptions, filters, ct);
List<CustomerDto>? f6 =
    await actions.GetWithFilterFull(
        where, selectDto, timeout, false, fullOptions, filters, ct);

// Streaming entities
IAsyncEnumerable<TestEntity>? fs1 =
    actions.GetWithFilterStreaming(
        full: false, where, timeout, false, fullOptions, filters, ct);
IAsyncEnumerable<TestEntity>? fs2 =
    actions.GetWithFilterStreaming(where, timeout, false, filters, ct);

// Streaming projections
IAsyncEnumerable<CustomerDto>? fs3 =
    actions.GetWithFilterStreaming(
        full: false, where, selectDto, timeout, false, fullOptions, filters, ct);
IAsyncEnumerable<CustomerDto>? fs4 =
    actions.GetWithFilterStreaming(
        where, selectDto, timeout, false, filters, ct);

// Explicit Full streaming
IAsyncEnumerable<TestEntity>? fs5 =
    actions.GetWithFilterFullStreaming(where, timeout, false, fullOptions, filters, ct);
IAsyncEnumerable<CustomerDto>? fs6 =
    actions.GetWithFilterFullStreaming(
        where, selectDto, timeout, false, fullOptions, filters, ct);

// Deferred query overloads (disposed-context limitation applies)
IQueryable<TestEntity> fq1 =
    actions.GetQueryWithFilter(where, timeout, false, filters);
IQueryable<CustomerDto> fq2 =
    actions.GetQueryWithFilter(where, selectDto, timeout, false, filters);
IQueryable<TestEntity> fq3 =
    actions.GetQueryWithFilterFull(
        where, timeout, handlingCircularRefException: false,
        trackEntities: false, fullOptions, filters);
IQueryable<CustomerDto> fq4 =
    actions.GetQueryWithFilterFull(
        where, selectDto, timeout, handlingCircularRefException: false,
        trackEntities: false, fullOptions, filters);
```

Projection filtered queries use `Distinct()` in the query-producing implementation; materialized filtered projections that delegate to them can therefore return distinct output values.

### Navigation-source methods

These methods query another entity type `TOutput` and select a `TEntity` navigation/property from matching rows:

```cs
Expression<Func<TestEntityDetail, bool>> detailWhere =
    d => d.TestEntityId == parentId;
Expression<Func<TestEntityDetail, TestEntity>> selectParent =
    d => d.TestEntity!;

// GetNavigationWithFilter(bool, ...) and GetNavigationWithFilter(...)
List<TestEntity>? n1 = await actions.GetNavigationWithFilter(
    full: false, detailWhere, selectParent, timeout, false,
    fullOptions, filters, ct);
List<TestEntity>? n2 = await actions.GetNavigationWithFilter(
    detailWhere, selectParent, timeout, false,
    fullQueryOptions: fullOptions, globalFilterOptions: filters,
    cancellationToken: ct);

// Streaming boolean-full and plain
IAsyncEnumerable<TestEntity>? ns1 =
    actions.GetNavigationWithFilterStreaming(
        full: false, detailWhere, selectParent, timeout, false,
        fullOptions, filters, ct);
IAsyncEnumerable<TestEntity>? ns2 =
    actions.GetNavigationWithFilterStreaming(
        detailWhere, selectParent, timeout, false,
        fullOptions, filters, ct);

// Explicit Full list and stream
List<TestEntity>? n3 = await actions.GetNavigationWithFilterFull(
    detailWhere, selectParent, timeout, false, fullOptions, filters, ct);
IAsyncEnumerable<TestEntity>? ns3 =
    actions.GetNavigationWithFilterFullStreaming(
        detailWhere, selectParent, timeout, false,
        fullOptions, filters, ct);

// Deferred query-producing overload
IQueryable<TestEntity> nq =
    actions.GetQueryNavigationWithFilterFull(
        detailWhere, selectParent, timeout,
        handlingCircularRefException: false, trackEntities: false,
        fullOptions, filters);
```

The explicit `Full` list/stream methods build another full query after first creating a projected `IQueryable`; this remains affected by the lifetime and forwarding limitations above.

### Paging methods

All paging methods return `GenericPagingModel<TOutput>`. `TotalRecords` is counted before `Skip`/`Take`. `pageSize <= 0` is converted to `int.MaxValue`, so `0` returns all matching rows. Always provide deterministic ordering.

The non-`Full` string-order overload currently accepts `orderByString` but does not apply it. Use the typed `ascendingOrderExpression` overload when deterministic ordering is required on the plain path. The explicit `Full` string-order query applies it through `System.Linq.Dynamic.Core`.

Eight overloads exist:

```cs
Expression<Func<TestEntity, CustomerDto>> pageSelect =
    x => new CustomerDto { Id = x.Id, Name = x.Name };

// String order: boolean-full and plain
GenericPagingModel<CustomerDto> p1 =
    await actions.GetWithPagingFilter(
        full: false, where, pageSelect, orderByString: nameof(TestEntity.Id),
        skip: 20, pageSize: 10, queryTimeout: timeout,
        trackEntities: false, fullQueryOptions: fullOptions,
        globalFilterOptions: filters, cancellationToken: ct);
GenericPagingModel<CustomerDto> p2 =
    await actions.GetWithPagingFilter(
        where, pageSelect, orderByString: nameof(TestEntity.Id),
        skip: 20, pageSize: 10, queryTimeout: timeout,
        trackEntities: false, globalFilterOptions: filters,
        cancellationToken: ct);

// Typed order expression: boolean-full and plain
GenericPagingModel<CustomerDto> p3 =
    await actions.GetWithPagingFilter(
        full: false, where, pageSelect, x => x.Id,
        skip: 20, pageSize: 10, queryTimeout: timeout,
        trackEntities: false, fullQueryOptions: fullOptions,
        globalFilterOptions: filters, cancellationToken: ct);
GenericPagingModel<CustomerDto> p4 =
    await actions.GetWithPagingFilter(
        where, pageSelect, x => x.Id,
        skip: 20, pageSize: 10, queryTimeout: timeout,
        trackEntities: false, globalFilterOptions: filters,
        cancellationToken: ct);

// Explicit Full, string and typed order
GenericPagingModel<CustomerDto> p5 =
    await actions.GetWithPagingFilterFull(
        where, pageSelect, nameof(TestEntity.Id), 20, 10,
        timeout, false, fullOptions, filters, ct);
GenericPagingModel<CustomerDto> p6 =
    await actions.GetWithPagingFilterFull(
        where, pageSelect, x => x.Id, 20, 10,
        timeout, false, fullOptions, filters, ct);

// Deferred Full query versions (disposed-context limitation applies)
IQueryable<CustomerDto> pq1 =
    actions.GetQueryPagingWithFilterFull(
        where, pageSelect, nameof(TestEntity.Id),
        timeout, false, false, fullOptions, filters);
IQueryable<CustomerDto> pq2 =
    actions.GetQueryPagingWithFilterFull(
        where, pageSelect, x => x.Id,
        timeout, false, false, fullOptions, filters);
```

The string-order overload uses `System.Linq.Dynamic.Core`; map accepted sort text from an allow-list rather than forwarding arbitrary request strings.

### Single/min/max/count methods

#### First matching row

```cs
// Entities: boolean-full and plain
TestEntity? o1 = await actions.GetOneWithFilter(
    full: false, where, timeout, false, fullOptions, filters, ct);
TestEntity? o2 = await actions.GetOneWithFilter(
    where, timeout, trackEntities: true, filters, ct);

// Projections: boolean-full and plain
CustomerDto? o3 = await actions.GetOneWithFilter(
    full: false, where, selectDto, timeout, false,
    fullOptions, filters, ct);
CustomerDto? o4 = await actions.GetOneWithFilter(
    where, selectDto, timeout, trackEntities: true, filters, ct);

// Explicit Full entities and projection
TestEntity? o5 =
    await actions.GetOneWithFilterFull(
        where, timeout, false, fullOptions, filters, ct);
CustomerDto? o6 =
    await actions.GetOneWithFilterFull(
        where, selectDto, timeout, false, fullOptions, filters, ct);
```

No ordering is supplied, so "first" is whichever row EF/provider returns first.

#### Row with maximum/minimum ordered key

```cs
// GetMaxByOrder<TKey>: boolean-full, plain, explicit Full
TestEntity? mx1 = await actions.GetMaxByOrder(
    full: false, where, x => x.CreatedDate,
    timeout, false, fullOptions, filters, ct);
TestEntity? mx2 = await actions.GetMaxByOrder(
    where, x => x.CreatedDate, timeout, trackEntities: true, filters, ct);
TestEntity? mx3 = await actions.GetMaxByOrderFull(
    where, x => x.CreatedDate, timeout, false, fullOptions, filters, ct);

// GetMinByOrder<TKey>: boolean-full, plain, explicit Full
TestEntity? mn1 = await actions.GetMinByOrder(
    full: false, where, x => x.CreatedDate,
    timeout, false, fullOptions, filters, ct);
TestEntity? mn2 = await actions.GetMinByOrder(
    where, x => x.CreatedDate, timeout, trackEntities: true, filters, ct);
TestEntity? mn3 = await actions.GetMinByOrderFull(
    where, x => x.CreatedDate, timeout, false, fullOptions, filters, ct);
```

These return an **entity**, unlike `GetMax`/`GetMin` below.

#### Maximum/minimum scalar and count

```cs
// GetMax<TOutput>: boolean-full, plain, explicit Full
int? max1 = await actions.GetMax(
    full: false, where, x => x.Id,
    timeout, false, fullOptions, filters, ct);
int? max2 = await actions.GetMax(
    where, x => x.Id, timeout, trackEntities: true, filters, ct);
int? max3 = await actions.GetMaxFull(
    where, x => x.Id, timeout, false, fullOptions, filters, ct);

// GetMin<TOutput>: boolean-full, plain, explicit Full
int? min1 = await actions.GetMin(
    full: false, where, x => x.Id,
    timeout, false, fullOptions, filters, ct);
int? min2 = await actions.GetMin(
    where, x => x.Id, timeout, trackEntities: true, filters, ct);
int? min3 = await actions.GetMinFull(
    where, x => x.Id, timeout, false, fullOptions, filters, ct);

int count = await actions.GetCount(
    x => x.IsActive, timeout, filters, ct);
```

Empty/non-translatable aggregate input can cause the logged fallback behavior; with a value type such as `int`, a failed `TOutput?` call can look like `0`.

### Write methods

Because of the context-lifetime issue, the examples show the API but are not a safe cross-context persistence recipe. Use one live `DbContext` for a real unit of work.

#### Create/update tracked-state helpers

```cs
// Create
await actions.Create(
    new TestEntity { Name = "New", CreatedDate = DateTime.UtcNow },
    removeNavigationProps: true);

// CreateMany
await actions.CreateMany(
    [new() { Name = "A" }, new() { Name = "B" }],
    removeNavigationProps: true);

// Update
existing.Name = "Changed";
actions.Update(existing, removeNavigationProps: true);

// UpdateMany(List<TEntity>)
bool updateRangeAccepted = actions.UpdateMany(
    [existing, another], removeNavigationProps: true,
    cancellationToken: ct);
```

`Create` throws `ArgumentNullException` for `null` before its catch block; other operation failures are generally logged rather than propagated. `removeNavigationProps` mutates the supplied object(s) by setting navigation properties to `null`.

None of these helpers saves. The currently separate `SaveChanges()` call is not reliable unless DI/context ownership is corrected.

#### Delete tracked-state helpers

```cs
// DeleteByObject
actions.DeleteByObject(
    existing, removeNavigationProps: true,
    globalFilterOptions: filters);

// DeleteByKey
bool keyFound = await actions.DeleteByKey(
    existing.Id, globalFilterOptions: filters);

// DeleteMany(IEnumerable<TEntity>)
bool rangeAccepted = actions.DeleteMany(
    [existing, another], removeNavigationProps: true,
    globalFilterOptions: filters);
```

`GlobalFilterOptions` does not apply to `Remove`/`RemoveRange` on already-loaded objects. `DeleteByKey` applies the options while finding the row, then marks it deleted; it still requires a save on the same context.

When filter bypass is requested, `DeleteByKey` builds a single-key expression. Supplying a compound key to that path is not supported and returns/logs a failure; the normal no-bypass `FindAsync` path can accept a compound key array when the values are correctly ordered.

#### Immediate provider operations

```cs
// DeleteMany(predicate): executes immediately
int? deleted = await actions.DeleteMany(
    x => x.CreatedDate < cutoff,
    globalFilterOptions: filters,
    cancellationToken: ct);

// UpdateMany(predicate, setters): executes immediately
int? updated = await actions.UpdateMany(
    whereExpression: x => x.IsActive,
    updateSetters: setters => setters
        .SetProperty(x => x.Name, x => x.Name + " (active)")
        .SetProperty(x => x.CreatedDate, DateTime.UtcNow),
    queryTimeout: timeout,
    globalFilterOptions: filters,
    cancellationToken: ct);
```

Both methods return affected-row count, or `null` after a caught error. They are not multi-statement transactions and bypass normal change tracking. Provider support and concurrency behavior apply.

Two Z.EntityFramework.Plus helpers also execute immediately:

```cs
// DeleteManyTracked(IEnumerable<TEntity>)
bool trackedDeleteSucceeded = await actions.DeleteManyTracked(
    [existing, another],
    removeNavigationProps: true,
    globalFilterOptions: filters);

// DeleteManyByKeys(IEnumerable<object>)
bool keyDeleteSucceeded = await actions.DeleteManyByKeys(
    [id1, id2], globalFilterOptions: filters);
```

Despite its name, `DeleteManyTracked` does not require `SaveChanges`; it calls `DeleteRangeByKeyAsync`. Filter options are ignored by these two methods. Provider behavior varies, and `DeleteManyByKeys` is documented in source as not working with PostgreSQL.

#### Save

```cs
bool savedRows = await actions.SaveChanges();
```

It returns `true` only when the separately resolved context reports more than zero saved rows. It returns `false` for zero changes and for logged errors.

## `Collections`

`GetObjectByPartial` and `GetObjectByPartialAsync` build equality predicates from the non-null public properties of a partial object. By default (`ignoreDefaultValues: true`), default-valued value-type properties are skipped. If no conditions are generated, the methods return `null`.

```cs
TestEntity probe = new() { Name = "Ada", Id = 0 };

// Synchronous
TestEntity? match = context.TestEntities.GetObjectByPartial(
    context, probe,
    ignoreDefaultValues: true,
    cancellationToken: ct);

// Asynchronous
TestEntity? asyncMatch =
    await context.TestEntities.GetObjectByPartialAsync(
        context, probe,
        ignoreDefaultValues: true,
        cancellationToken: ct);
```

All supplied properties are ANDed. Cancellation is checked while building the expression. `DateTime` values are normalized from EF provider/model metadata: PostgreSQL to UTC, SQL Server `datetime`/`datetime2` to `Unspecified`, SQL Server `datetimeoffset` to UTC, MySQL `datetime` to `Unspecified`, and UTC as a fallback. This provider heuristic does not normalize `DateTimeOffset`, which is compared as supplied.

## `QueryExtensions`

```cs
bool filterByName = !string.IsNullOrWhiteSpace(requestedName);

IQueryable<Customer> query = context.Customers
    .WhereIf(filterByName, x => x.Name == requestedName);
```

`WhereIf<T>(IQueryable<T>, bool, Expression<Func<T,bool>>)` returns the original query unchanged when `condition` is false and preserves deferred execution when true.

## `NavigationProperties`

### `NavigationPropertiesOptions`

```cs
NavigationPropertiesOptions options =
    new(maxNavigationDepth: 2, navPropAttributesToIgnore: null, useCaching: true);

options.MaxNavigationDepth = 2;
options.NavPropAttributesToIgnore =
    [typeof(JsonIgnoreAttribute), typeof(Newtonsoft.Json.JsonIgnoreAttribute)];
options.UseCaching = false;
```

Defaults are depth `100`, `null` ignore-attribute list, and caching enabled.

- The documented depth is traversal depth: `0` includes direct navigations only.
- A negative value is not a reliable “unlimited” setting in the current traversal implementation; it produces no navigation paths on a fresh calculation. Use a sufficiently large positive value.
- With `NavPropAttributesToIgnore == null`, both Newtonsoft `JsonIgnoreAttribute` and `System.Text.Json`'s `JsonIgnoreAttribute` are ignored. An empty list changes that behavior by disabling the default exclusions.
- Cycles are cut by visited name/type nodes.

### Complete navigation API examples

```cs
// IncludeNavigationProperties<T>
IQueryable<TestEntity> included =
    context.TestEntities.IncludeNavigationProperties(context, options);

// GetNavigations<T>
HashSet<string> allPaths =
    NavigationProperties.GetNavigations<TestEntity>(context, options);

// GetTopLevelNavigations<T>
List<string> directPaths =
    NavigationProperties.GetTopLevelNavigations<TestEntity>(
        context, options.NavPropAttributesToIgnore, useCaching: true);

// RemoveNavigationProperties<T>
entity.RemoveNavigationProperties(context);
```

`IncludeNavigationProperties` appends string-based EF `Include` calls for all returned paths. `RemoveNavigationProperties` sets every writable top-level EF navigation to `null`, including collection properties; it does not assign an empty collection and does not traverse recursively.

### Cache controls and public cache value types

Both cache managers expose the `ICacheManagerApi` operations:

```cs
NavigationProperties.NavigationCacheManager.SetUseLimitedCache(true);
NavigationProperties.NavigationCacheManager.SetLimitedCacheSize(256);
NavigationProperties.NavigationCacheManager.ClearAllCaches();

NavigationProperties.TopLevelNavigationCacheManager.SetUseLimitedCache(false);
NavigationProperties.TopLevelNavigationCacheManager.ClearAllCaches();
```

The public nested cache types can be constructed and compared:

```cs
NavigationProperties.NavigationPropertiesCacheKey key1 =
    new(typeof(TestEntity), "JsonIgnoreAttribute");
NavigationProperties.NavigationPropertiesCacheKey key2 =
    new(typeof(TestEntity), "JsonIgnoreAttribute");

Type source = key1.SourceType;
string? ignored = key1.NavigationPropertyTypesToIgnore;
bool equalKey = key1.Equals(key2);
bool equalKeyObject = key1.Equals((object)key2);
int keyHash = key1.GetHashCode();
bool keysSame = key1 == key2;
bool keysDiffer = key1 != key2;

NavigationProperties.NavigationPropertiesCacheValue value1 =
    new(["Details", "Details.Items"], maxDepth: 2);
NavigationProperties.NavigationPropertiesCacheValue value2 =
    new(["Details.Items", "Details"], maxDepth: 2);

HashSet<string> navigationPaths = value1.NavigationProperties;
int calculatedDepth = value1.MaxDepth;
HashSet<string> firstLevel = value1.GetNavigationsToDepth(0);
bool equalValue = value1.Equals(value2);
bool equalValueObject = value1.Equals((object)value2);
int valueHash = value1.GetHashCode();
bool valuesSame = value1 == value2;
bool valuesDiffer = value1 != value2;
```

Cache-value equality is set-content plus depth, not hash-set reference identity. Cache keys include entity type and the sorted/concatenated ignore-attribute type names, while cached depth is stored in the value.

## Query rendering

`SqlDialect` is a public enum with `SqlServer` and `PostgreSql` values.

Both render overloads are public extension methods:

```cs
IQueryable<Customer> query =
    context.Customers.Where(x => x.Name == "O'Brian");

// Auto: Npgsql => PostgreSql; every other provider => SqlServer
string autoScript = query.RenderQueryAsScript(context);

// Explicit dialect
string sqlServerScript =
    query.RenderQueryAsScript(SqlDialect.SqlServer);
string postgreSqlScript =
    query.RenderQueryAsScript(SqlDialect.PostgreSql);
```

SQL Server reuses EF's runnable parameter-declaration output. PostgreSQL rendering parses Npgsql's parameter comments and inlines literals because top-level PostgreSQL declarations are not available. These scripts contain actual query values and are diagnostic output only; do not run a script produced from untrusted input or publish it in broadly accessible logs.

## Filtered logging

`FilteredEfCoreLogger` implements `ILogger` and forwards scopes/logs after applying a minimum level.

```cs
using CommonNetFuncs.EFCore.Logging;
using Microsoft.Extensions.Logging;

ILogger inner = LoggerFactory
    .Create(builder => builder.AddConsole())
    .CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

FilteredEfCoreLogger logger = new(inner, LogLevel.Warning);

bool enabled = logger.IsEnabled(LogLevel.Information); // false

using IDisposable? scope = logger.BeginScope(
    new Dictionary<string, object> { ["Operation"] = "ReadCustomers" });

logger.Log(
    LogLevel.Error,
    new EventId(20102, "QueryFailed"),
    "SELECT failed",
    exception: null,
    formatter: (state, _) => state);
```

`IsEnabled` also checks the wrapped logger, and `Log` only forwards enabled levels. `BeginScope` and state/exception formatting are delegated to the inner logger.

`FilteredEfCoreLoggerFactory` wraps an `ILoggerFactory` and selects a minimum by the first category substring found in its filter dictionary:

```cs
ILoggerFactory innerFactory =
    LoggerFactory.Create(builder => builder.AddConsole());

using FilteredEfCoreLoggerFactory factory = new(
    innerFactory,
    new Dictionary<string, LogLevel>
    {
        ["Database.Command"] = LogLevel.Warning,
        ["Database.Connection"] = LogLevel.Error
    });

ILogger commandLogger =
    factory.CreateLogger(
        "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted");

ILoggerProvider provider = new CustomProvider();
factory.AddProvider(provider);

factory.Dispose();
```

Matching uses the current `Contains(string)` behavior (case-sensitive under the runtime used here). Dictionary enumeration determines the first matching substring, so avoid overlapping keys if precedence matters. An unmatched category currently receives the default `LogLevel.Trace` threshold through the wrapper. Disposing the wrapper disposes the inner factory.

## Installation

```bash
dotnet add package CommonNetFuncs.EFCore
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
