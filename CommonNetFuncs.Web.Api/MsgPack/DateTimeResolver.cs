using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CommonNetFuncs.Web.Api.MsgPack;

/// <summary>
/// Normalizes DateTime values with DateTimeKind.Unspecified to UTC before JSON serialization
/// so the output always has a trailing "Z", matching the MessagePack formatter behavior.
/// EF Core reads PostgreSQL timestamps as Unspecified even though they are stored as UTC.
/// </summary>
public sealed class DateTimeUtcJsonConverter : JsonConverter<DateTime>
{
	public static readonly DateTimeUtcJsonConverter Instance = new();

	public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		=> DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

	public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
	{
		if (value.Kind == DateTimeKind.Unspecified)
		{
			value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
		}
		writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));
	}
}

/// <summary>
/// Serializes DateTime as an ISO 8601 string instead of the MessagePack Timestamp
/// extension type (ext -1 / tag 255). This matches how System.Text.Json encodes dates
/// and what the TypeScript interfaces expect (string).
/// </summary>
public sealed class DateTimeAsStringFormatter : IMessagePackFormatter<DateTime>
{
	public static readonly DateTimeAsStringFormatter Instance = new();

	public void Serialize(ref MessagePackWriter writer, DateTime value, MessagePackSerializerOptions options)
	{
		// EF Core returns UTC values from the DB with DateTimeKind.Unspecified, which causes
		// the "O" format to omit the trailing "Z". JavaScript's new Date() then treats the
		// string as local time instead of UTC, breaking toLocaleString(). Normalize to Utc
		// so the "Z" suffix is always emitted.
		if (value.Kind == DateTimeKind.Unspecified)
		{
			value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
		}
		writer.Write(value.ToString("O")); // ISO 8601 round-trip format, e.g. 2026-04-14T12:00:00.0000000Z
	}

	public DateTime Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
	{
		return DateTime.Parse(reader.ReadString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
	}
}

/// <summary>
/// Serializes TimeSpan as a string in the constant ("c") format, e.g. "01:30:00" for 1 hour 30 minutes.
/// This is more human-readable and interoperable than the default MessagePack array format.
/// </summary>
public sealed class TimeSpanAsStringFormatter : IMessagePackFormatter<TimeSpan>
{
	public static readonly TimeSpanAsStringFormatter Instance = new();

	public void Serialize(ref MessagePackWriter writer, TimeSpan value, MessagePackSerializerOptions options)
	{
		writer.Write(value.ToString("c", CultureInfo.InvariantCulture)); // Constant ("c") format, e.g. "01:30:00" for 1 hour 30 minutes
	}

	public TimeSpan Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
	{
		return TimeSpan.Parse(reader.ReadString()!, CultureInfo.InvariantCulture);
	}
}

/// <summary>
/// Serializes <see cref="DateTimeOffset"/> as an ISO 8601 string in MessagePack.
/// This ensures that <see cref="MessagePackSerializer.ConvertToJson"/> (used in
/// MsgPackRequestMiddleware) produces a JSON string instead of a 2-element array,
/// which System.Text.Json can then deserialize as <see cref="DateTimeOffset"/>.
/// </summary>
public sealed class DateTimeOffsetAsStringFormatter : IMessagePackFormatter<DateTimeOffset>
{
	public static readonly DateTimeOffsetAsStringFormatter Instance = new();

	public void Serialize(ref MessagePackWriter writer, DateTimeOffset value, MessagePackSerializerOptions options)
		=> writer.Write(value.ToString("O", CultureInfo.InvariantCulture));

	public DateTimeOffset Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
		=> DateTimeOffset.Parse(reader.ReadString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed class DateOnlyAsStringFormatter : IMessagePackFormatter<DateOnly>
{
	public static readonly DateOnlyAsStringFormatter Instance = new();
	private DateOnlyAsStringFormatter() { }

	public void Serialize(ref MessagePackWriter writer, DateOnly value, MessagePackSerializerOptions options)
	{
		writer.Write(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
	}

	// Longest realistic input is a JS Date.toString() value (~80 chars); anything longer is rejected up front.
	private const int MaxInputLength = 128;

	// "M"/"d" accept 1-2 digits and "yyyy"/"yy" require exactly 4/2 in ParseExact, so these cover zero-padded and unpadded variants.
	// Ambiguous numeric dates resolve month-first for '-' and '/', and day-first for '.' (the conventional usage of each separator).
	private static readonly string[] DashFormats = ["yyyy-M-d", "M-d-yyyy", "d-M-yyyy", "M-d-yy", "d-M-yy"];
	private static readonly string[] SlashFormats = ["yyyy/M/d", "M/d/yyyy", "d/M/yyyy", "M/d/yy", "d/M/yy"];
	private static readonly string[] DotFormats = ["yyyy.M.d", "d.M.yyyy", "M.d.yyyy", "d.M.yy", "M.d.yy"];
	private static readonly string[] CompactFormats8 = ["yyyyMMdd", "MMddyyyy", "ddMMyyyy"];
	private static readonly string[] CompactFormats6 = ["yyMMdd", "MMddyy", "ddMMyy"];

	private static readonly SearchValues<char> DashChars = SearchValues.Create("0123456789-");
	private static readonly SearchValues<char> SlashChars = SearchValues.Create("0123456789/");
	private static readonly SearchValues<char> DotChars = SearchValues.Create("0123456789.");

	/// <summary>
	/// Accepts:
	/// <list type="bullet">
	/// <item>The integer <see cref="DateOnly.DayNumber"/> encoding written by the standard MessagePack <see cref="DateOnlyFormatter"/>.</item>
	/// <item>The MessagePack Timestamp extension (ext -1), e.g. a JS <c>Date</c> encoded by @msgpack/msgpack (UTC date is used).</item>
	/// <item>ISO 8601 calendar dates / date-times (e.g. JS <c>Date.toISOString()</c>); the literal date part is kept as-is, regardless of any offset.</item>
	/// <item>ISO 8601 week dates (yyyy-Www-d, yyyyWwwd, yyyy-Www, yyyyWww) and ordinal dates (yyyy-ddd, yyyyddd).</item>
	/// <item>Numeric dates separated by '-', '/' or '.', with 1-2 digit month/day and 2 or 4 digit years, plus compact yyyyMMdd / yyMMdd variants.</item>
	/// <item>Anything else <see cref="DateTimeOffset.TryParse(ReadOnlySpan{char}, IFormatProvider?, DateTimeStyles, out DateTimeOffset)"/> understands
	/// with the invariant culture (month names, RFC 1123 / JS <c>Date.toUTCString()</c>, JS <c>Date.toString()</c>, etc.).</item>
	/// </list>
	/// </summary>
	public DateOnly Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
	{
		switch (reader.NextMessagePackType)
		{
			case MessagePackType.Integer:
				return DateOnly.FromDayNumber(reader.ReadInt32());
			case MessagePackType.Extension:
				return DateOnly.FromDateTime(reader.ReadDateTime());
		}

		if (!reader.TryReadStringSpan(out ReadOnlySpan<byte> utf8))
		{
			// Nil or a string split across buffer segments (rare)
			string value = reader.ReadString() ?? throw new MessagePackSerializationException("Cannot deserialize nil as DateOnly.");
			return value.Length <= MaxInputLength ? ParseChars(value) : throw CreateFormatException();
		}

		if (TryParseIsoDatePrefix(utf8, out DateOnly result))
		{
			return result;
		}

		if (utf8.Length > MaxInputLength)
		{
			throw CreateFormatException();
		}

		Span<char> chars = stackalloc char[MaxInputLength];
		int written = Encoding.UTF8.GetChars(utf8, chars);
		return ParseChars(chars[..written]);
	}

	private static DateOnly ParseChars(ReadOnlySpan<char> value)
	{
		value = value.Trim();

		if (TryParseIsoDatePrefix(value, out DateOnly result) || TryParseNumeric(value, out result) || TryParseIsoWeekOrOrdinal(value, out result))
		{
			return result;
		}

		// .NET can't parse JS Date.toString()'s " GMT-0500 (Eastern Standard Time)" suffix, and only the literal date is needed anyway
		int zoneIndex = value.IndexOf(" GMT", StringComparison.OrdinalIgnoreCase);
		if (zoneIndex > 0)
		{
			value = value[..zoneIndex];
		}

		// DateTimeOffset keeps the literal clock time (no conversion to server local time), so the date part is preserved.
		if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out DateTimeOffset dateTimeOffset))
		{
			return DateOnly.FromDateTime(dateTimeOffset.DateTime);
		}

		throw CreateFormatException();
	}

	/// <summary>yyyy-MM-dd, optionally followed by a time component starting with 'T' or a space.</summary>
	private static bool TryParseIsoDatePrefix<T>(ReadOnlySpan<T> value, out DateOnly result) where T : unmanaged, IBinaryInteger<T>
	{
		if (value.Length >= 10 && IsChar(value[4], '-') && IsChar(value[7], '-')
			&& (value.Length == 10 || IsChar(value[10], 'T') || IsChar(value[10], 't') || IsChar(value[10], ' '))
			&& TryParseDigits(value[..4], out int year) && TryParseDigits(value.Slice(5, 2), out int month) && TryParseDigits(value.Slice(8, 2), out int day))
		{
			return TryCreateDate(year, month, day, out result);
		}

		result = default;
		return false;
	}

	private static bool TryParseNumeric(ReadOnlySpan<char> value, out DateOnly result)
	{
		int separatorIndex = value.IndexOfAnyExceptInRange('0', '9');
		string[]? formats;
		if (separatorIndex < 0)
		{
			formats = value.Length switch
			{
				8 => CompactFormats8,
				6 => CompactFormats6,
				_ => null,
			};
		}
		else
		{
			(formats, SearchValues<char>? allowed) = value[separatorIndex] switch
			{
				'-' => (DashFormats, DashChars),
				'/' => (SlashFormats, SlashChars),
				'.' => (DotFormats, DotChars),
				_ => (null, null),
			};

			// Leave anything with letters, times, offsets or mixed separators to the more lenient parsers
			if (allowed is not null && value.ContainsAnyExcept(allowed))
			{
				formats = null;
			}
		}

		if (formats is not null)
		{
			return DateOnly.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
		}

		result = default;
		return false;
	}

	/// <summary>ISO 8601 week dates (yyyy-Www-d, yyyyWwwd, yyyy-Www, yyyyWww) and ordinal dates (yyyy-ddd, yyyyddd).</summary>
	private static bool TryParseIsoWeekOrOrdinal(ReadOnlySpan<char> value, out DateOnly result)
	{
		result = default;
		if (value.Length is < 7 or > 10 || !TryParseDigits(value[..4], out int year) || year is < 1 or > 9999)
		{
			return false;
		}

		ReadOnlySpan<char> rest = value[4..];
		bool extended = rest[0] == '-';
		if (extended)
		{
			rest = rest[1..];
		}

		if (rest[0] is 'W' or 'w')
		{
			int dayOfWeek = 1;
			bool valid = rest.Length switch
			{
				3 => true,
				4 => !extended && TryParseDigits(rest[3..], out dayOfWeek),
				5 => extended && rest[3] == '-' && TryParseDigits(rest[4..], out dayOfWeek),
				_ => false,
			};

			if (!valid || !TryParseDigits(rest.Slice(1, 2), out int week) || week < 1 || week > ISOWeek.GetWeeksInYear(year) || dayOfWeek is < 1 or > 7)
			{
				return false;
			}

			// The first/last ISO weeks of year 1/9999 can spill outside the DateOnly range
			DateTime start = ISOWeek.GetYearStart(year);
			long dayNumber = (start.Ticks / TimeSpan.TicksPerDay) + ((week - 1) * 7L) + (dayOfWeek - 1);
			if (dayNumber < DateOnly.MinValue.DayNumber || dayNumber > DateOnly.MaxValue.DayNumber)
			{
				return false;
			}

			result = DateOnly.FromDayNumber((int)dayNumber);
			return true;
		}

		if (rest.Length == 3 && TryParseDigits(rest, out int dayOfYear) && dayOfYear >= 1 && dayOfYear <= (DateTime.IsLeapYear(year) ? 366 : 365))
		{
			result = DateOnly.FromDayNumber(new DateOnly(year, 1, 1).DayNumber + dayOfYear - 1);
			return true;
		}

		return false;
	}

	private static bool TryCreateDate(int year, int month, int day, out DateOnly result)
	{
		if (year is >= 1 and <= 9999 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
		{
			result = new DateOnly(year, month, day);
			return true;
		}

		result = default;
		return false;
	}

	private static bool TryParseDigits<T>(ReadOnlySpan<T> digits, out int value) where T : unmanaged, IBinaryInteger<T>
	{
		value = 0;
		foreach (T c in digits)
		{
			uint digit = uint.CreateTruncating(c) - '0';
			if (digit > 9)
			{
				return false;
			}
			value = (value * 10) + (int)digit;
		}
		return true;
	}

	private static bool IsChar<T>(T value, char expected) where T : unmanaged, IBinaryInteger<T> => uint.CreateTruncating(value) == expected;

	private static FormatException CreateFormatException() => new("The string was not recognized as a valid DateOnly.");
}

public sealed class DateTimeStringResolver : IFormatterResolver
{
	public static readonly DateTimeStringResolver Instance = new();

	public IMessagePackFormatter<T>? GetFormatter<T>()
	{
		// Use static generic caching to evaluate the formatter resolution only once per type.
		// This eliminates the overhead of Type.GetTypeCode() calls on every invocation.
		return CachedFormatter<T>.Value;
	}

	// Static generic class ensures the switch is evaluated only once per type at class initialization.
	// Subsequent calls simply return the cached reference, providing optimal performance for this hot path.
	private static class CachedFormatter<T>
	{
		public static readonly IMessagePackFormatter<T>? Value = GetFormatterInternal();

		private static IMessagePackFormatter<T>? GetFormatterInternal()
		{
			return Type.GetTypeCode(typeof(T)) switch
			{
				TypeCode.DateTime => (IMessagePackFormatter<T>)(object)DateTimeAsStringFormatter.Instance,
				TypeCode.Object when typeof(T) == typeof(DateTimeOffset) => (IMessagePackFormatter<T>)(object)DateTimeOffsetAsStringFormatter.Instance,
				TypeCode.Object when typeof(T) == typeof(TimeSpan) => (IMessagePackFormatter<T>)(object)TimeSpanAsStringFormatter.Instance,
				TypeCode.Object when typeof(T) == typeof(DateOnly) => (IMessagePackFormatter<T>)(object)DateOnlyAsStringFormatter.Instance,
				_ => null,
			};
		}
	}
}

public static class MsgPackSerializerConfig
{
	public static readonly MessagePackSerializerOptions DateTimesAsStrings =
		MessagePackSerializerOptions.Standard
			.WithSecurity(MessagePackSecurity.UntrustedData)
			.WithResolver(CompositeResolver.Create(DateTimeStringResolver.Instance, StandardResolver.Instance));
}