using System.Reflection;
using DocumentFormat.OpenXml.Spreadsheet;
using static CommonNetFuncs.Core.ExceptionLocation;

namespace CommonNetFuncs.Excel.OpenXml;

// Forward-only indexes used by the read loops so each Row/Cell element is visited (and its reference parsed) at most once,
// instead of rescanning SheetData / Row children for every cell.
public static partial class Common
{
	/// <summary>
	/// Forward-only index over SheetData rows. Find(r) returns exactly what Elements&lt;Row&gt;().FirstOrDefault(x =&gt; x.RowIndex != null &amp;&amp; x.RowIndex == r) returns,
	/// and throws at the same point (a row whose RowIndex text cannot be parsed stays pending, so every later miss rethrows like the original rescan).
	/// </summary>
	private sealed class LazyRowLookup
	{
		private readonly IEnumerator<Row>? rowEnumerator;
		private readonly Dictionary<uint, Row> rowsByIndex = [];
		private Row? pending;
		private bool exhausted;

		public LazyRowLookup(SheetData? sheetData)
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

	/// <summary>Forward-only per-row cell index keyed by parsed column; same match/exception semantics as the per-cell CellReference compare scan in <c>GetCellValue(SheetData, uint, uint, IReadOnlyDictionary)</c>.</summary>
	private sealed class LazyParsedCellLookup
	{
		private readonly IEnumerator<Cell> cellEnumerator;
		private readonly Dictionary<uint, Cell> cellsByColumn = [];
		private readonly uint rowIndex;
		private Cell? pending;
		private bool exhausted;

		private LazyParsedCellLookup(Row row, uint rowIndex)
		{
			cellEnumerator = row.Elements<Cell>().GetEnumerator();
			this.rowIndex = rowIndex;
		}

		public static LazyParsedCellLookup? ForRow(Row? row, uint rowIndex)
		{
			return row == null ? null : new LazyParsedCellLookup(row, rowIndex);
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
					// The original compares "<col><row>" strings, so only cells whose reference names this row can ever match
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

	/// <summary>Read-only replacement for <c>GetCellFromCoordinates</c> inside a table read: same results without mutating the DOM or rescanning rows/cells.</summary>
	private sealed class TableCellResolver
	{
		private readonly SheetData? sheetData;
		private readonly LazyRowLookup rows;
		private uint currentRowIndex;
		private LazyRefCellLookup? currentCells;

		public TableCellResolver(Worksheet worksheet)
		{
			sheetData = worksheet.GetFirstChild<SheetData>();
			rows = new LazyRowLookup(sheetData);
		}

		/// <returns>
		/// <see langword="false"/> when <c>GetCellFromCoordinates</c> would have caught an exception and returned null.
		/// <see langword="true"/> with a null <paramref name="cell"/> when it would have created (and returned) a new empty cell.
		/// </returns>
		public bool TryResolve(uint rowIndex, uint columnIndex, string canonicalReference, out Cell? cell)
		{
			cell = null;
			try
			{
				Row? row = rows.Find(rowIndex);
				if (row == null)
				{
					// GetCellFromCoordinates inserts a new row; its insertion-point scan is the only part that can throw
					_ = sheetData?.Elements<Row>().FirstOrDefault(r => r.RowIndex?.Value > rowIndex);
					return true;
				}

				if ((currentCells == null) || (currentRowIndex != rowIndex))
				{
					currentCells = new LazyRefCellLookup(row);
					currentRowIndex = rowIndex;
				}

				cell = currentCells.Find(canonicalReference);
				if (cell == null)
				{
					// GetCellFromCoordinates inserts a new cell; its insertion-point scan parses sibling references and can throw.
					// Cells it would have inserted earlier have lower columns and valid references, so they never change this scan's outcome.
					_ = row.Elements<Cell>().FirstOrDefault(c => c.CellReference?.Value is { } cellRefText && new CellReference(cellRefText).ColumnIndex > columnIndex);
				}
				return true;
			}
			catch (Exception ex)
			{
				logger.Error(ex, ErrorLocationTemplate, ex.GetLocationOfException());
				cell = null;
				return false;
			}
		}
	}

	/// <summary>Forward-only per-row index keyed by the exact (ordinal) CellReference text, matching the <c>c.CellReference == "B12"</c> compare in <c>GetCell</c>.</summary>
	private sealed class LazyRefCellLookup(Row row)
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

	/// <summary>Per-column pre-resolved conversion target + lazily resolved setter; <see cref="Set"/> mirrors the former per-cell SetExcelPropertyValue exactly (setter build/convert failures are swallowed).</summary>
	private sealed class ExcelColumnBinder
	{
		private readonly PropertyInfo property;
		private readonly Type targetType;
		private readonly bool isNullable;
		private Action<object, object?>? setter;

		public ExcelColumnBinder(PropertyInfo property)
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
				object? result = ConvertExcelCellValue(value, targetType);
				if ((result != null) || isNullable)
				{
					// Resolved inside the try so a setter that cannot be built is still silently skipped
					setter ??= ExcelSetterCache.GetOrAdd(property, BuildSetter);
					setter(obj, result);
				}
			}
			catch
			{
				// Non-convertible values are silently skipped; the property retains its default value
			}
		}
	}
}
