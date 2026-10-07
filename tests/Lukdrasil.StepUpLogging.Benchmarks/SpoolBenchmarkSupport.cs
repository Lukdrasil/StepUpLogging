using Lukdrasil.StepUpLogging.Audit.EncryptedSpool;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lukdrasil.StepUpLogging.Benchmarks;

/// <summary>What the spool benchmarks share: a spool location, a stand-in encryptor, a stand-in receiver.</summary>
internal static class SpoolBenchmarkSupport
{
    /// <summary>
    /// Names the environment variable that moves the spool benchmarks' directories. The default is
    /// under the build output rather than the temp directory: temp is often a RAM disk, where an
    /// fsync costs nothing and a durable-write benchmark would measure nothing.
    /// </summary>
    internal const string SpoolRootVariable = "STEPUP_BENCH_SPOOL_ROOT";

    internal static string FreshSpoolDirectory(string name)
    {
        var root = Environment.GetEnvironmentVariable(SpoolRootVariable) is { Length: > 0 } configured
            ? configured
            : Path.Combine(AppContext.BaseDirectory, "spool-benchmarks");
        var directory = Path.Combine(root, name, "spool");

        DeleteWithSiblings(directory);
        return directory;
    }

    internal static void DeleteWithSiblings(string spoolDirectory)
    {
        var parent = Path.GetDirectoryName(spoolDirectory)!;
        if (Directory.Exists(parent))
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    internal static EncryptedSpoolOptions OptionsFor(string spoolDirectory) => new()
    {
        SpoolDirectory = spoolDirectory,
        ModuleName = "benchmarks",
        Version = "1.0.0",
        EndpointBaseUrl = "https://audit.example/api"
    };

    internal static SpoolUsageTracker TrackerFor(EncryptedSpoolOptions options) =>
        new(new SpoolCapacity(options), TimeProvider.System, options.SpoolFullRecheckInterval);

    /// <summary>A sink over <paramref name="options"/>'s spool, built the way the host builds it, minus the DI container.</summary>
    internal static EncryptedSpoolAuditSink SinkFor(EncryptedSpoolOptions options) =>
        new(
            Options.Create(options),
            new SpoolWriter(options.SpoolDirectory),
            TrackerFor(options),
            new PassThroughEncryptor(),
            TimeProvider.System,
            NullLogger<EncryptedSpoolAuditSink>.Instance);

    internal static void RequireDropped(AuditWriteResult result)
    {
        if (result != AuditWriteResult.Dropped)
        {
            throw new InvalidOperationException($"the spool is not at its cap: the write was {result}");
        }
    }

    /// <summary>Puts <paramref name="count"/> small records straight into <paramref name="spoolDirectory"/>, without the fsync a write costs.</summary>
    internal static async Task FillWithRecordsAsync(string spoolDirectory, int count)
    {
        Directory.CreateDirectory(spoolDirectory);
        for (var i = 0; i < count; i++)
        {
            await File.WriteAllBytesAsync(Path.Combine(spoolDirectory, $"{i:D8}.env"), "{}"u8.ToArray());
        }
    }

    internal static AuditEvent AuditedOperation() =>
        AuditEvent.Success("order.cancel", "user-42", "user") with
        {
            EventId = Guid.CreateVersion7(),
            TimestampUtc = DateTimeOffset.UtcNow
        };
}

/// <summary>Not cryptography: hands the payload back, so the benchmarks measure the spool and not an algorithm.</summary>
internal sealed class PassThroughEncryptor : IAuditPayloadEncryptor
{
    public ValueTask<byte[]> EncryptAsync(ReadOnlyMemory<byte> payload) => ValueTask.FromResult(payload.ToArray());
}
