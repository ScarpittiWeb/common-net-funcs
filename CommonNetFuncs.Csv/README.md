# CommonNetFuncs.Csv

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Csv)](https://www.nuget.org/packages/CommonNetFuncs.Csv/)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Csv)](https://www.nuget.org/packages/CommonNetFuncs.Csv/)

CsvHelper-backed APIs for reading CSV into typed records or `DataTable` objects
and exporting either source shape. The package targets `net11.0` and
`netstandard2.1`.

## Contents

- [CommonNetFuncs.Csv](#commonnetfuncscsv)
  - [Contents](#contents)
  - [Reading typed records](#reading-typed-records)
    - [`ReadCsv`](#readcsv)
    - [`ReadCsvAsync`](#readcsvasync)
    - [`ReadCsvAsyncEnumerable`](#readcsvasyncenumerable)
  - [Reading into a DataTable](#reading-into-a-datatable)
  - [Writing CSV](#writing-csv)
    - [Typed generic sequences](#typed-generic-sequences)
    - [`DataTable`](#datatable)
  - [Behavior and limitations](#behavior-and-limitations)
  - [Installation](#installation)
  - [License](#license)

All examples below assume:

```csharp
using System.Data;
using System.Globalization;
using System.Text;
using CommonNetFuncs.Csv;

public sealed class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
    public decimal Balance { get; set; }
}
```

CsvHelper's mapping features still apply. For data without headers, positional
attributes/mapping or a matching member order are needed. Choose a culture
explicitly when parsing culture-sensitive values rather than depending on the
process culture.

## Reading typed records

Every read overload accepts:

- `hasHeaders` (default `true`);
- `cultureInfo` (default `CultureInfo.InvariantCulture`);
- `bufferSize` (default `4096`).

`bufferSize` is passed to both the `StreamReader` where applicable and
CsvHelper's parser. It is an I/O/parser buffer size, not a record, field, or
total-input limit.

Input is read as the returned sequence is enumerated. File, access, parser,
mapping, and conversion failures are therefore generally observed during
enumeration, not when `ReadCsv` is called. Stream overloads construct a
`StreamReader` over the supplied stream with UTF-8 and BOM detection. That
reader closes the stream as enumeration completes, is disposed, or fails.

### `ReadCsv`

Both lazy synchronous overloads are public:

```csharp
// File-path overload.
IEnumerable<Person> fromFile =
    CsvReadHelpers.ReadCsv<Person>(
        @"C:\data\people.csv",
        hasHeaders: true,
        cultureInfo: CultureInfo.GetCultureInfo("en-US"),
        bufferSize: 8192);

foreach (Person person in fromFile)
{
    Console.WriteLine($"{person.Name}: {person.Balance}");
}

// Stream overload. Do not reuse input after enumeration: the reader closes it.
byte[] bytes = File.ReadAllBytes(@"C:\data\people.csv");
var input = new MemoryStream(bytes);
IEnumerable<Person> fromStream =
    CsvReadHelpers.ReadCsv<Person>(
        input,
        hasHeaders: true,
        cultureInfo: CultureInfo.InvariantCulture,
        bufferSize: 4096);
List<Person> people = fromStream.ToList();
```

`ReadCsv` owns the stream it opens for a file path. The stream overload owns
the supplied stream for the lifetime of enumeration. Keep that stream alive
until enumeration has ended, and either finish enumerating or dispose the
enumerator.

### `ReadCsvAsync`

These overloads materialize and return a `List<T>`.

```csharp
// File-path overload.
List<Person> fromFile = await CsvReadHelpers.ReadCsvAsync<Person>(
    @"C:\data\people.csv",
    hasHeaders: true,
    cultureInfo: CultureInfo.GetCultureInfo("en-US"),
    bufferSize: 8192,
    cancellationToken);

// Stream overload; the supplied stream is closed by the inner StreamReader.
byte[] bytes = File.ReadAllBytes(@"C:\data\people.csv");
var input = new MemoryStream(bytes);
List<Person> fromStream = await CsvReadHelpers.ReadCsvAsync<Person>(
    input,
    hasHeaders: true,
    cultureInfo: CultureInfo.InvariantCulture,
    bufferSize: 4096,
    cancellationToken);
```

Cancellation is passed to CsvHelper's asynchronous `ReadAsync` calls. An
already-cancelled token or cancellation while reading propagates cancellation.

### `ReadCsvAsyncEnumerable`

These overloads stream records through `IAsyncEnumerable<T>`:

```csharp
// File-path overload.
await foreach (Person person in
    CsvReadHelpers.ReadCsvAsyncEnumerable<Person>(
        @"C:\data\people.csv",
        hasHeaders: true,
        cultureInfo: CultureInfo.InvariantCulture,
        bufferSize: 16384,
        cancellationToken))
{
    Console.WriteLine(person.Name);
}

// Stream overload. The stream remains in use until the async enumeration ends.
var input = new MemoryStream(
    Encoding.UTF8.GetBytes("Name,Age,Balance\nAda,37,12.50\n"));
await foreach (Person person in
    CsvReadHelpers.ReadCsvAsyncEnumerable<Person>(
        input,
        hasHeaders: true,
        cultureInfo: CultureInfo.InvariantCulture,
        cancellationToken: cancellationToken))
{
    Console.WriteLine(person.Age);
}
```

Cancellation can be supplied directly or through `WithCancellation` on the
async sequence. Disposing the async enumerator closes the stream reader.

## Reading into a DataTable

The four synchronous overloads materialize the complete CSV:

```csharp
// File path, columns determined by CsvDataReader.
using DataTable inferredFile =
    CsvReadHelpers.ReadCsvToDataTable(
        @"C:\data\people.csv",
        hasHeaders: true,
        cultureInfo: CultureInfo.InvariantCulture,
        bufferSize: 8192);

// Stream, columns determined by CsvDataReader; closes the supplied stream.
var inferredInput = new MemoryStream(
    Encoding.UTF8.GetBytes("Name,Age\nAda,37\n"));
using DataTable inferredStream =
    CsvReadHelpers.ReadCsvToDataTable(
        inferredInput,
        hasHeaders: true,
        cultureInfo: CultureInfo.InvariantCulture,
        bufferSize: 4096);

// File path with explicitly typed columns copied from Person's public
// properties, in reflection order.
using DataTable typedFile =
    CsvReadHelpers.ReadCsvToDataTable(
        @"C:\data\people.csv",
        typeof(Person),
        hasHeaders: true,
        cultureInfo: CultureInfo.GetCultureInfo("en-US"),
        bufferSize: 8192);

// Stream with explicitly typed columns; closes the supplied stream.
var typedInput = new MemoryStream(
    Encoding.UTF8.GetBytes("Name,Age,Balance\nAda,37,12.50\n"));
using DataTable typedStream =
    CsvReadHelpers.ReadCsvToDataTable(
        typedInput,
        typeof(Person),
        hasHeaders: true,
        cultureInfo: CultureInfo.InvariantCulture,
        bufferSize: 4096);
```

When `Type` is provided, columns are pre-created with each reflected public
property's name and property type. Without `Type`, CsvHelper/CsvDataReader
defines the columns and values. These methods do not add a cancellation
parameter. Parsing and conversion errors are not caught by the package.

## Writing CSV

Both export overloads are async extensions. A supplied `MemoryStream` is used
as-is and is left open; if none is supplied, a new one is returned. Dispose the
returned stream when finished.

### Typed generic sequences

```csharp
Person[] people =
[
    new() { Name = "Ada, Jr.", Age = 37, Balance = 12.50m },
    new() { Name = "Linus", Age = 28, Balance = 8.25m }
];

// New destination overload (the optional argument is omitted).
await using MemoryStream newOutput =
    await people.ExportToCsv(
        cancellationToken: cancellationToken);
Console.WriteLine(Encoding.UTF8.GetString(newOutput.ToArray()));

// Existing destination overload. The stream is left open and rewound to zero.
await using var existingOutput = new MemoryStream();
MemoryStream returned =
    await people.ExportToCsv(
        existingOutput,
        cancellationToken);
ReferenceEquals(returned, existingOutput); // true
```

CsvHelper supplies the headers, culture (`InvariantCulture`), quoting, embedded
quote escaping, and newline handling. Output is UTF-8 without a BOM. The method
writes at the destination's current position and rewinds the returned stream
to zero after writing.

### `DataTable`

```csharp
using var table = new DataTable();
table.Columns.Add("Name");
table.Columns.Add("Note");
table.Rows.Add("Ada", "First, preferred");

// New destination overload (the optional argument is omitted).
await using MemoryStream newOutput =
    await table.ExportToCsv(
        cancellationToken: cancellationToken);

// Existing destination overload.
await using var existingOutput = new MemoryStream();
MemoryStream returned =
    await table.ExportToCsv(
        existingOutput,
        cancellationToken);
ReferenceEquals(returned, existingOutput); // true
```

The `DataTable` implementation writes headers and uses `ToString()` for
non-`DBNull` values; `DBNull` becomes an empty field. It quotes a field when the
value contains a comma, but it does **not** escape embedded double quotes and
does not quote fields merely because they contain line breaks. Header values
are not quoted by this implementation.

The generated CSV is written through a temporary stream into the destination.
Unlike the generic overload, this overload does not explicitly rewind the
destination after copying. On a new stream, the position is therefore normally
at the end; call `Position = 0` before reading.

## Behavior and limitations

- Both export overloads catch and log ordinary exceptions, including cancellation
  exceptions, and then return the destination stream. A returned stream can be
  empty or partial and is not by itself proof of success. Check its contents and
  application logs.
- Both exporters leave a supplied destination stream open. The generic overload
  rewinds it; the `DataTable` overload does not explicitly rewind it.
- For arbitrary CSV text (commas, quotes, and line breaks), prefer the typed
  CsvHelper-backed overload. The `DataTable` exporter is only a minimal writer.
- CSV quoting is not spreadsheet formula-injection protection. Neutralize or
  validate untrusted fields beginning with formula characters such as `=`, `+`,
  `-`, or `@` before users open the result in spreadsheet software.
- The asynchronous list methods materialize every record; use an async sequence
  overload for bounded, incremental processing.

## Installation

```bash
dotnet add package CommonNetFuncs.Csv
```

## License

MIT; see [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
