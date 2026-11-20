using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class TrackInfo
    {
        public string Album = "";
        public int Year;
        public string Genre = "";
        public int TrackNumber, TrackCount;
        public double Length;          // sekundy
        public bool Single;
    }

    /// <summary>
    /// Informacje o utworze (album, rok, gatunek, numer na płycie) z darmowego iTunes Search API - bez klucza.
    /// </summary>
    public static class TrackInfoService
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly Dictionary<string, TrackInfo> Cache = new Dictionary<string, TrackInfo>();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/1.4 (+https://github.com/MichaelSzczerbowski/Isabelle)");
            return c;
        }

        /// <summary>Null = nic nie znaleziono albo brak internetu.</summary>
        public static async Task<TrackInfo> Get(string title, string artist)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            string key = (artist + "\n" + title).ToLowerInvariant();
            lock (Cache) if (Cache.TryGetValue(key, out var hit)) return hit;

            TrackInfo info = null;
            try
            {
                string mainArtist = MainArtist(artist);
                info = await Search($"{mainArtist} {Clean(title)}", title, artist, "PL")
                    ?? await Search($"{mainArtist} {Clean(title)}", title, artist, "US");
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return null;      // błąd sieci - nie zapamiętujemy, spróbujemy następnym razem
            }
            lock (Cache) Cache[key] = info;
            return info;
        }

        private static async Task<TrackInfo> Search(string term, string title, string artist, string country)
        {
            string url = "https://itunes.apple.com/search?media=music&entity=song&limit=25&country=" + country +
                         "&term=" + Uri.EscapeDataString(term);
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            if (!doc.RootElement.TryGetProperty("results", out var results)) return null;

            string wantTitle = Norm(Clean(title)), wantArtist = Norm(artist), wantMain = Norm(MainArtist(artist));
            TrackInfo best = null;
            DateTime bestDate = DateTime.MaxValue;
            foreach (var r in results.EnumerateArray())
            {
                string name = Str(r, "trackName"), art = Str(r, "artistName");
                string nt = Norm(Clean(name)), na = Norm(art);
                bool titleOk = nt == wantTitle || (wantTitle.Length >= 4 && (nt.StartsWith(wantTitle) || wantTitle.StartsWith(nt)));
                bool artistOk = na.Length > 0 && (na == wantArtist || na == wantMain || wantArtist.Contains(na) || na.Contains(wantMain));
                if (!titleOk || !artistOk) continue;

                DateTime date = DateTime.TryParse(Str(r, "releaseDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d)
                    ? d : DateTime.MaxValue;
                string album = Str(r, "collectionName");
                bool single = album.EndsWith(" - Single", StringComparison.OrdinalIgnoreCase) || album.EndsWith(" - EP", StringComparison.OrdinalIgnoreCase);
                bool compilation = album.IndexOf("greatest hits", StringComparison.OrdinalIgnoreCase) >= 0
                                || album.IndexOf("best of", StringComparison.OrdinalIgnoreCase) >= 0
                                || album.IndexOf("collection", StringComparison.OrdinalIgnoreCase) >= 0;
                // najwcześniejsze wydanie (oryginalny rok), składanki tylko gdy nic innego nie ma
                if (compilation && best != null) continue;
                if (best != null && date >= bestDate) continue;

                best = new TrackInfo
                {
                    Album = single ? album.Substring(0, album.LastIndexOf(" - ", StringComparison.Ordinal)) : album,
                    Single = single,
                    Year = date == DateTime.MaxValue ? 0 : date.Year,
                    Genre = Str(r, "primaryGenreName"),
                    TrackNumber = Int(r, "trackNumber"),
                    TrackCount = Int(r, "trackCount"),
                    Length = Int(r, "trackTimeMillis") / 1000.0,
                };
                bestDate = date;
            }
            return best;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

        private static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : 0;

        private static string MainArtist(string artist) =>
            (artist ?? "").Split(new[] { ", ", " & ", " feat", " ft.", " x " }, StringSplitOptions.None)[0].Trim();

        /// <summary>Bez dopisków typu „- Remastered 2011”, „(feat. …)”.</summary>
        private static string Clean(string s)
        {
            s ??= "";
            int i = s.IndexOf(" - ", StringComparison.Ordinal);
            if (i > 0) s = s.Substring(0, i);
            foreach (var open in new[] { " (feat", " (ft.", " (with", " [feat", " (Remaster", " [Remaster" })
            {
                int j = s.IndexOf(open, StringComparison.OrdinalIgnoreCase);
                if (j > 0) s = s.Substring(0, j);
            }
            return s.Trim();
        }

        private static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").Replace("ł", "l").Replace("Ł", "L").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }
    }
}
