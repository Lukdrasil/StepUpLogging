using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Delivers spooled audit records to the configured endpoint, oldest first, and deletes each only
/// once the endpoint has confirmed it stored it (ADR 0020 D7). A record it can never deliver goes
/// to <see cref="DeadLetterBox"/>; anything else that goes wrong leaves the record spooled for the
/// next attempt, which backs off while the endpoint keeps failing.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is at-least-once, so <b>the receiving endpoint must deduplicate on the envelope's
/// <c>eventId</c></b>. That is a correctness requirement, not a precaution: a crash between the
/// endpoint storing a record and this worker deleting its spool file sends the same record again on
/// the next start, and so does a spool file a crash left mid-rename (ADR 0020 D1-D2).
/// </para>
/// <para>
/// Whatever credentials the endpoint requires are configured on the <see cref="HttpClient"/> named
/// <see cref="HttpClientName"/>. The worker sends what that client is set up to send and never sees
/// a credential, exactly as it never sees a key: the record's payload is opaque to it.
/// </para>
/// <para>
/// <b>That client must be configured with <c>AllowAutoRedirect = false</c>.</b> A redirect followed
/// automatically turns this POST into a body-less GET before the worker ever sees the 3xx, and a 2xx
/// on that GET would read as delivery of a record that was never sent (B09 hand-off).
/// </para>
/// </remarks>
internal sealed class DrainWorker(
    IOptions<EncryptedSpoolOptions> options,
    IHttpClientFactory httpClientFactory,
    DeadLetterBox deadLetterBox,
    EndpointReachability reachability,
    TimeProvider timeProvider,
    ILogger<DrainWorker> logger) : BackgroundService
{
    /// <summary>The name of the <see cref="HttpClient"/> the worker delivers with.</summary>
    internal const string HttpClientName = "Lukdrasil.StepUpLogging.Audit";

    private readonly EncryptedSpoolOptions _options = options.Value;
    private readonly SpoolReader _reader = new(options.Value.SpoolDirectory);
    private readonly Uri _auditEndpoint = new($"{options.Value.EndpointBaseUrl.TrimEnd('/')}/audit");

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Straight into a cycle: whatever the last run left spooled has been waiting since then.
        while (!stoppingToken.IsCancellationRequested)
        {
            await DrainWithoutEndingTheWorkerAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(RetryDelayFor(reachability.ConsecutiveFailures, _options), timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Drains once more as the host stops, bounded by
    /// <see cref="EncryptedSpoolOptions.ShutdownDrainTimeout"/>. Whatever is left when it runs out
    /// stays spooled for the next start — the records are on disk, so a bounded shutdown costs
    /// nothing but the wait.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        using var shutdownTimeout = new CancellationTokenSource(_options.ShutdownDrainTimeout, timeProvider);
        using var drainWindow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownTimeout.Token);
        await DrainWithoutEndingTheWorkerAsync(drainWindow.Token).ConfigureAwait(false);
    }

    private async Task DrainWithoutEndingTheWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DrainAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is stopping, or the shutdown drain ran out of time. Both leave the records
            // where they are, which is what the next start reads.
        }
        catch (Exception ex)
        {
            // Anything the delivery contract does not describe — a spool file that cannot be moved,
            // a fault inside the client's own pipeline. Out of ExecuteAsync it would end the worker
            // and stop the host with it, taking the application down over an audit delivery fault.
            // Counted on the same instrument as a per-record delivery failure, so a cycle stuck
            // failing this way is visible to alerting rather than only to someone reading logs.
            EncryptedSpoolMetrics.DrainFailureCounter.Add(1);
            logger.LogError(
                ex,
                "The audit drain cycle over {SpoolDirectory} failed. The records it did not deliver stay spooled, and the next cycle picks them up.",
                _options.SpoolDirectory);
        }
    }

    /// <summary>Sends what the spool holds, oldest first.</summary>
    internal async Task DrainAsync(CancellationToken cancellationToken)
    {
        // Not disposed, and resolved per cycle rather than held: the factory owns the handler and
        // recycles it, which is what keeps a process-long worker from pinning a stale DNS answer.
        var client = httpClientFactory.CreateClient(HttpClientName);

        foreach (var entry in _reader.ReadOldestFirst())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.Envelope is not { } envelope)
            {
                if (entry.IsCorrupt)
                {
                    DeadLetter(entry.FilePath, "the spool file could not be parsed as an audit envelope");
                    continue;
                }

                // A sharing violation or permission fault, not a bad record: leave it spooled and
                // stop here exactly as a transient delivery failure would, so a lock that clears
                // gets retried instead of losing the record to dead-letter.
                ReportUndelivered(entry.FilePath, "the spool file could not be read");
                return;
            }

            var attempt = await DeliverAsync(client, envelope, cancellationToken).ConfigureAwait(false);
            switch (attempt.Outcome)
            {
                case DeliveryOutcome.Stored:
                    reachability.EndpointAnswered();

                    // Nothing tells the sink's SpoolUsageTracker that a record left: it is not
                    // thread-safe and is only ever touched under the sink's write gate. Its tally
                    // is then only ever too high, which is the direction it already covers — it
                    // re-measures the disk before it reports the spool full.
                    File.Delete(entry.FilePath);
                    EncryptedSpoolMetrics.DrainedCounter.Add(1);
                    break;

                case DeliveryOutcome.Rejected:
                    reachability.EndpointAnswered();
                    DeadLetter(entry.FilePath, attempt.Description);
                    break;

                case DeliveryOutcome.Undelivered:
                    ReportUndelivered(entry.FilePath, attempt.Description);

                    // The spool is drained oldest first, so the records behind this one wait with
                    // it: sending them now would reorder the audit trail and keep asking a
                    // receiver that has just said it cannot take records (ADR 0020 D7).
                    return;
            }
        }
    }

    /// <summary>How long to wait before the next attempt after <paramref name="consecutiveFailures"/> failed ones.</summary>
    internal static TimeSpan RetryDelayFor(int consecutiveFailures, EncryptedSpoolOptions options)
    {
        var delay = options.DrainInterval < options.MaxDrainBackoff ? options.DrainInterval : options.MaxDrainBackoff;

        // Doubled one step at a time instead of DrainInterval * 2^consecutiveFailures: that
        // multiplication overflows TimeSpan's range for a legal DrainInterval well before
        // consecutiveFailures reaches a number an outage would plausibly produce. Stepping instead
        // means every intermediate value stays inside range, and the loop itself exits as soon as
        // the cap is reached, however large consecutiveFailures is.
        for (var step = 0; step < consecutiveFailures && delay < options.MaxDrainBackoff; step++)
        {
            delay = delay > options.MaxDrainBackoff / 2 ? options.MaxDrainBackoff : delay * 2;
        }

        return delay;
    }

    /// <summary>
    /// Posts one record and reads the endpoint's answer as the delivery contract defines it: 2xx is
    /// durably stored, a status naming a defect in the record (see <see cref="IsPermanentRejection"/>)
    /// is a rejection no retry can change, and everything else — a redirect, an auth or routing
    /// failure, a server error, a refused connection, a timeout — leaves the record for another
    /// attempt (ADR 0020 D7).
    /// </summary>
    private async Task<DeliveryAttempt> DeliverAsync(HttpClient client, SpoolEnvelope envelope, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.PostAsJsonAsync(_auditEndpoint, envelope, cancellationToken).ConfigureAwait(false);
            var answer = $"the audit endpoint answered {(int)response.StatusCode} {response.StatusCode}";

            return response switch
            {
                { IsSuccessStatusCode: true } => new(DeliveryOutcome.Stored, answer),
                _ when IsPermanentRejection(response.StatusCode) => new(DeliveryOutcome.Rejected, $"{answer}, which says the request itself is defective, so no retry can change it"),
                _ => new(DeliveryOutcome.Undelivered, answer)
            };
        }
        catch (Exception ex) when (ex is (HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            // A connection that was refused or dropped, or a request that ran past the client's
            // timeout. None of it says anything about the record, so it stays for another attempt;
            // a cancellation of our own token is the host stopping and belongs to the caller.
            return new(DeliveryOutcome.Undelivered, $"the request to the audit endpoint failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Whether <paramref name="status"/> indicts the record itself — malformed, conflicting,
    /// oversized, wrongly typed, or unprocessable — rather than the request merely finding the
    /// endpoint unable to take it right now. Everything else, including 401/403/407 and 404/405,
    /// is transient: an expired credential or a receiver mid-rollout looks permanent in the moment,
    /// but dead-lettering on that basis would walk the whole spool into dead-letter at machine
    /// speed for the length of a config outage that fixes itself (ADR 0020 D7).
    /// </summary>
    private static bool IsPermanentRejection(HttpStatusCode status) => status is
        HttpStatusCode.BadRequest or
        HttpStatusCode.Conflict or
        HttpStatusCode.RequestEntityTooLarge or
        HttpStatusCode.UnsupportedMediaType or
        HttpStatusCode.UnprocessableEntity;

    /// <summary>
    /// Sets a record aside that no retry can deliver, and says so at Critical: it never reached the
    /// audit store, and only an operator can decide what to do about it (ADR 0020 D7).
    /// </summary>
    private void DeadLetter(string spoolFilePath, string reason)
    {
        var deadLetterPath = deadLetterBox.Deposit(spoolFilePath);
        EncryptedSpoolMetrics.DeadLetteredCounter.Add(1);

        logger.LogCritical(
            "Audit record {SpoolFile} was dead-lettered to {DeadLetterFile}: {Reason}. It never reached the audit store, nothing will retry it, and nothing removes it — investigate it and clear the directory by hand.",
            Path.GetFileName(spoolFilePath), deadLetterPath, reason);
    }

    private void ReportUndelivered(string spoolFilePath, string reason)
    {
        reachability.AttemptFailed();
        EncryptedSpoolMetrics.DrainFailureCounter.Add(1);

        logger.LogWarning(
            "Audit record {SpoolFile} is still waiting to be delivered: {Reason}. It stays in the spool, as does everything behind it, and the next attempt follows in {RetryDelay} — {ConsecutiveFailures} attempts in a row have failed now.",
            Path.GetFileName(spoolFilePath), reason, RetryDelayFor(reachability.ConsecutiveFailures, _options), reachability.ConsecutiveFailures);
    }

    /// <summary>What the audit endpoint made of one record, and the words for it.</summary>
    private readonly record struct DeliveryAttempt(DeliveryOutcome Outcome, string Description);

    private enum DeliveryOutcome
    {
        /// <summary>The endpoint confirmed the record is durably stored.</summary>
        Stored,

        /// <summary>The endpoint refused the record on its merits, so no retry can deliver it.</summary>
        Rejected,

        /// <summary>The record did not get through, for a reason that may not hold next time.</summary>
        Undelivered
    }
}
