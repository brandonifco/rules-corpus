using System.Formats.Tar;
using System.Security.Cryptography;

namespace RulesCorpus.Files;

/// <summary>
/// A packed corpus held in memory. Loading records every structural problem rather than
/// throwing, and records whether the archive is byte-for-byte the canonical packing of its own
/// entries, which is how a verifier proves the fixed metadata, entry order and absence of
/// trailing data all at once.
/// </summary>
internal sealed class PackedCorpusFiles : CorpusFiles
{
    private readonly Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

    private PackedCorpusFiles()
    {
    }

    /// <summary>Entry names in archive order, including any that were refused.</summary>
    public List<string> EntryNames { get; } = [];

    /// <summary>Structural problems: unreadable archive, non-file entries, unsafe or duplicate names, oversized entries.</summary>
    public List<string> Problems { get; } = [];

    /// <summary>Whether the archive's bytes are exactly the canonical packing of its entries.</summary>
    public bool IsCanonical { get; private set; }

    internal override string Description => "packed corpus";

    public static PackedCorpusFiles Load(Stream packed, CorpusLimits limits)
    {
        var result = new PackedCorpusFiles();
        long maxEntry = Math.Max(limits.MaxArtifactBytes, limits.MaxManifestBytes);
        using var hashing = new HashingReadStream(packed);
        try
        {
            using var reader = new TarReader(hashing, leaveOpen: true);
            while (reader.GetNextEntry(copyData: false) is { } entry)
            {
                string name = entry.Name;
                result.EntryNames.Add(name);
                if (entry.EntryType != TarEntryType.RegularFile)
                {
                    result.Problems.Add($"entry '{name}' is a {entry.EntryType} entry; a packed corpus holds regular files only");
                    continue;
                }

                if (PathProblem(name) is { } problem)
                {
                    result.Problems.Add($"entry '{name}' {problem}");
                    continue;
                }

                if (result.files.ContainsKey(name))
                {
                    result.Problems.Add($"entry '{name}' appears more than once");
                    continue;
                }

                if (entry.Length > maxEntry)
                {
                    result.Problems.Add($"entry '{name}' is {entry.Length} bytes; the limit is {maxEntry}");
                    continue;
                }

                byte[] data = new byte[entry.Length];
                entry.DataStream?.ReadExactly(data);
                result.files.Add(name, data);
            }

            hashing.CopyTo(Stream.Null);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException or FormatException or ArgumentException or IOException)
        {
            result.Problems.Add("the archive is not a readable tar: " + e.Message);
            return result;
        }

        byte[] archiveHash = hashing.Hash();
        using var canonical = new HashingWriteStream();
        foreach (string name in result.EntryNames)
        {
            if (result.files.TryGetValue(name, out byte[]? data))
            {
                PaxTarWriter.WriteEntry(canonical, name, data);
            }
        }

        PaxTarWriter.WriteEnd(canonical);
        result.IsCanonical = result.Problems.Count == 0 && archiveHash.AsSpan().SequenceEqual(canonical.Hash());
        return result;
    }

    internal override bool TryRead(string relativePath, long maxBytes, out byte[] bytes, out string problem)
    {
        bytes = [];
        problem = string.Empty;
        if (PathProblem(relativePath) is { } p)
        {
            problem = $"'{relativePath}' {p}";
            return false;
        }

        if (!files.TryGetValue(relativePath, out byte[]? data))
        {
            problem = $"'{relativePath}' is not in the packed corpus";
            return false;
        }

        if (data.LongLength > maxBytes)
        {
            problem = $"'{relativePath}' is {data.LongLength} bytes; the limit is {maxBytes}";
            return false;
        }

        bytes = data;
        return true;
    }

    /// <summary>Hashes every byte read through it; not seekable, so the tar reader reads sequentially.</summary>
    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public byte[] Hash() => hash.GetHashAndReset();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            hash.AppendData(buffer[..n]);
            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                hash.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>A write-only stream that only hashes, so a canonical packing can be compared without holding it.</summary>
internal sealed class HashingWriteStream : Stream
{
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public byte[] Hash() => hash.GetHashAndReset();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer) => hash.AppendData(buffer);

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hash.Dispose();
        }

        base.Dispose(disposing);
    }
}
