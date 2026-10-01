using System.Text.Json;

namespace RulesCorpus.Cli.Tests;

/// <summary>
/// The boundary between an operational failure (bad input, bad path, the file system saying no)
/// and a defect. Operational failures print their reason; a defect is reported as an internal
/// error with its type and message. Neither is a stack trace, and neither is hidden.
/// </summary>
public sealed class FailureBoundaryTests
{
    private static (int Exit, string Out, string Err) Guard(bool json, Func<int> body)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = Cli.Guard(new Output(stdout, stderr, json), body);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void A_path_the_platform_cannot_represent_is_a_usage_error_not_a_stack_trace()
    {
        using var s = new Scratch();

        CliResult r = s.Run("build", "--dir", "a\0b");

        Assert.Equal(2, r.Exit);
        Assert.Contains("not a valid path", r.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_device_is_refused_as_a_corpus_instead_of_being_read_to_its_end()
    {
        if (!File.Exists("/dev/zero"))
        {
            return;
        }

        using var s = new Scratch();

        // Before the fix this read /dev/zero for ever; the timeout turns a hang into a failure.
        Task<CliResult> run = Task.Run(() => s.Run("verify", "/dev/zero"));
        Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(run, finished);

        CliResult r = await run;
        Assert.Equal(1, r.Exit);
        Assert.Contains("not a regular file", r.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unexpected_exception_is_an_internal_error_with_its_type_and_message_exit_1_and_no_stack()
    {
        (int exit, string stdout, string stderr) = Guard(json: false, () => throw new InvalidOperationException("the invariant broke"));

        Assert.Equal(1, exit);
        Assert.Empty(stdout);
        Assert.Contains("internal error: InvalidOperationException: the invariant broke", stderr, StringComparison.Ordinal);
        Assert.Contains("defect in rules-corpus", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Under_json_an_unexpected_exception_also_writes_an_error_document()
    {
        (int exit, string stdout, _) = Guard(json: true, () => throw new NullReferenceException("oops"));

        Assert.Equal(1, exit);
        JsonElement json = JsonDocument.Parse(stdout).RootElement;
        Assert.Equal(1, json.GetProperty("exitCode").GetInt32());
        Assert.Contains("internal error: NullReferenceException", json.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Operational_failures_keep_their_plain_message()
    {
        (int exit, _, string stderr) = Guard(json: false, () => throw new IOException("the disk is full"));

        Assert.Equal(1, exit);
        Assert.Contains("the disk is full", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("internal error", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Running_out_of_memory_is_a_process_failure_and_is_not_swallowed()
    {
        Assert.Throws<OutOfMemoryException>(() => Guard(json: false, () => throw new OutOfMemoryException()));
    }
}
