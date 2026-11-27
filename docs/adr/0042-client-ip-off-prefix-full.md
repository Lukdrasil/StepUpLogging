# ADR 0042 - The client IP is off by default, and logs and audit choose separately between off, prefix and full

- Status: Accepted
- Date: 2026-11-27

## Context

The library writes the full client address in three places: `ClientIp` in the inbound request summary, `ClientIp`
in the completion event of `UseStepUpRequestLogging`, and `AuditEvent.SourceIp`. All three come from
`RequestClient.Addresses`, the one client-address rule (ADR 0008). A full IP address is personal data under GDPR,
and consumers had no option to log less of it, or none. The full address also leaks through two other paths: the
`ForwardedFor` field of the Full summary, and the `Headers` dictionary of the completion event, where
`X-Forwarded-For`, `Forwarded`, `X-Real-IP` and `X-Original-For` are not sensitive headers. Some consumers need
the address in the audit trail but not in the logs.

## Decision

**D1. Two options share one enum, `ClientIpMode: Off | Prefix | Full`, and both default to `Off`.**
`ClientIpLogging` governs the logs: `ClientIp` in the summary and in the completion event, `ForwardedFor`, and the
four IP headers in `Headers`. `ClientIpAudit` governs `AuditEvent.SourceIp`. The address is still resolved by the
one rule in `RequestClient.Addresses`, and each caller passes its mode. Both options are read once at startup
like every other option, so a change needs a restart. `Off` omits the field, it does not write a marker. `Full`
is the 8.x behaviour.

**D2. `Prefix` writes the network in CIDR form**, /24 for IPv4 and /48 for IPv6, for example `203.0.113.0/24` and
`2001:db8:1234::/48`, so a reader can tell a client prefix from a client address. The prefix lengths are fixed.
The field names and the Minimal short key `ip` (ADR 0033) do not change.

**D3. An IPv4-mapped IPv6 address is masked as IPv4.** Kestrel on a dual-mode socket reports IPv4 clients as
`::ffff:a.b.c.d`. Their /48 would be `::/48` for every client.

**D4. Only a value that parses is masked, and it is parsed after redaction.** With `TrustForwardedHeaders` the
address is the first `X-Forwarded-For` entry, which may carry a port, brackets or arbitrary text. The redacted
value is parsed as an endpoint or an address. A value that does not parse, including one a redaction regex
changed, is omitted instead of written as raw text. Parsing the raw value would bypass redaction of a
client-supplied value.

**D5. The other IP paths in the logs follow `ClientIpLogging`.** Under `Prefix`, `ForwardedFor` is masked entry by
entry and entries that do not parse are left out. Under `Off`, `ForwardedFor` is omitted. Under both, the four IP
headers are redacted in the logged `Headers`. Other client-IP headers (for example `CF-Connecting-IP`) are the
consumer's to add to `AdditionalSensitiveHeaders`. Redaction applies to the logged copy only: the library never
changes the request's headers, so forwarding them is unaffected. The audit record carries no headers.

**D6. The change ships as 9.0.0.** The `Off` defaults remove the address from logs and audit records on upgrade.
`MIGRATION.md` says to set both options to `Full` to keep the 8.x behaviour.

Rejected: masking in an application enricher, which misses the audit record and duplicates the client-address
rule. One option for logs and audit, which cannot audit the address without logging it. A bool to switch logging
off next to a masking option, which allows the meaningless pair off plus prefix. Reloading the options at
runtime, which nothing else in the library does. Configurable prefix lengths, which no consumer asked for. A
zeroed address without the suffix (`203.0.113.0`), which cannot be told apart from a real one. Masking inside
`Forwarded` header values, which needs a parser for RFC 7239 syntax. A 8.x minor release with the `Off` defaults,
which would silently remove audit data.

## Consequences

- A client prefix is pseudonymous, not anonymous: a /24 or /48 still narrows the client down. The options reduce
  what is written and do not by themselves make a deployment GDPR-compliant.
- An audit endpoint that validates `SourceIp` as an IP address rejects records under `Prefix`. The Expirio audit
  service stores it as text up to 45 bytes, and the longest masked value is 19 characters.
- Outside these options: `client.address` on spans (the library does not register ASP.NET Core instrumentation),
  and an address the application itself puts in an audit record's `Data` or in its own log events.
- The four IP headers are redacted through the sensitive-header path, so under `Off` and `Prefix` a proxied request
  counts as a redaction (`request_redaction_applied_total`, the `ApplyRedaction` activity) like any other sensitive header.
- Tests: each output under each mode and the two options independent of each other, IPv4-mapped input, an XFF
  entry with port and brackets, an unparseable and a redacted entry omitted, `ForwardedFor` masked per entry and
  omitted under `Off`, the four headers redacted under `Off` and `Prefix` only.
