---
name: commonnetfuncs-csv
description: 'Use CommonNetFuncs.Csv to read CSV records or DataTables and export CSV. Consult for streaming, culture, stream ownership, error handling, and spreadsheet formula safety.'
---

# CommonNetFuncs.Csv

Use `CsvReadHelpers` to read typed records or a `DataTable` and
`CsvExportHelpers.ExportToCsv` to export. The
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Csv/README.md) shows the supported overloads.

## Workflow

1. Choose typed records or a `DataTable`, and choose lazy enumeration versus a
   materialized async list. Supply the intended `CultureInfo` and header
   setting rather than relying on ambient defaults.
2. Keep the input stream alive through enumeration. Handle file, parsing, and
   conversion exceptions at the point where records are actually consumed.
3. For export, inspect the output and rewind it before handing it to a reader.

```csharp
using CommonNetFuncs.Csv;

var records = CsvReadHelpers.ReadCsv<Person>(filePath);
foreach (Person record in records)
{
    // Process the record.
}
```

## Important constraints

- The stream-reading overloads wrap the supplied stream in a `StreamReader`
  that **closes it** when enumeration completes, is disposed, or fails. Do not
  pass a stream that must remain open without arranging separate ownership.
- Async list and `DataTable` methods materialize their results. Buffer size is
  an I/O setting, not a row, field, or total-input limit.
- Export leaves a supplied destination `MemoryStream` open but catches and logs
  exceptions and returns the stream, which can be empty or partial. Check the
  output and application logs; a returned stream alone does not prove success.
  The generic export rewinds it; the `DataTable` overload does not explicitly
  do so.
- The `DataTable` exporter only quotes cells containing commas; it does not
  escape embedded quotes or quote cells containing line breaks. For arbitrary
  text data, prefer the generic CsvHelper-backed overload or use a fully
  conforming CSV writer.
- CSV quoting is not a defense against spreadsheet formula injection. If
  exported values may be opened in spreadsheet software, apply a documented
  neutralization policy to untrusted cells beginning with formula characters
  such as `=`, `+`, `-`, or `@`, and test the target application.

See [CsvReadHelpers.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Csv/CsvReadHelpers.cs) and
[CsvExportHelpers.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Csv/CsvExportHelpers.cs).
