using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RulesCorpus.Adapters;
using RulesCorpus.Tests.Support;

namespace RulesCorpus.Tests;

public class ManifestRoundTripTests
{
    [Fact]
    public void A_built_manifest_parses_and_writes_back_to_the_same_bytes()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();
        byte[] written = corpus.ReadBytes("corpus.json");

        CorpusManifest parsed = CorpusManifest.Parse(written);

        Assert.Equal(written, parsed.ToUtf8Json());
    }

    [Fact]
    public void A_reformatted_manifest_reads_to_the_same_model_and_writes_the_canonical_form()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        byte[] written = corpus.ReadBytes("corpus.json");
        string reformatted = JsonNode.Parse(written)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 7 });

        CorpusManifest parsed = CorpusManifest.Parse(Encoding.UTF8.GetBytes(reformatted));

        Assert.Equal(written, parsed.ToUtf8Json());
    }

    [Fact]
    public void The_model_exposes_what_was_built_in_declared_order()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        CorpusManifest m = corpus.Build();

        Assert.Equal(["rulebook-pdf", "rulebook-text", "rulebook-canonical"], m.Artifacts.Select(a => a.Id));
        Assert.Equal([ArtifactRole.Source, ArtifactRole.Derived, ArtifactRole.Derived], m.Artifacts.Select(a => a.Role));
        Assert.Null(m.Artifacts[1].Acquisition);
        Assert.Equal("rulebook-extraction", m.Artifacts[1].DerivedBy);
        Assert.False(m.Artifacts[0].Stored);
        Assert.Null(m.Artifacts[0].Path);
        Assert.Equal(new DateOnly(2026, 9, 14), m.Artifacts[0].Acquisition!.Retrieved);
        Assert.Equal(["rulebook-extraction", "rulebook-segmented"], m.Derivations.Select(d => d.Id));
        Assert.Equal(DerivationReproducibility.External, m.Derivations[0].Reproducibility);
        Assert.Equal(DerivationReproducibility.Reproducible, m.Derivations[1].Reproducibility);
        Assert.Equal("lines", m.Derivations[1].Tool.Id);
        Assert.Equal("r", m.Derivations[1].Parameters["prefix"]);
        Assert.Equal(["rl1", "rl2", "rl3"], m.Segments.Select(s => s.Id));
        Assert.All(m.Segments, s => Assert.Equal("rulebook-text", Assert.Single(s.Sources).Artifact));
        Assert.Equal(m.Artifacts[1].Digest, ContentDigest.Compute(Encoding.UTF8.GetBytes(TempCorpus.NotesText)));
    }
}

public class ManifestValidationTests
{
    public static TheoryData<string, bool, string, string> ReferenceAndStructureDefects() => new()
    {
        // case, reseal digests, path, reason fragment
        { "derivedBy names an undeclared derivation", true, "$.artifacts[1].derivedBy", "not declared" },
        { "a derivation input is undeclared", true, "$.derivations[0].inputs[0]", "not declared" },
        { "a derivation output is undeclared", true, "$.derivations[0].output", "not declared" },
        { "a baseline names an undeclared artifact", false, "$.baselines[0].artifact", "not declared" },
        { "a segment names an undeclared artifact", true, "$.segments[0].artifact", "not declared" },
        { "a source span names an undeclared artifact", true, "$.segments[0].sources[0].artifact", "not declared" },
        { "a source span names the segment's own artifact, not its derivation's input", true, "$.segments[0].sources[0].artifact", "is not the input" },
        { "two artifacts share an id", true, "$.artifacts[1].id", "already used" },
        { "a derivation id collides with an artifact id", true, "$.derivations[0].id", "already used" },
        { "two segments share an id", true, "$.segments[1].id", "already used" },
        { "two baselines share a source id", true, "$.baselines[1].sourceId", "already used" },
        { "two derivations output one artifact", true, "$.derivations[1].output", "also the output of" },
        { "an input is declared after its output", true, "$.derivations[0].inputs[0]", "not declared before the output" },
        { "a derived artifact names a derivation that outputs something else", true, "$.artifacts[2].derivedBy", "not this artifact" },
        { "a derivation outputs a source artifact", true, "$.derivations[0].output", "names source artifact" },
        { "a lossless derivation lists losses", true, "$.derivations[0].losses", "must be empty when fidelity is lossless" },
        { "a lossy derivation lists no losses", true, "$.derivations[0].losses", "must say what was discarded" },
        { "a reproducible derivation has two inputs", true, "$.derivations[0].inputs", "exactly one input" },
        { "a segment lies beyond its artifact's declared bytes", true, "$.segments[2]", "lies outside artifact" },
        { "a segment is a span of a source artifact", true, "$.segments[0].artifact", "is a source artifact" },
        { "a source span's byte range lies beyond its artifact", true, "$.segments[0].sources[0]", "lies outside artifact" },
        { "segments are out of document order", true, "$.segments[1]", "out of order" },
        { "two stored paths differ only by case", true, "$.artifacts[1].path", "collides" },
        { "an external derivation follows a reproducible one", true, "$.derivations[1]", "external derivations come first" },
    };

    public static TheoryData<string, string, string> SchemaDefects() => new()
    {
        { "an unknown top-level member", "$.extra", "not a known member" },
        { "an unknown nested member", "$.artifacts[0].acquisition.extra", "not a known member" },
        { "the wrong schema", "$.schema", "must be 'rules-corpus/manifest/1'" },
        { "a missing required member", "$.corpusId", "required but missing" },
        { "a string where a number belongs", "$.artifacts[0].bytes", "must be a number" },
        { "a derived artifact with acquisition", "$.artifacts[1].acquisition", "must be absent for a derived artifact" },
        { "a source artifact with derivedBy", "$.artifacts[0].derivedBy", "must be absent for a source artifact" },
        { "an unstored artifact with a path", "$.artifacts[0].path", "must be absent when stored is false" },
        { "a stored artifact without a path", "$.artifacts[0].path", "required but missing" },
        { "a path that leaves the corpus", "$.artifacts[0].path", "'..' component" },
        { "an uppercase digest", "$.artifacts[0].digest", "not a valid digest" },
        { "an unknown role", "$.artifacts[0].role", "must be 'source' or 'derived'" },
        { "an invalid date", "$.baselines[0].asOf", "not a valid date" },
        { "an invalid segment id", "$.segments[0].id", "not a valid segment id" },
        { "a zero-length segment", "$.segments[0].length", "must be from 1" },
        { "an empty sources array", "$.segments[0].sources", "must not be empty" },
        { "a source span with neither pages nor bytes", "$.segments[0].sources[0]", "must carry pages" },
        { "a page range starting at zero", "$.segments[0].sources[0].pages.from", "must be from 1" },
        { "a page range ending before it starts", "$.segments[0].sources[0].pages.to", "must be at least from" },
        { "an empty artifacts array", "$.artifacts", "must not be empty" },
        { "a null member", "$.segments[0].locator", "null" },
    };

    [Theory]
    [MemberData(nameof(ReferenceAndStructureDefects))]
    public void Every_reference_and_structure_defect_is_refused_with_its_path(string defect, bool reseal, string path, string reason)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        Mutate(defect, m);
        byte[] bytes = ManifestEdits.Bytes(m);
        if (reseal)
        {
            bytes = ManifestEdits.Reseal(bytes);
        }

        CorpusException e = ManifestEdits.ParseFails(bytes);

        ManifestEdits.AssertError(e, path, reason);
    }

    [Theory]
    [MemberData(nameof(SchemaDefects))]
    public void Every_schema_defect_is_refused_with_its_path(string defect, string path, string reason)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        Mutate(defect, m);

        CorpusException e = ManifestEdits.ParseFails(ManifestEdits.Bytes(m));

        ManifestEdits.AssertError(e, path, reason);
    }

    [Fact]
    public void An_edited_manifest_fails_manifest_digest_but_keeps_content_digest()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        m["artifacts"]![0]!["acquisition"]!["origin"] = "someone else";

        CorpusException e = ManifestEdits.ParseFails(ManifestEdits.Bytes(m));

        CorpusError error = Assert.Single(e.Errors);
        Assert.Equal("$.manifestDigest", error.Path);
    }

    [Fact]
    public void An_edited_segment_digest_fails_both_identities()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        m["segments"]![0]!["digest"] = ContentDigest.Compute("tampered"u8).ToString();

        CorpusException e = ManifestEdits.ParseFails(ManifestEdits.Bytes(m));

        Assert.Equal(["$.contentDigest", "$.manifestDigest"], e.Errors.Select(x => x.Path));
    }

    [Fact]
    public void A_manifest_over_the_size_limit_is_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        byte[] bytes = corpus.ReadBytes("corpus.json");

        CorpusException e = Assert.Throws<CorpusException>(() => CorpusManifest.Parse(bytes, new CorpusLimits { MaxManifestBytes = 100 }));

        ManifestEdits.AssertError(e, "$", "the limit is 100");
    }

    [Fact]
    public void Too_many_segments_are_refused()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        CorpusException e = Assert.Throws<CorpusException>(() => CorpusManifest.Parse(corpus.ReadBytes("corpus.json"), new CorpusLimits { MaxSegments = 2 }));

        ManifestEdits.AssertError(e, "$.segments", "exceeds the limit of 2");
    }

    [Fact]
    public void Every_error_is_reported_not_only_the_first()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        m["corpusId"] = "Bad Id";
        m["segments"]![0]!["id"] = "-";
        m["extra"] = true;

        CorpusException e = ManifestEdits.ParseFails(ManifestEdits.Bytes(m));

        Assert.Equal(["$.corpusId", "$.segments[0].id", "$.extra"], e.Errors.Select(x => x.Path));
    }

    private static void Mutate(string defect, JsonObject m)
    {
        JsonArray artifacts = m["artifacts"]!.AsArray();
        JsonArray derivations = m["derivations"]!.AsArray();
        JsonArray baselines = m["baselines"]!.AsArray();
        JsonArray segments = m["segments"]!.AsArray();
        JsonNode source = artifacts[0]!;
        JsonNode derived = artifacts[1]!;
        switch (defect)
        {
            case "derivedBy names an undeclared derivation": derived["derivedBy"] = "nope"; break;
            case "a derivation input is undeclared": derivations[0]!["inputs"]![0] = "nope"; break;
            case "a derivation output is undeclared": derivations[0]!["output"] = "nope"; break;
            case "a baseline names an undeclared artifact": baselines[0]!["artifact"] = "nope"; break;
            case "a segment names an undeclared artifact": segments[0]!["artifact"] = "nope"; break;
            case "a source span names an undeclared artifact": segments[0]!["sources"]![0]!["artifact"] = "nope"; break;
            case "a source span names the segment's own artifact, not its derivation's input": segments[0]!["sources"]![0]!["artifact"] = derived["id"]!.GetValue<string>(); break;
            case "two artifacts share an id": derived["id"] = "notes"; break;
            case "a derivation id collides with an artifact id":
                derivations[0]!["id"] = "notes";
                derived["derivedBy"] = "notes";
                break;
            case "two segments share an id": segments[1]!["id"] = "l1"; break;
            case "two baselines share a source id": baselines.Add(baselines[0]!.DeepClone()); break;
            case "two derivations output one artifact":
                JsonNode copy = derivations[0]!.DeepClone();
                copy["id"] = "notes-lines-again";
                derivations.Add(copy);
                break;
            case "an input is declared after its output":
                artifacts.Clear();
                artifacts.Add(derived.DeepClone());
                artifacts.Add(source.DeepClone());
                break;
            case "a derived artifact names a derivation that outputs something else":
                JsonNode orphan = derived.DeepClone();
                orphan["id"] = "orphan";
                orphan["path"] = "canonical/orphan.txt";
                artifacts.Add(orphan);
                break;
            case "a derivation outputs a source artifact": derivations[0]!["output"] = "notes"; break;
            case "a lossless derivation lists losses": derivations[0]!["losses"] = new JsonArray("something"); break;
            case "a lossy derivation lists no losses": derivations[0]!["fidelity"] = "lossy-traceable"; break;
            case "a reproducible derivation has two inputs": derivations[0]!["inputs"] = new JsonArray("notes", "notes"); break;
            case "a segment lies beyond its artifact's declared bytes": segments[2]!["length"] = 1000; break;
            case "a segment is a span of a source artifact": segments[0]!["artifact"] = "notes"; break;
            case "a source span's byte range lies beyond its artifact": segments[0]!["sources"]![0]!["start"] = 1000; break;
            case "segments are out of document order":
                JsonNode first = segments[0]!.DeepClone();
                segments[0] = segments[1]!.DeepClone();
                segments[1] = first;
                break;
            case "two stored paths differ only by case": derived["path"] = "SOURCES/notes.txt"; break;
            case "an external derivation follows a reproducible one":
                // Declare the source as the output of an external derivation listed last.
                source.AsObject().Remove("acquisition");
                source["role"] = "derived";
                source["derivedBy"] = "late-external";
                derivations.Add(new JsonObject
                {
                    ["id"] = "late-external",
                    ["inputs"] = new JsonArray("notes-canonical"),
                    ["output"] = "notes",
                    ["tool"] = new JsonObject { ["id"] = "tool", ["version"] = "1" },
                    ["parameters"] = new JsonObject(),
                    ["reproducibility"] = "external",
                    ["fidelity"] = "lossless",
                    ["losses"] = new JsonArray(),
                });
                break;

            case "an unknown top-level member": m["extra"] = 1; break;
            case "an unknown nested member": source["acquisition"]!["extra"] = "x"; break;
            case "the wrong schema": m["schema"] = "rules-corpus/manifest/2"; break;
            case "a missing required member": m.Remove("corpusId"); break;
            case "a string where a number belongs": source["bytes"] = "44"; break;
            case "a derived artifact with acquisition": derived["acquisition"] = new JsonObject { ["origin"] = "x" }; break;
            case "a source artifact with derivedBy": source["derivedBy"] = "notes-lines"; break;
            case "an unstored artifact with a path": source["stored"] = false; break;
            case "a stored artifact without a path": source.AsObject().Remove("path"); break;
            case "a path that leaves the corpus": source["path"] = "sources/../../notes.txt"; break;
            case "an uppercase digest": source["digest"] = source["digest"]!.GetValue<string>().ToUpperInvariant().Replace("SHA256", "sha256", StringComparison.Ordinal); break;
            case "an unknown role": source["role"] = "original"; break;
            case "an invalid date": baselines[0]!["asOf"] = "2026-02-30"; break;
            case "an invalid segment id": segments[0]!["id"] = "l1."; break;
            case "a zero-length segment": segments[0]!["length"] = 0; break;
            case "an empty sources array": segments[0]!["sources"] = new JsonArray(); break;
            case "a source span with neither pages nor bytes": segments[0]!["sources"] = new JsonArray(new JsonObject { ["artifact"] = "notes" }); break;
            case "a page range starting at zero":
                segments[0]!["sources"]![0]!["pages"] = new JsonObject { ["from"] = 0, ["to"] = 1 };
                break;
            case "a page range ending before it starts":
                segments[0]!["sources"]![0]!["pages"] = new JsonObject { ["from"] = 3, ["to"] = 2 };
                break;
            case "an empty artifacts array": artifacts.Clear(); break;
            case "a null member": segments[0]!["locator"] = null; break;
            default: throw new ArgumentException("unknown defect " + defect);
        }
    }
}

/// <summary>
/// A validated manifest cannot be changed by a caller: every collection it exposes is a
/// read-only wrapper, not the list or dictionary it was built from behind an interface.
/// </summary>
public class ManifestImmutabilityTests
{
    private static void AssertReadOnly<T>(IReadOnlyList<T> list)
    {
        Assert.False(list is List<T>, "a List<T> can be downcast and mutated");
        Assert.False(list is T[], "an array's elements can be replaced after a downcast");
        IList<T> asList = Assert.IsAssignableFrom<IList<T>>(list);
        Assert.True(asList.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asList.Clear());
    }

    private static void AssertReadOnly(IReadOnlyDictionary<string, string> map)
    {
        Assert.False(map is SortedDictionary<string, string>, "a SortedDictionary can be downcast and mutated");
        Assert.False(map is Dictionary<string, string>, "a Dictionary can be downcast and mutated");
        IDictionary<string, string> asMap = Assert.IsAssignableFrom<IDictionary<string, string>>(map);
        Assert.True(asMap.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => asMap["injected"] = "x");
    }

    private static void AssertDeeplyReadOnly(CorpusManifest m)
    {
        AssertReadOnly(m.Artifacts);
        AssertReadOnly(m.Derivations);
        AssertReadOnly(m.Baselines);
        AssertReadOnly(m.Segments);
        Assert.All(m.Derivations, d =>
        {
            AssertReadOnly(d.Inputs);
            AssertReadOnly(d.Losses);
            AssertReadOnly(d.Parameters);
        });
        Assert.All(m.Segments, s => AssertReadOnly(s.Sources));
    }

    [Fact]
    public void A_parsed_manifest_exposes_only_read_only_collections()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();

        CorpusManifest m = CorpusManifest.Parse(corpus.ReadBytes("corpus.json"));

        Assert.Contains(m.Derivations, d => d.Parameters.Count > 0);
        Assert.Contains(m.Derivations, d => d.Losses.Count > 0);
        Assert.Contains(m.Segments, s => s.Sources.Count > 0);
        AssertDeeplyReadOnly(m);
    }

    [Fact]
    public void A_built_manifest_exposes_only_read_only_collections()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();

        AssertDeeplyReadOnly(corpus.Build());
    }

    [Fact]
    public void Parameters_keep_their_ordinal_key_order()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.EditBuild(d => d["derivations"]![0]!["parameters"] = new JsonObject { ["prefix"] = "p" });
        corpus.Build();
        byte[] edited = corpus.ReadBytes("corpus.json");

        CorpusManifest m = CorpusManifest.Parse(edited);

        Assert.Equal(edited, m.ToUtf8Json());
    }
}

/// <summary>
/// Path collisions are checked in time linear in the number of paths. Comparing every pair
/// took about a minute at 64,000 artifacts. The bounds are generous, to catch the quadratic
/// shape rather than to time the machine.
/// </summary>
public class PathCollisionScaleTests
{
    private const int Count = 50_000;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly string Zero = "sha256:" + new string('0', 64);

    [Fact]
    public void A_manifest_with_many_artifacts_validates_in_linear_time_and_still_finds_a_collision()
    {
        var json = new StringBuilder($"{{\"schema\":\"rules-corpus/manifest/1\",\"corpusId\":\"x\",\"buildDigest\":\"{Zero}\",\"artifacts\":[");
        for (int i = 0; i < Count; i++)
        {
            // The last path differs from the first only by case.
            string path = i == Count - 1 ? "D0/F0" : $"d{i % 100}/f{i}";
            json.Append(i == 0 ? "" : ",")
                .Append($"{{\"id\":\"a{i}\",\"role\":\"source\",\"mediaType\":\"text/plain\",\"bytes\":0,\"digest\":\"{Zero}\",")
                .Append($"\"stored\":true,\"path\":\"{path}\",\"acquisition\":{{\"origin\":\"o\"}}}}");
        }

        json.Append($"],\"derivations\":[],\"baselines\":[],\"segments\":[],\"contentDigest\":\"{Zero}\",\"manifestDigest\":\"{Zero}\"}}");
        byte[] bytes = Encoding.UTF8.GetBytes(json.ToString());
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        CorpusException e = ManifestEdits.ParseFails(bytes);

        Assert.True(stopwatch.Elapsed < Bound, $"took {stopwatch.Elapsed}");
        ManifestEdits.AssertError(e, $"$.artifacts[{Count - 1}].path", "collides with $.artifacts[0].path 'd0/f0'");
        Assert.Single(e.Errors, x => x.Reason.Contains("collides", StringComparison.Ordinal));
    }

    [Fact]
    public void A_build_definition_with_many_sources_is_read_in_linear_time()
    {
        var json = new StringBuilder("{\"schema\":\"rules-corpus/build/1\",\"corpusId\":\"x\",\"sources\":[");
        for (int i = 0; i < Count; i++)
        {
            json.Append(i == 0 ? "" : ",").Append($"{{\"id\":\"s{i}\",\"path\":\"d{i % 100}/f{i}\",\"mediaType\":\"text/plain\",\"origin\":\"o\"}}");
        }

        json.Append("],\"derivations\":[],\"external\":[],\"baselines\":[]}");
        var errors = new List<CorpusError>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Build.BuildDefinition? definition = Build.BuildDefinition.Read(Encoding.UTF8.GetBytes(json.ToString()), errors);

        Assert.True(stopwatch.Elapsed < Bound, $"took {stopwatch.Elapsed}");
        Assert.Empty(errors);
        Assert.Equal(Count, definition!.Sources.Count);
    }

    [Theory]
    [InlineData("a/b", "a", "one inside the other")]
    [InlineData("a", "a/b", "one inside the other")]
    [InlineData("a/b/c", "A/B", "one inside the other")]
    [InlineData("A/B", "a/b/c", "one inside the other")]
    [InlineData("a/b", "A/b", "the same file")]
    public void Collisions_are_found_whichever_path_comes_first(string first, string second, string reason)
    {
        var index = new Internal.PathCollisions();

        Assert.Null(index.Add(first, "first"));
        string? collision = index.Add(second, "second");

        Assert.NotNull(collision);
        Assert.Contains("first", collision, StringComparison.Ordinal);
        Assert.Contains(reason, collision, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a/b", "a/bc")]
    [InlineData("a/b", "ab")]
    [InlineData("a.b/c", "a/b/c")]
    public void Paths_that_share_only_a_prefix_do_not_collide(string first, string second)
    {
        var index = new Internal.PathCollisions();

        Assert.Null(index.Add(first, "first"));
        Assert.Null(index.Add(second, "second"));
    }
}

public class TwoIdentitiesTests
{
    [Fact]
    public void A_metadata_only_change_keeps_content_digest_and_changes_manifest_digest()
    {
        using TempCorpus a = TempCorpus.Stored(origin: "a person");
        using TempCorpus b = TempCorpus.Stored(origin: "https://example.org/notes");

        CorpusManifest first = a.Build();
        CorpusManifest second = b.Build();

        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.NotEqual(first.ManifestDigest, second.ManifestDigest);
    }

    [Fact]
    public void A_tool_version_or_locator_change_keeps_content_digest()
    {
        using TempCorpus a = TempCorpus.Stored();
        using TempCorpus b = TempCorpus.Stored();

        CorpusManifest first = a.Build(new LinesAdapter("1"));
        CorpusManifest second = b.Build(new ScriptedAdapter(input =>
        {
            AdapterOutput o = new LinesAdapter().Derive(input);
            return new AdapterOutput(o.Canonical, o.MediaType, o.Fidelity, o.Losses, o.Segments.Select(s => s with { Locator = "elsewhere" }).ToList());
        })
        { Version = "2" });

        Assert.Equal(first.ContentDigest, second.ContentDigest);
        Assert.NotEqual(first.ManifestDigest, second.ManifestDigest);
    }

    [Fact]
    public void A_content_change_changes_both_identities()
    {
        using TempCorpus a = TempCorpus.Stored();
        using TempCorpus b = TempCorpus.Stored(notes: TempCorpus.NotesText + "Delta rule.\n");

        CorpusManifest first = a.Build();
        CorpusManifest second = b.Build();

        Assert.NotEqual(first.ContentDigest, second.ContentDigest);
        Assert.NotEqual(first.ManifestDigest, second.ManifestDigest);
    }

    [Fact]
    public void The_corpus_id_is_outside_content_identity()
    {
        using TempCorpus a = TempCorpus.Stored();
        using TempCorpus b = TempCorpus.Stored();
        b.EditBuild(d => d["corpusId"] = "renamed");

        Assert.Equal(a.Build().ContentDigest, b.Build().ContentDigest);
    }

    [Fact]
    public void Content_digest_is_sha256_of_the_documented_projection()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        CorpusManifest m = corpus.Build();
        string hash = m.Artifacts[0].Digest.ToString();
        var segments = string.Join(",", m.Segments.Select(s =>
            $"{{\"artifact\":\"{s.Artifact}\",\"digest\":\"{s.Digest}\",\"id\":\"{s.Id}\",\"length\":{s.Length},\"start\":{s.Start}}}"));
        string projection =
            $"{{\"baselines\":[{{\"asOf\":\"2026-01-01\",\"contentHash\":\"{hash}\",\"hashDerivation\":\"notes-bytes\",\"sourceId\":\"notes\"}}],\"segments\":[{segments}]}}";

        Assert.Equal(ContentDigest.Compute(Encoding.UTF8.GetBytes(projection)), m.ContentDigest);
    }

    [Fact]
    public void Manifest_digest_is_sha256_of_the_compact_manifest_without_its_own_member()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        CorpusManifest m = corpus.Build();
        JsonObject json = ManifestEdits.Read(corpus);
        json.Remove("manifestDigest");

        // System.Text.Json keeps insertion order, and corpus.json was written sorted, so its
        // compact serialization (with non-ASCII left literal) is the canonical form here.
        string compact = json.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        Assert.Equal(ContentDigest.Compute(Encoding.UTF8.GetBytes(compact)), m.ManifestDigest);
    }
}
