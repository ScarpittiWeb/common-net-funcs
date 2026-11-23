using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using CommonNetFuncs.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Spreadsheet;
using FastExpressionCompiler;
using ZLinq;
using static CommonNetFuncs.Core.ReflectionCaches;
using static CommonNetFuncs.Excel.OpenXml.Common;

namespace BenchmarkSuite;

// Office/CSV/Word hot-path candidates (Excel.OpenXml, Excel.Npoi, Excel.ClosedXml, Word.OpenXml).
// Each class pits a verbatim private copy of the current library hot loop ("Original", baseline) against the proposed replacement(s).
// "Shared" variants compile on both net11.0 and netstandard2.1. Library calls that cannot be reproduced without NPOI/ClosedXML/Word
// packages are replaced by an equivalent sink so only the code that actually changes is measured. PoVerify.Run() asserts equivalence.

// ============================================================================================================================
// 1. OpenXml ReadExcelFileToDataTable -> GetCellValue(SheetData, row, col, index) per cell
// ============================================================================================================================

/// <summary>OpenXml ReadExcelFileToDataTable: per-cell O(rows) row scan + O(cols) regex CellReference parses vs a forward-only lazy row/cell index.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoOpenXmlSheetReadLookupBenchmarks
{
	private const int ColumnCount = 20;
	private SheetData sheetData = null!;
	private Dictionary<int, string> sharedStringIndex = null!;
	private CellReference startCell = null!;
	private CellReference endCell = null!;

	[Params(250, 1000)]
	public int RowCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		Worksheet worksheet = PoData.CreateWorksheet(RowCount, ColumnCount, out sharedStringIndex);
		sheetData = worksheet.GetFirstChild<SheetData>()!;
		startCell = new CellReference("A1");
		endCell = new CellReference(ColumnCount, (uint)RowCount + 1);
	}

	[Benchmark(Baseline = true)]
	public DataTable Original()
	{
		return OriginalRead(sheetData, sharedStringIndex, true, startCell, endCell);
	}

	[Benchmark]
	public DataTable Shared_LazyRowCellIndex()
	{
		return OptimizedRead(sheetData, sharedStringIndex, true, startCell, endCell);
	}

	// Verbatim body of Common.ReadExcelFileToDataTable after the document has been opened (Common.cs ~2126-2168).
	internal static DataTable OriginalRead(SheetData sheetData, IReadOnlyDictionary<int, string>? sharedStringIndex, bool hasHeaders, CellReference startCell, CellReference endCell)
	{
		DataTable dataTable = new();
		try
		{
			// Add columns to DataTable
			for (uint col = startCell.ColumnIndex; col <= endCell.ColumnIndex; col++)
			{
				string columnName = hasHeaders ? OriginalGetCellValue(sheetData, startCell.RowIndex, col, sharedStringIndex) : $"Column{col - startCell.ColumnIndex}";
				dataTable.Columns.Add(columnName);
			}

			// Add rows to DataTable
			uint dataStartRow = hasHeaders ? (startCell.RowIndex + 1) : startCell.RowIndex;
			for (uint row = dataStartRow; row <= endCell.RowIndex; row++)
			{
				DataRow dataRow = dataTable.NewRow();
				bool rowHasData = false;

				for (uint col = startCell.ColumnIndex; col <= endCell.ColumnIndex; col++)
				{
					string cellValue = OriginalGetCellValue(sheetData, row, col, sharedStringIndex);
					dataRow[(int)(col - startCell.ColumnIndex)] = cellValue;
					if (!string.IsNullOrWhiteSpace(cellValue))
					{
						rowHasData = true;
					}
				}

				if (rowHasData)
				{
					dataTable.Rows.Add(dataRow);
				}
				else
				{
					break; // Stop at the first empty row
				}
			}
		}
		catch (Exception)
		{
			// Original writes the message to Console
		}
		return dataTable;
	}

	// Verbatim copy of Common.GetCellValue(SheetData, uint, uint, IReadOnlyDictionary) (Common.cs:2207)
	private static string OriginalGetCellValue(SheetData sheetData, uint row, uint col, IReadOnlyDictionary<int, string>? sharedStringIndex)
	{
		CellReference cellRef = new(col, row);
		Cell? cell = sheetData.Elements<Row>().FirstOrDefault(x => (x.RowIndex != null) && (x.RowIndex == row))?
			.Elements<Cell>().FirstOrDefault(x => (x.CellReference?.Value is { } cellRefText) && string.Equals(new CellReference(cellRefText).ToString(), cellRef.ToString(), StringComparison.OrdinalIgnoreCase));
		return cell?.GetCellValue(sharedStringIndex) ?? string.Empty;
	}

	// Same loop, but rows/cells are resolved through forward-only lazy indexes (each Row/Cell element is visited and parsed at most once).
	internal static DataTable OptimizedRead(SheetData sheetData, IReadOnlyDictionary<int, string>? sharedStringIndex, bool hasHeaders, CellReference startCell, CellReference endCell)
	{
		DataTable dataTable = new();
		try
		{
			PoLazyRowLookup rows = new(sheetData);

			PoLazyParsedCellLookup? headerCells = null;
			bool headerResolved = false;
			for (uint col = startCell.ColumnIndex; col <= endCell.ColumnIndex; col++)
			{
				string columnName;
				if (hasHeaders)
				{
					// Resolved on first use so any exception surfaces at the same point as the original per-cell lookup
					if (!headerResolved)
					{
						headerCells = PoLazyParsedCellLookup.ForRow(rows.Find(startCell.RowIndex), startCell.RowIndex);
						headerResolved = true;
					}
					columnName = headerCells?.Find(col)?.GetCellValue(sharedStringIndex) ?? string.Empty;
				}
				else
				{
					columnName = $"Column{col - startCell.ColumnIndex}";
				}
				dataTable.Columns.Add(columnName);
			}

			uint dataStartRow = hasHeaders ? (startCell.RowIndex + 1) : startCell.RowIndex;
			for (uint row = dataStartRow; row <= endCell.RowIndex; row++)
			{
				DataRow dataRow = dataTable.NewRow();
				bool rowHasData = false;
				PoLazyParsedCellLookup? cells = null;
				bool rowResolved = false;

				for (uint col = startCell.ColumnIndex; col <= endCell.ColumnIndex; col++)
				{
					if (!rowResolved)
					{
						cells = PoLazyParsedCellLookup.ForRow(rows.Find(row), row);
						rowResolved = true;
					}

					string cellValue = cells?.Find(col)?.GetCellValue(sharedStringIndex) ?? string.Empty;
					dataRow[(int)(col - startCell.ColumnIndex)] = cellValue;
					if (!string.IsNullOrWhiteSpace(cellValue))
					{
						rowHasData = true;
					}
				}

				if (rowHasData)
				{
					dataTable.Rows.Add(dataRow);
				}
				else
				{
					break;
				}
			}
		}
		catch (Exception)
		{
			// Original writes the message to Console
		}
		return dataTable;
	}
}

/// <summary>
/// Forward-only index over SheetData rows. Find(r) returns exactly what Elements&lt;Row&gt;().FirstOrDefault(x =&gt; x.RowIndex != null &amp;&amp; x.RowIndex == r) returns,
/// and throws at the same point (a row whose RowIndex text cannot be parsed stays pending, so every later miss rethrows like the original rescan).
/// </summary>
internal sealed class PoLazyRowLookup
{
	private readonly IEnumerator<Row>? rowEnumerator;
	private readonly Dictionary<uint, Row> rowsByIndex = [];
	private Row? pending;
	private bool exhausted;

	public PoLazyRowLookup(SheetData? sheetData)
	{
		rowEnumerator = sheetData?.Elements<Row>().GetEnumerator();
		exhausted = rowEnumerator == null;
	}

	public Row? Find(uint rowIndex)
	{
		if (rowsByIndex.TryGetValue(rowIndex, out Row? found))
		{
			return found;
		}

		while (!exhausted)
		{
			Row? candidate = pending;
			if (candidate == null)
			{
				if (!rowEnumerator!.MoveNext())
				{
					exhausted = true;
					break;
				}
				candidate = rowEnumerator.Current;
				pending = candidate;
			}

			if (candidate.RowIndex != null)
			{
				uint index = candidate.RowIndex.Value; // Throws for malformed text, exactly like the original comparison
				pending = null;
				rowsByIndex.TryAdd(index, candidate); // First occurrence wins, matching FirstOrDefault
				if (index == rowIndex)
				{
					return candidate;
				}
			}
			else
			{
				pending = null;
			}
		}
		return null;
	}
}

/// <summary>Forward-only per-row cell index keyed by parsed column; same match/exception semantics as the original per-cell CellReference compare scan.</summary>
internal sealed class PoLazyParsedCellLookup
{
	private readonly IEnumerator<Cell> cellEnumerator;
	private readonly Dictionary<uint, Cell> cellsByColumn = [];
	private readonly uint rowIndex;
	private Cell? pending;
	private bool exhausted;

	private PoLazyParsedCellLookup(Row row, uint rowIndex)
	{
		cellEnumerator = row.Elements<Cell>().GetEnumerator();
		this.rowIndex = rowIndex;
	}

	public static PoLazyParsedCellLookup? ForRow(Row? row, uint rowIndex)
	{
		return row == null ? null : new PoLazyParsedCellLookup(row, rowIndex);
	}

	public Cell? Find(uint columnIndex)
	{
		if (cellsByColumn.TryGetValue(columnIndex, out Cell? found))
		{
			return found;
		}

		while (!exhausted)
		{
			Cell? candidate = pending;
			if (candidate == null)
			{
				if (!cellEnumerator.MoveNext())
				{
					exhausted = true;
					break;
				}
				candidate = cellEnumerator.Current;
				pending = candidate;
			}

			if (candidate.CellReference?.Value is { } cellRefText)
			{
				CellReference parsed = new(cellRefText); // Throws for malformed references, exactly where the original predicate would
				pending = null;
				// Original compares "<col><row>" strings, so only cells whose reference names this row can ever match
				if (parsed.RowIndex == rowIndex)
				{
					cellsByColumn.TryAdd(parsed.ColumnIndex, candidate);
					if (parsed.ColumnIndex == columnIndex)
					{
						return candidate;
					}
				}
			}
			else
			{
				pending = null;
			}
		}
		return null;
	}
}

// ============================================================================================================================
// 2. OpenXml ReadExcelTableToDataTable -> GetCellFromCoordinates per cell (GetRow + GetCell linear scans)
// ============================================================================================================================

/// <summary>OpenXml ReadExcelTableToDataTable: per-cell GetCellFromCoordinates (O(rows) GetRow + O(cols) GetCell) vs indexed lookups with identical miss/exception handling.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoOpenXmlTableReadLookupBenchmarks
{
	private const int ColumnCount = 20;
	private Worksheet worksheet = null!;
	private Dictionary<int, string> sharedStringIndex = null!;
	private CellReference startCell = null!;
	private CellReference endCell = null!;

	[Params(250, 1000)]
	public int RowCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		// Dense sheet: the original never has to create missing cells, so the DOM is not mutated between iterations
		worksheet = PoData.CreateWorksheet(RowCount, ColumnCount, out sharedStringIndex);
		startCell = new CellReference("A1");
		endCell = new CellReference(ColumnCount, (uint)RowCount + 1);
	}

	[Benchmark(Baseline = true)]
	public DataTable Original()
	{
		return OriginalReadTable(worksheet, sharedStringIndex, startCell, endCell);
	}

	[Benchmark]
	public DataTable Shared_IndexedLookup()
	{
		return OptimizedReadTable(worksheet, sharedStringIndex, startCell, endCell);
	}

	// Verbatim header/body loops of Common.ReadExcelTableToDataTable (Common.cs ~2414-2440), cancellation token omitted.
	internal static DataTable OriginalReadTable(Worksheet? worksheet, IReadOnlyDictionary<int, string>? sharedStringIndex, CellReference startCell, CellReference endCell)
	{
		DataTable dataTable = new();
		try
		{
			// Get headers
			for (uint col = startCell.ColumnIndex; col <= endCell.ColumnIndex; col++)
			{
				Cell? headerCell = (worksheet != null) ? OriginalGetCellFromCoordinates(worksheet, (int)col, (int)startCell.RowIndex) : null;
				if (headerCell != null)
				{
					dataTable.Columns.Add(headerCell.GetStringValue(sharedStringIndex) ?? $"Column{col - startCell.ColumnIndex + 1}");
				}
			}

			// Get body data
			for (uint row = startCell.RowIndex + 1; row <= endCell.RowIndex; row++)
			{
				DataRow dataRow = dataTable.NewRow();
				for (uint col = startCell.ColumnIndex; col <= endCell.ColumnIndex; col++)
				{
					Cell? cell = (worksheet != null) ? OriginalGetCellFromCoordinates(worksheet, (int)col, (int)row) : null;
					if (cell != null)
					{
						dataRow[((int)col) - ((int)startCell.ColumnIndex)] = cell.GetStringValue(sharedStringIndex);
					}
				}
				dataTable.Rows.Add(dataRow);
			}
		}
		catch (Exception)
		{
			// Original logs via NLog
		}
		return dataTable;
	}

	// Verbatim copy of Common.GetCellFromCoordinates (Common.cs:374), logging omitted.
	private static Cell? OriginalGetCellFromCoordinates(Worksheet ws, int x, int y, int colOffset = 0, int rowOffset = 0)
	{
		try
		{
			uint targetRowIndex = (uint)(y + rowOffset);
			uint targetColIndex = (uint)(x + colOffset);
			Row? row = OriginalGetRow(ws, targetRowIndex);
			if (row == null)
			{
				row = new Row() { RowIndex = targetRowIndex };
				SheetData? sheetData = ws.GetFirstChild<SheetData>();
				Row? nextRow = sheetData?.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value > targetRowIndex);
				if (nextRow != null)
				{
					sheetData!.InsertBefore(row, nextRow);
				}
				else
				{
					sheetData?.AppendChild(row);
				}
			}
			Cell? cell = OriginalGetCell(row, targetColIndex);
			if (cell == null)
			{
				cell = new() { CellReference = new CellReference(targetColIndex, targetRowIndex).ToString() };
				Cell? nextCell = row.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value is { } cellRefText && new CellReference(cellRefText).ColumnIndex > targetColIndex);
				if (nextCell != null)
				{
					row.InsertBefore(cell, nextCell);
				}
				else
				{
					row.AppendChild(cell);
				}
			}
			return cell;
		}
		catch (Exception)
		{
			return null;
		}
	}

	// Verbatim copy of Common.GetRow (Common.cs:2500)
	private static Row? OriginalGetRow(Worksheet worksheet, uint rowIndex)
	{
		return worksheet.GetFirstChild<SheetData>()?.Elements<Row>().FirstOrDefault(r => (r.RowIndex != null) && (r.RowIndex == rowIndex));
	}

	// Verbatim copy of Common.GetCell (Common.cs:2511)
	private static Cell? OriginalGetCell(Row row, uint columnIndex)
	{
		if (row.RowIndex == null)
		{
			return null;
		}

		string cellReference = new CellReference(columnIndex, row.RowIndex.Value).ToString();
		return row.Elements<Cell>().FirstOrDefault(c => c.CellReference == cellReference);
	}

	internal static DataTable OptimizedReadTable(Worksheet? worksheet, IReadOnlyDictionary<int, string>? sharedStringIndex, CellReference startCell, CellReference endCell)
	{
		DataTable dataTable = new();
		try
		{
			PoTableCellResolver? resolver = (worksheet != null) ? new PoTableCellResolver(worksheet) : null;
			uint firstCol = startCell.ColumnIndex;
			string[] colLetters = new string[(endCell.ColumnIndex >= firstCol) ? (endCell.ColumnIndex - firstCol + 1) : 0];
			for (int i = 0; i < colLetters.Length; i++)
			{
				colLetters[i] = CellReference.NumberToColumnName(firstCol + (uint)i);
			}

			string headerRowText = startCell.RowIndex.ToString(CultureInfo.InvariantCulture);
			for (uint col = firstCol; col <= endCell.ColumnIndex; col++)
			{
				if ((resolver != null) && resolver.TryResolve(startCell.RowIndex, col, colLetters[col - firstCol] + headerRowText, out Cell? headerCell))
				{
					// A missing cell is created empty by the original, whose GetStringValue is ""
					dataTable.Columns.Add(((headerCell != null) ? headerCell.GetStringValue(sharedStringIndex) : string.Empty) ?? $"Column{col - firstCol + 1}");
				}
			}

			for (uint row = startCell.RowIndex + 1; row <= endCell.RowIndex; row++)
			{
				DataRow dataRow = dataTable.NewRow();
				string rowText = row.ToString(CultureInfo.InvariantCulture);
				for (uint col = firstCol; col <= endCell.ColumnIndex; col++)
				{
					if ((resolver != null) && resolver.TryResolve(row, col, colLetters[col - firstCol] + rowText, out Cell? cell))
					{
						dataRow[((int)col) - ((int)firstCol)] = (cell != null) ? cell.GetStringValue(sharedStringIndex) : string.Empty;
					}
				}
				dataTable.Rows.Add(dataRow);
			}
		}
		catch (Exception)
		{
			// Original logs via NLog
		}
		return dataTable;
	}
}

/// <summary>Read-only replacement for GetCellFromCoordinates inside a table read: same results without mutating the DOM or rescanning rows/cells.</summary>
internal sealed class PoTableCellResolver
{
	private readonly SheetData? sheetData;
	private readonly PoLazyRowLookup rows;
	private uint currentRowIndex;
	private PoLazyRefCellLookup? currentCells;

	public PoTableCellResolver(Worksheet worksheet)
	{
		sheetData = worksheet.GetFirstChild<SheetData>();
		rows = new PoLazyRowLookup(sheetData);
	}

	/// <returns>
	/// <see langword="false"/> when the original GetCellFromCoordinates would have caught an exception and returned null.
	/// <see langword="true"/> with a null <paramref name="cell"/> when the original would have created (and returned) a new empty cell.
	/// </returns>
	public bool TryResolve(uint rowIndex, uint columnIndex, string canonicalReference, out Cell? cell)
	{
		cell = null;
		try
		{
			Row? row = rows.Find(rowIndex);
			if (row == null)
			{
				// The original inserts a new row; its insertion-point scan is the only part that can throw
				_ = sheetData?.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value > rowIndex);
				return true;
			}

			if ((currentCells == null) || (currentRowIndex != rowIndex))
			{
				currentCells = new PoLazyRefCellLookup(row);
				currentRowIndex = rowIndex;
			}

			cell = currentCells.Find(canonicalReference);
			if (cell == null)
			{
				// The original inserts a new cell; its insertion-point scan parses sibling references and can throw.
				// Cells it inserted earlier have lower columns and valid references, so they never change this scan's outcome.
				_ = row.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value is { } cellRefText && new CellReference(cellRefText).ColumnIndex > columnIndex);
			}
			return true;
		}
		catch (Exception)
		{
			cell = null;
			return false;
		}
	}
}

/// <summary>Forward-only per-row index keyed by the exact (ordinal) CellReference text, matching GetCell's c.CellReference == "B12" compare.</summary>
internal sealed class PoLazyRefCellLookup(Row row)
{
	private readonly IEnumerator<Cell> cellEnumerator = row.Elements<Cell>().GetEnumerator();
	private readonly Dictionary<string, Cell> cellsByReference = new(StringComparer.Ordinal);
	private bool exhausted;

	public Cell? Find(string reference)
	{
		if (cellsByReference.TryGetValue(reference, out Cell? found))
		{
			return found;
		}

		while (!exhausted)
		{
			if (!cellEnumerator.MoveNext())
			{
				exhausted = true;
				break;
			}

			Cell candidate = cellEnumerator.Current;
			string? text = candidate.CellReference?.Value;
			if (text != null)
			{
				cellsByReference.TryAdd(text, candidate);
				if (string.Equals(text, reference, StringComparison.Ordinal))
				{
					return candidate;
				}
			}
		}
		return null;
	}
}

// ============================================================================================================================
// 3. CellReference(string) constructor (ToUpperInvariant + Regex + Group substrings + uint.Parse per call)
// ============================================================================================================================

/// <summary>CellReference(string) ctor: ToUpperInvariant + regex match + substrings + uint.Parse vs allocation-free ASCII fast path with regex fallback.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoCellReferenceParseBenchmarks
{
	private string[] references = [];

	[GlobalSetup]
	public void Setup()
	{
		List<string> list = new(20_000);
		for (uint row = 1; row <= 1000; row++)
		{
			for (uint col = 1; col <= 20; col++)
			{
				string reference = CellReference.NumberToColumnName(col * 37) + row.ToString(CultureInfo.InvariantCulture);
				list.Add(((row + col) % 10) == 0 ? reference.ToLowerInvariant() : reference);
			}
		}
		references = [.. list];
	}

	[Benchmark(Baseline = true)]
	public ulong Original()
	{
		ulong checksum = 0;
		foreach (string reference in references)
		{
			CellReference parsed = new(reference);
			checksum += (parsed.ColumnIndex * 1_048_576UL) + parsed.RowIndex;
		}
		return checksum;
	}

	[Benchmark]
	public ulong Shared_AsciiFastPath()
	{
		ulong checksum = 0;
		foreach (string reference in references)
		{
			PoCellRefParser.Parse(reference, out uint column, out uint row);
			checksum += (column * 1_048_576UL) + row;
		}
		return checksum;
	}
}

/// <summary>Proposed body for CellReference(string): try the ASCII fast path, otherwise fall back to the existing regex path (identical results/exceptions).</summary>
internal static class PoCellRefParser
{
	public static void Parse(string reference, out uint column, out uint row)
	{
		if (TryParseAscii(reference, out column, out row))
		{
			return;
		}

		// Anything unusual (lower-case non-ASCII letters, '$', ranges, whitespace, out-of-range values) keeps the original regex behavior
		CellReference fallback = new(reference);
		column = fallback.ColumnIndex;
		row = fallback.RowIndex;
	}

	/// <summary>Accepts only ^[A-Za-z]{1,3}[0-9]{1,7}$ with in-range values, for which the regex path provably yields the same column/row.</summary>
	public static bool TryParseAscii(string? reference, out uint column, out uint row)
	{
		column = 0;
		row = 0;
		if (reference == null)
		{
			return false;
		}

		int length = reference.Length;
		int letters = 0;
		while ((letters < length) && IsAsciiLetter(reference[letters]))
		{
			letters++;
		}

		int digits = length - letters;
		if ((letters is 0 or > 3) || (digits is 0 or > 7))
		{
			return false;
		}

		uint col = 0;
		for (int i = 0; i < letters; i++)
		{
			col = (col * 26) + (uint)((reference[i] | 0x20) - 'a' + 1);
		}

		uint r = 0;
		for (int i = letters; i < length; i++)
		{
			char ch = reference[i];
			if ((ch < '0') || (ch > '9'))
			{
				return false;
			}
			r = (r * 10) + (uint)(ch - '0');
		}

		// Out-of-range values go through the original path so the identical ArgumentOutOfRangeException is thrown
		if ((col is < 1 or > 16384) || (r is < 1 or > 1048576))
		{
			return false;
		}

		column = col;
		row = r;
		return true;
	}

	private static bool IsAsciiLetter(char c)
	{
		return (uint)((c | 0x20) - 'a') <= ('z' - 'a');
	}
}

// ============================================================================================================================
// 4. PropertyInfo.GetValue per cell in all generic exporters
// ============================================================================================================================

/// <summary>Generic exporters (OpenXml DOM/SAX, NPOI, ClosedXML): PropertyInfo.GetValue + ToString per cell vs cached compiled Func&lt;T, object?&gt; getters.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoCompiledGetterBenchmarks
{
	private List<ExportTestRow> rows = [];
	private PropertyInfo[] properties = [];

	[GlobalSetup]
	public void Setup()
	{
		rows = PoData.CreateExportRows(2000);
		properties = GetOrAddPropertiesFromReflectionCache(typeof(ExportTestRow));
		_ = PoGetterFactory.Create<ExportTestRow>(properties); // Warm the static delegate cache (one-time cost in the library)
	}

	[Benchmark(Baseline = true)]
	public long Original_PropertyInfoGetValue()
	{
		long chars = 0;
		PropertyInfo[] props = properties;
		foreach (ExportTestRow item in rows)
		{
			for (int i = 0; i < props.Length; i++)
			{
				chars += (props[i].GetValue(item)?.ToString() ?? string.Empty).Length;
			}
		}
		return chars;
	}

	[Benchmark]
	public long Shared_CompiledGetters()
	{
		long chars = 0;
		Func<ExportTestRow, object?>[] getters = PoGetterFactory.Create<ExportTestRow>(properties);
		foreach (ExportTestRow item in rows)
		{
			for (int i = 0; i < getters.Length; i++)
			{
				chars += (getters[i](item)?.ToString() ?? string.Empty).Length;
			}
		}
		return chars;
	}
}

internal static class PoGetterFactory
{
	public static Func<T, object?>[] Create<T>(PropertyInfo[] properties)
	{
		Func<T, object?>[] getters = new Func<T, object?>[properties.Length];
		for (int i = 0; i < properties.Length; i++)
		{
			getters[i] = PoGetterCache<T>.Get(properties[i]);
		}
		return getters;
	}
}

/// <summary>Per-T compiled getter cache. Anything a compiled getter cannot reproduce exactly (indexers, write-only, non-public getters, by-ref-like) falls back to PropertyInfo.GetValue.</summary>
internal static class PoGetterCache<T>
{
	private static readonly ConcurrentDictionary<PropertyInfo, Func<T, object?>> Cache = new();

	public static Func<T, object?> Get(PropertyInfo property)
	{
		return Cache.GetOrAdd(property, static p => Build(p));
	}

	private static Func<T, object?> Build(PropertyInfo property)
	{
		Type propertyType = property.PropertyType;
		if ((property.GetGetMethod(nonPublic: false) != null) && (property.GetIndexParameters().Length == 0) && !propertyType.IsByRef && !propertyType.IsPointer && !propertyType.IsByRefLike &&
			(property.DeclaringType?.IsAssignableFrom(typeof(T)) == true))
		{
			try
			{
				ParameterExpression item = Expression.Parameter(typeof(T), "item");
				UnaryExpression body = Expression.Convert(Expression.Property(item, property), typeof(object));
				return Expression.Lambda<Func<T, object?>>(body, item).CompileFast();
			}
			catch (Exception)
			{
				// Fall through to reflection
			}
		}
		return item => property.GetValue(item);
	}
}

// ============================================================================================================================
// 5. OpenXml ExportFromTable<T> DOM row build (Export.cs:220-245, AppendSharedStringCell Export.cs:850-866)
// ============================================================================================================================

/// <summary>OpenXml ExportFromTable&lt;T&gt; rows: per-cell row-number ToString + concat, index ToString, GetValue and double.TryParse width vs hoisted/cached strings, compiled getters and a width upper bound.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoOpenXmlRowBuildBenchmarks
{
	private const uint HeaderStyleId = 1;
	private const uint BodyStyleId = 2;
	private List<ExportTestRow> rows = [];
	private PropertyInfo[] properties = [];

	[Params(500, 2000)]
	public int RowCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		rows = PoData.CreateExportRows(RowCount);
		properties = GetOrAddPropertiesFromReflectionCache(typeof(ExportTestRow));
		_ = PoGetterFactory.Create<ExportTestRow>(properties);
	}

	[Benchmark(Baseline = true)]
	public double[] Original()
	{
		return OriginalBuild(rows, properties, new SheetData(), new SharedStringTable(), HeaderStyleId, BodyStyleId);
	}

	[Benchmark]
	public double[] Shared_HoistedStrings()
	{
		return OptimizedBuild(rows, properties, new SheetData(), new SharedStringTable(), HeaderStyleId, BodyStyleId, fullOptimization: false);
	}

	[Benchmark]
	public double[] Shared_HoistedStringsGettersWidthBound()
	{
		return OptimizedBuild(rows, properties, new SheetData(), new SharedStringTable(), HeaderStyleId, BodyStyleId, fullOptimization: true);
	}

	// Verbatim header/data-row section of Export.ExportFromTable<T> (Export.cs ~212-245) with LoadSharedStringTable replaced by an empty table.
	internal static double[] OriginalBuild<T>(IEnumerable<T> data, PropertyInfo[] properties, SheetData sheetData, SharedStringTable sharedStringTable, uint headerStyleId, uint bodyStyleId)
	{
		int colCount = properties.Length;
		string[] colLetters = OriginalCreateColumnLetters(colCount);
		Dictionary<string, int> sharedStringCache = new(StringComparer.Ordinal);
		int ssCount = 0;
		double[] colWidths = new double[colCount];

		uint y = 1;

		Row headerRow = new() { RowIndex = y };
		for (int i = 0; i < colCount; i++)
		{
			string text = properties[i].Name;
			OriginalAppendSharedStringCell(headerRow, colLetters[i] + y, text, headerStyleId, sharedStringCache, sharedStringTable, ref ssCount, colWidths, i);
		}
		sheetData.AppendChild(headerRow);
		y++;

		foreach (T item in data.Where(x => !x.ToNString().IsNullOrEmpty()))
		{
			Row dataRow = new() { RowIndex = y };
			for (int i = 0; i < colCount; i++)
			{
				string text = properties[i].GetValue(item)?.ToString() ?? string.Empty;
				OriginalAppendSharedStringCell(dataRow, colLetters[i] + y, text, bodyStyleId, sharedStringCache, sharedStringTable, ref ssCount, colWidths, i);
			}
			sheetData.AppendChild(dataRow);
			y++;
		}
		return colWidths;
	}

	private static string[] OriginalCreateColumnLetters(int columnCount)
	{
		string[] colLetters = new string[columnCount];
		for (int i = 0; i < columnCount; i++)
		{
			colLetters[i] = CellReference.NumberToColumnName((uint)(i + 1));
		}
		return colLetters;
	}

	// Verbatim copy of Export.AppendSharedStringCell
	private static void OriginalAppendSharedStringCell(Row row, string cellReference, string text, uint styleId, Dictionary<string, int> sharedStringCache, SharedStringTable sharedStringTable,
		ref int ssCount, double[] colWidths, int colIndex)
	{
		int ssIdx = OriginalGetOrAddSharedString(text, sharedStringCache, sharedStringTable, ref ssCount);
		row.AppendChild(new Cell
		{
			CellReference = cellReference,
			StyleIndex = styleId,
			DataType = CellValues.SharedString,
			CellValue = new CellValue(ssIdx.ToString())
		});

		double w = CalculateWidth(text, styleId);
		if (w > colWidths[colIndex])
		{
			colWidths[colIndex] = w;
		}
	}

	// Verbatim copy of Export.GetOrAddSharedString
	private static int OriginalGetOrAddSharedString(string text, Dictionary<string, int> cache, SharedStringTable table, ref int count)
	{
		if (cache.TryGetValue(text, out int index))
		{
			return index;
		}
		table.AppendChild(new SharedStringItem((OpenXmlElement[])[new Text(text)]));
		cache[text] = count;
		return count++;
	}

	internal static double[] OptimizedBuild<T>(IEnumerable<T> data, PropertyInfo[] properties, SheetData sheetData, SharedStringTable sharedStringTable, uint headerStyleId, uint bodyStyleId,
		bool fullOptimization)
	{
		int colCount = properties.Length;
		string[] colLetters = OriginalCreateColumnLetters(colCount);

		// Maps shared text -> its index already rendered as the CellValue string, so repeated values reuse one string instead of int.ToString() per cell.
		// (When pre-loading an existing table the library would store (ssCount++).ToString(CultureInfo.InvariantCulture), preserving its last-wins assignment.)
		Dictionary<string, string> sharedStringCache = new(StringComparer.Ordinal);
		int ssCount = 0;
		double[] colWidths = new double[colCount];
		Func<T, object?>[]? getters = fullOptimization ? PoGetterFactory.Create<T>(properties) : null;
		PoWidthBound headerBound = new(headerStyleId);
		PoWidthBound bodyBound = new(bodyStyleId);

		uint y = 1;
		string rowText = y.ToString(CultureInfo.InvariantCulture);

		Row headerRow = new() { RowIndex = y };
		for (int i = 0; i < colCount; i++)
		{
			OptimizedAppendSharedStringCell(headerRow, colLetters[i] + rowText, properties[i].Name, headerStyleId, sharedStringCache, sharedStringTable, ref ssCount, colWidths, i, fullOptimization, headerBound);
		}
		sheetData.AppendChild(headerRow);
		y++;

		foreach (T item in data.Where(x => !x.ToNString().IsNullOrEmpty()))
		{
			// One row-number string per row instead of one per cell
			rowText = y.ToString(CultureInfo.InvariantCulture);
			Row dataRow = new() { RowIndex = y };
			for (int i = 0; i < colCount; i++)
			{
				object? value = (getters != null) ? getters[i](item) : properties[i].GetValue(item);
				string text = value?.ToString() ?? string.Empty;
				OptimizedAppendSharedStringCell(dataRow, colLetters[i] + rowText, text, bodyStyleId, sharedStringCache, sharedStringTable, ref ssCount, colWidths, i, fullOptimization, bodyBound);
			}
			sheetData.AppendChild(dataRow);
			y++;
		}
		return colWidths;
	}

	private static void OptimizedAppendSharedStringCell(Row row, string cellReference, string text, uint styleId, Dictionary<string, string> sharedStringCache, SharedStringTable sharedStringTable,
		ref int ssCount, double[] colWidths, int colIndex, bool useWidthBound, PoWidthBound bound)
	{
		if (!sharedStringCache.TryGetValue(text, out string? indexText))
		{
			sharedStringTable.AppendChild(new SharedStringItem((OpenXmlElement[])[new Text(text)]));
			indexText = ssCount.ToString(CultureInfo.InvariantCulture);
			sharedStringCache[text] = indexText;
			ssCount++;
		}

		row.AppendChild(new Cell
		{
			CellReference = cellReference,
			StyleIndex = styleId,
			DataType = CellValues.SharedString,
			CellValue = new CellValue(indexText)
		});

		// CalculateWidth is monotonic in text length and its numeric bonus is +1 char, so when even the "numeric" bound can't beat the current max the
		// culture-sensitive double.TryParse inside CalculateWidth can be skipped without changing the result.
		if (!useWidthBound || (bound.UpperBound(text.Length) > colWidths[colIndex]))
		{
			double w = CalculateWidth(text, styleId);
			if (w > colWidths[colIndex])
			{
				colWidths[colIndex] = w;
			}
		}
	}
}

/// <summary>Largest value Common.CalculateWidth(text, styleId) can return for a given text length (i.e. assuming double.TryParse succeeds).</summary>
internal readonly struct PoWidthBound
{
	// Mirrors Common.NumberStyleIds / Common.BoldStyleIds
	private static readonly HashSet<uint> NumberStyleIds = [5, 6, 7, 8];
	private static readonly HashSet<uint> BoldStyleIds = [1, 2, 3, 4, 6, 7, 8];

	private readonly bool numberStyle;
	private readonly bool boldStyle;

	public PoWidthBound(uint styleId)
	{
		numberStyle = NumberStyleIds.Contains(styleId);
		boldStyle = BoldStyleIds.Contains(styleId);
	}

	public double UpperBound(int textLength)
	{
		if (textLength == 0)
		{
			return 0;
		}

		const int padding = 1;
		double width = textLength + padding + 1; // +1 = numeric bonus
		if (numberStyle)
		{
			int thousandCount = (int)Math.Truncate(width / 4);
			width += 3 + thousandCount;
		}

		if (boldStyle)
		{
			width++;
		}

		const double maxCharWidth = 5;
		return Math.Truncate(((width * maxCharWidth) + 5) / maxCharWidth * 256) / 256;
	}
}

// ============================================================================================================================
// 6. NPOI ExcelExport<T> per-cell loop (Npoi/Export.cs:292-320)
// ============================================================================================================================

/// <summary>NPOI ExcelExport&lt;T&gt; cell loop (NPOI calls replaced by a sink): GetValue + Dictionary&lt;int,int&gt; width tracking vs compiled getters + int[] widths.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoNpoiExportLoopBenchmarks
{
	private List<ExportTestRow> rows = [];
	private PropertyInfo[] properties = [];

	[GlobalSetup]
	public void Setup()
	{
		rows = PoData.CreateExportRows(2000);
		properties = GetOrAddPropertiesFromReflectionCache(typeof(ExportTestRow));
		_ = PoGetterFactory.Create<ExportTestRow>(properties);
	}

	[Benchmark(Baseline = true)]
	public int[] Original()
	{
		return OriginalLoop(rows, properties, null);
	}

	[Benchmark]
	public int[] Shared_GettersAndArrayWidths()
	{
		return OptimizedLoop(rows, properties, null);
	}

	// Verbatim width/value logic of Npoi Export.ExcelExport<T>; GetCell/CreateCell/SetCellValue/CellStyle replaced by the sink.
	internal static int[] OriginalLoop<T>(IEnumerable<T> data, PropertyInfo[] props, List<string?>? sink)
	{
		int x = 0;
		Dictionary<int, int> maxColumnWidths = [];

		foreach (string propName in props.Select(p => p.Name))
		{
			maxColumnWidths[x] = (propName.Length + 6) * 256;
			x++;
		}
		x = 0;

		foreach (T item in data)
		{
			if (item.ToNString().IsNullOrEmpty())
			{
				continue;
			}

			foreach (PropertyInfo prop in props)
			{
				object value = prop.GetValue(item) ?? string.Empty;

				string? valueString = value.ToString();
				sink?.Add(valueString); // c.SetCellValue(valueString); c.CellStyle = bodyStyle;
				int newVal = (valueString?.Length ?? (1 + 6)) * 256;
				if (maxColumnWidths[x] < newVal)
				{
					maxColumnWidths[x] = newVal;
				}
				x++;
			}
			x = 0;
		}

		int[] widths = new int[props.Length];
		for (int i = 0; i < props.Length; i++)
		{
			widths[i] = maxColumnWidths[i];
		}
		return widths;
	}

	internal static int[] OptimizedLoop<T>(IEnumerable<T> data, PropertyInfo[] props, List<string?>? sink)
	{
		int colCount = props.Length;
		int[] maxColumnWidths = new int[colCount];
		for (int i = 0; i < colCount; i++)
		{
			maxColumnWidths[i] = (props[i].Name.Length + 6) * 256;
		}

		Func<T, object?>[] getters = PoGetterFactory.Create<T>(props);
		foreach (T item in data)
		{
			if (item.ToNString().IsNullOrEmpty())
			{
				continue;
			}

			for (int i = 0; i < colCount; i++)
			{
				object value = getters[i](item) ?? string.Empty;
				string? valueString = value.ToString();
				sink?.Add(valueString);
				int newVal = (valueString?.Length ?? (1 + 6)) * 256;
				if (maxColumnWidths[i] < newVal)
				{
					maxColumnWidths[i] = newVal;
				}
			}
		}
		return maxColumnWidths;
	}
}

// ============================================================================================================================
// 7. DataTable exporters: row.ItemArray + HashSet<int> skip lookups per cell
// ============================================================================================================================

/// <summary>DataTable exporters (OpenXml DOM/SAX, NPOI, ClosedXML): ItemArray copy per row + HashSet skip lookup per cell vs indexed row[i] + bool[] skip mask.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoDataTableIterationBenchmarks
{
	private static readonly List<string> SkipColumnNames = ["field05", "FIELD12"];
	private DataTable table = null!;

	[GlobalSetup]
	public void Setup()
	{
		table = PoData.CreateDataTable(PoData.CreateExportRows(2000));
	}

	[Benchmark(Baseline = true)]
	public long Original()
	{
		return OriginalIterate(table, SkipColumnNames, null);
	}

	[Benchmark]
	public long Shared_IndexerAndSkipMask()
	{
		return OptimizedIterate(table, SkipColumnNames, null);
	}

	// Verbatim skip-set construction + row loop of OpenXml Export.ExportFromTable(DataTable) (Export.cs:298-341), AppendSharedStringCell replaced by the sink.
	internal static long OriginalIterate(DataTable data, List<string>? skipColumnNames, List<string>? sink)
	{
		int totalCols = data.Columns.Count;
		HashSet<int> skipColumnIndices = [];
		for (int i = 0; i < totalCols; i++)
		{
			if (skipColumnNames?.Contains(data.Columns[i].ColumnName, StringComparer.InvariantCultureIgnoreCase) == true)
			{
				skipColumnIndices.Add(i);
			}
		}

		long chars = 0;
		foreach (DataRow row in data.Rows)
		{
			object?[] items = row.ItemArray;
			for (int i = 0; i < items.Length; i++)
			{
				if (items[i] == null || skipColumnIndices.Contains(i))
				{
					continue;
				}
				string text = items[i]!.ToString() ?? string.Empty;
				chars += text.Length;
				sink?.Add(text);
			}
		}
		return chars;
	}

	internal static long OptimizedIterate(DataTable data, List<string>? skipColumnNames, List<string>? sink)
	{
		int totalCols = data.Columns.Count;
		bool[] skip = new bool[totalCols];
		for (int i = 0; i < totalCols; i++)
		{
			skip[i] = skipColumnNames?.Contains(data.Columns[i].ColumnName, StringComparer.InvariantCultureIgnoreCase) == true;
		}

		long chars = 0;
		foreach (DataRow row in data.Rows)
		{
			for (int i = 0; i < totalCols; i++)
			{
				if (skip[i])
				{
					continue;
				}

				object value = row[i]; // Same default-version read ItemArray performs, without copying the whole row
				if (value == null)
				{
					continue;
				}
				string text = value.ToString() ?? string.Empty;
				chars += text.Length;
				sink?.Add(text);
			}
		}
		return chars;
	}
}

// ============================================================================================================================
// 8. OpenXml ReadExcelFileToEnumerable<T> -> SetExcelPropertyValue per cell (Common.cs:3530, 3696)
// ============================================================================================================================

/// <summary>OpenXml ReadExcelFileToEnumerable&lt;T&gt;: per-cell Dictionary column lookup + 2x Nullable.GetUnderlyingType + ConcurrentDictionary setter lookup vs per-column pre-resolved binders.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoExcelReadSetterBenchmarks
{
	internal static readonly ConcurrentDictionary<PropertyInfo, Action<object, object?>> SetterCache = new();

	private string[][] cellRows = [];
	private Dictionary<uint, PropertyInfo> columnPropertyMap = [];

	[GlobalSetup]
	public void Setup()
	{
		columnPropertyMap = CreateColumnMap(out PropertyInfo[] ordered);
		cellRows = CreateCells(2000, ordered);
		_ = OriginalLoop(cellRows, columnPropertyMap); // Warm the setter cache (one-time cost in the library)
	}

	[Benchmark(Baseline = true)]
	public List<PoReadRow> Original()
	{
		return OriginalLoop(cellRows, columnPropertyMap);
	}

	[Benchmark]
	public List<PoReadRow> Shared_ColumnBinders()
	{
		return OptimizedLoop(cellRows, columnPropertyMap);
	}

	internal static Dictionary<uint, PropertyInfo> CreateColumnMap(out PropertyInfo[] ordered)
	{
		ordered = typeof(PoReadRow).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite).ToArray();
		Dictionary<uint, PropertyInfo> map = [];
		for (int i = 0; i < ordered.Length; i++)
		{
			map[(uint)(i + 1)] = ordered[i];
		}
		return map;
	}

	internal static string[][] CreateCells(int count, PropertyInfo[] ordered)
	{
		string[][] result = new string[count][];
		for (int r = 0; r < count; r++)
		{
			string[] values = new string[ordered.Length];
			for (int c = 0; c < ordered.Length; c++)
			{
				values[c] = ordered[c].Name switch
				{
					nameof(PoReadRow.Id) => (r + 1).ToString(CultureInfo.InvariantCulture),
					nameof(PoReadRow.OptionalInt) => ((r % 5) == 0) ? "n/a" : (((r % 7) == 0) ? " " : (r * 3).ToString(CultureInfo.InvariantCulture)),
					nameof(PoReadRow.Name) => $"Name {r}",
					nameof(PoReadRow.Amount) => (r * 1.25m).ToString(CultureInfo.InvariantCulture),
					nameof(PoReadRow.Ratio) => (r / 8d).ToString(CultureInfo.InvariantCulture),
					nameof(PoReadRow.IsActive) => (r % 3) switch { 0 => "1", 1 => "true", _ => "FALSE" },
					nameof(PoReadRow.Created) => PoData.BaseDate.AddDays(r).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
					nameof(PoReadRow.Updated) => ((r % 4) == 0) ? "not a date" : PoData.BaseDate.AddHours(r).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
					nameof(PoReadRow.Key) => new Guid(r, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10).ToString(),
					nameof(PoReadRow.Status) => (r % 3) switch { 0 => "active", 1 => "Inactive", _ => "bogus" },
					nameof(PoReadRow.Big) => (r * 1_000_003L).ToString(CultureInfo.InvariantCulture),
					_ => $"Note {r % 17}"
				};
			}
			result[r] = values;
		}
		return result;
	}

	// Per-cell section of Common.ReadExcelFileToEnumerable<T> (Common.cs ~3680-3705) over pre-extracted cell text.
	internal static List<PoReadRow> OriginalLoop(string[][] cellRows, Dictionary<uint, PropertyInfo> columnPropertyMap)
	{
		List<PoReadRow> results = new(cellRows.Length);
		foreach (string[] values in cellRows)
		{
			PoReadRow item = new();
			bool rowHasData = false;
			for (int c = 0; c < values.Length; c++)
			{
				uint columnIndex = (uint)(c + 1);
				if (!columnPropertyMap.TryGetValue(columnIndex, out PropertyInfo? prop))
				{
					continue;
				}

				string cellValue = values[c];
				if (!string.IsNullOrWhiteSpace(cellValue))
				{
					rowHasData = true;
					OriginalSetExcelPropertyValue(item, prop, cellValue);
				}
			}
			if (rowHasData)
			{
				results.Add(item);
			}
		}
		return results;
	}

	internal static List<PoReadRow> OptimizedLoop(string[][] cellRows, Dictionary<uint, PropertyInfo> columnPropertyMap)
	{
		// Built once per read when the header row has been mapped; index = columnIndex - startColumn
		uint maxColumn = 0;
		foreach (uint key in columnPropertyMap.Keys)
		{
			if (key > maxColumn)
			{
				maxColumn = key;
			}
		}
		PoColumnBinder?[] binders = new PoColumnBinder?[maxColumn];
		foreach (KeyValuePair<uint, PropertyInfo> kvp in columnPropertyMap)
		{
			binders[kvp.Key - 1] = new PoColumnBinder(kvp.Value);
		}

		List<PoReadRow> results = new(cellRows.Length);
		foreach (string[] values in cellRows)
		{
			PoReadRow item = new();
			bool rowHasData = false;
			for (int c = 0; c < values.Length; c++)
			{
				PoColumnBinder? binder = (c < binders.Length) ? binders[c] : null;
				if (binder == null)
				{
					continue;
				}

				string cellValue = values[c];
				if (!string.IsNullOrWhiteSpace(cellValue))
				{
					rowHasData = true;
					binder.Set(item, cellValue);
				}
			}
			if (rowHasData)
			{
				results.Add(item);
			}
		}
		return results;
	}

	// Verbatim copy of Common.BuildSetter
	internal static Action<object, object?> BuildSetter(PropertyInfo property)
	{
		ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
		ParameterExpression valueParam = Expression.Parameter(typeof(object), "value");
		BinaryExpression assign = Expression.Assign(Expression.Property(Expression.Convert(objParam, property.DeclaringType!), property), Expression.Convert(valueParam, property.PropertyType));
		return Expression.Lambda<Action<object, object?>>(assign, objParam, valueParam).CompileFast();
	}

	// Verbatim copy of Common.SetExcelPropertyValue
	private static void OriginalSetExcelPropertyValue<T>(T obj, PropertyInfo property, string value)
	{
		Type targetType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
		try
		{
			object? result = ConvertExcelCellValue(value, targetType);
			if (result != null || Nullable.GetUnderlyingType(property.PropertyType) != null)
			{
				SetterCache.GetOrAdd(property, BuildSetter)(obj!, result);
			}
		}
		catch
		{
			// Non-convertible values are silently skipped; the property retains its default value
		}
	}

	// Verbatim copy of Common.ConvertExcelCellValue (unchanged by the proposal)
	internal static object? ConvertExcelCellValue(string value, Type targetType)
	{
		if (targetType.IsEnum)
		{
			return Enum.TryParse(targetType, value, ignoreCase: true, out object? val) ? val : null;
		}

		return Type.GetTypeCode(targetType) switch
		{
			TypeCode.String => value,
			TypeCode.Boolean => bool.TryParse(value, out bool val) ? val : value == "1",
			TypeCode.Byte => byte.TryParse(value, out byte val) ? val : null,
			TypeCode.Int16 => short.TryParse(value, out short val) ? val : null,
			TypeCode.Int32 => int.TryParse(value, out int val) ? val : null,
			TypeCode.Int64 => long.TryParse(value, out long val) ? val : null,
			TypeCode.Single => float.TryParse(value, out float val) ? val : null,
			TypeCode.Double => double.TryParse(value, out double val) ? val : null,
			TypeCode.Decimal => decimal.TryParse(value, out decimal val) ? val : null,
			TypeCode.DateTime => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime val) ? val : null,
			_ when targetType == typeof(DateOnly) => DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly val) ? val : null,
			_ when targetType == typeof(TimeOnly) => TimeOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly val) ? val : null,
			_ when targetType == typeof(DateTimeOffset) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset val) ? val : null,
			_ when targetType == typeof(TimeSpan) => TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan val) ? val : null,
			_ when targetType == typeof(Guid) => Guid.TryParse(value, out Guid val) ? val : null,
			_ => Convert.ChangeType(value, targetType)
		};
	}
}

/// <summary>Per-column pre-resolved conversion target + lazily resolved setter; Set mirrors SetExcelPropertyValue exactly (setter build/convert failures are swallowed).</summary>
internal sealed class PoColumnBinder
{
	private readonly PropertyInfo property;
	private readonly Type targetType;
	private readonly bool isNullable;
	private Action<object, object?>? setter;

	public PoColumnBinder(PropertyInfo property)
	{
		this.property = property;
		Type? underlying = Nullable.GetUnderlyingType(property.PropertyType);
		isNullable = underlying != null;
		targetType = underlying ?? property.PropertyType;
	}

	public void Set(object obj, string value)
	{
		try
		{
			object? result = PoExcelReadSetterBenchmarks.ConvertExcelCellValue(value, targetType);
			if ((result != null) || isNullable)
			{
				// Resolved inside the try (like the original) so a setter that cannot be built is still silently skipped
				setter ??= PoExcelReadSetterBenchmarks.SetterCache.GetOrAdd(property, PoExcelReadSetterBenchmarks.BuildSetter);
				setter(obj, result);
			}
		}
		catch
		{
			// Non-convertible values are silently skipped; the property retains its default value
		}
	}
}

public enum PoReadStatus
{
	Unknown,
	Active,
	Inactive
}

public sealed class PoReadRow
{
	public int Id { get; set; }
	public int? OptionalInt { get; set; }
	public string? Name { get; set; }
	public decimal Amount { get; set; }
	public double Ratio { get; set; }
	public bool IsActive { get; set; }
	public DateTime Created { get; set; }
	public DateTime? Updated { get; set; }
	public Guid Key { get; set; }
	public PoReadStatus Status { get; set; }
	public long Big { get; set; }
	public string? Notes { get; set; }
}

// ============================================================================================================================
// 9. Word ChangeUrlsInWordDoc(Stream, Dictionary) per-hyperlink lookup (ChangeUrls.cs:74)
// ============================================================================================================================

/// <summary>Word ChangeUrlsInWordDoc(dictionary): per-hyperlink ZLinq scan + closure + Uri.ToString per entry vs hoisted loop / InvariantCultureIgnoreCase dictionary.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoWordUrlLookupBenchmarks
{
	private const int HyperlinkCount = 500;
	private Uri[] hyperlinks = [];
	private Dictionary<string, string> urlsToUpdate = [];

	[Params(5, 50)]
	public int DictionarySize { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		hyperlinks = new Uri[HyperlinkCount];
		for (int i = 0; i < HyperlinkCount; i++)
		{
			hyperlinks[i] = new Uri($"https://www.example.com/docs/page{i % (DictionarySize * 2)}");
		}

		urlsToUpdate = [];
		for (int k = 0; k < DictionarySize; k++)
		{
			string key = ((k % 2) == 0) ? $"https://www.example.com/docs/page{k}" : $"HTTPS://WWW.EXAMPLE.COM/docs/PAGE{k}";
			urlsToUpdate[key] = $"https://new.example.com/docs/page{k}";
		}
	}

	[Benchmark(Baseline = true)]
	public string?[] Original()
	{
		return OriginalLookup(hyperlinks, urlsToUpdate);
	}

	[Benchmark]
	public string?[] Shared_HoistedLoop()
	{
		return HoistedLookup(hyperlinks, urlsToUpdate);
	}

	[Benchmark]
	public string?[] Shared_ComparerDictionary()
	{
		return DictionaryLookup(hyperlinks, urlsToUpdate);
	}

	// Lookup expression copied verbatim from ChangeUrls.ChangeUrlsInWordDoc(Stream, Dictionary<string, string>)
	internal static string?[] OriginalLookup(Uri[] hyperlinks, Dictionary<string, string> urlsToUpdate)
	{
		string?[] results = new string?[hyperlinks.Length];
		for (int h = 0; h < hyperlinks.Length; h++)
		{
			Uri hyperlinkUri = hyperlinks[h];
			string? newUrl = urlsToUpdate.AsValueEnumerable().Where(x => string.Equals(x.Key, hyperlinkUri.ToString(), StringComparison.InvariantCultureIgnoreCase)).Select(x => x.Value).FirstOrDefault();
			results[h] = newUrl;
		}
		return results;
	}

	// Best for small dictionaries: no closure/enumerator allocation, Uri.ToString evaluated once per hyperlink
	internal static string?[] HoistedLookup(Uri[] hyperlinks, Dictionary<string, string> urlsToUpdate)
	{
		string?[] results = new string?[hyperlinks.Length];
		for (int h = 0; h < hyperlinks.Length; h++)
		{
			string uriText = hyperlinks[h].ToString();
			string? newUrl = null;
			foreach (KeyValuePair<string, string> kvp in urlsToUpdate)
			{
				if (string.Equals(kvp.Key, uriText, StringComparison.InvariantCultureIgnoreCase))
				{
					newUrl = kvp.Value;
					break;
				}
			}
			results[h] = newUrl;
		}
		return results;
	}

	// O(1) per hyperlink; TryAdd keeps the first key in enumeration order, matching FirstOrDefault when keys only differ by case
	internal static string?[] DictionaryLookup(Uri[] hyperlinks, Dictionary<string, string> urlsToUpdate)
	{
		Dictionary<string, string> lookup = new(urlsToUpdate.Count, StringComparer.InvariantCultureIgnoreCase);
		foreach (KeyValuePair<string, string> kvp in urlsToUpdate)
		{
			lookup.TryAdd(kvp.Key, kvp.Value);
		}

		string?[] results = new string?[hyperlinks.Length];
		for (int h = 0; h < hyperlinks.Length; h++)
		{
			results[h] = lookup.TryGetValue(hyperlinks[h].ToString(), out string? newUrl) ? newUrl : null;
		}
		return results;
	}
}

// ============================================================================================================================
// 10. Word ChangeUrlsInWordDocRegex: static Regex.Replace per match (ChangeUrls.cs:121, 170)
// ============================================================================================================================

/// <summary>Word ChangeUrlsInWordDocRegex: static Regex.Replace (Regex cache lookup per match) next to an already-built instance vs regex.Replace on that instance.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoWordRegexReplaceBenchmarks
{
	internal const string Pattern = @"https?://old\.example\.com";
	internal const string Replacement = "https://new.example.com";
	private string[] uris = [];

	[GlobalSetup]
	public void Setup()
	{
		uris = new string[500];
		for (int i = 0; i < uris.Length; i++)
		{
			uris[i] = ((i % 2) == 0) ? $"https://OLD.example.com/path/{i}?q={i}" : $"https://other.example.org/path/{i}";
		}
	}

	[Benchmark(Baseline = true)]
	public string?[] Original()
	{
		return OriginalReplace(uris, Pattern, Replacement, RegexOptions.IgnoreCase, null);
	}

	[Benchmark]
	public string?[] Shared_InstanceReplace()
	{
		return OptimizedReplace(uris, Pattern, Replacement, RegexOptions.IgnoreCase, null);
	}

	// Per-hyperlink logic of ChangeUrls.ChangeUrlsInWordDocRegex(Stream, string, string, ...) with the relationship swap replaced by the result array
	internal static string?[] OriginalReplace(string[] uris, string regexPattern, string replacementText, RegexOptions regexOptions, TimeSpan? regexTimeout)
	{
		regexTimeout ??= Regex.InfiniteMatchTimeout;
		Regex regex = new(regexPattern, regexOptions, (TimeSpan)regexTimeout);
		string?[] results = new string?[uris.Length];
		for (int i = 0; i < uris.Length; i++)
		{
			string currentUri = uris[i];
			if (regex.IsMatch(currentUri))
			{
				results[i] = Regex.Replace(currentUri, regexPattern, replacementText, regexOptions, (TimeSpan)regexTimeout);
			}
		}
		return results;
	}

	internal static string?[] OptimizedReplace(string[] uris, string regexPattern, string replacementText, RegexOptions regexOptions, TimeSpan? regexTimeout)
	{
		regexTimeout ??= Regex.InfiniteMatchTimeout;
		Regex regex = new(regexPattern, regexOptions, (TimeSpan)regexTimeout);
		string?[] results = new string?[uris.Length];
		for (int i = 0; i < uris.Length; i++)
		{
			string currentUri = uris[i];
			if (regex.IsMatch(currentUri))
			{
				// Same pattern/options/timeout (and culture, captured at construction in the same call) as the static overload
				results[i] = regex.Replace(currentUri, replacementText);
			}
		}
		return results;
	}
}

// ============================================================================================================================
// 11. OpenXml SAX exports: WriteSaxInlineStringCell allocates Cell/InlineString/Text + row-number string per cell
// ============================================================================================================================

/// <summary>OpenXml SAX export cells: new Cell/InlineString/Text + "col"+y.ToString() per cell vs reused element instances and one row-number string per row.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PoSaxCellWriteBenchmarks
{
	private const uint BodyStyleId = 2;
	private object?[][] rows = [];
	private string[] colLetters = [];

	[GlobalSetup]
	public void Setup()
	{
		DataTable table = PoData.CreateDataTable(PoData.CreateExportRows(1000));
		rows = table.Rows.Cast<DataRow>().Select(r => r.ItemArray).ToArray();
		colLetters = new string[table.Columns.Count];
		for (int i = 0; i < colLetters.Length; i++)
		{
			colLetters[i] = CellReference.NumberToColumnName((uint)(i + 1));
		}
	}

	[Benchmark(Baseline = true)]
	public long Original()
	{
		PoSinkStream sink = new(captureBytes: false);
		OriginalWrite(sink, rows, colLetters, BodyStyleId);
		return sink.BytesWritten;
	}

	[Benchmark]
	public long Shared_ReusedElements()
	{
		PoSinkStream sink = new(captureBytes: false);
		OptimizedWrite(sink, rows, colLetters, BodyStyleId);
		return sink.BytesWritten;
	}

	// Data-row section of Export.ExportFromTableSaxAsync(DataTable) (Export.cs:530-548) + verbatim WriteSaxInlineStringCell (Export.cs:798)
	internal static void OriginalWrite(Stream output, object?[][] rows, string[] colLetters, uint bodyStyleId)
	{
		using OpenXmlWriter writer = OpenXmlWriter.Create(output);
		writer.WriteStartElement(new Worksheet());
		writer.WriteStartElement(new SheetData());
		uint y = 2;
		foreach (object?[] items in rows)
		{
			writer.WriteStartElement(new Row { RowIndex = y });
			for (int i = 0; i < items.Length; i++)
			{
				OriginalWriteSaxInlineStringCell(writer, colLetters[i] + y, items[i]?.ToString() ?? string.Empty, bodyStyleId);
			}
			writer.WriteEndElement(); // Row
			y++;
		}
		writer.WriteEndElement(); // SheetData
		writer.WriteEndElement(); // Worksheet
	}

	private static void OriginalWriteSaxInlineStringCell(OpenXmlWriter writer, string cellRef, string text, uint styleId)
	{
		writer.WriteStartElement(new Cell { CellReference = cellRef, StyleIndex = styleId, DataType = CellValues.InlineString });
		writer.WriteStartElement(new InlineString());
		writer.WriteElement(new Text(text));
		writer.WriteEndElement(); // InlineString
		writer.WriteEndElement(); // Cell
	}

	internal static void OptimizedWrite(Stream output, object?[][] rows, string[] colLetters, uint bodyStyleId)
	{
		using OpenXmlWriter writer = OpenXmlWriter.Create(output);
		writer.WriteStartElement(new Worksheet());
		writer.WriteStartElement(new SheetData());

		// The SAX writer serializes tag + attributes (or the whole leaf element) immediately and keeps no reference, so one instance per element type can be reused
		Row rowElement = new();
		Cell cellElement = new() { StyleIndex = bodyStyleId, DataType = CellValues.InlineString };
		InlineString inlineStringElement = new();
		Text textElement = new();

		uint y = 2;
		foreach (object?[] items in rows)
		{
			rowElement.RowIndex = y;
			writer.WriteStartElement(rowElement);
			string rowText = y.ToString(CultureInfo.InvariantCulture);
			for (int i = 0; i < items.Length; i++)
			{
				cellElement.CellReference = colLetters[i] + rowText;
				textElement.Text = items[i]?.ToString() ?? string.Empty;
				writer.WriteStartElement(cellElement);
				writer.WriteStartElement(inlineStringElement);
				writer.WriteElement(textElement);
				writer.WriteEndElement(); // InlineString
				writer.WriteEndElement(); // Cell
			}
			writer.WriteEndElement(); // Row
			y++;
		}
		writer.WriteEndElement(); // SheetData
		writer.WriteEndElement(); // Worksheet
	}
}

/// <summary>Write-only stream that counts bytes (and optionally captures them for verification) so the SAX benchmark measures serialization, not zip/MemoryStream growth.</summary>
internal sealed class PoSinkStream(bool captureBytes) : Stream
{
	private readonly MemoryStream? capture = captureBytes ? new MemoryStream() : null;

	public long BytesWritten { get; private set; }

	public byte[] CapturedBytes => capture?.ToArray() ?? [];

	public override bool CanRead => false;

	public override bool CanSeek => false;

	public override bool CanWrite => true;

	public override long Length => BytesWritten;

	public override long Position
	{
		get => BytesWritten;
		set => throw new NotSupportedException();
	}

	public override void Flush()
	{
	}

	public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

	public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

	public override void SetLength(long value) => throw new NotSupportedException();

	public override void Write(byte[] buffer, int offset, int count)
	{
		BytesWritten += count;
		capture?.Write(buffer, offset, count);
	}

	public override void Write(ReadOnlySpan<byte> buffer)
	{
		BytesWritten += buffer.Length;
		capture?.Write(buffer);
	}

	protected override void Dispose(bool disposing)
	{
		// Intentionally keeps the captured bytes readable after the XML writer closes the stream
	}
}

// ============================================================================================================================
// Shared data + getter edge-case types
// ============================================================================================================================

internal static class PoData
{
	public static readonly DateTime BaseDate = new(2026, 1, 1, 8, 30, 0);

	public static List<ExportTestRow> CreateExportRows(int count)
	{
		System.Random rng = new(42);
		string[] statuses = ["Active", "Inactive", "Pending", "Closed"];
		string[] categories = ["Alpha", "Beta", "Gamma", "Delta", "Epsilon"];
		List<ExportTestRow> rows = new(count);
		for (int i = 1; i <= count; i++)
		{
			rows.Add(new ExportTestRow
			{
				Id = i,
				FirstName = $"First{i}",
				LastName = ((i % 11) == 0) ? null : $"Last{i}",
				Email = $"user{i}@example.com",
				Phone = $"555-{i % 10000:D4}",
				Status = statuses[i % statuses.Length],
				Category = categories[i % categories.Length],
				SubCategory = ((i % 13) == 0) ? string.Empty : $"Sub{i % 10}",
				Amount = Math.Round((decimal)(rng.NextDouble() * 10000), 2),
				Price = Math.Round((decimal)(rng.NextDouble() * 500), 2),
				Cost = Math.Round((decimal)(rng.NextDouble() * 200), 2),
				Total = Math.Round((decimal)(rng.NextDouble() * 12000), 2),
				Quantity = rng.Next(1, 100),
				Count = rng.Next(0, 1000),
				IsActive = (i % 2) == 0,
				IsEnabled = (i % 3) != 0,
				CreatedDate = BaseDate.AddDays(-i),
				UpdatedDate = BaseDate.AddHours(-i * 7),
				ProcessedDate = BaseDate.AddMinutes(-i * 13),
				Field01 = $"F01_{i}",
				Field02 = (i * 37).ToString(CultureInfo.InvariantCulture),
				Field03 = $"F03_{i % 50}",
				Field04 = $"F04_{i % 7}",
				Field05 = $"F05_{i}",
				Field06 = ((i % 5) == 0) ? null : $"F06_{i}",
				Field07 = $"F07_{i % 100}",
				Field08 = $"F08_{i}",
				Field09 = (i * 1.5).ToString(CultureInfo.InvariantCulture),
				Field10 = $"F10_{i % 3}",
				Field11 = $"F11_{i}",
				Field12 = $"Some longer descriptive text value number {i}",
				Field13 = $"F13_{i % 20}",
				Field14 = $"F14_{i}",
				Field15 = $"F15_{i % 2}",
				Field16 = $"F16_{i}",
				Field17 = ((i % 7) == 0) ? null : $"F17_{i}"
			});
		}
		return rows;
	}

	public static DataTable CreateDataTable(IEnumerable<ExportTestRow> rows)
	{
		DataTable table = new();
		PropertyInfo[] props = typeof(ExportTestRow).GetProperties(BindingFlags.Public | BindingFlags.Instance);
		foreach (PropertyInfo p in props)
		{
			table.Columns.Add(p.Name, Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType);
		}

		foreach (ExportTestRow row in rows)
		{
			object[] values = new object[props.Length];
			for (int i = 0; i < props.Length; i++)
			{
				values[i] = props[i].GetValue(row) ?? DBNull.Value;
			}
			table.Rows.Add(values);
		}
		return table;
	}

	/// <summary>Dense sheet (header row + dataRows) mixing shared-string and numeric cells.</summary>
	public static Worksheet CreateWorksheet(int dataRows, int columns, out Dictionary<int, string> sharedStrings)
	{
		Dictionary<string, int> lookup = new(StringComparer.Ordinal);
		Dictionary<int, string> index = [];
		SheetData sheetData = new();
		for (uint r = 1; r <= (uint)dataRows + 1; r++)
		{
			Row row = new() { RowIndex = r };
			for (uint c = 1; c <= (uint)columns; c++)
			{
				string reference = CellReference.NumberToColumnName(c) + r.ToString(CultureInfo.InvariantCulture);
				if ((r > 1) && ((c % 4) == 0))
				{
					row.AppendChild(new Cell { CellReference = reference, CellValue = new CellValue(((r * c) % 9973).ToString(CultureInfo.InvariantCulture)) });
				}
				else
				{
					string text = (r == 1) ? $"Col{c}" : $"V{((r * 31) + c) % 500}";
					if (!lookup.TryGetValue(text, out int ssIdx))
					{
						ssIdx = lookup.Count;
						lookup[text] = ssIdx;
						index[ssIdx] = text;
					}
					row.AppendChild(new Cell { CellReference = reference, DataType = CellValues.SharedString, CellValue = new CellValue(ssIdx.ToString(CultureInfo.InvariantCulture)) });
				}
			}
			sheetData.AppendChild(row);
		}
		sharedStrings = index;
		return new Worksheet(sheetData);
	}
}

public class PoGetterEdge
{
	public int Plain { get; set; } = 7;

	public string? NullString { get; set; }

	public int? NullableValue { get; set; }

	public int PrivateGetter { private get; set; } = 3;

	public int WriteOnly
	{
		set => Plain = value;
	}

	public string this[int index] => index.ToString(CultureInfo.InvariantCulture);

	public int Throws => throw new InvalidOperationException("getter failure");
}

public struct PoGetterStruct
{
	public int X { get; set; }

	public string? Y { get; set; }
}

public class PoGetterBase
{
	public virtual string Name { get; set; } = "base";

	public int BaseOnly { get; set; } = 1;
}

public class PoGetterDerived : PoGetterBase
{
	public override string Name
	{
		get => "derived:" + base.Name;
		set => base.Name = value;
	}

	public int Extra { get; set; } = 2;
}

public interface IPoGetterContract
{
	int ContractValue { get; }
}

public sealed class PoGetterContractImpl : IPoGetterContract
{
	int IPoGetterContract.ContractValue => 42;
}

// ============================================================================================================================
// Equivalence checks
// ============================================================================================================================

/// <summary>Asserts every Original/Optimized pair above produces identical output (including exception behavior) on edge-case inputs.</summary>
public static class PoVerify
{
	public static void Run()
	{
		VerifySheetRead();
		VerifyTableRead();
		VerifyCellReferenceParse();
		VerifyGetters();
		VerifyRowBuild();
		VerifyNpoiLoop();
		VerifyDataTableIteration();
		VerifyReadSetter();
		VerifyWordLookup();
		VerifyWordRegex();
		VerifySaxWrite();
	}

	private static void Check(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException($"PoVerify mismatch: {message}");
		}
	}

	private static string Outcome(Func<object?> func)
	{
		try
		{
			object? value = func();
			return "V:" + (value?.ToString() ?? "<null>");
		}
		catch (Exception ex)
		{
			// Compiled getters throw the getter's exception directly where PropertyInfo.GetValue wraps it (callers catch Exception either way)
			Exception root = (ex is TargetInvocationException { InnerException: { } inner }) ? inner : ex;
			return "E:" + root.GetType().FullName;
		}
	}

	private static void AssertTablesEqual(DataTable expected, DataTable actual, string name)
	{
		Check(expected.Columns.Count == actual.Columns.Count, $"{name}: column count {expected.Columns.Count} vs {actual.Columns.Count}");
		for (int c = 0; c < expected.Columns.Count; c++)
		{
			Check(string.Equals(expected.Columns[c].ColumnName, actual.Columns[c].ColumnName, StringComparison.Ordinal), $"{name}: column {c} name");
		}

		Check(expected.Rows.Count == actual.Rows.Count, $"{name}: row count {expected.Rows.Count} vs {actual.Rows.Count}");
		for (int r = 0; r < expected.Rows.Count; r++)
		{
			for (int c = 0; c < expected.Columns.Count; c++)
			{
				Check(Equals(expected.Rows[r][c], actual.Rows[r][c]), $"{name}: value at row {r}, column {c} ('{expected.Rows[r][c]}' vs '{actual.Rows[r][c]}')");
			}
		}
	}

	private static Cell SharedCell(string reference, int index)
	{
		return new Cell { CellReference = reference, DataType = CellValues.SharedString, CellValue = new CellValue(index.ToString(CultureInfo.InvariantCulture)) };
	}

	private static Cell ValueCell(string? reference, string value)
	{
		Cell cell = new() { CellValue = new CellValue(value) };
		if (reference != null)
		{
			cell.CellReference = reference;
		}
		return cell;
	}

	private static Row MakeRow(uint? index, params Cell[] cells)
	{
		Row row = new();
		if (index != null)
		{
			row.RowIndex = index.Value;
		}
		foreach (Cell cell in cells)
		{
			row.AppendChild(cell);
		}
		return row;
	}

	private static readonly Dictionary<int, string> EdgeSharedStrings = new() { [0] = "Header A", [1] = "Header B", [2] = "Header C", [3] = "shared body" };

	// Each factory builds a fresh SheetData because the original table read mutates the DOM when cells are missing
	private static IEnumerable<(string Name, Func<SheetData> Factory)> SheetScenarios()
	{
		yield return ("dense", () => PoData.CreateWorksheet(10, 6, out _).GetFirstChild<SheetData>()!.CloneNode(true) as SheetData ?? new SheetData());

		yield return ("gaps", () => new SheetData(
			MakeRow(1, SharedCell("A1", 0), SharedCell("B1", 1), SharedCell("C1", 2), ValueCell("D1", "4"), SharedCell("F1", 99)),
			MakeRow(2, ValueCell("a2", "lower"), ValueCell("C2", "c2"), ValueCell("C2", "dup"), ValueCell(null, "noref"), ValueCell("D3", "wrong row"), SharedCell("E2", 3)),
			MakeRow(3, ValueCell("B3", "b3"), ValueCell("A3", "out of order"), ValueCell("F3", " ")),
			MakeRow(null, ValueCell("A4", "no row index")),
			MakeRow(4, ValueCell("A4", "a4"), ValueCell("D4", "d4")),
			MakeRow(4, ValueCell("A4", "duplicate row")),
			MakeRow(6, ValueCell("A6", "after missing row 5")),
			MakeRow(7, ValueCell("A7", "   "))));

		yield return ("malformedBeforeTarget", () => new SheetData(
			MakeRow(1, SharedCell("A1", 0), SharedCell("B1", 1), SharedCell("C1", 2)),
			MakeRow(2, ValueCell("A2", "a2"), ValueCell("$B$2", "bad"), ValueCell("C2", "c2")),
			MakeRow(3, ValueCell("A3", "a3"))));

		yield return ("malformedAfterTargets", () => new SheetData(
			MakeRow(1, SharedCell("A1", 0), SharedCell("B1", 1)),
			MakeRow(2, ValueCell("A2", "a2"), ValueCell("B2", "b2"), ValueCell("C2", "c2"), ValueCell("D2", "d2"), ValueCell("E2", "e2"), ValueCell("F2", "f2"), ValueCell("$Z$9", "bad")),
			MakeRow(3, ValueCell("B3", "b3"), ValueCell("not a ref", "bad"))));

		yield return ("malformedRowIndex", () =>
		{
			Row badRow = MakeRow(null, ValueCell("A3", "bad row"));
			try
			{
				badRow.SetAttribute(new OpenXmlAttribute("r", string.Empty, "abc"));
			}
			catch (Exception)
			{
				// If the SDK rejects the attribute the scenario simply degrades to a row without RowIndex
			}
			return new SheetData(
				MakeRow(1, SharedCell("A1", 0), SharedCell("B1", 1)),
				MakeRow(2, ValueCell("A2", "a2")),
				badRow,
				MakeRow(3, ValueCell("A3", "a3")));
		});

		yield return ("empty", () => new SheetData());
	}

	private static void VerifySheetRead()
	{
		(string Start, string End)[] ranges = [("A1", "F12"), ("B2", "D5"), ("A1", "A1"), ("C1", "B3")];
		foreach ((string name, Func<SheetData> factory) in SheetScenarios())
		{
			foreach ((string start, string end) in ranges)
			{
				foreach (bool hasHeaders in new[] { true, false })
				{
					SheetData sheet = factory();
					DataTable expected = PoOpenXmlSheetReadLookupBenchmarks.OriginalRead(sheet, EdgeSharedStrings, hasHeaders, new CellReference(start), new CellReference(end));
					DataTable actual = PoOpenXmlSheetReadLookupBenchmarks.OptimizedRead(sheet, EdgeSharedStrings, hasHeaders, new CellReference(start), new CellReference(end));
					AssertTablesEqual(expected, actual, $"SheetRead[{name},{start}:{end},headers={hasHeaders}]");
				}
			}
		}

		Worksheet big = PoData.CreateWorksheet(60, 9, out Dictionary<int, string> bigIndex);
		SheetData bigSheet = big.GetFirstChild<SheetData>()!;
		AssertTablesEqual(PoOpenXmlSheetReadLookupBenchmarks.OriginalRead(bigSheet, bigIndex, true, new CellReference("A1"), new CellReference(9, 61)),
			PoOpenXmlSheetReadLookupBenchmarks.OptimizedRead(bigSheet, bigIndex, true, new CellReference("A1"), new CellReference(9, 61)), "SheetRead[big]");
	}

	private static void VerifyTableRead()
	{
		(string Start, string End)[] ranges = [("A1", "F8"), ("B2", "D5"), ("A1", "A1")];
		foreach ((string name, Func<SheetData> factory) in SheetScenarios())
		{
			foreach ((string start, string end) in ranges)
			{
				DataTable expected = PoOpenXmlTableReadLookupBenchmarks.OriginalReadTable(new Worksheet(factory()), EdgeSharedStrings, new CellReference(start), new CellReference(end));
				DataTable actual = PoOpenXmlTableReadLookupBenchmarks.OptimizedReadTable(new Worksheet(factory()), EdgeSharedStrings, new CellReference(start), new CellReference(end));
				AssertTablesEqual(expected, actual, $"TableRead[{name},{start}:{end}]");
			}
		}

		AssertTablesEqual(PoOpenXmlTableReadLookupBenchmarks.OriginalReadTable(new Worksheet(), EdgeSharedStrings, new CellReference("A1"), new CellReference("C3")),
			PoOpenXmlTableReadLookupBenchmarks.OptimizedReadTable(new Worksheet(), EdgeSharedStrings, new CellReference("A1"), new CellReference("C3")), "TableRead[noSheetData]");
		AssertTablesEqual(PoOpenXmlTableReadLookupBenchmarks.OriginalReadTable(null, EdgeSharedStrings, new CellReference("A1"), new CellReference("C3")),
			PoOpenXmlTableReadLookupBenchmarks.OptimizedReadTable(null, EdgeSharedStrings, new CellReference("A1"), new CellReference("C3")), "TableRead[nullWorksheet]");

		Worksheet bigExpected = PoData.CreateWorksheet(60, 9, out Dictionary<int, string> bigIndex);
		Worksheet bigActual = PoData.CreateWorksheet(60, 9, out _);
		AssertTablesEqual(PoOpenXmlTableReadLookupBenchmarks.OriginalReadTable(bigExpected, bigIndex, new CellReference("A1"), new CellReference(9, 61)),
			PoOpenXmlTableReadLookupBenchmarks.OptimizedReadTable(bigActual, bigIndex, new CellReference("A1"), new CellReference(9, 61)), "TableRead[big]");
	}

	private static void VerifyCellReferenceParse()
	{
		string?[] inputs =
		[
			"A1", "a1", "Zz10", "XFD1048576", "xfd1", "XFE1", "A0", "A1048576", "A1048577", "$A$1", "A1:B2", " A1", "A1 ", "AAAA1", "A01", "A0000001",
			"\u01311", "\u017F1", "A\u0661", "", "1A", "ab12cd", "ZZZ1", "A9999999", "A99999999", "A12345678901", "A-1", "A+1", null
		];
		foreach (string? input in inputs)
		{
			string expected = Outcome(() =>
			{
				CellReference parsed = new(input!);
				return $"{parsed.ColumnIndex}:{parsed.RowIndex}";
			});
			string actual = Outcome(() =>
			{
				PoCellRefParser.Parse(input!, out uint column, out uint row);
				return $"{column}:{row}";
			});
			Check(string.Equals(expected, actual, StringComparison.Ordinal), $"CellReference('{input}'): {expected} vs {actual}");
		}

		for (uint col = 1; col <= 16384; col += 7)
		{
			string reference = CellReference.NumberToColumnName(col) + col.ToString(CultureInfo.InvariantCulture);
			Check(PoCellRefParser.TryParseAscii(reference, out uint c, out uint r) && (c == col) && (r == col), $"CellReference fast path '{reference}'");
		}
	}

	private static void VerifyGetters()
	{
		CompareGetters(PoData.CreateExportRows(25));
		CompareGetters(new[] { new PoGetterEdge(), new PoGetterEdge { NullString = "x", NullableValue = 5, PrivateGetter = 9 } });
		CompareGetters(new[] { new PoGetterStruct { X = 1, Y = "y" }, default });
		CompareGetters(new PoGetterBase[] { new PoGetterBase(), new PoGetterDerived { Name = "n" } });
		CompareGetters(new[] { new PoGetterDerived { Name = "d" } });
		CompareGetters(new IPoGetterContract[] { new PoGetterContractImpl() });
	}

	private static void CompareGetters<T>(IEnumerable<T> items)
	{
		PropertyInfo[] properties = GetOrAddPropertiesFromReflectionCache(typeof(T));
		Func<T, object?>[] getters = PoGetterFactory.Create<T>(properties);
		foreach (T item in items)
		{
			for (int i = 0; i < properties.Length; i++)
			{
				string expected = Outcome(() => properties[i].GetValue(item));
				string actual = Outcome(() => getters[i](item));
				Check(string.Equals(expected, actual, StringComparison.Ordinal), $"Getter {typeof(T).Name}.{properties[i].Name}: {expected} vs {actual}");
			}
		}
	}

	private static List<ExportTestRow?> RowBuildEdgeRows()
	{
		List<ExportTestRow?> data = [.. PoData.CreateExportRows(40)];
		data.Add(null);
		data.Add(new ExportTestRow());
		data.Add(new ExportTestRow { Field01 = "12345678901234567890", Field02 = "1,234.5", Field03 = "   ", Field04 = "-0", Field05 = "1e10", FirstName = "First1" });
		data.Add(new ExportTestRow { Field12 = new string('x', 200), Field13 = "999999999999999999999999999999" });
		return data;
	}

	private static void VerifyRowBuild()
	{
		List<ExportTestRow?> data = RowBuildEdgeRows();
		PropertyInfo[] properties = GetOrAddPropertiesFromReflectionCache(typeof(ExportTestRow));
		(uint Header, uint Body)[] styles = [(1, 2), (1, 0), (7, 5), (0, 6), (3, 8), (4, 9)];
		foreach ((uint header, uint body) in styles)
		{
			SheetData expectedSheet = new();
			SharedStringTable expectedTable = new();
			double[] expectedWidths = PoOpenXmlRowBuildBenchmarks.OriginalBuild(data, properties, expectedSheet, expectedTable, header, body);
			foreach (bool full in new[] { false, true })
			{
				SheetData actualSheet = new();
				SharedStringTable actualTable = new();
				double[] actualWidths = PoOpenXmlRowBuildBenchmarks.OptimizedBuild(data, properties, actualSheet, actualTable, header, body, full);
				string name = $"RowBuild[styles={header}/{body},full={full}]";
				Check(string.Equals(expectedSheet.OuterXml, actualSheet.OuterXml, StringComparison.Ordinal), $"{name}: sheet XML");
				Check(string.Equals(expectedTable.OuterXml, actualTable.OuterXml, StringComparison.Ordinal), $"{name}: shared-string XML");
				Check(Enumerable.SequenceEqual(expectedWidths, actualWidths), $"{name}: column widths");
			}
		}

		// The width bound must never be below the real CalculateWidth result
		foreach (uint style in new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })
		{
			PoWidthBound bound = new(style);
			foreach (string text in new[] { "", "a", "1", "12", "123", "1234", "-12.5", "abc", "1e5", new string('9', 40), new string('z', 41) })
			{
				Check(bound.UpperBound(text.Length) >= CalculateWidth(text, style), $"WidthBound[{style},'{text}']");
			}
		}
	}

	private static void VerifyNpoiLoop()
	{
		List<ExportTestRow?> data = RowBuildEdgeRows();
		PropertyInfo[] properties = GetOrAddPropertiesFromReflectionCache(typeof(ExportTestRow));
		List<string?> expectedSink = [];
		List<string?> actualSink = [];
		int[] expected = PoNpoiExportLoopBenchmarks.OriginalLoop(data, properties, expectedSink);
		int[] actual = PoNpoiExportLoopBenchmarks.OptimizedLoop(data, properties, actualSink);
		Check(Enumerable.SequenceEqual(expected, actual), "NpoiLoop: widths");
		Check(Enumerable.SequenceEqual(expectedSink, actualSink), "NpoiLoop: cell values");
	}

	private static void VerifyDataTableIteration()
	{
		List<ExportTestRow> rows = PoData.CreateExportRows(30);
		rows.Add(new ExportTestRow());
		DataTable table = PoData.CreateDataTable(rows);
		table.Rows[3]["Amount"] = DBNull.Value;
		foreach (List<string>? skip in new List<string>?[] { null, [], ["field05", "FIELD12", "NotAColumn"], ["id", "Field17"] })
		{
			List<string> expectedSink = [];
			List<string> actualSink = [];
			long expected = PoDataTableIterationBenchmarks.OriginalIterate(table, skip, expectedSink);
			long actual = PoDataTableIterationBenchmarks.OptimizedIterate(table, skip, actualSink);
			Check(expected == actual, "DataTableIteration: char count");
			Check(Enumerable.SequenceEqual(expectedSink, actualSink), "DataTableIteration: cell values");
		}
	}

	private static void VerifyReadSetter()
	{
		Dictionary<uint, PropertyInfo> map = PoExcelReadSetterBenchmarks.CreateColumnMap(out PropertyInfo[] ordered);
		List<string[]> cells = [.. PoExcelReadSetterBenchmarks.CreateCells(40, ordered)];
		cells.Add(Enumerable.Repeat(" ", ordered.Length).ToArray()); // Whitespace-only row: skipped
		cells.Add(Enumerable.Repeat("garbage", ordered.Length).ToArray()); // Nothing converts except strings / nullable -> null
		cells.Add([.. Enumerable.Repeat("1", ordered.Length + 3)]); // More cells than mapped columns
		cells.Add(["5"]); // Fewer cells than mapped columns

		Dictionary<uint, PropertyInfo> sparseMap = new() { [2] = ordered[1], [5] = ordered[4], [9] = ordered[8] };
		foreach (Dictionary<uint, PropertyInfo> columnMap in new[] { map, sparseMap })
		{
			List<PoReadRow> expected = PoExcelReadSetterBenchmarks.OriginalLoop([.. cells], columnMap);
			List<PoReadRow> actual = PoExcelReadSetterBenchmarks.OptimizedLoop([.. cells], columnMap);
			Check(expected.Count == actual.Count, "ReadSetter: row count");
			for (int r = 0; r < expected.Count; r++)
			{
				foreach (PropertyInfo property in ordered)
				{
					Check(Equals(property.GetValue(expected[r]), property.GetValue(actual[r])), $"ReadSetter: row {r} {property.Name}");
				}
			}
		}
	}

	private static void VerifyWordLookup()
	{
		Dictionary<string, string> urlsToUpdate = new()
		{
			["https://www.example.com/a"] = "first",
			["HTTPS://WWW.EXAMPLE.COM/A"] = "second (case duplicate, must lose)",
			["https://www.example.com/nullvalue"] = null!,
			["https://www.example.com/B"] = "b",
			["https://www.example.com/café"] = "accented"
		};
		Uri[] hyperlinks =
		[
			new("https://www.example.com/a"), new("https://WWW.example.com/A"), new("https://www.example.com/nullvalue"), new("https://www.example.com/b"),
			new("https://www.example.com/c"), new("https://www.example.com/caf%C3%A9"), new("https://www.example.com/CAFÉ")
		];

		string?[] expected = PoWordUrlLookupBenchmarks.OriginalLookup(hyperlinks, urlsToUpdate);
		Check(Enumerable.SequenceEqual(expected, PoWordUrlLookupBenchmarks.HoistedLookup(hyperlinks, urlsToUpdate)), "WordLookup: hoisted loop");
		Check(Enumerable.SequenceEqual(expected, PoWordUrlLookupBenchmarks.DictionaryLookup(hyperlinks, urlsToUpdate)), "WordLookup: comparer dictionary");
		Check(Enumerable.SequenceEqual(PoWordUrlLookupBenchmarks.OriginalLookup(hyperlinks, []), PoWordUrlLookupBenchmarks.DictionaryLookup(hyperlinks, [])), "WordLookup: empty dictionary");
	}

	private static void VerifyWordRegex()
	{
		string[] uris =
		[
			"https://old.example.com/a", "HTTP://OLD.EXAMPLE.COM/b?x=1", "https://other.example.org/c", "https://old.example.com/https://old.example.com/twice", "", "ftp://old.example.com/x"
		];
		foreach (RegexOptions options in new[] { RegexOptions.IgnoreCase, RegexOptions.None, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant })
		{
			foreach ((string pattern, string replacement) in new[] { (PoWordRegexReplaceBenchmarks.Pattern, PoWordRegexReplaceBenchmarks.Replacement), (@"(\w+)\.example", "$1-x.example"), ("old", "$$") })
			{
				string?[] expected = PoWordRegexReplaceBenchmarks.OriginalReplace(uris, pattern, replacement, options, TimeSpan.FromSeconds(2));
				string?[] actual = PoWordRegexReplaceBenchmarks.OptimizedReplace(uris, pattern, replacement, options, TimeSpan.FromSeconds(2));
				Check(Enumerable.SequenceEqual(expected, actual), $"WordRegex[{pattern},{options}]");
			}
		}
	}

	private static void VerifySaxWrite()
	{
		object?[][] rows =
		[
			["plain", null, 42, "a & b < c > \"q\"", " padded "],
			[DBNull.Value, 3.5m, true, string.Empty, "x"],
			["tab\tnew\nline", PoData.BaseDate, -1, "ü€", "last"]
		];
		string[] letters = ["A", "B", "C", "D", "E"];
		PoSinkStream expected = new(captureBytes: true);
		PoSaxCellWriteBenchmarks.OriginalWrite(expected, rows, letters, 2);
		PoSinkStream actual = new(captureBytes: true);
		PoSaxCellWriteBenchmarks.OptimizedWrite(actual, rows, letters, 2);
		Check(Enumerable.SequenceEqual(expected.CapturedBytes, actual.CapturedBytes), "SaxWrite: XML bytes");
		Check(expected.BytesWritten == actual.BytesWritten, "SaxWrite: byte count");
	}
}
