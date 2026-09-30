using System.Text.Json.Nodes;
using RulesCorpus.Adapters;
using RulesCorpus.Tests.Support;

namespace RulesCorpus.Tests;

public class VerifierTests
{
    private static VerificationReport Verify(TempCorpus corpus, VerificationOptions? options = null) =>
        CorpusVerifier.Verify(CorpusFiles.FromDirectory(corpus.Root), options);

    private static VerificationCheck Check(VerificationReport report, string name) =>
        Assert.Single(report.Checks, c => c.Name == name);

    private static void AssertNeverOkWhileAnythingIsNot(VerificationReport report)
    {
        if (report.Checks.Any(c => c.Outcome != VerificationOutcome.Ok))
        {
            Assert.NotEqual(VerificationOutcome.Ok, report.Outcome);
        }

        Assert.Equal(report.Checks.Max(c => c.Outcome), report.Outcome);
    }

    [Fact]
    public void A_fully_stored_corpus_verifies_ok_with_every_check_ok()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Ok, report.Outcome);
        Assert.All(report.Checks, c => Assert.Equal(VerificationOutcome.Ok, c.Outcome));
        Assert.Equal(
            ["schema", "manifest-form", "uniqueness", "references", "derivations", "baselines", "segments", "source-spans", "limits", "content-digest", "manifest-digest",
             "build-definition", "artifact notes", "artifact notes-canonical", "segments notes-canonical"],
            report.Checks.Select(c => c.Name));
    }

    [Fact]
    public void An_unstored_artifact_is_not_verified_and_the_report_is_therefore_not_ok()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.NotVerified, Check(report, "artifact rulebook-pdf").Outcome);
        Assert.Equal(VerificationOutcome.NotVerified, report.Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "artifact rulebook-text").Outcome);
        AssertNeverOkWhileAnythingIsNot(report);
    }

    [Fact]
    public void A_report_with_no_checks_is_not_verified_rather_than_ok()
    {
        Assert.Equal(VerificationOutcome.NotVerified, new VerificationReport([]).Outcome);
    }

    [Fact]
    public void A_tampered_artifact_byte_fails_its_digest_and_its_segments()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        byte[] canonical = corpus.ReadBytes("canonical/notes.txt");
        canonical[0] ^= 0x20;
        corpus.WriteBytes("canonical/notes.txt", canonical);

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
        Assert.Equal(VerificationOutcome.Failed, Check(report, "artifact notes-canonical").Outcome);
        Assert.Equal(VerificationOutcome.Failed, Check(report, "segments notes-canonical").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "artifact notes").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "manifest-digest").Outcome);
    }

    [Fact]
    public void A_tampered_source_byte_fails_its_digest()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        corpus.WriteText("sources/notes.txt", TempCorpus.NotesText.Replace("Alpha", "Omega", StringComparison.Ordinal));

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, Check(report, "artifact notes").Outcome);
        Assert.Contains("digest", Check(report, "artifact notes").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tampered_segment_in_a_resealed_manifest_fails_the_segment_bytes_check()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        m["segments"]![0]!["start"] = 1;
        m["segments"]![0]!["length"] = 3;
        corpus.WriteBytes("corpus.json", ManifestEdits.Reseal(ManifestEdits.Bytes(m)));

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Ok, Check(report, "content-digest").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "manifest-digest").Outcome);
        VerificationCheck segments = Check(report, "segments notes-canonical");
        Assert.Equal(VerificationOutcome.Failed, segments.Outcome);
        Assert.Contains("'l1'", segments.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_edited_manifest_fails_manifest_digest()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        m["artifacts"]![0]!["acquisition"]!["origin"] = "someone else";
        ManifestEdits.Write(corpus, m);

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, Check(report, "manifest-digest").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "content-digest").Outcome);
        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
    }

    [Fact]
    public void A_missing_stored_artifact_fails()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        File.Delete(corpus.PathOf("canonical/notes.txt"));

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, Check(report, "artifact notes-canonical").Outcome);
        Assert.Equal(VerificationOutcome.Failed, Check(report, "segments notes-canonical").Outcome);
    }

    [Fact]
    public void A_missing_manifest_fails_the_schema_and_leaves_everything_else_not_verified()
    {
        using TempCorpus corpus = TempCorpus.Stored();

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
        Assert.Equal(VerificationOutcome.Failed, Check(report, "schema").Outcome);
        Assert.Equal(VerificationOutcome.NotVerified, Check(report, "content-digest").Outcome);
        Assert.Equal(VerificationOutcome.NotVerified, Check(report, "artifacts").Outcome);
        Assert.DoesNotContain(report.Checks, c => c.Name != "build-definition" && c.Outcome == VerificationOutcome.Ok);
    }

    [Fact]
    public void A_malformed_manifest_reports_its_errors_under_schema()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        m["extra"] = 1;
        ManifestEdits.Write(corpus, m);

        VerificationReport report = Verify(corpus);

        VerificationCheck schema = Check(report, "schema");
        Assert.Equal(VerificationOutcome.Failed, schema.Outcome);
        Assert.Contains("$.extra: is not a known member", schema.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_structural_failure_is_reported_while_bytes_are_still_examined()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        JsonObject m = ManifestEdits.Read(corpus);
        m["segments"]![1]!["id"] = "l1";
        corpus.WriteBytes("corpus.json", ManifestEdits.Reseal(ManifestEdits.Bytes(m)));

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, Check(report, "uniqueness").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "artifact notes-canonical").Outcome);
    }

    [Fact]
    public void A_missing_build_definition_fails()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        File.Delete(corpus.PathOf("corpus.build.json"));

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, Check(report, "build-definition").Outcome);
    }

    [Fact]
    public void Rebuild_reruns_a_reproducible_derivation_and_requires_identical_output()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true, Adapters = [new LinesAdapter()] });

        Assert.Equal(VerificationOutcome.Ok, report.Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "rebuild notes-lines").Outcome);
    }

    [Fact]
    public void Rebuild_without_the_adapter_fails_rather_than_not_verified()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true });

        VerificationCheck rebuild = Check(report, "rebuild notes-lines");
        Assert.Equal(VerificationOutcome.Failed, rebuild.Outcome);
        Assert.Contains("no adapter 'lines'", rebuild.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_reports_an_external_derivation_not_verified()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true, Adapters = [new LinesAdapter()] });

        Assert.Equal(VerificationOutcome.NotVerified, Check(report, "rebuild rulebook-extraction").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "rebuild rulebook-segmented").Outcome);
        Assert.Equal(VerificationOutcome.NotVerified, report.Outcome);
        AssertNeverOkWhileAnythingIsNot(report);
    }

    [Fact]
    public void Rebuild_fails_when_the_adapter_now_produces_different_segments()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        var changed = new ScriptedAdapter(input =>
        {
            AdapterOutput o = new LinesAdapter().Derive(input);
            return new AdapterOutput(o.Canonical, o.MediaType, o.Fidelity, o.Losses, o.Segments.Select(s => s with { Locator = "moved" }).ToList());
        });

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true, Adapters = [changed] });

        VerificationCheck rebuild = Check(report, "rebuild notes-lines");
        Assert.Equal(VerificationOutcome.Failed, rebuild.Outcome);
        Assert.Contains("segment 0 ('l1') differs", rebuild.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_fails_when_the_adapter_now_produces_different_bytes()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        var changed = new ScriptedAdapter(input =>
        {
            AdapterOutput o = new LinesAdapter().Derive(input);
            byte[] bytes = o.Canonical.ToArray();
            bytes[^1] = (byte)' ';
            return new AdapterOutput(bytes, o.MediaType, o.Fidelity, o.Losses, o.Segments);
        });

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true, Adapters = [changed] });

        Assert.Contains("output is", Check(report, "rebuild notes-lines").Detail, StringComparison.Ordinal);
        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
    }

    [Fact]
    public void Rebuild_fails_with_a_different_adapter_version()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true, Adapters = [new LinesAdapter("2")] });

        Assert.Contains("recorded with version 1", Check(report, "rebuild notes-lines").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_fails_when_the_adapter_refuses()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        var refusing = new ScriptedAdapter(_ => throw new CorpusAdapterException("no"));

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true, Adapters = [refusing] });

        Assert.Contains("refused: no", Check(report, "rebuild notes-lines").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Rebuild_fails_rather_than_crashing_when_the_adapter_throws_something_other_than_a_refusal()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        var throwing = new ScriptedAdapter(_ => throw new ArgumentOutOfRangeException("offset", "hostile parameters"));

        VerificationReport report = Verify(corpus, new VerificationOptions { Rebuild = true, Adapters = [throwing] });

        VerificationCheck check = Check(report, "rebuild notes-lines");
        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.Contains("adapter 'lines' failed with System.ArgumentOutOfRangeException", check.Detail, StringComparison.Ordinal);
        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
    }

    [Fact]
    public void Not_verified_is_never_reported_as_ok_across_every_outcome_mix()
    {
        using TempCorpus stored = TempCorpus.Stored();
        stored.Build();
        using TempCorpus external = TempCorpus.WithExternal();
        external.Build();

        VerificationReport[] reports =
        [
            Verify(stored),
            Verify(external),
            Verify(external, new VerificationOptions { Rebuild = true }),
            Verify(external, new VerificationOptions { Rebuild = true, Adapters = [new LinesAdapter()] }),
        ];

        Assert.All(reports, AssertNeverOkWhileAnythingIsNot);
        Assert.All(reports.SelectMany(r => r.Checks).Where(c => c.Outcome == VerificationOutcome.NotVerified), c => Assert.NotEmpty(c.Detail));
    }
}

/// <summary>
/// Verification binds every byte a corpus ships (decision 0007): corpus.json is exactly the
/// writer's form, corpus.build.json is exactly the bytes buildDigest names, and the build
/// definition declares exactly what the manifest records.
/// </summary>
public class BuildBindingTests
{
    private static VerificationReport Verify(TempCorpus corpus) =>
        CorpusVerifier.Verify(CorpusFiles.FromDirectory(corpus.Root));

    private static VerificationCheck Check(VerificationReport report, string name) =>
        Assert.Single(report.Checks, c => c.Name == name);

    [Fact]
    public void A_build_definition_rewritten_after_the_build_fails()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        corpus.WriteText("corpus.build.json", """
            {"schema":"rules-corpus/build/1","corpusId":"example",
             "sources":[{"id":"zzz","path":"elsewhere/z.txt","mediaType":"application/pdf","origin":"someone else"}],
             "derivations":[],"external":[],"baselines":[]}
            """);

        VerificationReport report = Verify(corpus);

        VerificationCheck check = Check(report, "build-definition");
        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.Contains("buildDigest", check.Detail, StringComparison.Ordinal);
        Assert.Contains("$.sources[0]", check.Detail, StringComparison.Ordinal);
        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
    }

    [Fact]
    public void A_build_definition_differing_only_in_whitespace_fails_its_digest()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        corpus.WriteBytes("corpus.build.json", [.. corpus.ReadBytes("corpus.build.json"), (byte)'\n']);

        VerificationCheck check = Check(Verify(corpus), "build-definition");

        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.Contains("the manifest records buildDigest", check.Detail, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Edits() => new()
    {
        { "source origin", "$.sources[0]" },
        { "source notes", "$.sources[0]" },
        { "source retrieved", "$.sources[0]" },
        { "source media type", "$.sources[0]" },
        { "source path", "$.sources[0]" },
        { "source id", "$.sources[0]" },
        { "an extra source", "$" },
        { "adapter parameters", "$.derivations[0]" },
        { "adapter id", "$.derivations[0]" },
        { "adapter input", "$.derivations[0]" },
        { "output path", "$.derivations[0].output" },
        { "derivation id", "$.derivations[0]" },
        { "baseline hash derivation", "$.baselines[0]" },
        { "baseline as-of", "$.baselines[0]" },
        { "a removed baseline", "$.baselines" },
        { "corpus id", "$.corpusId" },
    };

    [Theory]
    [MemberData(nameof(Edits))]
    public void A_build_definition_bound_by_digest_that_declares_something_else_fails(string edit, string path)
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        corpus.EditBuild(d =>
        {
            JsonNode source = d["sources"]![0]!;
            JsonNode derivation = d["derivations"]![0]!;
            JsonNode baseline = d["baselines"]![0]!;
            switch (edit)
            {
                case "source origin": source["origin"] = "someone else"; break;
                case "source notes": source["notes"] = "added later"; break;
                case "source retrieved": source["retrieved"] = "2026-01-03"; break;
                case "source media type": source["mediaType"] = "text/markdown"; break;
                case "source path": source["path"] = "sources/other.txt"; break;
                case "source id": source["id"] = "renamed"; derivation["input"] = "renamed"; baseline["artifact"] = "renamed"; break;
                case "an extra source": d["sources"]!.AsArray().Add(new JsonObject { ["id"] = "more", ["path"] = "sources/more.txt", ["mediaType"] = "text/plain", ["origin"] = "o" }); break;
                case "adapter parameters": derivation["parameters"] = new JsonObject { ["prefix"] = "x" }; break;
                case "adapter id": derivation["adapter"] = "other"; break;
                case "adapter input": d["sources"]!.AsArray().Insert(0, new JsonObject { ["id"] = "first", ["path"] = "sources/first.txt", ["mediaType"] = "text/plain", ["origin"] = "o" }); derivation["input"] = "first"; break;
                case "output path": derivation["output"]!["path"] = "canonical/moved.txt"; break;
                case "derivation id": derivation["id"] = "renamed-lines"; break;
                case "baseline hash derivation": baseline["hashDerivation"] = "notes-other"; break;
                case "baseline as-of": baseline.AsObject().Remove("asOf"); break;
                case "a removed baseline": d["baselines"]!.AsArray().Clear(); break;
                case "corpus id": d["corpusId"] = "another"; break;
                default: throw new ArgumentException(edit);
            }
        });
        ManifestEdits.BindToCurrentBuild(corpus);

        VerificationReport report = Verify(corpus);

        VerificationCheck check = Check(report, "build-definition");
        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.DoesNotContain("buildDigest", check.Detail, StringComparison.Ordinal);
        Assert.Contains(path + ":", check.Detail, StringComparison.Ordinal);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "manifest-digest").Outcome);
    }

    [Fact]
    public void An_external_derivation_that_disagrees_with_the_manifest_fails()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();
        corpus.EditBuild(d => d["external"]![0]!["tool"]!["version"] = "25.01.0");
        ManifestEdits.BindToCurrentBuild(corpus);

        VerificationCheck check = Check(Verify(corpus), "build-definition");

        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.Contains("$.external[0]:", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rebound_but_unchanged_build_definition_still_verifies()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();
        corpus.EditBuild(_ => { });
        ManifestEdits.BindToCurrentBuild(corpus);

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Ok, Check(report, "build-definition").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "manifest-form").Outcome);
    }

    [Fact]
    public void A_manifest_reformatted_but_otherwise_identical_fails_its_form()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        corpus.WriteBytes("corpus.json", ManifestEdits.Bytes(ManifestEdits.Read(corpus)));

        VerificationReport report = Verify(corpus);

        Assert.Equal(VerificationOutcome.Failed, Check(report, "manifest-form").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Check(report, "manifest-digest").Outcome);
        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
    }

    [Fact]
    public void Two_verified_packs_of_one_manifest_are_byte_identical()
    {
        using TempCorpus a = TempCorpus.WithExternal();
        using TempCorpus b = TempCorpus.WithExternal();
        a.Build();
        b.Build();
        var packA = new MemoryStream();
        var packB = new MemoryStream();

        CorpusPacker.Pack(a.Root, packA, allowNotVerified: true);
        CorpusPacker.Pack(b.Root, packB, allowNotVerified: true);

        Assert.Equal(packA.ToArray(), packB.ToArray());
    }
}
