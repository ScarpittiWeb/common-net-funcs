# CommonNetFuncs.Core

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Core)](https://www.nuget.org/packages/CommonNetFuncs.Core/)
[![NuGet downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Core)](https://www.nuget.org/packages/CommonNetFuncs.Core/)

General-purpose C# extension methods and support types. The package targets `net11.0` and `netstandard2.1`. `UnitConversion` is compiled only for `net11.0`; `MathHelpers.GenerateRange<TNumber>`, `GreatestCommonDenominator`, and `GetMedian<TNumber>` are likewise unavailable in the netstandard build.

## Contents

- [CommonNetFuncs.Core](#commonnetfuncscore)
  - [Contents](#contents)
  - [Async](#async)
  - [Cache managers and fixed-capacity dictionaries](#cache-managers-and-fixed-capacity-dictionaries)
  - [Collections](#collections)
  - [Copy](#copy)
  - [Dates](#dates)
  - [Dimension scaling](#dimension-scaling)
  - [Exception locations](#exception-locations)
  - [File helpers](#file-helpers)
  - [Inspection](#inspection)
  - [Math](#math)
  - [Randomization](#randomization)
  - [Batches](#batches)
  - [Streams](#streams)
  - [String constants and enums](#string-constants-and-enums)
  - [Slicing, casing, and whitespace](#slicing-casing-and-whitespace)
  - [String/collection tests and invariant searches](#stringcollection-tests-and-invariant-searches)
  - [Character classes and extraction](#character-classes-and-extraction)
  - [Object string normalization](#object-string-normalization)
  - [Conversion to/from nullable values](#conversion-tofrom-nullable-values)
  - [Hashing, date formats, regex, URL, phone, and lines](#hashing-date-formats-regex-url-phone-and-lines)
  - [Fractions and numeric extraction](#fractions-and-numeric-extraction)
  - [Log sanitization and written lists](#log-sanitization-and-written-lists)
  - [Type checks and type caches](#type-checks-and-type-caches)
  - [Unit conversion (`net11.0` only)](#unit-conversion-net110-only)
  - [Validation](#validation)
  - [License](#license)

```bash
dotnet add package CommonNetFuncs.Core
```

```csharp
using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Globalization;
using System.IO.Pipelines;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using CommonNetFuncs.Core;
using CommonNetFuncs.Core.CollectionClasses;
```

## Async

`ObjectFill` awaits a task and writes into the receiver. Class-to-class filling shallow-copies matching properties; list/bag/set overloads append results; dictionary overloads assign by key; `DataTable.Load` is used for tables; `MemoryStream.WriteTo` appends at the target's current position. These helpers catch/log exceptions rather than re-throwing them. A cancellation exception in an overload that accepts a token is therefore also swallowed. Some semaphore overloads release their semaphore in `finally` even when acquisition throws.

For DataTable and MemoryStream fills, the helper disposes the awaited result object after copying it.

Every receiver/task-factory overload is shown below.

```csharp
Person person = new();
Task<Person> personTask = Task.FromResult(new Person { Name = "Ada" });
Func<Task<Person>> personFactory = () => Task.FromResult(new Person());
using SemaphoreSlim gate = new(2);

await person.ObjectFill(personTask);
await person.ObjectFill(personFactory);
await person.ObjectFill(personFactory, gate);

List<int?> listItem = [];
await listItem.ObjectFill(Task.FromResult<int?>(1));
await listItem.ObjectFill(() => Task.FromResult<int?>(2));
await listItem.ObjectFill(() => Task.FromResult<int?>(3), gate, cancellationToken);

HashSet<int?> setItem = [];
await setItem.ObjectFill(Task.FromResult<int?>(1));
await setItem.ObjectFill(() => Task.FromResult<int?>(2));
await setItem.ObjectFill(() => Task.FromResult<int?>(3), gate, cancellationToken);

List<int> listMany = [];
await listMany.ObjectFill(Task.FromResult<IEnumerable<int>>([1, 2]));
await listMany.ObjectFill(() => Task.FromResult<IEnumerable<int>>([3]));
await listMany.ObjectFill(
    () => Task.FromResult<IEnumerable<int>>([4]), gate, cancellationToken);

HashSet<int> setMany = [];
await setMany.ObjectFill(Task.FromResult<IEnumerable<int>>([1, 2]));
await setMany.ObjectFill(() => Task.FromResult<IEnumerable<int>>([3]));
await setMany.ObjectFill(
    () => Task.FromResult<IEnumerable<int>>([4]), gate, cancellationToken);

ConcurrentBag<int?> bagItem = [];
await bagItem.ObjectFill(Task.FromResult<int?>(1));
await bagItem.ObjectFill(() => Task.FromResult<int?>(2));
await bagItem.ObjectFill(() => Task.FromResult<int?>(3), gate, cancellationToken);

ConcurrentBag<int> bagMany = [];
await bagMany.ObjectFill(Task.FromResult<IEnumerable<int>>([1]));
await bagMany.ObjectFill(() => Task.FromResult<IEnumerable<int>>([2]));
await bagMany.ObjectFill(
    () => Task.FromResult<IEnumerable<int>>([3]), gate, cancellationToken);

ConcurrentDictionary<string, int?> dictionary = [];
await dictionary.ObjectFill("a", Task.FromResult<int?>(1));
await dictionary.ObjectFill("b", () => Task.FromResult<int?>(2));
await dictionary.ObjectFill(
    "c", () => Task.FromResult<int?>(3), gate, cancellationToken);

DataTable table = new();
await table.ObjectFill(Task.FromResult(new DataTable()));
await table.ObjectFill(() => Task.FromResult(new DataTable()));
await table.ObjectFill(
    () => Task.FromResult(new DataTable()), gate, cancellationToken);

using MemoryStream stream = new();
await stream.ObjectFill(Task.FromResult(new MemoryStream([1, 2])));
await stream.ObjectFill(
    () => Task.FromResult(new MemoryStream([3, 4])));
await stream.ObjectFill(
    () => Task.FromResult(new MemoryStream([5, 6])),
    gate,
    cancellationToken);
```

`ObjectUpdate` performs a case-insensitive property-name lookup, catches/logs lookup, conversion, task, and reflection errors, and sets the property when the task completes.

```csharp
Person target = new();
await target.ObjectUpdate(nameof(Person.Name), Task.FromResult("Ada"));
await target.ObjectUpdate(nameof(Person.Name), () => Task.FromResult("Grace"));
await target.ObjectUpdate(
    nameof(Person.Name),
    () => Task.FromResult("Linus"),
    gate,
    cancellationToken);
```

Task collections and semaphore wrappers:

```csharp
Func<Task<int>>[] valueFactories = [() => Task.FromResult(1)];
ConcurrentBag<int> values = await valueFactories.RunAll(
    gate, cancellationTokenSource, breakOnError: true);

Func<Task>[] factories = [() => Task.CompletedTask];
await factories.RunAll(gate, cancellationTokenSource, breakOnError: true);

int? result = await Task.FromResult<int?>(7)
    .RunAsyncWithSemaphore(
        gate, cancellationTokenSource, breakOnError: true, errorText: "work");
await Task.CompletedTask.RunAsyncWithSemaphore(
    gate, cancellationTokenSource, breakOnError: false, "work");
```

Exceptions are logged and normally not propagated by the wrappers. `RunAsyncWithSemaphore<T>` returns the default value after an error.

`ResultTaskGroup<T>` and `TaskGroup` own mutable task lists and optionally a semaphore. Their `RunTasks` methods preserve `ResultTaskGroup` results in list order, start tasks whose status is `Created`, and clear `TaskGroup.Tasks` after success. With no semaphore, cancellation is checked before/between starts; with a semaphore, the passed token is also used by semaphore acquisition. A task failure is propagated.

```csharp
ResultTaskGroup<int> resultGroup =
    new([new Task<int>(() => 1), Task.FromResult(2)], gate);
int[] ordered = await resultGroup.RunTasks(cancellationToken);
resultGroup.Tasks.Add(Task.FromResult(3));

TaskGroup taskGroup = new([Task.CompletedTask], gate);
await taskGroup.RunTasks(cancellationToken);

AsyncIntString primitiveHolder = new()
{
    AsyncInt = 1,
    AsyncDecimal = 2m,
    AsyncFloat = 3f,
    AsyncString = "value"
};
```

## Cache managers and fixed-capacity dictionaries

`CacheManager<TKey,TValue>` uses a fixed-capacity LRU limited cache; `CacheManagerFifo<TKey,TValue>` uses FIFO. Both defaults are `limitedCacheSize: 100`, `useLimitedCache: true`. They also maintain a separate unlimited `ConcurrentDictionary`. Both derive from the public abstract `CacheManagerBase<TKey,TValue>` and implement the public `ICacheManagerApi<TKey,TValue>` contract.

```csharp
CacheManager<string, int> cache = new(limitedCacheSize: 2, useLimitedCache: true);
CacheManagerFifo<string, int> fifo = new(2, true);

cache.SetLimitedCacheSize(4);       // Values below 1 switch to unlimited mode.
cache.SetUseLimitedCache(false);    // Clears both caches.
int configured = cache.GetLimitedCacheSize();
bool limited = cache.IsUsingLimitedCache();

int a = cache.GetOrAddCache("a", static key => key.Length);
int b = cache.GetOrAddLimitedCache("b", static key => key.Length);
bool addedA = cache.TryAddCache("c", 3);
bool addedB = cache.TryAddLimitedCache("d", 4);
bool foundA = cache.TryGetCacheValue("a", out int aValue);
bool foundB = cache.TryGetLimitedCacheValue("b", out int bValue);
IReadOnlyDictionary<string, int> unlimited = cache.GetCache();
var bounded = cache.GetLimitedCache();
cache.ClearCache();
cache.ClearLimitedCache();
cache.ClearAllCaches();
```

`FixedLruDictionary` promotes successful reads and existing-key writes to most-recently-used; `FixedFifoDictionary` leaves update order unchanged. Both implement `FixedSizeDictionaryBase<TKey,TValue>` and the `IDictionary<TKey,TValue?>` surface. Adding beyond capacity evicts the oldest/current least-recently-used key. A non-positive capacity throws `ArgumentOutOfRangeException`; a source larger than capacity throws `ArgumentException`.

```csharp
var initial = new Dictionary<string, int?> { ["x"] = 1 };
FixedLruDictionary<string, int?> lru = new(2, initial);
FixedFifoDictionary<string, int?> fixedFifo = new(2, initial);

// Demonstrated for both concrete dictionaries.
fixedFifo.Add("a", 2);
fixedFifo.Add(new KeyValuePair<string, int?>("b", 3));
fixedFifo["a"] = 4;
int? item = fixedFifo["a"];
bool got = fixedFifo.TryGetValue("a", out int? gotValue);
int? generated = fixedFifo.GetOrAdd("c", static key => key.Length);
bool addWorked = fixedFifo.TryAdd("d", 5);
bool hasKey = fixedFifo.ContainsKey("a");
bool hasPair = fixedFifo.Contains(
    new KeyValuePair<string, int?>("a", 4));
bool removedKey = fixedFifo.Remove("a");
bool removedPair = lru.Remove(
    new KeyValuePair<string, int?>("x", 1));
KeyValuePair<string, int?>[] copyTarget = new KeyValuePair<string, int?>[10];
fixedFifo.CopyTo(copyTarget, 0);
foreach (KeyValuePair<string, int?> pair in fixedFifo) { }
using IEnumerator<KeyValuePair<string, int?>> enumerator =
    fixedFifo.GetEnumerator();
ICollection<string> keys = fixedFifo.Keys;
ICollection<int?> dictValues = fixedFifo.Values;
int count = fixedFifo.Count;
bool readOnly = fixedFifo.IsReadOnly;
fixedFifo.TrimExcess();
fixedFifo.Clear();

lru.Add("one", 1);
lru.Add(new KeyValuePair<string, int?>("two", 2));
_ = lru.GetOrAdd("three", _ => 3);
_ = lru.TryGetValue("one", out _);
```

## Collections

The six `AnyFast` overloads avoid LINQ enumeration and return `false` for null receivers.

```csharp
ICollection<int>? collection = new List<int>();
IList<int>? ilist = collection as IList<int>;
ConcurrentBag<int>? bag = [];
int[]? array = [1];
IDictionary<string, int>? map = new Dictionary<string, int>();
ConcurrentDictionary<string, int>? concurrentMap = [];
bool a = collection.AnyFast();
bool b = ilist.AnyFast();
bool c = bag.AnyFast();
bool d = array.AnyFast();
bool e = map.AnyFast();
bool f = concurrentMap.AnyFast();
```

Dictionary and range helpers silently skip duplicate dictionary keys (unless overwrite is requested), skip null collection items, and observe cancellation.

```csharp
Dictionary<string, int> destination = [];
destination.AddDictionaryItem(new("a", 1));
destination.AddDictionaryItems([new("b", 2)], cancellationToken);

ConcurrentBag<string> concurrentBag = [];
concurrentBag.AddRange(["1", "2", null]);
concurrentBag.AddRangeParallel(
    ["3", "4", null], new ParallelOptions(), cancellationToken);
HashSet<string> set = [];
set.AddRange(["1", null], cancellationToken);

Dictionary<string, int?> target = new() { ["x"] = 1 };
target.AddRange(
    new KeyValuePair<string, int?>?[] { new("x", 2), new("y", 3), null },
    overwriteExisting: false,
    cancellationToken);
target.AddRange(
    new Dictionary<string, int?> { ["x"] = 4 },
    overwriteExisting: true,
    cancellationToken);
```

Mutation and filtering:

```csharp
List<Person> people = [new()];
people.SetValue(static p => p.Name = "updated", cancellationToken);
List<string?> text = [" a ", null];
text.SetValue(static s => s?.Trim(), cancellationToken);
int[,] matrix = new int[2, 2];
matrix.SetValue((arr, index) =>
    arr.SetValue(((int?)arr.GetValue(index) ?? 0) + 1, index),
    cancellationToken);
IEnumerable<Person> updated = people.SetValueEnumerate(
    static p => p.Name = "updated", cancellationToken);
IEnumerable<string?> updatedText = text.SetValueEnumerate(
    static s => s?.Trim(), cancellationToken);
people.SetValueParallel(
    static p => p.Name = "parallel",
    maxDegreeOfParallelism: 2,
    cancellationToken);

IEnumerable<string>? nonEmpty =
    new string?[] { "", " ", "x", null }.SelectNonEmpty();
IEnumerable<string>? nonNull =
    new string?[] { "", null }.SelectNonNull();
List<int> wrapped = 4.SingleToList();
List<string> wrappedEmpty = " ".SingleToList(allowEmptyValues: false);
```

The string-only `SingleToList` excludes whitespace by default; `allowEmptyValues: true` permits `""` and whitespace but still returns an empty list for null.

`GetObjectByPartial` builds an equality predicate from non-null properties; `ignoreDefaultValues: true` additionally excludes default value-type members. It returns the first match or null.

```csharp
Person? found = people.AsQueryable().GetObjectByPartial(
    new Person { Name = "updated" },
    ignoreDefaultValues: true,
    cancellationToken);
```

`ClearTrim` supports arrays, lists, dictionaries, sets, stacks, and queues. It is null-safe. `forceGc: true` explicitly requests garbage collection and should generally be avoided.

```csharp
int[]? arr = [1, 2];
Collections.ClearTrim(ref arr, forceGc: false);
new List<int>().ClearTrim();
new Dictionary<string, int>().ClearTrim();
new HashSet<int>().ClearTrim();
new Stack<int>().ClearTrim();
new Queue<int>().ClearTrim();
```

Data-table conversions match public property names and require a parameterless class. `convertShortToBool` converts `Int16` database values for bool properties. Cancellation propagates. Parallel list results are not guaranteed to retain row order.

```csharp
List<Person> rows = table.ToList<Person>(false, cancellationToken);
List<Person> parallelRows =
    table.ToListParallel<Person>(2, false, cancellationToken);
IEnumerable<Person> parallelStream =
    table.ToEnumerableParallel<Person>(false, cancellationToken);
IEnumerable<Person> oneAheadStream =
    table.ToEnumerableStreaming<Person>(false, cancellationToken);

DataTable? created = people.ToDataTable();
DataTable? appended = people.ToDataTable(
    table,
    useParallel: true,
    approximateCount: people.Count,
    degreeOfParallelism: 2,
    cancellationToken);
```

`ToDataTable` returns null for null input. When a table is supplied, columns that do not match `T` are removed before matching columns are added.

Other collection APIs:

```csharp
Expression<Func<Person, bool>>? predicate = Collections.CombineExpressions(
    new Expression<Func<Person, bool>>[] { p => p.Active, p => p.Name != null });

IEnumerable<Person> oneProperty =
    people.StringAggProps("Name", separator: ";", distinct: true, parallel: false);
IEnumerable<Person> severalProperties =
    people.StringAggProps(
        new HashSet<string> { "Name", "Description" },
        "|", false, true);

int index1 = people.IndexOf(person);
int index2 = people.IndexOf(person, EqualityComparer<Person>.Default);
bool valid = "Monday".IsIn<DayOfWeek>();

IEnumerable<IEnumerable<string?>> sources =
    [["A", "B"], ["1", null]];
HashSet<string> combinations = sources.GetCombinations(
    maxCombinations: 10, separator: "|", nullReplacement: "none");
HashSet<string> randomCombinations = sources.GetRandomCombinations(
    5, "|", "none");
IEnumerable<string> streamingCombinations = sources.GetEnumeratedCombinations(
    5, "|", "none");

bool hasDuplicates = new[] { 1, 1, 2 }.ContainsDuplicates();
HashSet<int> duplicates = new[] { 1, 1, 2 }.GetUniqueDuplicates();
Dictionary<int, int> duplicateCounts =
    new[] { 1, 1, 2 }.GetDuplicatesWithCount(includeUniqueInCount: true);
```

`ReplaceParameterVisitor` is public and useful while composing expressions. `ArrayTraverse` exposes its mutable current `Position`; `Step` returns false after the last element.

```csharp
ParameterExpression oldParameter =
    Expression.Parameter(typeof(Person), "p");
ParameterExpression newParameter =
    Expression.Parameter(typeof(Person), "x");
ReplaceParameterVisitor visitor = new(oldParameter, newParameter);
Expression<Func<Person, bool>> originalExpression = p => p.Active;
Expression replaced = visitor.Visit(originalExpression.Body)!;

ArrayTraverse walker = new(matrix);
do
{
    int[] current = walker.Position;
} while (walker.Step());
```

## Copy

Matching is exact by property name and type. Shallow methods directly assign reference-valued properties. `CopyPropertiesTo` returns no new object and does nothing for null source/destination in cache mode. Cache can be bypassed with `useCache: false`.

```csharp
SourceDto sourceDto = new() { Name = "Ada" };
DestinationDto destinationDto = new();
sourceDto.CopyPropertiesTo(destinationDto, useCache: true);

SourceDto sameTypeCopy = sourceDto.CopyPropertiesToNew(useCache: true);
DestinationDto? newTypeCopy =
    sourceDto.CopyPropertiesToNew<SourceDto, DestinationDto>(false);
DestinationDto? deepCopy =
    sourceDto.CopyPropertiesToNewRecursive<SourceDto, DestinationDto>(
        maxDepth: 4, useCache: true);
```

Recursive copy handles enumerable/dictionary properties but does not track cycles. A `maxDepth` of `-1` is unlimited. The cached path uses expression-tree copy functions. Null source returns null/default.

Both merge overloads mutate and return the target. Only target properties whose current value equals the property's default are filled.

```csharp
SourceDto merged =
    new SourceDto().MergeInstances([new SourceDto { Name = "Ada" }],
        cancellationToken);
merged = merged.MergeInstances(
    new SourceDto { Id = 42 }, cancellationToken);
```

Expose and configure the two public caches, and obtain compiled shallow maps:

```csharp
Copy.DeepCopyCacheManager.ClearAllCaches();
Copy.CopyCacheManager.SetLimitedCacheSize(32);
Dictionary<string, (
    Action<DestinationDto, object?> Set,
    Func<SourceDto, object?> Get)> maps =
        Copy.GetOrCreatePropertyMaps<SourceDto, DestinationDto>();
```

`ReflectionCaches` is used by the helpers that repeatedly inspect public instance properties. Its public cache can be configured independently.

```csharp
PropertyInfo[] properties =
    ReflectionCaches.GetOrAddPropertiesFromReflectionCache(typeof(Person));
IReadOnlyDictionary<Type, PropertyInfo[]> reflectionCache =
    ReflectionCaches.CacheManager.GetCache();
ReflectionCaches.CacheManager.ClearAllCaches();
```

## Dates

Business-day ranges are inclusive, exclude Saturday/Sunday, subtract matching weekdays from the exception list, and return 0 for a null bound. Reverse ranges are not normalized. DateTime's partial-day branch truncates its decimal result to `int`.

```csharp
DateOnly doDate = new(2026, 3, 18);
int doDays = DateOnlyHelpers.GetBusinessDays(
    new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 22), [doDate]);
DateOnly monday = doDate.GetDayOfWeek(DayOfWeek.Monday);
(DateOnly doFirst, DateOnly doLast) =
    DateOnlyHelpers.GetMonthBoundaries(3, 2026);
(DateOnly doFirst2, DateOnly doLast2) = doDate.GetMonthBoundaries();
DateOnly doStart = DateOnlyHelpers.GetFirstDayOfMonth(3, 2026);
DateOnly doStart2 = doDate.GetFirstDayOfMonth();
DateOnly doEnd = DateOnlyHelpers.GetLastDayOfMonth(3, 2026);
DateOnly doEnd2 = doDate.GetLastDayOfMonth();
DateOnly todayLocal = DateOnlyHelpers.GetToday(false);
DateOnly todayUtc = DateOnlyHelpers.GetToday(true);

DateTime dt = new(2026, 3, 18, 12, 0, 0);
int dtDays = DateTimeHelpers.GetBusinessDays(
    new(2026, 3, 16), new(2026, 3, 22), [dt]);
DateTime dtMonday = dt.GetDayOfWeek(DayOfWeek.Monday);
(DateTime dtFirst, DateTime dtLast) =
    DateTimeHelpers.GetMonthBoundaries(3, 2026);
(DateTime dtFirst2, DateTime dtLast2) = dt.GetMonthBoundaries();
DateTime dtStart = DateTimeHelpers.GetFirstDayOfMonth(3, 2026);
DateTime dtStart2 = dt.GetFirstDayOfMonth();
DateTime dtEnd = DateTimeHelpers.GetLastDayOfMonth(3, 2026);
DateTime dtEnd2 = dt.GetLastDayOfMonth();
bool oa1 = ((double?)657435.0).IsValidOaDate();
bool oa2 = 657435.0.IsValidOaDate();
```

`DateTime` boundary values use `DateTimeKind.Unspecified`; extension overloads discard the source's time and return midnight. OADate validity is deliberately restricted to 657435 through 2958465.99999999.

## Dimension scaling

All dimensions must be positive. `scaleUpToFit: false` prevents enlargement but still shrinks. Decimal overloads optionally round. Integer overloads round to zero decimal places and cast to `int`.

```csharp
(int w2, int h2) =
    DimensionScale.ScaleDimensionsToConstraint(
        640, 480, 100, 100, scaleUpToFit: false);
(decimal dw2, decimal dh2) =
    DimensionScale.ScaleDimensionsToConstraint(
        640m, 480m, 100m, 100m, true, 2);
(int w3, int h3, int d3) =
    DimensionScale.ScaleDimensionsToConstraint(
        10, 20, 30, 20, 20, 20, true);
(decimal dw3, decimal dh3, decimal dd3) =
    DimensionScale.ScaleDimensionsToConstraint(
        10m, 20m, 30m, 20m, 20m, 20m, true, 1);
```

## Exception locations

`GetLocationOfException` returns a string from the exception stack trace. If no suitable stack frame is available, it can return an empty string.

```csharp
try { throw new InvalidOperationException(); }
catch (Exception exception)
{
    string location = exception.GetLocationOfException();
    string template = ExceptionLocation.ErrorLocationTemplate;
}
```

## File helpers

Safe-name overloads clean the filename characters `/ \ : < > " | ? *`, optionally create a missing directory, and append ` (n)` before the extension. The extension receiver returns a full path; the static `(path, fileName, ...)` overload returns only a filename. This check is not atomic.

```csharp
string full = @"C:\data\report.txt".GetSafeSaveName(
    startFromZero: true, suppressLogging: true, createPathIfMissing: true);
string fileOnly = FileHelpers.GetSafeSaveName(
    @"C:\data", "report.txt", false, true, true);
bool extensionAllowed =
    "REPORT.TXT".ValidateFileExtension(new HashSet<string> { ".txt" });
```

Hashing returns lowercase hex, defaulting to SHA-512. Missing/unreadable files or hashing failures return `string.Empty`. Stream hashing requires a seekable stream, seeks to zero, and leaves it at the end.

```csharp
string fileHash = await @"C:\data\report.txt".GetHashFromFile(
    EHashAlgorithm.SHA256);
using FileStream hashStream = File.OpenRead(@"C:\data\report.txt");
string streamHash = await hashStream.GetHashFromStream(
    EHashAlgorithm.SHA512);
```

Recursive file enumeration is lazy, returns no values for a missing directory, logs inaccessible directories, and supports cancellation:

```csharp
IEnumerable<string> textFiles =
    FileHelpers.GetAllFilesRecursive(@"C:\data", "*.txt", cancellationToken);
```

`ReadFileFromPipe` writes directly to caller-owned output, checks the byte limit before writing each newly read pipe buffer, flushes, and rewinds seekable output. It does not complete/dispose the `PipeReader`. Without an `errorReturn`, errors become `FileLoadException`; the factory receives the original exception.

```csharp
PipeReader reader = PipeReader.Create(new MemoryStream([1, 2, 3]));
using var upload = new MemoryStream();
(bool limitedOk, string? limitedResult) = await reader.ReadFileFromPipe(
    upload, 10, "ok", "too large", ex => "error", cancellationToken);

reader = PipeReader.Create(new MemoryStream([4, 5]));
(bool unlimitedOk, string? unlimitedResult) = await reader.ReadFileFromPipe(
    upload, "ok", ex => "error", cancellationToken);
```

## Inspection

```csharp
object? defaultInt = typeof(int).GetDefaultValue();
int defaultProperties = new Person().CountDefaultProps();
bool hasDescription =
    typeof(Described).ObjectHasAttribute(nameof(DescriptionAttribute));

Person a = new() { Name = "Ada" };
Person b = new() { Name = "Ada" };
bool equal = a.IsEqual(
    b,
    exemptProps: [nameof(Person.Id)],
    ignoreStringCase: true,
    recursive: true);

string objectHash = a.GetHashForObject(EHashAlgorithm.SHA256);
string asyncObjectHash =
    await a.GetHashForObjectAsync(EHashAlgorithm.SHA256);
```

`IsEqual` handles nulls and cycles, requires the same runtime type, and compares public readable non-indexed properties. `recursive: false` leaves supported complex reference properties effectively unchecked. Object hashing defaults to MD5, omits null top-level property values, orders property names, recursively walks object properties, and sorts collection item hashes. It does not track cycles and is not a canonical, authenticated or password representation. A null receiver returns `"null"`. Culture can affect numeric string rendering and property ordering.

## Math

```csharp
double ceilD = ((double?)10.1).Ceiling(5);
decimal ceilM = ((decimal?)10.1m).Ceiling(5m);
double floorD = ((double?)10.1).Floor(5);
decimal floorM = ((decimal?)10.1m).Floor(5m);

int nullableDecimalPrecision = ((decimal?)1.2300m).GetPrecision(); // 4
int nullableDoublePrecision = ((double?)1.23).GetPrecision(".");
int decimalPrecision = 1.2300m.GetPrecision(); // 4
int doublePrecision = 1.23.GetPrecision(".");

IEnumerable<int> ints = MathHelpers.GenerateRange(1, 3);
IEnumerable<long> longs = MathHelpers.GenerateRange(1L, 3L);
IEnumerable<float> floats = MathHelpers.GenerateRange(1f, 3f);
IEnumerable<double> doubles = MathHelpers.GenerateRange(1d, 3d);
IEnumerable<decimal> decimals = MathHelpers.GenerateRange(1m, 3m);
#if NET11_0_OR_GREATER
IEnumerable<int> genericRange = MathHelpers.GenerateRange<int>(1, 3);
int numerator = 12, denominator = 8;
MathHelpers.GreatestCommonDenominator(
    ref numerator, ref denominator, out int gcd);
#endif

bool almost1 = ((double?)1.0).Equals((double?)1.00001, 0.001m);
bool almost2 = 1.0.Equals(1.00001, 0.001m);
bool notAlmost1 = ((double?)1.0).NotEquals((double?)1.1, 0.001m);
bool notAlmost2 = 1.0.NotEquals(1.1, 0.001m);

int medianInt = new[] { 1, 4, 2 }.GetMedian();
long medianLong = new long[] { 1, 4, 2 }.GetMedian();
float medianFloat = new[] { 1f, 4f, 2f }.GetMedian();
double medianDouble = new[] { 1d, 4d, 2d }.GetMedian();
decimal medianDecimal = new[] { 1m, 4m, 2m }.GetMedian();
#if NET11_0_OR_GREATER
short medianGeneric = new short[] { 1, 4, 2 }.GetMedian();
#endif
```

Ranges are inclusive and throw when start exceeds end. Medians sort a copy, reject null/empty input, and use integer division for the average of two integral middle values. Nullable/null `double` tolerance equality treats two null values as equal.

## Randomization

The cryptographic `rng` field is publicly exposed. Integer helpers use modulo reduction without rejection sampling and are therefore biased for ranges that do not divide the 31-bit input space; string-by-character-set helpers use cryptographic `RandomNumberGenerator`, but repeatable helpers use `System.Random`. Do not use repeatable output or bounded modulo integer helpers as a security primitive. Generic seeds rely on `GetHashCode()`, which is not guaranteed stable across processes.

All integer overloads:

```csharp
using SystemRandom = System.Random;
SystemRandom random = new(42);

int ri1 = Random.GetRandomInt(0, 10);
int ri2 = Random.GetRandomInt(10);
int ri3 = Random.GetRandomInt();
int ri4 = Random.GetRepeatableRandomInt(0, 10, 42);
int ri5 = Random.GetRepeatableRandomInt(0, 10, random);
int ri6 = Random.GetRepeatableRandomInt(10, 42);
int ri7 = Random.GetRepeatableRandomInt(10, random);
int ri8 = Random.GetRepeatableRandomInt(42);
int ri9 = Random.GetRepeatableRandomInt(random);

IEnumerable<int> ris1 = Random.GetRandomInts(3, 0, 10, cancellationToken);
IEnumerable<int> ris2 =
    Random.GetRepeatableRandomInts(3, 42, 0, 10, cancellationToken);
IEnumerable<int> ris3 =
    Random.GetRepeatableRandomInts(3, random, 0, 10, cancellationToken);
```

All double/decimal overloads. Raw double normalization divides a 64-bit unsigned value by `ulong.MaxValue`, so the implementation can theoretically return exactly `1.0`; do not rely on a strict `[0, 1)` upper bound. `GetRandomDouble(int)` caps at 15 decimal places and rejects values below 1. Repeatable double methods delegate to `Math.Round` without that same cap. Decimal repeatable methods cap at 28 places and reject values below 1:

```csharp
double rd1 = Random.GetRandomDouble();
double rd2 = Random.GetRandomDouble(4);
double rd3 = Random.GetRepeatableRandomDouble(42);
double rd4 = Random.GetRepeatableRandomDouble(random);
double rd5 = Random.GetRepeatableRandomDouble(4, 42);
double rd6 = Random.GetRepeatableRandomDouble(4, random);
IEnumerable<double> rds1 =
    Random.GetRandomDoubles(3, 4, cancellationToken);
IEnumerable<double> rds2 =
    Random.GetRepeatableRandomDoubles(3, 42, 4, cancellationToken);
IEnumerable<double> rds3 =
    Random.GetRepeatableRandomDoubles(3, random, 4, cancellationToken);

decimal rm1 = Random.GetRandomDecimal();
decimal rm2 = Random.GetRandomDecimal(6);
decimal rm3 = Random.GetRepeatableRandomDecimal(42);
decimal rm4 = Random.GetRepeatableRandomDecimal(random);
decimal rm5 = Random.GetRepeatableRandomDecimal(6, 42);
decimal rm6 = Random.GetRepeatableRandomDecimal(6, random);
IEnumerable<decimal> rms1 =
    Random.GetRandomDecimals(3, 6, cancellationToken);
IEnumerable<decimal> rms2 =
    Random.GetRepeatableRandomDecimals(3, 42, 6, cancellationToken);
IEnumerable<decimal> rms3 =
    Random.GetRepeatableRandomDecimals(3, random, 6, cancellationToken);
```

Shuffle and selection overloads:

```csharp
List<int> shuffled = [1, 2, 3];
shuffled.ShuffleListInPlace(cancellationToken);
shuffled.RepeatableShuffleListInPlace(42, cancellationToken);
shuffled.RepeatableShuffleListInPlace(random, cancellationToken);
IEnumerable<int> enumerableShuffle = ((IEnumerable<int>)shuffled).Shuffle();
List<int> listShuffle = ((IList<int>)shuffled).Shuffle();
int[] arrayShuffle = [1, 2, 3];
arrayShuffle.Shuffle();                 // In place.
Span<int> spanShuffle = arrayShuffle;
spanShuffle.Shuffle();                  // In place.
IEnumerable<int> linqShuffle = shuffled.ShuffleLinq();

int? element = shuffled.GetRandomElement();
int? repeatElement1 = shuffled.GetRepeatableRandomElement(42);
int? repeatElement2 = shuffled.GetRepeatableRandomElement(random);
IEnumerable<int> elements = shuffled.GetRandomElements(2);
IEnumerable<int> repeatElements1 =
    shuffled.GetRepeatableRandomElements(42, 2);
IEnumerable<int> repeatElements2 =
    shuffled.GetRepeatableRandomElements(random, 2);
IEnumerable<int> unique = shuffled.GetUniqueRandomElements(2);
IEnumerable<int> repeatUnique1 =
    shuffled.GetRepeatableUniqueRandomElements(42, 2);
IEnumerable<int> repeatUnique2 =
    shuffled.GetRepeatableUniqueRandomElements(random, 2);
```

Selection with replacement can return duplicates. The unique variants deduplicate input and require `selectQuantity >= 1`. Empty input fails in `GetRandomElement`; the other sequence methods generally return/yield empty output, subject to their quantity validation.

String and byte overloads:

```csharp
string rs1 = Random.GenerateRandomString(12);
string rs2 = Random.GenerateRepeatableRandomString(12, 42);
string rs3 = Random.GenerateRepeatableRandomString(12, random);
IEnumerable<string> rss1 = Random.GenerateRandomStrings(3, 12);
IEnumerable<string> rss2 =
    Random.GenerateRepeatableRandomStrings(3, 12, 42);
IEnumerable<string> rss3 =
    Random.GenerateRepeatableRandomStrings(3, 12, random);

HashSet<char> chars = ['A', 'B', '1'];
string rc1 = Random.GenerateRandomStringByCharSet(8, chars, cancellationToken);
string rc2 =
    Random.GenerateRepeatableRandomStringByCharSet(8, 42, chars, cancellationToken);
string rc3 =
    Random.GenerateRepeatableRandomStringByCharSet(8, random, chars, cancellationToken);

byte[] rb1 = Random.GetRandomBytes(8); // System.Random, not RNG.
byte[] rb2 = Random.GetRandomBytes(8, random);
byte[] rb3 = [];
byte[] rb4 = Random.GetRandomBytes(ref rb3, 8, random);
byte[] rb5 = Random.GetRepeatableRandomBytes(
    ref rb3, 42, quantity: 8);

long ordinal = Random.GetFeistelOrdinal(
    index: 2, domain: 100, seed: 42, rounds: 6);
```

`minLength == -1` means exact `maxLength`; otherwise a length is selected in the `[minLength, maxLength)` behavior of the bounded helper. ASCII bounds are inclusive at the public string level. A blacklist covering the allowed range throws. The first random-string-by-charset default is alphanumeric without symbols.

## Batches

The `RunBatches` extensions have both overload families. They materialize distinct input values in a `HashSet` first, then process sequential batches. Duplicates and original order are therefore not preserved. `batchSize` must be positive; `success` is the AND of all processed results. Cancellation is checked between batches.

```csharp
IEnumerable<int> batchInput = Enumerable.Range(1, 10);
bool async1 = await batchInput.RunBatchedProcessAsync(
    (IEnumerable<int> batch) => Task.FromResult(batch.Any()),
    4, breakOnFail: true, logProgress: true, cancellationToken);
bool async2 = await batchInput.RunBatchedProcessAsync(
    (List<int> batch) => Task.FromResult(batch.Any()),
    4, true, true, cancellationToken);
bool sync1 = batchInput.RunBatchedProcess(
    (IEnumerable<int> batch) => batch.Any(),
    4, true, true, cancellationToken);
bool sync2 = batchInput.RunBatchedProcess(
    (List<int> batch) => batch.Any(),
    4, true, true, cancellationToken);
```

## Streams

`ReadStreamAsync` starts at the current position (it does not rewind), copies into a byte array, and leaves the caller stream open. `WriteStreamToStream` requires both streams to be seekable, resets the source to zero, writes at the target's current position, does not truncate existing tail bytes, flushes, and resets both positions to zero.

```csharp
using MemoryStream read = new([1, 2, 3]);
byte[] bytes = await read.ReadStreamAsync(1024, cancellationToken);

using MemoryStream target1 = new();
await target1.WriteStreamToStream(
    new MemoryStream([1, 2]), cancellationToken);
using MemoryStream target2 = new();
await target2.WriteStreamToStream(
    (Stream)new MemoryStream([3, 4]), cancellationToken);
```

`CountingStream` wraps an existing stream, counts only bytes written through the wrapper, and deliberately leaves the inner stream open when disposed. All read/write/seek/flush/capability methods follow the underlying stream.

```csharp
using MemoryStream inner = new();
using (CountingStream counting = new(inner))
{
    counting.WriteByte(1); // Inherited Stream.WriteByte uses overridden Write.
    counting.Write([2, 3], 0, 2);
    await counting.WriteAsync([4], 0, 1, cancellationToken);
    await counting.WriteAsync(
        new ReadOnlyMemory<byte>([5]), cancellationToken);
    long written = counting.BytesWritten; // 5

    await counting.CopyToAsync(inner, 16, cancellationToken);
    await counting.FlushAsync(cancellationToken);
    counting.Flush();
    int r1 = counting.Read(new byte[1], 0, 1);
    int r2 = await counting.ReadAsync(new byte[1], 0, 1, cancellationToken);
    int r3 = await counting.ReadAsync(new Memory<byte>(new byte[1]), cancellationToken);
    long position = counting.Position;
    bool readable = counting.CanRead;
    bool seekable = counting.CanSeek;
    bool writable = counting.CanWrite;
    long length = counting.Length;
    counting.Seek(0, SeekOrigin.Begin);
    counting.SetLength(0);
}

CountingStream disposedAsync = new(new MemoryStream());
await disposedAsync.DisposeAsync();
```

## String constants and enums

```csharp
string timestampFormat = Strings.TimestampUrlFormat; // yyyyMMddHHmmssFFF
string dateOnlyFormat = Strings.DateOnlyUrlFormat;   // yyyyMMdd
EYesNo yes = EYesNo.Yes;
EHashAlgorithm hashAlgorithm = EHashAlgorithm.SHA256;
EComparisonType comparison = EComparisonType.OR;
TitleCaseUppercaseWordHandling titleMode =
    TitleCaseUppercaseWordHandling.ConvertByLength;
```

## Slicing, casing, and whitespace

String overloads preserve null; `ReadOnlySpan<char>` overloads return slices or empty spans.

```csharp
string? left = ((string?)"abcd").Left(2);
ReadOnlySpan<char> leftSpan = "abcd".AsSpan().Left(2);
string? right = "abcd".Right(2);
ReadOnlySpan<char> rightSpan = "abcd".AsSpan().Right(2);
string? between = "a[middle]z".ExtractBetween("[", "]");
ReadOnlySpan<char> betweenSpan =
    "a[middle]z".AsSpan().ExtractBetween("[", "]");
string? literalNull = "NULLNULL".MakeNullNull(); // Null for repeated "null".
string? pascal = "XMLHttp".ParsePascalCase(); // "X M L Http"
ReadOnlySpan<char> pascalSpan =
    "XMLHttp".AsSpan().ParsePascalCase();
string? title = "NASA launch".ToTitleCase(
    "en-US", TitleCaseUppercaseWordHandling.ConvertByLength, 3, cancellationToken);
string? trimmed = "  one \t two  ".TrimFull();
string normalizedWhitespace = " one\t two\r\n ".NormalizeWhiteSpace();
```

`TrimFull` collapses all whitespace runs to one ASCII space. `NormalizeWhiteSpace` removes tabs and otherwise preserves the first whitespace character in each run. It returns `""` for null rather than null.

## String/collection tests and invariant searches

```csharp
bool nwhitespace = ((string?)null).IsNullOrWhiteSpace();
bool nempty1 = ((string?)null).IsNullOrEmpty();
bool nempty2 = ((IEnumerable<int>?)null).IsNullOrEmpty();

ReadOnlySpan<char> span = "Hello".AsSpan();
bool contains1 = "Hello".ContainsInvariant("EL");
bool contains2 = span.ContainsInvariant("EL".AsSpan());
bool contains3 = new[] { "one", "two" }.ContainsInvariant("TWO");
bool contains4 =
    new[] { "one", "two" }.ContainsInvariant("TWO".AsSpan());
bool contains5 =
    "one two".ContainsInvariant(["one", "three"], true);
bool contains6 =
    span.ContainsInvariant(["he", "lo"], false);

bool starts1 = "Hello".StartsWithInvariant("HE");
bool starts2 = span.StartsWithInvariant("HE".AsSpan());
bool ends1 = "Hello".EndsWithInvariant("LO");
bool ends2 = span.EndsWithInvariant("LO".AsSpan());
int index1 = "Hello".IndexOfInvariant("EL");
int index2 = "Hello".IndexOfInvariant('E');
int index3 = span.IndexOfInvariant("EL".AsSpan());
int index4 = span.IndexOfInvariant('e'); // Span char overload is case-sensitive.

bool containsSet1 = "one two".Contains(["one", "x"], true);
bool containsSet2 =
    span.Contains(["he", "lo"], false, StringComparison.OrdinalIgnoreCase);
bool charSet1 = "abc".ContainsAnyCharacter("xz".AsSpan());
bool charSet2 = span.ContainsAnyCharacter("xz".AsSpan());

string? replaced1 = "One ONE".ReplaceInvariant(
    "one", "1", replaceAllInstances: true, cancellationToken);
string? replaced2 = "One TWO".ReplaceInvariant(
    ["one", "two"], "x", false, cancellationToken);
bool strEq = " Ada ".StrEq("ada");
bool strComp1 = "Ada".StrComp("Ada");
bool strComp2 =
    "Ada".StrComp("ada", StringComparison.OrdinalIgnoreCase);
```

Invariant comparison uses invariant-culture, case-insensitive matching. `StrEq` trims both values and treats null as equal to an empty/whitespace-only value through span comparison. `StrComp` does not trim.

## Character classes and extraction

```csharp
bool alphaNumeric1 = "a B1".IsAlphanumeric(true);
bool alphaNumeric2 = "a B1".AsSpan().IsAlphanumeric(true);
bool alpha1 = "a B".IsAlphaOnly(true);
bool alpha2 = "a B".AsSpan().IsAlphaOnly(true);
bool numeric1 = "1 2".IsNumericOnly(true);
bool numeric2 = "1 2".AsSpan().IsNumericOnly(true);

string? before = "a/b/c".ExtractToLastInstance('/');
ReadOnlySpan<char> beforeSpan =
    "a/b/c".AsSpan().ExtractToLastInstance('/');
string? after = "a/b/c".ExtractFromLastInstance('/');
ReadOnlySpan<char> afterSpan =
    "a/b/c".AsSpan().ExtractFromLastInstance('/');

string? noLetters = "a1b".RemoveLetters();
string? noNumbers = "a1b".RemoveNumbers();
string? letters = " a1-b ".GetOnlyLetters();
string? numbers = "x 1 1/2".GetOnlyNumbers(allowFractions: true);
string? leftClean = "!!abc".RemoveLeadingNonAlphanumeric();
ReadOnlySpan<char> leftCleanSpan =
    "!!abc".AsSpan().RemoveLeadingNonAlphanumeric();
string? rightClean = "abc!!".RemoveTrailingNonAlphanumeric();
ReadOnlySpan<char> rightCleanSpan =
    "abc!!".AsSpan().RemoveTrailingNonAlphanumeric();
string? outerClean = "!!abc!!".TrimOuterNonAlphanumeric();

int count1 = "hello".AsSpan().CountChars('l');
int count2 = "hello".CountChars('l');
int count3 = "hello".CountChars("l");
bool atMost1 = "hello".AsSpan().HasNoMoreThanNumberOfChars('l', 2);
bool atMost2 = "hello".HasNoMoreThanNumberOfChars('l', 2);
bool atMost3 = "hello".HasNoMoreThanNumberOfChars("l", 2);
bool atLeast1 = "hello".AsSpan().HasNoLessThanNumberOfChars('l', 2);
bool atLeast2 = "hello".HasNoLessThanNumberOfChars('l', 2);
bool atLeast3 = "hello".HasNoLessThanNumberOfChars("l", 2);
```

The `string` char-count overloads reject a char string longer than one character, with different exception types by method (`ArgumentException` or `InvalidDataException`). Negative thresholds throw. Empty strings satisfy a zero-or-more maximum and a zero minimum.

## Object string normalization

Reflection-based `R` methods affect only direct string properties. Expression-tree variants can recurse. Both mutate and return the same object.

```csharp
Person model = new();
model = model.TrimObjectStrings(recursive: true)!;
model = model.NormalizeObjectStringsR(
    enableTrim: true, NormalizationForm.FormKC)!;
model = model.NormalizeObjectStrings(
    true, NormalizationForm.FormKC, recursive: true, useCache: true)!;
model = model.MakeObjectNullNullR()!;
model = model.MakeObjectNullNull(recursive: true)!;

Strings.CacheManager.ClearAllCaches();
Strings.CacheManager.SetLimitedCacheSize(20);
```

## Conversion to/from nullable values

Date/DateOnly parse overloads use invariant culture by default and then try an OLE Automation date number. Number parsing uses the current culture's normal `TryParse` overload. Failure returns null.

```csharp
string? dtText1 = ((DateTime?)new(2026, 1, 2)).ToNString("yyyy-MM-dd");
string? dtText2 = ((DateOnly?)new(2026, 1, 2)).ToNString("yyyyMMdd");
string? tsText = ((TimeSpan?)TimeSpan.FromMinutes(2)).ToNString("c");
string? intText = ((int?)1).ToNString();
string? longText = ((long?)2L).ToNString();
string? doubleText = ((double?)1.5).ToNString();
string? decimalText = ((decimal?)1.5m).ToNString();
string? boolText = ((bool?)true).ToNString();
string? objectText = ((object?)42).ToNString();

IEnumerable<int> intSequence =
    new[] { "1", "2" }.ToListInt();
List<int> intList =
    new List<string> { "3", "4" }.ToListInt();
int? int1 = "42".ToNInt();
int? int2 = "42".AsSpan().ToNInt();
double? double1 = "1.5".ToNDouble();
double? double2 = "1.5".AsSpan().ToNDouble();
decimal? decimal1 = "1.5".ToNDecimal();
decimal? decimal2 = "1.5".AsSpan().ToNDecimal();
DateTime? parsedDt1 =
    "2026-01-02".ToNDateTime(CultureInfo.InvariantCulture);
DateTime? parsedDt2 =
    "2026-01-02".AsSpan().ToNDateTime(CultureInfo.InvariantCulture);
DateOnly? parsedDo1 =
    "2026-01-02".ToNDateOnly(CultureInfo.InvariantCulture);
DateOnly? parsedDo2 =
    "2026-01-02".AsSpan().ToNDateOnly(CultureInfo.InvariantCulture);
```

Simple conversions:

```csharp
bool yes = " yes ".YesNoToBool();
bool y = "Y".YNToBool();
string no = false.BoolToYesNo();
string n = false.BoolToYN();
int one = true.BoolToInt();
```

`GetSafeDate` replaces `/` with `-`; null date inputs use today. `MakeExportNameUnique` checks the filesystem and appends `(0)`, `(1)`, etc.

```csharp
string today = Strings.GetSafeDate("yyyy/MM/dd");
string dtSafe = ((DateTime?)null).GetSafeDate("yyyy/MM/dd");
string doSafe = ((DateOnly?)null).GetSafeDate("yyyy/MM/dd");
string exportName =
    Strings.MakeExportNameUnique(@"C:\exports", "report.csv", "csv");
string short1 = ((TimeSpan?)TimeSpan.FromMinutes(2)).TimespanToShortForm();
string short2 = TimeSpan.FromMinutes(2).TimespanToShortForm();
```

## Hashing, date formats, regex, URL, phone, and lines

`GetHash` hashes UTF-8 bytes and returns lowercase hex. MD5 and SHA-1 are available for compatibility, not collision-sensitive security. There is no default algorithm.

```csharp
string hash = "text".GetHash(EHashAlgorithm.SHA256);
```

```csharp
string? date1 =
    "2026-01-02".FormatDateString("yyyy-MM-dd", "MM/dd/yyyy");
ReadOnlySpan<char> date2 =
    "2026-01-02".AsSpan().FormatDateString(
        "yyyy-MM-dd", "MM/dd/yyyy");

string? inverse1 = "abc-123".ReplaceInverse(
    "[A-Za-z0-9]+", "", matchFirstOnly: false,
    RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
Regex pattern = new("[A-Za-z0-9]+");
string? inverse2 = pattern.ReplaceInverse(
    "abc-123", "_", matchFirstOnly: false);

string? readable = "a b".UrlEncodeReadable(
    replaceEscapeSequences: null,
    appendDefaultEscapeSequences: true,
    cancellationToken);
string? phone = "11234567890x42".FormatPhoneNumber("-", true);
IEnumerable<string> lines = "one\r\ntwo".SplitLines(cancellationToken);
```

`ReplaceInverse` preserves regex matches and replaces the text outside them. It defaults to a five-second timeout and caches up to 64 pattern/option/timeout/culture combinations. Invalid format strings/date input throw framework format exceptions. `UrlEncodeReadable` intentionally decodes selected safe-looking escapes, so it is not a substitute for URL-safety validation. Supplied replacement lists are mutated when defaults are appended.

## Fractions and numeric extraction

`FractionTo*` throws on invalid fractions (and can divide by zero). `TryFractionTo*` and the `TryStringTo*` overloads return false instead. The nullable and non-nullable out overloads are separate.

```csharp
string? frac1 = ((decimal?)2.5m).ToFractionString(2);
string? frac2 = 2.5m.ToFractionString(2);
string? frac3 = ((double?)2.5d).ToFractionString(2);
string? frac4 = 2.5d.ToFractionString(2);

decimal? fd = "2 1/2".FractionToDecimal();
bool tfd1 = "2 1/2".TryFractionToDecimal(out decimal? fdNullable);
bool tfd2 = "2 1/2".TryFractionToDecimal(out decimal fdValue);
bool tsd1 = "value 2.5".TryStringToDecimal(out decimal? sdNullable);
bool tsd2 = "value 2.5".TryStringToDecimal(out decimal sdValue);

double? fdouble = "2 1/2".FractionToDouble();
bool tff1 = "2 1/2".TryFractionToDouble(out double? ffNullable);
bool tff2 = "2 1/2".TryFractionToDouble(out double ffValue);
bool tsf1 = "value 2.5".TryStringToDouble(out double? sfNullable);
bool tsf2 = "value 2.5".TryStringToDouble(out double sfValue);
```

Fraction string output always includes a whole component and fraction, such as `2 1/2`; `maxNumberOfDecimalsToConsider` controls the initial denominator via `10^n`. The `TryStringTo*` methods strip text down to numeric/fraction characters (so signs are not preserved), then parse. Their non-nullable out overloads treat a parsed zero as failure and are not equivalent to the nullable out overloads for zero.

## Log sanitization and written lists

`SanitizeForLog` expects a non-null string and replaces line separators with spaces. `JoinStringsAsWrittenList` returns null for null/empty input.

```csharp
string safeLog = "first\r\nsecond".SanitizeForLog();
string? list1 = new[] { "Apple", "Banana" }.JoinStringsAsWrittenList();
string? list2 = ((IEnumerable<string>?)null).JoinStringsAsWrittenList();
```

## Type checks and type caches

```csharp
TypeChecks.ClearSimpleTypeCache();
TypeChecks.ClearReadOnlyCollectionTypeCache();
TypeChecks.ClearNumericTypeCache();
TypeChecks.ClearEnumerableTypeCache();
TypeChecks.ClearTypeCheckCaches();

bool delegateType = typeof(Action).IsDelegate();
bool arrayType = typeof(int[]).IsArray();
bool dictionaryType = typeof(Hashtable).IsDictionary();
bool enumerableType = typeof(List<int>).IsEnumerable();
bool complexClass = typeof(object).IsClassOtherThanString();
bool numericValue = 42.IsNumeric();
bool numericType = typeof(int?).IsNumericType();
bool simpleType = typeof(DateTime).IsSimpleType();
bool readOnlyType =
    typeof(IReadOnlyList<int>).IsReadOnlyCollectionType();
```

`IsNumericType` supports the signed/unsigned built-in numeric family except `char` and nullable wrappers. `IsSimpleType` includes primitives, enums, strings, decimal, DateTime/Offset, TimeSpan, Guid, and nullable simple types. `IsClassOtherThanString` returns `true` for null and is implemented in terms of `type?.IsValueType != false || type != typeof(string)`; reference types other than string return true, value types also return true, so its name is broader than the implementation.

## Unit conversion (`net11.0` only)

Conversions use constants `KgToLbsConst = 2.20462`, `FtToInConst = 12`, `InToMmConst = 25.4`, and `MetersToMilesConst = 0.000621371`. Generic numeric and nullable overloads are demonstrated together; nullable overloads treat null as zero.

```csharp
#if NET11_0_OR_GREATER
decimal lbsToKg1 = 10m.LbsToKg();
decimal lbsToKg2 = ((decimal?)null).LbsToKg();
decimal kgToLbs1 = 10m.KgToLbs();
decimal kgToLbs2 = ((decimal?)null).KgToLbs();
decimal inToFt1 = 12m.InsToFt();
decimal inToFt2 = ((decimal?)12m).InsToFt();

decimal inToMm1 = 1m.InsToMm();
decimal inToMm2 = ((decimal?)1m).InsToMm(2);
decimal mmToIn1 = 25.4m.MmToIns(2);
decimal mmToIn2 = ((decimal?)25.4m).MmToIns(2);
decimal ftToIn1 = 1m.FtToIns();
decimal ftToIn2 = ((decimal?)1m).FtToIns();

decimal kb = 1024.BytesToKb(1);
long bytesFromKb = 1m.KbToBytes();
decimal mb = 1048576L.BytesToMb(1);
long bytesFromMb = 1m.MbToBytes();
decimal gb = 1073741824L.BytesToGb(1);
long bytesFromGb = 1m.GbToBytes();
decimal tb = 1099511627776L.BytesToTb(1);
long bytesFromTb = 1m.TbToBytes();
decimal kbm = 1024m.KbToMb(1);
decimal mbk = 1m.MbToKb(1);
decimal kbg = 1048576m.KbToGb(1);
decimal gbk = 1m.GbToKb(1);
decimal kbt = 1073741824m.KbToTb(1);
decimal tbk = 1m.TbToKb(1);
decimal mbg = 1024m.MbToGb(1);
decimal gbm = 1m.GbToMb(1);
decimal mbt = 1048576m.MbToTb(1);
decimal tbm = 1m.TbToMb(1);
decimal gbt = 1024m.GbToTb(1);
decimal tbg = 1m.TbToGb(1);

string binary1 = 1024L.GetFileSizeFromBytesWithUnits(1);
string binary2 = ((long?)1024L).GetFileSizeFromBytesWithUnits(1);
string bits1 = 1000L.GetFileSizeFromBitsWithUnits(1);
string bits2 = ((long?)1000L).GetFileSizeFromBitsWithUnits(1);

decimal miles1 = 1609.34m.MetersToMiles();
decimal miles2 = ((decimal?)null).MetersToMiles();
decimal meters1 = 1m.MilesToMeters();
decimal meters2 = ((decimal?)1m).MilesToMeters();
#endif
```

Byte-based conversions use binary 1024 units but method names abbreviate them as KB/MB. `GetFileSizeFromBytesWithUnits` uses IEC labels (`KiB`, `MiB`); `GetFileSizeFromBitsWithUnits` uses decimal 1000 units (`kb`, `Mb`, ...). Null nullable sizes return `"-0"`.

The current implementation of `GbToKb` divides by `1,048,576` (rather than multiplying), so it does not currently provide the mathematically expected inverse of `KbToGb`.

## Validation

`SetInvalidPropertiesToDefault` mutates and returns the same object. It validates with `Validator`, uses all validation attributes when `validateAll: true`, and resets only the invalid property's value. For value-type properties that have no setter, reflection can throw.

```csharp
Model model = new() { RequiredName = null, Age = 99 };
model = model.SetInvalidPropertiesToDefault(validateAll: true)!;

sealed class Model
{
    [Required]
    public string? RequiredName { get; set; }

    [Range(1, 10)]
    public int Age { get; set; }
}
```

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
