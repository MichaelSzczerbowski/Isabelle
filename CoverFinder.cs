using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandPlayer
{
    /// <summary>
    /// okładki dla utworów z biblioteki. Najpierw okładka wbudowana w plik (albo folder.jpg obok),
    /// a gdy jej brak - szukamy w darmowym wyszukiwaniu iTunes po wykonawcy i tytule. Okładki zapisujemy
    /// w folderze Isabelle (pliki muzyczne zostają nietknięte). Przy okazji, gdy plik nie miał tagów,
    /// poprawiamy nieczytelną nazwę (np. „YTDown.com_..._lyric-video_128k”) na prawdziwy tytuł i wykonawcę.
    /// </summary>
    public static class CoverFinder
    {
        private static readonly HttpClient Http = CreateClient();
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(3);
        private static CancellationTokenSource _cts;

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/1.5 (+https://github.com/MichaelSzczerbowski/Isabelle)");
            return c;
        }

        public static string Folder
        {
            get
            {
                string d = System.IO.Path.Combine(Settings.Folder, "covers");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        /// <summary>Szuka okładek dla wszystkich utworów, które jeszcze ich nie mają (w tle, po 3 naraz).</summary>
        public static async Task FillAsync(IReadOnlyList<LibraryTrack> tracks, Action progress)
        {
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            var todo = tracks.Where(t => t.CoverPath == null || (t.CoverPath.Length > 0 && !File.Exists(t.CoverPath))).ToList();
            int done = 0;
            var tasks = todo.Select(async t =>
            {
                await Gate.WaitAsync();
                try
                {
                    if (cts.IsCancellationRequested) return;
                    await Find(t);
                    if (Interlocked.Increment(ref done) % 5 == 0) progress?.Invoke();
                }
                catch (Exception ex) { Log.Info("covers: " + ex.Message); }
                finally { Gate.Release(); }
            });
            await Task.WhenAll(tasks);
            progress?.Invoke();
        }

        /// <summary>Okładka dla jednego utworu: ustawia t.CoverPath (pusty = nie znaleziono).</summary>
        public static async Task Find(LibraryTrack t)
        {
            string path = System.IO.Path.Combine(Folder, Hash(t.Path) + ".jpg");
            if (File.Exists(path)) { t.CoverPath = path; return; }

            // 1) wbudowana w plik / folder.jpg (Windows podaje ją jako miniaturę „muzyczną”)
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(t.Path);
                using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.MusicView, 600);
                if (thumb != null && thumb.Type == Windows.Storage.FileProperties.ThumbnailType.Image && thumb.Size > 0)
                {
                    using var src = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(thumb);
                    using var dst = File.Create(path);
                    await src.CopyToAsync(dst);
                    t.CoverPath = path;
                    return;
                }
            }
            catch { }

            // 2) wyszukiwanie online
            var hit = await Search(t);
            if (hit == null) { t.CoverPath = ""; return; }
            try
            {
                var bytes = await Http.GetByteArrayAsync(hit.Value.art);
                await File.WriteAllBytesAsync(path, bytes);
                t.CoverPath = path;
                // plik bez tagów: zamiast nazwy pliku pokażmy prawdziwy tytuł i wykonawcę
                if (!t.Tagged && hit.Value.sure)
                {
                    t.Title = hit.Value.title;
                    t.Artist = hit.Value.artist;
                    if (string.IsNullOrEmpty(t.Album)) t.Album = hit.Value.album;
                }
            }
            catch { t.CoverPath = ""; }
        }

        private static async Task<(string art, string title, string artist, string album, bool sure)?> Search(LibraryTrack t)
        {
            string term = (t.Tagged ? t.Artist + " " + t.Title : t.Artist.Length > 0 && !t.ArtistFromFolder ? t.Artist + " " + t.Title : t.Title).Trim();
            if (term.Length < 2) return null;
            try
            {
                string url = "https://itunes.apple.com/search?media=music&entity=song&limit=10&term=" + Uri.EscapeDataString(term);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (!doc.RootElement.TryGetProperty("results", out var results)) return null;
                var want = Words(term);
                (string, string, string, string, bool)? best = null;
                double bestScore = 0;
                foreach (var r in results.EnumerateArray())
                {
                    string name = Str(r, "trackName"), artist = Str(r, "artistName"), album = Str(r, "collectionName"), art = Str(r, "artworkUrl100");
                    if (art.Length == 0) continue;
                    // ile słów z tytułu i wykonawcy znalezionego utworu występuje w naszej nazwie
                    var have = Words(name + " " + artist);
                    if (have.Count == 0) continue;
                    double hitRate = have.Count(w => want.Contains(w)) / (double)have.Count;
                    double titleHit = Words(name).Count(w => want.Contains(w)) / (double)Math.Max(1, Words(name).Count);
                    double score = hitRate * 0.6 + titleHit * 0.4;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = (art.Replace("100x100bb", "600x600bb"), name, artist, album, score >= 0.75);
                    }
                }
                return bestScore >= 0.5 ? best : null;
            }
            catch (Exception ex)
            {
                Log.Info("covers: " + ex.Message);
                return null;
            }
        }

        private static HashSet<string> Words(string s)
        {
            var set = new HashSet<string>();
            var sb = new StringBuilder();
            foreach (char ch in (s ?? "").Replace("ł", "l").Replace("Ł", "L").Normalize(NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                else { if (sb.Length > 1) set.Add(sb.ToString()); sb.Clear(); }
            }
            if (sb.Length > 1) set.Add(sb.ToString());
            set.ExceptWith(new[] { "the", "feat", "ft", "remix", "remastered", "version", "official", "radio", "edit" });
            return set;
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static string Hash(string s)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
            return BitConverter.ToString(h, 0, 10).Replace("-", "").ToLowerInvariant();
        }
    }
}

namespace IslandPlayer
{
    /// <summary>
    /// teksty piosenek dla biblioteki - tak jak okładki. Najpierw plik .lrc obok utworu
    /// (standard innych odtwarzaczy), potem darmowy LRCLIB. Znaleziony tekst zapisujemy w folderze Isabelle,
    /// więc przy odtwarzaniu jest od razu, także bez internetu.
    /// </summary>
    public static class LyricsFinder
    {
        private static readonly System.Threading.SemaphoreSlim Gate = new System.Threading.SemaphoreSlim(2);

        public static string Folder
        {
            get
            {
                string d = System.IO.Path.Combine(Settings.Folder, "lyrics");
                System.IO.Directory.CreateDirectory(d);
                return d;
            }
        }

        public static async System.Threading.Tasks.Task FillAsync(System.Collections.Generic.IReadOnlyList<LibraryTrack> tracks, Action progress)
        {
            var todo = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Where(tracks,
                t => t.LyricsPath == null || (t.LyricsPath.Length > 0 && !System.IO.File.Exists(t.LyricsPath))));
            int done = 0;
            var tasks = System.Linq.Enumerable.Select(todo, async t =>
            {
                await Gate.WaitAsync();
                try
                {
                    await Find(t);
                    if (System.Threading.Interlocked.Increment(ref done) % 5 == 0) progress?.Invoke();
                }
                catch (Exception ex) { Log.Info("lyrics: " + ex.Message); }
                finally { Gate.Release(); }
            });
            await System.Threading.Tasks.Task.WhenAll(tasks);
            progress?.Invoke();
        }

        /// <summary>Ustawia t.LyricsPath (pusty = tekstu nie ma).</summary>
        public static async System.Threading.Tasks.Task Find(LibraryTrack t)
        {
            // 1) .lrc obok pliku muzycznego
            string side = System.IO.Path.ChangeExtension(t.Path, ".lrc");
            if (System.IO.File.Exists(side)) { t.LyricsPath = side; t.LyricsSynced = true; return; }

            string baseName = System.IO.Path.Combine(Folder, System.IO.Path.GetFileNameWithoutExtension(CoverFinderHash(t.Path)));
            if (System.IO.File.Exists(baseName + ".lrc")) { t.LyricsPath = baseName + ".lrc"; t.LyricsSynced = true; return; }
            if (System.IO.File.Exists(baseName + ".txt")) { t.LyricsPath = baseName + ".txt"; t.LyricsSynced = false; return; }

            // 2) LRCLIB (wykonawca zgadnięty z nazwy folderu nie pomaga - wtedy szukamy po samym tytule)
            string artist = t.ArtistFromFolder && !t.Tagged ? "" : t.Artist;
            var (synced, plain) = await LyricsService.FetchRawAsync(t.Title, artist, t.Duration);
            if (!string.IsNullOrWhiteSpace(synced))
            {
                await System.IO.File.WriteAllTextAsync(baseName + ".lrc", synced);
                t.LyricsPath = baseName + ".lrc";
                t.LyricsSynced = true;
            }
            else if (!string.IsNullOrWhiteSpace(plain))
            {
                await System.IO.File.WriteAllTextAsync(baseName + ".txt", plain);
                t.LyricsPath = baseName + ".txt";
                t.LyricsSynced = false;
            }
            else t.LyricsPath = "";
        }

        private static string CoverFinderHash(string s)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
            return BitConverter.ToString(h, 0, 10).Replace("-", "").ToLowerInvariant();
        }
    }
}
