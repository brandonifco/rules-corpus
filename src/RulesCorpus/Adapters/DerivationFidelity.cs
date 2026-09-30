namespace RulesCorpus.Adapters;

/// <summary>
/// What a derivation claims about information loss. A recorded claim for audit, not a
/// semantic judgement.
/// </summary>
public enum DerivationFidelity
{
    /// <summary>Nothing was discarded; written <c>lossless</c>.</summary>
    Lossless = 1,

    /// <summary>Something was discarded, and the losses say what; written <c>lossy-traceable</c>.</summary>
    LossyTraceable = 2,

    /// <summary>The input cannot be recovered from the output; written <c>non-reversible</c>.</summary>
    NonReversible = 3,
}
