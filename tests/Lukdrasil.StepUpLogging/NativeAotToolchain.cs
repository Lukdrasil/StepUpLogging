using System.Runtime.InteropServices;

namespace Lukdrasil.StepUpLogging.Tests;

/// <summary>
/// Whether this machine has the native toolchain a Native AOT publish needs, so a test that
/// publishes one can skip, naming what is missing, instead of failing in the linker.
/// </summary>
internal static class NativeAotToolchain
{
    private static readonly Dictionary<OSPlatform, (string Tool, string Reason)> Requirements = new()
    {
        [OSPlatform.Linux] = ("clang", "Native AOT publish on Linux needs clang on PATH."),
        [OSPlatform.OSX] = ("clang", "Native AOT publish on macOS needs clang (the Xcode command line tools) on PATH."),
        [OSPlatform.Windows] = (
            Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"),
            "Native AOT publish on Windows needs Visual Studio with the C++ workload; vswhere.exe was not found."),
    };

    /// <summary>
    /// Why a Native AOT publish cannot run on <paramref name="os"/>, or <see langword="null"/> when
    /// <paramref name="toolExists"/> finds its tool: a command name on PATH, or a rooted file path.
    /// </summary>
    internal static string? MissingReason(OSPlatform os, Func<string, bool> toolExists) =>
        Requirements.TryGetValue(os, out var requirement)
            ? (toolExists(requirement.Tool) ? null : requirement.Reason)
            : $"No Native AOT toolchain is known for {os}.";

    /// <summary><see cref="MissingReason"/> for the machine the tests run on.</summary>
    internal static string? Current() =>
        MissingReason(Requirements.Keys.FirstOrDefault(RuntimeInformation.IsOSPlatform, OSPlatform.Create("UNKNOWN")), ToolExists);

    private static bool ToolExists(string tool) =>
        Path.IsPathRooted(tool)
            ? File.Exists(tool)
            : (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(directory => File.Exists(Path.Combine(directory, tool)));
}
