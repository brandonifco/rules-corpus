using RulesCorpus.Adapters;
using RulesCorpus.Build;
using RulesCorpus.Files;
using RulesCorpus.Internal;
using RulesCorpus.Json;
using RulesCorpus.Manifest;

namespace RulesCorpus;

/// <summary>
/// Proves, offline, that a corpus directory or packed corpus matches its manifest
/// (docs/corpus-format.md, Verification). Every check reports ok, failed or not verified;
/// nothing that was not examined is reported as ok.
/// </summary>
public static class CorpusVerifier
{
    private const string SchemaCheck = "schema";
    private const string ManifestFormCheck = "manifest-form";
    private const string BuildDefinitionCheck = "build-definition";
    private const string PackageCheck = "package";

    /// <summary>Verifies a corpus and reports every check.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="corpus"/> is null.</exception>
    public static VerificationReport Verify(CorpusFiles corpus, VerificationOptions? options = null) =>
        Verify(corpus, options ?? new VerificationOptions(), out _);

    /// <summary>
    /// Verifies, and returns the digest of every file whose bytes were examined and matched, so a
    /// packer can prove that what it packs is what was verified.
    /// </summary>
    internal static VerificationReport Verify(CorpusFiles corpus, VerificationOptions options, out Dictionary<string, ContentDigest> verified)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(options);
        CorpusLimits limits = options.Limits ?? CorpusLimits.Default;
        var checks = new List<VerificationCheck>();
        verified = new Dictionary<string, ContentDigest>(StringComparer.Ordinal);

        CorpusManifest? manifest = CheckManifest(corpus, limits, checks, verified);
        CheckBuildDefinition(corpus, limits, manifest, checks, verified);
        if (corpus is PackedCorpusFiles packed)
        {
            checks.Add(CheckPackage(packed, manifest));
        }

        if (manifest is null)
        {
            const string why = "not examined: the manifest did not read";
            checks.Add(new VerificationCheck("artifacts", VerificationOutcome.NotVerified, why));
            checks.Add(new VerificationCheck("segments", VerificationOutcome.NotVerified, why));
            if (options.Rebuild)
            {
                checks.Add(new VerificationCheck("rebuild", VerificationOutcome.NotVerified, why));
            }

            return new VerificationReport(checks);
        }

        Dictionary<string, byte[]> bytesById = CheckArtifacts(corpus, manifest, limits, checks, verified);
        CheckSegmentBytes(manifest, bytesById, checks);
        if (options.Rebuild)
        {
            CheckRebuild(manifest, bytesById, options, limits, checks);
        }

        return new VerificationReport(checks);
    }

    private static CorpusManifest? CheckManifest(
        CorpusFiles corpus, CorpusLimits limits, List<VerificationCheck> checks, Dictionary<string, ContentDigest> verified)
    {
        List<(string Check, List<CorpusError> Errors)>? results = null;
        var errors = new List<CorpusError>();
        CorpusManifest? manifest = null;
        if (!corpus.TryRead(Vocabulary.ManifestFile, limits.MaxManifestBytes, out byte[] bytes, out string problem))
        {
            errors.Add(new CorpusError(Vocabulary.ManifestFile, problem));
        }
        else
        {
            (manifest, _) = CorpusManifest.ReadAndValidate(bytes, limits, errors, out results);
        }

        if (manifest is null || results is null)
        {
            checks.Add(Failed(SchemaCheck, errors));
            checks.Add(new VerificationCheck(ManifestFormCheck, VerificationOutcome.NotVerified, "not examined: the manifest did not read"));
            foreach (string name in ManifestValidator.CheckNames)
            {
                checks.Add(new VerificationCheck(name, VerificationOutcome.NotVerified, "not examined: the manifest did not read"));
            }

            return null;
        }

        checks.Add(new VerificationCheck(SchemaCheck, VerificationOutcome.Ok, "corpus.json is canonical JSON and every member matches schema rules-corpus/manifest/1"));

        // The writer is the only formatter of corpus.json (decision 0007): any other bytes for
        // the same manifest would make two packings of one corpus differ.
        bool canonicalForm = manifest.ToUtf8Json().AsSpan().SequenceEqual(bytes);
        checks.Add(canonicalForm
            ? new VerificationCheck(ManifestFormCheck, VerificationOutcome.Ok, "corpus.json is byte for byte the form the writer produces")
            : new VerificationCheck(ManifestFormCheck, VerificationOutcome.Failed, "corpus.json is not byte for byte the form the writer produces (two-space indentation, one member per line, a final newline); its whitespace or member order was changed after it was written"));
        foreach ((string name, List<CorpusError> checkErrors) in results)
        {
            checks.Add(checkErrors.Count == 0
                ? new VerificationCheck(name, VerificationOutcome.Ok, OkDetail(name, manifest))
                : Failed(name, checkErrors));
        }

        if (errors.Count == 0 && canonicalForm)
        {
            verified[Vocabulary.ManifestFile] = ContentDigest.Compute(bytes);
        }

        // A manifest that failed a structural check is still returned: its artifacts and
        // segments can be examined, and every failure is reported.
        return manifest;
    }

    private static void CheckBuildDefinition(
        CorpusFiles corpus, CorpusLimits limits, CorpusManifest? manifest, List<VerificationCheck> checks, Dictionary<string, ContentDigest> verified)
    {
        var errors = new List<CorpusError>();
        if (!corpus.TryRead(Vocabulary.BuildFile, limits.MaxManifestBytes, out byte[] bytes, out string problem))
        {
            errors.Add(new CorpusError(Vocabulary.BuildFile, problem));
        }
        else if (BuildDefinition.Read(bytes, errors) is { } definition && manifest is not null)
        {
            // Bound byte for byte by buildDigest, and declaring exactly what the manifest
            // records (decision 0007).
            ContentDigest digest = ContentDigest.Compute(bytes);
            if (digest != manifest.BuildDigest)
            {
                errors.Add(new CorpusError(Vocabulary.BuildFile, $"digests to {digest}; the manifest records buildDigest {manifest.BuildDigest}"));
            }

            errors.AddRange(definition.Disagreements(manifest));
        }

        if (errors.Count == 0 && manifest is null)
        {
            checks.Add(new VerificationCheck(BuildDefinitionCheck, VerificationOutcome.NotVerified, "corpus.build.json is valid, but it was not compared with a manifest that did not read"));
        }
        else if (errors.Count == 0)
        {
            verified[Vocabulary.BuildFile] = ContentDigest.Compute(bytes);
            checks.Add(new VerificationCheck(BuildDefinitionCheck, VerificationOutcome.Ok, "corpus.build.json matches buildDigest and declares exactly what the manifest records"));
        }
        else
        {
            checks.Add(Failed(BuildDefinitionCheck, errors));
        }
    }

    private static VerificationCheck CheckPackage(PackedCorpusFiles packed, CorpusManifest? manifest)
    {
        var problems = new List<string>(packed.Problems);
        if (packed.Problems.Count == 0 && !packed.IsCanonical)
        {
            problems.Add("the archive is not the canonical packing of its entries (entry metadata, header layout or trailing data differ)");
        }

        for (int i = 1; i < packed.EntryNames.Count; i++)
        {
            if (string.CompareOrdinal(packed.EntryNames[i - 1], packed.EntryNames[i]) >= 0)
            {
                problems.Add($"entry '{packed.EntryNames[i]}' follows '{packed.EntryNames[i - 1]}'; entries are in ordinal path order");
            }
        }

        if (manifest is null)
        {
            return problems.Count == 0
                ? new VerificationCheck(PackageCheck, VerificationOutcome.NotVerified, "the archive is well-formed, but its entries were not compared with a manifest that did not read")
                : new VerificationCheck(PackageCheck, VerificationOutcome.Failed, string.Join("; ", problems));
        }

        var expected = new SortedSet<string>(StringComparer.Ordinal) { Vocabulary.BuildFile, Vocabulary.ManifestFile };
        foreach (ManifestArtifact a in manifest.Artifacts)
        {
            if (a.Path is { } path)
            {
                expected.Add(path);
            }
        }

        var actual = new HashSet<string>(packed.EntryNames, StringComparer.Ordinal);
        foreach (string name in packed.EntryNames.Where(n => !expected.Contains(n)))
        {
            problems.Add($"entry '{name}' is not corpus.build.json, corpus.json or a stored artifact");
        }

        foreach (string name in expected.Where(n => !actual.Contains(n)))
        {
            problems.Add($"'{name}' is missing from the archive");
        }

        return problems.Count == 0
            ? new VerificationCheck(PackageCheck, VerificationOutcome.Ok, $"{packed.EntryNames.Count} entries, exactly the corpus files, canonically packed")
            : new VerificationCheck(PackageCheck, VerificationOutcome.Failed, string.Join("; ", problems));
    }

    private static Dictionary<string, byte[]> CheckArtifacts(
        CorpusFiles corpus, CorpusManifest manifest, CorpusLimits limits, List<VerificationCheck> checks, Dictionary<string, ContentDigest> verified)
    {
        var bytesById = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (ManifestArtifact a in manifest.Artifacts)
        {
            string name = "artifact " + a.Id;
            if (!a.Stored || a.Path is null)
            {
                checks.Add(new VerificationCheck(name, VerificationOutcome.NotVerified, $"declared by identity only (stored: false, {a.Bytes} bytes, {a.Digest}); its bytes are not in the corpus"));
                continue;
            }

            if (!corpus.TryRead(a.Path, limits.MaxArtifactBytes, out byte[] bytes, out string problem))
            {
                checks.Add(new VerificationCheck(name, VerificationOutcome.Failed, problem));
                continue;
            }

            ContentDigest actual = ContentDigest.Compute(bytes);
            if (bytes.LongLength != a.Bytes || actual != a.Digest)
            {
                checks.Add(new VerificationCheck(name, VerificationOutcome.Failed, $"'{a.Path}' is {bytes.LongLength} bytes with digest {actual}; the manifest declares {a.Bytes} bytes with digest {a.Digest}"));
                continue;
            }

            if (!bytesById.TryAdd(a.Id, bytes))
            {
                checks.Add(new VerificationCheck(name, VerificationOutcome.Failed, "the id is declared more than once"));
                continue;
            }

            verified[a.Path] = actual;
            checks.Add(new VerificationCheck(name, VerificationOutcome.Ok, $"'{a.Path}' is {a.Bytes} bytes with digest {a.Digest}"));
        }

        return bytesById;
    }

    private static void CheckSegmentBytes(CorpusManifest manifest, Dictionary<string, byte[]> bytesById, List<VerificationCheck> checks)
    {
        var groups = new List<(string Artifact, List<int> Segments)>();
        var byArtifact = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < manifest.Segments.Count; i++)
        {
            string artifact = manifest.Segments[i].Artifact;
            if (!byArtifact.TryGetValue(artifact, out List<int>? list))
            {
                list = [];
                byArtifact.Add(artifact, list);
                groups.Add((artifact, list));
            }

            list.Add(i);
        }

        foreach ((string artifact, List<int> indexes) in groups)
        {
            string name = "segments " + artifact;
            if (!bytesById.TryGetValue(artifact, out byte[]? bytes))
            {
                checks.Add(new VerificationCheck(name, VerificationOutcome.Failed, $"{indexes.Count} segment(s) name '{artifact}', whose bytes are not stored in the corpus or did not verify"));
                continue;
            }

            var problems = new List<string>();
            foreach (int i in indexes)
            {
                ManifestSegment s = manifest.Segments[i];
                string at = $"$.segments[{i}] ('{s.Id}')";
                if (s.Start > bytes.LongLength - s.Length)
                {
                    problems.Add($"{at}: span [{s.Start}, {s.Start + s.Length}) lies outside the {bytes.LongLength} bytes");
                    continue;
                }

                if (AdapterRun.Utf8Problem(bytes, s.Start, s.Length) is { } utf8)
                {
                    problems.Add($"{at}: {utf8}");
                }

                ContentDigest actual = ContentDigest.Compute(bytes.AsSpan((int)s.Start, (int)s.Length));
                if (actual != s.Digest)
                {
                    problems.Add($"{at}: bytes digest to {actual}; the manifest declares {s.Digest}");
                }
            }

            checks.Add(problems.Count == 0
                ? new VerificationCheck(name, VerificationOutcome.Ok, $"{indexes.Count} segment(s) in bounds, on UTF-8 boundaries, digests match")
                : new VerificationCheck(name, VerificationOutcome.Failed, string.Join("; ", problems)));
        }
    }

    private static void CheckRebuild(
        CorpusManifest manifest,
        Dictionary<string, byte[]> bytesById,
        VerificationOptions options,
        CorpusLimits limits,
        List<VerificationCheck> checks)
    {
        var adapters = new Dictionary<string, ICorpusAdapter>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (ICorpusAdapter adapter in options.Adapters ?? [])
        {
            ArgumentNullException.ThrowIfNull(adapter, nameof(options));
            if (!adapters.TryAdd(adapter.Id, adapter))
            {
                duplicated.Add(adapter.Id);
            }
        }

        for (int i = 0; i < manifest.Derivations.Count; i++)
        {
            ManifestDerivation d = manifest.Derivations[i];
            string name = "rebuild " + d.Id;
            if (d.Reproducibility == DerivationReproducibility.External)
            {
                checks.Add(new VerificationCheck(name, VerificationOutcome.NotVerified, $"external derivation by {d.Tool.Id} {d.Tool.Version}; re-deriving it needs that tool, so only its output's digest is verified"));
                continue;
            }

            // As the builder does: which of two same-named adapters ran would depend on supply order.
            checks.Add(duplicated.Contains(d.Tool.Id)
                ? new VerificationCheck(name, VerificationOutcome.Failed, $"two adapters are named '{d.Tool.Id}'; which one ran would be ambiguous")
                : Rebuild(manifest, d, i, bytesById, adapters, limits, name));
        }
    }

    private static VerificationCheck Rebuild(
        CorpusManifest manifest,
        ManifestDerivation d,
        int index,
        Dictionary<string, byte[]> bytesById,
        Dictionary<string, ICorpusAdapter> adapters,
        CorpusLimits limits,
        string name)
    {
        if (!adapters.TryGetValue(d.Tool.Id, out ICorpusAdapter? adapter))
        {
            return new VerificationCheck(name, VerificationOutcome.Failed, $"no adapter '{d.Tool.Id}' was supplied; the manifest claims this derivation is reproducible");
        }

        if (AdapterRun.AdapterProblem(adapter) is { } problem)
        {
            return new VerificationCheck(name, VerificationOutcome.Failed, problem);
        }

        if (!string.Equals(adapter.Version, d.Tool.Version, StringComparison.Ordinal))
        {
            return new VerificationCheck(name, VerificationOutcome.Failed, $"adapter '{adapter.Id}' is version {adapter.Version}; the derivation was recorded with version {d.Tool.Version}");
        }

        if (d.Inputs.Count != 1 || !bytesById.TryGetValue(d.Inputs[0], out byte[]? input))
        {
            return new VerificationCheck(name, VerificationOutcome.Failed, "the input's bytes are not stored or did not verify, so the derivation cannot be re-run");
        }

        ManifestArtifact? output = manifest.Artifacts.FirstOrDefault(a => a.Id == d.Output);
        if (output is null)
        {
            return new VerificationCheck(name, VerificationOutcome.Failed, $"the output '{d.Output}' is not declared");
        }

        var errors = new List<CorpusError>();
        AdapterResult? result = AdapterRun.Run(
            adapter, d.Inputs[0], input, d.Parameters, limits, d.Output, new HashSet<string>(StringComparer.Ordinal), 0, $"$.derivations[{index}]", errors);
        if (result is null)
        {
            return Failed(name, errors);
        }

        var differences = new List<string>();
        ContentDigest rebuilt = ContentDigest.Compute(result.Canonical);
        if (rebuilt != output.Digest || result.Canonical.LongLength != output.Bytes)
        {
            differences.Add($"output is {result.Canonical.LongLength} bytes with digest {rebuilt}; recorded {output.Bytes} bytes with digest {output.Digest}");
        }

        if (!string.Equals(result.MediaType, output.MediaType, StringComparison.Ordinal))
        {
            differences.Add($"media type is {result.MediaType}; recorded {output.MediaType}");
        }

        if (result.Fidelity != d.Fidelity || !result.Losses.SequenceEqual(d.Losses, StringComparer.Ordinal))
        {
            differences.Add("fidelity or losses differ from the recorded derivation");
        }

        List<ManifestSegment> recorded = manifest.Segments.Where(s => s.Artifact == d.Output).ToList();
        if (recorded.Count != result.Segments.Count)
        {
            differences.Add($"{result.Segments.Count} segments; recorded {recorded.Count}");
        }
        else
        {
            for (int k = 0; k < recorded.Count; k++)
            {
                byte[] expected = CanonicalJsonWriter.ToCompact(ManifestJson.ToJson(recorded[k]));
                byte[] actual = CanonicalJsonWriter.ToCompact(ManifestJson.ToJson(result.Segments[k]));
                if (!expected.AsSpan().SequenceEqual(actual))
                {
                    differences.Add($"segment {k} ('{recorded[k].Id}') differs from the re-derived '{result.Segments[k].Id}'");
                }
            }
        }

        return differences.Count == 0
            ? new VerificationCheck(name, VerificationOutcome.Ok, $"adapter {adapter.Id} {adapter.Version} re-derived byte-identical output and {recorded.Count} identical segment(s)")
            : new VerificationCheck(name, VerificationOutcome.Failed, string.Join("; ", differences));
    }

    private static VerificationCheck Failed(string name, List<CorpusError> errors) =>
        new(name, VerificationOutcome.Failed, errors.Count == 0 ? "failed" : string.Join("; ", errors.Select(e => e.ToString())));

    private static string OkDetail(string check, CorpusManifest m) => check switch
    {
        ManifestValidator.Uniqueness => "artifact, derivation and segment ids and stored paths are unique",
        ManifestValidator.References => "every artifact and derivation reference resolves",
        ManifestValidator.Derivations => $"{m.Derivations.Count} derivation(s) ordered, one per derived artifact, losses consistent with fidelity",
        ManifestValidator.Baselines => $"{m.Baselines.Count} baseline(s), one per source id",
        ManifestValidator.Segments => $"{m.Segments.Count} segment(s) ordered and within their artifacts' declared bytes",
        ManifestValidator.SourceSpans => "every source span lies within its artifact",
        ManifestValidator.Limits => "within limits",
        ManifestValidator.ContentDigestCheck => $"recomputed {m.ContentDigest}",
        ManifestValidator.ManifestDigestCheck => $"recomputed {m.ManifestDigest}",
        _ => "ok",
    };
}
