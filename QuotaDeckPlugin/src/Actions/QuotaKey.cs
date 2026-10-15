namespace Loupedeck.QuotaDeckPlugin
{
    using System;

    /// <summary>
    /// Base for every QuotaDeck key.
    ///
    /// The MX Creative Keypad delivers RunCommand for the press AND the release ~200 ms apart, so acting
    /// on RunCommand alone makes toggle-style keys undo themselves. <see cref="RunCommand"/> is therefore
    /// sealed: it debounces, and <see cref="ProcessButtonEvent2"/> forwards the Press edge only.
    /// Concrete keys override <see cref="Execute"/>, never RunCommand.
    /// (Behaviour verified on this keypad by neon-deck, MIT.)
    /// </summary>
    public abstract class QuotaKey : PluginDynamicCommand
    {
        /// <summary>Every live instance, so the plugin can refresh, pre-render and snapshot all keys.</summary>
        public static readonly List<QuotaKey> All = new();

        protected QuotaKey(String displayName, String description, String groupName)
            : base(displayName: displayName, description: description, groupName: groupName)
        {
            this.AutoRepeatOnLongPress = false;
            lock (All)
            {
                All.Add(this);
            }
        }

        /// <summary>Describe the current face. Called on every redraw, so keep it cheap.</summary>
        protected abstract Face BuildFace();

        /// <summary>The key's action. Runs once per physical press (see <see cref="RunCommand"/>).</summary>
        protected abstract void Execute(String actionParameter);

        private DateTime _lastRun = DateTime.MinValue;
        private const Int32 DebounceMs = 350;

        protected sealed override void RunCommand(String actionParameter)
        {
            var now = DateTime.UtcNow;
            if ((now - this._lastRun).TotalMilliseconds < DebounceMs)
            {
                PluginLog.Verbose($"{this.GetType().Name}: duplicate RunCommand ignored");
                return;
            }
            this._lastRun = now;
            this.Execute(actionParameter);
        }

        protected override Boolean ProcessButtonEvent2(String actionParameter, DeviceButtonEvent2 buttonEvent)
        {
            PluginLog.Verbose($"{this.GetType().Name}: button {buttonEvent.EventType} ({buttonEvent.PressDuration} ms)");
            if (buttonEvent.EventType == DeviceButtonEventType.Press)
            {
                return base.ProcessButtonEvent2(actionParameter, buttonEvent);
            }
            // Release / LongPress / RepeatPress: swallow so they never turn into a second RunCommand.
            return true;
        }

        protected override BitmapImage GetCommandImage(String actionParameter, PluginImageSize imageSize)
        {
            var size = imageSize == PluginImageSize.None ? PluginImageSize.Width90 : imageSize;
            var face = this.BuildFace();
            if (Panel.Active)
            {
                var shared = Panel.Tile(this, size, () => face); // the nine keys as one screen while a scene plays
                if (shared != null)
                {
                    return shared;
                }
            }
            return FaceRenderer.Draw(size, face, face.Overlay, this.GetType().Name);
        }

        // The face already carries its label; keep the service from stacking a second one on top.
        protected override String? GetCommandDisplayName(String actionParameter, PluginImageSize imageSize) => null;

        /// <summary>Ask the service to redraw this key.</summary>
        protected void Refresh() => this.ActionImageChanged();

        /// <summary>Redraw from outside the key (used by <see cref="Panel"/>).</summary>
        public void RefreshFace() => this.ActionImageChanged();

        /// <summary>Render this key's face once (debug snapshots; FaceRenderer writes the PNG).</summary>
        public void RenderForSnapshot(PluginImageSize size)
        {
            this.TryGetCommandImage(null, size, out var image);
            image?.Dispose();
        }

        /// <summary>Render the bare face (no scene, no snapshot side effect); used by the panel debug dump.</summary>
        internal BitmapImage RenderFace(PluginImageSize size) => FaceRenderer.Draw(size, this.BuildFace());

        /// <summary>Run the key's action from the dev harness; the press debounce does not apply.</summary>
        internal void ExecuteForDebug(String? actionParameter) => this.Execute(actionParameter!);
    }
}
