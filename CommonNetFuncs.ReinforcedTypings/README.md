# CommonNetFuncs.ReinforcedTypings

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.ReinforcedTypings)](https://www.nuget.org/packages/CommonNetFuncs.ReinforcedTypings/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.ReinforcedTypings)](https://www.nuget.org/packages/CommonNetFuncs.ReinforcedTypings/)

Build-time extensions for [Reinforced.Typings](https://github.com/reinforced/Reinforced.Typings):

- `[TsConst]` exports selected public static fields to TypeScript object literals.
- `[TsCollection]` exports selected public static collection fields to TypeScript array literals.
- `[GenerateValibotSchema]` configures a Reinforced.Typings interface generator that writes a companion Valibot schema file.

The package targets `net11.0`. It is not a runtime mapping or validation service.

## Contents

- [Installation](#installation)
- [Setup](#setup)
- [`TsConst`](#tsconst)
- [`TsCollection`](#tscollection)
- [Valibot schemas](#valibot-schemas)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.ReinforcedTypings
```

## Setup

Point the consuming project's Reinforced.Typings configuration at the public `Configure` hook:

```xml
<!-- Reinforced.Typings.settings.xml -->
<Project ToolsVersion="4.0" DefaultTargets="Build"
         xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup>
    <RtConfigurationMethod>
      CommonNetFuncs.ReinforcedTypings.ReinforcedTypingsFluentConfig.Configure
    </RtConfigurationMethod>
    <RtTargetDirectory>$(ProjectDir)TypeScriptModels</RtTargetDirectory>
    <!-- Keep the consuming project's normal RtAssemblies, RtTargetFile,
         RtDivideTypesAmongFiles, and other RT settings here. -->
  </PropertyGroup>

  <Target Name="AddReinforcedTypingsFluentConfigAssembly"
          BeforeTargets="ReinforcedTypingsGenerate">
    <ItemGroup>
      <RtAdditionalAssembly Include="@(ReferencePath)"
        Condition="'%(ReferencePath.FileName)' == 'CommonNetFuncs.ReinforcedTypings'" />
    </ItemGroup>
  </Target>
</Project>
```

`ReferencePath` is normally populated during reference resolution, so the additional assembly should be added inside a target as above. Reinforced.Typings looks for the configured method in source/added assemblies, not by searching every package reference.

The public entry point is intended to be called by Reinforced.Typings, but its signature is directly available:

```csharp
using CommonNetFuncs.ReinforcedTypings;
using Reinforced.Typings.Fluent;

void ConfigureReinforcedTypings(ConfigurationBuilder builder) =>
    ReinforcedTypingsFluentConfig.Configure(builder);
```

`Configure` scans `builder.Context.SourceAssemblies`, falling back to its own assembly only when no source assemblies are present. It discovers `[GenerateValibotSchema]` + `[TsInterface]` types, then writes `[TsConst]`/`[TsCollection]` files synchronously. A successful build alone does not prove output was written—inspect the configured target area and treat generated files as build products.

For hand-written constant/collection output, hierarchical `RtTargetDirectory` or the directory portion of `RtTargetFile` is used. Namespace folders, modules, warning comments, tabs/newlines, property casing, unresolved types, and pure-typings mode follow the resolved RT global settings.

## `TsConst`

The class-only attribute has an optional `Mode`:

```csharp
using CommonNetFuncs.ReinforcedTypings.Constants;

[TsConst] // TsConstExportMode.All
public static class AppDefaults
{
    public const string Title = "Portal";
    public static readonly int[] AllowedPageSizes = [10, 25, 50];

    [TsIgnoreConst]
    public const string ServerOnlySecret = "do-not-export";
}

[TsConst(TsConstExportMode.Selected)]
public static class SelectedPolicy
{
    public const string Hidden = "hidden";

    [TsExportConst]
    public const string EditUsers = "EditUsers";
}
```

`TsConstAttribute.Mode` is readable, and enum values are `All` (default, zero) and `Selected`:

```csharp
TsConstAttribute mode = new(TsConstExportMode.Selected);
Console.WriteLine(mode.Mode);
TsExportConstAttribute include = new();
TsIgnoreConstAttribute ignore = new();
```

In `All` mode, every public static field is included unless marked `[TsIgnoreConst]`; in `Selected`, only fields marked `[TsExportConst]` are included. `const` fields and `static readonly` fields are both discoverable, but the implementation scans public static fields generally and does not restrict the decorated class at runtime to `static`. The field attributes apply to fields and are not inherited.

Normal output resembles:

```ts
export const AppDefaults = {
  Title: 'Portal',
  AllowedPageSizes: [10, 25, 50],
} as const;

export type AppDefaultsKey = keyof typeof AppDefaults;
export type AppDefaultsValue = (typeof AppDefaults)[AppDefaultsKey];
```

Values are serialized at generation time from live static-field values. Strings, booleans, `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly`, and `TimeOnly` have explicit literal handling. Enums are numeric unless `[TsEnum(UseString = true)]` applies. Arrays/enumerables are recursive; class/record/other object values become object literals from public readable instance properties, skipping indexers and `[TsIgnore]`. `null` becomes `null`.

```csharp
using Reinforced.Typings.Attributes;

public sealed class PublicDefaults
{
    public string Name { get; init; } = "Ada";

    [TsIgnore]
    public string ServerOnly { get; init; } = "secret";
}

[TsConst]
public static class RichDefaults
{
    public static readonly Guid Tenant = new("11111111-1111-1111-1111-111111111111");
    public static readonly DateOnly LaunchDate = new(2024, 1, 2);
    public static readonly PublicDefaults User = new();
}
```

Date/time output is ISO-shaped; unsupported structs fall through to property-object serialization (for example, `TimeSpan` becomes an object containing public `TimeSpan` properties). There is no cycle detector for constant object serialization and no deep security redaction: never decorate constants containing credentials, signing material, or client-inappropriate values.

## `TsCollection`

```csharp
using CommonNetFuncs.ReinforcedTypings.Collections;

[TsCollection] // All
public static class Lists
{
    public static readonly string[] Names = ["Admin", "Editor"];

    [TsIgnoreCollection]
    public static readonly string[] Deprecated = ["Legacy"];

    public static readonly Dictionary<string, int> NotEligible = [];
    public const string NotACollection = "text";
}

[TsCollection(TsCollectionExportMode.Selected)]
public static class SelectedLists
{
    public static readonly int[] Hidden = [1];

    [TsExportCollection]
    public static readonly int[] Exported = [2, 3];
}

TsCollectionAttribute collectionMode = new(TsCollectionExportMode.Selected);
TsExportCollectionAttribute collectionInclude = new();
TsIgnoreCollectionAttribute collectionIgnore = new();
Console.WriteLine(collectionMode.Mode);
```

Eligibility means an array or a type that implements `IEnumerable<T>` with one element type. Strings and dictionaries are excluded. In `Selected`, only `[TsExportCollection]` fields are considered; in `All`, `[TsIgnoreCollection]` fields are skipped. An eligible `TsCollection` class with no resulting fields writes no file. The output object is named `<ClassName>Collections`, is asserted `as const`, and gets `<ClassName>CollectionsKey`/`Value` type aliases.

Basic TypeScript element types map to `string`, `number`, or `boolean`. Elements whose .NET types are decorated with `[TsEnum]`, `[TsInterface]`, or `[TsClass]` use that generated type (including `[TsInterface(AutoI = true)]`'s `I` prefix). ES-module output adds `import type`; namespace output qualifies the name. Other element types become `any`, or `unknown` when RT's `UnresolvedToUnknown` is enabled.

```csharp
[Reinforced.Typings.Attributes.TsEnum]
public enum Role { Admin, Editor }

[Reinforced.Typings.Attributes.TsInterface]
public sealed class UserSummary
{
    public string Name { get; set; } = string.Empty;
}

[TsCollection]
public static class SeedData
{
    public static readonly List<Role> Roles = [Role.Admin];
    public static readonly List<UserSummary> Users = [new() { Name = "Ada" }];
}
```

If a type has both `[TsConst]` and `[TsCollection]`, both sections are written to one file. If it also has a normal `[TsInterface]`, `[TsClass]`, or `[TsEnum]`, this package writes to `<TypeName>Constants.ts` to avoid Reinforced.Typings overwriting `<TypeName>.ts`.

The const/collection writer honors `WriteWarningComment`, `TabSymbol`, `NewLine`, `CamelCaseForProperties`, `UnresolvedToUnknown`, `UseModules`, `DiscardNamespacesWhenUsingModules`, `RootNamespace`, and `ExportPureTypings`. With pure typings, it emits ambient readonly declarations and omits runtime literals/type aliases. With modules disabled, it mirrors RT's namespace style instead of emitting top-level module syntax.

## Valibot schemas

Apply both `[TsInterface]` and `[GenerateValibotSchema]`:

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using CommonNetFuncs.ReinforcedTypings.Valibot;
using Reinforced.Typings.Attributes;

[TsInterface]
[GenerateValibotSchema]
public sealed class CreateUserModel
{
    [Required]
    [MaxLength(50)]
    [DisplayName("Full Name")]
    public string Name { get; set; } = string.Empty;

    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Range(18, 120)]
    public int Age { get; set; }
}

GenerateValibotSchemaAttribute marker = new();
```

`GenerateValibotSchemaAttribute` can decorate one class or struct, is not inherited, and is not multiply allowed. `Configure` registers `ValibotSchemaGenerator` only for types with both attributes. Reinforced.Typings invokes its public `GenerateNode` override while generating the interface:

```csharp
using Reinforced.Typings;
using Reinforced.Typings.Fluent;

// A real RT export invokes ValibotSchemaGenerator.GenerateNode internally.
ExportContext context = new(
    [typeof(CreateUserModel).Assembly], new FilesOperations())
{
    Hierarchical = true,
    TargetDirectory = Path.Combine(AppContext.BaseDirectory, "TypeScriptModels"),
    ConfigurationMethod = builder => builder.ExportAsInterfaces(
        [typeof(CreateUserModel)],
        configuration =>
            configuration.WithCodeGenerator<ValibotSchemaGenerator>())
};

new TsExporter(context).Export();
```

`GenerateNode` first produces the normal interface node. For opted-in types it writes a companion `<TypeName>.schema.ts`. **The current schema writer does not use the RT target directory directly.** It starts from the type assembly location, walks upward for a directory containing `TypeScriptModels` or a `.csproj`, and caches the resolved `<project>/TypeScriptModels` directory in a process-wide static field. Verify this location. It is not guaranteed to be beside an interface when the consuming RT target differs from that convention.

Generated schema code includes:

- `import * as v from 'valibot'`
- `<TypeName>Schema = v.object({...})`
- `<TypeName>Input = v.InferInput<typeof ...>`
- `<TypeName>Labels: Record<string,string>` with `[DisplayName]` or split-Pascal-case names.

Properties marked `[TsIgnore]` are omitted from schema and labels. The schema uses original C# property names, not `CamelCaseForProperties`.

Supported built-in shapes:

- `string` → `v.string()`
- `int`, `long`, `short`, `byte`, `uint`, `ulong`, `float`, `double`, `decimal` → `v.number()`
- `bool` → `v.boolean()`
- `Guid`, `DateTime`, `DateTimeOffset`, `DateOnly` → `v.string()` (they are JSON representations, not Valibot format validators)
- enums → `v.number()`
- arrays, `List<T>`, `IList<T>`, `IEnumerable<T>`, `ICollection<T>` → `v.array(...)`
- non-System/non-Microsoft complex classes → inline `v.object(...)`
- cycles and unrecognized types → `v.any()`.

Supported validation attributes are `[Required]`, `[MaxLength]`, `[MinLength]`, `[StringLength]`, `[Range]`, `[RegularExpression]`, `[EmailAddress]`, `[Url]`, `[Phone]`, and `[CreditCard]`. Error messages are copied into the matching Valibot call when present. Required/nullable handling follows reflected C# nullability: nullable references and nullable value types may be `optional(nullable(...))`; required nullable values become `nullable(...)`; nonnullable value types without `[Required]` can still be optional. A nonnullable `string` without `[Required]` is not wrapped in `optional`.

The generator additionally recognizes attributes from other assemblies by type name:

```csharp
// CommonNetFuncs.Web.Common-style attributes are recognized if present:
[DenyCharacters("<>")]
[DenyRegularExpression("bad")]
[AllowedNullableValues("A", "B")]
public string? SafeChoice { get; set; }

public string[]? Tags { get; set; } // List* attributes below become item validators
```

It supports `ListMaxLength`, `ListStringLength`, `ListRange`, `ListRegularExpression`, `ListDenyCharacters`, `ListDenyRegularExpression`, `DenyCharacters`, `DenyRegularExpression`, and `AllowedNullableValues`. This is name/reflection based, so the class name and expected properties still have to match. Unsupported member types or attributes can result in permissive `v.any()` or no extra pipe.

Types carrying an attribute named `SubsetOfAttribute` inherit validation attributes from the matching property on the source type. The source `Type` can come from a `SourceType` property or the attribute's first constructor argument:

```csharp
using CommonNetFuncs.SubsetModelBinder.Attribute;

public sealed class UserSource
{
    [Required]
    [MaxLength(50)]
    public string Name { get; set; } = string.Empty;
}

[TsInterface]
[GenerateValibotSchema]
[SubsetOf(typeof(UserSource))]
public sealed class UserInput
{
    public string Name { get; set; } = string.Empty;
}
```

Always inspect generated TypeScript and exercise `v.parse`/`v.safeParse` with valid and invalid values. Client-generated rules are not a replacement for server-side validation, and not every .NET type, nullability shape, custom validator, or regular-expression dialect has an exact Valibot/JavaScript equivalent.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
