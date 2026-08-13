using System.Text;

namespace Lukdrasil.StepUpLogging.Tests;

public class FakeAuditPayloadEncryptorTests
{
    [Fact]
    public async Task FakeAuditPayloadEncryptor_EncryptThenDecrypt_RoundTripsPayload()
    {
        var encryptor = new FakeAuditPayloadEncryptor();
        var payload = Encoding.UTF8.GetBytes("""{"action":"order.cancel"}""");

        var blob = await encryptor.EncryptAsync(payload);
        var decrypted = encryptor.Decrypt(blob);

        Assert.Equal(payload, decrypted);
        Assert.NotEqual(payload, blob);
    }
}
