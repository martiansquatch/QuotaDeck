namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using System.IO;

    /// <summary>
    /// Draws one <see cref="Face"/> onto a key bitmap. Everything is scaled from the bitmap width so
    /// PluginImageSize.Width116 (the device) and PluginImageSize.Width90 (the Options+ preview, which
    /// BitmapBuilder rasterises at 80x80) both look right.
    ///
    /// Layout: near-black tile, thin accent hairline at the top, corner text in the top band, a progress
    /// ring centred in the space between the bands (track at low alpha + arc from -90 degrees), the
    /// headline inside the ring with the secondary figure just under it, and the label at the bottom.
    /// A scene tint (overlay) is filled last, over everything.
    ///
    /// The drawing-call shapes (BitmapBuilder Clear / FillRectangle / DrawRectangle / DrawArc / DrawText /
    /// FillCircle / ToImage) follow neon-deck's Neon.cs (MIT).
    /// </summary>
    public static class FaceRenderer
    {
        public static readonly BitmapColor Background = new BitmapColor(12, 15, 20);
        public static readonly BitmapColor Text = new BitmapColor(235, 240, 247);
        public static readonly BitmapColor Dim = new BitmapColor(128, 140, 158);

        /// <summary>Whole-panel pulse colour for "a limit is about to run out".</summary>
        public static readonly BitmapColor Alert = new BitmapColor(255, 70, 90);

        /// <summary>Same colour with a different alpha, for tracks, hairlines and tinted text.</summary>
        public static BitmapColor WithAlpha(BitmapColor color, Int32 alpha) => new BitmapColor(color, alpha);

        /// <summary>
        /// Render a face at the given device/preview size. When <paramref name="callerName"/> is set and
        /// debug snapshots are on, the result is also saved as &lt;snapshots&gt;/&lt;callerName&gt;_&lt;size&gt;.png.
        /// </summary>
        public static BitmapImage Draw(PluginImageSize size, Face face, BitmapColor? overlay = null, String? callerName = null)
        {
            using var b = new BitmapBuilder(size);
            var w = b.Width;
            var h = b.Height;
            var pad = Math.Max(2, w / 24);

            b.Clear(Background);

            // accent hairline at the very top; brighter while active
            var hairH = Math.Max(2, h / 24);
            b.FillRectangle(0, 0, w, hairH, face.Active ? face.Accent : WithAlpha(face.Accent, 160));
            if (face.Active)
            {
                b.DrawRectangle(0, 0, w - 1, h - 1, WithAlpha(face.Accent, 190));
            }

            // top band holds the corner text, bottom band the label
            var bandH = Math.Max(hairH + 2, (Int32)(h * 0.17));
            var labelH = (Int32)(h * 0.17);

            if (!String.IsNullOrEmpty(face.Corner))
            {
                var cornerFont = Math.Max(8, (Int32)(w * 0.10));
                b.DrawText(face.Corner, pad, hairH, (Int32)(w * 0.64), bandH, WithAlpha(Text, 235), cornerFont, cornerFont + 3, 2, null);
            }

            // ring: centred between the two text bands
            var ringTop = bandH;
            var ringArea = h - bandH - labelH;
            var cx = w / 2;
            var cy = ringTop + ringArea / 2;
            var radius = Math.Min(w / 2, ringArea / 2) - Math.Max(2, w / 22);
            var stroke = Math.Max(2f, w / 24f);

            if (face.Progress.HasValue)
            {
                b.DrawArc(cx, cy, radius, -90, 360, WithAlpha(face.Accent, 55), stroke);
                var sweep = (Single)(Math.Clamp(face.Progress.Value, 0, 1) * 360);
                if (sweep > 0.5f)
                {
                    b.DrawArc(cx, cy, radius, -90, sweep, face.Accent, stroke);
                }
            }

            // headline inside the ring, with the secondary figure directly under it
            var hasSecondary = !String.IsNullOrEmpty(face.Secondary);
            if (!String.IsNullOrEmpty(face.Headline))
            {
                var headlineFont = HeadlineFont(w, face.Headline);
                var headlineH = headlineFont + 4;
                var secondaryFont = Math.Max(8, (Int32)(w * 0.105));
                var secondaryH = hasSecondary ? secondaryFont + 4 : 0;
                var top = cy - (headlineH + secondaryH) / 2;
                b.DrawText(face.Headline, 0, top, w, headlineH, Text, headlineFont, headlineFont + 2, 2, null);
                if (hasSecondary)
                {
                    b.DrawText(face.Secondary, 0, top + headlineH, w, secondaryH, face.Accent, secondaryFont, secondaryFont + 2, 2, null);
                }
            }

            // bottom label
            if (!String.IsNullOrEmpty(face.Label))
            {
                var labelFont = Math.Max(8, (Int32)(w * 0.13));
                b.DrawText(face.Label, pad / 2, h - labelH - pad / 2, w - pad, labelH, Text, labelFont, labelFont + 2, 2, null);
            }

            // scene tint last, over everything
            if (overlay.HasValue && overlay.Value.A > 0)
            {
                b.FillRectangle(0, 0, w, h, overlay.Value);
            }

            var image = b.ToImage();
            SaveSnapshot(size, callerName, image);
            return image;
        }

        /// <summary>Headline font size: the longer the text, the smaller it must get to stay inside the ring.</summary>
        private static Int32 HeadlineFont(Int32 width, String headline)
        {
            var ratio = headline.Length switch
            {
                <= 3 => 0.21,
                4 => 0.17,
                _ => 0.145,
            };
            return Math.Max(9, (Int32)(width * ratio));
        }

        private static void SaveSnapshot(PluginImageSize size, String? callerName, BitmapImage image)
        {
            if (callerName == null || !DeckConfig.Current.DebugSnapshots)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(DeckConfig.SnapshotDirectory);
                image.SaveToFile(Path.Combine(DeckConfig.SnapshotDirectory, $"{callerName}_{size}.png"));
            }
            catch
            {
                // debug aid only
            }
        }
    }
}
