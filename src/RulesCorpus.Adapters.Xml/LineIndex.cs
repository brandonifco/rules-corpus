using System.Text;

namespace RulesCorpus.Adapters.Xml;

/// <summary>
/// Turns the reader's (line, position) pairs into byte offsets in the UTF-8 the text was
/// decoded from. The reader counts UTF-16 code units from the start of each LF-terminated line
/// (the adapter refuses CR, so there is no other line break), and a tab is one unit.
/// </summary>
internal sealed class LineIndex
{
    private readonly string _text;
    private readonly List<int> _lineStartChars = [0];
    private readonly List<long> _lineStartBytes = [0];

    public LineIndex(string text)
    {
        _text = text;
        long bytes = 0;
        int lineStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                bytes += Encoding.UTF8.GetByteCount(text.AsSpan(lineStart, i + 1 - lineStart));
                lineStart = i + 1;
                _lineStartChars.Add(lineStart);
                _lineStartBytes.Add(bytes);
            }
        }
    }

    /// <summary>The offset of the <c>&lt;</c> that precedes the element name the reader is at.</summary>
    public long StartOfTag(int line, int position)
    {
        int index = CharIndex(line, position) - 1;
        if (index < 0 || _text[index] != '<')
        {
            throw Refusal.Of($"Internal position error at line {line}: no '<' before the element name.");
        }

        return ByteOffset(index);
    }

    /// <summary>The offset just past the <c>&gt;</c> that closes the end tag whose name the reader is at.</summary>
    public long EndOfTag(int line, int position)
    {
        int close = _text.IndexOf('>', CharIndex(line, position));
        if (close < 0)
        {
            throw Refusal.Of($"Internal position error at line {line}: no '>' after the end tag's name.");
        }

        return ByteOffset(close + 1);
    }

    private int CharIndex(int line, int position) => _lineStartChars[line - 1] + position - 1;

    private long ByteOffset(int charIndex)
    {
        int line = _lineStartChars.BinarySearch(charIndex);
        if (line < 0)
        {
            line = ~line - 1;
        }

        return _lineStartBytes[line] + Encoding.UTF8.GetByteCount(_text.AsSpan(_lineStartChars[line], charIndex - _lineStartChars[line]));
    }
}
