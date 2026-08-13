namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// The encryption seam between the encrypted spooling audit sink and the consumer's own
/// cryptography. The implementation is entirely the consumer's: it owns every cryptographic
/// concern — algorithm, key material, and key custody (acquisition, caching, rotation) — and is
/// registered by the consumer in <c>Program.cs</c>. This package never fetches, caches, or holds
/// a key anywhere; it only ever sees the opaque bytes this interface hands back.
/// </summary>
public interface IAuditPayloadEncryptor
{
    /// <summary>
    /// Encrypts <paramref name="payload"/> — the serialized audit record — into an opaque blob.
    /// The sink spools and delivers the result as-is; it never inspects or interprets either
    /// side of this call. An exception thrown here propagates unchanged to the sink's caller, per
    /// ADR 0016 D2: a record that cannot be encrypted must fail loudly, never be dropped or
    /// silently swallowed.
    /// </summary>
    /// <param name="payload">The serialized audit record to encrypt.</param>
    /// <returns>The opaque encrypted blob.</returns>
    ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload);
}
