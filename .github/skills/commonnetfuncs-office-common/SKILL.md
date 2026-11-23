---
name: commonnetfuncs-office-common
description: 'Use CommonNetFuncs.Office.Common PdfConversion to convert Office documents to PDF with LibreOffice, including process, path, timeout, and overwrite requirements.'
---

# CommonNetFuncs.Office.Common

`PdfConversion` runs an external LibreOffice executable to convert a document
on disk to PDF. It is not an in-process Office renderer.

## Workflow

1. Install LibreOffice on the host and supply the trusted executable path,
   input filename, and optional output directory to `ConvertToPdf` or
   `ConvertToPdfAsync`. The default directory is the input directory; the
   output filename is the input basename with a `.pdf` extension.
2. Configure retries and overwrite behavior deliberately. The synchronous
   overload accepts `conversionTimeout`; the async overload accepts a
   cancellation token. Process and cancellation failures can be wrapped in
   `LibreOfficeFailedException`.
3. Handle conversion, process, and filesystem failures and verify that the
   expected output exists. Both methods return no success value.

## Important constraints

- Default `maxRetries` is 3 and `overwriteExistingFile` is `false`; an existing
  output must not be assumed to have been replaced.
- Validate and restrict input/output paths and executable selection. Isolate
  processing of untrusted documents and bound file sizes and processing time:
  LibreOffice parses the supplied document in an external process.
- The caller owns its input and output files. This API does not accept a stream
  or install LibreOffice for the application.

See [PdfConversion.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Office.Common/PdfConversion.cs)
and the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Office.Common/README.md).
