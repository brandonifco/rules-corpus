namespace RulesCorpus.Adapters;

/// <summary>What an adapter is given: one input artifact, its parameters, and the limits.</summary>
public sealed class AdapterInput
{
    /// <summary>Creates an input. Parameters are copied into ordinal key order.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AdapterInput(
        string artifactId,
        ReadOnlyMemory<byte> bytes,
        IReadOnlyDictionary<string, string> parameters,
        CorpusLimits limits)
    {
        ArgumentNullException.ThrowIfNull(artifactId);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(limits);
        ArtifactId = artifactId;
        Bytes = bytes;
        Parameters = new SortedDictionary<string, string>(
            parameters.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        Limits = limits;
    }

    /// <summary>The input artifact's id, for messages; source spans are attributed by the core.</summary>
    public string ArtifactId { get; }

    /// <summary>The input artifact's exact bytes.</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    /// <summary>
    /// The build definition's parameters for this derivation, in ordinal key order. A key the
    /// adapter does not recognise is a refusal, never ignored: an ignored parameter would be
    /// recorded as though it had shaped the output.
    /// </summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>The bounds the adapter must respect.</summary>
    public CorpusLimits Limits { get; }
}
