# ADR 0029 - Framework category defaults

- Status: Accepted
- Date: 2026-10-17

## Context

With the default options, one failing request exported about 16 lines. The `Error` request summary
flushed the trace's held-back events, which were almost all framework `Information` events: Hosting
`Request starting` / `Request finished`, Routing `Executing endpoint`, `HttpResults` `Setting HTTP
status code`, `Npgsql.Command`, and four `System.Net.Http.HttpClient` handler lines per outbound call.
The `Error` also stepped the switch up to `Information` for `DurationSeconds`, so every request in
that window exported the same framework lines. Those events repeat the request summary and the spans;
the lines that explained the failure were the application `Warning` and the summary.

Every lever existed, but none was on by default: `CategoryFloors` and `PreErrorBufferExcludeCategories`
are empty, and a floor alone does not stop the flush (ADR 0024 D2, ADR 0028). A consumer had to know
both options and list the same prefixes twice.

## Decision

**D1. `FrameworkCategoryDefaults` (`Recommended` | `None`, default `Recommended`) applies a built-in rule
table.** Each rule is a prefix, a floor, and whether the category's events below `Warning` stay in the
pre-error buffer:

| Prefix | Floor | Kept in buffer |
|---|---|---|
| `Microsoft.AspNetCore.Hosting.Diagnostics` | Warning | no |
| `Microsoft.AspNetCore.Routing` | Warning | no |
| `Microsoft.AspNetCore.Mvc` | Warning | no |
| `Microsoft.AspNetCore.Http.HttpResults` | Warning | no |
| `Microsoft.AspNetCore.StaticFiles` | Warning | no |
| `Microsoft.AspNetCore.Cors` | Warning | yes |
| `System.Net.Http.HttpClient` | Warning | no |
| `Polly` | Warning | no |
| `Microsoft.EntityFrameworkCore.Database.Command` | Warning | no |
| `Microsoft.EntityFrameworkCore.Infrastructure` | Warning | no |
| `Npgsql.Command` | Warning | no |
| `Yarp.ReverseProxy.Forwarder` | Warning | yes |

CORS and the YARP forwarder stay in the buffer because a failed CORS check or a forwarding error
explains the failing request. Authentication, Authorization, Antiforgery, Kestrel, DataProtection,
health checks, `Microsoft.Hosting.Lifetime` and gRPC are not listed: their `Information` events are rare
or explain a failure. The floors never pass `Warning`, so no framework `Warning`, `Error` or `Fatal` is
hidden, and the buffer exclusion applies below `Warning` only, as in ADR 0028 D2.

**D2. A scalar preset, not seeded collections.** The configuration binder adds keys to an existing
dictionary and cannot remove one, and a JSON `{}` or `[]` binds nothing into a collection that already
holds entries, so a dictionary or array seeded with the defaults could not be cleared from appsettings. An enum binds by overwrite, also from an
environment variable (`SerilogStepUp__FrameworkCategoryDefaults=None`). The table is internal and the
options object is not written to; `CategoryFloors` still starts empty.

**D3. The user's `CategoryFloors` entries decide first.** `CategoryFloorMap` looks up the user entries
(most specific key wins) and falls back to the defaults (most specific prefix wins) only when no user
key matches. A single longest-prefix lookup over both sets was rejected: a user key `System.Net.Http`
could never lower the longer default `System.Net.Http.HttpClient`. A user entry is an ordinary floor and
is kept in the buffer (ADR 0024 D2); listing it in `PreErrorBufferExcludeCategories` keeps it out. This
amends ADR 0024 D5 for the defaults.

**D4. Modes.** `AlwaysOn` skips the defaults, as it skips `NeverStepUpCategories` (ADR 0021 D4): a
developer running it wants the detail. Diagnostic mode lifts the defaults like any floor, so their events
export at `DiagnosticLevel`, and also lifts their buffer exclusion; a `DiagnosticExemptCategories` entry
keeps a default and may name a default prefix while the preset is `Recommended`.

**D5. Shipped as a minor release (5.4.0).** No API is removed, `Warning` and above are unchanged, and
ADR 0021 shipped a non-empty category default the same way. The change is listed under "Changed".

## Consequences

- The example request exports the application `Warning`, the request summary and the step-up notice.
  Framework `Information` is neither exported during the step-up window nor flushed by an `Error`.
- Amends ADR 0021 D1: the EF Core SQL before an error is no longer flushed by default. The
  `NeverStepUpCategories` pin still applies and matters again with `None`.
- Consumers with `BaseLevel` `Information`, or `Mode` `Disabled` at `Information`, lose the listed
  framework `Information` lines from normal export too. Dashboards built on them need `None` or a
  `CategoryFloors` entry at `Information`.
- Each held or exported event with a `SourceContext` that no user key matches scans the 12 default
  prefixes: about 11 ns per event on the benchmark machine, measured on the lookup alone, 2 ns when a
  default matches, with no allocation. A merged single table, a first-character prefilter and a
  per-`SourceContext` cache were measured at 0.7, 1.5 and 9 ns less; none was taken, the cache because
  it grows with every distinct runtime category. Events below the enrichment gate are refused before
  the lookup.
- A broad user key such as `"Microsoft": "Warning"` matches every `Microsoft.*` default, so those
  categories get the user floor and are flushed on an error again. Narrower user keys, or
  `PreErrorBufferExcludeCategories`, restore the exclusion.
- The table may change in a minor release; each change is a changelog entry.
