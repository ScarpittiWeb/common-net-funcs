using AutoFixture;
using CommonNetFuncs.Compression;
using System.IO.Compression;
using static CommonNetFuncs.Compression.Files;
using static CommonNetFuncs.Compression.Streams;
using static CommonNetFuncs.Core.Random;
using static Xunit.TestContext;

namespace Compression.Tests;

public sealed class FilesTests
{
	private readonly Fixture fixture;

	public FilesTests()
	{
		fixture = new Fixture();
	}

	[Fact]
	public async Task ZipFile_Should_Create_Zip_With_Single_File()
	{
		// Arrange
		string fileName = fixture.Create<string>();
		MemoryStream fileStream = new(GetRandomBytes(100));
		MemoryStream zipFileStream = new();

		// Act
		await (fileStream, fileName).ZipFile(zipFileStream, cancellationToken: Current.CancellationToken);

		// Assert
		zipFileStream.Length.ShouldBeGreaterThan(0);
		using ZipArchive archive = new(zipFileStream, ZipArchiveMode.Read);
		archive.Entries.Count.ShouldBe(1);
		archive.Entries[0].Name.ShouldBe(fileName);
	}

	[Fact]
	public async Task ZipFile_NullStream_Should_Create_Zip_With_Single_File()
	{
		// Arrange
		string fileName = fixture.Create<string>();
		await using MemoryStream fileStream = new(GetRandomBytes(100));

		// Act
		await using MemoryStream zipFileStream = await (fileStream, fileName).ZipFile(cancellationToken: Current.CancellationToken);

		// Assert
		zipFileStream.Length.ShouldBeGreaterThan(0);
		using ZipArchive archive = new(zipFileStream, ZipArchiveMode.Read);
		archive.Entries.Count.ShouldBe(1);
		archive.Entries[0].Name.ShouldBe(fileName);
	}

	[Fact]
	public async Task ZipFiles_Should_Create_Zip_With_Multiple_Files()
	{
		// Arrange
		List<(Stream?, string)> files =
		[
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()), "file1.txt"),
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray()), "file2.txt"),
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)(i + 2)).ToArray()), "file3.txt")
		];
		await using MemoryStream zipFileStream = new();

		// Act
		await files.ZipFiles(zipFileStream, cancellationToken: Current.CancellationToken);

		// Assert
		zipFileStream.Length.ShouldBeGreaterThan(0);
		using ZipArchive archive = new(zipFileStream, ZipArchiveMode.Read);
		archive.Entries.Count.ShouldBe(files.Count);
		foreach ((Stream?, string fileName) file in files)
		{
			archive.Entries.ShouldContain(x => x.Name == file.fileName);
		}
	}

	[Fact]
	public async Task ZipFiles_Null_Stream_Should_Create_Zip_With_Multiple_Files()
	{
		// Arrange
		List<(Stream?, string)> files =
		[
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()), "file1.txt"),
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray()), "file2.txt"),
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)(i + 2)).ToArray()), "file3.txt")
		];

		// Act
		await using MemoryStream? zipFileStream = await files.ZipFiles(cancellationToken: Current.CancellationToken);

		// Assert
		zipFileStream?.Length.ShouldBeGreaterThan(0);
		using ZipArchive archive = new(zipFileStream ?? new(), ZipArchiveMode.Read);
		archive.Entries.Count.ShouldBe(files.Count);
		foreach ((Stream?, string fileName) file in files)
		{
			archive.Entries.ShouldContain(x => x.Name == file.fileName);
		}
	}

	[Fact]
	public async Task AddFilesToZip_Should_Add_Files_To_Archive()
	{
		// Arrange
		List<(Stream?, string)> files =
		[
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()), "file1.txt"),
			(new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)(i + 1)).ToArray()), "file2.txt")
		];
		await using MemoryStream memoryStream = new();
		using (ZipArchive archive = new(memoryStream, ZipArchiveMode.Create, true))
		{
			// Act
			await files.AddFilesToZip(archive, cancellationToken: Current.CancellationToken);
		}

		// Reopen the ZipArchive in Read mode to verify its contents
		using ZipArchive readArchive = new(memoryStream, ZipArchiveMode.Read, true);

		// Assert
		readArchive.Entries.Count.ShouldBe(files.Count);
		foreach ((Stream?, string fileName) file in files)
		{
			readArchive.Entries.ShouldContain(x => x.Name == file.fileName);
		}
	}

	[Fact]
	public async Task AddFileToZip_Should_Add_Single_File_To_Archive()
	{
		// Arrange
		string fileName = fixture.Create<string>();
		await using MemoryStream fileStream = new(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
		await using MemoryStream memoryStream = new();

		// Act
		using (ZipArchive archive = new(memoryStream, ZipArchiveMode.Create, true))
		{
			await fileStream.AddFileToZip(archive, fileName, cancellationToken: Current.CancellationToken);
		}

		// Assert
		using ZipArchive readArchive = new(memoryStream, ZipArchiveMode.Read, true);
		readArchive.Entries.Count.ShouldBe(1);
		readArchive.Entries[0].Name.ShouldBe(fileName);
	}

	[Fact]
	public async Task AddFileToZip_Should_Handle_Null_Stream_Gracefully()
	{
		// Arrange
		Stream? fileStream = null;
		string fileName = fixture.Create<string>();
		await using MemoryStream memoryStream = new();

		// Act
		using (ZipArchive archive = new(memoryStream, ZipArchiveMode.Create, true))
		{
			await fileStream.AddFileToZip(archive, fileName, cancellationToken: Current.CancellationToken);
		}

		// Assert
		using ZipArchive readArchive = new(memoryStream, ZipArchiveMode.Read, true);
		readArchive.Entries.Count.ShouldBe(0);
	}

	[Theory]
	[InlineData(ECompressionType.Gzip)]
	[InlineData(ECompressionType.Brotli)]
	[InlineData(ECompressionType.Deflate)]
	[InlineData(ECompressionType.ZLib)]
	public async Task CompressFile_Should_Create_Compressed_File(ECompressionType compressionType)
	{
		// Arrange
		string tempInput = Path.GetTempFileName();
		string tempOutput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.cmp");
		try
		{
			byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
			await File.WriteAllBytesAsync(tempInput, data, Current.CancellationToken);

			// Act
			await CompressFile(tempInput, tempOutput, compressionType, Current.CancellationToken);

			// Assert
			File.Exists(tempOutput).ShouldBeTrue();
			new FileInfo(tempOutput).Length.ShouldBeGreaterThan(0);
		}
		finally
		{
			if (File.Exists(tempInput))
			{
				File.Delete(tempInput);
			}

			if (File.Exists(tempOutput))
			{
				File.Delete(tempOutput);
			}
		}
	}

	[Fact]
	public async Task CompressFile_Should_Throw_If_Input_File_Does_Not_Exist()
	{
		// Arrange
		string nonExistent = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.txt");
		string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.cmp");

		// Act & Assert
		FileNotFoundException ex = await Should.ThrowAsync<FileNotFoundException>(() => CompressFile(nonExistent, output, ECompressionType.Gzip));
		ex.Message.ShouldContain("Input file not found");
	}

	[Fact]
	public async Task CompressFile_Should_Create_Output_Directory_If_Not_Exists()
	{
		// Arrange
		string tempInput = Path.GetTempFileName();
		string outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
		string tempOutput = Path.Combine(outputDir, "out.cmp");
		try
		{
			await File.WriteAllTextAsync(tempInput, "test data", Current.CancellationToken);

			// Act
			await CompressFile(tempInput, tempOutput, ECompressionType.Gzip, Current.CancellationToken);

			// Assert
			File.Exists(tempOutput).ShouldBeTrue();
			Directory.Exists(outputDir).ShouldBeTrue();
		}
		finally
		{
			if (File.Exists(tempInput))
			{
				File.Delete(tempInput);
			}

			if (File.Exists(tempOutput))
			{
				File.Delete(tempOutput);
			}

			if (Directory.Exists(outputDir))
			{
				Directory.Delete(outputDir, true);
			}
		}
	}

	[Theory]
	[InlineData(ECompressionType.Gzip)]
	[InlineData(ECompressionType.Brotli)]
	[InlineData(ECompressionType.Deflate)]
	[InlineData(ECompressionType.ZLib)]
	public async Task DecompressFile_Should_Restore_Original_Data(ECompressionType compressionType)
	{
		// Arrange
		string tempInput = Path.GetTempFileName();
		string tempCompressed = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.cmp");
		string tempOutput = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.out");
		try
		{
			byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)i).ToArray();
			await File.WriteAllBytesAsync(tempInput, data, Current.CancellationToken);

			await CompressFile(tempInput, tempCompressed, compressionType, Current.CancellationToken);

			// Act
			await DecompressFile(tempCompressed, tempOutput, compressionType, Current.CancellationToken);

			// Assert
			File.Exists(tempOutput).ShouldBeTrue();
			byte[] decompressed = await File.ReadAllBytesAsync(tempOutput, Current.CancellationToken);
			decompressed.ShouldBe(data);
		}
		finally
		{
			if (File.Exists(tempInput))
			{
				File.Delete(tempInput);
			}

			if (File.Exists(tempCompressed))
			{
				File.Delete(tempCompressed);
			}

			if (File.Exists(tempOutput))
			{
				File.Delete(tempOutput);
			}
		}
	}

	[Fact]
	public async Task DecompressFile_Should_Throw_If_Compressed_File_Does_Not_Exist()
	{
		// Arrange
		string nonExistent = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.cmp");
		string output = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.out");

		// Act & Assert
		FileNotFoundException ex = await Should.ThrowAsync<FileNotFoundException>(() => DecompressFile(nonExistent, output, ECompressionType.Gzip));
		ex.Message.ShouldContain("Compressed file not found");
	}

	[Fact]
	public async Task DecompressFile_Should_Create_Output_Directory_If_Not_Exists()
	{
		// Arrange
		string tempInput = Path.GetTempFileName();
		string tempCompressed = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.cmp");
		string outputDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
		string tempOutput = Path.Combine(outputDir, "out.txt");
		try
		{
			await File.WriteAllTextAsync(tempInput, "test data", Current.CancellationToken);
			await CompressFile(tempInput, tempCompressed, ECompressionType.Gzip, Current.CancellationToken);

			// Act
			await DecompressFile(tempCompressed, tempOutput, ECompressionType.Gzip, Current.CancellationToken);

			// Assert
			File.Exists(tempOutput).ShouldBeTrue();
			Directory.Exists(outputDir).ShouldBeTrue();
		}
		finally
		{
			if (File.Exists(tempInput))
			{
				File.Delete(tempInput);
			}

			if (File.Exists(tempCompressed))
			{
				File.Delete(tempCompressed);
			}

			if (File.Exists(tempOutput))
			{
				File.Delete(tempOutput);
			}

			if (Directory.Exists(outputDir))
			{
				Directory.Delete(outputDir, true);
			}
		}
	}
}

public sealed class FileCompressionBehaviorTests
{




















	private sealed class CapabilityStream : Stream
	{
		private readonly MemoryStream inner = new();
		private readonly bool canRead;
		private readonly bool canSeek;
		private readonly bool canWrite;

		public CapabilityStream(byte[] data, bool canRead, bool canWrite, bool canSeek)
		{
			inner.Write(data);
			inner.Position = 0;
			this.canRead = canRead;
			this.canWrite = canWrite;
			this.canSeek = canSeek;
		}

		public override bool CanRead => canRead;
		public override bool CanSeek => canSeek;
		public override bool CanWrite => canWrite;
		public override long Length => inner.Length;
		public override long Position
		{
			get => canSeek ? inner.Position : throw new NotSupportedException();
			set
			{
				if (!canSeek)
				{
					throw new NotSupportedException();
				}

				inner.Position = value;
			}
		}

		public byte[] ToArray() => inner.ToArray();

		public override void Flush() => inner.Flush();

		public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
			inner.ReadAsync(buffer, cancellationToken);

		public override long Seek(long offset, SeekOrigin origin) =>
			canSeek ? inner.Seek(offset, origin) : throw new NotSupportedException();

		public override void SetLength(long value) => inner.SetLength(value);

		public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
			inner.WriteAsync(buffer, cancellationToken);

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	private sealed class GatedReadStream(byte[] data) : Stream
	{
		private readonly MemoryStream inner = new(data);
		private readonly TaskCompletionSource readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource releaseRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private bool waitForRelease = true;

		public Task ReadStarted => readStarted.Task;

		public void ReleaseRead() => releaseRead.TrySetResult();

		public override bool CanRead => true;
		public override bool CanSeek => true;
		public override bool CanWrite => false;
		public override long Length => inner.Length;
		public override long Position { get => inner.Position; set => inner.Position = value; }

		public override void Flush() { }

		public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

		public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			if (waitForRelease)
			{
				waitForRelease = false;
				readStarted.TrySetResult();
				await releaseRead.Task.WaitAsync(cancellationToken);
			}

			return await inner.ReadAsync(buffer, cancellationToken);
		}

		public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				inner.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	[Fact]
	public async Task ZipFiles_NonWritableDestination_ThrowsAndIdentifiesParameter()
	{
		using MemoryStream destination = new([], writable: false);

		ArgumentException exception = await Should.ThrowAsync<ArgumentException>(
			() => Array.Empty<(Stream?, string)>().ZipFiles(destination, cancellationToken: Current.CancellationToken));

		exception.ParamName.ShouldBe("zipFileStream");
		exception.Message.ShouldContain("must be writable");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ZipFiles_EmptyInput_ReturnsEmptyDestination(bool provideDestination)
	{
		(Stream?, string)[] files = [];

		MemoryStream destination = provideDestination ? new MemoryStream() : null!;
		MemoryStream result = provideDestination
			? await files.ZipFiles(destination, cancellationToken: Current.CancellationToken)
			: await files.ZipFiles(cancellationToken: Current.CancellationToken);

		await using (result)
		{
			result.Position.ShouldBe(0);
			result.Length.ShouldBe(0);
			if (provideDestination)
			{
				result.ShouldBeSameAs(destination);
			}
		}
	}

	[Fact]
	public async Task AddFileToZip_NullName_ThrowsWhenFallbackInspectsCreateModeArchive()
	{
		byte[] content = [1, 3, 5, 7];
		await using MemoryStream source = new(content);
		await using MemoryStream zip = new();

		await using ZipArchive archive = new(zip, ZipArchiveMode.Create, true);

		NotSupportedException exception = await Should.ThrowAsync<NotSupportedException>(
			() => source.AddFileToZip(archive, null!, cancellationToken: Current.CancellationToken));

		exception.Message.ShouldContain("Create mode");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ZipFiles_CanceledToken_PropagatesWithoutClaimingArchiveSuccess(bool provideDestination)
	{
		await using MemoryStream source = new([1, 2, 3]);
		await using MemoryStream destination = new();
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();
		(Stream?, string)[] files = [(source, "content.bin")];

		Func<Task> operation = provideDestination
			? () => files.ZipFiles(destination, cancellationToken: cancellation.Token)
			: async () => { await using MemoryStream _ = await files.ZipFiles(cancellationToken: cancellation.Token); };

		await Should.ThrowAsync<OperationCanceledException>(operation);
	}

	[Fact]
	public async Task AddFileToZip_CanceledToken_DoesNotCompleteEntry()
	{
		await using MemoryStream source = new([1, 2, 3]);
		await using MemoryStream zip = new();
		await using ZipArchive archive = new(zip, ZipArchiveMode.Create, true);
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Should.ThrowAsync<OperationCanceledException>(
			() => source.AddFileToZip(archive, "content.bin", cancellationToken: cancellation.Token));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ZipFiles_AsynchronousSourceRead_Completes(bool provideDestination)
	{
		byte[] content = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
		await using GatedReadStream source = new(content);
		await using MemoryStream destination = new();
		(Stream?, string)[] files = [(source, "content.bin")];
		Task<MemoryStream> operation = provideDestination
			? files.ZipFiles(destination, cancellationToken: Current.CancellationToken)
			: files.ZipFiles(cancellationToken: Current.CancellationToken);

		await source.ReadStarted;
		source.ReleaseRead();
		await using MemoryStream result = await operation;

		result.Position = 0;
		using ZipArchive archive = new(result, ZipArchiveMode.Read);
		ZipArchiveEntry entry = archive.Entries.ShouldHaveSingleItem();
		entry.FullName.ShouldBe("content.bin");
		await using Stream entryStream = entry.Open();
		using MemoryStream actual = new();
		await entryStream.CopyToAsync(actual, Current.CancellationToken);
		actual.ToArray().ShouldBe(content);
	}

	[Fact]
	public async Task CompressAndDecompressFile_DirectorylessOutputPaths_RoundTrip()
	{
		byte[] content = "directoryless output path"u8.ToArray();
		string inputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".input");
		string compressedName = Guid.NewGuid().ToString("N") + ".gzip";
		string outputName = Guid.NewGuid().ToString("N") + ".output";

		try
		{
			await File.WriteAllBytesAsync(inputPath, content, Current.CancellationToken);

			await CompressFile(inputPath, compressedName, ECompressionType.Gzip, Current.CancellationToken);
			await DecompressFile(compressedName, outputName, ECompressionType.Gzip, Current.CancellationToken);

			(await File.ReadAllBytesAsync(outputName, Current.CancellationToken)).ShouldBe(content);
		}
		finally
		{
			File.Delete(inputPath);
			File.Delete(compressedName);
			File.Delete(outputName);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FileCompression_CanceledToken_Propagates(bool compress)
	{
		string inputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".input");
		string outputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".output");
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		try
		{
			byte[] content = "cancellation"u8.ToArray();
			await File.WriteAllBytesAsync(inputPath, compress ? content : content.Compress(ECompressionType.Gzip), CancellationToken.None);

			Func<Task> operation = compress
				? () => CompressFile(inputPath, outputPath, ECompressionType.Gzip, cancellation.Token)
				: () => DecompressFile(inputPath, outputPath, ECompressionType.Gzip, cancellation.Token);

			await Should.ThrowAsync<OperationCanceledException>(operation);
		}
		finally
		{
			File.Delete(inputPath);
			File.Delete(outputPath);
		}
	}

}
