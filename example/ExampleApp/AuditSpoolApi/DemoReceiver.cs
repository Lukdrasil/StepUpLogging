using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuditSpoolApi;

/// <summary>
/// Stands in for the audit endpoint: opens each delivered envelope with the key its id names and
/// answers <c>422</c> with the problem code <c>sealing.unknown_key</c> for a key it does not know.
/// </summary>
public sealed class DemoReceiver
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();
    private readonly ConcurrentQueue<string> _stored = new();

    public IEnumerable<string> KnownKeyIds => _keys.Keys;

    public IEnumerable<string> Stored => _stored;

    public void Publish(SealingKey key) => _keys[key.Id] = key.Bytes;

    public void Forget(string keyId) => _keys.TryRemove(keyId, out _);

    public IResult Receive(JsonElement envelope)
    {
        var blob = envelope.GetProperty("payload").GetBytesFromBase64();
        var keyId = Encoding.ASCII.GetString(blob, 1, blob[0]);
        if (!_keys.TryGetValue(keyId, out var key))
        {
            return Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Unknown sealing key",
                extensions: new Dictionary<string, object?> { ["code"] = "sealing.unknown_key" });
        }

        var nonce = blob.AsSpan(1 + blob[0], AesGcm.NonceByteSizes.MaxSize);
        var tag = blob.AsSpan(1 + blob[0] + nonce.Length, AesGcm.TagByteSizes.MaxSize);
        var ciphertext = blob.AsSpan(1 + blob[0] + nonce.Length + tag.Length);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        _stored.Enqueue(Encoding.UTF8.GetString(plaintext));
        return Results.NoContent();
    }
}
