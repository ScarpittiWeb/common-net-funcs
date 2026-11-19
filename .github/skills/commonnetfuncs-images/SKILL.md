---
name: commonnetfuncs-images
description: 'Use CommonNetFuncs.Images for SkiaSharp image resizing, conversion, Base64, perceptual fingerprints, and external optimization.'
---

# CommonNetFuncs.Images

Use the SkiaSharp-based `Manipulation`, `Fingerprinting`, and `Base64` helpers
for images. `Optimizer` additionally runs external image optimization tools.
The current package README describes Office conversion instead of these APIs;
consult the source and tests for image-specific usage.

## Workflow

1. Supply a readable image stream and choose dimensions, format, and quality.
   `ResizeTo` returns an `SKBitmap` owned by the caller; dispose it. The
   output-stream overload leaves the supplied stream open and rewinds it when
   seekable.
2. Use `FingerprintImage` and `FindDuplicates` for perceptual similarity. The
   default algorithm is `DifferenceHash`; select a threshold appropriate to
   the application and verify matches.
3. For `Optimizer.OptimizeImage`, install the relevant `gifsicle`,
   `jpegoptim`, or `optipng` executable and choose trusted options. It replaces
   the original file only after a successful optimization.

## Safety and limits

- A perceptual image fingerprint deliberately groups visually similar images;
  it is not a cryptographic integrity hash or proof of authenticity.
- Limit image dimensions, decoded pixel count, file size, and processing time
  for untrusted uploads. Check supported formats and native SkiaSharp assets on
  the target platform.
- Optimizer calls an external process and may log and return for unsupported
  formats. Validate input paths/options and check the actual result; do not
  treat completion alone as proof that a file was optimized.

See [Manipulation.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Images/Manipulation.cs),
[Fingerprinting.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Images/Fingerprinting.cs), and
[Optimizer.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Images/Optimizer.cs).
