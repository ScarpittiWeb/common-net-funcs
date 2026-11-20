# CommonNetFuncs.SubsetModelBinder.Generator

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.SubsetModelBinder.Generator)](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder.Generator/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.SubsetModelBinder.Generator)](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder.Generator/)

`CommonNetFuncs.SubsetModelBinder.Generator` is the incremental Roslyn analyzer that recognizes [`SubsetOfAttribute`](../CommonNetFuncs.SubsetModelBinder.Attribute/README.md), reports `SG0001`/`SG0002`/`SG0003`, and emits source metadata through `MetadataType` or MVC `ModelMetadataType`. It is packaged under `analyzers/dotnet/cs`, not as a runtime model binder.

## Contents

- [Installation and activation](#installation-and-activation)
- [Supported usage](#supported-usage)
- [Attribute inputs](#attribute-inputs)
- [Generated contract](#generated-contract)
- [Diagnostics](#diagnostics)
- [Generator public API](#generator-public-api)
- [Validation details](#validation-details)
- [Current runtime and code-shape limitations](#current-runtime-and-code-shape-limitations)
- [Target and package layout](#target-and-package-layout)
- [License](#license)

## Installation and activation

Install both split packages:

```bash
dotnet add package CommonNetFuncs.SubsetModelBinder.Attribute
dotnet add package CommonNetFuncs.SubsetModelBinder.Generator
```

The attribute is required at compile time because the generator detects the exact metadata name
`CommonNetFuncs.SubsetModelBinder.SubsetOfAttribute`.

The [umbrella package](../CommonNetFuncs.SubsetModelBinder/README.md) is the preferred high-level entry point, but its current dependency metadata excludes transitive build/analyzer assets. Verify activation in every consuming project; if an intentional extra property does **not** report `SG0002`, also install the generator package directly:

```bash
dotnet add package CommonNetFuncs.SubsetModelBinder
dotnet add package CommonNetFuncs.SubsetModelBinder.Generator
```

A source-tree project reference must be marked as an analyzer:

```xml
<ProjectReference
    Include="..\CommonNetFuncs.SubsetModelBinder.Generator\CommonNetFuncs.SubsetModelBinder.Generator.csproj"
    OutputItemType="Analyzer"
    ReferenceOutputAssembly="false" />
```

The attribute reference is a normal project/package reference; the generator reference is a compiler analyzer.

## Supported usage

Use a namespace-scoped, top-level, non-generic `partial class`. Keep the source type non-generic, non-nested, and resolvable by simple name in the subset namespace.

```csharp
using CommonNetFuncs.SubsetModelBinder;
using System.ComponentModel.DataAnnotations;

namespace Example;

public class AuditedEntity
{
    public DateTime CreatedUtc { get; set; }
}

public class Person : AuditedEntity
{
    [Required]
    public required string Name { get; set; }

    public int Age { get; set; }
}

[SubsetOf(typeof(Person))]
public partial class PersonSummary
{
    public required string Name { get; set; }

    // Inherited because allowInheritedProperties defaults to true.
    public DateTime CreatedUtc { get; set; }
}
```

For ASP.NET Core MVC metadata, opt into `ModelMetadataType`:

```csharp
using CommonNetFuncs.SubsetModelBinder;

namespace Example;

[SubsetOf(typeof(Person), isMvcApp: true)]
public partial class PersonMvcSummary
{
    public required string Name { get; set; }
}
```

The MVC form requires ASP.NET Core MVC to be referenced by the consuming compilation.

## Attribute inputs

The generator reads the four constructor arguments from `SubsetOfAttribute`:

| Argument | Default | Generator behavior |
| --- | --- | --- |
| `Type sourceType` | Required | Supplies the Roslyn named type whose properties are checked and named in generated `typeof(...)` |
| `bool isMvcApp` | `false` | Generates `System.ComponentModel.DataAnnotations.MetadataType`; `true` generates `Microsoft.AspNetCore.Mvc.ModelMetadataType` |
| `bool allowInheritedProperties` | `true` | Adds properties from successive class `BaseType` symbols; `false` stops after the source type |
| `bool ignoreType` | `false` | Suppresses `SG0001` but continues `SG0002` name checking |

Examples of every supported option form, grouped in one compilation:

```csharp
// Defaults: isMvcApp=false, allowInheritedProperties=true, ignoreType=false.
[SubsetOf(typeof(Person))]
public partial class DefaultOptions
{
    public required string Name { get; set; }
}

[SubsetOf(typeof(Person), isMvcApp: true)]
public partial class MvcOptions
{
    public required string Name { get; set; }
}

[SubsetOf(typeof(Person), allowInheritedProperties: false)]
public partial class DeclaredOnlyOptions
{
    public int Age { get; set; }
}

[SubsetOf(typeof(Person), ignoreType: true)]
public partial class RelaxedTypeOptions
{
    public long Age { get; set; }
}

[SubsetOf(
    typeof(Person),
    isMvcApp: true,
    allowInheritedProperties: false,
    ignoreType: true)]
public partial class ExplicitOptions
{
    public long Age { get; set; }
}
```

## Generated contract

The generator uses incremental syntax discovery with `ForAttributeWithMetadataName`. It only transforms class declarations with an attribute list whose bound attribute has the exact fully qualified metadata name.

For `[SubsetOf(typeof(Person))] public partial class PersonSummary` in namespace `Example`, it emits one file named `PersonSummary_Attributes.g.cs`, equivalent to:

```csharp
#nullable enable
using System;
using System.ComponentModel.DataAnnotations;
namespace Example
{
    [MetadataType(typeof(Person))]
    public partial class PersonSummary
    {
    }
}
```

For `isMvcApp: true`, the generated file uses:

```csharp
using Microsoft.AspNetCore.Mvc;

// ...
[ModelMetadataType(typeof(Person))]
```

The generated attribute associates the source type's framework metadata/validation annotations with the subset partial type. It does not copy annotations, create properties, or perform runtime validation itself.

Generation is still attempted when property or `partial` diagnostics exist, provided the source-type constructor argument resolves to a named type. An unresolved source type or invalid attribute binding is handled by ordinary compiler behavior and may produce no generated source.

## Diagnostics

Each diagnostic is an enabled-by-default **error** with category `SubsetValidatorGenerator`.

| ID and title | Trigger | Diagnostic location | Required response |
| --- | --- | --- | --- |
| `SG0001` — `Property type mismatch` | The subset property name exists, but `SymbolEqualityComparer.Default` finds a different property type and `ignoreType` is `false` | The subset property declaration | Change the property type or deliberately set `ignoreType: true` |
| `SG0002` — `Property not found` | The subset property's name is absent from the source lookup | The subset property declaration | Rename/remove it, change the source type, or allow inherited source properties |
| `SG0003` — `Class must be partial` | The class declaration carrying `SubsetOfAttribute` lacks the `partial` modifier | The subset class identifier | Add `partial` to that declaration |

There are no generator diagnostics for methods, fields, accessors, annotations, authorization, or runtime values. Multiple applicable property diagnostics can be emitted from the same class.

Example fixes:

```csharp
public class Person
{
    public int Id { get; set; }
}

// Correct: no diagnostics.
[SubsetOf(typeof(Person))]
public partial class ValidPerson
{
    public int Id { get; set; }
}

// SG0001: string is not int.
[SubsetOf(typeof(Person))]
public partial class WrongType
{
    public string Id { get; set; } = string.Empty;
}

// SG0002: Person has no Extra property.
[SubsetOf(typeof(Person))]
public partial class ExtraProperty
{
    public string Extra { get; set; } = string.Empty;
}

// SG0003: the attributed declaration must be partial.
[SubsetOf(typeof(Person))]
public class NotPartial
{
    public int Id { get; set; }
}
```

## Generator public API

The only public application type in the analyzer assembly is:

```csharp
[Generator(LanguageNames.CSharp)]
public sealed class SubsetValidatorGenerator : IIncrementalGenerator
{
    public SubsetValidatorGenerator();

    public void Initialize(IncrementalGeneratorInitializationContext context);
}
```

The constructor is an implicit, public parameterless constructor. Normal builds call `Initialize` through Roslyn; consumers do not call that method. A custom Roslyn host can exercise both members (construction and Roslyn's invocation of `Initialize`) directly with a normal compilation:

```csharp
using CommonNetFuncs.SubsetModelBinder;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

CSharpCompilation compilation =
    CSharpCompilation.Create("ExampleCompilation");

var generator = new SubsetValidatorGenerator();
GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
driver = driver.RunGenerators(compilation);
GeneratorDriverRunResult result = driver.GetRunResult();
```

This direct-host example requires a direct assembly reference (for example a project reference to the generator project) plus `Microsoft.CodeAnalysis.CSharp`. It is distinct from normal NuGet analyzer consumption, where the class is loaded by the compiler rather than referenced as application code.

## Validation details

- Only properties declared directly on the attributed subset symbol are iterated.
- Source lookup includes properties returned by `GetMembers()` from the supplied type and, by default, each base class until there is no base.
- If a more-derived source type hides a base property of the same name, the more-derived property is kept.
- Source interfaces are not traversed through `Interfaces`; only properties actually declared on the selected type/base classes are included.
- Property order is irrelevant.
- Name comparison uses the default dictionary comparer.
- Type comparison is `SymbolEqualityComparer.Default`; it covers constructed generic and array types but intentionally does not distinguish nullable-reference annotations. `Nullable<T>` remains different from `T`. It also does not separately compare getter/setter shapes, indexer parameters, accessibility, static/instance status, virtuality, or readonly semantics.
- Fields, methods, events, constructors, and attributes other than `SubsetOfAttribute` are not validated.
- A subset may contain no properties. It is still subject to the partial check and receives generated metadata.
- Generated hint names use the subset's simple class name, and duplicate matching simple names can produce a source-name collision.

## Current runtime and code-shape limitations

The current emitted source is not a full symbol-preserving declaration. Review these limitations before using the generator outside its supported shape:

1. The generated subset declaration is always `public partial class`; accessibility, static/file-local modifiers, generic parameters, base types/interfaces, record syntax, and containment are not reproduced.
2. The subset must reliably be a top-level, non-generic, namespace-scoped `class`. Nested, generic, static, file-local, record, and struct subset declarations, as well as global-namespace classes, are not emitted correctly as matching partial declarations.
3. `ForAttributeWithMetadataName` is predicated on `ClassDeclarationSyntax`, so a record or struct subset is not a supported target even though some .NET syntax may classify it as a class/attribute target.
4. The source metadata target uses `originalTypeSymbol.Name`, losing namespace, containing-type information, generic type arguments, and qualification. Keep the source non-generic/non-nested and in the generated file's namespace (or otherwise globally resolvable).
5. A source type in another namespace is not supplied with a generated `using`; file-level `using` directives do not copy into generated files.
6. `isMvcApp: true` requires `Microsoft.AspNetCore.Mvc.ModelMetadataType`; the analyzer package does not bring MVC into the consuming project.
7. The selected framework must honor the emitted `MetadataType` or `ModelMetadataType` and apply validation metadata. The source generator itself does not run validators.
8. This is compile-time shape checking, not a security boundary or over-posting policy. Validate and authorize independently.
9. `SymbolEqualityComparer.Default` does not enforce nullable-reference annotations. Do not rely on the generator to validate nullable annotations, accessor shapes, indexer parameters, or mutability details.

The focused tests cover diagnostics, default/options behavior, inheritance traversal, metadata selection, and several syntax forms. Some edge tests inspect that a syntax tree was emitted without compiling the updated output; the limitations above describe the actual generated contract rather than inferring broader support from those tests.

## Target and package layout

- Generator project target: `netstandard2.0`.
- Generator kind: incremental Roslyn source generator.
- Package build output: excluded from `lib`.
- Analyzer payload: `CommonNetFuncs.SubsetModelBinder.Generator.dll` and its PDB under `analyzers/dotnet/cs`.
- `Microsoft.CodeAnalysis` dependencies are private build-time dependencies and are not exposed as a runtime package dependency.
- The attribute is a separate runtime package and is not included by the generator package.

## License

This project is licensed under the MIT License. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
