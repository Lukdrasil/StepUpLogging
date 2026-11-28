# ADR 0044 - The client identity is logged in full on a client error request

- Status: Accepted
- Date: 2026-11-28

## Context

ADR 0042 made `ClientIpLogging` default to `Off`, so no request writes the client address to the logs. A request
answered with 4xx is the most likely trace of an attack (probing, credential stuffing, scanners), and investigating
or reporting it needs the address, and behind carrier-grade NAT also the source port. GDPR Art. 6(1)(f) with
recital 49 names network and information security as a legitimate interest for processing such data.

## Decision

**D1. A new option `ClientIpLoggingOnClientError: ClientIpMode`, default `Full`.** On a client error request
(status 400 to 499) the logs write the client address under `max(ClientIpLogging, ClientIpLoggingOnClientError)`,
so a 4xx never logs less than any other request. `Off` disables the escalation. The option is read once at startup.

**D2. The escalation covers every IP path of ADR 0042 D5 in the logs**: `ClientIp` and `ForwardedFor` in the
inbound request summary and the completion event (including the summary fields the completion event carries under
ADR 0043), and the four IP headers in the logged `Headers`. Secrets in `Authorization`, `Cookie` and the other
sensitive headers stay redacted. `ClientIpAudit`, outbound summaries and application events are not affected.

**D3. Two new fields follow the effective mode, not the status.** Under an effective `Full`, `RemotePort` (the
connection's source port) is written, and `RemoteIp` (the connection address, which the client cannot forge) is
written when `TrustForwardedHeaders` is on, since otherwise `ClientIp` already is that address. Under `Prefix` and
`Off` neither is written. Both are Compact fields, short keys `rip` and `rport` under Minimal.

**D4. All of 400 to 499, not configurable.** 5xx and unhandled exceptions are excluded: they are the
application's faults, not the caller's.

**D5. The change ships as 9.2.0.** It adds data and breaks no contract. `CHANGELOG.md` and `MIGRATION.md` say that
4xx requests now log the full address and how to turn it off. The startup report names the option.

Rejected: a bool next to `ClientIpLogging`, the pair ADR 0042 rejected. A configurable status list, which no
consumer asked for. A 10.0.0 release. Logging the `sub` claim, since `Jti` and `ClientId` already link a request to
its token.

## Consequences

- A 404 from a broken link logs a legitimate user's full address. The consumer's retention policy must cover it.
- The option reduces nothing by itself to GDPR compliance: the consumer still documents the legitimate interest and
  the retention.
- Tests: each mode of the new option against each `ClientIpLogging`, 399, 400, 499 and 500, an exception (500),
  `RemoteIp` only with `TrustForwardedHeaders`, the IP headers unredacted only on 4xx, the secrets still redacted.
