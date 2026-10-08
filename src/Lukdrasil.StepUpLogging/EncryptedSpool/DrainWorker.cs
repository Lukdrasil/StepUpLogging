using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Delivers spooled audit records to the configured endpoint, oldest first, and deletes each only
/// once the endpoint has confirmed it stored it (ADR 0020 D7). A record it can never deliver goes
/// to <see cref="DeadLetterBox"/>; anything else that goes wrong leaves the record spooled for the
/// next attempt, which backs off while the endpoint keeps failing. With
/// <see cref="EncryptedSpoolOptions.DeliveryBatchSize"/> above 1 it delivers a run of records as
/// one request, and what the endpoint says about it counts for every record in it; a rejected
/// batch is redelivered record by record, so only the record the endpoint rejects on its own is
/// dead-lettered.
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
    SpoolHead spoolHead,
    SpoolUsageTracker usageTracker,
    TimeProvider timeProvider,
    ILogger<DrainWorker> logger) : BackgroundService
{
    /// <summary>The name of the <see cref="HttpClient"/> the worker delivers with.</summary>
    internal const string HttpClientName = "Lukdrasil.StepUpLogging.Audit";

    private readonly EncryptedSpoolOptions _options = options.Value;
    private readonly SpoolHead _spoolHead = spoolHead; // af-stub
    private readonly SpoolReader _reader = new(options.Value.SpoolDirectory);
    private readonly Uri _auditEndpoint = new($"{options.Value.EndpointBaseUrl.TrimEnd('/')}/audit");
    private readonly Uri _batchEndpoint = new($"{options.Value.EndpointBaseUrl.TrimEnd('/')}/audit/batch");

    /// <summary>
    /// Consecutive <see cref="SpoolReadFault.Unreadable"/> reads, by path, since the worker last
    /// managed to read that file (or gave up on it). Cycles run one at a time on this worker, so a
    /// plain dictionary is enough — nothing else touches it concurrently.
    /// </summary>
    private readonly Dictionary<string, int> _unreadableAttempts = [];

    /// <summary>
    /// Cuts the spool, oldest first, into deliveries: runs of up to <paramref name="batchSize"/>
    /// readable records, and each corrupt or unreadable file on its own. A faulted file closes the
    /// batch before it, so order holds and the fault is acted on in its place. Lazy: a full batch is
    /// handed over before the rest of the spool is read, since a delivery that stops the cycle
    /// should not have paid for reading a deep spool first.
    /// </summary>
    internal static IEnumerable<DrainStep> Plan(IEnumerable<SpoolEntry> entries, int batchSize)
    {
        List<SpoolEntry> open = [];
        foreach (var entry in entries)
        {
            foreach (var step in Place(open, entry, batchSize))
            {
                yield return step;
            }
        }

        if (open.Count > 0)
        {
            yield return Close(open);
        }
    }

    /// <summary>
    /// What an answer from the audit endpoint means for the records posted to <paramref name="expected"/>:
    /// 2xx is durably stored, a status naming a defect in the request (see <see cref="IsPermanentRejection"/>)
    /// is a rejection no retry can change, and everything else — a redirect, an auth or routing
    /// failure, a server error — leaves the records for another attempt (ADR 0020 D7).
    /// </summary>
    internal static DeliveryAttempt Classify(HttpResponseMessage response, Uri expected)
    {
        var answer = $"the audit endpoint answered {(int)response.StatusCode} {response.StatusCode}";

        return response switch
        {
            // Defence in depth for the AllowAutoRedirect = false obligation on the named
            // client (see the class remarks): if a redirect were followed anyway, a 2xx from
            // wherever it led must not read as "this record is stored" — nothing was ever
            // posted to that URI.
            _ when response.RequestMessage?.RequestUri is { } answeredFrom && answeredFrom != expected =>
                new(DeliveryOutcome.Undelivered, $"the response came from {answeredFrom} instead of {expected} — a redirect was followed when it should not have been"),
            { IsSuccessStatusCode: true } => new(DeliveryOutcome.Stored, answer),
            _ when IsPermanentRejection(response.StatusCode) => new(DeliveryOutcome.Rejected, $"{answer}, which says the request itself is defective, so no retry can change it"),
            _ => new(DeliveryOutcome.Undelivered, answer)
        };
    }

    /// <summary>
    /// The request body for several records: the stored bytes of each, unchanged, as one JSON array.
    /// The files already hold the envelope in the wire format, so nothing is parsed or re-serialized.
    /// </summary>
    internal static byte[] BatchBody(IReadOnlyList<SpoolEntry> batch)
    {
        using var body = new MemoryStream(batch.Sum(entry => entry.Contents.Length) + batch.Count + 1);
        body.WriteByte((byte)'[');
        for (var i = 0; i < batch.Count; i++)
        {
            if (i > 0)
            {
                body.WriteByte((byte)',');
            }

            body.Write(batch[i].Contents);
        }

        body.WriteByte((byte)']');
        return body.ToArray();
    }

    private static DrainStep[] Place(List<SpoolEntry> open, SpoolEntry entry, int batchSize)
    {
        if (entry.Envelope is not null)
        {
            open.Add(entry);
            return open.Count >= batchSize ? [Close(open)] : [];
        }

        DrainStep alone = new([], entry);
        return open.Count > 0 ? [Close(open), alone] : [alone];
    }

    private static DrainStep Close(List<SpoolEntry> open)
    {
        DrainStep step = new([.. open], null);
        open.Clear();
        return step;
    }

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
            catch (Exception ex)
            {
                // A non-positive DrainInterval/MaxDrainBackoff — nothing validates them yet, that
                // is B09's job — makes Task.Delay throw here instead of in a delivery path; caught
                // for the same reason DrainWithoutEndingTheWorkerAsync catches broadly: a config
                // mistake must not fault ExecuteAsync and stop the host over an audit delivery worker.
                EncryptedSpoolMetrics.DrainFailureCounter.Add(1);
                logger.LogError(ex, "The audit drain worker could not wait before its next cycle over {SpoolDirectory}.", _options.SpoolDirectory);
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

        foreach (var step in Plan(_reader.ReadOldestFirst(), _options.DeliveryBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var carryOn = step.Faulted is { } faulted
                ? SetAsideFaulted(faulted)
                : await DeliverAsync(client, step.Batch, cancellationToken).ConfigureAwait(false);

            if (!carryOn)
            {
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
    /// Delivers <paramref name="batch"/> and acts on the answer: stored records leave the spool, a
    /// rejection is dead-lettered (see <see cref="RejectedAsync"/>), and anything else leaves them
    /// for another attempt. Returns whether the cycle carries on with the records behind them.
    /// </summary>
    private async Task<bool> DeliverAsync(HttpClient client, IReadOnlyList<SpoolEntry> batch, CancellationToken cancellationToken)
    {
        ForgetUnreadableAttempts(batch);
        var attempt = await PostAsync(client, batch, cancellationToken).ConfigureAwait(false);

        switch (attempt.Outcome)
        {
            case DeliveryOutcome.Stored:
                reachability.EndpointAnswered();
                foreach (var entry in batch)
                {
                    Delivered(entry);
                }

                return true;

            case DeliveryOutcome.Rejected:
                reachability.EndpointAnswered();
                return await RejectedAsync(client, batch, attempt.Description, cancellationToken).ConfigureAwait(false);

            default:
                ReportUndelivered(batch[0].FilePath, attempt.Description);
                return false;
        }
    }

    /// <summary>
    /// A rejected single record is defective and dead-lettered. A rejected batch only says that one
    /// of its records is, not which, so each is delivered on its own and only the one the endpoint
    /// rejects alone is set aside (ADR 0020 D7).
    /// </summary>
    private async Task<bool> RejectedAsync(HttpClient client, IReadOnlyList<SpoolEntry> batch, string reason, CancellationToken cancellationToken)
    {
        if (batch.Count == 1)
        {
            DeadLetterAndRelease(batch[0], reason);
            return true;
        }

        foreach (var entry in batch)
        {
            if (!await DeliverAsync(client, [entry], cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Posts the stored bytes of <paramref name="batch"/> as they are — one record to
    /// <c>/audit</c>, several as a JSON array to <c>/audit/batch</c> — and reads the endpoint's
    /// answer. A refused connection or a request that ran past the client's timeout leaves the
    /// records for another attempt like any other transient failure (ADR 0020 D7).
    /// </summary>
    private async Task<DeliveryAttempt> PostAsync(HttpClient client, IReadOnlyList<SpoolEntry> batch, CancellationToken cancellationToken)
    {
        var (endpoint, body) = batch.Count == 1 ? (_auditEndpoint, batch[0].Contents) : (_batchEndpoint, BatchBody(batch));

        try
        {
            using var content = new ByteArrayContent(body);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json", "utf-8");
            using var response = await client.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
            return Classify(response, endpoint);
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
    /// Deals with a corrupt or unreadable file standing on its own in the plan. Returns whether the
    /// cycle carries on: a corrupt file is dead-lettered outright, an unreadable one only once it
    /// has failed enough cycles, and until then stops the cycle exactly as a transient delivery
    /// failure would, so a lock that clears gets retried instead of reordering the records behind it.
    /// </summary>
    private bool SetAsideFaulted(SpoolEntry entry)
    {
        if (!entry.IsCorrupt)
        {
            return DeadLetterIfUnreadableTooLong(entry.FilePath);
        }

        _unreadableAttempts.Remove(entry.FilePath);
        DeadLetterAndRelease(entry, "the spool file could not be parsed as an audit envelope");
        return true;
    }

    private void ForgetUnreadableAttempts(IReadOnlyList<SpoolEntry> batch)
    {
        foreach (var entry in batch)
        {
            _unreadableAttempts.Remove(entry.FilePath);
        }
    }

    /// <summary>Deletes a record the endpoint confirmed and tells the usage tally its room is free.</summary>
    private void Delivered(SpoolEntry entry)
    {
        // The token is taken before the delete: a measure that runs in between may or may not have
        // seen the file, and then this release is ignored rather than counted off twice.
        var token = usageTracker.ReleaseToken;
        File.Delete(entry.FilePath);
        usageTracker.Released(token, entry.Contents.Length);
        EncryptedSpoolMetrics.DrainedCounter.Add(1);
    }

    private void DeadLetterAndRelease(SpoolEntry entry, string reason)
    {
        var token = usageTracker.ReleaseToken;
        DeadLetter(entry.FilePath, reason);
        usageTracker.Released(token, entry.Contents.Length);
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

    /// <summary>
    /// Counts one more failed read of <paramref name="spoolFilePath"/> and dead-letters it once
    /// <see cref="EncryptedSpoolOptions.UnreadableRetryLimit"/> is reached, returning whether it
    /// did. The endpoint was never contacted for a file that cannot even be opened, so this counts
    /// on <see cref="EncryptedSpoolMetrics.DrainFailureCounter"/> like any other read fault but
    /// never touches <see cref="EndpointReachability"/> — that would blame the receiver for a local
    /// disk fault.
    /// </summary>
    private bool DeadLetterIfUnreadableTooLong(string spoolFilePath)
    {
        var attempts = _unreadableAttempts.GetValueOrDefault(spoolFilePath) + 1;
        if (attempts < _options.UnreadableRetryLimit)
        {
            _unreadableAttempts[spoolFilePath] = attempts;
            EncryptedSpoolMetrics.DrainFailureCounter.Add(1);
            logger.LogWarning(
                "Audit record {SpoolFile} could not be read from disk (attempt {Attempt} of {Limit}). It stays in the spool, as does everything behind it, until it can be read or the limit is reached.",
                Path.GetFileName(spoolFilePath), attempts, _options.UnreadableRetryLimit);
            return false;
        }

        _unreadableAttempts.Remove(spoolFilePath);
        DeadLetter(spoolFilePath, $"the spool file could not be read from disk after {_options.UnreadableRetryLimit} attempts");
        return true;
    }

    /// <summary>What the audit endpoint made of one record, and the words for it.</summary>
    internal readonly record struct DeliveryAttempt(DeliveryOutcome Outcome, string Description);

    /// <summary>One delivery the plan calls for: a batch of readable records, or a faulted file standing on its own.</summary>
    internal readonly record struct DrainStep(IReadOnlyList<SpoolEntry> Batch, SpoolEntry? Faulted);

    internal enum DeliveryOutcome
    {
        /// <summary>The endpoint confirmed the record is durably stored.</summary>
        Stored,

        /// <summary>The endpoint refused the record on its merits, so no retry can deliver it.</summary>
        Rejected,

        /// <summary>The record did not get through, for a reason that may not hold next time.</summary>
        Undelivered
    }
}
