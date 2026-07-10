# ADR 0008 — Client IP is not taken from untrusted forwarded headers

- Status: Accepted
- Date: 2026-07-10

## Context
`ExtractClientIp` preferred the client-supplied `X-Forwarded-For` header over
`Connection.RemoteIpAddress`. XFF is trivially spoofable — any client can set it to any value —
so anything built on the logged `ClientIp` (audit trails, forensics, abuse analytics) can be
poisoned with a single header. ASP.NET Core already has the correct answer to this:
`ForwardedHeadersMiddleware` validates known proxies and rewrites `Connection.RemoteIpAddress`
accordingly. A logging library must not duplicate that trust decision on its own, using a
weaker rule that trusts the header unconditionally.

## Decision
Add an option `TrustForwardedHeaders`, default `false`. When off, `ClientIp` is
`Connection.RemoteIpAddress`; the raw XFF value is still captured, but separately, as a new
`ForwardedFor` field — redacted through `CompiledRedactionPatterns.Redact()` like every other
client-supplied value (F1b). When on, the v2 behavior is restored: the first XFF entry wins.
The default is secure; the escape hatch exists, is explicit, and is documented to be enabled
only behind a reverse proxy the operator controls with `ForwardedHeadersMiddleware` configured.

## Consequences
- Breaking behavior change for deployments that relied on XFF without
  `ForwardedHeadersMiddleware`: `ClientIp` now reflects the proxy's address unless that
  middleware is configured — which is the correct fix, not a regression.
- Request summaries gain a `ForwardedFor` field when the header is present.
- Tests: XFF ignored by default; honored under the flag; `ForwardedFor` redacted.
