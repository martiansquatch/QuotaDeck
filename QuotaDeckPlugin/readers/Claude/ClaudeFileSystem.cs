namespace QuotaDeck.Readers.Claude;

// No Codenotch counterpart: Swift reads the filesystem through Foundation, and its tests build
// temporary directories for real. QuotaDeck keeps the same temporary-directory tests, but the reads
// go through this seam so a test can also drive the shapes the filesystem will not produce on
// demand — a file that vanishes mid-scan, a directory that cannot be listed, an exactly-sized entry.

/// <summary>How a file is stamped: what the account-file cache and the cache scan both gate on.</summary>
public sealed record FileStamp(DateTimeOffset Modified, long Size);

/// <summary>One regular file in a directory listing, with the two facts a scan sorts and filters on.</summary>
public sealed record DirectoryFile(string Path, DateTimeOffset Modified, long Size);

/// <summary>The filesystem, as the Claude reader uses it.</summary>
public interface IClaudeFileSystem
{
    /// <summary>
    /// Entry names in one directory — files and directories, hidden ones included, no recursion.
    /// Empty when the directory is absent or cannot be listed: "no Claude here" and "no permission"
    /// mean the same thing to every caller.
    /// </summary>
    IReadOnlyList<string> ListNames(string directory);

    /// <summary>
    /// Regular files in one directory, hidden files skipped, with modification time and size. Empty
    /// when the directory cannot be listed.
    /// </summary>
    IReadOnlyList<DirectoryFile> ListFiles(string directory);

    bool IsDirectory(string path);

    bool FileExists(string path);

    /// <summary>Modification time and size, or nil when the file is absent or unreadable.</summary>
    FileStamp? Stamp(string path);

    /// <summary>
    /// The first <paramref name="maxBytes"/> of a file, and no more. Empty when the file cannot be
    /// read. A short result is normal: the caller's parse decides whether it was enough.
    /// </summary>
    byte[] Read(string path, int maxBytes);

    /// <summary>Creates a directory and its parents. Throws when that cannot be done.</summary>
    void CreateDirectory(string path);

    /// <summary>Whether the file exists and carries an execute bit, "executable file" as the shell means it.</summary>
    bool IsExecutable(string path);
}

/// <summary>The real filesystem.</summary>
public sealed class SystemClaudeFileSystem : IClaudeFileSystem
{
    public IReadOnlyList<string> ListNames(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    public IReadOnlyList<DirectoryFile> ListFiles(string directory)
    {
        var files = new List<DirectoryFile>();
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return files;
        }

        foreach (var path in entries)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || (info.Attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                // Entries that vanish between the listing and here simply drop out: Chromium evicts
                // while we look, and that is not an error.
                files.Add(new DirectoryFile(path, new DateTimeOffset(info.LastWriteTimeUtc), info.Length));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return files;
    }

    public bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool FileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public FileStamp? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new FileStamp(new DateTimeOffset(info.LastWriteTimeUtc), info.Length)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public byte[] Read(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var length = (int)Math.Min(Math.Max(stream.Length, 0), maxBytes);
            var buffer = new byte[length];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool IsExecutable(string path)
    {
        try
        {
            if (!File.Exists(path) || OperatingSystem.IsWindows())
            {
                return false;
            }

            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
