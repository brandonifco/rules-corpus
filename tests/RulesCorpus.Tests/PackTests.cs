using System.Formats.Tar;
using RulesCorpus.Files;
using RulesCorpus.Tests.Support;

namespace RulesCorpus.Tests;

public class PackTests
{
    private static byte[] Pack(TempCorpus corpus, bool allowNotVerified = false)
    {
        using var output = new MemoryStream();
        CorpusPacker.Pack(corpus.Root, output, allowNotVerified: allowNotVerified);
        return output.ToArray();
    }

    private static VerificationReport VerifyPacked(byte[] tar, VerificationOptions? options = null)
    {
        using var input = new MemoryStream(tar);
        return CorpusVerifier.Verify(CorpusFiles.FromPacked(input), options);
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

    private static (string Path, byte[] Bytes)[] Entries(TempCorpus corpus, params string[] paths) =>
        paths.Select(p => (p, corpus.ReadBytes(p))).ToArray();

    [Fact]
    public void Packing_twice_gives_identical_bytes()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        Assert.Equal(Pack(corpus), Pack(corpus));
    }

    [Fact]
    public void Packing_the_same_corpus_in_another_directory_gives_identical_bytes()
    {
        using TempCorpus a = TempCorpus.Stored();
        using TempCorpus b = TempCorpus.Stored();
        a.Build();
        b.Build();

        Assert.Equal(Pack(a), Pack(b));
    }

    [Fact]
    public void The_pack_is_a_fixed_function_of_the_corpus_not_of_the_process()
    {
        // A tar writer that embeds a process id or a clock would pass the two tests above and
        // fail this one on the next run. The expected digest was produced by this writer; the
        // archive was read back by GNU tar 1.35 and Python's tarfile, which both reported mode
        // 0644, uid/gid 0, empty user and group names, mtime 0 and a PAX path header per entry.
        // It changes only if the packed form does, which is a compatibility event.
        byte[] tar = Tar(("a.txt", "hello\n"u8.ToArray()), ("dir/b.txt", []));

        Assert.Equal(4608, tar.Length);
        Assert.Equal("sha256:57bdbfb3e3302db7f2b370476e0a9b19999d70d6130d5216eea41547e771b9cf", ContentDigest.Compute(tar).ToString());
    }

    [Fact]
    public void Every_entry_is_a_pax_regular_file_with_fixed_metadata_in_ordinal_order()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        byte[] tar = Pack(corpus);

        using var reader = new TarReader(new MemoryStream(tar));
        var names = new List<string>();
        while (reader.GetNextEntry() is { } entry)
        {
            names.Add(entry.Name);
            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            Assert.Equal(TarEntryFormat.Pax, entry.Format);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, entry.Mode);
            Assert.Equal(0, entry.Uid);
            Assert.Equal(0, entry.Gid);
            Assert.Equal(DateTimeOffset.UnixEpoch, entry.ModificationTime);
            PaxTarEntry pax = Assert.IsType<PaxTarEntry>(entry);
            Assert.Equal(string.Empty, pax.UserName);
            Assert.Equal(string.Empty, pax.GroupName);
            Assert.Equal(corpus.ReadBytes(entry.Name), ReadAll(entry.DataStream));
        }

        Assert.Equal(["canonical/notes.txt", "corpus.build.json", "corpus.json", "sources/notes.txt"], names);
    }

    [Fact]
    public void A_path_too_long_for_the_ustar_name_field_is_carried_by_the_pax_header()
    {
        string path = string.Join('/', Enumerable.Repeat("directory-name", 12)) + "/file.txt";
        byte[] tar = Tar((path, "x"u8.ToArray()));

        using var reader = new TarReader(new MemoryStream(tar));
        Assert.Equal(path, reader.GetNextEntry()!.Name);
        Assert.Null(reader.GetNextEntry());
    }

    [Fact]
    public void A_packed_corpus_verifies_ok()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        VerificationReport report = VerifyPacked(Pack(corpus), new VerificationOptions { Rebuild = true, Adapters = [new LinesAdapter()] });

        Assert.Equal(VerificationOutcome.Ok, report.Outcome);
        Assert.Equal(VerificationOutcome.Ok, Assert.Single(report.Checks, c => c.Name == "package").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Assert.Single(report.Checks, c => c.Name == "rebuild notes-lines").Outcome);
    }

    [Fact]
    public void A_packed_corpus_with_a_tampered_artifact_fails_that_artifact()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        (string Path, byte[] Bytes)[] entries = Entries(corpus, "canonical/notes.txt", "corpus.build.json", "corpus.json", "sources/notes.txt");
        entries[3].Bytes[0] ^= 0x01;

        VerificationReport report = VerifyPacked(Tar(entries));

        Assert.Equal(VerificationOutcome.Failed, Assert.Single(report.Checks, c => c.Name == "artifact notes").Outcome);
        Assert.Equal(VerificationOutcome.Ok, Assert.Single(report.Checks, c => c.Name == "package").Outcome);
    }

    [Fact]
    public void A_packed_corpus_with_an_extra_entry_fails_the_package_check()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        var entries = Entries(corpus, "canonical/notes.txt", "corpus.build.json", "corpus.json").ToList();
        entries.Add(("extra.txt", "x"u8.ToArray()));
        entries.AddRange(Entries(corpus, "sources/notes.txt"));

        VerificationCheck package = Assert.Single(VerifyPacked(Tar([.. entries])).Checks, c => c.Name == "package");

        Assert.Equal(VerificationOutcome.Failed, package.Outcome);
        Assert.Contains("'extra.txt' is not", package.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_packed_corpus_out_of_ordinal_order_fails_the_package_check()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        VerificationReport report = VerifyPacked(Tar(Entries(corpus, "sources/notes.txt", "corpus.json", "corpus.build.json", "canonical/notes.txt")));

        Assert.Equal(VerificationOutcome.Failed, Assert.Single(report.Checks, c => c.Name == "package").Outcome);
    }

    private static byte[] RawTar(Action<TarWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            write(writer);
        }

        return output.ToArray();
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    [InlineData(TarEntryType.Directory)]
    public void An_entry_that_is_not_a_regular_file_is_refused_by_name_and_kind(TarEntryType kind)
    {
        byte[] tar = RawTar(w =>
        {
            PaxTarEntry entry = kind == TarEntryType.Directory
                ? new PaxTarEntry(kind, "sources")
                : new PaxTarEntry(kind, "sources/link") { LinkName = "/etc/passwd" };
            w.WriteEntry(entry);
        });
        using var input = new MemoryStream(tar);

        PackedCorpusFiles loaded = PackedCorpusFiles.Load(input, CorpusLimits.Default);

        string problem = Assert.Single(loaded.Problems);
        Assert.Contains("regular files only", problem, StringComparison.Ordinal);
        Assert.Contains(kind.ToString(), problem, StringComparison.Ordinal);
        Assert.False(loaded.IsCanonical);
    }

    [Fact]
    public void A_duplicate_entry_name_is_refused_and_the_first_is_not_silently_replaced()
    {
        byte[] tar = RawTar(w =>
        {
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "corpus.json") { DataStream = new MemoryStream("first"u8.ToArray()) });
            w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "corpus.json") { DataStream = new MemoryStream("second"u8.ToArray()) });
        });
        using var input = new MemoryStream(tar);

        PackedCorpusFiles loaded = PackedCorpusFiles.Load(input, CorpusLimits.Default);

        Assert.Contains("appears more than once", Assert.Single(loaded.Problems), StringComparison.Ordinal);
        Assert.True(loaded.TryRead("corpus.json", 100, out byte[] kept, out _));
        Assert.Equal("first"u8.ToArray(), kept);
    }

    [Fact]
    public void A_tar_with_other_metadata_fails_the_package_check()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (string path in new[] { "canonical/notes.txt", "corpus.build.json", "corpus.json", "sources/notes.txt" })
            {
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path)
                {
                    ModificationTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    DataStream = new MemoryStream(corpus.ReadBytes(path)),
                });
            }
        }

        VerificationCheck package = Assert.Single(VerifyPacked(output.ToArray()).Checks, c => c.Name == "package");

        Assert.Equal(VerificationOutcome.Failed, package.Outcome);
        Assert.Contains("not the canonical packing", package.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Trailing_bytes_after_the_archive_fail_the_package_check()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();

        VerificationReport report = VerifyPacked([.. Pack(corpus), .. "junk"u8.ToArray()]);

        Assert.Equal(VerificationOutcome.Failed, Assert.Single(report.Checks, c => c.Name == "package").Outcome);
    }

    [Fact]
    public void A_truncated_archive_is_reported_not_thrown()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        byte[] tar = Pack(corpus);

        VerificationReport report = VerifyPacked(tar[..700]);

        Assert.Equal(VerificationOutcome.Failed, report.Outcome);
    }

    [Fact]
    public void An_unsafe_entry_path_fails_the_package_check()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        var entries = Entries(corpus, "canonical/notes.txt", "corpus.build.json", "corpus.json", "sources/notes.txt").ToList();
        entries.Insert(0, ("../escape.txt", "x"u8.ToArray()));

        VerificationCheck package = Assert.Single(VerifyPacked(Tar([.. entries])).Checks, c => c.Name == "package");

        Assert.Contains("'..' component", package.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_refuses_a_corpus_that_fails_verification_and_writes_nothing()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        corpus.Build();
        corpus.WriteText("canonical/notes.txt", "tampered");
        using var output = new MemoryStream();

        Assert.Throws<CorpusException>(() => CorpusPacker.Pack(corpus.Root, output));

        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void Pack_refuses_a_not_verified_corpus_unless_allowed()
    {
        using TempCorpus corpus = TempCorpus.WithExternal();
        corpus.Build();

        CorpusException e = Assert.Throws<CorpusException>(() => Pack(corpus));
        byte[] tar = Pack(corpus, allowNotVerified: true);

        Assert.Contains(e.Errors, x => x.Path == "artifact rulebook-pdf");
        Assert.Equal(VerificationOutcome.NotVerified, VerifyPacked(tar).Outcome);
    }

    [Fact]
    public void A_manifest_loads_from_a_packed_corpus()
    {
        using TempCorpus corpus = TempCorpus.Stored();
        CorpusManifest built = corpus.Build();

        CorpusManifest loaded = CorpusManifest.Load(CorpusFiles.FromPacked(new MemoryStream(Pack(corpus))));

        Assert.Equal(built.ManifestDigest, loaded.ManifestDigest);
    }

    private static byte[] ReadAll(Stream? stream)
    {
        using var copy = new MemoryStream();
        stream?.CopyTo(copy);
        return copy.ToArray();
    }
}
