namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

// af-stub
internal static class RejectionProblemCode
{
    internal const string ProblemJsonMediaType = "application/problem+json";

    internal const int MaxBodyBytes = 16 * 1024;

    internal static Task<string?> ReadAsync(HttpContent content, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    internal static string? Parse(ReadOnlySpan<byte> body) =>
        throw new NotImplementedException();
}
