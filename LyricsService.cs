using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class LyricLine
    {
        public double Time;
        public string Text;
        /// <summary>Czas linijki oszacowany (tekst bez synchronizacji) - przewija się w przybliżeniu.</summary>
        public bool Estimated;
        public bool Instrumental =>
            string.IsNullOrWhiteSpace((Text ?? "").Replace("♪", "").Replace("♫", "").Replace("🎵", "").Replace("🎶", "").Replace("…", ""));
    }

    /// <summary>Zsynchronizowany tekst piosenek z darmowego serwisu LRCLIB.</summary>
    public static class LyricsService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly Dictionary<string, List<LyricLine>> Cache = new Dictionary<string, List<LyricLine>>();
        private static readonly Regex Stamp = new Regex(@"\[(\d+):(\d+(?:\.\d+)?)\]", RegexOptions.Compiled);

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/1.1 (+https://github.com)");
            return c;
        }

        /// <summary>Zwraca linijki (pusta lista = brak tekstu). Wynik jest zapamiętywany.</summary>
        public static async Task<List<LyricLine>> GetAsync(string key, string title, string artist, double duration)
        {
            lock (Cache)
                if (Cache.TryGetValue(key, out var cached)) return cached;

            var result = new List<LyricLine>();
            try
            {
                var (synced, plain) = await FetchRawAsync(title, artist, duration);
                if (!string.IsNullOrEmpty(synced)) result = Parse(synced);
                else if (!string.IsNullOrEmpty(plain)) result = Estimate(plain, duration); // awaryjnie: tekst bez synchronizacji
            }
            catch (Exception ex)
            {
                Log.Info("tekst piosenki: " + ex.Message);
            }

            lock (Cache)
            {
                if (Cache.Count > 200) Cache.Clear();
                Cache[key] = result;
            }
            return result;
        }

        /// <summary>Tekst z pliku zapisanego w bibliotece (.lrc z czasami albo zwykły tekst).</summary>
        public static List<LyricLine> FromFile(string path, double duration)
        {
            try
            {
                string text = System.IO.File.ReadAllText(path);
                var lines = Parse(text);
                return lines.Count > 1 ? lines : Estimate(text, duration);
            }
            catch { return new List<LyricLine>(); }
        }

        /// <summary>Surowy tekst z LRCLIB: (z czasami, zwykły). Oba null = nie ma.</summary>
        public static async Task<(string synced, string plain)> FetchRawAsync(string title, string artist, double duration)
        {
            {
                string artist1 = CleanArtist(artist);
                string title1 = CleanTitle(title);
                string synced = null, plain = null;

                // 1) dokładne dopasowanie (tytuł + wykonawca + długość)
                var exact = await TryGet("https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                                         "&artist_name=" + Uri.EscapeDataString(artist1) +
                                         (duration > 0 ? "&duration=" + ((int)duration).ToString(CultureInfo.InvariantCulture) : ""));
                if (exact != null) (synced, plain) = Pick(exact.Value);

                // 2) wyszukiwanie: oczyszczony tytuł + wykonawca, potem wolne zapytanie "tytuł wykonawca"
                if (synced == null)
                {
                    var r = await Search("https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title1) +
                                         "&artist_name=" + Uri.EscapeDataString(artist1), duration);
                    synced = r.synced;
                    plain ??= r.plain;
                }
                if (synced == null)
                {
                    var r = await Search("https://lrclib.net/api/search?q=" + Uri.EscapeDataString(title1 + " " + artist1), duration);
                    synced = r.synced;
                    plain ??= r.plain;
                }

                return (synced, plain);
            }
        }

        private static async Task<JsonElement?> TryGet(string url)
        {
            try
            {
                using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url)))
                    return doc.RootElement.Clone();
            }
            catch
            {
                return null;
            }
        }

        private static (string synced, string plain) Pick(JsonElement item)
        {
            string synced = null, plain = null;
            if (item.ValueKind != JsonValueKind.Object) return (null, null);
            if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString()))
                synced = s.GetString();
            if (item.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString()))
                plain = p.GetString();
            return (synced, plain);
        }

        /// <summary>Z wyników wybiera ten z tekstem zsynchronizowanym i długością najbliższą utworowi.</summary>
        private static async Task<(string synced, string plain)> Search(string url, double duration)
        {
            var arr = await TryGet(url);
            if (arr == null || arr.Value.ValueKind != JsonValueKind.Array) return (null, null);
            JsonElement? best = null;
            double bestScore = double.MaxValue;
            foreach (var item in arr.Value.EnumerateArray())
            {
                bool hasSynced = item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(s.GetString());
                double d = item.TryGetProperty("duration", out var du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;
                double diff = duration > 0 && d > 0 ? Math.Abs(d - duration) : 5;
                if (duration > 0 && d > 0 && diff > 15) continue; // inna wersja utworu
                double score = diff + (hasSynced ? 0 : 1000);
                if (score < bestScore) { bestScore = score; best = item; }
            }
            return best != null ? Pick(best.Value) : (null, null);
        }

        private static string CleanTitle(string t)
        {
            t = Regex.Replace(t, @"\s*[\(\[].*?[\)\]]", "");                       // (Remastered), [Official Video]
            t = Regex.Replace(t, @"\s+-\s+.*$", "");                                  // " - Radio Edit"
            t = Regex.Replace(t, @"\s+(feat\.?|ft\.?|featuring)\s+.*$", "", RegexOptions.IgnoreCase);
            return t.Trim();
        }

        private static string CleanArtist(string a)
        {
            a = Regex.Split(a ?? "", @",|&| x | X |\s+(feat\.?|ft\.?|featuring)\s+", RegexOptions.IgnoreCase)[0];
            return a.Trim();
        }

        /// <summary>Tekst bez czasów: linijki rozłożone równo na długość utworu (przewijanie w przybliżeniu).</summary>
        private static List<LyricLine> Estimate(string plain, double duration)
        {
            var lines = new List<string>();
            foreach (string raw in plain.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length > 0) lines.Add(l);
            }
            var list = new List<LyricLine>();
            if (lines.Count == 0) return list;
            double start = 8, end = duration > 30 ? duration - 12 : Math.Max(60, lines.Count * 3.5);
            double step = Math.Max(1.5, (end - start) / lines.Count);
            list.Add(new LyricLine { Time = 0, Text = "", Estimated = true });
            for (int i = 0; i < lines.Count; i++)
                list.Add(new LyricLine { Time = start + i * step, Text = lines[i], Estimated = true });
            return list;
        }

        private static List<LyricLine> Parse(string lrc)
        {
            var list = new List<LyricLine>();
            foreach (string raw in lrc.Split('\n'))
            {
                var stamps = Stamp.Matches(raw);
                if (stamps.Count == 0) continue;
                string text = Regex.Replace(raw, @"\[[^\]]*\]", "").Trim();
                foreach (Match m in stamps)
                {
                    double t = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
                               double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    list.Add(new LyricLine { Time = t, Text = text });
                }
            }
            list = list.OrderBy(l => l.Time).ToList();
            // przed pierwszą linijką: "przerwa" (animowane słupki)
            if (list.Count > 0 && list[0].Time > 1)
                list.Insert(0, new LyricLine { Time = 0, Text = "" });
            return list;
        }
    }
}
