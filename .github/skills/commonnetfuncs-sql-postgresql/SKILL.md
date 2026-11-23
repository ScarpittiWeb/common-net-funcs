---
name: commonnetfuncs-sql-postgresql
description: 'Use CommonNetFuncs.Sql.PostgreSql DirectQuery with Npgsql. Consult for raw SQL safety, streaming, retries, transactions, and diagnostic logging.'
---

# CommonNetFuncs.Sql.PostgreSql

Use `CommonNetFuncs.Sql.PostgreSql.DirectQuery` for trusted PostgreSQL SQL.
It targets `net11.0` and uses Npgsql.

## Workflow

1. Configure a protected PostgreSQL connection string and the desired TLS and
   identity settings. Construct `DirectQuery`.
2. Select `GetDataTableAsync`, `RunUpdateQueryAsync`,
   `GetDataStreamAsync<T>`, or the corresponding synchronous method. The
   default command timeout is 30 seconds and `maxRetry` defaults to 3 total
   attempts.
3. Check `UpdateResult.Success` and `RecordsChanged`; an empty `DataTable` can
   mean no rows or a logged failure. Fully consume or dispose streaming
   enumerators to release their connection and command.

## SQL safety

- Direct-query methods take raw SQL, not parameter collections. For untrusted
  values use a lower-level `NpgsqlCommand` with typed `NpgsqlParameter`s; never
  interpolate user input or rely on string-cleaning helpers.
- Dynamic identifiers require a hard-coded allow-list and appropriate
  PostgreSQL quoting. Use provider transaction APIs for multi-operation
  atomicity; the shared direct-query contract has no transaction parameter.
- Review retries on non-idempotent updates. SQL-rendering diagnostics can
  expose query values and should be restricted to trusted logs.

See [DirectQuery.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.PostgreSql/DirectQuery.cs), the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.PostgreSql/README.md), and the
[shared SQL skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-sql-common/SKILL.md).
