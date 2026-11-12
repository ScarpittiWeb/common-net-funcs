using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using FastExpressionCompiler;
using ZLinq;

namespace BenchmarkSuite;

// Each class below pits a verbatim copy of the current library implementation ("Original") against the proposed replacement(s).
// "Shared" variants compile on both net11.0 and netstandard2.1, "Net11" variants rely on APIs that only exist on modern .NET.

/// <summary>Crypto random int: GetBytes + modulo (biased) vs RandomNumberGenerator.GetInt32 (unbiased, net11).</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptCryptoRandomIntBenchmarks
{
	private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

	[Benchmark(Baseline = true)]
	public int Original_GetBytesModulo()
	{
		Span<byte> randomNumber = stackalloc byte[4];
		Rng.GetBytes(randomNumber);
		int result = BitConverter.ToInt32(randomNumber) & 0x7FFFFFFF;
		return 0 + (result % (1000 - 0));
	}

	[Benchmark]
	public int Net11_GetInt32()
	{
		return RandomNumberGenerator.GetInt32(0, 1000);
	}
}

internal static class OptRandomHelpers
{
	internal static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

	internal static readonly HashSet<char> DefaultCharSet = ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',
		'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9'];

	// Verbatim copy of Random.GetRandomInt(int, int)
	internal static int OriginalGetRandomInt(int minValue, int maxValue)
	{
		if (maxValue <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxValue));
		}

		if (minValue > maxValue)
		{
			throw new ArgumentException("min > max", nameof(minValue));
		}

		if (minValue == maxValue)
		{
			return minValue;
		}

		Span<byte> randomNumber = stackalloc byte[4];
		Rng.GetBytes(randomNumber);
		int result = BitConverter.ToInt32(randomNumber) & 0x7FFFFFFF;
		return minValue + (result % (maxValue - minValue));
	}

	// Proposed shared implementation: one RNG call per batch of characters + unbiased rejection sampling
	internal static string BatchedFromCharSet(ReadOnlySpan<char> allowedChars, int length, CancellationToken cancellationToken = default)
	{
		if (length <= 0)
		{
			return string.Empty;
		}

		char[]? rented = null;
		Span<char> destination = length <= 256 ? stackalloc char[length] : (rented = ArrayPool<char>.Shared.Rent(length)).AsSpan(0, length);
		try
		{
			FillRandom(allowedChars, destination, cancellationToken);
			return new string(destination);
		}
		finally
		{
			if (rented != null)
			{
				ArrayPool<char>.Shared.Return(rented);
			}
		}
	}

	private static void FillRandom(ReadOnlySpan<char> allowedChars, Span<char> destination, CancellationToken cancellationToken)
	{
		int setLength = allowedChars.Length;
		int acceptLimit = 256 - (256 % setLength);
		Span<byte> randomBytes = stackalloc byte[256];
		int written = 0;
		while (written < destination.Length)
		{
			cancellationToken.ThrowIfCancellationRequested();
			int remaining = destination.Length - written;
			Span<byte> chunk = randomBytes[..Math.Min(randomBytes.Length, (remaining * 256 / acceptLimit) + 4)];
			Rng.GetBytes(chunk);
			foreach (byte b in chunk)
			{
				if (b < acceptLimit)
				{
					destination[written++] = allowedChars[b % setLength];
					if (written == destination.Length)
					{
						break;
					}
				}
			}
		}
	}
}

/// <summary>GenerateRandomStringByCharSet (default 62-char set).</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptRandomStringByCharSetBenchmarks
{
	private static readonly char[] DefaultCharArray = OptRandomHelpers.DefaultCharSet.ToArray();

	[Params(16, 256)]
	public int Length { get; set; }

	[Benchmark(Baseline = true)]
	public string Original()
	{
		ISet<char> charSet = OptRandomHelpers.DefaultCharSet;
		ReadOnlySpan<char> charSetSpan = new(charSet.ToArray());
		int setLength = charSetSpan.Length;

		StringBuilder result = new(Length);
		for (int i = 0; i < Length; i++)
		{
			char randomChar = charSetSpan[OptRandomHelpers.OriginalGetRandomInt(0, setLength)];
			result.Append(randomChar);
		}

		return result.ToString();
	}

	[Benchmark]
	public string Shared_BatchedRejectionSampling()
	{
		return OptRandomHelpers.BatchedFromCharSet(DefaultCharArray, Length);
	}

	[Benchmark]
	public string Net11_RandomNumberGeneratorGetString()
	{
		return RandomNumberGenerator.GetString(DefaultCharArray, Length);
	}
}

/// <summary>GenerateRandomString (ASCII 32-126, no blacklist).</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptRandomAsciiStringBenchmarks
{
	[Params(16, 256)]
	public int Length { get; set; }

	[Benchmark(Baseline = true)]
	public string Original()
	{
		StringBuilder result = new(Length);
		for (int i = 0; i < Length; i++)
		{
			result.Append((char)OptRandomHelpers.OriginalGetRandomInt(32, 126 + 1));
		}
		return result.ToString();
	}

	[Benchmark]
	public string Shared_BatchedRejectionSampling()
	{
		Span<char> allowed = stackalloc char[126 - 32 + 1];
		for (int i = 0; i < allowed.Length; i++)
		{
			allowed[i] = (char)(32 + i);
		}
		return OptRandomHelpers.BatchedFromCharSet(allowed, Length);
	}
}

/// <summary>ParsePascalCase: StringBuilder vs two-pass string.Create.</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptParsePascalCaseBenchmarks
{
	[Params("ThisIsAPascalCaseString", "ThisIsAMuchLongerPascalCaseStringThatHasQuiteAFewMoreWordsInItForTestingPurposesOnly")]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string Original()
	{
		StringBuilder result = new(Input.Length + 10);
		bool first = true;
		foreach (char c in Input.AsSpan())
		{
			if (char.IsUpper(c) && !first)
			{
				result.Append(' ');
			}

			result.Append(c);
			first = false;
		}

		return result.ToString();
	}

	[Benchmark]
	public string Shared_StringCreate()
	{
		string s = Input;
		int spaces = 0;
		for (int i = 1; i < s.Length; i++)
		{
			if (char.IsUpper(s[i]))
			{
				spaces++;
			}
		}

		if (spaces == 0)
		{
			return s;
		}

		return string.Create(s.Length + spaces, s, static (destination, source) =>
		{
			destination[0] = source[0];
			int position = 1;
			for (int i = 1; i < source.Length; i++)
			{
				char c = source[i];
				if (char.IsUpper(c))
				{
					destination[position++] = ' ';
				}
				destination[position++] = c;
			}
		});
	}
}

/// <summary>GetOnlyNumbers: string.Concat(MatchCollection) vs Regex.EnumerateMatches into a span.</summary>
[MemoryDiagnoser]
[RankColumn]
public partial class OptGetOnlyNumbersBenchmarks
{
	[GeneratedRegex(@"[0-9]*\.?[0-9]+", RegexOptions.None, 5000)]
	private static partial Regex NumbersOnlyRegex();

	[Params("  Order #12345 shipped on 2026-11-12 for $1,234.56 total  ", "abc123def456ghi789jkl0.5mno")]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return string.Concat(NumbersOnlyRegex().Matches(Input.Trim())).Trim();
	}

	[Benchmark]
	public string Net11_EnumerateMatches()
	{
		string trimmed = Input.Trim();
		char[]? rented = null;
		Span<char> buffer = trimmed.Length <= 256 ? stackalloc char[trimmed.Length] : (rented = ArrayPool<char>.Shared.Rent(trimmed.Length));
		try
		{
			int position = 0;
			foreach (ValueMatch match in NumbersOnlyRegex().EnumerateMatches(trimmed))
			{
				trimmed.AsSpan(match.Index, match.Length).CopyTo(buffer[position..]);
				position += match.Length;
			}
			return buffer[..position].Trim().ToString();
		}
		finally
		{
			if (rented != null)
			{
				ArrayPool<char>.Shared.Return(rented);
			}
		}
	}
}

/// <summary>GetRandomElement: ZLinq Skip(n).First() vs direct indexing when the source is an IList.</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptGetRandomElementBenchmarks
{
	private readonly List<int> list = Enumerable.Range(0, 1000).ToList();

	[Benchmark(Baseline = true)]
	public int Original_List()
	{
		IEnumerable<int> items = list;
		return items.AsValueEnumerable().Skip(OptRandomHelpers.OriginalGetRandomInt(0, items.Count())).First();
	}

	[Benchmark]
	public int Shared_IndexedFastPath_List()
	{
		IEnumerable<int> items = list;
		if (items is IList<int> indexable)
		{
			return indexable[OptRandomHelpers.OriginalGetRandomInt(0, indexable.Count)];
		}
		return items.AsValueEnumerable().Skip(OptRandomHelpers.OriginalGetRandomInt(0, items.Count())).First();
	}
}

/// <summary>Inspect.IsEqual cache key: Tuple.Create (heap allocation per call) vs ValueTuple key.</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptIsEqualCacheKeyBenchmarks
{
	private static readonly ConcurrentDictionary<Tuple<Type, bool, bool>, Func<object, object, IEnumerable<string>, bool>> TupleCache = new();
	private static readonly ConcurrentDictionary<(Type, bool, bool), Func<object, object, IEnumerable<string>, bool>> ValueTupleCache = new();
	private static readonly Func<object, object, IEnumerable<string>, bool> Comparer = static (_, _, _) => true;

	[GlobalSetup]
	public void Setup()
	{
		TupleCache[Tuple.Create(typeof(SimpleClass), false, true)] = Comparer;
		ValueTupleCache[(typeof(SimpleClass), false, true)] = Comparer;
	}

	[Benchmark(Baseline = true)]
	public Func<object, object, IEnumerable<string>, bool>? Original_TupleKey()
	{
		Tuple<Type, bool, bool> key = Tuple.Create(typeof(SimpleClass), false, true);
		TupleCache.TryGetValue(key, out Func<object, object, IEnumerable<string>, bool>? compareDelegate);
		return compareDelegate;
	}

	[Benchmark]
	public Func<object, object, IEnumerable<string>, bool>? Shared_ValueTupleKey()
	{
		ValueTupleCache.TryGetValue((typeof(SimpleClass), false, true), out Func<object, object, IEnumerable<string>, bool>? compareDelegate);
		return compareDelegate;
	}
}

/// <summary>Inspect.CountDefaultProps: GetUninitializedObject per property per call vs cached boxed defaults.</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptCountDefaultPropsBenchmarks
{
	private static readonly PropertyInfo[] Properties = typeof(SimpleClass).GetProperties(BindingFlags.Public | BindingFlags.Instance);
	private static readonly ConcurrentDictionary<Type, object?> DefaultValues = new();
	private readonly SimpleClass instance = new() { Id = 0, Name = "x", Value = 0, IsActive = true, CreatedDate = default };

	private static object? OriginalGetDefaultValue(Type type)
	{
		return type.IsValueType ? RuntimeHelpers.GetUninitializedObject(type) : null;
	}

	[Benchmark(Baseline = true)]
	public int Original()
	{
		return Properties.Count(x => x.CanWrite && Equals(x.GetValue(instance), OriginalGetDefaultValue(x.PropertyType)));
	}

	[Benchmark]
	public int Shared_CachedDefaultsLoop()
	{
		int count = 0;
		foreach (PropertyInfo property in Properties)
		{
			if (property.CanWrite && Equals(property.GetValue(instance), DefaultValues.GetOrAdd(property.PropertyType, static t => OriginalGetDefaultValue(t))))
			{
				count++;
			}
		}
		return count;
	}
}

public sealed class OptSqlRow
{
	public int Id { get; set; }
	public string? Name { get; set; }
	public string? Email { get; set; }
	public decimal Amount { get; set; }
	public DateTime Created { get; set; }
	public bool Active { get; set; }
	public long Count { get; set; }
	public double Ratio { get; set; }
	public string? Notes { get; set; }
	public Guid Key { get; set; }
}

/// <summary>DirectQuery mapper: GetOrdinal evaluated twice per property per row vs once (stored in a block local).</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptSqlMapperBenchmarks
{
	private DataTable table = null!;
	private Func<IDataReader, OptSqlRow> originalMapper = null!;
	private Func<IDataReader, OptSqlRow> optimizedMapper = null!;

	[GlobalSetup]
	public void Setup()
	{
		table = new DataTable();
		table.Columns.Add("Id", typeof(long));
		table.Columns.Add("Name", typeof(string));
		table.Columns.Add("Email", typeof(string));
		table.Columns.Add("Amount", typeof(decimal));
		table.Columns.Add("Created", typeof(DateTime));
		table.Columns.Add("Active", typeof(bool));
		table.Columns.Add("Count", typeof(long));
		table.Columns.Add("Ratio", typeof(double));
		table.Columns.Add("Notes", typeof(string));
		table.Columns.Add("Key", typeof(Guid));
		for (int i = 0; i < 1000; i++)
		{
			table.Rows.Add((long)i, $"Name{i}", $"user{i}@test.com", i * 1.5m, DateTime.UtcNow, i % 2 == 0, (long)i * 7, i / 3.0, i % 5 == 0 ? DBNull.Value : "note", Guid.NewGuid());
		}

		originalMapper = CreateOriginal<OptSqlRow>(typeof(OptSqlRow));
		optimizedMapper = CreateOptimized<OptSqlRow>(typeof(OptSqlRow));
	}

	// Verbatim copy of DirectQuery.CreateMapperDelegate
	private static Func<IDataReader, T> CreateOriginal<T>(Type type) where T : class, new()
	{
		ParameterExpression readerParam = Expression.Parameter(typeof(IDataReader), "reader");
		MethodInfo getValueMethod = typeof(IDataRecord).GetMethod("GetValue")!;
		MethodInfo getOrdinalMethod = typeof(IDataRecord).GetMethod("GetOrdinal")!;
		MethodInfo isDBNullMethod = typeof(IDataRecord).GetMethod("IsDBNull")!;
		NewExpression instanceExp = Expression.New(type);
		IEnumerable<PropertyInfo> properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite);
		List<MemberBinding> assignments = [];
		foreach (PropertyInfo prop in properties)
		{
			MethodCallExpression getOrdinalCall = Expression.Call(readerParam, getOrdinalMethod, Expression.Constant(prop.Name));
			MethodCallExpression isDbNullCall = Expression.Call(readerParam, isDBNullMethod, getOrdinalCall);
			MethodCallExpression getValue = Expression.Call(readerParam, getValueMethod, getOrdinalCall);
			Expression convertedValue = prop.PropertyType == typeof(int)
				? Expression.Convert(Expression.Call(typeof(Convert).GetMethod(nameof(Convert.ToInt32), [typeof(object)])!, getValue), typeof(int))
				: Expression.Convert(getValue, prop.PropertyType);
			ConditionalExpression assignValue = Expression.Condition(isDbNullCall, Expression.Default(prop.PropertyType), convertedValue);
			assignments.Add(Expression.Bind(prop, assignValue));
		}
		MemberInitExpression memberInit = Expression.MemberInit(instanceExp, assignments);
		return Expression.Lambda<Func<IDataReader, T>>(memberInit, readerParam).CompileFast();
	}

	// Proposed: resolve each ordinal once per row into a block-scoped local
	private static Func<IDataReader, T> CreateOptimized<T>(Type type) where T : class, new()
	{
		ParameterExpression readerParam = Expression.Parameter(typeof(IDataReader), "reader");
		MethodInfo getValueMethod = typeof(IDataRecord).GetMethod("GetValue")!;
		MethodInfo getOrdinalMethod = typeof(IDataRecord).GetMethod("GetOrdinal")!;
		MethodInfo isDBNullMethod = typeof(IDataRecord).GetMethod("IsDBNull")!;
		MethodInfo toInt32Method = typeof(Convert).GetMethod(nameof(Convert.ToInt32), [typeof(object)])!;
		NewExpression instanceExp = Expression.New(type);
		List<MemberBinding> assignments = [];
		foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (!prop.CanWrite)
			{
				continue;
			}
			ParameterExpression ordinal = Expression.Variable(typeof(int), "ordinal");
			MethodCallExpression getValue = Expression.Call(readerParam, getValueMethod, ordinal);
			Expression convertedValue = prop.PropertyType == typeof(int)
				? Expression.Convert(Expression.Call(toInt32Method, getValue), typeof(int))
				: Expression.Convert(getValue, prop.PropertyType);
			Expression assignValue = Expression.Block(prop.PropertyType, [ordinal],
				Expression.Assign(ordinal, Expression.Call(readerParam, getOrdinalMethod, Expression.Constant(prop.Name))),
				Expression.Condition(Expression.Call(readerParam, isDBNullMethod, ordinal), Expression.Default(prop.PropertyType), convertedValue));
			assignments.Add(Expression.Bind(prop, assignValue));
		}
		MemberInitExpression memberInit = Expression.MemberInit(instanceExp, assignments);
		return Expression.Lambda<Func<IDataReader, T>>(memberInit, readerParam).CompileFast();
	}

	private static long MapAll(DataTable table, Func<IDataReader, OptSqlRow> mapper)
	{
		long sum = 0;
		using DataTableReader reader = table.CreateDataReader();
		while (reader.Read())
		{
			sum += mapper(reader).Count;
		}
		return sum;
	}

	[Benchmark(Baseline = true)]
	public long Original_DoubleGetOrdinal() => MapAll(table, originalMapper);

	[Benchmark]
	public long Shared_SingleGetOrdinal() => MapAll(table, optimizedMapper);
}

/// <summary>HtmlEmailBuilder.CreateHtmlTable row loop: string.Concat(Select(interpolated)) vs direct StringBuilder appends.</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptHtmlTableBenchmarks
{
	private DataTable table = null!;

	[GlobalSetup]
	public void Setup()
	{
		table = new DataTable();
		for (int c = 0; c < 10; c++)
		{
			table.Columns.Add($"Col{c}", c % 2 == 0 ? typeof(string) : typeof(int));
		}
		for (int r = 0; r < 200; r++)
		{
			object[] values = new object[10];
			for (int c = 0; c < 10; c++)
			{
				values[c] = c % 2 == 0 ? $"Value {r}-{c}" : r * c;
			}
			table.Rows.Add(values);
		}
	}

	[Benchmark(Baseline = true)]
	public string Original()
	{
		StringBuilder tableHtmlBuilder = new();
		foreach (DataRow rowData in table.Rows)
		{
			tableHtmlBuilder.Append("<tr>");
			tableHtmlBuilder.Append(string.Concat(rowData.ItemArray.Select(x => $"<td>{x?.ToString()}</td>")));
			tableHtmlBuilder.Append("</tr>");
		}
		return tableHtmlBuilder.ToString();
	}

	[Benchmark]
	public string Shared_DirectAppend()
	{
		StringBuilder tableHtmlBuilder = new();
		int columnCount = table.Columns.Count;
		foreach (DataRow rowData in table.Rows)
		{
			tableHtmlBuilder.Append("<tr>");
			for (int i = 0; i < columnCount; i++)
			{
				tableHtmlBuilder.Append("<td>").Append(rowData[i]?.ToString()).Append("</td>");
			}
			tableHtmlBuilder.Append("</tr>");
		}
		return tableHtmlBuilder.ToString();
	}
}

/// <summary>CsvExportHelpers.ExportToCsv(DataTable) body.</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptCsvDataTableBenchmarks
{
	private DataTable table = null!;

	[GlobalSetup]
	public void Setup()
	{
		table = new DataTable();
		for (int c = 0; c < 10; c++)
		{
			table.Columns.Add($"Col{c}", c % 2 == 0 ? typeof(string) : typeof(int));
		}
		for (int r = 0; r < 1000; r++)
		{
			object[] values = new object[10];
			for (int c = 0; c < 10; c++)
			{
				values[c] = c % 2 == 0 ? (c == 4 ? $"Value, {r}" : $"Value {r}-{c}") : (c == 9 && r % 7 == 0 ? DBNull.Value : r * c);
			}
			table.Rows.Add(values);
		}
	}

	[Benchmark(Baseline = true)]
	public async Task<long> Original()
	{
		await using MemoryStream ms = new();
		await using StreamWriter streamWriter = new(ms, new UTF8Encoding(false));
		for (int i = 0; i < table.Columns.Count; i++)
		{
			await streamWriter.WriteAsync(table.Columns[i]?.ToString() ?? string.Empty).ConfigureAwait(false);
			if (i < table.Columns.Count - 1)
			{
				await streamWriter.WriteAsync(",").ConfigureAwait(false);
			}
		}
		await streamWriter.WriteAsync(streamWriter.NewLine).ConfigureAwait(false);
		foreach (DataRow row in table.Rows)
		{
			for (int i = 0; i < table.Columns.Count; i++)
			{
				if (!Convert.IsDBNull(row[i]))
				{
					string? value = row[i].ToString();
					if (value?.Contains(',') ?? false)
					{
						value = $"\"{value}\"";
						await streamWriter.WriteAsync(value).ConfigureAwait(false);
					}
					else
					{
						await streamWriter.WriteAsync(row[i]?.ToString() ?? string.Empty).ConfigureAwait(false);
					}
				}
				if (i < table.Columns.Count - 1)
				{
					await streamWriter.WriteAsync(",").ConfigureAwait(false);
				}
			}
			await streamWriter.WriteAsync(streamWriter.NewLine).ConfigureAwait(false);
		}
		await streamWriter.FlushAsync().ConfigureAwait(false);
		return ms.Length;
	}

	[Benchmark]
	public async Task<long> Shared_SingleToStringBufferedWrites()
	{
		await using MemoryStream ms = new();
		await using StreamWriter streamWriter = new(ms, new UTF8Encoding(false));
		int columnCount = table.Columns.Count;
		// The writer targets an in-memory stream, so synchronous writes only touch the writer's char buffer (no blocking I/O).
		for (int i = 0; i < columnCount; i++)
		{
			streamWriter.Write(table.Columns[i]?.ToString() ?? string.Empty);
			if (i < columnCount - 1)
			{
				streamWriter.Write(',');
			}
		}
		streamWriter.Write(streamWriter.NewLine);
		foreach (DataRow row in table.Rows)
		{
			for (int i = 0; i < columnCount; i++)
			{
				object cell = row[i];
				if (!Convert.IsDBNull(cell))
				{
					string value = cell.ToString() ?? string.Empty;
					if (value.Contains(','))
					{
						streamWriter.Write('"');
						streamWriter.Write(value);
						streamWriter.Write('"');
					}
					else
					{
						streamWriter.Write(value);
					}
				}
				if (i < columnCount - 1)
				{
					streamWriter.Write(',');
				}
			}
			streamWriter.Write(streamWriter.NewLine);
		}
		await streamWriter.FlushAsync().ConfigureAwait(false);
		return ms.Length;
	}
}

/// <summary>CountingStream.CopyToAsync: new byte[bufferSize] per copy vs ArrayPool rental.</summary>
[MemoryDiagnoser]
[RankColumn]
public class OptStreamCopyBufferBenchmarks
{
	private readonly byte[] payload = new byte[64 * 1024];

	[Params(4096, 81920)]
	public int BufferSize { get; set; }

	[Benchmark(Baseline = true)]
	public async Task Original_NewBuffer()
	{
		using MemoryStream source = new(payload, writable: false);
		byte[] buffer = new byte[BufferSize];
		int bytesRead;
		while ((bytesRead = await source.ReadAsync(buffer).ConfigureAwait(false)) != 0)
		{
			await Stream.Null.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
		}
	}

	[Benchmark]
	public async Task Shared_ArrayPool()
	{
		using MemoryStream source = new(payload, writable: false);
		byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
		try
		{
			int bytesRead;
			while ((bytesRead = await source.ReadAsync(buffer.AsMemory(0, BufferSize)).ConfigureAwait(false)) != 0)
			{
				await Stream.Null.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}
}

/// <summary>
/// End-to-end calls into the real CommonNetFuncs.Core APIs. Run once before and once after applying the optimizations to confirm the
/// gains survive in the shipped code paths.
/// </summary>
[MemoryDiagnoser]
public class OptLibraryBeforeAfterBenchmarks
{
	private readonly SimpleClass equalA = new() { Id = 1, Name = "Alpha", Value = 1.5, IsActive = true, CreatedDate = new DateTime(2026, 1, 1) };
	private readonly SimpleClass equalB = new() { Id = 1, Name = "Alpha", Value = 1.5, IsActive = true, CreatedDate = new DateTime(2026, 1, 1) };
	private readonly SimpleClass different = new() { Id = 2, Name = "Beta", Value = 2.5, IsActive = false, CreatedDate = new DateTime(2025, 1, 1) };
	private readonly SimpleClass partiallyDefault = new() { Id = 0, Name = "x", Value = 0, IsActive = true };
	private readonly List<int> list = Enumerable.Range(0, 1000).ToList();

	[Benchmark]
	public int GetRandomInt() => CommonNetFuncs.Core.Random.GetRandomInt(0, 1000);

	[Benchmark]
	public string GenerateRandomStringByCharSet_32() => CommonNetFuncs.Core.Random.GenerateRandomStringByCharSet(32);

	[Benchmark]
	public string GenerateRandomString_32() => CommonNetFuncs.Core.Random.GenerateRandomString(32);

	[Benchmark]
	public int GetRandomElement_List() => CommonNetFuncs.Core.Random.GetRandomElement(list);

	[Benchmark]
	public string ParsePascalCase() => CommonNetFuncs.Core.Strings.ParsePascalCase("ThisIsAPascalCaseStringWithSeveralWords");

	[Benchmark]
	public string? GetOnlyNumbers() => CommonNetFuncs.Core.Strings.GetOnlyNumbers("  Order #12345 shipped on 2026-11-12 for $1,234.56 total  ");

	[Benchmark]
	public bool IsEqual_Equal() => CommonNetFuncs.Core.Inspect.IsEqual(equalA, equalB);

	[Benchmark]
	public bool IsEqual_Different() => CommonNetFuncs.Core.Inspect.IsEqual(equalA, different);

	[Benchmark]
	public int CountDefaultProps() => CommonNetFuncs.Core.Inspect.CountDefaultProps(partiallyDefault);
}
