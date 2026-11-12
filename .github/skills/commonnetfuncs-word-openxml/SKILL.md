---
name: commonnetfuncs-word-openxml
description: 'Use CommonNetFuncs.Word.OpenXml to replace hyperlink targets in DOCX documents, including writable-stream requirements and regex timeout safety.'
---

# CommonNetFuncs.Word.OpenXml

Use `ChangeUrls.ChangeUrlsInWordDoc` or `ChangeUrlsInWordDocRegex` to update
hyperlink relationships in an existing Open XML Word document.

## Workflow

1. Open a writable, seekable stream for a `.docx` document.
2. Call an exact-replacement or regex overload and inspect its `bool` result;
   exceptions are logged and converted to `false`. A `true` result means the
   operation completed without an exception and a main document part existed;
   it does not mean any hyperlink matched.
3. Dispose the caller-owned stream after the operation.

```csharp
using CommonNetFuncs.Word.OpenXml;

using var document = File.Open("report.docx", FileMode.Open, FileAccess.ReadWrite);
bool success = ChangeUrls.ChangeUrlsInWordDoc(
    document, "https://new.example/", "https://old.example/");
```

## Important constraints

- This updates hyperlink relationships in the main document part, not visible
  text or every header/footer relationship. It does not process binary `.doc`
  files despite the broader wording in the README.
- `replaceAll` defaults to `true`. For regex overloads, explicitly supply a
  finite `regexTimeout`: the default is infinite. Do not accept arbitrary
  untrusted regex patterns without additional restrictions.
- Validate replacement URI schemes and destinations for the application.

See [ChangeUrls.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Word.OpenXml/ChangeUrls.cs) and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Word.OpenXml/README.md).
