# CommonNetFuncs.Web.Ftp

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Ftp)](https://www.nuget.org/packages/CommonNetFuncs.Web.Ftp/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Ftp)](https://www.nuget.org/packages/CommonNetFuncs.Web.Ftp/)

Helpers for **SFTP over SSH** using SSH.NET's `SftpClient`, including connection configuration, listing/existence/deletion, CSV readers, and a disposable `ISshFtpService` wrapper. Despite the package name, it does not implement FTP, FTPS, uploads, or general file downloads.

## Contents

- [Target frameworks](#target-frameworks)
- [Installation](#installation)
- [Security and ownership boundaries](#security-and-ownership-boundaries)
- [Connection configuration](#connection-configuration)
- [Static `SshFtp` API](#static-sshftp-api)
  - [Existence and listing](#existence-and-listing)
  - [Read CSV records](#read-csv-records)
  - [Delete](#delete)
- [Dependency-injection service](#dependency-injection-service)
- [Cancellation, paths, and failures](#cancellation-paths-and-failures)
- [License](#license)

---

## Target frameworks

The current package targets **.NET 11.0** and **.NET Standard 2.1**.

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Ftp
```

## Security and ownership boundaries

- `SshFtp.Connect` and the default `SshFtpService` connection construct an SSH.NET password client with host, port, username, and password. They do not expose host-key fingerprint pinning/callback configuration and do not configure key-based authentication.
- `FileTransferConnection.ConnectionProtocol` is currently not consulted; it does not switch between FTP, FTPS, and SFTP.
- If host-key verification, a custom host-key policy, private-key authentication, proxying, or a custom SSH negotiation policy is required, create/configure an `SftpClient` separately. Do not assume this helper pins a trusted server key.
- Keep credentials in secret configuration, use a least-privilege SFTP account, constrain remote paths, and apply transfer/row limits.
- When using static extensions, the caller owns the `SftpClient` and must retain/dispose the newly returned instance. `Connect` always constructs a new client and returns it.
- `SshFtpService` owns and disposes its `Client`. Replacing/reconnecting clients still requires care: reconnecting an existing client disconnects it but does not dispose it.
- `SftpClient` connections are mutable stateful objects; avoid sharing a client across tenants or concurrent operations unless the application supplies an appropriate ownership/concurrency policy.

## Connection configuration

`FileTransferConnection` exposes these settable properties:

```csharp
string HostName
string UserName
string Password
string ConnectionProtocol  // Currently unused.
int Port
uint BufferSize
```

A buffer size of `0` makes the extension `Connect`/`ConnectAsync` methods use 32 KiB. A real port must be valid for the configured SSH endpoint (commonly 22).

```csharp
using CommonNetFuncs.Web.Ftp;
using Renci.SshNet;

FileTransferConnection connection = new()
{
    HostName = configuration["Sftp:Host"]
        ?? throw new InvalidOperationException("Sftp:Host is required."),
    UserName = configuration["Sftp:User"]
        ?? throw new InvalidOperationException("Sftp:User is required."),
    Password = configuration["Sftp:Password"]
        ?? throw new InvalidOperationException("Sftp:Password is required."),
    Port = 22,
    BufferSize = 64 * 1024
};

string hostName = connection.GetHostName();

SftpClient? client = null;
client = client.Connect(connection);       // Returns a new connected client.
Console.WriteLine(client.IsConnected());

bool stillConnected = client.DisconnectClient();
client.Dispose();
```

`IsConnected()` safely returns `false` for a null client. `DisconnectClient()` returns the state after attempting disconnection, not whether a disconnect occurred; with a real client the expected result is normally `false`.

Asynchronous connection:

```csharp
using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
SftpClient? asyncClient = null;
asyncClient = await asyncClient.ConnectAsync(connection, cts);
try
{
    bool connected = asyncClient.IsConnected();
}
finally
{
    asyncClient.DisconnectClient();
    asyncClient.Dispose();
}
```

If the extension receiver is already connected, `Connect`/`ConnectAsync` disconnect it and replace it. The old client is not disposed by these methods.

## Static `SshFtp` API

The current extension surface is:

```csharp
string GetHostName(this FileTransferConnection);
bool IsConnected(this SftpClient?);

SftpClient Connect(this SftpClient?, FileTransferConnection);
Task<SftpClient> ConnectAsync(
    this SftpClient?, FileTransferConnection,
    CancellationTokenSource? = null);

bool DisconnectClient(this SftpClient?);
bool DirectoryOrFileExists(this SftpClient?, string path);
Task<bool> DirectoryOrFileExistsAsync(this SftpClient?, string path);

IEnumerable<string> GetFileList(
    this SftpClient?, string path, string extension = "*");
IAsyncEnumerable<string> GetFileListAsync(
    this SftpClient?, string path, string extension = "*",
    CancellationTokenSource? = null);

Task<List<T>> GetDataFromCsvAsync<T>(
    this SftpClient?, string remoteFilePath,
    bool csvHasHeaderRow = true,
    CultureInfo? cultureInfo = null,
    int bufferSize = 4096,
    CancellationToken cancellationToken = default);

IAsyncEnumerable<T> GetDataFromCsvAsyncEnumerable<T>(
    this SftpClient?, string remoteFilePath,
    bool csvHasHeaderRow = true,
    CultureInfo? cultureInfo = null,
    int bufferSize = 4096,
    CancellationToken cancellationToken = default);

IEnumerable<T> GetDataFromCsvEnumerable<T>(
    this SftpClient?, string remoteFilePath,
    bool csvHasHeaderRow = true,
    CultureInfo? cultureInfo = null,
    int bufferSize = 4096,
    CancellationToken cancellationToken = default);

IEnumerable<T> GetDataFromCsv<T>(
    this SftpClient?, string remoteFilePath,
    bool csvHasHeaderRow = true,
    CultureInfo? cultureInfo = null,
    int bufferSize = 4096);

bool DeleteSftpFile(this SftpClient?, string remoteFilePath);
Task<bool> DeleteFileAsync(this SftpClient?, string remoteFilePath);
```

All remote operations except `IsConnected`, `DisconnectClient`, and connection creation throw `SshConnectionException` when the client is null or not connected. SSH.NET and CSV parsing/network/path exceptions otherwise propagate; these helpers do not turn failures into `false` or empty results.

### Existence and listing

```csharp
using CommonNetFuncs.Web.Ftp;
using Renci.SshNet;

bool exists = client.DirectoryOrFileExists("/incoming/report.csv");
bool existsAsync = await client.DirectoryOrFileExistsAsync("/incoming/archive");

// Sync listing: only regular files, extension match without a leading dot.
// If no item matches, the enumerable contains one empty string.
IEnumerable<string> allFiles = client.GetFileList("/incoming", "*");
IEnumerable<string> csvFiles = client.GetFileList("/incoming", "csv");

using CancellationTokenSource listCts = new(TimeSpan.FromSeconds(30));
await foreach (string csvPath in client.GetFileListAsync(
                   "/incoming", "csv", listCts))
{
    Console.WriteLine(csvPath);
}
```

`GetFileList` throws `ArgumentException` if the directory path does not exist. The sync empty result and `GetFileListAsync` differ: the async stream simply yields no items when no regular file matches. Paths are concatenated as `"{path}/{file}"`; do not rely on the helper to normalize/canonicalize `..`, duplicate separators, or server-specific path semantics.

### Read CSV records

CSV files must end in the literal, case-sensitive suffix `.csv`. Headers default to enabled, culture defaults to invariant, and buffer size defaults to 4096.

```csharp
using System.Globalization;
using CommonNetFuncs.Web.Ftp;
using Renci.SshNet;

using CancellationTokenSource csvCts = new();
List<ReportRow> allRows = await client.GetDataFromCsvAsync<ReportRow>(
    "/incoming/report.csv",
    csvHasHeaderRow: true,
    cultureInfo: CultureInfo.GetCultureInfo("en-US"),
    bufferSize: 32 * 1024,
    csvCts.Token);

await foreach (ReportRow row in client.GetDataFromCsvAsyncEnumerable<ReportRow>(
                   "/incoming/large-report.csv",
                   csvHasHeaderRow: true,
                   cultureInfo: null,
                   bufferSize: 32 * 1024,
                   cancellationToken: csvCts.Token))
{
    Process(row);
}

foreach (ReportRow row in client.GetDataFromCsvEnumerable<ReportRow>(
             "/incoming/report.csv",
             csvHasHeaderRow: true,
             cultureInfo: CultureInfo.InvariantCulture,
             bufferSize: 4096,
             cancellationToken: csvCts.Token))
{
    Process(row);
}

// Current compatibility limitation: GetDataFromCsv<T> returns a deferred
// enumerable backed by a stream that this method disposes before enumeration.
// Prefer GetDataFromCsvAsync<T> or GetDataFromCsvAsyncEnumerable<T>.
IEnumerable<ReportRow> deferredRows = client.GetDataFromCsv<ReportRow>(
    "/incoming/report.csv",
    csvHasHeaderRow: true,
    cultureInfo: CultureInfo.InvariantCulture,
    bufferSize: 4096);

public sealed class ReportRow
{
    public string Id { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

static void Process(ReportRow row) { /* application work */ }
```

`GetDataFromCsvAsync` materializes a `List<T>`. The `...Enumerable` methods defer parsing; keep the connection connected until enumeration completes and dispose/cancel promptly. Cancellation is passed to the async CSV methods. The sync `GetDataFromCsvEnumerable` checks cancellation between yielded rows and stops without throwing when requested.

### Delete

Deletion is existence-checked and then issued:

```csharp
bool deletedSync = client.DeleteSftpFile("/archive/old.csv");
bool deletedAsync = await client.DeleteFileAsync("/archive/other.csv");
```

Both return `true` if the path is absent after the operation, including when it did not exist before. They do not recurse through directories; use them for a remote file/path supported by SSH.NET deletion.

## Dependency-injection service

`ISshFtpService : IDisposable` exposes:

```csharp
string GetHostName();
bool IsConnected();
SftpClient Connect();
Task<SftpClient> ConnectAsync(CancellationTokenSource?);
bool DisconnectClient();
bool DirectoryExists(string path);
Task<bool> DirectoryExistsAsync(string path);
IEnumerable<string> GetFileList(string path, string extension = "*");
IAsyncEnumerable<string> GetFileListAsync(
    string path, string extension = "*",
    CancellationTokenSource? cancellationTokenSource = null);
Task<List<T>> GetDataFromCsvAsync<T>(...);
IAsyncEnumerable<T> GetDataFromCsvAsyncEnumerable<T>(...);
IEnumerable<T> GetDataFromCsv<T>(...);
bool DeleteFile(string remoteFilePath);
Task<bool> DeleteFileAsync(string remoteFilePath);
void Dispose();
```

`SshFtpService.Client` is additionally public/read-only and the constructor accepts an optional client factory:

```csharp
SshFtpService(
    FileTransferConnection fileTransferConnection,
    Func<FileTransferConnection, SftpClient>? clientFactory = null)
```

Without a factory, construction immediately connects synchronously. With a factory, the returned client is accepted as supplied (it is not connected by the constructor automatically). The service disposes its current `Client` on `Dispose()`.

`SshFtpService.Connect()` reconnects a disconnected client by calling the static extension, which creates a replacement client from the stored connection. `ConnectAsync()` instead calls SSH.NET's `ConnectAsync` on the current client; it does not invoke the configured client factory again.

Registering it as a singleton makes one mutable client global to the process. Use a scoped/owned lifetime or another connection policy when concurrent or tenant-specific SFTP work requires isolation.

```csharp
using System.Globalization;
using CommonNetFuncs.Web.Ftp;
using Renci.SshNet;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddScoped<ISshFtpService>(provider =>
{
    var configured = new FileTransferConnection
    {
        HostName = builder.Configuration["Sftp:Host"]!,
        UserName = builder.Configuration["Sftp:User"]!,
        Password = builder.Configuration["Sftp:Password"]!,
        Port = 22,
        BufferSize = 32 * 1024
    };

    return new SshFtpService(configured);
});

// Optional: take control of client creation, authentication, and host-key policy.
FileTransferConnection customConnection = new()
{
    HostName = builder.Configuration["Sftp:Host"]!,
    UserName = builder.Configuration["Sftp:User"]!,
    Password = builder.Configuration["Sftp:Password"]!,
    Port = 22
};
var customClient = new SftpClient(
    customConnection.HostName,
    customConnection.Port,
    customConnection.UserName,
    customConnection.Password);
using ISshFtpService customService = new SshFtpService(
    customConnection, _ => customClient);
```

Every service member can be used as follows:

```csharp
using System.Globalization;
using CommonNetFuncs.Web.Ftp;
using Renci.SshNet;

async Task Consume(ISshFtpService service)
{
    string host = service.GetHostName();
    bool connected = service.IsConnected();
    SftpClient client = service.Connect();

    using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
    SftpClient asyncClient = await service.ConnectAsync(cts);

    bool exists = service.DirectoryExists("/incoming");
    bool existsAsync = await service.DirectoryExistsAsync("/incoming");

    IEnumerable<string> files = service.GetFileList("/incoming", "csv");
    await foreach (string path in service.GetFileListAsync(
                       "/incoming", "csv", cts))
    {
        Console.WriteLine(path);
    }

    List<ReportRow> list = await service.GetDataFromCsvAsync<ReportRow>(
        "/incoming/report.csv", true, CultureInfo.InvariantCulture, 4096,
        cts.Token);

    await foreach (ReportRow row in service.GetDataFromCsvAsyncEnumerable<ReportRow>(
                       "/incoming/report.csv", true, null, 4096, cts.Token))
    {
        Process(row);
    }

    IEnumerable<ReportRow> rows = service.GetDataFromCsv<ReportRow>(
        "/incoming/report.csv", true, CultureInfo.InvariantCulture, 4096);

    bool deleted = service.DeleteFile("/incoming/old.csv");
    bool deletedAsync = await service.DeleteFileAsync("/incoming/other.csv");

    bool stateAfterDisconnect = service.DisconnectClient();

    // DI owns/disposes the service at scope end. It can also be disposed explicitly.
    service.Dispose();
}
```

The service method named `DirectoryExists` checks whether either a directory or file exists, matching the static `DirectoryOrFileExists` implementation. The service interface intentionally has no `GetDataFromCsvEnumerable` member, although the static `SshFtp` class does.

## Cancellation, paths, and failures

- Connection and async CSV/list methods accept cancellation through `CancellationTokenSource` or `CancellationToken`.
- Existence/delete async methods have no cancellation argument in this API.
- Remote path formatting is the caller's responsibility. Restrict keys and paths to expected SFTP roots to avoid crossing tenant/application boundaries.
- Validation errors such as a missing remote directory or non-`.csv` CSV path commonly throw `ArgumentException`; disconnected clients throw `SshConnectionException`; CSV and SSH.NET failures are not wrapped.
- No helper retries, verifies a server host key, or manages SFTP directory creation.

## License

Licensed under MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
