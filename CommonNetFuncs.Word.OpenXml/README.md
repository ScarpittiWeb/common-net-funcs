# CommonNetFuncs.Word.OpenXml

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Word.OpenXml)](https://www.nuget.org/packages/CommonNetFuncs.Word.OpenXml/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Word.OpenXml)](https://www.nuget.org/packages/CommonNetFuncs.Word.OpenXml/)

Helpers for replacing external hyperlink relationship URIs in existing Open XML
Word (`.docx`) documents.

Targets `net11.0` and `netstandard2.1`. This package does not render Word, edit
visible text, or support the binary `.doc` format.

## Contents

- [CommonNetFuncs.Word.OpenXml](#commonnetfuncswordopenxml)
  - [Contents](#contents)
  - [Installation](#installation)
  - [API and behavior](#api-and-behavior)
  - [Usage examples for every overload](#usage-examples-for-every-overload)
  - [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Word.OpenXml
```

## API and behavior

All four public methods are synchronous and belong to
`CommonNetFuncs.Word.OpenXml.ChangeUrls`.

- The stream is caller-owned. Open it for reading and writing; the stream
  normally must also be seekable because `WordprocessingDocument.Open` edits an
  Open XML package. The methods dispose the `WordprocessingDocument`, not the
  supplied stream.
- Only relationships in `MainDocumentPart.HyperlinkRelationships` are changed.
  Header, footer, footnote, and other part relationships are not searched.
  Visible hyperlink text is not changed.
- Exact matching is case-insensitive. Dictionary keys are treated
  case-insensitively by a first-added-key-wins lookup.
- Regex replacement uses `RegexOptions.IgnoreCase` by default. The default
  `regexTimeout` is `Regex.InfiniteMatchTimeout`; pass a finite timeout for
  non-trivial or untrusted patterns. Replacement output must form an absolute
  URI accepted by `new Uri(...)`.
- `replaceAll` defaults to `true`. In the dictionary regex overload, `false`
  means at most one relationship per pattern, rather than one relationship for
  the entire operation.
- Exceptions are logged and converted to `false`. `true` means the operation
  completed and a main document part existed; it does **not** prove that any
  hyperlink matched. Validate the result and flush/save the caller's stream as
  appropriate.
- This package does not require Microsoft Word to be installed.

## Usage examples for every overload

Each example uses a fresh writable `.docx` stream because the operation modifies
the package in place.

```csharp
using System.Text.RegularExpressions;
using CommonNetFuncs.Word.OpenXml;

using (FileStream exactStream = File.Open(
    "report.docx",
    FileMode.Open,
    FileAccess.ReadWrite,
    FileShare.None))
{
    // Overload 1: exact old URI, exact replacement, first/all match control.
    bool exactSuccess = ChangeUrls.ChangeUrlsInWordDoc(
        exactStream,
        newUrl: "https://new.example/report",
        urlToReplace: "http://old.example/report",
        replaceAll: true);

    if (!exactSuccess)
        throw new InvalidOperationException("The exact hyperlink update failed.");
}

using (FileStream dictionaryStream = File.Open(
    "report.docx",
    FileMode.Open,
    FileAccess.ReadWrite,
    FileShare.None))
{
    // Overload 2: exact URI dictionary. Every relationship is considered once.
    var exactReplacements = new Dictionary<string, string>
    {
        ["http://old.example/a"] = "https://new.example/a",
        ["http://legacy.example/b"] = "https://new.example/b"
    };

    bool dictionarySuccess =
        ChangeUrls.ChangeUrlsInWordDoc(dictionaryStream, exactReplacements);

    if (!dictionarySuccess)
        throw new InvalidOperationException("The dictionary update failed.");
}

using (FileStream regexStream = File.Open(
    "report.docx",
    FileMode.Open,
    FileAccess.ReadWrite,
    FileShare.None))
{
    // Overload 3: one regex pattern.
    bool regexSuccess = ChangeUrls.ChangeUrlsInWordDocRegex(
        regexStream,
        regexPattern: @"^http://(?<host>[^/]+)",
        replacementText: "https://${host}",
        replaceAll: true,
        regexOptions: RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        regexTimeout: TimeSpan.FromSeconds(2));

    if (!regexSuccess)
        throw new InvalidOperationException("The regex hyperlink update failed.");
}

using (FileStream regexDictionaryStream = File.Open(
    "report.docx",
    FileMode.Open,
    FileAccess.ReadWrite,
    FileShare.None))
{
    // Overload 4: multiple regex/replacement pairs.
    var regexReplacements = new Dictionary<string, string>
    {
        [@"^http://"] = "https://",
        [@"old\.example"] = "new.example"
    };

    bool regexDictionarySuccess = ChangeUrls.ChangeUrlsInWordDocRegex(
        regexDictionaryStream,
        regexReplacements,
        replaceAll: true,
        regexOptions: RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        regexTimeout: TimeSpan.FromSeconds(2));

    if (!regexDictionarySuccess)
        throw new InvalidOperationException("The regex dictionary update failed.");
}
```

For the overloads with defaults, calls can omit trailing optional arguments, but
the signatures are:

```csharp
bool ChangeUrlsInWordDoc(Stream, string newUrl, string urlToReplace, bool replaceAll = true);
bool ChangeUrlsInWordDoc(Stream, Dictionary<string, string> urlsToUpdate);
bool ChangeUrlsInWordDocRegex(
    Stream, string regexPattern, string replacementText, bool replaceAll = true,
    RegexOptions regexOptions = RegexOptions.IgnoreCase, TimeSpan? regexTimeout = null);
bool ChangeUrlsInWordDocRegex(
    Stream, Dictionary<string, string> urlsToUpdate, bool replaceAll = true,
    RegexOptions regexOptions = RegexOptions.IgnoreCase, TimeSpan? regexTimeout = null);
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
