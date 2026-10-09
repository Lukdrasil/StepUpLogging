# ADR 0027 - Pre-error buffer: striped LRU and on-demand ring

- Status: Accepted
- Date: 2026-10-09
- Amends: ADR 0011 (the recorded global-lock ceiling), ADR 0015 (notes only; the floor stands). ADR 0023's
  decisions (exactly once, dropped on dispose) are unchanged.
- Amended 2026-10-09 (hold cost): D1, D5 and two Consequences below

## Context

`PreErrorBufferSink` kept the trace LRU behind one lock, `_lruGate`, held across the buffer lookup, the move
to the front of the list, the eviction and the enqueue. The lock was there for one reason: a concurrent
eviction of another trace must not remove this trace's buffer between its touch and its enqueue, which
would drop the event silently. ADR 0011 recorded it as a known ceiling and named approximate LRU as the
upgrade path.

`docs/performance/results-prebuffer.md` measured the ceiling: with 4096 traces passing through, eight threads
held in 711 ns each against 489 ns for one, and every hold of a new trace allocated 1032 B because each
trace's `Queue` was sized to the whole capacity (100 events by default, an 824 B array) however few events
the trace held.

## Decision

**D1. The LRU is split into stripes; a trace belongs to one.** `StripeCountFor(maxContexts)` is
`maxContexts / 64`, clamped to 1 to 16: one stripe per 64 of the cap, at most 16, so a cap of 1024 or more gives 16 stripes of at least 64 each and a cap below 128 gives one stripe that holds the whole cap (the eviction is then exact LRU). `StripeCapacities` spreads
`PreErrorMaxContexts` over the stripes with the remainder going to the first ones, so the capacities sum to
`PreErrorMaxContexts` and the buffer never keeps more traces than the option says. A trace id maps to its
stripe by `(uint)key.GetHashCode() % stripes`. `string.GetHashCode` is stable for the life of the process and
randomised per process, so a trace id cannot be chosen by a caller to aim at one stripe. Caps below 128
use one stripe, which is the exact LRU of before; every existing test (caps 1, 2, 16, 64) is in that range.

> **Amended (2026-10-09, hold cost):** a trace id maps to its stripe by FNV-1a over its last 4 characters
> (the whole key when it is shorter), mixed with a 32-bit finaliser and reduced to the stripe count by
> multiply-shift, not by `GetHashCode`. The result is the same in every process and does not hash the other 28
> characters, which `ConcurrentDictionary` then hashed again. W3C trace ids are random, so the last 4
> characters spread traces evenly over the stripes. The sentence above that a caller cannot aim a trace at a
> stripe no longer holds: a forged `traceparent` can steer its traces into one stripe, and that stripe then
> serialises its holds like the old global lock. The honest traces that share it (1 in 16 with 16 stripes) are
> evicted after 64 newer traces instead of after about 1024. No event is orphaned or exported twice, because
> the stripe lock and the eviction are still the same lock. The buffer dictionary keeps its randomised hash.

**D2. Each stripe has its own lock and its own order.** `TraceLruStripe` holds the linked list and the node
index for its traces, and nothing else: no buffers, no counters. `BufferEvent` locks the key's stripe,
re-checks the disposed flag, gets or creates the buffer, touches the key, evicts what the touch displaces
and enqueues, all under that lock. Evicting a key and holding it serialise on the same stripe lock because
they are the same stripe, so the orphaning the old lock prevented is still impossible. Two traces of
different stripes never wait for each other. The test hooks (`BeforeEnqueueTestHook`,
`AfterEnqueueTestHook`) stay on the sink, inside the stripe lock.

**D3. Eviction is least recently used within a stripe.** A full stripe evicts its own oldest trace, not the
oldest of the whole buffer. A stripe can be full while another has room, so the buffer can evict a trace
that a global LRU would have kept; with the default 1024 traces and 16 stripes of 64 a trace is evicted
after 64 newer traces hashed to its stripe, about 1024 newer traces in all. This is the price of D1 and is
stated in the `PreErrorMaxContexts` documentation.

**D4. `Dispose` drains the stripes in turn.** It sets a volatile flag, then locks each stripe, clears it,
and clears the buffers last. A hold inside a stripe lock finishes first; a hold that takes the lock after
its stripe was cleared sees the flag and returns. Nothing is left in the dictionary (ADR 0023 D3 stands).

**D5. A trace's ring starts small and grows to its capacity.** `Buffer` owns an array of
`min(4, capacity)` slots, a head and a count. When the array is full and below the capacity it doubles,
capped at the capacity, copying in order; at the capacity it overwrites its oldest event. A flush copies the
events oldest first into a snapshot, clears the array and resets the head and the count, so the event order
is that of before. The ring does not shrink after a flush. The `Queue` is gone because
`Queue.EnsureCapacity` overshoots (it grew to 128 for a capacity of 100). `LastTouchedUtc`, written on every
enqueue and never read, is removed with it.

> **Amended (2026-10-09, hold cost):** the ring grows 4, 16, then its capacity, not by doubling. A
> capacity below 16 goes from 4 to the capacity in one step. Per trace this is 376 B instead of 464 B at 9 to
> 16 events held, 1200 B instead of 744 B at 17 to 32 (worse), and 1200 B instead of 2104 B at 100; 5 to 8
> events held cost 376 B instead of 312 B (also worse), and a trace of 4 events or fewer is unchanged at
> 224 B. Measured at 10, 16, 17 and 100 events in `docs/performance/results-hold-cost.md`; the 5 to 8 band is
> arithmetic from the array sizes. The trade is that a trace crossing 16 events allocates its whole array at
> once, in return for three fewer copies and 904 B less at the default capacity of 100.

## Alternatives considered

- **Approximate LRU** (a timestamp per buffer and a periodic sweep, as ADR 0011 proposed). Rejected: it
  needs a sweeper thread or a sweep on the hold path, gives up exact eviction order at every cap, and the
  orphaning guarantee would have to be rebuilt another way.
- **A lock-free dictionary with per-key locks.** Rejected: every hold would still touch an order shared by
  all traces; striping removes the sharing instead of making it cheaper.
- **Striping the buffer dictionary too.** Not needed: `ConcurrentDictionary` already stripes its own locks,
  and its work happens under the stripe lock of the key.
- **A fixed 4 slots, no growth.** Rejected: the capacity (`PreErrorBufferSize`) is a published option and
  must stay reachable.

## Consequences

- Measured in `docs/performance/results-prebuffer.md`: eight threads over 4096 traces hold in 231 ns
  against 711 ns, over 256 traces in 106 ns against 292 ns; a new trace's first hold allocates 224 B against
  1032 B; a trace with 3 or 10 events held allocates 224 B or 464 B against 1.01 KB.
  The hold-cost amendment (`docs/performance/results-hold-cost.md`) brings the 10-event trace to 376 B.
- A trace that fills a 100-slot ring allocates 2104 B against 1.01 KB, because the ring doubles through 4,
  8, 16, 32, 64 and 100 slots. The crossover is between 32 and 64 events held. A host that fills its rings
  pays for that once per trace. After the hold-cost amendment the same ring allocates 1200 B (17 events
  held or more), against 744 B at 17 to 32 events before it; see `docs/performance/results-hold-cost.md`.
- Single-thread holds are about 15 to 20 ns slower (unresolved in a three-iteration run); **Hypothesis:** one
  more hash of the trace id to pick the stripe. The hold-cost amendment removes that hash; the single-thread
  holds measure 33 to 47 ns faster in `docs/performance/results-hold-cost.md`, unresolved by the 25 % rule.
- Eight threads that log in the same trace still queue on that trace's own buffer lock. Not changed here.
- `ContextCount` is at most `PreErrorMaxContexts` and, with all stripes in use, equal to it. The four buffer
  metrics keep their names and meaning; `buffer_evicted_contexts_total` counts one per evicted trace.
- Not in scope: ADR 0023 (exactly-once and drop-on-dispose are unchanged), `StepUpSink`, the wiring, and
  shrinking a ring after a flush.
