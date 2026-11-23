---
name: commonnetfuncs-ffmpeg
description: 'Use CommonNetFuncs.Ffmpeg for video conversion, FFmpeg/ffprobe metadata, progress, and hardware options. Consult for executable setup, process safety, and resource limits.'
---

# CommonNetFuncs.Ffmpeg

This package wraps Xabe.FFmpeg and raw FFmpeg/ffprobe processes. FFmpeg and
ffprobe binaries must be available and configured separately.

## Workflow

1. Select trusted executables, an existing input file, a writable output
   location, and the required codec, preset, hardware, and thread settings.
2. Use `ConversionTask.FfmpegConversionTask` or the appropriate raw conversion
   helper and inspect its returned `bool`; handle missing-file exceptions,
   cancellation, process errors, and logs.
3. Use the metadata/FPS helpers for progress or inspection. Bound concurrent
   conversions at the application level and clean up outputs according to the
   application's policy.

## Safety and limits

- Raw helpers build process arguments from paths and command strings. Do not
  concatenate untrusted options or arbitrary paths; use allow-listed codecs,
  constrained paths, and an isolated execution environment for untrusted media.
- Overwrite flags such as `-y` can replace files. Use an explicit overwrite
  policy and verify the intended destination before starting a process.
- The raw conversion helper returns `true` without running FFmpeg when the
  output already exists and overwrite is disabled. A `true` result alone does
  not prove that the existing file was produced from the current input or
  settings.
- `cancelIfLarger` compares output against source size; it is not a general
  disk, memory, or CPU quota. Set independent timeouts and resource limits.
- Conversion retries, partial files, and cancellation must not be interpreted
  as successful output without checking the result.

See [ConversionTask.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Ffmpeg/ConversionTask.cs),
[RawConversionTask.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Ffmpeg/FfmpegRawCalls/RawConversionTask.cs), and
the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Ffmpeg/README.md).
