# ADR 0045 - The caller's session id is logged, and token ids skip redaction by default

- Status: Accepted
- Date: 2026-11-28
- Amends: ADR 0031 D1

## Context

The inbound request summary and the request completion event write the caller token's `jti` claim as `Jti`. The
`sid` claim, the session the token belongs to, is not logged, so the requests of one session cannot be linked
when each refresh issues a new `jti`. ADR 0031 D1 made `RedactionExemptProperties` empty by default, so a broad
pattern (a bare UUID, say) masks `Jti` unless the consumer lists it.

## Decision

**D1. `Sid` is written next to `Jti`, from the inbound caller's token only.** The value is the `sid` claim, else
the `ClaimTypes.Sid` claim (the type the JWT handler maps `sid` to while `MapInboundClaims` is on), else no field.
It is written where `Jti` is: the inbound request summary from `Compact` (short key `sid` under `Minimal`) and the
request completion event, which keeps its own `Sid` under ADR 0043. Outbound summaries, audit records and
application events are not affected. `ConsoleTextProperties` keeps its default.

**D2. `RedactionExemptProperties` defaults to `["Jti", "Sid"]`.** The property becomes `string[]?`. When it is not
configured, the default applies after configuration, as for `ConsoleTextProperties`. A configured list replaces
the default and blank entries are ignored, so `[""]` exempts nothing. The token ids are pseudonymous correlation
keys, not secrets, and masking them defeats their purpose.

**D3. The change ships as 9.2.0.** `MIGRATION.md` says `Jti` and `Sid` are exempt by default and that
`"RedactionExemptProperties": [""]` restores the old behaviour.

Rejected: decoding the bearer token of outbound calls, since the token is not validated, may be opaque, and is a
secret the library otherwise only redacts. Pushing `Jti` and `Sid` onto every event in the request, which no one
asked for. A non-null default the configured list is appended to, since the configuration binder appends to an
array and `Jti` could then never be redacted again.

## Consequences

- A consumer with a broad pattern and no exempt list now exports `Jti` in clear after the upgrade.
- An application property named `Sid` or `Jti` skips the patterns on every event (ADR 0031: an exemption is by
  name).
- Changing the property to `string[]?` breaks no compiled consumer but may raise a nullable warning in code that
  reads it.
- Tests: `Sid` from `sid`, from `ClaimTypes.Sid`, absent without either; on the summary (Compact, Minimal key) and
  the completion event; not duplicated on the Omit path; the default exemption, a configured list replacing it,
  `[""]` exempting nothing.
