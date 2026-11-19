using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BenchmarkDotNet.Attributes;
using EHashAlgorithm = CommonNetFuncs.Core.EHashAlgorithm;
using TitleCaseUppercaseWordHandling = CommonNetFuncs.Core.TitleCaseUppercaseWordHandling;

namespace BenchmarkSuite;

// Performance candidates for CommonNetFuncs.Core\Strings.cs.
// Each class pits a verbatim private copy of the current library implementation ("Original", baseline) against the proposed replacement(s).
// "Shared" variants compile on both net11.0 and netstandard2.1, "Net11" variants rely on APIs that only exist on modern .NET.
// Copies call string.IsNullOrWhiteSpace / string.IsNullOrEmpty directly where the library calls its identical IsNullOrWhiteSpace / IsNullOrEmpty
// extension wrappers, and copies of internal helpers (HashCompat, StrComp, StrEq, generated regexes) are reproduced below.
// PsVerify.Run() asserts that every Original / Optimized pair produces identical results (including exception types) on edge-case inputs.

internal static partial class PsRegexes
{
	[GeneratedRegex(@"\s+", RegexOptions.Compiled, 5000)]
	internal static partial Regex MultiSpaceRegex();

	[GeneratedRegex(@"\D+", RegexOptions.Compiled, 5000)]
	internal static partial Regex ExtractNumbersRegex();

	[GeneratedRegex(@"(\d{3})(\d{4})", RegexOptions.Compiled, 5000)]
	internal static partial Regex SevenDigitPhoneNumberRegex();

	[GeneratedRegex(@"(\d{3})(\d{3})(\d{4})", RegexOptions.Compiled, 5000)]
	internal static partial Regex TenDigitPhoneNumberRegex();

	[GeneratedRegex(@"(\d{1})(\d{3})(\d{3})(\d{4})", RegexOptions.Compiled, 5000)]
	internal static partial Regex ElevenDigitPhoneNumberRegex();

	[GeneratedRegex(@"(\d{2})(\d{3})(\d{3})(\d{4})", RegexOptions.Compiled, 5000)]
	internal static partial Regex TwelveDigitPhoneNumberRegex();

	[GeneratedRegex("[A-Za-z ]", RegexOptions.Compiled, 5000)]
	internal static partial Regex LettersOnlyRegex();

	[GeneratedRegex(@"(\s+|[^\w\s])", RegexOptions.Compiled, 5000)]
	internal static partial Regex TitleCaseSplitRegex();

	[GeneratedRegex(@"\w", RegexOptions.Compiled, 5000)]
	internal static partial Regex TitleCaseWordRegex();

	[GeneratedRegex(@"^\s$", RegexOptions.None, 5000)]
	internal static partial Regex SingleWhiteSpaceRegex();
}

internal static class PsShared
{
	internal const string LongSentence = "  Lorem ipsum   dolor sit amet,\tconsectetur  adipiscing elit, sed do   eiusmod tempor incididunt ut labore\r\n et dolore magna aliqua. Ut enim ad minim veniam,  quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat.  ";

	// Verbatim copy of Strings.StrComp(string?, string?)
	internal static bool OriginalStrComp(string? s1, string? s2)
	{
		if (s1 == null && s2 == null)
		{
			return true;
		}

		if (s1 == null)
		{
			return s2!.Length == 0;
		}

		if (s2 == null)
		{
			return s1.Length == 0;
		}

		return string.Equals(s1, s2);
	}
}

/// <summary>TrimFull: Trim + Regex.Replace(\s+, " ") + Trim vs one span pass that allocates only the result (or nothing when no whitespace run needs collapsing).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsTrimFullBenchmarks
{
	[Params("  John   Smith  ", PsShared.LongSentence)]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string? Original()
	{
		return OriginalTrimFull(Input);
	}

	[Benchmark]
	public string? Shared_SpanSinglePass()
	{
		return OptimizedTrimFull(Input);
	}

	// Verbatim copy of Strings.TrimFull
	internal static string? OriginalTrimFull(string? s)
	{
		if (!string.IsNullOrWhiteSpace(s))
		{
			s = PsRegexes.MultiSpaceRegex().Replace(s.Trim(), " ");
		}
		return s?.Trim();
	}

	// Regex \s is char.IsWhiteSpace and string.Trim trims char.IsWhiteSpace, so: trim, then collapse every whitespace run (including a single non-space whitespace char) to one ' '.
	internal static string? OptimizedTrimFull(string? s)
	{
		if (string.IsNullOrWhiteSpace(s))
		{
			return s?.Trim();
		}

		ReadOnlySpan<char> span = s.AsSpan().Trim();

		// Trimmed span never ends in whitespace, so span[i + 1] is always in range when span[i] is whitespace
		int firstChange = -1;
		for (int i = 0; i < span.Length; i++)
		{
			char c = span[i];
			if (char.IsWhiteSpace(c) && (c != ' ' || char.IsWhiteSpace(span[i + 1])))
			{
				firstChange = i;
				break;
			}
		}

		if (firstChange < 0)
		{
			return span.Length == s.Length ? s : span.ToString();
		}

		char[]? rented = null;
		Span<char> buffer = span.Length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(span.Length));
		try
		{
			span[..firstChange].CopyTo(buffer);
			int position = firstChange;
			bool inWhiteSpace = false;
			for (int i = firstChange; i < span.Length; i++)
			{
				char c = span[i];
				if (char.IsWhiteSpace(c))
				{
					if (!inWhiteSpace)
					{
						buffer[position++] = ' ';
						inWhiteSpace = true;
					}
				}
				else
				{
					buffer[position++] = c;
					inWhiteSpace = false;
				}
			}
			return buffer[..position].ToString();
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

/// <summary>NormalizeWhiteSpace: Trim + Replace("\t") + ToCharArray + new string (3-4 allocations) vs one span pass into a stack/pooled buffer (1 allocation, 0 when unchanged).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsNormalizeWhiteSpaceBenchmarks
{
	[Params("Hello   World\t\nTest", PsShared.LongSentence)]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return OriginalNormalizeWhiteSpace(Input);
	}

	[Benchmark]
	public string Shared_SpanSinglePass()
	{
		return OptimizedNormalizeWhiteSpace(Input);
	}

	// Verbatim copy of Strings.NormalizeWhiteSpace
	internal static string OriginalNormalizeWhiteSpace(string? input)
	{
		if (input == null)
		{
			return string.Empty;
		}

		input = input.Trim().Replace("\t", string.Empty);

		int len = input.Length;
		int index = 0;
		int i = 0;

		char[] sourceCharArray = input.ToCharArray();
		bool skip = false;
		char character;

		for (; i < len; i++)
		{
			character = sourceCharArray[i];
			switch (character)
			{
				case '\u0020':
				case '\u00A0':
				case '\u1680':
				case '\u2000':
				case '\u2001':
				case '\u2002':
				case '\u2003':
				case '\u2004':
				case '\u2005':
				case '\u2006':
				case '\u2007':
				case '\u2008':
				case '\u2009':
				case '\u200A':
				case '\u202F':
				case '\u205F':
				case '\u3000':
				case '\u2028':
				case '\u2029':
				case '\u0009':
				case '\u000A':
				case '\u000B':
				case '\u000C':
				case '\u000D':
				case '\u0085':
					if (skip)
					{
						continue;
					}

					sourceCharArray[index++] = character;
					skip = true;
					continue;

				default:
					skip = false;
					sourceCharArray[index++] = character;
					continue;
			}
		}
		return new(sourceCharArray, 0, index);
	}

	// The switch above lists exactly the char.IsWhiteSpace set (verified in PsVerify). Tabs are dropped without touching the run state, matching the original pre-pass Replace.
	internal static string OptimizedNormalizeWhiteSpace(string? input)
	{
		if (input == null)
		{
			return string.Empty;
		}

		ReadOnlySpan<char> span = input.AsSpan().Trim();
		char[]? rented = null;
		Span<char> buffer = span.Length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(span.Length));
		try
		{
			int index = 0;
			bool skip = false;
			foreach (char c in span)
			{
				if (c == '\t')
				{
					continue;
				}

				if (char.IsWhiteSpace(c))
				{
					if (skip)
					{
						continue;
					}

					buffer[index++] = c;
					skip = true;
				}
				else
				{
					skip = false;
					buffer[index++] = c;
				}
			}

			// Only removals happen, so an unchanged length means the content is unchanged
			return index == input.Length ? input : buffer[..index].ToString();
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

/// <summary>StrEq: string.Trim() on both sides (allocates whenever padded, e.g. CHAR(n) DB columns) vs span Trim + MemoryExtensions.Equals with the same InvariantCultureIgnoreCase comparison.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsStrEqBenchmarks
{
	private string left = string.Empty;
	private string right = string.Empty;

	[Params(false, true)]
	public bool Padded { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		left = Padded ? "  ACME Corporation      " : "ACME Corporation";
		right = Padded ? "acme corporation  " : "acme corporation";
	}

	[Benchmark(Baseline = true)]
	public bool Original()
	{
		return OriginalStrEq(left, right);
	}

	[Benchmark]
	public bool Shared_SpanTrim()
	{
		return OptimizedStrEq(left, right);
	}

	// Verbatim copy of Strings.StrEq
	internal static bool OriginalStrEq(string? s1, string? s2)
	{
		if (s1 == null && s2 == null)
		{
			return true;
		}

		if (s1 == null || s2 == null)
		{
			return string.Equals(string.Empty, (s1 ?? s2)?.Trim(), StringComparison.InvariantCultureIgnoreCase);
		}

		return string.Equals(s1.Trim(), s2.Trim(), StringComparison.InvariantCultureIgnoreCase);
	}

	// string.Equals(a, b, InvariantCultureIgnoreCase) and MemoryExtensions.Equals(a, b, InvariantCultureIgnoreCase) both resolve to CompareInfo.Invariant.Compare(.., IgnoreCase) == 0
	internal static bool OptimizedStrEq(string? s1, string? s2)
	{
		if (s1 == null && s2 == null)
		{
			return true;
		}

		if (s1 == null || s2 == null)
		{
			return string.Empty.AsSpan().Equals((s1 ?? s2).AsSpan().Trim(), StringComparison.InvariantCultureIgnoreCase);
		}

		return s1.AsSpan().Trim().Equals(s2.AsSpan().Trim(), StringComparison.InvariantCultureIgnoreCase);
	}
}

/// <summary>MakeNullNull: ToUpperInvariant() + Replace("NULL") allocates a full upper-cased copy for every non-null value vs an allocation-free "is it NULL repeated" char check (plus span-based StrEq).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsMakeNullNullBenchmarks
{
	[Params("John Smith", "  Null  ")]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string? Original()
	{
		return OriginalMakeNullNull(Input);
	}

	[Benchmark]
	public string? Shared_NoUpperCopy()
	{
		return OptimizedMakeNullNull(Input);
	}

	// Verbatim copy of Strings.MakeNullNull (s?.StrEq(..) != false reduces to StrEq(..) because s is known non-null at that point)
	internal static string? OriginalMakeNullNull(string? s)
	{
		if (!string.IsNullOrWhiteSpace(s) && (PsStrEqBenchmarks.OriginalStrEq(s, "Null") || s.ToUpperInvariant().Replace("NULL", string.Empty)?.Length == 0))
		{
			return null;
		}
		return s;
	}

	internal static string? OptimizedMakeNullNull(string? s)
	{
		if (!string.IsNullOrWhiteSpace(s) && (PsStrEqBenchmarks.OptimizedStrEq(s, "Null") || IsRepeatedNull(s)))
		{
			return null;
		}
		return s;
	}

	// Equivalent to s.ToUpperInvariant().Replace("NULL", "").Length == 0: "NULL" cannot overlap itself, so the ordinal Replace only empties the
	// string when it is exactly "NULL" repeated. ToUpperInvariant is a per-UTF-16-unit simple mapping for BMP chars and maps surrogates to surrogates.
	private static bool IsRepeatedNull(string s)
	{
		if ((s.Length & 3) != 0)
		{
			return false;
		}

		for (int i = 0; i < s.Length; i += 4)
		{
			if (char.ToUpperInvariant(s[i]) != 'N' || char.ToUpperInvariant(s[i + 1]) != 'U' || char.ToUpperInvariant(s[i + 2]) != 'L' || char.ToUpperInvariant(s[i + 3]) != 'L')
			{
				return false;
			}
		}
		return true;
	}
}

/// <summary>GetHash: StringBuilder + byte.ToString("x2") per byte (one string per byte) vs a single string.Create hex pass (Shared) or stackalloc UTF-8 + span HashData + Convert.ToHexStringLower (Net11).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsGetHashBenchmarks
{
	private const string HexChars = "0123456789abcdef";

	[Params("user@example.com", PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence)]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return OriginalGetHash(Input, EHashAlgorithm.SHA256);
	}

	[Benchmark]
	public string Shared_StringCreateHex()
	{
		return SharedGetHash(Input, EHashAlgorithm.SHA256);
	}

	[Benchmark]
	public string Net11_SpanHashToHexStringLower()
	{
		return Net11GetHash(Input, EHashAlgorithm.SHA256);
	}

	// Verbatim copy of Strings.GetHash with Internal.HashCompat.XxxHashData replaced by the XXX.HashData calls it forwards to on net11.0
	internal static string OriginalGetHash(string originalString, EHashAlgorithm algorithm)
	{
		ReadOnlySpan<byte> bytes = algorithm switch
		{
			EHashAlgorithm.SHA1 => SHA1.HashData(Encoding.UTF8.GetBytes(originalString)),
			EHashAlgorithm.SHA256 => SHA256.HashData(Encoding.UTF8.GetBytes(originalString)),
			EHashAlgorithm.SHA384 => SHA384.HashData(Encoding.UTF8.GetBytes(originalString)),
			EHashAlgorithm.MD5 => MD5.HashData(Encoding.UTF8.GetBytes(originalString)),
			_ => SHA512.HashData(Encoding.UTF8.GetBytes(originalString)),
		};
		StringBuilder builder = new();
		for (int i = 0; i < bytes.Length; i++)
		{
			builder.Append(bytes[i].ToString("x2"));
		}
		return builder.ToString();
	}

	// Hashing unchanged (netstandard2.1 keeps using HashCompat), only the hex encoding changes
	internal static string SharedGetHash(string originalString, EHashAlgorithm algorithm)
	{
		byte[] bytes = algorithm switch
		{
			EHashAlgorithm.SHA1 => SHA1.HashData(Encoding.UTF8.GetBytes(originalString)),
			EHashAlgorithm.SHA256 => SHA256.HashData(Encoding.UTF8.GetBytes(originalString)),
			EHashAlgorithm.SHA384 => SHA384.HashData(Encoding.UTF8.GetBytes(originalString)),
			EHashAlgorithm.MD5 => MD5.HashData(Encoding.UTF8.GetBytes(originalString)),
			_ => SHA512.HashData(Encoding.UTF8.GetBytes(originalString)),
		};

		return string.Create(bytes.Length * 2, bytes, static (destination, hash) =>
		{
			for (int i = 0; i < hash.Length; i++)
			{
				byte b = hash[i];
				destination[i * 2] = HexChars[b >> 4];
				destination[(i * 2) + 1] = HexChars[b & 0xF];
			}
		});
	}

	internal static string Net11GetHash(string originalString, EHashAlgorithm algorithm)
	{
		// Encoding.GetBytes(string) throws ArgumentNullException("s") for null, the span overload would silently hash an empty input
		if (originalString == null)
		{
			throw new ArgumentNullException("s");
		}

		// Larger inputs keep the array overload so pathological sizes fail exactly like the original
		Span<byte> utf8Buffer = stackalloc byte[1024];
		ReadOnlySpan<byte> data = originalString.Length > 341 ? Encoding.UTF8.GetBytes(originalString) : utf8Buffer[..Encoding.UTF8.GetBytes(originalString, utf8Buffer)];

		Span<byte> hash = stackalloc byte[64];
		int hashLength = algorithm switch
		{
			EHashAlgorithm.SHA1 => SHA1.HashData(data, hash),
			EHashAlgorithm.SHA256 => SHA256.HashData(data, hash),
			EHashAlgorithm.SHA384 => SHA384.HashData(data, hash),
			EHashAlgorithm.MD5 => MD5.HashData(data, hash),
			_ => SHA512.HashData(data, hash),
		};
		return Convert.ToHexStringLower(hash[..hashLength]);
	}
}

/// <summary>TrimObjectStrings / MakeObjectNullNull: GetOrAdd(key, _ => Create(recursive)) allocates a closure + delegate on every call (even for null obj) vs a static lambda reading the key, or a static generic cache class.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsObjectStringsCacheBenchmarks
{
	public sealed class Target
	{
		public string? Name { get; set; } = "  John   Smith  ";
		public string? Email { get; set; } = "john@example.com";
	}

	private static readonly ConcurrentDictionary<(Type, bool), Delegate> OriginalCache = new();
	private static readonly ConcurrentDictionary<(Type, bool), Delegate> StaticLambdaCache = new();
	private readonly Target target = new();

	[Benchmark(Baseline = true)]
	public Target? Original()
	{
		return OriginalTrimObjectStrings(target);
	}

	[Benchmark]
	public Target? Shared_StaticLambda()
	{
		return StaticLambdaTrimObjectStrings(target);
	}

	[Benchmark]
	public Target? Shared_StaticGenericCache()
	{
		return StaticGenericCacheTrimObjectStrings(target);
	}

	// The real factory (expression build + CompileFast) only runs once per key, so a no-op action isolates the per-call lookup overhead being measured
	private static Action<T> CreateAction<T>(bool recursive) where T : class
	{
		_ = recursive;
		return static _ => { };
	}

	// Verbatim copy of Strings.TrimObjectStrings (identical shape to MakeObjectNullNull) with the cached factory swapped for CreateAction
	internal static T? OriginalTrimObjectStrings<T>(T? obj, bool recursive = false) where T : class
	{
		if (obj == null)
		{
			return obj;
		}

		Type type = typeof(T);
		(Type type, bool recursive) key = (type, recursive);

		Action<T> action = (Action<T>)OriginalCache.GetOrAdd(key, _ => CreateAction<T>(recursive));
		action(obj);

		return obj;
	}

	// Non-capturing lambdas in generic methods are cached per T by the compiler, so this allocates nothing after the first call
	internal static T? StaticLambdaTrimObjectStrings<T>(T? obj, bool recursive = false) where T : class
	{
		if (obj == null)
		{
			return obj;
		}

		Action<T> action = (Action<T>)StaticLambdaCache.GetOrAdd((typeof(T), recursive), static key => CreateAction<T>(key.Item2));
		action(obj);

		return obj;
	}

	private static class ActionCache<T> where T : class
	{
		internal static Action<T>? NonRecursive;
		internal static Action<T>? Recursive;
	}

	internal static T? StaticGenericCacheTrimObjectStrings<T>(T? obj, bool recursive = false) where T : class
	{
		if (obj == null)
		{
			return obj;
		}

		Action<T> action = recursive ? (ActionCache<T>.Recursive ??= CreateAction<T>(true)) : (ActionCache<T>.NonRecursive ??= CreateAction<T>(false));
		action(obj);

		return obj;
	}
}

/// <summary>ToTitleCase: new CultureInfo(name, false) (+ fresh TextInfo) per call and ToUpper()/ToLower() copies per word vs cached CultureInfo.GetCultureInfo and span-based casing checks.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsToTitleCaseBenchmarks
{
	private const string Input = "the QUICK brown FOX jumps over the LAZY dog's back - NASA report, vol. II";

	[Params(TitleCaseUppercaseWordHandling.IgnoreUppercase, TitleCaseUppercaseWordHandling.ConvertAllUppercase)]
	public TitleCaseUppercaseWordHandling Handling { get; set; }

	[Benchmark(Baseline = true)]
	public string? Original()
	{
		return OriginalToTitleCase(Input, "en-US", Handling);
	}

	[Benchmark]
	public string? Shared_CachedCultureSpanCasing()
	{
		return OptimizedToTitleCase(Input, "en-US", Handling);
	}

	// Verbatim copy of Strings.ToTitleCase
	internal static string? OriginalToTitleCase(string? input, string cultureString = "en-US", TitleCaseUppercaseWordHandling uppercaseHandling = TitleCaseUppercaseWordHandling.IgnoreUppercase, int minLengthToConvert = 0, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return input;
		}

		TextInfo textInfo = new CultureInfo(cultureString, false).TextInfo;

		if (uppercaseHandling == TitleCaseUppercaseWordHandling.IgnoreUppercase)
		{
			return textInfo.ToTitleCase(input);
		}

		string[] words = PsRegexes.TitleCaseSplitRegex().Split(input);
		StringBuilder result = new();

		foreach (string word in words)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrEmpty(word))
			{
				continue;
			}

			bool isAllUppercase = word.Length > 0 && PsShared.OriginalStrComp(word, word.ToUpper()) && !PsShared.OriginalStrComp(word, word.ToLower());
			if (isAllUppercase)
			{
				switch (uppercaseHandling)
				{
					case TitleCaseUppercaseWordHandling.ConvertAllUppercase:
						result.Append(textInfo.ToTitleCase(word.ToLower()));
						break;
					case TitleCaseUppercaseWordHandling.ConvertByLength:
						if (word.Length >= minLengthToConvert)
						{
							result.Append(textInfo.ToTitleCase(word.ToLower()));
						}
						else
						{
							result.Append(word);
						}
						break;
				}
			}
			else
			{
				if (PsRegexes.TitleCaseWordRegex().IsMatch(word))
				{
					result.Append(textInfo.ToTitleCase(word.ToLower()));
				}
				else
				{
					result.Append(word);
				}
			}
		}

		return result.ToString();
	}

	internal static string? OptimizedToTitleCase(string? input, string cultureString = "en-US", TitleCaseUppercaseWordHandling uppercaseHandling = TitleCaseUppercaseWordHandling.IgnoreUppercase, int minLengthToConvert = 0, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return input;
		}

		// GetCultureInfo returns a cached, read-only culture built with useUserOverride: false, i.e. the same TextInfo rules as new CultureInfo(name, false)
		TextInfo textInfo = CultureInfo.GetCultureInfo(cultureString).TextInfo;

		if (uppercaseHandling == TitleCaseUppercaseWordHandling.IgnoreUppercase)
		{
			return textInfo.ToTitleCase(input);
		}

		string[] words = PsRegexes.TitleCaseSplitRegex().Split(input);
		StringBuilder result = new(input.Length);

		foreach (string word in words)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (string.IsNullOrEmpty(word))
			{
				continue;
			}

			if (IsAllUppercase(word))
			{
				switch (uppercaseHandling)
				{
					case TitleCaseUppercaseWordHandling.ConvertAllUppercase:
						result.Append(textInfo.ToTitleCase(word.ToLower()));
						break;
					case TitleCaseUppercaseWordHandling.ConvertByLength:
						if (word.Length >= minLengthToConvert)
						{
							result.Append(textInfo.ToTitleCase(word.ToLower()));
						}
						else
						{
							result.Append(word);
						}
						break;
				}
			}
			else
			{
				if (PsRegexes.TitleCaseWordRegex().IsMatch(word))
				{
					result.Append(textInfo.ToTitleCase(word.ToLower()));
				}
				else
				{
					result.Append(word);
				}
			}
		}

		return result.ToString();
	}

	// Same as word == word.ToUpper() && word != word.ToLower() (current culture, ordinal equality) without allocating either cased copy
	private static bool IsAllUppercase(string word)
	{
		if (word.Length > 256)
		{
			return string.Equals(word, word.ToUpper()) && !string.Equals(word, word.ToLower());
		}

		CultureInfo culture = CultureInfo.CurrentCulture;
		Span<char> buffer = stackalloc char[256];
		Span<char> cased = buffer[..word.Length];
		word.AsSpan().ToUpper(cased, culture);
		if (!cased.SequenceEqual(word.AsSpan()))
		{
			return false;
		}

		word.AsSpan().ToLower(cased, culture);
		return !cased.SequenceEqual(word.AsSpan());
	}
}

/// <summary>FormatPhoneNumber: ToLowerInvariant + Split("x") + Regex.Split + Concat + interpolated replacement pattern + Regex.Replace + concat vs one span pass into a stack buffer.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsFormatPhoneNumberBenchmarks
{
	[Params("(555) 123-4567", "+1 (555) 123-4567 x 890")]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string? Original()
	{
		return OriginalFormatPhoneNumber(Input);
	}

	[Benchmark]
	public string? Shared_SpanFormat()
	{
		return OptimizedFormatPhoneNumber(Input);
	}

	// Verbatim copy of Strings.FormatPhoneNumber
	internal static string? OriginalFormatPhoneNumber(string? input, string separator = "-", bool addParenToAreaCode = false)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return input;
		}

		string[] phoneParts = input.ToLowerInvariant().Split("x");
		string? extension = phoneParts.Length > 1 ? phoneParts[1] : null;

		input = string.Concat(PsRegexes.ExtractNumbersRegex().Split(phoneParts[0]));

		Regex? phoneParser;
		string format;

		switch (input.Length)
		{
			case 7:
				phoneParser = PsRegexes.SevenDigitPhoneNumberRegex();
				format = $"$1{separator}$2";
				break;

			case 10:
				phoneParser = PsRegexes.TenDigitPhoneNumberRegex();
				format = !addParenToAreaCode ? $"$1{separator}$2{separator}$3" : $"($1){separator}$2{separator}$3";
				break;

			case 11:
				phoneParser = PsRegexes.ElevenDigitPhoneNumberRegex();
				format = !addParenToAreaCode ? $"+$1 $2{separator}$3{separator}$4" : $"+$1 ($2){separator}$3{separator}$4";
				break;

			case 12:
				phoneParser = PsRegexes.TwelveDigitPhoneNumberRegex();
				format = !addParenToAreaCode ? $"+$1 $2{separator}$3{separator}$4" : $"+$1 ($2){separator}$3{separator}$4";
				break;
			default:
				if (extension != null)
				{
					input += $"x{extension}";
				}
				return input;
		}
		input = phoneParser.Replace(input, format);
		if (extension != null)
		{
			input += $"x{extension}";
		}
		return input;
	}

	// Only 'x'/'X' lower-case to 'x', \d is char.IsDigit and casing never creates/removes digits (all verified over the BMP in PsVerify).
	// A '$' or a leading ASCII digit in the separator changes how Regex.Replace parses "$1{separator}$2", so those separators keep the original path.
	internal static string? OptimizedFormatPhoneNumber(string? input, string separator = "-", bool addParenToAreaCode = false)
	{
		if (string.IsNullOrWhiteSpace(input))
		{
			return input;
		}

		string sep = separator ?? string.Empty;
		ReadOnlySpan<char> source = input.AsSpan();
		int firstX = source.IndexOfAny('x', 'X');
		ReadOnlySpan<char> number = firstX < 0 ? source : source[..firstX];
		ReadOnlySpan<char> extension = ReadOnlySpan<char>.Empty;
		if (firstX >= 0)
		{
			ReadOnlySpan<char> rest = source[(firstX + 1)..];
			int secondX = rest.IndexOfAny('x', 'X');
			extension = secondX < 0 ? rest : rest[..secondX];
		}

		int maxLength = number.Length + (2 * sep.Length) + 4 + (firstX >= 0 ? 1 + extension.Length : 0);
		if (sep.Contains('$') || (sep.Length > 0 && (uint)(sep[0] - '0') <= 9) || number.Length > 128 || maxLength > 256)
		{
			return OriginalFormatPhoneNumber(input, separator!, addParenToAreaCode);
		}

		Span<char> digitBuffer = stackalloc char[128];
		int digitCount = 0;
		foreach (char c in number)
		{
			if (char.IsDigit(c))
			{
				digitBuffer[digitCount++] = c;
			}
		}
		ReadOnlySpan<char> d = digitBuffer[..digitCount];

		Span<char> output = stackalloc char[256];
		int position = 0;
		switch (digitCount)
		{
			case 7:
				Append(output, ref position, d[..3]);
				Append(output, ref position, sep);
				Append(output, ref position, d[3..]);
				break;

			case 10:
				AppendAreaCode(output, ref position, d[..3], addParenToAreaCode);
				Append(output, ref position, sep);
				Append(output, ref position, d[3..6]);
				Append(output, ref position, sep);
				Append(output, ref position, d[6..]);
				break;

			case 11:
				output[position++] = '+';
				Append(output, ref position, d[..1]);
				output[position++] = ' ';
				AppendAreaCode(output, ref position, d[1..4], addParenToAreaCode);
				Append(output, ref position, sep);
				Append(output, ref position, d[4..7]);
				Append(output, ref position, sep);
				Append(output, ref position, d[7..]);
				break;

			case 12:
				output[position++] = '+';
				Append(output, ref position, d[..2]);
				output[position++] = ' ';
				AppendAreaCode(output, ref position, d[2..5], addParenToAreaCode);
				Append(output, ref position, sep);
				Append(output, ref position, d[5..8]);
				Append(output, ref position, sep);
				Append(output, ref position, d[8..]);
				break;

			default:
				Append(output, ref position, d);
				break;
		}

		if (firstX >= 0)
		{
			output[position++] = 'x';
			position += extension.ToLowerInvariant(output[position..]);
		}

		return output[..position].ToString();
	}

	private static void Append(Span<char> destination, ref int position, ReadOnlySpan<char> value)
	{
		value.CopyTo(destination[position..]);
		position += value.Length;
	}

	private static void AppendAreaCode(Span<char> destination, ref int position, ReadOnlySpan<char> areaCode, bool addParen)
	{
		if (addParen)
		{
			destination[position++] = '(';
			Append(destination, ref position, areaCode);
			destination[position++] = ')';
		}
		else
		{
			Append(destination, ref position, areaCode);
		}
	}
}

/// <summary>GetOnlyLetters: string.Concat(MatchCollection) of a single-char class allocates a Match + string per kept character vs one span filter pass.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsGetOnlyLettersBenchmarks
{
	[Params("  Order #12345 for John O'Brien-Smith  ", PsShared.LongSentence)]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string? Original()
	{
		return OriginalGetOnlyLetters(Input);
	}

	[Benchmark]
	public string? Shared_SpanFilter()
	{
		return OptimizedGetOnlyLetters(Input);
	}

	// Verbatim copy of Strings.GetOnlyLetters
	internal static string? OriginalGetOnlyLetters(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		return string.Concat(PsRegexes.LettersOnlyRegex().Matches(value.Trim())).Trim();
	}

	internal static string? OptimizedGetOnlyLetters(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}

		ReadOnlySpan<char> source = value.AsSpan().Trim();
		char[]? rented = null;
		Span<char> buffer = source.Length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(source.Length));
		try
		{
			int position = 0;
			foreach (char c in source)
			{
				// [A-Za-z ] without IgnoreCase: ASCII letters only, (c | 0x20) folds 'A'-'Z' onto 'a'-'z' and maps nothing else into that range
				if (c == ' ' || (uint)((c | 0x20) - 'a') <= 'z' - 'a')
				{
					buffer[position++] = c;
				}
			}
			return ((ReadOnlySpan<char>)buffer[..position]).Trim().ToString();
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

/// <summary>TryFractionToDecimal (also TryStringToDecimal/TryFractionToDouble callers): exceptions as control flow (FormatException/DivideByZeroException caught) + string.Split vs a non-throwing span parser.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsTryFractionToDecimalBenchmarks
{
	private static readonly char[] FractionSplitChars = new[] { ' ', '/' };

	[Params("1 1/2", "192.168.1.1")]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public decimal? Original()
	{
		OriginalTryFractionToDecimal(Input, out decimal? result);
		return result;
	}

	[Benchmark]
	public decimal? Shared_NonThrowingSpanParse()
	{
		OptimizedTryFractionToDecimal(Input, out decimal? result);
		return result;
	}

	// Verbatim copy of Strings.TryFractionToDecimal(string?, out decimal?)
	internal static bool OriginalTryFractionToDecimal(string? fractionString, out decimal? result)
	{
		result = null;
		if (string.IsNullOrWhiteSpace(fractionString))
		{
			return false;
		}

		bool success = true;
		try
		{
			result = OriginalFractionToDecimal(fractionString);
		}
		catch (Exception)
		{
			success = false;
		}
		return success;
	}

	// Verbatim copy of Strings.FractionToDecimal
	internal static decimal? OriginalFractionToDecimal(string? fractionString)
	{
		if (fractionString == null)
		{
			return null;
		}

		if (decimal.TryParse(fractionString, out decimal result))
		{
			return result;
		}

		string[] split = fractionString.Split(FractionSplitChars);

		if (split.Length is 2 or 3 && int.TryParse(split[0], out int numeratorOrWhole) && int.TryParse(split[1], out int denominatorOrNumerator))
		{
			if (split.Length == 2)
			{
				return (decimal)numeratorOrWhole / denominatorOrNumerator;
			}

			if (int.TryParse(split[2], out int denominator))
			{
				return numeratorOrWhole + ((decimal)denominatorOrNumerator / denominator);
			}
		}

		throw new FormatException("Not a valid fraction.");
	}

	internal static bool OptimizedTryFractionToDecimal(string? fractionString, out decimal? result)
	{
		result = null;
		if (string.IsNullOrWhiteSpace(fractionString))
		{
			return false;
		}

		if (!TryParseFraction(fractionString, out decimal value))
		{
			return false;
		}

		result = value;
		return true;
	}

	// Returns false exactly where FractionToDecimal throws (FormatException, or DivideByZeroException for a zero denominator).
	// int.TryParse(string) is TryParse(span, NumberStyles.Integer, NumberFormatInfo.CurrentInfo), so part parsing is unchanged.
	private static bool TryParseFraction(string fractionString, out decimal value)
	{
		if (decimal.TryParse(fractionString, out value))
		{
			return true;
		}

		value = default;
		ReadOnlySpan<char> s = fractionString.AsSpan();
		int firstSeparator = s.IndexOfAny(' ', '/');
		if (firstSeparator < 0)
		{
			return false;
		}

		ReadOnlySpan<char> first = s[..firstSeparator];
		ReadOnlySpan<char> remainder = s[(firstSeparator + 1)..];
		int secondSeparator = remainder.IndexOfAny(' ', '/');
		ReadOnlySpan<char> second = secondSeparator < 0 ? remainder : remainder[..secondSeparator];
		ReadOnlySpan<char> third = secondSeparator < 0 ? ReadOnlySpan<char>.Empty : remainder[(secondSeparator + 1)..];
		if (secondSeparator >= 0 && third.IndexOfAny(' ', '/') >= 0)
		{
			return false;
		}

		if (!int.TryParse(first, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out int numeratorOrWhole) ||
			!int.TryParse(second, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out int denominatorOrNumerator))
		{
			return false;
		}

		if (secondSeparator < 0)
		{
			if (denominatorOrNumerator == 0)
			{
				return false;
			}

			value = (decimal)numeratorOrWhole / denominatorOrNumerator;
			return true;
		}

		if (!int.TryParse(third, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out int denominator) || denominator == 0)
		{
			return false;
		}

		value = numeratorOrWhole + ((decimal)denominatorOrNumerator / denominator);
		return true;
	}
}

/// <summary>ReplaceInverse(string pattern): constructs (parses + compiles to interpreter code) a new Regex on every call and appends match.Value strings vs a bounded Regex cache + span appends (Shared) or EnumerateMatches (Net11).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsReplaceInverseBenchmarks
{
	private const string Input = "Order #12345 shipped 2026-11-12, tracking 1Z999AA10123456784, qty 3";
	private const string Pattern = @"\d+";
	private const int MaxCachedRegexes = 64;
	private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options, TimeSpan Timeout, string Culture), Regex> RegexCache = new();

	[Benchmark(Baseline = true)]
	public string? Original()
	{
		return OriginalReplaceInverse(Input, Pattern, "*");
	}

	[Benchmark]
	public string? Shared_CachedRegexSpanAppend()
	{
		return SharedReplaceInverse(Input, Pattern, "*");
	}

	[Benchmark]
	public string? Net11_CachedRegexEnumerateMatches()
	{
		return Net11ReplaceInverse(Input, Pattern, "*");
	}

	// Verbatim copy of Strings.ReplaceInverse(string?, string, ...)
	internal static string? OriginalReplaceInverse(string? input, string regexPattern, string? replacement = "", bool matchFirstOnly = false, RegexOptions regexOptions = RegexOptions.None, TimeSpan? matchTimeout = null)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		Regex regex = new(regexPattern, regexOptions, matchTimeout ?? TimeSpan.FromSeconds(5));
		return OriginalReplaceInverse(regex, input, replacement, matchFirstOnly);
	}

	// Verbatim copy of Strings.ReplaceInverse(Regex, string?, ...)
	internal static string? OriginalReplaceInverse(Regex regex, string? input, string? replacement = "", bool matchFirstOnly = false)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		replacement ??= string.Empty;

		StringBuilder result = new();
		int lastMatchEnd = 0;

		foreach (Match match in regex.Matches(input))
		{
			if (match.Index > lastMatchEnd && replacement.Length > 0)
			{
				result.Append(replacement);
			}

			result.Append(match.Value);
			lastMatchEnd = match.Index + match.Length;

			if (matchFirstOnly)
			{
				break;
			}
		}

		if (lastMatchEnd < input.Length && replacement.Length > 0)
		{
			result.Append(replacement);
		}

		return result.ToString();
	}

	// Regex instances are immutable and thread-safe. IgnoreCase regexes capture CurrentCulture at construction, so the culture is part of the key
	// (mirrors Regex's own static-method cache). The cap keeps caller-generated patterns from growing the cache without bound.
	private static Regex GetOrCreateRegex(string regexPattern, RegexOptions regexOptions, TimeSpan matchTimeout)
	{
		(string, RegexOptions, TimeSpan, string) key = (regexPattern, regexOptions, matchTimeout, CultureInfo.CurrentCulture.Name);
		if (RegexCache.TryGetValue(key, out Regex? regex))
		{
			return regex;
		}

		regex = new Regex(regexPattern, regexOptions, matchTimeout);
		if (RegexCache.Count < MaxCachedRegexes)
		{
			RegexCache.TryAdd(key, regex);
		}
		return regex;
	}

	internal static string? SharedReplaceInverse(string? input, string regexPattern, string? replacement = "", bool matchFirstOnly = false, RegexOptions regexOptions = RegexOptions.None, TimeSpan? matchTimeout = null)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		return SharedReplaceInverse(GetOrCreateRegex(regexPattern, regexOptions, matchTimeout ?? TimeSpan.FromSeconds(5)), input, replacement, matchFirstOnly);
	}

	internal static string? SharedReplaceInverse(Regex regex, string? input, string? replacement = "", bool matchFirstOnly = false)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		replacement ??= string.Empty;

		StringBuilder result = new(input.Length);
		int lastMatchEnd = 0;

		foreach (Match match in regex.Matches(input))
		{
			if (match.Index > lastMatchEnd && replacement.Length > 0)
			{
				result.Append(replacement);
			}

			result.Append(input.AsSpan(match.Index, match.Length));
			lastMatchEnd = match.Index + match.Length;

			if (matchFirstOnly)
			{
				break;
			}
		}

		if (lastMatchEnd < input.Length && replacement.Length > 0)
		{
			result.Append(replacement);
		}

		return result.ToString();
	}

	internal static string? Net11ReplaceInverse(string? input, string regexPattern, string? replacement = "", bool matchFirstOnly = false, RegexOptions regexOptions = RegexOptions.None, TimeSpan? matchTimeout = null)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		return Net11ReplaceInverse(GetOrCreateRegex(regexPattern, regexOptions, matchTimeout ?? TimeSpan.FromSeconds(5)), input, replacement, matchFirstOnly);
	}

	internal static string? Net11ReplaceInverse(Regex regex, string? input, string? replacement = "", bool matchFirstOnly = false)
	{
		if (string.IsNullOrEmpty(input))
		{
			return input;
		}

		replacement ??= string.Empty;

		StringBuilder result = new(input.Length);
		int lastMatchEnd = 0;

		foreach (ValueMatch match in regex.EnumerateMatches(input))
		{
			if (match.Index > lastMatchEnd && replacement.Length > 0)
			{
				result.Append(replacement);
			}

			result.Append(input.AsSpan(match.Index, match.Length));
			lastMatchEnd = match.Index + match.Length;

			if (matchFirstOnly)
			{
				break;
			}
		}

		if (lastMatchEnd < input.Length && replacement.Length > 0)
		{
			result.Append(replacement);
		}

		return result.ToString();
	}
}

/// <summary>CountChars: scalar foreach loop vs vectorized span.IndexOf skipping (Shared) or MemoryExtensions.Count (Net11, vectorized).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsCountCharsBenchmarks
{
	private const string CsvLine = "id,name,email,phone,city,state,zip,created,updated,status";

	[Params(CsvLine, PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence + PsShared.LongSentence)]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public int Original()
	{
		return OriginalCountChars(Input, ',');
	}

	[Benchmark]
	public int Shared_IndexOfSkip()
	{
		return SharedCountChars(Input, ',');
	}

	[Benchmark]
	public int Net11_SpanCount()
	{
		return Net11CountChars(Input, ',');
	}

	// Verbatim copy of Strings.CountChars(string?, char) + CountChars(ReadOnlySpan<char>, char)
	internal static int OriginalCountChars(string? input, char charToFind)
	{
		if (string.IsNullOrEmpty(input))
		{
			return 0;
		}

		return OriginalCountChars(input.AsSpan(), charToFind);
	}

	internal static int OriginalCountChars(ReadOnlySpan<char> input, char charToFind)
	{
		if (input.Length == 0)
		{
			return 0;
		}

		int count = 0;
		foreach (char @char in input)
		{
			if (@char == charToFind)
			{
				count++;
			}
		}
		return count;
	}

	internal static int SharedCountChars(string? input, char charToFind)
	{
		if (string.IsNullOrEmpty(input))
		{
			return 0;
		}

		ReadOnlySpan<char> span = input.AsSpan();
		int count = 0;
		int index;
		while ((index = span.IndexOf(charToFind)) >= 0)
		{
			count++;
			span = span[(index + 1)..];
		}
		return count;
	}

	internal static int Net11CountChars(string? input, char charToFind)
	{
		if (string.IsNullOrEmpty(input))
		{
			return 0;
		}

		return input.AsSpan().Count(charToFind);
	}
}

/// <summary>SanitizeForLog: three chained string.Replace passes (up to 3 allocations) vs one IndexOfAny probe (no allocation when clean) and a single rewrite pass.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsSanitizeForLogBenchmarks
{
	private static readonly char[] LineBreakChars = ['\r', '\n'];

	[Params("User 'bob' logged in from 10.0.0.1 via /api/login", "Line one\r\nLine two\nLine three\rEnd of user supplied value")]
	public string Input { get; set; } = string.Empty;

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return OriginalSanitizeForLog(Input);
	}

	[Benchmark]
	public string Shared_SinglePass()
	{
		return OptimizedSanitizeForLog(Input);
	}

	// Verbatim copy of Strings.SanitizeForLog
	internal static string OriginalSanitizeForLog(string input)
	{
		return input.Replace(Environment.NewLine, " ").Replace("\n", " ").Replace("\r", " ");
	}

	// Replace("\r\n") is ordinal, leftmost, non-overlapping, so it equals "CR immediately followed by LF -> one space"; every other CR/LF -> one space.
	internal static string OptimizedSanitizeForLog(string input)
	{
		// Instance call keeps the original NullReferenceException for null input
		int first = input.IndexOfAny(LineBreakChars);
		if (first < 0)
		{
			return input;
		}

		string newLine = Environment.NewLine;
		if (newLine is not "\r\n" and not "\n")
		{
			return OriginalSanitizeForLog(input);
		}

		bool collapseCrLf = newLine.Length == 2;
		char[]? rented = null;
		Span<char> buffer = input.Length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(input.Length));
		try
		{
			input.AsSpan(0, first).CopyTo(buffer);
			int position = first;
			for (int i = first; i < input.Length; i++)
			{
				char c = input[i];
				if (c == '\r')
				{
					buffer[position++] = ' ';
					if (collapseCrLf && i + 1 < input.Length && input[i + 1] == '\n')
					{
						i++;
					}
				}
				else if (c == '\n')
				{
					buffer[position++] = ' ';
				}
				else
				{
					buffer[position++] = c;
				}
			}
			return buffer[..position].ToString();
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

/// <summary>TimespanToShortForm: ToString + 2x Split + interpolated pattern + Replace + several substrings vs TryFormat into a stack buffer and span slicing (1 allocation).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PsTimespanToShortFormBenchmarks
{
	private TimeSpan value;

	[Params(0, 1)]
	public int Scenario { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		// 0: 00:01:05.4320000 (no days, ms present)  1: 2.03:04:05.6780000 (days + ms)
		value = Scenario == 0 ? TimeSpan.FromTicks(654_320_000) : new TimeSpan(2, 3, 4, 5, 678);
	}

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return OriginalTimespanToShortForm(value);
	}

	[Benchmark]
	public string Shared_TryFormatSpan()
	{
		return OptimizedTimespanToShortForm(value);
	}

	// Verbatim copy of Strings.TimespanToShortForm(in TimeSpan)
	internal static string OriginalTimespanToShortForm(in TimeSpan t)
	{
		string stringForm = t.ToString();

		if (t.Milliseconds > 0)
		{
			stringForm = stringForm.Replace($".{stringForm.Split(".")[^1]}", string.Empty);
		}

		stringForm = stringForm.Split(".")[^1];
		string days = string.Empty;

		if (t.Days > 0)
		{
			days = t.Days.ToString();
			if (PsShared.OriginalStrComp(days[..1], "0"))
			{
				days = days[1..];
			}
		}
		else
		{
			if (PsShared.OriginalStrComp(stringForm[..3], "00:"))
			{
				stringForm = stringForm[3..];

				if (PsShared.OriginalStrComp(stringForm[..1], "0"))
				{
					stringForm = stringForm[1..];
				}
			}
		}

		return string.IsNullOrWhiteSpace(days) ? stringForm : $"{days}:{stringForm}";
	}

	internal static string OptimizedTimespanToShortForm(in TimeSpan t)
	{
		// TimeSpan.ToString() is the "c" format ([-][d.]hh:mm:ss[.fffffff], max 26 chars), which is also TryFormat's default
		Span<char> formatted = stackalloc char[32];
		if (!t.TryFormat(formatted, out int written))
		{
			return OriginalTimespanToShortForm(t);
		}

		ReadOnlySpan<char> stringForm = formatted[..written];

		// Milliseconds > 0 implies a trailing ".fffffff"; that 8-char token cannot occur anywhere else, so the original Replace only strips this suffix
		if (t.Milliseconds > 0)
		{
			stringForm = stringForm[..stringForm.LastIndexOf('.')];
		}

		int lastDot = stringForm.LastIndexOf('.');
		if (lastDot >= 0)
		{
			stringForm = stringForm[(lastDot + 1)..];
		}

		if (t.Days > 0)
		{
			// A positive Days value never formats with a leading '0', so the original strip is a no-op
			Span<char> result = stackalloc char[48];
			t.Days.TryFormat(result, out int dayChars);
			result[dayChars] = ':';
			stringForm.CopyTo(result[(dayChars + 1)..]);
			return result[..(dayChars + 1 + stringForm.Length)].ToString();
		}

		// Slicing [..3] throws ArgumentOutOfRangeException for short input exactly like the original string slice
		if (stringForm[..3].SequenceEqual("00:".AsSpan()))
		{
			stringForm = stringForm[3..];

			if (stringForm[..1].SequenceEqual("0".AsSpan()))
			{
				stringForm = stringForm[1..];
			}
		}

		return stringForm.ToString();
	}
}

/// <summary>Asserts that every Original / Optimized pair in this file is observably identical (value and exception type) on edge-case inputs.</summary>
public static class PsVerify
{
	private static readonly string?[] StringCases =
	[
		null, "", " ", "   ", "\t", "\r\n", "\n\r", "\u00A0", "\u3000 \u2028",
		"a", " a ", "a  b", "a\tb", "a \t b", "a\t\tb", "  John   Smith  ", "already trimmed", "\t\nwhitespace\r\n", "Hello   World\t\nTest",
		"\u00A0x\u00A0\u00A0y\u3000", "\u2028line\u2029", "ab\u0085cd", "a\u000Bb\u000Cc", "a\u200Bb",
		"null", "NULL", "Null", " Null ", "\tNull\t", "nullnull", "NuLLnUlL", "  nullnull  ", "NULL NULL", "NULL\nNULL", "null\u0000", "nul", "nulll", "Null123", "123Null",
		"ı", "İ", "ﬀ", "ß", "Ünïcödé  ţëxt", "😀  emoji\t\ttext 😀", "\uD800 lone", "lone \uDC00", "x", "X", "xx",
		"1234567", "(555) 123-4567", "555.123.4567", "1234567890", "11234567890", "121234567890", "+1 (555) 123-4567 x 890", "555.123.4567X12x34", "x123", "12345", "٠١٢٣٤٥٦", "５５５１２３４",
		"1 1/2", "1/2", "3/4", "2", "2.5", "1/0", "0/0", "1 1/0", "2 3/4/5", "1//2", "/2", "1/", "a/b", " 1/2", "1/2 ", "-1/2", "1 -1/2", "2147483648/1", "1,000/3", "1\t/2", "192.168.1.1", "1.5", "abc", "bad",
		"Order #12345 for John O'Brien-Smith", "the QUICK brown FOX jumps over the LAZY dog's back - NASA report, vol. II", "HELLO", "Hello", "HeLLo WORLD", "A", "a1B2", "---",
		"ab\r\ncd", "ab\n\rcd", "ab\r\r\ncd", "\r\n\r\n", "\r", "\n",
		PsShared.LongSentence, PsShared.LongSentence + PsShared.LongSentence, new string(' ', 300) + "x" + new string('\t', 300), new string('a', 300) + "  " + new string('Z', 300),
	];

	public static void Run()
	{
		VerifyCharacterAssumptions();
		VerifyTrimFull();
		VerifyNormalizeWhiteSpace();
		VerifyStrEqAndMakeNullNull();
		VerifyGetHash();
		VerifyObjectStringsCache();
		VerifyToTitleCase();
		VerifyFormatPhoneNumber();
		VerifyGetOnlyLetters();
		VerifyTryFractionToDecimal();
		VerifyReplaceInverse();
		VerifyCountChars();
		VerifySanitizeForLog();
		VerifyTimespanToShortForm();
	}

	private static (T? Value, Type? Error) Capture<T>(Func<T> func)
	{
		try
		{
			return (func(), null);
		}
		catch (Exception ex)
		{
			return (default, ex.GetType());
		}
	}

	private static void Same<T>(string name, object? input, Func<T> original, Func<T> optimized)
	{
		(T? originalValue, Type? originalError) = Capture(original);
		(T? optimizedValue, Type? optimizedError) = Capture(optimized);
		if (originalError != optimizedError || !EqualityComparer<T?>.Default.Equals(originalValue, optimizedValue))
		{
			throw new InvalidOperationException($"{name} mismatch for input '{input}': original='{originalValue}' ({originalError?.Name ?? "no error"}), optimized='{optimizedValue}' ({optimizedError?.Name ?? "no error"})");
		}
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
	}

	// Per-character facts the optimized versions rely on, checked over the whole BMP on the current runtime / globalization mode
	private static void VerifyCharacterAssumptions()
	{
		for (int code = 0; code <= char.MaxValue; code++)
		{
			char c = (char)code;
			string s = c.ToString();
			Assert(PsRegexes.SingleWhiteSpaceRegex().IsMatch(s) == char.IsWhiteSpace(c), $@"Regex \s differs from char.IsWhiteSpace for U+{code:X4}");
			Assert((char.ToLowerInvariant(c) == 'x') == (c is 'x' or 'X'), $"Only x/X may lower-case to 'x' (U+{code:X4})");
			Assert(char.IsDigit(char.ToLowerInvariant(c)) == char.IsDigit(c), $"ToLowerInvariant changes digit-ness of U+{code:X4}");
			Assert(!char.IsDigit(c) || char.ToLowerInvariant(c) == c, $"ToLowerInvariant changes digit U+{code:X4}");
			Assert(s.ToUpperInvariant()[0] == char.ToUpperInvariant(c), $"string/char ToUpperInvariant differ for U+{code:X4}");
		}
	}

	private static void VerifyTrimFull()
	{
		foreach (string? s in StringCases)
		{
			Same("TrimFull", s, () => PsTrimFullBenchmarks.OriginalTrimFull(s), () => PsTrimFullBenchmarks.OptimizedTrimFull(s));
		}

		for (int code = 0; code <= char.MaxValue; code++)
		{
			string s1 = $"{(char)code}a{(char)code}{(char)code}b{(char)code}";
			string s2 = $"a {(char)code}b";
			Same("TrimFull", s1, () => PsTrimFullBenchmarks.OriginalTrimFull(s1), () => PsTrimFullBenchmarks.OptimizedTrimFull(s1));
			Same("TrimFull", s2, () => PsTrimFullBenchmarks.OriginalTrimFull(s2), () => PsTrimFullBenchmarks.OptimizedTrimFull(s2));
		}
	}

	private static void VerifyNormalizeWhiteSpace()
	{
		foreach (string? s in StringCases)
		{
			Same("NormalizeWhiteSpace", s, () => PsNormalizeWhiteSpaceBenchmarks.OriginalNormalizeWhiteSpace(s), () => PsNormalizeWhiteSpaceBenchmarks.OptimizedNormalizeWhiteSpace(s));
		}

		// Covers "switch list == char.IsWhiteSpace" for every BMP char
		for (int code = 0; code <= char.MaxValue; code++)
		{
			string s1 = $"a{(char)code}{(char)code}b";
			string s2 = $"{(char)code}a \t{(char)code}b{(char)code}";
			Same("NormalizeWhiteSpace", s1, () => PsNormalizeWhiteSpaceBenchmarks.OriginalNormalizeWhiteSpace(s1), () => PsNormalizeWhiteSpaceBenchmarks.OptimizedNormalizeWhiteSpace(s1));
			Same("NormalizeWhiteSpace", s2, () => PsNormalizeWhiteSpaceBenchmarks.OriginalNormalizeWhiteSpace(s2), () => PsNormalizeWhiteSpaceBenchmarks.OptimizedNormalizeWhiteSpace(s2));
		}
	}

	private static void VerifyStrEqAndMakeNullNull()
	{
		foreach (string? a in StringCases)
		{
			Same("MakeNullNull", a, () => PsMakeNullNullBenchmarks.OriginalMakeNullNull(a), () => PsMakeNullNullBenchmarks.OptimizedMakeNullNull(a));
			foreach (string? b in StringCases)
			{
				Same("StrEq", $"{a}|{b}", () => PsStrEqBenchmarks.OriginalStrEq(a, b), () => PsStrEqBenchmarks.OptimizedStrEq(a, b));
			}
		}

		for (int code = 0; code <= char.MaxValue; code++)
		{
			char c = (char)code;
			foreach (string s in new[] { $"nul{c}", $"{c}ULL", $"NULL{c}ull", $"n{c}ll", $"{c}", $" {c}Null{c} " })
			{
				Same("MakeNullNull", s, () => PsMakeNullNullBenchmarks.OriginalMakeNullNull(s), () => PsMakeNullNullBenchmarks.OptimizedMakeNullNull(s));
				Same("StrEq", s, () => PsStrEqBenchmarks.OriginalStrEq(s, "Null"), () => PsStrEqBenchmarks.OptimizedStrEq(s, "Null"));
			}
		}
	}

	private static void VerifyGetHash()
	{
		foreach (EHashAlgorithm algorithm in Enum.GetValues<EHashAlgorithm>())
		{
			foreach (string? s in StringCases)
			{
				Same("GetHash.Shared", s, () => PsGetHashBenchmarks.OriginalGetHash(s!, algorithm), () => PsGetHashBenchmarks.SharedGetHash(s!, algorithm));
				Same("GetHash.Net11", s, () => PsGetHashBenchmarks.OriginalGetHash(s!, algorithm), () => PsGetHashBenchmarks.Net11GetHash(s!, algorithm));
			}

			foreach (int length in new[] { 340, 341, 342, 1000 })
			{
				string s = new('é', length);
				Same("GetHash.Net11", length, () => PsGetHashBenchmarks.OriginalGetHash(s, algorithm), () => PsGetHashBenchmarks.Net11GetHash(s, algorithm));
			}
		}
	}

	private static void VerifyObjectStringsCache()
	{
		PsObjectStringsCacheBenchmarks.Target target = new();
		foreach (bool recursive in new[] { false, true })
		{
			Assert(PsObjectStringsCacheBenchmarks.OriginalTrimObjectStrings<PsObjectStringsCacheBenchmarks.Target>(null, recursive) == null, "Cache: null must stay null");
			Assert(PsObjectStringsCacheBenchmarks.StaticLambdaTrimObjectStrings<PsObjectStringsCacheBenchmarks.Target>(null, recursive) == null, "Cache: null must stay null");
			Assert(PsObjectStringsCacheBenchmarks.StaticGenericCacheTrimObjectStrings<PsObjectStringsCacheBenchmarks.Target>(null, recursive) == null, "Cache: null must stay null");
			Assert(ReferenceEquals(PsObjectStringsCacheBenchmarks.OriginalTrimObjectStrings(target, recursive), target), "Cache: must return the same instance");
			Assert(ReferenceEquals(PsObjectStringsCacheBenchmarks.StaticLambdaTrimObjectStrings(target, recursive), target), "Cache: must return the same instance");
			Assert(ReferenceEquals(PsObjectStringsCacheBenchmarks.StaticGenericCacheTrimObjectStrings(target, recursive), target), "Cache: must return the same instance");
		}
	}

	private static void VerifyToTitleCase()
	{
		TitleCaseUppercaseWordHandling[] handlings = [TitleCaseUppercaseWordHandling.IgnoreUppercase, TitleCaseUppercaseWordHandling.ConvertAllUppercase, TitleCaseUppercaseWordHandling.ConvertByLength];
		string?[] cultures = ["en-US", "tr-TR", "", "de-DE", null, "not-a-real-culture-zzzz"];
		foreach (string? culture in cultures)
		{
			foreach (TitleCaseUppercaseWordHandling handling in handlings)
			{
				foreach (string? s in StringCases)
				{
					Same($"ToTitleCase[{culture},{handling}]", s, () => PsToTitleCaseBenchmarks.OriginalToTitleCase(s, culture!, handling, 3), () => PsToTitleCaseBenchmarks.OptimizedToTitleCase(s, culture!, handling, 3));
				}
			}
		}

		CultureInfo previous = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
			foreach (string? s in StringCases)
			{
				Same("ToTitleCase[current tr-TR]", s, () => PsToTitleCaseBenchmarks.OriginalToTitleCase(s, "en-US", TitleCaseUppercaseWordHandling.ConvertAllUppercase), () => PsToTitleCaseBenchmarks.OptimizedToTitleCase(s, "en-US", TitleCaseUppercaseWordHandling.ConvertAllUppercase));
			}
		}
		finally
		{
			CultureInfo.CurrentCulture = previous;
		}
	}

	private static void VerifyFormatPhoneNumber()
	{
		string?[] separators = ["-", "", ".", " ", null, "$", "1", "$1", "--", "a1"];
		foreach (string? separator in separators)
		{
			foreach (bool paren in new[] { false, true })
			{
				foreach (string? s in StringCases)
				{
					Same($"FormatPhoneNumber[{separator},{paren}]", s, () => PsFormatPhoneNumberBenchmarks.OriginalFormatPhoneNumber(s, separator!, paren), () => PsFormatPhoneNumberBenchmarks.OptimizedFormatPhoneNumber(s, separator!, paren));
				}
			}
		}

		for (int code = 0; code <= char.MaxValue; code++)
		{
			char c = (char)code;
			string s1 = $"555{c}123{c}4567";
			string s2 = $"5551234567{c}12{c}3";
			Same("FormatPhoneNumber", s1, () => PsFormatPhoneNumberBenchmarks.OriginalFormatPhoneNumber(s1), () => PsFormatPhoneNumberBenchmarks.OptimizedFormatPhoneNumber(s1));
			Same("FormatPhoneNumber", s2, () => PsFormatPhoneNumberBenchmarks.OriginalFormatPhoneNumber(s2), () => PsFormatPhoneNumberBenchmarks.OptimizedFormatPhoneNumber(s2));
		}
	}

	private static void VerifyGetOnlyLetters()
	{
		foreach (string? s in StringCases)
		{
			Same("GetOnlyLetters", s, () => PsGetOnlyLettersBenchmarks.OriginalGetOnlyLetters(s), () => PsGetOnlyLettersBenchmarks.OptimizedGetOnlyLetters(s));
		}

		for (int code = 0; code <= char.MaxValue; code++)
		{
			string s = $" {(char)code}a{(char)code}1 ";
			Same("GetOnlyLetters", s, () => PsGetOnlyLettersBenchmarks.OriginalGetOnlyLetters(s), () => PsGetOnlyLettersBenchmarks.OptimizedGetOnlyLetters(s));
		}
	}

	private static void VerifyTryFractionToDecimal()
	{
		foreach (string? s in StringCases)
		{
			Same("TryFractionToDecimal", s,
				() => (PsTryFractionToDecimalBenchmarks.OriginalTryFractionToDecimal(s, out decimal? r), r),
				() => (PsTryFractionToDecimalBenchmarks.OptimizedTryFractionToDecimal(s, out decimal? r), r));
		}
	}

	private static void VerifyReplaceInverse()
	{
		string[] patterns = [@"\d+", "[a-z]+", @"\s", "(?=x)"];
		string?[] replacements = ["*", "", null, "--"];
		RegexOptions[] options = [RegexOptions.None, RegexOptions.IgnoreCase, RegexOptions.RightToLeft];
		foreach (string pattern in patterns)
		{
			foreach (RegexOptions option in options)
			{
				foreach (string? replacement in replacements)
				{
					foreach (bool firstOnly in new[] { false, true })
					{
						foreach (string? s in StringCases)
						{
							Same("ReplaceInverse.Shared", s, () => PsReplaceInverseBenchmarks.OriginalReplaceInverse(s, pattern, replacement, firstOnly, option), () => PsReplaceInverseBenchmarks.SharedReplaceInverse(s, pattern, replacement, firstOnly, option));
							Same("ReplaceInverse.Net11", s, () => PsReplaceInverseBenchmarks.OriginalReplaceInverse(s, pattern, replacement, firstOnly, option), () => PsReplaceInverseBenchmarks.Net11ReplaceInverse(s, pattern, replacement, firstOnly, option));
						}
					}
				}
			}
		}

		// Invalid pattern / timeout must fail identically; null or empty input short-circuits before the pattern is parsed
		Same("ReplaceInverse.InvalidPattern", "(", () => PsReplaceInverseBenchmarks.OriginalReplaceInverse("abc", "("), () => PsReplaceInverseBenchmarks.SharedReplaceInverse("abc", "("));
		Same("ReplaceInverse.NullPattern", null, () => PsReplaceInverseBenchmarks.OriginalReplaceInverse("abc", null!), () => PsReplaceInverseBenchmarks.SharedReplaceInverse("abc", null!));
		Same("ReplaceInverse.NullInputInvalidPattern", null, () => PsReplaceInverseBenchmarks.OriginalReplaceInverse((string?)null, "("), () => PsReplaceInverseBenchmarks.SharedReplaceInverse((string?)null, "("));
		Same("ReplaceInverse.BadTimeout", null, () => PsReplaceInverseBenchmarks.OriginalReplaceInverse("abc", "a", matchTimeout: TimeSpan.FromSeconds(-5)), () => PsReplaceInverseBenchmarks.SharedReplaceInverse("abc", "a", matchTimeout: TimeSpan.FromSeconds(-5)));
	}

	private static void VerifyCountChars()
	{
		foreach (string? s in StringCases)
		{
			foreach (char c in new[] { ' ', 'a', ',', '\t', 'x', '\uD800' })
			{
				Same("CountChars.Shared", s, () => PsCountCharsBenchmarks.OriginalCountChars(s, c), () => PsCountCharsBenchmarks.SharedCountChars(s, c));
				Same("CountChars.Net11", s, () => PsCountCharsBenchmarks.OriginalCountChars(s, c), () => PsCountCharsBenchmarks.Net11CountChars(s, c));
			}
		}
	}

	private static void VerifySanitizeForLog()
	{
		foreach (string? s in StringCases)
		{
			Same("SanitizeForLog", s, () => PsSanitizeForLogBenchmarks.OriginalSanitizeForLog(s!), () => PsSanitizeForLogBenchmarks.OptimizedSanitizeForLog(s!));
		}

		string large = string.Concat(System.Linq.Enumerable.Repeat("line\r\n\r\r\n\n", 100));
		Same("SanitizeForLog", "large", () => PsSanitizeForLogBenchmarks.OriginalSanitizeForLog(large), () => PsSanitizeForLogBenchmarks.OptimizedSanitizeForLog(large));
	}

	private static void VerifyTimespanToShortForm()
	{
		TimeSpan[] values =
		[
			TimeSpan.Zero, TimeSpan.FromTicks(1), TimeSpan.FromTicks(500), TimeSpan.FromTicks(10_000), TimeSpan.FromTicks(12_345_670), TimeSpan.FromTicks(654_320_000),
			new TimeSpan(0, 5, 30), new TimeSpan(10, 0, 0), new TimeSpan(1, 0, 5, 30), new TimeSpan(2, 3, 4, 5, 678), new TimeSpan(10, 0, 0, 0), new TimeSpan(1, 2, 3, 4).Add(TimeSpan.FromTicks(5)),
			new TimeSpan(25, 0, 0), new TimeSpan(0, 0, -1), TimeSpan.FromTicks(-15_000_000), new TimeSpan(-1, -2, -3, -4), new TimeSpan(-1, -2, -3, -4, -5), TimeSpan.MaxValue, TimeSpan.MinValue,
			new TimeSpan(0, 0, 0, 0, 999), new TimeSpan(0, 9, 59, 59, 1), new TimeSpan(100, 0, 0, 0, 1),
		];

		foreach (TimeSpan value in values)
		{
			Same("TimespanToShortForm", value, () => PsTimespanToShortFormBenchmarks.OriginalTimespanToShortForm(value), () => PsTimespanToShortFormBenchmarks.OptimizedTimespanToShortForm(value));
		}
	}
}
