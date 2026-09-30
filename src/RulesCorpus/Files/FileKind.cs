using System.Runtime.InteropServices;

namespace RulesCorpus.Files;

/// <summary>What a directory entry is, examined without following it and without opening it.</summary>
internal enum EntryKind
{
    Missing,
    RegularFile,
    Directory,
    SymbolicLink,

    /// <summary>A named pipe, a device or a socket: something a corpus never holds.</summary>
    Special,

    /// <summary>The entry exists but its type could not be determined; treated as refused.</summary>
    Unknown,
}

/// <summary>
/// Classifies a directory entry before anything opens it.
///
/// <para>
/// Why a native call. Opening a named pipe for reading blocks until something opens it for
/// writing, so a corpus holding one at an artifact path would hang the verifier; a device such
/// as <c>/dev/zero</c> never ends. The type has to be known before the open. Evidence (SDK
/// 10.0.112, Linux): for a named pipe, a regular file, <c>/dev/null</c> and <c>/dev/zero</c>,
/// <see cref="FileInfo.Exists"/> is true, <see cref="FileSystemInfo.Attributes"/> is
/// <see cref="FileAttributes.Normal"/>, <see cref="FileSystemInfo.UnixFileMode"/> carries
/// permission bits only, <see cref="FileSystemInfo.LinkTarget"/> is null, and a
/// <c>FileSystemEnumerable</c> entry reports the same attributes. No managed API tells them
/// apart without opening, and the BCL cannot open with <c>O_NONBLOCK</c>.
/// </para>
///
/// <para>
/// So on Unix this calls <c>SystemNative_LStat</c> in <c>libSystem.Native</c>, the shim that
/// ships with every .NET runtime on Unix and that <see cref="FileInfo"/> itself calls. Its
/// <c>FileStatus</c> begins with two 32-bit fields, flags then mode, and the mode's type bits
/// use the POSIX values (<c>S_IFMT</c> 0xF000, <c>S_IFREG</c> 0x8000, <c>S_IFDIR</c> 0x4000,
/// <c>S_IFLNK</c> 0xA000). Rather than libc's <c>lstat</c>, whose structure layout and symbol
/// name differ by C library, architecture and operating system, this depends on one stable
/// layout. The structure is over-allocated so a larger future <c>FileStatus</c> cannot write
/// past it. If the entry point is missing the kind is <see cref="EntryKind.Unknown"/>, which
/// every caller refuses: a verifier that cannot tell what it is reading does not read it.
/// </para>
///
/// <para>
/// Windows has no named pipes or devices in the file-system namespace (device names are refused
/// by the path rules), so there the managed checks suffice. Between the check and the open
/// another process could swap the entry; a process that can do that during verification
/// already controls what is verified.
/// </para>
/// </summary>
internal static class FileKind
{
    private const int TypeMask = 0xF000;
    private const int RegularType = 0x8000;
    private const int DirectoryType = 0x4000;
    private const int SymbolicLinkType = 0xA000;

    public static EntryKind Of(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var info = new FileInfo(fullPath);
            if (info.LinkTarget is not null)
            {
                return EntryKind.SymbolicLink;
            }

            return System.IO.Directory.Exists(fullPath) ? EntryKind.Directory : info.Exists ? EntryKind.RegularFile : EntryKind.Missing;
        }

        int result;
        FileStatus status;
        try
        {
            result = LStat(fullPath, out status);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return File.Exists(fullPath) || System.IO.Directory.Exists(fullPath) ? EntryKind.Unknown : EntryKind.Missing;
        }

        if (result != 0)
        {
            // Missing, or a component is not a directory, or not searchable. Say which only
            // when the managed API agrees the entry is there.
            return File.Exists(fullPath) || System.IO.Directory.Exists(fullPath) ? EntryKind.Unknown : EntryKind.Missing;
        }

        return (status.Mode & TypeMask) switch
        {
            RegularType => EntryKind.RegularFile,
            DirectoryType => EntryKind.Directory,
            SymbolicLinkType => EntryKind.SymbolicLink,
            _ => EntryKind.Special,
        };
    }

    [DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat", ExactSpelling = true)]
    private static extern int LStat([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out FileStatus status);

    /// <summary>The leading fields of the shim's <c>FileStatus</c>; the rest is room it may write into.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 512)]
    private struct FileStatus
    {
        public int Flags;
        public int Mode;
    }
}
