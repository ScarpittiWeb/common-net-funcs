# CommonNetFuncs.Office.Common

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Office.Common)](https://www.nuget.org/packages/CommonNetFuncs.Office.Common/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Office.Common)](https://www.nuget.org/packages/CommonNetFuncs.Office.Common/)

Disk-to-disk Office document conversion to PDF by launching an external
LibreOffice process. The package targets `net11.0` and `netstandard2.1`.

## Contents

- [Installation](#installation)
- [Public API](#public-api)
- [Behavior and requirements](#behavior-and-requirements)
- [Usage examples for every method and exception constructor](#usage-examples-for-every-method-and-exception-constructor)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Office.Common
```

## Public API

`CommonNetFuncs.Office.Common.PdfConversion` exposes:

```csharp
void ConvertToPdf(
    string libreOfficeExecutable,
    string fileName,
    string? outputPath = null,
    TimeSpan? conversionTimeout = null,
    int maxRetries = 3,
    bool overwriteExistingFile = false);

Task ConvertToPdfAsync(
    string libreOfficeExecutable,
    string fileName,
    string? outputPath = null,
    int maxRetries = 3,
    bool overwriteExistingFile = false,
    CancellationToken? cancellationToken = null);
```

It also exposes the nested
`PdfConversion.LibreOfficeFailedException` exception with parameterless,
message, and message-plus-inner-exception constructors.

## Behavior and requirements

- LibreOffice must already be installed. `libreOfficeExecutable` is a trusted
  path or executable alias accepted by `ProcessStartInfo`.
- Supported case-insensitive source extensions are `.xlsx`, `.xls`, `.csv`,
  `.docx`, `.doc`, `.pptx`, and `.ppt`. Calc, Writer, and Impress export filters
  are selected from the extension. An unsupported extension throws
  `ArgumentException`.
- A missing input throws `FileNotFoundException`.
- `fileName` is the complete input file path. `outputPath`, despite its name, is
  an **output directory**, not a PDF filename. It defaults to the input
  file's directory. The generated filename is the source basename plus `.pdf`
  (for example, `proposal.docx` becomes `proposal.pdf`).
- The implementation copies the source into the system temporary directory,
  invokes LibreOffice there, moves a produced PDF to the requested directory,
  and cleans up its temporary files. The original input is not modified. The
  API takes paths, not streams.
- `maxRetries` means retries after the initial attempt, so the intended maximum
  number of process attempts is `maxRetries + 1`. The default is `3`.
- The synchronous overload waits indefinitely when `conversionTimeout` is
  `null`. A timeout kills the process and raises `LibreOfficeFailedException`.
- The asynchronous overload has no timeout argument; pass a cancellation token
  to stop waiting. Cancellation is wrapped in `LibreOfficeFailedException`.
  Async conversions are serialized by a process-wide semaphore.
- `overwriteExistingFile` defaults to `false`. Moving onto an existing output
  fails and is wrapped; `true` permits replacement.
- Process start, nonzero exit, filesystem, cancellation, timeout, and move
  failures are generally wrapped in `LibreOfficeFailedException`. Pre-flight
  missing-file and unsupported-extension checks occur before the wrapper.
- Both conversion methods return no success value. An exit code of zero does not
  replace verification: check that the expected PDF exists and is valid. In
  particular, the move step only occurs when LibreOffice produced the expected
  temporary PDF.

LibreOffice parses the supplied document in a separate process. Restrict file
size, runtime, input/output locations, executable selection, and macro/content
trust; isolate conversion of untrusted documents.

## Usage examples for every method and exception constructor

```csharp
using CommonNetFuncs.Office.Common;
const string libreOffice = @"C:\Program Files\LibreOffice\program\soffice.com";
const string input = @"C:\work\incoming\quarterly-report.xlsx";
const string outputDirectory = @"C:\work\pdf";

// Synchronous overload. Includes its timeout, retry, and overwrite options.
PdfConversion.ConvertToPdf(
    libreOffice,
    input,
    outputDirectory,
    conversionTimeout: TimeSpan.FromMinutes(2),
    maxRetries: 1,
    overwriteExistingFile: true);

string expectedPdf = Path.Combine(
    outputDirectory,
    $"{Path.GetFileNameWithoutExtension(input)}.pdf");
if (!File.Exists(expectedPdf))
    throw new IOException($"LibreOffice did not create {expectedPdf}.");

// Async overload. Cancellation replaces the synchronous timeout parameter.
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await PdfConversion.ConvertToPdfAsync(
    libreOffice,
    input,
    outputDirectory,
    maxRetries: 1,
    overwriteExistingFile: true,
    cancellationToken: cancellation.Token);

if (!File.Exists(expectedPdf))
    throw new IOException($"LibreOffice did not create {expectedPdf}.");

// All three public exception constructors are available to callers.
var emptyFailure = new PdfConversion.LibreOfficeFailedException();
var messageFailure =
    new PdfConversion.LibreOfficeFailedException("Conversion failed.");
var wrappedFailure = new PdfConversion.LibreOfficeFailedException(
    "Conversion failed.",
    new IOException("The output could not be moved."));
```

To use the default output directory, call
`PdfConversion.ConvertToPdf(libreOffice, input)` or await
`PdfConversion.ConvertToPdfAsync(libreOffice, input)`.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
