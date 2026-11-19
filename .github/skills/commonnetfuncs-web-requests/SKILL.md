---
name: commonnetfuncs-web-requests
description: 'Use CommonNetFuncs.Web.Requests for HTTP, JSON, streaming, MessagePack, resilience, and REST client DI. Consult for timeouts, errors, retries, and token safety.'
---

# CommonNetFuncs.Web.Requests

Use `RestHelpers`/`RequestOptions<TBody>` for HTTP calls, or configure the
named-client wrapper with `AddRestClientFactory`, `AddRestHelpersWrapper`, and
`AddRestClient` as shown in the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Requests/README.md).

## Workflow

1. Select the destination, method, body, serializer, authentication, and a
   positive request timeout in seconds. Pass a cancellation token.
2. Use `RestRequestObject<TResponse, TBody>` when status, headers, or error
   information is required; inspect `Response`, `Result`, and `Error`. The
   simpler `RestRequest` can log and collapse failures or cancellation to a
   default/null result.
3. For streaming requests, consume or dispose the async enumerator promptly so
   the response and reader are released. Do not dispose the shared client per
   request.

## Important constraints

- The shared `HttpClient` has an infinite timeout, but the helper applies a
  linked per-request timeout: `RequestOptions.Timeout` defaults to 100 seconds
  when unset or nonpositive. Set an appropriate explicit timeout for the call.
- Wrapper retry and token-refresh behavior is configurable and is not
  automatically supplied by every bare helper. Retry only operations that are
  idempotent or protected with an idempotency strategy.
- Validate URLs and redirect policy for server-side requests, require HTTPS
  where appropriate, keep certificate validation enabled, and protect bearer
  tokens and potentially sensitive response/error bodies.
- The static handler limits connections per server; this is not an
  application-wide concurrency or rate limit.

See [RestHelpersStatic.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Requests/Rest/RestHelpersStatic.cs)
and [RestHelpersCommon.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Requests/Rest/RestHelpersCommon.cs).
