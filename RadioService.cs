using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class RadioStation
    {
        public string Uuid { get; set; } = "";
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        public string Favicon { get; set; } = "";
        public string Homepage { get; set; } = "";
        public string Country { get; set; } = "";
        public string CountryCode { get; set; } = "";
        public string Tags { get; set; } = "";
        public int Bitrate { get; set; }
        public string Codec { get; set; } = "";
        public int Votes { get; set; }
        public int Clicks { get; set; }
        public int Trend { get; set; }
        public bool LastCheckOk { get; set; } = true;
        public bool Hls { get; set; }

        [JsonIgnore]
        public bool Https => Url.StartsWith("https:", StringComparison.OrdinalIgnoreCase);

        /// <summary>Szacowana jakość dźwięku 0-5 (0 = nieznana). AAC/Opus brzmią lepiej niż MP3 przy tym samym bitrate.</summary>
        [JsonIgnore]
        public int SoundQuality
        {
            get
            {
                string c = Codec.ToUpperInvariant();
                if (c.Contains("FLAC")) return 5;
                if (Bitrate <= 0) return 0;
                double eff = Bitrate * (c.Contains("AAC") || c.Contains("OPUS") || c.Contains("OGG") ? 1.5 : 1.0);
                return eff >= 250 ? 5 : eff >= 160 ? 4 : eff >= 110 ? 3 : eff >= 64 ? 2 : 1;
            }
        }

        [JsonIgnore]
        public string SoundLabel => SoundQuality switch
        {
            5 => L.T("Doskonała", "Excellent"),
            4 => L.T("Bardzo dobra", "Very good"),
            3 => L.T("Dobra", "Good"),
            2 => L.T("Średnia", "Average"),
            1 => L.T("Słaba", "Poor"),
            _ => L.T("Nieznana", "Unknown"),
        };

        [JsonIgnore]
        public string Format => (Codec.Length > 0 ? Codec : "?") + (Bitrate > 0 ? " " + Bitrate + " kbps" : "");

        /// <summary>
        /// Szacowana niezawodność sygnału 1-4 („kreski zasięgu”): ostatni test bazy, szyfrowane połączenie,
        /// popularność (popularne stacje mają mocne serwery).
        /// </summary>
        [JsonIgnore]
        public int Signal
        {
            get
            {
                if (!LastCheckOk) return 1;
                int s = 2;
                if (Https) s++;
                if (Clicks > 300 || Votes > 300) s++;
                return Math.Min(4, s);
            }
        }

        [JsonIgnore]
        public string SignalLabel => Signal switch
        {
            4 => L.T("Bardzo stabilny", "Very stable"),
            3 => L.T("Stabilny", "Stable"),
            2 => L.T("Może przerywać", "May drop out"),
            _ => L.T("Niestabilny", "Unstable"),
        };

        [JsonIgnore]
        public string Info => string.Join(" · ", new[] { Country, Tags }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>
    /// stacje radiowe z darmowej, otwartej bazy Radio Browser (radio-browser.info) - bez klucza.
    /// </summary>
    public static class RadioService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly string[] Servers = { "de1", "de2", "fi1", "nl1" };

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/1.5 (+https://github.com/MichaelSzczerbowski/Isabelle)");
            return c;
        }

        /// <summary>Kategorie w menu radia: klucz, nazwa, zapytanie do bazy (null = lista lokalna).</summary>
        public static readonly (string key, string pl, string en, string query)[] Categories =
        {
            ("fav", "Ulubione", "Favorites", null),
            ("recent", "Ostatnio słuchane", "Recently played", null),
            ("pl-top", "Polska – najpopularniejsze", "Poland – most popular", "countrycode=PL&order=clickcount"),
            ("pl-votes", "Polska – najlepiej oceniane", "Poland – top rated", "countrycode=PL&order=votes"),
            ("pl-trend", "Polska – zyskujące", "Poland – trending", "countrycode=PL&order=clicktrend"),
            ("pl-hq", "Najlepszy dźwięk (PL)", "Best sound (PL)", "countrycode=PL&order=bitrate&bitrateMin=128"),
            ("world-top", "Świat – najpopularniejsze", "World – most popular", "order=clickcount"),
            ("world-votes", "Świat – najlepiej oceniane", "World – top rated", "order=votes"),
            ("tag-pop", "Pop", "Pop", "tag=pop&order=clickcount"),
            ("tag-rock", "Rock", "Rock", "tag=rock&order=clickcount"),
            ("tag-dance", "Dance / elektronika", "Dance / electronic", "tag=dance&order=clickcount"),
            ("tag-hiphop", "Hip-hop / rap", "Hip-hop / rap", "tag=hip hop&order=clickcount"),
            ("tag-jazz", "Jazz", "Jazz", "tag=jazz&order=clickcount"),
            ("tag-chill", "Chill / lo-fi", "Chill / lo-fi", "tag=chillout&order=clickcount"),
            ("tag-classical", "Muzyka klasyczna", "Classical", "tag=classical&order=clickcount"),
            ("tag-news", "Wiadomości i rozmowy", "News and talk", "tag=news&order=clickcount"),
        };

        public static string CategoryPath(string query) => "stations/search?" + query + "&hidebroken=true&reverse=true&limit=100";

        public static Task<List<RadioStation>> Category(string query) => Query(CategoryPath(query));

        public static Task<List<RadioStation>> Search(string text) =>
            Query("stations/search?name=" + Uri.EscapeDataString(text ?? "") + "&hidebroken=true&order=clickcount&reverse=true&limit=100");

        // ---- pamięć podręczna list: lista pokazuje się od razu, a świeża dociąga się w tle ----
        private static readonly Dictionary<string, (DateTime at, List<RadioStation> list)> Memory = new Dictionary<string, (DateTime, List<RadioStation>)>();
        private static string CacheFile => System.IO.Path.Combine(Settings.Folder, "radio-cache.json");
        private static bool _diskLoaded;

        /// <summary>Zapamiętana lista (z pamięci albo z dysku) - null, gdy jeszcze jej nie było.</summary>
        public static List<RadioStation> Cached(string query)
        {
            LoadDisk();
            lock (Memory) return Memory.TryGetValue(CategoryPath(query), out var c) ? c.list : null;
        }

        public static bool IsFresh(string query)
        {
            lock (Memory) return Memory.TryGetValue(CategoryPath(query), out var c) && (DateTime.UtcNow - c.at).TotalMinutes < 30;
        }

        /// <summary>Po otwarciu okna: dociągamy w tle wszystkie kategorie, żeby przełączanie było natychmiastowe.</summary>
        public static async void Prefetch()
        {
            foreach (var c in Categories)
            {
                if (c.query == null || IsFresh(c.query)) continue;
                try { await Category(c.query); } catch { }
            }
        }

        private static void LoadDisk()
        {
            if (_diskLoaded) return;
            _diskLoaded = true;
            try
            {
                if (!System.IO.File.Exists(CacheFile)) return;
                var data = JsonSerializer.Deserialize<Dictionary<string, List<RadioStation>>>(System.IO.File.ReadAllText(CacheFile));
                if (data == null) return;
                lock (Memory)
                    foreach (var kv in data)
                        if (!Memory.ContainsKey(kv.Key)) Memory[kv.Key] = (DateTime.MinValue, kv.Value);
            }
            catch { }
        }

        private static void SaveDisk()
        {
            try
            {
                Dictionary<string, List<RadioStation>> data;
                lock (Memory)
                {
                    data = new Dictionary<string, List<RadioStation>>();
                    foreach (var kv in Memory) if (kv.Key.StartsWith("stations/search?countrycode") || kv.Key.StartsWith("stations/search?order") || kv.Key.StartsWith("stations/search?tag")) data[kv.Key] = kv.Value.list;
                }
                System.IO.File.WriteAllText(CacheFile, JsonSerializer.Serialize(data));
            }
            catch { }
        }

        /// <summary>Zgłoszenie odsłuchu do bazy (zgodnie z jej zasadami - pomaga ustalać popularność).</summary>
        public static async void RegisterClick(string uuid)
        {
            if (string.IsNullOrEmpty(uuid)) return;
            try { await Http.GetStringAsync("https://" + Servers[0] + ".api.radio-browser.info/json/url/" + uuid); }
            catch { }
        }

        /// <summary>
        /// Pytamy wszystkie serwery bazy naraz i bierzemy pierwszą odpowiedź - pojedynczy serwer
        /// bywa wolny albo przeciążony, a tak lista przychodzi z najszybszego.
        /// </summary>
        private static async Task<List<RadioStation>> Query(string path)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
            var tasks = Servers.Select(server => Fetch(server, path, cts.Token)).ToList();
            List<RadioStation> result = null;
            while (tasks.Count > 0)
            {
                var done = await Task.WhenAny(tasks);
                tasks.Remove(done);
                if (done.Status == TaskStatus.RanToCompletion && done.Result != null) { result = done.Result; break; }
            }
            cts.Cancel();     // reszta serwerów niepotrzebna
            if (result == null)
            {
                lock (Memory) return Memory.TryGetValue(path, out var old) ? old.list : new List<RadioStation>();   // brak internetu - ostatnia znana lista
            }
            lock (Memory) Memory[path] = (DateTime.UtcNow, result);
            _ = Task.Run(SaveDisk);
            return result;
        }

        private static async Task<List<RadioStation>> Fetch(string server, string path, System.Threading.CancellationToken ct)
        {
            try
            {
                using var res = await Http.GetAsync("https://" + server + ".api.radio-browser.info/json/" + path.Replace(" ", "%20"), ct);
                if (!res.IsSuccessStatusCode) return null;
                string json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var list = new List<RadioStation>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string url = Str(e, "url_resolved");
                    if (string.IsNullOrWhiteSpace(url)) url = Str(e, "url");
                    string name = Str(e, "name").Trim();
                    if (url.Length == 0 || name.Length == 0 || !seen.Add(name)) continue;
                    string codec = Str(e, "codec").ToUpperInvariant();
                    if (codec == "UNKNOWN") codec = "";
                    var tagList = Str(e, "tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3);
                    list.Add(new RadioStation
                    {
                        Uuid = Str(e, "stationuuid"),
                        Name = name, Url = url, Favicon = Str(e, "favicon"), Homepage = Str(e, "homepage"),
                        Country = Str(e, "country"), CountryCode = Str(e, "countrycode"),
                        Tags = string.Join(", ", tagList), Codec = codec,
                        Bitrate = Int(e, "bitrate"), Votes = Int(e, "votes"), Clicks = Int(e, "clickcount"), Trend = Int(e, "clicktrend"),
                        LastCheckOk = Int(e, "lastcheckok") == 1, Hls = Int(e, "hls") == 1,
                    });
                }
                return list;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested) Log.Info("radio: " + server + " " + ex.Message);
                return null;
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;
    }
}
