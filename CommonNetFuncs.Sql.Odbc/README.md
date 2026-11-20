# CommonNetFuncs.Sql.Odbc

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Sql.Odbc)](https://www.nuget.org/packages/CommonNetFuncs.Sql.Odbc/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Sql.Odbc)](https://www.nuget.org/packages/CommonNetFuncs.Sql.Odbc/)

ODBC implementation of `IDirectQuery` from `CommonNetFuncs.Sql.Common`. Targets `net11.0` and `netstandard2.1`.

## Contents

- [Prerequisites](#prerequisites)
- [Important behavior](#important-behavior)
- [Construction](#construction)
- [Complete `DirectQuery` examples](#complete-directquery-examples)
  - [Result and failure semantics](#result-and-failure-semantics)
  - [Streaming lifetime and retry limitation](#streaming-lifetime-and-retry-limitation)
- [Parameterized ODBC SQL](#parameterized-odbc-sql)
- [Transactions](#transactions)
- [Installation](#installation)
- [License](#license)

## Prerequisites

Install an ODBC driver for the target database and process architecture, and configure/protect the connection string. This package does not install drivers or data sources. Driver SQL syntax, parameter markers, affected-row behavior, and transaction support remain provider-specific.

## Important behavior

- Every operation accepts **raw SQL text**, not a parameter collection. The provider wrapper creates only a connection and command.
- Parameterize untrusted values with `OdbcCommand` and typed `OdbcParameter` instances in a lower-level call. ODBC commonly uses positional `?` markers; follow the selected driver's convention.
- Dynamic identifiers must come from a fixed allow-list.
- `commandTimeoutSeconds` defaults to `30`.
- `maxRetry` is the total number of attempts; its default `3` is not three additional retries. Avoid retrying non-idempotent writes.
- Table and update failures are logged through NLog and converted to an empty `DataTable` or failed `UpdateResult`.
- `GetDataDirectAsync` buffers mapped rows and throws `DataException` after exhausted retries.
- The contract has no transaction argument.

Mapped operations require `T : class, new()`. Public writable properties are mapped from exact column names. Missing/incompatible columns and values can throw `DataException`; `DBNull` maps to the property's default value. `useCache` controls the shared compiled-mapper cache and defaults to `true`.

## Construction

The default constructor creates `OdbcConnection` instances:

```cs
using CommonNetFuncs.Sql.Odbc;

DirectQuery queries = new();
```

The optional connection factory is a public constructor overload:

```cs
using System.Data.Odbc;
using CommonNetFuncs.Sql.Common;

IDirectQuery queries = new DirectQuery(connectionString =>
    new OdbcConnection(connectionString));
```

The factory receives the exact `connStr` passed to each operation. It runs when the operation executes; an async-iterator body does not execute until enumeration starts.

## Complete `DirectQuery` examples

The following calls clearly demonstrate all seven public operations. SQL is fixed/trusted; for user values see [Parameterized ODBC SQL](#parameterized-odbc-sql).

```cs
using System.Data;
using CommonNetFuncs.Sql.Common;
using CommonNetFuncs.Sql.Odbc;

const string connectionString =
    "Driver={ODBC Driver 18 for SQL Server};Server=sql.example;Database=App;Trusted_Connection=Yes;Encrypt=Yes;";

DirectQuery queries = new();
CancellationToken cancellationToken = CancellationToken.None;

// GetDataTableAsync overload
using DataTable asyncTable = await queries.GetDataTableAsync(
    "SELECT Id, Name FROM Customers WHERE IsActive = 1",
    connectionString,
    commandTimeoutSeconds: 30,
    maxRetry: 3,
    cancellationToken);

// GetDataTable
using DataTable table = queries.GetDataTable(
    "SELECT Id, Name FROM Customers WHERE IsActive = 1",
    connectionString,
    commandTimeoutSeconds: 30,
    maxRetry: 3);

// RunUpdateQueryAsync
UpdateResult asyncUpdate = await queries.RunUpdateQueryAsync(
    "UPDATE Customers SET IsActive = 1 WHERE Id = 42",
    connectionString,
    commandTimeoutSeconds: 30,
    maxRetry: 1,
    cancellationToken);

// RunUpdateQuery
UpdateResult update = queries.RunUpdateQuery(
    "UPDATE Customers SET IsActive = 1 WHERE Id = 42",
    connectionString,
    commandTimeoutSeconds: 30,
    maxRetry: 1);

// GetDataStreamAsync<T>
await foreach (Customer row in queries.GetDataStreamAsync<Customer>(
    "SELECT Id, Name FROM Customers ORDER BY Id",
    connectionString,
    commandTimeoutSeconds: 30,
    maxRetry: 3,
    useCache: true,
    cancellationToken))
{
    await ProcessAsync(row, cancellationToken);
}

// GetDataStream<T> (see the synchronous-stream limitation below)
IEnumerable<Customer> stream = queries.GetDataStream<Customer>(
    "SELECT Id, Name FROM Customers ORDER BY Id",
    connectionString,
    commandTimeoutSeconds: 30,
    maxRetry: 3,
    useCache: true,
    cancellationToken);
foreach (Customer row in stream)
{
    Process(row);
}

// GetDataDirectAsync<T>
IEnumerable<Customer> buffered = await queries.GetDataDirectAsync<Customer>(
    "SELECT Id, Name FROM Customers ORDER BY Id",
    connectionString,
    commandTimeoutSeconds: 30,
    maxRetry: 3,
    useCache: true,
    cancellationToken);
```

`Customer` is an ordinary mutable class:

```cs
public sealed class Customer
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
```

### Result and failure semantics

For updates, always inspect both fields:

```cs
UpdateResult result = await queries.RunUpdateQueryAsync(sql, connectionString);
if (!result.Success || result.RecordsChanged != 1)
{
    // The update failed, changed no rows, or changed more than expected.
}
```

An empty table from `GetDataTable*` is ambiguous: it may contain no matching rows or represent an exhausted, logged failure.

### Streaming lifetime and retry limitation

`GetDataStreamAsync<T>` keeps its provider connection and command alive until the async enumeration completes or is disposed; fully consume or promptly dispose it. The current `maxRetry` wrapper is created before enumeration and does not surround failures that occur on `MoveNextAsync`, so a runtime execution failure may not be retried.

The provider `GetDataStream<T>` currently returns a lazy enumerable after disposing its outer connection/command scope. Do not rely on deferred enumeration of this provider overload in the current release. Prefer `GetDataStreamAsync<T>`, or use `CommonNetFuncs.Sql.Common.DirectQuery.GetDataStream<T>` with a caller-owned `OdbcConnection`.

## Parameterized ODBC SQL

Use provider ADO.NET for values rather than concatenation:

```cs
using System.Data.Odbc;

await using OdbcConnection connection = new(connectionString);
await connection.OpenAsync(cancellationToken);
await using OdbcCommand command = connection.CreateCommand();
command.CommandText = "SELECT Id, Name FROM Customers WHERE Id = ?";
command.Parameters.Add(new OdbcParameter { Value = 42 });

await using OdbcDataReader reader =
    await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
}
```

## Transactions

Multi-operation atomicity requires an explicit provider transaction:

```cs
await using OdbcConnection connection = new(connectionString);
await connection.OpenAsync(cancellationToken);
await using OdbcTransaction transaction =
    (OdbcTransaction)await connection.BeginTransactionAsync(cancellationToken);
await using OdbcCommand command = connection.CreateCommand();
command.Transaction = transaction;

try
{
    command.CommandText = "UPDATE Accounts SET Balance = Balance - 10 WHERE Id = ?";
    command.Parameters.Add(new OdbcParameter { Value = 1 });
    await command.ExecuteNonQueryAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
}
catch
{
    await transaction.RollbackAsync(cancellationToken);
    throw;
}
```

`DirectQuery` does not participate in this transaction because it cannot accept `connection`, `command`, or `transaction`; use the provider command directly for the transaction's commands.

## Installation

```bash
dotnet add package CommonNetFuncs.Sql.Odbc
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
