using System.Text.Unicode;
using RulesCorpus.Adapters;
using RulesCorpus.Internal;
using RulesCorpus.Json;

namespace RulesCorpus.Build;

/// <summary>What one adapter run produced, measured and validated by the core.</summary>
internal sealed record AdapterResult(
    byte[] Canonical,
    string MediaType,
    DerivationFidelity Fidelity,
    IReadOnlyList<string> Losses,
    IReadOnlyList<ManifestSegment> Segments);

/// <summary>
/// Runs an adapter and checks everything it claims (docs/adapter-contract.md): an adapter's
/// output is evidence to be measured, not a record to be copied. Shared by the builder and by
/// verification's rebuild, so the two cannot disagree about what a valid output is.
/// </summary>
internal static class AdapterRun
{
    /// <summary>Checks that an adapter may be run at all: named validly, versioned, deterministic.</summary>
    public static string? AdapterProblem(ICorpusAdapter adapter)
    {
        if (!CorpusGrammar.IsId(adapter.Id))
        {
            return $"adapter id '{adapter.Id}' is not a valid id";
        }

        if (string.IsNullOrEmpty(adapter.Version) || CanonicalJsonWriter.HasLoneSurrogate(adapter.Version))
        {
            return $"adapter '{adapter.Id}' has no usable version";
        }

        if (!adapter.IsDeterministic)
        {
            return $"adapter '{adapter.Id}' is not deterministic; a non-deterministic transformation is declared in external[], not run by the build";
        }

        return null;
    }

    /// <summary>
    /// Runs the adapter over a copy of the input and validates its output. Returns null having
    /// appended errors, under <paramref name="at"/>, when it refused or produced anything invalid.
    /// </summary>
    public static AdapterResult? Run(
        ICorpusAdapter adapter,
        string inputId,
        byte[] inputBytes,
        IReadOnlyDictionary<string, string> parameters,
        CorpusLimits limits,
        string outputId,
        IReadOnlySet<string> takenSegmentIds,
        int segmentsSoFar,
        string at,
        List<CorpusError> errors)
    {
        AdapterOutput? output;
        try
        {
            // A copy, so an adapter that writes through its input cannot alter what is recorded.
            output = adapter.Derive(new AdapterInput(inputId, inputBytes.ToArray(), parameters, limits));
        }
        catch (CorpusAdapterException e)
        {
            errors.Add(new CorpusError(at, $"adapter '{adapter.Id}' refused: {e.Message}"));
            return null;
        }
        catch (Exception e) when (!IsCritical(e))
        {
            // The contract says an adapter refuses with CorpusAdapterException, but a bug an
            // input or its parameters provoke is still a verdict on that input, not a reason
            // for a hostile corpus to crash the build or the verifier. Only failures of the
            // process itself propagate.
            errors.Add(new CorpusError(at, $"adapter '{adapter.Id}' failed with {e.GetType().FullName}: {e.Message}"));
            return null;
        }

        if (output is null)
        {
            errors.Add(new CorpusError(at, $"adapter '{adapter.Id}' returned no output"));
            return null;
        }

        // Copied once: everything below is checked against, and recorded from, this copy.
        byte[] canonical = output.Canonical.ToArray();
        int before = errors.Count;
        string o = at + ":output";

        if (!CorpusGrammar.IsMediaType(output.MediaType))
        {
            errors.Add(new CorpusError(o + ".mediaType", $"'{output.MediaType}' is not a valid media type (type/subtype, lowercase ASCII, at most 128 characters)"));
        }

        if (!Vocabulary.IsDefined(output.Fidelity))
        {
            errors.Add(new CorpusError(o + ".fidelity", $"{(int)output.Fidelity} is not a defined fidelity"));
        }

        CheckLosses(output, o, errors);

        if (canonical.LongLength > limits.MaxArtifactBytes)
        {
            errors.Add(new CorpusError(o + ".canonical", $"{canonical.LongLength} bytes exceeds the limit of {limits.MaxArtifactBytes}"));
        }

        if (output.Segments is null)
        {
            errors.Add(new CorpusError(o + ".segments", "is null"));
            return null;
        }

        if ((long)segmentsSoFar + output.Segments.Count > limits.MaxSegments)
        {
            errors.Add(new CorpusError(o + ".segments", $"{output.Segments.Count} segments brings the manifest to {(long)segmentsSoFar + output.Segments.Count}, over the limit of {limits.MaxSegments}"));
            return null;
        }

        var segments = new List<ManifestSegment>(output.Segments.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        long previousStart = 0;
        for (int i = 0; i < output.Segments.Count; i++)
        {
            ManifestSegment? segment = CheckSegment(
                output.Segments[i], $"{o}.segments[{i}]", canonical, inputId, inputBytes.LongLength, outputId, ids, takenSegmentIds, ref previousStart, errors);
            if (segment is not null)
            {
                segments.Add(segment);
            }
        }

        if (errors.Count != before)
        {
            return null;
        }

        return new AdapterResult(canonical, output.MediaType, output.Fidelity, output.Losses.ToArray(), segments);
    }

    /// <summary>Failures of the process rather than of the adapter's work on this input.</summary>
    private static bool IsCritical(Exception e) =>
        e is OutOfMemoryException or StackOverflowException or AccessViolationException;

    private static void CheckLosses(AdapterOutput output, string o, List<CorpusError> errors)
    {
        if (output.Losses is null)
        {
            errors.Add(new CorpusError(o + ".losses", "is null"));
            return;
        }

        for (int i = 0; i < output.Losses.Count; i++)
        {
            string? loss = output.Losses[i];
            if (string.IsNullOrEmpty(loss) || CanonicalJsonWriter.HasLoneSurrogate(loss))
            {
                errors.Add(new CorpusError($"{o}.losses[{i}]", "must be a non-empty statement of what was discarded"));
            }
        }

        bool lossless = output.Fidelity == DerivationFidelity.Lossless;
        if (lossless && output.Losses.Count != 0)
        {
            errors.Add(new CorpusError(o + ".losses", "must be empty when fidelity is lossless; a transformation that discarded something is not lossless"));
        }
        else if (!lossless && output.Losses.Count == 0)
        {
            errors.Add(new CorpusError(o + ".losses", "must say what was discarded when fidelity is not lossless"));
        }
    }

    private static ManifestSegment? CheckSegment(
        AdapterSegment? s,
        string at,
        byte[] canonical,
        string inputId,
        long inputLength,
        string outputId,
        HashSet<string> ids,
        IReadOnlySet<string> takenSegmentIds,
        ref long previousStart,
        List<CorpusError> errors)
    {
        if (s is null)
        {
            errors.Add(new CorpusError(at, "is null"));
            return null;
        }

        int before = errors.Count;
        if (!CorpusGrammar.IsSegmentId(s.Id))
        {
            errors.Add(new CorpusError(at + ".id", $"'{s.Id}' is not a valid segment id ([A-Za-z0-9]([A-Za-z0-9._()/-]*[A-Za-z0-9)])?, at most 256 characters)"));
        }
        else if (!ids.Add(s.Id))
        {
            errors.Add(new CorpusError(at + ".id", $"'{s.Id}' is repeated in this output"));
        }
        else if (takenSegmentIds.Contains(s.Id))
        {
            errors.Add(new CorpusError(at + ".id", $"'{s.Id}' is already a segment of an earlier derivation; segment ids are unique in the manifest"));
        }

        bool spanOk = true;
        if (s.Start < 0)
        {
            errors.Add(new CorpusError(at + ".start", $"is {s.Start}; offsets are at least 0"));
            spanOk = false;
        }

        if (s.Length < 1)
        {
            errors.Add(new CorpusError(at + ".length", $"is {s.Length}; a segment is at least 1 byte"));
            spanOk = false;
        }

        if (spanOk && s.Start > canonical.LongLength - s.Length)
        {
            errors.Add(new CorpusError(at, $"span [{s.Start}, {s.Start + s.Length}) lies outside the {canonical.LongLength} canonical bytes"));
            spanOk = false;
        }

        if (spanOk)
        {
            if (Utf8Problem(canonical, s.Start, s.Length) is { } problem)
            {
                errors.Add(new CorpusError(at, problem));
            }

            if (s.Start < previousStart)
            {
                errors.Add(new CorpusError(at + ".start", $"is {s.Start}, before the previous segment's {previousStart}; segments are in document order"));
            }

            previousStart = s.Start;
        }

        if (s.Locator is not null && (s.Locator.Length == 0 || CanonicalJsonWriter.HasLoneSurrogate(s.Locator)))
        {
            errors.Add(new CorpusError(at + ".locator", "must be null or a non-empty string without lone surrogates"));
        }

        var spans = new List<ManifestSourceSpan>();
        if (s.Sources is null)
        {
            errors.Add(new CorpusError(at + ".sources", "is null; an adapter with no mapping returns an empty list"));
        }
        else
        {
            for (int k = 0; k < s.Sources.Count; k++)
            {
                if (CheckSpan(s.Sources[k], $"{at}.sources[{k}]", inputId, inputLength, errors) is { } span)
                {
                    spans.Add(span);
                }
            }
        }

        if (errors.Count != before)
        {
            return null;
        }

        ContentDigest digest = ContentDigest.Compute(canonical.AsSpan((int)s.Start, (int)s.Length));
        return new ManifestSegment(s.Id, outputId, s.Start, s.Length, digest, s.Locator, spans);
    }

    private static ManifestSourceSpan? CheckSpan(AdapterSourceSpan? span, string at, string inputId, long inputLength, List<CorpusError> errors)
    {
        if (span is null)
        {
            errors.Add(new CorpusError(at, "is null"));
            return null;
        }

        int before = errors.Count;
        if (span.Pages is null && span.Bytes is null)
        {
            errors.Add(new CorpusError(at, "carries neither pages nor a byte range"));
        }

        if (span.Pages is { } pages && (pages.From < 1 || pages.To < pages.From))
        {
            errors.Add(new CorpusError(at + ".pages", $"{pages.From}-{pages.To} is not a page range (1 <= from <= to)"));
        }

        if (span.Bytes is { } bytes)
        {
            if (bytes.Start < 0 || bytes.Length < 1)
            {
                errors.Add(new CorpusError(at + ".bytes", $"start {bytes.Start}, length {bytes.Length} is not a byte range (start >= 0, length >= 1)"));
            }
            else if (bytes.Start > inputLength - bytes.Length)
            {
                errors.Add(new CorpusError(at + ".bytes", $"[{bytes.Start}, {bytes.Start + bytes.Length}) lies outside input '{inputId}' of {inputLength} bytes"));
            }
        }

        return errors.Count == before ? new ManifestSourceSpan(inputId, span.Pages, span.Bytes) : null;
    }

    /// <summary>
    /// Why the span is not well-formed UTF-8 lying on sequence boundaries of the whole artifact,
    /// or null when it is. Shared with verification.
    /// </summary>
    public static string? Utf8Problem(ReadOnlySpan<byte> artifact, long start, long length)
    {
        ReadOnlySpan<byte> span = artifact.Slice((int)start, (int)length);
        if (!Utf8.IsValid(span))
        {
            return (span[0] & 0xC0) == 0x80
                ? $"starts at byte {start}, inside a UTF-8 sequence"
                : $"bytes [{start}, {start + length}) are not well-formed UTF-8, or end inside a UTF-8 sequence";
        }

        long end = start + length;
        if (end < artifact.Length && (artifact[(int)end] & 0xC0) == 0x80)
        {
            return $"ends at byte {end}, inside a UTF-8 sequence";
        }

        return null;
    }
}
