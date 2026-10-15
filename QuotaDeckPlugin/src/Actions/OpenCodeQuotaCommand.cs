namespace Loupedeck.QuotaDeckPlugin
{
    /// <summary>OpenCode's quota face.</summary>
    public sealed class OpenCodeQuotaCommand : ProviderKey
    {
        public OpenCodeQuotaCommand()
            : base("opencode", "OpenCode", "OpenCode usage ring")
        {
        }
    }
}
