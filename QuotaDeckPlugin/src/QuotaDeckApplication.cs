namespace Loupedeck.QuotaDeckPlugin
{
    using System;

    // QuotaDeck is a universal (no-application) plugin, but the macOS Plugin Service still expects a
    // ClientApplication subclass to exist. Without one it logs "Cannot load plugin" and blacklists the
    // plugin until the service restarts. It is never linked to an application.
    public class QuotaDeckApplication : ClientApplication
    {
        public QuotaDeckApplication()
        {
        }

        protected override String GetProcessName() => "";

        protected override String GetBundleName() => "";

        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}
