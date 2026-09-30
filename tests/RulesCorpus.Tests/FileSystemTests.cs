using System.Text;
using RulesCorpus.Adapters;
using RulesCorpus.Tests.Support;

namespace RulesCorpus.Tests;

/// <summary>
/// What a corpus directory can hold besides regular files and symbolic links: hard links, which
/// a write must replace rather than write through, and named pipes and devices, which a read must
/// refuse without opening, because opening a pipe blocks until something writes to it.
/// </summary>
public class FileSystemTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>Canonical bytes that differ from the source, so a write through a link to it shows.</summary>
    private static ScriptedAdapter Upper() => new(input =>
    {
        byte[] upper = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(input.Bytes.Span).ToUpperInvariant());
        return new AdapterOutput(upper, "text/plain", DerivationFidelity.Lossless, [], [new AdapterSegment("all", 0, upper.Length, null, [])]);
    });

    [Fact]
    public void A_hard_link_at_an_output_path_is_replaced_not_written_through_to_a_file_outside()
    {
        if (!Posix.Supported)
        {
            return;
        }

        using TempCorpus corpus = TempCorpus.Stored();
        using var outside = new TempCorpus();
        outside.WriteText("victim.txt", "precious outside file\n");
        Directory.CreateDirectory(corpus.PathOf("canonical"));
        Posix.HardLink(outside.PathOf("victim.txt"), corpus.PathOf("canonical/notes.txt"));

        corpus.Build(Upper());

        Assert.Equal("precious outside file\n", Encoding.UTF8.GetString(outside.ReadBytes("victim.txt")));
        Assert.Equal(TempCorpus.NotesText.ToUpperInvariant(), Encoding.UTF8.GetString(corpus.ReadBytes("canonical/notes.txt")));
    }

    [Fact]
    public void A_hard_link_from_an_output_path_to_a_source_leaves_the_source_unchanged()
    {
        if (!Posix.Supported)
        {
            return;
        }

        using TempCorpus corpus = TempCorpus.Stored();
        Directory.CreateDirectory(corpus.PathOf("canonical"));
        Posix.HardLink(corpus.PathOf("sources/notes.txt"), corpus.PathOf("canonical/notes.txt"));

        corpus.Build(Upper());

        Assert.Equal(TempCorpus.NotesText, Encoding.UTF8.GetString(corpus.ReadBytes("sources/notes.txt")));
        VerificationReport report = CorpusVerifier.Verify(CorpusFiles.FromDirectory(corpus.Root));
        Assert.Equal(VerificationOutcome.Ok, report.Outcome);
    }

    [Fact]
    public void A_hard_link_at_the_manifest_path_is_replaced_not_written_through()
    {
        if (!Posix.Supported)
        {
            return;
        }

        using TempCorpus corpus = TempCorpus.Stored();
        using var outside = new TempCorpus();
        outside.WriteText("victim.json", "{}\n");
        Posix.HardLink(outside.PathOf("victim.json"), corpus.PathOf("corpus.json"));

        corpus.Build();

        Assert.Equal("{}\n", Encoding.UTF8.GetString(outside.ReadBytes("victim.json")));
    }

    [Fact]
    public void Verifying_a_corpus_whose_artifact_is_a_named_pipe_fails_that_artifact_without_blocking()
    {
        if (!Posix.Supported)
        {
            return;
        }

        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        string fifo = corpus.PathOf("canonical/notes.txt");
        File.Delete(fifo);
        Posix.MakeFifo(fifo);

        VerificationReport report = WithoutBlocking(fifo, () => CorpusVerifier.Verify(CorpusFiles.FromDirectory(corpus.Root)));

        VerificationCheck check = Assert.Single(report.Checks, c => c.Name == "artifact notes-canonical");
        Assert.Equal(VerificationOutcome.Failed, check.Outcome);
        Assert.Contains("not a regular file", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Building_from_a_source_that_is_a_named_pipe_is_refused_without_blocking()
    {
        if (!Posix.Supported)
        {
            return;
        }

        using TempCorpus corpus = TempCorpus.Stored();
        string fifo = corpus.PathOf("sources/notes.txt");
        File.Delete(fifo);
        Posix.MakeFifo(fifo);

        CorpusException e = WithoutBlocking(fifo, () => Assert.Throws<CorpusException>(() => corpus.Build()));

        ManifestEdits.AssertError(e, "$.sources[0].path", "not a regular file");
        Assert.False(corpus.Exists("corpus.json"));
    }

    [Fact]
    public void A_build_definition_that_is_a_named_pipe_is_refused_without_blocking()
    {
        if (!Posix.Supported)
        {
            return;
        }

        using TempCorpus corpus = TempCorpus.Stored();
        string fifo = corpus.PathOf("corpus.build.json");
        File.Delete(fifo);
        Posix.MakeFifo(fifo);

        CorpusException e = WithoutBlocking(fifo, () => Assert.Throws<CorpusException>(() => corpus.Build()));

        ManifestEdits.AssertError(e, "corpus.build.json", "not a regular file");
    }

    [Fact]
    public void A_character_device_is_not_read_as_an_artifact()
    {
        if (!Posix.Supported || !File.Exists("/dev/zero"))
        {
            return;
        }

        var files = new Files.DirectoryCorpusFiles("/dev");

        Assert.False(files.TryRead("zero", 1024, out _, out string problem));
        Assert.Contains("not a regular file", problem, StringComparison.Ordinal);
    }

    private static T WithoutBlocking<T>(string fifo, Func<T> action)
    {
        Task<T> task = Task.Run(action);
        if (!task.Wait(Patience))
        {
            Posix.ReleaseFifoReader(fifo);
            Assert.Fail($"blocked for {Patience.TotalSeconds} s opening the named pipe '{fifo}'");
        }

        return task.Result;
    }
}
