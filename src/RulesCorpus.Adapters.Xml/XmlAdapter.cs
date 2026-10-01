using System.Text;
using System.Xml;

namespace RulesCorpus.Adapters.Xml;

/// <summary>
/// Segments a UTF-8 XML document into the elements a build definition names, as
/// docs/adapter-contract.md specifies for <c>xml</c> version 1.
///
/// <para>
/// The canonical artifact is the input, byte for byte: the adapter checks that the document is
/// well-formed, UTF-8, and free of anything it cannot read the same way twice, and refuses
/// otherwise. It transforms nothing, so the derivation is lossless and a segment's source span
/// is the same byte range as its span of the canonical artifact. A segment is one element, from
/// the <c>&lt;</c> of its start tag through the <c>&gt;</c> of its end tag.
/// </para>
///
/// <para>
/// It is not an XML query language. It matches one element name and reads one attribute from
/// each match; what the name and the attribute mean is the build definition's business.
/// </para>
/// </summary>
public sealed class XmlAdapter : ICorpusAdapter
{
    private const string MediaType = "application/xml";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// <c>xml</c>: the name a build definition's <c>adapter</c> member uses to select this
    /// adapter, and the tool id recorded against its derivations.
    /// </summary>
    public string Id => "xml";

    /// <summary>
    /// <c>1</c>. Any change to the bytes or segments emitted for the same input and parameters
    /// is a new version, since a rebuild could otherwise not tell it from a changed source.
    /// </summary>
    public string Version => "1";

    /// <summary>
    /// Always <see langword="true"/>: output depends only on the input bytes and parameters.
    /// Refusal messages carry positions, never the parser's culture-dependent text.
    /// </summary>
    public bool IsDeterministic => true;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is null.</exception>
    public AdapterOutput Derive(AdapterInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Parameters first: a malformed derivation is refused the same way whatever it is run on.
        (string element, string idAttribute) = ReadParameters(input.Parameters);

        if (input.Bytes.Length > input.Limits.MaxArtifactBytes)
        {
            throw Refusal.Of(
                $"Input '{input.ArtifactId}' is {input.Bytes.Length} bytes, over MaxArtifactBytes ({input.Limits.MaxArtifactBytes}).");
        }

        try
        {
            List<AdapterSegment> segments = Segment(input.Bytes.Span, element, idAttribute, input.Limits);
            return new AdapterOutput(input.Bytes, MediaType, DerivationFidelity.Lossless, [], segments);
        }
        catch (CorpusAdapterException e)
        {
            // Content refusals name the artifact; the build may run this adapter over many.
            throw new CorpusAdapterException($"{e.Message} (input '{input.ArtifactId}')", e);
        }
    }

    private static (string Element, string IdAttribute) ReadParameters(IReadOnlyDictionary<string, string> parameters)
    {
        string? element = null;
        string? idAttribute = null;

        // Parameters arrive in ordinal key order, so the first refusal is the same on every run.
        foreach (var (key, value) in parameters)
        {
            switch (key)
            {
                case "segmentElement":
                    element = Name(key, value);
                    break;
                case "idAttribute":
                    idAttribute = Name(key, value);
                    break;
                default:
                    throw Refusal.Of($"Unknown parameter '{key}'; the parameters are segmentElement and idAttribute.");
            }
        }

        return (
            element ?? throw Refusal.Of("Parameter 'segmentElement' is required: the name of the elements that become segments."),
            idAttribute ?? throw Refusal.Of("Parameter 'idAttribute' is required: the attribute that holds each segment's id."));
    }

    private static string Name(string key, string value)
    {
        try
        {
            return XmlConvert.VerifyName(value);
        }
        catch (XmlException)
        {
            throw Refusal.Of($"Parameter '{key}' is '{value}', which is not an XML name.");
        }
    }

    private static List<AdapterSegment> Segment(ReadOnlySpan<byte> bytes, string element, string idAttribute, CorpusLimits limits)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw Refusal.Of("Input starts with a byte-order mark; the xml adapter does not strip one.");
        }

        if (bytes.Contains((byte)'\r'))
        {
            throw Refusal.Of("Input contains a carriage return; the xml adapter reads LF line endings only.");
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw Refusal.Of("Input is not valid UTF-8.");
        }

        var lines = new LineIndex(text);
        var segments = new List<AdapterSegment>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings
        {
            // Parse, so the reader reports a DOCTYPE as a node we can refuse by name; it is refused
            // before any entity could be expanded, and with no resolver nothing is fetched.
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            CheckCharacters = true,
            IgnoreWhitespace = false,
            ConformanceLevel = ConformanceLevel.Document,
        };

        using var reader = XmlReader.Create(new StringReader(text), settings);
        var position = (IXmlLineInfo)reader;
        string? id = null;
        long start = 0;
        int depth = -1;

        try
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.XmlDeclaration:
                        string? encoding = reader.GetAttribute("encoding");
                        if (encoding is not null && !string.Equals(encoding, "UTF-8", StringComparison.OrdinalIgnoreCase))
                        {
                            throw Refusal.Of($"The XML declaration names encoding '{encoding}'; the input is read as UTF-8 only.");
                        }

                        break;
                    case XmlNodeType.DocumentType:
                        throw Refusal.Of($"The input has a DOCTYPE (line {position.LineNumber}); it is refused so no entity or external content is ever read.");
                    case XmlNodeType.Element when reader.Name == element:
                        int line = position.LineNumber;
                        if (depth >= 0)
                        {
                            throw Refusal.Of($"A '{element}' element at line {line} is nested inside another; segments cannot nest.");
                        }

                        if (reader.IsEmptyElement)
                        {
                            throw Refusal.Of($"The '{element}' element at line {line} is an empty element; a segment needs content.");
                        }

                        id = reader.GetAttribute(idAttribute)
                            ?? throw Refusal.Of($"The '{element}' element at line {line} has no '{idAttribute}' attribute.");
                        if (!ids.Add(id))
                        {
                            throw Refusal.Of($"Segment id '{id}' (line {line}) is repeated.");
                        }

                        start = lines.StartOfTag(position.LineNumber, position.LinePosition);
                        depth = reader.Depth;
                        break;
                    case XmlNodeType.EndElement when depth >= 0 && reader.Depth == depth && reader.Name == element:
                        long end = lines.EndOfTag(position.LineNumber, position.LinePosition);
                        if (segments.Count >= limits.MaxSegments)
                        {
                            throw Refusal.Of($"More than MaxSegments ({limits.MaxSegments}) segments.");
                        }

                        segments.Add(new AdapterSegment(id!, start, end - start, null, [new AdapterSourceSpan(null, new ByteRange(start, end - start))]));
                        depth = -1;
                        break;
                }
            }
        }
        catch (XmlException e)
        {
            // Not e.Message: its text is localized, and a refusal must read the same everywhere.
            throw Refusal.Of($"Input is not well-formed XML (line {e.LineNumber}, position {e.LinePosition}).");
        }

        if (segments.Count == 0)
        {
            throw Refusal.Of($"No '{element}' elements found, so there are no segments.");
        }

        return segments;
    }
}
