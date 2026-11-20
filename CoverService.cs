using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>
    /// Czysta okładka z internetu (iTunes, potem Deezer - darmowe, bez klucza).
    /// Spotify wysyła do Windows okładkę z doklejonym paskiem i logo - tak jej unikamy.
    /// </summary>
    public static class CoverService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly Dictionary<string, (BitmapSource, Color)?> Cache = new Dictionary<string, (BitmapSource, Color)?>();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/1.4 (+https://github.com/MichaelSzczerbowski/Isabelle)");
            return c;
        }

        public static async Task<(BitmapSource bmp, Color accent)?> FindAsync(string title, string artist)
        {
            string key = Norm(title) + "|" + Norm(artist);
            lock (Cache)
                if (Cache.TryGetValue(key, out var hit)) return hit;

            (BitmapSource, Color)? result = null;
            try
            {
                string url = await FromITunes(title, artist) ?? await FromDeezer(title, artist);
                if (url != null)
                {
                    byte[] data = await Http.GetByteArrayAsync(url);
                    var (bmp, accent) = ImageUtil.ProcessCover(data, "");
                    if (bmp != null) result = (bmp, accent);
                }
            }
            catch (Exception ex)
            {
                Log.Info("okładka online: " + ex.Message);
            }

            lock (Cache)
            {
                if (Cache.Count > 300) Cache.Clear();
                Cache[key] = result;
            }
            return result;
        }

        private static async Task<string> FromITunes(string title, string artist)
        {
            try
            {
                string q = Uri.EscapeDataString(CleanTitle(title) + " " + MainArtist(artist));
                string json = await Http.GetStringAsync("https://itunes.apple.com/search?entity=song&limit=8&term=" + q);
                using (var doc = JsonDocument.Parse(json))
                {
                    foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
                    {
                        string t = Str(r, "trackName"), a = Str(r, "artistName"), art = Str(r, "artworkUrl100");
                        if (art.Length > 0 && Matches(title, artist, t, a))
                            return art.Replace("100x100bb", "600x600bb");
                    }
                }
            }
            catch { }
            return null;
        }

        private static async Task<string> FromDeezer(string title, string artist)
        {
            try
            {
                string q = Uri.EscapeDataString("artist:\"" + MainArtist(artist) + "\" track:\"" + CleanTitle(title) + "\"");
                string json = await Http.GetStringAsync("https://api.deezer.com/search?limit=8&q=" + q);
                using (var doc = JsonDocument.Parse(json))
                {
                    if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
                    foreach (var r in data.EnumerateArray())
                    {
                        string t = Str(r, "title");
                        string a = r.TryGetProperty("artist", out var ar) ? Str(ar, "name") : "";
                        string art = r.TryGetProperty("album", out var al) ? Str(al, "cover_xl") : "";
                        if (art.Length > 0 && Matches(title, artist, t, a)) return art;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        /// <summary>Tytuł i wykonawca muszą się zgadzać (jeden zawiera drugi), żeby nie wstawić okładki innego utworu.</summary>
        private static bool Matches(string title, string artist, string foundTitle, string foundArtist)
        {
            string t1 = Norm(CleanTitle(title)), t2 = Norm(CleanTitle(foundTitle));
            string a1 = Norm(MainArtist(artist)), a2 = Norm(foundArtist);
            if (t1.Length == 0 || a1.Length == 0) return false;
            bool titleOk = t1 == t2 || t2.Contains(t1) || t1.Contains(t2) && t2.Length >= 3;
            bool artistOk = a2.Contains(a1) || a1.Contains(a2) && a2.Length >= 2;
            return titleOk && artistOk;
        }

        private static string CleanTitle(string t)
        {
            t = Regex.Replace(t ?? "", @"\s*[\(\[].*?[\)\]]", "");
            t = Regex.Replace(t, @"\s+-\s+.*$", "");
            t = Regex.Replace(t, @"\s+(feat\.?|ft\.?|featuring)\s+.*$", "", RegexOptions.IgnoreCase);
            return t.Trim();
        }

        private static string MainArtist(string a) =>
            Regex.Split(a ?? "", @",|&| x |\s+(feat\.?|ft\.?|featuring)\s+", RegexOptions.IgnoreCase)[0].Trim();

        private static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD))
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue; // ą -> a
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }
    }
}
