namespace RulesCorpus;

/// <summary>
/// Resource bounds. Imported content is untrusted, so a build refuses rather than truncates
/// when a bound is exceeded (docs/corpus-format.md, Limits).
/// </summary>
public sealed record CorpusLimits
{
    /// <summary>The defaults every build uses unless told otherwise.</summary>
    public static CorpusLimits Default { get; } = new();

    /// <summary>Largest single artifact, in bytes. Default 256 MiB.</summary>
    public long MaxArtifactBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Most segments in one manifest. Default 1,000,000.</summary>
    public int MaxSegments { get; init; } = 1_000_000;

    /// <summary>Largest manifest file, in bytes. Default 256 MiB.</summary>
    public long MaxManifestBytes { get; init; } = 256L * 1024 * 1024;
}
