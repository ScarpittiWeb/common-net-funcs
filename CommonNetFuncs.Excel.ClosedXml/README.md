# CommonNetFuncs.Excel.ClosedXml

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Excel.ClosedXml)](https://www.nuget.org/packages/CommonNetFuncs.Excel.ClosedXml/)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Excel.ClosedXml)](https://www.nuget.org/packages/CommonNetFuncs.Excel.ClosedXml/)

ClosedXML-based `.xlsx` helpers for export, reading, worksheet navigation,
tables, styles, and images. The package targets `net11.0` and
`netstandard2.1`; workbooks and saved `MemoryStream` output are in-memory, so
bound large or untrusted exports.

## Contents

- [Setup](#setup)
- [Export APIs](#export-apis)
  - [`GenericExcelExport`](#genericexcelexport)
  - [`AddGenericTable`](#addgenerictable)
  - [`ExcelExport`](#excelexport)
  - [`ExportFromTable`](#exportfromtable)
- [Cell, range, and named-range helpers](#cell-range-and-named-range-helpers)
- [Reading worksheets and tables](#reading-worksheets-and-tables)
- [Tables and measurements](#tables-and-measurements)
- [Images](#images)
- [Styles, borders, and fonts](#styles-borders-and-fonts)
- [Writing workbooks](#writing-workbooks)
- [Export behavior and errors](#export-behavior-and-errors)
- [Installation](#installation)
- [License](#license)

## Setup

```csharp
using System.Data;
using ClosedXML.Excel;
using CommonNetFuncs.Excel.ClosedXml;
using CommonNetFuncs.Excel.Common;

public sealed class SalesRow
{
    public string Customer { get; set; } = "";
    public int Quantity { get; set; }
    public decimal Total { get; set; }
    public string InternalId { get; set; } = "";
}

SalesRow[] rows =
[
    new() { Customer = "Contoso", Quantity = 2, Total = 25m, InternalId = "42" }
];

using var table = new DataTable();
table.Columns.Add("Customer");
table.Columns.Add("Quantity", typeof(int));
table.Columns.Add("InternalId");
table.Rows.Add("Contoso", 2, "42");
```

ClosedXML uses **1-based** row and column coordinates.

## Export APIs

All export APIs use public property names as headers for `IEnumerable<T>`.
Values are converted with `ToString()` and written as strings. `skipColumnNames`
is matched case-insensitively; omitted columns are compacted. Null items in a
typed sequence are skipped.

When `createTable` is `false`, a header autofilter and standard header/body
styles are used. When `true`, the range is converted to an Excel table with
striped rows, an autofilter, and the selected `ETableStyle`. Column widths are
adjusted to contents on a best-effort basis; a failure is logged.

### `GenericExcelExport`

Both overloads create a new `XLWorkbook`, add one worksheet, save it to a
`MemoryStream`, and rewind a successful output to position zero.

```csharp
// IEnumerable<T> overload.
await using MemoryStream? typedOutput = await rows.GenericExcelExport(
    memoryStream: null,
    createTable: true,
    sheetName: "Sales",
    tableName: "SalesTable",
    skipColumnNames: ["InternalId"],
    wrapText: true,
    tableStyle: ETableStyle.TableStyleMedium9,
    cancellationToken: cancellationToken);

if (typedOutput is null || typedOutput.Length == 0)
{
    throw new InvalidOperationException("Export did not produce a workbook.");
}

// DataTable overload.
await using MemoryStream? tableOutput = await table.GenericExcelExport(
    createTable: false,
    sheetName: "Data",
    tableName: "IgnoredUnlessTable",
    skipColumnNames: ["InternalId"],
    wrapText: false,
    cancellationToken: cancellationToken);
```

If `memoryStream` is supplied, it is used and remains open. Blank sheet/table
names become `"Data"`. A sheet name over 31 characters or a table name over 255
characters is logged and results in a new empty stream. Other ClosedXML naming
and Excel format rules still apply.

Cancellation is converted to `TaskCanceledException`. Other exceptions are
logged and converted to a new empty stream. A `null` result is possible when
the lower-level export reports failure, so validate both nullability and
`Length`.

### `AddGenericTable`

Both overloads add a new worksheet to an existing workbook and return `false`
when they fail:

```csharp
using var workbook = new XLWorkbook();

// IEnumerable<T> overload.
bool typedAdded = workbook.AddGenericTable(
    rows,
    sheetName: "Sales",
    createTable: true,
    tableName: "SalesTable",
    skipColumnNames: ["InternalId"],
    wrapText: true,
    tableStyle: ETableStyle.TableStyleLight9);

// DataTable overload.
bool tableAdded = workbook.AddGenericTable(
    table,
    sheetName: "SalesTableData",
    createTable: false,
    tableName: "UnusedTableName",
    skipColumnNames: ["InternalId"],
    wrapText: false,
    tableStyle: ETableStyle.TableStyleMedium1);
```

Blank names become `"Data"`. Duplicate sheet names are made unique with
`" (1)"`, `" (2)"`, and so on (comparison is case-insensitive). The method
still validates only the requested base name's 31-character limit.

### `ExcelExport`

Use these overloads to populate a caller-owned worksheet:

```csharp
using var workbook = new XLWorkbook();
IXLWorksheet typedSheet = workbook.AddWorksheet("Typed");
IXLWorksheet tableSheet = workbook.AddWorksheet("Table");

// IEnumerable<T> overload.
bool typedExported = rows.ExcelExport(
    workbook,
    typedSheet,
    createTable: true,
    tableName: "TypedSales",
    skipColumnNames: ["InternalId"],
    wrapText: true,
    tableStyle: ETableStyle.TableStyleDark2,
    cancellationToken: cancellationToken);

// DataTable overload.
bool tableExported = table.ExcelExport(
    workbook,
    tableSheet,
    createTable: false,
    tableName: "TableSales",
    skipColumnNames: ["InternalId"],
    wrapText: false,
    tableStyle: ETableStyle.TableStyleMedium1,
    cancellationToken: cancellationToken);
```

An empty typed sequence or a `DataTable` with no rows returns `true` without
adding headers. Other failures return `false`. Cancellation throws
`TaskCanceledException`. `tableName` is checked even when `createTable` is
`false`; it defaults to `"Data"` and may not exceed 255 characters.

### `ExportFromTable`

These static compatibility methods delegate to `ExcelExport`. Prefer
`ExcelExport` in new code.

```csharp
using var workbook = new XLWorkbook();
IXLWorksheet typedSheet = workbook.AddWorksheet("Typed");
IXLWorksheet tableSheet = workbook.AddWorksheet("Table");

// Generic compatibility overload.
bool typedOk = Export.ExportFromTable(
    workbook,
    typedSheet,
    rows,
    createTable: true,
    wrapText: true,
    cancellationToken: cancellationToken);

// DataTable compatibility overload.
bool tableOk = Export.ExportFromTable(
    workbook,
    tableSheet,
    table,
    createTable: false,
    wrapText: false,
    cancellationToken: cancellationToken);
```

The nullable data input returns `true` and leaves the worksheet unchanged.
Unlike `ExcelExport`, a cancellation caught by this wrapper returns `false`.
The compatibility signatures do not expose table name, skipped columns, or
table style.

## Cell, range, and named-range helpers

Every lookup below is demonstrated with its zero-based versus one-based
behavior made explicit:

```csharp
using var workbook = new XLWorkbook();
IXLWorksheet sheet = workbook.AddWorksheet("Lookup");

sheet.Cell(1, 1).Value = "Name";
sheet.Cell(2, 2).Value = "Ada";
workbook.DefinedNames.Add("PersonRange", "Lookup!$B$2:$B$2");

bool empty = sheet.Cell(4, 4).IsCellEmpty();
string? nullValue = ((IXLCell?)null).GetStringValue();
string? value = sheet.Cell(2, 2).GetStringValue();

// A1 notation or a range; the top-left cell is used.
IXLCell? byReference = sheet.GetCellFromReference("B2:D5");
IXLCell? byReferenceOffset =
    sheet.GetCellFromReference("B2", colOffset: 1, rowOffset: 1);

IXLCell? byCellOffset = sheet.Cell(2, 2).GetCellOffset(1, 0);

// ClosedXML coordinate overload: row and column indexes are 1-based.
IXLCell? byCoordinates =
    sheet.GetCellFromCoordinates(colIndex: 2, rowIndex: 2);
IXLCell? byCoordinatesOffset =
    sheet.GetCellFromCoordinates(2, 2, colOffset: 1, rowOffset: 1);

// Last row is 1-based. These are separate public overloads.
int byNumber = sheet.GetLastPopulatedRowInColumn(2);
int byName = sheet.GetLastPopulatedRowInColumn("B");

IXLCell? named = workbook.GetCellFromName("PersonRange");
IXLCell? namedOffset =
    workbook.GetCellFromName("PersonRange", colOffset: 1, rowOffset: 1);

sheet.Cell(3, 2).Value = "temporary";
workbook.DefinedNames.Add("ClearRange", "Lookup!$B$2:$B$3");
workbook.ClearAllFromName("ClearRange");

IXLRange? mergedArea = sheet.Cell(2, 2).GetRangeOfMergedCells();
IXLCell[,] cells = sheet.GetRange("A1:C4");
```

`IsCellEmpty` treats whitespace as empty. `GetStringValue` returns `null` for a
null cell and `""` for a blank cell. Invalid references/offsets in the lookup
helpers are logged and return `null`.

`GetCellFromName` chooses the top-leftmost cell across a possibly multi-area
defined name. `ClearAllFromName` clears every range assigned to the name and
logs rather than throwing for lookup failures. `GetRangeOfMergedCells` returns
the merged range, a one-cell range for an unmerged cell, or `null` for null.

Both validation overloads create an in-cell list with Stop-style error UI:

```csharp
List<string> choices = ["Open", "Closed"];

// Worksheet plus A1-string range overload.
sheet.AddDataValidation("C2:C20", choices);

// IXLRange overload.
IXLRange statusRange = sheet.Range("D2:D20");
statusRange.AddDataValidation(choices);
```

## Reading worksheets and tables

The reader methods reset the stream to position zero and return `DataTable`
values as strings. They catch and log errors and return an empty or partial
table rather than throwing ordinary read/parsing/cancellation exceptions.

```csharp
await using var workbookInput = new MemoryStream();
// Reuse the workbook from an export or load an existing .xlsx first.
using (var source = new XLWorkbook())
{
    IXLWorksheet sourceSheet = source.AddWorksheet("Sales");
    sourceSheet.Cell(1, 1).Value = "Customer";
    sourceSheet.Cell(2, 1).Value = "Contoso";
    source.SaveAs(workbookInput);
}

// Read a worksheet. Column names come from headers; otherwise Column0, ...
using DataTable worksheetData =
    workbookInput.ReadExcelFileToDataTable(
        hasHeaders: true,
        sheetName: "Sales",
        startCellReference: "A1",
        endCellReference: "B20",
        cancellationToken: cancellationToken);

// Read a named table. Unknown names fall back to the first table found.
using DataTable tableData =
    workbookInput.ReadExcelTableToDataTable(
        tableName: "SalesTable",
        cancellationToken: cancellationToken);
```

Without `sheetName`, the first worksheet is used. Without `startCellReference`,
reading starts at `A1`. Without `endCellReference`, columns run until the first
blank cell in the header/start row, and rows stop at the first fully blank row
or first row with no non-whitespace values in the selected columns. A specified
end cell gives an inclusive rectangular range.

`ReadExcelTableToDataTable` enumerates tables across worksheets. If no matching
table exists, it uses the first table found. It reads table headers and body
rows only.

## Tables and measurements

The two column conversion overloads use ClosedXML's **1-based** indexes:

```csharp
int oneBasedColumn = "AA".ColumnNameToNumber();       // 27
string fromNullable = ((int?)27).ColumnIndexToName(); // "AA"
string fromInt = 27.ColumnIndexToName();              // "AA"
```

`ColumnNameToNumber(null)` or an empty name throws `ArgumentException`.
Both `ColumnIndexToName` overloads throw for values below 1 (and the nullable
overload also throws for `null`).

```csharp
using var workbook = new XLWorkbook();
IXLWorksheet sheet = workbook.AddWorksheet("Measurements");
sheet.Column(1).Width = 12;
sheet.Column(2).Width = 18;
sheet.Row(1).Height = 20;

int widthPx = sheet.GetRangeWidthInPx(startCol: 1, endCol: 2);
int heightPx = sheet.GetRangeHeightInPx(startRow: 1, endRow: 1);

workbook.CreateTable(
    sheetName: "Measurements",
    tableName: "MeasurementsTable",
    firstColIndex: 1,
    lastColIndex: 3,
    firstRowIndex: 1,
    lastRowIndex: 4,
    columnNames: ["Name", "Value"],
    tableStyle: ETableStyle.TableStyleMedium2,
    showRowStripes: true,
    showColStripes: false);
```

Measurement endpoints are ordered internally when supplied backwards. Width is
an approximation using seven pixels per ClosedXML character unit at 96 DPI;
height converts points to pixels at 96 DPI. These values are not a precise
cross-platform font/layout measurement.

`CreateTable` writes the provided ordered header names and generates `Column1`,
`Column2`, ... for any missing names. It enables row/column stripe options and
an autofilter, and throws if the table name exceeds 255 characters. Excel and
ClosedXML impose additional validation on names and ranges.

## Images

All image helpers expect encoded image bytes that SkiaSharp can decode. PNG is
commonly used. They do not validate arbitrary content before decoding.

```csharp
byte[] logo = File.ReadAllBytes(@"C:\images\logo.png");
using var workbook = new XLWorkbook();
IXLWorksheet sheet = workbook.AddWorksheet("Images");
workbook.DefinedNames.Add("LogoSlot", "Images!$B$2:$D$6");
IXLRange directRange = sheet.Range("F2:H6");
IXLCell directCell = sheet.Cell(9, 2);

// Four AddImage overloads.
workbook.AddImage(
    logo, "LogoSlot",
    XLPicturePlacement.MoveAndSize);
workbook.AddImage(
    sheet, logo, "F2:H6",
    XLPicturePlacement.MoveAndSize);
workbook.AddImage(
    sheet, logo, directRange,
    XLPicturePlacement.MoveAndSize);
workbook.AddImage(
    sheet, logo, directCell,
    XLPicturePlacement.MoveAndSize);

// Two AddImages overloads.
workbook.AddImages(
    [logo],
    ["LogoSlot"],
    XLPicturePlacement.MoveAndSize);
workbook.AddImages(
    sheet,
    [logo],
    [sheet.Range("J2:L6")],
    XLPicturePlacement.MoveAndSize);

// Low-level single-picture helper.
workbook.AddPicture(
    sheet,
    sheet.Range("N2:P6"),
    logo,
    XLPicturePlacement.MoveAndSize);
```

The list overloads require non-empty, equal-length image and destination lists;
otherwise they do nothing. Empty image bytes and unresolved named ranges are
skipped. A string range overload resolves the top-left cell and its merged
area; an invalid range throws `ArgumentException`.

`AddPicture` preserves aspect ratio, centers the picture in the range, and
leaves a small margin. It uses the image-background style when no style is
supplied. Invalid image bytes can make SkiaSharp return zero/invalid dimensions
or throw, so validate untrusted images before calling these APIs.

## Styles, borders, and fonts

`ClosedXmlBorderStyles` is a mutable transfer object:

```csharp
using var workbook = new XLWorkbook();
IXLCell cell = workbook.AddWorksheet("Style").Cell(1, 1);

// Constructor from an existing style.
var copiedBorders = new ClosedXmlBorderStyles(cell.Style);

// Explicit constructor.
var requestedBorders = new ClosedXmlBorderStyles(
    borderTop: XLBorderStyleValues.Medium,
    borderLeft: XLBorderStyleValues.Thin,
    borderRight: XLBorderStyleValues.Thin,
    borderBottom: XLBorderStyleValues.Double,
    borderTopColor: XLColor.Black,
    borderLeftColor: XLColor.Gray,
    borderRightColor: XLColor.Gray,
    borderBottomColor: XLColor.Black);

// Public extraction/update method.
requestedBorders.ExtractBorderStyles(cell.Style);
```

It exposes `BorderTop`, `BorderLeft`, `BorderRight`, `BorderBottom` and the four
corresponding `...Color` properties. The style constructor tolerates a null
style and leaves all properties null.

```csharp
var borders = new ClosedXmlBorderStyles(
    borderTop: XLBorderStyleValues.Thick);

IXLStyle? standard = workbook.GetStandardCellStyle(
    EStyle.Header,
    cellLocked: true,
    wrapText: true,
    borderStyles: borders);

IXLFont customFont = workbook.GetFont(EFont.Header);
IXLStyle? custom = Common.GetStyle(
    EStyle.Custom,
    workbook,
    cellLocked: false,
    wrapText: true,
    htmlColor: "#E8F1FF",
    font: customFont,
    alignment: XLAlignmentHorizontalValues.Left);

// GetFont is a static helper.
IXLFont defaultFont = Common.GetFont(EFont.Default, workbook);
```

`GetStandardCellStyle` implements `Header`, `HeaderThickTop`, `Body`, `Error`,
`Blackout`, `Whiteout`, and `ImageBackground`; `Custom` produces only an empty
base style there. `GetStyle` supports the same presets and additionally uses
`htmlColor`, `font`, and `alignment` for `EStyle.Custom`. All return an
`IXLStyle?` because creation uses ClosedXML internals and may return null.

The default font presets use Calibri at 10 points; image-background uses Calibri
11. `cellLocked` and `wrapText` are applied after the selected preset.

## Writing workbooks

```csharp
using var workbook = new XLWorkbook();
workbook.AddWorksheet("Data").Cell(1, 1).Value = "Hello";

// Static path writer. It creates/truncates the file and disposes the workbook
// after a successful write.
using var fileWorkbook = new XLWorkbook();
fileWorkbook.AddWorksheet("Data").Cell(1, 1).Value = "Hello";
bool written = Common.WriteExcelFile(fileWorkbook, @"C:\exports\report.xlsx");

// MemoryStream extension. Successful output is flushed and rewound.
await using var output = new MemoryStream();
await output.WriteFileToMemoryStreamAsync(
    workbook,
    cancellationToken);
```

`WriteExcelFile` catches and logs failures and returns `false`. On success it
disposes the supplied workbook. `WriteFileToMemoryStreamAsync` saves through a
temporary stream with formula evaluation, package validation, and calculation
chain generation enabled, copies to the supplied stream, and rewinds it. It does
not clear an existing destination first; write into an empty stream or manage
the destination position deliberately.

## Export behavior and errors

- `GenericExcelExport` and lower-level export helpers catch ordinary failures
  and return failure indicators (`null`/`false`/empty stream). Check contents
  before sending a file.
- Cancellation is rethrown as `TaskCanceledException` by the main and
  lower-level methods, except the compatibility `ExportFromTable` wrappers,
  which return `false`.
- No formula-injection neutralization is performed. Treat untrusted values
  beginning with spreadsheet formula characters as text/validate them before
  export according to your threat model.
- Export values are strings; original `DateTime`, numeric, and boolean types are
  not preserved as native Excel cell types.
- `AdjustToContents` is best-effort and may depend on fonts/native libraries.
- This backend needs no Microsoft Office installation.

## Installation

```bash
dotnet add package CommonNetFuncs.Excel.ClosedXml
```

## License

MIT; see [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
