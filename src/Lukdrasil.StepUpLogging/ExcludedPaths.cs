using Microsoft.AspNetCore.Http;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// The request paths excluded from request logging and the request summary (<see cref="StepUpLoggingOptions.ExcludePaths"/>).
/// An entry ending in <c>*</c> is a prefix matched on a segment boundary, so <c>/api/*</c> excludes <c>/api</c> and
/// <c>/api/x</c> but not <c>/apifoo</c>; any other entry matches the whole path. Both ignore case.
/// </summary>
internal sealed class ExcludedPaths
{
    private readonly HashSet<string> _exact;
    private readonly string[] _prefixes;

    internal ExcludedPaths(IEnumerable<string> paths)
    {
        var entries = paths.ToArray();
        _exact = new HashSet<string>(entries, StringComparer.OrdinalIgnoreCase);
        _prefixes = [.. entries.Where(p => p.EndsWith('*')).Select(p => p.TrimEnd('*').TrimEnd('/'))];
    }

    /// <summary>The request path without its trailing slashes, except for the root path <c>/</c>.</summary>
    internal static string NormalizedPath(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    internal bool Contains(string path)
        => _exact.Contains(path) || _prefixes.Any(prefix =>
            path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
}
