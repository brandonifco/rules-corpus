namespace RulesCorpus.Cli.Tests;

public sealed class AtomicFileTests
{
    private static string Fresh()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rules-corpus-atomic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Remove(string dir) => Directory.Delete(dir, recursive: true);

    [Fact]
    public void A_successful_write_leaves_exactly_the_destination()
    {
        string dir = Fresh();
        try
        {
            string destination = Path.Combine(dir, "out.tar");

            AtomicFile.CreateNew(destination, s => s.Write("complete"u8));

            Assert.Equal("complete", File.ReadAllText(destination));
            Assert.Equal([destination], Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void A_write_that_fails_after_starting_leaves_no_destination_and_no_temporary_file()
    {
        string dir = Fresh();
        try
        {
            string destination = Path.Combine(dir, "out.tar");

            Assert.Throws<InvalidOperationException>(() => AtomicFile.CreateNew(destination, s =>
            {
                s.Write("half of an arch"u8);
                throw new InvalidOperationException("the disk filled up");
            }));

            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void After_a_failed_write_the_same_destination_can_be_written()
    {
        string dir = Fresh();
        try
        {
            string destination = Path.Combine(dir, "out.tar");
            Assert.Throws<IOException>(() => AtomicFile.CreateNew(destination, _ => throw new IOException("no space left on device")));

            AtomicFile.CreateNew(destination, s => s.Write("whole"u8));

            Assert.Equal("whole", File.ReadAllText(destination));
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void An_existing_destination_is_refused_unchanged_with_no_temporary_file_left()
    {
        string dir = Fresh();
        try
        {
            string destination = Path.Combine(dir, "out.tar");
            File.WriteAllText(destination, "precious");

            RefusalException e = Assert.Throws<RefusalException>(() => AtomicFile.CreateNew(destination, s => s.Write("new"u8)));

            Assert.Contains("already exists", e.Message, StringComparison.Ordinal);
            Assert.Equal("precious", File.ReadAllText(destination));
            Assert.Equal([destination], Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            Remove(dir);
        }
    }

    [Fact]
    public void A_destination_in_a_missing_directory_fails_without_creating_anything()
    {
        string dir = Fresh();
        try
        {
            string destination = Path.Combine(dir, "missing", "out.tar");

            Assert.Throws<DirectoryNotFoundException>(() => AtomicFile.CreateNew(destination, s => s.Write("x"u8)));

            Assert.Empty(Directory.GetFileSystemEntries(dir));
        }
        finally
        {
            Remove(dir);
        }
    }
}
