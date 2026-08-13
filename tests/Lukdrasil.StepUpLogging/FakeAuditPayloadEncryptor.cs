using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// A reversible, in-memory <see cref="IAuditPayloadEncryptor"/> for tests. Not cryptography —
/// it exists so later blocks (spool, sink, drain worker) can encrypt and then decrypt a payload
/// without depending on real key material.
/// </summary>
internal sealed class FakeAuditPayloadEncryptor : IAuditPayloadEncryptor
{
    public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload) =>
        ValueTask.FromResult(InvertBits(payload.Span));

    public byte[] Decrypt(ReadOnlyMemory<byte> blob) => InvertBits(blob.Span);

    private static byte[] InvertBits(ReadOnlySpan<byte> bytes)
    {
        var inverted = new byte[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            inverted[i] = (byte)~bytes[i];
        }

        return inverted;
    }
}
