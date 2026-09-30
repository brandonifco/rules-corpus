using RulesCorpus.Adapters;
using RulesCorpus.Internal;
using RulesCorpus.Json;
using RulesCorpus.Manifest;

namespace RulesCorpus.Build;

/// <summary>A <c>corpus.build.json</c> as read: hand-written, so read strictly and cross-checked.</summary>
internal sealed class BuildDefinition
{
    public required string CorpusId { get; init; }

    public required List<BuildSource> Sources { get; init; }

    public required List<BuildDerivation> Derivations { get; init; }

    public required List<BuildExternal> External { get; init; }

    public required List<ManifestBaseline> Baselines { get; init; }

    // Which external derivation, if any, produced each source; built on first use.
    private Dictionary<string, string>? _externalOutputs;

    /// <summary>The artifact record a source becomes, given its measured (or, unstored, declared) length and digest.</summary>
    public ManifestArtifact SourceArtifact(BuildSource s, long length, ContentDigest digest)
    {
        if (_externalOutputs is null)
        {
            _externalOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (BuildExternal e in External)
            {
                _externalOutputs.TryAdd(e.Output, e.Id);
            }
        }

        string? derivedBy = _externalOutputs.GetValueOrDefault(s.Id);
        return new ManifestArtifact(
            s.Id,
            derivedBy is null ? ArtifactRole.Source : ArtifactRole.Derived,
            s.MediaType,
            length,
            digest,
            s.Stored,
            s.Path,
            derivedBy is null ? new ArtifactAcquisition(s.Origin!, s.Retrieved, s.Notes) : null,
            derivedBy);
    }

    /// <summary>The artifact record an adapter derivation's output becomes.</summary>
    public static ManifestArtifact AdapterOutput(BuildDerivation d, string mediaType, long length, ContentDigest digest) =>
        new(d.OutputId, ArtifactRole.Derived, mediaType, length, digest, stored: true, d.OutputPath, acquisition: null, derivedBy: d.Id);

    /// <summary>The derivation record an adapter run becomes; version, fidelity and losses are the adapter's.</summary>
    public static ManifestDerivation AdapterDerivation(BuildDerivation d, string adapterVersion, DerivationFidelity fidelity, IReadOnlyList<string> losses) =>
        new(d.Id, [d.Input], d.OutputId, new DerivationTool(d.Adapter, adapterVersion), d.Parameters, DerivationReproducibility.Reproducible, fidelity, losses);

    /// <summary>The derivation record an external derivation becomes: copied as declared.</summary>
    public static ManifestDerivation ExternalDerivation(BuildExternal e) =>
        new(e.Id, e.Inputs, e.Output, e.Tool, e.Parameters, DerivationReproducibility.External, e.Fidelity, e.Losses);

    /// <summary>
    /// Every way the manifest's records differ from what building this definition records
    /// (decision 0007): the same corpus id, sources, external derivations, adapter derivations
    /// and baselines, in the same order. What the definition does not declare (a stored file's
    /// bytes and digest, an adapter's version, media type, fidelity and losses) is taken from
    /// the manifest here; verification checks those against the files and, with a rebuild,
    /// against the adapter.
    /// </summary>
    public List<CorpusError> Disagreements(CorpusManifest m)
    {
        var errors = new List<CorpusError>();
        if (!string.Equals(CorpusId, m.CorpusId, StringComparison.Ordinal))
        {
            errors.Add(new CorpusError("$.corpusId", $"'{CorpusId}' differs from the manifest's '{m.CorpusId}'"));
        }

        if (m.Artifacts.Count != Sources.Count + Derivations.Count)
        {
            errors.Add(new CorpusError("$", $"declares {Sources.Count} source(s) and {Derivations.Count} adapter output(s); the manifest records {m.Artifacts.Count} artifact(s)"));
        }

        if (m.Derivations.Count != External.Count + Derivations.Count)
        {
            errors.Add(new CorpusError("$", $"declares {External.Count} external and {Derivations.Count} adapter derivation(s); the manifest records {m.Derivations.Count} derivation(s)"));
        }

        if (m.Baselines.Count != Baselines.Count)
        {
            errors.Add(new CorpusError("$.baselines", $"declares {Baselines.Count} baseline(s); the manifest records {m.Baselines.Count}"));
        }

        for (int i = 0; i < Sources.Count && i < m.Artifacts.Count; i++)
        {
            BuildSource s = Sources[i];
            ManifestArtifact recorded = m.Artifacts[i];
            ManifestArtifact expected = s.Stored
                ? SourceArtifact(s, recorded.Bytes, recorded.Digest)
                : SourceArtifact(s, s.Bytes!.Value, s.Digest!);
            Compare($"$.sources[{i}]", ManifestJson.ToJson(expected), $"$.artifacts[{i}]", ManifestJson.ToJson(recorded), errors);
        }

        for (int i = 0; i < External.Count && i < m.Derivations.Count; i++)
        {
            Compare($"$.external[{i}]", ManifestJson.ToJson(ExternalDerivation(External[i])), $"$.derivations[{i}]", ManifestJson.ToJson(m.Derivations[i]), errors);
        }

        for (int i = 0; i < Derivations.Count; i++)
        {
            BuildDerivation d = Derivations[i];
            int ai = Sources.Count + i;
            if (ai < m.Artifacts.Count)
            {
                ManifestArtifact recorded = m.Artifacts[ai];
                ManifestArtifact expected = AdapterOutput(d, recorded.MediaType, recorded.Bytes, recorded.Digest);
                Compare($"$.derivations[{i}].output", ManifestJson.ToJson(expected), $"$.artifacts[{ai}]", ManifestJson.ToJson(recorded), errors);
            }

            int di = External.Count + i;
            if (di < m.Derivations.Count)
            {
                ManifestDerivation recorded = m.Derivations[di];
                ManifestDerivation expected = AdapterDerivation(d, recorded.Tool.Version, recorded.Fidelity, recorded.Losses);
                Compare($"$.derivations[{i}]", ManifestJson.ToJson(expected), $"$.derivations[{di}]", ManifestJson.ToJson(recorded), errors);
            }
        }

        for (int i = 0; i < Baselines.Count && i < m.Baselines.Count; i++)
        {
            Compare($"$.baselines[{i}]", ManifestJson.ToJson(Baselines[i]), $"$.baselines[{i}]", ManifestJson.ToJson(m.Baselines[i]), errors);
        }

        return errors;
    }

    private static void Compare(string at, CjObject declared, string manifestAt, CjObject recorded, List<CorpusError> errors)
    {
        byte[] expected = CanonicalJsonWriter.ToCompact(declared);
        byte[] actual = CanonicalJsonWriter.ToCompact(recorded);
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            errors.Add(new CorpusError(at, $"builds {Utf8(expected)}, but the manifest's {manifestAt} records {Utf8(actual)}"));
        }
    }

    private static string Utf8(byte[] bytes) => System.Text.Encoding.UTF8.GetString(bytes);

    /// <summary>Reads and cross-checks a build definition, or returns null having appended every error.</summary>
    public static BuildDefinition? Read(ReadOnlySpan<byte> utf8, List<CorpusError> errors)
    {
        int before = errors.Count;
        CjValue? root = CanonicalJsonReader.Parse(utf8, errors);
        if (root is null)
        {
            return null;
        }

        var top = new JsonMembers(root, "$", errors);
        top.Constant("schema", Vocabulary.BuildSchema);
        string? corpusId = top.Id("corpusId", required: true, "corpus id");
        List<BuildSource>? sources = ReadList(top, "sources", allowEmpty: false, errors, ReadSource);
        List<BuildDerivation>? derivations = ReadList(top, "derivations", allowEmpty: true, errors, ReadDerivation);
        List<BuildExternal>? external = ReadList(top, "external", allowEmpty: true, errors, ReadExternal);
        List<ManifestBaseline>? baselines = ReadList(top, "baselines", allowEmpty: true, errors, ManifestJson.ReadBaseline);
        top.Finish();

        if (errors.Count != before)
        {
            return null;
        }

        var definition = new BuildDefinition
        {
            CorpusId = corpusId!,
            Sources = sources!,
            Derivations = derivations!,
            External = external!,
            Baselines = baselines!,
        };
        definition.CrossCheck(errors);
        return errors.Count == before ? definition : null;
    }

    private void CrossCheck(List<CorpusError> errors)
    {
        // One namespace for artifact and derivation ids, as in the manifest.
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        void Claim(string id, string where)
        {
            if (!ids.TryAdd(id, where))
            {
                errors.Add(new CorpusError(where, $"'{id}' collides with {ids[id]}; artifact and derivation ids are unique across the build definition"));
            }
        }

        var sourceIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Sources.Count; i++)
        {
            Claim(Sources[i].Id, $"$.sources[{i}].id");
            sourceIndex.TryAdd(Sources[i].Id, i);
        }

        for (int i = 0; i < Derivations.Count; i++)
        {
            Claim(Derivations[i].OutputId, $"$.derivations[{i}].output.id");
        }

        for (int i = 0; i < External.Count; i++)
        {
            Claim(External[i].Id, $"$.external[{i}].id");
        }

        for (int i = 0; i < Derivations.Count; i++)
        {
            Claim(Derivations[i].Id, $"$.derivations[{i}].id");
        }

        // External derivations: output is a source, inputs are sources declared before it.
        var externalOutputs = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < External.Count; i++)
        {
            BuildExternal e = External[i];
            if (!sourceIndex.TryGetValue(e.Output, out int outputIndex))
            {
                errors.Add(new CorpusError($"$.external[{i}].output", $"names '{e.Output}', which is not a source; an external derivation's output is a sources[] entry"));
                continue;
            }

            if (!externalOutputs.TryAdd(e.Output, i))
            {
                errors.Add(new CorpusError($"$.external[{i}].output", $"'{e.Output}' is also the output of $.external[{externalOutputs[e.Output]}]; exactly one derivation produces each derived artifact"));
            }

            for (int k = 0; k < e.Inputs.Count; k++)
            {
                if (!sourceIndex.TryGetValue(e.Inputs[k], out int inputIndex) || inputIndex >= outputIndex)
                {
                    errors.Add(new CorpusError($"$.external[{i}].inputs[{k}]", $"'{e.Inputs[k]}' must be a source declared before the output '{e.Output}' in sources"));
                }
            }
        }

        // Acquisition evidence belongs to acquired sources only.
        for (int i = 0; i < Sources.Count; i++)
        {
            BuildSource s = Sources[i];
            if (externalOutputs.ContainsKey(s.Id))
            {
                foreach ((string key, bool present) in new[] { ("origin", s.Origin is not null), ("retrieved", s.Retrieved is not null), ("notes", s.Notes is not null) })
                {
                    if (present)
                    {
                        errors.Add(new CorpusError($"$.sources[{i}].{key}", "must be absent: this source is the output of an external derivation, which records its provenance"));
                    }
                }
            }
            else if (s.Origin is null)
            {
                errors.Add(new CorpusError($"$.sources[{i}].origin", "is required for an acquired source"));
            }
        }

        // Adapter derivations: the input is a stored source or an earlier output.
        var available = new HashSet<string>(Sources.Where(s => s.Stored).Select(s => s.Id), StringComparer.Ordinal);
        for (int i = 0; i < Derivations.Count; i++)
        {
            BuildDerivation d = Derivations[i];
            if (!available.Contains(d.Input))
            {
                string reason = sourceIndex.TryGetValue(d.Input, out int si) && !Sources[si].Stored
                    ? $"names '{d.Input}', which is not stored; an adapter needs the input's bytes"
                    : $"names '{d.Input}', which is neither a source nor the output of an earlier derivation";
                errors.Add(new CorpusError($"$.derivations[{i}].input", reason));
            }

            available.Add(d.OutputId);
        }

        // Paths: nothing written may land on, inside, or around anything else.
        var paths = new List<(string Path, string Where)>();
        for (int i = 0; i < Sources.Count; i++)
        {
            if (Sources[i].Path is { } p)
            {
                paths.Add((p, $"$.sources[{i}].path"));
            }
        }

        for (int i = 0; i < Derivations.Count; i++)
        {
            paths.Add((Derivations[i].OutputPath, $"$.derivations[{i}].output.path"));
        }

        var collisions = new PathCollisions();
        foreach ((string path, string where) in paths)
        {
            if (collisions.Add(path, where) is { } collision)
            {
                errors.Add(new CorpusError(where, $"'{path}' {collision}; build never writes to a source path"));
            }
        }

        // Baselines name a known artifact, one per source id.
        var sourceIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Baselines.Count; i++)
        {
            ManifestBaseline b = Baselines[i];
            if (!sourceIndex.ContainsKey(b.Artifact) && !available.Contains(b.Artifact))
            {
                errors.Add(new CorpusError($"$.baselines[{i}].artifact", $"names artifact '{b.Artifact}', which is not declared"));
            }

            if (!sourceIds.TryAdd(b.SourceId, i))
            {
                errors.Add(new CorpusError($"$.baselines[{i}].sourceId", $"'{b.SourceId}' is already used at $.baselines[{sourceIds[b.SourceId]}]"));
            }
        }
    }

    private static List<T>? ReadList<T>(JsonMembers parent, string key, bool allowEmpty, List<CorpusError> errors, Func<JsonMembers, T?> readItem)
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
            T? item = members.IsObject ? readItem(members) : null;
            members.Finish();
            if (item is not null)
            {
                result.Add(item);
            }
        }

        return result.Count == items.Count ? result : null;
    }

    private static BuildSource? ReadSource(JsonMembers m)
    {
        int before = m.Errors.Count;
        string? id = m.Id("id", required: true, "artifact id");
        string? mediaType = m.String("mediaType", required: true, CorpusGrammar.IsMediaType, "media type (type/subtype, lowercase ASCII, at most 128 characters)");
        bool stored = m.Bool("stored", required: false) ?? true;
        string? path = null;
        long? bytes = null;
        ContentDigest? digest = null;
        if (stored)
        {
            path = ManifestJson.ReadPath(m, "path", required: true);
            m.Absent("bytes", "must not be given for a stored source; it is measured from the file");
            m.Absent("digest", "must not be given for a stored source; it is computed from the file");
        }
        else
        {
            m.Absent("path", "must be absent when stored is false");
            bytes = m.Number("bytes", required: true);
            digest = m.Digest("digest", required: true);
        }

        string? origin = m.String("origin", required: false);
        DateOnly? retrieved = m.Date("retrieved", required: false);
        string? notes = m.String("notes", required: false);

        if (m.Errors.Count != before || id is null || mediaType is null)
        {
            return null;
        }

        return new BuildSource(id, mediaType, stored, path, bytes, digest, origin, retrieved, notes);
    }

    private static BuildDerivation? ReadDerivation(JsonMembers m)
    {
        int before = m.Errors.Count;
        string? id = m.Id("id", required: true, "derivation id");
        string? adapter = m.Id("adapter", required: true, "adapter id");
        string? input = m.Id("input", required: true, "artifact id");
        JsonMembers? output = m.Object("output", required: true);
        string? outputId = output?.Id("id", required: true, "artifact id");
        string? outputPath = output is null ? null : ManifestJson.ReadPath(output, "path", required: true);
        output?.Finish();
        SortedDictionary<string, string>? parameters = m.StringMap("parameters", required: true);

        if (m.Errors.Count != before || id is null || adapter is null || input is null || outputId is null || outputPath is null || parameters is null)
        {
            return null;
        }

        return new BuildDerivation(id, adapter, input, outputId, outputPath, parameters);
    }

    private static BuildExternal? ReadExternal(JsonMembers m)
    {
        int before = m.Errors.Count;
        string? id = m.Id("id", required: true, "derivation id");
        List<string>? inputs = m.Strings("inputs", required: true, allowEmptyArray: false, CorpusGrammar.IsId, "artifact id");
        string? output = m.Id("output", required: true, "artifact id");
        DerivationTool? tool = ManifestJson.ReadTool(m.Object("tool", required: true));
        DerivationFidelity? fidelity = ManifestJson.ReadFidelity(m);
        List<string>? losses = m.Strings("losses", required: true, allowEmptyArray: true);
        SortedDictionary<string, string>? parameters = m.Has("parameters")
            ? m.StringMap("parameters", required: true)
            : new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (fidelity is not null && losses is not null)
        {
            if (fidelity == DerivationFidelity.Lossless && losses.Count != 0)
            {
                m.Error("losses", "must be empty when fidelity is lossless");
            }
            else if (fidelity != DerivationFidelity.Lossless && losses.Count == 0)
            {
                m.Error("losses", $"must say what was discarded when fidelity is {Vocabulary.Write(fidelity.Value)}");
            }
        }

        if (m.Errors.Count != before || id is null || inputs is null || output is null || tool is null
            || fidelity is null || losses is null || parameters is null)
        {
            return null;
        }

        return new BuildExternal(id, inputs, output, tool, fidelity.Value, losses, parameters);
    }
}

internal sealed record BuildSource(
    string Id,
    string MediaType,
    bool Stored,
    string? Path,
    long? Bytes,
    ContentDigest? Digest,
    string? Origin,
    DateOnly? Retrieved,
    string? Notes);

internal sealed record BuildDerivation(
    string Id,
    string Adapter,
    string Input,
    string OutputId,
    string OutputPath,
    SortedDictionary<string, string> Parameters);

internal sealed record BuildExternal(
    string Id,
    List<string> Inputs,
    string Output,
    DerivationTool Tool,
    DerivationFidelity Fidelity,
    List<string> Losses,
    SortedDictionary<string, string> Parameters);
