---
name: commonnetfuncs-sql-common
description: 'Use CommonNetFuncs.Sql.Common contracts and query-string helpers with ODBC, PostgreSQL, or SQL Server. Consult for raw SQL safety, retries, and result handling.'
---

# CommonNetFuncs.Sql.Common

This package supplies `IDirectQuery`, `UpdateResult`, and shared query-string
helpers. Select a provider-specific package for actual database connections:
`CommonNetFuncs.Sql.Odbc`, `.Sql.PostgreSql`, or `.Sql.SqlServer`.

## Workflow

1. Choose the provider and a trusted SQL statement. Read the
   [interface](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.Common/IDirectQuery.cs) for available
   result, timeout, retry, and cancellation options.
2. Inspect `UpdateResult.Success` and `RecordsChanged`; do not infer success
   merely from completing the call. DataTable retrieval can return an empty
   table after failure, which is indistinguishable from an empty result without
   additional error information.
3. Use streaming methods for large results and dispose the enumerator promptly
   so the underlying connection can be released.

## SQL safety

- The direct-query contract accepts SQL text, not a collection of database
  parameters. Never interpolate untrusted values into that text. For
  parameterized queries use the provider's `DbCommand` and typed parameters,
  or another API that exposes parameters.
- `CleanQueryParam`, `IsClean`, and `SanitizeSqlParameter` are string
  transformations/checks, **not** SQL parameterization or an injection
  defense. They can also change the supplied value. Map dynamic table and
  column identifiers to an explicit allow-list.
- The contract has no transaction argument; use an appropriate transaction API
  when several operations must be atomic. The default `maxRetry` of 3 means
  three total attempts, not an initial attempt plus three retries. Retrying a
  non-idempotent write can repeat its effect.

See [QueryParameters.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.Common/QueryParameters.cs)
and the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.Common/README.md).
