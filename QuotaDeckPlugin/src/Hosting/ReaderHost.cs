namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using QuotaDeck.Core.Alerts;
    using QuotaDeck.Core.Model;
    using QuotaDeck.Core.Policy;
    using QuotaDeck.Core.Readers;
    using QuotaDeck.Core.Readers.Services;
    using QuotaDeck.Core.Settings;
    using QuotaDeck.Readers.Shared;

    /// <summary>
    /// The plugin's reader host: discovers provider modules, owns one reader per provider, fetches on
    /// the schedule the state engine decides, keeps the latest snapshot per id, redraws that
    /// provider's keys, and turns watcher events into notifications and the panel pulse.
    ///
    /// Readers are the store's counterpart in Codenotch, and this class is the store side of that
    /// split: <c>UsageStore</c>'s tick, publish and alert plumbing, reduced to what the plugin needs.
    /// With no modules the host is an empty shell that still starts and ticks — the seven keys exist
    /// and say "not installed", and fixture mode still drives them.
    /// </summary>
    public sealed class ReaderHost : IDisposable
    {
        /// <summary>At most one panel pulse per this window, whatever asked for it.</summary>
        private static readonly TimeSpan PulseSpacing = TimeSpan.FromSeconds(10);

        private readonly QuotaDeckPlugin _plugin;
        private readonly object _gate = new();
        private readonly Dictionary<string, IUsageReader> _readers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ProviderSnapshot> _latest = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IProviderModule> _modules = new(StringComparer.Ordinal);
        private readonly HashSet<string> _fixtures = new(StringComparer.Ordinal);
        private readonly RefreshCoordinator _schedule = new();
        private readonly ThresholdNotifier _thresholds;
        private readonly UsageLimitWatcher _limits;
        private readonly UsageResetWatcher _resets;
        private readonly MacNotifier _notifier;
        private DateTimeOffset _lastPulse = DateTimeOffset.MinValue;
        private Boolean _stopped;

        public ReaderHost(QuotaDeckPlugin plugin)
        {
            ArgumentNullException.ThrowIfNull(plugin);
            this._plugin = plugin;

            var process = new ProcessRunner();
            this._notifier = new MacNotifier(process);
            this._thresholds = new ThresholdNotifier(deliver: this.OnThreshold);
            this._limits = new UsageLimitWatcher(deliver: this.OnLimitReached);
            this._resets = new UsageResetWatcher(deliver: this.OnReset);

            this.BuildReaders(process);
        }

        /// <summary>Subscribe to the tick and ask for the launch refresh, like the store's first pass.</summary>
        public void Start()
        {
            IReadOnlyList<String> due;
            lock (this._gate)
            {
                due = this._schedule.Due([.. this._latest.Values], TimeProvider.System.GetUtcNow());
            }

            this._plugin.Tick += this.OnTick;
            PluginLog.Info($"ReaderHost: starting, {due.Count} launch fetch(es)");
            foreach (var id in due)
            {
                this.FetchInBackground(id, Freshness.Standard);
            }
        }

        /// <summary>The last reading for a provider, or null before anything has been fetched.</summary>
        public ProviderSnapshot? Latest(String providerId)
        {
            lock (this._gate)
            {
                return this._latest.TryGetValue(providerId, out var snapshot) ? snapshot : null;
            }
        }

        /// <summary>The module that owns a provider, when one is compiled in.</summary>
        public IProviderModule? Module(String providerId)
        {
            lock (this._gate)
            {
                return this._modules.TryGetValue(providerId, out var module) ? module : null;
            }
        }

        /// <summary>True when a fixture stands in for this provider.</summary>
        public Boolean HasFixture(String providerId)
        {
            lock (this._gate)
            {
                return this._fixtures.Contains(providerId);
            }
        }

        /// <summary>Someone looked: fetch from source if the 15 s look spacing allows.</summary>
        public void Look(String providerId)
        {
            lock (this._gate)
            {
                if (!this._readers.ContainsKey(providerId))
                {
                    return;
                }
            }

            if (this._schedule.Look(providerId, TimeProvider.System.GetUtcNow()))
            {
                PluginLog.Verbose($"{providerId}: look, fetching from source");
                this.FetchInBackground(providerId, Freshness.FromSource);
            }
        }

        /// <summary>Fetch now, from source — every reader, or one by id. Used by the dev harness.</summary>
        public void FetchNow(String? providerId)
        {
            List<String> ids;
            lock (this._gate)
            {
                ids = providerId is { Length: > 0 }
                    ? this._readers.ContainsKey(providerId) ? [providerId] : []
                    : [.. this._readers.Keys];
            }

            if (providerId is { Length: > 0 } && ids.Count == 0)
            {
                PluginLog.Warning($"fetch: no reader named '{providerId}'");
                return;
            }

            PluginLog.Info($"fetch: {(providerId is { Length: > 0 } ? providerId : $"{ids.Count} provider(s)")}");
            foreach (var id in ids)
            {
                this.FetchInBackground(id, Freshness.FromSource);
            }
        }

        public void Dispose()
        {
            this._stopped = true;
            this._plugin.Tick -= this.OnTick;

            List<IUsageReader> readers;
            lock (this._gate)
            {
                readers = [.. this._readers.Values];
            }

            foreach (var reader in readers)
            {
                (reader as IDisposable)?.Dispose();
            }
        }

        private void BuildReaders(IProcessRunner process)
        {
            var settings = DeckConfig.Current;
            var fixturesDirectory = Path.Combine(DeckConfig.DataDirectory, "fixtures");
            var fixtureMode = settings.DebugSnapshots && Directory.Exists(fixturesDirectory);
            var modules = ProviderModules.Discover(typeof(HttpClientTransport).Assembly, settings.Order);

            foreach (var module in modules)
            {
                var config = settings.ForProvider(module.ProviderId);
                if (!config.Enabled)
                {
                    continue;
                }

                this._modules[module.ProviderId] = module;
                var fixture = Path.Combine(fixturesDirectory, module.ProviderId + ".json");
                if (fixtureMode && File.Exists(fixture))
                {
                    this._fixtures.Add(module.ProviderId);
                    this.AddReader(Fixture(module.ProviderId, module.DisplayName, config, fixture));
                }
                else
                {
                    this.AddReader(module.CreateReader(new ReaderContext
                    {
                        Config = config,
                        StateStore = JsonFileReaderStateStore.ForPath(Path.Combine(DeckConfig.DataDirectory, "readings.json")),
                        TimeProvider = TimeProvider.System,
                        DataDirectory = DeckConfig.DataDirectory,
                        Http = new HttpClientTransport(),
                        Process = process,
                    }));
                }
            }

            if (fixtureMode)
            {
                // A fixture can stand in for a provider no module claims yet, so its key can be
                // rendered before its reader lands.
                foreach (var file in Directory.EnumerateFiles(fixturesDirectory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var id = Path.GetFileNameWithoutExtension(file);
                    if (this._readers.ContainsKey(id))
                    {
                        continue;
                    }

                    var config = settings.ForProvider(id);
                    if (!config.Enabled)
                    {
                        continue;
                    }

                    this._fixtures.Add(id);
                    this.AddReader(Fixture(id, id, config, file));
                }
            }

            PluginLog.Info(
                $"ReaderHost: discovered {modules.Count} modules, {this._readers.Count} readers"
                + (fixtureMode ? $", fixture mode ({this._fixtures.Count} fixtures)" : ""));
        }

        /// <summary>
        /// A fixture reader keeps its memory in RAM on purpose: a synthetic reading must never
        /// overwrite the real archive in readings.json.
        /// </summary>
        private static FixtureReader Fixture(String id, String displayName, ReaderConfig config, String path) =>
            new(id, displayName, config, new InMemoryReaderStateStore(), TimeProvider.System, path);

        private void AddReader(IUsageReader reader)
        {
            this._readers[reader.Id] = reader;

            // What is on screen before the first fetch lands: nothing truthful is known yet, so the
            // placeholder says so. The launch fetch replaces it within a tick.
            this._latest[reader.Id] = Degradation.Placeholder(
                reader.Id, reader.DisplayName, ProviderStatus.Stale(DateTimeOffset.MinValue));
        }

        private void OnTick(Int32 tick)
        {
            if (this._stopped)
            {
                return;
            }

            IReadOnlyList<String> due;
            lock (this._gate)
            {
                due = this._schedule.Due([.. this._latest.Values], TimeProvider.System.GetUtcNow());
            }

            foreach (var id in due)
            {
                this.FetchInBackground(id, Freshness.Standard);
            }
        }

        private void FetchInBackground(String providerId, Freshness freshness) =>
            _ = Task.Run(() => this.FetchAsync(providerId, freshness));

        private async Task FetchAsync(String providerId, Freshness freshness)
        {
            IUsageReader? reader;
            lock (this._gate)
            {
                this._readers.TryGetValue(providerId, out reader);
            }

            if (reader is null)
            {
                return;
            }

            var snapshot = await reader.FetchAsync(freshness).ConfigureAwait(false);
            if (this._stopped)
            {
                return;
            }

            lock (this._gate)
            {
                this._latest[providerId] = snapshot;
            }

            this.Redraw(providerId);
            this.FeedWatchers();
        }

        private void Redraw(String providerId)
        {
            QuotaKey[] keys;
            lock (QuotaKey.All)
            {
                keys = [.. QuotaKey.All];
            }

            foreach (var key in keys)
            {
                if (key is ProviderKey providerKey
                    && String.Equals(providerKey.ProviderId, providerId, StringComparison.Ordinal))
                {
                    providerKey.RefreshFace();
                }
            }
        }

        private void FeedWatchers()
        {
            List<ProviderSnapshot> snapshots;
            lock (this._gate)
            {
                snapshots = [.. this._latest.Values];
            }

            this._thresholds.Observe(snapshots);
            this._limits.Observe(snapshots);
            this._resets.Observe(snapshots);
        }

        private void OnThreshold(ThresholdAlert alert)
        {
            var message = AlertTexts.ForThreshold(alert);
            _ = this._notifier.NotifyAsync(message.Title, message.Body);
            if (alert.Threshold >= 100)
            {
                this.Pulse();
            }
        }

        private void OnLimitReached(UsageAlertEvent usageEvent)
        {
            var message = AlertTexts.ForUsage(usageEvent);
            _ = this._notifier.NotifyAsync(message.Title, message.Body);
            this.Pulse();
        }

        private void OnReset(UsageAlertEvent usageEvent)
        {
            var message = AlertTexts.ForUsage(usageEvent);
            _ = this._notifier.NotifyAsync(message.Title, message.Body);
        }

        private void Pulse()
        {
            var now = TimeProvider.System.GetUtcNow();
            lock (this._gate)
            {
                if (now - this._lastPulse < PulseSpacing)
                {
                    return;
                }

                this._lastPulse = now;
            }

            PluginLog.Info("alert: panel pulse");
            Panel.Play(new PulseScene(FaceRenderer.Alert));
        }
    }
}
