# CommonNetFuncs.SubsetModelBinder.Attribute

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.SubsetModelBinder.Attribute)](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder.Attribute/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.SubsetModelBinder.Attribute)](https://www.nuget.org/packages/CommonNetFuncs.SubsetModelBinder.Attribute/)

`CommonNetFuncs.SubsetModelBinder.Attribute` is the `netstandard2.0` runtime package for the `SubsetOfAttribute` consumed by the [generator analyzer](../CommonNetFuncs.SubsetModelBinder.Generator/README.md). The attribute alone stores configuration but performs no compile-time checking or runtime mapping/validation; normal use should include the [umbrella package](../CommonNetFuncs.SubsetModelBinder/README.md) with an active generator analyzer.

## Contents

- [CommonNetFuncs.SubsetModelBinder.Attribute](#commonnetfuncssubsetmodelbinderattribute)
  - [Contents](#contents)
  - [Installation](#installation)
  - [Public API](#public-api)
  - [Usage](#usage)
  - [Option semantics](#option-semantics)
  - [Generator integration](#generator-integration)
  - [Limitations](#limitations)
  - [Targets](#targets)
  - [License](#license)

## Installation

Attribute only:

```bash
dotnet add package CommonNetFuncs.SubsetModelBinder.Attribute
```

Attribute plus analyzer:

```bash
dotnet add package CommonNetFuncs.SubsetModelBinder.Attribute
dotnet add package CommonNetFuncs.SubsetModelBinder.Generator
```

Or install the umbrella package and, if analyzer activation is not present, directly reference the generator package:

```bash
dotnet add package CommonNetFuncs.SubsetModelBinder
dotnet add package CommonNetFuncs.SubsetModelBinder.Generator
```

## Public API

Namespace: `CommonNetFuncs.SubsetModelBinder`

```csharp
[AttributeUsage(
    AttributeTargets.Class,
    Inherited = false,
    AllowMultiple = false)]
public sealed class SubsetOfAttribute : Attribute
{
    public SubsetOfAttribute(
        Type sourceType,
        bool isMvcApp = false,
        bool allowInheritedProperties = true,
        bool ignoreType = false);

    public Type SourceType { get; }
    public bool IsMvcApp { get; }
    public bool AllowInheritedProperties { get; }
    public bool IgnoreType { get; }
}
```

There is one constructor, not four separate overloads. Optional arguments can be omitted or supplied positionally/named. The four public properties are read-only and preserve the constructor values.

The attribute:

- Is `sealed`.
- Inherits from `System.Attribute`.
- Targets classes.
- Is not inherited by a derived class.
- Does not allow multiple applications to one declaration.

## Usage

The common application form uses the constructor with only the source type. The subset class is `partial` because the generator adds another partial declaration.

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
}

// One-argument form: all optional values use their defaults.
[SubsetOf(typeof(Person))]
public partial class PersonSummary
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public DateTime CreatedUtc { get; set; }
}

// Demonstrates isMvcApp.
[SubsetOf(typeof(Person), isMvcApp: true)]
public partial class PersonMvcModel
{
    public required string Name { get; set; }
}

// Demonstrates allowInheritedProperties.
[SubsetOf(typeof(Person), allowInheritedProperties: false)]
public partial class PersonWithoutInheritedProperties
{
    public int Id { get; set; }
}

// Demonstrates ignoreType.
[SubsetOf(typeof(Person), ignoreType: true)]
public partial class PersonTransport
{
    public string Id { get; set; } = string.Empty;
}

// Demonstrates the full positional form.
[SubsetOf(typeof(Person), true, false, true)]
public partial class PersonAllOptions
{
    public string Id { get; set; } = string.Empty;
}
```

The constructor can also be used directly, for example in reflection or an attribute consumer:

```csharp
using CommonNetFuncs.SubsetModelBinder;

SubsetOfAttribute defaults = new(typeof(Person));
SubsetOfAttribute mvcOnly = new(typeof(Person), isMvcApp: true);
SubsetOfAttribute ownOnly = new(typeof(Person), allowInheritedProperties: false);
SubsetOfAttribute relaxed = new(typeof(Person), ignoreType: true);
SubsetOfAttribute all = new(typeof(Person), true, false, true);

Type source = all.SourceType;
bool useMvc = all.IsMvcApp;
bool includeBaseTypes = all.AllowInheritedProperties;
bool skipTypeCheck = all.IgnoreType;
```

Here `typeof(Person)` stands for any source `Type` accepted by the C# attribute argument rules. The attribute itself accepts class, struct, interface, array, nested, generic, and other `Type` values at runtime; the generator's emitted-source limitations are narrower and are documented below.

## Option semantics

| Constructor argument | Property | Default | Meaning to the generator |
| --- | --- | --- | --- |
| `Type sourceType` | `SourceType` | Required | Source type against which subset property names and types are checked |
| `bool isMvcApp` | `IsMvcApp` | `false` | Selects `ModelMetadataType` (`true`) instead of `MetadataType` (`false`) |
| `bool allowInheritedProperties` | `AllowInheritedProperties` | `true` | Includes properties found by walking class base types when `true` |
| `bool ignoreType` | `IgnoreType` | `false` | Suppresses `SG0001` type mismatches; name checking remains active |

These values do not take effect until a consumer processes them. The attribute contains no runtime logic beyond retaining the values.

## Generator integration

The generator discovers the exact metadata name `CommonNetFuncs.SubsetModelBinder.SubsetOfAttribute`. Normal source includes:

```csharp
using CommonNetFuncs.SubsetModelBinder;

[SubsetOf(typeof(Person))]
public partial class PersonSummary
{
    public required string Name { get; set; }
}
```

When the analyzer is active, diagnostics are:

- `SG0001`: same property name, unequal property type, and `ignoreType` is `false`.
- `SG0002`: property name not found on the selected source type.
- `SG0003`: the declaration carrying the attribute is not `partial`.

Without the analyzer, none of these diagnostics is produced and no `MetadataType`/`ModelMetadataType` partial declaration is generated.

## Limitations

- `SubsetOfAttribute` does not require or reference ASP.NET Core. `isMvcApp: true` is only a stored flag; the active generator emits `ModelMetadataType`, which requires MVC to be available in the consuming compilation.
- It is not a validation attribute. It does not validate an object when model validation runs.
- It does not copy, map, bind, authorize, or hide properties.
- `AllowInheritedProperties` is interpreted by the generator and applies to source class base types; it does not independently traverse source interfaces.
- Attribute arguments use normal C# attribute-argument rules. A value that cannot be resolved produces ordinary compiler diagnostics.
- The generator currently emits partial declarations using simple type names and cannot reliably support nested/generic source types or non-top-level/generic/nested/file-local subset classes. See the generator README for the exact generated contract and all shape limitations.

## Targets

The attribute assembly targets `netstandard2.0`, is nullable-annotated in its source project, and has no runtime package dependencies.

## License

This project is licensed under the MIT License. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
