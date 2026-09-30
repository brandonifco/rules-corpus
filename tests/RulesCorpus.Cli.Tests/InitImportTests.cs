using System.Text.Json;

namespace RulesCorpus.Cli.Tests;

public sealed class InitImportTests
{
    private const string Skeleton =
        "{\n  \"baselines\": [],\n  \"corpusId\": \"example\",\n  \"derivations\": [],\n  \"external\": [],\n"
        + "  \"schema\": \"rules-corpus/build/1\",\n  \"sources\": []\n}\n";

    [Fact]
    public void Init_creates_the_directory_and_a_skeleton_build_definition()
    {
        using var s = new Scratch();
        CliResult r = s.Run("init", "new/corpus", "--corpus-id", "example");
        Assert.Equal(0, r.Exit);
        Assert.Equal(Skeleton, s.ReadText("new/corpus/corpus.build.json"));
        Assert.Contains("created ", r.Stdout, StringComparison.Ordinal);
        Assert.Empty(r.Stderr);
    }

    [Fact]
    public void Init_refuses_an_existing_build_definition_and_leaves_it_alone()
    {
        using var s = new Scratch();
        s.Write("c/corpus.build.json", "hand-written");
        CliResult r = s.Run("init", "c", "--corpus-id", "example");
        Assert.Equal(1, r.Exit);
        Assert.Contains("already exists", r.Stderr, StringComparison.Ordinal);
        Assert.Equal("hand-written", s.ReadText("c/corpus.build.json"));
        r.AssertNoStackTrace();
    }

    [Fact]
    public void Init_refuses_an_invalid_corpus_id_as_a_usage_error()
    {
        using var s = new Scratch();
        CliResult r = s.Run("init", "c", "--corpus-id", "Not_An_Id");
        Assert.Equal(2, r.Exit);
        Assert.False(Directory.Exists(s.PathOf("c")));
    }

    [Fact]
    public void Build_refuses_the_skeleton_until_a_source_is_imported()
    {
        using var s = new Scratch();
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        CliResult r = s.Run("build", "--dir", "c");
        Assert.Equal(1, r.Exit);
        Assert.Contains("$.sources", r.Stderr, StringComparison.Ordinal);
        r.AssertNoStackTrace();
    }

    [Fact]
    public void Import_copies_the_file_and_appends_a_stored_source_without_a_retrieved_date()
    {
        using var s = new Scratch();
        s.Write("inbox/notes.txt", "Alpha.\n\nBeta.\n");
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        CliResult r = s.Run("import", "inbox/notes.txt", "--dir", "c", "--id", "notes", "--media-type", "text/plain", "--origin", "a person");
        Assert.Equal(0, r.Exit);
        Assert.Equal("Alpha.\n\nBeta.\n", s.ReadText("c/sources/notes.txt"));

        string definition = s.ReadText("c/corpus.build.json");
        Assert.Equal(
            "{\n  \"baselines\": [],\n  \"corpusId\": \"example\",\n  \"derivations\": [],\n  \"external\": [],\n"
            + "  \"schema\": \"rules-corpus/build/1\",\n  \"sources\": [\n    {\n      \"id\": \"notes\",\n"
            + "      \"mediaType\": \"text/plain\",\n      \"origin\": \"a person\",\n      \"path\": \"sources/notes.txt\"\n"
            + "    }\n  ]\n}\n",
            definition);
        Assert.DoesNotContain("retrieved", definition, StringComparison.Ordinal);

        // The definition import wrote is one build accepts.
        CliResult built = s.Run("build", "--dir", "c", "--json");
        Assert.Equal(0, built.Exit);
        Assert.DoesNotContain("retrieved", s.ReadText("c/corpus.json"), StringComparison.Ordinal);
    }

    [Fact]
    public void Import_writes_the_retrieved_date_only_when_given()
    {
        using var s = new Scratch();
        s.Write("a.txt", "A.\n");
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        CliResult r = s.Run("import", "a.txt", "--dir", "c", "--id", "a", "--media-type", "text/plain", "--origin", "o", "--retrieved", "2026-02-03", "--json");
        Assert.Equal(0, r.Exit);
        Assert.Contains("\"retrieved\": \"2026-02-03\"", s.ReadText("c/corpus.build.json"), StringComparison.Ordinal);
        Assert.Equal("2026-02-03", r.Json.GetProperty("source").GetProperty("retrieved").GetString());
    }

    [Fact]
    public void Import_json_reports_the_source_it_added()
    {
        using var s = new Scratch();
        s.Write("a.txt", "A.\n");
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        CliResult r = s.Run("import", "a.txt", "--dir", "c", "--id", "a", "--media-type", "text/plain", "--origin", "o", "--path", "raw/a-copy.txt", "--json");
        Assert.Equal(0, r.Exit);
        JsonElement source = r.Json.GetProperty("source");
        Assert.Equal("import", r.Json.GetProperty("command").GetString());
        Assert.Equal("a", source.GetProperty("id").GetString());
        Assert.Equal("raw/a-copy.txt", source.GetProperty("path").GetString());
        Assert.Equal(3, source.GetProperty("bytes").GetInt64());
        Assert.Equal(ContentDigest.Compute("A.\n"u8).ToString(), source.GetProperty("digest").GetString());
        Assert.False(source.TryGetProperty("retrieved", out _));
        Assert.True(File.Exists(s.PathOf("c/raw/a-copy.txt")));
    }

    [Fact]
    public void Import_rewrites_the_definition_deterministically()
    {
        using var s = new Scratch();
        s.Write("a.txt", "A.\n");
        s.Write("b.txt", "B.\n");
        foreach (string dir in new[] { "one", "two" })
        {
            Assert.Equal(0, s.Run("init", dir, "--corpus-id", "example").Exit);
            Assert.Equal(0, s.Run("import", "a.txt", "--dir", dir, "--id", "a", "--media-type", "text/plain", "--origin", "o").Exit);
            Assert.Equal(0, s.Run("import", "b.txt", "--dir", dir, "--id", "b", "--media-type", "text/plain", "--origin", "o").Exit);
        }

        Assert.Equal(s.Read("one/corpus.build.json"), s.Read("two/corpus.build.json"));
        string text = s.ReadText("one/corpus.build.json");
        Assert.True(text.IndexOf("\"a\"", StringComparison.Ordinal) < text.IndexOf("\"b\"", StringComparison.Ordinal), "sources keep their order");
    }

    [Fact]
    public void Import_refuses_an_id_already_in_use_and_writes_nothing()
    {
        using var s = new Scratch();
        s.Write("a.txt", "A.\n");
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        Assert.Equal(0, s.Run("import", "a.txt", "--dir", "c", "--id", "a", "--media-type", "text/plain", "--origin", "o").Exit);
        byte[] before = s.Read("c/corpus.build.json");

        CliResult r = s.Run("import", "a.txt", "--dir", "c", "--id", "a", "--media-type", "text/plain", "--origin", "o", "--path", "other/a.txt");
        Assert.Equal(1, r.Exit);
        Assert.Contains("already uses the id 'a'", r.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, s.Read("c/corpus.build.json"));
        Assert.False(File.Exists(s.PathOf("c/other/a.txt")));
        r.AssertNoStackTrace();
    }

    [Fact]
    public void Import_refuses_a_path_that_collides_ignoring_case()
    {
        using var s = new Scratch();
        s.Write("a.txt", "A.\n");
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        Assert.Equal(0, s.Run("import", "a.txt", "--dir", "c", "--id", "a", "--media-type", "text/plain", "--origin", "o").Exit);
        CliResult r = s.Run("import", "a.txt", "--dir", "c", "--id", "b", "--media-type", "text/plain", "--origin", "o", "--path", "Sources/A.txt");
        Assert.Equal(1, r.Exit);
        Assert.Contains("collides", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_never_overwrites_an_existing_file()
    {
        using var s = new Scratch();
        s.Write("a.txt", "A.\n");
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        s.Write("c/sources/a.txt", "already here");
        CliResult r = s.Run("import", "a.txt", "--dir", "c", "--id", "a", "--media-type", "text/plain", "--origin", "o");
        Assert.Equal(1, r.Exit);
        Assert.Contains("never overwrites", r.Stderr, StringComparison.Ordinal);
        Assert.Equal("already here", s.ReadText("c/sources/a.txt"));
        Assert.Equal(Skeleton, s.ReadText("c/corpus.build.json"));
    }

    [Fact]
    public void Import_refuses_a_missing_file_or_a_directory_without_a_build_definition()
    {
        using var s = new Scratch();
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        CliResult missing = s.Run("import", "nope.txt", "--dir", "c", "--id", "a", "--media-type", "text/plain", "--origin", "o");
        Assert.Equal(1, missing.Exit);
        Assert.Contains("does not exist", missing.Stderr, StringComparison.Ordinal);

        s.Write("a.txt", "A.\n");
        Directory.CreateDirectory(s.PathOf("empty"));
        CliResult noDefinition = s.Run("import", "a.txt", "--dir", "empty", "--id", "a", "--media-type", "text/plain", "--origin", "o", "--json");
        Assert.Equal(1, noDefinition.Exit);
        Assert.Contains("rules-corpus init", noDefinition.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, noDefinition.Json.GetProperty("exitCode").GetInt32());
        Assert.Equal("corpus.build.json", noDefinition.Json.GetProperty("error").GetProperty("problems")[0].GetProperty("path").GetString());
        Assert.False(File.Exists(s.PathOf("empty/sources/a.txt")));
    }

    [Theory]
    [InlineData("--id", "Bad_Id")]
    [InlineData("--media-type", "text/plain; charset=utf-8")]
    [InlineData("--retrieved", "2026-02-30")]
    [InlineData("--retrieved", "yesterday")]
    [InlineData("--path", "../outside.txt")]
    [InlineData("--path", "corpus.json")]
    [InlineData("--origin", "")]
    public void Import_refuses_a_malformed_argument_as_a_usage_error(string option, string value)
    {
        using var s = new Scratch();
        s.Write("a.txt", "A.\n");
        Assert.Equal(0, s.Run("init", "c", "--corpus-id", "example").Exit);
        var args = new Dictionary<string, string>
        {
            ["--dir"] = "c",
            ["--id"] = "a",
            ["--media-type"] = "text/plain",
            ["--origin"] = "o",
        };
        args[option] = value;
        CliResult r = s.Run(["import", "a.txt", .. args.SelectMany(p => new[] { p.Key, p.Value })]);
        Assert.Equal(2, r.Exit);
        Assert.Equal(Skeleton, s.ReadText("c/corpus.build.json"));
    }
}
