# CommonNetFuncs.Images

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Images)](https://www.nuget.org/packages/CommonNetFuncs.Images)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Images)](https://www.nuget.org/packages/CommonNetFuncs.Images/)

SkiaSharp-based image conversion, resizing, metadata inspection, Base64
handling, perceptual fingerprinting, and external-command image optimization.
The package targets `net11.0` and `netstandard2.1`.

## Contents

- [Installation](#installation)
- [Public types and options](#public-types-and-options)
- [Base64](#base64)
- [Image manipulation](#image-manipulation)
  - [Every synchronous resize overload](#every-synchronous-resize-overload)
  - [Every `ResizeTo` overload](#every-resizeto-overload)
  - [Every quality-reduction overload](#every-quality-reduction-overload)
  - [Format detection, metadata, conversion, and extension lookup](#format-detection-metadata-conversion-and-extension-lookup)
  - [Every asynchronous manipulation overload](#every-asynchronous-manipulation-overload)
- [Perceptual fingerprinting](#perceptual-fingerprinting)
- [External optimizer](#external-optimizer)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Images
```

Supported native/runtime assets must be present for the target platform.
`SkiaSharp` decides which formats can be decoded or encoded on that platform.
Unless stated otherwise, caller-provided streams remain open. For untrusted
uploads, impose file-size, decoded-dimension/pixel-count, and runtime limits
before invoking image processing.

## Public types and options

- `ImageSize(int width, int height)` provides `Width` and `Height`.
- `ResizeOptions` provides `Size` and `Mode`.
- `ResizeMode.Stretch` resizes to the exact requested dimensions (and can
  distort); `Max` preserves aspect ratio without upscaling; `Crop` scales to
  fill and center-crops.
- `ImageInfo` contains `Width`, `Height`, nullable `EncodedFormat`, and
  horizontal/vertical resolution. The metadata helpers currently populate
  resolution as `96.0`; that value is not a guarantee that embedded metadata was
  read.
- `ImageHashAlgorithm` has `AverageHash`, `DifferenceHash` (the default), and
  `PerceptualHash`.
- `ImageFingerprint(FilePath, Hash, Algorithm)` and
  `SimilarityResult(ImageA, ImageB, SimilarityPercent, IsDuplicate)` are
  records, so constructor/property/deconstruction/value equality behavior is
  available.

## Base64

`Base64` exposes `ConvertImageFileToBase64` overloads for a path or
`MemoryStream`, `ConvertImageFileToBase64Async` for a `MemoryStream`,
`ExtractBase64`, `RemoveImageVersionQuery`, synchronous/asynchronous
`ImageSaveToFile`, and `IsValidBase64Image`.

The path conversion reads the complete file; a missing path throws
`FileNotFoundException`. Stream conversion requires a readable nonempty
`MemoryStream` containing an image SkiaSharp can identify, rewinds a seekable
stream to zero, leaves it open, and returns `null` on logged failures. The async
variant is a `Task.FromResult` wrapper over the synchronous operation, not
asynchronous I/O.

`ExtractBase64` accepts raw Base64 image data and common
`data:image/...;base64,...`/CSS `base64...` forms, and validates the result as an
image. `RemoveImageVersionQuery` truncates at the first case-insensitive `?v=`.
`IsValidBase64Image` validates both Base64 syntax and decodable image bounds.

`ImageSaveToFile` and its async counterpart decode Base64 and re-encode with the
requested `SKEncodedImageFormat` and quality. Quality must be from `0` through
`100`; invalid quality throws `ArgumentOutOfRangeException`. Encoding, path, and
decode exceptions are logged and produce `false`. A `true` result means no
exception was thrown, so verify an output file was produced. Existing files are
opened for writing rather than replaced.

```csharp
using CommonNetFuncs.Images;
using SkiaSharp;

const string path = @"C:\images\input.png";

// Every Base64 method/overload.
string? fromPath = path.ConvertImageFileToBase64();

byte[] originalBytes = File.ReadAllBytes(path);
using var inputStream = new MemoryStream(originalBytes, writable: false);
string? fromMemory = inputStream.ConvertImageFileToBase64();
string? fromMemoryAsync = await inputStream.ConvertImageFileToBase64Async();

const string dataUri =
    "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB";
string? raw = dataUri.ExtractBase64();
bool valid = raw.IsValidBase64Image();

string? withoutVersion =
    "https://cdn.example/image.png?v=12&size=2".RemoveImageVersionQuery();
string? nullUrl = ((string?)null).RemoveImageVersionQuery(); // null is preserved

if (raw is not null)
{
    bool saved = raw.ImageSaveToFile(
        @"C:\images\out.jpg",
        SKEncodedImageFormat.Jpeg,
        quality: 85);

    bool savedAsync = await raw.ImageSaveToFileAsync(
        @"C:\images\out-async.png",
        SKEncodedImageFormat.Png,
        quality: 100);
}
```

## Image manipulation

The manipulation API exposes synchronous and offloaded asynchronous wrappers.
The async methods use `Task.Run`; they do not take cancellation tokens.
Operations return `bool` and normally log processing/decode/encode failures as
`false`. Invalid quality values are an explicit exception.

File-based overloads own only streams they open. Stream overloads leave supplied
streams open. Conversion/resize/reduction stream inputs are consumed from their
current position and seekable inputs are reset to zero afterward; detection
restores the original position; metadata inspection resets a seekable stream to
zero. Seekable outputs are rewound to zero, and non-seekable outputs retain no
seek semantics. In-memory overloads read the entire input stream. Outputs are
encoded only after processing completes.

Resize operations encode at quality `90`. `ResizeImage` infers a file output's
format from its extension when `outputFormat` is omitted. `ReduceImageQuality`
defaults to JPEG when no explicit output format is supplied, even if the input
is another format. Its quality range is `1` through `100`.

The dimension-based overloads use an exact target unless `useDimsAsMax` is true;
the `ResizeOptions` overloads additionally support `Stretch`, `Max`, and `Crop`.
A zero/negative dimension in `ReduceImageQuality` means no resize. A supplied
`mutate` callback returns the `SKBitmap` that will be encoded. Callers should
return a valid, non-disposed bitmap.

`ConvertImageFormat` encodes at quality `100`. Extension inference supports BMP,
JPEG/JPG, PNG, and WEBP; GIF and TIFF explicitly throw from
`GetImageFormatByExtension` because they are not supported for encoding by this
path.

### Every synchronous resize overload

```csharp
using CommonNetFuncs.Images;
using SkiaSharp;

const string inputPath = @"C:\images\input.png";
byte[] imageBytes = File.ReadAllBytes(inputPath);
var options = new ResizeOptions
{
    Size = new ImageSize(320, 240),
    Mode = ResizeMode.Max
};

// 1: file, dimensions
bool r1 = Manipulation.ResizeImage(
    inputPath, "r1.webp", 320, 240,
    SKEncodedImageFormat.Webp,
    new SKSamplingOptions(SKCubicResampler.Mitchell),
    useDimsAsMax: false,
    mutate: null);

// 2: file, ResizeOptions
bool r2 = Manipulation.ResizeImage(
    inputPath, "r2.jpg", options, SKEncodedImageFormat.Jpeg,
    useDimsAsMax: false, mutate: null);

// 3: stream, dimensions
using var r3In = File.OpenRead(inputPath);
using var r3Out = new MemoryStream();
bool r3 = Manipulation.ResizeImage(
    r3In, r3Out, 320, 240, SKEncodedImageFormat.Png,
    new SKSamplingOptions(SKCubicResampler.Mitchell),
    useDimsAsMax: true, mutate: null);

// 4: stream, ResizeOptions
using var r4In = File.OpenRead(inputPath);
using var r4Out = new MemoryStream();
bool r4 = Manipulation.ResizeImage(
    r4In, r4Out, options, SKEncodedImageFormat.Jpeg,
    useDimsAsMax: false, mutate: null);

// 5: span, dimensions
using var r5Out = new MemoryStream();
bool r5 = Manipulation.ResizeImage(
    imageBytes.AsSpan(), r5Out, 320, 240, SKEncodedImageFormat.Webp,
    new SKSamplingOptions(SKCubicResampler.Mitchell),
    useDimsAsMax: false, mutate: null);

// 6: span, ResizeOptions
using var r6Out = new MemoryStream();
bool r6 = Manipulation.ResizeImage(
    imageBytes.AsSpan(), r6Out, options, SKEncodedImageFormat.Png,
    useDimsAsMax: false, mutate: null);
```

### Every `ResizeTo` overload

```csharp
using CommonNetFuncs.Images;
using SkiaSharp;

const string sourcePath = @"C:\images\input.png";
using SKBitmap bitmap = SKBitmap.Decode(sourcePath);
using SKImage image = SKImage.FromBitmap(bitmap);

// Return values are caller-owned SKBitmap instances and must be disposed.
using FileStream sourceA = File.OpenRead(sourcePath);
using SKBitmap a = sourceA.ResizeTo(160, 120);

using var encodedOutput = new MemoryStream();
using FileStream sourceB = File.OpenRead(sourcePath);
using SKBitmap b = sourceB.ResizeTo(
    encodedOutput, 160, 120, SKEncodedImageFormat.Jpeg, 85);
// encodedOutput remains open and is rewound to zero.

using SKBitmap c = bitmap.ResizeTo(
    160, 120, SKCubicResampler.Mitchell);
using SKBitmap d = image.ResizeTo(
    160, 120, SKCubicResampler.Mitchell);
```

Each stream overload consumes its input from the current position; rewind a
seekable stream before reusing it.

### Every quality-reduction overload

The calls below demonstrate all twelve synchronous overloads. The distinction is
file/stream/span input, dimensions/`ResizeOptions`, and inferred/explicit format.

```csharp
using CommonNetFuncs.Images;
using SkiaSharp;

var resize = new ResizeOptions
{
    Size = new ImageSize(800, 600),
    Mode = ResizeMode.Crop
};
byte[] bytes = File.ReadAllBytes(@"C:\images\input.png");

bool q1 = Manipulation.ReduceImageQuality(
    @"C:\images\input.png", "q1.jpg", 70, 800, 600);
bool q2 = Manipulation.ReduceImageQuality(
    @"C:\images\input.png", "q2.jpg", 70, resize);
bool q3 = Manipulation.ReduceImageQuality(
    @"C:\images\input.png", "q3.webp",
    SKEncodedImageFormat.Webp, 70, 800, 600);
bool q4 = Manipulation.ReduceImageQuality(
    @"C:\images\input.png", "q4.webp",
    SKEncodedImageFormat.Webp, 70, resize);

using var s1 = File.OpenRead(@"C:\images\input.png");
using var o1 = new MemoryStream();
bool q5 = Manipulation.ReduceImageQuality(s1, o1, 70, 800, 600);

using var s2 = File.OpenRead(@"C:\images\input.png");
using var o2 = new MemoryStream();
bool q6 = Manipulation.ReduceImageQuality(s2, o2, 70, resize);

using var s3 = File.OpenRead(@"C:\images\input.png");
using var o3 = new MemoryStream();
bool q7 = Manipulation.ReduceImageQuality(
    s3, o3, SKEncodedImageFormat.Webp, 70, 800, 600);

using var s4 = File.OpenRead(@"C:\images\input.png");
using var o4 = new MemoryStream();
bool q8 = Manipulation.ReduceImageQuality(
    s4, o4, SKEncodedImageFormat.Webp, 70, resize);

using var o5 = new MemoryStream();
bool q9 = Manipulation.ReduceImageQuality(
    bytes.AsSpan(), o5, 70, 800, 600);
using var o6 = new MemoryStream();
bool q10 = Manipulation.ReduceImageQuality(
    bytes.AsSpan(), o6, 70, resize);
using var o7 = new MemoryStream();
bool q11 = Manipulation.ReduceImageQuality(
    bytes.AsSpan(), o7, SKEncodedImageFormat.Webp, 70, 800, 600);
using var o8 = new MemoryStream();
bool q12 = Manipulation.ReduceImageQuality(
    bytes.AsSpan(), o8, SKEncodedImageFormat.Webp, 70, resize);
```

### Format detection, metadata, conversion, and extension lookup

```csharp
using CommonNetFuncs.Images;
using SkiaSharp;

byte[] data = File.ReadAllBytes(@"C:\images\input.png");
using var stream = new MemoryStream(data);

// Three synchronous detection overloads.
bool d1 = Manipulation.TryDetectImageType(
    @"C:\images\input.png", out SKEncodedImageFormat? f1);
bool d2 = Manipulation.TryDetectImageType(
    stream, out SKEncodedImageFormat? f2);
bool d3 = Manipulation.TryDetectImageType(
    data.AsSpan(), out SKEncodedImageFormat? f3);

// Three synchronous metadata overloads.
bool m1 = Manipulation.TryGetMetadata(
    @"C:\images\input.png", out ImageInfo i1);
stream.Position = 0;
bool m2 = Manipulation.TryGetMetadata(stream, out ImageInfo i2);
bool m3 = Manipulation.TryGetMetadata(data.AsSpan(), out ImageInfo i3);

// Four synchronous conversion overloads.
bool c1 = Manipulation.ConvertImageFormat(
    @"C:\images\input.png", @"C:\images\output.webp");
bool c2 = Manipulation.ConvertImageFormat(
    @"C:\images\input.png", @"C:\images\output.jpg",
    SKEncodedImageFormat.Jpeg);

stream.Position = 0;
using var convertedStream = new MemoryStream();
bool c3 = Manipulation.ConvertImageFormat(
    stream, convertedStream, SKEncodedImageFormat.Png);

using var convertedSpanStream = new MemoryStream();
bool c4 = Manipulation.ConvertImageFormat(
    data.AsSpan(), convertedSpanStream, SKEncodedImageFormat.Webp);

SKEncodedImageFormat png = Manipulation.GetImageFormatByExtension(".png");
SKEncodedImageFormat jpg = Manipulation.GetImageFormatByExtension("jpg");
```

### Every asynchronous manipulation overload

```csharp
using CommonNetFuncs.Images;
using SkiaSharp;

const string sourcePath = @"C:\images\input.png";
var resizeOptions = new ResizeOptions
{
    Size = new ImageSize(640, 480),
    Mode = ResizeMode.Max
};

// Four ResizeImageAsync overloads.
bool ar1 = await Manipulation.ResizeImageAsync(
    sourcePath, "ar1.jpg", 640, 480, SKEncodedImageFormat.Jpeg);
bool ar2 = await Manipulation.ResizeImageAsync(
    sourcePath, "ar2.jpg", resizeOptions, SKEncodedImageFormat.Jpeg);
using (var ai1 = File.OpenRead(sourcePath))
using (var ao1 = new MemoryStream())
{
    bool ar3 = await Manipulation.ResizeImageAsync(
        ai1, ao1, 640, 480, SKEncodedImageFormat.Png);
}
using (var ai2 = File.OpenRead(sourcePath))
using (var ao2 = new MemoryStream())
{
    bool ar4 = await Manipulation.ResizeImageAsync(
        ai2, ao2, resizeOptions, SKEncodedImageFormat.Png);
}

// Eight ReduceImageQualityAsync overloads.
bool aq1 = await Manipulation.ReduceImageQualityAsync(
    sourcePath, "aq1.jpg", 60, 640, 480);
bool aq2 = await Manipulation.ReduceImageQualityAsync(
    sourcePath, "aq2.jpg", 60, resizeOptions);
bool aq3 = await Manipulation.ReduceImageQualityAsync(
    sourcePath, "aq3.webp", SKEncodedImageFormat.Webp, 60, 640, 480);
bool aq4 = await Manipulation.ReduceImageQualityAsync(
    sourcePath, "aq4.webp", SKEncodedImageFormat.Webp, 60, resizeOptions);

using (var qi1 = File.OpenRead(sourcePath))
using (var qo1 = new MemoryStream())
{
    bool aq5 = await Manipulation.ReduceImageQualityAsync(
        qi1, qo1, 60, 640, 480);
}
using (var qi2 = File.OpenRead(sourcePath))
using (var qo2 = new MemoryStream())
{
    bool aq6 = await Manipulation.ReduceImageQualityAsync(
        qi2, qo2, 60, resizeOptions);
}
using (var qi3 = File.OpenRead(sourcePath))
using (var qo3 = new MemoryStream())
{
    bool aq7 = await Manipulation.ReduceImageQualityAsync(
        qi3, qo3, SKEncodedImageFormat.Webp, 60, 640, 480);
}
using (var qi4 = File.OpenRead(sourcePath))
using (var qo4 = new MemoryStream())
{
    bool aq8 = await Manipulation.ReduceImageQualityAsync(
        qi4, qo4, SKEncodedImageFormat.Webp, 60, resizeOptions);
}

// Two detection and two metadata async overloads.
SKEncodedImageFormat? ad1 =
    await Manipulation.TryDetectImageTypeAsync(sourcePath);
using var detectStream = File.OpenRead(sourcePath);
SKEncodedImageFormat? ad2 =
    await Manipulation.TryDetectImageTypeAsync(detectStream);

ImageInfo? am1 = await Manipulation.TryGetMetadataAsync(sourcePath);
using var metadataStream = File.OpenRead(sourcePath);
ImageInfo? am2 = await Manipulation.TryGetMetadataAsync(metadataStream);

// Three ConvertImageFormatAsync overloads.
bool ac1 = await Manipulation.ConvertImageFormatAsync(
    sourcePath, "ac1.png");
bool ac2 = await Manipulation.ConvertImageFormatAsync(
    sourcePath, "ac2.webp", SKEncodedImageFormat.Webp);
using var convertInput = File.OpenRead(sourcePath);
using var convertOutput = new MemoryStream();
bool ac3 = await Manipulation.ConvertImageFormatAsync(
    convertInput, convertOutput, SKEncodedImageFormat.Png);
```

## Perceptual fingerprinting

The fingerprint is a 64-bit perceptual hash, not a cryptographic hash or proof
of authenticity. Similarity is the percentage of equal bits (Hamming
similarity). The default duplicate threshold is `90.0m`, but suitable values are
application-dependent.

- File fingerprinting is asynchronous and opens its own read stream.
- Stream and `ReadOnlySpan<byte>` fingerprinting is synchronous.
- The `SKBitmap` overload does not dispose the source bitmap.
- Invalid/missing files and undecodable data throw rather than return a null
  fingerprint.
- Directory scanning considers `.jpg`, `.jpeg`, `.png`, `.bmp`, `.gif`, and
  `.webp` files, optionally recursively. Unreadable files are skipped with
  console warnings. Results are an ordered list of successfully decoded files.
- Comparing fingerprints produced by different algorithms throws
  `InvalidOperationException`.
- `FindDuplicates` returns only qualifying pairs, ordered by descending
  similarity. `FindDuplicatesInDirectory` currently always uses the internal
  default threshold (`90.0m`); it has no threshold parameter.

```csharp
using CommonNetFuncs.Images;
using SkiaSharp;

const string imagePath = @"C:\images\input.png";

// Every FingerprintImage overload.
ImageFingerprint f1 = await imagePath.FingerprintImage(
    ImageHashAlgorithm.DifferenceHash);

using var stream = File.OpenRead(imagePath);
ImageFingerprint f2 = stream.FingerprintImage(
    label: "stream-example",
    algorithm: ImageHashAlgorithm.AverageHash);

byte[] fingerprintBytes = File.ReadAllBytes(imagePath);
ImageFingerprint f3 = fingerprintBytes.AsSpan().FingerprintImage(
    label: "span-example",
    algorithm: ImageHashAlgorithm.PerceptualHash);

using SKBitmap bitmap = SKBitmap.Decode(imagePath);
ImageFingerprint f4 = bitmap.FingerprintImage(
    label: "bitmap-example",
    algorithm: ImageHashAlgorithm.DifferenceHash);

IReadOnlyList<ImageFingerprint> directoryFingerprints =
    await ImageFingerprinting.FingerprintDirectory(
        @"C:\images",
        recursive: true,
        algorithm: ImageHashAlgorithm.DifferenceHash);

SimilarityResult comparison = ImageFingerprinting.Compare(
    f1, f4, duplicateThreshold: 95.0m);

IReadOnlyList<SimilarityResult> pairs =
    ImageFingerprinting.FindDuplicates(
        directoryFingerprints, duplicateThreshold: 90.0m);

IReadOnlyList<SimilarityResult> directoryPairs =
    await ImageFingerprinting.FindDuplicatesInDirectory(
        @"C:\images",
        recursive: true,
        algorithm: ImageHashAlgorithm.DifferenceHash);

double hammingSimilarity =
    ImageFingerprinting.HammingSimilarity(f1.Hash, f4.Hash);

// Public record constructors/properties are usable directly.
var customFingerprint = new ImageFingerprint(
    "example.png", 123UL, ImageHashAlgorithm.AverageHash);
var customResult = new SimilarityResult(
    customFingerprint.FilePath, "other.png", 98.5, isDuplicate: true);
var (fileA, fileB, percent, duplicate) = customResult;
```

## External optimizer

`Optimizer.OptimizeImage` is the package's only optimizer method. It works
in-place: it copies the source to a temporary file, invokes an external
optimizer, and replaces the original only after a successful process exit. The
method returns `Task` and therefore no success value; verify the file afterward.

- `.gif` requires `gifsicle`; default arguments are `-b -O3 <temp-file>`.
- `.jpg`/`.jpeg` requires `jpegoptim`; defaults are
  `--preserve-perms --preserve <temp-file>`.
- `.png`, `.bmp`, `.pnm`, and `.tiff` require `optipng`; defaults are
  `-fix -o5 <temp-file>`.
- Custom argument enumerables are accepted for each tool. The temporary working
  file path is appended unless already present.
- Missing input throws `FileNotFoundException`. Unsupported extensions log a
  warning and return. Missing executables/process-start and cancellation errors
  are not converted to a Boolean result. Nonzero process exits leave the
  original file unchanged.
- Validate paths and command arguments. Do not pass untrusted arguments to
  external programs.

```csharp
using CommonNetFuncs.Images;

await Optimizer.OptimizeImage(
    @"C:\images\photo.jpg",
    gifsicleArgs: null,
    jpegoptimArgs: ["--strip-all", "--max=85"],
    optipngArgs: null,
    cancellationToken: CancellationToken.None);
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
