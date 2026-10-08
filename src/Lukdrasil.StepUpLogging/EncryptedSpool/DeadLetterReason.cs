namespace Lukdrasil.StepUpLogging.Audit.EncryptedSpool;

/// <summary>
/// Why the drain worker set a record aside, the value of the <see cref="TagName"/> tag on
/// <c>audit_spool_dead_lettered_total</c>: a closed set, so the metric stays low in cardinality.
/// </summary>
internal readonly record struct DeadLetterReason(string Tag)
{
    /// <summary>The tag on the dead-letter counter that carries the reason.</summary>
    internal const string TagName = "reason";

    /// <summary>The endpoint refused the record on its merits.</summary>
    internal static readonly DeadLetterReason Rejected = new("rejected");

    /// <summary>The spool file does not parse as an audit envelope.</summary>
    internal static readonly DeadLetterReason Corrupt = new("corrupt");

    /// <summary>The spool file could not be read from disk within the retry limit.</summary>
    internal static readonly DeadLetterReason Unreadable = new("unreadable");
}
