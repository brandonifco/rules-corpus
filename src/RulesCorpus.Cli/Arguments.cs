namespace RulesCorpus.Cli;

/// <summary>The command line is wrong: exit 2, with the reason and a pointer to the usage text.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// The input was well-formed but is refused: exit 1. Carries the problems in the same
/// path-and-reason shape the core's refusals use, so both render the same way.
/// </summary>
internal sealed class RefusalException(string message, IReadOnlyList<(string Path, string Reason)>? problems = null)
    : Exception(message)
{
    public IReadOnlyList<(string Path, string Reason)> Problems { get; } = problems ?? [];
}

/// <summary>What one command accepts.</summary>
internal sealed record CommandSpec(
    string Name,
    int MinPositionals,
    int MaxPositionals,
    string[] ValueOptions,
    string[] RequiredOptions,
    string[] Switches);

/// <summary>
/// A parsed command line. Hand-parsed on purpose: the grammar is <c>--name value</c> options,
/// <c>--name</c> switches and positionals, with nothing implicit, so a typo is a usage error
/// rather than something silently ignored.
/// </summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, string> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _switches = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    public string? Option(string name) => _options.TryGetValue(name, out string? value) ? value : null;

    public bool Switch(string name) => _switches.Contains(name);

    /// <summary>Parses the tokens after the command name.</summary>
    /// <exception cref="UsageException">An unknown, repeated or incomplete option, or the wrong number of positionals.</exception>
    public static Arguments Parse(IReadOnlyList<string> tokens, CommandSpec spec)
    {
        var parsed = new Arguments();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (spec.Switches.Contains(token))
                {
                    if (!parsed._switches.Add(token))
                    {
                        throw new UsageException($"{spec.Name}: {token} is given more than once");
                    }
                }
                else if (spec.ValueOptions.Contains(token))
                {
                    if (i + 1 >= tokens.Count)
                    {
                        throw new UsageException($"{spec.Name}: {token} needs a value");
                    }

                    if (!parsed._options.TryAdd(token, tokens[++i]))
                    {
                        throw new UsageException($"{spec.Name}: {token} is given more than once");
                    }
                }
                else
                {
                    throw new UsageException($"{spec.Name}: unknown option {token}");
                }
            }
            else if (token.Length > 1 && token[0] == '-')
            {
                throw new UsageException($"{spec.Name}: unknown option {token}");
            }
            else
            {
                parsed.Positionals.Add(token);
            }
        }

        foreach (string required in spec.RequiredOptions)
        {
            if (parsed.Option(required) is null)
            {
                throw new UsageException($"{spec.Name}: {required} is required");
            }
        }

        if (parsed.Positionals.Count < spec.MinPositionals || parsed.Positionals.Count > spec.MaxPositionals)
        {
            string expected = spec.MinPositionals == spec.MaxPositionals
                ? $"{spec.MinPositionals}"
                : $"{spec.MinPositionals} to {spec.MaxPositionals}";
            throw new UsageException($"{spec.Name}: expected {expected} argument(s), got {parsed.Positionals.Count}");
        }

        return parsed;
    }
}
