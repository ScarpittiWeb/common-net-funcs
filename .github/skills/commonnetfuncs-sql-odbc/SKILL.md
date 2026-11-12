---
name: commonnetfuncs-sql-odbc
description: 'Use CommonNetFuncs.Sql.Odbc DirectQuery with an installed ODBC driver. Consult for raw SQL safety, streaming, timeouts, retries, and transactions.'
---

# CommonNetFuncs.Sql.Odbc

Use `CommonNetFuncs.Sql.Odbc.DirectQuery` for trusted SQL against an installed
ODBC driver. Construct `DirectQuery`, then choose `GetDataTableAsync`,
`RunUpdateQueryAsync`, `GetDataStreamAsync<T>`, or a synchronous equivalent.

## Workflow

1. Install/configure the correct ODBC driver, architecture, and connection
   string for the target database. Protect connection-string credentials.
2. Pass a trusted, provider-compatible SQL statement. Configure
   `commandTimeoutSeconds` and `maxRetry` explicitly as needed; defaults are
   30 seconds and 3 total attempts.
3. Check `UpdateResult.Success` and `RecordsChanged` for updates. An empty
   `DataTable` can indicate either no rows or a logged failure. Streaming
   enumeration holds database resources until it completes or is disposed.

## SQL safety

- Direct-query methods accept raw SQL and do not accept a parameter collection.
  For user-provided values, use a lower-level `OdbcCommand` with typed
  `OdbcParameter`s and the driver's positional `?` markers. Do not substitute
  string sanitization for parameters.
- Map dynamic identifiers to a fixed allow-list. The shared contract has no
  transaction argument; use provider ADO.NET when an explicit transaction is
  needed.
- Retrying a non-idempotent update can repeat its effect. Select retry policy
  and idempotency deliberately; don't assume all failures throw.

See [DirectQuery.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.Odbc/DirectQuery.cs), the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Sql.Odbc/README.md), and the
[shared SQL skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-sql-common/SKILL.md).
