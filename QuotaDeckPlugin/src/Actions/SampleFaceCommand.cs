namespace Loupedeck.QuotaDeckPlugin
{
    using System;

    /// <summary>
    /// Phase 1 key: one static quota face. Pressing it plays the full-panel pulse, so a single key press
    /// demonstrates both the face renderer and the nine-keys-as-one-screen plumbing.
    /// </summary>
    public sealed class SampleFaceCommand : QuotaKey
    {
        private static readonly BitmapColor Teal = new BitmapColor(0, 200, 170);

        public SampleFaceCommand()
            : base("Sample face", "Static sample quota face used to verify the deck", "Quota Deck")
        {
        }

        protected override Face BuildFace() => new Face
        {
            Accent = Teal,
            Progress = 0.73,
            Headline = "73%",
            Secondary = "wk 41%",
            Corner = "↻ 2h 05m",
            Label = "Sample",
        };

        protected override void Execute(String actionParameter)
        {
            PluginLog.Info("Sample face pressed: playing the panel pulse.");
            Panel.Play(new PulseScene(FaceRenderer.Alert));
        }
    }
}
