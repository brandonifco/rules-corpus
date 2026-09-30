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
            ["schema", "uniqueness", "references", "derivations", "baselines", "segments", "source-spans", "limits", "content-digest", "manifest-digest",
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
