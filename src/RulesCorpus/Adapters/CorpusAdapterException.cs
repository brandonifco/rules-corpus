namespace RulesCorpus.Adapters;

/// <summary>An adapter refused its input or parameters. There is no partial output.</summary>
public sealed class CorpusAdapterException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CorpusAdapterException()
    {
    }

    /// <summary>Creates the exception with a message saying what was refused and why.</summary>
    public CorpusAdapterException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public CorpusAdapterException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
