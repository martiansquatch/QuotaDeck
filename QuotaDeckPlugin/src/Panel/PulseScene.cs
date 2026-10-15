namespace Loupedeck.QuotaDeckPlugin
{
    using System;

    /// <summary>
    /// The "a limit is about to run out" pulse: every key tints in one colour on top of its own face,
    /// three times, 0.6 s per pulse at 12 fps.
    /// (Same shape as neon-deck's AlertScene, MIT; DrawFrame returns false so the keys keep their faces.)
    /// </summary>
    public sealed class PulseScene : Panel.Scene
    {
        private const Int32 Pulses = 3;
        private const Double Period = 0.6;
        private const Int32 PeakAlpha = 170;

        private readonly BitmapColor _color;

        public PulseScene(BitmapColor color)
        {
            this._color = color;
        }

        public override Double Duration => Pulses * Period;

        public override Int32 Fps => 12;

        public override Boolean DrawFrame(BitmapBuilder b, Double t) => false;

        public override BitmapColor Overlay(Double t)
        {
            var phase = (t % Period) / Period;
            var alpha = Math.Abs(Math.Sin(phase * Math.PI));
            return FaceRenderer.WithAlpha(this._color, (Int32)(alpha * PeakAlpha));
        }
    }
}
