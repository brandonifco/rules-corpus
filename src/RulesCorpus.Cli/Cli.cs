using System.Reflection;
using System.Text;
using RulesCorpus.Adapters;
using RulesCorpus.Json;

namespace RulesCorpus.Cli;

/// <summary>
/// The <c>rules-corpus</c> command. <see cref="Run(string[], TextWriter, TextWriter, string)"/>
/// is the whole program; <c>Program.cs</c> only hands it the process's streams and directory,
/// so tests run every command in-process.
/// </summary>
internal static class Cli
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitNotVerified = 3;

    public const string Usage = """
        Usage: rules-corpus <command> [arguments] [--json]

        Commands:
          init <dir> --corpus-id <id>
              Create <dir> with a corpus.build.json skeleton. Refuses if one exists.
              The skeleton has no sources; build refuses it until one is imported.
          import <file> --dir <corpus> --id <artifact-id> --media-type <type> --origin <text>
                 [--retrieved YYYY-MM-DD] [--path <relative path>]
              Copy <file> into the corpus (default path sources/<file name>) and add it to
              corpus.build.json as a stored source. No --retrieved, no retrieved date: the
              clock is never read. Refuses an existing file, path or id.
          build [--dir <corpus>]
              Build the corpus (default: the current directory) with its text and xml adapters.
          verify [<corpus dir or .tar>] [--rebuild]
                 [--allow-not-verified | --expect-not-verified <check>[,<check>...]]
              Verify a corpus and print every check. --rebuild re-runs every reproducible
              derivation and requires byte-identical output. --expect-not-verified names,
              exactly as printed, the checks a consumer accepts as not verified: exit 0
              when those and no others are not verified and none failed, 1 otherwise.
          inspect <segment-id> [--corpus <dir or .tar>]
              Print a segment's metadata and its exact text, after checking its digest.
          diff <a> <b>
              Compare two corpora (directories or .tar); say whether contentDigest and
              manifestDigest are equal and list what was added, removed or changed.
          pack <output.tar> [--dir <corpus>] [--allow-not-verified]
              Verify, then write the corpus's canonical tar. Refuses an existing file.

        Options:
          --json      Machine-readable output on stdout (errors are also written to stderr).
          --help      This text.
          --version   The tool's version.

        Exit codes:
          0  success
          1  build or verification failure (any failed check, or an --expect-not-verified
             pin that was not met), or refused input
          2  usage error: unknown command or option, missing or malformed argument
          3  verify or pack found checks that were not verified and none that failed,
             and neither --allow-not-verified nor --expect-not-verified was given
        """;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Runs one command against the process's current directory.</summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, Directory.GetCurrentDirectory());

    /// <summary>
    /// Runs one command. Relative paths resolve against <paramref name="workingDirectory"/>.
    /// Expected refusals print their message, never a stack trace.
    /// </summary>
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        (bool json, bool help, List<string> tokens, string? misplaced) = SplitGlobalFlags(args);
        var output = new Output(stdout, stderr, json);

        return Guard(output, () =>
        {
            if (misplaced is not null)
            {
                throw new UsageException(misplaced);
            }

            if (tokens.Count == 0 || help)
            {
                output.Line(Usage.TrimEnd('\n'));
                return ExitOk;
            }

            if (tokens.Count == 1 && tokens[0] == "--version")
            {
                string version = Version();
                output.Result(new CjObject().Add("version", CjValue.Of(version)), () => output.Line(version));
                return ExitOk;
            }

            var context = new Context(output, workingDirectory);
            List<string> rest = tokens.Skip(1).ToList();
            return tokens[0] switch
            {
                "init" => Commands.Init(context, rest),
                "import" => Commands.Import(context, rest),
                "build" => Commands.Build(context, rest),
                "verify" => Commands.Verify(context, rest),
                "inspect" => Commands.Inspect(context, rest),
                "diff" => Commands.Diff(context, rest),
                "pack" => Commands.Pack(context, rest),
                _ => throw new UsageException($"unknown command '{tokens[0]}'"),
            };
        });
    }

    /// <summary>
    /// Runs a command and turns every way it can fail into an exit code and a message, never a
    /// stack trace. The boundary:
    /// <list type="bullet">
    /// <item>Expected, and reported as themselves: a usage error (exit 2); a refusal, a failed
    /// check, an adapter's refusal, and the file system saying no (exit 1).</item>
    /// <item>Anything else is a defect in this tool. It is reported as an <c>internal error</c>
    /// with its type and message, exit 1, so a script still sees a failure and a person still
    /// sees what failed. It is not hidden and not retried.</item>
    /// <item>Running out of memory is a process failure, not a result: it propagates.</item>
    /// </list>
    /// </summary>
    internal static int Guard(Output output, Func<int> body)
    {
        try
        {
            return body();
        }
        catch (UsageException e)
        {
            output.Error(ExitUsage, e.Message + "\nRun 'rules-corpus --help' for usage.", e.Message, []);
            return ExitUsage;
        }
        catch (RefusalException e)
        {
            output.Error(ExitFailed, Describe(e.Message, e.Problems), e.Message, e.Problems);
            return ExitFailed;
        }
        catch (CorpusException e)
        {
            // The message already lists the errors (CorpusException.Describe).
            var problems = e.Errors.Select(x => (x.Path, x.Reason)).ToList();
            output.Error(ExitFailed, e.Message, FirstLine(e.Message), problems);
            return ExitFailed;
        }
        catch (Exception e) when (e is CorpusAdapterException or IOException or UnauthorizedAccessException)
        {
            output.Error(ExitFailed, e.Message, e.Message, []);
            return ExitFailed;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            string message = $"internal error: {e.GetType().Name}: {e.Message}";
            output.Error(ExitFailed, message + "\nThis is a defect in rules-corpus, not a problem with your input; please report it.", message, []);
            return ExitFailed;
        }
    }

    /// <summary>
    /// Separates --json, --help and -h from the command. A token that follows an option taking a
    /// value is that option's value, so a global flag there is reported rather than swallowed.
    /// </summary>
    private static (bool Json, bool Help, List<string> Tokens, string? Misplaced) SplitGlobalFlags(string[] args)
    {
        IReadOnlyList<string> valueOptions = args.Length > 0 ? Commands.ValueOptionsOf(args[0]) : [];
        bool json = false;
        bool help = false;
        string? misplaced = null;
        var tokens = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            if (i > 0 && valueOptions.Contains(token) && i + 1 < args.Length)
            {
                tokens.Add(token);
                string value = args[++i];
                if (value is "--json" or "--help" or "-h")
                {
                    misplaced ??= $"{args[0]}: {token} needs a value, not {value}";
                }
                else
                {
                    tokens.Add(value);
                }
            }
            else if (token == "--json")
            {
                json = true;
            }
            else if (token is "--help" or "-h")
            {
                help = true;
                tokens.Add(token);
            }
            else
            {
                tokens.Add(token);
            }
        }

        return (json, help, tokens, misplaced);
    }

    /// <summary>Decodes bytes that verification has already shown to be well-formed UTF-8.</summary>
    public static string DecodeUtf8(ReadOnlySpan<byte> bytes) => StrictUtf8.GetString(bytes);

    private static string Version() =>
        typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Cli).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static string Describe(string message, IReadOnlyList<(string Path, string Reason)> problems) =>
        problems.Count == 0 ? message : message + "\n" + string.Join("\n", problems.Select(p => $"  {p.Path}: {p.Reason}"));

    private static string FirstLine(string message)
    {
        int newline = message.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? message : message[..newline];
    }
}

/// <summary>What every command needs: where to write and where relative paths start.</summary>
internal sealed class Context(Output output, string workingDirectory)
{
    public Output Output { get; } = output;

    public bool Json => Output.Json;

    /// <summary>An argument path made absolute against the working directory.</summary>
    /// <exception cref="UsageException">The path is empty; the working directory is spelled '.'.</exception>
    public string Resolve(string path)
    {
        if (path.Length == 0)
        {
            throw new UsageException("an empty path is not accepted; write '.' for the working directory");
        }

        try
        {
            return Path.GetFullPath(path, workingDirectory);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            throw new UsageException("not a valid path on this system: " + path.Replace("\0", "\\0", StringComparison.Ordinal));
        }
    }
}

/// <summary>
/// Human text or one JSON document on stdout; errors on stderr. JSON goes through the core's
/// canonical writer, so the same result always prints the same bytes, with keys sorted.
/// </summary>
internal sealed class Output(TextWriter stdout, TextWriter stderr, bool json)
{
    public bool Json { get; } = json;

    /// <summary>One line of human output on stdout, always ending in LF.</summary>
    public void Line(string text = "") => stdout.Write(text + "\n");

    /// <summary>A line on stderr, for notes that are not the result.</summary>
    public void Note(string text) => stderr.Write(text + "\n");

    /// <summary>The command's result: the JSON document, or the human rendering.</summary>
    public void Result(CjObject result, Action human)
    {
        if (Json)
        {
            stdout.Write(Encoding.UTF8.GetString(CanonicalJsonWriter.ToIndented(result)));
        }
        else
        {
            human();
        }
    }

    /// <summary>An error: always text on stderr; with --json, also an error document on stdout.</summary>
    public void Error(int exitCode, string human, string message, IReadOnlyList<(string Path, string Reason)> problems)
    {
        stderr.Write("rules-corpus: " + human + "\n");
        if (Json)
        {
            var list = new CjArray(problems.Select(p => (CjValue)new CjObject()
                .Add("path", CjValue.Of(p.Path))
                .Add("reason", CjValue.Of(p.Reason))));
            var error = new CjObject()
                .Add("message", CjValue.Of(message))
                .Add("problems", list);
            stdout.Write(Encoding.UTF8.GetString(CanonicalJsonWriter.ToIndented(
                new CjObject().Add("error", error).Add("exitCode", CjValue.Of(exitCode)))));
        }
    }
}
