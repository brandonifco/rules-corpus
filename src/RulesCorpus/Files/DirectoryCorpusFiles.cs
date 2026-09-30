namespace RulesCorpus.Files;

/// <summary>
/// A corpus directory. Every path is checked component by component so that nothing read or
/// written resolves outside the directory: a symbolic link anywhere below the root is refused,
/// whether or not its target happens to lie inside, because a link's target can change after
/// verification and a packed corpus could not reproduce it.
/// </summary>
internal sealed class DirectoryCorpusFiles(string directory) : CorpusFiles
{
    private const string TemporaryName = "~rules-corpus-write";

    public string Root { get; } = Path.GetFullPath(directory);

    internal override string Description => Root;

    internal override bool TryRead(string relativePath, long maxBytes, out byte[] bytes, out string problem)
    {
        bytes = [];
        if (!TryResolve(relativePath, createDirectories: false, out string full, out problem))
        {
            return false;
        }

        // Classified before opening: opening a named pipe blocks, and a device never ends.
        switch (FileKind.Of(full))
        {
            case EntryKind.Missing:
                problem = $"'{relativePath}' does not exist";
                return false;
            case EntryKind.Directory:
                problem = $"'{relativePath}' is a directory, not a file";
                return false;
            case EntryKind.SymbolicLink:
                problem = $"'{relativePath}' is a symbolic link; a corpus holds regular files only";
                return false;
            case EntryKind.Special:
                problem = $"'{relativePath}' is not a regular file (a named pipe, device or socket); a corpus holds regular files only";
                return false;
            case EntryKind.Unknown:
                problem = $"'{relativePath}' could not be examined, so it is not known to be a regular file";
                return false;
        }

        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            long length = stream.Length;
            if (length > maxBytes)
            {
                problem = $"'{relativePath}' is {length} bytes; the limit is {maxBytes}";
                return false;
            }

            byte[] buffer = new byte[length];
            stream.ReadExactly(buffer);
            if (stream.ReadByte() != -1)
            {
                problem = $"'{relativePath}' changed size while it was being read";
                return false;
            }

            bytes = buffer;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = $"'{relativePath}' could not be read: {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// Writes a file, creating its directories. Refuses a path that is unsafe, passes through or
    /// ends at a symbolic link, or names an existing directory.
    ///
    /// <para>
    /// The bytes go to a temporary file in the same directory, which is then renamed over the
    /// target. A rename replaces the directory entry, so whatever was there, a hard link to a
    /// source or to a file outside the corpus included, is unlinked rather than written through,
    /// and a reader never sees a half-written file. The temporary name holds '~', which no
    /// artifact path may, so it can never be a file the corpus declares.
    /// </para>
    /// </summary>
    public bool TryWrite(string relativePath, byte[] bytes, out string problem)
    {
        if (!CanWrite(relativePath, out problem))
        {
            return false;
        }

        TryResolve(relativePath, createDirectories: true, out string full, out problem);
        string temporary = Path.Combine(Path.GetDirectoryName(full)!, TemporaryName);
        try
        {
            // A leftover from an interrupted build. Deleting a link removes the link, not its target.
            File.Delete(temporary);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, full, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            problem = $"'{relativePath}' could not be written: {e.Message}";
            return false;
        }
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
