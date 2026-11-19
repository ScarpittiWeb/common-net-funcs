---
name: commonnetfuncs-excel-npoi
description: 'Use CommonNetFuncs.Excel.Npoi for XLSX export with NPOI and SXSSF. Consult for streaming workbooks, temporary resources, output memory, and failure handling.'
---

# CommonNetFuncs.Excel.Npoi

Choose this backend for NPOI workbook generation and SXSSF row-window
streaming. Import `CommonNetFuncs.Excel.Npoi` and call `GenericExcelExport` on
an `IEnumerable<T>` or `DataTable`.

## Workflow

1. Choose names, `createTable`, skipped columns, wrapping, and cancellation.
   `createTable` defaults to `false`.
2. Await the export, check for `null` and empty output, and dispose the returned
   or supplied `MemoryStream` after use. The convenience method leaves the
   output open and rewinds it on success.
3. When using `SXSSFWorkbook` directly with `AddGenericTable` or `ExcelExport`,
   close/dispose the workbook yourself, including its temporary resources.

```csharp
using CommonNetFuncs.Excel.Npoi;

using var workbook = await rows.GenericExcelExport(
    createTable: true, sheetName: "Report", cancellationToken: cancellationToken);
if (workbook is null || workbook.Length == 0)
{
    throw new InvalidOperationException("Excel export failed.");
}
```

## Important constraints

- A failed export can return `null`; other exceptions can be logged and
  converted to an empty stream. Check the result rather than treating a
  returned stream as guaranteed success. Cancellation is rethrown.
- The convenience path rejects sheet and table names over 31 characters. Excel
  format limits and NPOI naming rules still apply.
- SXSSF reduces retained worksheet rows but the convenience output is still a
  `MemoryStream`, and temporary files and input size consume resources. Bound
  large or untrusted exports; no Microsoft Office installation is required.

See [Export.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.Npoi/Export.cs) and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.Npoi/README.md).
