using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Lukdrasil.StepUpLogging;

/// <summary>
/// Wraps a live <see cref="IConfiguration"/> and hides every key its <paramref name="isVisible"/>
/// predicate rejects, while delegating <see cref="GetReloadToken"/> to the inner instance so
/// <c>reloadOnChange</c> (e.g. runtime <c>Serilog:MinimumLevel:Override</c> retuning) keeps working —
/// which a materialized snapshot would silently discard (ADR 0012).
/// </summary>
/// <remarks>
/// <paramref name="isVisible"/> is evaluated against the full (absolute) configuration path of each
/// leaf or section. An ancestor section resolves through <see cref="GetSection"/> unconditionally
/// (matching <see cref="IConfiguration"/> semantics) but only surfaces in <see cref="GetChildren"/>
/// and reports a non-null <c>Value</c> when its path is visible; a rejected leaf reads back as null.
/// </remarks>
internal sealed class PrefixFilteredConfiguration(IConfiguration inner, Func<string, bool> isVisible)
    : IConfiguration
{
    public string? this[string key]
    {
        get => isVisible(key) ? inner[key] : null;
        set => inner[key] = value;
    }

    public IConfigurationSection GetSection(string key)
        => new PrefixFilteredConfigurationSection(inner.GetSection(key), isVisible);

    public IEnumerable<IConfigurationSection> GetChildren()
        => inner.GetChildren()
            .Where(child => isVisible(child.Path))
            .Select(IConfigurationSection (child) => new PrefixFilteredConfigurationSection(child, isVisible));

    public IChangeToken GetReloadToken() => inner.GetReloadToken();
}

/// <summary>
/// The <see cref="IConfigurationSection"/> shape of <see cref="PrefixFilteredConfiguration"/>, letting
/// Serilog's configuration reader walk <c>GetSection("Serilog").GetSection("WriteTo")</c> down to the
/// surviving leaves. Visibility is decided on the inner section's absolute <see cref="Path"/>.
/// </summary>
internal sealed class PrefixFilteredConfigurationSection(IConfigurationSection inner, Func<string, bool> isVisible)
    : IConfigurationSection
{
    public string Key => inner.Key;

    public string Path => inner.Path;

    public string? Value
    {
        get => isVisible(inner.Path) ? inner.Value : null;
        set => inner.Value = value;
    }

    public string? this[string key]
    {
        get => isVisible(ConfigurationPath.Combine(inner.Path, key)) ? inner[key] : null;
        set => inner[key] = value;
    }

    public IConfigurationSection GetSection(string key)
        => new PrefixFilteredConfigurationSection(inner.GetSection(key), isVisible);

    public IEnumerable<IConfigurationSection> GetChildren()
        => inner.GetChildren()
            .Where(child => isVisible(child.Path))
            .Select(IConfigurationSection (child) => new PrefixFilteredConfigurationSection(child, isVisible));

    public IChangeToken GetReloadToken() => inner.GetReloadToken();
}
