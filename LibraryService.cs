using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class LibraryTrack : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        /// <summary>Polubiony (serduszko) - zapisane w playlists.json, nie w bibliotece.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Liked => PlaylistStore.IsLiked(Path);

        /// <summary>Zapisany gdziekolwiek (polubiony albo na którejś playliście) - ikonka ✓ zamiast „+”.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Saved => Liked || PlaylistStore.InAnyPlaylist(Path);

        public void NotifyLiked()
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Liked)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Saved)));
        }

        public string Path { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public double Duration { get; set; }
        public long Modified { get; set; }
        /// <summary>Kiedy plik trafił do biblioteki (UTC, ticks) - do „Ostatnio dodane”.</summary>
        public long Added { get; set; }
        /// <summary>Plik z okładką (w folderze Isabelle); null = jeszcze nie szukano, "" = brak.</summary>
        public string CoverPath { get; set; }
        /// <summary>Plik miał własne tagi (tytuł) - wtedy nie poprawiamy nazw.</summary>
        public bool Tagged { get; set; }
        /// <summary>Wykonawca to tylko nazwa folderu (zgadnięty), a nie prawdziwy.</summary>
        public bool ArtistFromFolder { get; set; }
        /// <summary>Plik z tekstem (.lrc / .txt); null = jeszcze nie szukano, "" = brak.</summary>
        public string LyricsPath { get; set; }
        public bool LyricsSynced { get; set; }

        /// <summary>Plakietka na liście: tekst z czasami / sam tekst / nic.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string LyricsBadge => string.IsNullOrEmpty(LyricsPath) ? "" : LyricsSynced ? "LRC" : L.T("tekst", "lyrics");

        public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : Artist + " – " + Title;
        public string Time => Duration > 0 ? TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss") : "";
    }

    /// <summary>
    /// biblioteka muzyki z wybranego folderu. Najpierw szybka lista z nazw plików,
    /// potem w tle uzupełnianie tytułów, wykonawców i długości z tagów. Wynik zapisany w library.json.
    /// </summary>
    public static class LibraryService
    {
        private static readonly string[] Extensions = { ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma", ".alac" };

        public static bool IsMusic(string path) => Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

        /// <summary>
        /// Kopiuje pliki muzyczne (i całe foldery) do folderu biblioteki. Gdy folderu jeszcze nie ma -
        /// tworzy Muzyka\Isabelle. Zwraca liczbę dodanych utworów.
        /// </summary>
        public static async Task<int> ImportAsync(IEnumerable<string> paths)
        {
            string lib = App.Config.LibraryFolder;
            if (string.IsNullOrWhiteSpace(lib) || !Directory.Exists(lib))
            {
                lib = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Isabelle");
                Directory.CreateDirectory(lib);
                App.Config.LibraryFolder = lib;
                App.Config.Save();
                StartWatching();
            }
            var list = paths.ToList();
            int added = await Task.Run(() =>
            {
                int n = 0;
                foreach (var p in list)
                {
                    try
                    {
                        if (Directory.Exists(p))
                        {
                            // folder: zachowujemy jego nazwę (np. album) jako podfolder
                            string baseDir = System.IO.Path.GetDirectoryName(p.TrimEnd('\\', '/')) ?? p;
                            foreach (var f in Directory.EnumerateFiles(p, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                                if (IsMusic(f) && CopyInto(f, System.IO.Path.Combine(lib, System.IO.Path.GetRelativePath(baseDir, f)))) n++;
                        }
                        else if (File.Exists(p) && IsMusic(p))
                        {
                            if (CopyInto(p, System.IO.Path.Combine(lib, System.IO.Path.GetFileName(p)))) n++;
                        }
                    }
                    catch (Exception ex) { Log.Info("import: " + ex.Message); }
                }
                return n;
            });
            await ScanAsync(lib);
            return added;
        }

        /// <summary>Kopia pliku; plik już leżący w bibliotece zostaje, a ta sama nazwa dostaje dopisek (2), (3)…</summary>
        private static bool CopyInto(string src, string dest)
        {
            string lib = App.Config.LibraryFolder ?? "";
            if (System.IO.Path.GetFullPath(src).StartsWith(System.IO.Path.GetFullPath(lib), StringComparison.OrdinalIgnoreCase)) return false;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            if (File.Exists(dest))
            {
                if (new FileInfo(dest).Length == new FileInfo(src).Length) return false;   // to samo już jest
                string dir = System.IO.Path.GetDirectoryName(dest)!, name = System.IO.Path.GetFileNameWithoutExtension(dest), ext = System.IO.Path.GetExtension(dest);
                for (int i = 2; File.Exists(dest); i++) dest = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
            }
            File.Copy(src, dest);
            return true;
        }
        private static string CachePath => System.IO.Path.Combine(Settings.Folder, "library2.json");   // v2: okładki i poprawione nazwy

        public static List<LibraryTrack> Tracks { get; private set; } = new List<LibraryTrack>();
        /// <summary>Lista się zmieniła (wołane z wątku tła).</summary>
        public static event Action Changed;
        public static bool Scanning { get; private set; }

        private static CancellationTokenSource _cts;

        public static void LoadCache()
        {
            try
            {
                if (File.Exists(CachePath))
                    Tracks = JsonSerializer.Deserialize<List<LibraryTrack>>(File.ReadAllText(CachePath)) ?? new List<LibraryTrack>();
            }
            catch (Exception ex) { Log.Error(ex); }
        }

        public static async Task ScanAsync(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            if (Tracks.Count == 0) LoadCache();      // skan z tła (śledzenie folderu) zanim ktoś otworzył okno
            _cts?.Cancel();
            var cts = _cts = new CancellationTokenSource();
            Scanning = true;
            Changed?.Invoke();
            try
            {
                var old = Tracks.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var files = await Task.Run(() => Directory
                    .EnumerateFiles(folder, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                    .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                    .ToList());

                // 1) od razu lista z nazw plików (albo z zapamiętanych tagów)
                var list = new List<LibraryTrack>();
                var todo = new List<LibraryTrack>();
                foreach (var f in files)
                {
                    long mod = File.GetLastWriteTimeUtc(f).Ticks;
                    if (old.TryGetValue(f, out var known) && known.Modified == mod)
                    {
                        if (known.Added == 0) known.Added = AddedTime(f);
                        list.Add(known);
                        continue;
                    }
                    var t = FromFileName(f);
                    t.Modified = mod;
                    t.Added = known != null && known.Added != 0 ? known.Added : AddedTime(f);
                    list.Add(t);
                    todo.Add(t);
                }
                Sort(list);
                Tracks = list;
                Changed?.Invoke();

                // 2) tagi w tle
                int done = 0;
                foreach (var t in todo)
                {
                    if (cts.IsCancellationRequested) return;
                    await ReadTags(t);
                    if (++done % 40 == 0) Changed?.Invoke();
                }
                Sort(list);
                Save();

                // 3) okładki (wbudowane albo z internetu) - też w tle, lista odświeża się na bieżąco
                await CoverFinder.FillAsync(list, () => Changed?.Invoke());
                if (!cts.IsCancellationRequested) Save();

                // 4) teksty piosenek (po okładkach - bo przy nich poprawiają się tytuły)
                await LyricsFinder.FillAsync(list, () => Changed?.Invoke());
                if (!cts.IsCancellationRequested) Save();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            finally
            {
                if (ReferenceEquals(cts, _cts)) Scanning = false;
                Changed?.Invoke();
            }
        }

        /// <summary>Data trafienia do folderu: data utworzenia pliku (kopia dostaje datę skopiowania).</summary>
        private static long AddedTime(string f)
        {
            try { return File.GetCreationTimeUtc(f).Ticks; } catch { return DateTime.UtcNow.Ticks; }
        }

        // ================= śledzenie folderów =================
        private static FileSystemWatcher _libWatch, _inboxWatch;
        private static Timer _rescan;
        private static readonly HashSet<string> _pendingImport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Folder biblioteki: nowe / usunięte pliki same pojawiają się na liście.
        /// Folder śledzony (np. Pobrane): nowa muzyka sama kopiuje się do biblioteki.
        /// Wołane przy starcie i po zmianie któregoś folderu.
        /// </summary>
        public static void StartWatching()
        {
            try
            {
                _libWatch?.Dispose();
                _inboxWatch?.Dispose();
                _libWatch = _inboxWatch = null;
                string lib = App.Config.LibraryFolder;
                if (!string.IsNullOrWhiteSpace(lib) && Directory.Exists(lib))
                {
                    _libWatch = new FileSystemWatcher(lib)
                    {
                        IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                    };
                    FileSystemEventHandler changed = (s, e) => { if (IsMusic(e.FullPath) || !System.IO.Path.HasExtension(e.FullPath)) ScheduleRescan(); };
                    _libWatch.Created += changed;
                    _libWatch.Deleted += changed;
                    _libWatch.Changed += changed;
                    _libWatch.Renamed += (s, e) => ScheduleRescan();
                    _libWatch.Error += (s, e) => ScheduleRescan();
                    _libWatch.EnableRaisingEvents = true;
                }
                string inbox = App.Config.WatchFolder;
                if (!string.IsNullOrWhiteSpace(inbox) && Directory.Exists(inbox) && !SameOrInside(inbox, lib))
                {
                    _inboxWatch = new FileSystemWatcher(inbox) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
                    // przeglądarki zapisują najpierw .crdownload / .part, a na końcu zmieniają nazwę na .mp3
                    _inboxWatch.Created += (s, e) => QueueImport(e.FullPath);
                    _inboxWatch.Renamed += (s, e) => QueueImport(e.FullPath);
                    _inboxWatch.EnableRaisingEvents = true;
                }
            }
            catch (Exception ex) { Log.Info("śledzenie folderu: " + ex.Message); }
        }

        private static bool SameOrInside(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                string fa = System.IO.Path.GetFullPath(a).TrimEnd('\') + "\", fb = System.IO.Path.GetFullPath(b).TrimEnd('\') + "\";
                return fa.StartsWith(fb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Kilka zmian naraz (kopiowanie albumu) = jeden skan, 2 s po ostatniej.</summary>
        private static void ScheduleRescan()
        {
            lock (_pendingImport)
            {
                _rescan ??= new Timer(state => _ = ScanAsync(App.Config.LibraryFolder), null, Timeout.Infinite, Timeout.Infinite);
                _rescan.Change(2000, Timeout.Infinite);
            }
        }

        private static async void QueueImport(string path)
        {
            if (!IsMusic(path)) return;
            lock (_pendingImport) { if (!_pendingImport.Add(path)) return; }
            try
            {
                // czekamy, aż plik się dopisze (pobieranie / kopiowanie) - najwyżej 3 minuty
                long lastSize = -1;
                for (int i = 0; i < 180; i++)
                {
                    await Task.Delay(1000);
                    if (!File.Exists(path)) return;
                    long size = new FileInfo(path).Length;
                    if (size > 0 && size == lastSize && CanOpen(path)) break;
                    lastSize = size;
                }
                if (!File.Exists(path)) return;
                int n = await ImportAsync(new[] { path });
                if (n > 0) Log.Info("śledzenie folderu: dodano " + path);
            }
            catch (Exception ex) { Log.Info("śledzenie folderu: " + ex.Message); }
            finally { lock (_pendingImport) _pendingImport.Remove(path); }
        }

        private static bool CanOpen(string path)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) return true; }
            catch { return false; }
        }

        private static void Sort(List<LibraryTrack> list) =>
            list.Sort((a, b) =>
            {
                int c = string.Compare(a.Artist, b.Artist, StringComparison.CurrentCultureIgnoreCase);
                if (c != 0) return c;
                c = string.Compare(a.Album, b.Album, StringComparison.CurrentCultureIgnoreCase);
                return c != 0 ? c : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });

        /// <summary>„Wykonawca - Tytuł.mp3” → wykonawca i tytuł. Czyści też śmieci z pobieraczek
        /// („YTDown.com_YouTube_Media_xxxx_…_128k”, „(Official Video)”, „lyric video” itp.).</summary>
        private static LibraryTrack FromFileName(string path)
        {
            string name = CleanName(System.IO.Path.GetFileNameWithoutExtension(path));
            string artist, title = name;
            bool fromFolder = false;
            int dash = name.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                artist = name.Substring(0, dash).Trim();
                title = name.Substring(dash + 3).Trim();
            }
            else
            {
                artist = new DirectoryInfo(System.IO.Path.GetDirectoryName(path) ?? "").Name;
                fromFolder = true;
            }
            return new LibraryTrack { Path = path, Title = title, Artist = artist, ArtistFromFolder = fromFolder };
        }

        private static string CleanName(string name)
        {
            string R(string input, string pattern, string with = " ") =>
                System.Text.RegularExpressions.Regex.Replace(input, pattern, with, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            string n = name;
            n = R(n, @"^(ytdown|y2mate|savefrom|ytmp3|mp3juices|loader|yt1s|ssyoutube|x2convert)(\.[a-z]{2,4})?[_\s\-.]*");
            n = R(n, @"youtube[_\s]*media[_\s]*[A-Za-z0-9_\-]{11}[_\s]");
            n = R(n, @"[\[\(][A-Za-z0-9_\-]{11}[\]\)]");                          // [dQw4w9WgXcQ]
            n = R(n.TrimEnd(), @"[_\s\-]*\(?\d{2,3}\s?kbps?\)?$|[_\s\-]+\d{2,3}k$", "");  // _128k, 320kbps
            n = R(n.TrimEnd(), @"[_\s\-]+\d{1,3}$", "");                            // _007 na końcu
            n = n.Replace('_', ' ');
            // nazwa „z-myślnikami-zamiast-spacji” (adres strony) → zwykłe słowa
            bool slug = !n.Contains(" - ") && n.Count(c => c == '-') >= 2 && n.Trim().Split(' ').Length <= 2;
            if (slug) n = n.Replace('-', ' ');
            n = R(n, @"[\(\[]?\b(official\s*(music\s*)?video|official\s*audio|official\s*lyric\s*video|lyrics?\s*video|lyrics?|visualizer|audio|video\s*clip|teledysk|hd|hq|4k)\b[\)\]]?");
            n = System.Text.RegularExpressions.Regex.Replace(n, @"\s{2,}", " ").Trim(' ', '-', '.', '_');
            n = System.Text.RegularExpressions.Regex.Replace(n, @"^\d{1,3}[\s.\-]+", "");   // numer utworu na początku
            if (n.Length == 0) n = name;
            // nazwa z adresu (same małe litery) - ładniej z wielkiej
            if (slug && n == n.ToLowerInvariant()) n = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(n);
            return n;
        }

        private static async Task ReadTags(LibraryTrack t)
        {
            try
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(t.Path);
                var m = await file.Properties.GetMusicPropertiesAsync();
                if (!string.IsNullOrWhiteSpace(m.Title)) { t.Title = m.Title.Trim(); t.Tagged = true; }
                string artist = !string.IsNullOrWhiteSpace(m.Artist) ? m.Artist : m.AlbumArtist;
                if (!string.IsNullOrWhiteSpace(artist)) { t.Artist = artist.Trim(); t.ArtistFromFolder = false; }
                if (!string.IsNullOrWhiteSpace(m.Album)) t.Album = m.Album.Trim();
                t.Duration = m.Duration.TotalSeconds;
            }
            catch { }
        }

        private static void Save()
        {
            try { File.WriteAllText(CachePath, JsonSerializer.Serialize(Tracks)); }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
