---
name: commonnetfuncs-web-middleware
description: 'Use CommonNetFuncs.Web.Middleware for ASP.NET Core JSON Patch, caching, response headers, logging, OPTIONS, and XSRF tokens. Consult for pipeline order and trust boundaries.'
---

# CommonNetFuncs.Web.Middleware

Select only the middleware the application needs and register its prerequisites
and pipeline order. See the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Middleware/README.md)
and current extension methods before copying configuration.

## Workflow

1. For JSON Patch, register `JsonPatchDocumentConverterFactory` in the
   appropriate JSON options and use `UseJsonPatchRequestBody` before routing
   and model binding. Authorize the endpoint and allowed patch paths.
2. For in-memory caching, register `AddMemoryCache` and configure the current
   `UseMemoryValueCaching` extension and `CacheOptions`. The README may use the
   older name `UseMemoryCache`.
3. Place custom headers, logging, OPTIONS, and XSRF middleware according to the
   desired pipeline scope; check prerequisites and response behavior.

## Security boundaries

- The opt-in response cache key includes path, selected query data, POST body
  hash, and `Accept`, but **not** request method, user identity, authorization
  headers, cookies, or host. Never use it for personalized or sensitive
  responses across users/tenants, or for multiple methods whose responses can
  differ at the same path. Query-driven cache/eviction controls are
  caller-accessible; restrict their use where necessary.
- `UseXsrfToken` publishes an antiforgery cookie; it does not validate unsafe
  requests. Configure antiforgery validation on those endpoints. Its defaults
  use `secure: true` and `httpOnly: false`, the latter allowing JavaScript to
  read the token; use HTTPS and understand the exposure.
- CORS/OPTIONS handling, custom security headers, and JSON Patch are not
  authentication or authorization. Configure allowed origins and security
  headers for the actual application, and prevent sensitive response logging.

See [MemoryCacheMiddleware.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Middleware/CachingMiddleware/MemoryCacheMiddleware.cs)
and [JsonPatchMiddleware.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Middleware/JsonPatchMiddleware.cs).
