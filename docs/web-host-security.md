---
description: CORS policy, session and token cookies, and the Data Protection key ring shared by all replicas.
applies_to: src/BotServices/Configuration/*Cors*.cs, src/BotServices/Configuration/Configure*Options.cs
---

# CORS, cookies and the Data Protection key ring

## CORS and cookies (AB#7270)

- CORS = the shared known-origins policy of `AddOctoServiceInfrastructure()` (`app.UseCors()` without a policy): allowed
  origins are the registered client origins of the addressed tenant (system routes and unknown tenants: system tenant),
  never `*`, never credentials. `BotCorsConfiguration` only adds the tus response headers (`Upload-Offset`,
  `Upload-Length`, `Tus-Resumable`, `Location`) and a 1 h preflight cache via `OctoCorsOptions`. A browser app that calls
  bots must be a registered client origin. `CorsAndCookieWiringTests` pins this; the policy itself is tested in
  octo-common-services (`KnownOriginsCorsPolicyTests`).
- Session cookie of the OIDC login (`Octo-BotServices`): HttpOnly, Secure, `SameSite=Lax`. No cross-site flow needs
  `None`: the identity callback is same-site and the SPAs use bearer headers.
- `OctoIdentityAccessToken` (Studio opens `/ui/jobs?jwt_token=…`): set by `UseOctoCookieBasedAuthentication(
  BotServiceConstants.HangfireDashboardBranchPath)` with HttpOnly, Secure, `SameSite=Strict`, `Path=/ui`; outside `/ui`
  neither the query token nor the cookie becomes an Authorization header. After the update an old cookie with
  `Path=/` stays in the browser until it expires; it is ignored outside `/ui`.

## Data Protection key ring shared by all replicas (AB#9172)

Data Protection keys (cookies, OIDC `state`/`nonce`, antiforgery) are persisted with `AddOctoDataProtectionKeyRing("OctoBotServices")` (`octo-common-services/docs/data-protection.md`): one key ring shared by all replicas and kept across restarts, stored in the system tenant and encrypted with the `SecretEncryption` key ring. Without a key ring the service keeps the local default directory (one ring per pod) and logs a warning; then multi-replica sign-in fails again. Roll out an identity service that ignores prefixed key names first (soft order).
