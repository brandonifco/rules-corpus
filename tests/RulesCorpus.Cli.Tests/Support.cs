using System.Text;
using System.Text.Json;

namespace RulesCorpus.Cli.Tests;

/// <summary>One in-process run of the command: its exit code and both streams.</summary>
internal sealed record CliResult(int Exit, string Stdout, string Stderr)
{
    public JsonElement Json => JsonDocument.Parse(Stdout).RootElement;

    /// <summary>An expected refusal prints its reason, never a stack trace.</summary>
    public void AssertNoStackTrace()
    {
        Assert.DoesNotContain("   at ", Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", Stderr, StringComparison.Ordinal);
    }
}

/// <summary>A scratch directory, deleted on dispose, and the command run against it.</summary>
internal sealed class Scratch : IDisposable
{
    public Scratch()
    {
        Root = Path.Combine(Path.GetTempPath(), "rules-corpus-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    /// <summary>The committed samples, copied next to the test assembly.</summary>
    public static string SamplesRoot => Path.Combine(AppContext.BaseDirectory, "samples");

    public string PathOf(string relative) => Path.Combine(Root, relative);

    /// <summary>Runs the command with the scratch directory as the working directory.</summary>
    public CliResult Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = Cli.Run(args, stdout, stderr, Root);
        return new CliResult(exit, stdout.ToString(), stderr.ToString());
    }

    public void Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    public void Write(string relative, byte[] bytes)
    {
        string full = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    public byte[] Read(string relative) => File.ReadAllBytes(PathOf(relative));

    public string ReadText(string relative) => Encoding.UTF8.GetString(Read(relative));

    /// <summary>
    /// A sample's build definition and stored sources only (never its built outputs), under
    /// <paramref name="name"/> in the scratch directory.
    /// </summary>
    public string FreshSample(string sample, string? name = null)
    {
        string from = Path.Combine(SamplesRoot, sample);
        string to = PathOf(name ?? sample);
        Directory.CreateDirectory(to);
        File.Copy(Path.Combine(from, "corpus.build.json"), Path.Combine(to, "corpus.build.json"));
        using JsonDocument definition = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(from, "corpus.build.json")));
        foreach (JsonElement source in definition.RootElement.GetProperty("sources").EnumerateArray())
        {
            if (source.TryGetProperty("path", out JsonElement path))
            {
                string target = Path.Combine(to, path.GetString()!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(from, path.GetString()!), target);
            }
        }

        return to;
    }

    /// <summary>A sample copied and built, ready to verify, inspect, diff or pack.</summary>
    public string BuiltSample(string sample, string? name = null)
    {
        string dir = FreshSample(sample, name);
        CliResult built = Run("build", "--dir", dir);
        Assert.Equal(0, built.Exit);
        return dir;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
