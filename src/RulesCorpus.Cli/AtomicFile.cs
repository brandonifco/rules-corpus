namespace RulesCorpus.Cli;

/// <summary>
/// Writes a file so that its destination either holds the complete bytes or does not exist. The
/// bytes go to a temporary sibling, are flushed to disk, and only then take the destination's
/// name; a failure at any point removes the temporary file and leaves the destination as it was.
/// </summary>
internal static class AtomicFile
{
    /// <summary>
    /// Creates <paramref name="destination"/> with what <paramref name="write"/> produces. It never
    /// overwrites: a destination that exists when the file is published is refused.
    /// </summary>
    /// <exception cref="RefusalException">The destination exists.</exception>
    /// <exception cref="IOException">The temporary file could not be written or published.</exception>
    public static void CreateNew(string destination, Action<Stream> write)
    {
        string directory = Path.GetDirectoryName(destination) ?? ".";
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: false);
        }
        catch (IOException) when (File.Exists(destination) || Directory.Exists(destination))
        {
            Discard(temporary);
            throw new RefusalException($"'{destination}' already exists; pack never overwrites a file");
        }
        catch
        {
            Discard(temporary);
            throw;
        }
    }

    private static void Discard(string temporary)
    {
        try
        {
            File.Delete(temporary);
        }
        catch (IOException)
        {
            // The original failure is the one worth reporting.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
