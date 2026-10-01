using System.Text;
using System.Text.Json;

namespace RulesCorpus.Cli.Tests;

public sealed class InspectDiffPackTests
{
    [Fact]
    public void Inspect_prints_the_metadata_and_the_exact_text()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("regulatory");
        CliResult r = s.Run("inspect", "107.9", "--corpus", dir);
        Assert.Equal(0, r.Exit);

        ManifestSegment segment = Segment(dir, "107.9");
        string text = SegmentText(dir, "canonical/14-cfr-107-excerpt.txt", segment);
        string expected =
            "segment   107.9\n"
            + "artifact  cfr-14-107-canonical\n"
            + $"start     {segment.Start}\n"
            + $"length    {segment.Length}\n"
            + $"digest    {segment.Digest}\n"
            + "locator   § 107.9 Safety event reporting.\n"
            + $"source    cfr-14-107-text bytes [{segment.Start}, {segment.Start + segment.Length})\n"
            + "\n"
            + text + "\n";
        Assert.Equal(expected, r.Stdout);
        Assert.StartsWith("§ 107.9 Safety event reporting.\nNo later than 10 calendar days", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_json_carries_the_segment_record_and_its_exact_text_from_a_packed_corpus()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        Assert.Equal(0, s.Run("pack", "book.tar", "--dir", dir, "--allow-not-verified").Exit);

        CliResult r = s.Run("inspect", "p191.b1", "--corpus", "book.tar", "--json");
        Assert.Equal(0, r.Exit);
        ManifestSegment segment = Segment(dir, "p191.b1");
        JsonElement json = r.Json;
        Assert.Equal("inspect", json.GetProperty("command").GetString());
        JsonElement record = json.GetProperty("segment");
        Assert.Equal("p191.b1", record.GetProperty("id").GetString());
        Assert.Equal("srd-canonical", record.GetProperty("artifact").GetString());
        Assert.Equal(segment.Start, record.GetProperty("start").GetInt64());
        Assert.Equal(segment.Length, record.GetProperty("length").GetInt64());
        Assert.Equal(segment.Digest.ToString(), record.GetProperty("digest").GetString());
        Assert.Equal("p. 191", record.GetProperty("locator").GetString());
        JsonElement source = record.GetProperty("sources")[0];
        Assert.Equal("srd-text", source.GetProperty("artifact").GetString());
        Assert.Equal(191, source.GetProperty("pages").GetProperty("from").GetInt32());
        Assert.Equal(SegmentText(dir, "canonical/srd-5.2.1-excerpt.txt", segment), json.GetProperty("text").GetString());
    }

    [Fact]
    public void Inspect_refuses_an_unknown_segment_or_bytes_that_do_not_match()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("regulatory");
        CliResult unknown = s.Run("inspect", "999.9", "--corpus", dir);
        Assert.Equal(1, unknown.Exit);
        Assert.Contains("has no segment '999.9'", unknown.Stderr, StringComparison.Ordinal);

        File.AppendAllText(Path.Combine(dir, "canonical/14-cfr-107-excerpt.txt"), "x");
        CliResult tampered = s.Run("inspect", "107.9", "--corpus", dir);
        Assert.Equal(1, tampered.Exit);
        Assert.Empty(tampered.Stdout);
        Assert.Contains("does not match the manifest", tampered.Stderr, StringComparison.Ordinal);
        tampered.AssertNoStackTrace();
    }

    [Fact]
    public void Diff_of_two_builds_says_both_identities_are_equal()
    {
        using var s = new Scratch();
        string one = s.BuiltSample("regulatory", "one");
        string two = s.BuiltSample("regulatory", "two");
        CliResult r = s.Run("diff", one, two);
        Assert.Equal(0, r.Exit);
        Assert.Contains("contentDigest   equal", r.Stdout, StringComparison.Ordinal);
        Assert.Contains("manifestDigest  equal", r.Stdout, StringComparison.Ordinal);
        Assert.EndsWith("no differences\n", r.Stdout, StringComparison.Ordinal);

        CliResult json = s.Run("diff", one, two, "--json");
        Assert.True(json.Json.GetProperty("contentDigestEqual").GetBoolean());
        Assert.True(json.Json.GetProperty("manifestDigestEqual").GetBoolean());
        Assert.Equal(0, json.Json.GetProperty("segments").GetProperty("changed").GetArrayLength());
        Assert.Equal(0, json.Json.GetProperty("derivations").GetProperty("changed").GetArrayLength());
    }

    [Fact]
    public void Diff_shows_a_derivation_whose_parameters_changed_when_no_artifact_or_segment_did()
    {
        using var s = new Scratch();
        string before = s.BuiltSample("regulatory", "before");
        string changed = s.FreshSample("regulatory", "changed");
        string definition = Path.Combine(changed, "corpus.build.json");
        File.WriteAllText(definition, File.ReadAllText(definition).Replace("107\\\\.[0-9]+", "107\\\\.[0-9]{1,}", StringComparison.Ordinal));
        Assert.Equal(0, s.Run("build", "--dir", changed).Exit);

        CliResult human = s.Run("diff", before, changed);
        Assert.Contains("~ derivation cfr-14-107-sections", human.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("~ segment", human.Stdout, StringComparison.Ordinal);
        JsonElement json = s.Run("diff", before, changed, "--json").Json;
        Assert.Equal("cfr-14-107-sections", json.GetProperty("derivations").GetProperty("changed")[0].GetProperty("after").GetProperty("id").GetString());
    }

    [Fact]
    public void Diff_separates_a_metadata_change_from_a_content_change()
    {
        using var s = new Scratch();
        string before = s.BuiltSample("regulatory", "before");
        string metadata = s.FreshSample("regulatory", "metadata");
        string definition = Path.Combine(metadata, "corpus.build.json");
        File.WriteAllText(definition, File.ReadAllText(definition).Replace("\"origin\": \"https://", "\"origin\": \"http://", StringComparison.Ordinal));
        Assert.Equal(0, s.Run("build", "--dir", metadata).Exit);

        JsonElement meta = s.Run("diff", before, metadata, "--json").Json;
        Assert.True(meta.GetProperty("contentDigestEqual").GetBoolean());
        Assert.False(meta.GetProperty("manifestDigestEqual").GetBoolean());
        Assert.Equal("cfr-14-107-text", meta.GetProperty("artifacts").GetProperty("changed")[0].GetProperty("after").GetProperty("id").GetString());

        string content = s.FreshSample("regulatory", "content");
        string source = Path.Combine(content, "sources/14-cfr-107-excerpt.txt");
        File.WriteAllText(source, File.ReadAllText(source).Replace("10 calendar days", "11 calendar days", StringComparison.Ordinal));
        Assert.Equal(0, s.Run("build", "--dir", content).Exit);

        CliResult human = s.Run("diff", before, content);
        Assert.Equal(0, human.Exit);
        Assert.Contains("contentDigest   DIFFERENT", human.Stdout, StringComparison.Ordinal);
        Assert.Contains("~ segment 107.9", human.Stdout, StringComparison.Ordinal);
        Assert.Contains("~ baseline cfr-14-107-excerpt", human.Stdout, StringComparison.Ordinal);
        JsonElement json = s.Run("diff", before, content, "--json").Json;
        Assert.False(json.GetProperty("contentDigestEqual").GetBoolean());
        Assert.Equal("107.9", json.GetProperty("segments").GetProperty("changed")[0].GetProperty("before").GetProperty("id").GetString());
    }

    [Fact]
    public void Diff_accepts_a_packed_corpus_and_refuses_a_missing_one()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("regulatory");
        Assert.Equal(0, s.Run("pack", "r.tar", "--dir", dir).Exit);
        JsonElement json = s.Run("diff", dir, "r.tar", "--json").Json;
        Assert.True(json.GetProperty("manifestDigestEqual").GetBoolean());

        Assert.Equal(1, s.Run("diff", dir, "missing.tar").Exit);
    }

    [Fact]
    public void Pack_writes_the_same_bytes_twice_and_the_archive_verifies()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("regulatory");
        CliResult first = s.Run("pack", "a.tar", "--dir", dir, "--json");
        Assert.Equal(0, first.Exit);
        Assert.Equal(0, s.Run("pack", "b.tar", "--dir", dir).Exit);
        Assert.Equal(s.Read("a.tar"), s.Read("b.tar"));

        JsonElement json = first.Json;
        Assert.Equal("pack", json.GetProperty("command").GetString());
        Assert.Equal(s.PathOf("a.tar"), json.GetProperty("path").GetString());
        Assert.Equal(s.Read("a.tar").LongLength, json.GetProperty("bytes").GetInt64());
        Assert.Equal(ContentDigest.Compute(s.Read("a.tar")).ToString(), json.GetProperty("digest").GetString());
        Assert.Equal("ok", json.GetProperty("outcome").GetString());

        CliResult verified = s.Run("verify", "a.tar");
        Assert.Equal(0, verified.Exit);
        Assert.Contains("ok            package:", verified.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_exits_3_without_allow_not_verified_and_writes_nothing()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("rulebook");
        CliResult r = s.Run("pack", "book.tar", "--dir", dir, "--json");
        Assert.Equal(3, r.Exit);
        Assert.False(File.Exists(s.PathOf("book.tar")));
        Assert.Contains("--allow-not-verified", r.Stderr, StringComparison.Ordinal);
        Assert.Equal("artifact srd-pdf", r.Json.GetProperty("error").GetProperty("problems")[0].GetProperty("path").GetString());

        CliResult allowed = s.Run("pack", "book.tar", "--dir", dir, "--allow-not-verified");
        Assert.Equal(0, allowed.Exit);
        Assert.Contains("not-verified  artifact srd-pdf", allowed.Stdout, StringComparison.Ordinal);
        Assert.Equal(3, s.Run("verify", "book.tar").Exit);
    }

    [Fact]
    public void Pack_refuses_a_failed_corpus_and_an_existing_output()
    {
        using var s = new Scratch();
        string dir = s.BuiltSample("regulatory");
        s.Write("taken.tar", "keep me");
        CliResult existing = s.Run("pack", "taken.tar", "--dir", dir);
        Assert.Equal(1, existing.Exit);
        Assert.Equal("keep me", s.ReadText("taken.tar"));

        File.AppendAllText(Path.Combine(dir, "canonical/14-cfr-107-excerpt.txt"), "x");
        CliResult failed = s.Run("pack", "failed.tar", "--dir", dir, "--allow-not-verified");
        Assert.Equal(1, failed.Exit);
        Assert.Contains("failed verification", failed.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(s.PathOf("failed.tar")));
        failed.AssertNoStackTrace();
    }

    private static ManifestSegment Segment(string dir, string id) =>
        CorpusManifest.Load(CorpusFiles.FromDirectory(dir)).Segments.Single(x => x.Id == id);

    private static string SegmentText(string dir, string path, ManifestSegment segment) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, path)), (int)segment.Start, (int)segment.Length);
}
