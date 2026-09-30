using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RulesCorpus.Adapters.Text;

/// <summary>
/// One pass over the canonical lines, recording page markers and emitting segments in document
/// order as they close. Nothing is sorted afterwards; the order is the text's.
/// </summary>
internal sealed class Segmenter
{
    private const int MaxSegmentIdLength = 256;

    // Validated once already; a strict decoder here means a bug cannot quietly substitute U+FFFD.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly CanonicalText _text;
    private readonly byte[] _bytes;
    private readonly TextOptions _options;
    private readonly int _maxSegments;

    // Page markers in document order: where each takes effect, and the page it names.
    private readonly List<int> _markerOffsets = [];
    private readonly List<int> _markerPages = [];

    private readonly List<AdapterSegment> _segments = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    // Blocks: the open block's first and last line, and the numbering state.
    private int _blockStart = -1;
    private int _blockEnd = -1;
    private int _unpagedBlocks;
    private int _pagedBlockPage;
    private int _pagedBlocks;

    // Headings: the open segment's start, id and locator.
    private int _headingStart = -1;
    private string _headingId = "";
    private string _headingLocator = "";

    private Segmenter(CanonicalText text, TextOptions options, CorpusLimits limits)
    {
        _text = text;
        _bytes = text.Bytes;
        _options = options;
        _maxSegments = limits.MaxSegments;
    }

    public static List<AdapterSegment> Run(CanonicalText text, TextOptions options, CorpusLimits limits)
    {
        var segmenter = new Segmenter(text, options, limits);
        segmenter.Scan();
        return segmenter._segments;
    }

    private void Scan()
    {
        bool needsText = _options.PageMarker is not null || _options.HeadingPattern is not null;
        int position = 0;
        int lineNumber = 0;
        while (position < _bytes.Length)
        {
            int newline = Array.IndexOf(_bytes, (byte)'\n', position);
            int end = newline < 0 ? _bytes.Length : newline;
            lineNumber++;

            string? line = needsText ? Utf8.GetString(_bytes, position, end - position) : null;
            bool isMarkerLine = _options.PageMarker is not null && RecordMarkers(line!, position, lineNumber);

            switch (_options.Segmentation)
            {
                case Segmentation.Blocks:
                    OnBlockLine(position, end, isMarkerLine);
                    break;
                case Segmentation.Headings:
                    OnHeadingLine(line!, position, lineNumber);
                    break;
                case Segmentation.Whole:
                    break;
            }

            position = newline < 0 ? _bytes.Length : newline + 1;
        }

        switch (_options.Segmentation)
        {
            case Segmentation.Whole:
                if (_bytes.Length == 0)
                {
                    throw Refusal.Of("Input is empty; segmentation 'whole' needs at least one byte for segment 'all'.");
                }

                Emit("all", 0, _bytes.Length, locator: null);
                break;
            case Segmentation.Blocks:
                CloseBlock();
                if (_segments.Count == 0)
                {
                    throw Refusal.Of("Segmentation 'blocks' found no block: the input has no non-blank line outside page markers.");
                }

                break;
            case Segmentation.Headings:
                CloseHeading(_bytes.Length);
                if (_segments.Count == 0)
                {
                    throw Refusal.Of("Parameter 'headingPattern' matched no line, so there are no segments.");
                }

                break;
        }
    }

    // Returns whether the line is a marker line in its entirety.
    private bool RecordMarkers(string line, int lineStart, int lineNumber)
    {
        Regex marker = _options.PageMarker!;
        Match match = marker.Match(line);
        if (!match.Success)
        {
            return false;
        }

        RequireWholeCharacters(line, match.Index, match.Length, $"Page marker match on line {Format(lineNumber)}");
        if (match.Index == 0 && match.Length == line.Length)
        {
            AddMarker(match, lineStart, line, lineNumber);
            return true;
        }

        // Each marker's byte offset is carried forward from the previous one, so a long line
        // with many markers is counted once rather than once per marker.
        int charsCounted = 0;
        int bytesCounted = 0;
        for (; match.Success; match = match.NextMatch())
        {
            RequireWholeCharacters(line, match.Index, match.Length, $"Page marker match on line {Format(lineNumber)}");
            bytesCounted += Utf8.GetByteCount(line.AsSpan(charsCounted, match.Index - charsCounted));
            charsCounted = match.Index;
            AddMarker(match, lineStart + bytesCounted, line, lineNumber);
        }

        return false;
    }

    // A pattern can match half of a surrogate pair (a class such as [\uDC00-\uDFFF] does),
    // and a span that starts or ends there has no byte offset: it lies inside one UTF-8
    // sequence. That is a refusal, not an encoder exception.
    private static void RequireWholeCharacters(string line, int index, int length, string what)
    {
        if (SplitsPair(line, index) || SplitsPair(line, index + length))
        {
            throw Refusal.Of(
                $"{what} runs from character {Format(index)} to {Format(index + length)}, which starts or ends inside a "
                + "surrogate pair; a match must cover whole characters.");
        }
    }

    private static bool SplitsPair(string text, int index) =>
        index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]);

    private void AddMarker(Match match, int offset, string line, int lineNumber)
    {
        Group group = match.Groups[_options.PageMarkerGroup];
        int page = ParsePage(group, line, lineNumber);
        if (_options.PagesContiguous)
        {
            int expected = _markerPages.Count == 0 ? 1 : _markerPages[^1] + 1;
            if (page != expected)
            {
                throw Refusal.Of(
                    $"Page marker on line {Format(lineNumber)} names page {Format(page)}, but pagesContiguous "
                    + $"requires page {Format(expected)} next.");
            }
        }

        _markerOffsets.Add(offset);
        _markerPages.Add(page);
    }

    private static int ParsePage(Group group, string line, int lineNumber)
    {
        string digits = group.Success ? group.Value : "";

        // ASCII digits only, no leading zero: "p. 07" and "p. 7" must not both name page 7,
        // and char.IsDigit would admit digits from other scripts.
        bool canonical = digits.Length is > 0 and <= 10
            && digits[0] is >= '1' and <= '9'
            && digits.All(char.IsAsciiDigit);
        if (!canonical || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int page))
        {
            throw Refusal.Of(
                $"Page marker on line {Format(lineNumber)} ('{line}') captured '{digits}', which is not a page "
                + "number: expected a positive decimal integer without leading zeros.");
        }

        return page;
    }

    private void OnBlockLine(int start, int end, bool isMarkerLine)
    {
        if (isMarkerLine || IsBlank(start, end))
        {
            CloseBlock();
            return;
        }

        if (_blockStart < 0)
        {
            _blockStart = start;
        }

        _blockEnd = end;
    }

    private bool IsBlank(int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (_bytes[i] is not ((byte)' ' or (byte)'\t'))
            {
                return false;
            }
        }

        return true;
    }

    private void CloseBlock()
    {
        if (_blockStart < 0)
        {
            return;
        }

        int start = _blockStart;
        int end = _blockEnd;
        _blockStart = -1;

        int marker = MarkerAt(start);
        if (marker < 0)
        {
            Emit("b" + Format(++_unpagedBlocks), start, end, locator: null);
            return;
        }

        // k restarts when the page changes, not at every marker: a second marker naming the
        // page already in effect (an inline one, say) continues the numbering.
        int pageNumber = _markerPages[marker];
        if (pageNumber != _pagedBlockPage)
        {
            _pagedBlockPage = pageNumber;
            _pagedBlocks = 0;
        }

        string page = Format(pageNumber);
        Emit("p" + page + ".b" + Format(++_pagedBlocks), start, end, "p. " + page);
    }

    private void OnHeadingLine(string line, int start, int lineNumber)
    {
        Match match = _options.HeadingPattern!.Match(line);
        if (!match.Success)
        {
            return;
        }

        CloseHeading(start);

        Group idGroup = match.Groups["id"];
        string id = idGroup.Success ? idGroup.Value : "";
        if (!IsValidSegmentId(id))
        {
            throw Refusal.Of(
                $"Heading on line {Format(lineNumber)} gives segment id '{id}', which is not a valid segment id "
                + "(docs/corpus-format.md: [A-Za-z0-9]([A-Za-z0-9._()/-]*[A-Za-z0-9)])?, at most 256 characters).");
        }

        string locator = line;
        if (_options.HeadingHasLocatorGroup)
        {
            Group locatorGroup = match.Groups["locator"];
            if (!locatorGroup.Success || locatorGroup.Length == 0)
            {
                throw Refusal.Of($"Heading on line {Format(lineNumber)} has an empty or unmatched 'locator' group.");
            }

            RequireWholeCharacters(line, locatorGroup.Index, locatorGroup.Length, $"Heading 'locator' group on line {Format(lineNumber)}");
            locator = locatorGroup.Value;
        }

        if (_ids.Contains(id))
        {
            throw Refusal.Of($"Heading on line {Format(lineNumber)} repeats segment id '{id}'.");
        }

        _headingStart = start;
        _headingId = id;
        _headingLocator = locator;
    }

    private void CloseHeading(int nextStart)
    {
        if (_headingStart < 0)
        {
            return;
        }

        // Trailing newlines belong to the gap between segments, not to either one. The heading
        // line itself is non-empty (it holds the id), so the segment keeps at least one byte.
        int end = nextStart;
        while (end > _headingStart && _bytes[end - 1] == (byte)'\n')
        {
            end--;
        }

        Emit(_headingId, _headingStart, end, _headingLocator);
        _headingStart = -1;
    }

    private void Emit(string id, int start, int end, string? locator)
    {
        if (_segments.Count == _maxSegments)
        {
            throw Refusal.Of($"Input produces more than MaxSegments ({Format(_maxSegments)}) segments.");
        }

        if (!_ids.Add(id))
        {
            // Reachable for generated ids when a page number recurs after another page, which
            // pagesContiguous would have refused.
            throw Refusal.Of($"Segment id '{id}' occurs twice; page markers may return to an earlier page number.");
        }

        PageRange? pages = null;
        int first = MarkerAt(start);
        if (first >= 0)
        {
            int from = _markerPages[first];
            int to = _markerPages[MarkerAt(end - 1)];
            if (to < from)
            {
                throw Refusal.Of(
                    $"Segment '{id}' starts on page {Format(from)} and ends on page {Format(to)}; "
                    + "a page range cannot run backwards.");
            }

            pages = new PageRange(from, to);
        }

        long inputStart = _text.ToInputOffset(start);
        long inputEnd = _text.ToInputOffset(end);
        var source = new AdapterSourceSpan(pages, new ByteRange(inputStart, inputEnd - inputStart));
        _segments.Add(new AdapterSegment(id, start, end - start, locator, [source]));
    }

    // Index of the last marker at or before the offset, or -1 when the offset precedes them all.
    private int MarkerAt(int offset)
    {
        int index = _markerOffsets.BinarySearch(offset);
        return index >= 0 ? index : ~index - 1;
    }

    private static bool IsValidSegmentId(string id)
    {
        if (id.Length is 0 or > MaxSegmentIdLength || !char.IsAsciiLetterOrDigit(id[0]))
        {
            return false;
        }

        for (int i = 1; i < id.Length - 1; i++)
        {
            char c = id[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '(' or ')' or '/' or '-'))
            {
                return false;
            }
        }

        char last = id[^1];
        return char.IsAsciiLetterOrDigit(last) || last == ')';
    }

    private static string Format(long value) => CanonicalText.Format(value);
}
