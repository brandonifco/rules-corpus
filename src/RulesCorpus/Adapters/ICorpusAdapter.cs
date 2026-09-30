namespace RulesCorpus.Adapters;

/// <summary>
/// Turns one input artifact into canonical content and its segments. The core runs it,
/// measures what it produced, and records the result: an adapter never computes a digest the
/// manifest trusts, and everything it returns is validated before it is recorded
/// (docs/adapter-contract.md).
/// </summary>
public interface ICorpusAdapter
{
    /// <summary>
    /// The adapter's name in the corpus id grammar, recorded as the derivation's tool id and
    /// matched against the build definition's <c>adapter</c> member.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Recorded as the derivation's tool version. Emitting different output for the same input
    /// and parameters is a new version: a rebuild compares against what the recorded version
    /// produced, and it cannot tell a changed adapter from a changed source otherwise.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Whether identical input and parameters always produce identical output. The build only
    /// runs deterministic adapters; anything else is declared as an external derivation, where
    /// it is recorded as evidence rather than reproduced.
    /// </summary>
    bool IsDeterministic { get; }

    /// <summary>Derives canonical content, or throws <see cref="CorpusAdapterException"/>.</summary>
    /// <exception cref="CorpusAdapterException">
    /// The input or parameters are refused. There is no partial output.
    /// </exception>
    AdapterOutput Derive(AdapterInput input);
}
