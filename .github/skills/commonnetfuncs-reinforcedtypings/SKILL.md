---
name: commonnetfuncs-reinforcedtypings
description: 'Use CommonNetFuncs.ReinforcedTypings to generate TypeScript constants, collections, and Valibot schemas from .NET types and DataAnnotations.'
---

# CommonNetFuncs.ReinforcedTypings

This is build-time integration with Reinforced.Typings, not a runtime service.
It adds `TsConst`, `TsCollection`, and `GenerateValibotSchema` support.

## Workflow

1. Configure Reinforced.Typings and its target directory. In
   `ReinforcedTypings.settings.xml`, select
   `ReinforcedTypingsFluentConfig.Configure` and include the package assembly
   through `RtAdditionalAssembly` as shown in the
   [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.ReinforcedTypings/README.md).
2. Decorate the intended classes/interfaces and configure exports and ignores.
   Build and review the generated TypeScript and schema files.
3. Test generated validators with representative valid and invalid data, and
   actually invoke the schema at runtime where validation is required.

## Important constraints

- Missing assembly/configuration can result in missing generated output;
  verify the files, not just build success.
- Not every .NET type or DataAnnotation necessarily has an equivalent
  TypeScript/Valibot representation. Inspect unsupported or `unknown` output
  and keep server-side validation.
- Exported constants become client-visible source. Do not decorate types
  containing secrets or values that should remain server-only.

See [ReinforcedTypingsFluentConfig.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.ReinforcedTypings/ReinforcedTypingsFluentConfig.cs)
and [ValibotSchemaGenerator.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.ReinforcedTypings/Valibot/ValibotSchemaGenerator.cs).
