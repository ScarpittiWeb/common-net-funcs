---
name: commonnetfuncs-compression
description: 'Use CommonNetFuncs.Compression for GZip, Brotli, Deflate, ZIP files, and stream or byte-array compression. Consult when choosing formats, managing streams, or limiting decompression.'
---

# CommonNetFuncs.Compression

Use `CommonNetFuncs.Compression.Streams` for byte arrays and streams and
`CommonNetFuncs.Compression.Files` for files and ZIP archives. Choose the same
`ECompressionType` for compression and decompression; ZIP archives are a
separate file/archive operation.

## Workflow

1. Select the overload for the input and destination you own; inspect the
   [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Compression/README.md) for examples.
2. Set an appropriate expansion limit and bound the incoming compressed size
   before decompressing untrusted data.
3. Dispose compression wrappers to finish writing, and handle
   `CompressionLimitExceededException` explicitly.

## Important constraints

- Byte-array `Decompress` and `DecompressAsync` default to a maximum expansion
  ratio of 1,000 times the compressed length. Provide a smaller positive
  `maxCompressionRatio` if suitable, and impose an absolute output limit too.
- Stream-to-stream decompression uses that ratio for seekable input but uses
  `long.MaxValue` for non-seekable input. It is therefore **not** an effective
  decompression-bomb limit for non-seekable uploads; enforce an independent
  output limit or use an appropriately bounded alternative.
- Stream-to-stream decompression rewinds seekable input and output and leaves
  caller streams open. The wrapper-returning overloads have a `leaveOpen`
  option that defaults to `false`: disposing the wrapper normally disposes its
  underlying stream.
- Archive helpers can materialize ZIP contents in memory. Bound file counts and
  sizes, validate output paths and overwrite policy, and do not treat
  compression as encryption.

Check [Streams.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Compression/Streams.cs) and
[Files.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Compression/Files.cs) for the exact overload
and ownership semantics.
