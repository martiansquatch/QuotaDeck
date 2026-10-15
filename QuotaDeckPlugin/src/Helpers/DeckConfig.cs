namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using System.IO;
    using System.Runtime.InteropServices;
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// QuotaDeck settings, kept in a per-user data directory:
    ///   macOS:   $HOME/Library/Application Support/QuotaDeck
    ///   Windows: %LOCALAPPDATA%\QuotaDeck
    ///   other:   $XDG_DATA_HOME/QuotaDeck, or ~/.local/share/QuotaDeck
    /// The file is config.json; comments and trailing commas are tolerated, and it is rewritten on load
    /// so keys added by newer versions appear with their defaults.
    /// </summary>
    public sealed class DeckConfig
    {
        /// <summary>
        /// Write every rendered key face to snapshots/&lt;key&gt;_&lt;size&gt;.png and enable the trigger.txt
        /// dev harness (see the project README).
        /// </summary>
        [JsonPropertyName("debugSnapshots")]
        public Boolean DebugSnapshots { get; set; }

        /// <summary>Where config.json, trigger.txt and the snapshots folder live.</summary>
        public static String DataDirectory { get; } = ResolveDataDirectory();

        public static String ConfigFile => Path.Combine(DataDirectory, "config.json");

        /// <summary>Folder for debug face renders (only written while <see cref="DebugSnapshots"/> is on).</summary>
        public static String SnapshotDirectory => Path.Combine(DataDirectory, "snapshots");

        public static DeckConfig Current { get; private set; } = new DeckConfig();

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        private static String ResolveDataDirectory()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                var root = String.IsNullOrEmpty(localAppData) ? home : localAppData;
                return Path.Combine(root, "QuotaDeck");
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return Path.Combine(home, "Library", "Application Support", "QuotaDeck");
            }

            var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var dataRoot = String.IsNullOrEmpty(xdgDataHome) ? Path.Combine(home, ".local", "share") : xdgDataHome;
            return Path.Combine(dataRoot, "QuotaDeck");
        }

        public static void Load()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory);
                if (File.Exists(ConfigFile))
                {
                    Current = JsonSerializer.Deserialize<DeckConfig>(File.ReadAllText(ConfigFile), JsonOptions) ?? new DeckConfig();
                    // Persist any keys added since the file was written (e.g. new defaults in later phases).
                    File.WriteAllText(ConfigFile, JsonSerializer.Serialize(Current, JsonOptions));
                }
                else
                {
                    Current = new DeckConfig();
                    File.WriteAllText(ConfigFile, JsonSerializer.Serialize(Current, JsonOptions));
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "config load failed, using defaults");
                Current = new DeckConfig();
            }
        }
    }
}
