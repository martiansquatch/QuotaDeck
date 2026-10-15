namespace QuotaDeck.Readers.Tests.Claude;

using QuotaDeck.Readers.Claude;

/// <summary>
/// An in-memory filesystem, for the shapes a temporary directory cannot produce on demand: a file
/// that vanishes between the listing and the read, a directory that cannot be listed, an
/// exactly-sized entry. Temporary directories are used for everything else — the same shape the
/// Swift tests use.
/// </summary>
public sealed class FakeClaudeFileSystem : IClaudeFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, DateTimeOffset> Modified { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Directories { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Executables { get; } = new(StringComparer.Ordinal);

    /// <summary>Asked before every read; true means the file is gone by then.</summary>
    public Func<string, bool>? Vanished { get; set; }

    /// <summary>Directories whose listing fails, so "no permission" and "not there" read alike.</summary>
    public HashSet<string> Unlistable { get; } = new(StringComparer.Ordinal);

    public void AddFile(string path, byte[] bytes, DateTimeOffset? modified = null)
    {
        this.Files[path] = bytes;
        this.Modified[path] = modified ?? DateTimeOffset.UnixEpoch;
        this.Directories.Add(DirectoryName(path));
    }

    /// <summary>A file that can be run, with every directory above it, so listings find it.</summary>
    public void AddExecutable(string path)
    {
        this.Files[path] = [];
        this.Modified[path] = DateTimeOffset.UnixEpoch;
        this.Executables.Add(path);

        var directory = DirectoryName(path);
        while (this.Directories.Add(directory))
        {
            var parent = DirectoryName(directory);
            if (parent == directory)
            {
                break;
            }

            directory = parent;
        }
    }

    public void AddDirectory(string path) => this.Directories.Add(path);

    public IReadOnlyList<string> ListNames(string directory)
    {
        if (this.Unlistable.Contains(directory))
        {
            return [];
        }

        return this.Directories
            .Where(dir => DirectoryName(dir) == directory)
            .Select(Path.GetFileName)
            .Concat(this.Files.Keys
                .Where(file => DirectoryName(file) == directory)
                .Select(Path.GetFileName))
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    public IReadOnlyList<DirectoryFile> ListFiles(string directory)
    {
        if (this.Unlistable.Contains(directory))
        {
            return [];
        }

        return this.Files.Keys
            .Where(file => DirectoryName(file) == directory)
            .Where(file => !Path.GetFileName(file).StartsWith('.'))
            .Select(file => new DirectoryFile(file, this.Modified[file], this.Files[file].LongLength))
            .ToList();
    }

    public bool IsDirectory(string path) => this.Directories.Contains(path.TrimEnd('/'));

    public bool FileExists(string path) => this.Files.ContainsKey(path);

    public FileStamp? Stamp(string path) =>
        this.Files.TryGetValue(path, out var bytes)
            ? new FileStamp(this.Modified[path], bytes.LongLength)
            : null;

    public byte[] Read(string path, int maxBytes)
    {
        if (this.Vanished?.Invoke(path) == true || !this.Files.TryGetValue(path, out var bytes))
        {
            return [];
        }

        return bytes.Length <= maxBytes ? bytes : bytes[..maxBytes];
    }

    public void CreateDirectory(string path) => this.Directories.Add(path.TrimEnd('/'));

    public bool IsExecutable(string path) => this.Executables.Contains(path);

    private static string DirectoryName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }
}
