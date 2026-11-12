---
name: commonnetfuncs-fastmap
description: 'Use CommonNetFuncs.FastMap for cached C# source-to-destination object mapping. Consult for nullable results, mapping limitations, cache behavior, and DTO safety.'
---

# CommonNetFuncs.FastMap

Use `FastMapper.FastMap<TSource, TDest>` for expression-based mapping between
model types. Import `CommonNetFuncs.FastMap` and check the nullable result:

```csharp
using CommonNetFuncs.FastMap;

Destination? destination = source.FastMap<Source, Destination>();
```

## Workflow and limits

1. Verify the destination model and its matching properties against the
   [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.FastMap/README.md) and
   [FastMapper.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.FastMap/FastMapper.cs).
2. Test nested objects, collections, constructor and setter requirements, and
   nulls for the specific model. Matching uses property names; incompatible or
   missing properties can be skipped. Same-type properties are assigned
   directly, so nested references can still point to the original objects.
   Do not assume mapping is a deep clone or that cyclic graphs are supported.
3. Mapping delegates are cached by default; the `useCache` overload can bypass
   caching. Cache size can grow with distinct source/destination type pairs.
4. Mapping does not authorize or sanitize input. Do not map untrusted input
   directly onto privileged fields; use an explicit allowed-field DTO or
   projection at a trust boundary.
