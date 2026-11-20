using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    /// <summary>
    /// Prawdziwy link do utworu w Spotify (open.spotify.com/track/…), który na Discordzie i w komunikatorach
    /// pokazuje podgląd z próbką piosenki. Windows nie podaje ID utworu, więc szukamy go przez Spotify Web API -
    /// na własnej (darmowej) aplikacji użytkownika: Client ID + Client Secret z developer.spotify.com.
    /// </summary>
    public static class SpotifyLinks
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>();
        private static string _token, _tokenFor;
        private static DateTime _tokenUntil;

        public static bool Configured =>
            !string.IsNullOrWhiteSpace(App.Config.SpotifyClientId) && !string.IsNullOrWhiteSpace(App.Config.SpotifyClientSecret);

        /// <summary>Link do utworu albo null (brak kluczy, brak wyniku, błąd).</summary>
        public static async Task<string> FindTrack(string title, string artist)
        {
            if (!Configured || string.IsNullOrWhiteSpace(title)) return null;
            string key = (artist + "\n" + title).ToLowerInvariant();
            lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;
            try
            {
                string token = await Token();
                if (token == null) return null;
                string mainArtist = (artist ?? "").Split(new[] { ", ", " & ", " feat", " ft." }, StringSplitOptions.None)[0].Trim();
                string url = await Search(token, $"track:{Clean(title)} artist:{mainArtist}", title, artist)
                          ?? await Search(token, $"{mainArtist} {Clean(title)}", title, artist);
                if (url != null) lock (Cache) Cache[key] = url;
                return url;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return null;
            }
        }

        /// <summary>Sprawdza klucze (do przycisku „Sprawdź” w ustawieniach).</summary>
        public static async Task<bool> Test()
        {
            _token = null;
            try { return await Token() != null; }
            catch { return false; }
        }

        private static async Task<string> Token()
        {
            string id = App.Config.SpotifyClientId.Trim(), secret = App.Config.SpotifyClientSecret.Trim();
            if (_token != null && _tokenFor == id && DateTime.UtcNow < _tokenUntil) return _token;
            var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
            {
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("grant_type", "client_credentials") }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(id + ":" + secret)));
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) { Log.Info("Spotify token: " + (int)res.StatusCode); return null; }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            _token = doc.RootElement.GetProperty("access_token").GetString();
            _tokenFor = id;
            _tokenUntil = DateTime.UtcNow.AddSeconds(doc.RootElement.GetProperty("expires_in").GetInt32() - 60);
            return _token;
        }

        private static async Task<string> Search(string token, string query, string title, string artist)
        {
            var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.spotify.com/v1/search?type=track&limit=10&q=" + Uri.EscapeDataString(query));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) { Log.Info("Spotify search: " + (int)res.StatusCode); return null; }
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) || !tracks.TryGetProperty("items", out var items)) return null;

            // najlepiej pasujący wynik: zgodny tytuł i wykonawca
            string wantTitle = Norm(Clean(title)), wantArtist = Norm(artist);
            string best = null;
            int bestScore = 0;
            foreach (var t in items.EnumerateArray())
            {
                string name = t.TryGetProperty("name", out var n) ? n.GetString() : "";
                string link = t.TryGetProperty("external_urls", out var ex) && ex.TryGetProperty("spotify", out var sp) ? sp.GetString() : null;
                if (link == null) continue;
                var artists = t.TryGetProperty("artists", out var ar)
                    ? ar.EnumerateArray().Select(a => Norm(a.TryGetProperty("name", out var an) ? an.GetString() : "")).ToList()
                    : new List<string>();
                string nt = Norm(Clean(name));
                int score = 0;
                if (nt == wantTitle) score += 4;
                else if (nt.StartsWith(wantTitle) || wantTitle.StartsWith(nt)) score += 2;
                if (artists.Any(a => a.Length > 0 && wantArtist.Contains(a))) score += 3;
                if (score > bestScore) { bestScore = score; best = link; }
            }
            return bestScore >= 5 ? best : null;
        }

        /// <summary>Bez dopisków typu „- Remastered 2011”, „(feat. …)”.</summary>
        private static string Clean(string s)
        {
            s ??= "";
            int i = s.IndexOf(" - ", StringComparison.Ordinal);
            if (i > 0) s = s.Substring(0, i);
            foreach (var open in new[] { " (feat", " (ft.", " (with", " [feat" })
            {
                int j = s.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (j > 0) s = s.Substring(0, j);
            }
            return s.Trim();
        }

        private static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString().Replace("ł", "l");
        }
    }
}
