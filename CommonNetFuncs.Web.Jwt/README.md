# CommonNetFuncs.Web.Jwt

[![License](https://img.shields.io/github/license/ScarpittiWeb/common-net-funcs.svg)](https://opensource.org/licenses/MIT)
[![NuGet Version](https://img.shields.io/nuget/v/CommonNetFuncs.Web.Jwt)](https://www.nuget.org/packages/CommonNetFuncs.Web.Jwt/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CommonNetFuncs.Web.Jwt)](https://www.nuget.org/packages/CommonNetFuncs.Web.Jwt/)

A small JWT issuer for applications that already authenticate a username/password pair. `JwtManager.Authenticate` returns a token only when the submitted values exactly match the expected values. This package does not validate bearer tokens, manage users, verify password hashes, implement lockout, or authorize requests.

## Contents

- [CommonNetFuncs.Web.Jwt](#commonnetfuncswebjwt)
  - [Contents](#contents)
  - [Target frameworks](#target-frameworks)
  - [Installation](#installation)
  - [Public API](#public-api)
    - [`Authenticate`](#authenticate)
  - [Usage](#usage)
  - [Signing-key and lifetime guidance](#signing-key-and-lifetime-guidance)
  - [Token validation and authorization](#token-validation-and-authorization)
  - [License](#license)

---

## Target frameworks

The current package targets **.NET 11.0** and **.NET Standard 2.1**.

## Installation

```bash
dotnet add package CommonNetFuncs.Web.Jwt
```

## Public API

- `IJwtManager.Authenticate(...)`
- `JwtManager.Authenticate(...)`
- `JwtToken.Token`
- `JwtToken.JwtExpireTime`
- `JwtToken.RefreshToken`

### `Authenticate`

```csharp
JwtToken? Authenticate(
    string? userName,
    string? password,
    string actualUserName,
    string actualPassword,
    string environment,
    string key,
    TimeSpan devTokenLifespan,
    TimeSpan stdTokenLifespan,
    string? issuer = null,
    string? email = null,
    string? audience = null);
```

Behavior:

- Missing/whitespace submitted credentials, or an exact case-sensitive mismatch of username or password, return `null`.
- Matching credentials produce a compact JWT signed with symmetric **HMAC-SHA512**.
- The subject always contains the submitted name claim.
- An email claim is always added; it is an empty string when `email` is null. Non-blank `issuer` and `audience` values populate their standard token fields; null/whitespace values omit them.
- The environment comparison for the literal `Development` is case-insensitive. Only that environment uses `devTokenLifespan`; every other environment, including unknown values, uses `stdTokenLifespan`.
- `JwtExpireTime` is the same expiration used in the token descriptor.
- `RefreshToken` is not implemented and remains `null` for tokens issued by this package.
- Exceptions from token creation (for example, an unacceptable signing key or invalid lifetime input) are not converted to authentication failures; they propagate to the caller.

This is plain value comparison, not a password-hash verifier. `actualUserName` and `actualPassword` are expected values supplied by the application; they should not be hard-coded in source or read from a plaintext password store. Use an established identity/authentication system for real accounts.

## Usage

The following demonstrates the method through both the interface and its concrete implementation, including all optional parameters:

```csharp
using CommonNetFuncs.Web.Jwt;

string signingKey = configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is required.");

IJwtManager issuer = new JwtManager();

JwtToken? token = issuer.Authenticate(
    userName: submittedUsername,
    password: submittedPassword,
    actualUserName: expectedUsername,
    actualPassword: expectedPassword,
    environment: app.Environment.EnvironmentName,
    key: signingKey,
    devTokenLifespan: TimeSpan.FromHours(24),
    stdTokenLifespan: TimeSpan.FromMinutes(15),
    issuer: "https://identity.example.com",
    email: "person@example.com",
    audience: "orders-api");

if (token is null)
{
    return Results.Unauthorized();
}

string encodedToken = token.Token
    ?? throw new InvalidOperationException("The token was not created.");
DateTime? expiration = token.JwtExpireTime;
string? refreshToken = token.RefreshToken; // null: not populated by this issuer.

return Results.Ok(new
{
    access_token = encodedToken,
    expires_at_utc = expiration,
    refresh_token = refreshToken
});
```

All optional claim parameters can be omitted:

```csharp
using CommonNetFuncs.Web.Jwt;

var manager = new JwtManager();
JwtToken? token = manager.Authenticate(
    submittedUsername,
    submittedPassword,
    expectedUsername,
    expectedPassword,
    environment: "Production",
    key: signingKey,
    devTokenLifespan: TimeSpan.FromHours(8),
    stdTokenLifespan: TimeSpan.FromMinutes(15));
```

`JwtToken` is a mutable data class and can be created/read directly. It is annotated for MessagePack serialization on both targets and for MemoryPack on targets other than .NET Standard 2.1:

```csharp
using CommonNetFuncs.Web.Jwt;

JwtToken model = new()
{
    Token = "eyJ...",
    JwtExpireTime = DateTime.UtcNow.AddMinutes(15),
    RefreshToken = null
};

string? encoded = model.Token;
DateTime? expires = model.JwtExpireTime;
string? refresh = model.RefreshToken;
```

## Signing-key and lifetime guidance

- Load a high-entropy signing key from secret configuration. UTF-8 bytes from `key` become the symmetric key; do not use a short password or expose the key to clients.
- Choose the shortest practical lifetime and explicit issuer/audience values for the resource server.
- The package does not set `not before` or implement clock-skew policy, refresh tokens, rotation, revocation, or key rollover.
- HMAC signing produces a signed token, not an encrypted token. Clients and intermediaries can decode its claims.

## Token validation and authorization

Incoming bearer tokens must be validated separately. Configure an authentication handler with at least:

- the same HMAC key;
- the permitted `HS512` algorithm;
- the expected issuer and audience;
- signature and expiration/lifetime validation;
- an appropriate clock-skew policy; and
- authorization policies for protected endpoints.

Never trust decoded claims until validation succeeds. The issuer's direct credential comparison is independent from `AddJwtBearer`/authorization middleware and must not be mistaken for a complete login flow.

## License

Licensed under MIT. See [LICENSE](https://github.com/ScarpittiWeb/common-net-funcs/blob/main/LICENSE).
