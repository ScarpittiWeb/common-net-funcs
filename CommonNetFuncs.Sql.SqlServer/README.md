# CommonNetFuncs.Sql.SqlServer

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Sql.SqlServer)](https://www.nuget.org/packages/CommonNetFuncs.Sql.SqlServer/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Sql.SqlServer)](https://www.nuget.org/packages/CommonNetFuncs.Sql.SqlServer/)

SQL Server implementation of `IDirectQuery` from `CommonNetFuncs.Sql.Common`. Targets `net11.0` and `netstandard2.1`.

## Contents

- [Important behavior](#important-behavior)
- [Construction](#construction)
- [All `DirectQuery` operations](#all-directquery-operations)
- [Streaming and ownership](#streaming-and-ownership)
- [Parameterized SQL Server commands](#parameterized-sql-server-commands)
- [Transactions](#transactions)
- [Diagnostic T-SQL rendering](#diagnostic-t-sql-rendering)
- [Installation](#installation)
- [License](#license)

## Important behavior

- Operations accept trusted T-SQL, not a parameter collection. Protect the connection string and configure authentication/TLS appropriately.
- Parameterize untrusted values with `SqlCommand` and typed `SqlParameter` values instead of interpolating `sql`.
- Choose dynamic identifiers from a fixed allow-list and quote them correctly.
- `commandTimeoutSeconds` defaults to `30`; `maxRetry` defaults to `3` **total attempts**. Retrying a non-idempotent write can repeat its effect.
- Table/update exceptions are logged through NLog. After exhaustion, table methods return an empty `DataTable` and update methods return a default `UpdateResult`.
- Mapped operations require `T : class, new()` and exact public writable property/column names. `DBNull` maps to the property's default; missing columns and incompatible types can throw `DataException`.
- The contract has no transaction argument.

## Construction

The constructor accepts an optional connection factory:

```cs
using CommonNetFuncs.Sql.SqlServer;
using Microsoft.Data.SqlClient;

DirectQuery defaultFactory = new();
DirectQuery customFactory = new(connectionString =>
    new SqlConnection(connectionString));
```

Each operation creates, owns, and disposes its provider connection and command. Async-iterator setup is deferred until enumeration starts.

## All `DirectQuery` operations

Every public query method is demonstrated below. The SQL is fixed/trusted:

```cs
using System.Data;
using CommonNetFuncs.Sql.Common;
using CommonNetFuncs.Sql.SqlServer;

const string connectionString =
    "Server=sql.example;Database=App;Authentication=Active Directory Default;Encrypt=True;";

DirectQuery queries = new();
CancellationToken cancellationToken = CancellationToken.None;

// GetDataTableAsync
using DataTable asyncTable = await queries.GetDataTableAsync(
    "SELECT Id, Name FROM dbo.Customers WHERE IsActive = 1",
    connectionString, 30, 3, cancellationToken);

// GetDataTable
using DataTable table = queries.GetDataTable(
    "SELECT Id, Name FROM dbo.Customers WHERE IsActive = 1",
    connectionString, 30, 3);

// RunUpdateQueryAsync
UpdateResult asyncResult = await queries.RunUpdateQueryAsync(
    "UPDATE dbo.Customers SET IsActive = 1 WHERE Id = 42",
    connectionString, 30, 1, cancellationToken);

// RunUpdateQuery
UpdateResult result = queries.RunUpdateQuery(
    "UPDATE dbo.Customers SET IsActive = 1 WHERE Id = 42",
    connectionString, 30, 1);

// GetDataStreamAsync<T>
await foreach (Customer row in queries.GetDataStreamAsync<Customer>(
    "SELECT Id, Name FROM dbo.Customers ORDER BY Id",
    connectionString, 30, 3, useCache: true, cancellationToken))
{
    await ProcessAsync(row, cancellationToken);
}

// GetDataStream<T> (see synchronous-stream limitation below)
IEnumerable<Customer> stream = queries.GetDataStream<Customer>(
    "SELECT Id, Name FROM dbo.Customers ORDER BY Id",
    connectionString, 30, 3, useCache: true, cancellationToken);
foreach (Customer row in stream)
{
    Process(row);
}

// GetDataDirectAsync<T>
IEnumerable<Customer> buffered = await queries.GetDataDirectAsync<Customer>(
    "SELECT Id, Name FROM dbo.Customers ORDER BY Id",
    connectionString, 30, 3, useCache: true, cancellationToken);
```

```cs
public sealed class Customer
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
```

Inspect both update fields:

```cs
UpdateResult update = await queries.RunUpdateQueryAsync(sql, connectionString);
if (!update.Success || update.RecordsChanged != 1)
{
    // Handle failure or an unexpected affected-row count.
}
```

An empty result table can mean either zero rows or a logged error.

## Streaming and ownership

`GetDataStreamAsync<T>` retains its connection/command until the async sequence finishes or is disposed. Consume it completely or dispose the enumerator promptly.

The current streaming retry helper does not surround exceptions raised during `MoveNextAsync`, so a runtime query/mapping failure may not be retried despite `maxRetry`.

`GetDataStream<T>` currently returns a lazy sequence after its outer connection/command scope is disposed. Do not rely on this provider overload in the current release. Prefer `GetDataStreamAsync<T>` or use `CommonNetFuncs.Sql.Common.DirectQuery.GetDataStream<T>` with a caller-owned `SqlConnection`.

`GetDataDirectAsync<T>` buffers all mapped rows and throws `DataException` after all attempts fail.

## Parameterized SQL Server commands

Use lower-level provider ADO.NET for values:

```cs
using Microsoft.Data.SqlClient;

await using SqlConnection connection = new(connectionString);
await connection.OpenAsync(cancellationToken);
await using SqlCommand command = new(
    "SELECT Id, Name FROM dbo.Customers WHERE TenantId = @tenantId",
    connection);
command.Parameters.Add(new SqlParameter("@tenantId", tenantId));

await using SqlDataReader reader =
    await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
}
```

## Transactions

The wrapper methods cannot participate in an existing transaction. Use `SqlTransaction` directly for atomic work:

```cs
await using SqlConnection connection = new(connectionString);
await connection.OpenAsync(cancellationToken);
await using SqlTransaction transaction =
    (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

try
{
    await using SqlCommand debit = connection.CreateCommand();
    debit.Transaction = transaction;
    debit.CommandText =
        "UPDATE dbo.Accounts SET Balance = Balance - @amount WHERE Id = @id";
    debit.Parameters.Add(new SqlParameter("@amount", amount));
    debit.Parameters.Add(new SqlParameter("@id", sourceId));
    await debit.ExecuteNonQueryAsync(cancellationToken);

    await transaction.CommitAsync(cancellationToken);
}
catch
{
    await transaction.RollbackAsync(cancellationToken);
    throw;
}
```

## Diagnostic T-SQL rendering

On `net11.0`, the package exposes `CommonNetFuncs.Sql.SqlServer.Debug.RenderCommandAsScript(DbCommand)`. This API is excluded from the `netstandard2.1` asset by `#if !NETSTANDARD2_1`.

```cs
using System.Data;
using Microsoft.Data.SqlClient;
using CommonNetFuncs.Sql.SqlServer;

using SqlCommand command = new(
    "SELECT Id, Name FROM dbo.Customers WHERE Id = @id");
command.Parameters.Add(new SqlParameter("@id", SqlDbType.Int)
{
    Value = 42
});

string diagnosticScript = Debug.RenderCommandAsScript(command);
```

It emits `DECLARE` statements and the command text for tools such as SSMS. It includes actual parameter values and is **diagnostics only**, not a safe substitute for runtime parameterization or a log suitable for untrusted access.

## Installation

```bash
dotnet add package CommonNetFuncs.Sql.SqlServer
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
