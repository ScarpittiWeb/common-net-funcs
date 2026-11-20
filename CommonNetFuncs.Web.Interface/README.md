# CommonNetFuncs.Web.Interface

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Interface)](https://www.nuget.org/packages/CommonNetFuncs.Web.Interface/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Interface)](https://www.nuget.org/packages/CommonNetFuncs.Web.Interface/)

ASP.NET Core MVC helpers for DataTables-style requests, model-state error projection, and `SelectListItem` creation. The package targets **.NET 11.0**.

## Contents

- [Installation](#installation)
- [DataTables request helpers](#datatables-request-helpers)
- [Model-state errors](#model-state-errors)
- [Select-list items](#select-list-items)
- [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Interface
```

## DataTables request helpers

`GetDataTableRequest` is an `HttpRequest` extension. It reads only form requests whose content type contains `application/x-www-form-urlencoded` or `multipart/form-data`, case-insensitively. It returns an empty/default `DataTableRequest` when the content type is unsuitable or `request.Form` is unavailable. Any parsing exception is logged and the method returns the partially populated/default object rather than rethrowing.

Recognized fields:

| DataTables form field | Result |
| --- | --- |
| `draw` | `Draw` string |
| `start` | `Skip`, converted with `Convert.ToInt32` |
| `length` | `PageSize`, converted with `Convert.ToInt32` |
| `order[i][column]` | zero-based order index; value is used to look up `columns[n][data]` |
| `order[i][dir]` | `SortColumnDir[i]` |
| `columns[n][data]` | `SortColumns[i]` |
| `search[value]` | comma-separated `key=value` entries copied into `SearchValues` |

Field names are compared case-insensitively. Orders are read contiguously beginning at index 0. A missing column-data or direction value becomes `null`. Search entries without `=` are skipped; `key=` stores a `null` value. Duplicate search keys cause the parser to enter its catch path.

### Types

`DataTableRequest` initializes:

- `PageSize` and `Skip` to `0`
- `Draw` to `null`
- `SortColumns`, `SortColumnDir`, and `SearchValues` to empty dictionaries

`DataTableReturnData<T>` is the response shape expected by DataTables:

- `Draw` serializes as `draw`
- `RecordsFiltered` as `recordsFiltered`
- `RecordsTotal` as `recordsTotal`
- `Data` as `data` and defaults to an empty sequence

`SortAndLimitPostModel` initializes its two sort dictionaries and is annotated with both MemoryPack and MessagePack object metadata. It contains `SortColumns`, `SortColumnDir`, `Skip`, and `PageSize`.

### Parse a DataTables request

```csharp
using CommonNetFuncs.Web.Interface;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

DefaultHttpContext context = new();
context.Request.ContentType = "application/x-www-form-urlencoded";
context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
{
    ["draw"] = "4",
    ["start"] = "20",
    ["length"] = "10",
    ["order[0][column]"] = "2",
    ["order[0][dir]"] = "desc",
    ["columns[2][data]"] = "CreatedAt",
    ["search[value]"] = "tenantId=42,status=active"
});

DataTableRequest request = context.Request.GetDataTableRequest();
int pageSize = request.PageSize;       // 10
int skip = request.Skip;               // 20
string? sort = request.SortColumns[0];  // "CreatedAt"
string? direction = request.SortColumnDir[0]; // "desc"
string? status = request.SearchValues["status"];
```

### Convert to a paging/sorting model

`GetSortAndLimitPostModel` copies the dictionary references, values, skip, and page size. It does not deep-clone the dictionaries and does not include `Draw` or `SearchValues`.

```csharp
using CommonNetFuncs.Web.Interface;

DataTableRequest parsed = new()
{
    Skip = 20,
    PageSize = 10,
    SortColumns = new() { [0] = "CreatedAt" },
    SortColumnDir = new() { [0] = "desc" },
    SearchValues = new() { ["status"] = "active" }
};

SortAndLimitPostModel paging =
    DataTableHelpers.GetSortAndLimitPostModel(parsed);
```

### Return the DataTables response

```csharp
using CommonNetFuncs.Web.Interface;

DataTableReturnData<UserRow> response = new()
{
    Draw = request.Draw,
    RecordsTotal = 125,
    RecordsFiltered = 17,
    Data = rows
};
return Json(response);
```

### Apply sorting and paging safely

The package parses client input; it does not validate authorization, page-size limits, sort names, or directions, and it does not query data itself. Keep those rules in the application:

```csharp
Dictionary<string, Expression<Func<User, object>>> sortable = new()
{
    ["CreatedAt"] = user => user.CreatedAt,
    ["Name"] = user => user.Name
};

int pageSize = Math.Clamp(request.PageSize, 1, 100);
string? requestedColumn = request.SortColumns.GetValueOrDefault(0);
bool descending = string.Equals(
    request.SortColumnDir.GetValueOrDefault(0),
    "desc",
    StringComparison.OrdinalIgnoreCase);

if (requestedColumn is not null && sortable.TryGetValue(requestedColumn, out var sort))
{
    query = descending ? query.OrderByDescending(sort) : query.OrderBy(sort);
}

List<User> rows = await query
    .Skip(Math.Max(request.Skip, 0))
    .Take(pageSize)
    .ToListAsync(cancellationToken);
```

Do not use a request-provided `SortColumns` value as raw SQL or an unrestricted dynamic member name. Apply authorization and tenant filters before counting and paging.

## Model-state errors

`ParseModelStateErrors` copies every model-state key that has at least one error into a dictionary and concatenates that entry's messages with a trailing space after every message. It always adds `string.Empty` with `Invalid model state`, even if the model state is otherwise empty. Keys without errors are omitted.

```csharp
using CommonNetFuncs.Web.Interface;
using Microsoft.AspNetCore.Mvc.ModelBinding;

ModelStateDictionary modelState = new();
modelState.AddModelError("Name", "Name is required.");
modelState.AddModelError("Name", "Use the customer's legal name.");

Dictionary<string, string?> errors =
    ModelErrorHelpers.ParseModelStateErrors(modelState);

// errors["Name"] == "Name is required. Use the customer's legal name. "
// errors[""] == "Invalid model state"
```

## Select-list items

`SelectListItemCreation` is the static class containing the overloads.

The twelve `ToSelectListItem` overloads cover:

1. nullable `string` with or without display text and selection state;
2. non-nullable generic numbers with or without display text and selection state;
3. nullable generic numbers with or without display text and selection state.

Numeric generic parameters satisfy `struct, INumber<TNumber>`, so integer and floating-point numeric types are supported. A null receiver returns `null`; a non-null receiver always produces an item. If display text is omitted, the converted value is used. If an explicitly supplied `text` is null, `Text` is an empty string (contrary to using the value as a fallback). Numeric conversion and string values are preserved with the receiver's current `ToString()` behavior.

```csharp
using CommonNetFuncs.Web.Interface;
using Microsoft.AspNetCore.Mvc.Rendering;

string? textValue = "active";
string? nullText = null;
int number = 42;
int? nullableNumber = 42;
int? noNumber = null;

// String overloads
SelectListItem? s1 = textValue.ToSelectListItem();
SelectListItem? s2 = textValue.ToSelectListItem(selected: true);
SelectListItem? s3 = textValue.ToSelectListItem(text: "Active");
SelectListItem? s4 = textValue.ToSelectListItem("Active", selected: true);
SelectListItem? s5 = nullText.ToSelectListItem();
SelectListItem? s6 = nullText.ToSelectListItem("Missing", selected: false);

// Non-nullable numeric overloads
SelectListItem n1 = number.ToSelectListItem();
SelectListItem n2 = number.ToSelectListItem(selected: true);
SelectListItem n3 = number.ToSelectListItem(text: "Forty-two");
SelectListItem n4 = number.ToSelectListItem("Forty-two", selected: true);

// Nullable numeric overloads
SelectListItem? nn1 = nullableNumber.ToSelectListItem();
SelectListItem? nn2 = nullableNumber.ToSelectListItem(selected: true);
SelectListItem? nn3 = nullableNumber.ToSelectListItem(text: "Forty-two");
SelectListItem? nn4 = nullableNumber.ToSelectListItem("Forty-two", selected: true);
SelectListItem? nn5 = noNumber.ToSelectListItem(); // null
```

When mapping a collection, select a materialization shape that suits the caller:

```csharp
SelectListItem[] options = people
    .Select(person => person.Id.ToSelectListItem(person.Name))
    .ToArray();
```

## License

This project is licensed under the MIT License. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
