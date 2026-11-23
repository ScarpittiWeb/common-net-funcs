# CommonNetFuncs.DeepClone

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.DeepClone)](https://www.nuget.org/packages/CommonNetFuncs.DeepClone/)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.DeepClone)](https://www.nuget.org/packages/CommonNetFuncs.DeepClone/)

Expression-tree graph cloning for `net11.0` and `netstandard2.1`. The package exposes one operation, `ExpressionTrees.DeepClone<T>`, plus a cache-management property.

The package does not currently contain reflection- or serialization-based cloning methods; `DeepCloneR` and `DeepCloneS` do not exist.

## Contents

- [CommonNetFuncs.DeepClone](#commonnetfuncsdeepclone)
  - [Contents](#contents)
  - [Installation](#installation)
  - [`DeepClone<T>`](#deepclonet)
  - [Cache](#cache)
  - [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.DeepClone
```

```csharp
using CommonNetFuncs.Core;
using CommonNetFuncs.DeepClone;
using System.Runtime.CompilerServices;
```

## `DeepClone<T>`

```csharp
public sealed class Person
{
    public string Name { get; set; } = string.Empty;
    public Person? Manager { get; set; }
    public List<string> Tags { get; set; } = [];
}

Person original = new() { Name = "Chris", Tags = ["staff"] };
original.Manager = original; // A cycle is supported.

Person? clone = original.DeepClone();                  // cached mapper
Person? uncached = original.DeepClone(useCache: false); // no shared cache
Person? fromNull = ((Person?)null).DeepClone();         // null
```

The generic method has one public signature:

```csharp
sealed class IdentityComparer : EqualityComparer<object>
{
    public override bool Equals(object? x, object? y) =>
        ReferenceEquals(x, y);
    public override int GetHashCode(object obj) =>
        RuntimeHelpers.GetHashCode(obj);
}

Dictionary<object, object> references = new(new IdentityComparer());

Person? trackedClone = original.DeepClone(
    copiedReferencesDict: references,
    useCache: true);
```

The nullable annotation guarantees a non-null result when the source is non-null. A default or custom dictionary may be supplied; it is populated with original-to-copy mappings and must use reference equality for correct identity behavior. The default dictionary does this. Do not share a dictionary across concurrent operations.

The copier starts with `MemberwiseClone`, then recursively copies reference-bearing fields. It preserves aliases and cycles within one call, copies arrays (including multidimensional arrays), collections backed by fields, nested structs containing references, and private/readonly fields. Primitive values, enums, strings, and simple structs are reused by value. Delegate-valued fields are set to `null`; passing a delegate as the top-level object throws `ArgumentException`.

This is not guaranteed to clone arbitrary runtime resources safely. Streams, native handles, synchronization primitives, proxies, weak references, and framework objects with hidden invariants may remain unusable or retain internal state despite the new outer object. It is also not a serializer, access control boundary, or DTO projection. A clone retains secrets present in the source.

## Cache

Compiled copier delegates are cached by `Type`. `useCache: false` compiles without adding to the shared cache. The exposed cache is a Core `ICacheManagerApi<Type, Func<object, Dictionary<object, object>, object>>`.

```csharp
ExpressionTrees.CacheManager.ClearAllCaches();
ExpressionTrees.CacheManager.SetLimitedCacheSize(128);
ExpressionTrees.CacheManager.SetUseLimitedCache(true);

bool limited = ExpressionTrees.CacheManager.IsUsingLimitedCache();
int capacity = ExpressionTrees.CacheManager.GetLimitedCacheSize();
IReadOnlyDictionary<
    Type,
    Func<object, Dictionary<object, object>, object>> current =
        ExpressionTrees.CacheManager.GetCache();
IReadOnlyDictionary<
    Type,
    Func<object, Dictionary<object, object>, object>?> limitedCurrent =
        ExpressionTrees.CacheManager.GetLimitedCache();

ExpressionTrees.CacheManager.ClearCache();
ExpressionTrees.CacheManager.ClearLimitedCache();

Func<object, Dictionary<object, object>, object> copier =
    ExpressionTrees.CacheManager.GetOrAddCache(
        typeof(Person),
        _ => static (source, references) => source);
bool addedUnlimited =
    ExpressionTrees.CacheManager.TryAddCache(typeof(List<int>), copier);
Func<object, Dictionary<object, object>, object> limitedCopier =
    ExpressionTrees.CacheManager.GetOrAddLimitedCache(
        typeof(string), _ => copier);
bool addedLimited =
    ExpressionTrees.CacheManager.TryAddLimitedCache(
        typeof(Dictionary<int, int>), copier);
ExpressionTrees.CacheManager.ClearAllCaches(); // Remove the demonstration delegates.
```

`CacheManager` is configurable and thread-safe, but the call-scoped memo is thread-local. The cache can grow with the number of distinct runtime types; clear or limit it when cloning many dynamically generated types.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
