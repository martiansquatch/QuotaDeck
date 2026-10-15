namespace Loupedeck.QuotaDeckPlugin
{
    /// <summary>Codex's quota face.</summary>
    public sealed class CodexQuotaCommand : ProviderKey
    {
        public CodexQuotaCommand()
            : base("codex", "Codex", "Codex usage ring")
        {
        }
    }
}
