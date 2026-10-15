namespace QuotaDeck.Readers.OpenCode;

using QuotaDeck.Core.Readers;

/// <summary>
/// QuotaDeck's entry point for OpenCode: the Go plan's usage faces.
///
/// Port of the registration half of Codenotch <c>Sources/Providers/OpenCodeProvider.swift</c>
/// (<c>id</c>/<c>displayName</c>/<c>glyph</c> lines 24-26 and <c>account()</c> lines 51-59, reduced
/// to what the QuotaDeck module contract asks for). Presence is asked of OpenCode's own files —
/// installed-but-signed-out and never-installed are different answers — and never touches a
/// credential, so a machine that never had OpenCode shows no cell at all.
/// </summary>
public sealed class OpenCodeModule : IProviderModule
{
    public string ProviderId => OpenCodeReader.ReaderId;

    public string DisplayName => OpenCodeReader.ReaderDisplayName;

    public ProviderPalette Palette { get; } = new(0xF5, 0xA5, 0x24);

    public IUsageReader CreateReader(ReaderContext context) => new OpenCodeReader(context);

    public bool IsPresent(ReaderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The context is deliberately unused: its DataDirectory is QuotaDeck's store, while
        // presence is a question about OpenCode's own data directory.
        return OpenCodeCredentials.Present();
    }
}
