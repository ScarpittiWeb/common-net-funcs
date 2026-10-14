using System.Diagnostics;

namespace CommonNetFuncs.Ffmpeg.Internal;

/// <summary>
/// Shims for BCL/Core members that only exist on newer TFMs, needed so this project can multi-target netstandard2.1.
/// Not compiled for TFMs where the real member already exists.
/// </summary>
#if !NET5_0_OR_GREATER
internal static class ProcessCompatExtensions
{
	public static Task WaitForExitAsync(this Process process, CancellationToken cancellationToken = default)
	{
		return Task.Run(() => process.WaitForExit(), cancellationToken);
	}
}
#endif

#if !NET7_0_OR_GREATER
internal static class EnumerableCompatExtensions
{
	public static IEnumerable<T> Order<T>(this IEnumerable<T> source)
	{
		return source.OrderBy(static x => x);
	}
}

/// <summary>
/// Non-generic replacement for <c>CommonNetFuncs.Core.UnitConversion.GetFileSizeFromBytesWithUnits</c>, which relies on
/// generic math (IBinaryInteger&lt;T&gt;) that isn't available on netstandard2.1.
/// </summary>
internal static class FileSizeCompatExtensions
{
	private static readonly string[] ByteUnits = new string[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB", "ZiB", "YiB" };

	public static string GetFileSizeFromBytesWithUnits(this long inputBytes, int decimalPlaces = 1)
	{
		long bytes = Math.Abs(inputBytes);

		if (bytes == 0)
		{
			return "0 B";
		}

		const int k = 1024;
		int dm = decimalPlaces < 0 ? 0 : decimalPlaces;

		int i = (int)Math.Floor(Math.Log(bytes) / Math.Log(k));

		// Ensure index is within bounds
		if (i >= ByteUnits.Length)
		{
			i = ByteUnits.Length - 1;
		}

		long multiplier = inputBytes < 0 ? -1L : 1L;
		decimal result = Math.Round((decimal)(multiplier * bytes) / (decimal)Math.Pow(k, i), dm, MidpointRounding.AwayFromZero);
		return $"{result} {ByteUnits[i]}";
	}

	public static string GetFileSizeFromBytesWithUnits(this long? nullBytes, int decimalPlaces = 1)
	{
		return nullBytes == null ? "-0" : nullBytes.Value.GetFileSizeFromBytesWithUnits(decimalPlaces);
	}
}
#endif
