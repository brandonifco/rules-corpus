using System.Diagnostics;

namespace RulesCorpus.Tests.Support;

/// <summary>
/// File-system objects the BCL cannot create: hard links and named pipes. Tests only; src/ never
/// starts a process. Callers return early on Windows, where neither applies to a corpus.
/// </summary>
internal static class Posix
{
    public static bool Supported => !OperatingSystem.IsWindows();

    public static void HardLink(string existing, string link) => Run("ln", existing, link);

    public static void MakeFifo(string path) => Run("mkfifo", path);

    /// <summary>
    /// Opens the pipe for writing and closes it, which releases a reader blocked opening it, so a
    /// test that caught a hang does not leave a thread stuck behind it.
    /// </summary>
    public static void ReleaseFifoReader(string path)
    {
        try
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException)
        {
        }
    }

    private static void Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
