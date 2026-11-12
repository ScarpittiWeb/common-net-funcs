---
name: commonnetfuncs-core
description: 'Use CommonNetFuncs.Core for C# strings, hashing, files, streams, copying, batching, collections, dates, and random helpers. Consult when selecting these APIs or handling their security and behavior limits.'
---

# CommonNetFuncs.Core

Use the `CommonNetFuncs.Core` namespace and select the helper for the actual
operation; this package contains many unrelated extension methods. Its
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Core/README.md) lists the available APIs and
examples. The project targets `net11.0` and `netstandard2.1`;
`UnitConversion` is not included in the latter.

## Workflow

1. Choose a method from the relevant helper: `Strings` for text and nullable
   conversions, `FileHelpers` for paths and file hashes, `Streams` for stream
   copying, `Copy` for property mapping, or `RunBatches` for batch processing.
2. Check nullable returns, side effects, and the behavior of the exact overload
   before substituting it for a framework method.
3. Use the README and source when the operation depends on collection ordering,
   culture, recursive copying, cancellation, or error reporting.

```csharp
using CommonNetFuncs.Core;

string digest = "example".GetHash(EHashAlgorithm.SHA256);
int? count = "42".ToNInt();
```

## Safety and behavioral boundaries

- `Strings.GetHash` hashes UTF-8 bytes and returns lowercase hex, but allows
  MD5 and SHA-1, which are unsuitable for collision-sensitive security uses.
  Select a modern algorithm such as SHA-256 explicitly when a digest is
  appropriate. A plain hash, even SHA-256, is not password hashing or
  authentication: use a salted password KDF for passwords and a keyed MAC or
  signature for authenticity.
- `GetHashForObject` and its async counterpart default to MD5, omit null
  property values, and treat collection order as insignificant. They are not a
  canonical, authenticated representation; do not use them as a security
  boundary. Recursive object traversal does not track reference cycles, so
  cyclic graphs can recurse indefinitely. A null object returns the literal
  `"null"`.
- `GetHashFromFile` and `GetHashFromStream` default to SHA-512 but also accept
  weak algorithms. A missing file or hashing failure produces an empty string;
  handle that as failure. The stream variant requires seeking, rewinds the
  stream before hashing, and leaves it at the end.
- The repeatable random helpers use `System.Random` and must not generate
  secrets. `GetRandomInt` samples with modulo reduction, so its results can be
  biased; use `RandomNumberGenerator.GetInt32` for uniformly sampled,
  security-sensitive bounded integers. Generic seeds use `GetHashCode`, which
  is not a stable cross-process seed for every type.
- `GetSafeSaveName` checks for existing files but does not atomically reserve a
  name or confine an untrusted path to an allowed directory. Validate paths and
  use an atomic file-creation mode where needed. Its full-path overload returns
  a path; its `(path, fileName, ...)` overload returns a filename.
- `ReadStreamAsync` reads from the current position into a byte array. Both
  `WriteStreamToStream` overloads require seekable streams, reset the source to
  zero, write at the target's current position, and reset both positions to
  zero afterward; they do not truncate the target.
- `RunBatchedProcess` and `RunBatchedProcessAsync` materialize **distinct**
  inputs before processing, so they are not suitable when duplicates must be
  preserved. They process batches sequentially and return a combined `bool`;
  cancellation is checked between batches.
- `CopyPropertiesTo` copies matching properties, not necessarily an independent
  object graph. Use the recursive overload deliberately and choose an
  appropriate depth. Nullable conversions such as `ToNInt` return `null` when
  parsing fails; do not silently treat that as a valid zero.

For details, inspect [Strings.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Core/Strings.cs),
[FileHelpers.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Core/FileHelpers.cs), and the specific
helper implementation.
