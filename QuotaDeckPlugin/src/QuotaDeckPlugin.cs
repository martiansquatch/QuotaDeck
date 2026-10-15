namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// QuotaDeck - AI-provider quota faces for the Logitech MX Creative Keypad (Logi Actions SDK).
    ///
    /// Phase 1: the "hello deck" gate. The plugin owns the shared 1 s ticker that live faces will subscribe to
    /// and, when <c>debugSnapshots</c> is enabled, the trigger.txt dev harness (see the project README).
    /// </summary>
    public class QuotaDeckPlugin : Plugin
    {
        // Universal plugin: no application, but the API-only flags still apply.
        public override Boolean UsesApplicationApiOnly => true;
        public override Boolean HasNoApplication => true;

        /// <summary>The single live plugin instance (actions walk up to it for the ticker and the snapshot folder).</summary>
        public static QuotaDeckPlugin? Instance { get; private set; }

        /// <summary>Raised once per second from a background thread. Handlers must be cheap.</summary>
        public event Action<Int32>? Tick;

        private Timer? _ticker;
        private Int32 _tick;
        private FileSystemWatcher? _trigger;

        public QuotaDeckPlugin()
        {
            PluginLog.Init(this.Log);
            PluginResources.Init(this.Assembly);
            Instance = this;
        }

        public override void Load()
        {
            DeckConfig.Load();
            PluginLog.Info($"QuotaDeck starting. dataDir={DeckConfig.DataDirectory} debugSnapshots={DeckConfig.Current.DebugSnapshots}");

            this._ticker = new Timer(_ => this.OnTick(), null, 1000, 1000);

            if (DeckConfig.Current.DebugSnapshots)
            {
                this.StartTriggerWatcher();
                // Pre-render every registered face so snapshots exist even before keys are assigned on the device.
                _ = Task.Delay(1500).ContinueWith(_ => RenderAllSnapshots("startup"));
            }

            PluginLog.Info("QuotaDeck loaded.");
        }

        public override void Unload()
        {
            Panel.Stop(refresh: false);
            this._ticker?.Dispose();
            this._ticker = null;
            this._trigger?.Dispose();
            this._trigger = null;
            PluginLog.Info("QuotaDeck unloaded.");
        }

        private void OnTick()
        {
            var handlers = this.Tick;
            if (handlers == null)
            {
                return;
            }

            this._tick++;
            foreach (Action<Int32> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this._tick);
                }
                catch (Exception ex)
                {
                    PluginLog.Warning(ex, "tick handler failed");
                }
            }
        }

        /// <summary>Renders every registered key at both device and preview sizes (debug snapshots).</summary>
        internal static void RenderAllSnapshots(String reason)
        {
            try
            {
                QuotaKey[] all;
                lock (QuotaKey.All)
                {
                    all = QuotaKey.All.ToArray();
                }
                foreach (var key in all)
                {
                    key.RenderForSnapshot(PluginImageSize.Width116);
                    key.RenderForSnapshot(PluginImageSize.Width90);
                }
                PluginLog.Info($"rendered {all.Length} face(s) ({reason}) into {DeckConfig.SnapshotDirectory}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "snapshot render failed");
            }
        }

        // ---- dev harness: write a line into <data>/trigger.txt to poke the plugin without touching the device.
        //      "snapshot" re-renders every face, "pulse" starts the panel pulse, any other text names a
        //      QuotaKey subclass to Execute. See QuotaDeckPlugin/README.md. ----

        private void StartTriggerWatcher()
        {
            try
            {
                Directory.CreateDirectory(DeckConfig.DataDirectory);
                this._trigger = new FileSystemWatcher(DeckConfig.DataDirectory, "trigger.txt")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler handler = (_, _) => this.HandleTrigger();
                this._trigger.Created += handler;
                this._trigger.Changed += handler;
                this._trigger.Renamed += (_, _) => this.HandleTrigger();
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "trigger watcher failed");
            }
        }

        private void HandleTrigger()
        {
            try
            {
                Thread.Sleep(150); // let the writer finish
                var path = Path.Combine(DeckConfig.DataDirectory, "trigger.txt");
                if (!File.Exists(path))
                {
                    return;
                }
                var text = File.ReadAllText(path).Trim();
                File.Delete(path);
                PluginLog.Info($"trigger: '{text}'");

                if (text.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    RenderAllSnapshots("trigger");
                    return;
                }
                if (text.Equals("pulse", StringComparison.OrdinalIgnoreCase))
                {
                    Panel.Play(new PulseScene(FaceRenderer.Alert));
                    return;
                }

                QuotaKey[] all;
                lock (QuotaKey.All)
                {
                    all = QuotaKey.All.ToArray();
                }
                var key = Array.Find(all, k => k.GetType().Name.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (key == null)
                {
                    PluginLog.Warning($"trigger: no action named '{text}'");
                    return;
                }
                key.ExecuteForDebug(null);
                Thread.Sleep(400);
                key.RenderForSnapshot(PluginImageSize.Width116);
                key.RenderForSnapshot(PluginImageSize.Width90);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "trigger failed");
            }
        }
    }
}
