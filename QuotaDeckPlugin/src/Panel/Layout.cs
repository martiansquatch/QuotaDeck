namespace Loupedeck.QuotaDeckPlugin
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Runtime.InteropServices;
    using System.Text;
    using System.Text.Json;

    /// <summary>
    /// Which key sits in which window of the 3x3 grid.
    ///
    /// Read from the Logi profile for the keypad (control ids 0..8 are row-major). Re-read at most every
    /// 30 s and only when some ProfileInfo.json's mtime changed. Until a human assigns keys in
    /// Logi Options+, the fallback places SampleFaceCommand in the centre cell.
    /// (Profile-walking approach ported from neon-deck's Layout.cs, MIT.)
    /// </summary>
    public static class Layout
    {
        private const String ActionPrefix = "$QuotaDeck___Loupedeck.QuotaDeckPlugin.";

        private static Dictionary<String, Int32> _cells = Default();
        private static String? _signature;
        private static DateTime _checked = DateTime.MinValue;

        private static Dictionary<String, Int32> Default() => new()
        {
            ["SampleFaceCommand"] = 4,
        };

        /// <summary>Grid cell 0..8 (row-major) of a key, or -1 when it is not on the grid.</summary>
        public static Int32 CellOf(QuotaKey key)
        {
            Refresh();
            return _cells.TryGetValue(key.GetType().Name, out var cell) ? cell : -1;
        }

        private static String ProfilesRoot
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    return Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Logi", "LogiPluginService", "Applications");
                }
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Library", "Application Support", "Logi", "LogiPluginService", "Applications");
            }
        }

        private static void Refresh()
        {
            if ((DateTime.UtcNow - _checked).TotalSeconds < 30)
            {
                return;
            }
            _checked = DateTime.UtcNow;
            try
            {
                if (!Directory.Exists(ProfilesRoot))
                {
                    return;
                }
                var files = Directory.EnumerateFiles(ProfilesRoot, "ProfileInfo.json", SearchOption.AllDirectories).ToList();
                var sig = new StringBuilder();
                foreach (var f in files)
                {
                    sig.Append(f).Append('|').Append(File.GetLastWriteTimeUtc(f).Ticks).Append(';');
                }
                var signature = sig.ToString();
                if (signature == _signature)
                {
                    return;
                }
                _signature = signature;

                Dictionary<String, Int32>? best = null;
                foreach (var f in files)
                {
                    var page = BestPage(f);
                    if (page != null && (best == null || page.Count > best.Count))
                    {
                        best = page;
                    }
                }
                if (best != null && best.Count > 0)
                {
                    _cells = best;
                    PluginLog.Info($"panel: layout from profile - {String.Join(" ", best.OrderBy(kv => kv.Value).Select(kv => $"{kv.Value}:{kv.Key}"))}");
                }
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "panel: layout read failed");
            }
        }

        /// <summary>The page of this profile with the most QuotaDeck keys, as class name -> cell.</summary>
        private static Dictionary<String, Int32>? BestPage(String file)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            Dictionary<String, Int32>? best = null;
            if (!doc.RootElement.TryGetProperty("layout", out var layout) || !layout.TryGetProperty("layoutModes", out var modes))
            {
                return null;
            }
            foreach (var mode in modes.EnumerateArray())
            {
                if (!mode.TryGetProperty("workspaces", out var workspaces))
                {
                    continue;
                }
                foreach (var ws in workspaces.EnumerateArray())
                {
                    if (!ws.TryGetProperty("pressPages", out var pages))
                    {
                        continue;
                    }
                    foreach (var page in pages.EnumerateArray())
                    {
                        if (!page.TryGetProperty("controls", out var controls))
                        {
                            continue;
                        }
                        var map = new Dictionary<String, Int32>();
                        foreach (var control in controls.EnumerateArray())
                        {
                            if (!control.TryGetProperty("controlId", out var idElement) || idElement.ValueKind != JsonValueKind.Number)
                            {
                                continue;
                            }
                            var id = idElement.GetInt32();
                            var action = control.TryGetProperty("pressAction", out var pressAction) && pressAction.ValueKind == JsonValueKind.String
                                ? pressAction.GetString()
                                : null;
                            if (id < 0 || id > 8 || action == null || !action.StartsWith(ActionPrefix, StringComparison.Ordinal))
                            {
                                continue;
                            }
                            var className = action.Substring(ActionPrefix.Length);
                            if (!map.ContainsKey(className))
                            {
                                map[className] = id;
                            }
                        }
                        if (map.Count > 0 && (best == null || map.Count > best.Count))
                        {
                            best = map;
                        }
                    }
                }
            }
            return best;
        }
    }
}
