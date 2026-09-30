using System.Reflection;
using System.Text.Json;

namespace RulesCorpus.Cli.Tests;

public sealed class UsageTests
{
    [Fact]
    public void No_arguments_prints_usage_and_exits_0()
    {
        using var s = new Scratch();
        CliResult r = s.Run();
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("Usage: rules-corpus", r.Stdout, StringComparison.Ordinal);
        Assert.Empty(r.Stderr);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_lists_every_command_and_every_exit_code(string flag)
    {
        using var s = new Scratch();
        CliResult r = s.Run(flag);
        Assert.Equal(0, r.Exit);
        foreach (string command in new[] { "init", "import", "build", "verify", "inspect", "diff", "pack" })
        {
            Assert.Contains($"\n  {command} ", r.Stdout, StringComparison.Ordinal);
        }

        foreach (string code in new[] { "0  success", "1  build or verification failure", "2  usage error", "3  verify or pack found checks" })
        {
            Assert.Contains(code, r.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Help_after_a_command_prints_usage()
    {
        using var s = new Scratch();
        CliResult r = s.Run("build", "--help");
        Assert.Equal(0, r.Exit);
        Assert.StartsWith("Usage: rules-corpus", r.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Version_prints_the_informational_version()
    {
        string expected = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        using var s = new Scratch();
        CliResult r = s.Run("--version");
        Assert.Equal(0, r.Exit);
        Assert.Equal(expected + "\n", r.Stdout);
        Assert.StartsWith("0.1.0-dev", expected, StringComparison.Ordinal);

        CliResult json = s.Run("--version", "--json");
        Assert.Equal(expected, json.Json.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("build", "--dri", "x")]
    [InlineData("build", "-x")]
    [InlineData("build", "extra")]
    [InlineData("build", "--dir")]
    [InlineData("build", "--dir", "a", "--dir", "b")]
    [InlineData("verify", "a", "b")]
    [InlineData("verify", "--rebuild", "--rebuild")]
    [InlineData("init", "dir")]
    [InlineData("init", "--corpus-id", "x")]
    [InlineData("import", "file", "--dir", "d", "--id", "a", "--media-type", "text/plain")]
    [InlineData("inspect")]
    [InlineData("diff", "a")]
    [InlineData("pack")]
    [InlineData("--version", "extra")]
    public void A_malformed_command_line_is_a_usage_error_with_exit_2(params string[] args)
    {
        using var s = new Scratch();
        CliResult r = s.Run(args);
        Assert.Equal(2, r.Exit);
        Assert.Empty(r.Stdout);
        Assert.StartsWith("rules-corpus: ", r.Stderr, StringComparison.Ordinal);
        Assert.Contains("rules-corpus --help", r.Stderr, StringComparison.Ordinal);
        r.AssertNoStackTrace();
    }

    [Fact]
    public void A_usage_error_under_json_also_writes_an_error_document()
    {
        using var s = new Scratch();
        CliResult r = s.Run("frobnicate", "--json");
        Assert.Equal(2, r.Exit);
        JsonElement json = r.Json;
        Assert.Equal(2, json.GetProperty("exitCode").GetInt32());
        Assert.Equal("unknown command 'frobnicate'", json.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(0, json.GetProperty("error").GetProperty("problems").GetArrayLength());
        Assert.Contains("unknown command", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_output_is_canonical_indented_with_sorted_keys_and_a_final_newline()
    {
        using var s = new Scratch();
        CliResult r = s.Run("init", "c", "--corpus-id", "example", "--json");
        Assert.Equal(0, r.Exit);
        string path = s.PathOf("c/corpus.build.json");
        string expected = "{\n  \"command\": \"init\",\n  \"corpusId\": \"example\",\n  \"path\": "
            + JsonSerializer.Serialize(path) + "\n}\n";
        Assert.Equal(expected, r.Stdout);
    }
}
