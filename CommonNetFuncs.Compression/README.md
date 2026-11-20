# CommonNetFuncs.Compression

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Compression)](https://www.nuget.org/packages/CommonNetFuncs.Compression/)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Compression)](https://www.nuget.org/packages/CommonNetFuncs.Compression/)

Compression/decompression helpers for byte arrays, streams, files, and ZIP archives. The package targets `net11.0` and `netstandard2.1`.

Supported stream/byte algorithms are Brotli, GZip, Deflate, and ZLib. `ECompressionType.None` is a sentinel and causes the compression/decompression stream factories to throw `NotImplementedException`. ZIP creation is a separate archive operation.

## Contents

- [Installation](#installation)
- [Stream and byte-array API](#stream-and-byte-array-api)
  - [Explicit bounded copying](#explicit-bounded-copying)
- [Detection](#detection)
  - [`ConcatenatedStream`](#concatenatedstream)
  - [Limit exception](#limit-exception)
- [File and ZIP API](#file-and-zip-api)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Compression
```

```csharp
using System.IO.Compression;
using CommonNetFuncs.Compression;
```

## Stream and byte-array API

The `Streams` static class supplies the following extensions. Compression and stream-to-stream methods default to `CompressionLevel.Optimal`; asynchronous methods accept cancellation.

```csharp
byte[] input = "hello"u8.ToArray();
using var source = new MemoryStream(input);
using var compressed = new MemoryStream();

// Each stream-to-stream overload is shown explicitly.
source.CompressStream(compressed, Streams.ECompressionType.Gzip);
using var sourceForAsync = new MemoryStream(input);
using var compressedForAsync = new MemoryStream();
await sourceForAsync.CompressStreamAsync(
    compressedForAsync,
    Streams.ECompressionType.Gzip,
    CompressionLevel.SmallestSize,
    cancellationToken);

// Decompression rewinds a seekable compressed input, writes at the output's
// current position, flushes, and rewinds a seekable output. It leaves both
// caller-owned streams open.
using var restored = new MemoryStream();
compressed.DecompressStream(restored, Streams.ECompressionType.Gzip);
using var restoredAsync = new MemoryStream();
await compressedForAsync.DecompressStreamAsync(
    restoredAsync,
    Streams.ECompressionType.Gzip,
    cancellationToken);
```

The stream-wrapper overloads are write-only compression wrappers or read-only decompression wrappers, not streams containing eagerly copied data. They must be disposed to finish a compression stream. `leaveOpen` defaults to `false`, so disposing the wrapper normally disposes the underlying stream. For a compression wrapper, the receiver is the underlying destination; the implementation additionally requires that stream to report `CanRead`, so a write-only stream is rejected. Wrapper-returning decompression has no expansion-ratio limit.

```csharp
using var writableTarget = new MemoryStream();
using (Stream gzipWriter = writableTarget.Compress(
    Streams.ECompressionType.Gzip,
    CompressionLevel.Optimal,
    leaveOpen: true))
{
    gzipWriter.Write(input);
}

writableTarget.Position = 0;
using (Stream gzipReader = writableTarget.Decompress(
    Streams.ECompressionType.Gzip,
    leaveOpen: true))
{
    using var result = new MemoryStream();
    gzipReader.CopyTo(result);
}
```

Both byte-array overloads are demonstrated below.

```csharp
byte[] compressedSync = input.Compress(
    Streams.ECompressionType.ZLib,
    CompressionLevel.SmallestSize);
byte[] compressedAsync = await input.CompressAsync(
    Streams.ECompressionType.ZLib,
    CompressionLevel.SmallestSize,
    cancellationToken);

byte[] plainSync = compressedSync.Decompress(
    Streams.ECompressionType.ZLib,
    maxCompressionRatio: 100,
    cancellationToken);
byte[] plainAsync = await compressedAsync.DecompressAsync(
    Streams.ECompressionType.ZLib,
    maxCompressionRatio: 100,
    cancellationToken);
```

Byte-array decompression defaults to a maximum output of 1,000 times the compressed length and throws `CompressionLimitExceededException` when exceeded. `maxCompressionRatio` is not validated as positive by the implementation; pass a positive value. For untrusted data, also impose an absolute incoming/output size appropriate to the application.

Stream-to-stream decompression uses the same 1,000x ratio only when the compressed stream is seekable. For a non-seekable compressed stream the implementation uses `long.MaxValue`, so that path is not an effective decompression-bomb limit.

### Explicit bounded copying

```csharp
using var boundedTarget = new MemoryStream();
source.Position = 0;
source.CopyWithLimit(
    boundedTarget,
    maxBytes: 1_000_000,
    cancellationToken);

using var boundedTargetAsync = new MemoryStream();
sourceForAsync.Position = 0;
await sourceForAsync.CopyWithLimitAsync(
    boundedTargetAsync,
    maxBytes: 1_000_000,
    cancellationToken);
```

The methods copy from the current positions and leave streams open. If a read would make the copied total exceed `maxBytes`, they throw `CompressionLimitExceededException`. Because the sync method reads a complete chunk before testing, the destination can receive at most the bytes from earlier chunks, not a partial rejected chunk.

## Detection

```csharp
byte[] gzip = input.Compress(Streams.ECompressionType.Gzip);
using var gzipInput = new MemoryStream(gzip);
Streams.ECompressionType detected =
    await gzipInput.DetectCompressionType();

bool gzipBytes = gzip.IsGzipCompressed();
bool gzipStream = new MemoryStream(gzip).IsGzipCompressed();
byte[] zlib = input.Compress(Streams.ECompressionType.ZLib);
bool zlibBytes = zlib.IsZLibCompressed();
bool zlibStream = new MemoryStream(zlib).IsZLibCompressed();

// Brotli has no reliable fixed signature in normal framework output.
// These methods only recognize CE B2 CF 81 by convention, so use
// them only with data that promises that marker.
bool brotliBytes = new byte[] { 0xCE, 0xB2, 0xCF, 0x81 }.IsBrotliCompressed();
bool brotliStream = new MemoryStream(
    new byte[] { 0xCE, 0xB2, 0xCF, 0x81 }).IsBrotliCompressed();

bool deflate = await input.Compress(Streams.ECompressionType.Deflate)
    .IsDeflateCompressed();
```

Header checks return `false` for null/unreadable inputs. Seekable stream checks inspect from the current position and restore it. Non-seekable `IsGzipCompressed`, `IsZLibCompressed`, and `IsBrotliCompressed` checks consume bytes. `IsDeflateCompressed` attempts to read one decompressed byte; it is not a full-stream integrity check. `DetectCompressionType` consumes four bytes from a non-seekable stream and cannot recover them. Detection recognizes Deflate only by attempting decompression on a seekable stream/sample; `DetectCompressionType` returns `None` for Deflate on a non-seekable input.

Use `DetectCompressionTypeAndReset` when a non-seekable input must remain readable:

```csharp
(Streams.ECompressionType type, Stream replayable) =
    await Streams.DetectCompressionTypeAndReset(networkStream);

using (replayable)
using (Stream reader = replayable.Decompress(type, leaveOpen: false))
{
    // Consume reader from the beginning, including the eight-byte header
    // already read by the detector.
}
```

For a seekable input, it returns the original stream and restores its original position. For a non-seekable input, it returns a public `ConcatenatedStream` that reads the consumed header followed by the original stream.

### `ConcatenatedStream`

`ConcatenatedStream` is public, non-seekable, and read-only. Its constructor accepts two `Stream` instances and its synchronous/span/asynchronous `Read` operations read from the first and then the second. `Flush` is a no-op; `Length`, `Position`, `Seek`, `SetLength`, and `Write` throw `NotSupportedException`. Disposing it does not dispose either input stream because no ownership override is supplied.

```csharp
using Stream first = new MemoryStream([1, 2]);
using Stream second = new MemoryStream([3, 4]);
using var concatenated = new ConcatenatedStream(first, second);
byte[] output1 = new byte[2];
int count1 = concatenated.Read(output1, 0, output1.Length);
int count2;
{
    Span<byte> output2 = stackalloc byte[2];
    count2 = concatenated.Read(output2);
}
byte[] output3 = new byte[4];
int count3 = await concatenated.ReadAsync(
    output3, 0, output3.Length, cancellationToken);
byte[] output4 = new byte[4];
int count4 = await concatenated.ReadAsync(
    output4.AsMemory(), cancellationToken);
concatenated.Flush();
bool canRead = concatenated.CanRead;
bool canSeek = concatenated.CanSeek;
bool canWrite = concatenated.CanWrite;

// The unsupported members throw NotSupportedException.
_ = () => concatenated.Length;
_ = () => concatenated.Position;
_ = () => concatenated.Position = 0;
_ = () => concatenated.Seek(0, SeekOrigin.Begin);
_ = () => concatenated.SetLength(0);
_ = () => concatenated.Write([0], 0, 1);
```

### Limit exception

All three public constructors of `CompressionLimitExceededException` are available:

```csharp
Exception inner = new InvalidDataException();
CompressionLimitExceededException a = new();
CompressionLimitExceededException b = new("limit exceeded");
CompressionLimitExceededException c = new("limit exceeded", inner);
```

## File and ZIP API

ZIP helpers stream data into `ZipArchive`; they do not validate archive entry names, apply traversal protection, or encrypt content. `AddFileToZip` ignores a null file stream and resets a supplied seekable stream by assigning `Position = 0` (a non-seekable stream therefore fails); a null file name gets a generated `File n` entry name. ZIP helper outputs that contain at least one tuple are left positioned at zero. A truly empty `ZipFiles` sequence returns an empty stream rather than a ZIP central directory. Caller-owned source streams and archives are not disposed by these methods.

```csharp
using var one = new MemoryStream(input);
using var two = new MemoryStream(input);
(Stream? fileStream, string fileName) a = (one, "one.txt");
(Stream? fileStream, string fileName) b = (two, "folder/two.txt");

// Single-file overloads: supplied destination or newly allocated result.
await using var suppliedZip = new MemoryStream();
await a.ZipFile(
    suppliedZip,
    CompressionLevel.SmallestSize,
    cancellationToken);
await using MemoryStream allocatedZip = await a.ZipFile(
    CompressionLevel.SmallestSize,
    cancellationToken);

// Multiple-file overloads: supplied destination or newly allocated result.
await using var suppliedManyZip = new MemoryStream();
await new[] { a, b }.ZipFiles(
    suppliedManyZip,
    CompressionLevel.Optimal,
    cancellationToken);
await using MemoryStream allocatedManyZip = await new[] { a, b }.ZipFiles(
    CompressionLevel.Optimal,
    cancellationToken);

// Add to an existing archive.
using (var archiveStream = new MemoryStream())
{
    using (var archive = new ZipArchive(
        archiveStream,
        ZipArchiveMode.Create,
        leaveOpen: true))
    {
        await one.AddFileToZip(
            archive,
            "one.txt",
            CompressionLevel.Fastest,
            cancellationToken);
        await new[] { b }.AddFilesToZip(
            archive,
            CompressionLevel.Optimal,
            cancellationToken);
    }
}
```

For direct file-to-file compression, the input must exist. The output directory is created when needed, and `FileMode.Create` replaces/truncates an existing output. These helpers use `CompressionLevel.Optimal` and leave no `FileStream` open.

```csharp
await Files.CompressFile(
    "input.bin",
    "compressed/input.gz",
    Streams.ECompressionType.Gzip,
    cancellationToken);
await Files.DecompressFile(
    "compressed/input.gz",
    "restored/input.bin",
    Streams.ECompressionType.Gzip,
    cancellationToken);
```

`ECompressionType` members are `Brotli = 1`, `Gzip = 2`, `Deflate = 3`, `ZLib = 4`, and `None = 5`.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
