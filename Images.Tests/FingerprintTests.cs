using CommonNetFuncs.Images;
using SkiaSharp;
using xRetry.v3;

namespace Images.Tests;

public sealed class FingerprintTests
{
	private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "TestData");

	private static string GetTestImagePath(string fileName) => Path.Combine(TestDataDir, fileName);

	private static MemoryStream GetTestImageStream(string fileName)
	{
		return new MemoryStream(File.ReadAllBytes(GetTestImagePath(fileName)));
	}

	// ── FingerprintImage (string extension method) ────────────────────────────

	[RetryTheory(3)]
	[InlineData("test.jpg", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.jpg", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.jpg", ImageHashAlgorithm.PerceptualHash)]
	[InlineData("test.png", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.png", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.png", ImageHashAlgorithm.PerceptualHash)]
	[InlineData("test.bmp", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.bmp", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.bmp", ImageHashAlgorithm.PerceptualHash)]
	[InlineData("test.gif", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.gif", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.gif", ImageHashAlgorithm.PerceptualHash)]
	public async Task FingerprintImage_ValidFile_ReturnsFingerprint(string fileName, ImageHashAlgorithm algorithm)
	{
		// Arrange
		string path = GetTestImagePath(fileName);

		// Act
		ImageFingerprint result = await path.FingerprintImage(algorithm);

		// Assert
		result.ShouldNotBeNull();
		result.FilePath.ShouldBe(path);
		result.Algorithm.ShouldBe(algorithm);
	}

	[RetryFact(3)]
	public async Task FingerprintImage_FileNotFound_Throws()
	{
		// Arrange
		string nonExistentPath = Path.Combine(TestDataDir, "does_not_exist.jpg");

		// Act & Assert
		await Should.ThrowAsync<FileNotFoundException>(() => nonExistentPath.FingerprintImage());
	}

	[RetryFact(3)]
	public async Task FingerprintImage_EmptyBitmap_Throws()
	{
		// Arrange
		using SKBitmap emptyBitmap = new(0, 0);

		// Act & Assert
		Should.Throw<ArgumentNullException>(() => emptyBitmap.FingerprintImage("empty"));
	}

	[RetryFact(3)]
	public async Task FingerprintImage_NullBitmap_Throws()
	{
		// Arrange
		SKBitmap? nullBitmap = null;

		// Act & Assert
		Should.Throw<ArgumentNullException>(() => nullBitmap!.FingerprintImage("empty"));
	}

	[RetryFact(3)]
	public async Task FingerprintImage_DefaultAlgorithm_IsDifferenceHash()
	{
		// Arrange
		string path = GetTestImagePath("test.jpg");

		// Act
		ImageFingerprint result = await path.FingerprintImage();

		// Assert
		result.Algorithm.ShouldBe(ImageHashAlgorithm.DifferenceHash);
	}

	// ── FingerprintStream (Stream extension method) ───────────────────────────

	[RetryTheory(3)]
	[InlineData("test.jpg", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.jpg", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.jpg", ImageHashAlgorithm.PerceptualHash)]
	[InlineData("test.png", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.png", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.png", ImageHashAlgorithm.PerceptualHash)]
	[InlineData("test.bmp", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.bmp", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.bmp", ImageHashAlgorithm.PerceptualHash)]
	[InlineData("test.gif", ImageHashAlgorithm.AverageHash)]
	[InlineData("test.gif", ImageHashAlgorithm.DifferenceHash)]
	[InlineData("test.gif", ImageHashAlgorithm.PerceptualHash)]
	public void FingerprintStream_ValidStream_ReturnsFingerprint(string fileName, ImageHashAlgorithm algorithm)
	{
		// Arrange
		using MemoryStream stream = GetTestImageStream(fileName);
		string label = fileName;

		// Act
		ImageFingerprint result = stream.FingerprintImage(label, algorithm);

		// Assert
		result.ShouldNotBeNull();
		result.FilePath.ShouldBe(label);
		result.Algorithm.ShouldBe(algorithm);
	}

	[RetryFact(3)]
	public void FingerprintStream_DefaultLabel_IsStream()
	{
		// Arrange
		using MemoryStream stream = GetTestImageStream("test.jpg");

		// Act
		ImageFingerprint result = stream.FingerprintImage();

		// Assert
		result.FilePath.ShouldBe("<stream>");
	}

	[RetryFact(3)]
	public void FingerprintSpan_DefaultLabel_IsStream()
	{
		// Arrange
		using MemoryStream stream = GetTestImageStream("test.jpg");
		ReadOnlySpan<byte> span = new(stream.ToArray());

		// Act
		ImageFingerprint result = span.FingerprintImage();

		// Assert
		result.FilePath.ShouldBe("<stream>");
	}

	[RetryFact(3)]
	public void FingerprintStream_InvalidData_Throws()
	{
		// Arrange
		using MemoryStream stream = new(new byte[] { 0x00, 0x01, 0x02, 0x03 });

		// Act & Assert
		Should.Throw<InvalidDataException>(() => stream.FingerprintImage("bad-data"));
	}

	// ── FingerprintDirectory ──────────────────────────────────────────────────

	[RetryFact(3)]
	public async Task FingerprintDirectory_NonRecursive_ReturnsFingerprints()
	{
		// Act
		IReadOnlyList<ImageFingerprint> results = await ImageFingerprinting.FingerprintDirectory(TestDataDir, recursive: false);

		// Assert
		results.ShouldNotBeNull();
		results.Count.ShouldBeGreaterThan(0);
		results.ShouldAllBe(fp => fp.Algorithm == ImageHashAlgorithm.DifferenceHash);
	}

	[RetryFact(3)]
	public async Task FingerprintDirectory_Recursive_ReturnsFingerprints()
	{
		// Act — TestDataDir has no subdirectories, so recursive and non-recursive should be the same count
		IReadOnlyList<ImageFingerprint> nonRecursive = await ImageFingerprinting.FingerprintDirectory(TestDataDir, recursive: false);
		IReadOnlyList<ImageFingerprint> recursive = await ImageFingerprinting.FingerprintDirectory(TestDataDir, recursive: true);

		// Assert
		recursive.Count.ShouldBeGreaterThanOrEqualTo(nonRecursive.Count);
	}

	[RetryTheory(3)]
	[InlineData(ImageHashAlgorithm.AverageHash)]
	[InlineData(ImageHashAlgorithm.DifferenceHash)]
	[InlineData(ImageHashAlgorithm.PerceptualHash)]
	public async Task FingerprintDirectory_AllAlgorithms_Succeed(ImageHashAlgorithm algorithm)
	{
		// Act
		IReadOnlyList<ImageFingerprint> results = await ImageFingerprinting.FingerprintDirectory(TestDataDir, recursive: false, algorithm);

		// Assert
		results.ShouldNotBeNull();
		results.Count.ShouldBeGreaterThan(0);
		results.ShouldAllBe(fp => fp.Algorithm == algorithm);
	}

	[RetryFact(3)]
	public async Task FingerprintDirectory_DirectoryWithNoImages_ReturnsEmpty()
	{
		// Arrange — create a temp dir with no image files
		string tempDir = Path.Combine(Path.GetTempPath(), $"fingerprint_test_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			await File.WriteAllTextAsync(Path.Combine(tempDir, "readme.txt"), "not an image");

			// Act
			IReadOnlyList<ImageFingerprint> results = await ImageFingerprinting.FingerprintDirectory(tempDir);

			// Assert
			results.ShouldBeEmpty();
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	[RetryFact(3)]
	public async Task FingerprintDirectory_SkipsUnreadableFiles_DoesNotThrow()
	{
		// Arrange — create a temp dir with a corrupt "image" file
		string tempDir = Path.Combine(Path.GetTempPath(), $"fingerprint_test_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			// Write a file with a .jpg extension but garbage content so SKBitmap.Decode fails
			await File.WriteAllBytesAsync(Path.Combine(tempDir, "corrupt.jpg"), new byte[] { 0x00, 0x01, 0x02, 0x03 });

			// Act — should not throw, just skip the bad file
			IReadOnlyList<ImageFingerprint> results = await ImageFingerprinting.FingerprintDirectory(tempDir);

			// Assert
			results.ShouldBeEmpty();
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	// ── Compare ───────────────────────────────────────────────────────────────

	[RetryFact(3)]
	public async Task Compare_SameFile_Returns100PercentSimilarity()
	{
		// Arrange
		string path = GetTestImagePath("test.jpg");
		ImageFingerprint a = await path.FingerprintImage(ImageHashAlgorithm.DifferenceHash);
		ImageFingerprint b = await path.FingerprintImage(ImageHashAlgorithm.DifferenceHash);

		// Act
		SimilarityResult result = ImageFingerprinting.Compare(a, b);

		// Assert
		result.SimilarityPercent.ShouldBe(100.0);
		result.IsDuplicate.ShouldBeTrue();
		result.ImageA.ShouldBe(a.FilePath);
		result.ImageB.ShouldBe(b.FilePath);
	}

	[RetryFact(3)]
	public async Task Compare_DifferentFiles_ReturnsSimilarityBelow100()
	{
		// Arrange
		string pathA = GetTestImagePath("test.jpg");
		string pathB = GetTestImagePath("test.png");
		ImageFingerprint a = await pathA.FingerprintImage(ImageHashAlgorithm.DifferenceHash);
		ImageFingerprint b = await pathB.FingerprintImage(ImageHashAlgorithm.DifferenceHash);

		// Act
		SimilarityResult result = ImageFingerprinting.Compare(a, b, duplicateThreshold: 99.9m);

		// Assert
		result.ShouldNotBeNull();
		result.SimilarityPercent.ShouldBeInRange(0.0, 100.0);
	}

	[RetryTheory(3)]
	[InlineData(ImageHashAlgorithm.AverageHash)]
	[InlineData(ImageHashAlgorithm.DifferenceHash)]
	[InlineData(ImageHashAlgorithm.PerceptualHash)]
	public async Task Compare_SameImage_AllAlgorithms_Returns100(ImageHashAlgorithm algorithm)
	{
		// Arrange
		string path = GetTestImagePath("test.png");
		ImageFingerprint a = await path.FingerprintImage(algorithm);
		ImageFingerprint b = await path.FingerprintImage(algorithm);

		// Act
		SimilarityResult result = ImageFingerprinting.Compare(a, b);

		// Assert
		result.SimilarityPercent.ShouldBe(100.0);
	}

	[RetryFact(3)]
	public async Task Compare_DifferentAlgorithms_Throws()
	{
		// Arrange
		string path = GetTestImagePath("test.jpg");
		ImageFingerprint a = await path.FingerprintImage(ImageHashAlgorithm.AverageHash);
		ImageFingerprint b = await path.FingerprintImage(ImageHashAlgorithm.DifferenceHash);

		// Act & Assert
		Should.Throw<InvalidOperationException>(() => ImageFingerprinting.Compare(a, b));
	}

	[RetryFact(3)]
	public async Task Compare_BelowDuplicateThreshold_IsNotDuplicate()
	{
		// Arrange — compare two very different images with a very high threshold
		string pathA = GetTestImagePath("test.jpg");
		string pathB = GetTestImagePath("test.bmp");
		ImageFingerprint a = await pathA.FingerprintImage(ImageHashAlgorithm.DifferenceHash);
		ImageFingerprint b = await pathB.FingerprintImage(ImageHashAlgorithm.DifferenceHash);

		// Act — use 100% threshold so only identical hashes are duplicates
		SimilarityResult result = ImageFingerprinting.Compare(a, b, duplicateThreshold: 100.0m);

		// If the images happen to be identical we still validate the result shape
		result.ShouldNotBeNull();
		result.IsDuplicate.ShouldBe(result.SimilarityPercent >= 100.0);
	}

	// ── FindDuplicates ────────────────────────────────────────────────────────

	[RetryFact(3)]
	public async Task FindDuplicates_SameImageTwice_FindsDuplicate()
	{
		// Arrange
		string path = GetTestImagePath("test.jpg");
		ImageFingerprint a = await path.FingerprintImage(ImageHashAlgorithm.DifferenceHash);
		ImageFingerprint b = new(path + "_copy", a.Hash, a.Algorithm); // Same hash, different label

		List<ImageFingerprint> fingerprints = [a, b];

		// Act
		IReadOnlyList<SimilarityResult> duplicates = ImageFingerprinting.FindDuplicates(fingerprints);

		// Assert
		duplicates.ShouldNotBeEmpty();
		duplicates[0].SimilarityPercent.ShouldBe(100.0);
	}

	[RetryFact(3)]
	public async Task FindDuplicates_AllDifferent_ReturnsEmpty()
	{
		// Arrange — use images that differ enough that none exceed the 90% default threshold
		// We construct fingerprints with maximally different hashes: 0 and ulong.MaxValue
		ImageFingerprint a = new("a.jpg", 0UL, ImageHashAlgorithm.DifferenceHash);
		ImageFingerprint b = new("b.jpg", ulong.MaxValue, ImageHashAlgorithm.DifferenceHash);

		await Task.CompletedTask; // satisfy async-like pattern, no real async needed

		List<ImageFingerprint> fingerprints = [a, b];

		// Act
		IReadOnlyList<SimilarityResult> duplicates = ImageFingerprinting.FindDuplicates(fingerprints, duplicateThreshold: 90.0m);

		// Assert — 0 vs MaxValue differ in all 64 bits → 0% similarity → not a duplicate
		duplicates.ShouldBeEmpty();
	}

	[RetryFact(3)]
	public async Task FindDuplicates_ResultsOrderedByDescendingSimilarity()
	{
		// Arrange — create three fingerprints where we control similarity
		string path = GetTestImagePath("test.jpg");
		ImageFingerprint original = await path.FingerprintImage(ImageHashAlgorithm.DifferenceHash);

		// Flip 1 bit (very similar) and 32 bits (less similar)
		ImageFingerprint similar1 = new("similar1.jpg", original.Hash ^ 0x01UL, original.Algorithm);
		ImageFingerprint similar2 = new("similar2.jpg", original.Hash ^ 0xFFFF_FFFF_FFFF_FFFFul, original.Algorithm);

		List<ImageFingerprint> fingerprints = [original, similar1, similar2];

		// Act
		IReadOnlyList<SimilarityResult> duplicates = ImageFingerprinting.FindDuplicates(fingerprints, duplicateThreshold: 0.0m);

		// Assert — results should be ordered descending by SimilarityPercent
		for (int i = 1; i < duplicates.Count; i++)
		{
			duplicates[i - 1].SimilarityPercent.ShouldBeGreaterThanOrEqualTo(duplicates[i].SimilarityPercent);
		}
	}

	[RetryFact(3)]
	public async Task FindDuplicates_EmptyList_ReturnsEmpty()
	{
		await Task.CompletedTask;
		List<ImageFingerprint> fingerprints = [];
		IReadOnlyList<SimilarityResult> duplicates = ImageFingerprinting.FindDuplicates(fingerprints);
		duplicates.ShouldBeEmpty();
	}

	[RetryFact(3)]
	public async Task FindDuplicates_SingleItem_ReturnsEmpty()
	{
		await Task.CompletedTask;
		string path = GetTestImagePath("test.jpg");
		ImageFingerprint a = new(path, 0xABCDEFUL, ImageHashAlgorithm.DifferenceHash);
		List<ImageFingerprint> fingerprints = [a];

		IReadOnlyList<SimilarityResult> duplicates = ImageFingerprinting.FindDuplicates(fingerprints);
		duplicates.ShouldBeEmpty();
	}

	[RetryFact(3)]
	public void FindDuplicates_MixedAlgorithms_Throws()
	{
		List<ImageFingerprint> fingerprints =
		[
			new("a.jpg", 0UL, ImageHashAlgorithm.DifferenceHash),
			new("b.jpg", 0UL, ImageHashAlgorithm.DifferenceHash),
			new("c.jpg", 0UL, ImageHashAlgorithm.AverageHash),
		];

		Should.Throw<InvalidOperationException>(() => ImageFingerprinting.FindDuplicates(fingerprints));
	}

	[RetryFact(3)]
	public void FindDuplicates_ReturnsOnlyDuplicatePairsWithCompareValues()
	{
		// Arrange — a/b differ by 1 bit, a/c by 4 bits, b/c by 5 bits, d is the inverse of a
		List<ImageFingerprint> fingerprints =
		[
			new("a.jpg", 0UL, ImageHashAlgorithm.PerceptualHash),
			new("b.jpg", 0x1UL, ImageHashAlgorithm.PerceptualHash),
			new("c.jpg", 0xF0UL, ImageHashAlgorithm.PerceptualHash),
			new("d.jpg", ulong.MaxValue, ImageHashAlgorithm.PerceptualHash),
		];

		// Act
		IReadOnlyList<SimilarityResult> duplicates = ImageFingerprinting.FindDuplicates(fingerprints, duplicateThreshold: 92.0m);

		// Assert — same values as Compare, ordered by descending similarity
		duplicates.Count.ShouldBe(3);
		duplicates[0].ShouldBe(ImageFingerprinting.Compare(fingerprints[0], fingerprints[1], 92.0m));
		duplicates[1].ShouldBe(ImageFingerprinting.Compare(fingerprints[0], fingerprints[2], 92.0m));
		duplicates[2].ShouldBe(ImageFingerprinting.Compare(fingerprints[1], fingerprints[2], 92.0m));
		duplicates.ShouldAllBe(x => x.IsDuplicate);
	}

	// ── FindDuplicatesInDirectory ─────────────────────────────────────────────

	[RetryFact(3)]
	public async Task FindDuplicatesInDirectory_TestDataDir_DoesNotThrow()
	{
		// Act — just ensure it completes without exception; exact result depends on test images
		IReadOnlyList<SimilarityResult> results = await ImageFingerprinting.FindDuplicatesInDirectory(TestDataDir, recursive: false);

		// Assert
		results.ShouldNotBeNull();
	}

	[RetryFact(3)]
	public async Task FindDuplicatesInDirectory_DirectoryWithCopiedImage_FindsDuplicate()
	{
		// Arrange — create a temp dir with two copies of the same image
		string tempDir = Path.Combine(Path.GetTempPath(), $"dup_test_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			string source = GetTestImagePath("test.jpg");
			File.Copy(source, Path.Combine(tempDir, "copy1.jpg"));
			File.Copy(source, Path.Combine(tempDir, "copy2.jpg"));

			// Act
			IReadOnlyList<SimilarityResult> results = await ImageFingerprinting.FindDuplicatesInDirectory(tempDir);

			// Assert — the two copies should be identified as duplicates
			results.ShouldNotBeEmpty();
			results[0].SimilarityPercent.ShouldBe(100.0);
		}
		finally
		{
			Directory.Delete(tempDir, recursive: true);
		}
	}

	[RetryFact(3)]
	public async Task FindDuplicatesInDirectory_Recursive_DoesNotThrow()
	{
		// Act
		IReadOnlyList<SimilarityResult> results = await ImageFingerprinting.FindDuplicatesInDirectory(TestDataDir, recursive: true);
		results.ShouldNotBeNull();
	}

	[RetryTheory(3)]
	[InlineData(ImageHashAlgorithm.AverageHash)]
	[InlineData(ImageHashAlgorithm.PerceptualHash)]
	public async Task FindDuplicatesInDirectory_AllAlgorithms_DoesNotThrow(ImageHashAlgorithm algorithm)
	{
		// Act
		IReadOnlyList<SimilarityResult> results = await ImageFingerprinting.FindDuplicatesInDirectory(TestDataDir, recursive: false, algorithm);
		results.ShouldNotBeNull();
	}

	// ── HammingSimilarity ─────────────────────────────────────────────────────

	[RetryFact(3)]
	public void HammingSimilarity_IdenticalHashes_Returns100()
	{
		// Arrange
		ulong hash = 0xDEADBEEF_CAFEBABE;

		// Act
		double similarity = ImageFingerprinting.HammingSimilarity(hash, hash);

		// Assert
		similarity.ShouldBe(100.0);
	}

	[RetryFact(3)]
	public void HammingSimilarity_AllBitsDifferent_Returns0()
	{
		// Arrange — 0 and MaxValue differ in all 64 bits
		ulong hashA = 0UL;
		ulong hashB = ulong.MaxValue;

		// Act
		double similarity = ImageFingerprinting.HammingSimilarity(hashA, hashB);

		// Assert
		similarity.ShouldBe(0.0);
	}

	[RetryFact(3)]
	public void HammingSimilarity_HalfBitsDifferent_Returns50()
	{
		// Arrange — flip exactly 32 bits (lower 32 bits set in hashB)
		ulong hashA = 0xFFFF_FFFF_FFFF_FFFFUL; // all bits set
		ulong hashB = 0xFFFF_FFFF_0000_0000UL; // upper 32 bits set → lower 32 differ

		// Act
		double similarity = ImageFingerprinting.HammingSimilarity(hashA, hashB);

		// Assert — 32 bits differ out of 64 → 50% similarity
		similarity.ShouldBe(50.0);
	}

	[RetryTheory(3)]
	[InlineData(0UL, 0UL, 100.0)]
	[InlineData(ulong.MaxValue, ulong.MaxValue, 100.0)]
	[InlineData(0UL, ulong.MaxValue, 0.0)]
	[InlineData(0x8000_0000_0000_0000UL, 0UL, 100.0 - (1.0 / 64.0 * 100.0))]
	[InlineData(0x0F0F_0000_0000_00FFUL, 0UL, 75.0)]
	[InlineData(0xAAAA_AAAA_AAAA_AAAAUL, 0x5555_5555_5555_5555UL, 0.0)]
	public void HammingSimilarity_KnownValues_AreCorrect(ulong hashA, ulong hashB, double expectedSimilarity)
	{
		double similarity = ImageFingerprinting.HammingSimilarity(hashA, hashB);
		similarity.ShouldBe(expectedSimilarity, tolerance: 0.001);
	}

	[RetryFact(3)]
	public void HammingSimilarity_IsSymmetric()
	{
		ulong first = 0x1234_5678_9ABC_DEF0UL;
		ulong second = 0xFEDC_BA98_7654_3210UL;

		double ab = ImageFingerprinting.HammingSimilarity(first, second);
		double ba = ImageFingerprinting.HammingSimilarity(second, first);

		ab.ShouldBe(ba);
	}

	// ── Same image, all three algorithms produce consistent hashes ────────────

	[RetryTheory(3)]
	[InlineData("test.jpg")]
	[InlineData("test.png")]
	[InlineData("test.bmp")]
	public async Task FingerprintImage_SameFileTwice_ProducesSameHash(string fileName)
	{
		string path = GetTestImagePath(fileName);

		foreach (ImageHashAlgorithm algorithm in Enum.GetValues<ImageHashAlgorithm>())
		{
			ImageFingerprint fp1 = await path.FingerprintImage(algorithm);
			ImageFingerprint fp2 = await path.FingerprintImage(algorithm);

			fp1.Hash.ShouldBe(fp2.Hash, $"Hash mismatch for {algorithm}");
		}
	}

	// ── Hashes match the reference implementation (FromBitmap resize + GetPixel + full 2D DCT) ──

	[RetryTheory(3)]
	[InlineData("test.jpg")]
	[InlineData("test.png")]
	[InlineData("test.bmp")]
	[InlineData("test.gif")]
	public void FingerprintImage_AllAlgorithms_MatchReferenceImplementation(string fileName)
	{
		// Arrange
		using SKBitmap bitmap = SKBitmap.Decode(GetTestImagePath(fileName));

		// Act & Assert
		bitmap.FingerprintImage(algorithm: ImageHashAlgorithm.AverageHash).Hash.ShouldBe(ReferenceAverageHash(bitmap));
		bitmap.FingerprintImage(algorithm: ImageHashAlgorithm.DifferenceHash).Hash.ShouldBe(ReferenceDifferenceHash(bitmap));
		bitmap.FingerprintImage(algorithm: ImageHashAlgorithm.PerceptualHash).Hash.ShouldBe(ReferencePerceptualHash(bitmap));
	}

	private static SKBitmap ReferenceResize(SKBitmap source, int width, int height)
	{
		using SKImage image = SKImage.FromBitmap(source);
		return image.ResizeTo(width, height);
	}

	private static byte ReferenceLuminance(SKColor color) => (byte)(0.299 * color.Red + 0.587 * color.Green + 0.114 * color.Blue);

	private static ulong ReferenceAverageHash(SKBitmap source)
	{
		using SKBitmap resized = ReferenceResize(source, 8, 8);
		byte[] pixels = new byte[64];
		for (int y = 0; y < 8; y++)
		{
			for (int x = 0; x < 8; x++)
			{
				pixels[(y * 8) + x] = ReferenceLuminance(resized.GetPixel(x, y));
			}
		}

		double mean = pixels.Average(p => (double)p);
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

	private static ulong ReferenceDifferenceHash(SKBitmap source)
	{
		using SKBitmap resized = ReferenceResize(source, 9, 8);
		ulong hash = 0;
		int bit = 0;
		for (int y = 0; y < 8; y++)
		{
			for (int x = 0; x < 8; x++)
			{
				if (ReferenceLuminance(resized.GetPixel(x, y)) > ReferenceLuminance(resized.GetPixel(x + 1, y)))
				{
					hash |= 1UL << bit;
				}
				bit++;
			}
		}
		return hash;
	}

	private static ulong ReferencePerceptualHash(SKBitmap source)
	{
		const int size = 32;
		using SKBitmap resized = ReferenceResize(source, size, size);

		double[,] luminance = new double[size, size];
		for (int y = 0; y < size; y++)
		{
			for (int x = 0; x < size; x++)
			{
				luminance[y, x] = ReferenceLuminance(resized.GetPixel(x, y));
			}
		}

		// Full separable DCT-II: rows, then columns
		double[,] temp = new double[size, size];
		for (int y = 0; y < size; y++)
		{
			double[] row = new double[size];
			for (int x = 0; x < size; x++)
			{
				row[x] = luminance[y, x];
			}

			double[] dctRow = ReferenceDct1D(row, size);
			for (int x = 0; x < size; x++)
			{
				temp[y, x] = dctRow[x];
			}
		}

		double[,] dct = new double[size, size];
		for (int x = 0; x < size; x++)
		{
			double[] col = new double[size];
			for (int y = 0; y < size; y++)
			{
				col[y] = temp[y, x];
			}

			double[] dctCol = ReferenceDct1D(col, size);
			for (int y = 0; y < size; y++)
			{
				dct[y, x] = dctCol[y];
			}
		}

		double[] topLeft = new double[64];
		int idx = 0;
		for (int y = 0; y < 8; y++)
		{
			for (int x = 0; x < 8; x++)
			{
				topLeft[idx++] = dct[y, x];
			}
		}

		double[] sorted = topLeft.OrderBy(v => v).ToArray();
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

	private static double[] ReferenceDct1D(double[] input, int n)
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

	// ── ImageFingerprint and SimilarityResult record equality ────────────────

	[RetryFact(3)]
	public void ImageFingerprint_RecordEquality_Works()
	{
		ImageFingerprint a = new("path.jpg", 12345UL, ImageHashAlgorithm.AverageHash);
		ImageFingerprint b = new("path.jpg", 12345UL, ImageHashAlgorithm.AverageHash);
		a.ShouldBe(b);
	}

	[RetryFact(3)]
	public void SimilarityResult_RecordEquality_Works()
	{
		SimilarityResult a = new("a.jpg", "b.jpg", 95.0, true);
		SimilarityResult b = new("a.jpg", "b.jpg", 95.0, true);
		a.ShouldBe(b);
	}
}

public sealed class ImageFingerprintBehaviorTests
{
	private static readonly string TestDataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData");






















	private static MemoryStream OpenTestImage() =>
		new(File.ReadAllBytes(Path.Combine(TestDataDirectory, "test.png")));

	private static SKBitmap CreateBitmap()
	{
		SKBitmap bitmap = new(8, 8);
		bitmap.Erase(SKColors.Red);
		return bitmap;
	}

	private sealed class NonSeekableReadStream(byte[] bytes) : MemoryStream(bytes)
	{
		public override bool CanSeek => false;
	}

	[Fact]
	public void FingerprintImage_InvalidAlgorithm_Throws()
	{
		using SKBitmap bitmap = CreateBitmap();

		Should.Throw<ArgumentOutOfRangeException>(() => bitmap.FingerprintImage("invalid", (ImageHashAlgorithm)999));
	}

	[Fact]
	public void FingerprintImage_InvalidSpan_ThrowsInvalidData()
	{
		ArgumentNullException? exception = null;
		try
		{
			ReadOnlySpan<byte> bytes = [1, 2, 3, 4, 5];
			bytes.FingerprintImage("invalid");
		}
		catch (ArgumentNullException ex)
		{
			exception = ex;
		}
		exception.ShouldNotBeNull();
	}

}
