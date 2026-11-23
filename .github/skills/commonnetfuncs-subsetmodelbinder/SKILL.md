---
name: commonnetfuncs-subsetmodelbinder
description: 'Use CommonNetFuncs.SubsetModelBinder to validate subset DTO shapes and generate model metadata with SubsetOf and the Roslyn generator.'
---

# CommonNetFuncs.SubsetModelBinder

The umbrella package brings together `SubsetOfAttribute` and a source
generator. It validates subset-property shape at compile time and generates
metadata attributes; it is not a mapping library or an authorization system.

## Workflow

1. Reference the umbrella package in the consuming project. Mark a subset class
   `partial` and annotate it with `[SubsetOf(typeof(SourceType))]`; set
   `isMvcApp: true` when MVC `ModelMetadataType` is required.
2. Declare the subset properties with names and types matching the source.
   Inherited source properties are allowed by default; `ignoreType: true`
   deliberately disables type equality checking.
3. Verify that the generator is actually active in the consuming build: an
   intentionally missing subset property should produce `SG0002`. The umbrella
   project references the generator, but its source project references alone do
   not prove analyzer activation for every NuGet consumption path. If needed,
   reference the generator package as an analyzer alongside the attribute.
4. Fix `SG0001` type mismatches, `SG0002` missing properties, and `SG0003`
   non-partial classes. Inspect generated code for unusual nested or generic
   types and apply model validation and authorization separately.

```csharp
using CommonNetFuncs.SubsetModelBinder;

namespace Example;

public class Person
{
    public string? Name { get; set; }
}

[SubsetOf(typeof(Person), isMvcApp: true)]
public partial class PersonEdit
{
    public string? Name { get; set; }
}
```

See the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.SubsetModelBinder/README.md), the
[attribute skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-subsetmodelbinder-attribute/SKILL.md), and
the [generator skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-subsetmodelbinder-generator/SKILL.md).
