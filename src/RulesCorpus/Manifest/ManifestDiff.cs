using RulesCorpus.Json;
using RulesCorpus.Manifest;

namespace RulesCorpus;

/// <summary>
/// The structured difference between two manifests: whether each identity moved, and which
/// artifacts, derivations, baselines and segments were added, removed or changed. Rendering is the caller's.
/// </summary>
public sealed class ManifestDiff
{
    private ManifestDiff()
    {
    }

    /// <summary>Whether the content identity is unchanged: engines cite the same content at the same addresses.</summary>
    public bool ContentDigestEqual { get; private init; }

    /// <summary>Whether the manifest identity is unchanged: nothing declared changed at all.</summary>
    public bool ManifestDigestEqual { get; private init; }

    /// <summary>Artifacts only in the second manifest, in its order.</summary>
    public IReadOnlyList<ManifestArtifact> ArtifactsAdded { get; private init; } = [];

    /// <summary>Artifacts only in the first manifest, in its order.</summary>
    public IReadOnlyList<ManifestArtifact> ArtifactsRemoved { get; private init; } = [];

    /// <summary>Artifacts in both whose records differ in any member (digest, length, path, provenance), in the second manifest's order.</summary>
    public IReadOnlyList<ManifestChange<ManifestArtifact>> ArtifactsChanged { get; private init; } = [];

    /// <summary>Derivations only in the second manifest, matched by id, in its order.</summary>
    public IReadOnlyList<ManifestDerivation> DerivationsAdded { get; private init; } = [];

    /// <summary>Derivations only in the first manifest, matched by id, in its order.</summary>
    public IReadOnlyList<ManifestDerivation> DerivationsRemoved { get; private init; } = [];

    /// <summary>
    /// Derivations in both whose records differ in any member (inputs, output, tool, parameters,
    /// fidelity, losses), in the second manifest's order. A derivation that now discards more is
    /// listed here even though no artifact or segment record changed.
    /// </summary>
    public IReadOnlyList<ManifestChange<ManifestDerivation>> DerivationsChanged { get; private init; } = [];

    /// <summary>Baselines only in the second manifest, matched by source id.</summary>
    public IReadOnlyList<ManifestBaseline> BaselinesAdded { get; private init; } = [];

    /// <summary>Baselines only in the first manifest, matched by source id.</summary>
    public IReadOnlyList<ManifestBaseline> BaselinesRemoved { get; private init; } = [];

    /// <summary>
    /// Baselines in both whose artifact, hash derivation, as-of date or content hash differ. The
    /// content hash is the named artifact's digest, so a baseline whose artifact's bytes changed is
    /// listed even though its own members did not.
    /// </summary>
    public IReadOnlyList<ManifestChange<ManifestBaseline>> BaselinesChanged { get; private init; } = [];

    /// <summary>Segments only in the second manifest, matched by id.</summary>
    public IReadOnlyList<ManifestSegment> SegmentsAdded { get; private init; } = [];

    /// <summary>Segments only in the first manifest, matched by id.</summary>
    public IReadOnlyList<ManifestSegment> SegmentsRemoved { get; private init; } = [];

    /// <summary>Segments in both whose records differ in any member (span, digest, locator, source spans).</summary>
    public IReadOnlyList<ManifestChange<ManifestSegment>> SegmentsChanged { get; private init; } = [];

    /// <summary>Compares two manifests.</summary>
    /// <exception cref="ArgumentNullException">A manifest is null.</exception>
    public static ManifestDiff Compare(CorpusManifest before, CorpusManifest after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var (artifactsAdded, artifactsRemoved, artifactsChanged) = Match(
            before.Artifacts, after.Artifacts, a => a.Id, a => CanonicalJsonWriter.ToCompact(ManifestJson.ToJson(a)));

        var (derivationsAdded, derivationsRemoved, derivationsChanged) = Match(
            before.Derivations, after.Derivations, d => d.Id, d => CanonicalJsonWriter.ToCompact(ManifestJson.ToJson(d)));

        Func<ManifestBaseline, byte[]> baselineKey(CorpusManifest m) => b =>
        {
            ContentDigest? hash = m.Artifacts.FirstOrDefault(a => a.Id == b.Artifact)?.Digest;
            CjObject json = ManifestJson.ToJson(b).Add("contentHash", CjValue.Of(hash?.ToString() ?? string.Empty));
            return CanonicalJsonWriter.ToCompact(json);
        };

        var (baselinesAdded, baselinesRemoved, baselinesChanged) = Match(
            before.Baselines, after.Baselines, b => b.SourceId, baselineKey(before), baselineKey(after));

        var (segmentsAdded, segmentsRemoved, segmentsChanged) = Match(
            before.Segments, after.Segments, s => s.Id, s => CanonicalJsonWriter.ToCompact(ManifestJson.ToJson(s)));

        return new ManifestDiff
        {
            ContentDigestEqual = before.ContentDigest == after.ContentDigest,
            ManifestDigestEqual = before.ManifestDigest == after.ManifestDigest,
            ArtifactsAdded = artifactsAdded,
            ArtifactsRemoved = artifactsRemoved,
            ArtifactsChanged = artifactsChanged,
            DerivationsAdded = derivationsAdded,
            DerivationsRemoved = derivationsRemoved,
            DerivationsChanged = derivationsChanged,
            BaselinesAdded = baselinesAdded,
            BaselinesRemoved = baselinesRemoved,
            BaselinesChanged = baselinesChanged,
            SegmentsAdded = segmentsAdded,
            SegmentsRemoved = segmentsRemoved,
            SegmentsChanged = segmentsChanged,
        };
    }

    private static (List<T> Added, List<T> Removed, List<ManifestChange<T>> Changed) Match<T>(
        IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> key, Func<T, byte[]> form)
        where T : class => Match(before, after, key, form, form);

    private static (List<T> Added, List<T> Removed, List<ManifestChange<T>> Changed) Match<T>(
        IReadOnlyList<T> before, IReadOnlyList<T> after, Func<T, string> key, Func<T, byte[]> beforeForm, Func<T, byte[]> afterForm)
        where T : class
    {
        var beforeByKey = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (T item in before)
        {
            beforeByKey.TryAdd(key(item), item);
        }

        var afterKeys = new HashSet<string>(after.Select(key), StringComparer.Ordinal);
        var added = new List<T>();
        var changed = new List<ManifestChange<T>>();
        foreach (T item in after)
        {
            if (!beforeByKey.TryGetValue(key(item), out T? old))
            {
                added.Add(item);
            }
            else if (!beforeForm(old).AsSpan().SequenceEqual(afterForm(item)))
            {
                changed.Add(new ManifestChange<T>(old, item));
            }
        }

        List<T> removed = before.Where(item => !afterKeys.Contains(key(item))).ToList();
        return (added, removed, changed);
    }
}

/// <summary>One record present in both manifests with different contents.</summary>
/// <typeparam name="T">The record type.</typeparam>
public sealed class ManifestChange<T>
    where T : class
{
    internal ManifestChange(T before, T after)
    {
        Before = before;
        After = after;
    }

    /// <summary>The record in the first manifest.</summary>
    public T Before { get; }

    /// <summary>The record in the second manifest.</summary>
    public T After { get; }
}
