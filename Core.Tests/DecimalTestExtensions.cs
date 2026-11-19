using System.Globalization;

namespace Core.Tests;

// .NET 11 made (decimal)double exact (2.71 -> 2.7099999999999999644728632120m); tests want the shortest round-trip decimal instead.
internal static class DecimalTestExtensions
{
	public static decimal ToShortestDecimal(this double value) =>
		decimal.Parse(value.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);

	public static decimal? ToShortestDecimal(this double? value) => value.HasValue ? value.Value.ToShortestDecimal() : null;
}
