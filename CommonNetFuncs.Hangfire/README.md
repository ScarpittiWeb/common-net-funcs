# CommonNetFuncs.Hangfire

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Hangfire)](https://www.nuget.org/packages/CommonNetFuncs.Hangfire/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Hangfire)](https://www.nuget.org/packages/CommonNetFuncs.Hangfire/)

Helpers for authenticated Hangfire dashboard access, job cancellation/matching, job failure context, and shutdown observation. This package targets `net11.0` and assumes Hangfire storage and server configuration have already been supplied by the host application.

## Contents

- [CommonNetFuncs.Hangfire](#commonnetfuncshangfire)
  - [Contents](#contents)
  - [Installation](#installation)
  - [Dashboard authorization](#dashboard-authorization)
  - [Job matching and deletion](#job-matching-and-deletion)
  - [`HangfireJobException`](#hangfirejobexception)
  - [Shutdown observation](#shutdown-observation)
  - [Waiting for jobs](#waiting-for-jobs)
  - [License](#license)

## Installation

```bash
dotnet add package CommonNetFuncs.Hangfire
```

## Dashboard authorization

`HangfireAuthorizationFilter` implements `IDashboardAuthorizationFilter`; its constructor takes a `FrozenSet<string>`.

```csharp
using System.Collections.Frozen;
using CommonNetFuncs.Hangfire;
using Hangfire;
using Hangfire.Dashboard;

FrozenSet<string> roles = new[] { "Admin", "Manager" }.ToFrozenSet();
HangfireAuthorizationFilter roleFilter = new(roles);

// Authentication middleware must run before the dashboard.
app.UseAuthentication();
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = [roleFilter]
});

// Hangfire calls this method for dashboard requests. A unit/integration helper can
// invoke the same public entry point with the context supplied by the host:
static bool CheckAuthorization(
    HangfireAuthorizationFilter filter,
    DashboardContext context) =>
    filter.Authorize(context);
```

The filter only checks an already-populated `HttpContext.User`: unauthenticated users are denied. If the set is nonempty, access is granted when the user has **any** listed role; names use `ClaimsPrincipal.IsInRole`. If the set is empty, every authenticated user is granted access.

```csharp
FrozenSet<string> noRoleRestriction = FrozenSet<string>.Empty;
var authenticatedOnly = new HangfireAuthorizationFilter(noRoleRestriction);
```

It does not authenticate a request, challenge a client, restrict the dashboard hostname, or configure Hangfire itself. Apply ASP.NET authentication and network exposure controls separately.

## Job matching and deletion

`HangfireJobHelpers` reads enqueued jobs for the named queue and processing/scheduled jobs globally, filters by method/argument values, and calls `BackgroundJob.Delete` for matches. It uses `JobStorage.Current`, so Hangfire storage must be configured.

The strongly typed overload extracts the method name and argument names/values from an expression:

```csharp
public interface IReportJob
{
    void GenerateReport(int accountId, string format, int maxRetries = 3);
}

int accountId = 42;
List<string> deleted = "reports".FindAndCancelJobs<IReportJob>(
    job => job.GenerateReport(accountId, "pdf"));
```

For that expression, required/default argument rules matter: `null` values and values equal to a parameter's declared default are omitted as wildcards; no-argument/defaulted values therefore do not narrow the match. The lambda body must be a method call or `ArgumentException` is thrown. All matched values are converted with `ToString()` in the current process culture; use stable string representations when culture-sensitive values are involved.

The string overload permits manual subset matching and demonstrates the same operation:

```csharp
List<string> alsoDeleted = "reports".FindAndCancelJobs(
    methodName: "GenerateReport",
    matchParameters: new(StringComparer.OrdinalIgnoreCase)
    {
        ["accountId"] = "42",
        ["format"] = "pdf"
    });
```

Method names, parameter dictionary keys, and string representations of argument values are matched case-insensitively. An empty dictionary matches any arguments for the method. Only the supplied parameters must match.

`IsJobMatch` can be used without storage:

```csharp
using Hangfire.Common;
using System.Reflection;

MethodInfo method = typeof(IReportJob).GetMethod(nameof(IReportJob.GenerateReport))!;
Job job = new(typeof(IReportJob), method, [42, "pdf", 3]);
bool matches = HangfireJobHelpers.IsJobMatch(
    job, "generatereport",
    new() { ["accountId"] = "42" });
```

Queue checks have two public overloads:

```csharp
bool inQueue = HangfireJobHelpers.IsJobInQueue("123", "reports");

using Hangfire.Storage.IStorageConnection connection =
    JobStorage.Current.GetConnection();
bool inQueueWithConnection =
    HangfireJobHelpers.IsJobInQueue("123", "reports", connection);
```

They read the job's `Queue` storage parameter. A missing parameter or a storage/lookup exception returns `true` conservatively to avoid false-negative deletion. Consequently, `true` does not prove the queue is known.

Deletion is not cooperative cancellation: deleting a processing job does not necessarily stop code already executing. The helper also attempts no worker-thread abort and returns IDs only for deletions that Hangfire reports as successful. These operations are parallelized across the three states and use `JobStorage.Current`; failures from storage or deletion can surface to the caller. Do not rely on the helper's internal lock-observation logging as proof that a distributed lock was removed—it currently only reads/logs job data.

## `HangfireJobException`

The type stores optional `OperationName`, optional integer `EntityId`, and `AllowRetry`. Its `Message` adds operation/entity context and appends `[PERMANENT FAILURE - NO RETRY]` when `AllowRetry` is false.

Every public constructor is shown below:

```csharp
Exception inner = new InvalidOperationException("database unavailable");

HangfireJobException a = new();
HangfireJobException b = new("Job failed");
HangfireJobException c = new("Job failed", inner);
HangfireJobException d = new("Permanent validation error", allowRetry: false);
HangfireJobException e = new(
    "Notification failed", "SendNotification", allowRetry: true);
HangfireJobException f = new(
    "Customer update failed", "UpdateCustomer", 42, allowRetry: false);
HangfireJobException g = new(
    "Dependency failed", inner, allowRetry: true);
HangfireJobException h = new(
    "Customer update failed", "UpdateCustomer", 42, inner, allowRetry: false);

Console.WriteLine(h.OperationName);
Console.WriteLine(h.EntityId);
Console.WriteLine(h.AllowRetry);
Console.WriteLine(h.InnerException);
Console.WriteLine(h.Message);
```

The throwing helpers each have a default `allowRetry` value of `true` in the current API, despite comments that describe some defaults as false:

```csharp
HangfireJobException.ThrowEntityNotFound("User", 42, "LoadUser");
HangfireJobException.ThrowEntityNotFound(
    "Document", "doc-42", "LoadDocument", allowRetry: false);
HangfireJobException.ThrowEntityNotFound(
    "Transaction", 9876543210L, "LoadTransaction", allowRetry: true);
HangfireJobException.ThrowValidationFailed(
    "Email is invalid", "ValidateEmail", 42, allowRetry: false);
HangfireJobException.ThrowDependencyUnavailable(
    "PaymentGateway", "ChargePayment", allowRetry: true);
```

String and `long` not-found overloads include the ID in `Message` but cannot populate the `int? EntityId` property. `ThrowDependencyUnavailable` currently sets `EntityId` to `0`.

`AllowRetry` is context only. **This package contains no Hangfire filter or automatic retry policy that reads it.** Configure retry attributes/filters and storage policy separately if `allowRetry: false` must actually prevent retries.

## Shutdown observation

Register `HangfireShutdownMonitor` as a hosted service after configuring Hangfire:

```csharp
builder.Services.AddHangfire(configuration => configuration
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connectionString));
builder.Services.AddHangfireServer();
builder.Services.AddHostedService<HangfireShutdownMonitor>();
```

Its constructor requires `IServiceProvider` and `IHostApplicationLifetime`. `StartAsync` registers an `ApplicationStopping` callback; `StopAsync` returns immediately:

```csharp
HangfireShutdownMonitor monitor = new(
    serviceProvider, hostApplicationLifetime);
await monitor.StartAsync(CancellationToken.None);
await monitor.StopAsync(CancellationToken.None);
```

On shutdown it synchronously resolves a concrete `BackgroundJobServer`, reads processing/enqueued/scheduled statistics from `JobStorage.Current`, disposes that server, and logs either no pending jobs or the three counts. Exceptions are caught and logged. This does not wait for jobs, drain every Hangfire server, or guarantee persistence/recovery. It assumes durable Hangfire storage, and resolving/disposal can fail if the concrete server is not registered through the expected DI service. If the server is also owned by Hangfire's hosting integration, coordinate its disposal rather than assuming this monitor is the only owner.

## Waiting for jobs

```csharp
await WaitForHangfireJobsToComplete.WaitForAllHangfireJobsToComplete();
await WaitForHangfireJobsToComplete.WaitForAllHangfireJobsToComplete(
    checkIntervalSeconds: 10,
    maxWaitMinutes: 10);
```

The method checks `JobStorage.Current` every five seconds by default and returns when enqueued + processing + scheduled counts become zero. It returns (after logging a warning) when 60 minutes have elapsed by default, so return does **not** prove all jobs completed. There is no cancellation token and no returned success result. Job arguments/types are not controlled here; use stable, serialization-safe job inputs and a bounded application shutdown policy. Inputs are passed directly to `TimeSpan`/`Task.Delay`, so validate external values before calling.

## License

MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
