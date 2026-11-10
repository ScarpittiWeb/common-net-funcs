using System.Globalization;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace CommonNetFuncs.Web.Api.MsgPack;

/// <summary>
/// MessagePack formatter for <see cref="decimal"/> that accepts both the standard string encoding used by C# (<see cref="MessagePack.Formatters.DecimalFormatter"/>) and the numeric (integer / float) encodings sent by JavaScript clients — msgpackr always encodes
/// JS <c>number</c> values as ints or floats, never as strings.
/// </summary>
public sealed class DecimalFormatter : IMessagePackFormatter<decimal>
{
	public static readonly DecimalFormatter Instance = new();
	private DecimalFormatter() { }

	public void Serialize(ref MessagePackWriter writer, decimal value, MessagePackSerializerOptions options)
		=> writer.Write(value.ToString(CultureInfo.InvariantCulture));

	public decimal Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
	{
		MessagePackType type = reader.NextMessagePackType;
		if (type == MessagePackType.Float)
		{
			return DoubleToDecimal(reader.ReadDouble());
		}

		if (type == MessagePackType.Integer)
		{
			return reader.ReadInt64();
		}
		return decimal.Parse(reader.ReadString()!, CultureInfo.InvariantCulture);
	}

	// .NET 11 made (decimal)double exact (2.71 -> 2.7099999999999999644728632120m); parse the shortest round-trip text instead to match what the client sent.
	internal static decimal DoubleToDecimal(double value) =>
		decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>
/// MessagePack formatter for <see cref="Nullable{T}">decimal?</see> that accepts both the
/// standard string encoding used by C# and numeric encodings sent by JavaScript clients.
/// </summary>
public sealed class NullableDecimalFormatter : IMessagePackFormatter<decimal?>
{
	public static readonly NullableDecimalFormatter Instance = new();
	private NullableDecimalFormatter() { }

	public void Serialize(ref MessagePackWriter writer, decimal? value, MessagePackSerializerOptions options)
	{
		if (value is null)
		{
			writer.WriteNil();
			return;
		}

		writer.Write(value.Value.ToString(CultureInfo.InvariantCulture));
	}

	public decimal? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
	{
		if (reader.TryReadNil()) return null;
		MessagePackType type = reader.NextMessagePackType;
		if (type == MessagePackType.Float)
		{
			return DecimalFormatter.DoubleToDecimal(reader.ReadDouble());
		}

		if (type == MessagePackType.Integer)
		{
			return reader.ReadInt64();
		}
		return decimal.Parse(reader.ReadString()!, CultureInfo.InvariantCulture);
	}
}

/// <summary>
/// Resolver that intercepts <c>decimal</c> and <c>decimal?</c> requests and returns
/// <see cref="DecimalFormatter"/> / <see cref="NullableDecimalFormatter"/>
/// instead of the standard <see cref="MessagePack.Formatters.DecimalFormatter"/> (which rejects numeric msgpack codes).
/// Register before <see cref="StandardResolver"/> in a <see cref="CompositeResolver"/>.
/// </summary>
public sealed class FlexibleDecimalResolver : IFormatterResolver
{
	public static readonly FlexibleDecimalResolver Instance = new();
	private FlexibleDecimalResolver() { }

	public IMessagePackFormatter<T>? GetFormatter<T>()
	{
		if (typeof(T) == typeof(decimal))
		{
			return (IMessagePackFormatter<T>)(object)DecimalFormatter.Instance;
		}
		if (typeof(T) == typeof(decimal?))
		{
			return (IMessagePackFormatter<T>)(object)NullableDecimalFormatter.Instance;
		}
		return null;
	}
}
