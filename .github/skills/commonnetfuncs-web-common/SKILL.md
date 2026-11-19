---
name: commonnetfuncs-web-common
description: 'Use CommonNetFuncs.Web.Common for MIME types, URI/query helpers, JSON naming, security-header defaults, and web validation attributes.'
---

# CommonNetFuncs.Web.Common

Use these shared helpers for web applications rather than for endpoint
authorization or application-specific policy. The project targets `net11.0`.

## Workflow

1. Use `ContentTypes` for supported MIME/extension mappings and handle unknown
   values; use `UriHelpers` for query, date, and URI-redaction operations.
2. Configure `PascalCaseJsonNamingPolicy` on the intended
   `JsonSerializerOptions` only if its naming behavior matches the API contract.
3. Use `SecurityHeadersStore` as a starting point for response headers, and
   apply the provided validation attributes to appropriate models. Review the
   application's own requirements before deploying.

## Important constraints

- MIME lookup is not content validation. For uploads, independently check
  allowed types, size, and content rather than trusting an extension or header.
- URI/query helpers format or transform values, but not every helper URL-encodes
  its input (for example, `ListToQueryParameters` appends keys and values
  directly). Encode query components as required by the target API. These
  helpers do not authorize a URL or make it safe to fetch; validate schemes and
  destinations for outbound requests, and verify that redaction covers the
  application's secret fields before logging.
- Pascal-case conversion changes the first character; do not assume it
  rewrites every possible naming convention.
- `SecurityHeadersStore`'s bundled CSP includes third-party CDN origins in
  `script-src` (listed as HTTP URLs, with `upgrade-insecure-requests` also
  enabled) and is not a production-ready policy. Review and replace it with a
  restrictive policy appropriate to the application. Default security headers
  and validation attributes do not replace authentication, authorization, or
  database constraints.

See [UriHelpers.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Common/UriHelpers.cs) and the
[README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Common/README.md).
