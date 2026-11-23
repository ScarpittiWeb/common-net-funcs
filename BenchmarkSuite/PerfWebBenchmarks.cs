using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using CommonNetFuncs.Web.Common.CachingSupportClasses;
using CommonNetFuncs.Web.Requests.Rest;
using CommonNetFuncs.Web.Requests.Rest.Options;
using MessagePack;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ZLinq;
using CT = CommonNetFuncs.Web.Common.ContentTypes;
using ECompressionType = CommonNetFuncs.Compression.Streams.ECompressionType;

namespace BenchmarkSuite;

// Web.* hot-path candidates. Each class pits a verbatim private copy of the current library implementation ("Original", baseline)
// against the proposed replacement(s). PwVerify.Run() asserts Original and Optimized produce identical outcomes on edge-case inputs.

#region 1. DataTableHelpers.GetDataTableRequest

/// <summary>DataTableHelpers.GetDataTableRequest: O(n²) culture-aware form scans with per-element string allocations vs one form snapshot and a single linear pass per key.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwDataTableRequestBenchmarks
{
	private HttpRequest request = null!;

	[Params(10, 30)]
	public int ColumnCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		request = PwFixtures.CreateDataTableContext(ColumnCount, 2).Request;
	}

	[Benchmark(Baseline = true)]
	public PwDataTableRequest Original()
	{
		return PwDataTableHelpers.Original(request);
	}

	[Benchmark]
	public PwDataTableRequest Optimized()
	{
		return PwDataTableHelpers.Optimized(request);
	}
}

public sealed class PwDataTableRequest
{
	public int PageSize { get; set; }

	public int Skip { get; set; }

	public Dictionary<int, string?> SortColumnDir { get; set; } = [];

	public Dictionary<int, string?> SortColumns { get; set; } = [];

	public string? Draw { get; set; }

	public Dictionary<string, string?> SearchValues { get; set; } = [];
}

internal static class PwDataTableHelpers
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	// Verbatim copy of DataTableHelpers.GetDataTableRequest (CommonNetFuncs.Web.Interface\DataTableHelpers.cs)
	internal static PwDataTableRequest Original(HttpRequest request)
	{
		PwDataTableRequest dataTableRequest = new();
		try
		{
			if (CT.FormDataTypes.Any(x => request.ContentType?.Contains(x, StringComparison.InvariantCultureIgnoreCase) ?? false) && request.Form != null)
			{
				dataTableRequest.Draw = request.Form.AsValueEnumerable().Where(x => string.Equals(x.Key, "draw", StringComparison.InvariantCultureIgnoreCase)).Select(x => x.Value).FirstOrDefault();

				for (int i = 0; request.Form.Any(y => string.Equals(y.Key, $"order[{i}][column]", StringComparison.InvariantCultureIgnoreCase)); i++)
				{
					dataTableRequest.SortColumns.Add(i, request.Form.AsValueEnumerable().Where(x => string.Equals(x.Key, "columns[" + request.Form.AsValueEnumerable().Where(y => string.Equals(y.Key, $"order[{i}][column]", StringComparison.InvariantCultureIgnoreCase))
						.Select(z => z.Value).FirstOrDefault() + "][data]", StringComparison.InvariantCultureIgnoreCase)).Select(x => x.Value).FirstOrDefault());

					dataTableRequest.SortColumnDir.Add(i, request.Form.AsValueEnumerable().Where(x => string.Equals(x.Key, $"order[{i}][dir]", StringComparison.InvariantCultureIgnoreCase)).Select(x => x.Value).FirstOrDefault());
				}

				string? start = request.Form.AsValueEnumerable().Where(x => string.Equals(x.Key, "start", StringComparison.InvariantCultureIgnoreCase)).Select(x => x.Value).FirstOrDefault();
				string? length = request.Form.AsValueEnumerable().Where(x => string.Equals(x.Key, "length", StringComparison.InvariantCultureIgnoreCase)).Select(x => x.Value).FirstOrDefault();
				string? searchValue = request.Form.AsValueEnumerable().Where(x => string.Equals(x.Key, "search[value]", StringComparison.InvariantCultureIgnoreCase)).Select(x => x.Value).FirstOrDefault();

				//Paging Size (10,20,50,100)
				dataTableRequest.PageSize = length != StringValues.Empty ? Convert.ToInt32(length) : 0;
				dataTableRequest.Skip = start != StringValues.Empty ? Convert.ToInt32(start) : 0;

				//Get search value key pairs
				if (!string.IsNullOrEmpty(searchValue))
				{
					foreach (string val in searchValue.Split(','))
					{
						string cleanVal = CleanQueryParam(val)!;
						int startPos = cleanVal.IndexOf('=') + 1;
						if (startPos == 0)
						{
							continue;
						}
						string key = cleanVal[..(startPos - 1)];
						if (startPos + 1 <= cleanVal.Length)
						{
							int valLength = cleanVal.Length - startPos;
							string outVal = cleanVal.Substring(startPos, valLength);
							dataTableRequest.SearchValues.Add(key, outVal);
						}
						else
						{
							dataTableRequest.SearchValues.Add(key, null);
						}
					}
				}
			}
			else
			{
				logger.Warn($"Unable to read Datatable request: Body is not a valid form data type [{string.Join(", ", CT.FormDataTypes)}], or form data is null");
			}
		}
		catch (Exception ex)
		{
			logger.Error(ex, "{Class}.{Method} Error", "DataTableHelpers.GetDataTableRequest");
		}
		return dataTableRequest;
	}

	// Snapshot the form once (preserving enumeration order) and resolve each key with a single first-match pass using the same comparison.
	internal static PwDataTableRequest Optimized(HttpRequest request)
	{
		PwDataTableRequest dataTableRequest = new();
		try
		{
			if (CT.FormDataTypes.Any(x => request.ContentType?.Contains(x, StringComparison.InvariantCultureIgnoreCase) ?? false) && request.Form != null)
			{
				KeyValuePair<string, StringValues>[] fields = SnapshotForm(request.Form);

				dataTableRequest.Draw = FindFirst(fields, "draw");

				for (int i = 0; TryFindFirst(fields, $"order[{i}][column]", out StringValues orderColumn); i++)
				{
					dataTableRequest.SortColumns.Add(i, FindFirst(fields, "columns[" + orderColumn + "][data]"));
					dataTableRequest.SortColumnDir.Add(i, FindFirst(fields, $"order[{i}][dir]"));
				}

				string? start = FindFirst(fields, "start");
				string? length = FindFirst(fields, "length");
				string? searchValue = FindFirst(fields, "search[value]");

				dataTableRequest.PageSize = length != StringValues.Empty ? Convert.ToInt32(length) : 0;
				dataTableRequest.Skip = start != StringValues.Empty ? Convert.ToInt32(start) : 0;

				if (!string.IsNullOrEmpty(searchValue))
				{
					foreach (string val in searchValue.Split(','))
					{
						string cleanVal = CleanQueryParam(val)!;
						int startPos = cleanVal.IndexOf('=') + 1;
						if (startPos == 0)
						{
							continue;
						}
						string key = cleanVal[..(startPos - 1)];
						if (startPos + 1 <= cleanVal.Length)
						{
							int valLength = cleanVal.Length - startPos;
							string outVal = cleanVal.Substring(startPos, valLength);
							dataTableRequest.SearchValues.Add(key, outVal);
						}
						else
						{
							dataTableRequest.SearchValues.Add(key, null);
						}
					}
				}
			}
			else
			{
				logger.Warn($"Unable to read Datatable request: Body is not a valid form data type [{string.Join(", ", CT.FormDataTypes)}], or form data is null");
			}
		}
		catch (Exception ex)
		{
			logger.Error(ex, "{Class}.{Method} Error", "DataTableHelpers.GetDataTableRequest");
		}
		return dataTableRequest;
	}

	private static KeyValuePair<string, StringValues>[] SnapshotForm(IFormCollection form)
	{
		KeyValuePair<string, StringValues>[] fields = new KeyValuePair<string, StringValues>[form.Count];
		int count = 0;
		foreach (KeyValuePair<string, StringValues> field in form)
		{
			if (count == fields.Length)
			{
				Array.Resize(ref fields, (count * 2) + 1);
			}
			fields[count++] = field;
		}

		if (count != fields.Length)
		{
			Array.Resize(ref fields, count);
		}
		return fields;
	}

	private static StringValues FindFirst(KeyValuePair<string, StringValues>[] fields, string key)
	{
		TryFindFirst(fields, key, out StringValues value);
		return value;
	}

	private static bool TryFindFirst(KeyValuePair<string, StringValues>[] fields, string key, out StringValues value)
	{
		foreach (KeyValuePair<string, StringValues> field in fields)
		{
			if (string.Equals(field.Key, key, StringComparison.InvariantCultureIgnoreCase))
			{
				value = field.Value;
				return true;
			}
		}
		value = default;
		return false;
	}

	// Verbatim copy of QueryParameters.CleanQueryParam(string?) (CommonNetFuncs.Sql.Common)
	private static string? CleanQueryParam(string? value)
	{
		return CommonNetFuncs.Core.Strings.MakeNullNull(value)?.Replace("\n", string.Empty).Trim();
	}
}

#endregion

#region 2. MemoryCacheMiddleware.GenerateCacheKey

/// <summary>MemoryCacheMiddleware.GenerateCacheKey: LINQ Where/OrderBy + per-pair interpolation + byte[]/hash/base64 allocations vs pooled stable sort and stack-only hashing.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwCacheKeyBenchmarks
{
	private DefaultHttpContext context = null!;
	private PwCacheKeyGenerator generator = null!;

	[Params(false, true)]
	public bool IsPost { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		generator = new PwCacheKeyGenerator(new CacheOptions());
		context = PwFixtures.CreateCacheKeyContext(IsPost, "?useCache=true&zeta=1&alpha=2&evictCacheEntry=false&tags=a&tags=b&Beta=3&page=10&pageSize=50", PwFixtures.CreatePostBody(2048));
	}

	[Benchmark(Baseline = true)]
	public Task<string> Original()
	{
		return generator.OriginalGenerateCacheKey(context);
	}

	[Benchmark]
	public Task<string> Optimized()
	{
		return generator.OptimizedGenerateCacheKey(context);
	}
}

internal sealed class PwCacheKeyGenerator(CacheOptions cacheOptions)
{
	private readonly CacheOptions cacheOptions = cacheOptions;

	private readonly record struct PwQueryEntry(string Key, StringValues Value, int Index);

	// Culture-aware key order (same as OrderBy(x => x.Key)) with the original position as tie-breaker, which reproduces OrderBy's stability.
	private static readonly Comparison<PwQueryEntry> QueryEntryComparison = static (a, b) =>
	{
		int comparison = Comparer<string>.Default.Compare(a.Key, b.Key);
		return comparison != 0 ? comparison : a.Index.CompareTo(b.Index);
	};

	// Verbatim copy of MemoryCacheMiddleware.GenerateCacheKey (CommonNetFuncs.Web.Middleware\CachingMiddleware\MemoryCacheMiddleware.cs)
	internal async Task<string> OriginalGenerateCacheKey(HttpContext context)
	{
		StringBuilder stringBuilder = new();
		stringBuilder.Append(context.Request.Path.Value);

		// Use StringValues directly instead of converting to string
		IOrderedEnumerable<KeyValuePair<string, StringValues>> queryValues = context.Request.Query.Where(x => !CommonNetFuncs.Core.Strings.StrComp(x.Key, cacheOptions.EvictionQueryParam)).OrderBy(x => x.Key);

		if (queryValues.Any())
		{
			stringBuilder.Append('?');
			stringBuilder.AppendJoin('&', queryValues.Select(q => $"{q.Key}={q.Value}"));
		}

		if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
		{
			context.Request.EnableBuffering();
			using StreamReader reader = new(context.Request.Body, leaveOpen: true);
			string body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
			context.Request.Body.Position = 0;

			stringBuilder.Append('|').Append(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))));
		}

		stringBuilder.Append('|').Append(context.Request.Headers.Accept);
		return stringBuilder.ToString();
	}

	internal async Task<string> OptimizedGenerateCacheKey(HttpContext context)
	{
		StringBuilder stringBuilder = new();
		stringBuilder.Append(context.Request.Path.Value);
		AppendSortedQuery(stringBuilder, context.Request.Query, cacheOptions.EvictionQueryParam);

		if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
		{
			context.Request.EnableBuffering();
			using StreamReader reader = new(context.Request.Body, leaveOpen: true);
			string body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
			context.Request.Body.Position = 0;

			stringBuilder.Append('|');
			AppendBodyHash(stringBuilder, body);
		}

		stringBuilder.Append('|').Append(context.Request.Headers.Accept);
		return stringBuilder.ToString();
	}

	private static void AppendSortedQuery(StringBuilder stringBuilder, IQueryCollection query, string evictionQueryParam)
	{
		int queryCount = query.Count;
		if (queryCount == 0)
		{
			return;
		}

		PwQueryEntry[] buffer = ArrayPool<PwQueryEntry>.Shared.Rent(queryCount);
		int count = 0;
		try
		{
			foreach (KeyValuePair<string, StringValues> kvp in query)
			{
				if (CommonNetFuncs.Core.Strings.StrComp(kvp.Key, evictionQueryParam))
				{
					continue;
				}

				if (count == buffer.Length)
				{
					PwQueryEntry[] larger = ArrayPool<PwQueryEntry>.Shared.Rent(count * 2);
					buffer.AsSpan(0, count).CopyTo(larger);
					ArrayPool<PwQueryEntry>.Shared.Return(buffer, clearArray: true);
					buffer = larger;
				}

				buffer[count] = new PwQueryEntry(kvp.Key, kvp.Value, count);
				count++;
			}

			if (count == 0)
			{
				return;
			}

			Span<PwQueryEntry> entries = buffer.AsSpan(0, count);
			if (count > 1)
			{
				entries.Sort(QueryEntryComparison);
			}

			stringBuilder.Append('?');
			for (int i = 0; i < entries.Length; i++)
			{
				if (i > 0)
				{
					stringBuilder.Append('&');
				}
				stringBuilder.Append(entries[i].Key).Append('=').Append(entries[i].Value.ToString());
			}
		}
		finally
		{
			ArrayPool<PwQueryEntry>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static void AppendBodyHash(StringBuilder stringBuilder, string body)
	{
		byte[] rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(body));
		try
		{
			int written = Encoding.UTF8.GetBytes(body.AsSpan(), rented.AsSpan());
			Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
			SHA256.HashData(rented.AsSpan(0, written), hash);

			Span<char> base64 = stackalloc char[44];
			Convert.TryToBase64Chars(hash, base64, out int charsWritten);
			stringBuilder.Append(base64[..charsWritten]);
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(rented);
		}
	}
}

#endregion

#region 3. ContentTypes.GetContentType / GetContentTypeByExtension

/// <summary>ContentTypes.GetContentType: Path.GetExtension string + ToLowerInvariant string per call vs span extension lowered into a stack buffer and a span switch.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwContentTypeBenchmarks
{
	private static readonly string[] FileNames = ["report.PDF", "photo.jpeg", "data.json", "archive.zip", "sheet.xlsx", "clip.mp4", "page.html", "blob.unknownext"];

	[Benchmark(Baseline = true, OperationsPerInvoke = 8)]
	public int Original()
	{
		int total = 0;
		foreach (string fileName in FileNames)
		{
			total += PwContentTypes.OriginalGetContentType(fileName).Length;
		}
		return total;
	}

	[Benchmark(OperationsPerInvoke = 8)]
	public int Optimized()
	{
		int total = 0;
		foreach (string fileName in FileNames)
		{
			total += PwContentTypes.OptimizedGetContentType(fileName).Length;
		}
		return total;
	}
}

internal static class PwContentTypes
{
	// Longer than the longest constant case (".mszipupload" = 12 chars), so anything longer can never hit a constant arm.
	private const int MaxKnownExtensionLength = 16;

	// Verbatim copy of ContentTypes.GetContentType (CommonNetFuncs.Web.Common\ContentTypes.cs)
	internal static string OriginalGetContentType(string fileName)
	{
		if (string.IsNullOrEmpty(fileName))
		{
			throw new ArgumentException("File name cannot be null or empty.", nameof(fileName));
		}
		return OriginalGetContentTypeByExtension(Path.GetExtension(fileName));
	}

	// Verbatim copy of ContentTypes.GetContentTypeByExtension
	internal static string OriginalGetContentTypeByExtension(string extension)
	{
		if (string.IsNullOrEmpty(extension))
		{
			throw new ArgumentException("File extension cannot be null or empty.", nameof(extension));
		}
		// Ensure the extension starts with a dot
		if (!extension.StartsWith('.'))
		{
			extension = $".{extension}";
		}

		// Use switch expression to return content type based on file extension
		return extension.ToLowerInvariant() switch
		{
			".json" => CT.Json,
			".mempack" => CT.MemPack,
			".msgpack" => CT.MsgPack,
			".png" => CT.Png,
			".jpg" or ".jpeg" => CT.Jpeg,
			".gif" => CT.Gif,
			".bmp" => CT.Bmp,
			".tiff" => CT.Tiff,
			".svg" => CT.Svg,
			".xlsx" => CT.Xlsx,
			".xls" => CT.Xls,
			".docx" => CT.Docx,
			".doc" => CT.Doc,
			".pptx" => CT.Pptx,
			".ppt" => CT.Ppt,
			".pdf" => CT.Pdf,
			".csv" => CT.Csv,
			".zip" or ".mszipupload" => CT.Zip,
			".ts" => CT.TransportStream,
			".mpeg" or ".mpg" => CT.Mpeg,
			".mp4" => CT.Mp4,
			".webm" => CT.Webm,
			".flv" => CT.Flv,
			".avi" => CT.Avi,
			".mp3" => CT.Mp3,
			".aac" => CT.Aac,
			_ when CT.FormDataTypes.Contains(extension) => CT.UrlEncodedFormData, // Default to form data types
			_ when extension.StartsWith(".html") || extension.StartsWith(".htm") => CT.Html, // Handle HTML files
			_ when extension.StartsWith(".txt") || extension.StartsWith(".text") => CT.Text, // Handle text files
			_ when extension.StartsWith(".js") || extension.StartsWith(".javascript") => CT.Js, // Handle JavaScript files
			_ when extension.StartsWith(".css") || extension.StartsWith(".stylesheet") => CT.Css, // Handle CSS files
			_ when extension.StartsWith(".xml") || extension.StartsWith(".xhtml") => CT.Xml, // Handle XML files
			_ when extension.StartsWith(".binary") || extension.StartsWith(".octet-stream") => CT.BinaryStream, // Handle binary streams
			_ => "application/octet-stream", // Default content type for unknown extensions
		};
	}

	internal static string OptimizedGetContentType(string fileName)
	{
		if (string.IsNullOrEmpty(fileName))
		{
			throw new ArgumentException("File name cannot be null or empty.", nameof(fileName));
		}

		// Path.GetExtension never returns a value without the leading '.', so the "ensure dot" step of the original is a no-op here.
		ReadOnlySpan<char> extension = Path.GetExtension(fileName.AsSpan());
		if (extension.IsEmpty)
		{
			throw new ArgumentException("File extension cannot be null or empty.", "extension");
		}
		return LookupKnownExtension(extension) ?? MatchUnknownExtension(extension.ToString());
	}

	internal static string OptimizedGetContentTypeByExtension(string extension)
	{
		if (string.IsNullOrEmpty(extension))
		{
			throw new ArgumentException("File extension cannot be null or empty.", nameof(extension));
		}
		if (!extension.StartsWith('.'))
		{
			extension = $".{extension}";
		}
		return LookupKnownExtension(extension) ?? MatchUnknownExtension(extension);
	}

	private static string? LookupKnownExtension(ReadOnlySpan<char> extension)
	{
		if (extension.Length > MaxKnownExtensionLength)
		{
			return null;
		}

		Span<char> loweredBuffer = stackalloc char[MaxKnownExtensionLength];
		int length = extension.ToLowerInvariant(loweredBuffer);
		ReadOnlySpan<char> lowered = loweredBuffer[..length];
		return lowered switch
		{
			".json" => CT.Json,
			".mempack" => CT.MemPack,
			".msgpack" => CT.MsgPack,
			".png" => CT.Png,
			".jpg" or ".jpeg" => CT.Jpeg,
			".gif" => CT.Gif,
			".bmp" => CT.Bmp,
			".tiff" => CT.Tiff,
			".svg" => CT.Svg,
			".xlsx" => CT.Xlsx,
			".xls" => CT.Xls,
			".docx" => CT.Docx,
			".doc" => CT.Doc,
			".pptx" => CT.Pptx,
			".ppt" => CT.Ppt,
			".pdf" => CT.Pdf,
			".csv" => CT.Csv,
			".zip" or ".mszipupload" => CT.Zip,
			".ts" => CT.TransportStream,
			".mpeg" or ".mpg" => CT.Mpeg,
			".mp4" => CT.Mp4,
			".webm" => CT.Webm,
			".flv" => CT.Flv,
			".avi" => CT.Avi,
			".mp3" => CT.Mp3,
			".aac" => CT.Aac,
			_ => null,
		};
	}

	// The original's guarded arms, unchanged: they test the dotted, NON-lowered extension with culture-sensitive StartsWith.
	private static string MatchUnknownExtension(string extension)
	{
		if (CT.FormDataTypes.Contains(extension))
		{
			return CT.UrlEncodedFormData;
		}
		if (extension.StartsWith(".html") || extension.StartsWith(".htm"))
		{
			return CT.Html;
		}
		if (extension.StartsWith(".txt") || extension.StartsWith(".text"))
		{
			return CT.Text;
		}
		if (extension.StartsWith(".js") || extension.StartsWith(".javascript"))
		{
			return CT.Js;
		}
		if (extension.StartsWith(".css") || extension.StartsWith(".stylesheet"))
		{
			return CT.Css;
		}
		if (extension.StartsWith(".xml") || extension.StartsWith(".xhtml"))
		{
			return CT.Xml;
		}
		if (extension.StartsWith(".binary") || extension.StartsWith(".octet-stream"))
		{
			return CT.BinaryStream;
		}
		return "application/octet-stream";
	}
}

#endregion

#region 4. UriHelpers.ListToQueryParameters

/// <summary>UriHelpers.ListToQueryParameters: StringBuilder.Append(object) boxes + ToString()s every value-type item vs typed Append overloads (format straight into the builder).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PwListToQueryParametersBenchmarks
{
	private List<int> ints = null!;
	private List<double> doubles = null!;

	[GlobalSetup]
	public void Setup()
	{
		System.Random random = new(42);
		ints = Enumerable.Range(0, 50).Select(_ => random.Next(-100000, 100000)).ToList();
		doubles = Enumerable.Range(0, 50).Select(_ => random.NextDouble() * 1000).ToList();
	}

	[Benchmark(Baseline = true), BenchmarkCategory("Int")]
	public string Original_Int()
	{
		return PwUriHelpers.OriginalListToQueryParameters(ints, "id");
	}

	[Benchmark, BenchmarkCategory("Int")]
	public string Optimized_Int()
	{
		return PwUriHelpers.OptimizedListToQueryParameters(ints, "id");
	}

	[Benchmark(Baseline = true), BenchmarkCategory("Double")]
	public string Original_Double()
	{
		return PwUriHelpers.OriginalListToQueryParameters(doubles, "value");
	}

	[Benchmark, BenchmarkCategory("Double")]
	public string Optimized_Double()
	{
		return PwUriHelpers.OptimizedListToQueryParameters(doubles, "value");
	}
}

internal static class PwUriHelpers
{
	// Verbatim copy of UriHelpers.ListToQueryParameters<T>(IEnumerable<T>?, string?) (CommonNetFuncs.Web.Common\UriHelpers.cs)
	internal static string OriginalListToQueryParameters<T>(IEnumerable<T>? parameters, string? queryParameterName)
	{
		if (parameters?.Any() != true || CommonNetFuncs.Core.Strings.IsNullOrWhiteSpace(queryParameterName))
		{
			return string.Empty;
		}

		StringBuilder stringBuilder = new();
		bool firstItem = true;
		foreach (T parameter in parameters)
		{
			if (!firstItem)
			{
				stringBuilder.Append('&');
			}
			stringBuilder.Append(queryParameterName);
			stringBuilder.Append('=');
			stringBuilder.Append(parameter);
			firstItem = false;
		}
		return stringBuilder.ToString();
	}

	internal static string OptimizedListToQueryParameters<T>(IEnumerable<T>? parameters, string? queryParameterName)
	{
		if (parameters?.Any() != true || CommonNetFuncs.Core.Strings.IsNullOrWhiteSpace(queryParameterName))
		{
			return string.Empty;
		}

		StringBuilder stringBuilder = new();
		bool firstItem = true;
		foreach (T parameter in parameters)
		{
			if (!firstItem)
			{
				stringBuilder.Append('&');
			}
			stringBuilder.Append(queryParameterName);
			stringBuilder.Append('=');
			AppendValue(stringBuilder, parameter);
			firstItem = false;
		}
		return stringBuilder.ToString();
	}

	// The typeof(T) checks are JIT-time constants, so each instantiation keeps exactly one branch and the (X)(object)value casts do not box.
	// Every typed overload formats with format: default / provider: null, i.e. the current culture, exactly like the boxed ToString() call.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal static void AppendValue<T>(StringBuilder stringBuilder, T value)
	{
		if (typeof(T) == typeof(int))
		{
			stringBuilder.Append((int)(object)value!);
		}
		else if (typeof(T) == typeof(long))
		{
			stringBuilder.Append((long)(object)value!);
		}
		else if (typeof(T) == typeof(short))
		{
			stringBuilder.Append((short)(object)value!);
		}
		else if (typeof(T) == typeof(byte))
		{
			stringBuilder.Append((byte)(object)value!);
		}
		else if (typeof(T) == typeof(sbyte))
		{
			stringBuilder.Append((sbyte)(object)value!);
		}
		else if (typeof(T) == typeof(uint))
		{
			stringBuilder.Append((uint)(object)value!);
		}
		else if (typeof(T) == typeof(ulong))
		{
			stringBuilder.Append((ulong)(object)value!);
		}
		else if (typeof(T) == typeof(ushort))
		{
			stringBuilder.Append((ushort)(object)value!);
		}
		else if (typeof(T) == typeof(double))
		{
			stringBuilder.Append((double)(object)value!);
		}
		else if (typeof(T) == typeof(float))
		{
			stringBuilder.Append((float)(object)value!);
		}
		else if (typeof(T) == typeof(decimal))
		{
			stringBuilder.Append((decimal)(object)value!);
		}
		else if (typeof(T) == typeof(bool))
		{
			stringBuilder.Append((bool)(object)value!);
		}
		else if (typeof(T) == typeof(char))
		{
			stringBuilder.Append((char)(object)value!);
		}
		else if (typeof(T) == typeof(Guid))
		{
			stringBuilder.Append($"{(Guid)(object)value!}");
		}
		else
		{
			stringBuilder.Append(value);
		}
	}
}

#endregion

#region 5. DenyCharactersAttribute / ListDenyCharactersAttribute

/// <summary>Deny-character validation: IndexOfAny(span) rebuilds its probabilistic map on every call (and ListDeny* allocates a method-group delegate per IsValid) vs cached SearchValues + cached delegate.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PwDenyCharactersBenchmarks
{
	private const string DenyChars = "<>&;'\"%$";

	private readonly PwOriginalDenyCharactersAttribute originalSingle = new(DenyChars);
	private readonly PwOptimizedDenyCharactersAttribute optimizedSingle = new(DenyChars);
	private readonly PwOriginalListDenyCharactersAttribute originalList = new(DenyChars);
	private readonly PwOptimizedListDenyCharactersAttribute optimizedList = new(DenyChars);
	private readonly ValidationContext validationContext = new(new object()) { MemberName = "Value", DisplayName = "Value" };

	private string singleValue = null!;
	private List<string> listValue = null!;

	[GlobalSetup]
	public void Setup()
	{
		singleValue = "The quick brown fox jumps over the lazy dog while carrying 42 plain characters only";
		listValue = Enumerable.Range(0, 20).Select(i => $"list item number {i} with only safe characters in it").ToList();
	}

	[Benchmark(Baseline = true), BenchmarkCategory("Single")]
	public ValidationResult? Original_Single()
	{
		return originalSingle.GetValidationResult(singleValue, validationContext);
	}

	[Benchmark, BenchmarkCategory("Single")]
	public ValidationResult? Optimized_Single()
	{
		return optimizedSingle.GetValidationResult(singleValue, validationContext);
	}

	[Benchmark(Baseline = true), BenchmarkCategory("List")]
	public ValidationResult? Original_List()
	{
		return originalList.GetValidationResult(listValue, validationContext);
	}

	[Benchmark, BenchmarkCategory("List")]
	public ValidationResult? Optimized_List()
	{
		return optimizedList.GetValidationResult(listValue, validationContext);
	}
}

internal static class PwValidationHelpers
{
	// Verbatim copy of ValidationAttributeHelpers.ValidateStringList (CommonNetFuncs.Web.Common\ValidationAttributes\ValidationAttributeHelpers.cs)
	internal static ValidationResult? ValidateStringList(object? value, ValidationContext validationContext, string attributeName,
		Func<IEnumerable<string?>, string, ValidationResult?> validateEnumerable)
	{
		if (value is null)
		{
			return ValidationResult.Success;
		}

		string memberName = validationContext.MemberName ?? string.Empty;

		// Handle different types of collections
		if (value is IEnumerable<string?> enumerable)
		{
			return validateEnumerable(enumerable, memberName) ?? ValidationResult.Success;
		}
		else if (CommonNetFuncs.Core.TypeChecks.IsEnumerable(value.GetType()))
		{
			return validateEnumerable(((IEnumerable)value).Cast<object?>().Select(x => Convert.ToString(x, CultureInfo.CurrentCulture)), memberName) ?? ValidationResult.Success;
		}
		throw new InvalidDataException($"${attributeName} can only be used on properties that implement IEnumerable");
	}
}

// Verbatim copy of DenyCharactersAttribute (string constructor) (CommonNetFuncs.Web.Common\ValidationAttributes\DenyCharactersAttribute.cs)
internal sealed class PwOriginalDenyCharactersAttribute : ValidationAttribute
{
	public string DenyCharacters { get; }

	public PwOriginalDenyCharactersAttribute(string denyCharacters)
	{
		ArgumentNullException.ThrowIfNull(denyCharacters);

		if (denyCharacters.Length == 0)
		{
			throw new ArgumentException("Deny characters cannot be empty", nameof(denyCharacters));
		}

		DenyCharacters = denyCharacters;
		ErrorMessage = "The field {0} must not contain any of the following characters: '{1}'.";
	}

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		if (value is null)
		{
			return ValidationResult.Success;
		}

		string? stringValue = Convert.ToString(value, CultureInfo.CurrentCulture);

		if (string.IsNullOrEmpty(stringValue))
		{
			return ValidationResult.Success;
		}

		if (CommonNetFuncs.Core.Strings.ContainsAnyCharacter(stringValue, DenyCharacters.AsSpan()))
		{
			string memberName = validationContext.MemberName ?? string.Empty;
			return new ValidationResult(
				FormatErrorMessage(validationContext.DisplayName),
				string.IsNullOrEmpty(memberName) ? null : new[] { memberName }
			);
		}

		return ValidationResult.Success;
	}

	public override string FormatErrorMessage(string name)
	{
		return string.IsNullOrEmpty(ErrorMessageString)
			? $"The field {name} must not contain any of the following characters: '{DenyCharacters}'."
			: string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, DenyCharacters);
	}
}

internal sealed class PwOptimizedDenyCharactersAttribute : ValidationAttribute
{
	private readonly SearchValues<char> denySearchValues;

	public string DenyCharacters { get; }

	public PwOptimizedDenyCharactersAttribute(string denyCharacters)
	{
		ArgumentNullException.ThrowIfNull(denyCharacters);

		if (denyCharacters.Length == 0)
		{
			throw new ArgumentException("Deny characters cannot be empty", nameof(denyCharacters));
		}

		DenyCharacters = denyCharacters;
		denySearchValues = SearchValues.Create(denyCharacters.AsSpan());
		ErrorMessage = "The field {0} must not contain any of the following characters: '{1}'.";
	}

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		if (value is null)
		{
			return ValidationResult.Success;
		}

		string? stringValue = Convert.ToString(value, CultureInfo.CurrentCulture);

		if (string.IsNullOrEmpty(stringValue))
		{
			return ValidationResult.Success;
		}

		// Same result as IndexOfAny(DenyCharacters) >= 0 (both inputs are non-empty here), but the lookup structure is built once
		if (stringValue.AsSpan().ContainsAny(denySearchValues))
		{
			string memberName = validationContext.MemberName ?? string.Empty;
			return new ValidationResult(
				FormatErrorMessage(validationContext.DisplayName),
				string.IsNullOrEmpty(memberName) ? null : new[] { memberName }
			);
		}

		return ValidationResult.Success;
	}

	public override string FormatErrorMessage(string name)
	{
		return string.IsNullOrEmpty(ErrorMessageString)
			? $"The field {name} must not contain any of the following characters: '{DenyCharacters}'."
			: string.Format(CultureInfo.CurrentCulture, ErrorMessageString, name, DenyCharacters);
	}
}

// Verbatim copy of ListDenyCharactersAttribute (string constructor) (CommonNetFuncs.Web.Common\ValidationAttributes\ListDenyCharactersAttribute.cs)
internal sealed class PwOriginalListDenyCharactersAttribute : ValidationAttribute
{
	public string DenyCharacters { get; }

	public PwOriginalListDenyCharactersAttribute(string denyCharacters)
	{
		ArgumentNullException.ThrowIfNull(denyCharacters);

		if (denyCharacters.Length == 0)
		{
			throw new ArgumentException("Deny characters cannot be empty", nameof(denyCharacters));
		}

		DenyCharacters = denyCharacters;
		ErrorMessage = "Item at index {0} '{1}' must not contain any of the following characters: '{2}'.";
	}

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		return PwValidationHelpers.ValidateStringList(value, validationContext, "ListDenyCharactersAttribute", ValidateEnumerable);
	}

	private ValidationResult? ValidateEnumerable(IEnumerable<string?> values, string memberName)
	{
		int index = 0;
		ReadOnlySpan<char> denyCharactersSpan = DenyCharacters.AsSpan();

		foreach (string? item in values)
		{
			if (!string.IsNullOrEmpty(item) && CommonNetFuncs.Core.Strings.ContainsAnyCharacter(item, denyCharactersSpan)) // Null / empty passes automatically
			{
				return new ValidationResult(
					string.Format(CultureInfo.CurrentCulture, ErrorMessageString, index, CommonNetFuncs.Core.Strings.UrlEncodeReadable(item), DenyCharacters),
					[memberName]
				);
			}
			index++;
		}
		return null;
	}

	public override string FormatErrorMessage(string name)
	{
		return string.Format(CultureInfo.CurrentCulture, ErrorMessageString, "{index}", "{value}", DenyCharacters);
	}
}

internal sealed class PwOptimizedListDenyCharactersAttribute : ValidationAttribute
{
	private readonly SearchValues<char> denySearchValues;
	private readonly Func<IEnumerable<string?>, string, ValidationResult?> validateEnumerable;

	public string DenyCharacters { get; }

	public PwOptimizedListDenyCharactersAttribute(string denyCharacters)
	{
		ArgumentNullException.ThrowIfNull(denyCharacters);

		if (denyCharacters.Length == 0)
		{
			throw new ArgumentException("Deny characters cannot be empty", nameof(denyCharacters));
		}

		DenyCharacters = denyCharacters;
		denySearchValues = SearchValues.Create(denyCharacters.AsSpan());
		validateEnumerable = ValidateEnumerable; // Cached once instead of a new delegate per IsValid call
		ErrorMessage = "Item at index {0} '{1}' must not contain any of the following characters: '{2}'.";
	}

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		return PwValidationHelpers.ValidateStringList(value, validationContext, "ListDenyCharactersAttribute", validateEnumerable);
	}

	private ValidationResult? ValidateEnumerable(IEnumerable<string?> values, string memberName)
	{
		int index = 0;
		foreach (string? item in values)
		{
			if (!string.IsNullOrEmpty(item) && item.AsSpan().ContainsAny(denySearchValues))
			{
				return new ValidationResult(
					string.Format(CultureInfo.CurrentCulture, ErrorMessageString, index, CommonNetFuncs.Core.Strings.UrlEncodeReadable(item), DenyCharacters),
					[memberName]
				);
			}
			index++;
		}
		return null;
	}

	public override string FormatErrorMessage(string name)
	{
		return string.Format(CultureInfo.CurrentCulture, ErrorMessageString, "{index}", "{value}", DenyCharacters);
	}
}

#endregion

#region 6. AllowedNullableValuesAttribute (enum + numeric value path)

/// <summary>AllowedNullableValuesAttribute numeric-vs-enum check: Enum.GetValues + boxing + LINQ on every IsValid vs a lazily cached long[] of the enum's values.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwAllowedNullableValuesBenchmarks
{
	private readonly PwOriginalAllowedNullableValuesAttribute original = new(typeof(PwColor));
	private readonly PwOptimizedAllowedNullableValuesAttribute optimized = new(typeof(PwColor));
	private readonly ValidationContext validationContext = new(new object()) { MemberName = "Color", DisplayName = "Color" };
	private readonly object numericValue = 64;

	[Benchmark(Baseline = true)]
	public ValidationResult? Original()
	{
		return original.GetValidationResult(numericValue, validationContext);
	}

	[Benchmark]
	public ValidationResult? Optimized()
	{
		return optimized.GetValidationResult(numericValue, validationContext);
	}
}

public enum PwColor
{
	Red = 1,
	Green = 2,
	Blue = 4,
	Yellow = 8,
	Cyan = 16,
	Magenta = 32,
	Black = 64,
	White = 128
}

public enum PwBigEnum : ulong
{
	Small = 1,
	Huge = ulong.MaxValue
}

// Verbatim copy of AllowedNullableValuesAttribute (CommonNetFuncs.Web.Common\ValidationAttributes\AllowedNullableValuesAttribute.cs)
internal sealed class PwOriginalAllowedNullableValuesAttribute : ValidationAttribute
{
	private readonly IEnumerable<object> allowedValues;
	private readonly bool allowNull;
	private readonly Type? enumType;

	public PwOriginalAllowedNullableValuesAttribute(params object[] allowedValues) : this(true, allowedValues) { }

	public PwOriginalAllowedNullableValuesAttribute(bool allowNull, params object[] allowedValues)
	{
		this.allowedValues = allowedValues ?? throw new ArgumentNullException(nameof(allowedValues));
		this.allowNull = allowNull;
	}

	public PwOriginalAllowedNullableValuesAttribute(Type enumType) : this(true, enumType)
	{
	}

	public PwOriginalAllowedNullableValuesAttribute(bool allowNull, Type enumType)
	{
		if (!enumType.IsEnum)
		{
			throw new ArgumentException("Type must be an enum", nameof(enumType));
		}

		this.enumType = enumType;
		allowedValues = Enum.GetValues(enumType).Cast<object>();
		this.allowNull = allowNull;
	}

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		if (value == null)
		{
			return allowNull ? ValidationResult.Success : new ValidationResult($"The field {validationContext.DisplayName} cannot be null.");
		}

		if (enumType != null)
		{
			if (value.GetType().IsEnum)
			{
				if (!Enum.IsDefined(enumType, value))
				{
					string enumValues = string.Join(", ", Enum.GetNames(enumType));
					return new ValidationResult($"The field {validationContext.DisplayName} must be one of the following values: {enumValues}");
				}
				return ValidationResult.Success;
			}

			if (CommonNetFuncs.Core.TypeChecks.IsNumericType(value.GetType()))
			{
				long numericValue = Convert.ToInt64(value);
				IEnumerable<long> enumValues = Enum.GetValues(enumType).Cast<object>().Select(Convert.ToInt64);

				if (enumValues.Contains(numericValue))
				{
					return ValidationResult.Success;
				}

				IEnumerable<string> enumValuesWithNumbers = Enum.GetNames(enumType).Select(name => $"{name} ({Convert.ToInt64(Enum.Parse(enumType, name))})");
				string validValues = string.Join(", ", enumValuesWithNumbers);

				return new ValidationResult($"The field {validationContext.DisplayName} must be one of the following values: {validValues}");
			}
		}

		if (allowedValues.Any(v => v?.Equals(value) == true))
		{
			return ValidationResult.Success;
		}

		string allowedValuesList = string.Join(", ", allowedValues.Select(v => v?.ToString() ?? "null"));
		return new ValidationResult($"The field {validationContext.DisplayName} must be one of the following values: {allowedValuesList}");
	}
}

internal sealed class PwOptimizedAllowedNullableValuesAttribute : ValidationAttribute
{
	private const int EnumCacheNotCacheable = 2;

	private readonly IEnumerable<object> allowedValues;
	private readonly bool allowNull;
	private readonly Type? enumType;
	private long[]? cachedEnumNumericValues;
	private int enumNumericCacheState;

	public PwOptimizedAllowedNullableValuesAttribute(params object[] allowedValues) : this(true, allowedValues) { }

	public PwOptimizedAllowedNullableValuesAttribute(bool allowNull, params object[] allowedValues)
	{
		this.allowedValues = allowedValues ?? throw new ArgumentNullException(nameof(allowedValues));
		this.allowNull = allowNull;
	}

	public PwOptimizedAllowedNullableValuesAttribute(Type enumType) : this(true, enumType)
	{
	}

	public PwOptimizedAllowedNullableValuesAttribute(bool allowNull, Type enumType)
	{
		if (!enumType.IsEnum)
		{
			throw new ArgumentException("Type must be an enum", nameof(enumType));
		}

		this.enumType = enumType;
		allowedValues = Enum.GetValues(enumType).Cast<object>();
		this.allowNull = allowNull;
	}

	protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
	{
		if (value == null)
		{
			return allowNull ? ValidationResult.Success : new ValidationResult($"The field {validationContext.DisplayName} cannot be null.");
		}

		if (enumType != null)
		{
			if (value.GetType().IsEnum)
			{
				if (!Enum.IsDefined(enumType, value))
				{
					string enumValues = string.Join(", ", Enum.GetNames(enumType));
					return new ValidationResult($"The field {validationContext.DisplayName} must be one of the following values: {enumValues}");
				}
				return ValidationResult.Success;
			}

			if (CommonNetFuncs.Core.TypeChecks.IsNumericType(value.GetType()))
			{
				long numericValue = Convert.ToInt64(value);
				long[]? cachedValues = GetCachedEnumNumericValues(enumType);

				// Falls back to the exact original (lazy, possibly throwing) evaluation when the enum has a value that does not fit in a long
				bool isDefined = cachedValues != null
					? Array.IndexOf(cachedValues, numericValue) >= 0
					: Enum.GetValues(enumType).Cast<object>().Select(Convert.ToInt64).Contains(numericValue);

				if (isDefined)
				{
					return ValidationResult.Success;
				}

				IEnumerable<string> enumValuesWithNumbers = Enum.GetNames(enumType).Select(name => $"{name} ({Convert.ToInt64(Enum.Parse(enumType, name))})");
				string validValues = string.Join(", ", enumValuesWithNumbers);

				return new ValidationResult($"The field {validationContext.DisplayName} must be one of the following values: {validValues}");
			}
		}

		if (allowedValues.Any(v => v?.Equals(value) == true))
		{
			return ValidationResult.Success;
		}

		string allowedValuesList = string.Join(", ", allowedValues.Select(v => v?.ToString() ?? "null"));
		return new ValidationResult($"The field {validationContext.DisplayName} must be one of the following values: {allowedValuesList}");
	}

	// Benign race: concurrent first callers compute identical arrays; the published array is never mutated.
	private long[]? GetCachedEnumNumericValues(Type enumTypeToCache)
	{
		long[]? values = Volatile.Read(ref cachedEnumNumericValues);
		if (values != null || Volatile.Read(ref enumNumericCacheState) == EnumCacheNotCacheable)
		{
			return values;
		}

		try
		{
			Array rawValues = Enum.GetValues(enumTypeToCache);
			values = new long[rawValues.Length];
			for (int i = 0; i < rawValues.Length; i++)
			{
				values[i] = Convert.ToInt64(rawValues.GetValue(i));
			}
		}
		catch (Exception)
		{
			Volatile.Write(ref enumNumericCacheState, EnumCacheNotCacheable);
			return null;
		}

		Volatile.Write(ref cachedEnumNumericValues, values);
		return values;
	}
}

#endregion

#region 7. MessagePackStreamingResult per-item writes

/// <summary>MessagePackStreamingResult item writers: per-item "\n"u8.ToArray() + separate newline write (JSON) and a new ArrayBufferWriter per item (MsgPack) vs cached newline / one reused buffer per response.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PwStreamingWriteBenchmarks
{
	private const int ItemCount = 100;

	private TestModel[] items = null!;
	private PwOriginalStreamingWriter<TestModel> original = null!;

	[GlobalSetup]
	public void Setup()
	{
		items = PwFixtures.CreateTestModels(ItemCount);
		original = new PwOriginalStreamingWriter<TestModel>(null, null);
	}

	[Benchmark(Baseline = true, OperationsPerInvoke = ItemCount), BenchmarkCategory("Json")]
	public async Task Original_Json()
	{
		foreach (TestModel item in items)
		{
			await original.WriteJsonItemAsync(Stream.Null, item, CancellationToken.None).ConfigureAwait(false);
		}
	}

	[Benchmark(OperationsPerInvoke = ItemCount), BenchmarkCategory("Json")]
	public async Task Optimized_Json_StaticNewLine()
	{
		PwOptimizedStreamingWriter<TestModel> writer = new(null, null); // One per response, as it would be inside ExecuteAsync
		foreach (TestModel item in items)
		{
			await writer.WriteJsonItemStaticNewLineAsync(Stream.Null, item, CancellationToken.None).ConfigureAwait(false);
		}
	}

	[Benchmark(OperationsPerInvoke = ItemCount), BenchmarkCategory("Json")]
	public async Task Optimized_Json_ReusedBuffer()
	{
		PwOptimizedStreamingWriter<TestModel> writer = new(null, null);
		foreach (TestModel item in items)
		{
			await writer.WriteJsonItemReusedBufferAsync(Stream.Null, item, CancellationToken.None).ConfigureAwait(false);
		}
	}

	[Benchmark(Baseline = true, OperationsPerInvoke = ItemCount), BenchmarkCategory("MsgPack")]
	public async Task Original_MsgPack()
	{
		foreach (TestModel item in items)
		{
			await original.WriteMessagePackItemAsync(Stream.Null, item, CancellationToken.None).ConfigureAwait(false);
		}
	}

	[Benchmark(OperationsPerInvoke = ItemCount), BenchmarkCategory("MsgPack")]
	public async Task Optimized_MsgPack_ReusedBuffer()
	{
		PwOptimizedStreamingWriter<TestModel> writer = new(null, null);
		foreach (TestModel item in items)
		{
			await writer.WriteMessagePackItemAsync(Stream.Null, item, CancellationToken.None).ConfigureAwait(false);
		}
	}
}

// Verbatim copies of MessagePackStreamingResult<T>.WriteJsonItemAsync / WriteMessagePackItemAsync (CommonNetFuncs.Web.Requests\MessagePack\MessagePackStreamingResult.cs),
// taking response.Body as a Stream parameter.
internal sealed class PwOriginalStreamingWriter<T>(JsonSerializerOptions? jsonOptions, MessagePackSerializerOptions? messagePackOptions)
{
	private readonly JsonSerializerOptions? jsonOptions = jsonOptions;
	private readonly MessagePackSerializerOptions? messagePackOptions = messagePackOptions;

	internal async Task WriteMessagePackItemAsync(Stream body, T item, CancellationToken cancellationToken)
	{
		// Serialize each item to a buffer
		ArrayBufferWriter<byte> bufferWriter = new();
		MessagePackSerializer.Serialize(bufferWriter, item, messagePackOptions ?? MessagePackSerializerOptions.Standard, cancellationToken);

		// Write the serialized bytes to the response stream
		await body.WriteAsync(bufferWriter.WrittenMemory, cancellationToken).ConfigureAwait(false);
		await body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	internal async Task WriteJsonItemAsync(Stream body, T item, CancellationToken cancellationToken)
	{
		// Serialize item as JSON
		byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(item, jsonOptions);

		// Write JSON followed by newline (NDJSON format)
		await body.WriteAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
		await body.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
		await body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}
}

// Instance is per response (created inside ExecuteAsync), so the reused buffer is never shared between concurrent requests.
internal sealed class PwOptimizedStreamingWriter<T>(JsonSerializerOptions? jsonOptions, MessagePackSerializerOptions? messagePackOptions)
{
	private static readonly byte[] NewLine = [(byte)'\n'];

	private readonly JsonSerializerOptions? jsonOptions = jsonOptions;
	private readonly MessagePackSerializerOptions? messagePackOptions = messagePackOptions;
	private readonly ArrayBufferWriter<byte> buffer = new();
	private Utf8JsonWriter? jsonWriter;

	internal async Task WriteMessagePackItemAsync(Stream body, T item, CancellationToken cancellationToken)
	{
		buffer.ResetWrittenCount();
		MessagePackSerializer.Serialize(buffer, item, messagePackOptions ?? MessagePackSerializerOptions.Standard, cancellationToken);

		await body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
		await body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	// Zero-risk variant: identical serializer call, only the per-item newline array allocation is removed
	internal async Task WriteJsonItemStaticNewLineAsync(Stream body, T item, CancellationToken cancellationToken)
	{
		byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(item, jsonOptions);

		await body.WriteAsync(jsonBytes, cancellationToken).ConfigureAwait(false);
		await body.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
		await body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	// Serializes into the reused buffer, appends the newline and issues a single write (same bytes, one write call instead of two)
	internal async Task WriteJsonItemReusedBufferAsync(Stream body, T item, CancellationToken cancellationToken)
	{
		buffer.ResetWrittenCount();
		SerializeJsonLine(item);

		await body.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
		await body.FlushAsync(cancellationToken).ConfigureAwait(false);
	}

	private void SerializeJsonLine(T item)
	{
		JsonSerializerOptions options = jsonOptions ?? JsonSerializerOptions.Default;
		if (jsonWriter == null)
		{
			jsonWriter = new Utf8JsonWriter(buffer, ToWriterOptions(options));
		}
		else
		{
			jsonWriter.Reset(buffer);
		}

		JsonSerializer.Serialize(jsonWriter, item, options);
		jsonWriter.Flush();
		buffer.Write("\n"u8);
	}

	// Mirrors JsonSerializerOptions.GetWriterOptions() (internal), which SerializeToUtf8Bytes uses in release builds
	internal static JsonWriterOptions ToWriterOptions(JsonSerializerOptions options)
	{
		return new JsonWriterOptions
		{
			Encoder = options.Encoder,
			Indented = options.WriteIndented,
			IndentCharacter = options.IndentCharacter,
			IndentSize = options.IndentSize,
			MaxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth,
			NewLine = options.NewLine,
			SkipValidation = true,
		};
	}
}

#endregion

#region 8. DistributedCacheExtensions.SetAsync / TryGetValueAsync

/// <summary>DistributedCacheExtensions: Serialize-to-string + UTF8.GetBytes (Set) and MemoryStream + DeserializeAsync over an in-memory byte[] (Get) vs SerializeToUtf8Bytes / span Deserialize.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class PwDistributedCacheSerializationBenchmarks
{
	private List<TestModel> value = null!;
	private byte[] cachedBytes = null!;

	[Params(1, 50)]
	public int ItemCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		value = [.. PwFixtures.CreateTestModels(ItemCount)];
		cachedBytes = PwDistributedCacheSerialization.OriginalSerialize(value);
	}

	[Benchmark(Baseline = true), BenchmarkCategory("Set")]
	public byte[] Original_Set()
	{
		return PwDistributedCacheSerialization.OriginalSerialize(value);
	}

	[Benchmark, BenchmarkCategory("Set")]
	public byte[] Optimized_Set()
	{
		return PwDistributedCacheSerialization.OptimizedSerialize(value);
	}

	[Benchmark(Baseline = true), BenchmarkCategory("Get")]
	public Task<List<TestModel>?> Original_Get()
	{
		return PwDistributedCacheSerialization.OriginalDeserializeAsync<List<TestModel>>(cachedBytes);
	}

	[Benchmark, BenchmarkCategory("Get")]
	public List<TestModel>? Optimized_Get()
	{
		return PwDistributedCacheSerialization.OptimizedDeserialize<List<TestModel>>(cachedBytes);
	}
}

internal static class PwDistributedCacheSerialization
{
	private static readonly JsonSerializerOptions serializerOptions = new()
	{
		PropertyNamingPolicy = null,
		WriteIndented = true,
		AllowTrailingCommas = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

	// Verbatim: payload creation in DistributedCacheExtensions.SetAsync<T> (CommonNetFuncs.Web.Requests\DistributedCacheExtensions.cs)
	internal static byte[] OriginalSerialize<T>(T value)
	{
		return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, serializerOptions));
	}

	internal static byte[] OptimizedSerialize<T>(T value)
	{
		return JsonSerializer.SerializeToUtf8Bytes(value, serializerOptions);
	}

	// Verbatim: DistributedCacheExtensions.TryGetValueAsync<T> after the cache returned a non-null byte[]
	internal static async Task<T?> OriginalDeserializeAsync<T>(byte[] val)
	{
		await using MemoryStream stream = new(val);
		return await JsonSerializer.DeserializeAsync<T>(stream, serializerOptions).ConfigureAwait(false);
	}

	// The stream reader skips a single leading UTF-8 BOM; Utf8JsonReader over a span does not, so the BOM is sliced off here to keep parity.
	internal static T? OptimizedDeserialize<T>(byte[] val)
	{
		ReadOnlySpan<byte> utf8Json = val;
		if (utf8Json.StartsWith(Utf8Bom))
		{
			utf8Json = utf8Json[Utf8Bom.Length..];
		}
		return JsonSerializer.Deserialize<T>(utf8Json, serializerOptions);
	}
}

#endregion

#region 9. Task-queue processing-time sliding window

/// <summary>EndpointQueue / SequentialTaskProcessor / PrioritizedQueueStatsRecorder: List.Add + RemoveAt(0) (O(window) memmove under the stats lock) vs Queue Enqueue/Dequeue (O(1)).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwProcessingTimeWindowBenchmarks
{
	private const int RecordsPerInvoke = 1000;

	private PwOriginalProcessingTimeWindow original = null!;
	private PwOptimizedProcessingTimeWindow optimized = null!;

	[Params(1000)]
	public int Window { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		original = new PwOriginalProcessingTimeWindow(Window);
		optimized = new PwOptimizedProcessingTimeWindow(Window);
		for (int i = 0; i < Window; i++)
		{
			original.Record(TimeSpan.FromTicks(i));
			optimized.Record(TimeSpan.FromTicks(i));
		}
	}

	[Benchmark(Baseline = true, OperationsPerInvoke = RecordsPerInvoke)]
	public void Original()
	{
		for (int i = 0; i < RecordsPerInvoke; i++)
		{
			original.Record(TimeSpan.FromTicks(i));
		}
	}

	[Benchmark(OperationsPerInvoke = RecordsPerInvoke)]
	public void Optimized()
	{
		for (int i = 0; i < RecordsPerInvoke; i++)
		{
			optimized.Record(TimeSpan.FromTicks(i));
		}
	}
}

// Verbatim copy of the processing-time bookkeeping in EndpointQueue.ProcessTasksAsync / GetCurrentStats (same pattern in SequentialTaskProcessor and PrioritizedQueueStatsRecorder)
internal sealed class PwOriginalProcessingTimeWindow(int processTimeWindow)
{
	private readonly List<TimeSpan> processingTimes = new();
	private readonly Lock statsLock = new();
	private readonly int processTimeWindow = processTimeWindow;

	internal void Record(TimeSpan elapsed)
	{
		lock (statsLock)
		{
			processingTimes.Add(elapsed);

			// Keep only last 100 processing times for average calculation
			if (processingTimes.Count > processTimeWindow)
			{
				processingTimes.RemoveAt(0);
			}
		}
	}

	internal TimeSpan? GetAverage()
	{
		lock (statsLock)
		{
			if (CommonNetFuncs.Core.Collections.AnyFast(processingTimes))
			{
				return TimeSpan.FromMilliseconds(processingTimes.Average(t => t.TotalMilliseconds));
			}
			return null;
		}
	}

	internal TimeSpan[] Snapshot()
	{
		lock (statsLock)
		{
			return [.. processingTimes];
		}
	}
}

// Queue<T> is a ring buffer that enumerates oldest -> newest, so Average(selector) sums in the same order and yields bit-identical results.
internal sealed class PwOptimizedProcessingTimeWindow(int processTimeWindow)
{
	private readonly Queue<TimeSpan> processingTimes = new();
	private readonly Lock statsLock = new();
	private readonly int processTimeWindow = processTimeWindow;

	internal void Record(TimeSpan elapsed)
	{
		lock (statsLock)
		{
			processingTimes.Enqueue(elapsed);

			if (processingTimes.Count > processTimeWindow)
			{
				processingTimes.Dequeue();
			}
		}
	}

	internal TimeSpan? GetAverage()
	{
		lock (statsLock)
		{
			// Queue<T> is not ICollection<T>, so AnyFast() does not apply; Count > 0 is the same check
			if (processingTimes.Count > 0)
			{
				return TimeSpan.FromMilliseconds(processingTimes.Average(t => t.TotalMilliseconds));
			}
			return null;
		}
	}

	internal TimeSpan[] Snapshot()
	{
		lock (statsLock)
		{
			return [.. processingTimes];
		}
	}
}

#endregion

#region 10. EndpointQueueService.GetOrCreateQueue

/// <summary>EndpointQueueService.GetOrCreateQueue: capturing GetOrAdd lambda allocates a closure + delegate on every request (even on hits) vs static lambda with the GetOrAdd factory-argument overload.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwEndpointQueueLookupBenchmarks
{
	private const string EndpointKey = "POST:/api/orders/submit";

	private readonly BoundedChannelOptions channelOptions = new(100);
	private readonly PwOriginalEndpointQueueService original = new();
	private readonly PwOptimizedEndpointQueueService optimized = new();

	[GlobalSetup]
	public void Setup()
	{
		original.GetOrCreateQueue(EndpointKey, channelOptions);
		optimized.GetOrCreateQueue(EndpointKey, channelOptions);
	}

	[Benchmark(Baseline = true)]
	public object Original()
	{
		return original.GetOrCreateQueue(EndpointKey, channelOptions);
	}

	[Benchmark]
	public object Optimized()
	{
		return optimized.GetOrCreateQueue(EndpointKey, channelOptions);
	}
}

internal sealed class PwEndpointQueueStub(string key, BoundedChannelOptions options)
{
	public string Key { get; } = key;

	public BoundedChannelOptions Options { get; } = options;
}

// Verbatim copy of EndpointQueueService.GetOrCreateQueue(string, BoundedChannelOptions) (CommonNetFuncs.Web.Api\TaskQueuing\EndpointQueue\EndpointQueueService.cs)
internal sealed class PwOriginalEndpointQueueService
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	private readonly ConcurrentDictionary<string, PwEndpointQueueStub> queues = new();

	internal PwEndpointQueueStub GetOrCreateQueue(string endpointKey, BoundedChannelOptions boundedChannelOptions)
	{
		return queues.GetOrAdd(endpointKey, key =>
		{
			logger.Info("Creating new queue for endpoint: {EndpointKey}", key);
			return new PwEndpointQueueStub(key, boundedChannelOptions);
		});
	}
}

internal sealed class PwOptimizedEndpointQueueService
{
	private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

	private readonly ConcurrentDictionary<string, PwEndpointQueueStub> queues = new();

	internal PwEndpointQueueStub GetOrCreateQueue(string endpointKey, BoundedChannelOptions boundedChannelOptions)
	{
		return queues.GetOrAdd(endpointKey, static (key, options) =>
		{
			logger.Info("Creating new queue for endpoint: {EndpointKey}", key);
			return new PwEndpointQueueStub(key, options);
		}, boundedChannelOptions);
	}
}

#endregion

#region 11. WrapperHelpers.SetCompressionHttpHeadersInPlace

/// <summary>WrapperHelpers.SetCompressionHttpHeadersInPlace: ImmutableDictionary enumerated through IEnumerable (boxed enumerator + pooled stack) with ContainsKey+indexer vs pre-snapshotted arrays + TryAdd.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwCompressionHeadersBenchmarks
{
	private readonly CompressionOptions compressionOptions = new(true, ECompressionType.Gzip);

	[Benchmark(Baseline = true)]
	public Dictionary<string, string> Original()
	{
		Dictionary<string, string> headers = new() { ["X-Api-Key"] = "abc123" };
		PwCompressionHeaders.Original(headers, compressionOptions, false);
		return headers;
	}

	[Benchmark]
	public Dictionary<string, string> Optimized()
	{
		Dictionary<string, string> headers = new() { ["X-Api-Key"] = "abc123" };
		PwCompressionHeaders.Optimized(headers, compressionOptions, false);
		return headers;
	}
}

internal static class PwCompressionHeaders
{
	private static readonly ImmutableDictionary<string, string> MemPackHeadersWithGzip = ImmutableDictionary.CreateRange([RestHelperConstants.MemPackContentHeader, RestHelperConstants.MemPackAcceptHeader, RestHelperConstants.GzipEncodingHeader]);
	private static readonly ImmutableDictionary<string, string> MemPackHeadersWithBrotli = ImmutableDictionary.CreateRange([RestHelperConstants.MemPackContentHeader, RestHelperConstants.MemPackAcceptHeader, RestHelperConstants.BrotliEncodingHeader]);

	private static readonly ImmutableDictionary<string, string> MsgPackHeadersWithGzip = ImmutableDictionary.CreateRange([RestHelperConstants.MsgPackContentHeader, RestHelperConstants.MsgPackAcceptHeader, RestHelperConstants.GzipEncodingHeader]);
	private static readonly ImmutableDictionary<string, string> MsgPackHeadersWithBrotli = ImmutableDictionary.CreateRange([RestHelperConstants.MsgPackContentHeader, RestHelperConstants.MsgPackAcceptHeader, RestHelperConstants.BrotliEncodingHeader]);

	private static readonly ImmutableDictionary<string, string> JsonHeadersWithGzip = ImmutableDictionary.CreateRange([RestHelperConstants.JsonContentHeader, RestHelperConstants.JsonAcceptHeader, RestHelperConstants.GzipEncodingHeader]);
	private static readonly ImmutableDictionary<string, string> JsonHeadersWithBrotli = ImmutableDictionary.CreateRange([RestHelperConstants.JsonContentHeader, RestHelperConstants.JsonAcceptHeader, RestHelperConstants.BrotliEncodingHeader]);

	// Snapshots taken from the very same immutable dictionaries, so iteration order (and therefore the resulting Dictionary insertion order) is unchanged
	private static readonly KeyValuePair<string, string>[] MemPackHeadersWithGzipArray = [.. MemPackHeadersWithGzip];
	private static readonly KeyValuePair<string, string>[] MemPackHeadersWithBrotliArray = [.. MemPackHeadersWithBrotli];
	private static readonly KeyValuePair<string, string>[] MsgPackHeadersWithGzipArray = [.. MsgPackHeadersWithGzip];
	private static readonly KeyValuePair<string, string>[] MsgPackHeadersWithBrotliArray = [.. MsgPackHeadersWithBrotli];
	private static readonly KeyValuePair<string, string>[] JsonHeadersWithGzipArray = [.. JsonHeadersWithGzip];
	private static readonly KeyValuePair<string, string>[] JsonHeadersWithBrotliArray = [.. JsonHeadersWithBrotli];
	private static readonly KeyValuePair<string, string>[] MemPackHeadersArray = [.. RestHelperConstants.MemPackHeaders];
	private static readonly KeyValuePair<string, string>[] MsgPackHeadersArray = [.. RestHelperConstants.MsgPackHeaders];
	private static readonly KeyValuePair<string, string>[] JsonHeadersArray = [.. RestHelperConstants.JsonHeaders];
	private static readonly KeyValuePair<string, string>[] JsonNoEncodeHeadersArray = [.. RestHelperConstants.JsonNoEncodeHeaders];

	// Verbatim copy of WrapperHelpers.SetCompressionHttpHeadersInPlace (CommonNetFuncs.Web.Requests\Rest\RestHelperWrapper\WrapperHelpers.cs)
	internal static void Original(Dictionary<string, string> headers, CompressionOptions? compressionOptions = null, bool isStreaming = false)
	{
		IEnumerable<KeyValuePair<string, string>>? compressionHeaders = null;

		if (compressionOptions?.UseCompression == true)
		{
			if (compressionOptions.CompressionType != null)
			{
				if (compressionOptions.CompressionType == ECompressionType.Gzip)
				{
					if (compressionOptions.UseMemPack)
					{
						compressionHeaders = MemPackHeadersWithGzip;
					}
					else if (compressionOptions.UseMsgPack)
					{
						compressionHeaders = MsgPackHeadersWithGzip;
					}
					else
					{
						compressionHeaders = JsonHeadersWithGzip;
					}
				}
				else if (compressionOptions.CompressionType == ECompressionType.Brotli)
				{
					if (compressionOptions.UseMemPack)
					{
						compressionHeaders = MemPackHeadersWithBrotli;
					}
					else if (compressionOptions.UseMsgPack)
					{
						compressionHeaders = MsgPackHeadersWithBrotli;
					}
					else
					{
						compressionHeaders = JsonHeadersWithBrotli;
					}
				}
				else
				{
					if (compressionOptions.UseMemPack)
					{
						compressionHeaders = MemPackHeadersWithGzip;
					}
					else if (compressionOptions.UseMsgPack)
					{
						compressionHeaders = MsgPackHeadersWithGzip;
					}
					else
					{
						compressionHeaders = JsonHeadersWithGzip;
					}
				}
			}
			else
			{
				if (compressionOptions.UseMemPack)
				{
					compressionHeaders = RestHelperConstants.MemPackHeaders;
				}
				else if (compressionOptions.UseMsgPack)
				{
					compressionHeaders = RestHelperConstants.MsgPackHeaders;
				}
				else
				{
					compressionHeaders = RestHelperConstants.JsonHeaders;
				}
			}
		}
		else if (isStreaming)
		{
			// When streaming without compression, use appropriate headers based on serialization type
			if (compressionOptions?.UseMemPack == true)
			{
				compressionHeaders = RestHelperConstants.MemPackHeaders;
			}
			else if (compressionOptions?.UseMsgPack == true)
			{
				compressionHeaders = RestHelperConstants.MsgPackHeaders;
			}
			else
			{
				compressionHeaders = RestHelperConstants.JsonNoEncodeHeaders; // Default to JSON with no compression when streaming
			}
		}

		// Add compression headers to the existing dictionary if they don't already exist
		if (compressionHeaders != null)
		{
			foreach (KeyValuePair<string, string> header in compressionHeaders)
			{
				if (!headers.ContainsKey(header.Key))
				{
					headers[header.Key] = header.Value;
				}
			}
		}
	}

	internal static void Optimized(Dictionary<string, string> headers, CompressionOptions? compressionOptions = null, bool isStreaming = false)
	{
		KeyValuePair<string, string>[]? compressionHeaders = SelectCompressionHeaders(compressionOptions, isStreaming);
		if (compressionHeaders != null)
		{
			foreach (KeyValuePair<string, string> header in compressionHeaders)
			{
				headers.TryAdd(header.Key, header.Value);
			}
		}
	}

	private static KeyValuePair<string, string>[]? SelectCompressionHeaders(CompressionOptions? compressionOptions, bool isStreaming)
	{
		if (compressionOptions?.UseCompression == true)
		{
			if (compressionOptions.CompressionType == null)
			{
				return compressionOptions.UseMemPack ? MemPackHeadersArray : compressionOptions.UseMsgPack ? MsgPackHeadersArray : JsonHeadersArray;
			}

			// Any type other than Brotli (including Gzip) maps to the Gzip header sets, exactly like the original's Gzip and "else" branches
			if (compressionOptions.CompressionType == ECompressionType.Brotli)
			{
				return compressionOptions.UseMemPack ? MemPackHeadersWithBrotliArray : compressionOptions.UseMsgPack ? MsgPackHeadersWithBrotliArray : JsonHeadersWithBrotliArray;
			}
			return compressionOptions.UseMemPack ? MemPackHeadersWithGzipArray : compressionOptions.UseMsgPack ? MsgPackHeadersWithGzipArray : JsonHeadersWithGzipArray;
		}

		if (isStreaming)
		{
			if (compressionOptions?.UseMemPack == true)
			{
				return MemPackHeadersArray;
			}
			return compressionOptions?.UseMsgPack == true ? MsgPackHeadersArray : JsonNoEncodeHeadersArray;
		}
		return null;
	}
}

#endregion

#region 12. RestHelpersStatic.LogResponse (System.Text.Json branch)

/// <summary>RestHelpersStatic.LogResponse (STJ branch, runs per item when streaming): MemoryStream + SerializeAsync + ToArray copy + GetString vs GetBuffer (no copy) or a direct string Serialize.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwResponseLogSerializationBenchmarks
{
	private List<TestModel> response = null!;

	[Params(1, 50)]
	public int ItemCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		response = [.. PwFixtures.CreateTestModels(ItemCount)];
	}

	[Benchmark(Baseline = true)]
	public Task<string?> Original()
	{
		return PwResponseLogSerializer.Original(response, CancellationToken.None);
	}

	[Benchmark]
	public Task<string?> Optimized_GetBuffer()
	{
		return PwResponseLogSerializer.OptimizedGetBuffer(response, CancellationToken.None);
	}

	[Benchmark]
	public ValueTask<string?> Optimized_SyncSerialize()
	{
		return PwResponseLogSerializer.OptimizedSyncSerialize(response, CancellationToken.None);
	}
}

internal static class PwResponseLogSerializer
{
	// Verbatim copy of the System.Text.Json branch of RestHelpersStatic.LogResponse (CommonNetFuncs.Web.Requests\Rest\RestHelpersStatic.cs)
	internal static async Task<string?> Original<TResponse>(TResponse? response, CancellationToken cancellationToken)
	{
		string? resultJson = null;
		await using MemoryStream outputStream = new();
		await JsonSerializer.SerializeAsync(outputStream, response, cancellationToken: cancellationToken).ConfigureAwait(false);
		resultJson = Encoding.UTF8.GetString(outputStream.ToArray());
		return resultJson;
	}

	// Zero-risk variant: identical async serialization, decodes straight from the stream's buffer instead of copying it first
	internal static async Task<string?> OptimizedGetBuffer<TResponse>(TResponse? response, CancellationToken cancellationToken)
	{
		await using MemoryStream outputStream = new();
		await JsonSerializer.SerializeAsync(outputStream, response, cancellationToken: cancellationToken).ConfigureAwait(false);
		return Encoding.UTF8.GetString(outputStream.GetBuffer(), 0, (int)outputStream.Length);
	}

	// Same JSON text (same TValue and default options). A token that is already cancelled is routed to the original path so the
	// cancellation exception type/message stay identical.
	internal static ValueTask<string?> OptimizedSyncSerialize<TResponse>(TResponse? response, CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
		{
			return new ValueTask<string?>(Original(response, cancellationToken));
		}
		return new ValueTask<string?>(JsonSerializer.Serialize(response));
	}
}

#endregion

#region 13. WrapperHelpers.GetRequestOptions URL combination

/// <summary>WrapperHelpers.GetRequestOptions URL: re-parses the (constant) client BaseAddress into a new normalized Uri on every attempt vs caching the normalized base per BaseAddress instance.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PwRequestUrlCombineBenchmarks
{
	private readonly Uri baseAddress = new("https://api.example.com/v1");
	private const string RelativeUrl = "/orders/12345/items?expand=true&page=2";

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return PwRequestUrlBuilder.Original(baseAddress, RelativeUrl);
	}

	[Benchmark]
	public string Optimized()
	{
		return PwRequestUrlBuilder.Optimized(baseAddress, RelativeUrl);
	}
}

internal static class PwRequestUrlBuilder
{
	private const char UrlSeparator = (char)47;

	// Keyed by reference: Uri is immutable, so the normalized base derived from a given instance never changes; entries die with the Uri.
	private static readonly ConditionalWeakTable<Uri, Uri> NormalizedBaseAddresses = new();

	// Verbatim copy of the Url initializer in WrapperHelpers.GetRequestOptions (CommonNetFuncs.Web.Requests\Rest\RestHelperWrapper\WrapperHelpers.cs)
	internal static string Original(Uri? baseAddress, string optionsUrl)
	{
		return baseAddress != null
			? new Uri(new Uri(baseAddress.ToString().TrimEnd(UrlSeparator) + UrlSeparator), optionsUrl.TrimStart(UrlSeparator)).ToString()
			: optionsUrl;
	}

	internal static string Optimized(Uri? baseAddress, string optionsUrl)
	{
		if (baseAddress == null)
		{
			return optionsUrl;
		}

		Uri normalizedBase = NormalizedBaseAddresses.GetValue(baseAddress, static address => new Uri(address.ToString().TrimEnd(UrlSeparator) + UrlSeparator));
		return new Uri(normalizedBase, optionsUrl.TrimStart(UrlSeparator)).ToString();
	}
}

#endregion

#region Fixtures

internal static class PwFixtures
{
	internal static DefaultHttpContext CreateFormContext(Dictionary<string, StringValues> fields, string contentType)
	{
		DefaultHttpContext context = new();
		context.Request.ContentType = contentType; // Must be set before Form, otherwise FormFeature discards the assigned form
		context.Request.Form = new FormCollection(fields);
		return context;
	}

	internal static DefaultHttpContext CreateDataTableContext(int columnCount, int sortCount)
	{
		Dictionary<string, StringValues> fields = new(StringComparer.OrdinalIgnoreCase) { ["draw"] = "3" };
		for (int c = 0; c < columnCount; c++)
		{
			fields[$"columns[{c}][data]"] = $"field{c}";
			fields[$"columns[{c}][name]"] = string.Empty;
			fields[$"columns[{c}][searchable]"] = "true";
			fields[$"columns[{c}][orderable]"] = "true";
			fields[$"columns[{c}][search][value]"] = string.Empty;
			fields[$"columns[{c}][search][regex]"] = "false";
		}
		for (int s = 0; s < sortCount; s++)
		{
			fields[$"order[{s}][column]"] = (s * 3 % columnCount).ToString(CultureInfo.InvariantCulture);
			fields[$"order[{s}][dir]"] = s % 2 == 0 ? "asc" : "desc";
		}
		fields["start"] = "20";
		fields["length"] = "10";
		fields["search[value]"] = "name=bob,status=active";
		fields["search[regex]"] = "false";
		return CreateFormContext(fields, "application/x-www-form-urlencoded");
	}

	internal static DefaultHttpContext CreateCacheKeyContext(bool isPost, string queryString, byte[] body)
	{
		DefaultHttpContext context = new();
		context.Request.Method = isPost ? "POST" : "GET";
		context.Request.Path = "/api/items/search";
		context.Request.QueryString = new QueryString(queryString);
		context.Request.Headers.Accept = "application/json";
		if (isPost)
		{
			context.Request.ContentType = "application/json";
			context.Request.Body = new MemoryStream(body, writable: false);
		}
		return context;
	}

	internal static byte[] CreatePostBody(int approximateLength)
	{
		StringBuilder builder = new("{\"query\":\"héllo wörld 😀\",\"items\":[");
		int i = 0;
		while (builder.Length < approximateLength)
		{
			if (i > 0)
			{
				builder.Append(',');
			}
			builder.Append("{\"id\":").Append(i).Append(",\"name\":\"item ").Append(i).Append("\"}");
			i++;
		}
		builder.Append("]}");
		return Encoding.UTF8.GetBytes(builder.ToString());
	}

	internal static TestModel[] CreateTestModels(int count)
	{
		TestModel[] models = new TestModel[count];
		for (int i = 0; i < count; i++)
		{
			models[i] = new TestModel { Name = $"User {i} Zoë <b>&'\"", Value = i, Description = $"Description number {i} with some realistic content 😀" };
		}
		return models;
	}
}

#endregion

#region Verification

/// <summary>Asserts every Original/Optimized pair above produces identical outcomes (results or exception type + message) on edge-case inputs.</summary>
public static class PwVerify
{
	public static void Run()
	{
		VerifyDataTableRequest();
		VerifyCacheKey();
		VerifyContentTypes();
		VerifyListToQueryParameters();
		VerifyDenyCharacters();
		VerifyAllowedNullableValues();
		VerifyStreamingWriters();
		VerifyDistributedCacheSerialization();
		VerifyProcessingTimeWindow();
		VerifyEndpointQueueLookup();
		VerifyCompressionHeaders();
		VerifyResponseLogSerialization();
		VerifyRequestUrlCombine();
	}

	private static void Fail(string name, string detail)
	{
		throw new InvalidOperationException($"PwVerify mismatch in {name}: {detail}");
	}

	private static string Outcome<T>(Func<T> func, Func<T, string> describe)
	{
		try
		{
			return "OK:" + describe(func());
		}
		catch (Exception ex)
		{
			return $"EX:{ex.GetType().FullName}:{ex.Message}";
		}
	}

	private static void Same<T>(string name, Func<T> original, Func<T> optimized, Func<T, string> describe)
	{
		string originalOutcome = Outcome(original, describe);
		string optimizedOutcome = Outcome(optimized, describe);
		if (!string.Equals(originalOutcome, optimizedOutcome, StringComparison.Ordinal))
		{
			Fail(name, $"original=<{originalOutcome}> optimized=<{optimizedOutcome}>");
		}
	}

	private static string Describe(string? value)
	{
		return value ?? "<null>";
	}

	private static string Describe(ValidationResult? result)
	{
		return result == null ? "<success>" : $"{result.ErrorMessage}|{string.Join(",", result.MemberNames)}";
	}

	private static string Describe<TKey, TValue>(Dictionary<TKey, TValue> dictionary) where TKey : notnull
	{
		return string.Join(";", dictionary.Select(kvp => $"{kvp.Key}={(object?)kvp.Value ?? "<null>"}"));
	}

	private static string Describe(PwDataTableRequest request)
	{
		return $"draw={Describe(request.Draw)}|size={request.PageSize}|skip={request.Skip}|sort={Describe(request.SortColumns)}|dir={Describe(request.SortColumnDir)}|search={Describe(request.SearchValues)}";
	}

	private static void VerifyDataTableRequest()
	{
		List<(string Name, Dictionary<string, StringValues> Fields, string ContentType)> cases =
		[
			("standard", new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
			{
				["draw"] = "1", ["columns[0][data]"] = "id", ["columns[1][data]"] = "name", ["columns[2][data]"] = "age",
				["order[0][column]"] = "1", ["order[0][dir]"] = "asc", ["order[1][column]"] = "2", ["order[1][dir]"] = "desc",
				["start"] = "10", ["length"] = "25", ["search[value]"] = "name=bob, age=, novalue,=x,d=e=f,\nstatus=active\n"
			}, "application/x-www-form-urlencoded"),
			// Ordinal dictionary so both "draw" and "DRAW" exist: verifies first-match-in-enumeration-order semantics
			("mixed case + duplicates", new Dictionary<string, StringValues>(StringComparer.Ordinal)
			{
				["DRAW"] = "7", ["draw"] = "8", ["Order[0][Column]"] = "1", ["COLUMNS[1][DATA]"] = "name", ["columns[1][data]"] = "other",
				["order[0][DIR]"] = "desc", ["START"] = "5", ["Length"] = "50", ["Search[Value]"] = "a=1"
			}, "application/x-www-form-urlencoded; charset=utf-8"),
			("missing column + gap", new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
			{
				["order[0][column]"] = "7", ["order[2][column]"] = "1", ["columns[1][data]"] = "name", ["length"] = "10"
			}, "multipart/form-data; boundary=abc"),
			("multi-valued order column", new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
			{
				["order[0][column]"] = new StringValues(["1", "2"]), ["columns[1,2][data]"] = "combo", ["order[0][dir]"] = new StringValues(["asc", "desc"])
			}, "application/x-www-form-urlencoded"),
			("invalid length", new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
			{
				["draw"] = "2", ["order[0][column]"] = "0", ["columns[0][data]"] = "id", ["length"] = "abc", ["start"] = "0", ["search[value]"] = "x=1"
			}, "application/x-www-form-urlencoded"),
			("null search token", new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase)
			{
				["search[value]"] = "a=1,null,b=2", ["length"] = "", ["start"] = ""
			}, "application/x-www-form-urlencoded"),
			("empty form", new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase), "application/x-www-form-urlencoded"),
			("not form content", new Dictionary<string, StringValues>(StringComparer.OrdinalIgnoreCase) { ["draw"] = "1" }, "application/json"),
		];

		foreach ((string name, Dictionary<string, StringValues> fields, string contentType) in cases)
		{
			HttpRequest request = PwFixtures.CreateFormContext(fields, contentType).Request;
			Same($"DataTableRequest[{name}]", () => PwDataTableHelpers.Original(request), () => PwDataTableHelpers.Optimized(request), Describe);
		}

		HttpRequest large = PwFixtures.CreateDataTableContext(30, 5).Request;
		Same("DataTableRequest[large]", () => PwDataTableHelpers.Original(large), () => PwDataTableHelpers.Optimized(large), Describe);
	}

	private static void VerifyCacheKey()
	{
		PwCacheKeyGenerator generator = new(new CacheOptions());
		string[] queries =
		[
			string.Empty,
			"?evictCacheEntry=true",
			"?useCache=true&zeta=1&alpha=2&evictCacheEntry=false&tags=a&tags=b&Beta=3&page=10",
			"?a%C2%AD=1&a=2&B=3&b2=4&%C3%A4=5&ae=6&10=7&9=8&x=&y&_z=9&Z=10&A=11",
			"?q=hello%20world&q=second&EVICTCACHEENTRY=1&evictcacheentry=2",
		];
		byte[][] bodies =
		[
			PwFixtures.CreatePostBody(512),
			[],
			[0xEF, 0xBB, 0xBF, (byte)'{', 0xFF, 0xFE, (byte)'}'],
		];

		foreach (string query in queries)
		{
			foreach (bool isPost in new[] { false, true })
			{
				foreach (byte[] body in isPost ? bodies : [Array.Empty<byte>()])
				{
					DefaultHttpContext context = PwFixtures.CreateCacheKeyContext(isPost, query, body);
					Same($"CacheKey[{query}|post={isPost}|body={body.Length}]", () => generator.OriginalGenerateCacheKey(context).GetAwaiter().GetResult(),
						() => generator.OptimizedGenerateCacheKey(context).GetAwaiter().GetResult(), Describe);
				}
			}
		}

		DefaultHttpContext multiAccept = PwFixtures.CreateCacheKeyContext(false, "?b=1&a=2", []);
		multiAccept.Request.Headers.Accept = new StringValues(["application/json", "text/plain"]);
		Same("CacheKey[multi-accept]", () => generator.OriginalGenerateCacheKey(multiAccept).GetAwaiter().GetResult(),
			() => generator.OptimizedGenerateCacheKey(multiAccept).GetAwaiter().GetResult(), Describe);
	}

	private static void VerifyContentTypes()
	{
		string?[] fileNames =
		[
			"a.json", "A.JSON", "x.jpeg", "x.JPG", "page.html", "page.HTML", "page.htm5", "notes.txt", "notes.text5", "app.js", "app.JS", "style.css", "data.xml",
			"blob.binary", "file.unknown", "file.", "noext", "dir.d/file", "dir/file.mszipupload", "x.MsZipUpload", "x.Tİff", "x.ts", "long." + new string('x', 40),
			"x.ﬀ", "C:\\path\\to\\report.Pdf", string.Empty, null,
		];
		foreach (string? fileName in fileNames)
		{
			Same($"GetContentType[{fileName ?? "<null>"}]", () => PwContentTypes.OriginalGetContentType(fileName!), () => PwContentTypes.OptimizedGetContentType(fileName!), Describe);
		}

		string?[] extensions = ["json", "JSON", ".Json", "html", ".htmlx", "mszipupload", ".jpeg", "unknown", "x", ".", "..json", string.Empty, null, new string('y', 40)];
		foreach (string? extension in extensions)
		{
			Same($"GetContentTypeByExtension[{extension ?? "<null>"}]", () => PwContentTypes.OriginalGetContentTypeByExtension(extension!),
				() => PwContentTypes.OptimizedGetContentTypeByExtension(extension!), Describe);
		}
	}

	private sealed class PwOddFormattable : IFormattable
	{
		public override string ToString()
		{
			return "plain-to-string";
		}

		public string ToString(string? format, IFormatProvider? formatProvider)
		{
			return "formattable-to-string";
		}
	}

	private static void VerifyListToQueryParameters()
	{
		CultureInfo previousCulture = CultureInfo.CurrentCulture;
		try
		{
			foreach (string cultureName in new[] { "", "de-DE", "sv-SE", "fr-CH" })
			{
				CultureInfo.CurrentCulture = new CultureInfo(cultureName);
				CheckQuery("int", new List<int> { 0, -1, int.MinValue, int.MaxValue, 42 });
				CheckQuery("long", new[] { long.MinValue, -5L, 7L });
				CheckQuery("short", new short[] { short.MinValue, 3 });
				CheckQuery("byte", new byte[] { 0, 255 });
				CheckQuery("sbyte", new sbyte[] { -128, 127 });
				CheckQuery("uint", new uint[] { 0, uint.MaxValue });
				CheckQuery("ulong", new ulong[] { ulong.MaxValue });
				CheckQuery("ushort", new ushort[] { ushort.MaxValue });
				CheckQuery("double", new[] { 1.5, -0.0, double.NaN, double.PositiveInfinity, 1.5e300, -123456.789, 0.1 + 0.2 });
				CheckQuery("float", new[] { 1.5f, float.NegativeInfinity, -3.4e38f });
				CheckQuery("decimal", new[] { 1.50m, -0.001m, decimal.MaxValue });
				CheckQuery("bool", new[] { true, false });
				CheckQuery("char", new[] { 'a', '&', 'é' });
				CheckQuery("guid", new[] { Guid.Empty, new Guid("0f8fad5b-d9cb-469f-a165-70867728950e") });
				CheckQuery("nullable-int", new int?[] { 1, null, -3 });
				CheckQuery("string", new string?[] { "a", null, "b c" });
				CheckQuery("datetime", new[] { new DateTime(2024, 2, 29, 13, 45, 0, DateTimeKind.Utc) });
				CheckQuery("odd-formattable", new[] { new PwOddFormattable() });
				CheckQuery("empty", new List<int>());
				CheckQuery("null", (IEnumerable<int>?)null);
				Same("ListToQuery[whitespace-name]", () => PwUriHelpers.OriginalListToQueryParameters(new[] { 1 }, " "), () => PwUriHelpers.OptimizedListToQueryParameters(new[] { 1 }, " "), Describe);
				Same("ListToQuery[null-name]", () => PwUriHelpers.OriginalListToQueryParameters(new[] { 1 }, null), () => PwUriHelpers.OptimizedListToQueryParameters(new[] { 1 }, null), Describe);
			}
		}
		finally
		{
			CultureInfo.CurrentCulture = previousCulture;
		}

		static void CheckQuery<T>(string name, IEnumerable<T>? values)
		{
			Same($"ListToQuery[{name}|{CultureInfo.CurrentCulture.Name}]", () => PwUriHelpers.OriginalListToQueryParameters(values, "p"),
				() => PwUriHelpers.OptimizedListToQueryParameters(values, "p"), Describe);
		}
	}

	private static void VerifyDenyCharacters()
	{
		foreach (string denyChars in new[] { "<>&;'\"%$", "<>", "ab", "\uD83D\uDE00é" })
		{
			PwOriginalDenyCharactersAttribute originalSingle = new(denyChars);
			PwOptimizedDenyCharactersAttribute optimizedSingle = new(denyChars);
			PwOriginalListDenyCharactersAttribute originalList = new(denyChars);
			PwOptimizedListDenyCharactersAttribute optimizedList = new(denyChars);

			foreach (ValidationContext context in new[] { new ValidationContext(new object()) { MemberName = "Value", DisplayName = "Value" }, new ValidationContext(new object()) })
			{
				object?[] singles = [null, string.Empty, "clean", "has<angle", "has>", "x$", 12345, 3.5, new object(), "😀 smile", "naïve", "plain ab"];
				foreach (object? value in singles)
				{
					Same($"DenyCharacters[{denyChars}|{value ?? "<null>"}]", () => originalSingle.GetValidationResult(value, context), () => optimizedSingle.GetValidationResult(value, context), Describe);
				}

				object?[] lists =
				[
					null,
					new List<string?> { "a", null, string.Empty, "b" },
					new List<string?> { "ok", "bad>", "also<bad" },
					new[] { "x", "😀" },
					new[] { 1, 2, 3 },
					new List<object?> { "fine", null, 5, "a&b" },
					5,
					"not-a-list<",
				];
				foreach (object? value in lists)
				{
					Same($"ListDenyCharacters[{denyChars}|{value?.GetType().Name ?? "<null>"}]", () => originalList.GetValidationResult(value, context),
						() => optimizedList.GetValidationResult(value, context), Describe);
				}
			}
		}
	}

	private static void VerifyAllowedNullableValues()
	{
		ValidationContext context = new(new object()) { MemberName = "Color", DisplayName = "Color" };
		(Func<ValidationAttribute> Original, Func<ValidationAttribute> Optimized, string Name)[] factories =
		[
			(() => new PwOriginalAllowedNullableValuesAttribute(typeof(PwColor)), () => new PwOptimizedAllowedNullableValuesAttribute(typeof(PwColor)), "PwColor"),
			(() => new PwOriginalAllowedNullableValuesAttribute(false, typeof(PwColor)), () => new PwOptimizedAllowedNullableValuesAttribute(false, typeof(PwColor)), "PwColor-noNull"),
			(() => new PwOriginalAllowedNullableValuesAttribute(typeof(PwBigEnum)), () => new PwOptimizedAllowedNullableValuesAttribute(typeof(PwBigEnum)), "PwBigEnum"),
			(() => new PwOriginalAllowedNullableValuesAttribute("a", 1, null!), () => new PwOptimizedAllowedNullableValuesAttribute("a", 1, null!), "params"),
		];
		object?[] values = [null, PwColor.Red, (PwColor)3, PwBigEnum.Huge, 1, 3, 4L, (byte)2, 2.0, 2.5, 3.5, 1000m, "Red", "a", 1UL, ulong.MaxValue, -1, (short)64, 128f, double.NaN];

		foreach ((Func<ValidationAttribute> originalFactory, Func<ValidationAttribute> optimizedFactory, string name) in factories)
		{
			ValidationAttribute original = originalFactory();
			ValidationAttribute optimized = optimizedFactory();

			// Two passes so the optimized instance is exercised both before and after its cache is populated
			for (int pass = 0; pass < 2; pass++)
			{
				foreach (object? value in values)
				{
					Same($"AllowedNullableValues[{name}|pass {pass}|{value?.GetType().Name}:{value ?? "<null>"}]", () => original.GetValidationResult(value, context),
						() => optimized.GetValidationResult(value, context), Describe);
				}
			}
		}
	}

	private static void VerifyStreamingWriters()
	{
		TestModel[] items = [.. PwFixtures.CreateTestModels(5), new TestModel { Name = string.Empty, Description = "\u2028\u0000\t" }];
		JsonSerializerOptions?[] jsonOptions =
		[
			null,
			new JsonSerializerOptions { WriteIndented = true },
			new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase },
			new JsonSerializerOptions { WriteIndented = true, IndentCharacter = '\t', IndentSize = 1, NewLine = "\n" },
			new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 8 },
		];
		MessagePackSerializerOptions?[] messagePackOptions = [null, MessagePackSerializerOptions.Standard.WithCompression(MessagePackCompression.Lz4BlockArray)];

		foreach (JsonSerializerOptions? options in jsonOptions)
		{
			string original = WriteAll(items, (writer, stream, item) => new PwOriginalStreamingWriter<TestModel>(options, null).WriteJsonItemAsync(stream, item, CancellationToken.None));
			PwOptimizedStreamingWriter<TestModel> staticNewLineWriter = new(options, null);
			PwOptimizedStreamingWriter<TestModel> reusedWriter = new(options, null);
			string staticNewLine = WriteAll(items, (_, stream, item) => staticNewLineWriter.WriteJsonItemStaticNewLineAsync(stream, item, CancellationToken.None));
			string reused = WriteAll(items, (_, stream, item) => reusedWriter.WriteJsonItemReusedBufferAsync(stream, item, CancellationToken.None));
			if (original != staticNewLine || original != reused)
			{
				Fail("StreamingWriters[json]", $"options indented={options?.WriteIndented}: original={original} staticNewLine={staticNewLine} reused={reused}");
			}
		}

		foreach (MessagePackSerializerOptions? options in messagePackOptions)
		{
			PwOriginalStreamingWriter<TestModel> originalWriter = new(null, options);
			PwOptimizedStreamingWriter<TestModel> optimizedWriter = new(null, options);
			string original = WriteAll(items, (_, stream, item) => originalWriter.WriteMessagePackItemAsync(stream, item, CancellationToken.None));
			string optimized = WriteAll(items, (_, stream, item) => optimizedWriter.WriteMessagePackItemAsync(stream, item, CancellationToken.None));
			if (original != optimized)
			{
				Fail("StreamingWriters[msgpack]", $"original={original} optimized={optimized}");
			}
		}

		static string WriteAll(TestModel[] items, Func<object?, Stream, TestModel, Task> write)
		{
			using MemoryStream stream = new();
			foreach (TestModel item in items)
			{
				write(null, stream, item).GetAwaiter().GetResult();
			}
			return Convert.ToBase64String(stream.ToArray());
		}
	}

	private static void VerifyDistributedCacheSerialization()
	{
		static string Bytes(byte[] bytes)
		{
			return Convert.ToBase64String(bytes);
		}

		Same("DistributedCache.Set[model]", () => PwDistributedCacheSerialization.OriginalSerialize(PwFixtures.CreateTestModels(1)[0]), () => PwDistributedCacheSerialization.OptimizedSerialize(PwFixtures.CreateTestModels(1)[0]), Bytes);
		Same("DistributedCache.Set[list]", () => PwDistributedCacheSerialization.OriginalSerialize(PwFixtures.CreateTestModels(10).ToList()), () => PwDistributedCacheSerialization.OptimizedSerialize(PwFixtures.CreateTestModels(10).ToList()), Bytes);
		Same("DistributedCache.Set[null]", () => PwDistributedCacheSerialization.OriginalSerialize<TestModel?>(null), () => PwDistributedCacheSerialization.OptimizedSerialize<TestModel?>(null), Bytes);
		Same("DistributedCache.Set[non-ascii]", () => PwDistributedCacheSerialization.OriginalSerialize("Zoë 😀 \u2028 <script>"), () => PwDistributedCacheSerialization.OptimizedSerialize("Zoë 😀 \u2028 <script>"), Bytes);
		Same("DistributedCache.Set[lone-surrogate]", () => PwDistributedCacheSerialization.OriginalSerialize("bad \uD800 text"), () => PwDistributedCacheSerialization.OptimizedSerialize("bad \uD800 text"), Bytes);

		byte[] validList = PwDistributedCacheSerialization.OriginalSerialize(PwFixtures.CreateTestModels(3).ToList());
		byte[] bom = [0xEF, 0xBB, 0xBF];
		byte[][] listPayloads =
		[
			validList,
			[.. bom, .. validList],
			[.. bom, .. bom, .. validList],
			[],
			"null"u8.ToArray(),
			"[{\"Name\":\"a\",\"Value\":1,},]"u8.ToArray(),
			"{"u8.ToArray(),
			"[1,2]"u8.ToArray(),
		];
		foreach (byte[] payload in listPayloads)
		{
			Same($"DistributedCache.Get[list|{payload.Length}]", () => PwDistributedCacheSerialization.OriginalDeserializeAsync<List<TestModel>>(payload).GetAwaiter().GetResult(),
				() => PwDistributedCacheSerialization.OptimizedDeserialize<List<TestModel>>(payload), result => JsonSerializer.Serialize(result));
		}

		byte[][] intPayloads = ["[1,2,]"u8.ToArray(), "42"u8.ToArray(), [.. bom, .. "7"u8.ToArray()], "\"x\""u8.ToArray()];
		foreach (byte[] payload in intPayloads)
		{
			Same($"DistributedCache.Get[int|{payload.Length}]", () => PwDistributedCacheSerialization.OriginalDeserializeAsync<int[]>(payload).GetAwaiter().GetResult(),
				() => PwDistributedCacheSerialization.OptimizedDeserialize<int[]>(payload), result => JsonSerializer.Serialize(result));
		}
	}

	private static void VerifyProcessingTimeWindow()
	{
		foreach (int window in new[] { 1, 7, 1000 })
		{
			PwOriginalProcessingTimeWindow original = new(window);
			PwOptimizedProcessingTimeWindow optimized = new(window);
			if (original.GetAverage() != optimized.GetAverage())
			{
				Fail("ProcessingTimeWindow[empty]", $"window={window}");
			}

			System.Random random = new(window);
			for (int i = 0; i < 2537; i++)
			{
				TimeSpan elapsed = TimeSpan.FromTicks(random.NextInt64(1, TimeSpan.TicksPerSecond * 30));
				original.Record(elapsed);
				optimized.Record(elapsed);

				if (i % 97 == 0 || i == 2536)
				{
					TimeSpan? originalAverage = original.GetAverage();
					TimeSpan? optimizedAverage = optimized.GetAverage();
					if (originalAverage != optimizedAverage || !original.Snapshot().SequenceEqual(optimized.Snapshot()))
					{
						Fail("ProcessingTimeWindow", $"window={window} i={i} original={originalAverage} optimized={optimizedAverage}");
					}
				}
			}
		}
	}

	private static void VerifyEndpointQueueLookup()
	{
		PwOriginalEndpointQueueService original = new();
		PwOptimizedEndpointQueueService optimized = new();
		BoundedChannelOptions first = new(10);
		BoundedChannelOptions second = new(20);

		PwEndpointQueueStub originalA = original.GetOrCreateQueue("a", first);
		PwEndpointQueueStub originalA2 = original.GetOrCreateQueue("a", second);
		PwEndpointQueueStub originalB = original.GetOrCreateQueue("b", second);
		PwEndpointQueueStub optimizedA = optimized.GetOrCreateQueue("a", first);
		PwEndpointQueueStub optimizedA2 = optimized.GetOrCreateQueue("a", second);
		PwEndpointQueueStub optimizedB = optimized.GetOrCreateQueue("b", second);

		string originalDescription = $"{ReferenceEquals(originalA, originalA2)}|{originalA.Key}:{originalA.Options.Capacity}|{originalB.Key}:{originalB.Options.Capacity}";
		string optimizedDescription = $"{ReferenceEquals(optimizedA, optimizedA2)}|{optimizedA.Key}:{optimizedA.Options.Capacity}|{optimizedB.Key}:{optimizedB.Options.Capacity}";
		if (originalDescription != optimizedDescription)
		{
			Fail("EndpointQueueLookup", $"original={originalDescription} optimized={optimizedDescription}");
		}
	}

	private static void VerifyCompressionHeaders()
	{
		List<CompressionOptions?> optionsList = [null];
		foreach (bool useCompression in new[] { false, true })
		{
			foreach (ECompressionType? compressionType in new ECompressionType?[] { null, ECompressionType.Gzip, ECompressionType.Brotli, ECompressionType.Deflate, ECompressionType.ZLib, ECompressionType.None })
			{
				foreach (bool useMemPack in new[] { false, true })
				{
					foreach (bool useMsgPack in new[] { false, true })
					{
						optionsList.Add(new CompressionOptions(useCompression, compressionType, useMemPack, useMsgPack));
					}
				}
			}
		}

		Func<Dictionary<string, string>>[] initialHeaders =
		[
			() => new Dictionary<string, string>(),
			() => new Dictionary<string, string> { ["Accept"] = "text/plain" },
			() => new Dictionary<string, string> { ["X-Custom"] = "1", ["Content-Type"] = "application/xml", ["Accept-Encoding"] = "deflate" },
		];

		int caseIndex = 0;
		foreach (CompressionOptions? options in optionsList)
		{
			foreach (bool isStreaming in new[] { false, true })
			{
				foreach (Func<Dictionary<string, string>> createHeaders in initialHeaders)
				{
					Same($"CompressionHeaders[{caseIndex++}]", () =>
					{
						Dictionary<string, string> headers = createHeaders();
						PwCompressionHeaders.Original(headers, options, isStreaming);
						return headers;
					}, () =>
					{
						Dictionary<string, string> headers = createHeaders();
						PwCompressionHeaders.Optimized(headers, options, isStreaming);
						return headers;
					}, Describe);
				}
			}
		}
	}

	private static void VerifyResponseLogSerialization()
	{
		using CancellationTokenSource cancelled = new();
		cancelled.Cancel();

		foreach (CancellationToken token in new[] { CancellationToken.None, cancelled.Token })
		{
			CheckLog("model", PwFixtures.CreateTestModels(1)[0], token);
			CheckLog("null", (TestModel?)null, token);
			CheckLog("list", PwFixtures.CreateTestModels(25).ToList(), token);
			CheckLog("string", "Zoë 😀 <b>", token);
			CheckLog("object", (object?)new Dictionary<string, object?> { ["a"] = 1, ["b"] = null }, token);
		}

		static void CheckLog<T>(string name, T? value, CancellationToken token)
		{
			string tokenState = token.IsCancellationRequested ? "cancelled" : "live";
			Same($"ResponseLog.GetBuffer[{name}|{tokenState}]", () => PwResponseLogSerializer.Original(value, token).GetAwaiter().GetResult(),
				() => PwResponseLogSerializer.OptimizedGetBuffer(value, token).GetAwaiter().GetResult(), Describe);
			Same($"ResponseLog.Sync[{name}|{tokenState}]", () => PwResponseLogSerializer.Original(value, token).GetAwaiter().GetResult(),
				() => PwResponseLogSerializer.OptimizedSyncSerialize(value, token).AsTask().GetAwaiter().GetResult(), Describe);
		}
	}

	private static void VerifyRequestUrlCombine()
	{
		Uri?[] baseAddresses =
		[
			null,
			new Uri("https://api.example.com"),
			new Uri("https://api.example.com/"),
			new Uri("https://api.example.com/v1"),
			new Uri("https://api.example.com/v1//"),
			new Uri("http://host:8080/a%20b/c%2Fd/"),
			new Uri("https://api.example.com/v1?existing=1"),
			new Uri("https://user:pass@münchen.example/base/#frag"),
		];
		string?[] urls = ["items", "/items?x=1&y=%20", "//items", "../up", "https://other.example/abs", string.Empty, "?q=1", "a b/ü", null];

		foreach (Uri? baseAddress in baseAddresses)
		{
			foreach (string? url in urls)
			{
				// Twice: the optimized cache is cold on the first call and warm on the second
				for (int pass = 0; pass < 2; pass++)
				{
					Same($"RequestUrl[{baseAddress}|{url ?? "<null>"}|{pass}]", () => PwRequestUrlBuilder.Original(baseAddress, url!), () => PwRequestUrlBuilder.Optimized(baseAddress, url!), Describe);
				}
			}
		}
	}
}

#endregion
