using System.Text;
using System.Text.Json.Nodes;
using RulesCorpus.Adapters;
using RulesCorpus.Tests.Support;

namespace RulesCorpus.Tests;

public class BuilderDeterminismTests
{
    [Fact]
    public void Building_twice_in_one_directory_is_byte_identical()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();
        SortedDictionary<string, byte[]> first = corpus.Snapshot();

        corpus.Build();

        Assert.Equal(first, corpus.Snapshot());
    }

    [Fact]
    public void Building_the_same_inputs_in_two_directories_is_byte_identical()
    {
        using TempCorpus a = TempCorpus.WithExternal();
        using TempCorpus b = TempCorpus.WithExternal();

        a.Build();
        b.Build();

        Assert.Equal(a.Snapshot(), b.Snapshot());
    }

    [Fact]
    public void The_manifest_written_is_the_one_returned_and_parses()
    {
        using TempCorpus corpus = TempCorpus.Stored();

        CorpusManifest built = corpus.Build();

        Assert.Equal(built.ToUtf8Json(), corpus.ReadBytes("corpus.json"));
        Assert.Equal(built.ManifestDigest, CorpusManifest.Parse(corpus.ReadBytes("corpus.json")).ManifestDigest);
    }

    [Fact]
    public void The_derived_artifact_holds_the_adapters_canonical_bytes_and_sources_are_untouched()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        byte[] source = corpus.ReadBytes("sources/notes.txt");

        corpus.Build();

        Assert.Equal(source, corpus.ReadBytes("sources/notes.txt"));
        Assert.Equal(source, corpus.ReadBytes("canonical/notes.txt"));
    }

    [Fact]
    public void The_written_manifest_is_the_documented_on_disk_form()
    {
        using var corpus = new TempCorpus();
        corpus.WriteText("s.txt", "a\nb");
        corpus.WriteBuild(new JsonObject
        {
            ["schema"] = "rules-corpus/build/1",
            ["corpusId"] = "tiny",
            ["sources"] = new JsonArray(new JsonObject { ["id"] = "s", ["path"] = "s.txt", ["mediaType"] = "text/plain", ["origin"] = "me" }),
            ["derivations"] = new JsonArray(new JsonObject
            {
                ["id"] = "d",
                ["adapter"] = "lines",
                ["input"] = "s",
                ["output"] = new JsonObject { ["id"] = "c", ["path"] = "c.txt" },
                ["parameters"] = new JsonObject(),
            }),
            ["external"] = new JsonArray(),
            ["baselines"] = new JsonArray(),
        });

        CorpusManifest m = corpus.Build();

        string digestAB = ContentDigest.Compute("a\nb"u8).ToString();
        string digestA = ContentDigest.Compute("a"u8).ToString();
        string digestB = ContentDigest.Compute("b"u8).ToString();
        string expected = $$"""
            {
              "artifacts": [
                {
                  "acquisition": {
                    "origin": "me"
                  },
                  "bytes": 3,
                  "digest": "{{digestAB}}",
                  "id": "s",
                  "mediaType": "text/plain",
                  "path": "s.txt",
                  "role": "source",
                  "stored": true
                },
                {
                  "bytes": 3,
                  "derivedBy": "d",
                  "digest": "{{digestAB}}",
                  "id": "c",
                  "mediaType": "text/plain",
                  "path": "c.txt",
                  "role": "derived",
                  "stored": true
                }
              ],
              "baselines": [],
              "contentDigest": "{{m.ContentDigest}}",
              "corpusId": "tiny",
              "derivations": [
                {
                  "fidelity": "lossless",
                  "id": "d",
                  "inputs": [
                    "s"
                  ],
                  "losses": [],
                  "output": "c",
                  "parameters": {},
                  "reproducibility": "reproducible",
                  "tool": {
                    "id": "lines",
                    "version": "1"
                  }
                }
              ],
              "manifestDigest": "{{m.ManifestDigest}}",
              "schema": "rules-corpus/manifest/1",
              "segments": [
                {
                  "artifact": "c",
                  "digest": "{{digestA}}",
                  "id": "l1",
                  "length": 1,
                  "locator": "line 1",
                  "sources": [
                    {
                      "artifact": "s",
                      "length": 1,
                      "start": 0
                    }
                  ],
                  "start": 0
                },
                {
                  "artifact": "c",
                  "digest": "{{digestB}}",
                  "id": "l2",
                  "length": 1,
                  "locator": "line 2",
                  "sources": [
                    {
                      "artifact": "s",
                      "length": 1,
                      "start": 2
                    }
                  ],
                  "start": 2
                }
              ]
            }

            """;

        Assert.Equal(expected, Encoding.UTF8.GetString(corpus.ReadBytes("corpus.json")));
        Assert.Equal(
            ContentDigest.Compute(Encoding.UTF8.GetBytes(
                $"{{\"baselines\":[],\"segments\":[{{\"artifact\":\"c\",\"digest\":\"{digestA}\",\"id\":\"l1\",\"length\":1,\"start\":0}},{{\"artifact\":\"c\",\"digest\":\"{digestB}\",\"id\":\"l2\",\"length\":1,\"start\":2}}]}}")),
            m.ContentDigest);
    }

    [Fact]
    public void A_chained_derivation_runs_over_an_earlier_output()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "notes-again",
            ["adapter"] = "lines",
            ["input"] = "notes-canonical",
            ["output"] = new JsonObject { ["id"] = "notes-again-out", ["path"] = "canonical/again.txt" },
            ["parameters"] = new JsonObject { ["prefix"] = "x" },
        }));

        CorpusManifest m = corpus.Build();

        Assert.Equal(["notes", "notes-canonical", "notes-again-out"], m.Artifacts.Select(a => a.Id));
        Assert.Equal(["l1", "l2", "l3", "xl1", "xl2", "xl3"], m.Segments.Select(s => s.Id));
        Assert.Equal("notes-canonical", m.Segments[3].Sources[0].Artifact);
    }
}

public class BuilderRefusalTests
{
    private static CorpusException Refused(TempCorpus corpus, params ICorpusAdapter[] adapters) =>
        Assert.Throws<CorpusException>(() => corpus.Build(adapters));

    private static void AssertNothingWritten(TempCorpus corpus)
    {
        Assert.False(corpus.Exists("corpus.json"));
        Assert.False(corpus.Exists("canonical/notes.txt"));
    }

    [Fact]
    public void An_output_path_equal_to_a_source_path_is_refused_and_the_source_is_untouched()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        byte[] source = corpus.ReadBytes("sources/notes.txt");
        corpus.EditBuild(d => d["derivations"]![0]!["output"]!["path"] = "sources/notes.txt");

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.derivations[0].output.path", "never writes to a source path");
        Assert.Equal(source, corpus.ReadBytes("sources/notes.txt"));
        Assert.False(corpus.Exists("corpus.json"));
    }

    [Theory]
    [InlineData("SOURCES/NOTES.TXT")]
    [InlineData("sources/notes.txt/inner")]
    [InlineData("sources")]
    public void An_output_path_that_would_alias_a_source_on_some_system_is_refused(string path)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]![0]!["output"]!["path"] = path);

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.derivations[0].output.path", "collides");
    }

    [Theory]
    [InlineData("corpus.json")]
    [InlineData("corpus.build.json")]
    [InlineData("Corpus.Json")]
    [InlineData("corpus.json/x")]
    public void An_output_path_on_a_reserved_file_is_refused(string path)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]![0]!["output"]!["path"] = path);

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.derivations[0].output.path", "reserved name");
    }

    [Theory]
    [InlineData("../outside.txt", "'..' component")]
    [InlineData("sources/../../outside.txt", "'..' component")]
    [InlineData("/etc/passwd", "absolute")]
    [InlineData("./sources/notes.txt", "'.' component")]
    [InlineData("sources//notes.txt", "empty component")]
    [InlineData("sources/notes.txt/", "empty component")]
    [InlineData("sources\\notes.txt", "contains '\\'")]
    [InlineData("C:/notes.txt", "contains ':'")]
    [InlineData("sources/no\ttes.txt", "control character")]
    public void A_source_path_that_is_not_a_safe_relative_path_is_refused(string path, string reason)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["sources"]![0]!["path"] = path);

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.sources[0].path", reason);
        AssertNothingWritten(corpus);
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("/tmp/escaped.txt")]
    public void An_output_path_that_escapes_the_directory_is_refused(string path)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]![0]!["output"]!["path"] = path);

        CorpusException e = Refused(corpus);

        Assert.Contains(e.Errors, x => x.Path == "$.derivations[0].output.path");
        Assert.False(File.Exists(Path.Combine(corpus.Root, "..", "escaped.txt")));
    }

    [Fact]
    public void A_source_behind_a_symbolic_link_that_escapes_the_directory_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        using var outside = new TempCorpus();
        outside.WriteText("secret.txt", "outside\n");
        File.Delete(corpus.PathOf("sources/notes.txt"));
        File.CreateSymbolicLink(corpus.PathOf("sources/notes.txt"), outside.PathOf("secret.txt"));

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.sources[0].path", "symbolic link");
        AssertNothingWritten(corpus);
    }

    [Fact]
    public void A_source_under_a_symbolically_linked_directory_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        using var outside = new TempCorpus();
        outside.WriteText("notes.txt", TempCorpus.NotesText);
        Directory.Delete(corpus.PathOf("sources"), recursive: true);
        Directory.CreateSymbolicLink(corpus.PathOf("sources"), outside.Root);

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.sources[0].path", "symbolic link 'sources'");
    }

    [Fact]
    public void An_output_path_through_a_symbolic_link_is_refused_and_nothing_is_written_outside()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        using var outside = new TempCorpus();
        Directory.CreateSymbolicLink(corpus.PathOf("canonical"), outside.Root);

        CorpusException e = Refused(corpus);

        Assert.Contains(e.Errors, x => x.Reason.Contains("symbolic link", StringComparison.Ordinal));
        Assert.False(outside.Exists("notes.txt"));
        Assert.False(corpus.Exists("corpus.json"));
    }

    [Fact]
    public void A_missing_source_file_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        File.Delete(corpus.PathOf("sources/notes.txt"));

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.sources[0].path", "does not exist");
    }

    [Fact]
    public void A_source_over_the_artifact_size_limit_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build(new CorpusLimits { MaxArtifactBytes = 10 }, new LinesAdapter()));

        ManifestEdits.AssertError(e, "$.sources[0].path", "the limit is 10");
    }

    [Fact]
    public void A_missing_build_definition_is_refused()
    {
        using var corpus = new TempCorpus();

        CorpusException e = Refused(corpus, new LinesAdapter());

        ManifestEdits.AssertError(e, "corpus.build.json", "does not exist");
    }

    [Fact]
    public void An_unknown_adapter_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]![0]!["adapter"] = "other");

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.derivations[0].adapter", "no adapter 'other'");
        AssertNothingWritten(corpus);
    }

    [Fact]
    public void A_non_deterministic_adapter_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();

        CorpusException e = Refused(corpus, new ScriptedAdapter(new LinesAdapter().Derive) { IsDeterministic = false });

        ManifestEdits.AssertError(e, "(adapters)", "not deterministic");
    }

    [Fact]
    public void Two_adapters_with_one_id_are_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();

        CorpusException e = Refused(corpus, new LinesAdapter(), new LinesAdapter("2"));

        ManifestEdits.AssertError(e, "(adapters)", "two adapters are named 'lines'");
    }

    [Fact]
    public void An_adapter_refusal_refuses_the_build_and_writes_nothing()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]![0]!["parameters"] = new JsonObject { ["unknown"] = "x" });

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.derivations[0]", "adapter 'lines' refused: unknown parameter 'unknown'");
        AssertNothingWritten(corpus);
    }

    public static TheoryData<string, string, string> BadOutputs() => new()
    {
        { "an invalid segment id", "$.derivations[0]:output.segments[0].id", "not a valid segment id" },
        { "a segment id repeated in the output", "$.derivations[0]:output.segments[1].id", "repeated in this output" },
        { "a negative start", "$.derivations[0]:output.segments[0].start", "at least 0" },
        { "a zero length", "$.derivations[0]:output.segments[0].length", "at least 1 byte" },
        { "a span past the end", "$.derivations[0]:output.segments[0]", "lies outside the 3 canonical bytes" },
        { "a start inside a UTF-8 sequence", "$.derivations[0]:output.segments[0]", "inside a UTF-8 sequence" },
        { "an end inside a UTF-8 sequence", "$.derivations[0]:output.segments[0]", "inside a UTF-8 sequence" },
        { "invalid UTF-8 inside a segment", "$.derivations[0]:output.segments[0]", "not well-formed UTF-8" },
        { "segments out of document order", "$.derivations[0]:output.segments[1].start", "document order" },
        { "a source byte range past the input", "$.derivations[0]:output.segments[0].sources[0].bytes", "lies outside input 'notes'" },
        { "a source span with neither pages nor bytes", "$.derivations[0]:output.segments[0].sources[0]", "neither pages nor a byte range" },
        { "a page range starting at zero", "$.derivations[0]:output.segments[0].sources[0].pages", "not a page range" },
        { "a page range ending before it starts", "$.derivations[0]:output.segments[0].sources[0].pages", "not a page range" },
        { "null source spans", "$.derivations[0]:output.segments[0].sources", "is null" },
        { "an empty locator", "$.derivations[0]:output.segments[0].locator", "non-empty" },
        { "lossless with losses", "$.derivations[0]:output.losses", "must be empty when fidelity is lossless" },
        { "lossy without losses", "$.derivations[0]:output.losses", "must say what was discarded" },
        { "an empty loss statement", "$.derivations[0]:output.losses[0]", "non-empty statement" },
        { "an undefined fidelity", "$.derivations[0]:output.fidelity", "not a defined fidelity" },
        { "an invalid media type", "$.derivations[0]:output.mediaType", "not a valid media type" },
        { "canonical bytes over the artifact limit", "$.derivations[0]:output.canonical", "exceeds the limit" },
        { "more segments than the limit", "$.derivations[0]:output.segments", "over the limit of 2" },
    };

    [Theory]
    [MemberData(nameof(BadOutputs))]
    public void Every_kind_of_invalid_adapter_output_is_refused_with_its_path(string defect, string path, string reason)
    {
        using TempCorpus corpus = TempCorpus.Stored(notes: "abc");
        var adapter = new ScriptedAdapter(_ => BadOutput(defect));
        var limits = new CorpusLimits { MaxSegments = 2, MaxArtifactBytes = defect == "canonical bytes over the artifact limit" ? 3 : 1024 };

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build(limits, adapter));

        ManifestEdits.AssertError(e, path, reason);
        AssertNothingWritten(corpus);
    }

    [Fact]
    public void A_segment_id_already_used_by_an_earlier_derivation_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "notes-again",
            ["adapter"] = "lines",
            ["input"] = "notes",
            ["output"] = new JsonObject { ["id"] = "notes-again-out", ["path"] = "canonical/again.txt" },
            ["parameters"] = new JsonObject(),
        }));

        CorpusException e = Refused(corpus);

        ManifestEdits.AssertError(e, "$.derivations[1]:output.segments[0].id", "already a segment of an earlier derivation");
    }

    private static AdapterOutput BadOutput(string defect)
    {
        byte[] abc = "abc"u8.ToArray();
        ByteRange all = new(0, 3);
        AdapterSegment Seg(string id = "s1", long start = 0, long length = 3, string? locator = null, IReadOnlyList<AdapterSourceSpan>? sources = null) =>
            new(id, start, length, locator, sources ?? [new AdapterSourceSpan(null, all)]);
        AdapterOutput Out(byte[]? canonical = null, string media = "text/plain", DerivationFidelity fidelity = DerivationFidelity.Lossless, string[]? losses = null, AdapterSegment[]? segments = null) =>
            new(canonical ?? abc, media, fidelity, losses ?? [], segments ?? [Seg()]);
        byte[] twoByte = "\u00e9a"u8.ToArray(); // C3 A9 61

        return defect switch
        {
            "an invalid segment id" => Out(segments: [Seg(id: "bad id")]),
            "a segment id repeated in the output" => Out(segments: [Seg(length: 1), Seg(start: 1, length: 1)]),
            "a negative start" => Out(segments: [Seg(start: -1)]),
            "a zero length" => Out(segments: [Seg(length: 0)]),
            "a span past the end" => Out(segments: [Seg(start: 1, length: 3)]),
            "a start inside a UTF-8 sequence" => Out(canonical: twoByte, segments: [Seg(start: 1, length: 2)]),
            "an end inside a UTF-8 sequence" => Out(canonical: twoByte, segments: [Seg(start: 0, length: 1)]),
            "invalid UTF-8 inside a segment" => Out(canonical: [0x61, 0xFF, 0x62], segments: [Seg()]),
            "segments out of document order" => Out(segments: [Seg(id: "s1", start: 2, length: 1), Seg(id: "s2", start: 0, length: 1)]),
            "a source byte range past the input" => Out(segments: [Seg(sources: [new AdapterSourceSpan(null, new ByteRange(2, 2))])]),
            "a source span with neither pages nor bytes" => Out(segments: [Seg(sources: [new AdapterSourceSpan(null, null)])]),
            "a page range starting at zero" => Out(segments: [Seg(sources: [new AdapterSourceSpan(new PageRange(0, 1), null)])]),
            "a page range ending before it starts" => Out(segments: [Seg(sources: [new AdapterSourceSpan(new PageRange(3, 2), null)])]),
            "null source spans" => Out(segments: [new AdapterSegment("s1", 0, 3, null, null!)]),
            "an empty locator" => Out(segments: [Seg(locator: string.Empty)]),
            "lossless with losses" => Out(losses: ["something"]),
            "lossy without losses" => Out(fidelity: DerivationFidelity.LossyTraceable),
            "an empty loss statement" => Out(fidelity: DerivationFidelity.LossyTraceable, losses: [string.Empty]),
            "an undefined fidelity" => Out(fidelity: (DerivationFidelity)0),
            "an invalid media type" => Out(media: "Text/Plain"),
            "canonical bytes over the artifact limit" => Out(canonical: "abcd"u8.ToArray(), segments: [Seg()]),
            "more segments than the limit" => Out(segments: [Seg("s1", 0, 1), Seg("s2", 1, 1), Seg("s3", 2, 1)]),
            _ => throw new ArgumentException(defect),
        };
    }
}

public class BuildDefinitionTests
{
    private static CorpusException Refused(Action<JsonObject> edit)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(edit);
        return Assert.Throws<CorpusException>(() => corpus.Build());
    }

    [Fact]
    public void An_unknown_member_is_refused()
    {
        CorpusException e = Refused(d => d["sources"]![0]!["checksum"] = "x");

        ManifestEdits.AssertError(e, "$.sources[0].checksum", "not a known member");
    }

    [Fact]
    public void A_missing_top_level_array_is_refused()
    {
        CorpusException e = Refused(d => d.Remove("external"));

        ManifestEdits.AssertError(e, "$.external", "required but missing");
    }

    [Fact]
    public void The_wrong_schema_is_refused()
    {
        CorpusException e = Refused(d => d["schema"] = "rules-corpus/manifest/1");

        ManifestEdits.AssertError(e, "$.schema", "must be 'rules-corpus/build/1'");
    }

    [Fact]
    public void Colliding_ids_are_refused()
    {
        CorpusException e = Refused(d => d["derivations"]![0]!["output"]!["id"] = "notes");

        ManifestEdits.AssertError(e, "$.derivations[0].output.id", "collides with $.sources[0].id");
    }

    [Fact]
    public void A_derivation_id_colliding_with_an_artifact_id_is_refused()
    {
        CorpusException e = Refused(d => d["derivations"]![0]!["id"] = "notes-canonical");

        ManifestEdits.AssertError(e, "$.derivations[0].id", "collides with $.derivations[0].output.id");
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("digest")]
    public void Bytes_or_digest_on_a_stored_source_are_refused(string member)
    {
        CorpusException e = Refused(d => d["sources"]![0]![member] = member == "bytes" ? 44 : "sha256:" + new string('0', 64));

        ManifestEdits.AssertError(e, $"$.sources[0].{member}", "must not be given for a stored source");
    }

    [Fact]
    public void An_acquired_source_without_an_origin_is_refused()
    {
        CorpusException e = Refused(d => d["sources"]![0]!.AsObject().Remove("origin"));

        ManifestEdits.AssertError(e, "$.sources[0].origin", "required for an acquired source");
    }

    [Fact]
    public void Acquisition_evidence_on_an_external_output_is_refused()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.EditBuild(d => d["sources"]![1]!["origin"] = "somewhere");

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build());

        ManifestEdits.AssertError(e, "$.sources[1].origin", "must be absent");
    }

    [Fact]
    public void An_external_output_that_is_not_a_source_is_refused()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.EditBuild(d => d["external"]![0]!["output"] = "rulebook-canonical");

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build());

        ManifestEdits.AssertError(e, "$.external[0].output", "not a source");
    }

    [Fact]
    public void An_external_input_declared_after_its_output_is_refused()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.EditBuild(d =>
        {
            JsonArray sources = d["sources"]!.AsArray();
            JsonNode pdf = sources[0]!.DeepClone();
            sources.RemoveAt(0);
            sources.Add(pdf);
        });

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build());

        ManifestEdits.AssertError(e, "$.external[0].inputs[0]", "declared before the output");
    }

    [Fact]
    public void A_derivation_over_an_unstored_source_is_refused()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.EditBuild(d => d["derivations"]![0]!["input"] = "rulebook-pdf");

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build());

        ManifestEdits.AssertError(e, "$.derivations[0].input", "not stored");
    }

    [Fact]
    public void A_derivation_over_a_later_output_is_refused()
    {
        CorpusException e = Refused(d => d["derivations"]![0]!["input"] = "notes-canonical");

        ManifestEdits.AssertError(e, "$.derivations[0].input", "neither a source nor the output of an earlier derivation");
    }

    [Fact]
    public void An_external_derivation_whose_losses_contradict_its_fidelity_is_refused()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.EditBuild(d => d["external"]![0]!["fidelity"] = "lossless");

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build());

        ManifestEdits.AssertError(e, "$.external[0].losses", "must be empty when fidelity is lossless");
    }

    [Fact]
    public void A_baseline_naming_an_undeclared_artifact_is_refused()
    {
        CorpusException e = Refused(d => d["baselines"]![0]!["artifact"] = "nope");

        ManifestEdits.AssertError(e, "$.baselines[0].artifact", "not declared");
    }

    [Fact]
    public void A_build_definition_with_a_byte_order_mark_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.WriteBytes("corpus.build.json", [0xEF, 0xBB, 0xBF, .. corpus.ReadBytes("corpus.build.json")]);

        CorpusException e = Assert.Throws<CorpusException>(() => corpus.Build());

        ManifestEdits.AssertError(e, "$", "byte-order mark");
    }
}
