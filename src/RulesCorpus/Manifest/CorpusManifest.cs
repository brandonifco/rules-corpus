using RulesCorpus.Internal;
using RulesCorpus.Json;
using RulesCorpus.Manifest;

namespace RulesCorpus;

/// <summary>
/// A corpus manifest, <c>corpus.json</c> (docs/corpus-format.md): what was built, from what,
/// how, and the two identities over it.
///
/// <para>
/// There is no public constructor. An instance is either read by <see cref="Parse"/>, which
/// refuses anything the format does not allow, or produced by <see cref="CorpusBuilder"/>,
/// which validates the same way before it writes. So holding a <see cref="CorpusManifest"/>
/// means holding one whose references resolve and whose digests were recomputed and matched;
/// no consumer has to re-check.
/// </para>
/// </summary>
public sealed class CorpusManifest
{
    internal CorpusManifest(
        string corpusId,
        IReadOnlyList<ManifestArtifact> artifacts,
        IReadOnlyList<ManifestDerivation> derivations,
        IReadOnlyList<ManifestBaseline> baselines,
        IReadOnlyList<ManifestSegment> segments,
        ContentDigest contentDigest,
        ContentDigest manifestDigest)
    {
        CorpusId = corpusId;
        Artifacts = ReadOnly.List(artifacts);
        Derivations = ReadOnly.List(derivations);
        Baselines = ReadOnly.List(baselines);
        Segments = ReadOnly.List(segments);
        ContentDigest = contentDigest;
        ManifestDigest = manifestDigest;
    }

    /// <summary>The corpus id. Deliberately outside content identity (decision 0002).</summary>
    public string CorpusId { get; }

    /// <summary>Every artifact: sources in build-definition order, then adapter outputs in derivation order.</summary>
    public IReadOnlyList<ManifestArtifact> Artifacts { get; }

    /// <summary>Every derivation: external ones first, then adapter runs, each in build-definition order.</summary>
    public IReadOnlyList<ManifestDerivation> Derivations { get; }

    /// <summary>The baselines engines pin, one per cited source.</summary>
    public IReadOnlyList<ManifestBaseline> Baselines { get; }

    /// <summary>The segment table, in derivation order and, within a derivation, document order.</summary>
    public IReadOnlyList<ManifestSegment> Segments { get; }

    /// <summary>
    /// The content identity: changes when the baselines engines cite or the segment addressing
    /// changes, and only then. A metadata edit cannot pass for a content change.
    /// </summary>
    public ContentDigest ContentDigest { get; }

    /// <summary>
    /// The manifest identity: changes when anything declared changes, so a content change cannot
    /// hide behind a stable label.
    /// </summary>
    public ContentDigest ManifestDigest { get; }

    /// <summary>
    /// Reads and fully validates a manifest: strict canonical-JSON reading, schema and grammar of
    /// every member, uniqueness, reference resolution, derivation and segment ordering, spans in
    /// bounds of declared lengths, limits, and both digests recomputed. Bytes on disk are not
    /// examined; that is <see cref="CorpusVerifier"/>'s job.
    /// </summary>
    /// <exception cref="CorpusException">The manifest is invalid; every problem found is listed.</exception>
    public static CorpusManifest Parse(ReadOnlySpan<byte> utf8, CorpusLimits? limits = null)
    {
        limits ??= CorpusLimits.Default;
        var errors = new List<CorpusError>();
        (CorpusManifest? manifest, _) = ReadAndValidate(utf8, limits, errors, out _);
        if (manifest is null || errors.Count != 0)
        {
            throw new CorpusException("The manifest is invalid.", errors);
        }

        return manifest;
    }

    /// <summary>Reads <c>corpus.json</c> from a corpus directory or packed corpus and validates it as <see cref="Parse"/> does.</summary>
    /// <exception cref="CorpusException">The manifest cannot be read or is invalid.</exception>
    public static CorpusManifest Load(CorpusFiles corpus, CorpusLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        limits ??= CorpusLimits.Default;
        if (corpus is Files.PackedCorpusFiles { Problems.Count: > 0 } packed)
        {
            throw new CorpusException(
                "The packed corpus is malformed.",
                packed.Problems.Select(p => new CorpusError("(archive)", p)).ToList());
        }

        if (!corpus.TryRead(Vocabulary.ManifestFile, limits.MaxManifestBytes, out byte[] bytes, out string problem))
        {
            throw new CorpusException("The manifest cannot be read.", [new CorpusError(Vocabulary.ManifestFile, problem)]);
        }

        return Parse(bytes, limits);
    }

    /// <summary>
    /// The on-disk form of <c>corpus.json</c>: canonical JSON with two-space indentation and a
    /// final newline. The only formatter of it; the same manifest always writes the same bytes.
    /// </summary>
    public byte[] ToUtf8Json() => CanonicalJsonWriter.ToIndented(ManifestJson.ToJson(this, includeManifestDigest: true));

    /// <summary>
    /// Reads the schema, then runs every validator check. Returns the manifest (null if the schema
    /// failed) and the parsed JSON, appending every error. <paramref name="checks"/> receives the
    /// per-check results when the schema passed.
    /// </summary>
    internal static (CorpusManifest? Manifest, CjObject? Json) ReadAndValidate(
        ReadOnlySpan<byte> utf8,
        CorpusLimits limits,
        List<CorpusError> errors,
        out List<(string Check, List<CorpusError> Errors)>? checks)
    {
        checks = null;
        (CorpusManifest? manifest, CjObject? json) = ReadSchema(utf8, errors);
        if (manifest is null)
        {
            return (null, json);
        }

        checks = ManifestValidator.Validate(manifest, json, utf8.Length, limits);
        foreach ((_, List<CorpusError> checkErrors) in checks)
        {
            errors.AddRange(checkErrors);
        }

        return (manifest, json);
    }

    /// <summary>Strict JSON and schema only.</summary>
    internal static (CorpusManifest? Manifest, CjObject? Json) ReadSchema(ReadOnlySpan<byte> utf8, List<CorpusError> errors)
    {
        CjValue? root = CanonicalJsonReader.Parse(utf8, errors);
        if (root is null)
        {
            return (null, null);
        }

        CorpusManifest? manifest = ManifestJson.Read(root, errors);
        return (manifest, root as CjObject);
    }
}
