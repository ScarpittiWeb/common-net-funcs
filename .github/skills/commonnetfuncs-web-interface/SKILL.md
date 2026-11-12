---
name: commonnetfuncs-web-interface
description: 'Use CommonNetFuncs.Web.Interface for DataTables request parsing, MVC select lists, and model-state errors. Consult for pagination and sort-field validation.'
---

# CommonNetFuncs.Web.Interface

Use `DataTableHelpers`, `SelectListItemCreation`, and `ModelErrorHelpers` for
ASP.NET Core interface/controller plumbing. This package targets `net11.0`.

## Workflow

1. For a DataTables request, accept the documented form fields and call
   `GetDataTableRequest` on the `HttpRequest`, then translate the parsed
   pagination, search, and sort data to the application's query.
2. Cap page size and map requested sort/column names to a server-defined
   allow-list. Apply authorization and row filters independently.
3. Use `ParseModelStateErrors` for UI error reporting and `ToSelectListItem`
   for supported values; handle nullable overload results.

## Important constraints

- Request-provided sort names and search values are untrusted. Never append
  them directly to SQL; parameterize values and select identifiers from a
  fixed allow-list.
- Missing or malformed form fields follow the parser's defaults; they are not
  evidence of permission to request an unlimited result set.
- Inspect the model-state conversion before relying on it to preserve multiple
  errors per key. Numeric select-list overloads use generic math.

See [DataTableHelpers.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Interface/DataTableHelpers.cs)
and the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Interface/README.md).
