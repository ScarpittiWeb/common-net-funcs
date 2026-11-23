---
name: commonnetfuncs-efcore
description: 'Use CommonNetFuncs.EFCore query and CRUD helpers safely. Consult for DbContext lifetime, tracking, save behavior, global filters, and streaming limitations.'
---

# CommonNetFuncs.EFCore

This `net11.0` package supplies `BaseDbContextActions<TEntity, TContext>`,
`IBaseDbContextActions`, query helpers, and navigation-property utilities.
The actions class accepts an `IServiceProvider` and resolves `TContext` from it.

## Workflow

1. Select the needed query or write method and inspect its exact context
   lifetime, tracking, and return behavior in the source.
2. Keep an EF `DbContext` alive through query enumeration and through the
   complete unit of work. For operations affected by the limitations below,
   use a correctly scoped context and EF APIs directly, or provide a corrected
   actions implementation.
3. Check returned `bool`/nullable results and logs; do not assume every caught
   failure propagates as an exception. Apply authorization and tenant filters
   independently.

## Current lifetime and persistence limitations

- The `GetQueryAll`, `GetQueryWithFilter`, and corresponding `*Full` query
  methods (including projection overloads) return `IQueryable` after disposing
  the resolved context. Materializing and streaming helpers built on those
  methods are affected too. Do not rely on them; build and materialize queries
  against a context whose lifetime you control, or use a corrected
  implementation. Several non-full filtered, projection, and streaming
  overloads also fail to forward the supplied `GlobalFilterOptions`; verify
  the exact overload before relying on filter-disabling options.
- `CreateMany`, `Update`, and the entity-list `UpdateMany(List<TEntity>)`
  change tracked state on a context that they dispose without saving.
  `SaveChanges` resolves a context separately, so a later call cannot reliably
  persist that state. Use an explicit EF unit of work or a corrected
  implementation instead of assuming that sequence succeeds. The predicate
  `UpdateMany` overload uses `ExecuteUpdateAsync` and acts immediately.
  `SaveChanges` returns `false` when no rows are saved, as well as on logged
  failures.
- Several methods dispose a context resolved from the service provider. Do not
  assume a DI-scoped context remains usable after one of these calls.
- Query methods commonly default to `trackEntities: false`. Choose tracking
  intentionally; disconnected entities do not automatically persist changes.
  Predicate-based `DeleteMany` uses `ExecuteDeleteAsync` and acts immediately,
  unlike tracked state changes.
- Restrict `GlobalFilterOptions` that disable filters to trusted code,
  especially for tenant isolation. Query-rendering diagnostics can expose SQL
  and values; restrict logs accordingly.

See [BaseDbContextActions.Read.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.EFCore/BaseDbContextActions.Read.cs),
[BaseDbContextActions.Write.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.EFCore/BaseDbContextActions.Write.cs),
and the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.EFCore/README.md).
