namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The source type of a request derived from its User-Agent (ADR 0032 D2): the first rule whose token the header
/// contains, case-insensitive, wins. The header is client-supplied and spoofable, so this is a filtering label, not
/// an identity.
/// </summary>
internal static class ClientKind
{
    internal const string HealthProbe = "HealthProbe";
    internal const string Bot = "Bot";
    internal const string Tool = "Tool";
    internal const string Library = "Library";
    internal const string Browser = "Browser";
    internal const string Unknown = "Unknown";
    internal const string None = "None";

    // Browser is checked last: bots send "Mozilla/" too.
    private static readonly (string Kind, string[] Tokens)[] Rules =
    [
        (HealthProbe, ["kube-probe/", "ELB-HealthChecker", "GoogleHC", "AlwaysOn"]),
        (Bot, ["bot", "crawler", "spider", "Slurp"]),
        (Tool, ["curl/", "Wget/", "PostmanRuntime/", "insomnia/"]),
        (Library, ["python-requests/", "Go-http-client/", "okhttp/", "Java/", "axios/", "node-fetch", "Apache-HttpClient/"]),
        (Browser, ["Mozilla/"]),
    ];

    /// <summary>The kind of <paramref name="userAgent"/>; <see cref="None"/> when it is missing or blank.</summary>
    internal static string Classify(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return None;
        }

        return Rules.FirstOrDefault(rule => rule.Tokens.Any(token => userAgent.Contains(token, StringComparison.OrdinalIgnoreCase))).Kind ?? Unknown;
    }
}
