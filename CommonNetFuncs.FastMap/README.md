# CommonNetFuncs.FastMap

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.FastMap)](https://www.nuget.org/packages/CommonNetFuncs.FastMap/)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.FastMap)](https://www.nuget.org/packages/CommonNetFuncs.FastMap/)

Expression-tree mapping for `net11.0` and `netstandard2.1`.

## Contents

- [Installation](#installation)
- [`FastMap`](#fastmap)
- [Cache management](#cache-management)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.FastMap
```

```csharp
using CommonNetFuncs.Core;
using CommonNetFuncs.FastMap;
```

## `FastMap`

Both public overloads are demonstrated below:

```csharp
public sealed class Child
{
    public string Value { get; set; } = string.Empty;
}

public sealed class Source
{
    public string Name { get; set; } = string.Empty;
    public Child SourceChild { get; set; } = new();
    public List<int> Values { get; set; } = [];
}

public sealed class Destination
{
    public Destination() { } // Destination types require a public parameterless constructor.
    public string? Name { get; set; }
    public Child SourceChild { get; set; } = new(); // Same type: assigned directly.
    public List<int>? Values { get; set; }
    public string? MissingInSource { get; set; }
}

Source source = new() { Name = "Ada", Values = [1, 2] };

Destination? staticCached = source.FastMap<Source, Destination>();
Destination? configuredCached = source.FastMap<Source, Destination>(useCache: true);
Destination? uncached = source.FastMap<Source, Destination>(useCache: false);
Destination? fromNull =
    ((Source?)null).FastMap<Source, Destination>();
```

The default overload uses a static generic cache that cannot be cleared at runtime. The `useCache: true` overload uses `FastMapper.CacheManager`; `false` compiles a mapper for that call. A null source returns `null`.

Mapping is by exact property name, without string case conversion or custom profiles. A destination property is assigned only when it is writable, the source property is readable, and the property types are supported. Missing, read-only, or incompatible properties retain their destination initializer/default value. Public destination construction must succeed (`new TDest()` is used in the expression).

Supported shapes:

- Same-type scalar/object properties are assigned directly. Same-type nested reference objects are **not** cloned.
- Different nested class types are recursively mapped by matching properties and guarded for null.
- Arrays, `List<T>`, `HashSet<T>`, and other `IEnumerable` source shapes can be converted to array/list/hash-set destinations.
- Elements with different types are mapped recursively. Same-type element collections are copied into a new array/list/hash set (or into a list for the generic fallback), but reference elements are still shared.
- `Dictionary` key types must match. Same value types produce a new dictionary; different value types are mapped. Null dictionary values are preserved as null.

Unsupported destination collection shapes can cause expression-construction/compile errors. For example, a `Queue<T>`/`Stack<T>` property should not be assumed to work as a destination. Cycles are not tracked. Value-only and collection conversions may throw from expression compilation, constructors, incompatible key types, or mapping of unsupported types. The default static-cache path can wrap first-time construction failures in `TypeInitializationException`.

```csharp
public sealed record SourceItem(string Name);
public sealed class DestinationItem
{
    public string? Name { get; set; }
}

SourceItem[] rows = [new("A"), new("B")];
DestinationItem[] mapped =
    rows.FastMap<SourceItem[], DestinationItem[]>()!;

Dictionary<string, SourceItem> byId = new()
{
    ["a"] = new("A")
};
Dictionary<string, DestinationItem> mappedById =
    byId.FastMap<Dictionary<string, SourceItem>,
        Dictionary<string, DestinationItem>>()!;
```

This mapper does not validate, sanitize, authorize, or deep-clone privileged data. Do not map an untrusted object directly into fields a caller may not set; use an explicit DTO/projection.

## Cache management

`FastMapper.CacheManager` exposes Core's `ICacheManagerApi<MapperCacheKey, Delegate>`. `ClearCache()` clears only the managed cache; it cannot remove mappers already stored in the default overload's per-generic-type static cache.

```csharp
FastMapper.CacheManager.SetLimitedCacheSize(50);
FastMapper.CacheManager.SetUseLimitedCache(true);

FastMapper.MapperCacheKey key = new(typeof(Source), typeof(Destination));
bool same = key == new(typeof(Source), typeof(Destination));
bool different = key != new(typeof(Source), typeof(object));
bool equalByMethod = key.Equals(new(typeof(Source), typeof(Destination)));
bool equalByObject = key.Equals(
    (object)new FastMapper.MapperCacheKey(
        typeof(Source),
        typeof(Destination)));
int hash = key.GetHashCode();
Console.WriteLine($"{key.SourceType.Name} -> {key.DestType.Name}");

FastMapper.CacheManager.ClearAllCaches();
FastMapper.ClearCache(); // Same managed caches; not static generic caches.

var cacheView = FastMapper.CacheManager.GetCache();
var limitedView = FastMapper.CacheManager.GetLimitedCache();
int cacheSize = FastMapper.CacheManager.GetLimitedCacheSize();
bool isLimited = FastMapper.CacheManager.IsUsingLimitedCache();
FastMapper.CacheManager.SetUseLimitedCache(false);
Delegate mapper = FastMapper.CacheManager.GetOrAddCache(
    key,
    _ => (Func<Source, Destination>)(value => new Destination()));
bool added = FastMapper.CacheManager.TryAddCache(
    new(typeof(Source), typeof(Destination)),
    mapper);
_ = FastMapper.CacheManager.GetOrAddLimitedCache(key, _ => mapper);
_ = FastMapper.CacheManager.TryAddLimitedCache(
    new(typeof(Source), typeof(Destination)),
    mapper);
FastMapper.CacheManager.ClearCache();
FastMapper.CacheManager.ClearLimitedCache();
FastMapper.CacheManager.ClearAllCaches(); // Remove demonstration delegates.
```

`MapperCacheKey` is a readonly struct with a two-`Type` constructor, public `SourceType`/`DestType` fields, typed and object equality overloads, `GetHashCode`, and `==`/`!=`.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
