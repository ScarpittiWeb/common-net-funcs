# CommonNetFuncs.Excel.Common

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](http://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Excel.Common)](https://www.nuget.org/packages/CommonNetFuncs.Excel.Common)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Excel.Common)](https://www.nuget.org/packages/CommonNetFuncs.Excel.Common)

Shared style, font, table-theme, and indexed-color enums for the CommonNetFuncs
Excel backends. This package performs no workbook I/O and targets `net11.0`
and `netstandard2.1`; install a ClosedXML, NPOI, or Open XML backend for I/O.

## Contents

- [CommonNetFuncs.Excel.Common](#commonnetfuncsexcelcommon)
  - [Contents](#contents)
  - [`EStyle`](#estyle)
  - [`EFont`](#efont)
  - [`ETableStyle`](#etablestyle)
  - [`EFontName`](#efontname)
  - [`EIndexedExcelColors`](#eindexedexcelcolors)
  - [Backend compatibility](#backend-compatibility)
  - [Installation](#installation)
  - [License](#license)

Import the namespace:

```csharp
using CommonNetFuncs.Excel.Common;
```

## `EStyle`

Preset semantic style categories:

```csharp
EStyle[] styles =
[
    EStyle.Header,
    EStyle.HeaderThickTop,
    EStyle.Body,
    EStyle.Error,
    EStyle.Blackout,
    EStyle.Whiteout,
    EStyle.Custom,
    EStyle.ImageBackground
];
```

How a category is rendered is backend-specific. In particular, `Custom` is not
implemented by `CommonNetFuncs.Excel.ClosedXml.GetStandardCellStyle`; use its
`GetStyle` custom-color/font/alignment overload instead. NPOI's custom-style
helpers do not accept `EStyle`; they accept color/font/alignment parameters
directly.

## `EFont`

Preset font categories:

```csharp
EFont[] fonts =
[
    EFont.Default,
    EFont.Header,
    EFont.Whiteout,
    EFont.ImageBackground
];
```

The ClosedXML and NPOI backends currently map these values to their own
`IXLFont` or `IFont` objects. The enum only names the category; it does not
guarantee a particular font is installed on the consuming machine.

## `ETableStyle`

All values correspond by name to Excel built-in table styles. A backend converts
the enum member's name (`ToString()`) into its native table theme/style.

```csharp
ETableStyle style = ETableStyle.TableStyleMedium9;
ETableStyle[] allFamilies =
[
    ETableStyle.TableStyleDark1,
    ETableStyle.TableStyleLight1,
    ETableStyle.TableStyleMedium1
];
```

Complete members:

<details>
<summary>Show every <code>ETableStyle</code> member</summary>

```text
TableStyleDark1, TableStyleDark2, TableStyleDark3, TableStyleDark4,
TableStyleDark5, TableStyleDark6, TableStyleDark7, TableStyleDark8,
TableStyleDark9, TableStyleDark10, TableStyleDark11,
TableStyleLight1, TableStyleLight2, TableStyleLight3, TableStyleLight4,
TableStyleLight5, TableStyleLight6, TableStyleLight7, TableStyleLight8,
TableStyleLight9, TableStyleLight10, TableStyleLight11, TableStyleLight12,
TableStyleLight13, TableStyleLight14, TableStyleLight15, TableStyleLight16,
TableStyleLight17, TableStyleLight18, TableStyleLight19, TableStyleLight20,
TableStyleLight21,
TableStyleMedium1, TableStyleMedium2, TableStyleMedium3, TableStyleMedium4,
TableStyleMedium5, TableStyleMedium6, TableStyleMedium7, TableStyleMedium8,
TableStyleMedium9, TableStyleMedium10, TableStyleMedium11, TableStyleMedium12,
TableStyleMedium13, TableStyleMedium14, TableStyleMedium15, TableStyleMedium16,
TableStyleMedium17, TableStyleMedium18, TableStyleMedium19, TableStyleMedium20,
TableStyleMedium21, TableStyleMedium22, TableStyleMedium23, TableStyleMedium24,
TableStyleMedium25, TableStyleMedium26, TableStyleMedium27, TableStyleMedium28
```

</details>

Every member uses its default numeric enum value; callers should pass the named
member, not persist or serialize the ordinal across package versions.

## `EFontName`

This enum is a convenient vocabulary of commonly encountered font-family
names. It does not bundle or install fonts, and using a member does not prove
that a viewer or host has that font.

```csharp
string fontName = nameof(EFontName.Calibri);
```

The complete set of members is:

<details>
<summary>Show every <code>EFontName</code> member</summary>

```text
AbadiMT, AgencyFB, AharoniBold, Aldhabi, Algerian, AlmanacMT, AmericanUncial,
AndaleMono, Andalus, Andy, AngsanaUPC, AngsanaNew, Aparajita,
ArabicTransparent, ArabicTypesetting, Arial, ArialBlack, ArialNarrow,
ArialNarrowSpecial, ArialRoundedMT, ArialSpecial, ArialUnicodeMS,
AugsburgerInitials, AvenirNextLTPro, Bahnschrift, BaskervilleOldFace,
BatangBatangChe, Bauhaus93, BeeskneesITC, BellMT, Bembo, BerlinSansFB,
BernardMTCondensed, BickleyScript, Biome, BIZUDGothic, BIZUDMinchoMedium,
BlackadderITC, BodoniMT, BodoniMTCondensed, BonApetitMT, BookmanOldStyle,
BookshelfSymbol, BookAntiqua, BradleyHandITC, Braggadocio, BriemScript,
BritannicBold, Broadway, BrowalliaUPC, BrowalliaNew, BrushScriptMT, Calibri,
CalifornianFB, CalistoMT, Cambria, CambriaMath, Candara, Cariadings,
Castellar, Cavolini, Centaur, Century, CenturyGothic, CenturySchoolbook,
Chiller, ColonnaMT, ComicSansMS, Consolas, Constantia, ContemporaryBrush,
CooperBlack, CopperplateGothic, Corbel, CordiaUPC, CordiaNew, CourierNew,
CurlzMT, Dante, DaunPenh, David, Daytona, Desdemona, DFKaiSB, DilleniaUPC,
DirectionsMT, DokChampa, DotumDotumChe, Ebrima, Eckmann, Edda,
EdwardianScriptITC, Elephant, EngraversMT, Enviro, ErasITC, EstrangeloEdessa,
EucrosiaUPC, Euphemia, Eurostile, FangSong, FelixTitling, FineHand,
FixedMiriamTransparent, Flexure, FootlightMT, Forte, FranklinGothic,
FranklinGothicMedium, FrankRuehl, FreesiaUPC, FreestyleScript, FrenchScriptMT,
Futura, Gabriola, Gadugi, Garamond, GaramondMT, Gautami, Georgia, GeorgiaRef,
Gigi, GillSansMT, GillSansMTCondensed, Gisha, Gloucester, GoudyOldStyle,
GoudyStout, Gradl, Grotesque, GulimGulimChe, GungsuhGungsuhChe,
HadassahFriedlaender, Haettenschweiler, HarlowSolidItalic, Harrington,
HGGothicE, HGMinchoE, HGSoeiKakugothicUB, HighTowerText, HolidaysMT,
HoloLensMDL2Assets, Impact, ImprintMTShadow, InformalRoman, IrisUPC,
IskoolaPota, JasmineUPC, JavaneseText, Jokerman, JuiceITC, KaiTi, Kalinga,
Kartika, KeystrokesMT, KhmerUI, Kigelia, KinoMT, KodchiangUPC, Kokila,
KristenITC, KunstlerScript, LaoUI, Latha, LCD, Leelawadee, LevenimMT,
LilyUPC, LucidaBlackletter, LucidaBright, LucidaBrightMath,
LucidaCalligraphy, LucidaConsole, LucidaFax, LucidaHandwriting, LucidaSans,
LucidaSansTypewriter, LucidaSansUnicode, Magneto, MaiandraGD, MalgunGothic,
Mangal, MapSymbols, Marlett, MatisseITC, MaturaMTScriptCapitals, McZee,
MeadBold, Meiryo, MercuriusScriptMTBold, MicrosoftGothicNeo, MicrosoftHimalaya,
MicrosoftJhengHei, MicrosoftJhengHeiUI, MicrosoftNewTaiLue, MicrosoftPhagsPa,
MicrosoftSansSerif, MicrosoftTaiLe, MicrosoftUighur, MicrosoftYaHei,
MicrosoftYaHeiUI, MicrosoftYiBaiti, MingLiU, MingLiUExtB, MingLiU_HKSCS,
MingLiU_HKSCSExtB, MinionWeb, Miriam, MiriamFixed, Mistral, ModernLove,
ModernNo20, MongolianBaiti, Monotypecom, MonotypeCorsiva, MonotypeSorts,
MoolBoran, MSGothic, MSLineDraw, MSMincho, MSOutlook, MSPGothic, MSPMincho,
MSReference, MSUIGothic, MTExtra, MVBoli, MyanmarText, Narkisim, NewsGothicMT,
NewCaledonia, Niagara, NirmalaUI, Nyala, OCRBDigits, OCRB, OCRAExtended,
OldEnglishTextMT, Onyx, PalaceScriptMT, PalatinoLinotype, Papyrus, Parade,
Parchment, PartiesMT, PeignotMedium, PepitaMT, Perpetua, PerpetuaTitlingMT,
PlacardCondensed, PlantagenetCherokee, Playbill, PMingLiU, PMingLiUExtB,
PoorRichard, Posterama, Pristina, QuireSans, Raavi, RageItalic, Ransom, Ravie,
RefSpecialty, Rockwell, RockwellNova, Rod, RunicMTCondensed, SabonNextLT,
Sagona, SakkalMajalla, ScriptMTBold, SegoeChess, SegoePrint, SegoeScript,
SegoeUI, SegoeUISymbol, Selawik, ShonarBangla, ShowcardGothic, Shruti,
SignsMT, SimHei, SimplifiedArabicFixed, SimSun, SimSunExtB, Sitka, NSimSun,
SnapITC, SportsMT, STCaiyun, Stencil, STFangsong, STHupo, STKaiti, Stop,
STXihei, STXingkai, STXinwei, STZhongsong, Sylfaen, Symbol, Tahoma,
TempoGrunge, TempusSansITC, TempInstallerFont, TheHand, TheSerifHand,
TimesNewRoman, TimesNewRomanSpecial, TisaOffcSerifPro, TraditionalArabic,
TransportMT, TrebuchetMS, Tunga, TwCenMT, Univers, UrduTypesetting, Utsaah,
VacationMT, Vani, Verdana, VerdanaRef, Vijaya, VinerHandITC, Vivaldi, VixarASCI,
VladimirScript, Vrinda, Walbaum, Webdings, Westminster, WideLatin, Wingdings
```

</details>

## `EIndexedExcelColors`

Named entries use the Excel indexed-color values shown below. The numeric
assignments are part of the current public declaration and intentionally
contain gaps.

```csharp
short redIndex = (short)EIndexedExcelColors.Red;
short customIndex = (short)EIndexedExcelColors.Grey80Percent;
```

| Member | Value | Member | Value |
| --- | ---: | --- | ---: |
| `Black` | 8 | `White` | 9 |
| `Red` | 10 | `BrightGreen` | 11 |
| `Blue` | 12 | `Yellow` | 13 |
| `Pink` | 14 | `Turquoise` | 15 |
| `DarkRed` | 16 | `Green` | 17 |
| `DarkBlue` | 18 | `DarkYellow` | 19 |
| `Violet` | 20 | `Teal` | 21 |
| `Grey25Percent` | 22 | `Grey50Percent` | 23 |
| `CornflowerBlue` | 24 | `Maroon` | 25 |
| `LemonChiffon` | 26 | `Orchid` | 28 |
| `Coral` | 29 | `RoyalBlue` | 30 |
| `LightCornflowerBlue` | 31 | `SkyBlue` | 40 |
| `LightTurquoise` | 41 | `LightGreen` | 42 |
| `LightYellow` | 43 | `PaleBlue` | 44 |
| `Rose` | 45 | `Lavender` | 46 |
| `Tan` | 47 | `LightBlue` | 48 |
| `Aqua` | 49 | `Lime` | 50 |
| `Gold` | 51 | `LightOrange` | 52 |
| `Orange` | 53 | `BlueGrey` | 54 |
| `Grey40Percent` | 55 | `DarkTeal` | 56 |
| `SeaGreen` | 57 | `DarkGreen` | 58 |
| `OliveGreen` | 59 | `Brown` | 60 |
| `Plum` | 61 | `Indigo` | 62 |
| `Grey80Percent` | 63 | | |

These are primarily useful where a backend accepts legacy indexed palette
numbers. Modern OOXML colors are often represented as RGB/theme colors instead.

## Backend compatibility

An enum name in this package is not a capability promise. Before using a value:

1. Select the backend package.
2. Use only an overload that accepts the enum.
3. Review backend-specific constraints such as table-name length, supported
   color format, or font availability.

## Installation

```bash
dotnet add package CommonNetFuncs.Excel.Common
```

## License

MIT; see [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
