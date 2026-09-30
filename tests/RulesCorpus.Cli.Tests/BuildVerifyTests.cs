using System.Text.Json;

namespace RulesCorpus.Cli.Tests;

public sealed class BuildVerifyTests
{
    [Theory]
    [InlineData("regulatory", "canonical/14-cfr-107-excerpt.txt")]
    [InlineData("rulebook", "canonical/srd-5.2.1-excerpt.txt")]
    public void Build_reproduces_the_committed_sample_byte_for_byte(string sample, string output)
    {
        using var s = new Scratch();
        string dir = s.FreshSample(sample);
        CliResult r = s.Run("build", "--dir", dir);
        Assert.Equal(0, r.Exit);
        string committed = Path.Combine(Scratch.SamplesRoot, sample);
        foreach (string file in new[] { "corpus.json", output })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(committed, file)), File.ReadAllBytes(Path.Combine(dir, file)));
        }
    }

    [Fact]
    public void Build_json_reports_both_identities_and_every_artifact()
    {
        using var s = new Scratch();
        string dir = s.FreshSample("regulatory");
        CliResult r = s.Run("build", "--dir", dir, "--json");
        Assert.Equal(0, r.Exit);
        CorpusManifest manifest = CorpusManifest.Load(CorpusFiles.FromDirectory(dir));
        JsonElement json = r.Json;
        Assert.Equal("build", json.GetProperty("command").GetString());
        Assert.Equal("cfr-14-107-excerpt", json.GetProperty("corpusId").GetString());
        Assert.Equal(manifest.ContentDigest.ToString(), json.GetProperty("contentDigest").GetString());
        Assert.Equal(manifest.ManifestDigest.ToString(), json.GetProperty("manifestDigest").GetString());
        Assert.Equal(5, json.GetProperty("segments").GetInt32());
        JsonElement[] artifacts = [.. json.GetProperty("artifacts").EnumerateArray()];
        Assert.Equal(["cfr-14-107-text", "cfr-14-107-canonical"], artifacts.Select(a => a.GetProperty("id").GetString()));
        Assert.Equal("derived", artifacts[1].GetProperty("role").GetString());
    }

    [Fact]
    public void Build_defaults_to_the_working_directory()
    {
        using var s = new Scratch();
        s.FreshSample("regulatory", ".");
        CliResult r = s.Run("build");
        Assert.Equal(0, r.Exit);
        Assert.True(File.Exists(s.PathOf("corpus.json")));
    }

    [Fact]
    public void Build_refusals_print_every_error_and_no_stack_trace()
    {
        using var s = new Scratch();
        string dir = s.FreshSample("regulatory");
        string definition = File.ReadAllText(Path.Combine(dir, "corpus.build.json"))
            .Replace("\"segmentation\": \"headings\"", "\"segmentation\": \"headings\",\n        \"frobnicate\": \"yes\"", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, "corpus.build.json"), definition);

        CliResult r = s.Run("build", "--dir", dir, "--json");
        Assert.Equal(1, r.Exit);
        Assert.Contains("frobnicate", r.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(dir, "corpus.json")));
        Assert.Equal(1, r.Json.GetProperty("exitCode").GetInt32());
        Assert.True(r.Json.GetProperty("error").GetProperty("problems").GetArrayLength() >= 1);
        r.AssertNoStackTrace();
    }

    [Fact]
    public void Build_of_a_missing_directory_is_refused()
    {
        using var s = new Scratch();
        CliResult r = s.Run("build", "--dir", "nowhere");
        Assert.Equal(1, r.Exit);
        r.AssertNoStackTrace();
    }

    [Fact]
    public void Verify_rebuild_of_a_fully_stored_corpus_is_ok_and_prints_every_check()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("regulatory");
        CliResult r = s.Run("verify", dir, "--rebuild");
        Assert.Equal(0, r.Exit);
        VerificationReport report = CorpusVerifier.Verify(
            CorpusFiles.FromDirectory(dir),
            new VerificationOptions { Rebuild = true, Adapters = [new Adapters.Text.TextAdapter()] });
        string[] lines = r.Stdout.TrimEnd('\n').Split('\n');
        Assert.Equal(report.Checks.Count + 1, lines.Length);
        for (int i = 0; i < report.Checks.Count; i++)
        {
            Assert.Equal($"ok            {report.Checks[i].Name}: {report.Checks[i].Detail}", lines[i]);
        }

        Assert.Contains("rebuild cfr-14-107-sections", r.Stdout, StringComparison.Ordinal);
        Assert.EndsWith($"ok ({report.Checks.Count} checks: {report.Checks.Count} ok, 0 not verified, 0 failed)", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_json_lists_every_check_with_name_outcome_and_detail()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        CliResult r = s.Run("verify", dir, "--rebuild", "--json");
        Assert.Equal(3, r.Exit);
        JsonElement json = r.Json;
        Assert.Equal("verify", json.GetProperty("command").GetString());
        Assert.Equal("not-verified", json.GetProperty("outcome").GetString());
        Assert.Equal(3, json.GetProperty("exitCode").GetInt32());
        Assert.Equal(dir, json.GetProperty("target").GetString());
        JsonElement[] checks = [.. json.GetProperty("checks").EnumerateArray()];
        Assert.All(checks, c =>
        {
            Assert.Equal(3, c.EnumerateObject().Count());
            Assert.Contains(c.GetProperty("outcome").GetString(), new[] { "ok", "not-verified", "failed" });
            Assert.False(string.IsNullOrEmpty(c.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrEmpty(c.GetProperty("detail").GetString()));
        });
        string[] notVerified = [.. checks.Where(c => c.GetProperty("outcome").GetString() == "not-verified").Select(c => c.GetProperty("name").GetString()!)];
        Assert.Equal(["artifact srd-pdf", "rebuild srd-extraction"], notVerified);
        JsonElement counts = json.GetProperty("counts");
        Assert.Equal(checks.Length - 2, counts.GetProperty("ok").GetInt32());
        Assert.Equal(2, counts.GetProperty("notVerified").GetInt32());
        Assert.Equal(0, counts.GetProperty("failed").GetInt32());
    }

    [Fact]
    public void Verify_exits_3_for_not_verified_checks_unless_they_are_allowed()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        CliResult strict = s.Run("verify", dir);
        Assert.Equal(3, strict.Exit);
        Assert.Contains("not-verified  artifact srd-pdf: declared by identity only", strict.Stdout, StringComparison.Ordinal);
        Assert.Contains("--allow-not-verified", strict.Stderr, StringComparison.Ordinal);

        CliResult allowed = s.Run("verify", dir, "--allow-not-verified");
        Assert.Equal(0, allowed.Exit);
        Assert.Contains("not-verified  artifact srd-pdf", allowed.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_exits_1_when_any_check_fails_even_with_allow_not_verified()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        File.AppendAllText(Path.Combine(dir, "canonical/srd-5.2.1-excerpt.txt"), "tampered");
        CliResult r = s.Run("verify", dir, "--allow-not-verified");
        Assert.Equal(1, r.Exit);
        Assert.Contains("failed        artifact srd-canonical", r.Stdout, StringComparison.Ordinal);
    }

    private const string RulebookNotVerified = "artifact srd-pdf,rebuild srd-extraction";

    [Fact]
    public void Verify_exits_0_when_exactly_the_expected_checks_are_not_verified()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        CliResult r = s.Run("verify", dir, "--rebuild", "--expect-not-verified", RulebookNotVerified, "--json");
        Assert.Equal(0, r.Exit);
        JsonElement expectation = r.Json.GetProperty("expectedNotVerified");
        Assert.True(expectation.GetProperty("met").GetBoolean());
        Assert.Equal(["artifact srd-pdf", "rebuild srd-extraction"], expectation.GetProperty("expected").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(expectation.GetProperty("unexpected").EnumerateArray());
        Assert.Empty(expectation.GetProperty("missing").EnumerateArray());
        Assert.Equal("not-verified", r.Json.GetProperty("outcome").GetString());
        Assert.Equal(0, r.Json.GetProperty("exitCode").GetInt32());

        CliResult human = s.Run("verify", dir, "--rebuild", "--expect-not-verified", RulebookNotVerified);
        Assert.Equal(0, human.Exit);
        Assert.Contains("expected not verified: met (2 named, and no others)", human.Stdout, StringComparison.Ordinal);
        Assert.Equal("", human.Stderr);
    }

    [Fact]
    public void Verify_exits_1_when_a_not_verified_check_was_not_expected()
    {
        // The hole --allow-not-verified leaves: a consumer that pinned only the PDF must not
        // silently accept an unverified extraction as well.
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        CliResult r = s.Run("verify", dir, "--rebuild", "--expect-not-verified", "artifact srd-pdf", "--json");
        Assert.Equal(1, r.Exit);
        JsonElement expectation = r.Json.GetProperty("expectedNotVerified");
        Assert.False(expectation.GetProperty("met").GetBoolean());
        Assert.Equal(["rebuild srd-extraction"], expectation.GetProperty("unexpected").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(expectation.GetProperty("missing").EnumerateArray());
        Assert.Equal(1, r.Json.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Verify_exits_1_when_an_expected_check_is_not_reported_not_verified()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");

        // Without --rebuild there is no rebuild check, so the pin describes a different run.
        CliResult r = s.Run("verify", dir, "--expect-not-verified", RulebookNotVerified);
        Assert.Equal(1, r.Exit);
        Assert.Contains("expected not verified: NOT met; expected but not reported not verified: rebuild srd-extraction", r.Stdout, StringComparison.Ordinal);

        string regulatory = s.BuiltSample("regulatory");
        CliResult verified = s.Run("verify", regulatory, "--expect-not-verified", "artifact srd-pdf", "--json");
        Assert.Equal(1, verified.Exit);
        Assert.Equal(["artifact srd-pdf"], verified.Json.GetProperty("expectedNotVerified").GetProperty("missing").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Verify_exits_1_when_a_check_fails_even_if_the_expectation_is_met()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        File.AppendAllText(Path.Combine(dir, "canonical/srd-5.2.1-excerpt.txt"), "tampered");
        CliResult r = s.Run("verify", dir, "--rebuild", "--expect-not-verified", RulebookNotVerified);
        Assert.Equal(1, r.Exit);
        Assert.Contains("failed        artifact srd-canonical", r.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("artifact srd-pdf,")]
    [InlineData(",artifact srd-pdf")]
    [InlineData("artifact srd-pdf, rebuild srd-extraction")]
    [InlineData("artifact srd-pdf,artifact srd-pdf")]
    [InlineData("")]
    public void Verify_refuses_a_malformed_expectation_as_a_usage_error(string value)
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        CliResult r = s.Run("verify", dir, "--expect-not-verified", value);
        Assert.Equal(2, r.Exit);
        r.AssertNoStackTrace();
    }

    [Fact]
    public void Verify_refuses_an_expectation_combined_with_allow_not_verified()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        CliResult r = s.Run("verify", dir, "--allow-not-verified", "--expect-not-verified", RulebookNotVerified);
        Assert.Equal(2, r.Exit);
        Assert.Contains("cannot be combined", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_defaults_to_the_working_directory_and_refuses_a_missing_target()
    {
        using var s = new Scratch();
        s.FreshSample("regulatory", ".");
        Assert.Equal(0, s.Run("build").Exit);
        Assert.Equal(0, s.Run("verify").Exit);

        CliResult missing = s.Run("verify", "nowhere");
        Assert.Equal(1, missing.Exit);
        Assert.Contains("neither a corpus directory nor a packed corpus", missing.Stderr, StringComparison.Ordinal);
    }
}
