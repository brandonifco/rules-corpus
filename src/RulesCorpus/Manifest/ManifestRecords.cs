using RulesCorpus.Adapters;

namespace RulesCorpus;

/// <summary>Whether an artifact is acquired evidence or the product of a derivation.</summary>
public enum ArtifactRole
{
    /// <summary>Acquired evidence, recorded with where it came from; written <c>source</c>.</summary>
    Source = 1,

    /// <summary>Produced by exactly one derivation; written <c>derived</c>.</summary>
    Derived = 2,
}

/// <summary>Who can re-run a derivation, which decides what verification can prove about it.</summary>
public enum DerivationReproducibility
{
    /// <summary>A rules-corpus adapter re-runs it offline; written <c>reproducible</c>.</summary>
    Reproducible = 1,

    /// <summary>
    /// Produced outside rules-corpus. Its output is verified by digest; re-deriving it needs the
    /// named tool, so a rebuild reports it not verified (decision 0004). Written <c>external</c>.
    /// </summary>
    External = 2,
}

/// <summary>
/// One artifact the corpus declares: an acquired original or a derived one. Instances come
/// only from a validated manifest, so every member already satisfies docs/corpus-format.md.
/// </summary>
public sealed class ManifestArtifact
{
    internal ManifestArtifact(
        string id,
        ArtifactRole role,
        string mediaType,
        long bytes,
        ContentDigest digest,
        bool stored,
        string? path,
        ArtifactAcquisition? acquisition,
        string? derivedBy)
    {
        Id = id;
        Role = role;
        MediaType = mediaType;
        Bytes = bytes;
        Digest = digest;
        Stored = stored;
        Path = path;
        Acquisition = acquisition;
        DerivedBy = derivedBy;
    }

    /// <summary>The artifact id, unique in the manifest.</summary>
    public string Id { get; }

    /// <summary>Acquired or derived.</summary>
    public ArtifactRole Role { get; }

    /// <summary>The media type of the bytes.</summary>
    public string MediaType { get; }

    /// <summary>The exact length in bytes.</summary>
    public long Bytes { get; }

    /// <summary>The digest of the exact bytes.</summary>
    public ContentDigest Digest { get; }

    /// <summary>
    /// Whether the bytes are in the corpus at <see cref="Path"/>. An unstored artifact is
    /// declared by identity alone; verification reports its bytes not verified rather than ok.
    /// </summary>
    public bool Stored { get; }

    /// <summary>The relative path of the stored bytes; null exactly when not stored.</summary>
    public string? Path { get; }

    /// <summary>Where a source's bytes came from; null exactly for a derived artifact.</summary>
    public ArtifactAcquisition? Acquisition { get; }

    /// <summary>The derivation that produced a derived artifact; null exactly for a source.</summary>
    public string? DerivedBy { get; }
}

/// <summary>
/// Recorded evidence of how a source was acquired. Nothing here is fetched or checked against
/// the world: it is what the person who acquired the bytes declared.
/// </summary>
public sealed class ArtifactAcquisition
{
    internal ArtifactAcquisition(string origin, DateOnly? retrieved, string? notes)
    {
        Origin = origin;
        Retrieved = retrieved;
        Notes = notes;
    }

    /// <summary>Where the bytes came from: a URL, a publication reference, a person.</summary>
    public string Origin { get; }

    /// <summary>When they were retrieved, as declared; nothing reads a clock.</summary>
    public DateOnly? Retrieved { get; }

    /// <summary>Anything else the acquirer recorded.</summary>
    public string? Notes { get; }
}

/// <summary>How one derived artifact was produced from earlier ones. A claim recorded for audit.</summary>
public sealed class ManifestDerivation
{
    internal ManifestDerivation(
        string id,
        IReadOnlyList<string> inputs,
        string output,
        DerivationTool tool,
        IReadOnlyDictionary<string, string> parameters,
        DerivationReproducibility reproducibility,
        DerivationFidelity fidelity,
        IReadOnlyList<string> losses)
    {
        Id = id;
        Inputs = inputs;
        Output = output;
        Tool = tool;
        Parameters = parameters;
        Reproducibility = reproducibility;
        Fidelity = fidelity;
        Losses = losses;
    }

    /// <summary>The derivation id, unique in the manifest.</summary>
    public string Id { get; }

    /// <summary>The input artifact ids, each declared before <see cref="Output"/>.</summary>
    public IReadOnlyList<string> Inputs { get; }

    /// <summary>The derived artifact this derivation, and only this one, produced.</summary>
    public string Output { get; }

    /// <summary>The adapter or external program, with its version.</summary>
    public DerivationTool Tool { get; }

    /// <summary>The parameters it ran with, in ordinal key order.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; }

    /// <summary>Whether rules-corpus can re-run it.</summary>
    public DerivationReproducibility Reproducibility { get; }

    /// <summary>The claimed fidelity.</summary>
    public DerivationFidelity Fidelity { get; }

    /// <summary>What was discarded; empty exactly when <see cref="Fidelity"/> is lossless.</summary>
    public IReadOnlyList<string> Losses { get; }
}

/// <summary>The program that performed a derivation.</summary>
public sealed class DerivationTool
{
    internal DerivationTool(string id, string version)
    {
        Id = id;
        Version = version;
    }

    /// <summary>The adapter id, or the external program's name, in the id grammar.</summary>
    public string Id { get; }

    /// <summary>
    /// The version. For an adapter, output for the same input and parameters is stable within a
    /// version, which is what lets a rebuild compare bytes.
    /// </summary>
    public string Version { get; }
}

/// <summary>
/// One cited source, mapping field for field onto rules-kernel's <c>SourceBaselineId</c>: the
/// source id, the named artifact's digest as the content hash, the hash derivation, and the
/// optional as-of date.
/// </summary>
public sealed class ManifestBaseline
{
    internal ManifestBaseline(string sourceId, string artifact, string hashDerivation, DateOnly? asOf)
    {
        SourceId = sourceId;
        Artifact = artifact;
        HashDerivation = hashDerivation;
        AsOf = asOf;
    }

    /// <summary>The source id, unique in the manifest.</summary>
    public string SourceId { get; }

    /// <summary>The artifact whose digest is the content hash. Not repeated here, so it cannot disagree.</summary>
    public string Artifact { get; }

    /// <summary>What the hash covers, in rules-kernel's grammar.</summary>
    public string HashDerivation { get; }

    /// <summary>The moment of a revisable source; null for a source with no temporal dimension.</summary>
    public DateOnly? AsOf { get; }
}

/// <summary>An addressable span of a stored derived artifact.</summary>
public sealed class ManifestSegment
{
    internal ManifestSegment(
        string id,
        string artifact,
        long start,
        long length,
        ContentDigest digest,
        string? locator,
        IReadOnlyList<ManifestSourceSpan> sources)
    {
        Id = id;
        Artifact = artifact;
        Start = start;
        Length = length;
        Digest = digest;
        Locator = locator;
        Sources = sources;
    }

    /// <summary>The segment id, unique in the manifest.</summary>
    public string Id { get; }

    /// <summary>The stored derived artifact the segment is a span of.</summary>
    public string Artifact { get; }

    /// <summary>Byte offset into the artifact.</summary>
    public long Start { get; }

    /// <summary>Byte length, at least 1; the span lies on UTF-8 boundaries.</summary>
    public long Length { get; }

    /// <summary>The digest of exactly those bytes.</summary>
    public ContentDigest Digest { get; }

    /// <summary>A human-facing locator, verbatim from the source, when the adapter gave one.</summary>
    public string? Locator { get; }

    /// <summary>Where the segment came from; empty when the adapter gave no mapping.</summary>
    public IReadOnlyList<ManifestSourceSpan> Sources { get; }
}

/// <summary>A mapping from a segment back into an artifact it came from. Pages, bytes, or both.</summary>
public sealed class ManifestSourceSpan
{
    internal ManifestSourceSpan(string artifact, PageRange? pages, ByteRange? bytes)
    {
        Artifact = artifact;
        Pages = pages;
        Bytes = bytes;
    }

    /// <summary>The artifact the span points into.</summary>
    public string Artifact { get; }

    /// <summary>The inclusive page range, when the artifact carries page structure.</summary>
    public PageRange? Pages { get; }

    /// <summary>The byte range, lying within the artifact.</summary>
    public ByteRange? Bytes { get; }
}
