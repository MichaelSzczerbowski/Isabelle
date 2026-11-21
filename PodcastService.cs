using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace IslandPlayer
{
    public sealed class PodcastShow
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Feed { get; set; } = "";
        public string Art { get; set; } = "";
    }

    public sealed class PodcastEpisode
    {
        public string Key = "";        // guid albo adres pliku
        public string Title = "", Url = "", Art = "", Show = "";
        public DateTime Date;
        public int Duration;           // sekundy (0 = nie wiadomo)
    }

    /// <summary>
    /// Podcasty: wyszukiwanie i popularne (katalog podcastów Apple, bez logowania), subskrypcje,
    /// odcinki z kanału RSS i zapamiętane miejsce słuchania każdego odcinka (podcasts.json).
    /// </summary>
    public static class PodcastService
    {
        private sealed class Store
        {
            public List<PodcastShow> Subs { get; set; } = new List<PodcastShow>();
            public Dictionary<string, double> Positions { get; set; } = new Dictionary<string, double>();
            public HashSet<string> Done { get; set; } = new HashSet<string>();
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        private static string FilePath => Path.Combine(Settings.Folder, "podcasts.json");
        private static Store _store;
        private static readonly object Lock = new object();
        private static readonly Dictionary<string, (DateTime at, List<PodcastEpisode> eps)> FeedCache = new Dictionary<string, (DateTime, List<PodcastEpisode>)>();
        public static event Action Changed;

        static PodcastService()
        {
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/1.6 (podcast player)");
        }

        private static Store Data
        {
            get
            {
                if (_store != null) return _store;
                try { if (File.Exists(FilePath)) _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)); } catch (Exception ex) { Log.Error(ex); }
                return _store ??= new Store();
            }
        }

        private static void Save()
        {
            lock (Lock)
            {
                try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Data)); } catch (Exception ex) { Log.Error(ex); }
            }
        }

        // ================= subskrypcje =================
        public static List<PodcastShow> Subscriptions { get { lock (Lock) return Data.Subs.ToList(); } }

        public static bool IsSubscribed(PodcastShow s)
        {
            lock (Lock) return Data.Subs.Any(x => x.Feed == s.Feed);
        }

        public static void ToggleSubscribe(PodcastShow s)
        {
            lock (Lock)
            {
                var have = Data.Subs.FirstOrDefault(x => x.Feed == s.Feed);
                if (have != null) Data.Subs.Remove(have); else Data.Subs.Insert(0, s);
            }
            Save();
            Changed?.Invoke();
        }

        // ================= miejsce słuchania =================
        public static double Position(string key) { lock (Lock) return Data.Positions.TryGetValue(key ?? "", out var p) ? p : 0; }
        public static bool IsDone(string key) { lock (Lock) return Data.Done.Contains(key ?? ""); }

        private static DateTime _lastSave = DateTime.MinValue;

        /// <summary>Zapamiętuje, gdzie skończyłeś (zapis na dysk najwyżej co 15 s).</summary>
        public static void SetPosition(string key, double sec, double duration)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (Lock)
            {
                if (duration > 60 && sec > duration - 45)
                {
                    // wysłuchany do końca - następnym razem od początku, z ptaszkiem na liście
                    Data.Done.Add(key);
                    Data.Positions.Remove(key);
                }
                else if (sec > 10) Data.Positions[key] = Math.Round(sec);
                if (Data.Positions.Count > 500) Data.Positions.Remove(Data.Positions.Keys.First());
            }
            if ((DateTime.UtcNow - _lastSave).TotalSeconds > 15) { _lastSave = DateTime.UtcNow; Save(); }
        }

        public static void Flush() => Save();

        // ================= katalog =================
        public static async Task<List<PodcastShow>> SearchAsync(string term)
        {
            var list = new List<PodcastShow>();
            string url = "https://itunes.apple.com/search?media=podcast&entity=podcast&limit=25&term=" + Uri.EscapeDataString(term);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            foreach (var it in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                string feed = it.TryGetProperty("feedUrl", out var f) ? f.GetString() : null;
                if (string.IsNullOrEmpty(feed)) continue;
                list.Add(new PodcastShow
                {
                    Id = it.TryGetProperty("collectionId", out var id) ? id.GetInt64() : 0,
                    Title = it.TryGetProperty("collectionName", out var n) ? n.GetString() : "",
                    Author = it.TryGetProperty("artistName", out var a) ? a.GetString() : "",
                    Feed = feed,
                    Art = it.TryGetProperty("artworkUrl600", out var art) ? art.GetString() : it.TryGetProperty("artworkUrl100", out var art2) ? art2.GetString() : "",
                });
            }
            return list;
        }

        private static List<PodcastShow> _top;

        /// <summary>Najpopularniejsze podcasty w Polsce (lista Apple Podcasts).</summary>
        public static async Task<List<PodcastShow>> TopAsync()
        {
            if (_top != null) return _top;
            var ids = new List<string>();
            string country = L.En ? "us" : "pl";
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync("https://rss.applemarketingtools.com/api/v2/" + country + "/podcasts/top/25/podcasts.json")))
                foreach (var it in doc.RootElement.GetProperty("feed").GetProperty("results").EnumerateArray())
                    if (it.TryGetProperty("id", out var id)) ids.Add(id.GetString());
            var list = new List<PodcastShow>();
            if (ids.Count == 0) return list;
            // adresy kanałów jednym zapytaniem
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync("https://itunes.apple.com/lookup?id=" + string.Join(",", ids))))
            {
                var byId = new Dictionary<string, PodcastShow>();
                foreach (var it in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    string feed = it.TryGetProperty("feedUrl", out var f) ? f.GetString() : null;
                    if (string.IsNullOrEmpty(feed)) continue;
                    var s = new PodcastShow
                    {
                        Id = it.GetProperty("collectionId").GetInt64(),
                        Title = it.TryGetProperty("collectionName", out var n) ? n.GetString() : "",
                        Author = it.TryGetProperty("artistName", out var a) ? a.GetString() : "",
                        Feed = feed,
                        Art = it.TryGetProperty("artworkUrl600", out var art) ? art.GetString() : "",
                    };
                    byId[s.Id.ToString()] = s;
                }
                foreach (var id in ids) if (byId.TryGetValue(id, out var s)) list.Add(s);       // kolejność z rankingu
            }
            _top = list;
            return list;
        }

        /// <summary>Odcinki z kanału RSS (najnowsze pierwsze), w pamięci na 20 minut.</summary>
        public static async Task<List<PodcastEpisode>> EpisodesAsync(PodcastShow show, bool refresh = false)
        {
            lock (Lock)
                if (!refresh && FeedCache.TryGetValue(show.Feed, out var c) && (DateTime.UtcNow - c.at).TotalMinutes < 20) return c.eps;
            string xml = await Http.GetStringAsync(show.Feed);
            var doc = XDocument.Parse(xml);
            XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
            var channel = doc.Root?.Element("channel");
            string showArt = channel?.Element(itunes + "image")?.Attribute("href")?.Value ?? show.Art;
            var list = new List<PodcastEpisode>();
            foreach (var item in channel?.Elements("item") ?? Enumerable.Empty<XElement>())
            {
                string url = item.Element("enclosure")?.Attribute("url")?.Value;
                if (string.IsNullOrEmpty(url)) continue;
                var ep = new PodcastEpisode
                {
                    Title = (item.Element("title")?.Value ?? "").Trim(),
                    Url = url,
                    Key = (item.Element("guid")?.Value ?? url).Trim(),
                    Art = item.Element(itunes + "image")?.Attribute("href")?.Value ?? showArt,
                    Show = show.Title,
                    Duration = ParseDuration(item.Element(itunes + "duration")?.Value),
                };
                if (DateTime.TryParse(item.Element("pubDate")?.Value?.Replace(" GMT", " +0000").Replace(" UT", " +0000"), out var d)) ep.Date = d;
                list.Add(ep);
                if (list.Count >= 150) break;
            }
            list = list.OrderByDescending(e => e.Date).ToList();
            lock (Lock) FeedCache[show.Feed] = (DateTime.UtcNow, list);
            return list;
        }

        private static int ParseDuration(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (int.TryParse(s, out var sec)) return sec;
            var parts = s.Split(':');
            int total = 0;
            foreach (var p in parts) { if (!int.TryParse(p, out var v)) return 0; total = total * 60 + v; }
            return total;
        }
    }
}
