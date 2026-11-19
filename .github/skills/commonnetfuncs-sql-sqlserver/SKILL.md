---
name: commonnetfuncs-sql-sqlserver
description: 'Use CommonNetFuncs.Sql.SqlServer DirectQuery with SQL Server. Consult for raw SQL safety, streaming, retry behavior, transactions, and diagnostic logging.'
---

# CommonNetFuncs.Sql.SqlServer

Use `CommonNetFuncs.Sql.SqlServer.DirectQuery` for trusted SQL Server queries.
It uses `SqlConnection` and implements the shared direct-query contract.

## Workflow

1. Configure a protected connection string and appropriate authentication/TLS.
   Construct `DirectQuery`.
2. Select `GetDataTableAsync`, `RunUpdateQueryAsync`,
   `GetDataStreamAsync<T>`, or a synchronous equivalent. Defaults are a
   30-second command timeout and 3 total attempts.
3. Inspect `UpdateResult.Success` and `RecordsChanged`; an empty `DataTable`
   can mean no rows or a logged failure. Release streaming enumerators promptly
   so connections and commands are disposed.

## SQL safety

- These methods accept raw SQL and no parameter collection. For untrusted
  values use a lower-level `SqlCommand` with typed `SqlParameter`s; do not
  interpolate request data or substitute string cleaning for parameterization.
- Choose dynamic identifiers from a fixed allow-list and quote them
  appropriately. Use provider transaction APIs when multiple operations must
  be atomic.
- Retrying a non-idempotent update can repeat its effects. Diagnostic SQL
  rendering can expose values; restrict access to the resulting logs.

See [DirectQuery.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.SqlServer/DirectQuery.cs), the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.SqlServer/README.md), and the
[shared SQL skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-sql-common/SKILL.md).
