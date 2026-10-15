namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using QuotaDeck.Core.Faces;
    using QuotaDeck.Core.Model;
    using QuotaDeck.Core.Readers;

    /// <summary>
    /// Base for the seven provider keys: one key per provider, drawn from the host's latest reading
    /// and the shared settings.
    ///
    /// The face is composed by <see cref="FaceComposer"/>; this class only maps it to the renderer's
    /// shape — the module's palette, the band colours of <see cref="FaceColors"/>, the ring sweep,
    /// and the grey-and-overlay treatment for a stale or refused reading. A provider with no module
    /// and no fixture can never be read at all, and says "not installed" instead.
    /// </summary>
    public abstract class ProviderKey : QuotaKey
    {
        /// <summary>The accent before any provider module exists to name one.</summary>
        internal static readonly ProviderPalette NeutralPalette = new(0, 200, 170);

        protected ProviderKey(String providerId, String displayName, String description)
            : base(displayName, description, "Quota Deck")
        {
            this.ProviderId = providerId;
            this.ProviderName = displayName;
        }

        public String ProviderId { get; }

        public String ProviderName { get; }

        protected override Face BuildFace()
        {
            var host = QuotaDeckPlugin.Instance?.Host;
            var module = host?.Module(this.ProviderId);
            var fixture = host?.HasFixture(this.ProviderId) ?? false;

            if (module is null && !fixture)
            {
                // Nothing can ever read this provider on this install: no module claims it and no
                // fixture stands in for it. That is a truthful face, not a fetch that never lands.
                return new Face
                {
                    Accent = ToColor(NeutralPalette),
                    Headline = "not installed",
                    Label = this.ProviderName,
                };
            }

            var snapshot = host?.Latest(this.ProviderId);
            var model = FaceComposer.Compose(snapshot, DeckConfig.Current, DateTimeOffset.Now);
            var palette = module?.Palette ?? NeutralPalette;
            var figures = snapshot is null ? null : FaceFigures.For(snapshot, DeckConfig.Current);

            return new Face
            {
                Accent = ToColor(FaceColors.Accent(model.Band, palette, model.Dimmed)),

                // A dimmed reading keeps its grey ring and takes a translucent overlay over the
                // whole face, so old numbers read as quiet without being blanked.
                Overlay = model.Dimmed
                    ? FaceRenderer.WithAlpha(new BitmapColor(0, 0, 0), FaceColors.DimmedAlpha)
                    : null,
                Progress = figures?.RingSweep,
                Headline = model.Headline ?? model.StatusText,
                Secondary = model.Secondary,
                Corner = model.Corner,
                Label = this.ProviderName,
            };
        }

        protected override void Execute(String actionParameter)
        {
            PluginLog.Info($"{this.ProviderId}: look");
            QuotaDeckPlugin.Instance?.Host?.Look(this.ProviderId);
        }

        private static BitmapColor ToColor(ProviderPalette palette) =>
            new BitmapColor(palette.Red, palette.Green, palette.Blue);
    }
}
