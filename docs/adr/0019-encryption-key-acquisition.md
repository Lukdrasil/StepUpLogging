# ADR 0019 — The public key is fetched from the receiver at runtime and used past its TTL

- Status: Advisory (withdrawn as a build decision, 2026-08-13)
- Date: 2026-08-13
- Issue: #22 (Part B)

> **Withdrawn as a build decision:** during planning the developer narrowed the package to
> spool-and-deliver logic only. Encryption **including key acquisition** lives entirely behind the
> `IAuditPayloadEncryptor` port (ADR 0017 D3), i.e. in the client application's implementation.
> Nothing below is built by this library; it is preserved as advisory input for whoever writes
> that implementation.

## Context

The producer (app1) encrypts each audit record with a public key whose private half lives only in
the receiver (app2). app2 is a single service that does both jobs: it publishes the public key via
Ogham's JWKS endpoint, and it receives records, decrypts them and writes them to immudb.

The key is not a session key. It is an **archival key**: it protects records for the whole
retention period, and it can never be rotated retroactively, because the producer cannot read back
what it wrote and therefore cannot re-encrypt anything. Two consequences follow — losing the
private key destroys the archive rather than merely stopping audit, and app2 must retain every
private key it has ever used.

The producer must therefore obtain the public key, choose the right one, and keep working when
app2 is unreachable. The Rejected alternatives section below weighs the four options considered
against the decision below.

## Decision

1. **The key is fetched from app2's JWKS endpoint at runtime and cached**, rather than configured
   statically. This is what buys rotation without redeploying every producer.

2. **Key selection: filter the JWKS on `use: "enc"`, then take the newest.** Ogham also publishes
   *signing* keys for its token feature; encrypting with one of those would produce records app2
   cannot decrypt, and the failure would only surface on the far side.

3. **The cache TTL comes from the response's `Cache-Control: max-age`**, clamped by a minimum and
   maximum in options. app2 therefore controls the refresh cadence: before a rotation it shortens
   `max-age` and every producer picks the new key up sooner, with no change to their configuration.
   The clamp exists so app2 cannot set a TTL of a year or of a second.

4. **A stale key keeps being used when refresh fails, and the last known key is cached to disk.**
   A public key does not expire cryptographically — the TTL is a refresh hint, not a validity
   bound, and app2 still holds the matching private key. A restart during an app2 outage starts
   from the disk cache. Audit keeps working, records accumulate in the spool, and the health check
   reports the condition.

5. **With neither a live key nor a cached one, the host refuses to start.** Decision 4 presupposes
   a cache that a first deployment does not have; on that path there is no key at all, and issue
   #22 B3 already forbids any fallback to writing plaintext. Failing the deployment is the only
   loud option, and it is narrow — it applies solely when app2 has never been reached from this
   host. Once one fetch has succeeded, decision 4 covers every later outage.

6. **Substitution of the public key is an accepted risk**, recorded here rather than mitigated.

## Consequences

- An app2 outage does not stop a **running** application, and does not stop one that has fetched a
  key at least once on this host. It does stop a **first** deployment, by decision 5 — there is no
  key anywhere on that path, and the alternative would be running unencrypted. Without decision 4,
  a failed refresh would make `WriteAsync` throw, that exception would propagate to the business
  call site under ADR 0016 D2, and a key-service blip would take down every audited operation in
  the application — a far worse failure than the one being guarded against.
- A key withdrawn from circulation keeps being used indefinitely by a producer that cannot reach
  app2, and the disk cache can be overwritten by anyone with write access to the host. Both are
  accepted, and both are the same class of risk as decision 6.
- Rotation is an app2-side operation with no producer coordination: spooled records carry the `kid`
  they were encrypted under, and app2 still holds that private key.

## Accepted risk — a substituted public key is undetectable

app1 cannot tell a genuine public key from one supplied by whoever controls app2, DNS to it, or a
proxy in front of it. A record encrypted to an attacker's key is byte-for-byte indistinguishable
from a correct one, app2 would still answer 2xx, and from that moment the audit trail is readable
by the attacker and unreadable by its owner. The TTL reopens this window on every refresh rather
than only at deployment.

**This decision assumes the network between app1 and app2 is trusted** — a private network, or
mutual TLS. That assumption is the whole of the mitigation, and this ADR must be revisited if it
stops holding.

*The mitigation not taken:* app2 returns a **signed** JWKS and app1 verifies the signature against
a long-lived verification key held in static configuration. The trust anchor is then out-of-band
and static while the encryption key still rotates freely, and the signed response can safely be
cached to disk. It was rejected as work in Ogham and app2 that nobody is planning, and because it
introduces a second key for someone to manage.

## Rejected alternatives

- **Static configuration of the public key.** No network trust needed at all, and the strongest
  option in the analysis — but rotation then requires redeploying every producer, which is the
  reason the fetch exists.
- **Pinning app2's TLS certificate.** Cheaper than a signed JWKS and needs nothing from Ogham, but
  breaks on every certificate renewal.
- **Pinning the expected `kid` in app1's configuration.** Deterministic and auditable, but throws
  away rotation-without-redeploy.
- **Refusing to encrypt once the TTL expires.** The strongest freshness guarantee, at the price of
  making the entire application's availability depend on app2's.
