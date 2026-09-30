using RulesCorpus.Adapters;
using RulesCorpus.Internal;
using RulesCorpus.Json;

namespace RulesCorpus.Manifest;

/// <summary>
/// Everything about a manifest that relates members to each other, grouped into the named
/// checks verification reports. Schema and grammar are <see cref="ManifestJson"/>'s; bytes on
/// disk are the verifier's.
/// </summary>
internal static class ManifestValidator
{
    public const string Uniqueness = "uniqueness";
    public const string References = "references";
    public const string Derivations = "derivations";
    public const string Baselines = "baselines";
    public const string Segments = "segments";
    public const string SourceSpans = "source-spans";
    public const string Limits = "limits";
    public const string ContentDigestCheck = "content-digest";
    public const string ManifestDigestCheck = "manifest-digest";

    /// <summary>The check names, in the order they are reported.</summary>
    public static IReadOnlyList<string> CheckNames { get; } =
        [Uniqueness, References, Derivations, Baselines, Segments, SourceSpans, Limits, ContentDigestCheck, ManifestDigestCheck];

    /// <summary>
    /// Runs every check. <paramref name="json"/> is the manifest as read, when there is one, so
    /// the manifest digest is recomputed over exactly what was declared.
    /// </summary>
    public static List<(string Check, List<CorpusError> Errors)> Validate(
        CorpusManifest m, CjObject? json, long manifestBytes, CorpusLimits limits)
    {
        var index = new Index(m);
        return
        [
            (Uniqueness, CheckUniqueness(m)),
            (References, CheckReferences(m, index)),
            (Derivations, CheckDerivations(m, index)),
            (Baselines, CheckBaselines(m)),
            (Segments, CheckSegments(m, index)),
            (SourceSpans, CheckSourceSpans(m, index)),
            (Limits, CheckLimits(m, manifestBytes, limits)),
            (ContentDigestCheck, CheckContentDigest(m)),
            (ManifestDigestCheck, CheckManifestDigest(m, json)),
        ];
    }

    private static List<CorpusError> CheckUniqueness(CorpusManifest m)
    {
        var errors = new List<CorpusError>();

        // Artifacts and derivations share one namespace: a derivedBy or an input that could name
        // either would be ambiguous to a reader even when the schema says which it is.
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < m.Artifacts.Count; i++)
        {
            Claim(ids, m.Artifacts[i].Id, $"$.artifacts[{i}].id", errors);
        }

        for (int i = 0; i < m.Derivations.Count; i++)
        {
            Claim(ids, m.Derivations[i].Id, $"$.derivations[{i}].id", errors);
        }

        var segmentIds = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < m.Segments.Count; i++)
        {
            Claim(segmentIds, m.Segments[i].Id, $"$.segments[{i}].id", errors);
        }

        var paths = new List<(string Path, string Where)>();
        for (int i = 0; i < m.Artifacts.Count; i++)
        {
            if (m.Artifacts[i].Path is not { } path)
            {
                continue;
            }

            string where = $"$.artifacts[{i}].path";
            foreach ((string otherPath, string otherWhere) in paths)
            {
                if (CorpusPaths.Collide(path, otherPath))
                {
                    errors.Add(new CorpusError(where, $"'{path}' collides with {otherWhere} '{otherPath}' (the same file, ignoring case, or one inside the other)"));
                }
            }

            paths.Add((path, where));
        }

        return errors;
    }

    private static void Claim(Dictionary<string, string> seen, string id, string where, List<CorpusError> errors)
    {
        if (seen.TryGetValue(id, out string? first))
        {
            errors.Add(new CorpusError(where, $"'{id}' is already used at {first}; ids are unique in the manifest"));
        }
        else
        {
            seen.Add(id, where);
        }
    }

    private static List<CorpusError> CheckReferences(CorpusManifest m, Index index)
    {
        var errors = new List<CorpusError>();
        for (int i = 0; i < m.Artifacts.Count; i++)
        {
            if (m.Artifacts[i].DerivedBy is { } by && !index.Derivation.ContainsKey(by))
            {
                errors.Add(new CorpusError($"$.artifacts[{i}].derivedBy", $"names derivation '{by}', which is not declared"));
            }
        }

        for (int i = 0; i < m.Derivations.Count; i++)
        {
            ManifestDerivation d = m.Derivations[i];
            for (int k = 0; k < d.Inputs.Count; k++)
            {
                RequireArtifact(index, d.Inputs[k], $"$.derivations[{i}].inputs[{k}]", errors);
            }

            RequireArtifact(index, d.Output, $"$.derivations[{i}].output", errors);
        }

        for (int i = 0; i < m.Baselines.Count; i++)
        {
            RequireArtifact(index, m.Baselines[i].Artifact, $"$.baselines[{i}].artifact", errors);
        }

        for (int i = 0; i < m.Segments.Count; i++)
        {
            ManifestSegment s = m.Segments[i];
            RequireArtifact(index, s.Artifact, $"$.segments[{i}].artifact", errors);
            for (int k = 0; k < s.Sources.Count; k++)
            {
                RequireArtifact(index, s.Sources[k].Artifact, $"$.segments[{i}].sources[{k}].artifact", errors);
            }
        }

        return errors;
    }

    private static void RequireArtifact(Index index, string id, string where, List<CorpusError> errors)
    {
        if (!index.Artifact.ContainsKey(id))
        {
            errors.Add(new CorpusError(where, $"names artifact '{id}', which is not declared"));
        }
    }

    private static List<CorpusError> CheckDerivations(CorpusManifest m, Index index)
    {
        var errors = new List<CorpusError>();
        var producer = new Dictionary<string, int>(StringComparer.Ordinal);
        bool seenReproducible = false;

        for (int i = 0; i < m.Derivations.Count; i++)
        {
            ManifestDerivation d = m.Derivations[i];
            string at = $"$.derivations[{i}]";

            bool lossless = d.Fidelity == DerivationFidelity.Lossless;
            if (lossless && d.Losses.Count != 0)
            {
                errors.Add(new CorpusError(at + ".losses", "must be empty when fidelity is lossless"));
            }
            else if (!lossless && d.Losses.Count == 0)
            {
                errors.Add(new CorpusError(at + ".losses", $"must say what was discarded when fidelity is {Vocabulary.Write(d.Fidelity)}"));
            }

            if (d.Reproducibility == DerivationReproducibility.Reproducible)
            {
                seenReproducible = true;
            }
            else if (seenReproducible)
            {
                errors.Add(new CorpusError(at, "is external but follows a reproducible derivation; external derivations come first"));
            }

            if (producer.TryGetValue(d.Output, out int first))
            {
                errors.Add(new CorpusError(at + ".output", $"'{d.Output}' is also the output of $.derivations[{first}]; exactly one derivation produces each derived artifact"));
            }
            else
            {
                producer.Add(d.Output, i);
            }

            index.Artifact.TryGetValue(d.Output, out int outputIndex);
            ManifestArtifact? output = index.Artifact.ContainsKey(d.Output) ? m.Artifacts[outputIndex] : null;
            if (output is not null)
            {
                if (output.Role != ArtifactRole.Derived)
                {
                    errors.Add(new CorpusError(at + ".output", $"names source artifact '{d.Output}'; a derivation's output is a derived artifact"));
                }
                else if (!string.Equals(output.DerivedBy, d.Id, StringComparison.Ordinal))
                {
                    errors.Add(new CorpusError(at + ".output", $"artifact '{d.Output}' records derivedBy '{output.DerivedBy}', not '{d.Id}'"));
                }
            }

            var seenInputs = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < d.Inputs.Count; k++)
            {
                string input = d.Inputs[k];
                string inputAt = $"{at}.inputs[{k}]";
                if (!seenInputs.Add(input))
                {
                    errors.Add(new CorpusError(inputAt, $"'{input}' is listed twice"));
                }

                if (output is not null && index.Artifact.TryGetValue(input, out int inputIndex) && inputIndex >= outputIndex)
                {
                    errors.Add(new CorpusError(inputAt, $"'{input}' is not declared before the output '{d.Output}'; inputs precede their output in artifacts order"));
                }
            }

            if (d.Reproducibility == DerivationReproducibility.Reproducible)
            {
                if (d.Inputs.Count != 1)
                {
                    errors.Add(new CorpusError(at + ".inputs", "a reproducible derivation has exactly one input, the artifact its adapter ran over"));
                }
                else if (index.Artifact.TryGetValue(d.Inputs[0], out int only) && !m.Artifacts[only].Stored)
                {
                    errors.Add(new CorpusError(at + ".inputs[0]", $"'{d.Inputs[0]}' is not stored, so the adapter could not have run over it and cannot re-run"));
                }

                if (output is not null && !output.Stored)
                {
                    errors.Add(new CorpusError(at + ".output", $"'{d.Output}' is not stored; an adapter's output is written into the corpus"));
                }
            }
        }

        // Every derived artifact is the output of the derivation it names.
        for (int i = 0; i < m.Artifacts.Count; i++)
        {
            ManifestArtifact a = m.Artifacts[i];
            if (a.DerivedBy is { } by && index.Derivation.TryGetValue(by, out int di)
                && !string.Equals(m.Derivations[di].Output, a.Id, StringComparison.Ordinal))
            {
                errors.Add(new CorpusError($"$.artifacts[{i}].derivedBy", $"derivation '{by}' outputs '{m.Derivations[di].Output}', not this artifact"));
            }
        }

        // Artifacts order: sources (and external outputs) first, then adapter outputs in the order
        // of the derivations that produced them.
        int lastReproducible = -1;
        for (int i = 0; i < m.Artifacts.Count; i++)
        {
            ManifestArtifact a = m.Artifacts[i];
            int? by = a.DerivedBy is { } id && index.Derivation.TryGetValue(id, out int di) ? di : null;
            bool adapterOutput = by is { } b && m.Derivations[b].Reproducibility == DerivationReproducibility.Reproducible;
            if (!adapterOutput)
            {
                if (lastReproducible >= 0)
                {
                    errors.Add(new CorpusError($"$.artifacts[{i}]", "follows an adapter output; artifacts are sources first, then adapter outputs in derivations order"));
                }
            }
            else
            {
                if (by!.Value < lastReproducible)
                {
                    errors.Add(new CorpusError($"$.artifacts[{i}]", "is out of derivations order; adapter outputs appear in the order of the derivations that produced them"));
                }

                lastReproducible = by.Value;
            }
        }

        return errors;
    }

    private static List<CorpusError> CheckBaselines(CorpusManifest m)
    {
        var errors = new List<CorpusError>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < m.Baselines.Count; i++)
        {
            Claim(seen, m.Baselines[i].SourceId, $"$.baselines[{i}].sourceId", errors);
        }

        return errors;
    }

    private static List<CorpusError> CheckSegments(CorpusManifest m, Index index)
    {
        var errors = new List<CorpusError>();
        int previousDerivation = -1;
        string? previousArtifact = null;
        long previousStart = 0;
        for (int i = 0; i < m.Segments.Count; i++)
        {
            ManifestSegment s = m.Segments[i];
            string at = $"$.segments[{i}]";
            if (!index.Artifact.TryGetValue(s.Artifact, out int ai))
            {
                continue;
            }

            ManifestArtifact a = m.Artifacts[ai];
            if (a.Role != ArtifactRole.Derived)
            {
                errors.Add(new CorpusError(at + ".artifact", $"'{a.Id}' is a source artifact; segments are spans of derived artifacts"));
            }

            if (!a.Stored)
            {
                errors.Add(new CorpusError(at + ".artifact", $"'{a.Id}' is not stored; a segment's bytes must be in the corpus"));
            }

            if (s.Start > a.Bytes - s.Length)
            {
                errors.Add(new CorpusError(at, $"span [{s.Start}, {s.Start + s.Length}) lies outside artifact '{a.Id}' of {a.Bytes} bytes"));
            }

            // Order is only defined for a segment whose artifact names its derivation; the
            // derivations check reports the rest.
            if (a.DerivedBy is not { } by || !index.Derivation.TryGetValue(by, out int derivation))
            {
                continue;
            }

            if (previousArtifact is not null)
            {
                bool sameArtifact = string.Equals(previousArtifact, s.Artifact, StringComparison.Ordinal);
                bool outOfOrder = sameArtifact ? s.Start < previousStart : derivation <= previousDerivation;
                if (outOfOrder)
                {
                    errors.Add(new CorpusError(at, "is out of order; segments are in derivations order and, within one derivation, in document order"));
                }
            }

            previousDerivation = derivation;
            previousArtifact = s.Artifact;
            previousStart = s.Start;
        }

        return errors;
    }

    private static List<CorpusError> CheckSourceSpans(CorpusManifest m, Index index)
    {
        var errors = new List<CorpusError>();
        for (int i = 0; i < m.Segments.Count; i++)
        {
            ManifestSegment s = m.Segments[i];
            for (int k = 0; k < s.Sources.Count; k++)
            {
                ManifestSourceSpan span = s.Sources[k];
                if (span.Bytes is { } range && index.Artifact.TryGetValue(span.Artifact, out int ai))
                {
                    ManifestArtifact a = m.Artifacts[ai];
                    if (range.Start > a.Bytes - range.Length)
                    {
                        errors.Add(new CorpusError(
                            $"$.segments[{i}].sources[{k}]",
                            $"byte range [{range.Start}, {range.Start + range.Length}) lies outside artifact '{a.Id}' of {a.Bytes} bytes"));
                    }
                }
            }
        }

        return errors;
    }

    private static List<CorpusError> CheckLimits(CorpusManifest m, long manifestBytes, CorpusLimits limits)
    {
        var errors = new List<CorpusError>();
        if (manifestBytes > limits.MaxManifestBytes)
        {
            errors.Add(new CorpusError("$", $"the manifest is {manifestBytes} bytes; the limit is {limits.MaxManifestBytes}"));
        }

        for (int i = 0; i < m.Artifacts.Count; i++)
        {
            if (m.Artifacts[i].Bytes > limits.MaxArtifactBytes)
            {
                errors.Add(new CorpusError($"$.artifacts[{i}].bytes", $"{m.Artifacts[i].Bytes} bytes exceeds the limit of {limits.MaxArtifactBytes}"));
            }
        }

        if (m.Segments.Count > limits.MaxSegments)
        {
            errors.Add(new CorpusError("$.segments", $"{m.Segments.Count} segments exceeds the limit of {limits.MaxSegments}"));
        }

        return errors;
    }

    private static List<CorpusError> CheckContentDigest(CorpusManifest m)
    {
        ContentDigest? computed = ManifestJson.ComputeContentDigest(m.Baselines, m.Artifacts, m.Segments);
        if (computed is null)
        {
            return [new CorpusError("$.contentDigest", "cannot be recomputed: a baseline names an artifact that is not declared")];
        }

        return computed == m.ContentDigest
            ? []
            : [new CorpusError("$.contentDigest", $"declares {m.ContentDigest} but the baselines and segments digest to {computed}")];
    }

    private static List<CorpusError> CheckManifestDigest(CorpusManifest m, CjObject? json)
    {
        ContentDigest computed = ManifestJson.ComputeManifestDigest(json ?? ManifestJson.ToJson(m, includeManifestDigest: false));
        return computed == m.ManifestDigest
            ? []
            : [new CorpusError("$.manifestDigest", $"declares {m.ManifestDigest} but the manifest digests to {computed}")];
    }

    /// <summary>First index of each id; duplicates are the uniqueness check's to report.</summary>
    private sealed class Index
    {
        public Index(CorpusManifest m)
        {
            for (int i = 0; i < m.Artifacts.Count; i++)
            {
                Artifact.TryAdd(m.Artifacts[i].Id, i);
            }

            for (int i = 0; i < m.Derivations.Count; i++)
            {
                Derivation.TryAdd(m.Derivations[i].Id, i);
            }
        }

        public Dictionary<string, int> Artifact { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, int> Derivation { get; } = new(StringComparer.Ordinal);
    }
}
