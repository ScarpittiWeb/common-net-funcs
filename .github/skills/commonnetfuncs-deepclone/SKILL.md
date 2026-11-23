---
name: commonnetfuncs-deepclone
description: 'Use CommonNetFuncs.DeepClone to clone C# object graphs with expression trees. Consult for cycles, reference tracking, cache use, and unsupported runtime objects.'
---

# CommonNetFuncs.DeepClone

Import `CommonNetFuncs.DeepClone` and call `DeepClone` on the source. It is a
graph-copying helper, not a serializer, validator, or permission filter.

```csharp
using CommonNetFuncs.DeepClone;

Customer? copy = customer.DeepClone();
```

## Workflow and limits

1. Check the nullable result, especially when the source may be `null`.
2. Use the default reference tracking for an independent operation. It
   preserves shared references and handles cycles; if supplying
   `copiedReferencesDict`, use reference equality and do not share the mutable
   dictionary concurrently.
3. The compiled copier is cached by default; `useCache: false` bypasses that
   cache. Consider cache growth if mapping many dynamically generated types.
4. Test representative models. Arrays, nested collections, and cyclic graphs
   are covered by the package tests, but delegates explicitly throw
   `ArgumentException`. Do not assume streams, native handles, proxies, or
   other runtime resources can safely be cloned.
5. A clone can still contain secrets or privileged data. Use explicit DTO
   projection and validation at trust boundaries.

See the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.DeepClone/README.md) and
[ExpressionTrees.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.DeepClone/ExpressionTrees.cs).
