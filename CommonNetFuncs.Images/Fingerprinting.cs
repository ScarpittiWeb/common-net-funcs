using SkiaSharp;

namespace CommonNetFuncs.Images;

/// <summary>
/// Supported perceptual hashing algorithms.
/// </summary>
public enum ImageHashAlgorithm
{
	/// <summary>Fast, good for exact/near-exact duplicates.</summary>
	AverageHash,
	/// <summary>Best general-purpose balance. Recommended default.</summary>
	DifferenceHash,
	/// <summary>DCT-based. Most robust against edits/compression.</summary>
	PerceptualHash
}

/// <summary>
/// A computed image fingerprint.
/// </summary>
public record ImageFingerprint(string FilePath, ulong Hash, ImageHashAlgorithm Algorithm);

/// <summary>
/// Result of a pairwise image comparison.
/// </summary>
public record SimilarityResult(
		string ImageA,
		string ImageB,
		double SimilarityPercent,
		bool IsDuplicate
);

/// <summary>
/// Perceptual image fingerprinting service using SkiaSharp only.
/// No SixLabors.ImageSharp or any other paid/non-free dependency required.
///
/// Implements three classic algorithms:
///   - AverageHash  : resize to 8x8, compare each pixel to mean luminance
///   - DifferenceHash: resize to 9x8, compare adjacent pixels per row
///   - PerceptualHash: resize to 32x32, apply DCT, compare to median
/// </summary>
public static class ImageFingerprinting
{
	private const int DctSize = 32; // full PerceptualHash DCT grid
	private const int DctHashSize = 8; // top-left DCT sub-grid used for the PerceptualHash

	// Cosine factors and scales for the DCT coefficients used by PerceptualHash (computed once instead of per Math.Cos call)
	private static readonly double[][] DctCosTable = BuildDctCosTable();
	private static readonly double[] DctScale = BuildDctScale();

	// ── Public API ────────────────────────────────────────────────────────────

	/// <summary>Fingerprint a single image file.</summary>
	public static async Task<ImageFingerprint> FingerprintImage(this string imagePath, ImageHashAlgorithm algorithm = ImageHashAlgorithm.DifferenceHash)
	{
		if (!File.Exists(imagePath))
		{
			throw new FileNotFoundException($"Image not found: {imagePath}");
		}

		await using FileStream stream = File.OpenRead(imagePath);
		return FingerprintImage(stream, imagePath, algorithm);
	}

	/// <summary>Fingerprint an image from a stream.</summary>
	public static ImageFingerprint FingerprintImage(this Stream stream, string label = "<stream>", ImageHashAlgorithm algorithm = ImageHashAlgorithm.DifferenceHash)
	{
		using SKBitmap bitmap = SKBitmap.Decode(stream) ?? throw new InvalidDataException($"Could not decode image: {label}");
		return bitmap.FingerprintImage(label, algorithm);
	}

	/// <summary>Fingerprint an image from a span of bytes.</summary>
	public static ImageFingerprint FingerprintImage(this ReadOnlySpan<byte> stream, string label = "<stream>", ImageHashAlgorithm algorithm = ImageHashAlgorithm.DifferenceHash)
	{
		using SKBitmap bitmap = SKBitmap.Decode(stream) ?? throw new InvalidDataException($"Could not decode image: {label}");
		return bitmap.FingerprintImage(label, algorithm);
	}

	/// <summary>Fingerprint an image from a bitmap.</summary>
	public static ImageFingerprint FingerprintImage(this SKBitmap bitmap, string label = "<bitmap>", ImageHashAlgorithm algorithm = ImageHashAlgorithm.DifferenceHash)
	{
		if (bitmap == null || bitmap.IsEmpty)
		{
			throw new ArgumentNullException(nameof(bitmap), "Bitmap cannot be null or empty.");
		}

		ulong hash = algorithm switch
		{
			ImageHashAlgorithm.AverageHash => ComputeAverageHash(bitmap),
			ImageHashAlgorithm.DifferenceHash => ComputeDifferenceHash(bitmap),
			ImageHashAlgorithm.PerceptualHash => ComputePerceptualHash(bitmap),
			_ => throw new ArgumentOutOfRangeException(nameof(algorithm), message: "Invalid hashing algorithm specified.")
		};

		return new ImageFingerprint(label, hash, algorithm);
	}

	/// <summary>Fingerprint all images in a directory.</summary>
	public static async Task<IReadOnlyList<ImageFingerprint>> FingerprintDirectory(string directoryPath, bool recursive = false, ImageHashAlgorithm algorithm = ImageHashAlgorithm.DifferenceHash)
	{
		HashSet<string> supported = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp" };

		SearchOption option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

		List<string> files = Directory.GetFiles(directoryPath, "*.*", option).Where(x => supported.Contains(Path.GetExtension(x))).ToList();

		List<ImageFingerprint> results = new(files.Count);
		foreach (string file in files)
		{
			try
			{
				results.Add(await FingerprintImage(file, algorithm).ConfigureAwait(false));
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[WARN] Skipping {Path.GetFileName(file)}: {ex.Message}");
			}
		}

		Console.WriteLine($"[INFO] Fingerprinted {results.Count} images using {algorithm}.");
		return results;
	}

	/// <summary>Compare two fingerprints. Both must use the same algorithm.</summary>
	public static SimilarityResult Compare(ImageFingerprint a, ImageFingerprint b, decimal duplicateThreshold = 90.0m)
	{
		if (a.Algorithm != b.Algorithm)
		{
			throw new InvalidOperationException("Cannot compare fingerprints computed with different algorithms.");
		}

		double similarity = HammingSimilarity(a.Hash, b.Hash);
		return new SimilarityResult(a.FilePath, b.FilePath, similarity, similarity >= (double)duplicateThreshold);
	}

	/// <summary>Find all duplicate pairs in a list of fingerprints.</summary>
	public static IReadOnlyList<SimilarityResult> FindDuplicates(IReadOnlyList<ImageFingerprint> fingerprints, decimal duplicateThreshold = 90.0m)
	{
		// Same pair order, exceptions and similarity values as calling Compare per pair, but only duplicate pairs allocate a result
		List<SimilarityResult> duplicates = new();
		double threshold = (double)duplicateThreshold;
		int count = fingerprints.Count;
		for (int i = 0; i < count; i++)
		{
			ImageFingerprint a = fingerprints[i];
			for (int j = i + 1; j < count; j++)
			{
				ImageFingerprint b = fingerprints[j];
				if (a.Algorithm != b.Algorithm)
				{
					throw new InvalidOperationException("Cannot compare fingerprints computed with different algorithms.");
				}

				double similarity = HammingSimilarity(a.Hash, b.Hash);
				if (similarity >= threshold)
				{
					duplicates.Add(new SimilarityResult(a.FilePath, b.FilePath, similarity, true));
				}
			}
		}
		return duplicates.OrderByDescending(r => r.SimilarityPercent).ToList();
	}

	/// <summary>Convenience: scan a directory and return all duplicate pairs.</summary>
	public static async Task<IReadOnlyList<SimilarityResult>> FindDuplicatesInDirectory(string directoryPath, bool recursive = false, ImageHashAlgorithm algorithm = ImageHashAlgorithm.DifferenceHash)
	{
		IReadOnlyList<ImageFingerprint> fingerprints = await FingerprintDirectory(directoryPath, recursive, algorithm).ConfigureAwait(false);
		return FindDuplicates(fingerprints);
	}

	// ── Hash Algorithms ───────────────────────────────────────────────────────

	/// <summary>
	/// AverageHash: resize to 8x8 grayscale, compare each pixel to the mean.
	/// Produces a 64-bit hash. Fast but sensitive to gamma/brightness shifts.
	/// </summary>
	private static ulong ComputeAverageHash(SKBitmap source)
	{
		// Step 1: resize to 8x8 with high-quality downsampling
		using SKBitmap resizedBitmap = source.ResizeTo(8, 8);
		SKColor[] colors = resizedBitmap.Pixels; // Row-major, one native call (same SkBitmap::getColor values as GetPixel)

		// Step 2: extract grayscale luminance for all 64 pixels
		Span<byte> pixels = stackalloc byte[64];
		int sum = 0;
		for (int i = 0; i < 64; i++)
		{
			byte luminance = Luminance(colors[i]);
			pixels[i] = luminance;
			sum += luminance;
		}

		// Step 3: compute mean luminance (identical to averaging the exact small integers as doubles)
		double mean = (double)sum / 64;

		// Step 4: set bit=1 if pixel >= mean, else 0
		ulong hash = 0;
		for (int i = 0; i < 64; i++)
		{
			if (pixels[i] >= mean)
			{
				hash |= 1UL << i;
			}
		}

		return hash;
	}

	/// <summary>
	/// DifferenceHash: resize to 9x8 grayscale, compare adjacent pixels in each row.
	/// 9 columns → 8 comparisons per row × 8 rows = 64 bits.
	/// More robust than AverageHash for brightness variations.
	/// </summary>
	private static ulong ComputeDifferenceHash(SKBitmap source)
	{
		// Step 1: resize to 9 wide x 8 tall (one extra column for diff)
		using SKBitmap resizedBitmap = source.ResizeTo(9, 8);
		SKColor[] colors = resizedBitmap.Pixels; // Row-major, one native call (same SkBitmap::getColor values as GetPixel)

		ulong hash = 0;
		int bit = 0;

		// Step 2: for each row, compare adjacent pixel pairs
		for (int y = 0; y < 8; y++)
		{
			int rowStart = y * 9;
			byte left = Luminance(colors[rowStart]);
			for (int x = 0; x < 8; x++)
			{
				byte right = Luminance(colors[rowStart + x + 1]);

				// bit = 1 if left pixel is brighter than right
				if (left > right)
				{
					hash |= 1UL << bit;
				}
				bit++;
				left = right;
			}
		}

		return hash;
	}

	/// <summary>
	/// PerceptualHash (pHash): resize to 32x32, compute 2D DCT, take top-left
	/// 8x8 of DCT coefficients, compare each to the median.
	/// Most robust against JPEG compression, minor edits, watermarks.
	/// </summary>
	private static ulong ComputePerceptualHash(SKBitmap source)
	{
		const int dctSize = DctSize; // full DCT grid
		const int hashSize = DctHashSize;  // top-left sub-grid used for hash

		// Step 1: resize to 32x32 grayscale
		using SKBitmap resizedBitmap = source.ResizeTo(dctSize, dctSize);

		// Step 2: build luminance matrix
		double[,] luminanceMatrix = new double[dctSize, dctSize];
		for (int y = 0; y < dctSize; y++)
		{
			for (int x = 0; x < dctSize; x++)
			{
				luminanceMatrix[y, x] = Luminance(resizedBitmap.GetPixel(x, y));
			}
		}

		// Step 3: apply the 2D DCT (only the coefficients used by the hash are computed)
		// Step 4: extract top-left 8x8 DCT coefficients
		Span<double> topLeft = stackalloc double[hashSize * hashSize];
		ComputeDct2DTopLeft(luminanceMatrix, topLeft);

		// Step 5: compute median of the 64 values (ascending sort; ties can only be +0.0/-0.0 or equal values, which cannot change the median
		// or the >= comparisons)
		double[] sorted = new double[hashSize * hashSize];
		topLeft.CopyTo(sorted);
		Array.Sort(sorted);
		double median = (sorted[31] + sorted[32]) / 2.0;

		// Step 6: set bit=1 if coefficient >= median
		ulong hash = 0;
		for (int i = 0; i < 64; i++)
		{
			if (topLeft[i] >= median)
			{
				hash |= 1UL << i;
			}
		}

		return hash;
	}

	// ── Helpers ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Compute perceptual luminance (grayscale value) from an SKColor.
	/// Uses the standard Rec. 601 luma coefficients.
	/// </summary>
	private static byte Luminance(SKColor color) => (byte)(0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue);

	/// <summary>
	/// Compute the top-left <see cref="DctHashSize"/> x <see cref="DctHashSize"/> coefficients of the separable 2D Discrete Cosine Transform (DCT-II)
	/// of a <see cref="DctSize"/> x <see cref="DctSize"/> matrix. Used by PerceptualHash.
	/// </summary>
	/// <remarks>
	/// Every coefficient is accumulated in the same order (i = 0..n-1) with the same cosine factors and scale as the full row/column 1D DCT-II,
	/// so results are bit-identical to computing the full transform and taking its top-left block.
	/// </remarks>
	private static void ComputeDct2DTopLeft(double[,] input, Span<double> topLeft)
	{
		const int n = DctSize;

		// Row pass: rowPass[y * DctHashSize + k] == 1D DCT of row y, coefficient k (only k < DctHashSize is needed)
		Span<double> rowPass = stackalloc double[n * DctHashSize];
		for (int y = 0; y < n; y++)
		{
			for (int k = 0; k < DctHashSize; k++)
			{
				double[] cos = DctCosTable[k];
				double sum = 0;
				for (int i = 0; i < n; i++)
				{
					sum += input[y, i] * cos[i];
				}
				rowPass[(y * DctHashSize) + k] = sum * DctScale[k];
			}
		}

		// Column pass: only output rows 0..DctHashSize-1 of columns 0..DctHashSize-1
		for (int y = 0; y < DctHashSize; y++)
		{
			double[] cos = DctCosTable[y];
			for (int x = 0; x < DctHashSize; x++)
			{
				double sum = 0;
				for (int i = 0; i < n; i++)
				{
					sum += rowPass[(i * DctHashSize) + x] * cos[i];
				}
				topLeft[(y * DctHashSize) + x] = sum * DctScale[y];
			}
		}
	}

	// Built with the exact same expressions/operand types as the 1D DCT-II so every factor is bit-identical
	private static double[][] BuildDctCosTable()
	{
		int n = DctSize;
		double[][] table = new double[DctHashSize][];
		for (int k = 0; k < DctHashSize; k++)
		{
			double[] row = new double[n];
			for (int i = 0; i < n; i++)
			{
				row[i] = Math.Cos(Math.PI * k * (2 * i + 1) / (2.0 * n));
			}
			table[k] = row;
		}
		return table;
	}

	private static double[] BuildDctScale()
	{
		int n = DctSize;
		double[] scale = new double[DctHashSize];
		for (int k = 0; k < DctHashSize; k++)
		{
			scale[k] = k == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n);
		}
		return scale;
	}

	/// <summary>
	/// Compute similarity between two 64-bit hashes using Hamming distance.
	/// Returns a value between 0.0 (completely different) and 100.0 (identical).
	/// </summary>
	public static double HammingSimilarity(ulong hashA, ulong hashB)
	{
		return (1.0 - PopCount(hashA ^ hashB) / 64.0) * 100.0;
	}

	private static int PopCount(ulong value)
	{
#if NET
		return System.Numerics.BitOperations.PopCount(value);
#else
		value -= (value >> 1) & 0x5555555555555555UL;
		value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
		value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
		return (int)((value * 0x0101010101010101UL) >> 56);
#endif
	}
}