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
    /// only once the audit endpoint has confirmed it stored it.
    /// </summary>
    public string SpoolDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "audit-spool");

    /// <summary>
    /// The name of the module producing these audit records. It travels inside the encrypted
    /// payload because the receiving audit store carries it as a column of its own, and these
    /// options are its only source — it is not an <see cref="AuditEvent"/> field.
    /// </summary>
    public string ModuleName { get; set; } = string.Empty;

    /// <summary>
    /// The version of the module producing these audit records, alongside <see cref="ModuleName"/>
    /// inside the encrypted payload and, like it, sourced only from here.
    /// </summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// The largest serialized audit payload the sink will encrypt and spool. A record over it is
    /// rejected at the call site that wrote it, rather than surfacing later as an encryption or a
    /// disk error. Must be greater than zero.
    /// </summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// The size the spooled records may reach before new ones are dropped. Must be greater than
    /// zero. Reaching either this or <see cref="SpoolMaxEntries"/> is enough. Measured before each
    /// write, so the record admitted last can carry the spool its own size past this.
    /// </summary>
    public long SpoolMaxBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>
    /// The number of spooled records that may accumulate before new ones are dropped. Must be
    /// greater than zero. Reaching either this or <see cref="SpoolMaxBytes"/> is enough.
    /// </summary>
    public int SpoolMaxEntries { get; set; } = 100_000;

    /// <summary>
    /// The status the spool's health check reports from half the cap on, while writes still
    /// succeed. Unhealthy by default: a spool that stopped draining is on its way to losing audit
    /// records, and taking the instance out of rotation is cheaper than that (ADR 0020 D5).
    /// </summary>
    public HealthStatus SpoolWarnStatus { get; set; } = HealthStatus.Unhealthy;
}
