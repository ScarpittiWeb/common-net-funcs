# CommonNetFuncs.Excel.Npoi

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Excel.Npoi)](https://www.nuget.org/packages/CommonNetFuncs.Excel.Npoi/)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Excel.Npoi)](https://www.nuget.org/packages/CommonNetFuncs.Excel.Npoi/)

NPOI-based Excel helpers for export, reading, worksheet navigation, tables,
styles, and images, targeting `net11.0` and `netstandard2.1`. The APIs support
`.xlsx` and selected `.xls` workflows without Microsoft Office; SXSSF export
limits retained rows but still produces a complete in-memory `MemoryStream`.

## Contents

- [Setup and indexing](#setup-and-indexing)
- [Export APIs](#export-apis)
  - [`GenericExcelExport`](#genericexcelexport)
  - [`AddGenericTable`](#addgenerictable)
  - [`ExcelExport`](#excelexport)
- [Cell, range, and named-range helpers](#cell-range-and-named-range-helpers)
- [Reading workbooks and tables](#reading-workbooks-and-tables)
- [Format detection and column conversion](#format-detection-and-column-conversion)
- [Tables and measurements](#tables-and-measurements)
- [Images](#images)
- [Styles, borders, and font models](#styles-borders-and-font-models)
- [Writing workbooks](#writing-workbooks)
- [Export behavior and limits](#export-behavior-and-limits)
- [Installation](#installation)
- [License](#license)

## Setup and indexing

```csharp
using System.Data;
using CommonNetFuncs.Excel.Common;
using CommonNetFuncs.Excel.Npoi;
using NPOI.HSSF.Util;
using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.Streaming;
using NPOI.XSSF.UserModel;

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

NPOI's row and column indexes are **0-based**. `CellRangeAddress` and A1
conversion helpers preserve that convention. The returned `DataTable` column
names are unrelated to physical worksheet indexes.

## Export APIs

`IEnumerable<T>` exports use public property names as headers and convert every
value to a string. Null items are skipped. `skipColumnNames` is case-insensitive.

For typed exports with `createTable: false`, the header receives a standard
header style, data receives a body style, and an autofilter is added. With a
table, the XSSF table and style are created directly. The methods estimate
column widths and cap them at the public
`Export.MaxCellWidthInExcelUnits` constant (`65280`).

### `GenericExcelExport`

Both extensions return a `Task<MemoryStream?>` and own the temporary
`SXSSFWorkbook` they create:

```csharp
// IEnumerable<T> overload.
await using MemoryStream? typedOutput = await rows.GenericExcelExport(
    memoryStream: null,
    createTable: true,
    sheetName: "Sales",
    tableName: "SalesTable",
    skipColumnNames: ["InternalId"],
    wrapText: true,
    cancellationToken: cancellationToken);

if (typedOutput is null || typedOutput.Length == 0)
{
    throw new InvalidOperationException("Export did not produce a workbook.");
}

// DataTable overload.
await using MemoryStream? tableOutput = await table.GenericExcelExport(
    createTable: false,
    sheetName: "Data",
    tableName: "UnusedForPlainRange",
    skipColumnNames: ["InternalId"],
    wrapText: false,
    cancellationToken: cancellationToken);
```

Blank names become `"Data"`. This convenience path rejects sheet and table names
longer than 31 characters, even though the lower-level `CreateTable` helper
allows up to 255. NPOI/Excel naming restrictions besides length still apply.

A successful returned stream is rewound to position zero and remains open.
Cancellation becomes `TaskCanceledException`. Other exceptions are logged and
become a new empty stream. Lower-level export failure returns `null`.

### `AddGenericTable`

Four overloads add a new sheet to an existing workbook:

```csharp
// SXSSFWorkbook + IEnumerable<T>.
using var streamingWorkbook = new SXSSFWorkbook();
bool streamingTyped = streamingWorkbook.AddGenericTable(
    rows,
    "Typed",
    createTable: true,
    tableName: "TypedSales",
    skipColumnNames: ["InternalId"],
    wrapText: true);

// SXSSFWorkbook + DataTable.
bool streamingTable = streamingWorkbook.AddGenericTable(
    table,
    "Table",
    createTable: false,
    tableName: "TableSales",
    skipColumnNames: ["InternalId"],
    wrapText: false);

// XSSFWorkbook + DataTable.
using var normalWorkbook = new XSSFWorkbook();
bool normalTable = normalWorkbook.AddGenericTable(
    table,
    "XssfTable",
    createTable: true,
    tableName: "XssfSales",
    skipColumnNames: ["InternalId"],
    wrapText: true);

// XSSFWorkbook + IEnumerable<T>.
bool normalTyped = normalWorkbook.AddGenericTable(
    rows,
    "XssfTyped",
    createTable: true,
    tableName: "XssfTypedSales",
    skipColumnNames: ["InternalId"],
    wrapText: true);
```

Duplicate sheet names receive `" (1)"`, `" (2)"`, and so on. Blank names become
`"Data"`. Null data and invalid names/errors return `false`.

The `XSSFWorkbook` overloads wrap that workbook in a temporary
`SXSSFWorkbook` and dispose the wrapper when done; the caller retains ownership
of the underlying workbook. Table names in all these export overloads are
validated against the 31-character limit.

### `ExcelExport`

Populate an already-created `SXSSFWorkbook` sheet with either source shape:

```csharp
using var workbook = new SXSSFWorkbook();
ISheet typedSheet = workbook.CreateSheet("Typed");
ISheet tableSheet = workbook.CreateSheet("Table");

// IEnumerable<T> overload.
bool typedExported = rows.ExcelExport(
    workbook,
    typedSheet,
    createTable: true,
    tableName: "TypedSales",
    skipColumnNames: ["InternalId"],
    wrapText: true,
    cancellationToken: cancellationToken);

// DataTable overload.
bool tableExported = table.ExcelExport(
    workbook,
    tableSheet,
    createTable: false,
    tableName: "TableSales",
    skipColumnNames: ["InternalId"],
    wrapText: false,
    cancellationToken: cancellationToken);
```

Empty or null data returns `true` and leaves the sheet empty. Ordinary failures
return `false`; cancellation is converted to `TaskCanceledException`. The
table-name limit is 31 characters in these overloads.

For the current `DataTable` implementation, excluded columns retain their
original worksheet positions (leaving a gap). Table creation still spans the
original column count. Prefer the typed overload when a compacted set of
columns or a table over a filtered `DataTable` is required.

## Cell, range, and named-range helpers

NPOI indexes are 0-based:

```csharp
using var workbook = new XSSFWorkbook();
ISheet sheet = workbook.CreateSheet("Lookup");
IRow row = sheet.CreateRow(0);
ICell cell = row.CreateCell(1);
cell.SetCellValue("Ada");

bool empty = sheet.CreateRow(4).CreateCell(4).IsCellEmpty();
string? value = cell.GetStringValue();
string? nullValue = ((ICell?)null).GetStringValue();

ICell? byReference = sheet.GetCellFromReference("B2:D5");
ICell? byReferenceOffset =
    sheet.GetCellFromReference("B2", colOffset: 1, rowOffset: 1);

ICell? byCellOffset = cell.GetCellOffset(1, 0);

ICell? byCoordinates =
    sheet.GetCellFromCoordinates(colIndex: 1, rowIndex: 1);
ICell? byCoordinatesOffset =
    sheet.GetCellFromCoordinates(1, 1, colOffset: 1, rowOffset: 1);

// Last row overloads return 0-based indexes or -1 when not found.
int byNumber = sheet.GetLastPopulatedRowInColumn(1);
int byName = sheet.GetLastPopulatedRowInColumn("B");

IName namedRange = workbook.CreateName();
namedRange.NameName = "PersonRange";
namedRange.RefersToFormula = "Lookup!$B$1:$B$2";
ICell? named = workbook.GetCellFromName("PersonRange");
ICell? namedOffset =
    workbook.GetCellFromName("PersonRange", colOffset: 1, rowOffset: 1);

workbook.ClearAllFromName("PersonRange");

ICell explicitlyCreated = Common.CreateCell(row, 5);
sheet.AddMergedRegion(new CellRangeAddress(0, 1, 0, 1));
CellRangeAddress? mergedArea = sheet.GetRow(0).GetCell(0).GetRangeOfMergedCells();
ICell[,] cells = sheet.GetRange("A1:B2");
```

`IsCellEmpty` treats null, blank, and whitespace-only string values as empty.
`GetStringValue` handles numeric, string, boolean, blank, formula, and error
types; formula values use the cached result, and error values become `""`.

Reference/offset and named-range helpers catch exceptions and return `null`.
Reference helpers create missing rows/cells. `GetCellFromName` selects the
lowest row/column reference found in the name (and uses the first sheet that it
encounters). `ClearAllFromName` removes cells from every referenced address and
logs lookup failures.

`GetRange` expects a two-address colon range such as `A1:B2`; it does not create
missing rows. `GetRangeOfMergedCells` returns the matching merged area, a
one-cell range for an unmerged cell, or `null` for null.

```csharp
ISheet validationSheet = workbook.CreateSheet("Validation");
var addresses = new CellRangeAddressList(1, 20, 0, 0);
validationSheet.AddDataValidation(addresses, ["Open", "Closed"]);
```

The list-validation helper sets a Stop-style error titled `InvalidValue` and
does not show an input prompt.

## Reading workbooks and tables

Both methods reset a supplied stream to position zero and catch/log ordinary
read and cancellation failures, returning an empty or partial `DataTable`.

```csharp
await using var workbookInput = new MemoryStream();
workbook.Write(workbookInput, true);
workbookInput.Position = 0;

// Detects OOXML (.xlsx) versus OLE2 (.xls) and selects the first sheet when
// no sheetName is supplied.
using DataTable worksheetData =
    workbookInput.ReadExcelFileToDataTable(
        hasHeaders: true,
        sheetName: "Lookup",
        startCellReference: "A1",
        endCellReference: "B20",
        cancellationToken: cancellationToken);

// Reads only OOXML Excel tables. HSSFWorkbook input yields an empty table.
using DataTable tableData =
    workbookInput.ReadExcelTableToDataTable(
        tableName: "SalesTable",
        cancellationToken: cancellationToken);
```

For `ReadExcelFileToDataTable`, blank `startCellReference` means `A1`. Without
an explicit end, it reads columns until the first blank value and rows while
rows exist; a complete blank row can be represented as an empty row rather than
terminating when the physical row object exists, so prefer an explicit end for
sensitive imports. Without headers, columns are named `Column0`, `Column1`,
and so on. Values are converted with `GetStringValue`.

`ReadExcelTableToDataTable` searches for the requested table and, if it is not
found or omitted, selects the first table in worksheet order. It reads only the
table range, excluding the header row from data rows.

## Format detection and column conversion

Both `IsXlsx` overloads are public:

```csharp
await using var stream = new MemoryStream();
workbook.Write(stream, true);
stream.Position = 0;

bool streamIsXlsx = stream.IsXlsx();
bool workbookIsXlsx = ((IWorkbook)workbook).IsXlsx();
```

The stream overload resets position to zero and inspects NPOI's file magic.
Invalid input may throw. The workbook overload returns false only for an
`HSSFWorkbook` type.

```csharp
int zeroBased = "AA".ColumnNameToNumber();             // 26
string fromNullable = ((int?)26).ColumnIndexToName();   // "AA"
string fromInt = 26.ColumnIndexToName();                // "AA"
```

Names are upper-cased and are not restricted to Excel's finite 16,384-column
range. `ColumnNameToNumber(null)` or `""` throws `ArgumentException`.
`ColumnIndexToName` throws for null/negative values as applicable.

`GetClosestHssfColor` maps `#RRGGBB` to the closest NPOI legacy HSSF palette
color and caches the result:

```csharp
HSSFColor nearestRed =
    Common.GetClosestHssfColor("#FF1000", cachedColorLimit: 100);
```

Invalid hex syntax throws `ArgumentException`. The cache is global, static, and
case-sensitive by string key. `cachedColorLimit` is a retention hint, not a
thread-safety or cache-size contract for concurrent use.

## Tables and measurements

```csharp
using var workbook = new XSSFWorkbook();
workbook.CreateSheet("Measurements");

workbook.CreateTable(
    sheetName: "Measurements",
    tableName: "MeasurementsTable",
    firstColIndex: 0,
    lastColIndex: 2,
    firstRowIndex: 0,
    lastRowIndex: 3,
    columnNames: ["Name", "Value"],
    tableStyle: ETableStyle.TableStyleMedium2,
    showRowStripes: true,
    showColStripes: false);

ISheet sheet = workbook.GetSheet("Measurements");
int widthPx = sheet.GetRangeWidthInPx(startCol: 0, endCol: 2);
int heightPx = sheet.GetRangeHeightInPx(startRow: 0, endRow: 3);
```

`CreateTable` is available on `XSSFWorkbook`, with 0-based inclusive bounds. It
writes provided header names and uses `Column1`, `Column2`, ... for missing
names. It creates table metadata with an autofilter and the selected style.
The method validates names at 255 characters; normal NPOI/Excel table rules
still apply.

Width uses NPOI's column-pixel calculation. Height sums `HeightInPoints` and
converts points to pixels. Backwards bounds are swapped, and missing rows
contribute zero height.

## Images

The picture type passed to NPOI is PNG, regardless of the actual input bytes.
Pass valid PNG bytes to avoid ambiguous decoding/writing behavior.

```csharp
byte[] logo = File.ReadAllBytes(@"C:\images\logo.png");
using var workbook = new XSSFWorkbook();
ISheet sheet = workbook.CreateSheet("Images");
sheet.AddMergedRegion(new CellRangeAddress(1, 5, 1, 3));
IName named = workbook.CreateName();
named.NameName = "LogoSlot";
named.RefersToFormula = "Images!$B$2:$D$6";

var area = new CellRangeAddress(7, 11, 5, 7);
ICell cell = sheet.GetCellFromCoordinates(15, 1)!;

// Four AddImage overloads.
workbook.AddImage(
    logo, "LogoSlot",
    AnchorType.MoveAndResize);
workbook.AddImage(
    sheet, logo, "B2:D6",
    AnchorType.MoveAndResize);
workbook.AddImage(
    sheet, logo, area,
    AnchorType.MoveAndResize);
workbook.AddImage(
    sheet, logo, cell,
    AnchorType.MoveAndResize);

// Two AddImages overloads.
workbook.AddImages(
    [logo],
    ["LogoSlot"],
    AnchorType.MoveAndResize);
workbook.AddImages(
    sheet,
    [logo],
    [area],
    AnchorType.MoveAndResize);

// Low-level overload that accepts an existing drawing context.
IDrawing<IShape> drawing = sheet.CreateDrawingPatriarch();
ICreationHelper helper = workbook.GetCreationHelper();
workbook.AddPicture(
    sheet,
    new CellRangeAddress(13, 17, 1, 3),
    logo,
    drawing,
    AnchorType.MoveAndResize,
    helper);
```

The list overloads require equal, non-empty counts; otherwise they do nothing.
Empty image bytes and unresolved named ranges are skipped. The `AddPicture`
helper applies an image-background style, preserves aspect ratio, and builds an
anchor with centered pixel/EMU margins. It silently returns if its first target
cell cannot be obtained.

## Styles, borders, and font models

### `NpoiBorderStyles`

```csharp
using var workbook = new XSSFWorkbook();
ICellStyle native = workbook.CreateCellStyle();

// Constructor from a native style.
var copied = new NpoiBorderStyles(native);

// Explicit constructor.
var borders = new NpoiBorderStyles(
    borderTop: BorderStyle.Thick,
    borderLeft: BorderStyle.Thin,
    borderRight: BorderStyle.Thin,
    borderBottom: BorderStyle.Double,
    borderTopColor: HSSFColor.Black.Index,
    borderLeftColor: HSSFColor.Grey50Percent.Index,
    borderRightColor: HSSFColor.Grey50Percent.Index,
    borderBottomColor: HSSFColor.Black.Index);

// Public update method.
borders.ExtractBorderStyles(native);
```

`BorderTop`, `BorderLeft`, `BorderRight`, `BorderBottom`, `BorderTopColor`,
`BorderLeftColor`, `BorderRightColor`, and `BorderBottomColor` are public
get/set properties. Passing a null native style leaves them unset.

### Custom and standard styles

All three custom-style overloads are demonstrated:

```csharp
IFont font = workbook.GetFont(EFont.Header);

// No-color custom overload.
ICellStyle plainCustom = workbook.GetCustomStyle(
    cellLocked: false,
    font: font,
    alignment: HorizontalAlignment.Left,
    fillPattern: FillPattern.SolidForeground,
    borderStyles: borders,
    wrapText: true);

// Hex-color overload. XSSF accepts #RRGGBB.
ICellStyle hexCustom = workbook.GetCustomStyle(
    "#E8F1FF",
    cellLocked: true,
    font: font,
    alignment: HorizontalAlignment.Center,
    fillPattern: FillPattern.SolidForeground,
    borderStyles: borders,
    cachedColorLimit: 100,
    wrapText: true);

// HSSF/indexed-color overload.
ICellStyle indexedCustom = workbook.GetCustomStyle(
    hssfColor: HSSFColor.LightBlue.Index,
    cellLocked: false,
    font: font,
    alignment: HorizontalAlignment.Right,
    fillPattern: FillPattern.SolidForeground,
    borderStyles: borders,
    wrapText: false);

ICellStyle standard = workbook.GetStandardCellStyle(
    EStyle.Header,
    cellLocked: true,
    wrapText: true,
    borderStyles: borders);
```

Styles and fonts are cached with a `ConditionalWeakTable` per workbook and
therefore identical inputs can return the same native style/font object. Hex
colors for an XSSF workbook must be `#RRGGBB`; HSSF workbooks map the same value
to the closest indexed color. Border overrides passed to the private custom
builder are applied only when the workbook reports XSSF, while standard styles
apply their border/color settings through the shared style model.

`GetStandardCellStyle` implements `Header`, `HeaderThickTop`, `Body`, `Error`,
`Blackout`, `Whiteout`, and `ImageBackground`. `EStyle.Custom` is not a
standard-style mapping; call one of the custom-style overloads.

```csharp
// Public preset font method and all EFont inputs.
IFont defaultFont = workbook.GetFont(EFont.Default);
IFont headerFont = workbook.GetFont(EFont.Header);
IFont whiteoutFont = workbook.GetFont(EFont.Whiteout);
IFont imageFont = workbook.GetFont(EFont.ImageBackground);
```

Preset fonts use Calibri; default/whiteout/header use 10-point settings and
image-background uses 11 points.

### Public `CellStyle` and `CellFont` models

These are public `ICellStyle`/`IFont`-compatible value holders used by the style
cache implementation. They can also be constructed directly:

`CellStyle` exposes mutable `ShrinkToFit`, `DataFormat`, `FormatString`,
`IsHidden`, `IsLocked`, `IsQuotePrefixed`, `Alignment`, `WrapText`,
`VerticalAlignment`, `Rotation`, `Indention`, `BorderLeft`, `BorderRight`,
`BorderTop`, `BorderBottom`, `LeftBorderColor`, `RightBorderColor`,
`TopBorderColor`, `BottomBorderColor`, `FillPattern`,
`FillBackgroundColor`, `FillForegroundColor`, `BorderDiagonalColor`,
`BorderDiagonalLineStyle`, `BorderDiagonal`, `ReadingOrder`, `HexColor`, and
`HssfColor` properties. `Index`, `FontIndex`, `FillBackgroundColorColor`, and
`FillForegroundColorColor` are read-only in the class (`FontIndex` is changed
by `SetFont`). Its methods are `CloneStyleFrom`, `GetDataFormatString`,
`GetFont`, and `SetFont`.

`CellFont` exposes mutable `FontName`, `FontHeight`, `FontHeightInPoints`,
`IsItalic`, `IsStrikeout`, `Color`, `TypeOffset`, `Underline`, `Charset`, and
`IsBold`, plus read-only `Index`. Its methods are `CloneStyleFrom` and
`CopyProperties`.

```csharp
// CellStyle exposes all assigned ICellStyle properties plus HexColor and
// HssfColor. Index and color objects are read-only here.
var styleModel = new CellStyle
{
    Alignment = HorizontalAlignment.Center,
    WrapText = true,
    BorderTop = BorderStyle.Thin,
    FillPattern = FillPattern.SolidForeground,
    HexColor = "#FF0000"
};
styleModel.CloneStyleFrom(native);
styleModel.SetFont(font);
IFont modelFont = styleModel.GetFont(workbook);

// This method is explicitly not implemented by this class.
try
{
    _ = styleModel.GetDataFormatString();
}
catch (NotImplementedException)
{
}

var fontModel = new CellFont
{
    FontName = "Calibri",
    FontHeightInPoints = 11,
    IsBold = true
};
fontModel.CloneStyleFrom(font);

IFont destinationFont = workbook.CreateFont();
fontModel.CopyProperties(destinationFont);
```

`CloneStyleFrom` copies the source with the package's internal NPOI extension.
`CellStyle.SetFont` records the font's `Index`, and `GetFont` resolves that
index in the supplied workbook. `CellStyle.GetDataFormatString` always throws
`NotImplementedException`; do not use this public model with code requiring a
working data-format string. `CellFont.CopyProperties` copies either
`FontHeight` when nonzero or `FontHeightInPoints`.

## Writing workbooks

```csharp
// SXSSFWorkbook path overload. The method closes the workbook on success.
using var sxssf = new SXSSFWorkbook();
sxssf.CreateSheet("Data").CreateRow(0).CreateCell(0).SetCellValue("Hello");
bool sxssfWritten = sxssf.WriteExcelFile(@"C:\exports\report.xlsx");

// HSSFWorkbook path overload. It writes .xls and closes on success.
using var hssf = new NPOI.HSSF.UserModel.HSSFWorkbook();
hssf.CreateSheet("Data").CreateRow(0).CreateCell(0).SetCellValue("Hello");
bool hssfWritten = hssf.WriteExcelFile(@"C:\exports\report.xls");

// Any IWorkbook to MemoryStream. Successful output is rewound.
using var memoryWorkbook = new XSSFWorkbook();
memoryWorkbook.CreateSheet("Data");
await using var output = new MemoryStream();
await output.WriteFileToMemoryStreamAsync(
    memoryWorkbook,
    cancellationToken);
```

Both path overloads create/truncate the file, catch/log errors, and return
`false` on failure. A successful path write closes the supplied workbook, so do
not continue using it. `WriteFileToMemoryStreamAsync` writes through a temporary
stream with NPOI's `leaveOpen` behavior, copies to the supplied destination,
flushes, and rewinds it. It does not clear a non-empty destination first.

For an `SXSSFWorkbook`, dispose or close it after use even if you did not call
the path helper so its temporary resources are cleaned up.

## Export behavior and limits

- Export methods return strings, not native numeric/date/boolean Excel cells.
- A `null`/false/empty result is a failure signal; validate `Length` and logs.
- Cancellation from export/reading calls follows the behavior documented
  above; readers intentionally swallow it and return partial/empty data.
- `SXSSFWorkbook` reduces retained rows, not final stream memory, input object
  memory, styles, temporary disk usage, or output package size. Bound large and
  untrusted exports.
- Auto-width is an estimate; the public cap prevents widths above 65,280 units.
- No spreadsheet formula-injection neutralization is performed.
- A normal `MemoryStream` remains open after the helper returns unless the
  caller disposes it.

## Installation

```bash
dotnet add package CommonNetFuncs.Excel.Npoi
```

## License

MIT; see [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
