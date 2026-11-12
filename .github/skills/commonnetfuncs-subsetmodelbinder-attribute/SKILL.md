---
name: commonnetfuncs-subsetmodelbinder-attribute
description: 'Use CommonNetFuncs.SubsetModelBinder.Attribute and SubsetOfAttribute with the subset source generator; understand options and runtime limitations.'
---

# CommonNetFuncs.SubsetModelBinder.Attribute

This component defines `CommonNetFuncs.SubsetModelBinder.SubsetOfAttribute`.
Prefer the umbrella package for normal use; the attribute alone does not run
the source generator or enforce property matching.

## Workflow

1. Apply `[SubsetOf(typeof(SourceType))]` to a partial subset class and ensure
   the generator analyzer is active in the consumer.
2. Set `isMvcApp: true` for MVC metadata, `allowInheritedProperties: false` to
   exclude inherited source properties, or `ignoreType: true` only when
   intentionally relaxing type matching.
3. Build and address the generator diagnostics. The attribute itself performs
   no runtime validation, model binding, mapping, or authorization.

The exact constructor and defaults are in
[SubsetOfAttribute.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.SubsetModelBinder.Attribute/SubsetOfAttribute.cs).
See the [umbrella skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-subsetmodelbinder/SKILL.md) and
[generator skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-subsetmodelbinder-generator/SKILL.md).
