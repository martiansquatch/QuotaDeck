namespace Loupedeck.QuotaDeckPlugin
{
    /// <summary>Muse's quota face.</summary>
    public sealed class MuseQuotaCommand : ProviderKey
    {
        public MuseQuotaCommand()
            : base("muse", "Muse", "Muse usage ring")
        {
        }
    }
}
