using RulesCorpus.Adapters;
using RulesCorpus.Internal;
using RulesCorpus.Json;

namespace RulesCorpus.Manifest;

/// <summary>
/// Maps between <c>corpus.json</c> and the manifest model, and computes both identities. The
/// read direction checks the schema and the grammar of every member; everything that relates
/// members to each other is <see cref="ManifestValidator"/>'s.
/// </summary>
internal static class ManifestJson
{
    /// <summary>Reads the model, or returns null when any schema or grammar error was appended.</summary>
    public static CorpusManifest? Read(CjValue root, List<CorpusError> errors)
    {
        int before = errors.Count;
        var top = new JsonMembers(root, "$", errors);
        top.Constant("schema", Vocabulary.ManifestSchema);
        string? corpusId = top.Id("corpusId", required: true, "corpus id");
        List<ManifestArtifact>? artifacts = ReadList(top, "artifacts", allowEmpty: false, errors, ReadArtifact);
        List<ManifestDerivation>? derivations = ReadList(top, "derivations", allowEmpty: true, errors, ReadDerivation);
        List<ManifestBaseline>? baselines = ReadList(top, "baselines", allowEmpty: true, errors, ReadBaseline);
        List<ManifestSegment>? segments = ReadList(top, "segments", allowEmpty: true, errors, ReadSegment);
        ContentDigest? contentDigest = top.Digest("contentDigest", required: true);
        ContentDigest? manifestDigest = top.Digest("manifestDigest", required: true);
        top.Finish();

        if (errors.Count != before)
        {
            return null;
        }

        return new CorpusManifest(
            corpusId!, artifacts!, derivations!, baselines!, segments!, contentDigest!, manifestDigest!);
    }

    /// <summary>The manifest as a canonical value, with or without its own digest member.</summary>
    public static CjObject ToJson(
        string corpusId,
        IReadOnlyList<ManifestArtifact> artifacts,
        IReadOnlyList<ManifestDerivation> derivations,
        IReadOnlyList<ManifestBaseline> baselines,
        IReadOnlyList<ManifestSegment> segments,
        ContentDigest contentDigest,
        ContentDigest? manifestDigest)
    {
        return new CjObject()
            .Add("schema", CjValue.Of(Vocabulary.ManifestSchema))
            .Add("corpusId", CjValue.Of(corpusId))
            .Add("artifacts", new CjArray(artifacts.Select(ToJson)))
            .Add("derivations", new CjArray(derivations.Select(ToJson)))
            .Add("baselines", new CjArray(baselines.Select(ToJson)))
            .Add("segments", new CjArray(segments.Select(ToJson)))
            .Add("contentDigest", CjValue.Of(contentDigest.ToString()))
            .AddOptional("manifestDigest", manifestDigest is null ? null : CjValue.Of(manifestDigest.ToString()));
    }

    public static CjObject ToJson(CorpusManifest m, bool includeManifestDigest) =>
        ToJson(m.CorpusId, m.Artifacts, m.Derivations, m.Baselines, m.Segments, m.ContentDigest, includeManifestDigest ? m.ManifestDigest : null);

    public static CjObject ToJson(ManifestArtifact a)
    {
        CjObject? acquisition = a.Acquisition is null
            ? null
            : new CjObject()
                .Add("origin", CjValue.Of(a.Acquisition.Origin))
                .AddOptional("retrieved", a.Acquisition.Retrieved is { } r ? CjValue.Of(CorpusGrammar.FormatDate(r)) : null)
                .AddOptional("notes", a.Acquisition.Notes is { } n ? CjValue.Of(n) : null);
        return new CjObject()
            .Add("id", CjValue.Of(a.Id))
            .Add("role", CjValue.Of(Vocabulary.Write(a.Role)))
            .Add("mediaType", CjValue.Of(a.MediaType))
            .Add("bytes", CjValue.Of(a.Bytes))
            .Add("digest", CjValue.Of(a.Digest.ToString()))
            .Add("stored", CjValue.Of(a.Stored))
            .AddOptional("path", a.Path is null ? null : CjValue.Of(a.Path))
            .AddOptional("acquisition", acquisition)
            .AddOptional("derivedBy", a.DerivedBy is null ? null : CjValue.Of(a.DerivedBy));
    }

    public static CjObject ToJson(ManifestDerivation d)
    {
        var parameters = new CjObject();
        foreach (KeyValuePair<string, string> p in d.Parameters)
        {
            parameters.Add(p.Key, CjValue.Of(p.Value));
        }

        return new CjObject()
            .Add("id", CjValue.Of(d.Id))
            .Add("inputs", new CjArray(d.Inputs.Select(i => (CjValue)CjValue.Of(i))))
            .Add("output", CjValue.Of(d.Output))
            .Add("tool", new CjObject().Add("id", CjValue.Of(d.Tool.Id)).Add("version", CjValue.Of(d.Tool.Version)))
            .Add("parameters", parameters)
            .Add("reproducibility", CjValue.Of(Vocabulary.Write(d.Reproducibility)))
            .Add("fidelity", CjValue.Of(Vocabulary.Write(d.Fidelity)))
            .Add("losses", new CjArray(d.Losses.Select(l => (CjValue)CjValue.Of(l))));
    }

    public static CjObject ToJson(ManifestBaseline b) =>
        new CjObject()
            .Add("sourceId", CjValue.Of(b.SourceId))
            .Add("artifact", CjValue.Of(b.Artifact))
            .Add("hashDerivation", CjValue.Of(b.HashDerivation))
            .AddOptional("asOf", b.AsOf is { } d ? CjValue.Of(CorpusGrammar.FormatDate(d)) : null);

    public static CjObject ToJson(ManifestSegment s) =>
        new CjObject()
            .Add("id", CjValue.Of(s.Id))
            .Add("artifact", CjValue.Of(s.Artifact))
            .Add("start", CjValue.Of(s.Start))
            .Add("length", CjValue.Of(s.Length))
            .Add("digest", CjValue.Of(s.Digest.ToString()))
            .AddOptional("locator", s.Locator is null ? null : CjValue.Of(s.Locator))
            .AddOptional("sources", s.Sources.Count == 0 ? null : new CjArray(s.Sources.Select(ToJson)));

    public static CjObject ToJson(ManifestSourceSpan span)
    {
        var obj = new CjObject().Add("artifact", CjValue.Of(span.Artifact));
        if (span.Pages is { } pages)
        {
            obj.Add("pages", new CjObject().Add("from", CjValue.Of(pages.From)).Add("to", CjValue.Of(pages.To)));
        }

        if (span.Bytes is { } bytes)
        {
            obj.Add("start", CjValue.Of(bytes.Start)).Add("length", CjValue.Of(bytes.Length));
        }

        return obj;
    }

    /// <summary>
    /// The content identity: the baselines as rules-kernel sees them and the segment table's
    /// addressing, and nothing else. Null when a baseline's artifact does not resolve, because
    /// then there is no content hash to cover.
    /// </summary>
    public static ContentDigest? ComputeContentDigest(
        IReadOnlyList<ManifestBaseline> baselines,
        IReadOnlyList<ManifestArtifact> artifacts,
        IReadOnlyList<ManifestSegment> segments)
    {
        var digests = new Dictionary<string, ContentDigest>(StringComparer.Ordinal);
        foreach (ManifestArtifact a in artifacts)
        {
            digests.TryAdd(a.Id, a.Digest);
        }

        var baselineArray = new CjArray();
        foreach (ManifestBaseline b in baselines)
        {
            if (!digests.TryGetValue(b.Artifact, out ContentDigest? hash))
            {
                return null;
            }

            baselineArray.Items.Add(new CjObject()
                .AddOptional("asOf", b.AsOf is { } d ? CjValue.Of(CorpusGrammar.FormatDate(d)) : null)
                .Add("contentHash", CjValue.Of(hash.ToString()))
                .Add("hashDerivation", CjValue.Of(b.HashDerivation))
                .Add("sourceId", CjValue.Of(b.SourceId)));
        }

        var segmentArray = new CjArray(segments.Select(s => (CjValue)new CjObject()
            .Add("artifact", CjValue.Of(s.Artifact))
            .Add("digest", CjValue.Of(s.Digest.ToString()))
            .Add("id", CjValue.Of(s.Id))
            .Add("length", CjValue.Of(s.Length))
            .Add("start", CjValue.Of(s.Start))));

        var content = new CjObject().Add("baselines", baselineArray).Add("segments", segmentArray);
        return ContentDigest.Compute(CanonicalJsonWriter.ToCompact(content));
    }

    /// <summary>The manifest identity: the canonical JSON of everything but the manifestDigest member.</summary>
    public static ContentDigest ComputeManifestDigest(CjObject manifestWithoutDigest)
    {
        if (manifestWithoutDigest.Members.ContainsKey("manifestDigest"))
        {
            var copy = new CjObject();
            foreach (KeyValuePair<string, CjValue> member in manifestWithoutDigest.Members)
            {
                if (member.Key != "manifestDigest")
                {
                    copy.Add(member.Key, member.Value);
                }
            }

            manifestWithoutDigest = copy;
        }

        return ContentDigest.Compute(CanonicalJsonWriter.ToCompact(manifestWithoutDigest));
    }

    private static List<T>? ReadList<T>(
        JsonMembers parent, string key, bool allowEmpty, List<CorpusError> errors, Func<JsonMembers, T?> readItem)
        where T : class
    {
        List<CjValue>? items = parent.Array(key, required: true, allowEmpty);
        if (items is null)
        {
            return null;
        }

        var result = new List<T>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            var members = new JsonMembers(items[i], $"{parent.PathOf(key)}[{i}]", errors);
            T? item = readItem(members);
            members.Finish();
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result.Count == items.Count ? result : null;
    }

    private static ManifestArtifact? ReadArtifact(JsonMembers m)
    {
        string? id = m.Id("id", required: true, "artifact id");
        string? roleText = m.String("role", required: true);
        ArtifactRole? role = roleText is null ? null : Vocabulary.ReadRole(roleText);
        if (roleText is not null && role is null)
        {
            m.Error("role", $"must be 'source' or 'derived'; got '{roleText}'");
        }

        string? mediaType = m.String("mediaType", required: true, CorpusGrammar.IsMediaType, "media type (type/subtype, lowercase ASCII, at most 128 characters)");
        long? bytes = m.Number("bytes", required: true);
        ContentDigest? digest = m.Digest("digest", required: true);
        bool? stored = m.Bool("stored", required: true);

        string? path = null;
        bool pathOk = true;
        if (stored == false)
        {
            m.Absent("path", "must be absent when stored is false");
        }
        else
        {
            path = ReadPath(m, "path", required: stored == true);
            pathOk = path is not null || stored is null;
        }

        ArtifactAcquisition? acquisition = null;
        string? derivedBy = null;
        bool roleMembersOk = true;
        switch (role)
        {
            case ArtifactRole.Source:
                m.Absent("derivedBy", "must be absent for a source artifact; a source is acquired, not derived");
                JsonMembers? acq = m.Object("acquisition", required: true);
                acquisition = acq is null ? null : ReadAcquisition(acq);
                roleMembersOk = acquisition is not null;
                break;
            case ArtifactRole.Derived:
                m.Absent("acquisition", "must be absent for a derived artifact; its provenance is its derivation");
                derivedBy = m.Id("derivedBy", required: true, "derivation id");
                roleMembersOk = derivedBy is not null;
                break;
            default:
                m.Value("acquisition", required: false);
                m.Value("derivedBy", required: false);
                break;
        }

        if (id is null || role is null || mediaType is null || bytes is null || digest is null || stored is null || !pathOk || !roleMembersOk)
        {
            return null;
        }

        return new ManifestArtifact(id, role.Value, mediaType, bytes.Value, digest, stored.Value, path, acquisition, derivedBy);
    }

    internal static string? ReadPath(JsonMembers m, string key, bool required)
    {
        string? path = m.String(key, required);
        if (path is not null && CorpusPaths.Problem(path) is { } problem)
        {
            m.Error(key, $"'{path}' {problem}");
            return null;
        }

        return path;
    }

    private static ArtifactAcquisition? ReadAcquisition(JsonMembers m)
    {
        string? origin = m.String("origin", required: true);
        DateOnly? retrieved = m.Date("retrieved", required: false);
        bool retrievedOk = retrieved is not null || !m.Has("retrieved");
        string? notes = m.String("notes", required: false);
        bool notesOk = notes is not null || !m.Has("notes");
        m.Finish();
        return origin is null || !retrievedOk || !notesOk || !m.IsObject ? null : new ArtifactAcquisition(origin, retrieved, notes);
    }

    private static ManifestDerivation? ReadDerivation(JsonMembers m)
    {
        string? id = m.Id("id", required: true, "derivation id");
        List<string>? inputs = m.Strings("inputs", required: true, allowEmptyArray: false, CorpusGrammar.IsId, "artifact id");
        string? output = m.Id("output", required: true, "artifact id");
        DerivationTool? tool = ReadTool(m.Object("tool", required: true));
        SortedDictionary<string, string>? parameters = m.StringMap("parameters", required: true);
        string? reproText = m.String("reproducibility", required: true);
        DerivationReproducibility? reproducibility = reproText is null ? null : Vocabulary.ReadReproducibility(reproText);
        if (reproText is not null && reproducibility is null)
        {
            m.Error("reproducibility", $"must be 'reproducible' or 'external'; got '{reproText}'");
        }

        DerivationFidelity? fidelity = ReadFidelity(m);
        List<string>? losses = m.Strings("losses", required: true, allowEmptyArray: true);

        if (id is null || inputs is null || output is null || tool is null || parameters is null
            || reproducibility is null || fidelity is null || losses is null)
        {
            return null;
        }

        return new ManifestDerivation(id, inputs, output, tool, parameters, reproducibility.Value, fidelity.Value, losses);
    }

    internal static DerivationFidelity? ReadFidelity(JsonMembers m)
    {
        string? text = m.String("fidelity", required: true);
        DerivationFidelity? fidelity = text is null ? null : Vocabulary.ReadFidelity(text);
        if (text is not null && fidelity is null)
        {
            m.Error("fidelity", $"must be 'lossless', 'lossy-traceable' or 'non-reversible'; got '{text}'");
        }

        return fidelity;
    }

    internal static DerivationTool? ReadTool(JsonMembers? m)
    {
        if (m is null)
        {
            return null;
        }

        string? id = m.Id("id", required: true, "tool id");
        string? version = m.String("version", required: true);
        m.Finish();
        return id is null || version is null || !m.IsObject ? null : new DerivationTool(id, version);
    }

    internal static ManifestBaseline? ReadBaseline(JsonMembers m)
    {
        string? sourceId = m.Id("sourceId", required: true, "source id");
        string? artifact = m.Id("artifact", required: true, "artifact id");
        string? hashDerivation = m.String("hashDerivation", required: true, CorpusGrammar.IsHashDerivation, "hash derivation ([a-z0-9]+([-.][a-z0-9]+)*, at most 128 characters)");
        DateOnly? asOf = m.Date("asOf", required: false);
        bool asOfOk = asOf is not null || !m.Has("asOf");
        return sourceId is null || artifact is null || hashDerivation is null || !asOfOk
            ? null
            : new ManifestBaseline(sourceId, artifact, hashDerivation, asOf);
    }

    private static ManifestSegment? ReadSegment(JsonMembers m)
    {
        string? id = m.String("id", required: true, CorpusGrammar.IsSegmentId, "segment id ([A-Za-z0-9]([A-Za-z0-9._()/-]*[A-Za-z0-9)])?, at most 256 characters)");
        string? artifact = m.Id("artifact", required: true, "artifact id");
        long? start = m.Number("start", required: true);
        long? length = m.Number("length", required: true, min: 1);
        ContentDigest? digest = m.Digest("digest", required: true);
        string? locator = m.String("locator", required: false);
        bool locatorOk = locator is not null || !m.Has("locator");

        List<ManifestSourceSpan>? sources = [];
        List<CjValue>? spanItems = m.Array("sources", required: false, allowEmpty: false);
        if (spanItems is not null)
        {
            for (int i = 0; i < spanItems.Count; i++)
            {
                var span = new JsonMembers(spanItems[i], $"{m.PathOf("sources")}[{i}]", m.Errors);
                ManifestSourceSpan? read = ReadSpan(span);
                span.Finish();
                if (read is null)
                {
                    sources = null;
                }
                else
                {
                    sources?.Add(read);
                }
            }
        }
        else if (m.Has("sources"))
        {
            sources = null;
        }

        if (id is null || artifact is null || start is null || length is null || digest is null || !locatorOk || sources is null)
        {
            return null;
        }

        return new ManifestSegment(id, artifact, start.Value, length.Value, digest, locator, sources);
    }

    private static ManifestSourceSpan? ReadSpan(JsonMembers m)
    {
        if (!m.IsObject)
        {
            return null;
        }

        string? artifact = m.Id("artifact", required: true, "artifact id");
        PageRange? pages = null;
        bool pagesOk = true;
        JsonMembers? pageMembers = m.Object("pages", required: false);
        if (pageMembers is not null)
        {
            long? from = pageMembers.Number("from", required: true, min: 1, max: int.MaxValue);
            long? to = pageMembers.Number("to", required: true, min: 1, max: int.MaxValue);
            pageMembers.Finish();
            if (from is not null && to is not null && to < from)
            {
                pageMembers.Error("to", $"must be at least from ({from}); got {to}");
                pagesOk = false;
            }
            else if (from is null || to is null || !pageMembers.IsObject)
            {
                pagesOk = false;
            }
            else
            {
                pages = new PageRange((int)from.Value, (int)to.Value);
            }
        }

        ByteRange? bytes = null;
        bool bytesOk = true;
        if (m.Has("start") || m.Has("length"))
        {
            long? start = m.Number("start", required: true);
            long? length = m.Number("length", required: true, min: 1);
            if (start is null || length is null)
            {
                bytesOk = false;
            }
            else
            {
                bytes = new ByteRange(start.Value, length.Value);
            }
        }

        if (pageMembers is null && !m.Has("start") && !m.Has("length"))
        {
            m.Errors.Add(new CorpusError(m.Path, "must carry pages, or start and length, or both"));
            return null;
        }

        return artifact is null || !pagesOk || !bytesOk ? null : new ManifestSourceSpan(artifact, pages, bytes);
    }
}
