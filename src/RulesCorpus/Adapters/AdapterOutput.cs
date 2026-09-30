namespace RulesCorpus.Adapters;

/// <summary>What an adapter produced. Validated by the core before anything is recorded.</summary>
public sealed class AdapterOutput
{
    /// <summary>Creates an output. Losses and segments are copied.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public AdapterOutput(
        ReadOnlyMemory<byte> canonical,
        string mediaType,
        DerivationFidelity fidelity,
        IReadOnlyList<string> losses,
        IReadOnlyList<AdapterSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(mediaType);
        ArgumentNullException.ThrowIfNull(losses);
        ArgumentNullException.ThrowIfNull(segments);
        Canonical = canonical;
        MediaType = mediaType;
        Fidelity = fidelity;
        Losses = losses.ToArray();
        Segments = segments.ToArray();
    }

    /// <summary>The canonical bytes.</summary>
    public ReadOnlyMemory<byte> Canonical { get; }

    /// <summary>The canonical artifact's media type.</summary>
    public string MediaType { get; }

    /// <summary>What kind of transformation this was.</summary>
    public DerivationFidelity Fidelity { get; }

    /// <summary>
    /// What the derivation discarded, one plain statement each. Empty exactly when
    /// <see cref="Fidelity"/> is <see cref="DerivationFidelity.Lossless"/>: a transformation is
    /// never presented as lossless unless it is.
    /// </summary>
    public IReadOnlyList<string> Losses { get; }

    /// <summary>Segments of <see cref="Canonical"/>, in document order.</summary>
    public IReadOnlyList<AdapterSegment> Segments { get; }
}

/// <summary>One addressable span of an adapter's canonical output.</summary>
/// <param name="Id">The segment id; must be unique in the manifest.</param>
/// <param name="Start">Byte offset into the canonical bytes.</param>
/// <param name="Length">Byte length, at least 1.</param>
/// <param name="Locator">Optional human-facing locator, verbatim from the source.</param>
/// <param name="Sources">Where in the adapter's input the segment came from.</param>
public sealed record AdapterSegment(
    string Id,
    long Start,
    long Length,
    string? Locator,
    IReadOnlyList<AdapterSourceSpan> Sources);

/// <summary>
/// A mapping from a segment back into the adapter's input artifact. At least one of
/// <see cref="Pages"/> and <see cref="Bytes"/> is set.
/// </summary>
/// <param name="Pages">The page range, when the input carries page structure.</param>
/// <param name="Bytes">The byte range of the input.</param>
public sealed record AdapterSourceSpan(PageRange? Pages, ByteRange? Bytes);

/// <summary>An inclusive page range, 1-based.</summary>
/// <param name="From">First page, at least 1.</param>
/// <param name="To">Last page, at least <paramref name="From"/>.</param>
public sealed record PageRange(int From, int To);

/// <summary>A byte range.</summary>
/// <param name="Start">Offset, at least 0.</param>
/// <param name="Length">Length, at least 1.</param>
public sealed record ByteRange(long Start, long Length);
