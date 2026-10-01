using RulesCorpus.Files;

namespace RulesCorpus.Tests;

public class LimitsTests
{
    private static readonly long MaxArray = Array.MaxLength;

    public static TheoryData<string, long> InvalidSizes() => new()
    {
        { "MaxArtifactBytes", 0 },
        { "MaxArtifactBytes", -1 },
        { "MaxArtifactBytes", long.MinValue },
        { "MaxArtifactBytes", MaxArray + 1 },
        { "MaxArtifactBytes", long.MaxValue },
        { "MaxManifestBytes", 0 },
        { "MaxManifestBytes", -1 },
        { "MaxManifestBytes", MaxArray + 1 },
        { "MaxManifestBytes", long.MaxValue },
        { "MaxPackedBytes", 0 },
        { "MaxPackedBytes", -1 },
        { "MaxSegments", 0 },
        { "MaxSegments", -1 },
        { "MaxPackedEntries", 0 },
        { "MaxPackedEntries", -1 },
    };

    private static CorpusLimits With(string name, long value) => name switch
    {
        "MaxArtifactBytes" => new CorpusLimits { MaxArtifactBytes = value },
        "MaxManifestBytes" => new CorpusLimits { MaxManifestBytes = value },
        "MaxPackedBytes" => new CorpusLimits { MaxPackedBytes = value },
        "MaxSegments" => new CorpusLimits { MaxSegments = (int)value },
        "MaxPackedEntries" => new CorpusLimits { MaxPackedEntries = (int)value },
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Theory]
    [MemberData(nameof(InvalidSizes))]
    public void An_invalid_limit_is_refused_where_it_is_set_naming_the_limit(string name, long value)
    {
        ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => With(name, value));

        Assert.Equal(name, e.ParamName);
        Assert.Contains(name, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_with_expression_is_validated_too()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CorpusLimits.Default with { MaxArtifactBytes = -5 });
    }

    [Fact]
    public void The_smallest_and_largest_allocatable_values_are_accepted()
    {
        var smallest = new CorpusLimits { MaxArtifactBytes = 1, MaxManifestBytes = 1, MaxSegments = 1, MaxPackedEntries = 1, MaxPackedBytes = 1 };
        var largest = new CorpusLimits { MaxArtifactBytes = MaxArray, MaxManifestBytes = MaxArray, MaxSegments = int.MaxValue, MaxPackedEntries = int.MaxValue, MaxPackedBytes = long.MaxValue };

        Assert.Equal(1, smallest.MaxArtifactBytes);
        Assert.Equal(MaxArray, largest.MaxArtifactBytes);
        Assert.Equal(long.MaxValue, largest.MaxPackedBytes);
    }

    [Fact]
    public void The_defaults_are_valid_and_unchanged()
    {
        CorpusLimits d = CorpusLimits.Default;

        Assert.Equal(256L * 1024 * 1024, d.MaxArtifactBytes);
        Assert.Equal(256L * 1024 * 1024, d.MaxManifestBytes);
        Assert.Equal(1_000_000, d.MaxSegments);
        Assert.Equal(10_000, d.MaxPackedEntries);
        Assert.Equal(1024L * 1024 * 1024, d.MaxPackedBytes);
    }

    private static byte[] Tar(params (string Path, byte[] Bytes)[] entries)
    {
        using var output = new MemoryStream();
        foreach ((string path, byte[] bytes) in entries)
        {
            PaxTarWriter.WriteEntry(output, path, bytes);
        }

        PaxTarWriter.WriteEnd(output);
        return output.ToArray();
    }

    private static PackedCorpusFiles Load(byte[] tar, CorpusLimits limits)
    {
        using var stream = new MemoryStream(tar);
        return PackedCorpusFiles.Load(stream, limits);
    }

    [Fact]
    public void An_archive_with_more_entries_than_the_limit_is_refused_naming_the_count_and_the_limit()
    {
        byte[] tar = Tar(("a", [1]), ("b", [2]), ("c", [3]));

        PackedCorpusFiles loaded = Load(tar, new CorpusLimits { MaxPackedEntries = 2 });

        string problem = Assert.Single(loaded.Problems);
        Assert.Contains("entries", problem, StringComparison.Ordinal);
        Assert.Contains("limit is 2", problem, StringComparison.Ordinal);
        Assert.False(loaded.IsCanonical);
    }

    [Fact]
    public void An_archive_over_the_total_byte_limit_is_refused_not_truncated()
    {
        byte[] tar = Tar(("a", new byte[600]), ("b", new byte[600]), ("c", new byte[600]));

        PackedCorpusFiles loaded = Load(tar, new CorpusLimits { MaxPackedBytes = 1000 });

        string problem = Assert.Single(loaded.Problems);
        Assert.Contains("1000", problem, StringComparison.Ordinal);
        Assert.Contains("'b'", problem, StringComparison.Ordinal);
        Assert.False(loaded.TryRead("a", 1000, out _, out _) && loaded.TryRead("c", 1000, out _, out _), "a refused archive must not be partly readable as if whole");
    }

    [Fact]
    public void Refusal_is_the_same_on_every_load()
    {
        byte[] tar = Tar(("a", new byte[600]), ("b", new byte[600]));
        var limits = new CorpusLimits { MaxPackedBytes = 1000 };

        Assert.Equal(Load(tar, limits).Problems, Load(tar, limits).Problems);
    }

    [Fact]
    public void An_archive_at_exactly_the_limits_loads()
    {
        byte[] tar = Tar(("a", new byte[500]), ("b", new byte[500]));

        PackedCorpusFiles loaded = Load(tar, new CorpusLimits { MaxPackedEntries = 2, MaxPackedBytes = 1000 });

        Assert.Empty(loaded.Problems);
        Assert.True(loaded.IsCanonical);
    }

    [Fact]
    public void The_per_entry_limits_still_apply_inside_the_aggregate()
    {
        byte[] tar = Tar(("a", new byte[600]));

        PackedCorpusFiles loaded = Load(tar, new CorpusLimits { MaxArtifactBytes = 500, MaxManifestBytes = 500 });

        Assert.Contains("the limit is 500", Assert.Single(loaded.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void A_normal_packed_corpus_is_unaffected_by_the_default_limits()
    {
        using var corpus = Support.TempCorpus.Stored();
        corpus.Build();
        using var packed = new MemoryStream();
        CorpusPacker.Pack(corpus.Root, packed);

        PackedCorpusFiles loaded = Load(packed.ToArray(), CorpusLimits.Default);

        Assert.Empty(loaded.Problems);
        Assert.True(loaded.IsCanonical);
    }
}
