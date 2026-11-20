# CommonNetFuncs.Sql.PostgreSql

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Sql.PostgreSql)](https://www.nuget.org/packages/CommonNetFuncs.Sql.PostgreSql/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Sql.PostgreSql)](https://www.nuget.org/packages/CommonNetFuncs.Sql.PostgreSql/)

PostgreSQL/Npgsql implementation of `IDirectQuery` from `CommonNetFuncs.Sql.Common`. Targets `net11.0`.

## Contents

- [Important behavior](#important-behavior)
- [Construction](#construction)
- [All `DirectQuery` operations](#all-directquery-operations)
- [Streaming and ownership](#streaming-and-ownership)
- [Parameterized PostgreSQL SQL](#parameterized-postgresql-sql)
- [Transactions](#transactions)
- [Diagnostic SQL rendering](#diagnostic-sql-rendering)
- [Installation](#installation)
- [License](#license)

## Important behavior

- Calls accept PostgreSQL SQL text, not a parameter collection. Protect the connection string and configure appropriate TLS and authentication.
- Parameterize untrusted values with `NpgsqlCommand` and typed `NpgsqlParameter` values; do not interpolate them into the `sql` argument.
- Use an explicit allow-list and PostgreSQL identifier quoting for dynamic identifiers.
- `commandTimeoutSeconds` defaults to `30`; `maxRetry` defaults to `3` **total attempts**. Retrying a non-idempotent statement can repeat its effect.
- Table/update failures are logged through NLog. Table failures become an empty `DataTable`; update failures become the default `UpdateResult`.
- Streaming and direct mapping require `T : class, new()` and exact, case-sensitive mapping to public writable property names. `DBNull` becomes the property's default value; unsupported conversions/missing columns can cause `DataException`.
- The direct-query contract has no transaction parameter.

## Construction

The constructor has one optional `Func<string, NpgsqlConnection>` factory:

```cs
using CommonNetFuncs.Sql.PostgreSql;
using Npgsql;

DirectQuery defaultFactory = new();
DirectQuery customFactory = new(connectionString =>
    new NpgsqlConnection(connectionString));
```

Each operation creates a provider connection and command; the wrapper owns and disposes them. Async-iterator setup is deferred until enumeration starts.

## All `DirectQuery` operations

The examples demonstrate every public operation. They contain only trusted static SQL:

```cs
using System.Data;
using CommonNetFuncs.Sql.Common;
using CommonNetFuncs.Sql.PostgreSql;

const string connectionString =
    "Host=db.example;Database=app;Username=app;Password=from-secret-store;Ssl Mode=Require";

DirectQuery queries = new();
CancellationToken cancellationToken = CancellationToken.None;

// GetDataTableAsync
using DataTable asyncTable = await queries.GetDataTableAsync(
    """SELECT id, name FROM customers WHERE is_active = TRUE""",
    connectionString, 30, 3, cancellationToken);

// GetDataTable
using DataTable table = queries.GetDataTable(
    """SELECT id, name FROM customers WHERE is_active = TRUE""",
    connectionString, 30, 3);

// RunUpdateQueryAsync
UpdateResult asyncResult = await queries.RunUpdateQueryAsync(
    """UPDATE customers SET is_active = TRUE WHERE id = 42""",
    connectionString, 30, 1, cancellationToken);

// RunUpdateQuery
UpdateResult result = queries.RunUpdateQuery(
    """UPDATE customers SET is_active = TRUE WHERE id = 42""",
    connectionString, 30, 1);

// GetDataStreamAsync<T>
await foreach (Customer row in queries.GetDataStreamAsync<Customer>(
    """SELECT id AS "Id", name AS "Name" FROM customers ORDER BY id""",
    connectionString, 30, 3, useCache: true, cancellationToken))
{
    await ProcessAsync(row, cancellationToken);
}

// GetDataStream<T> (see synchronous-stream limitation below)
IEnumerable<Customer> stream = queries.GetDataStream<Customer>(
    """SELECT id AS "Id", name AS "Name" FROM customers ORDER BY id""",
    connectionString, 30, 3, useCache: true, cancellationToken);
foreach (Customer row in stream)
{
    Process(row);
}

// GetDataDirectAsync<T>
IEnumerable<Customer> buffered = await queries.GetDataDirectAsync<Customer>(
    """SELECT id AS "Id", name AS "Name" FROM customers ORDER BY id""",
    connectionString, 30, 3, useCache: true, cancellationToken);
```

Column aliases must match CLR property names exactly. The example aliases folded PostgreSQL identifiers to the exact CLR names:

```cs
public sealed class Customer
{
    public int Id { get; set; }     // SELECT id AS "Id"
    public string? Name { get; set; } // SELECT name AS "Name"
}
```

For updates, verify both fields:

```cs
UpdateResult update = await queries.RunUpdateQueryAsync(sql, connectionString);
if (!update.Success || update.RecordsChanged != 1)
{
    // Handle failure or an unexpected affected-row count.
}
```

An empty `DataTable` is ambiguous and can represent either zero matches or a logged failure.

## Streaming and ownership

`GetDataStreamAsync<T>` owns its connection/command until async enumeration completes, is canceled, or is disposed. Fully enumerate or promptly dispose the enumerator. The `maxRetry` wrapper does not currently surround exceptions raised during `MoveNextAsync`, so runtime failures during enumeration may not be retried.

`GetDataStream<T>` currently creates a lazy enumerable and disposes its outer provider connection/command scope on return. Do not rely on deferred enumeration through this provider overload. Prefer the async method, or call `CommonNetFuncs.Sql.Common.DirectQuery.GetDataStream<T>` with a caller-owned `NpgsqlConnection`.

`GetDataDirectAsync<T>` materializes a list, closes its connection, and throws `DataException` after retries are exhausted. Cancellation and other execution errors are included in that retry path.

## Parameterized PostgreSQL SQL

Use Npgsql directly when values come from any untrusted source:

```cs
using Npgsql;

await using NpgsqlConnection connection = new(connectionString);
await connection.OpenAsync(cancellationToken);
await using NpgsqlCommand command = new(
    """SELECT id AS "Id", name AS "Name" FROM customers WHERE tenant_id = $1""",
    connection);
command.Parameters.Add(new NpgsqlParameter<int> { Value = tenantId });

await using NpgsqlDataReader reader =
    await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    Console.WriteLine($"{reader.GetInt32(0)} {reader.GetString(1)}");
}
```

## Transactions

The `DirectQuery` methods do not accept an existing transaction. Compose atomic operations on one provider connection:

```cs
await using NpgsqlConnection connection = new(connectionString);
await connection.OpenAsync(cancellationToken);
await using NpgsqlTransaction transaction =
    await connection.BeginTransactionAsync(cancellationToken);

try
{
    await using NpgsqlCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText =
        "UPDATE accounts SET balance = balance - $2 WHERE id = $1";
    command.Parameters.Add(new NpgsqlParameter<int> { Value = sourceId });
    command.Parameters.Add(new NpgsqlParameter<decimal> { Value = amount });
    await command.ExecuteNonQueryAsync(cancellationToken);
    await transaction.CommitAsync(cancellationToken);
}
catch
{
    await transaction.RollbackAsync(cancellationToken);
    throw;
}
```

## Diagnostic SQL rendering

`CommonNetFuncs.Sql.PostgreSql.Debug.RenderCommandAsScript(DbCommand)` is the package's additional public method. It produces a diagnostic script with parameter values inlined, including `@name`, `:name`, and `$N` forms:

```cs
using CommonNetFuncs.Sql.PostgreSql;
using Npgsql;
using NpgsqlTypes;

await using NpgsqlCommand command = new(
    "SELECT * FROM customers WHERE id = @id AND name = @name");
command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Integer)
{
    Value = 42
});
command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text)
{
    Value = "O'Brian"
});

string diagnosticScript = Debug.RenderCommandAsScript(command);
```

The script is **diagnostics only**. It contains real values and is not safe to execute as a production parameterization mechanism. It supports nulls, numerics, dates/times, GUIDs, byte arrays, and enumerable parameters, but restrict access to the resulting logs.

## Installation

```bash
dotnet add package CommonNetFuncs.Sql.PostgreSql
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
