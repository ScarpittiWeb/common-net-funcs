---
name: commonnetfuncs-excel-openxml
description: 'Use CommonNetFuncs.Excel.OpenXml for XLSX export with Open XML and asynchronous SAX writing. Consult for stream ownership, failure signaling, and large exports.'
---

# CommonNetFuncs.Excel.OpenXml

Choose this backend for Open XML document control or asynchronous/SAX-style
export. `ExportSettings` configures table creation, table name, skipped columns,
and text wrapping; defaults do not create a table.

## Workflow

1. Import `CommonNetFuncs.Excel.OpenXml`. For large or asynchronous data,
   call `GenericExcelExportAsync` with an `IEnumerable<T>`,
   `IAsyncEnumerable<T>`, or `DataTable` and a caller-owned output stream.
2. Await completion, verify the resulting workbook, and rewind the output
   stream before reading if it is seekable. Dispose the output when finished.
3. For document/worksheet-part control, use `ExportFromTableSaxAsync` with a
   `SpreadsheetDocument` and `WorksheetPart`, and own their lifetimes.

```csharp
using CommonNetFuncs.Excel.OpenXml;

await rows.GenericExcelExportAsync(
    outputStream, sheetName: "Report", cancellationToken: cancellationToken);
if (outputStream.CanSeek)
{
    outputStream.Position = 0;
}
```

## Important constraints

- The convenience async export returns only `Task`: it logs and swallows
  ordinary exceptions and can leave an empty or partial output. A completed
  task alone is not proof of success. Verify the workbook and consult logs, or
  use a lower-level path when exception propagation is required. Cancellation
  is rethrown.
- SAX helps avoid holding every row in the workbook object model, but output,
  styles, input data, and Excel row/column limits still need resource planning.
- Validate sheet/table names and untrusted input. No Microsoft Office
  installation is needed.

See [Export.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.OpenXml/Export.cs) and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.OpenXml/README.md).
