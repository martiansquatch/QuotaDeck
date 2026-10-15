namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using System.IO;
    using System.Threading;

    /// <summary>
    /// The nine keys as one screen.
    ///
    /// The MX Creative Keypad is a single 480x480 LCD under a 3x3 grid of 118 px key windows (first window
    /// at 23,6; next ones every 158 px; geometry from neon-deck, MIT). While a <see cref="Scene"/> plays,
    /// every QuotaDeck key shows its own window of one shared frame instead of its face, so things can move
    /// across the whole panel and the bezels simply hide the gaps. A scene can also tint all keys at once
    /// on top of their normal faces (the quota pulse).
    /// </summary>
    public static class Panel
    {
        public const Int32 Width = 480;
        public const Int32 Height = 480;
        public const Int32 TileSize = 118;
        public const Int32 OriginX = 23;
        public const Int32 OriginY = 6;
        public const Int32 Pitch = 158;

        public static Int32 CellX(Int32 cell) => OriginX + (cell % 3) * Pitch;
        public static Int32 CellY(Int32 cell) => OriginY + (cell / 3) * Pitch;

        public abstract class Scene
        {
            /// <summary>Seconds the scene runs.</summary>
            public abstract Double Duration { get; }

            public virtual Int32 Fps => 8;

            /// <summary>
            /// Draw the whole 480x480 panel at time <paramref name="t"/>. Return false to show the keys' own
            /// faces instead (the <see cref="Overlay"/> still applies).
            /// </summary>
            public abstract Boolean DrawFrame(BitmapBuilder b, Double t);

            /// <summary>Tint laid over every key at time <paramref name="t"/>; alpha 0 = none.</summary>
            public virtual BitmapColor Overlay(Double t) => BitmapColor.Transparent;
        }

        private static readonly Object Gate = new();
        private static Scene? _scene;
        private static Timer? _timer;
        private static DateTime _started;
        private static BitmapImage? _frame;          // current shared frame, or null while the scene shows the faces
        private static BitmapColor _overlay = BitmapColor.Transparent;
        private static Int32 _frameNo;
        private static Int32 _rendering;

        public static Boolean Active => _scene != null;

        /// <summary>Start a scene (replacing any running one).</summary>
        public static void Play(Scene scene)
        {
            lock (Gate)
            {
                StopCore();
                _scene = scene;
                _started = DateTime.UtcNow;
                _frameNo = 0;
                _timer = new Timer(_ => Step(), null, 0, Math.Max(20, 1000 / Math.Max(1, scene.Fps)));
            }
            PluginLog.Info($"panel: playing {scene.GetType().Name} ({scene.Duration:0.0} s @ {scene.Fps} fps)");
        }

        /// <summary>End the running scene; the keys go back to their own faces (unless the plugin is unloading).</summary>
        public static void Stop(Boolean refresh = true)
        {
            String? name;
            lock (Gate)
            {
                name = _scene?.GetType().Name;
                StopCore();
            }
            if (name != null)
            {
                PluginLog.Info($"panel: stopped {name}");
            }
            if (refresh)
            {
                RefreshAll();
            }
        }

        private static void StopCore()
        {
            _timer?.Dispose();
            _timer = null;
            _scene = null;
            _frame = null;
            _overlay = BitmapColor.Transparent;
        }

        private static void Step()
        {
            if (Interlocked.Exchange(ref _rendering, 1) == 1)
            {
                return; // previous frame still rendering: drop this tick rather than queue up
            }
            try
            {
                var scene = _scene;
                if (scene == null)
                {
                    return;
                }
                var t = (DateTime.UtcNow - _started).TotalSeconds;
                if (t >= scene.Duration)
                {
                    Stop();
                    return;
                }
                BitmapImage? frame = null;
                using (var b = new BitmapBuilder(Width, Height))
                {
                    b.Clear(FaceRenderer.Background);
                    if (scene.DrawFrame(b, t))
                    {
                        frame = b.ToImage();
                    }
                }
                _overlay = scene.Overlay(t);
                _frame = frame;
                _frameNo++;
                SaveDebugFrame(frame, _overlay, _frameNo);
                RefreshAll();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "panel: frame failed");
            }
            finally
            {
                Interlocked.Exchange(ref _rendering, 0);
            }
        }

        private static void RefreshAll()
        {
            QuotaKey[] all;
            lock (QuotaKey.All)
            {
                all = QuotaKey.All.ToArray();
            }
            foreach (var key in all)
            {
                try
                {
                    key.RefreshFace();
                }
                catch
                {
                    // a key that is on its way out; nothing to show there anyway
                }
            }
        }

        /// <summary>
        /// What <paramref name="key"/> should show right now: its window of the shared frame, or its own face
        /// under the scene's tint. Null when no scene is playing or the key is not on the 3x3 grid.
        /// </summary>
        public static BitmapImage? Tile(QuotaKey key, PluginImageSize size, Func<Face> face)
        {
            if (_scene == null)
            {
                return null;
            }
            var cell = Layout.CellOf(key);
            if (cell < 0)
            {
                return null;
            }
            var frame = _frame;
            var overlay = _overlay;
            if (frame == null)
            {
                return overlay.A == 0 ? null : FaceRenderer.Draw(size, face(), overlay);
            }

            using var b = new BitmapBuilder(size);
            var s = b.Width / (Double)TileSize;                   // 116 px keys show a 118 px window
            var panel = (Int32)Math.Round(Width * s);
            b.Clear(FaceRenderer.Background);
            b.DrawImage(frame, -(Int32)Math.Round(CellX(cell) * s), -(Int32)Math.Round(CellY(cell) * s), panel, panel, BitmapRotation.None);
            if (overlay.A > 0)
            {
                b.FillRectangle(0, 0, b.Width, b.Height, overlay);
            }
            return b.ToImage();
        }

        // ---- debug: dump frames as PNG so a scene can be checked without the device ----

        /// <summary>
        /// Writes panel_NN.png (what the nine keys show together) and panelview_NN.png (the same with the
        /// bezels blacked out, i.e. as seen on the device). Scenes that draw a shared frame are dumped as
        /// is; scenes that only tint (the pulse) are composed from the keys' own faces plus the overlay.
        /// </summary>
        private static void SaveDebugFrame(BitmapImage? frame, BitmapColor overlay, Int32 no)
        {
            if (!DeckConfig.Current.DebugSnapshots)
            {
                return;
            }
            try
            {
                var dir = DeckConfig.SnapshotDirectory;
                Directory.CreateDirectory(dir);

                BitmapImage? composed = frame;
                BitmapImage? owned = null;
                if (composed == null)
                {
                    using var b = new BitmapBuilder(Width, Height);
                    b.Clear(FaceRenderer.Background);
                    QuotaKey[] all;
                    lock (QuotaKey.All)
                    {
                        all = QuotaKey.All.ToArray();
                    }
                    foreach (var key in all)
                    {
                        var cell = Layout.CellOf(key);
                        if (cell < 0)
                        {
                            continue;
                        }
                        using var face = key.RenderFace(PluginImageSize.Width116);
                        b.DrawImage(face, CellX(cell) + (TileSize - face.Width) / 2, CellY(cell) + (TileSize - face.Height) / 2, BitmapRotation.None);
                    }
                    owned = b.ToImage();
                    composed = owned;
                }

                using var view = new BitmapBuilder(Width, Height);
                view.Clear(BitmapColor.Black);
                view.DrawImage(composed, 0, 0, BitmapRotation.None);
                if (overlay.A > 0)
                {
                    view.FillRectangle(0, 0, Width, Height, overlay); // so the dump matches what the keys show
                }

                using var panel = view.ToImage();
                panel.SaveToFile(Path.Combine(dir, $"panel_{no:00}.png"));

                // "as seen on the device": the panel with the bezels blacked out
                var black = BitmapColor.Black;
                view.FillRectangle(0, 0, Width, OriginY, black);
                view.FillRectangle(0, OriginY + 2 * Pitch + TileSize, Width, Height, black);
                view.FillRectangle(0, 0, OriginX, Height, black);
                view.FillRectangle(OriginX + 2 * Pitch + TileSize, 0, Width, Height, black);
                for (var i = 0; i < 2; i++)
                {
                    view.FillRectangle(OriginX + TileSize + i * Pitch, 0, Pitch - TileSize, Height, black);
                    view.FillRectangle(0, OriginY + TileSize + i * Pitch, Width, Pitch - TileSize, black);
                }
                using var bezelView = view.ToImage();
                bezelView.SaveToFile(Path.Combine(dir, $"panelview_{no:00}.png"));

                owned?.Dispose();
            }
            catch
            {
                // debug aid only
            }
        }
    }
}
