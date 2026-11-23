---
name: commonnetfuncs-hangfire
description: 'Use CommonNetFuncs.Hangfire for dashboard authorization, contextual job exceptions, shutdown monitoring, and waiting for background jobs.'
---

# CommonNetFuncs.Hangfire

This package supplements Hangfire; configure Hangfire storage, server, and
authentication separately. It targets `net11.0`.

## Workflow

1. Configure authenticated dashboard access with `HangfireAuthorizationFilter`
   and a `FrozenSet<string>` of allowed roles. A nonempty set accepts any
   matching role; an empty set permits **every authenticated user**.
2. Register `HangfireShutdownMonitor` as shown in the
   [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Hangfire/README.md), and use
   `WaitForAllHangfireJobsToComplete` only when a bounded wait is appropriate.
3. Use `HangfireJobException` for operation/entity context. Configure actual
   Hangfire retry behavior independently.

## Important constraints

- The dashboard filter does not authenticate users; ensure authentication runs
  before dashboard authorization and restrict dashboard exposure.
- `HangfireJobException.AllowRetry` is a property only. This package does not
  include a retry filter that reads it; setting it to `false` does not itself
  suppress retries. Add a suitable application filter or configure retries.
- The wait helper defaults to polling every 5 seconds for up to 60 minutes and
  logs/returns on timeout. Return does not guarantee all jobs completed.
  Recovery across restarts depends on durable Hangfire storage.
- Job types, method names, and arguments are serialized into storage. Use
  stable, serialization-friendly arguments; do not enqueue live streams,
  scoped services, or secrets.

See [HangfireAuthorizationFilter.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Hangfire/HangfireAuthorizationFilter.cs)
and [HangfireJobException.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Hangfire/HangfireJobException.cs).
