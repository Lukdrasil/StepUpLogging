using System.Runtime.InteropServices;

namespace Lukdrasil.StepUpLogging.Tests;

public class NativeAotToolchainTests
{
    [Fact]
    public void MissingReason_LinuxWithoutClang_NamesClang()
    {
        var reason = NativeAotToolchain.MissingReason(OSPlatform.Linux, _ => false);

        Assert.NotNull(reason);
        Assert.Contains("clang", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingReason_LinuxWithClang_IsNull()
    {
        var reason = NativeAotToolchain.MissingReason(OSPlatform.Linux, tool => tool == "clang");

        Assert.Null(reason);
    }
}
