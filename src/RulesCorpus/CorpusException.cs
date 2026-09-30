namespace RulesCorpus;

/// <summary>
/// A corpus, manifest or build definition was refused. Carries every problem found, not just
/// the first, so one run shows everything that must be fixed.
/// </summary>
public sealed class CorpusException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CorpusException()
    {
        Errors = [];
    }

    /// <summary>Creates the exception with a message.</summary>
    public CorpusException(string message)
        : base(message)
    {
        Errors = [];
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public CorpusException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [];
    }

    internal CorpusException(string message, IReadOnlyList<CorpusError> errors, Exception? innerException = null)
        : base(Describe(message, errors), innerException)
    {
        Errors = errors.ToArray();
    }

    /// <summary>Each problem, with the member it concerns, in the order found.</summary>
    public IReadOnlyList<CorpusError> Errors { get; }

    private static string Describe(string message, IReadOnlyList<CorpusError> errors)
    {
        const int shown = 20;
        IEnumerable<string> lines = errors.Take(shown).Select(e => "  " + e);
        string more = errors.Count > shown ? $"\n  ... and {errors.Count - shown} more" : string.Empty;
        return errors.Count == 0
            ? message
            : message + "\n" + string.Join("\n", lines) + more;
    }
}

/// <summary>
/// One problem: where it is and what is wrong. The path names the member in the document it
/// came from (<c>$.artifacts[2].digest</c>), so the reader can go straight to it.
/// </summary>
public sealed class CorpusError
{
    internal CorpusError(string path, string reason)
    {
        Path = path;
        Reason = reason;
    }

    /// <summary>The member concerned, as a JSON path from the document root <c>$</c>.</summary>
    public string Path { get; }

    /// <summary>What is wrong with it.</summary>
    public string Reason { get; }

    /// <summary><c>path: reason</c>.</summary>
    public override string ToString() => Path + ": " + Reason;
}
