using RulesCorpus.Adapters;

namespace RulesCorpus;

/// <summary>
/// The outcome of one check, or of a whole verification. Ordered by severity, so a report's
/// outcome is its worst check's (decision 0004).
/// </summary>
public enum VerificationOutcome
{
    /// <summary>The evidence was examined and matched.</summary>
    Ok = 1,

    /// <summary>
    /// The evidence needed was not available: an artifact declared by identity alone, an external
    /// derivation under rebuild, or a check that could not run because an earlier one failed.
    /// Never ok: a verifier that reports what it did not examine as passing is one nobody can rely on.
    /// </summary>
    NotVerified = 2,

    /// <summary>The evidence was examined and did not match, or evidence that must be present was missing.</summary>
    Failed = 3,
}

/// <summary>One named check and what it found.</summary>
public sealed class VerificationCheck
{
    internal VerificationCheck(string name, VerificationOutcome outcome, string detail)
    {
        Name = name;
        Outcome = outcome;
        Detail = detail;
    }

    /// <summary>
    /// What was checked: <c>schema</c>, <c>references</c>, <c>content-digest</c>, …, or a
    /// per-item check such as <c>artifact rulebook-text</c>, <c>segments rulebook-canonical</c>
    /// or <c>rebuild rulebook-segmented</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>What the check found.</summary>
    public VerificationOutcome Outcome { get; }

    /// <summary>What was examined, or every problem found, or why it could not be examined.</summary>
    public string Detail { get; }

    /// <summary><c>outcome name: detail</c>.</summary>
    public override string ToString() => $"{Outcome} {Name}: {Detail}";
}

/// <summary>
/// Every check a verification ran, and the overall outcome. The overall outcome is the worst
/// check's, and a report with no checks is not verified, so <see cref="VerificationOutcome.Ok"/>
/// is reachable only when every check examined its evidence and it matched.
/// </summary>
public sealed class VerificationReport
{
    internal VerificationReport(IReadOnlyList<VerificationCheck> checks)
    {
        Checks = checks.ToArray();
        Outcome = Checks.Count == 0 ? VerificationOutcome.NotVerified : Checks.Max(c => c.Outcome);
    }

    /// <summary>Every check, in the order it ran.</summary>
    public IReadOnlyList<VerificationCheck> Checks { get; }

    /// <summary>The worst outcome of any check.</summary>
    public VerificationOutcome Outcome { get; }
}

/// <summary>What a verification may use beyond the corpus itself.</summary>
public sealed class VerificationOptions
{
    /// <summary>
    /// Re-run every reproducible derivation and require byte-identical output and identical
    /// segments. External derivations are then reported not verified; a reproducible derivation
    /// whose adapter is not supplied fails, because the corpus claims it can be re-run.
    /// </summary>
    public bool Rebuild { get; init; }

    /// <summary>The adapters a rebuild may run, looked up by id.</summary>
    public IReadOnlyList<ICorpusAdapter> Adapters { get; init; } = [];

    /// <summary>Resource bounds applied to everything read.</summary>
    public CorpusLimits Limits { get; init; } = CorpusLimits.Default;
}
