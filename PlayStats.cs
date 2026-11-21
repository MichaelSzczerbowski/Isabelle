using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace IslandPlayer
{
    public sealed class PlayStat
    {
        public int Plays { get; set; }
        /// <summary>Kiedy ostatnio grany (UTC, ticks); 0 = nigdy.</summary>
        public long Last { get; set; }
    }

    /// <summary>Ile razy i kiedy grał każdy utwór z Mojej muzyki (stats.json) - podstawa inteligentnych playlist.</summary>
    public static class PlayStats
    {
        private static string FilePath => Path.Combine(Settings.Folder, "stats.json");
        private static Dictionary<string, PlayStat> _data;
        private static readonly object Lock = new object();
        public static event Action Changed;

        private static Dictionary<string, PlayStat> Data
        {
            get
            {
                if (_data != null) return _data;
                var d = new Dictionary<string, PlayStat>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, PlayStat>>(File.ReadAllText(FilePath));
                        if (loaded != null) foreach (var kv in loaded) if (kv.Value != null) d[kv.Key] = kv.Value;
                    }
                }
                catch (Exception ex) { Log.Error(ex); }
                _data = d;
                return d;
            }
        }

        public static PlayStat Get(string path)
        {
            lock (Lock) return path != null && Data.TryGetValue(path, out var s) ? s : null;
        }

        public static int Plays(string path) => Get(path)?.Plays ?? 0;
        public static long Last(string path) => Get(path)?.Last ?? 0;

        public static void Count(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            lock (Lock)
            {
                if (!Data.TryGetValue(path, out var s)) Data[path] = s = new PlayStat();
                s.Plays++;
                s.Last = DateTime.UtcNow.Ticks;
                try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Data)); }
                catch (Exception ex) { Log.Error(ex); }
            }
            try { Changed?.Invoke(); } catch (Exception ex) { Log.Error(ex); }
        }
    }
}
