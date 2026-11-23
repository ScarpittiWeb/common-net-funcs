---
name: commonnetfuncs-subsetmodelbinder-generator
description: 'Use the CommonNetFuncs.SubsetModelBinder.Generator Roslyn analyzer to generate subset metadata and diagnose SG0001, SG0002, and SG0003.'
---

# CommonNetFuncs.SubsetModelBinder.Generator

This is a build-time C# source generator, not a runtime model binder. It
discovers `SubsetOfAttribute`, validates declared subset properties, and emits a
metadata-decorated partial class.

## Workflow

1. Prefer the umbrella package, or reference both the attribute assembly and
   this generator package. The generator package places its DLL under
   `analyzers/dotnet/cs`; the attribute must also resolve in the consumer.
2. Mark the subset class `partial` and verify analyzer activation in the actual
   consuming build, for example by checking that a deliberately missing
   source property reports `SG0002`.
3. Address `SG0001` (type mismatch), `SG0002` (missing source property), and
   `SG0003` (class not partial). Review generated code and test less common
   nested/generic types and namespaces.
4. The default generated attribute is `MetadataType`; `isMvcApp: true`
   selects MVC `ModelMetadataType`. These attributes do not perform mapping or
   authorization and do not validate every aspect of a model's behavior.

See [SubsetValidatorGenerator.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.SubsetModelBinder.Generator/SubsetValidatorGenerator.cs)
and the [umbrella skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-subsetmodelbinder/SKILL.md).
