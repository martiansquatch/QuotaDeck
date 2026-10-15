namespace Loupedeck.QuotaDeckPlugin
{
    /// <summary>Claude's quota face.</summary>
    public sealed class ClaudeQuotaCommand : ProviderKey
    {
        public ClaudeQuotaCommand()
            : base("claude", "Claude", "Claude usage ring")
        {
        }
    }
}
