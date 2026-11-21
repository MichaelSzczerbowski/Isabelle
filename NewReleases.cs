using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class Release
    {
        public long Id { get; set; }
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public string Date { get; set; } = "";       // yyyy-MM-dd
        public string Art { get; set; } = "";
        public bool Single { get; set; }
    }

    /// <summary>
    /// Nowości od Twoich artystów: raz na dobę sprawdza w katalogu muzyki, czy najczęściej słuchani wykonawcy
    /// wydali coś w ostatnich 3 tygodniach. Nowość = dymek przy zegarze i lista w Isabelle Wrapped.
    /// </summary>
    public static class NewReleases
    {
        private sealed class Store
        {
            public long LastCheck { get; set; }
            public List<long> Seen { get; set; } = new List<long>();
            public List<Release> Recent { get; set; } = new List<Release>();
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static string FilePath => Path.Combine(Settings.Folder, "releases.json");
        private static Store _store;
        private static System.Windows.Threading.DispatcherTimer _timer;
        private static bool _checking;

        /// <summary>Nowe wydanie (dla dymka przy zegarze).</summary>
        public static event Action<Release> Found;

        private static Store Data
        {
            get
            {
                if (_store != null) return _store;
                try { if (File.Exists(FilePath)) _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)); } catch (Exception ex) { Log.Error(ex); }
                return _store ??= new Store();
            }
        }

        /// <summary>Ostatnie nowości (najnowsze pierwsze).</summary>
        public static List<Release> Recent => Data.Recent.OrderByDescending(r => r.Date).ToList();

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _timer.Tick += (s, e) => { _timer.Interval = TimeSpan.FromMinutes(30); _ = CheckIfDue(); _ = ConcertService.CheckIfDue(); };
            _timer.Interval = TimeSpan.FromMinutes(2);        // pierwsze sprawdzenie chwilę po starcie
            _timer.Start();
        }

        private static async Task CheckIfDue()
        {
            if (!App.Config.NewReleases) return;
            if ((DateTime.UtcNow - new DateTime(Data.LastCheck, DateTimeKind.Utc)).TotalHours < 24) return;
            await CheckAsync();
        }

        public static async Task CheckAsync()
        {
            if (_checking) return;
            _checking = true;
            try
            {
                var artists = ListeningHistory.TopArtists(DateTime.UtcNow.AddDays(-90), 10).Where(a => a.count >= 3).Select(a => a.artist).ToList();
                bool firstRun = Data.LastCheck == 0;
                var found = new List<Release>();
                foreach (var artist in artists)
                {
                    try { found.AddRange(await LatestFor(artist)); }
                    catch (Exception ex) { Log.Info("nowości " + artist + ": " + ex.Message); }
                    await Task.Delay(400);           // grzecznie dla katalogu
                }
                foreach (var r in found)
                {
                    if (Data.Seen.Contains(r.Id)) continue;
                    Data.Seen.Add(r.Id);
                    Data.Recent.Add(r);
                    // za pierwszym razem bez dymków - tylko lista (żeby nie zasypać powiadomieniami)
                    if (!firstRun) { try { Found?.Invoke(r); } catch (Exception ex) { Log.Error(ex); } }
                }
                // na liście zostają nowości z ostatnich 60 dni
                string cut = DateTime.UtcNow.AddDays(-60).ToString("yyyy-MM-dd");
                Data.Recent.RemoveAll(r => string.CompareOrdinal(r.Date, cut) < 0);
                if (Data.Seen.Count > 2000) Data.Seen.RemoveRange(0, Data.Seen.Count - 2000);
                Data.LastCheck = DateTime.UtcNow.Ticks;
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Data));
            }
            catch (Exception ex) { Log.Error(ex); }
            finally { _checking = false; }
        }

        /// <summary>Wydania artysty z ostatnich 21 dni (albumy, EP i single).</summary>
        private static async Task<List<Release>> LatestFor(string artist)
        {
            var list = new List<Release>();
            // 1) identyfikator wykonawcy (dokładnie ta nazwa)
            string url = "https://itunes.apple.com/search?entity=musicArtist&limit=5&term=" + Uri.EscapeDataString(artist);
            long artistId = 0;
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url)))
            {
                foreach (var it in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    if (it.TryGetProperty("artistName", out var n) && string.Equals(n.GetString(), artist, StringComparison.CurrentCultureIgnoreCase) &&
                        it.TryGetProperty("artistId", out var id)) { artistId = id.GetInt64(); break; }
                }
            }
            if (artistId == 0) return list;
            // 2) najnowsze wydania
            url = "https://itunes.apple.com/lookup?entity=album&sort=recent&limit=10&id=" + artistId;
            DateTime from = DateTime.UtcNow.AddDays(-21);
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync(url)))
            {
                foreach (var it in doc.RootElement.GetProperty("results").EnumerateArray())
                {
                    if (!it.TryGetProperty("wrapperType", out var w) || w.GetString() != "collection") continue;
                    if (!it.TryGetProperty("releaseDate", out var rd) || !DateTime.TryParse(rd.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)) continue;
                    if (date < from || date > DateTime.UtcNow.AddDays(1)) continue;
                    string name = it.TryGetProperty("collectionName", out var cn) ? cn.GetString() : "";
                    int tracks = it.TryGetProperty("trackCount", out var tc) ? tc.GetInt32() : 0;
                    list.Add(new Release
                    {
                        Id = it.GetProperty("collectionId").GetInt64(),
                        Artist = it.TryGetProperty("artistName", out var an) ? an.GetString() : artist,
                        Album = name.Replace(" - Single", "").Replace(" - EP", ""),
                        Date = date.ToString("yyyy-MM-dd"),
                        Art = it.TryGetProperty("artworkUrl100", out var au) ? au.GetString()?.Replace("100x100bb", "300x300bb") : "",
                        Single = tracks <= 3 || name.EndsWith(" - Single"),
                    });
                }
            }
            return list;
        }

        /// <summary>Otwiera wydanie w Spotify (albo YouTube Music, gdy nie ma Spotify).</summary>
        public static void Open(Release r)
        {
            if (r == null) return;
            string term = Uri.EscapeDataString(r.Artist + " " + r.Album);
            bool spotify = false;
            try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("spotify"); spotify = k != null; } catch { }
            string link = spotify ? "spotify:search:" + term : "https://music.youtube.com/search?q=" + term;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true }); } catch { }
        }
    }
}
