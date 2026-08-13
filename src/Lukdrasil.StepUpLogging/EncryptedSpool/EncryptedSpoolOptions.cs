using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Configures the encrypted spooling audit sink. Nothing here concerns encryption or key material:
/// both live entirely inside the consumer's <see cref="IAuditPayloadEncryptor"/> (ADR 0017 D3).
/// </summary>
public sealed class EncryptedSpoolOptions
{
    /// <summary>
    /// The directory the write-ahead spool keeps its records in, one file per record, each deleted
    /// only once the audit endpoint has confirmed it stored it. There is no default, and it must be
    /// storage that outlives the process — a mounted volume, never a path inside the deployment
    /// artifact: a container's own filesystem is replaced on the next restart or rollout, and it
    /// would take with it every record <c>WriteAsync</c> already reported durable to the business
    /// call site.
    /// </summary>
    public string SpoolDirectory { get; set; } = string.Empty;

    /// <summary>
    /// The absolute base URL of the audit endpoint spooled records are delivered to, without a
    /// default. The drain worker posts one record per request to <c>{EndpointBaseUrl}/audit</c>.
    /// Whatever credentials the endpoint requires are configured on the named
    /// <see cref="HttpClient"/> the worker resolves, not here: the worker sends what it is given and
    /// never handles a credential itself.
    /// </summary>
    public string EndpointBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// The name of the module producing these audit records. It travels inside the encrypted
    /// payload because the receiving audit store carries it as a column of its own, and these
    /// options are its only source — it is not an <see cref="AuditEvent"/> field. There is no
    /// default; it has to be configured.
    /// </summary>
    public string ModuleName { get; set; } = string.Empty;

    /// <summary>
    /// The version of the module producing these audit records, alongside <see cref="ModuleName"/>
    /// inside the encrypted payload and, like it, sourced only from here and without a default.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// The largest serialized audit payload the sink will encrypt and spool. A record over it is
    /// rejected at the call site that wrote it, rather than surfacing later as an encryption or a
    /// disk error. In bytes, defaults to 64 KB. Must be greater than zero.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// The size the spooled records may reach before new ones are dropped. In bytes, defaults to
    /// 256 MB. Must be greater than zero. Reaching either this or <see cref="SpoolMaxEntries"/> is
    /// enough. Measured before each write, so the record admitted last can carry the spool its own
    /// size past this.
    /// </summary>
    public long SpoolMaxBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// The number of spooled records that may accumulate before new ones are dropped. Defaults to
    /// 100 000. Must be greater than zero. Reaching either this or <see cref="SpoolMaxBytes"/> is
    /// enough. The count is measured before the write that would cross it, so unlike
    /// <see cref="SpoolMaxBytes"/> this one is only ever passed by files a failed write left
    /// behind.
    /// </summary>
    public int SpoolMaxEntries { get; set; } = 100_000;

    /// <summary>
    /// The status the spool's health check reports from half the cap on, while writes still
    /// succeed. Unhealthy by default: a spool that stopped draining is on its way to losing audit
    /// records, and taking the instance out of rotation is cheaper than that (ADR 0020 D5).
    /// </summary>
    public HealthStatus SpoolWarnStatus { get; set; } = HealthStatus.Unhealthy;

    /// <summary>
    /// The status the spool's health check reports once the spool is full and audit records are
    /// being dropped. Separate from <see cref="SpoolWarnStatus"/> and Unhealthy by default: an
    /// instance that is already losing audit records must not read as whatever milder status was
    /// chosen for "the spool is filling up".
    /// </summary>
    public HealthStatus SpoolFullStatus { get; set; } = HealthStatus.Unhealthy;

    /// <summary>
    /// How often the drain worker looks for spooled records to deliver, and the wait it starts
    /// backing off from when delivery fails. Defaults to 5 seconds: records sit on disk until they
    /// are delivered, and a host that is compromised or destroyed takes whatever is still there
    /// with it (ADR 0020 D8).
    /// </summary>
    public TimeSpan DrainInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The longest the drain worker waits between attempts while the audit endpoint keeps failing.
    /// The wait doubles from <see cref="DrainInterval"/> after each failed attempt and stops
    /// growing here, so an endpoint that is down for hours is retried at a fixed, modest rate
    /// instead of being hammered. Defaults to 5 minutes.
    /// </summary>
    public TimeSpan MaxDrainBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a stopping host waits for the drain worker to deliver what is still spooled.
    /// Defaults to 5 seconds, and has to stay well inside the host's own shutdown timeout. Nothing
    /// is lost when it runs out: the records stay on disk and the next start delivers them.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many consecutive drain cycles a spool file may fail to even be opened (a sharing
    /// violation, a broken ACL, a bad sector) before it is dead-lettered as unreadable. Without a
    /// limit a fault that never clears would block the queue behind it forever, growing the spool
    /// to its cap; the record is still never lost — dead-lettering only ever moves it, never
    /// deletes it. Defaults to 10.
    /// </summary>
    public int UnreadableRetryLimit { get; set; } = 10;

    /// <summary>
    /// The status the health check reports once three of the drain worker's delivery attempts have
    /// failed in a row, until one gets through. Degraded by default, and deliberately milder than
    /// the spool's own statuses: no audit record has been lost yet — they are on disk, and delivery
    /// resumes on its own once the endpoint is back (ADR 0019 D4).
    /// </summary>
    public HealthStatus UnreachableStatus { get; set; } = HealthStatus.Degraded;
}
