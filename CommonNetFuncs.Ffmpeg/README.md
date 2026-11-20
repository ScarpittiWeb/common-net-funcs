# CommonNetFuncs.Ffmpeg

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Ffmpeg)](https://www.nuget.org/packages/CommonNetFuncs.Ffmpeg/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Ffmpeg)](https://www.nuget.org/packages/CommonNetFuncs.Ffmpeg/)

Xabe.FFmpeg wrappers, raw `ffmpeg`/`ffprobe` process helpers, progress/FPS parsing, and media-inspection helpers. The package targets `net11.0` and `netstandard2.1`.

## Contents

- [Installation](#installation)
- [High-level `ConversionTask`](#high-level-conversiontask)
- [Raw process conversion](#raw-process-conversion)
- [Media, FPS, and result helpers](#media-fps-and-result-helpers)
- [Hardware checks and public types](#hardware-checks-and-public-types)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Ffmpeg
```

The package does not bundle FFmpeg. Configure Xabe's executable directory for Xabe-based calls (for example `FFmpeg.SetExecutablesPath(...)`). Raw calls invoke `ffmpeg`/`ffprobe` by name from `PATH`, except that hardware encoder detection uses `/usr/bin/ffmpeg` on Linux. Verify executable trust, versions, codecs, and writable output paths before converting.

## High-level `ConversionTask`

`ConversionTask.FfmpegConversionTask` has a Xabe-settings overload and a command-string overload:

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using CommonNetFuncs.Ffmpeg;
using Xabe.FFmpeg;

FFmpeg.SetExecutablesPath(@"C:\ffmpeg\bin");

FileInfo input = new(@"C:\media\source.mp4");
ConcurrentDictionary<int, decimal> fps = new();
ConcurrentBag<string> results = new();
using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(10));
IMediaInfo media = await FFmpeg.GetMediaInfo(input.FullName);

HardwareAccelerationValues hardware = new()
{
    hardwareAccelerator = HardwareAccelerator.auto,
    decoder = VideoCodec.h264,
    encoder = VideoCodec.h264_nvenc,
    device = 0
};

bool settingsResult = await ConversionTask.FfmpegConversionTask(
    input,
    "output.mp4",
    VideoCodec.libx264,
    Format.mp4,
    ConversionPreset.Medium,
    workingPath: @"C:\media\converted",
    conversionIndex: 1,
    fpsDict: fps,
    mediaInfo: media,
    numberOfThreads: 2,
    cancelIfLarger: true,
    taskDescription: "archive",
    strict: true,
    overwriteOutput: true,
    processPriority: ProcessPriorityClass.BelowNormal,
    hardwareAccelerationValues: hardware,
    conversionOutputs: results,
    additionalLogText: "batch-7",
    cancellationTokenSource: cancellation);

bool commandResult = await ConversionTask.FfmpegConversionTask(
    input,
    "output-copy.mp4",
    "-c:v libx264 -preset medium -c:a copy -movflags faststart -y",
    workingPath: @"C:\media\converted",
    conversionIndex: 2,
    fpsDict: fps,
    mediaInfo: media,
    numberOfThreads: 2,
    cancelIfLarger: true,
    taskDescription: "command",
    strict: false,
    overwriteOutput: true,
    processPriority: ProcessPriorityClass.Normal,
    hardwareAccelerationValues: null,
    conversionOutputs: results,
    additionalLogText: "batch-7",
    cancellationTokenSource: cancellation);
```

Both overloads obtain `IMediaInfo` when it is not supplied, select the first video and audio streams, set the output under `workingPath` or the system temp directory, and attach Xabe progress/data handlers. They support concurrent FPS aggregation through a caller-owned dictionary and remove their index in `finally`. The current implementation calls Xabe's hardware-acceleration setup twice when values are supplied. The `HardwareAccelerationValues.device` property exists but is not passed to Xabe by this implementation; only accelerator/decoder/encoder are used.

`-strict -2` is appended when `strict` is true. `cancelIfLarger` watches parsed `size=...kB` progress data and cancels when that reported output is larger than the source. It is not a storage/memory/CPU quota and can miss output that is not represented by the expected progress format. Cancellation after a conversion starts is logged and returns `false`; cancellation while probing is handled for selected exception types.

The method's Boolean is not a complete output validation: a `ConversionException` is logged in the outer catch without setting the failure flag, and can consequently still result in `true`. Other unhandled failures may propagate. Verify the process/output independently when correctness is important. Progress is logged through NLog; this library does not expose a progress event or write a console result.

## Raw process conversion

The raw Xabe-to-command overload builds a full command and starts a real process:

```csharp
using CommonNetFuncs.Ffmpeg.FfmpegRawCalls;

bool rawXabeResult = await RawConversionTask.FfmpegConversionTaskFromXabe(
    input, "raw-output.mp4", VideoCodec.libx264,
    overwriteExisting: true,
    Format.mp4, ConversionPreset.Medium,
    workingPath: @"C:\media\converted",
    conversionIndex: 3,
    fpsDict: fps,
    mediaInfo: media,
    numberOfThreads: 2,
    cancelIfLarger: true,
    taskDescription: "raw xabe",
    strict: true,
    processPriority: ProcessPriorityClass.BelowNormal,
    hardwareAccelerationValues: hardware,
    conversionOutputs: results,
    additionalLogText: "batch-7",
    cancellationTokenSource: cancellation);
```

The command builder itself is public:

```csharp
string command = await RawConversionTask.GetConversionCommandFromXabe(
    input,
    "preview.mp4",
    VideoCodec.libx264,
    Format.mp4,
    ConversionPreset.Medium,
    workingPath: @"C:\media\converted",
    mediaInfo: media,
    numberOfThreads: 2,
    strict: true,
    overwriteOutput: false,
    processPriority: ProcessPriorityClass.BelowNormal,
    hardwareAccelerationValues: null);
```

It returns Xabe's complete command line, including executable/input/output information. When hardware values are supplied, the method does not apply the explicitly supplied codec to the video stream; Xabe hardware configuration is expected to determine it.

The direct raw-process overload accepts arguments *without* the executable/input/output:

```csharp
bool rawCommandResult = await RawConversionTask.FfmpegConversionTask(
    input,
    "direct-output.mp4",
    "-c:v libx264 -preset medium -c:a copy -movflags faststart",
    overwriteExisting: true,
    workingPath: @"C:\media\converted",
    conversionIndex: 4,
    fpsDict: fps,
    cancelIfLarger: true,
    taskDescription: "raw command",
    conversionOutputs: results,
    additionalLogText: "batch-7",
    processPriority: ProcessPriorityClass.BelowNormal,
    cancellationTokenSource: cancellation);
```

It prepends `-y` when overwriting, prepends `-i "input"`, and appends `"outputPath"`. It then starts `ffmpeg` from `PATH`, parses stderr for progress, attempts to set process priority, and returns false for a nonzero exit code, caught exception, or cancellation. If `overwriteExisting` is false and the output already exists, it returns `true` without invoking FFmpeg; that result says nothing about whether the existing file matches the requested source/options.

Raw arguments and paths are incorporated into a process command line. Use allow-listed options and trusted/constrained paths—do not accept arbitrary user command text. Use an isolated execution environment and resource/time limits for untrusted media.

`WaitHandleExtensions.WaitOneAsync` is also public and adapts an OS wait handle to a task:

```csharp
using WaitHandle signal = new ManualResetEvent(initialState: false);
Task<bool> signaledTask = signal.WaitOneAsync();
((ManualResetEvent)signal).Set();
bool signaled = await signaledTask; // true if the wait completed without timing out
```

Its wait is infinite; it has no cancellation or timeout parameter.

## Media, FPS, and result helpers

```csharp
ConcurrentDictionary<int, decimal> activeFps = new();
activeFps[1] = 24.5m;
activeFps[2] = 30m;
decimal total = Helpers.GetTotalFps(activeFps); // 54.5

decimal parsedFps = "frame= 48 fps=5.8 q=0.0".ParseFfmpegLogFps();
// Invalid/unparseable lines are logged and return -1.

ConcurrentBag<string> conversionOutputs = new();
await Helpers.RecordResults(
    "source.mp4", success: true, conversionOutputs,
    originalSize: 2_000_000, endSize: 1_500_000);

await Helpers.RecordResults(
    "source-2.mp4", success: false, conversionOutputs,
    logFile: @"C:\logs\conversions.txt",
    originalSize: 4_000_000, endSize: 0);

string aggregateDifference = Helpers.GetTotalFileDif(conversionOutputs);
```

`RecordResults` has two overloads. Both append a string with `FileName`, `Success`, unit-formatted `OriginalSize`, and `EndSize`. `SizeRatio` and `SizeDif` are present only when both byte counts are provided. The log-file overload creates/opens the file and appends the same line. Results containing `False` are counted by `GetTotalFileDif` too; the method sums the parsed `SizeDif` tags and formats the total in binary units. Missing/null sizes do not add a `SizeDif`.

`Constants.EOutputTags` contains the six public tag names: `FileName`, `Success`, `OriginalSize`, `EndSize`, `SizeRatio`, and `SizeDif`.

```csharp
string? codec = await @"C:\media\source.mp4"
    .GetVideoMetadata(Helpers.EVideoMetadata.Codec_Name);
decimal frameRate = await @"C:\media\source.mp4".GetFrameRate();
decimal completeSpacing =
    await @"C:\media\source.mp4".GetKeyFrameSpacing();
decimal sampledSpacing =
    await @"C:\media\source.mp4".GetKeyFrameSpacing(
        numberOfSamples: 20, sampleLengthSec: 30);
```

`GetVideoMetadata` invokes Xabe `ffprobe` and supports every `Helpers.EVideoMetadata` member (stream index, codec/profile/type/tags, dimensions, aspect ratios/pixel/color fields, rates, timestamps, durations, bit rates, and frame/packet counts). It validates nonblank/existing paths, but catches/logs errors and returns `null`.

`GetFrameRate` parses `Avg_Frame_Rate` as a ratio, rounds to two decimals away from zero, and returns `-1` on failure. The no-argument `GetKeyFrameSpacing` scans the whole file. The explicit overload scans the whole file when `numberOfSamples` is `-1`; otherwise it samples intervals (a nonpositive `sampleLengthSec` becomes 10 seconds). It returns the average frame count between parsed keyframes, or `-1` when no average can be produced.

`LogFfmpegOutput` is public because the Xabe and raw process paths share it:

```csharp
using System.Diagnostics;

DateTime lastLine = DateTime.UtcNow.AddSeconds(-6);
DateTime lastSummary = DateTime.UtcNow.AddSeconds(-31);
bool failed = false;
bool tooLarge = false;
using Process process = new();
process.StartInfo = new ProcessStartInfo("ffmpeg")
{
    RedirectStandardError = true,
    UseShellExecute = false
};
process.ErrorDataReceived += (_, args) =>
    args.LogFfmpegOutput(
        ref lastLine, ref lastSummary, ref failed, ref tooLarge,
        input, TimeSpan.FromMinutes(5),
        conversionIndex: 8, cancelIfLarger: true,
        taskDescription: "worker", additionalLogText: "batch",
        conversionOutputs, activeFps, cancellation);
```

It throttles parsing/logging, updates the FPS dictionary, calculates a best-effort ETA from FFmpeg `time`/`speed`, and may cancel the supplied source when parsed output size exceeds the source file. It expects standard FFmpeg progress text and is not a general log parser. `failed`/`tooLarge` are caller-owned flags modified through `ref`.

## Hardware checks and public types

```csharp
bool anyHardware = await Helpers.IsAnyHardwareAcceleratorAvailable();
bool nvencListed = await Helpers.CheckHardwareEncoderByName("h264_nvenc");
```

`IsAnyHardwareAcceleratorAvailable` short-circuits through `Helpers.EHwAccelerator` values: `h264_nvenc`, `h264_qsv`, `h264_amf`, `h264_vaapi`, `h264_vdpau`, `h264_vulkan`, and `h264_videotoolbox`. These checks inspect the encoder listing from `ffmpeg -encoders`; they do not verify a usable device, driver, decode path, or successful encode. Blank encoder names, process startup failures, and exceptions return `false`.

`Helpers.EVideoMetadata` values are `Index`, `Codec_Name`, `Codec_Long_Name`, `Profile`, `Codec_Type`, `Codec_Tag_String`, `Codec_Tag`, `Width`, `Height`, `Coded_Width`, `Coded_Height`, `Closed_Captions`, `Film_Grain`, `Has_B_Frames`, `Sample_Aspect_Ratio`, `Display_Aspect_Ratio`, `Pix_Fmt`, `Level`, `Color_Range`, `Color_Space`, `Color_Transformer`, `Color_Primaries`, `Chroma_Location`, `Field_Order`, `Refs`, `Id`, `R_Frame_Rate`, `Avg_Frame_Rate`, `Time_Base`, `Start_Pts`, `Start_Time`, `Duration_Ts`, `Duration`, `Bit_Rate`, `Max_Bit_Rate`, `Bits_Per_Raw_Sample`, `Nb_Frames`, `Nb_Read_Frames`, `Nb_Read_Packets`, and `Extradata_Size`.

`FfmpegRawCalls.CodecType` is a public enum with `Video`, `Audio`, and `Subtitle`; raw internal media-info classes that classify streams are not part of the public package API.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
