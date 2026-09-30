namespace RulesCorpus.Adapters.Text;

/// <summary>
/// Turns UTF-8 text into canonical <c>text/plain</c> and segments it by whole text, blank-line
/// blocks, or heading lines, as docs/adapter-contract.md specifies for <c>text</c> version 1.
///
/// <para>
/// Every transformation it makes is one it can record: removing a byte-order mark and
/// normalizing line endings, each only when asked or needed, each listed as a loss. It does
/// not normalize Unicode, because under invariant globalization that would be recorded without
/// happening (docs/decisions/0003). Anything it cannot do faithfully it refuses instead:
/// invalid UTF-8, an unknown or inert parameter, a pattern that linear-time matching cannot
/// run, a segment id outside the format's grammar, or more segments than the limits allow.
/// </para>
///
/// <para>
/// Source spans cite the input exactly as given. Their byte ranges point into the original
/// bytes, before the byte-order mark or any CR was removed, so the evidence a segment claims
/// can be checked against the artifact the manifest names without re-running the adapter.
/// </para>
/// </summary>
public sealed class TextAdapter : ICorpusAdapter
{
    private const string MediaType = "text/plain";

    /// <summary>
    /// <c>text</c>: the name a build definition's <c>adapter</c> member uses to select this
    /// adapter, and the tool id recorded against its derivations.
    /// </summary>
    public string Id => "text";

    /// <summary>
    /// <c>1</c>. Any change to the bytes or segments emitted for the same input and parameters
    /// is a new version, since a rebuild could otherwise not tell it from a changed source.
    /// </summary>
    public string Version => "1";

    /// <summary>
    /// Always <see langword="true"/>: output depends only on the input bytes and parameters.
    /// No clock, culture, environment or randomness is read, and patterns run culture-invariant.
    /// </summary>
    public bool IsDeterministic => true;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public AdapterOutput Derive(AdapterInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Parameters first: a malformed derivation is refused the same way whatever it is run on.
        var options = TextOptions.Parse(input.Parameters);

        if (input.Bytes.Length > input.Limits.MaxArtifactBytes)
        {
            throw Refusal.Of(
                $"Input '{input.ArtifactId}' is {CanonicalText.Format(input.Bytes.Length)} bytes, over "
                + $"MaxArtifactBytes ({CanonicalText.Format(input.Limits.MaxArtifactBytes)}).");
        }

        CanonicalText text;
        List<AdapterSegment> segments;
        try
        {
            text = CanonicalText.From(input.Bytes.Span, options);
            segments = Segmenter.Run(text, options, input.Limits);
        }
        catch (CorpusAdapterException e)
        {
            // Content refusals name the artifact; the build may run this adapter over many.
            throw new CorpusAdapterException($"{e.Message} (input '{input.ArtifactId}')", e);
        }

        var fidelity = text.Losses.Count == 0 ? DerivationFidelity.Lossless : DerivationFidelity.LossyTraceable;
        return new AdapterOutput(text.Bytes, MediaType, fidelity, text.Losses, segments);
    }
}
