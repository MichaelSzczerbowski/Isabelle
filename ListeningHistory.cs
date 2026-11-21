using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IslandPlayer
{
    /// <summary>Jedno odsłuchanie (co najmniej 30 s grania).</summary>
    public sealed class Listen
    {
        public string T { get; set; } = "";     // tytuł
        public string A { get; set; } = "";     // wykonawca
        public string S { get; set; } = "";     // skąd: Spotify / Moja muzyka / Radio / Przeglądarka / nazwa programu
        public long D { get; set; }             // kiedy (UTC, ticks)
        public int Sec { get; set; }            // ile sekund grało
    }

    /// <summary>
    /// Historia słuchania ze wszystkiego, co pokazuje wyspa (Spotify, przeglądarka, Moja muzyka, radio…) -
    /// podstawa Isabelle Wrapped i „Nowości od Twoich artystów”. Tylko na tym komputerze (history.json).
    /// </summary>
    public static class ListeningHistory
    {
        private const int MaxEntries = 40000;
        private static string FilePath => Path.Combine(Settings.Folder, "history.json");
        private static List<Listen> _items;
        private static readonly object Lock = new object();
        private static bool _dirty;
        private static DateTime _lastSave = DateTime.UtcNow, _lastTick = DateTime.UtcNow;

        // obecnie grany utwór
        private static string _key;
        private static double _sec;
        private static Listen _current;
        private static string _t, _a, _s;

        private static List<Listen> Items
        {
            get
            {
                if (_items != null) return _items;
                var list = new List<Listen>();
                try
                {
                    if (File.Exists(FilePath)) list = JsonSerializer.Deserialize<List<Listen>>(File.ReadAllText(FilePath)) ?? new List<Listen>();
                }
                catch (Exception ex) { Log.Error(ex); }
                _items = list;
                return list;
            }
        }

        /// <summary>Wołane kilka razy na sekundę: liczy czas grania obecnego utworu.</summary>
        public static void Tick(MediaSnapshot snap, bool playing)
        {
            var now = DateTime.UtcNow;
            double dt = Math.Min(2, (now - _lastTick).TotalSeconds);
            _lastTick = now;
            if (snap == null || string.IsNullOrWhiteSpace(snap.Title)) return;
            lock (Lock)
            {
                if (snap.Key != _key)
                {
                    _key = snap.Key;
                    _sec = 0;
                    _current = null;
                    _t = snap.Title.Trim();
                    _a = (snap.Artist ?? "").Split(" · ")[0].Trim();       // bez dopisku „fragment 30 s”
                    _s = Source(snap.AppId);
                }
                if (!playing) return;
                _sec += dt;
                if (_current == null && _sec >= 30)
                {
                    _current = new Listen { T = _t, A = _a, S = _s, D = now.Ticks, Sec = (int)_sec };
                    Items.Add(_current);
                    if (Items.Count > MaxEntries) Items.RemoveRange(0, Items.Count - MaxEntries);
                    _dirty = true;
                }
                else if (_current != null && (int)_sec != _current.Sec)
                {
                    _current.Sec = (int)_sec;
                    _dirty = true;
                }
            }
            if (_dirty && (now - _lastSave).TotalSeconds > 90) Save();
        }

        public static void Save()
        {
            lock (Lock)
            {
                if (!_dirty || _items == null) return;
                try
                {
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
                    _dirty = false;
                    _lastSave = DateTime.UtcNow;
                }
                catch (Exception ex) { Log.Error(ex); }
            }
        }

        /// <summary>Odsłuchania od danej chwili (kopia - można liczyć bez blokady).</summary>
        public static List<Listen> Since(DateTime utc)
        {
            long from = utc.Ticks;
            lock (Lock) return Items.Where(x => x.D >= from).ToList();
        }

        /// <summary>Najczęściej słuchani wykonawcy (pierwszy z listy „A, B”).</summary>
        public static List<(string artist, int count)> TopArtists(DateTime sinceUtc, int count)
        {
            return Since(sinceUtc)
                .Where(x => x.S != "Radio" && x.S != "Podcast" && x.A.Length > 0)
                .GroupBy(x => MainArtist(x.A), StringComparer.CurrentCultureIgnoreCase)
                .Select(g => (artist: g.Key, count: g.Count()))
                .OrderByDescending(x => x.count)
                .Take(count)
                .ToList();
        }

        public static string MainArtist(string a)
        {
            if (string.IsNullOrWhiteSpace(a)) return "";
            foreach (var sep in new[] { ", ", " & ", " feat. ", " ft. ", " x " })
            {
                int i = a.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
                if (i > 0) a = a.Substring(0, i);
            }
            // kanały YouTube: „Artysta - Topic”, „ArtystaVEVO”
            if (a.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 8);
            if (a.EndsWith("VEVO", StringComparison.Ordinal) && a.Length > 4) a = a.Substring(0, a.Length - 4);
            return a.Trim();
        }

        /// <summary>Skąd grała muzyka - czytelna nazwa.</summary>
        public static string Source(string appId)
        {
            string a = (appId ?? "").ToLowerInvariant();
            if (a.Contains("isabelle")) return LocalPlayer.CurrentStation != null ? "Radio" : LocalPlayer.CurrentEpisode != null ? "Podcast" : L.T("Moja muzyka", "My music");
            if (a.Contains("spotify")) return "Spotify";
            if (a.Contains("chrome") || a.Contains("msedge") || a.Contains("firefox") || a.Contains("opera") || a.Contains("brave") || a.Contains("vivaldi"))
                return L.T("Przeglądarka", "Browser");
            if (a.Contains("tidal")) return "Tidal";
            if (a.Contains("applemusic") || a.Contains("itunes")) return "Apple Music";
            if (a.Contains("deezer")) return "Deezer";
            if (a.Contains("zune") || a.Contains("media")) return L.T("Odtwarzacz Windows", "Windows player");
            string name = Path.GetFileNameWithoutExtension(a.Split('!')[0]);
            return name.Length == 0 ? "?" : char.ToUpper(name[0]) + name.Substring(1);
        }
    }
}
