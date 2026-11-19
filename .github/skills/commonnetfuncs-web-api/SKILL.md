---
name: commonnetfuncs-web-api
description: 'Use CommonNetFuncs.Web.Api for ASP.NET Core MVC and minimal API CRUD helpers, validation, MessagePack, OpenAPI, and task queues.'
---

# CommonNetFuncs.Web.Api

This `net11.0` package provides generic MVC and minimal API helpers over EF
actions, plus validation filters, MessagePack support, OpenAPI helpers, and
queues. Pick and configure only the surfaces the application needs.

## Workflow

1. Configure an EF `DbContext` and an
   `IBaseDbContextActions<TEntity, TContext>` implementation. For MVC, use
   `GenericEndpoints`/`GenericDtoEndpoints`; for minimal APIs, use their
   `GenericMinimal...` counterparts.
2. Register the relevant validation, serialization middleware/filters, and
   OpenAPI services explicitly. See the
   [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Api/README.md) for the corresponding
   setup.
3. Apply endpoint authentication, authorization, allowed-field DTOs, tenant
   filters, input limits, and transaction policy in the application. Test
   actual status and error behavior.

## Important constraints

- Some generic CRUD operations return `NoContent` on failed persistence or
  caught exceptions, as well as `Ok` on success. A 204 response is not by
  itself proof that a write succeeded; choose a different wrapper when the API
  must expose a distinct failure status.
- DTO mapping, validation filters, and navigation-property removal are not
  substitutes for authorization or explicit writable-field allow-lists.
- The default EF actions dispose contexts used by tracked writes before their
  separate `SaveChanges` call. Do not assume the generic create/update flow
  persists data with that implementation; use a corrected action
  implementation or an explicit EF unit of work. See the
  [EFCore skill](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/.github/skills/commonnetfuncs-efcore/SKILL.md).

See [GenericEndpoints.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Api/GenericEndpoints.cs)
and [GenericMinimalEndpoints.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Api/GenericMinimalEndpoints.cs).
