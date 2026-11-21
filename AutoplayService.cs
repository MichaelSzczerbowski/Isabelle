using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    /// <summary>
    /// Autoplay podobnych: gdy skończy się playlista w Mojej muzyce, gra dalej podobne piosenki -
    /// z Twojej biblioteki (ci sami i pokrewni wykonawcy), a gdy tam mało - przez Spotify Premium.
    /// Pokrewnych wykonawców podaje darmowy katalog Deezer (bez logowania).
    /// </summary>
    public static class AutoplayService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly Dictionary<string, List<string>> RelatedCache = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private static bool _busy;

        public static async Task OnListEndedAsync(List<LibraryTrack> ended)
        {
            if (!App.Config.Autoplay || _busy || ended == null || ended.Count == 0) return;
            if (PartyServer.Running && PartyServer.HasResume) return;     // tryb imprezy sam wraca do poprzedniego odtwarzacza
            _busy = true;
            try
            {
                // MediaEnded bywa też przy przejściu między utworami - autoplay tylko, gdy naprawdę cisza
                await Task.Delay(1500);
                if (LocalPlayer.IsPlaying || LocalPlayer.CurrentStation != null || LocalPlayer.CurrentEpisode != null) return;

                var seeds = ended.Skip(Math.Max(0, ended.Count - 8)).Select(t => ListeningHistory.MainArtist(t.Artist)).Where(a => a.Length > 0)
                                 .GroupBy(a => a, StringComparer.CurrentCultureIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3).ToList();
                if (seeds.Count == 0) return;

                var related = new List<string>();
                foreach (var a in seeds)
                {
                    try { related.AddRange(await Related(a)); } catch (Exception ex) { Log.Info("autoplay: " + ex.Message); }
                }
                var artists = new HashSet<string>(seeds.Concat(related), StringComparer.CurrentCultureIgnoreCase);

                // 1) z biblioteki: ci sami i pokrewni wykonawcy, bez właśnie granych utworów
                var justPlayed = new HashSet<string>(ended.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
                var rnd = new Random();
                var lib = LibraryService.Tracks.ToList()
                    .Where(t => !justPlayed.Contains(t.Path) && artists.Contains(ListeningHistory.MainArtist(t.Artist)))
                    .OrderBy(_ => rnd.Next()).Take(40).ToList();
                if (lib.Count >= 5 || !SpotifyService.CanPlay)
                {
                    // za mało podobnych - dokładamy losowe z całej biblioteki, żeby muzyka nie ucichła
                    if (lib.Count < 5)
                        lib.AddRange(LibraryService.Tracks.ToList().Where(t => !justPlayed.Contains(t.Path) && !lib.Contains(t)).OrderBy(_ => rnd.Next()).Take(20 - lib.Count));
                    if (lib.Count > 0) LocalPlayer.PlayTracks(lib, 0, false);
                    return;
                }

                // 2) Spotify Premium: najpopularniejsze utwory pokrewnych wykonawców
                var uris = new List<string>();
                foreach (var a in related.Concat(seeds).Distinct(StringComparer.CurrentCultureIgnoreCase).Take(8))
                {
                    try
                    {
                        var found = await SpotifyService.SearchAsync("artist:\"" + a + "\"", 3);
                        if (found != null) uris.AddRange(found.Select(t => t.Uri).Take(2));
                    }
                    catch { }
                    if (uris.Count >= 14) break;
                }
                if (uris.Count > 0) await SpotifyService.PlayUrisAsync(uris.OrderBy(_ => rnd.Next()).ToList());
                else if (lib.Count > 0) LocalPlayer.PlayTracks(lib, 0, false);
            }
            catch (Exception ex) { Log.Info("autoplay: " + ex.Message); }
            finally { _busy = false; }
        }

        /// <summary>Pokrewni wykonawcy z katalogu Deezer.</summary>
        private static async Task<List<string>> Related(string artist)
        {
            lock (RelatedCache) if (RelatedCache.TryGetValue(artist, out var c)) return c;
            var list = new List<string>();
            long id = 0;
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.deezer.com/search/artist?limit=1&q=" + Uri.EscapeDataString(artist))))
                if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0) id = data[0].GetProperty("id").GetInt64();
            if (id != 0)
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://api.deezer.com/artist/" + id + "/related?limit=10"));
                if (doc.RootElement.TryGetProperty("data", out var data))
                    foreach (var a in data.EnumerateArray())
                        if (a.TryGetProperty("name", out var n)) list.Add(n.GetString());
            }
            lock (RelatedCache) RelatedCache[artist] = list;
            return list;
        }
    }
}
