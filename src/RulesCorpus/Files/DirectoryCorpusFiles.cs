namespace RulesCorpus.Files;

/// <summary>
/// A corpus directory. Every path is checked component by component so that nothing read or
/// written resolves outside the directory: a symbolic link anywhere below the root is refused,
/// whether or not its target happens to lie inside, because a link's target can change after
/// verification and a packed corpus could not reproduce it.
/// </summary>
internal sealed class DirectoryCorpusFiles(string directory) : CorpusFiles
{
    public string Root { get; } = Path.GetFullPath(directory);

    internal override string Description => Root;

    internal override bool TryRead(string relativePath, long maxBytes, out byte[] bytes, out string problem)
    {
        bytes = [];
        if (!TryResolve(relativePath, createDirectories: false, out string full, out problem))
        {
            return false;
        }

        var info = new FileInfo(full);
        if (!info.Exists)
        {
            problem = Directory.Exists(full) ? $"'{relativePath}' is a directory, not a file" : $"'{relativePath}' does not exist";
            return false;
        }

        if (info.Length > maxBytes)
        {
            problem = $"'{relativePath}' is {info.Length} bytes; the limit is {maxBytes}";
            return false;
        }

        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] buffer = new byte[info.Length];
        stream.ReadExactly(buffer);
        if (stream.ReadByte() != -1)
        {
            problem = $"'{relativePath}' changed size while it was being read";
            return false;
        }

        bytes = buffer;
        return true;
    }

    /// <summary>
    /// Writes a file, creating its directories. Refuses a path that is unsafe, passes through or
    /// ends at a symbolic link, or names an existing directory.
    /// </summary>
    public bool TryWrite(string relativePath, byte[] bytes, out string problem)
    {
        if (!CanWrite(relativePath, out problem))
        {
            return false;
        }

        TryResolve(relativePath, createDirectories: true, out string full, out problem);
        File.WriteAllBytes(full, bytes);
        return true;
    }

    /// <summary>Checks, without creating anything, that a write to the path would be allowed.</summary>
    public bool CanWrite(string relativePath, out string problem)
    {
        if (!TryResolve(relativePath, createDirectories: false, out string full, out problem))
        {
            return false;
        }

        if (Directory.Exists(full))
        {
            problem = $"'{relativePath}' is an existing directory";
            return false;
        }

        return true;
    }

    private bool TryResolve(string relativePath, bool createDirectories, out string full, out string problem)
    {
        full = Root;
        problem = string.Empty;
        if (PathProblem(relativePath) is { } p)
        {
            problem = $"'{relativePath}' {p}";
            return false;
        }

        if (!Directory.Exists(Root))
        {
            problem = $"the corpus directory '{Root}' does not exist";
            return false;
        }

        string[] components = relativePath.Split('/');
        string current = Root;
        for (int i = 0; i < components.Length; i++)
        {
            current = Path.Combine(current, components[i]);
            bool last = i == components.Length - 1;
            // LinkTarget reads the link itself, so it sees dangling links and links to directories.
            if (new FileInfo(current).LinkTarget is not null)
            {
                problem = $"'{relativePath}' passes through the symbolic link '{string.Join('/', components[..(i + 1)])}'; a corpus holds regular files only";
                return false;
            }

            if (!last && File.Exists(current))
            {
                problem = $"'{relativePath}' passes through '{string.Join('/', components[..(i + 1)])}', which is a file";
                return false;
            }

            if (!last && !Directory.Exists(current) && createDirectories)
            {
                Directory.CreateDirectory(current);
            }
        }

        full = current;
        return true;
    }
}
