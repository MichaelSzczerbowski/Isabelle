using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class Concert
    {
        public string Id { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Name { get; set; } = "";
        public string Date { get; set; } = "";       // yyyy-MM-dd
        public string City { get; set; } = "";
        public string Venue { get; set; } = "";
        public string Url { get; set; } = "";
    }

    /// <summary>
    /// Koncerty w okolicy: raz na dobę sprawdza w Ticketmaster (darmowy klucz z developer.ticketmaster.com),
    /// czy najczęściej słuchani wykonawcy grają w wybranych miastach. Nowy koncert = dymek przy zegarze.
    /// </summary>
    public static class ConcertService
    {
        private sealed class Store
        {
            public long LastCheck { get; set; }
            public List<string> Seen { get; set; } = new List<string>();
            public List<Concert> Upcoming { get; set; } = new List<Concert>();
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static string FilePath => Path.Combine(Settings.Folder, "concerts.json");
        private static Store _store;
        private static bool _checking;
        public static event Action<Concert> Found;
        public static string LastError { get; private set; }

        public static bool Configured => !string.IsNullOrWhiteSpace(App.Config.TicketmasterKey);

        private static Store Data
        {
            get
            {
                if (_store != null) return _store;
                try { if (File.Exists(FilePath)) _store = JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)); } catch (Exception ex) { Log.Error(ex); }
                return _store ??= new Store();
            }
        }

        /// <summary>Nadchodzące koncerty (najbliższe pierwsze).</summary>
        public static List<Concert> Upcoming
        {
            get
            {
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                return Data.Upcoming.Where(c => string.CompareOrdinal(c.Date, today) >= 0).OrderBy(c => c.Date).ToList();
            }
        }

        public static async Task CheckIfDue()
        {
            if (!App.Config.Concerts || !Configured) return;
            if ((DateTime.UtcNow - new DateTime(Data.LastCheck, DateTimeKind.Utc)).TotalHours < 24) return;
            await CheckAsync();
        }

        public static async Task CheckAsync()
        {
            if (_checking || !Configured) return;
            _checking = true;
            try
            {
                LastError = null;
                var artists = ListeningHistory.TopArtists(DateTime.UtcNow.AddDays(-120), 12).Where(a => a.count >= 3).Select(a => a.artist).ToList();
                var cities = (App.Config.ConcertCities ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(c => Plain(c.Trim())).Where(c => c.Length > 0).ToList();
                bool firstRun = Data.LastCheck == 0;
                var all = new List<Concert>();
                foreach (var artist in artists)
                {
                    try { all.AddRange(await EventsFor(artist)); }
                    catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    {
                        LastError = L.T("Zły klucz Ticketmaster.", "Invalid Ticketmaster key.");
                        break;
                    }
                    catch (Exception ex) { Log.Info("koncerty " + artist + ": " + ex.Message); }
                    await Task.Delay(300);         // limit zapytań Ticketmaster
                }
                // tylko wybrane miasta (bez polskich znaków w porównaniu: „Krakow” = „Kraków”)
                if (cities.Count > 0) all = all.Where(c => cities.Any(x => Plain(c.City).Contains(x) || x.Contains(Plain(c.City)))).ToList();

                foreach (var c in all)
                {
                    if (Data.Upcoming.All(u => u.Id != c.Id)) Data.Upcoming.Add(c);
                    if (Data.Seen.Contains(c.Id)) continue;
                    Data.Seen.Add(c.Id);
                    if (!firstRun) { try { Found?.Invoke(c); } catch (Exception ex) { Log.Error(ex); } }
                }
                string today = DateTime.Now.ToString("yyyy-MM-dd");
                Data.Upcoming.RemoveAll(c => string.CompareOrdinal(c.Date, today) < 0);
                if (Data.Seen.Count > 2000) Data.Seen.RemoveRange(0, Data.Seen.Count - 2000);
                if (LastError == null) Data.LastCheck = DateTime.UtcNow.Ticks;
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Data));
            }
            catch (Exception ex) { Log.Error(ex); LastError = ex.Message; }
            finally { _checking = false; }
        }

        private static async Task<List<Concert>> EventsFor(string artist)
        {
            var list = new List<Concert>();
            string country = string.IsNullOrWhiteSpace(App.Config.ConcertCountry) ? "PL" : App.Config.ConcertCountry.Trim().ToUpperInvariant();
            string url = "https://app.ticketmaster.com/discovery/v2/events.json?classificationName=music&size=30&sort=date,asc" +
                         "&countryCode=" + country + "&keyword=" + Uri.EscapeDataString(artist) + "&apikey=" + Uri.EscapeDataString(App.Config.TicketmasterKey.Trim());
            using var res = await Http.GetAsync(url);
            if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized) throw new HttpRequestException("401", null, res.StatusCode);
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("_embedded", out var emb) || !emb.TryGetProperty("events", out var events)) return list;
            foreach (var e in events.EnumerateArray())
            {
                // tylko gdy wykonawca naprawdę występuje (nie tylko pasuje słowo w nazwie)
                bool performs = e.TryGetProperty("_embedded", out var ee) && ee.TryGetProperty("attractions", out var atts) &&
                                atts.EnumerateArray().Any(a => a.TryGetProperty("name", out var n) && string.Equals(n.GetString(), artist, StringComparison.CurrentCultureIgnoreCase));
                if (!performs) continue;
                var c = new Concert
                {
                    Id = e.GetProperty("id").GetString(),
                    Artist = artist,
                    Name = e.TryGetProperty("name", out var nm) ? nm.GetString() : artist,
                    Url = e.TryGetProperty("url", out var u) ? u.GetString() : "",
                    Date = e.TryGetProperty("dates", out var d) && d.TryGetProperty("start", out var st) && st.TryGetProperty("localDate", out var ld) ? ld.GetString() : "",
                };
                if (ee.TryGetProperty("venues", out var venues) && venues.GetArrayLength() > 0)
                {
                    var v = venues[0];
                    c.Venue = v.TryGetProperty("name", out var vn) ? vn.GetString() : "";
                    c.City = v.TryGetProperty("city", out var city) && city.TryGetProperty("name", out var cn) ? cn.GetString() : "";
                }
                if (c.Date.Length > 0) list.Add(c);
            }
            return list;
        }

        public static void Open(Concert c)
        {
            if (string.IsNullOrEmpty(c?.Url)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(c.Url) { UseShellExecute = true }); } catch { }
        }

        /// <summary>Małe litery bez polskich znaków (do porównywania miast).</summary>
        private static string Plain(string s)
        {
            var n = (s ?? "").ToLowerInvariant().Replace('ł', 'l').Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var ch in n) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
            return sb.ToString();
        }
    }
}
