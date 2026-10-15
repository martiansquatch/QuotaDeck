namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using QuotaDeck.Core.Model;

    /// <summary>
    /// Phase 1 key: one sample quota face, built through the state engine's <see cref="FaceFigures"/>
    /// from the current settings so the demo shows what a live face will — the headline, the weekly
    /// figure and the ring sweep all follow the remaining/used toggle. Pressing it plays the
    /// full-panel pulse, so a single key press demonstrates both the face renderer and the
    /// nine-keys-as-one-screen plumbing.
    /// </summary>
    public sealed class SampleFaceCommand : QuotaKey
    {
        private static readonly BitmapColor Teal = new BitmapColor(0, 200, 170);

        public SampleFaceCommand()
            : base("Sample face", "Static sample quota face used to verify the deck", "Quota Deck")
        {
        }

        protected override Face BuildFace()
        {
            // A fixed reading in the shape a reader returns: a 73%-used session with a 41%-used week.
            var snapshot = new ProviderSnapshot
            {
                Id = "sample",
                DisplayName = "Sample",
                Fidelity = Fidelity.Official,
                Windows =
                [
                    new LimitWindow { Id = "session", Label = "5-hour limit", UsedFraction = 0.73 },
                    new LimitWindow { Id = "weekly", Label = "Weekly limit", UsedFraction = 0.41 },
                ],
                HeadlineId = "session",
                WeeklyId = "weekly",
            };

            var figures = FaceFigures.For(snapshot, DeckConfig.Current);
            return new Face
            {
                Accent = Teal,
                Progress = figures.RingSweep,
                Headline = figures.Headline,
                Secondary = figures.Secondary,
                Corner = "↻ 2h 05m",
                Label = "Sample",
            };
        }

        protected override void Execute(String actionParameter)
        {
            PluginLog.Info("Sample face pressed: playing the panel pulse.");
            Panel.Play(new PulseScene(FaceRenderer.Alert));
        }
    }
}
