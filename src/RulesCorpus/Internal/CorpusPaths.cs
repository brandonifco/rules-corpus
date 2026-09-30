namespace RulesCorpus.Internal;

/// <summary>
/// The artifact path rules of docs/corpus-format.md, plus the portability rules that keep a
/// path meaning the same file on every operating system a corpus is verified on.
///
/// <para>
/// Components are restricted to ASCII letters, digits, '.', '_' and '-' because anything wider
/// aliases somewhere: Windows and macOS fold case and macOS normalizes Unicode, so two names that
/// differ as strings can open one file, and Unicode normalization is not available to compare
/// them (decision 0003: under invariant globalization it silently does nothing). Windows also
/// drops a trailing '.' and opens a device for a reserved name whatever its extension.
/// </para>
/// </summary>
internal static class CorpusPaths
{
    private const int MaxPathLength = 1024;
    private const int MaxComponentBytes = 255;

    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com0", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt0", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    /// <summary>Why the path is not a valid artifact path, or null when it is.</summary>
    public static string? Problem(string path)
    {
        if (path.Length == 0)
        {
            return "must not be empty";
        }

        if (path.Length > MaxPathLength)
        {
            return $"is longer than {MaxPathLength} characters";
        }

        if (path[0] == '/')
        {
            return "is absolute; artifact paths are relative to the corpus directory";
        }

        string[] components = path.Split('/');
        foreach (string component in components)
        {
            if (ComponentProblem(component) is { } problem)
            {
                return problem;
            }
        }

        if (IsReserved(components[0]))
        {
            return $"uses the reserved name '{components[0]}'; {Vocabulary.ManifestFile} and {Vocabulary.BuildFile} are never artifact paths";
        }

        return null;
    }

    private static string? ComponentProblem(string component)
    {
        if (component.Length == 0)
        {
            return "has an empty component";
        }

        if (component is "." or "..")
        {
            return $"has a '{component}' component; artifact paths never leave or restate the corpus directory";
        }

        foreach (char c in component)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            {
                continue;
            }

            return c switch
            {
                < ' ' or '\x7F' => "contains a control character",
                '\\' => "contains '\\'; artifact paths separate components with '/' only, so the path means the same file on every system",
                ':' => "contains ':', which is a drive or stream separator on some systems",
                _ => $"contains {Describe(c)}; path components use only ASCII letters, digits, '.', '_' and '-', so the path names the same file on every system",
            };
        }

        if (component.Length > MaxComponentBytes)
        {
            return $"has a component longer than {MaxComponentBytes} bytes";
        }

        if (component[^1] == '.')
        {
            return $"has the component '{component}', which ends in '.'; Windows drops a trailing '.', so it would name another file there";
        }

        int dot = component.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? component : component[..dot];
        if (WindowsDeviceNames.Contains(stem))
        {
            return $"has the component '{component}'; '{stem}' is a device name on Windows, with or without an extension";
        }

        return null;
    }

    private static string Describe(char c) =>
        c is > ' ' and < '\x7F' ? $"'{c}'" : $"U+{(int)c:X4}";

    /// <summary>
    /// True when the two paths would name the same file, or one would have to be a directory
    /// holding the other, on some system. Compared ignoring case because common file systems do.
    /// </summary>
    public static bool Collide(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase)
        || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);

    private static bool IsReserved(string firstComponent) =>
        string.Equals(firstComponent, Vocabulary.ManifestFile, StringComparison.OrdinalIgnoreCase)
        || string.Equals(firstComponent, Vocabulary.BuildFile, StringComparison.OrdinalIgnoreCase);
}
