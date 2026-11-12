---
name: commonnetfuncs-web-jwt
description: 'Use CommonNetFuncs.Web.Jwt to issue JWTs with JwtManager. Consult for credential handling, signing keys, token validation, lifetimes, and refresh-token limitations.'
---

# CommonNetFuncs.Web.Jwt

`JwtManager.Authenticate` is a small JWT issuer, not a complete authentication
or token-validation system. It returns `JwtToken?`, with `null` for missing or
nonmatching credentials.

## Workflow

1. Review the `Authenticate` signature in
   [JwtManager.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Jwt/JwtManager.cs): it accepts
   submitted and expected username/password strings, environment, signing key,
   development and standard lifetimes, and optional issuer/email/audience.
2. Supply a high-entropy secret signing key from secure configuration, choose
   explicit issuer, audience, and suitable lifetimes, and protect the key.
3. Configure a separate authentication handler to validate incoming token
   signature, signing algorithm, issuer, audience, and expiration. Apply
   authorization policies independently.

## Security boundaries

- `Authenticate` compares supplied and expected credentials directly; it is
  not a password-hash verifier, account store, lockout system, or replacement
  for established authentication. Avoid storing plaintext passwords and use a
  proper password-verification/authentication system for real accounts.
- Tokens use HMAC-SHA512. Never treat parsed claims as trusted until the token
  has been validated with the correct key and validation parameters.
- Only the `Development` environment uses the development lifetime; other
  environments use the standard lifetime. Do not use a development policy as
  a production default.
- `JwtToken.RefreshToken` is not populated by this issuer. Implement secure
  refresh-token issuance, storage, rotation, and revocation separately if
  required; do not assume those features exist.

See the [README](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Jwt/README.md) and
[JwtToken.cs](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/CommonNetFuncs.Web.Jwt/JwtToken.cs).
