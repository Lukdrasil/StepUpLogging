using System.Security.Cryptography;
using System.Text;

using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

namespace AuditSpoolApi;

/// <summary>
/// Stands in for the key service of a real deployment: holds the current sealing key, seals records
/// with AES-GCM and publishes each new key to the <see cref="DemoReceiver"/>. The blob is
/// [key id length][key id][nonce][tag][ciphertext].
/// </summary>
public sealed class DemoKeyRing(DemoReceiver receiver) : IAuditPayloadEncryptor
{
    private readonly Lock _gate = new();
    private SealingKey? _current;

    public string CurrentKeyId => Current().Id;

    public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload)
    {
        var key = Current();
        var id = Encoding.ASCII.GetBytes(key.Id);
        var blob = new byte[1 + id.Length + AesGcm.NonceByteSizes.MaxSize + AesGcm.TagByteSizes.MaxSize + payload.Length];
        blob[0] = (byte)id.Length;
        id.CopyTo(blob, 1);
        var nonce = blob.AsSpan(1 + id.Length, AesGcm.NonceByteSizes.MaxSize);
        var tag = blob.AsSpan(1 + id.Length + nonce.Length, AesGcm.TagByteSizes.MaxSize);
        var ciphertext = blob.AsSpan(1 + id.Length + nonce.Length + tag.Length);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key.Bytes, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, payload.Span, ciphertext, tag);
        return ValueTask.FromResult(blob);
    }

    /// <summary>Replaces the current key with a new one the receiver knows.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            _current = NewKey();
        }
    }

    private SealingKey Current()
    {
        lock (_gate)
        {
            return _current ??= NewKey();
        }
    }

    private SealingKey NewKey()
    {
        var key = new SealingKey($"k-{Guid.NewGuid():N}"[..10], RandomNumberGenerator.GetBytes(32));
        receiver.Publish(key);
        return key;
    }
}

public sealed record SealingKey(string Id, byte[] Bytes);
