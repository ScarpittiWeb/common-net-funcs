# CommonNetFuncs.SubsetModelBinder

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.SubsetModelBinder)](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.SubsetModelBinder)](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder/)

`CommonNetFuncs.SubsetModelBinder` combines [`SubsetOfAttribute`](../CommonNetFuncs.SubsetModelBinder.Attribute/README.md) with its [Roslyn source generator](../CommonNetFuncs.SubsetModelBinder.Generator/README.md) to validate subset property names/types at compile time and emit `MetadataType` or MVC `ModelMetadataType` metadata. It is not a runtime mapper, validator, or authorization mechanism.

## Contents

- [Installation](#installation)
- [Package relationship](#package-relationship)
- [Usage](#usage)
- [Attribute options](#attribute-options)
- [Generator behavior](#generator-behavior)
- [Diagnostics](#diagnostics)
- [Runtime limitations and supported shapes](#runtime-limitations-and-supported-shapes)
- [Targets and compatibility](#targets-and-compatibility)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.SubsetModelBinder
```

The umbrella package has a runtime dependency on
[`CommonNetFuncs.SubsetModelBinder.Attribute`](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder.Attribute/) and a dependency on
[`CommonNetFuncs.SubsetModelBinder.Generator`](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder.Generator/).

Because the generator is currently packaged as an analyzer and the umbrella package's dependency metadata excludes build/analyzer assets, the analyzer may not be activated for an umbrella-only consumer.
Verify activation in the actual consuming build. A deliberate property-name error must produce `SG0002`.
If it does not, reference the generator package directly so its analyzer asset is used:

```bash
dotnet add package CommonNetFuncs.SubsetModelBinder.Generator
```

For a project in this repository, the equivalent `ProjectReference` should explicitly activate the analyzer:

```xml
<ProjectReference
    Include="..\CommonNetFuncs.SubsetModelBinder.Generator\CommonNetFuncs.SubsetModelBinder.Generator.csproj"
    OutputItemType="Analyzer"
    ReferenceOutputAssembly="false" />
```

Installing only the attribute package supplies the attribute but does not activate the generator.

## Package relationship

| Package | Contains | Normal consumer use |
| --- | --- | --- |
| `CommonNetFuncs.SubsetModelBinder.Attribute` | `SubsetOfAttribute` runtime assembly | Attribute-only scenarios or explicit split installation |
| `CommonNetFuncs.SubsetModelBinder.Generator` | `SubsetValidatorGenerator.dll` under `analyzers/dotnet/cs` | Compiler-time validation and metadata source generation |
| `CommonNetFuncs.SubsetModelBinder` | A small `netstandard2.0` package assembly plus dependencies | Preferred entry point, with direct generator installation when analyzer activation must be guaranteed |

The umbrella assembly does not expose additional public helper methods. Its public functionality is the transitive attribute and the generator when both are active.

## Usage

Use a top-level, non-generic, non-nested `partial class` in a namespace. Put the source type in the same namespace so the generated source can resolve its bare type name.

```csharp
using CommonNetFuncs.SubsetModelBinder;

namespace Example;

public class AuditedEntity
{
    public DateTime CreatedUtc { get; set; }
}

public class Person : AuditedEntity
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
}

// Source type only: isMvcApp=false, allowInheritedProperties=true, ignoreType=false.
[SubsetOf(typeof(Person))]
public partial class PersonSummary
{
    public int Id { get; set; }
    public required string Name { get; set; }

    // Allowed because inherited source properties are included by default.
    public DateTime CreatedUtc { get; set; }
}

// isMvcApp=true selects Microsoft.AspNetCore.Mvc.ModelMetadataType.
[SubsetOf(typeof(Person), isMvcApp: true)]
public partial class PersonMvcModel
{
    public required string Name { get; set; }
    public string? Email { get; set; }
}

// Only properties declared directly on Person are valid in this subset.
[SubsetOf(typeof(Person), allowInheritedProperties: false)]
public partial class PersonOwnProperties
{
    public int Id { get; set; }
}

// Keep name checking, but deliberately relax property-type equality.
[SubsetOf(typeof(Person), ignoreType: true)]
public partial class PersonTransport
{
    public string Id { get; set; } = string.Empty;
}

// All constructor parameters can also be supplied explicitly.
[SubsetOf(
    typeof(Person),
    isMvcApp: true,
    allowInheritedProperties: false,
    ignoreType: true)]
public partial class PersonExplicitOptions
{
    public string Id { get; set; } = string.Empty;
}
```

The `SubsetOf` attribute has one constructor. Its final three parameters are optional:

```csharp
SubsetOfAttribute(Type sourceType,
                  bool isMvcApp = false,
                  bool allowInheritedProperties = true,
                  bool ignoreType = false);
```

## Attribute options

| Option/property | Default | Effect |
| --- | --- | --- |
| `sourceType` / `SourceType` | Required | Type whose properties form the allowed source-name/type set |
| `isMvcApp` / `IsMvcApp` | `false` | `false` generates `System.ComponentModel.DataAnnotations.MetadataType`; `true` generates `Microsoft.AspNetCore.Mvc.ModelMetadataType` |
| `allowInheritedProperties` / `AllowInheritedProperties` | `true` | Walks the source type's class base types; `false` examines only properties declared by the supplied type |
| `ignoreType` / `IgnoreType` | `false` | `false` reports `SG0001` for unequal property types; `true` suppresses only that type check |

All four attribute properties are get-only. The attribute is sealed, may be applied only to classes, is not inherited, and cannot be applied more than once to the same declaration.

## Generator behavior

For each class syntax declaration decorated with the fully qualified
`CommonNetFuncs.SubsetModelBinder.SubsetOfAttribute`, the incremental generator:

1. Requires the declaration carrying the attribute to contain the `partial` modifier (`SG0003` otherwise).
2. Builds a name-to-property lookup from the supplied source type, optionally walking class base types.
3. Examines properties declared directly on the marked subset class.
4. Reports `SG0002` when a subset property name is absent.
5. Unless `ignoreType` is `true`, reports `SG0001` when Roslyn does not consider the property types equal.
6. Emits a `public partial class` in the subset class's containing namespace and adds the selected metadata attribute.

Given the default `[SubsetOf(typeof(Person))]`, output is equivalent to:

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

With `isMvcApp: true`, the only generated metadata differences are:

```csharp
using Microsoft.AspNetCore.Mvc;

// ...
[ModelMetadataType(typeof(Person))]
public partial class PersonMvcModel
{
}
```

Property order does not matter. Fields, methods, events, and validation-attribute behavior are not independently compared. A property is still checked if it is static, read-only, expression-bodied, virtual, or overridden; the check concerns its declared name and type, not its accessors, accessibility, or modifiers.

## Diagnostics

All three diagnostics are errors, are enabled by default, and use category `SubsetValidatorGenerator`.

| ID | When emitted | Fix |
| --- | --- | --- |
| `SG0001` — `Property type mismatch` | A same-named subset property has a Roslyn type different from the selected source property and `ignoreType` is `false` | Match the exact property type, or intentionally set `ignoreType: true` |
| `SG0002` — `Property not found` | A subset property is absent from the source type (and eligible source base types, when enabled) | Rename/remove the property, select the correct source type, or enable inherited properties |
| `SG0003` — `Class must be partial` | The class declaration carrying `SubsetOf` does not contain `partial` | Mark that declaration `partial` |

The generator can report multiple property diagnostics in one compilation and still emit its partial declaration. Invalid/unresolved attribute syntax is left to normal C# diagnostics; this package has no separate diagnostic for an invalid `typeof` argument.

## Runtime limitations and supported shapes

The package validates source shape only during compilation. The generated attributes do not themselves perform mapping, validation, model binding, authorization, or over-posting protection. Apply normal input validation and authorization separately.

Current generator limitations are important:

- Supported reliably: a top-level, non-generic, non-nested, namespace-scoped `partial class`.
- The generated declaration always says `public partial class`; it does not preserve internal accessibility, static/file-local modifiers, generic type parameters, record syntax, containing types, or base/interface lists.
- The generated metadata target uses only the source type's simple name. Keep the source type non-generic, non-nested, and in the subset's namespace (or otherwise globally resolvable without a generated-file-specific `using`).
- The predicate handles `class` declarations, not `record` or `struct` subset declarations.
- The generator reads properties from source class base types, not inherited interface members, unless a property is implemented on the selected class. It does not validate properties inherited by the subset class; it validates properties declared on the marked type.
- Source-property hiding is resolved from the most-derived type outward: the first property encountered for a name wins.
- Reference-type nullability annotations are not enforced by `SymbolEqualityComparer.Default`; nullable value types remain distinct CLR/Semantic types. Setter/getter shape and accessibility are also not checked.
- `isMvcApp: true` requires the consumer to reference ASP.NET Core MVC. The package does not add MVC to the consumer.
- Global-namespace classes are not reliably supported because the generated `namespace` statement is unqualified.

These limitations describe the current implementation even where low-level generator tests only inspect generated syntax rather than a full compilation of the generated output.

## Targets and compatibility

All three component projects target `netstandard2.0`. The generator is an incremental C# Roslyn generator and therefore requires a compiler that supports incremental source generators. Modern .NET SDK builds are the intended environment.

`SubsetOfAttribute` stores the supplied values as runtime metadata, but property validation and source emission occur only in a build that has the generator analyzer enabled.

## License

This project is licensed under the MIT License. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
