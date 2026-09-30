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

        for (int i = 0; i < paths.Count; i++)
        {
            for (int j = 0; j < i; j++)
            {
                if (CorpusPaths.Collide(paths[i].Path, paths[j].Path))
                {
                    errors.Add(new CorpusError(paths[i].Where, $"'{paths[i].Path}' collides with {paths[j].Where} '{paths[j].Path}' (the same file, ignoring case, or one inside the other); build never writes to a source path"));
                }
            }
        }

        // Baselines name a known artifact, one per source id.
        var sourceIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Baselines.Count; i++)
        {
            ManifestBaseline b = Baselines[i];
            if (!sourceIndex.ContainsKey(b.Artifact) && !Derivations.Any(d => d.OutputId == b.Artifact))
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
