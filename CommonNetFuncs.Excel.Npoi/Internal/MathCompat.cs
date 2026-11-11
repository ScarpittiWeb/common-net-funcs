namespace CommonNetFuncs.Excel.Npoi.Internal;

/// <summary>
/// Shim for <see cref="Math.Round(double, int, MidpointRounding)"/> with <see cref="MidpointRounding.ToZero"/> (not available on netstandard2.1).
/// </summary>
internal static class MathCompat
{
	public static double Round(double value, int digits)
	{
#if !NETSTANDARD2_1
		return Math.Round(value, digits, MidpointRounding.ToZero);
#else
		// MidpointRounding.ToZero truncates toward zero for every value, not only exact midpoints
		double factor = Math.Pow(10, digits);
		return Math.Truncate(value * factor) / factor;
#endif
	}

	public static decimal Round(decimal value, int digits)
	{
#if !NETSTANDARD2_1
		return Math.Round(value, digits, MidpointRounding.ToZero);
#else
		// MidpointRounding.ToZero truncates toward zero for every value, not only exact midpoints
		decimal factor = (decimal)Math.Pow(10, digits);
		return Math.Truncate(value * factor) / factor;
#endif
	}
}
