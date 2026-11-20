# CommonNetFuncs.Excel.OpenXml

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Excel.OpenXml)](https://www.nuget.org/packages/CommonNetFuncs.Excel.OpenXml)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Excel.OpenXml)](https://www.nuget.org/packages/CommonNetFuncs.Excel.OpenXml/)

Low-level Open XML Spreadsheet helpers and synchronous/streaming exporters for
creating, reading, styling, and modifying `.xlsx` workbooks. The package targets
`net11.0` and `netstandard2.1`. Microsoft Excel is not required.

## Contents

- [CommonNetFuncs.Excel.OpenXml](#commonnetfuncsexcelopenxml)
  - [Contents](#contents)
  - [Installation](#installation)
  - [Export settings and exporter behavior](#export-settings-and-exporter-behavior)
    - [Every exporter overload](#every-exporter-overload)
  - [Workbook, sheet, and lookup helpers](#workbook-sheet-and-lookup-helpers)
  - [Styles and stylesheet helpers](#styles-and-stylesheet-helpers)
  - [Cell insertion, formulas, shared strings, and tables](#cell-insertion-formulas-shared-strings-and-tables)
  - [Image insertion and drawing helpers](#image-insertion-and-drawing-helpers)
  - [Reading ranges and values](#reading-ranges-and-values)
  - [Column sizing and width calculation](#column-sizing-and-width-calculation)
  - [Every typed cell-setter overload](#every-typed-cell-setter-overload)
  - [Formula recalculation, validation, tables, and finalization](#formula-recalculation-validation-tables-and-finalization)
  - [`CellReference`, attributes, and enumerable mapping](#cellreference-attributes-and-enumerable-mapping)
  - [Correctness and resource notes](#correctness-and-resource-notes)
  - [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Excel.OpenXml
```

The package uses `DocumentFormat.OpenXml` and the companion
`CommonNetFuncs.Excel.Common` types. Keep streams caller-owned unless a method
explicitly creates one. Respect Excel limits (1,048,576 rows and 16,384
columns), validate sheet/table names and untrusted data, and plan memory and
output size for large workbooks. SAX output avoids retaining worksheet rows in
the DOM, but the encoded workbook still has to be stored by the supplied stream
and shared objects/styles/input data still consume resources.

## Export settings and exporter behavior

`ExportSettings` is a public class with a primary constructor:

```csharp
var settings = new ExportSettings(
    createTable: true,
    tableName: "SalesData",
    skipColumnNames: ["InternalId"],
    wrapText: true);

settings.CreateTable = true;
settings.TableName = "SalesData";
settings.SkipColumnNames = ["InternalId"];
settings.WrapText = true;
```

Defaults are `createTable: false`, `tableName: "Data"`, no skipped columns, and
`wrapText: false`. Skip names are compared case-insensitively. Headers come from
public property names for object rows and column names for `DataTable`.

Synchronous DOM exporters create a worksheet DOM and use shared strings.
`GenericExcelExport` returns a rewound `MemoryStream`, caller-provided or newly
created, and catches ordinary exceptions. A caught exception can produce a new
empty stream, while a lower-level export failure can produce `null`; inspect the
returned stream and workbook rather than treating completion as proof.

The async exporters use Open XML SAX writing. They return `Task`, swallow and
log ordinary exceptions, and may leave an empty or partial output. Cancellation
is rethrown as cancellation. They do not rewind the caller-owned stream and do
not dispose it. Rewind a seekable stream before reading it. Lower-level
`ExportFromTableSaxAsync` methods write the selected `WorksheetPart`; the caller
must register it in the workbook. Data written by SAX is inline-string data and
column widths are estimated from headers only.

Cancellation behavior is not fully uniform: the generic synchronous
`ExportFromTable<T>` translates cancellation to `TaskCanceledException`, while
the `DataTable` DOM overload catches it with ordinary exceptions and returns
`false`. The SAX methods rethrow `OperationCanceledException`.

If `CreateTable` is false, an auto-filter is added instead. If it is true, an
Excel table is created. `AddGenericTable` disambiguates an existing sheet name by
adding ` (1)`, ` (2)`, and so on.

### Every exporter overload

```csharp
using System.Data;
using System.Runtime.CompilerServices;
using CommonNetFuncs.Excel.OpenXml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

var rows = new[]
{
    new Sale { Name = "A", Amount = 12.50m, InternalId = 7 },
    new Sale { Name = "B", Amount = 8.25m, InternalId = 8 }
};
var options = new ExportSettings(
    createTable: true, tableName: "Sales", skipColumnNames: ["InternalId"]);

using DataTable table = new();
table.Columns.Add("Name", typeof(string));
table.Columns.Add("Amount", typeof(decimal));
table.Rows.Add("A", 12.50m);

// GenericExcelExport<T>(IEnumerable<T>, ...)
using MemoryStream? objectStream =
    rows.GenericExcelExport(sheetName: "Objects", exportSettings: options);

// GenericExcelExport(DataTable, ...)
using MemoryStream supplied = new();
using MemoryStream? tableStream =
    table.GenericExcelExport(supplied, "TableRows", options);

// AddGenericTable<T>(SpreadsheetDocument, IEnumerable<T>, ...)
using var addStream = new MemoryStream();
using var addDocument = SpreadsheetDocument.Create(
    addStream, SpreadsheetDocumentType.Workbook, true);
addDocument.InitializeExcelFile("Summary");
bool objectsAdded =
    addDocument.AddGenericTable(rows, "Objects", options);
addDocument.Save();

// AddGenericTable(SpreadsheetDocument, DataTable, ...)
using var addTableStream = new MemoryStream();
using var addTableDocument = SpreadsheetDocument.Create(
    addTableStream, SpreadsheetDocumentType.Workbook, true);
addTableDocument.InitializeExcelFile("Summary");
bool tableAdded =
    addTableDocument.AddGenericTable(table, "Rows", options);
addTableDocument.Save();

// DOM ExportFromTable<T>(document, worksheet, IEnumerable<T>, ...)
using var domStream = new MemoryStream();
using var domDocument = SpreadsheetDocument.Create(
    domStream, SpreadsheetDocumentType.Workbook, true);
uint domSheetId = domDocument.InitializeExcelFile("Objects");
Worksheet domSheet = domDocument.GetWorksheetById(domSheetId)!;
using var domCts = new CancellationTokenSource();
bool objectsExported = Export.ExportFromTable(
    domDocument, domSheet, rows, options, domCts.Token);

// DOM ExportFromTable(document, worksheet, DataTable, ...)
bool tableExported = Export.ExportFromTable(
    domDocument, domSheet, table, options, domCts.Token);
// The second call is shown for overload coverage; normally populate one input
// on a fresh worksheet.

// GenericExcelExportAsync<T>(IEnumerable<T>, Stream, ...)
using var asyncObjects = new MemoryStream();
await rows.GenericExcelExportAsync(
    asyncObjects, "AsyncObjects", options, CancellationToken.None);
asyncObjects.Position = 0;

// GenericExcelExportAsync<T>(IAsyncEnumerable<T>, Stream, ...)
using var asyncSequenceOutput = new MemoryStream();
await AsAsync(rows).GenericExcelExportAsync(
    asyncSequenceOutput, "AsyncSequence", options, CancellationToken.None);
asyncSequenceOutput.Position = 0;

// GenericExcelExportAsync(DataTable, Stream, ...)
using var asyncTableOutput = new MemoryStream();
await table.GenericExcelExportAsync(
    asyncTableOutput, "AsyncTable", options, CancellationToken.None);
asyncTableOutput.Position = 0;

// Low-level SAX overload for IEnumerable<T>.
using var saxStream1 = new MemoryStream();
using var saxDocument1 = SpreadsheetDocument.Create(
    saxStream1, SpreadsheetDocumentType.Workbook, true);
WorkbookPart saxBook1 = saxDocument1.InitializeExcelFile();
WorksheetPart saxPart1 = saxBook1.AddNewPart<WorksheetPart>();
await Export.ExportFromTableSaxAsync(
    saxDocument1, saxPart1, rows, options, CancellationToken.None);
// Register saxPart1 in saxBook1.Workbook.Sheets before saving.

// Low-level SAX overload for IAsyncEnumerable<T>.
using var saxStream2 = new MemoryStream();
using var saxDocument2 = SpreadsheetDocument.Create(
    saxStream2, SpreadsheetDocumentType.Workbook, true);
WorkbookPart saxBook2 = saxDocument2.InitializeExcelFile();
WorksheetPart saxPart2 = saxBook2.AddNewPart<WorksheetPart>();
await Export.ExportFromTableSaxAsync(
    saxDocument2, saxPart2, AsAsync(rows), options, CancellationToken.None);

// Low-level SAX overload for DataTable.
using var saxStream3 = new MemoryStream();
using var saxDocument3 = SpreadsheetDocument.Create(
    saxStream3, SpreadsheetDocumentType.Workbook, true);
WorkbookPart saxBook3 = saxDocument3.InitializeExcelFile();
WorksheetPart saxPart3 = saxBook3.AddNewPart<WorksheetPart>();
await Export.ExportFromTableSaxAsync(
    saxDocument3, saxPart3, table, options, CancellationToken.None);

static async IAsyncEnumerable<T> AsAsync<T>(
    IEnumerable<T> source,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    foreach (T item in source)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return item;
        await Task.Yield();
    }
}

public sealed class Sale
{
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public int InternalId { get; set; }
}
```

## Workbook, sheet, and lookup helpers

`InitializeExcelFile(document)` creates/reuses `WorkbookPart`, `Workbook`, and
`Sheets` but creates no sheet. The sheet-name overload creates the first sheet.
`CreateNewSheet` creates a sheet named `sheetName`, or `SheetN` by default.
Cell/range lookup methods generally create missing rows/cells for writing and
catch/log malformed-reference errors where noted, returning `null`. Range and
name callers should still validate the resulting cell.

```csharp
using CommonNetFuncs.Excel.OpenXml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

using var stream = new MemoryStream();
using var document = SpreadsheetDocument.Create(
    stream, SpreadsheetDocumentType.Workbook, true);

WorkbookPart bookPart = document.InitializeExcelFile();
uint firstId = document.InitializeExcelFile("First");
uint secondId = document.CreateNewSheet(); // "Sheet2"

Worksheet? byName = document.GetWorksheetByName(
    "First", createIfMissing: true);
Worksheet? byId = document.GetWorksheetById(firstId);
Worksheet sheet = byName!;
Worksheet fromCell = sheet
    .GetFirstChild<SheetData>()!
    .InsertCell(1, 1)!
    .GetWorksheetFromCell();
Workbook? bookFromCell =
    fromCell.GetFirstChild<SheetData>()!.GetFirstChild<Row>()!
        .GetFirstChild<Cell>()!.GetWorkbookFromCell();
Workbook? bookFromWorksheet = sheet.GetWorkbookFromWorksheet();
WorksheetPart sheetPart =
    bookPart.GetPartById(bookPart.Workbook!.Sheets!
        .Elements<Sheet>().First().Id!) as WorksheetPart
    ?? throw new InvalidOperationException();
Workbook? bookFromPart = sheetPart.GetWorkbookFromWorksheet();

Sheet? namedSheet = document.GetSheetByName("First");
SheetData sheetData = document.GetSheetDataFromDocument("First");
bool empty = sheetData.GetFirstChild<Row>()?.GetFirstChild<Cell>().IsCellEmpty()
    ?? true;

var a1 = new CommonNetFuncs.Excel.OpenXml.Common.CellReference("A1");
Cell? a1Object = sheet.GetCellFromReference(a1, colOffset: 0, rowOffset: 0);
Cell? b2String = sheet.GetCellFromReference("B2", colOffset: 0, rowOffset: 0);
Cell? c3 = a1Object!.GetCellOffset(colOffset: 2, rowOffset: 2);
Cell? d4 = sheet.GetCellFromCoordinates(4, 4);

// Named ranges used below must exist in Workbook.DefinedNames.
Cell? namedOnDocument = document.GetCellFromName("ExampleCell");
Cell? namedOnPart = bookPart.GetCellFromName("ExampleCell");
CommonNetFuncs.Excel.OpenXml.Common.CellReference? namedRefDocument =
    document.GetCellReferenceFromName("ExampleCell");
CommonNetFuncs.Excel.OpenXml.Common.CellReference? namedRefPart =
    bookPart.GetCellReferenceFromName("ExampleCell");
WorksheetPart? namedCellPart =
    Common.GetWorksheetPartByCellReference(bookPart, a1);
```

Use the fully qualified nested
`CommonNetFuncs.Excel.OpenXml.Common.CellReference` shown in calls, or alias it:

```csharp
using CellReference =
    CommonNetFuncs.Excel.OpenXml.Common.CellReference;
```

## Styles and stylesheet helpers

Standard style caches are keyed per `SpreadsheetDocument` through a
`ConditionalWeakTable` and are normally reclaimed with the document. The public
clear operations either clear format/element caches for one document or replace
the global cache table. Clearing the custom cache is useful with a reusable
template.

For a standard style and `cellLocked` combination, the current format-cache key
does not distinguish `wrapText`. Use a consistent `wrapText` choice for that
combination within a document, or clear the document's standard format cache
before changing it.

Public `WorkbookStyleCache` exposes four dictionary properties:
`FontCache`, `FillCache`, `BorderCache`, and `CellFormatCache`. The methods below
return style element indices; those indices are zero-based Open XML collection
indices.

```csharp
using CommonNetFuncs.Excel.Common;
using CommonNetFuncs.Excel.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

Common.ClearStandardFormatCache();
document.ClearStandardFormatCache();
document.ClearStyleElementCache();
Common.ClearCustomFormatCache();
document.ClearCustomFormatCache();
Common.WorkbookStyleCache? currentStyleCache =
    document.GetCustomFormatCache();

Stylesheet stylesheet = document.GetStylesheet(createIfMissing: true)!;
Borders borders = stylesheet.GetBorders(createIfMissing: true)!;
Fills fills = stylesheet.GetFills(createIfMissing: true)!;
Fonts fonts = stylesheet.GetFonts(createIfMissing: true)!;
CellFormats formats = stylesheet.GetCellFormats(createIfMissing: true)!;

uint headerStyle = document.GetStandardCellStyle(
    EStyle.Header, cellLocked: false, wrapText: true);
uint bodyStyle = document.GetStandardCellStyle(
    EStyle.Body, cellLocked: true, wrapText: false);
uint fontId = Common.GetFontId(EFont.Header, fonts);

var alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Left };
var protection = new Protection { Locked = true };
bool sameFormat = Common.CellFormatsAreEqual(
    new CellFormat
    {
        Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Left },
        Protection = new Protection { Locked = true }
    },
    new CellFormat
    {
        Alignment = new Alignment { Horizontal = HorizontalAlignmentValues.Left },
        Protection = new Protection { Locked = true }
    });
bool sameAlignment = Common.FormatAlignmentsAreEqual(
    alignment, new Alignment { Horizontal = HorizontalAlignmentValues.Left });
bool sameProtection = Common.FormatProtectionsAreEqual(
    protection, new Protection { Locked = true });

var styleCache = new Common.WorkbookStyleCache();
Font requestedFont = new() { Bold = new Bold() };
Fill requestedFill = new()
{
    PatternFill = new PatternFill { PatternType = PatternValues.Solid }
};
Border requestedBorder = new()
{
    LeftBorder = new LeftBorder { Style = BorderStyleValues.Thin }
};

uint? customFormat = document.GetCustomStyle(
    cellLocked: true,
    font: requestedFont,
    alignment: HorizontalAlignmentValues.Center,
    fill: requestedFill,
    border: requestedBorder,
    wrapText: true);

Font cacheFont = new() { Italic = new Italic() };
Fill cacheFill = new()
{
    PatternFill = new PatternFill { PatternType = PatternValues.Gray125 }
};
Border cacheBorder = new()
{
    RightBorder = new RightBorder { Style = BorderStyleValues.Thin }
};
uint customFontId = stylesheet.GetOrAddFont(styleCache, cacheFont);
uint customFillId = stylesheet.GetOrAddFill(styleCache, cacheFill);
uint customBorderId =
    stylesheet.GetOrAddBorder(styleCache, cacheBorder);
int openXmlHash = Common.GetHashCode(requestedFont);
```

`EStyle` also provides `HeaderThickTop`, `Error`, `Blackout`, `Whiteout`,
`Custom`, and `ImageBackground`; only styles implemented by this Open XML
switch receive preset elements for their cases. `ETableStyle` contains Excel's
Dark/Light/Medium 1-style range used by `CreateTable`.

## Cell insertion, formulas, shared strings, and tables

Coordinate arguments in these methods are one-based Excel row/column indices.
`InsertCellValue` defaults to `CellValues.SharedString` and converts the value's
text to a shared-string index. `InsertCellFormula` defaults to `CellValues.String`
and forces a requested shared-string formula result to `String`.

Shared-string write overloads do not save the shared-string part after every
insertion. Use a caller-managed `Dictionary<string, int>` cache for bulk writes
and call `SharedStringTable.Save()` or `SpreadsheetDocument.Save()` once.

```csharp
using CommonNetFuncs.Excel.Common;
using CommonNetFuncs.Excel.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

Cell? worksheetCell = sheet.InsertCell(1, 1);
Cell? dataCell = sheetData.InsertCell(2, 1, styleIndex: headerStyle);

sheet.InsertCellValue(
    1, 2, new CellValue("123"), CellValues.Number, bodyStyle);
sheetData.InsertCellValue(
    2, 2, new CellValue("Hello"), CellValues.String, bodyStyle);
sheet.InsertCellFormula(
    3, 1, "SUM(A1:A2)", CellValues.Number, bodyStyle);
sheetData.InsertCellFormula(
    3, 2, "\"cached\"", CellValues.String, bodyStyle);

Workbook workbook = document.WorkbookPart!.Workbook!;
int linearSharedIndex =
    workbook.InsertSharedStringItem("shared");
var sharedWriteCache = new Dictionary<string, int>(StringComparer.Ordinal);
int cachedSharedIndex =
    workbook.InsertSharedStringItem("cached", sharedWriteCache);
SharedStringTablePart sharedPart =
    document.WorkbookPart.GetPartsOfType<SharedStringTablePart>().First();
sharedPart.SharedStringTable!.Save();
IReadOnlyDictionary<int, string> sharedReadIndex =
    sharedPart.BuildSharedStringIndex();

sheet.CreateTable(
    startRow: 1, startCol: 1, endRow: 10, endColumn: 3,
    tableName: "ReportTable",
    styleName: ETableStyle.TableStyleMedium1,
    showRowStripes: true,
    showColStripes: false);
sheet.SetAutoFilter(1, 1, 10, 3);
```

## Image insertion and drawing helpers

Image bytes are associated with a PNG image part in the current implementation;
supply PNG-encoded bytes. Named-range overloads require workbook defined names.
Merged-area overloads anchor and center the image with aspect-preserving sizing.
The `AddImages` methods require equally sized data/range lists and reuse the
first worksheet/drawings part resolved for the batch; call them separately for
named ranges on different worksheets.

```csharp
using CommonNetFuncs.Excel.OpenXml;
using DocumentFormat.OpenXml.Drawing.Spreadsheet;
using DocumentFormat.OpenXml.Packaging;

byte[] png = File.ReadAllBytes(@"C:\images\logo.png");
var from = new CommonNetFuncs.Excel.OpenXml.Common.CellReference("B2");
var to = new CommonNetFuncs.Excel.OpenXml.Common.CellReference("D8");

document.AddImage(png, "LogoRange");
document.AddImage(png, (from, to));
document.AddImages([png, png], ["LogoRange", "SecondLogoRange"]);
document.AddImages(
    [png, png],
    [(from, to),
     (new CommonNetFuncs.Excel.OpenXml.Common.CellReference("F2"),
      new CommonNetFuncs.Excel.OpenXml.Common.CellReference("H8"))]);

DrawingsPart? drawings = Common.GetOrCreateDrawingsPart(sheetPart);
if (drawings is not null)
{
    sheetPart.AddImagePart(drawings, (from, to), bodyStyle, png);
    (CommonNetFuncs.Excel.OpenXml.Common.CellReference start,
     CommonNetFuncs.Excel.OpenXml.Common.CellReference end) area =
        sheetPart.GetMergedCellArea(from);
    int rangeWidth = Common.GetRangeWidthInPx(sheetPart, (start, end));
    int rangeHeight = Common.GetRangeHeightInPx(sheetPart, (start, end));

    // AddImageToWorksheet is the final low-level one-picture anchor helper.
    ImagePart part = drawings.AddImagePart(ImagePartType.Png);
    using (var imageBytes = new MemoryStream(png))
        part.FeedData(imageBytes);
    Common.AddImageToWorksheet(
        drawings, drawings.GetIdOfPart(part), from,
        xMargin: 0, yMargin: 0, width: 120, height: 60);
}
```

## Reading ranges and values

Reader methods set/rely on stream position zero, open the package read-only, and
leave the caller to manage the stream. They do not return streaming progress
notifications. `ReadExcelFileToDataTable` catches/logs ordinary errors to the
console and returns the current/empty table; `ReadExcelTableToDataTable` catches
and logs errors and returns the current/empty table. Cancellation in the table
reader is caught by its broad exception block, so inspect the table rather than
relying on a thrown cancellation exception.
The flat-sheet reader stops at the first completely empty data row when no
explicit end reference is supplied.

The object readers use `new()` and public writable properties. Header matching is
case-insensitive and `[ExcelColumn("...")]` names take priority. Conversion
supports strings, booleans, numeric primitive types, enums, `DateOnly`,
`TimeOnly`, `DateTime`, `DateTimeOffset`, `TimeSpan`, and `Guid`; failed
conversions leave the property at its default. The async object reader yields
once before wrapping the synchronous lazy enumeration and checks cancellation
between yielded rows.

```csharp
using System.Data;
using CommonNetFuncs.Excel.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

using var readStream = File.OpenRead(@"C:\data\input.xlsx");
readStream.Position = 0;

IEnumerable<Cell?> range = sheet.GetRange("A1:C10");
DataTable unformatted = readStream.ReadExcelFileToDataTable(
    hasHeaders: true,
    sheetName: "Sheet1",
    startCellReference: "A1",
    endCellReference: "D100");

readStream.Position = 0;
DataTable excelTable = readStream.ReadExcelTableToDataTable(
    tableName: "SalesTable",
    cancellationToken: CancellationToken.None);

readStream.Position = 0;
IEnumerable<SaleRow> entities =
    readStream.ReadExcelFileToEnumerable<SaleRow>(
        hasHeaders: true,
        sheetName: "Sheet1",
        startCellReference: "A1",
        endCellReference: "D100");

readStream.Position = 0;
await foreach (SaleRow entity in readStream
    .ReadExcelFileToAsyncEnumerable<SaleRow>(
        hasHeaders: true,
        sheetName: "Sheet1",
        startCellReference: "A1",
        endCellReference: "D100",
        cancellationToken: CancellationToken.None))
{
    Console.WriteLine(entity.Name);
}

CommonNetFuncs.Excel.OpenXml.Common.CellReference last =
    sheetData.GetLastPopulatedCell();
string v1 = sheetData.GetCellValue(row: 1, col: 1);
string v2 = sheetData.GetCellValue(
    row: 1, col: 1, sharedReadIndex);
string v3 = sheetData.GetCellValue(a1);
string v4 = sheet.GetCellValue(a1);
string v5 = worksheetCell.GetCellValue();
string v6 = worksheetCell.GetCellValue(sharedReadIndex);

string? sv1 = sheet.GetStringValue(a1);
string? sv2 = worksheetCell.GetStringValue();
string? sv3 = worksheetCell.GetStringValue(sharedReadIndex);

Sheet? tableSheet = document.GetSheetForTable(
    document.WorkbookPart!.WorksheetParts
        .SelectMany(x => x.TableDefinitionParts)
        .Select(x => x.Table!)
        .First());
Table? foundTable = document.WorkbookPart.FindTable("SalesTable");
Row? rowOne = sheet.GetRow(1);
Cell? colOne = rowOne!.GetCell(1);
```

`GetCellValue` returns raw/decoded cell text and empty string for a missing cell.
`GetStringValue` additionally formats Boolean cells as `TRUE`/`FALSE` and error
cells as `ERROR: <value>`; formula cells use their cached `CellValue`. Neither
method computes formulas.

## Column sizing and width calculation

```csharp
using CommonNetFuncs.Excel.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;

sheet.AutoFitColumns(maxWidth: 80);
Columns columns = sheet.GetColumns();
sheet.SizeColumn(colIndex: 1, columnWidth: 18, columns);
Column? column = sheet.GetOrCreateColumn(
    colIndex: 2, columnWidth: 24, columns);

double fromCellWidth = worksheetCell!.CalculateWidth();
double fromIndexedCellWidth =
    worksheetCell.CalculateWidth(sharedReadIndex);
double fromTextWidth = Common.CalculateWidth("Customer Name", bodyStyle);
```

`AutoFitColumns` uses the maximum observed text width per column, capped by
`maxWidth`. `CalculateWidth(string, styleIndex)` recognizes the package's
built-in numeric/bold style indices for an estimate. Widths are character-style
estimates, not pixel-perfect font/layout calculations.

## Every typed cell-setter overload

String setters use `CellValues.String` with a `CellValue`; numeric setters use
`CellValues.Number`; date setters use `CellValues.Date`. `Cell?` overloads are
no-ops for `null`. `Worksheet`/reference overloads can create a missing row/cell.
`SheetData` coordinate/reference overloads only update an existing cell; insert
it first when necessary.

```csharp
using CommonNetFuncs.Excel.OpenXml;
using System.Globalization;

DateOnly day = new(2026, 11, 20);
DateTime moment = new(2026, 11, 20, 14, 30, 0);
var refOne = a1;

// SetCellStringValue: each type has Cell, SheetData(row/col),
// SheetData(reference), and Worksheet(reference) overloads.
worksheetCell.SetCellStringValue("text");
sheetData.SetCellStringValue(1, 1, "text");
sheetData.SetCellStringValue(a1, "text");
sheet.SetCellStringValue(a1, "text");

worksheetCell.SetCellStringValue(true);
sheetData.SetCellStringValue(1, 1, true);
sheetData.SetCellStringValue(a1, true);
sheet.SetCellStringValue(a1, true);

worksheetCell.SetCellStringValue(42);
sheetData.SetCellStringValue(1, 1, 42);
sheetData.SetCellStringValue(a1, 42);
sheet.SetCellStringValue(a1, 42);

worksheetCell.SetCellStringValue(42.5);
sheetData.SetCellStringValue(1, 1, 42.5);
sheetData.SetCellStringValue(a1, 42.5);
sheet.SetCellStringValue(a1, 42.5);

worksheetCell.SetCellStringValue(42.50m);
sheetData.SetCellStringValue(1, 1, 42.50m);
sheetData.SetCellStringValue(a1, 42.50m);
sheet.SetCellStringValue(a1, 42.50m);

worksheetCell.SetCellStringValue(day, "yyyy-MM-dd");
sheetData.SetCellStringValue(
    1, 1, day, "yyyy-MM-dd");
sheetData.SetCellStringValue(
    a1, day, "yyyy-MM-dd");
sheet.SetCellStringValue(
    a1, day, "yyyy-MM-dd");

worksheetCell.SetCellStringValue(moment, "O");
sheetData.SetCellStringValue(
    1, 1, moment, "O");
sheetData.SetCellStringValue(
    a1, moment, "O");
sheet.SetCellStringValue(
    a1, moment, "O");

// SetCellDateValue: DateOnly and DateTime, each in four receiver forms.
worksheetCell.SetCellDateValue(day);
sheetData.SetCellDateValue(1, 1, day);
sheetData.SetCellDateValue(a1, day);
sheet.SetCellDateValue(a1, day);

worksheetCell.SetCellDateValue(moment);
sheetData.SetCellDateValue(1, 1, moment);
sheetData.SetCellDateValue(a1, moment);
sheet.SetCellDateValue(a1, moment);

// SetCellNumericValue: int, double, and decimal, each in four receiver forms.
worksheetCell.SetCellNumericValue(42);
sheetData.SetCellNumericValue(1, 1, 42);
sheetData.SetCellNumericValue(a1, 42);
sheet.SetCellNumericValue(a1, 42);

worksheetCell.SetCellNumericValue(42.5);
sheetData.SetCellNumericValue(1, 1, 42.5);
sheetData.SetCellNumericValue(a1, 42.5);
sheet.SetCellNumericValue(a1, 42.5);

worksheetCell.SetCellNumericValue(42.50m);
sheetData.SetCellNumericValue(1, 1, 42.50m);
sheetData.SetCellNumericValue(a1, 42.50m);
sheet.SetCellNumericValue(a1, 42.50m);
```

Date string formatting uses `DateOnly.ToString(dateFormat)` or
`DateTime.ToString(dateFormat)`. The default is `"MM/dd/yyyy"` except for the
`Cell, DateTime` overload, whose parameter default is `"g"`. Format strings use
the current culture. Date-valued cell methods write the date's text as an Open
XML date cell; they do not establish an Excel number-format style.

## Formula recalculation, validation, tables, and finalization

```csharp
using CommonNetFuncs.Excel.OpenXml;

document.ForceFormulaRecalculation();
document.WorkbookPart!.ForceFormulaRecalculation();
document.WorkbookPart.Workbook!.ForceFormulaRecalculation();

Common.AddDropDownValidation(
    sheet,
    cellReference: "B2:B100",
    formula: "\"Open,Closed,Pending\"");

int absoluteColumn = foundTable!.GetColumnIndex(
    "Name", tableStart: foundTable.GetTableStart());
CommonNetFuncs.Excel.OpenXml.Common.CellReference tableStart =
    foundTable.GetTableStart();

// Stream-backed document: closes it and rewinds the caller's seekable stream.
using var streamBacked = new MemoryStream();
using var streamDocument = SpreadsheetDocument.Create(
    streamBacked, SpreadsheetDocumentType.Workbook);
streamDocument.InitializeExcelFile("Sheet1");
streamDocument.WriteAndClose(
    streamBacked, clearCachedStyles: false);

// File-backed document copied into a MemoryStream.
string path = Path.Combine(Path.GetTempPath(), "output.xlsx");
using var fileResult = new MemoryStream();
using var fileDocument = SpreadsheetDocument.Create(
    path, SpreadsheetDocumentType.Workbook);
fileDocument.InitializeExcelFile("Sheet1");
fileDocument.WriteAndClose(
    fileResult, path, clearCachedStyles: true);

// Async variant for a file-backed document.
string asyncPath = Path.Combine(Path.GetTempPath(), "output-async.xlsx");
using var asyncResult = new MemoryStream();
using var asyncFileDocument = SpreadsheetDocument.Create(
    asyncPath, SpreadsheetDocumentType.Workbook);
asyncFileDocument.InitializeExcelFile("Sheet1");
await asyncFileDocument.WriteAndCloseAsync(
    asyncResult, asyncPath, clearCachedStyles: true);
```

`WriteAndClose` saves/disposes the `SpreadsheetDocument`; callers do not dispose
it again. The stream overload resets only a seekable stream. The file overloads
copy the saved file into the provided `MemoryStream` at position zero but do not
clear pre-existing `MemoryStream` contents before copying, so pass a fresh or
already truncated stream. The async overload is genuinely asynchronous only for
the final file-to-memory copy.

## `CellReference`, attributes, and enumerable mapping

`Common.CellReference` is the package's one-based reference helper. Row values
are checked against `1..1,048,576`; column values against `1..16,384`. It accepts
the usual letter/number form such as `A1`, provides `ToString()`, and supplies
static column conversions. `ExcelColumnAttribute` names a destination property
when workbook headers differ from property names.

```csharp
using CommonNetFuncs.Excel.OpenXml;
using CellReference =
    CommonNetFuncs.Excel.OpenXml.Common.CellReference;

var fromText = new CellReference("XFD1048576");
fromText.RowIndex = 42;
fromText.ColumnIndex = 3;
string canonical = fromText.ToString();

var fromNumbers = new CellReference(col: 27, row: 9);
uint columnNumber = CellReference.ColumnNameToNumber("AA");
string columnName = CellReference.NumberToColumnName(columnNumber);

public sealed class SaleRow
{
    [ExcelColumn("Customer")]
    public string Name { get; set; } = "";

    public decimal Amount { get; set; }
}

var attribute = new ExcelColumnAttribute("Customer");
string attributeName = attribute.Name;
```

The `[ExcelColumn]` attribute affects `ReadExcelFileToEnumerable<T>` and
`ReadExcelFileToAsyncEnumerable<T>`, not export header names.

## Correctness and resource notes

- Save or finalize the workbook after modifications. Most low-level extension
  methods mutate the Open XML object graph and do not save the document.
- Methods that create/retrieve missing cells (`GetCellFromReference`,
  `GetCellFromCoordinates`, typed setters) mutate a workbook. Do not use those
  on a read-only package.
- Reading convenience methods generally position the input at zero and leave
  ownership with the caller. Build and reuse a shared-string index for repeated
  cell lookups.
- The DOM exporter is not constant-memory for large rows. SAX output reduces
  worksheet DOM retention but still relies on output capacity and input
  enumeration.
- Image helpers need valid PNG bytes in the current implementation, sufficient
  cell area, and native SkiaSharp support. No external Office process is used.
- Validate workbook, sheet, table, named-range, and replacement input. Exceptions
  are not uniform: some helpers propagate, some return `null`/`false`, and some
  log and return an empty result. Check both the API result and package contents.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
