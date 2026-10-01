namespace RulesCorpus;

/// <summary>
/// Resource bounds. Imported content is untrusted, so a build refuses rather than truncates
/// when a bound is exceeded (docs/corpus-format.md, Limits).
///
/// <para>
/// Every bound is checked where it is set, not where it is used: a value that could never be a
/// real limit (zero, negative, or larger than one array can hold) throws
/// <see cref="ArgumentOutOfRangeException"/> from the setter, so it cannot reach an allocation
/// such as <c>new byte[length]</c>.
/// </para>
/// </summary>
public sealed record CorpusLimits
{
    private long maxArtifactBytes = 256L * 1024 * 1024;
    private int maxSegments = 1_000_000;
    private long maxManifestBytes = 256L * 1024 * 1024;
    private int maxPackedEntries = 10_000;
    private long maxPackedBytes = 1024L * 1024 * 1024;

    /// <summary>The defaults every build uses unless told otherwise.</summary>
    public static CorpusLimits Default { get; } = new();

    /// <summary>
    /// Largest single artifact, in bytes. Default 256 MiB. At least 1 and at most
    /// <see cref="Array.MaxLength"/>: an artifact is held in one byte array.
    /// </summary>
    public long MaxArtifactBytes
    {
        get => maxArtifactBytes;
        init => maxArtifactBytes = ArrayBytes(value, nameof(MaxArtifactBytes));
    }

    /// <summary>Most segments in one manifest. Default 1,000,000. At least 1.</summary>
    public int MaxSegments
    {
        get => maxSegments;
        init => maxSegments = AtLeastOne(value, nameof(MaxSegments));
    }

    /// <summary>
    /// Largest manifest file, in bytes. Default 256 MiB. At least 1 and at most
    /// <see cref="Array.MaxLength"/>: a manifest is held in one byte array.
    /// </summary>
    public long MaxManifestBytes
    {
        get => maxManifestBytes;
        init => maxManifestBytes = ArrayBytes(value, nameof(MaxManifestBytes));
    }

    /// <summary>
    /// Most entries in a packed corpus. Default 10,000: every stored artifact is declared and
    /// cited one by one, and the corpora this tool is built for hold a handful. At least 1.
    /// </summary>
    public int MaxPackedEntries
    {
        get => maxPackedEntries;
        init => maxPackedEntries = AtLeastOne(value, nameof(MaxPackedEntries));
    }

    /// <summary>
    /// Most bytes of entry content a packed corpus may hold in memory in total. Default 1 GiB,
    /// four times the default per-file bound: the manifest, the build definition and several
    /// maximum-size artifacts. The per-entry bounds <see cref="MaxArtifactBytes"/> and
    /// <see cref="MaxManifestBytes"/> still apply to each entry. At least 1.
    /// </summary>
    public long MaxPackedBytes
    {
        get => maxPackedBytes;
        init => maxPackedBytes = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxPackedBytes), value, "MaxPackedBytes must be at least 1.");
    }

    private static long ArrayBytes(long value, string name) =>
        value >= 1 && value <= Array.MaxLength
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} must be from 1 to {Array.MaxLength} (the most one array can hold).");

    private static int AtLeastOne(int value, string name) =>
        value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(name, value, $"{name} must be at least 1.");
}
