using System.Globalization;
using System.Text;

namespace RulesCorpus.Files;

/// <summary>
/// Writes the canonical packed form (docs/corpus-format.md, Packing): a POSIX PAX tar in which
/// every entry is a regular file with mode 0644, uid and gid 0, empty user and group names and
/// modification time 0, preceded by a PAX extended header that carries its path.
///
/// <para>
/// Written here rather than with <c>System.Formats.Tar</c>'s <c>TarWriter</c>, which is used to
/// read. Evidence (SDK 10.0.112): <c>TarWriter</c> names each PAX extended-header entry
/// <c>PaxHeaders.&lt;process id&gt;/…</c>, so two packs of the same corpus from two processes
/// differ. Decision 0006 requires the same corpus to pack to the same bytes; the header layout
/// is fixed and small enough to own.
/// </para>
/// </summary>
internal static class PaxTarWriter
{
    private const int Block = 512;
    private const string ExtendedHeaderName = "PaxHeader";

    public static void WriteEntry(Stream output, string path, ReadOnlySpan<byte> data)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(path);
        byte[] record = PaxRecord("path", pathBytes);
        WriteHeader(output, Encoding.ASCII.GetBytes(ExtendedHeaderName), [], record.Length, (byte)'x');
        WritePadded(output, record);

        (byte[] prefix, byte[] name) = UstarName(pathBytes);
        WriteHeader(output, name, prefix, data.Length, (byte)'0');
        WritePadded(output, data);
    }

    /// <summary>The end-of-archive marker: two zero blocks.</summary>
    public static void WriteEnd(Stream output) => output.Write(new byte[Block * 2]);

    private static byte[] PaxRecord(string key, byte[] value)
    {
        // "<length> <key>=<value>\n", where <length> counts the whole record including itself.
        int body = 1 + key.Length + 1 + value.Length + 1;
        int digits = 1;
        while (DecimalDigits(body + digits) != digits)
        {
            digits++;
        }

        int length = body + digits;
        using var record = new MemoryStream(length);
        record.Write(Encoding.ASCII.GetBytes(length.ToString(CultureInfo.InvariantCulture) + " " + key + "="));
        record.Write(value);
        record.WriteByte((byte)'\n');
        return record.ToArray();
    }

    private static int DecimalDigits(int value) => value.ToString(CultureInfo.InvariantCulture).Length;

    // The ustar name for readers that ignore the PAX path: split at a '/' into prefix and name
    // when the path is too long for the name field, truncated only when it cannot be split.
    private static (byte[] Prefix, byte[] Name) UstarName(byte[] path)
    {
        if (path.Length <= 100)
        {
            return ([], path);
        }

        for (int i = path.Length - 1; i > 0; i--)
        {
            if (path[i] == (byte)'/' && path.Length - i - 1 <= 100 && i <= 155)
            {
                return (path[..i], path[(i + 1)..]);
            }
        }

        int cut = 100;
        while (cut > 0 && (path[cut] & 0xC0) == 0x80)
        {
            cut--;
        }

        return ([], path[..cut]);
    }

    private static void WriteHeader(Stream output, byte[] name, byte[] prefix, long size, byte type)
    {
        byte[] header = new byte[Block];
        name.CopyTo(header, 0);
        Octal(header, 100, 8, 0x1A4); // 0644
        Octal(header, 108, 8, 0);
        Octal(header, 116, 8, 0);
        Octal(header, 124, 12, size);
        Octal(header, 136, 12, 0);
        header[156] = type;
        "ustar\0"u8.CopyTo(header.AsSpan(257));
        "00"u8.CopyTo(header.AsSpan(263));
        Octal(header, 329, 8, 0);
        Octal(header, 337, 8, 0);
        prefix.CopyTo(header, 345);

        // The checksum is computed with its own field read as eight spaces.
        header.AsSpan(148, 8).Fill((byte)' ');
        int sum = 0;
        foreach (byte b in header)
        {
            sum += b;
        }

        Octal(header, 148, 7, sum);
        header[155] = (byte)' ';
        output.Write(header);
    }

    // Zero-padded octal filling width - 1 digits, then NUL.
    private static void Octal(byte[] header, int offset, int width, long value)
    {
        for (int i = offset + width - 2; i >= offset; i--)
        {
            header[i] = (byte)('0' + (int)(value & 7));
            value >>= 3;
        }

        header[offset + width - 1] = 0;
    }

    private static void WritePadded(Stream output, ReadOnlySpan<byte> data)
    {
        output.Write(data);
        int remainder = data.Length % Block;
        if (remainder != 0)
        {
            output.Write(new byte[Block - remainder]);
        }
    }
}
