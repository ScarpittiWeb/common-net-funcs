# CommonNetFuncs.Sql.Common

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Sql.Common)](https://www.nuget.org/packages/CommonNetFuncs.Sql.Common/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Sql.Common)](https://www.nuget.org/packages/CommonNetFuncs.Sql.Common/)

Shared ADO.NET query helpers, the `IDirectQuery` provider contract, object-mapping cache, and string-cleaning utilities. Targets `net11.0` and `netstandard2.1`.

## Contents

- [CommonNetFuncs.Sql.Common](#commonnetfuncssqlcommon)
  - [Contents](#contents)
  - [Important behavior and safety](#important-behavior-and-safety)
  - [`IDirectQuery`](#idirectquery)
  - [`DirectQuery`](#directquery)
    - [Data tables](#data-tables)
      - [`GetDataTableAsync`](#getdatatableasync)
      - [`GetDataTable`](#getdatatable)
    - [Updates](#updates)
      - [`RunUpdateQueryAsync`](#runupdatequeryasync)
      - [`RunUpdateQuery`](#runupdatequery)
    - [Mapped streaming](#mapped-streaming)
      - [`GetDataStreamAsync<T>`](#getdatastreamasynct)
      - [`GetDataStream<T>`](#getdatastreamt)
    - [Mapped materialization](#mapped-materialization)
      - [`GetDataDirectAsync<T>`](#getdatadirectasynct)
    - [Mapping cache](#mapping-cache)
  - [`UpdateResult`](#updateresult)
  - [`QueryParameters`](#queryparameters)
    - [`CleanQueryParam(string?)`](#cleanqueryparamstring)
    - [`CleanQueryParam(IEnumerable<string>?)`](#cleanqueryparamienumerablestring)
    - [`CleanQueryParam(IList<string>?)`](#cleanqueryparamiliststring)
    - [`IsClean`](#isclean)
    - [`SanitizeSqlParameter`](#sanitizesqlparameter)
  - [Transactions](#transactions)
  - [Installation](#installation)
  - [License](#license)

For connection-string convenience, use a provider package:

- `CommonNetFuncs.Sql.Odbc`
- `CommonNetFuncs.Sql.PostgreSql`
- `CommonNetFuncs.Sql.SqlServer`

## Important behavior and safety

These methods accept a provider `DbConnection` and `DbCommand`; create parameters on the command. Neither the helper nor the string utilities provide SQL parameterization. Never interpolate untrusted values into `CommandText`, and allow-list dynamic table or column identifiers.

`CleanQueryParam`, `IsClean`, and `SanitizeSqlParameter` are transformations or limited checks, **not** SQL-injection protection. Their output can be changed or rejected, and a string that passes `IsClean` is not necessarily safe SQL.

The shared direct-query methods:

- open and close the supplied connection around each attempt;
- do **not** dispose the caller-owned connection or command;
- do not expose an `IDbTransaction` argument;
- log execution failures through NLog rather than returning a structured error with details;
- treat `maxRetry` as the total number of attempts, not an additional retry count. The default `3` means at most three attempts.

Retries can repeat a non-idempotent write. Prefer idempotent SQL, or use `maxRetry: 1`, when a failed attempt might have committed.

## `IDirectQuery`

`IDirectQuery` is implemented by each provider package and contains these operations:

```cs
IDirectQuery queries = configuredProviderDirectQuery;

using DataTable table = await queries.GetDataTableAsync(
    "SELECT * FROM Customers", connectionString, 30, 3, cancellationToken);

DataTable syncTable = queries.GetDataTable(
    "SELECT * FROM Customers", connectionString, 30, 3);

UpdateResult asyncUpdate = await queries.RunUpdateQueryAsync(
    "UPDATE Customers SET IsActive = 1 WHERE CustomerId = 42",
    connectionString, 30, 3, cancellationToken);

UpdateResult syncUpdate = queries.RunUpdateQuery(
    "UPDATE Customers SET IsActive = 1 WHERE CustomerId = 42",
    connectionString, 30, 3);

await foreach (Customer row in queries.GetDataStreamAsync<Customer>(
    "SELECT Id, Name FROM Customers", connectionString,
    commandTimeoutSeconds: 30, maxRetry: 3, useCache: true, cancellationToken))
{
    Console.WriteLine(row.Name);
}

foreach (Customer row in queries.GetDataStream<Customer>(
    "SELECT Id, Name FROM Customers", connectionString,
    commandTimeoutSeconds: 30, maxRetry: 3, useCache: true, cancellationToken))
{
    Console.WriteLine(row.Name);
}

IEnumerable<Customer> buffered = await queries.GetDataDirectAsync<Customer>(
    "SELECT Id, Name FROM Customers", connectionString,
    commandTimeoutSeconds: 30, maxRetry: 3, useCache: true, cancellationToken);
```

Provider wrappers create and own their provider connections and commands. See the provider README for parameterization and provider-specific limitations.

## `DirectQuery`

`CommonNetFuncs.Sql.Common.DirectQuery` works with any ADO.NET provider. The examples use SQL Server for concrete provider types:

```cs
using Microsoft.Data.SqlClient;

public sealed class Customer
{
    public int Id { get; set; }
    public string? Name { get; set; }
}
```

### Data tables

Both methods return an empty `DataTable` after all attempts fail; an empty table can therefore mean either no rows or a logged failure.

#### `GetDataTableAsync`

```cs
await using SqlConnection connection = new(connectionString);
await using SqlCommand command = new(
    "SELECT Id, Name FROM Customers WHERE IsActive = @active", connection);
command.Parameters.Add(new SqlParameter("@active", true));

using DataTable table = await DirectQuery.GetDataTableAsync(
    connection, command,
    commandTimeoutSeconds: 30,
    maxRetry: 3,
    cancellationToken);
```

#### `GetDataTable`

```cs
using SqlConnection connection = new(connectionString);
using SqlCommand command = new(
    "SELECT Id, Name FROM Customers WHERE IsActive = @active", connection);
command.Parameters.Add(new SqlParameter("@active", true));

using DataTable table = DirectQuery.GetDataTable(
    connection, command, commandTimeoutSeconds: 30, maxRetry: 3);
```

The methods close the connection in `finally`. They dispose their data readers, but do not dispose `connection` or `command`; the caller retains ownership.

### Updates

Inspect both `Success` and `RecordsChanged`. A successful command that affects zero rows has `Success == true` and `RecordsChanged == 0`.

#### `RunUpdateQueryAsync`

```cs
await using SqlConnection connection = new(connectionString);
await using SqlCommand command = new(
    "UPDATE Customers SET IsActive = @active WHERE CustomerId = @id", connection);
command.Parameters.Add(new SqlParameter("@active", false));
command.Parameters.Add(new SqlParameter("@id", 42));

UpdateResult result = await DirectQuery.RunUpdateQueryAsync(
    connection, command, 30, 3, cancellationToken);

if (!result.Success || result.RecordsChanged != 1)
{
    // Log or handle failure/zero-or-multiple-row behavior.
}
```

#### `RunUpdateQuery`

```cs
using SqlConnection connection = new(connectionString);
using SqlCommand command = new(
    "UPDATE Customers SET IsActive = @active WHERE CustomerId = @id", connection);
command.Parameters.Add(new SqlParameter("@active", false));
command.Parameters.Add(new SqlParameter("@id", 42));

UpdateResult result = DirectQuery.RunUpdateQuery(connection, command, 30, 3);
```

Execution exceptions are logged and retried. If all attempts fail, the default `UpdateResult` is returned (`Success == false`, `RecordsChanged == 0`).

Cancellation is caught by the current broad exception handling in these table/update methods, so a canceled async call can be retried and ultimately represented as an empty table or failed `UpdateResult` rather than necessarily throwing `OperationCanceledException`.

### Mapped streaming

Streaming maps one row at a time and does not materialize the complete result. Keep the enumeration alive until completion or dispose its enumerator; the connection remains open for the reader's lifetime.

`T` must be a reference type with a public parameterless constructor. Every public writable property is mapped from a column with the exact property name. A missing column, incompatible runtime type, or custom conversion can fail mapping with `DataException`; `DBNull` maps to the property's default value. Properties are not renamed or matched by attributes.

There is a special `Int32` conversion from the reader value; other values are directly converted by cast. This is a lightweight mapper, not a full ORM.

#### `GetDataStreamAsync<T>`

```cs
await using SqlConnection connection = new(connectionString);
await using SqlCommand command = new(
    "SELECT Id, Name FROM Customers ORDER BY Id", connection);

await foreach (Customer customer in DirectQuery.GetDataStreamAsync<Customer>(
    connection, command, 30, useCache: true, cancellationToken))
{
    await ProcessAsync(customer, cancellationToken);
}
```

The supplied connection is closed when enumeration completes, is disposed, or exits with an exception. The caller still owns and disposes the connection and command.

#### `GetDataStream<T>`

```cs
using SqlConnection connection = new(connectionString);
using SqlCommand command = new(
    "SELECT Id, Name FROM Customers ORDER BY Id", connection);

foreach (Customer customer in DirectQuery.GetDataStream<Customer>(
    connection, command, 30, useCache: true, cancellationToken))
{
    Process(customer);
}
```

This overload has no retry argument. Cancellation is checked between rows; the connection closes in `finally`.

### Mapped materialization

#### `GetDataDirectAsync<T>`

```cs
await using SqlConnection connection = new(connectionString);
await using SqlCommand command = new(
    "SELECT Id, Name FROM Customers WHERE IsActive = @active", connection);
command.Parameters.Add(new SqlParameter("@active", true));

IEnumerable<Customer> customers = await DirectQuery.GetDataDirectAsync<Customer>(
    connection, command,
    commandTimeoutSeconds: 30,
    maxRetry: 3,
    useCache: true,
    cancellationToken);
```

This method buffers all mapped rows in a list, then closes the connection. The final failed attempt is wrapped in `DataException`; unlike table and update methods, the exhausted-retry case throws. Mapping failures are also wrapped in `DataException`.

The internal result list is not cleared between retries. If an attempt reads some rows and later fails, a subsequent attempt can append the result again; use retries on this method only when that behavior is acceptable.

### Mapping cache

`CacheManager` exposes an `ICacheManagerApi<Type, Delegate>` for the compiled row mappers. It is public package functionality:

```cs
ICacheManagerApi<Type, Delegate> cache = DirectQuery.CacheManager;

bool limited = cache.IsUsingLimitedCache();
cache.SetLimitedCacheSize(256);
cache.SetUseLimitedCache(true);
cache.ClearAllCaches();
```

Passing `useCache: false` bypasses mapper lookup and creates a mapper for that operation. Changing cache mode is process-wide. The cache key is only `typeof(T)`, so mapping remains based on the model's writable properties; query shape must contain their columns.

## `UpdateResult`

`UpdateResult` is a sealed mutable result type. Its public constructor accepts the two values:

```cs
UpdateResult success = new(recordsChanged: 2, success: true);
int recordsChanged = success.RecordsChanged;
bool succeeded = success.Success;

success.RecordsChanged = 0;
success.Success = false;
```

The default constructor values are both zero/false.

## `QueryParameters`

Use these helpers only for normalization or validation requirements. Continue to use provider parameters for all values; do not concatenate their output into SQL.

### `CleanQueryParam(string?)`

The extension converts case-insensitive, trimmed `"null"` text according to `MakeNullNull` (including repeated `null` text recognized by that helper), removes `\n` characters, and trims leading/trailing whitespace. It does not remove `\r`, spaces inside the value, SQL tokens, or other line separators.

```cs
string? cleaned = "\n Test \n".CleanQueryParam(); // "Test"
string? noValue = "null".CleanQueryParam();         // null
```

### `CleanQueryParam(IEnumerable<string>?)`

This overload is lazy, returns an empty sequence for `null`, drops values that become `null`, and preserves the remaining cleaned values in source order:

```cs
IEnumerable<string>? source = new[] { " A\n", "null", " B " };
string[] cleaned = source.CleanQueryParam()!.ToArray(); // ["A", "B"]
```

### `CleanQueryParam(IList<string>?)`

The list overload returns a `List<string>`, returns `null` for a null input, and uses parallel processing; result order is therefore not guaranteed.

```cs
IList<string>? source = new List<string> { "A\n", "null", " B " };
List<string>? cleaned = source.CleanQueryParam(); // contains A and B
```

### `IsClean`

The check rejects `;`, `[`, `]`, `"`, backtick, `/*`, `*/`, `--`, and the exact lowercase token `xp_`. Matching is case-sensitive, so `XP_` is not rejected. `excludeSingleQuote: true` permits `'`; the default rejects it.

```cs
bool ordinary = "customer".IsClean();                 // true
bool comment = "customer--x".IsClean();                // false
bool permitQuote = "O'Brian".IsClean(excludeSingleQuote: true); // true
```

### `SanitizeSqlParameter`

The method rejects values according to the same `IsClean` rules (while always allowing single quotes for the check), optional length/alphanumeric constraints, and then doubles `'` and removes `%` and `*`. On rejection it returns `defaultValue`, which defaults to `string.Empty`.

When one or more character-mode flags are set, the value passes if any selected mode accepts it. The runtime check does not stop after the first flag merely because it was selected.

```cs
string? escaped = "O'Brian%".SanitizeSqlParameter();                 // "O''Brian"
string? alphaNumeric = "A12".SanitizeSqlParameter(onlyAlphanumeric: true);
string? alpha = "Name".SanitizeSqlParameter(onlyAlphaChars: true);
string? number = "123".SanitizeSqlParameter(onlyNumberChars: true);
string? bounded = "1234".SanitizeSqlParameter(maxLength: 4, minLength: 2);
string? rejected = "x;y".SanitizeSqlParameter(defaultValue: "INVALID");
string? nullableDefault = ((string?)null).SanitizeSqlParameter(defaultValue: null);
```

Do not rely on escaped output for query concatenation: pass the original typed value as a provider parameter instead.

## Transactions

No helper accepts an existing transaction. For atomic multi-command work, use the provider connection/transaction directly:

```cs
await using SqlConnection connection = new(connectionString);
await connection.OpenAsync(cancellationToken);
await using SqlTransaction transaction =
    (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

try
{
    await using SqlCommand first = connection.CreateCommand();
    first.Transaction = transaction;
    first.CommandText = "UPDATE Accounts SET Balance = Balance - 10 WHERE Id = @id";
    first.Parameters.Add(new SqlParameter("@id", 1));
    await first.ExecuteNonQueryAsync(cancellationToken);

    await using SqlCommand second = connection.CreateCommand();
    second.Transaction = transaction;
    second.CommandText = "UPDATE Accounts SET Balance = Balance + 10 WHERE Id = @id";
    second.Parameters.Add(new SqlParameter("@id", 2));
    await second.ExecuteNonQueryAsync(cancellationToken);

    await transaction.CommitAsync(cancellationToken);
}
catch
{
    await transaction.RollbackAsync(cancellationToken);
    throw;
}
```

## Installation

```bash
dotnet add package CommonNetFuncs.Sql.Common
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
