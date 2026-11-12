---
name: commonnetfuncs-excel-common
description: 'Use CommonNetFuncs.Excel.Common shared Excel style, font, table, and color enums with the ClosedXML, NPOI, or Open XML exporters.'
---

# CommonNetFuncs.Excel.Common

This package defines shared Excel constants and enums; it does not read or
write workbooks. Use a separate Excel backend to produce a file.

## Workflow

1. Import `CommonNetFuncs.Excel.Common` and select the enum required by the
   chosen backend, such as `ETableStyle`, `EStyle`, `EFont`, `EFontName`, or
   `EIndexedExcelColors`.
2. Pass the value only to an exporter overload that accepts that enum. The
   same enum does not imply identical formatting support across all backends.
3. For workbook generation, consult the relevant ClosedXML, NPOI, or Open XML
   package skill and its API.

```csharp
using CommonNetFuncs.Excel.Common;

ETableStyle style = ETableStyle.TableStyleMedium1;
```

See [Constants.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.Common/Constants.cs) and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Excel.Common/README.md).
