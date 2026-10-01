using RulesCorpus.Adapters;
using RulesCorpus.Adapters.Text;
using RulesCorpus.Adapters.Xml;
using RulesCorpus.Files;
using RulesCorpus.Internal;
using RulesCorpus.Json;
using RulesCorpus.Manifest;

namespace RulesCorpus.Cli;

/// <summary>One method per command. Each returns its exit code or throws a usage or refusal exception.</summary>
internal static class Commands
{
    private static readonly CommandSpec InitSpec = new("init", 1, 1, ["--corpus-id"], ["--corpus-id"], []);

    private static readonly CommandSpec ImportSpec = new(
        "import", 1, 1,
        ["--dir", "--id", "--media-type", "--origin", "--retrieved", "--path"],
        ["--dir", "--id", "--media-type", "--origin"],
        []);

    private static readonly CommandSpec BuildSpec = new("build", 0, 0, ["--dir"], [], []);
    private static readonly CommandSpec VerifySpec = new("verify", 0, 1, ["--expect-not-verified"], [], ["--rebuild", "--allow-not-verified"]);
    private static readonly CommandSpec InspectSpec = new("inspect", 1, 1, ["--corpus"], [], []);
    private static readonly CommandSpec DiffSpec = new("diff", 2, 2, [], [], []);
    private static readonly CommandSpec PackSpec = new("pack", 1, 1, ["--dir"], [], ["--allow-not-verified"]);

    /// <summary>The options of <paramref name="command"/> that take the next token as their value; none for an unknown command.</summary>
    public static IReadOnlyList<string> ValueOptionsOf(string command) => command switch
    {
        "init" => InitSpec.ValueOptions,
        "import" => ImportSpec.ValueOptions,
        "build" => BuildSpec.ValueOptions,
        "verify" => VerifySpec.ValueOptions,
        "inspect" => InspectSpec.ValueOptions,
        "diff" => DiffSpec.ValueOptions,
        "pack" => PackSpec.ValueOptions,
        _ => [],
    };

    /// <summary>The adapters this tool ships, for build and rebuild.</summary>
    private static ICorpusAdapter[] Adapters => [new TextAdapter(), new XmlAdapter()];

    public static int Init(Context context, IReadOnlyList<string> tokens)
    {
        Arguments args = Arguments.Parse(tokens, InitSpec);
        string corpusId = args.Option("--corpus-id")!;
        if (!CorpusGrammar.IsId(corpusId))
        {
            throw new UsageException($"init: --corpus-id '{corpusId}' is not a corpus id ([a-z0-9]+([-.][a-z0-9]+)*, at most 128 characters)");
        }

        string directory = context.Resolve(args.Positionals[0]);
        string file = Path.Combine(directory, Vocabulary.BuildFile);
        if (File.Exists(file) || Directory.Exists(file))
        {
            throw new RefusalException($"'{file}' already exists; init never overwrites a build definition");
        }

        var skeleton = new CjObject()
            .Add("schema", CjValue.Of(Vocabulary.BuildSchema))
            .Add("corpusId", CjValue.Of(corpusId))
            .Add("sources", new CjArray())
            .Add("derivations", new CjArray())
            .Add("external", new CjArray())
            .Add("baselines", new CjArray());

        Directory.CreateDirectory(directory);
        using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(CanonicalJsonWriter.ToIndented(skeleton));
        }

        context.Output.Result(
            new CjObject()
                .Add("command", CjValue.Of("init"))
                .Add("corpusId", CjValue.Of(corpusId))
                .Add("path", CjValue.Of(file)),
            () =>
            {
                context.Output.Line($"created {file} for corpus '{corpusId}'");
                context.Output.Line("next: add a source with 'rules-corpus import'; build refuses a definition with no sources");
            });
        return Cli.ExitOk;
    }

    public static int Import(Context context, IReadOnlyList<string> tokens)
    {
        Arguments args = Arguments.Parse(tokens, ImportSpec);
        string id = args.Option("--id")!;
        string mediaType = args.Option("--media-type")!;
        string origin = args.Option("--origin")!;
        string? retrieved = args.Option("--retrieved");
        string sourceFile = context.Resolve(args.Positionals[0]);
        string path = args.Option("--path") ?? "sources/" + Path.GetFileName(sourceFile);

        if (!CorpusGrammar.IsId(id))
        {
            throw new UsageException($"import: --id '{id}' is not an artifact id ([a-z0-9]+([-.][a-z0-9]+)*, at most 128 characters)");
        }

        if (!CorpusGrammar.IsMediaType(mediaType))
        {
            throw new UsageException($"import: --media-type '{mediaType}' is not a media type (type/subtype, lowercase ASCII, no parameters)");
        }

        if (origin.Length == 0)
        {
            throw new UsageException("import: --origin must say where the bytes came from");
        }

        if (retrieved is not null && !CorpusGrammar.IsDate(retrieved))
        {
            throw new UsageException($"import: --retrieved '{retrieved}' is not a date (YYYY-MM-DD, a real calendar date)");
        }

        if (CorpusPaths.Problem(path) is { } pathProblem)
        {
            throw new UsageException($"import: the artifact path '{path}' {pathProblem}");
        }

        var files = new DirectoryCorpusFiles(context.Resolve(args.Option("--dir")!));
        CjObject definition = ReadDefinitionForEdit(files, out CjArray sources);
        CheckNewSource(definition, id, path);

        if (!files.CanWrite(path, out string writeProblem))
        {
            throw new RefusalException($"import cannot write '{path}'", [(path, writeProblem)]);
        }

        if (File.Exists(Path.Combine(files.Root, path)))
        {
            throw new RefusalException($"'{path}' already exists in the corpus; import never overwrites a file");
        }

        if (!File.Exists(sourceFile))
        {
            throw new RefusalException($"'{sourceFile}' does not exist or is not a file");
        }

        long length = new FileInfo(sourceFile).Length;
        if (length > CorpusLimits.Default.MaxArtifactBytes)
        {
            throw new RefusalException($"'{sourceFile}' is {length} bytes; the limit is {CorpusLimits.Default.MaxArtifactBytes}");
        }

        byte[] bytes = File.ReadAllBytes(sourceFile);
        var source = new CjObject()
            .Add("id", CjValue.Of(id))
            .Add("mediaType", CjValue.Of(mediaType))
            .Add("origin", CjValue.Of(origin))
            .Add("path", CjValue.Of(path))
            .AddOptional("retrieved", retrieved is null ? null : CjValue.Of(retrieved));
        sources.Items.Add(source);

        if (!files.TryWrite(path, bytes, out string problem))
        {
            throw new RefusalException($"import cannot write '{path}'", [(path, problem)]);
        }

        if (!files.TryWrite(Vocabulary.BuildFile, CanonicalJsonWriter.ToIndented(definition), out problem))
        {
            File.Delete(Path.Combine(files.Root, path));
            throw new RefusalException($"import cannot rewrite {Vocabulary.BuildFile}", [(Vocabulary.BuildFile, problem)]);
        }

        ContentDigest digest = ContentDigest.Compute(bytes);
        context.Output.Result(
            new CjObject()
                .Add("command", CjValue.Of("import"))
                .Add("source", new CjObject()
                    .Add("id", CjValue.Of(id))
                    .Add("mediaType", CjValue.Of(mediaType))
                    .Add("origin", CjValue.Of(origin))
                    .Add("path", CjValue.Of(path))
                    .AddOptional("retrieved", retrieved is null ? null : CjValue.Of(retrieved))
                    .Add("bytes", CjValue.Of(bytes.LongLength))
                    .Add("digest", CjValue.Of(digest.ToString()))),
            () => context.Output.Line($"imported '{id}' as {path}: {bytes.LongLength} bytes, {digest}"));
        return Cli.ExitOk;
    }

    public static int Build(Context context, IReadOnlyList<string> tokens)
    {
        Arguments args = Arguments.Parse(tokens, BuildSpec);
        string directory = context.Resolve(args.Option("--dir") ?? ".");
        CorpusManifest manifest = CorpusBuilder.Build(directory, Adapters);

        var artifacts = new CjArray(manifest.Artifacts.Select(a => (CjValue)new CjObject()
            .Add("id", CjValue.Of(a.Id))
            .Add("role", CjValue.Of(Vocabulary.Write(a.Role)))
            .Add("bytes", CjValue.Of(a.Bytes))
            .Add("digest", CjValue.Of(a.Digest.ToString()))
            .AddOptional("path", a.Path is null ? null : CjValue.Of(a.Path))));
        context.Output.Result(
            new CjObject()
                .Add("command", CjValue.Of("build"))
                .Add("corpusId", CjValue.Of(manifest.CorpusId))
                .Add("contentDigest", CjValue.Of(manifest.ContentDigest.ToString()))
                .Add("manifestDigest", CjValue.Of(manifest.ManifestDigest.ToString()))
                .Add("artifacts", artifacts)
                .Add("segments", CjValue.Of(manifest.Segments.Count)),
            () =>
            {
                context.Output.Line($"built corpus '{manifest.CorpusId}' in {directory}: {manifest.Artifacts.Count} artifact(s), {manifest.Segments.Count} segment(s)");
                context.Output.Line($"contentDigest   {manifest.ContentDigest}");
                context.Output.Line($"manifestDigest  {manifest.ManifestDigest}");
            });
        return Cli.ExitOk;
    }

    public static int Verify(Context context, IReadOnlyList<string> tokens)
    {
        Arguments args = Arguments.Parse(tokens, VerifySpec);
        string target = context.Resolve(args.Positionals.Count == 0 ? "." : args.Positionals[0]);
        bool allow = args.Switch("--allow-not-verified");
        string[]? expected = ExpectedNotVerified(args, allow);
        CorpusFiles files = Open(target);
        VerificationReport report = CorpusVerifier.Verify(files, new VerificationOptions
        {
            Rebuild = args.Switch("--rebuild"),
            Adapters = Adapters,
        });

        int exit = ExitFor(report, allow);
        Expectation? expectation = null;
        if (expected is not null)
        {
            expectation = Expectation.Of(expected, report);
            if (report.Outcome != VerificationOutcome.Failed)
            {
                exit = expectation.Met ? Cli.ExitOk : Cli.ExitFailed;
            }
        }

        context.Output.Result(
            ReportJson("verify", report, exit)
                .Add("target", CjValue.Of(target))
                .AddOptional("expectedNotVerified", expectation?.ToJson()),
            () =>
            {
                foreach (VerificationCheck check in report.Checks)
                {
                    context.Output.Line($"{Word(check.Outcome),-12}  {check.Name}: {check.Detail}");
                }

                context.Output.Line($"verify {target}: {Summary(report)}");
                if (expectation is not null)
                {
                    context.Output.Line($"expected not verified: {expectation.Describe()}");
                }
            });
        NoteNotVerified(context, report, exit);
        return exit;
    }

    /// <summary>
    /// The check names <c>--expect-not-verified</c> pins, comma-separated and in the caller's
    /// order. Check names are built from ids, which never contain a comma.
    /// </summary>
    private static string[]? ExpectedNotVerified(Arguments args, bool allow)
    {
        string? value = args.Option("--expect-not-verified");
        if (value is null)
        {
            return null;
        }

        if (allow)
        {
            throw new UsageException("verify: --expect-not-verified and --allow-not-verified cannot be combined; the first accepts exactly the named checks, the second accepts any");
        }

        string[] names = value.Split(',');
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in names)
        {
            if (name.Length == 0 || name.Trim() != name)
            {
                throw new UsageException($"verify: --expect-not-verified '{value}' has an empty or space-padded check name; give names exactly as verify prints them, separated by commas");
            }

            if (!seen.Add(name))
            {
                throw new UsageException($"verify: --expect-not-verified names '{name}' more than once");
            }
        }

        return names;
    }

    /// <summary>
    /// The comparison of a pinned set of not-verified checks with a report's. It is met only
    /// when the two sets are equal: an extra not-verified check is evidence the consumer never
    /// agreed to leave unchecked, and a pinned check that is no longer not verified means the
    /// corpus is not the one the pin describes.
    /// </summary>
    private sealed record Expectation(string[] Expected, string[] Unexpected, string[] Missing)
    {
        public bool Met => Unexpected.Length == 0 && Missing.Length == 0;

        public static Expectation Of(string[] expected, VerificationReport report)
        {
            string[] actual = [.. report.Checks.Where(c => c.Outcome == VerificationOutcome.NotVerified).Select(c => c.Name)];
            return new Expectation(
                expected,
                [.. actual.Where(n => !expected.Contains(n, StringComparer.Ordinal))],
                [.. expected.Where(n => !actual.Contains(n, StringComparer.Ordinal))]);
        }

        public CjObject ToJson() => new CjObject()
            .Add("expected", Strings(Expected))
            .Add("unexpected", Strings(Unexpected))
            .Add("missing", Strings(Missing))
            .Add("met", CjValue.Of(Met));

        public string Describe()
        {
            if (Met)
            {
                return $"met ({Expected.Length} named, and no others)";
            }

            var parts = new List<string>();
            if (Unexpected.Length > 0)
            {
                parts.Add($"not verified but not expected: {string.Join(", ", Unexpected)}");
            }

            if (Missing.Length > 0)
            {
                parts.Add($"expected but not reported not verified: {string.Join(", ", Missing)}");
            }

            return "NOT met; " + string.Join("; ", parts);
        }

        private static CjArray Strings(string[] values) => new(values.Select(v => (CjValue)CjValue.Of(v)));
    }

    public static int Inspect(Context context, IReadOnlyList<string> tokens)
    {
        Arguments args = Arguments.Parse(tokens, InspectSpec);
        string segmentId = args.Positionals[0];
        CorpusFiles files = Open(context.Resolve(args.Option("--corpus") ?? "."));
        CorpusManifest manifest = CorpusManifest.Load(files);
        ManifestSegment segment = manifest.Segments.FirstOrDefault(s => s.Id == segmentId)
            ?? throw new RefusalException($"corpus '{manifest.CorpusId}' has no segment '{segmentId}'");

        // A validated manifest names a stored artifact for every segment; the bytes are still
        // checked here, so inspect never prints text that does not match its digest.
        ManifestArtifact artifact = manifest.Artifacts.First(a => a.Id == segment.Artifact);
        if (!files.TryRead(artifact.Path!, CorpusLimits.Default.MaxArtifactBytes, out byte[] bytes, out string problem))
        {
            throw new RefusalException($"the bytes of segment '{segmentId}' cannot be read", [(artifact.Path!, problem)]);
        }

        if (ContentDigest.Compute(bytes) != artifact.Digest || segment.Start > bytes.LongLength - segment.Length)
        {
            throw new RefusalException($"'{artifact.Path}' does not match the manifest; run verify");
        }

        ReadOnlySpan<byte> span = bytes.AsSpan((int)segment.Start, (int)segment.Length);
        if (ContentDigest.Compute(span) != segment.Digest)
        {
            throw new RefusalException($"the bytes of segment '{segmentId}' do not match its digest; run verify");
        }

        string text = Cli.DecodeUtf8(span);
        context.Output.Result(
            new CjObject()
                .Add("command", CjValue.Of("inspect"))
                .Add("segment", ManifestJson.ToJson(segment))
                .Add("text", CjValue.Of(text)),
            () =>
            {
                Output o = context.Output;
                o.Line($"segment   {segment.Id}");
                o.Line($"artifact  {segment.Artifact}");
                o.Line($"start     {segment.Start}");
                o.Line($"length    {segment.Length}");
                o.Line($"digest    {segment.Digest}");
                if (segment.Locator is { } locator)
                {
                    o.Line($"locator   {locator}");
                }

                foreach (ManifestSourceSpan source in segment.Sources)
                {
                    o.Line($"source    {Describe(source)}");
                }

                o.Line();
                o.Line(text);
            });
        return Cli.ExitOk;
    }

    public static int Diff(Context context, IReadOnlyList<string> tokens)
    {
        Arguments args = Arguments.Parse(tokens, DiffSpec);
        string pathA = context.Resolve(args.Positionals[0]);
        string pathB = context.Resolve(args.Positionals[1]);
        CorpusManifest a = CorpusManifest.Load(Open(pathA));
        CorpusManifest b = CorpusManifest.Load(Open(pathB));
        ManifestDiff d = ManifestDiff.Compare(a, b);

        CjObject Side(string path, CorpusManifest m) => new CjObject()
            .Add("path", CjValue.Of(path))
            .Add("corpusId", CjValue.Of(m.CorpusId))
            .Add("contentDigest", CjValue.Of(m.ContentDigest.ToString()))
            .Add("manifestDigest", CjValue.Of(m.ManifestDigest.ToString()));

        CjObject Changes<T>(IReadOnlyList<T> added, IReadOnlyList<T> removed, IReadOnlyList<ManifestChange<T>> changed, Func<T, CjObject> json)
            where T : class => new CjObject()
            .Add("added", new CjArray(added.Select(x => (CjValue)json(x))))
            .Add("removed", new CjArray(removed.Select(x => (CjValue)json(x))))
            .Add("changed", new CjArray(changed.Select(c => (CjValue)new CjObject()
                .Add("before", json(c.Before))
                .Add("after", json(c.After)))));

        context.Output.Result(
            new CjObject()
                .Add("command", CjValue.Of("diff"))
                .Add("a", Side(pathA, a))
                .Add("b", Side(pathB, b))
                .Add("contentDigestEqual", CjValue.Of(d.ContentDigestEqual))
                .Add("manifestDigestEqual", CjValue.Of(d.ManifestDigestEqual))
                .Add("artifacts", Changes(d.ArtifactsAdded, d.ArtifactsRemoved, d.ArtifactsChanged, ManifestJson.ToJson))
                .Add("derivations", Changes(d.DerivationsAdded, d.DerivationsRemoved, d.DerivationsChanged, ManifestJson.ToJson))
                .Add("baselines", Changes(d.BaselinesAdded, d.BaselinesRemoved, d.BaselinesChanged, ManifestJson.ToJson))
                .Add("segments", Changes(d.SegmentsAdded, d.SegmentsRemoved, d.SegmentsChanged, ManifestJson.ToJson)),
            () => RenderDiff(context.Output, a, b, d));
        return Cli.ExitOk;
    }

    public static int Pack(Context context, IReadOnlyList<string> tokens)
    {
        Arguments args = Arguments.Parse(tokens, PackSpec);
        string destination = context.Resolve(args.Positionals[0]);
        string directory = context.Resolve(args.Option("--dir") ?? ".");
        bool allow = args.Switch("--allow-not-verified");
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new RefusalException($"'{destination}' already exists; pack never overwrites a file");
        }

        // Verified here as well as inside the packer, so a refusal can say which checks and
        // exit 3 rather than 1 when nothing failed but something was not verified.
        VerificationReport report = CorpusVerifier.Verify(CorpusFiles.FromDirectory(directory), new VerificationOptions());
        int exit = ExitFor(report, allow);
        if (exit != Cli.ExitOk)
        {
            List<(string, string)> problems = report.Checks
                .Where(c => c.Outcome != VerificationOutcome.Ok)
                .Select(c => (c.Name, $"{Word(c.Outcome)}: {c.Detail}"))
                .ToList();
            string message = exit == Cli.ExitNotVerified
                ? $"not packed: {Summary(report)}; pass --allow-not-verified to pack anyway"
                : $"not packed: the corpus failed verification ({Summary(report)})";
            context.Output.Error(exit, message + "\n" + string.Join("\n", problems.Select(p => $"  {p.Item1}: {p.Item2}")), message, problems);
            return exit;
        }

        using var archive = new MemoryStream();
        CorpusPacker.Pack(directory, archive, new VerificationOptions(), allow);
        byte[] bytes = archive.ToArray();
        AtomicFile.CreateNew(destination, stream => stream.Write(bytes));

        ContentDigest digest = ContentDigest.Compute(bytes);
        context.Output.Result(
            ReportJson("pack", report, exit)
                .Add("path", CjValue.Of(destination))
                .Add("bytes", CjValue.Of(bytes.LongLength))
                .Add("digest", CjValue.Of(digest.ToString())),
            () =>
            {
                context.Output.Line($"packed {directory} to {destination}: {bytes.LongLength} bytes, {digest}");
                context.Output.Line($"verification: {Summary(report)}");
                foreach (VerificationCheck check in report.Checks.Where(c => c.Outcome != VerificationOutcome.Ok))
                {
                    context.Output.Line($"{Word(check.Outcome),-12}  {check.Name}: {check.Detail}");
                }
            });
        return Cli.ExitOk;
    }

    /// <summary>A directory, or a packed corpus file (decision 0006: either is accepted).</summary>
    private static CorpusFiles Open(string target)
    {
        if (File.Exists(target))
        {
            using FileStream stream = File.OpenRead(target);
            return CorpusFiles.FromPacked(stream);
        }

        if (Directory.Exists(target))
        {
            return CorpusFiles.FromDirectory(target);
        }

        throw new RefusalException($"'{target}' is neither a corpus directory nor a packed corpus file");
    }

    /// <summary>Reads corpus.build.json as a canonical value that import can append to.</summary>
    private static CjObject ReadDefinitionForEdit(DirectoryCorpusFiles files, out CjArray sources)
    {
        if (!files.TryRead(Vocabulary.BuildFile, CorpusLimits.Default.MaxManifestBytes, out byte[] bytes, out string problem))
        {
            throw new RefusalException($"the corpus has no readable {Vocabulary.BuildFile}; create one with 'rules-corpus init'", [(Vocabulary.BuildFile, problem)]);
        }

        var errors = new List<CorpusError>();
        if (CanonicalJsonReader.Parse(bytes, errors) is not CjObject root)
        {
            throw new RefusalException(
                $"{Vocabulary.BuildFile} is not a JSON object",
                errors.Select(e => (e.Path, e.Reason)).ToList());
        }

        if (!root.Members.TryGetValue("sources", out CjValue? value) || value is not CjArray array)
        {
            throw new RefusalException($"{Vocabulary.BuildFile} has no sources array", [("$.sources", "must be an array")]);
        }

        sources = array;
        return root;
    }

    /// <summary>
    /// Refuses an id or path the definition already uses. Only what import adds is checked;
    /// the rest of the definition is build's to validate.
    /// </summary>
    private static void CheckNewSource(CjObject definition, string id, string path)
    {
        var problems = new List<(string, string)>();
        foreach ((string where, CjValue value) in Members(definition))
        {
            if (value is not CjString s)
            {
                continue;
            }

            if (where.EndsWith(".id", StringComparison.Ordinal) && s.Value == id)
            {
                problems.Add((where, $"already uses the id '{id}'"));
            }

            if (where.EndsWith(".path", StringComparison.Ordinal) && Collide(s.Value, path))
            {
                problems.Add((where, $"'{s.Value}' collides with '{path}' (the same file ignoring case, or one inside the other)"));
            }
        }

        if (problems.Count != 0)
        {
            throw new RefusalException($"import refuses '{id}' at '{path}'", problems);
        }
    }

    /// <summary>The id and path members of sources, derivations (and their outputs) and external derivations.</summary>
    private static IEnumerable<(string Where, CjValue Value)> Members(CjObject definition)
    {
        foreach (string list in new[] { "sources", "derivations", "external" })
        {
            if (!definition.Members.TryGetValue(list, out CjValue? value) || value is not CjArray array)
            {
                continue;
            }

            for (int i = 0; i < array.Items.Count; i++)
            {
                if (array.Items[i] is not CjObject item)
                {
                    continue;
                }

                string at = $"$.{list}[{i}]";
                foreach (string key in new[] { "id", "path" })
                {
                    if (item.Members.TryGetValue(key, out CjValue? member))
                    {
                        yield return ($"{at}.{key}", member);
                    }
                }

                if (item.Members.TryGetValue("output", out CjValue? output) && output is CjObject outputObject)
                {
                    foreach (string key in new[] { "id", "path" })
                    {
                        if (outputObject.Members.TryGetValue(key, out CjValue? member))
                        {
                            yield return ($"{at}.output.{key}", member);
                        }
                    }
                }
            }
        }
    }

    private static int ExitFor(VerificationReport report, bool allowNotVerified) => report.Outcome switch
    {
        VerificationOutcome.Ok => Cli.ExitOk,
        VerificationOutcome.NotVerified => allowNotVerified ? Cli.ExitOk : Cli.ExitNotVerified,
        _ => Cli.ExitFailed,
    };

    private static void NoteNotVerified(Context context, VerificationReport report, int exit)
    {
        if (exit == Cli.ExitNotVerified)
        {
            context.Output.Note("rules-corpus: some checks were not verified (exit 3); pass --expect-not-verified with their names to accept exactly those, or --allow-not-verified to accept any");
        }
    }

    private static CjObject ReportJson(string command, VerificationReport report, int exit) =>
        new CjObject()
            .Add("command", CjValue.Of(command))
            .Add("outcome", CjValue.Of(Word(report.Outcome)))
            .Add("exitCode", CjValue.Of(exit))
            .Add("counts", new CjObject()
                .Add("ok", CjValue.Of(Count(report, VerificationOutcome.Ok)))
                .Add("notVerified", CjValue.Of(Count(report, VerificationOutcome.NotVerified)))
                .Add("failed", CjValue.Of(Count(report, VerificationOutcome.Failed))))
            .Add("checks", new CjArray(report.Checks.Select(c => (CjValue)new CjObject()
                .Add("name", CjValue.Of(c.Name))
                .Add("outcome", CjValue.Of(Word(c.Outcome)))
                .Add("detail", CjValue.Of(c.Detail)))));

    private static long Count(VerificationReport report, VerificationOutcome outcome) =>
        report.Checks.Count(c => c.Outcome == outcome);

    private static string Summary(VerificationReport report) =>
        $"{Word(report.Outcome)} ({report.Checks.Count} checks: {Count(report, VerificationOutcome.Ok)} ok, "
        + $"{Count(report, VerificationOutcome.NotVerified)} not verified, {Count(report, VerificationOutcome.Failed)} failed)";

    private static string Word(VerificationOutcome outcome) => outcome switch
    {
        VerificationOutcome.Ok => "ok",
        VerificationOutcome.NotVerified => "not-verified",
        _ => "failed",
    };

    private static string Describe(ManifestSourceSpan span)
    {
        var parts = new List<string> { span.Artifact };
        if (span.Pages is { } pages)
        {
            parts.Add(pages.From == pages.To ? $"page {pages.From}" : $"pages {pages.From}-{pages.To}");
        }

        if (span.Bytes is { } range)
        {
            parts.Add($"bytes [{range.Start}, {range.Start + range.Length})");
        }

        return string.Join(" ", parts);
    }

    private static void RenderDiff(Output o, CorpusManifest a, CorpusManifest b, ManifestDiff d)
    {
        o.Line(d.ContentDigestEqual
            ? $"contentDigest   equal      {a.ContentDigest}"
            : $"contentDigest   DIFFERENT  {a.ContentDigest} -> {b.ContentDigest}");
        o.Line(d.ManifestDigestEqual
            ? $"manifestDigest  equal      {a.ManifestDigest}"
            : $"manifestDigest  DIFFERENT  {a.ManifestDigest} -> {b.ManifestDigest}");

        var lines = new List<string>();
        lines.AddRange(d.ArtifactsAdded.Select(x => $"+ artifact {x.Id}"));
        lines.AddRange(d.ArtifactsRemoved.Select(x => $"- artifact {x.Id}"));
        lines.AddRange(d.ArtifactsChanged.Select(x => $"~ artifact {x.After.Id}"));
        lines.AddRange(d.DerivationsAdded.Select(x => $"+ derivation {x.Id}"));
        lines.AddRange(d.DerivationsRemoved.Select(x => $"- derivation {x.Id}"));
        lines.AddRange(d.DerivationsChanged.Select(x => $"~ derivation {x.After.Id}"));
        lines.AddRange(d.BaselinesAdded.Select(x => $"+ baseline {x.SourceId}"));
        lines.AddRange(d.BaselinesRemoved.Select(x => $"- baseline {x.SourceId}"));
        lines.AddRange(d.BaselinesChanged.Select(x => $"~ baseline {x.After.SourceId}"));
        lines.AddRange(d.SegmentsAdded.Select(x => $"+ segment {x.Id}"));
        lines.AddRange(d.SegmentsRemoved.Select(x => $"- segment {x.Id}"));
        lines.AddRange(d.SegmentsChanged.Select(x => $"~ segment {x.After.Id}"));
        foreach (string line in lines)
        {
            o.Line(line);
        }

        if (lines.Count == 0)
        {
            o.Line(d.ManifestDigestEqual
                ? "no differences"
                : "no artifact, derivation, baseline or segment differs; the difference is elsewhere in the manifest (corpus id or build definition)");
        }
    }

    private static bool Collide(string existing, string candidate)
    {
        var collisions = new PathCollisions();
        collisions.Add(existing, "existing");
        return collisions.Add(candidate, "import") is not null;
    }
}
