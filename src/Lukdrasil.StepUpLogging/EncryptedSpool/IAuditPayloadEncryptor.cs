namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The encryption seam between the encrypted spooling audit sink and the consumer's own
/// cryptography. The implementation is entirely the consumer's: it owns every cryptographic
/// concern — algorithm, key material, and key custody (acquisition, caching, rotation) — and is
/// registered by the consumer in <c>Program.cs</c>. This package never fetches, caches, or holds
/// a key anywhere; it only ever sees the opaque bytes this interface hands back.
/// </summary>
/// <remarks>
/// Implementations MUST be thread-safe: the sink registers as a singleton (ADR 0017 D5), so
/// <see cref="EncryptAsync"/> is called concurrently from multiple in-flight audit writes.
/// Shared mutable state — a cached key, a counter-derived nonce — must be synchronized;
/// unsynchronized nonce reuse in an AEAD scheme is a confidentiality break across the whole
/// archive, not just the affected records.
/// </remarks>
public interface IAuditPayloadEncryptor
{
    /// <summary>
    /// Encrypts <paramref name="payload"/> — the serialized audit record — into an opaque blob.
    /// The sink spools and delivers the result as-is; it never inspects or interprets either
    /// side of this call. An exception thrown here propagates unchanged to the sink's caller, per
    /// ADR 0016 D2: a record that cannot be encrypted must fail loudly, never be dropped or
    /// silently swallowed.
    /// </summary>
    /// <remarks>
    /// There is deliberately no <see cref="System.Threading.CancellationToken"/> parameter, and
    /// none should be added: the natural token for a caller to pass would be
    /// <c>HttpContext.RequestAborted</c>, which would let a disconnecting client cancel — and so
    /// erase — its own audit trail (ADR 0016 D6). An implementation that fetches a key remotely
    /// must bound its own latency internally (a self-timeout); nothing external will cancel it.
    /// <paramref name="payload"/> is valid only for the duration of this call — an implementation
    /// must not retain a reference to it after the returned <see cref="ValueTask{TResult}"/>
    /// completes; copy what it needs before returning.
    /// </remarks>
    /// <param name="payload">The serialized audit record to encrypt.</param>
    /// <returns>The opaque encrypted blob.</returns>
    ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload);
}
