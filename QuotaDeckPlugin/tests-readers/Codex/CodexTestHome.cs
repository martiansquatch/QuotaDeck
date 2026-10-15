namespace QuotaDeck.Readers.Tests.Codex;

/// <summary>
/// A throwaway home directory, so profile and credential tests never touch the real one.
/// Mirrors the <c>home(_:)</c> helper in Codenotch <c>Tests/CodexProfileTests.swift</c> (lines 6-22).
/// </summary>
internal sealed class CodexTestHome : IDisposable
{
    public CodexTestHome()
    {
        this.Root = Directory.CreateTempSubdirectory("quotadeck-codex-").FullName;
    }

    public string Root { get; }

    /// <summary>Directories and the files each contains, like the Swift layout helper.</summary>
    public CodexTestHome WithLayout(IReadOnlyDictionary<string, string[]> layout)
    {
        foreach (var (directory, files) in layout)
        {
            foreach (var file in files)
            {
                this.WriteFile(Path.Combine(directory, file), string.Empty);
            }
        }

        return this;
    }

    /// <summary>Writes one file under the root, creating its directories.</summary>
    public string WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(this.Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(this.Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory must not fail a test run.
        }
    }
}
