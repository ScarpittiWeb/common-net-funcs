using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using BenchmarkDotNet.Attributes;
using CommonNetFuncs.Compression;
#if PM_SKIASHARP
using SkiaSharp;
#endif
#if PM_ROSLYN
using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
#endif

namespace BenchmarkSuite;

// Performance candidates for Compression, Images, Email, ReinforcedTypings and SubsetModelBinder.Generator.
// Each class pits a verbatim copy of the current library implementation ("Original", baseline) against the proposed replacement(s).
// "Shared_" variants compile on every TFM the owning library targets; "Net11_" variants need modern-.NET-only APIs.
// Optional groups (need extra references, see PerfMiscBenchmarks notes):
//   PM_SKIASHARP -> <PackageReference Include="SkiaSharp" /> + <DefineConstants>$(DefineConstants);PM_SKIASHARP</DefineConstants>
//   PM_ROSLYN    -> <PackageReference Include="Microsoft.CodeAnalysis.CSharp" /> + <DefineConstants>$(DefineConstants);PM_ROSLYN</DefineConstants>
// PmVerify.Run() asserts Original and Optimized produce identical results (throws InvalidOperationException on mismatch).

#region Images: perceptual hash DCT

/// <summary>ImageFingerprinting.ComputePerceptualHash (after resize): full 32x32 DCT with 65,536 Math.Cos calls vs cached cosine table computing only the used 8x8 block.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmPerceptualHashDctBenchmarks
{
	private double[,] luminance = null!;

	[GlobalSetup]
	public void Setup()
	{
		luminance = PmPerceptualHash.CreateLuminance(42, smooth: true);
	}

	[Benchmark(Baseline = true)]
	public ulong Original()
	{
		return PmPerceptualHash.OriginalHash(luminance);
	}

	[Benchmark]
	public ulong Shared_CosTableTopLeftOnly()
	{
		return PmPerceptualHash.OptimizedHash(luminance);
	}
}

internal static class PmPerceptualHash
{
	private const int DctSize = 32;
	private const int HashSize = 8;

	// Built with the exact same expressions/operand types as Dct1D so every factor is bit-identical.
	private static readonly double[][] CosTable = BuildCosTable();
	private static readonly double[] Scale = BuildScale();

	internal static double[,] CreateLuminance(int seed, bool smooth)
	{
		System.Random random = new(seed);
		double[,] matrix = new double[DctSize, DctSize];
		for (int y = 0; y < DctSize; y++)
		{
			for (int x = 0; x < DctSize; x++)
			{
				// Library values come from Luminance(SKColor) which returns a byte
				matrix[y, x] = (byte)(smooth ? Math.Clamp((x * 5) + (y * 3) + random.Next(-12, 13), 0, 255) : random.Next(256));
			}
		}
		return matrix;
	}

	// Verbatim copy of steps 3-6 of ImageFingerprinting.ComputePerceptualHash (steps 1-2 resize + GetPixel are covered by the Skia benchmarks).
	internal static ulong OriginalHash(double[,] luminanceMatrix)
	{
		const int dctSize = 32; // full DCT grid
		const int hashSize = 8;  // top-left sub-grid used for hash

		// Step 3: apply 2D DCT
		double[,] dct = ComputeDct2D(luminanceMatrix, dctSize);

		// Step 4: extract top-left 8x8 DCT coefficients (skip [0,0] DC component)
		double[] topLeft = new double[hashSize * hashSize];
		int idx = 0;
		for (int y = 0; y < hashSize; y++)
		{
			for (int x = 0; x < hashSize; x++)
			{
				topLeft[idx++] = dct[y, x];
			}
		}

		// Step 5: compute median of the 64 values
		double[] sorted = topLeft.OrderBy(v => v).ToArray();
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

	// Verbatim copy of ImageFingerprinting.ComputeDct2D
	private static double[,] ComputeDct2D(double[,] input, int size)
	{
		// Apply 1D DCT to each row
		double[,] temp = new double[size, size];
		for (int y = 0; y < size; y++)
		{
			double[] row = new double[size];
			for (int x = 0; x < size; x++)
			{
				row[x] = input[y, x];
			}

			double[] dctRow = Dct1D(row, size);
			for (int x = 0; x < size; x++)
			{
				temp[y, x] = dctRow[x];
			}
		}

		// Apply 1D DCT to each column of the row-transformed result
		double[,] output = new double[size, size];
		for (int x = 0; x < size; x++)
		{
			double[] col = new double[size];
			for (int y = 0; y < size; y++)
			{
				col[y] = temp[y, x];
			}

			double[] dctCol = Dct1D(col, size);
			for (int y = 0; y < size; y++)
			{
				output[y, x] = dctCol[y];
			}
		}

		return output;
	}

	// Verbatim copy of ImageFingerprinting.Dct1D
	private static double[] Dct1D(double[] input, int n)
	{
		double[] output = new double[n];
		for (int k = 0; k < n; k++)
		{
			double sum = 0;
			for (int i = 0; i < n; i++)
			{
				sum += input[i] * Math.Cos(Math.PI * k * (2 * i + 1) / (2.0 * n));
			}
			output[k] = sum * (k == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n));
		}
		return output;
	}

	private static double[][] BuildCosTable()
	{
		int n = DctSize;
		double[][] table = new double[HashSize][];
		for (int k = 0; k < HashSize; k++)
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

	private static double[] BuildScale()
	{
		int n = DctSize;
		double[] scale = new double[HashSize];
		for (int k = 0; k < HashSize; k++)
		{
			scale[k] = k == 0 ? Math.Sqrt(1.0 / n) : Math.Sqrt(2.0 / n);
		}
		return scale;
	}

	// Separable DCT restricted to the 8 row coefficients x 8 column coefficients that feed the hash. Every output coefficient is
	// accumulated in the same order (i = 0..31) with the same factors as the full transform, so results are bit-identical.
	internal static ulong OptimizedHash(double[,] luminanceMatrix)
	{
		const int n = DctSize;

		// Row pass: rowPass[y * HashSize + k] == temp[y, k] of the original for k < 8
		Span<double> rowPass = stackalloc double[n * HashSize];
		for (int y = 0; y < n; y++)
		{
			for (int k = 0; k < HashSize; k++)
			{
				double[] cos = CosTable[k];
				double sum = 0;
				for (int i = 0; i < n; i++)
				{
					sum += luminanceMatrix[y, i] * cos[i];
				}
				rowPass[(y * HashSize) + k] = sum * Scale[k];
			}
		}

		// Column pass: only output rows 0..7 of columns 0..7
		Span<double> topLeft = stackalloc double[HashSize * HashSize];
		for (int y = 0; y < HashSize; y++)
		{
			double[] cos = CosTable[y];
			for (int x = 0; x < HashSize; x++)
			{
				double sum = 0;
				for (int i = 0; i < n; i++)
				{
					sum += rowPass[(i * HashSize) + x] * cos[i];
				}
				topLeft[(y * HashSize) + x] = sum * Scale[y];
			}
		}

		// Ascending sort; ties can only be +0.0/-0.0 or equal values, which cannot change the median or the >= comparisons
		double[] sorted = new double[HashSize * HashSize];
		topLeft.CopyTo(sorted);
		Array.Sort(sorted);
		double median = (sorted[31] + sorted[32]) / 2.0;

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
}

#endregion

#region Images: FindDuplicates / HammingSimilarity

public enum PmImageHashAlgorithm
{
	AverageHash,
	DifferenceHash,
	PerceptualHash
}

public record PmImageFingerprint(string FilePath, ulong Hash, PmImageHashAlgorithm Algorithm);

public record PmSimilarityResult(string ImageA, string ImageB, double SimilarityPercent, bool IsDuplicate);

/// <summary>ImageFingerprinting.FindDuplicates: SimilarityResult record per pair + bit-by-bit Hamming loop vs PopCount and allocating only for duplicates.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmFindDuplicatesBenchmarks
{
	private List<PmImageFingerprint> fingerprints = null!;

	[Params(500, 2000)]
	public int Count { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		fingerprints = PmFingerprintCompare.CreateFingerprints(Count, 11);
	}

	[Benchmark(Baseline = true)]
	public IReadOnlyList<PmSimilarityResult> Original()
	{
		return PmFingerprintCompare.OriginalFindDuplicates(fingerprints);
	}

	[Benchmark]
	public IReadOnlyList<PmSimilarityResult> Shared_PopCountNoPairAllocation()
	{
		return PmFingerprintCompare.OptimizedFindDuplicates(fingerprints);
	}
}

internal static class PmFingerprintCompare
{
	internal static List<PmImageFingerprint> CreateFingerprints(int count, int seed, PmImageHashAlgorithm algorithm = PmImageHashAlgorithm.DifferenceHash)
	{
		System.Random random = new(seed);
		List<PmImageFingerprint> list = new(count);
		for (int i = 0; i < count; i++)
		{
			ulong hash = i >= 10 && i % 10 == 0
				? list[random.Next(i)].Hash ^ (1UL << random.Next(64)) ^ (1UL << random.Next(64)) // near-duplicate
				: (ulong)random.NextInt64() ^ ((ulong)random.Next() << 33);
			list.Add(new PmImageFingerprint($"img_{i:D5}.jpg", hash, algorithm));
		}
		return list;
	}

	// Verbatim copy of ImageFingerprinting.Compare
	internal static PmSimilarityResult OriginalCompare(PmImageFingerprint a, PmImageFingerprint b, decimal duplicateThreshold = 90.0m)
	{
		if (a.Algorithm != b.Algorithm)
		{
			throw new InvalidOperationException("Cannot compare fingerprints computed with different algorithms.");
		}

		double similarity = OriginalHammingSimilarity(a.Hash, b.Hash);
		return new PmSimilarityResult(a.FilePath, b.FilePath, similarity, similarity >= (double)duplicateThreshold);
	}

	// Verbatim copy of ImageFingerprinting.FindDuplicates
	internal static IReadOnlyList<PmSimilarityResult> OriginalFindDuplicates(IReadOnlyList<PmImageFingerprint> fingerprints, decimal duplicateThreshold = 90.0m)
	{
		List<PmSimilarityResult> duplicates = new();
		for (int i = 0; i < fingerprints.Count; i++)
		{
			for (int j = i + 1; j < fingerprints.Count; j++)
			{
				PmSimilarityResult result = OriginalCompare(fingerprints[i], fingerprints[j], duplicateThreshold);
				if (result.IsDuplicate)
				{
					duplicates.Add(result);
				}
			}
		}
		return duplicates.OrderByDescending(r => r.SimilarityPercent).ToList();
	}

	// Verbatim copy of ImageFingerprinting.HammingSimilarity
	internal static double OriginalHammingSimilarity(ulong hashA, ulong hashB)
	{
		ulong xor = hashA ^ hashB;
		int differentBits = 0;
		while (xor != 0)
		{
			differentBits += (int)(xor & 1); xor >>= 1;
		}
		return (1.0 - differentBits / 64.0) * 100.0;
	}

	internal static double OptimizedHammingSimilarity(ulong hashA, ulong hashB)
	{
		return (1.0 - PopCount(hashA ^ hashB) / 64.0) * 100.0;
	}

	private static int PopCount(ulong value)
	{
#if NET
		return BitOperations.PopCount(value);
#else
		value -= (value >> 1) & 0x5555555555555555UL;
		value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
		value = (value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL;
		return (int)((value * 0x0101010101010101UL) >> 56);
#endif
	}

	// Same pair order, same exception for mixed algorithms (thrown at the same pair), same similarity values and the same stable
	// OrderByDescending; only pairs that are duplicates allocate a result.
	internal static IReadOnlyList<PmSimilarityResult> OptimizedFindDuplicates(IReadOnlyList<PmImageFingerprint> fingerprints, decimal duplicateThreshold = 90.0m)
	{
		List<PmSimilarityResult> duplicates = new();
		double threshold = (double)duplicateThreshold;
		int count = fingerprints.Count;
		for (int i = 0; i < count; i++)
		{
			PmImageFingerprint a = fingerprints[i];
			for (int j = i + 1; j < count; j++)
			{
				PmImageFingerprint b = fingerprints[j];
				if (a.Algorithm != b.Algorithm)
				{
					throw new InvalidOperationException("Cannot compare fingerprints computed with different algorithms.");
				}

				double similarity = OptimizedHammingSimilarity(a.Hash, b.Hash);
				if (similarity >= threshold)
				{
					duplicates.Add(new PmSimilarityResult(a.FilePath, b.FilePath, similarity, true));
				}
			}
		}
		return duplicates.OrderByDescending(r => r.SimilarityPercent).ToList();
	}
}

#endregion

#region Compression: Decompress(byte[]) / IsDeflateCompressed

/// <summary>Streams.Decompress(byte[]): 4x pre-sized MemoryStream + 1 MB copy buffer + growth + ToArray vs decompressing straight into a growable pooled buffer.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmDecompressBytesBenchmarks
{
	private byte[] compressed = null!;

	[Params(64 * 1024, 4 * 1024 * 1024)]
	public int PayloadSize { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		compressed = PmCompressionHelpers.CreatePayload(PayloadSize).Compress(Streams.ECompressionType.Gzip);
	}

	[Benchmark(Baseline = true)]
	public byte[] Original()
	{
		return PmDecompression.OriginalDecompress(compressed, Streams.ECompressionType.Gzip);
	}

	[Benchmark]
	public byte[] Shared_PooledDirectRead()
	{
		return PmDecompression.OptimizedDecompress(compressed, Streams.ECompressionType.Gzip);
	}
}

/// <summary>Streams.IsDeflateCompressed: copies the whole input into a new MemoryStream (and runs an async state machine) to read 1 byte vs wrapping the existing array synchronously.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmIsDeflateCompressedBenchmarks
{
	private byte[] data = null!;

	[Params(8 * 1024, 8 * 1024 * 1024)]
	public int PayloadSize { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		data = PmCompressionHelpers.CreatePayload(PayloadSize).Compress(Streams.ECompressionType.Deflate);
	}

	[Benchmark(Baseline = true)]
	public Task<bool> Original()
	{
		return PmDecompression.OriginalIsDeflateCompressed(data);
	}

	[Benchmark]
	public Task<bool> Shared_WrapArraySync()
	{
		return PmDecompression.OptimizedIsDeflateCompressed(data);
	}
}

internal static class PmCompressionHelpers
{
	internal static byte[] CreatePayload(int size)
	{
		StringBuilder sb = new(size + 128);
		System.Random random = new(1234);
		int line = 0;
		while (sb.Length < size)
		{
			sb.Append("{\"id\":").Append(line).Append(",\"name\":\"Item ").Append(line % 97).Append("\",\"price\":")
				.Append(random.Next(1, 100000).ToString(CultureInfo.InvariantCulture)).Append(",\"active\":").Append(line % 3 == 0 ? "true" : "false").Append("}\n");
			line++;
		}
		byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
		Array.Resize(ref bytes, size);
		return bytes;
	}
}

internal static class PmDecompression
{
	private const int ChunkSize = 1024 * 1024; // 1 MB
	private const int MaxCompressionRatio = 1000;
	private const int MaxArrayLength = 0x7FFFFFC7; // Array.MaxLength (not available on netstandard2.1)

	// Verbatim copy of Streams.Decompress(byte[], ...)
	internal static byte[] OriginalDecompress(byte[] compressedData, Streams.ECompressionType compressionType, int maxCompressionRatio = MaxCompressionRatio, CancellationToken cancellationToken = default)
	{
		using MemoryStream compressedStream = new(compressedData);
		// Estimate initial capacity (compressed data typically expands 2-10x)
		int estimatedSize = Math.Min((int)(compressedData.Length * 4L), int.MaxValue / 2);
		using MemoryStream decompressedStream = new(estimatedSize);

		long maxDecompressedSize = compressedData.LongLength * maxCompressionRatio;

		using (Stream decompressionStream = CreateDecompressionStream(compressedStream, compressionType, true))
		{
			OriginalCopyWithLimit(decompressionStream, decompressedStream, maxDecompressedSize, cancellationToken);
		}

		return decompressedStream.ToArray();
	}

	// Verbatim copy of Streams.CopyWithLimit
	private static void OriginalCopyWithLimit(Stream source, Stream destination, long maxBytes, CancellationToken cancellationToken = default)
	{
		long totalBytes = 0;
		int bytesRead;

		byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
		try
		{
			while ((bytesRead = source.Read(buffer, 0, ChunkSize)) > 0)
			{
				cancellationToken.ThrowIfCancellationRequested();

				totalBytes += bytesRead;

				if (totalBytes > maxBytes)
				{
					throw new Streams.CompressionLimitExceededException($"Operation would exceed maximum size limit of {maxBytes} bytes");
				}

				destination.Write(buffer, 0, bytesRead);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	// Verbatim copy of Streams.CreateDecompressionStream (net branch)
	private static Stream CreateDecompressionStream(Stream stream, Streams.ECompressionType compressionType, bool leaveOpen)
	{
		return compressionType switch
		{
			Streams.ECompressionType.Brotli => new BrotliStream(stream, CompressionMode.Decompress, leaveOpen),
			Streams.ECompressionType.Gzip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen),
			Streams.ECompressionType.Deflate => new DeflateStream(stream, CompressionMode.Decompress, leaveOpen),
			Streams.ECompressionType.ZLib => new ZLibStream(stream, CompressionMode.Decompress, leaveOpen),
			_ => throw new NotImplementedException($"Compression type {compressionType} is not supported.")
		};
	}

	// Reads straight into a pooled, growable buffer: no 4x up-front allocation, no 1 MB staging copy, no MemoryStream growth copies.
	// Same limit check (cumulative bytes > max, same message), same cancellation check placement, same final exact-size array.
	// Only difference: near the 2 GB array limit it throws IOException("Stream was too long.") up to 1 MB earlier.
	internal static byte[] OptimizedDecompress(byte[] compressedData, Streams.ECompressionType compressionType, int maxCompressionRatio = MaxCompressionRatio, CancellationToken cancellationToken = default)
	{
		using MemoryStream compressedStream = new(compressedData);
		int estimatedSize = Math.Min((int)(compressedData.Length * 4L), int.MaxValue / 2);
		if (estimatedSize < 0)
		{
			// Preserve the original ArgumentOutOfRangeException thrown by new MemoryStream(capacity) for inputs > 512 MB
			using MemoryStream _ = new(estimatedSize);
		}

		long maxDecompressedSize = compressedData.LongLength * maxCompressionRatio;

		byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(estimatedSize, ChunkSize));
		try
		{
			int total = 0;
			using (Stream decompressionStream = CreateDecompressionStream(compressedStream, compressionType, true))
			{
				while (true)
				{
					// Always request exactly ChunkSize like the original so the decompressor sees an identical read pattern
					// (same per-read counts => same limit / cancellation / corrupt-data exception ordering).
					if (buffer.Length - total < ChunkSize)
					{
						buffer = Grow(buffer, total);
					}

					int bytesRead = decompressionStream.Read(buffer, total, ChunkSize);
					if (bytesRead <= 0)
					{
						break;
					}

					cancellationToken.ThrowIfCancellationRequested();

					total += bytesRead;

					if (total > maxDecompressedSize)
					{
						throw new Streams.CompressionLimitExceededException($"Operation would exceed maximum size limit of {maxDecompressedSize} bytes");
					}
				}
			}

			return buffer.AsSpan(0, total).ToArray();
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}

	private static byte[] Grow(byte[] buffer, int used)
	{
		long required = (long)used + ChunkSize;
		if (required > MaxArrayLength)
		{
			throw new IOException("Stream was too long.");
		}

		byte[] next = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max((long)buffer.Length * 2, required), MaxArrayLength));
		Buffer.BlockCopy(buffer, 0, next, 0, used);
		ArrayPool<byte>.Shared.Return(buffer);
		return next;
	}

	// Verbatim copies of Streams.IsDeflateCompressed(byte[]) / (Memory<byte>) / IsDeflateCompressedCore
	internal static Task<bool> OriginalIsDeflateCompressed(byte[] data)
	{
		return OriginalIsDeflateCompressedCore(data.AsMemory());
	}

	internal static Task<bool> OriginalIsDeflateCompressed(Memory<byte> data)
	{
		return OriginalIsDeflateCompressedCore(data);
	}

	private static async Task<bool> OriginalIsDeflateCompressedCore(Memory<byte> data)
	{
		byte[]? buffer = null;
		try
		{
			// Create MemoryStream and write data directly to avoid ToArray() allocation
			await using MemoryStream memoryStream = new(data.Length);
			await memoryStream.WriteAsync(data).ConfigureAwait(false);
			memoryStream.Position = 0;
			await using DeflateStream deflateStream = new(memoryStream, CompressionMode.Decompress);

			buffer = ArrayPool<byte>.Shared.Rent(1);
			int bytesRead = await deflateStream.ReadAsync(buffer.AsMemory(0, 1)).ConfigureAwait(false);
			return bytesRead > 0;
		}
		catch (Exception)
		{
			return false;
		}
		finally
		{
			if (buffer != null)
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}
	}

	internal static Task<bool> OptimizedIsDeflateCompressed(byte[] data)
	{
		return Task.FromResult(OptimizedIsDeflateCompressedCore(data.AsMemory()));
	}

	internal static Task<bool> OptimizedIsDeflateCompressed(Memory<byte> data)
	{
		return Task.FromResult(OptimizedIsDeflateCompressedCore(data));
	}

	// Everything here completes synchronously over in-memory data, so Task.FromResult (cached true/false tasks) replaces the state machine.
	private static bool OptimizedIsDeflateCompressedCore(ReadOnlyMemory<byte> data)
	{
		try
		{
			using MemoryStream memoryStream = MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
				? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
				: new MemoryStream(data.ToArray(), writable: false);
			using DeflateStream deflateStream = new(memoryStream, CompressionMode.Decompress);

			Span<byte> firstByte = stackalloc byte[1];
			return deflateStream.Read(firstByte) > 0;
		}
		catch (Exception)
		{
			return false;
		}
	}
}

#endregion

#region Images: stream buffering / Base64

/// <summary>Manipulation Stream overloads (Resize/ReduceQuality/ConvertImageFormat/TryDetectImageType/TryGetMetadata): CopyTo(new MemoryStream()) + ToArray vs exact pre-sized buffer handed out without the extra copy.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmImageStreamToBytesBenchmarks
{
	private Stream source = null!;

	[Params(256 * 1024, 4 * 1024 * 1024)]
	public int Size { get; set; }

	[Params(false, true)]
	public bool FileLikeSource { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		byte[] payload = new byte[Size];
		new System.Random(5).NextBytes(payload);
		source = FileLikeSource ? new PmSeekableReadOnlyStream(payload) : new MemoryStream(payload, writable: false);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		source.Dispose();
	}

	[Benchmark(Baseline = true)]
	public byte[] Original()
	{
		source.Position = 0;
		return PmImageBuffers.OriginalReadAll(source);
	}

	[Benchmark]
	public byte[] Shared_PresizedExactBuffer()
	{
		source.Position = 0;
		return PmImageBuffers.OptimizedReadAll(source);
	}
}

/// <summary>Base64.ConvertImageFileToBase64(MemoryStream): ms.ToArray() copy before encoding vs encoding straight from TryGetBuffer.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmImageBase64Benchmarks
{
	private MemoryStream ms = null!;

	[Params(64 * 1024, 2 * 1024 * 1024)]
	public int Size { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		byte[] payload = new byte[Size];
		new System.Random(6).NextBytes(payload);
		ms = new MemoryStream();
		ms.Write(payload, 0, payload.Length);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		ms.Dispose();
	}

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return PmImageBuffers.OriginalToBase64(ms);
	}

	[Benchmark]
	public string Shared_TryGetBuffer()
	{
		return PmImageBuffers.OptimizedToBase64(ms);
	}
}

internal static class PmImageBuffers
{
	private const int MaxArrayLength = 0x7FFFFFC7;

	// Verbatim copy of the buffering line used by Manipulation.ResizeImageBase/ReduceImageQualityBase/ConvertImageFormat(Stream)
	// (TryDetectImageType/TryGetMetadata(Stream) use the same CopyTo + ToArray pair)
	internal static byte[] OriginalReadAll(Stream inputStream)
	{
		byte[] _streamBytes;
		using (MemoryStream _ms = new()) { inputStream.CopyTo(_ms); _streamBytes = _ms.ToArray(); }
		return _streamBytes;
	}

	// Pre-sizes to the remaining length of seekable streams; if the buffer was filled exactly it is returned as-is (no ToArray copy).
	// Non-seekable streams, streams that report a wrong Length, or Position > Length fall back to the original grow + ToArray behavior.
	internal static byte[] OptimizedReadAll(Stream inputStream)
	{
		int capacity = 0;
		if (inputStream.CanSeek)
		{
			long remaining = inputStream.Length - inputStream.Position;
			if (remaining > 0 && remaining <= MaxArrayLength)
			{
				capacity = (int)remaining;
			}
		}

		using MemoryStream ms = new(capacity);
		inputStream.CopyTo(ms);
		byte[] buffer = ms.GetBuffer();
		return buffer.Length == ms.Length ? buffer : ms.ToArray();
	}

	// Verbatim copy of the encoding lines of Base64.ConvertImageFileToBase64(MemoryStream) (SKBitmap.DecodeBounds check omitted: identical in both)
	internal static string OriginalToBase64(MemoryStream ms)
	{
		ReadOnlySpan<byte> imageBytes = ms.ToArray();
		return Convert.ToBase64String(imageBytes);
	}

	// TryGetBuffer exposes exactly the [origin, length) range ToArray would copy
	internal static string OptimizedToBase64(MemoryStream ms)
	{
		return ms.TryGetBuffer(out ArraySegment<byte> segment)
			? Convert.ToBase64String(segment.Array!, segment.Offset, segment.Count)
			: Convert.ToBase64String(ms.ToArray());
	}
}

/// <summary>Seekable stream without MemoryStream's CopyTo fast path (behaves like FileStream for buffering purposes).</summary>
internal sealed class PmSeekableReadOnlyStream(byte[] data) : Stream
{
	private long position;

	public override bool CanRead => true;

	public override bool CanSeek => true;

	public override bool CanWrite => false;

	public override long Length => data.Length;

	public override long Position { get => position; set => position = value; }

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count)
	{
		return Read(buffer.AsSpan(offset, count));
	}

	public override int Read(Span<byte> buffer)
	{
		if (position >= data.Length)
		{
			return 0;
		}

		int count = (int)Math.Min(buffer.Length, data.Length - position);
		data.AsSpan((int)position, count).CopyTo(buffer);
		position += count;
		return count;
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		position = origin switch
		{
			SeekOrigin.Begin => offset,
			SeekOrigin.Current => position + offset,
			_ => data.Length + offset
		};
		return position;
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}
}

/// <summary>Non-seekable stream used by PmVerify to exercise the fallback path.</summary>
internal sealed class PmNonSeekableReadOnlyStream(byte[] data) : Stream
{
	private int position;

	public override bool CanRead => true;

	public override bool CanSeek => false;

	public override bool CanWrite => false;

	public override long Length => throw new NotSupportedException();

	public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

	public override void Flush() { }

	public override int Read(byte[] buffer, int offset, int count)
	{
		int read = Math.Min(count, data.Length - position);
		Array.Copy(data, position, buffer, offset, read);
		position += read;
		return read;
	}

	public override long Seek(long offset, SeekOrigin origin)
	{
		throw new NotSupportedException();
	}

	public override void SetLength(long value)
	{
		throw new NotSupportedException();
	}

	public override void Write(byte[] buffer, int offset, int count)
	{
		throw new NotSupportedException();
	}
}

#endregion

#region Email: FormatAllUrlsToHtml / StringToHtml

/// <summary>HtmlEmailBuilder.FormatAllUrlsToHtml: one StringBuilder.Replace pass over the whole email per URL vs a single splice pass (falls back to the original when URLs overlap/duplicate).</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmFormatAllUrlsBenchmarks
{
	private string html = null!;

	[Params(0, 1, 10)]
	public int UrlCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		html = PmHtmlEmail.CreateSampleHtml(UrlCount, 500);
	}

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return PmHtmlEmail.OriginalFormatAllUrlsToHtml(html);
	}

	[Benchmark]
	public string Shared_SingleSplicePass()
	{
		return PmHtmlEmail.OptimizedFormatAllUrlsToHtml(html);
	}
}

/// <summary>HtmlEmailBuilder.StringToHtml: four chained string.Replace calls (4 full-size copies) vs one span scan into a StringBuilder.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmStringToHtmlBenchmarks
{
	private string text = null!;

	[GlobalSetup]
	public void Setup()
	{
		text = PmHtmlEmail.CreateSamplePlainText(400);
	}

	[Benchmark(Baseline = true)]
	public string Original()
	{
		return PmHtmlEmail.OriginalStringToHtml(text);
	}

	[Benchmark]
	public string Shared_SinglePass()
	{
		return PmHtmlEmail.OptimizedStringToHtml(text);
	}
}

internal static partial class PmHtmlEmail
{
	private const int MaxSinglePassMatches = 32;

	// Same pattern/options/timeout/culture as HtmlEmailBuilder.UrlRegex
	[GeneratedRegex(@"https?://[^\n\t< ]+", RegexOptions.IgnoreCase | RegexOptions.Compiled, 5000, "en-US")]
	private static partial Regex UrlRegex();

	internal static string CreateSampleHtml(int urlCount, int rows)
	{
		StringBuilder sb = new("<html><body>");
		for (int i = 0; i < urlCount; i++)
		{
			sb.Append("Please review the report at https://reports.example.com/view/").Append(i).Append("/details?id=").Append(1000 + i).Append(" before Friday.<br><br>");
		}

		sb.Append("<table><tr><th>Id</th><th>Name</th><th>Status</th></tr>");
		for (int r = 0; r < rows; r++)
		{
			sb.Append("<tr><td>").Append(r).Append("</td><td>Customer ").Append(r).Append("</td><td>Active</td></tr>");
		}
		sb.Append("</table></body></html>");
		return sb.ToString();
	}

	internal static string CreateSamplePlainText(int lines)
	{
		StringBuilder sb = new();
		for (int i = 0; i < lines; i++)
		{
			sb.Append("Line ").Append(i).Append(":\tOrder #").Append(i * 7).Append(" shipped to <Customer & Sons> \"HQ\" on schedule").Append(i % 3 == 0 ? "\n" : "\r\n");
		}
		return sb.ToString();
	}

	// Verbatim copy of HtmlEmailBuilder.FormatAllUrlsToHtml
	internal static string OriginalFormatAllUrlsToHtml(string text, string? linkText = null)
	{
		StringBuilder textToFormat = new(text);
		foreach (string url in UrlRegex().Matches(text).Select(x => x.Value))
		{
			textToFormat.Replace(url, CreateHtmlLink(url, linkText ?? "Click Here"));
		}
		return textToFormat.ToString();
	}

	// Verbatim copy of HtmlEmailBuilder.CreateHtmlLink
	internal static string CreateHtmlLink(string url, string linkText = "Click Here")
	{
		return $"<a href=\"{url}\">{linkText}</a>";
	}

	// Equivalence: every ordinal occurrence of a matched URL in the original text is either its own match or lies inside another
	// match (URLs contain no delimiter, matches end at a delimiter/end). Inserted links can only "re-match" a later URL if that URL is a
	// substring of another link (a link cannot be crossed into/out of because it starts with '<' and ends with "</a>"). So if no URL is
	// contained in any other match's link, the sequential global replaces equal one splice pass; otherwise run the original verbatim.
	internal static string OptimizedFormatAllUrlsToHtml(string text, string? linkText = null)
	{
		MatchCollection matches = UrlRegex().Matches(text);
		int count = matches.Count;
		if (count == 0)
		{
			return text;
		}

		if (count <= MaxSinglePassMatches)
		{
			string resolvedLinkText = linkText ?? "Click Here";
			string[] urls = new string[count];
			string[] links = new string[count];
			int extraLength = 0;
			for (int i = 0; i < count; i++)
			{
				urls[i] = matches[i].Value;
				links[i] = CreateHtmlLink(urls[i], resolvedLinkText);
				extraLength += links[i].Length - urls[i].Length;
			}

			if (CanSplice(urls, links))
			{
				StringBuilder sb = new(text.Length + extraLength);
				int position = 0;
				for (int i = 0; i < count; i++)
				{
					Match match = matches[i];
					sb.Append(text, position, match.Index - position).Append(links[i]);
					position = match.Index + match.Length;
				}
				sb.Append(text, position, text.Length - position);
				return sb.ToString();
			}
		}

		return OriginalFormatAllUrlsToHtml(text, linkText);
	}

	private static bool CanSplice(string[] urls, string[] links)
	{
		for (int i = 0; i < urls.Length; i++)
		{
			for (int j = 0; j < links.Length; j++)
			{
				if (i != j && links[j].Contains(urls[i], StringComparison.Ordinal))
				{
					return false;
				}
			}
		}
		return true;
	}

	// Verbatim copy of HtmlEmailBuilder.StringToHtml
	internal static string OriginalStringToHtml(string? text)
	{
		if (text != null)
		{
			text = HttpUtility.HtmlEncode(text);
			text = text.Replace(Environment.NewLine, "\r").Replace("\n", "\r").Replace("\r", "<br>").Replace("\t", "&nbsp&nbsp&nbsp");
		}
		return text ?? string.Empty;
	}

	// The Replace chain maps: each Environment.NewLine (greedy, left to right) -> one <br>, every other '\r' or '\n' -> <br>, '\t' -> 3x &nbsp.
	// (On Linux NewLine is "\n", so "\r\n" still becomes "<br><br>" exactly like the original.)
	internal static string OptimizedStringToHtml(string? text)
	{
		if (text == null)
		{
			return string.Empty;
		}

		string encoded = HttpUtility.HtmlEncode(text);
		int next = encoded.AsSpan().IndexOfAny('\r', '\n', '\t');
		if (next < 0)
		{
			return encoded;
		}

		string newLine = Environment.NewLine;
		StringBuilder sb = new(encoded.Length + 64);
		int position = 0;
		while (next >= 0)
		{
			sb.Append(encoded, position, next - position);
			if (encoded[next] == '\t')
			{
				sb.Append("&nbsp&nbsp&nbsp");
				position = next + 1;
			}
			else
			{
				sb.Append("<br>");
				position = next + (encoded.AsSpan(next).StartsWith(newLine.AsSpan()) ? newLine.Length : 1);
			}

			int relative = encoded.AsSpan(position).IndexOfAny('\r', '\n', '\t');
			next = relative < 0 ? -1 : position + relative;
		}
		sb.Append(encoded, position, encoded.Length - position);
		return sb.ToString();
	}
}

#endregion

#region ReinforcedTypings: ValibotSchemaGenerator

/// <summary>ValibotSchemaGenerator.GenerateValibotSchema: attributes materialized 3x per property (2x GetCustomAttributes + lazy re-enumeration) and a new NullabilityInfoContext per property vs one fetch and one shared context.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmValibotSchemaBenchmarks
{
	[Benchmark(Baseline = true)]
	public int Original()
	{
		return PmValibotOriginal.GenerateValibotSchema(typeof(PmValibotModels.Customer)).Length
			+ PmValibotOriginal.GenerateValibotSchema(typeof(PmValibotModels.CustomerDto)).Length;
	}

	[Benchmark]
	public int Shared_SingleAttributeFetchSharedContext()
	{
		return PmValibotOptimized.GenerateValibotSchema(typeof(PmValibotModels.Customer)).Length
			+ PmValibotOptimized.GenerateValibotSchema(typeof(PmValibotModels.CustomerDto)).Length;
	}
}

/// <summary>Stand-in for Reinforced.Typings.Attributes.TsIgnoreAttribute.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PmTsIgnoreAttribute : Attribute
{
}

/// <summary>Sample models for the Valibot benchmarks (nested so attribute type names match what the generator looks for).</summary>
public static class PmValibotModels
{
	[AttributeUsage(AttributeTargets.Class)]
	public sealed class SubsetOfAttribute(Type sourceType) : Attribute
	{
		public Type SourceType { get; } = sourceType;
	}

	[AttributeUsage(AttributeTargets.Property)]
	public sealed class DenyCharactersAttribute(string deniedCharacters) : ValidationAttribute
	{
		public string DeniedCharacters { get; } = deniedCharacters;
	}

	[AttributeUsage(AttributeTargets.Property)]
	public sealed class ListMaxLengthAttribute(int length) : ValidationAttribute
	{
		public int Length { get; } = length;
	}

	public sealed class Address
	{
		[Required]
		[MaxLength(100)]
		public string Street { get; set; } = string.Empty;

		[StringLength(50, MinimumLength = 2, ErrorMessage = "City's length is invalid")]
		public string? City { get; set; }

		public Customer? Owner { get; set; }
	}

	public sealed class Customer
	{
		[Required]
		public int Id { get; set; }

		[Required]
		[MaxLength(50, ErrorMessage = "Name too long")]
		[DisplayName("Customer Name")]
		public string Name { get; set; } = string.Empty;

		[EmailAddress(ErrorMessage = "Invalid email")]
		public string? Email { get; set; }

		[Phone]
		public string? Phone { get; set; }

		[Range(0, 150)]
		public int? Age { get; set; }

		[RegularExpression("^[A-Z]{2}$")]
		public string? Country { get; set; }

		[Url]
		public string? Website { get; set; }

		[DenyCharacters("<>-")]
		public string? Notes { get; set; }

		[ListMaxLength(20, ErrorMessage = "Tag too long")]
		public List<string>? Tags { get; set; }

		public decimal Balance { get; set; }

		public DateTime CreatedOn { get; set; }

		public Guid ExternalId { get; set; }

		public bool IsActive { get; set; }

		public Address? Address { get; set; }

		public List<Address> PreviousAddresses { get; set; } = [];

		[PmTsIgnore]
		public object? Internal { get; set; }
	}

	[SubsetOf(typeof(Customer))]
	public sealed class CustomerDto
	{
		public int Id { get; set; }

		public string Name { get; set; } = string.Empty;

		public string? Email { get; set; }

		public List<string>? Tags { get; set; }

		public Address? Address { get; set; }
	}
}

// Verbatim copy of the static schema-building parts of ValibotSchemaGenerator (TsIgnoreAttribute -> PmTsIgnoreAttribute, private -> internal).
internal static class PmValibotOriginal
{
	internal static string GenerateValibotSchema(Type type)
	{
		// Resolve the source type for validation attributes
		// BoundX types use [SubsetOf(typeof(SourceType))] and inherit validation from the parent
		Type sourceType = ResolveSourceType(type);
		string typeName = type.Name;

		StringBuilder sb = new();
		sb.AppendLine("//     This code was generated by the ValibotSchemaGenerator.");
		sb.AppendLine("//     Changes to this file may cause incorrect behavior and will be lost if");
		sb.AppendLine("//     the code is regenerated.");
		sb.AppendLine();
		sb.AppendLine("import * as v from 'valibot';");
		sb.AppendLine();
		sb.AppendLine($"export const {typeName}Schema = v.object({{");

		PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

		// Seed the ancestor set with the root type so cycles back to it are detected
		HashSet<Type> ancestors = [type];

		// Also build a labels map for DisplayName attributes
		StringBuilder labelsSb = new();
		labelsSb.AppendLine($"export const {typeName}Labels: Record<string, string> = {{");

		foreach (PropertyInfo prop in properties)
		{
			// Skip navigation/ignored properties
			if (prop.GetCustomAttribute<PmTsIgnoreAttribute>() != null)
			{
				continue;
			}

			// Get validation attributes from the source type (parent) if available
			PropertyInfo? sourceProp = sourceType != type
				? sourceType.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance)
				: null;

			PropertyInfo effectiveProp = sourceProp ?? prop;

			// Use source property attributes if available, otherwise use the property's own attributes
			IEnumerable<ValidationAttribute> validationAttrs = effectiveProp
				.GetCustomAttributes(true)
				.OfType<ValidationAttribute>();

			// Also check for custom CommonNetFuncs validation attributes via base class name matching
			IEnumerable<Attribute> allAttrs = effectiveProp.GetCustomAttributes(true).OfType<Attribute>();

			string schema = BuildPropertySchema(prop, validationAttrs, allAttrs, ancestors);
			sb.AppendLine($"\t{prop.Name}: {schema},");

			// Extract DisplayName
			DisplayNameAttribute? displayName = effectiveProp.GetCustomAttribute<DisplayNameAttribute>(true);
			string label = displayName?.DisplayName ?? SplitPascalCase(prop.Name);
			labelsSb.AppendLine($"\t{prop.Name}: '{EscapeJsString(label)}',");
		}

		sb.AppendLine("});");
		sb.AppendLine();
		sb.AppendLine($"export type {typeName}Input = v.InferInput<typeof {typeName}Schema>;");
		sb.AppendLine();

		labelsSb.AppendLine("};");
		sb.Append(labelsSb);

		return sb.ToString();
	}

	internal static string SplitPascalCase(string name)
	{
		StringBuilder result = new();
		for (int i = 0; i < name.Length; i++)
		{
			if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
			{
				result.Append(' ');
			}
			result.Append(name[i]);
		}
		return result.ToString();
	}

	internal static string EscapeJsString(string s)
	{
		return s.Replace("\\", "\\\\").Replace("'", "\\'");
	}

	internal static string ErrorMessageArg(string? message, bool leadingComma = true)
	{
		if (string.IsNullOrWhiteSpace(message))
		{
			return string.Empty;
		}

		string escaped = EscapeJsString(message);
		return leadingComma ? $", '{escaped}'" : $"'{escaped}'";
	}

	internal static string? GetAttrErrorMessage(Attribute attr)
	{
		if (attr is ValidationAttribute validationAttribute && !string.IsNullOrWhiteSpace(validationAttribute.ErrorMessage))
		{
			return validationAttribute.ErrorMessage;
		}
		return attr.GetType().GetProperty("ErrorMessage")?.GetValue(attr) as string;
	}

	private static string BuildPropertySchema(PropertyInfo prop, IEnumerable<ValidationAttribute> validationAttrs, IEnumerable<Attribute> allAttrs, HashSet<Type> ancestors)
	{
		Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
		bool isNullable = Nullable.GetUnderlyingType(prop.PropertyType) != null || !prop.PropertyType.IsValueType || (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>));

		// For reference types (string, arrays, etc.), check if nullable
		if (!prop.PropertyType.IsValueType)
		{
			System.Reflection.NullabilityInfoContext nullContext = new();
			System.Reflection.NullabilityInfo nullInfo = nullContext.Create(prop);
			isNullable = nullInfo.WriteState != System.Reflection.NullabilityState.NotNull;
		}

		// Collect outer-level pipe validators (applied to the property value itself)
		List<string> outerPipes = [];
		// Collect item-level pipe validators (applied to each element of array/list properties)
		List<string> itemPipes = [];

		foreach (ValidationAttribute attr in validationAttrs)
		{
			string? pipe = GetValidationPipe(attr);

			if (pipe != null)
			{
				outerPipes.Add(pipe);
			}
		}

		// Handle custom CommonNetFuncs.Web.Common.ValidationAttributes.
		// List* attributes produce item-level pipes; others produce outer-level pipes.
		foreach (Attribute attr in allAttrs)
		{
			(bool isItemLevel, string? pipe) = GetCustomAttributePipe(attr);
			if (pipe != null)
			{
				if (isItemLevel)
				{
					itemPipes.Add(pipe);
				}
				else
				{
					outerPipes.Add(pipe);
				}
			}
		}

		// Handle Required attribute specifically
		bool isRequired = validationAttrs.Any(x => x is RequiredAttribute);

		return FinishPropertySchema(propType, isNullable, isRequired, outerPipes, itemPipes, ancestors, GetBaseSchema);
	}

	// The attr switch of BuildPropertySchema (extracted unchanged so both variants share it)
	internal static string? GetValidationPipe(ValidationAttribute attr)
	{
		return attr switch
		{
			RequiredAttribute => null, // Handled by nullable/optional logic
			MaxLengthAttribute maxLen => $"v.maxLength({maxLen.Length}{ErrorMessageArg(maxLen.ErrorMessage)})",
			MinLengthAttribute minLen => $"v.minLength({minLen.Length}{ErrorMessageArg(minLen.ErrorMessage)})",
			StringLengthAttribute strLen => BuildStringLengthPipe(strLen),
			RangeAttribute range => BuildRangePipe(range),
			RegularExpressionAttribute regex => $"v.regex(/{regex.Pattern}/{ErrorMessageArg(regex.ErrorMessage)})",
			EmailAddressAttribute email => $"v.email({ErrorMessageArg(email.ErrorMessage, leadingComma: false)})",
			UrlAttribute url => $"v.url({ErrorMessageArg(url.ErrorMessage, leadingComma: false)})",
			PhoneAttribute phone => $"v.regex(/^\\+?[\\d\\s\\-().]+$/{ErrorMessageArg(phone.ErrorMessage)})",
			CreditCardAttribute card => $"v.creditCard({ErrorMessageArg(card.ErrorMessage, leadingComma: false)})",
			_ => null
		};
	}

	// The tail of BuildPropertySchema (extracted unchanged; the base-schema resolver is passed in so each variant recurses into itself)
	internal static string FinishPropertySchema(Type propType, bool isNullable, bool isRequired, List<string> outerPipes, List<string> itemPipes, HashSet<Type> ancestors, Func<Type, HashSet<Type>, string> getBaseSchema)
	{
		// Build base schema. For array/list types with item validators, embed the validators
		// into the element schema: v.array(v.pipe(itemType, ...itemPipes))
		Type? itemType = GetArrayItemType(propType);
		string baseSchema;
		if (itemType != null && itemPipes.Count > 0)
		{
			string itemBaseSchema = getBaseSchema(itemType, ancestors);
			string itemSchema = $"v.pipe({itemBaseSchema}, {string.Join(", ", itemPipes)})";
			baseSchema = $"v.array({itemSchema})";
		}
		else
		{
			baseSchema = getBaseSchema(propType, ancestors);
		}

		// Build the final schema
		string schema;
		if (outerPipes.Count > 0)
		{
			schema = $"v.pipe({baseSchema}, {string.Join(", ", outerPipes)})";
		}
		else
		{
			schema = baseSchema;
		}

		// Wrap in optional/nullable as appropriate
		if (!isRequired && isNullable)
		{
			schema = $"v.optional(v.nullable({schema}))";
		}
		else if (isNullable) // isRequired && isNullable: field must be provided but its value may be null
		{
			schema = $"v.nullable({schema})";
		}
		else if (!isRequired && propType != typeof(string))
		{
			schema = $"v.optional({schema})";
		}

		return schema;
	}

	private static string GetBaseSchema(Type propType, HashSet<Type> ancestors)
	{
		return GetBaseSchemaCore(propType, ancestors, GetBaseSchema, BuildInlineObjectSchema);
	}

	// GetBaseSchema body (unchanged; recursion targets are passed in so each variant recurses into itself)
	internal static string GetBaseSchemaCore(Type propType, HashSet<Type> ancestors, Func<Type, HashSet<Type>, string> getBaseSchema, Func<Type, HashSet<Type>, string> buildInlineObjectSchema)
	{
		// Check if it's an array or list type
		if (propType.IsArray)
		{
			Type elementType = propType.GetElementType()!;
			return $"v.array({getBaseSchema(elementType, ancestors)})";
		}

		if (propType.IsGenericType)
		{
			Type genericDef = propType.GetGenericTypeDefinition();
			if (genericDef == typeof(List<>) || genericDef == typeof(IList<>) || genericDef == typeof(IEnumerable<>) || genericDef == typeof(ICollection<>))
			{
				Type elementType = propType.GetGenericArguments()[0];
				return $"v.array({getBaseSchema(elementType, ancestors)})";
			}
		}

		if (propType == typeof(string))
		{
			return "v.string()";
		}

		if (propType == typeof(int) || propType == typeof(long) || propType == typeof(short) || propType == typeof(byte) || propType == typeof(uint) || propType == typeof(ulong))
		{
			return "v.number()";
		}

		if (propType == typeof(float) || propType == typeof(double) || propType == typeof(decimal))
		{
			return "v.number()";
		}

		if (propType == typeof(bool))
		{
			return "v.boolean()";
		}

		if (propType == typeof(DateTime) || propType == typeof(DateTimeOffset) || propType == typeof(DateOnly))
		{
			return "v.string()"; // Dates come as strings in JSON
		}

		if (propType == typeof(Guid))
		{
			return "v.string()";
		}

		if (propType.IsEnum)
		{
			return "v.number()"; // Enums are typically numbers in TS
		}

		// Recurse into complex domain types (non-system classes with public properties)
		if (propType.IsClass && propType.Namespace != null && !propType.Namespace.StartsWith("System", StringComparison.Ordinal) && !propType.Namespace.StartsWith("Microsoft", StringComparison.Ordinal))
		{
			if (ancestors.Contains(propType))
			{
				return "v.any()"; // Cycle detected — break recursion
			}

			ancestors.Add(propType);
			string objectSchema = buildInlineObjectSchema(propType, ancestors);
			ancestors.Remove(propType);
			return objectSchema;
		}

		// Fallback for any remaining unrecognized types
		return "v.any()";
	}

	private static string BuildInlineObjectSchema(Type type, HashSet<Type> ancestors)
	{
		PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

		StringBuilder sb = new();
		sb.Append("v.object({");

		bool first = true;
		foreach (PropertyInfo prop in properties)
		{
			if (prop.GetCustomAttribute<PmTsIgnoreAttribute>() != null)
			{
				continue;
			}

			IEnumerable<ValidationAttribute> validationAttrs = prop.GetCustomAttributes(true).OfType<ValidationAttribute>();
			IEnumerable<Attribute> allAttrs = prop.GetCustomAttributes(true).OfType<Attribute>();

			if (!first)
			{
				sb.Append(", ");
			}

			sb.Append($"{prop.Name}: {BuildPropertySchema(prop, validationAttrs, allAttrs, ancestors)}");
			first = false;
		}

		sb.Append("})");
		return sb.ToString();
	}

	internal static Type? GetArrayItemType(Type type)
	{
		Type? itemType = null;

		if (type.IsArray)
		{
			itemType = type.GetElementType();
		}
		else if (type.IsGenericType)
		{
			Type genericDef = type.GetGenericTypeDefinition();
			if (genericDef == typeof(List<>) || genericDef == typeof(IList<>) || genericDef == typeof(IEnumerable<>) || genericDef == typeof(ICollection<>))
			{
				itemType = type.GetGenericArguments()[0];
			}
		}

		// Unwrap Nullable<T> so GetBaseSchema can recognize the underlying primitive
		return itemType != null ? (Nullable.GetUnderlyingType(itemType) ?? itemType) : null;
	}

	internal static string BuildStringLengthPipe(StringLengthAttribute attr)
	{
		string msgArg = ErrorMessageArg(attr.ErrorMessage);
		List<string> parts = [];
		if (attr.MinimumLength > 0)
		{
			parts.Add($"v.minLength({attr.MinimumLength}{msgArg})");
		}

		parts.Add($"v.maxLength({attr.MaximumLength}{msgArg})");
		return string.Join(", ", parts);
	}

	internal static string BuildRangePipe(RangeAttribute attr)
	{
		string msgArg = ErrorMessageArg(attr.ErrorMessage);
		List<string> parts = [];
		if (attr.Minimum != null)
		{
			parts.Add($"v.minValue({attr.Minimum}{msgArg})");
		}

		if (attr.Maximum != null)
		{
			parts.Add($"v.maxValue({attr.Maximum}{msgArg})");
		}

		return string.Join(", ", parts);
	}

	internal static (bool isItemLevel, string? pipe) GetCustomAttributePipe(Attribute attr)
	{
		string typeName = attr.GetType().Name;

		return typeName switch
		{
			"ListMaxLengthAttribute" => (true, GetListMaxLengthItemPipe(attr)),
			"ListStringLengthAttribute" => (true, GetListStringLengthItemPipe(attr)),
			"ListRangeAttribute" => (true, GetListRangeItemPipe(attr)),
			"ListRegularExpressionAttribute" => (true, GetListRegexItemPipe(attr)),
			"ListDenyCharactersAttribute" => (true, GetDenyCharsPipe(attr)),
			"ListDenyRegularExpressionAttribute" => (true, GetDenyRegexPipe(attr)),
			"DenyCharactersAttribute" => (false, GetDenyCharsPipe(attr)),
			"DenyRegularExpressionAttribute" => (false, GetDenyRegexPipe(attr)),
			"AllowedNullableValuesAttribute" => (false, GetAllowedValuesPipe(attr)),
			_ => (false, null)
		};
	}

	private static string? GetListMaxLengthItemPipe(Attribute attr)
	{
		PropertyInfo? lengthProp = attr.GetType().GetProperty("Length");
		if (lengthProp?.GetValue(attr) is int length)
		{
			return $"v.maxLength({length}{ErrorMessageArg(GetAttrErrorMessage(attr))})";
		}

		return null;
	}

	private static string? GetListStringLengthItemPipe(Attribute attr)
	{
		PropertyInfo? maxProp = attr.GetType().GetProperty("MaximumLength");
		PropertyInfo? minProp = attr.GetType().GetProperty("MinimumLength");
		int? max = maxProp?.GetValue(attr) as int?;
		int? min = minProp?.GetValue(attr) as int?;
		string msgArg = ErrorMessageArg(GetAttrErrorMessage(attr));

		List<string> pipes = [];
		if (min.HasValue && min.Value > 0)
		{
			pipes.Add($"v.minLength({min.Value}{msgArg})");
		}

		if (max.HasValue)
		{
			pipes.Add($"v.maxLength({max.Value}{msgArg})");
		}
		return pipes.Count > 0 ? string.Join(", ", pipes) : null;
	}

	private static string? GetListRangeItemPipe(Attribute attr)
	{
		PropertyInfo? minProp = attr.GetType().GetProperty("Minimum");
		PropertyInfo? maxProp = attr.GetType().GetProperty("Maximum");
		object? min = minProp?.GetValue(attr);
		object? max = maxProp?.GetValue(attr);
		string msgArg = ErrorMessageArg(GetAttrErrorMessage(attr));

		List<string> pipes = [];
		if (min != null)
		{
			pipes.Add($"v.minValue({min}{msgArg})");
		}

		if (max != null)
		{
			pipes.Add($"v.maxValue({max}{msgArg})");
		}

		return pipes.Count > 0 ? string.Join(", ", pipes) : null;
	}

	private static string? GetListRegexItemPipe(Attribute attr)
	{
		PropertyInfo? patternProp = attr.GetType().GetProperty("Pattern");
		if (patternProp?.GetValue(attr) is string pattern)
		{
			return $"v.regex(/{pattern}/{ErrorMessageArg(GetAttrErrorMessage(attr))})";
		}

		return null;
	}

	private static string? GetDenyCharsPipe(Attribute attr)
	{
		PropertyInfo? charsProp = attr.GetType().GetProperty("DeniedCharacters") ?? attr.GetType().GetProperty("Characters");
		if (charsProp?.GetValue(attr) is string chars)
		{
			return $"v.regex(/^[^{EscapeRegexChars(chars)}]*$/{ErrorMessageArg(GetAttrErrorMessage(attr))})";
		}

		return null;
	}

	private static string? GetDenyRegexPipe(Attribute attr)
	{
		PropertyInfo? patternProp = attr.GetType().GetProperty("Pattern");
		if (patternProp?.GetValue(attr) is string pattern)
		{
			return $"v.regex(/^(?!.*{pattern}).*$/{ErrorMessageArg(GetAttrErrorMessage(attr))})";
		}

		return null;
	}

	private static string? GetAllowedValuesPipe(Attribute attr)
	{
		PropertyInfo? valuesProp = attr.GetType().GetProperty("AllowedValues") ?? attr.GetType().GetProperty("Values");
		if (valuesProp?.GetValue(attr) is object[] values && values.Length > 0)
		{
			string literals = string.Join(", ", values.Select(x => x is string str ? $"v.literal('{str}')" : $"v.literal({x})"));
			return $"v.union([{literals}]{ErrorMessageArg(GetAttrErrorMessage(attr))})";
		}
		return null;
	}

	internal static Type ResolveSourceType(Type type)
	{
		// Look for [SubsetOf(typeof(X))] attribute
		Attribute? subsetAttr = type.GetCustomAttributes(false)
			.OfType<Attribute>()
			.FirstOrDefault(x => x.GetType().Name == "SubsetOfAttribute");

		if (subsetAttr != null)
		{
			// Try SourceType property first
			PropertyInfo? typeProp = subsetAttr.GetType().GetProperty("SourceType");

			if (typeProp != null)
			{
				object? val = typeProp.GetValue(subsetAttr);
				if (val is Type sourceType)
				{
					return sourceType;
				}
			}

			// Fallback: look at constructor arguments via CustomAttributeData
			CustomAttributeData? attrData = type.CustomAttributes.FirstOrDefault(x => x.AttributeType.Name == "SubsetOfAttribute");

			if (attrData?.ConstructorArguments.Count > 0 && attrData.ConstructorArguments[0].Value is Type ctorSourceType)
			{
				return ctorSourceType;
			}
		}

		return type;
	}

	private static string EscapeRegexChars(string chars)
	{
		// Escape characters that are special in regex character classes
		StringBuilder sb = new();
		foreach (char c in chars)
		{
			if (c is ']' or '^' or '-')
			{
				sb.Append('\\');
			}
			sb.Append(c);
		}
		return sb.ToString();
	}
}

// Same output; GetCustomAttributes(true) is called once per property (the original instantiates every attribute three times) and a
// single NullabilityInfoContext (a pure cache) is shared for the whole schema instead of being rebuilt for every reference-type property.
internal static class PmValibotOptimized
{
	internal static string GenerateValibotSchema(Type type)
	{
		Type sourceType = PmValibotOriginal.ResolveSourceType(type);
		string typeName = type.Name;

		StringBuilder sb = new();
		sb.AppendLine("//     This code was generated by the ValibotSchemaGenerator.");
		sb.AppendLine("//     Changes to this file may cause incorrect behavior and will be lost if");
		sb.AppendLine("//     the code is regenerated.");
		sb.AppendLine();
		sb.AppendLine("import * as v from 'valibot';");
		sb.AppendLine();
		sb.AppendLine($"export const {typeName}Schema = v.object({{");

		PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
		HashSet<Type> ancestors = [type];
		PmValibotContext context = new();

		StringBuilder labelsSb = new();
		labelsSb.AppendLine($"export const {typeName}Labels: Record<string, string> = {{");

		foreach (PropertyInfo prop in properties)
		{
			if (prop.GetCustomAttribute<PmTsIgnoreAttribute>() != null)
			{
				continue;
			}

			PropertyInfo? sourceProp = sourceType != type
				? sourceType.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance)
				: null;

			PropertyInfo effectiveProp = sourceProp ?? prop;

			string schema = context.BuildPropertySchema(prop, effectiveProp.GetCustomAttributes(true), ancestors);
			sb.AppendLine($"\t{prop.Name}: {schema},");

			DisplayNameAttribute? displayName = effectiveProp.GetCustomAttribute<DisplayNameAttribute>(true);
			string label = displayName?.DisplayName ?? PmValibotOriginal.SplitPascalCase(prop.Name);
			labelsSb.AppendLine($"\t{prop.Name}: '{PmValibotOriginal.EscapeJsString(label)}',");
		}

		sb.AppendLine("});");
		sb.AppendLine();
		sb.AppendLine($"export type {typeName}Input = v.InferInput<typeof {typeName}Schema>;");
		sb.AppendLine();

		labelsSb.AppendLine("};");
		sb.Append(labelsSb);

		return sb.ToString();
	}

	private sealed class PmValibotContext
	{
		private readonly System.Reflection.NullabilityInfoContext nullabilityContext = new();
		private readonly Func<Type, HashSet<Type>, string> getBaseSchema;
		private readonly Func<Type, HashSet<Type>, string> buildInlineObjectSchema;

		public PmValibotContext()
		{
			getBaseSchema = GetBaseSchema;
			buildInlineObjectSchema = BuildInlineObjectSchema;
		}

		public string BuildPropertySchema(PropertyInfo prop, object[] attributes, HashSet<Type> ancestors)
		{
			Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
			bool isNullable = Nullable.GetUnderlyingType(prop.PropertyType) != null || !prop.PropertyType.IsValueType || (prop.PropertyType.IsGenericType && prop.PropertyType.GetGenericTypeDefinition() == typeof(Nullable<>));

			if (!prop.PropertyType.IsValueType)
			{
				System.Reflection.NullabilityInfo nullInfo = nullabilityContext.Create(prop);
				isNullable = nullInfo.WriteState != System.Reflection.NullabilityState.NotNull;
			}

			List<string> outerPipes = [];
			List<string> itemPipes = [];
			bool isRequired = false;

			// Same order as the original: all ValidationAttribute pipes first, then custom (by-name) pipes
			foreach (object attribute in attributes)
			{
				if (attribute is ValidationAttribute attr)
				{
					isRequired |= attr is RequiredAttribute;
					string? pipe = PmValibotOriginal.GetValidationPipe(attr);
					if (pipe != null)
					{
						outerPipes.Add(pipe);
					}
				}
			}

			foreach (object attribute in attributes)
			{
				if (attribute is Attribute attr)
				{
					(bool isItemLevel, string? pipe) = PmValibotOriginal.GetCustomAttributePipe(attr);
					if (pipe != null)
					{
						if (isItemLevel)
						{
							itemPipes.Add(pipe);
						}
						else
						{
							outerPipes.Add(pipe);
						}
					}
				}
			}

			return PmValibotOriginal.FinishPropertySchema(propType, isNullable, isRequired, outerPipes, itemPipes, ancestors, getBaseSchema);
		}

		private string GetBaseSchema(Type propType, HashSet<Type> ancestors)
		{
			return PmValibotOriginal.GetBaseSchemaCore(propType, ancestors, getBaseSchema, buildInlineObjectSchema);
		}

		private string BuildInlineObjectSchema(Type type, HashSet<Type> ancestors)
		{
			PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);

			StringBuilder sb = new();
			sb.Append("v.object({");

			bool first = true;
			foreach (PropertyInfo prop in properties)
			{
				if (prop.GetCustomAttribute<PmTsIgnoreAttribute>() != null)
				{
					continue;
				}

				if (!first)
				{
					sb.Append(", ");
				}

				sb.Append($"{prop.Name}: {BuildPropertySchema(prop, prop.GetCustomAttributes(true), ancestors)}");
				first = false;
			}

			sb.Append("})");
			return sb.ToString();
		}
	}
}

#endregion

#if PM_SKIASHARP
#region Images (SkiaSharp): fingerprint pipeline / TryGetMetadata

public enum PmSkiaHashKind
{
	Average,
	Difference
}

/// <summary>ImageFingerprinting Average/Difference hash: SKImage.FromBitmap (full-resolution pixel copy) + one GetPixel P/Invoke per pixel vs zero-copy SKImage.FromPixels + one bulk Pixels read.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmFingerprintPipelineBenchmarks
{
	private SKBitmap source = null!;

	[Params(PmSkiaHashKind.Average, PmSkiaHashKind.Difference)]
	public PmSkiaHashKind Kind { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		source = PmSkia.CreateBitmap(3000, 2000, opaque: true, seed: 7);
	}

	[GlobalCleanup]
	public void Cleanup()
	{
		source.Dispose();
	}

	[Benchmark(Baseline = true)]
	public ulong Original()
	{
		return Kind == PmSkiaHashKind.Average ? PmSkia.OriginalAverageHash(source) : PmSkia.OriginalDifferenceHash(source);
	}

	[Benchmark]
	public ulong Shared_ZeroCopyBulkPixels()
	{
		return Kind == PmSkiaHashKind.Average ? PmSkia.OptimizedAverageHash(source) : PmSkia.OptimizedDifferenceHash(source);
	}
}

/// <summary>Manipulation.TryGetMetadata: fully decodes every pixel only to read Width/Height (always equal to codec.Info) vs reading the codec header.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmTryGetMetadataBenchmarks
{
	private byte[] jpeg = null!;

	[GlobalSetup]
	public void Setup()
	{
		using SKBitmap bitmap = PmSkia.CreateBitmap(4000, 3000, opaque: true, seed: 3);
		using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
		jpeg = encoded.ToArray();
	}

	[Benchmark(Baseline = true)]
	public int Original()
	{
		PmSkia.OriginalTryGetMetadata(jpeg, out PmImageInfo metadata);
		return metadata.Width ^ metadata.Height;
	}

	[Benchmark]
	public int Shared_CodecInfoOnly()
	{
		PmSkia.OptimizedTryGetMetadata(jpeg, out PmImageInfo metadata);
		return metadata.Width ^ metadata.Height;
	}
}

/// <summary>Copy of CommonNetFuncs.Images.ImageInfo.</summary>
public class PmImageInfo
{
	public int Width { get; init; }
	public int Height { get; init; }
	public SKEncodedImageFormat? EncodedFormat { get; init; }
	public double HorizontalResolution { get; init; }
	public double VerticalResolution { get; init; }
}

internal static class PmSkia
{
	internal static SKBitmap CreateBitmap(int width, int height, bool opaque, int seed)
	{
		SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul));
		System.Random random = new(seed);
		byte[] row = new byte[width * 4];
		IntPtr pixels = bitmap.GetPixels();
		for (int y = 0; y < height; y++)
		{
			for (int x = 0; x < width; x++)
			{
				int alpha = opaque ? 255 : random.Next(256);
				int o = x * 4;
				// Smooth gradients plus noise; channels are premultiplied (<= alpha) for the Premul case
				row[o] = (byte)(((((x * 255) / Math.Max(1, width - 1)) + random.Next(24)) % 256) * alpha / 255);
				row[o + 1] = (byte)(((((y * 255) / Math.Max(1, height - 1)) + random.Next(24)) % 256) * alpha / 255);
				row[o + 2] = (byte)((((x + y) * 7 % 256) + random.Next(24)) % 256 * alpha / 255);
				row[o + 3] = (byte)alpha;
			}
			Marshal.Copy(row, 0, pixels + (y * bitmap.RowBytes), row.Length);
		}
		bitmap.NotifyPixelsChanged();
		return bitmap;
	}

	// Verbatim copy of Manipulation.ResizeTo(SKBitmap, ...)
	internal static SKBitmap OriginalResizeTo(SKBitmap sourceBitmap, int width, int height, SKCubicResampler? resampler = null)
	{
		using SKImage image = SKImage.FromBitmap(sourceBitmap);
		return ResizeTo(image, width, height, resampler);
	}

	// Verbatim copy of Manipulation.ResizeTo(SKImage, ...)
	internal static SKBitmap ResizeTo(SKImage sourceImage, int width, int height, SKCubicResampler? resampler = null)
	{
		SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
		SKBitmap resized = new(info);

		using SKCanvas canvas = new(resized);
		using SKPaint paint = new()
		{
			IsAntialias = true
		};

		canvas.DrawImage(sourceImage, new SKRect(0, 0, width, height), new SKSamplingOptions(resampler ?? SKCubicResampler.Mitchell), paint);

		return resized;
	}

	// SKImage.FromBitmap copies the pixels of a mutable bitmap (every decoded bitmap); wrapping the pixel memory is enough because the
	// image never outlives this call. Falls back to the original path when the bitmap has no raster pixels.
	internal static SKBitmap OptimizedResizeTo(SKBitmap sourceBitmap, int width, int height, SKCubicResampler? resampler = null)
	{
		IntPtr pixels = sourceBitmap.GetPixels();
		if (pixels == IntPtr.Zero)
		{
			return OriginalResizeTo(sourceBitmap, width, height, resampler);
		}

		using SKImage? image = SKImage.FromPixels(sourceBitmap.Info, pixels, sourceBitmap.RowBytes);
		return image == null ? OriginalResizeTo(sourceBitmap, width, height, resampler) : ResizeTo(image, width, height, resampler);
	}

	// Verbatim copy of ImageFingerprinting.Luminance
	private static byte Luminance(SKColor color) => (byte)(0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue);

	// Verbatim copy of ImageFingerprinting.ComputeAverageHash
	internal static ulong OriginalAverageHash(SKBitmap source)
	{
		// Step 1: resize to 8x8 with high-quality downsampling
		using SKBitmap resizedBitmap = OriginalResizeTo(source, 8, 8);

		// Step 2: extract grayscale luminance for all 64 pixels
		byte[] pixels = new byte[64];
		for (int y = 0; y < 8; y++)
		{
			for (int x = 0; x < 8; x++)
			{
				pixels[y * 8 + x] = Luminance(resizedBitmap.GetPixel(x, y));
			}
		}

		// Step 3: compute mean luminance
		double mean = pixels.Average(p => (double)p);

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

	// Verbatim copy of ImageFingerprinting.ComputeDifferenceHash
	internal static ulong OriginalDifferenceHash(SKBitmap source)
	{
		// Step 1: resize to 9 wide x 8 tall (one extra column for diff)
		using SKBitmap resizedBitmap = OriginalResizeTo(source, 9, 8);

		ulong hash = 0;
		int bit = 0;

		// Step 2: for each row, compare adjacent pixel pairs
		for (int y = 0; y < 8; y++)
		{
			for (int x = 0; x < 8; x++)
			{
				byte left = Luminance(resizedBitmap.GetPixel(x, y));
				byte right = Luminance(resizedBitmap.GetPixel(x + 1, y));

				// bit = 1 if left pixel is brighter than right
				if (left > right)
				{
					hash |= 1UL << bit;
				}
				bit++;
			}
		}

		return hash;
	}

	internal static ulong OptimizedAverageHash(SKBitmap source)
	{
		using SKBitmap resizedBitmap = OptimizedResizeTo(source, 8, 8);
		SKColor[] colors = resizedBitmap.Pixels; // row-major, one native call (same SkBitmap::getColor values as GetPixel)

		Span<byte> pixels = stackalloc byte[64];
		int sum = 0;
		for (int i = 0; i < 64; i++)
		{
			byte luminance = Luminance(colors[i]);
			pixels[i] = luminance;
			sum += luminance;
		}

		// Enumerable.Average sums exact small integers in a double and divides by the count: identical value
		double mean = (double)sum / 64;

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

	internal static ulong OptimizedDifferenceHash(SKBitmap source)
	{
		using SKBitmap resizedBitmap = OptimizedResizeTo(source, 9, 8);
		SKColor[] colors = resizedBitmap.Pixels;

		ulong hash = 0;
		int bit = 0;
		for (int y = 0; y < 8; y++)
		{
			int rowStart = y * 9;
			byte left = Luminance(colors[rowStart]);
			for (int x = 0; x < 8; x++)
			{
				byte right = Luminance(colors[rowStart + x + 1]);
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

	// Verbatim copy of Manipulation.DecodeBitmap
	private static SKBitmap DecodeBitmap(byte[] data)
	{
		SKBitmap? bitmap = null;
		try { bitmap = SKBitmap.Decode(data); } catch { /* format not supported by SKBitmap.Decode */ }

		if (bitmap != null)
		{
			return bitmap;
		}

		// Try SKCodec fallback
		using SKData skData = SKData.CreateCopy(data);
		using SKCodec? codec = SKCodec.Create(skData);
		if (codec != null)
		{
			SKImageInfo info = codec.Info;
			bitmap = new SKBitmap(info.Width, info.Height, info.ColorType, info.AlphaType);
			SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
			if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
			{
				bitmap.Dispose();
				throw new InvalidOperationException($"Failed to decode image pixels: {result}");
			}
			return bitmap;
		}

		throw new NotSupportedException("The image format is not supported. TIFF and other formats not handled by SkiaSharp cannot be decoded.");
	}

	// Verbatim copy of Manipulation.TryGetMetadata(ReadOnlySpan<byte>, out ImageInfo) (logger call removed)
	internal static bool OriginalTryGetMetadata(ReadOnlySpan<byte> imageData, out PmImageInfo metadata)
	{
		metadata = new PmImageInfo();
		SKData? data = null;
		SKCodec? codec = null;
		SKBitmap? bitmap = null;
		try
		{
			if (imageData.Length < 4)
			{
				return false;
			}

			byte[] arr = imageData.ToArray();
			data = SKData.CreateCopy(arr);
			codec = SKCodec.Create(data);
			if (codec == null) return false;
			try { bitmap = DecodeBitmap(arr); } catch { /* fallback to codec info */ }
			int w = bitmap?.Width ?? codec.Info.Width;
			int h = bitmap?.Height ?? codec.Info.Height;
			if (w > 0 && h > 0)
			{
				metadata = new PmImageInfo
				{
					Width = w,
					Height = h,
					EncodedFormat = codec.EncodedFormat,
					HorizontalResolution = 96.0,
					VerticalResolution = 96.0,
				};
				return true;
			}
		}
		catch (Exception)
		{
			// logger.Error(ex, "Error reading metadata from image data");
		}
		finally
		{
			bitmap?.Dispose();
			codec?.Dispose();
			data?.Dispose();
		}

		return false;
	}

	// SKBitmap.Decode and the SKCodec fallback both allocate a bitmap of exactly codec.Info.Width x Height (and any decode failure falls
	// back to codec.Info), so the full pixel decode never changes the result.
	internal static bool OptimizedTryGetMetadata(ReadOnlySpan<byte> imageData, out PmImageInfo metadata)
	{
		metadata = new PmImageInfo();
		SKData? data = null;
		SKCodec? codec = null;
		try
		{
			if (imageData.Length < 4)
			{
				return false;
			}

			data = SKData.CreateCopy(imageData.ToArray());
			codec = SKCodec.Create(data);
			if (codec == null) return false;
			SKImageInfo info = codec.Info;
			if (info.Width > 0 && info.Height > 0)
			{
				metadata = new PmImageInfo
				{
					Width = info.Width,
					Height = info.Height,
					EncodedFormat = codec.EncodedFormat,
					HorizontalResolution = 96.0,
					VerticalResolution = 96.0,
				};
				return true;
			}
		}
		catch (Exception)
		{
			// logger.Error(ex, "Error reading metadata from image data");
		}
		finally
		{
			codec?.Dispose();
			data?.Dispose();
		}

		return false;
	}
}

#endregion
#endif

#if PM_ROSLYN
#region SubsetModelBinder.Generator: incremental pipeline

/// <summary>SubsetValidatorGenerator re-run after an unrelated edit: CompilationProvider.Combine(Collect()) re-executes binding + AddSource for every class vs equatable per-class models whose outputs stay cached.</summary>
[MemoryDiagnoser]
[RankColumn]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 8)]
public class PmSubsetGeneratorBenchmarks
{
	private PmGeneratorScenario scenario = null!;
	private GeneratorDriver originalDriver = null!;
	private GeneratorDriver optimizedDriver = null!;

	[Params(100)]
	public int ClassCount { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		scenario = PmGeneratorScenario.Create(ClassCount);
		originalDriver = scenario.CreateDriver(new PmOriginalSubsetGenerator()).RunGenerators(scenario.BaseCompilation);
		optimizedDriver = scenario.CreateDriver(new PmOptimizedSubsetGenerator()).RunGenerators(scenario.BaseCompilation);
	}

	// Each invocation simulates one keystroke in an unrelated file (fresh Compilation, warmed driver state)
	[Benchmark(Baseline = true)]
	public GeneratorDriver Original()
	{
		return originalDriver.RunGenerators(scenario.CreateEditedCompilation());
	}

	[Benchmark]
	public GeneratorDriver Shared_EquatableModels()
	{
		return optimizedDriver.RunGenerators(scenario.CreateEditedCompilation());
	}
}

internal sealed class PmGeneratorScenario
{
	private readonly SyntaxTree scratchTree;
	private readonly SyntaxTree editedScratchTree;

	private PmGeneratorScenario(CSharpParseOptions parseOptions, CSharpCompilation baseCompilation, SyntaxTree scratchTree, SyntaxTree editedScratchTree)
	{
		ParseOptions = parseOptions;
		BaseCompilation = baseCompilation;
		this.scratchTree = scratchTree;
		this.editedScratchTree = editedScratchTree;
	}

	public CSharpParseOptions ParseOptions { get; }

	public CSharpCompilation BaseCompilation { get; }

	public static PmGeneratorScenario Create(int classCount)
	{
		CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
		List<SyntaxTree> trees =
		[
			CSharpSyntaxTree.ParseText("""
				namespace CommonNetFuncs.SubsetModelBinder
				{
					[System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
					public sealed class SubsetOfAttribute : System.Attribute
					{
						public SubsetOfAttribute(System.Type sourceType, bool isMvcApp = false, bool allowInheritedProperties = true, bool ignoreType = false) { }
					}
				}
				""", parseOptions, "SubsetOfAttribute.cs")
		];

		for (int i = 0; i < classCount; i++)
		{
			string partial = i % 10 == 3 ? string.Empty : "partial "; // SG0003
			string quantity = i % 10 == 5 ? "public long Quantity { get; set; }" : "public int Quantity { get; set; }"; // SG0001
			string extra = i % 10 == 7 ? "public string Missing { get; set; } = \"\";" : string.Empty; // SG0002
			string mvc = i % 2 == 0 ? "true" : "false";
			trees.Add(CSharpSyntaxTree.ParseText($$"""
				namespace Bench.Models
				{
					public class BaseEntity{{i}} { public int Id { get; set; } public System.DateTime CreatedOn { get; set; } }
					public class Source{{i}} : BaseEntity{{i}} { public string Name { get; set; } = ""; public decimal Price { get; set; } public int Quantity { get; set; } public string? Notes { get; set; } public bool Active { get; set; } }
				}
				namespace Bench.Dtos
				{
					[CommonNetFuncs.SubsetModelBinder.SubsetOf(typeof(Bench.Models.Source{{i}}), {{mvc}})]
					public {{partial}}class Subset{{i}} { public int Id { get; set; } public string Name { get; set; } = ""; public decimal Price { get; set; } {{quantity}} {{extra}} }
				}
				""", parseOptions, $"Subset{i}.cs"));
		}

		SyntaxTree scratch = CSharpSyntaxTree.ParseText("namespace Bench { public static class Scratch { public static int Value => 1; } }", parseOptions, "Scratch.cs");
		SyntaxTree editedScratch = CSharpSyntaxTree.ParseText("namespace Bench { public static class Scratch { public static int Value => 2; } }", parseOptions, "Scratch.cs");
		trees.Add(scratch);

		IEnumerable<MetadataReference> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
			.Split(Path.PathSeparator)
			.Where(x =>
			{
				string fileName = Path.GetFileName(x);
				return fileName.StartsWith("System.", StringComparison.Ordinal) || fileName is "netstandard.dll" or "mscorlib.dll";
			})
			.Select(x => (MetadataReference)MetadataReference.CreateFromFile(x))
			.ToList();

		CSharpCompilation compilation = CSharpCompilation.Create("PmGeneratorBench", trees, references,
			new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

		return new PmGeneratorScenario(parseOptions, compilation, scratch, editedScratch);
	}

	public CSharpCompilation CreateEditedCompilation()
	{
		return BaseCompilation.ReplaceSyntaxTree(scratchTree, editedScratchTree);
	}

	public GeneratorDriver CreateDriver(IIncrementalGenerator generator)
	{
		return CSharpGeneratorDriver.Create(new[] { generator.AsSourceGenerator() }, null, ParseOptions, null, default(GeneratorDriverOptions));
	}
}

// Verbatim copy of SubsetValidatorGenerator ([Generator] attribute omitted; nameof(SubsetValidatorGenerator) kept as its literal value).
internal sealed class PmOriginalSubsetGenerator : IIncrementalGenerator
{
	private const string FullyQualifiedAttributeName = "CommonNetFuncs.SubsetModelBinder.SubsetOfAttribute";
	private const string AttributeName = "SubsetOfAttribute";
	private const string DiagnosticCategory = "SubsetValidatorGenerator";

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<ClassDeclarationSyntax> classDeclarations = context.SyntaxProvider
			.ForAttributeWithMetadataName( //Used to be CreateSyntaxProvider
				FullyQualifiedAttributeName,
				predicate: (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
				transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx))
			.Where(static m => m is not null)!;

		IncrementalValueProvider<(Compilation, ImmutableArray<ClassDeclarationSyntax>)> compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

		context.RegisterSourceOutput(compilationAndClasses, static (spc, source) => Execute(source.Item1, source.Item2, spc));
	}

	private static ClassDeclarationSyntax? GetSemanticTargetForGeneration(GeneratorAttributeSyntaxContext context) //Used to be GeneratorSyntaxContext
	{
		ClassDeclarationSyntax classDeclarationSyntax = (ClassDeclarationSyntax)context.TargetNode;
		foreach (AttributeListSyntax attributeListSyntax in classDeclarationSyntax.AttributeLists)
		{
			foreach (AttributeSyntax attributeSyntax in attributeListSyntax.Attributes)
			{
				if (context.SemanticModel.GetSymbolInfo(attributeSyntax).Symbol is IMethodSymbol attributeSymbol)
				{
					INamedTypeSymbol attributeContainingTypeSymbol = attributeSymbol.ContainingType;
					string fullName = attributeContainingTypeSymbol.ToDisplayString();

					if (string.Equals(fullName, FullyQualifiedAttributeName))
					{
						return classDeclarationSyntax;
					}
				}
			}
		}
		return null;
	}

	private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> classes, SourceProductionContext context)
	{
		if (classes.IsDefaultOrEmpty)
		{
			return;
		}

		// Use a ConcurrentDictionary to track reported diagnostics
		ConcurrentDictionary<string, bool> reportedDiagnostics = new();

		foreach (ClassDeclarationSyntax subsetClass in classes)
		{
			SemanticModel semanticModel = compilation.GetSemanticModel(subsetClass.SyntaxTree);

			if (semanticModel.GetDeclaredSymbol(subsetClass, context.CancellationToken) is not INamedTypeSymbol subsetClassSymbol)
			{
				continue;
			}

			// Check if the class is marked as partial
			if (!subsetClass.Modifiers.Any(SyntaxKind.PartialKeyword))
			{
				ReportDiagnosticOnce(context, "SG0003", "Class must be partial", $"The class '{subsetClassSymbol.Name}' decorated with SubsetOf attribute must be marked as partial", subsetClass.Identifier.GetLocation(), reportedDiagnostics);
			}

			AttributeData? subsetOfAttribute = subsetClassSymbol.GetAttributes().FirstOrDefault(a => string.Equals(a.AttributeClass?.Name, AttributeName));

			if (subsetOfAttribute == null)
			{
				continue;
			}

			if (subsetOfAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol originalTypeSymbol)
			{
				continue;
			}

			bool isMvcApp = subsetOfAttribute.ConstructorArguments.Length > 1 && (bool)subsetOfAttribute.ConstructorArguments[1].Value!;
			bool allowInheritedProperties = subsetOfAttribute.ConstructorArguments.Length > 2 && (bool)subsetOfAttribute.ConstructorArguments[2].Value!;
			bool ignoreType = subsetOfAttribute.ConstructorArguments.Length > 3 && (bool)subsetOfAttribute.ConstructorArguments[3].Value!;

			// Get all properties of the original type, including inherited ones
			Dictionary<string, IPropertySymbol> originalProperties = GetAllProperties(originalTypeSymbol, allowInheritedProperties);

			foreach (IPropertySymbol subsetProperty in subsetClassSymbol.GetMembers().OfType<IPropertySymbol>())
			{
				if (!originalProperties.TryGetValue(subsetProperty.Name, out IPropertySymbol? originalProperty))
				{
					ReportDiagnosticOnce(context, "SG0002", "Property not found", $"Property '{subsetProperty.Name}' is not present in the parent class '{originalTypeSymbol.Name}'{(allowInheritedProperties ? " or its base classes" : string.Empty)}",
						subsetProperty.Locations.FirstOrDefault(), reportedDiagnostics);
				}
				else if (!ignoreType && !SymbolEqualityComparer.Default.Equals(subsetProperty.Type, originalProperty.Type))
				{
					ReportDiagnosticOnce(context, "SG0001", "Property type mismatch", $"Property '{subsetProperty.Name}' has a different type than in the original class '{originalTypeSymbol.Name}'{(allowInheritedProperties ? " or its base classes" : string.Empty)}. Expected: {originalProperty.Type.Name}, Found: {subsetProperty.Type.Name}",
						subsetProperty.Locations.FirstOrDefault(), reportedDiagnostics);
				}
			}

			string attributeCode = GenerateAttributeCode(subsetClassSymbol, originalTypeSymbol, isMvcApp);
			context.AddSource($"{subsetClassSymbol.Name}_Attributes.g.cs", SourceText.From(attributeCode, Encoding.UTF8));
		}
	}

	private static string GenerateAttributeCode(INamedTypeSymbol subsetClassSymbol, INamedTypeSymbol originalTypeSymbol, bool isMvcApp)
	{
		StringBuilder sb = new();
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using System;");
		sb.AppendLine(isMvcApp ? "using Microsoft.AspNetCore.Mvc;" : "using System.ComponentModel.DataAnnotations;");
		sb.AppendLine($"namespace {subsetClassSymbol.ContainingNamespace.ToDisplayString()}");
		sb.AppendLine("{");
		sb.AppendLine($"    [{(isMvcApp ? "ModelMetadataType" : "MetadataType")}(typeof({originalTypeSymbol.Name}))]");
		sb.AppendLine($"    public partial class {subsetClassSymbol.Name}");
		sb.AppendLine("    {");
		sb.AppendLine("    }");
		sb.AppendLine("}");

		return sb.ToString();
	}

	private static void ReportDiagnosticOnce(SourceProductionContext context, string id, string title, string message, Location? location, ConcurrentDictionary<string, bool> reportedDiagnostics)
	{
		string key = $"{id}:{location}:{message}";
		if (reportedDiagnostics.TryAdd(key, true))
		{
			DiagnosticDescriptor descriptor = new(id, title, message, DiagnosticCategory, DiagnosticSeverity.Error, isEnabledByDefault: true);
			context.ReportDiagnostic(Diagnostic.Create(descriptor, location ?? Location.None));
		}
	}

	internal static Dictionary<string, IPropertySymbol> GetAllProperties(INamedTypeSymbol type, bool includeInheritedProperties)
	{
		Dictionary<string, IPropertySymbol> properties = [];
		INamedTypeSymbol? currentType = type;

		while (currentType != null)
		{
			foreach (IPropertySymbol? member in currentType.GetMembers().OfType<IPropertySymbol>().Where(member => !properties.ContainsKey(member.Name)))
			{
				properties[member.Name] = member;
			}

			if (!includeInheritedProperties)
			{
				break; // Stop after processing the current type if inherited properties are not allowed
			}

			currentType = currentType.BaseType;
		}

		return properties;
	}
}

/// <summary>Equatable diagnostic payload. SourceLocation equality is (SyntaxTree reference, span), and unchanged trees keep their identity across compilations.</summary>
internal sealed record PmDiagnosticInfo(string Id, string Title, string Message, Location? Location);

/// <summary>Equatable per-class model: no Compilation, symbols or syntax nodes cross the pipeline boundary.</summary>
internal sealed record PmSubsetModel(string? Namespace, string? ClassName, string? OriginalTypeName, bool IsMvcApp, PmEquatableArray<PmDiagnosticInfo> Diagnostics);

internal readonly struct PmEquatableArray<T>(T[] items) : IEquatable<PmEquatableArray<T>> where T : IEquatable<T>
{
	public T[] Items { get; } = items;

	public bool Equals(PmEquatableArray<T> other)
	{
		return Items.AsSpan().SequenceEqual(other.Items.AsSpan());
	}

	public override bool Equals(object? obj)
	{
		return obj is PmEquatableArray<T> other && Equals(other);
	}

	public override int GetHashCode()
	{
		HashCode hash = new();
		foreach (T item in Items ?? Array.Empty<T>())
		{
			hash.Add(item);
		}
		return hash.ToHashCode();
	}
}

// Same diagnostics (ids, titles, messages, source locations) and same generated sources, but the semantic work happens in the
// ForAttributeWithMetadataName transform and produces an equatable model, so unrelated edits never re-run AddSource/ReportDiagnostic.
internal sealed class PmOptimizedSubsetGenerator : IIncrementalGenerator
{
	private const string FullyQualifiedAttributeName = "CommonNetFuncs.SubsetModelBinder.SubsetOfAttribute";
	private const string AttributeName = "SubsetOfAttribute";
	private const string DiagnosticCategory = "SubsetValidatorGenerator";

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		IncrementalValuesProvider<PmSubsetModel> models = context.SyntaxProvider
			.ForAttributeWithMetadataName(
				FullyQualifiedAttributeName,
				predicate: static (node, _) => node is ClassDeclarationSyntax { AttributeLists.Count: > 0 },
				transform: static (ctx, ct) => CreateModel(ctx, ct))
			.Where(static m => m is not null)!;

		context.RegisterSourceOutput(models, static (spc, model) => Emit(spc, model));
	}

	private static PmSubsetModel? CreateModel(GeneratorAttributeSyntaxContext context, CancellationToken cancellationToken)
	{
		// ForAttributeWithMetadataName only invokes the transform for nodes whose attribute binds to the fully qualified name,
		// which is exactly what the original GetSymbolInfo loop re-checked; TargetSymbol == GetDeclaredSymbol(TargetNode).
		ClassDeclarationSyntax subsetClass = (ClassDeclarationSyntax)context.TargetNode;
		if (context.TargetSymbol is not INamedTypeSymbol subsetClassSymbol)
		{
			return null;
		}

		List<PmDiagnosticInfo> diagnostics = [];
		HashSet<string> reported = [];

		if (!subsetClass.Modifiers.Any(SyntaxKind.PartialKeyword))
		{
			AddOnce(diagnostics, reported, "SG0003", "Class must be partial", $"The class '{subsetClassSymbol.Name}' decorated with SubsetOf attribute must be marked as partial", subsetClass.Identifier.GetLocation());
		}

		AttributeData? subsetOfAttribute = subsetClassSymbol.GetAttributes().FirstOrDefault(a => string.Equals(a.AttributeClass?.Name, AttributeName));
		if (subsetOfAttribute == null || subsetOfAttribute.ConstructorArguments[0].Value is not INamedTypeSymbol originalTypeSymbol)
		{
			return new PmSubsetModel(null, null, null, false, new PmEquatableArray<PmDiagnosticInfo>([.. diagnostics]));
		}

		bool isMvcApp = subsetOfAttribute.ConstructorArguments.Length > 1 && (bool)subsetOfAttribute.ConstructorArguments[1].Value!;
		bool allowInheritedProperties = subsetOfAttribute.ConstructorArguments.Length > 2 && (bool)subsetOfAttribute.ConstructorArguments[2].Value!;
		bool ignoreType = subsetOfAttribute.ConstructorArguments.Length > 3 && (bool)subsetOfAttribute.ConstructorArguments[3].Value!;

		Dictionary<string, IPropertySymbol> originalProperties = PmOriginalSubsetGenerator.GetAllProperties(originalTypeSymbol, allowInheritedProperties);

		foreach (IPropertySymbol subsetProperty in subsetClassSymbol.GetMembers().OfType<IPropertySymbol>())
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (!originalProperties.TryGetValue(subsetProperty.Name, out IPropertySymbol? originalProperty))
			{
				AddOnce(diagnostics, reported, "SG0002", "Property not found", $"Property '{subsetProperty.Name}' is not present in the parent class '{originalTypeSymbol.Name}'{(allowInheritedProperties ? " or its base classes" : string.Empty)}",
					subsetProperty.Locations.FirstOrDefault());
			}
			else if (!ignoreType && !SymbolEqualityComparer.Default.Equals(subsetProperty.Type, originalProperty.Type))
			{
				AddOnce(diagnostics, reported, "SG0001", "Property type mismatch", $"Property '{subsetProperty.Name}' has a different type than in the original class '{originalTypeSymbol.Name}'{(allowInheritedProperties ? " or its base classes" : string.Empty)}. Expected: {originalProperty.Type.Name}, Found: {subsetProperty.Type.Name}",
					subsetProperty.Locations.FirstOrDefault());
			}
		}

		return new PmSubsetModel(subsetClassSymbol.ContainingNamespace.ToDisplayString(), subsetClassSymbol.Name, originalTypeSymbol.Name, isMvcApp, new PmEquatableArray<PmDiagnosticInfo>([.. diagnostics]));
	}

	private static void AddOnce(List<PmDiagnosticInfo> diagnostics, HashSet<string> reported, string id, string title, string message, Location? location)
	{
		if (reported.Add($"{id}:{location}:{message}"))
		{
			diagnostics.Add(new PmDiagnosticInfo(id, title, message, location));
		}
	}

	private static void Emit(SourceProductionContext context, PmSubsetModel model)
	{
		foreach (PmDiagnosticInfo diagnostic in model.Diagnostics.Items)
		{
			DiagnosticDescriptor descriptor = new(diagnostic.Id, diagnostic.Title, diagnostic.Message, DiagnosticCategory, DiagnosticSeverity.Error, isEnabledByDefault: true);
			context.ReportDiagnostic(Diagnostic.Create(descriptor, diagnostic.Location ?? Location.None));
		}

		if (model.ClassName != null)
		{
			context.AddSource($"{model.ClassName}_Attributes.g.cs", SourceText.From(GenerateAttributeCode(model.Namespace!, model.ClassName, model.OriginalTypeName!, model.IsMvcApp), Encoding.UTF8));
		}
	}

	private static string GenerateAttributeCode(string containingNamespace, string className, string originalTypeName, bool isMvcApp)
	{
		StringBuilder sb = new();
		sb.AppendLine("#nullable enable");
		sb.AppendLine("using System;");
		sb.AppendLine(isMvcApp ? "using Microsoft.AspNetCore.Mvc;" : "using System.ComponentModel.DataAnnotations;");
		sb.AppendLine($"namespace {containingNamespace}");
		sb.AppendLine("{");
		sb.AppendLine($"    [{(isMvcApp ? "ModelMetadataType" : "MetadataType")}(typeof({originalTypeName}))]");
		sb.AppendLine($"    public partial class {className}");
		sb.AppendLine("    {");
		sb.AppendLine("    }");
		sb.AppendLine("}");

		return sb.ToString();
	}
}

#endregion
#endif

#region Verification

/// <summary>Asserts every Original/Optimized pair above produces identical results (outputs, exception types and messages) on edge-case inputs.</summary>
public static class PmVerify
{
	public static void Run()
	{
		VerifyPerceptualHash();
		VerifyFindDuplicates();
		VerifyDecompress();
		VerifyIsDeflateCompressed();
		VerifyStreamToBytes();
		VerifyBase64();
		VerifyFormatAllUrls();
		VerifyStringToHtml();
		VerifyValibot();
#if PM_SKIASHARP
		VerifySkia();
#endif
#if PM_ROSLYN
		VerifyGenerator();
#endif
	}

	private static void Check(bool condition, string message)
	{
		if (!condition)
		{
			throw new InvalidOperationException(message);
		}
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

	private static string DescribeBytes(byte[] bytes)
	{
		return $"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}";
	}

	private static void VerifyPerceptualHash()
	{
		List<double[,]> inputs = [];
		for (int seed = 0; seed < 100; seed++)
		{
			inputs.Add(PmPerceptualHash.CreateLuminance(seed, smooth: true));
			inputs.Add(PmPerceptualHash.CreateLuminance(seed, smooth: false));
		}

		double[,] constant = new double[32, 32];
		double[,] gradient = new double[32, 32];
		double[,] checker = new double[32, 32];
		for (int y = 0; y < 32; y++)
		{
			for (int x = 0; x < 32; x++)
			{
				constant[y, x] = 128;
				gradient[y, x] = x * 8;
				checker[y, x] = (x + y) % 2 == 0 ? 255 : 0;
			}
		}
		inputs.Add(constant);
		inputs.Add(gradient);
		inputs.Add(checker);
		inputs.Add(new double[32, 32]);

		for (int i = 0; i < inputs.Count; i++)
		{
			Check(PmPerceptualHash.OriginalHash(inputs[i]) == PmPerceptualHash.OptimizedHash(inputs[i]), $"PerceptualHash mismatch for input #{i}");
		}
	}

	private static void VerifyFindDuplicates()
	{
		foreach (int count in new[] { 0, 1, 2, 50, 300 })
		{
			foreach (decimal threshold in new[] { 0m, 50m, 90m, 96.875m, 100m, 101m, -5m })
			{
				List<PmImageFingerprint> fingerprints = PmFingerprintCompare.CreateFingerprints(count, count + 1);
				string original = Outcome(() => PmFingerprintCompare.OriginalFindDuplicates(fingerprints, threshold), r => string.Join("|", r));
				string optimized = Outcome(() => PmFingerprintCompare.OptimizedFindDuplicates(fingerprints, threshold), r => string.Join("|", r));
				Check(original == optimized, $"FindDuplicates mismatch (count {count}, threshold {threshold})");
			}
		}

		List<PmImageFingerprint> mixed = PmFingerprintCompare.CreateFingerprints(20, 3);
		mixed.Add(new PmImageFingerprint("x", 1, PmImageHashAlgorithm.AverageHash));
		Check(Outcome(() => PmFingerprintCompare.OriginalFindDuplicates(mixed), r => string.Join("|", r)) == Outcome(() => PmFingerprintCompare.OptimizedFindDuplicates(mixed), r => string.Join("|", r)),
			"FindDuplicates mismatch for mixed algorithms");

		List<PmImageFingerprint> withNull = PmFingerprintCompare.CreateFingerprints(5, 9);
		withNull.Insert(2, null!);
		Check(Outcome(() => PmFingerprintCompare.OriginalFindDuplicates(withNull), r => string.Join("|", r)) == Outcome(() => PmFingerprintCompare.OptimizedFindDuplicates(withNull), r => string.Join("|", r)),
			"FindDuplicates mismatch for null entry");

		System.Random random = new(99);
		List<(ulong, ulong)> pairs = [(0, 0), (0, ulong.MaxValue), (ulong.MaxValue, ulong.MaxValue), (1, 0), (1UL << 63, 0)];
		for (int i = 0; i < 10000; i++)
		{
			pairs.Add(((ulong)random.NextInt64() ^ ((ulong)random.Next() << 40), (ulong)random.NextInt64()));
		}
		foreach ((ulong a, ulong b) in pairs)
		{
			Check(BitConverter.DoubleToInt64Bits(PmFingerprintCompare.OriginalHammingSimilarity(a, b)) == BitConverter.DoubleToInt64Bits(PmFingerprintCompare.OptimizedHammingSimilarity(a, b)),
				$"HammingSimilarity mismatch for {a:X16}/{b:X16}");
		}
	}

	private static void VerifyDecompress()
	{
		Streams.ECompressionType[] types = [Streams.ECompressionType.Brotli, Streams.ECompressionType.Gzip, Streams.ECompressionType.Deflate, Streams.ECompressionType.ZLib];
		int[] sizes = [0, 1, 1000, 100_000, (3 * 1024 * 1024) + 17];
		using CancellationTokenSource canceled = new();
		canceled.Cancel();

		foreach (Streams.ECompressionType type in types)
		{
			foreach (int size in sizes)
			{
				byte[] payload = PmCompressionHelpers.CreatePayload(size);
				byte[] compressed = payload.Compress(type);

				string library = Outcome(() => compressed.Decompress(type), DescribeBytes);
				string original = Outcome(() => PmDecompression.OriginalDecompress(compressed, type), DescribeBytes);
				string optimized = Outcome(() => PmDecompression.OptimizedDecompress(compressed, type), DescribeBytes);
				Check(library == original, $"Decompress verbatim copy drifted from library ({type}, {size})");
				Check(original == "OK:" + DescribeBytes(payload), $"Decompress round-trip failed ({type}, {size})");
				Check(original == optimized, $"Decompress mismatch ({type}, {size})");

				foreach (int ratio in new[] { -1, 0, 1, 2, 5 })
				{
					Check(Outcome(() => PmDecompression.OriginalDecompress(compressed, type, ratio), DescribeBytes) == Outcome(() => PmDecompression.OptimizedDecompress(compressed, type, ratio), DescribeBytes),
						$"Decompress limit mismatch ({type}, {size}, ratio {ratio})");
				}

				Check(Outcome(() => PmDecompression.OriginalDecompress(compressed, type, cancellationToken: canceled.Token), DescribeBytes) == Outcome(() => PmDecompression.OptimizedDecompress(compressed, type, cancellationToken: canceled.Token), DescribeBytes),
					$"Decompress cancellation mismatch ({type}, {size})");

				if (compressed.Length > 20)
				{
					byte[] corrupt = (byte[])compressed.Clone();
					for (int i = corrupt.Length / 2; i < Math.Min(corrupt.Length, (corrupt.Length / 2) + 8); i++)
					{
						corrupt[i] ^= 0x5A;
					}
					Check(Outcome(() => PmDecompression.OriginalDecompress(corrupt, type), DescribeBytes) == Outcome(() => PmDecompression.OptimizedDecompress(corrupt, type), DescribeBytes),
						$"Decompress corrupt-data mismatch ({type}, {size})");
					Check(Outcome(() => PmDecompression.OriginalDecompress(corrupt, type, 2), DescribeBytes) == Outcome(() => PmDecompression.OptimizedDecompress(corrupt, type, 2), DescribeBytes),
						$"Decompress corrupt-data + limit mismatch ({type}, {size})");
				}
			}

			byte[] garbage = new byte[5000];
			new System.Random(17).NextBytes(garbage);
			Check(Outcome(() => PmDecompression.OriginalDecompress(garbage, type), DescribeBytes) == Outcome(() => PmDecompression.OptimizedDecompress(garbage, type), DescribeBytes),
				$"Decompress garbage mismatch ({type})");
			Check(Outcome(() => PmDecompression.OriginalDecompress([], type), DescribeBytes) == Outcome(() => PmDecompression.OptimizedDecompress([], type), DescribeBytes),
				$"Decompress empty-input mismatch ({type})");
		}
	}

	private static void VerifyIsDeflateCompressed()
	{
		byte[] text = PmCompressionHelpers.CreatePayload(10_000);
		byte[] random = new byte[10_000];
		new System.Random(21).NextBytes(random);
		List<byte[]?> inputs =
		[
			null,
			[],
			[0x00],
			[0x03, 0x00],
			text,
			random,
			text.Compress(Streams.ECompressionType.Deflate),
			text.Compress(Streams.ECompressionType.Gzip),
			text.Compress(Streams.ECompressionType.ZLib),
			text.Compress(Streams.ECompressionType.Brotli),
			Array.Empty<byte>().Compress(Streams.ECompressionType.Deflate)
		];

		foreach (byte[]? input in inputs)
		{
			bool library = Streams.IsDeflateCompressed(input!).GetAwaiter().GetResult();
			bool original = PmDecompression.OriginalIsDeflateCompressed(input!).GetAwaiter().GetResult();
			bool optimized = PmDecompression.OptimizedIsDeflateCompressed(input!).GetAwaiter().GetResult();
			Check(library == original, "IsDeflateCompressed verbatim copy drifted from library");
			Check(original == optimized, $"IsDeflateCompressed mismatch (length {input?.Length.ToString(CultureInfo.InvariantCulture) ?? "null"})");

			if (input?.Length > 4)
			{
				Memory<byte> slice = input.AsMemory(2, input.Length - 3);
				Check(PmDecompression.OriginalIsDeflateCompressed(slice).GetAwaiter().GetResult() == PmDecompression.OptimizedIsDeflateCompressed(slice).GetAwaiter().GetResult(),
					"IsDeflateCompressed(Memory) slice mismatch");
			}
		}
	}

	private static void VerifyStreamToBytes()
	{
		foreach (int size in new[] { 0, 1, 255, 256, 257, 81920, 81921, (1024 * 1024) + 3 })
		{
			byte[] payload = new byte[size];
			new System.Random(size).NextBytes(payload);

			foreach (int start in new[] { 0, 1, size / 2, size, size + 10 })
			{
				Func<Stream>[] factories =
				[
					() => new MemoryStream(payload, writable: false),
					() => new PmSeekableReadOnlyStream(payload),
				];

				foreach (Func<Stream> factory in factories)
				{
					using Stream a = factory();
					using Stream b = factory();
					a.Position = start;
					b.Position = start;
					byte[] original = PmImageBuffers.OriginalReadAll(a);
					byte[] optimized = PmImageBuffers.OptimizedReadAll(b);
					Check(original.AsSpan().SequenceEqual(optimized), $"ReadAll mismatch (size {size}, start {start}, {a.GetType().Name})");
					Check(a.Position == b.Position, $"ReadAll final position mismatch (size {size}, start {start})");
				}
			}

			using PmNonSeekableReadOnlyStream c = new(payload);
			using PmNonSeekableReadOnlyStream d = new(payload);
			Check(PmImageBuffers.OriginalReadAll(c).AsSpan().SequenceEqual(PmImageBuffers.OptimizedReadAll(d)), $"ReadAll non-seekable mismatch (size {size})");
		}
	}

	private static void VerifyBase64()
	{
		byte[] payload = new byte[5000];
		new System.Random(4).NextBytes(payload);

		MemoryStream visible = new();
		visible.Write(payload, 0, payload.Length);
		visible.Position = 17;

		List<MemoryStream> streams =
		[
			new MemoryStream(),
			visible,
			new MemoryStream(payload),
			new MemoryStream(payload, 3, 100, writable: true, publiclyVisible: true),
			new MemoryStream(payload, 3, 100, writable: false, publiclyVisible: false),
		];

		foreach (MemoryStream ms in streams)
		{
			Check(PmImageBuffers.OriginalToBase64(ms) == PmImageBuffers.OptimizedToBase64(ms), "Base64 mismatch");
			ms.Dispose();
		}
	}

	private static void VerifyFormatAllUrls()
	{
		string[] inputs =
		[
			string.Empty,
			"No URL here",
			"Visit http://example.com today!",
			"Check https://example.com/path?q=1 now",
			"a http://a.com b https://b.org/x c http://c.net/y?z=1",
			"dup http://a.com and http://a.com again",
			"prefix http://a.com then http://a.com/b",
			"nested http://x.com/?u=http://a.com then http://a.com",
			"case HTTP://A.COM and http://a.com",
			"quote http://a.com\">x</a> http://b.com",
			"delims http://a.com\nhttp://b.com\thttp://c.com<br>http://d.com",
			"trailing http://end.com",
			"https://start.com first",
			"weird http://a.com\"http://b.com",
			"many " + string.Join(" ", Enumerable.Range(0, 40).Select(i => $"http://site{i}.com/p")),
			PmHtmlEmail.CreateSampleHtml(10, 20),
		];

		foreach (string input in inputs)
		{
			foreach (string? linkText in new[] { null, "Go", "http://a.com", "Click Here http://b.com" })
			{
				string original = Outcome(() => PmHtmlEmail.OriginalFormatAllUrlsToHtml(input, linkText), x => x);
				string optimized = Outcome(() => PmHtmlEmail.OptimizedFormatAllUrlsToHtml(input, linkText), x => x);
				Check(original == optimized, $"FormatAllUrlsToHtml mismatch for \"{input}\" / \"{linkText}\"");
			}
		}

		Check(Outcome(() => PmHtmlEmail.OriginalFormatAllUrlsToHtml(null!), x => x) == Outcome(() => PmHtmlEmail.OptimizedFormatAllUrlsToHtml(null!), x => x), "FormatAllUrlsToHtml null mismatch");
	}

	private static void VerifyStringToHtml()
	{
		string?[] inputs =
		[
			null,
			string.Empty,
			"plain",
			"Hello\nWorld",
			"Hello\r\nWorld",
			"Hello\rWorld",
			"Hello\tWorld",
			"\r\r\n\n\r\n\r",
			"\r\n\r\n",
			"trailing\r",
			"trailing\n",
			"\tleading tab",
			"Hello <script>&\"'é ü 漢字 \U0001F600\t\r\n",
			PmHtmlEmail.CreateSamplePlainText(50),
		];

		foreach (string? input in inputs)
		{
			Check(PmHtmlEmail.OriginalStringToHtml(input) == PmHtmlEmail.OptimizedStringToHtml(input), $"StringToHtml mismatch for \"{input}\"");
		}
	}

	private static void VerifyValibot()
	{
		foreach (Type type in new[] { typeof(PmValibotModels.Customer), typeof(PmValibotModels.CustomerDto), typeof(PmValibotModels.Address) })
		{
			string original = PmValibotOriginal.GenerateValibotSchema(type);
			string optimized = PmValibotOptimized.GenerateValibotSchema(type);
			Check(original == optimized, $"Valibot schema mismatch for {type.Name}");
		}
	}

#if PM_SKIASHARP
	private static void VerifySkia()
	{
		List<SKBitmap> bitmaps =
		[
			PmSkia.CreateBitmap(400, 300, opaque: true, seed: 1),
			PmSkia.CreateBitmap(37, 23, opaque: false, seed: 2),
			PmSkia.CreateBitmap(1, 1, opaque: true, seed: 3),
			PmSkia.CreateBitmap(9, 8, opaque: false, seed: 4),
			PmSkia.CreateBitmap(8, 8, opaque: true, seed: 5),
		];

		foreach (SKBitmap bitmap in bitmaps)
		{
			foreach ((int width, int height) in new[] { (8, 8), (9, 8), (32, 32) })
			{
				using SKBitmap original = PmSkia.OriginalResizeTo(bitmap, width, height);
				using SKBitmap optimized = PmSkia.OptimizedResizeTo(bitmap, width, height);
				SKColor[] optimizedPixels = optimized.Pixels;
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						Check(original.GetPixel(x, y) == optimizedPixels[(y * width) + x], $"ResizeTo/Pixels mismatch at ({x},{y}) for {bitmap.Width}x{bitmap.Height} -> {width}x{height}");
					}
				}
			}

			Check(PmSkia.OriginalAverageHash(bitmap) == PmSkia.OptimizedAverageHash(bitmap), $"AverageHash mismatch for {bitmap.Width}x{bitmap.Height}");
			Check(PmSkia.OriginalDifferenceHash(bitmap) == PmSkia.OptimizedDifferenceHash(bitmap), $"DifferenceHash mismatch for {bitmap.Width}x{bitmap.Height}");
		}

		List<byte[]> images = [];
		using (SKData jpeg = bitmaps[0].Encode(SKEncodedImageFormat.Jpeg, 80))
		{
			images.Add(jpeg.ToArray());
		}
		using (SKData png = bitmaps[1].Encode(SKEncodedImageFormat.Png, 100))
		{
			images.Add(png.ToArray());
		}
		images.Add(images[0][..(images[0].Length / 2)]); // truncated JPEG
		images.Add(images[0][..64]);
		images.Add([1, 2, 3]);
		images.Add(new byte[2048]);
		images.Add([]);

		foreach (SKBitmap bitmap in bitmaps)
		{
			bitmap.Dispose();
		}

		foreach (byte[] image in images)
		{
			bool originalResult = PmSkia.OriginalTryGetMetadata(image, out PmImageInfo originalInfo);
			bool optimizedResult = PmSkia.OptimizedTryGetMetadata(image, out PmImageInfo optimizedInfo);
			Check(originalResult == optimizedResult && originalInfo.Width == optimizedInfo.Width && originalInfo.Height == optimizedInfo.Height
				&& originalInfo.EncodedFormat == optimizedInfo.EncodedFormat && originalInfo.HorizontalResolution.Equals(optimizedInfo.HorizontalResolution)
				&& originalInfo.VerticalResolution.Equals(optimizedInfo.VerticalResolution), $"TryGetMetadata mismatch for {image.Length}-byte input");
		}
	}
#endif

#if PM_ROSLYN
	private static void VerifyGenerator()
	{
		PmGeneratorScenario scenario = PmGeneratorScenario.Create(30);

		foreach (CSharpCompilation compilation in new[] { scenario.BaseCompilation, scenario.CreateEditedCompilation() })
		{
			string original = DescribeRun(scenario.CreateDriver(new PmOriginalSubsetGenerator()).RunGenerators(compilation));
			string optimized = DescribeRun(scenario.CreateDriver(new PmOptimizedSubsetGenerator()).RunGenerators(compilation));
			Check(original == optimized, "SubsetValidatorGenerator cold-run mismatch");
		}

		string originalIncremental = DescribeRun(scenario.CreateDriver(new PmOriginalSubsetGenerator()).RunGenerators(scenario.BaseCompilation).RunGenerators(scenario.CreateEditedCompilation()));
		string optimizedIncremental = DescribeRun(scenario.CreateDriver(new PmOptimizedSubsetGenerator()).RunGenerators(scenario.BaseCompilation).RunGenerators(scenario.CreateEditedCompilation()));
		Check(originalIncremental == optimizedIncremental, "SubsetValidatorGenerator incremental-run mismatch");
	}

	private static string DescribeRun(GeneratorDriver driver)
	{
		GeneratorRunResult result = driver.GetRunResult().Results[0];
		if (result.Exception != null)
		{
			throw new InvalidOperationException("Generator threw", result.Exception);
		}

		StringBuilder sb = new();
		foreach (GeneratedSourceResult source in result.GeneratedSources.OrderBy(x => x.HintName, StringComparer.Ordinal))
		{
			sb.Append(source.HintName).Append('\n').Append(source.SourceText.ToString()).Append('\n');
		}

		foreach (string diagnostic in result.Diagnostics.Select(x => $"{x.Id}|{x.Severity}|{x.GetMessage(CultureInfo.InvariantCulture)}|{x.Location.Kind}|{x.Location.GetLineSpan()}").OrderBy(x => x, StringComparer.Ordinal))
		{
			sb.Append(diagnostic).Append('\n');
		}

		return sb.ToString();
	}
#endif
}

#endregion
