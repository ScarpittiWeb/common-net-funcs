---
name: commonnetfuncs-excel-closedxml
description: 'Use CommonNetFuncs.Excel.ClosedXml to export objects or DataTables to XLSX with ClosedXML. Consult for workbook options, stream ownership, memory, and failure handling.'
---

# CommonNetFuncs.Excel.ClosedXml

Choose this backend for a high-level, in-memory ClosedXML workbook. Import
`CommonNetFuncs.Excel.ClosedXml` and call `GenericExcelExport` on an
`IEnumerable<T>` or `DataTable`.

## Workflow

1. Select the properties and optional `skipColumnNames`, sheet/table names,
   table style, wrapping, and cancellation token. `createTable` defaults to
   `false`.
2. Await `GenericExcelExport`, check the nullable result and its contents, then
   return or save it as an XLSX file. The result is rewound on success.
3. Dispose the returned or supplied `MemoryStream` when finished. The
   convenience method owns its workbook but leaves the destination stream open.

```csharp
using CommonNetFuncs.Excel.ClosedXml;

using var workbook = await rows.GenericExcelExport(
    createTable: true, sheetName: "Report", cancellationToken: cancellationToken);
if (workbook is null || workbook.Length == 0)
{
    throw new InvalidOperationException("Excel export failed.");
}
```

## Important constraints

- The exporter can return `null` when export reports failure, or log an
  exception and return a new empty stream. Do not treat a non-null stream
  alone as proof of a valid workbook. Cancellation is rethrown.
- Sheet names longer than 31 characters and table names longer than 255 are
  rejected. Validate other Excel naming/format constraints as well.
- Workbook and output are memory-backed. Limit rows and input sizes for large
  or untrusted datasets; consider the Open XML SAX or NPOI streaming APIs when
  their tradeoffs fit better.

See [Export.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.ClosedXml/Export.cs) and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.ClosedXml/README.md).
