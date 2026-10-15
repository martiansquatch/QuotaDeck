namespace QuotaDeck.Core.Readers;

/// <summary>
/// One provider's entry point: who it is, how to draw it, how to read it, and whether it looks
/// installed at all.
///
/// The contract the plugin scans for. A module is a tiny, stateless factory — the reader itself is
/// built per call by <see cref="CreateReader"/> — so discovery can instantiate every module once and
/// ask cheap questions without touching credentials.
/// </summary>
public interface IProviderModule
{
    /// <summary>Stable id, used as the settings key and the alert's provider id.</summary>
    string ProviderId { get; }

    string DisplayName { get; }

    ProviderPalette Palette { get; }

    /// <summary>The reader for this provider, configured with the context's services and store.</summary>
    IUsageReader CreateReader(ReaderContext context);

    /// <summary>
    /// A cheap presence probe — does this machine look like it has the provider at all (a config
    /// directory, a CLI on PATH)? Used to decide whether a key shows "not installed" instead of a
    /// reader that is certain to fail. Never touches a credential prompt.
    /// </summary>
    bool IsPresent(ReaderContext context);
}
