using System.Text;
using RulesCorpus.Json;

namespace RulesCorpus.Internal;

/// <summary>
/// The artifact path rules of docs/corpus-format.md, plus the portability rules that keep a
/// path meaning the same file on every operating system a corpus is verified on.
/// </summary>
internal static class CorpusPaths
{
    private const int MaxPathLength = 1024;
    private const int MaxComponentBytes = 255;

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

        if (CanonicalJsonWriter.HasLoneSurrogate(path))
        {
            return "holds a lone surrogate";
        }

        foreach (char c in path)
        {
            if (c < 0x20 || c == 0x7F)
            {
                return "contains a control character";
            }

            if (c == '\\')
            {
                return "contains '\\'; artifact paths separate components with '/' only, so the path means the same file on every system";
            }

            if (c == ':')
            {
                return "contains ':', which is a drive or stream separator on some systems";
            }
        }

        if (path[0] == '/')
        {
            return "is absolute; artifact paths are relative to the corpus directory";
        }

        string[] components = path.Split('/');
        foreach (string component in components)
        {
            if (component.Length == 0)
            {
                return "has an empty component";
            }

            if (component is "." or "..")
            {
                return $"has a '{component}' component; artifact paths never leave or restate the corpus directory";
            }

            if (Encoding.UTF8.GetByteCount(component) > MaxComponentBytes)
            {
                return $"has a component longer than {MaxComponentBytes} bytes";
            }
        }

        if (IsReserved(components[0]))
        {
            return $"uses the reserved name '{components[0]}'; {Vocabulary.ManifestFile} and {Vocabulary.BuildFile} are never artifact paths";
        }

        return null;
    }

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
