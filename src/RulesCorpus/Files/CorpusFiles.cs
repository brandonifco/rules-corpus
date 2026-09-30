using RulesCorpus.Files;
using RulesCorpus.Internal;

namespace RulesCorpus;

/// <summary>
/// A corpus's files, on disk or packed, read by relative path. Verification, inspection and
/// diff accept either (decision 0006), and every read goes through the same path rules: no
/// traversal, no absolute paths, no symbolic links, no file over the limits.
///
/// <para>
/// Not extensible outside this library: the path and size rules are part of what verification
/// proves, so a caller cannot supply a reader that skips them.
/// </para>
/// </summary>
public abstract class CorpusFiles
{
    private protected CorpusFiles()
    {
    }

    /// <summary>A corpus directory. Nothing is read until it is asked for.</summary>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is null or empty.</exception>
    public static CorpusFiles FromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        return new DirectoryCorpusFiles(directory);
    }

    /// <summary>
    /// A packed corpus, read to its end now. A malformed archive is not an exception here: it is
    /// recorded, and verification reports it as a failed check.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="packed"/> is null.</exception>
    public static CorpusFiles FromPacked(Stream packed, CorpusLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(packed);
        return PackedCorpusFiles.Load(packed, limits ?? CorpusLimits.Default);
    }

    /// <summary>For messages: the directory, or "packed corpus".</summary>
    internal abstract string Description { get; }

    /// <summary>
    /// Reads one file by its relative path. Returns false with a reason when the path is unsafe,
    /// the file is missing, not a regular file, or larger than <paramref name="maxBytes"/>.
    /// </summary>
    internal abstract bool TryRead(string relativePath, long maxBytes, out byte[] bytes, out string problem);

    /// <summary>Whether the path is a reserved corpus file or a valid artifact path, with the reason if not.</summary>
    internal static string? PathProblem(string relativePath) =>
        relativePath is Vocabulary.ManifestFile or Vocabulary.BuildFile ? null : CorpusPaths.Problem(relativePath);
}
