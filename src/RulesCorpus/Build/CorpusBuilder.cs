using RulesCorpus.Adapters;
using RulesCorpus.Build;
using RulesCorpus.Files;
using RulesCorpus.Internal;
using RulesCorpus.Json;
using RulesCorpus.Manifest;

namespace RulesCorpus;

/// <summary>
/// Builds a corpus: reads <c>corpus.build.json</c>, fingerprints the stored sources, runs each
/// declared adapter derivation, validates everything the adapters claim, and writes the derived
/// artifacts and <c>corpus.json</c>.
///
/// <para>
/// The same directory contents and adapters always produce byte-identical outputs: nothing here
/// reads a clock, the environment, randomness or the network, and every collection is ordered
/// by construction. Everything is computed and validated before the first byte is written, so
/// a refused build leaves the directory as it was.
/// </para>
/// </summary>
public static class CorpusBuilder
{
    /// <summary>Builds the corpus in <paramref name="corpusDirectory"/> and returns the manifest it wrote.</summary>
    /// <param name="corpusDirectory">The corpus directory, holding <c>corpus.build.json</c> and the stored sources.</param>
    /// <param name="adapters">The adapters the build may run, looked up by <see cref="ICorpusAdapter.Id"/>.</param>
    /// <param name="limits">Resource bounds; <see cref="CorpusLimits.Default"/> when null.</param>
    /// <exception cref="ArgumentNullException">An argument or adapter is null.</exception>
    /// <exception cref="CorpusException">
    /// The build was refused: an invalid build definition, a missing, oversized or unsafe source,
    /// an unknown or non-deterministic adapter, an adapter refusal or invalid adapter output, or a
    /// write that would land on a source, a reserved file or outside the directory.
    /// </exception>
    public static CorpusManifest Build(string corpusDirectory, IEnumerable<ICorpusAdapter> adapters, CorpusLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(corpusDirectory);
        ArgumentNullException.ThrowIfNull(adapters);
        limits ??= CorpusLimits.Default;
        var files = new DirectoryCorpusFiles(corpusDirectory);
        var errors = new List<CorpusError>();

        Dictionary<string, ICorpusAdapter> byId = IndexAdapters(adapters, errors);
        Refuse("The adapters supplied are invalid.", errors);

        if (!files.TryRead(Vocabulary.BuildFile, limits.MaxManifestBytes, out byte[] definitionBytes, out string problem))
        {
            errors.Add(new CorpusError(Vocabulary.BuildFile, problem));
        }

        Refuse("The build definition cannot be read.", errors);
        BuildDefinition? definition = BuildDefinition.Read(definitionBytes, errors);
        Refuse("The build definition is invalid.", errors);

        (List<ManifestArtifact> artifacts, Dictionary<string, byte[]> bytesById) = Fingerprint(definition!, files, limits, errors);
        Refuse("The sources cannot be fingerprinted.", errors);

        var derivations = new List<ManifestDerivation>();
        foreach (BuildExternal e in definition!.External)
        {
            derivations.Add(new ManifestDerivation(
                e.Id, e.Inputs, e.Output, e.Tool, e.Parameters, DerivationReproducibility.External, e.Fidelity, e.Losses));
        }

        var segments = new List<ManifestSegment>();
        var segmentIds = new HashSet<string>(StringComparer.Ordinal);
        var outputs = new List<(string Path, byte[] Bytes)>();
        for (int i = 0; i < definition.Derivations.Count; i++)
        {
            BuildDerivation d = definition.Derivations[i];
            string at = $"$.derivations[{i}]";
            if (!byId.TryGetValue(d.Adapter, out ICorpusAdapter? adapter))
            {
                errors.Add(new CorpusError(at + ".adapter", $"no adapter '{d.Adapter}' was supplied"));
                break;
            }

            AdapterResult? result = AdapterRun.Run(
                adapter, d.Input, bytesById[d.Input], d.Parameters, limits, d.OutputId, segmentIds, segments.Count, at, errors);
            if (result is null)
            {
                break;
            }

            artifacts.Add(new ManifestArtifact(
                d.OutputId,
                ArtifactRole.Derived,
                result.MediaType,
                result.Canonical.LongLength,
                ContentDigest.Compute(result.Canonical),
                stored: true,
                d.OutputPath,
                acquisition: null,
                derivedBy: d.Id));
            derivations.Add(new ManifestDerivation(
                d.Id,
                [d.Input],
                d.OutputId,
                new DerivationTool(adapter.Id, adapter.Version),
                d.Parameters,
                DerivationReproducibility.Reproducible,
                result.Fidelity,
                result.Losses));
            foreach (ManifestSegment s in result.Segments)
            {
                segments.Add(s);
                segmentIds.Add(s.Id);
            }

            bytesById.Add(d.OutputId, result.Canonical);
            outputs.Add((d.OutputPath, result.Canonical));
        }

        Refuse("A derivation failed.", errors);

        CorpusManifest manifest = Compose(definition.CorpusId, artifacts, derivations, definition.Baselines, segments);
        byte[] manifestBytes = manifest.ToUtf8Json();

        // The manifest this build is about to write passes the same validation a reader applies.
        var validation = new List<CorpusError>();
        CorpusManifest.ReadAndValidate(manifestBytes, limits, validation, out _);
        Refuse("The manifest this build would write is invalid.", validation);

        // Every write is checked before any is made, so a refusal writes nothing.
        foreach ((string path, _) in outputs)
        {
            if (!files.CanWrite(path, out string writeProblem))
            {
                errors.Add(new CorpusError(path, writeProblem));
            }
        }

        if (!files.CanWrite(Vocabulary.ManifestFile, out string manifestProblem))
        {
            errors.Add(new CorpusError(Vocabulary.ManifestFile, manifestProblem));
        }

        Refuse("The build cannot write its outputs.", errors);

        foreach ((string path, byte[] bytes) in outputs)
        {
            WriteOrThrow(files, path, bytes);
        }

        WriteOrThrow(files, Vocabulary.ManifestFile, manifestBytes);
        return manifest;
    }

    /// <summary>Computes both identities and assembles the manifest.</summary>
    internal static CorpusManifest Compose(
        string corpusId,
        List<ManifestArtifact> artifacts,
        List<ManifestDerivation> derivations,
        List<ManifestBaseline> baselines,
        List<ManifestSegment> segments)
    {
        ContentDigest contentDigest = ManifestJson.ComputeContentDigest(baselines, artifacts, segments)
            ?? throw new CorpusException("A baseline names an artifact that is not declared.");
        CjObject withoutDigest = ManifestJson.ToJson(corpusId, artifacts, derivations, baselines, segments, contentDigest, manifestDigest: null);
        ContentDigest manifestDigest = ManifestJson.ComputeManifestDigest(withoutDigest);
        return new CorpusManifest(corpusId, artifacts, derivations, baselines, segments, contentDigest, manifestDigest);
    }

    private static Dictionary<string, ICorpusAdapter> IndexAdapters(IEnumerable<ICorpusAdapter> adapters, List<CorpusError> errors)
    {
        var byId = new Dictionary<string, ICorpusAdapter>(StringComparer.Ordinal);
        foreach (ICorpusAdapter adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter, nameof(adapters));
            if (AdapterRun.AdapterProblem(adapter) is { } problem)
            {
                errors.Add(new CorpusError("(adapters)", problem));
            }
            else if (!byId.TryAdd(adapter.Id, adapter))
            {
                errors.Add(new CorpusError("(adapters)", $"two adapters are named '{adapter.Id}'; which one ran would be ambiguous"));
            }
        }

        return byId;
    }

    private static (List<ManifestArtifact> Artifacts, Dictionary<string, byte[]> Bytes) Fingerprint(
        BuildDefinition definition, DirectoryCorpusFiles files, CorpusLimits limits, List<CorpusError> errors)
    {
        var externalOutputs = definition.External.ToDictionary(e => e.Output, e => e.Id, StringComparer.Ordinal);
        var artifacts = new List<ManifestArtifact>();
        var bytesById = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < definition.Sources.Count; i++)
        {
            BuildSource s = definition.Sources[i];
            long length;
            ContentDigest digest;
            if (s.Stored)
            {
                if (!files.TryRead(s.Path!, limits.MaxArtifactBytes, out byte[] bytes, out string problem))
                {
                    errors.Add(new CorpusError($"$.sources[{i}].path", problem));
                    continue;
                }

                length = bytes.LongLength;
                digest = ContentDigest.Compute(bytes);
                bytesById.Add(s.Id, bytes);
            }
            else
            {
                length = s.Bytes!.Value;
                digest = s.Digest!;
                if (length > limits.MaxArtifactBytes)
                {
                    errors.Add(new CorpusError($"$.sources[{i}].bytes", $"{length} bytes exceeds the limit of {limits.MaxArtifactBytes}"));
                }
            }

            bool external = externalOutputs.TryGetValue(s.Id, out string? derivedBy);
            artifacts.Add(new ManifestArtifact(
                s.Id,
                external ? ArtifactRole.Derived : ArtifactRole.Source,
                s.MediaType,
                length,
                digest,
                s.Stored,
                s.Path,
                external ? null : new ArtifactAcquisition(s.Origin!, s.Retrieved, s.Notes),
                derivedBy));
        }

        return (artifacts, bytesById);
    }

    private static void WriteOrThrow(DirectoryCorpusFiles files, string path, byte[] bytes)
    {
        if (!files.TryWrite(path, bytes, out string problem))
        {
            throw new CorpusException("The build could not write its outputs.", [new CorpusError(path, problem)]);
        }
    }

    private static void Refuse(string message, List<CorpusError> errors)
    {
        if (errors.Count != 0)
        {
            throw new CorpusException(message, errors);
        }
    }
}
