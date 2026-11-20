# CommonNetFuncs.Web.Aws.S3

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Aws.S3)](https://www.nuget.org/packages/CommonNetFuncs.Web.Aws.S3/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Aws.S3)](https://www.nuget.org/packages/CommonNetFuncs.Web.Aws.S3/)

Helpers for Amazon S3 upload, multipart upload, download, deletion, existence checks, listing, validation, and one-minute presigned URLs through `IAmazonS3` extensions or the `IAwsS3`/`ApiAwsS3` wrapper. AWS credentials, region, retries, lifecycle, and IAM policies remain application responsibilities.

## Contents

- [Target frameworks](#target-frameworks)
- [Installation](#installation)
- [Configure and inject the client](#configure-and-inject-the-client)
- [Public API](#public-api)
- [Complete operation examples](#complete-operation-examples)
- [Upload behavior](#upload-behavior)
  - [Stream and file-path overloads](#stream-and-file-path-overloads)
  - [Multipart details](#multipart-details)
- [Download behavior](#download-behavior)
- [Delete, existence, listing, URLs, and validation](#delete-existence-listing-urls-and-validation)
- [Cancellation and errors](#cancellation-and-errors)
- [License](#license)

---

## Target frameworks

The current package targets **.NET 11.0** and **.NET Standard 2.1**. Because the public API exposes `IAmazonS3`, applications should reference a compatible `AWSSDK.S3` package explicitly and configure it for the intended region/credential provider.

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Aws.S3
dotnet add package AWSSDK.S3
```

## Configure and inject the client

```csharp
using Amazon;
using Amazon.S3;
using CommonNetFuncs.Web.Aws.S3;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IAmazonS3>(
    _ => new AmazonS3Client(RegionEndpoint.USEast1));
builder.Services.AddSingleton<IAwsS3, ApiAwsS3>();

WebApplication app = builder.Build();
```

Use least-privilege IAM permissions for only the operations the application calls. Depending on the selected methods, these commonly include `s3:PutObject`, `s3:GetObject`, `s3:DeleteObject`, `s3:ListBucket`, `s3:GetBucketLocation`, and multipart-upload actions. Do not use public buckets or broad IAM credentials merely to make these helpers work.

Object keys and local paths must be application-controlled or strictly constrained. A key is a complete S3 key, not a local path, and no package method provides tenant/path authorization.

## Public API

`IAwsS3` and `ApiAwsS3` expose these methods:

```csharp
Task<bool> UploadS3File(
    string bucketName, string fileName, Stream fileData,
    ConcurrentDictionary<string, bool>? validatedBuckets = null,
    long thresholdForMultiPartUpload = 10 * 1024 * 1024,
    bool compressSteam = true,
    ECompressionType compressionType = ECompressionType.Gzip,
    CancellationToken cancellationToken = default);

Task<bool> UploadS3File(
    string bucketName, string fileName, string filePath,
    ConcurrentDictionary<string, bool>? validatedBuckets = null,
    long thresholdForMultiPartUpload = 10 * 1024 * 1024,
    CancellationToken cancellationToken = default);

Task<bool> UploadMultipartAsync(
    string bucketName, string fileName, Stream stream,
    CancellationToken cancellationToken = default);

Task GetS3File(
    string bucketName, string? fileName, Stream fileData,
    ConcurrentDictionary<string, bool>? validatedBuckets = null,
    bool decompressGzipData = true,
    CancellationToken cancellationToken = default);

Task GetS3File(
    string bucketName, string? fileName, string filePath,
    ConcurrentDictionary<string, bool>? validatedBuckets = null,
    CancellationToken cancellationToken = default);

Task<bool> DeleteS3File(
    string bucketName, string fileName,
    ConcurrentDictionary<string, bool>? validatedBuckets = null,
    CancellationToken cancellationToken = default);

Task<bool> S3FileExists(
    string bucketName, string fileName, string? versionId = null,
    CancellationToken cancellationToken = default);

Task<List<string>?> GetAllS3BucketFiles(
    string bucketName, int maxKeysPerQuery = 1000,
    CancellationToken cancellationToken = default);

Task<string?> GetS3Url(
    string bucketName, string fileName,
    ConcurrentDictionary<string, bool>? validatedBuckets = null);

Task<bool> IsBucketValid(
    string bucketName,
    ConcurrentDictionary<string, bool>? validatedBuckets = null);
```

The first argument to `AwsS3HelpersStatic` calls is an `IAmazonS3` receiver. `ApiAwsS3` forwards to the same extension methods; it also has a public settable `EnableInfoLogging` property, but the current implementation does not consult that property to change logging.

## Complete operation examples

All examples use the same configured client. Both direct-extension and `IAwsS3` calls are shown; either surface is valid.

```csharp
using System.Collections.Concurrent;
using Amazon.S3;
using CommonNetFuncs.Web.Aws.S3;
using static CommonNetFuncs.Compression.Streams;

IAmazonS3 aws = serviceProvider.GetRequiredService<IAmazonS3>();
IAwsS3 s3 = new ApiAwsS3(aws);
var bucketValidation = new ConcurrentDictionary<string, bool>();
using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));
CancellationToken ct = cts.Token;

// ApiAwsS3 stream upload overload.
await using (FileStream input = File.OpenRead("report.pdf"))
{
    bool wrapperStreamUpload = await s3.UploadS3File(
        "private-app-bucket", "reports/report.pdf", input,
        bucketValidation,
        thresholdForMultiPartUpload: 10 * 1024 * 1024,
        compressSteam: true,
        compressionType: ECompressionType.Gzip,
        cancellationToken: ct);
}

// IAmazonS3 extension overload: file path.
bool directPathUpload = await aws.UploadS3File(
    "private-app-bucket", "reports/source.csv", "local/source.csv",
    bucketValidation,
    thresholdForMultiPartUpload: 10 * 1024 * 1024,
    cancellationToken: ct);

// Direct low-level multipart upload; the caller supplies a seekable stream with Length.
await using (FileStream large = File.OpenRead("large/archive.bin"))
{
    bool multipart = await aws.UploadMultipartAsync(
        "private-app-bucket", "archives/archive.bin", large, ct);
}

// Stream download overload through the wrapper.
using (MemoryStream downloaded = new())
{
    await s3.GetS3File(
        "private-app-bucket", "reports/report.pdf", downloaded,
        bucketValidation, decompressGzipData: true, cancellationToken: ct);
}

// File-path download overload directly on IAmazonS3.
await aws.GetS3File(
    "private-app-bucket", "reports/source.csv", "downloads/source.csv",
    bucketValidation, cancellationToken: ct);

bool deleted = await s3.DeleteS3File(
    "private-app-bucket", "reports/old.pdf", bucketValidation, ct);

bool exists = await aws.S3FileExists(
    "private-app-bucket", "reports/report.pdf",
    versionId: null, cancellationToken: ct);

List<string>? keys = await s3.GetAllS3BucketFiles(
    "private-app-bucket", maxKeysPerQuery: 1000, cancellationToken: ct);

string? url = await aws.GetS3Url(
    "private-app-bucket", "reports/report.pdf", bucketValidation);

bool bucketIsReachable = await s3.IsBucketValid(
    "private-app-bucket", bucketValidation);

var concrete = new ApiAwsS3(aws);
concrete.EnableInfoLogging = false; // Exposed property; currently does not change helper logging.
```

The static and wrapper surfaces have the same overloads. For example, the stream/path upload and download forms can each be invoked on either `IAmazonS3` or `IAwsS3`.

## Upload behavior

### Stream and file-path overloads

Both regular upload overloads validate the bucket, delete an existing object at the same key, and then upload the new content. A file whose `Length` is **less than** the threshold uses `PutObject`; at or above the threshold it uses multipart upload. The default threshold is 10 MiB.

The stream overload:

1. Rejects a requested compression type other than GZip or Deflate **before upload** by throwing `NotSupportedException` when `compressSteam` is true.
2. Detects the source stream's compression. Existing Brotli or ZLib streams are decompressed before the selected upload branch. Existing GZip/Deflate streams are not first decompressed.
3. Below the multipart threshold, optionally compresses GZip/Deflate, sets `Content-Encoding`, S3 bucket-key encryption, and AES-256 server-side encryption.
4. At/above the threshold, uploads the original stream through multipart and does not apply `compressSteam`/`compressionType` or add a content-encoding value.

The stream must expose a usable `Length`; non-seekable streams that cannot report length should be copied to a seekable/bounded stream first. Multipart additionally requires seek support because parts seek to offsets.

Stream ownership is not uniform:

- A below-threshold upload with `compressSteam: false` uses the caller's stream as the AWS input stream and disposes it when the method's `await using` ends.
- Compression and other branches may leave the supplied stream open.

Do not depend on the source remaining open after a call. Coordinate disposal explicitly and avoid reusing an upload stream without checking the selected branch.

The file-path overload throws `ArgumentException` for a blank object name and `FileNotFoundException` for a missing local file before S3 work begins. It opens the file for read sharing and does not apply the stream overload's optional compression.

Both overloads catch S3/general exceptions from the remaining operation and return `false` after logging rather than propagating them. `false` can mean invalid/unknown bucket, blank stream key, upload failure, multipart failure, or an unsuccessful AWS status.

### Multipart details

`UploadMultipartAsync` initiates and completes the multipart request itself, uploads parts in parallel, and attempts `AbortMultipartUpload` when a failure occurs. It:

- chooses chunks of at least 10 MiB (also respecting AWS's 5 MiB minimum/5 GiB maximum constraints);
- targets at most 10,000 parts;
- requires seekable data and a reliable `Length`;
- does not validate the bucket first;
- sets bucket-key encryption and AES-256 encryption on initiation; and
- returns `false` after logging errors or missing part uploads rather than throwing an `InvalidOperationException`.

Large or untrusted input needs application-level size limits. The helper does not limit total decompressed size and does not preserve a source's `Position` for multipart reads.

## Download behavior

`GetS3File` has a `Task` return type, not a success boolean. Most S3 and general errors are logged and swallowed, so task completion alone does not prove that data was downloaded.

For the **stream overload**:

- the destination stream is not disposed by the helper;
- when `decompressGzipData` is true and the S3 `Content-Encoding` contains `gzip` or `deflate`, the response is decompressed;
- otherwise, bytes are inspected for a supported compression signature and known compressed content can still be decompressed; consequently the flag does not guarantee byte-for-byte raw output for all signed formats;
- decoding has no package-level decompressed-size limit, so constrain object size and consider quotas for untrusted buckets/keys; and
- a missing, blank, or invalid key/bucket normally leaves the destination empty (and may leave exceptions only in logs).

For the **file-path overload**:

- the path is opened/truncated before bucket validation and the S3 request;
- the response stream is copied as stored, without decompression; and
- failure can therefore leave an empty/partial local file.

Write downloads to an authorized application directory, use collision-resistant temporary names where needed, and verify expected file size/content independently when failure must be distinguishable.

## Delete, existence, listing, URLs, and validation

- `DeleteS3File` returns `true` only when the validated object existed and deletion completed. Missing objects/blank keys return `false`. Errors are logged and swallowed.
- `S3FileExists` issues a metadata request, supports an optional `versionId`, and returns `false` for blank names, not-found responses, or swallowed errors.
- `GetAllS3BucketFiles` automatically follows v2 continuation tokens. The current implementation returns an empty list, not `null`, for blank buckets or swallowed failures. `maxKeysPerQuery` is passed to AWS; it is not an overall result limit.
- `GetS3Url` returns a **presigned GET URL expiring in about one minute** after validating the bucket. It is not necessarily a permanent public URL. If it returns a URL, protect and scope it as a credential-bearing capability; prefer private buckets.
- `IsBucketValid` caches a boolean per bucket name. When no dictionary is supplied, the helper uses its process-static default dictionary, keyed only by bucket name and not by client/account/region. A false result is retried. Pass a cache scoped to the correct client/account when credentials or accounts can vary.

```csharp
using Amazon.S3;
using CommonNetFuncs.Web.Aws.S3;
using Microsoft.Extensions.Logging;

IAmazonS3 client = serviceProvider.GetRequiredService<IAmazonS3>();
ILogger logger = serviceProvider.GetRequiredService<ILogger<Program>>();

// Existence is only a probe; a later Get/Delete is still subject to normal S3 races and IAM.
if (await client.S3FileExists("private-app-bucket", "reports/report.pdf"))
{
    logger.LogInformation("Object exists");
}

// A completed Task does not indicate download success. Verify the result separately.
using var destination = new MemoryStream();
await client.GetS3File("private-app-bucket", "reports/report.pdf", destination);
if (destination.Length == 0)
{
    logger.LogWarning("No bytes were downloaded");
}
```

## Cancellation and errors

Most methods accept a cancellation token except `GetS3Url` and `IsBucketValid`. Much of the package catches `Exception`, logs it, and converts it into `false`, `null`, or an empty result. Cancellation may therefore be logged/swallowed on methods whose broad catch encloses the token-using call rather than always throwing `OperationCanceledException`.

Use the raw AWS SDK when callers require precise exception propagation, status codes, progress, custom retry behavior, range requests, server-side encryption choices, or operation-specific response metadata.

## License

Licensed under MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
