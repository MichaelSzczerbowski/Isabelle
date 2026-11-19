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
                string artist1 = artist.Split(',', '&')[0].Trim();
                string synced = null;
                try
                {
                    string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) +
                                 "&artist_name=" + Uri.EscapeDataString(artist1) +
                                 (duration > 0 ? "&duration=" + ((int)duration).ToString(CultureInfo.InvariantCulture) : "");
                    using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url)))
                        if (doc.RootElement.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                            synced = s.GetString();
                }
                catch { }

                if (string.IsNullOrEmpty(synced))
                {
                    string url = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(CleanTitle(title)) +
                                 "&artist_name=" + Uri.EscapeDataString(artist1);
                    using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url)))
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                            {
                                synced = s.GetString();
                                break;
                            }
                        }
                    }
                }
                if (!string.IsNullOrEmpty(synced)) result = Parse(synced);
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

        private static string CleanTitle(string t)
        {
            t = Regex.Replace(t, @"\s*[\(\[].*?[\)\]]", "");
            t = Regex.Replace(t, @"\s+-\s+.*$", "");
            return t.Trim();
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
