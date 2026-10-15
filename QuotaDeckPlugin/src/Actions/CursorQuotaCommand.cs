namespace Loupedeck.QuotaDeckPlugin
{
    /// <summary>Cursor's quota face.</summary>
    public sealed class CursorQuotaCommand : ProviderKey
    {
        public CursorQuotaCommand()
            : base("cursor", "Cursor", "Cursor usage ring")
        {
        }
    }
}
