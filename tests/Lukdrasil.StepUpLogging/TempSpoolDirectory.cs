namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// A throwaway directory under the OS temp path, deleted when the test finishes. The spool tests
/// run against real files — the durability behaviour they cover only exists on a real file system.
/// </summary>
internal sealed class TempSpoolDirectory : IDisposable
{
    public TempSpoolDirectory()
    {
        FullPath = Path.Combine(Path.GetTempPath(), "stepup-spool-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(FullPath);
    }

    public string FullPath { get; }

    public void Dispose() => Directory.Delete(FullPath, recursive: true);
}
