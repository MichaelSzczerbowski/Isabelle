using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace IslandPlayer
{
    /// <summary>
    /// Porządkowanie biblioteki: duplikaty (zostaje najlepsza jakość, reszta do Kosza), brakujące tytuły
    /// i wykonawcy (uzupełniane z katalogu muzyki), dziwne nazwy plików i podsumowanie jakości.
    /// </summary>
    public sealed class LibraryCleanupWindow : Window
    {
        private static LibraryCleanupWindow _current;

        public static void ShowWindow(Window owner)
        {
            if (_current != null) { _current.Activate(); return; }
            _current = new LibraryCleanupWindow { Owner = owner };
            _current.Closed += (s, e) => _current = null;
            _current.Show();
        }

        private sealed class Report
        {
            public List<List<LibraryTrack>> Duplicates = new List<List<LibraryTrack>>();
            public List<LibraryTrack> Missing = new List<LibraryTrack>();
            public List<(LibraryTrack t, string newName)> Rename = new List<(LibraryTrack, string)>();
            public Dictionary<string, int> Formats = new Dictionary<string, int>();
            public List<LibraryTrack> LowQuality = new List<LibraryTrack>();
            public int Total;
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private readonly StackPanel _body = new StackPanel();
        private readonly SolidColorBrush _accent;
        private Report _report;
        private bool _busy;

        private LibraryCleanupWindow()
        {
            Title = L.T("Porządkowanie biblioteki", "Tidy up library");
            Width = 620;
            Height = 720;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            var accentColor = App.Island?.AccentColor ?? Color.FromRgb(255, 92, 160);
            _accent = new SolidColorBrush(accentColor);

            var card = new Border
            {
                Margin = new Thickness(18), CornerRadius = new CornerRadius(26), BorderBrush = Hex("#2EFFFFFF"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.5, Direction = 270, Color = Colors.Black },
            };
            var root = new Grid();
            var clip = new RectangleGeometry { RadiusX = 25, RadiusY = 25 };
            root.Clip = clip;
            root.SizeChanged += (s, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            var glass = new GlassBackground();
            glass.SetColors(App.Island?.CoverImage, accentColor);
            root.Children.Add(glass);
            card.Child = root;
            Content = card;

            var layout = new Grid { Margin = new Thickness(28, 22, 28, 24) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(layout);

            var head = new Grid { Background = Brushes.Transparent };
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = L.T("Porządkowanie biblioteki", "Tidy up library"), FontSize = 24, FontWeight = FontWeights.Bold });
            titles.Children.Add(new TextBlock { Text = L.T("Duplikaty, brakujące informacje, nazwy plików i jakość", "Duplicates, missing info, file names and quality"), FontSize = 12.5, Foreground = Hex("#99FFFFFF"), Margin = new Thickness(0, 2, 0, 0) });
            head.Children.Add(titles);
            var close = new TextBlock { Text = "✕", FontSize = 16, Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Cursor = Cursors.Hand, Padding = new Thickness(8, 2, 2, 2) };
            close.MouseLeftButtonDown += (s, e) => { e.Handled = true; Close(); };
            head.Children.Add(close);
            head.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            layout.Children.Add(head);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _body, Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Loaded += async (s, e) => await Analyze();
        }

        // ================= analiza =================
        private async Task Analyze()
        {
            _body.Children.Clear();
            _body.Children.Add(Note(L.T("Sprawdzam bibliotekę…", "Checking your library…")));
            if (LibraryService.Tracks.Count == 0) LibraryService.LoadCache();
            var tracks = LibraryService.Tracks.ToList();
            _report = await Task.Run(() => Build(tracks));
            Render();
        }

        private static Report Build(List<LibraryTrack> tracks)
        {
            var r = new Report { Total = tracks.Count };
            // duplikaty: ten sam wykonawca + tytuł (bez dopisków) i podobna długość
            foreach (var g in tracks.GroupBy(t => Norm(ListeningHistory.MainArtist(t.Artist)) + "|" + Norm(StripExtras(t.Title))))
            {
                if (g.Key.StartsWith("|") || g.Count() < 2) continue;
                var list = g.ToList();
                var group = new List<LibraryTrack> { list[0] };
                foreach (var t in list.Skip(1))
                    if (list[0].Duration <= 0 || t.Duration <= 0 || Math.Abs(list[0].Duration - t.Duration) <= 4) group.Add(t);
                if (group.Count > 1) r.Duplicates.Add(group.OrderByDescending(Quality).ToList());
            }
            foreach (var t in tracks)
            {
                if (!t.Tagged || t.ArtistFromFolder) r.Missing.Add(t);
                string name = Path.GetFileNameWithoutExtension(t.Path);
                string wanted = SafeName(t.Artist + " - " + t.Title);
                // nazwy ze śmieciami (y2mate, _128k, (Official Video), podkreślniki…) → „Wykonawca - Tytuł”
                if (!t.ArtistFromFolder && wanted.Length > 3 && !string.Equals(name, wanted, StringComparison.CurrentCultureIgnoreCase) && LibraryService.CleanName(name) != name)
                    r.Rename.Add((t, wanted + Path.GetExtension(t.Path)));
                string ext = Path.GetExtension(t.Path).TrimStart('.').ToUpperInvariant();
                r.Formats[ext] = r.Formats.TryGetValue(ext, out var n) ? n + 1 : 1;
                if (ext == "MP3" && Kbps(t) is double k && k > 0 && k < 150) r.LowQuality.Add(t);
            }
            return r;
        }

        private static double Quality(LibraryTrack t)
        {
            string ext = Path.GetExtension(t.Path).ToLowerInvariant();
            double rank = ext is ".flac" or ".wav" or ".alac" ? 3000 : ext is ".m4a" or ".aac" ? 1000 : 0;
            return rank + (Kbps(t) ?? 0);
        }

        private static double? Kbps(LibraryTrack t)
        {
            try { return t.Duration > 0 ? new FileInfo(t.Path).Length * 8 / 1000.0 / t.Duration : (double?)null; } catch { return null; }
        }

        private static string StripExtras(string title)
        {
            string s = System.Text.RegularExpressions.Regex.Replace(title ?? "", @"\s*[\(\[](feat|ft|with|prod|remaster|official|lyric|audio|video)[^\)\]]*[\)\]]", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s+-\s+(remaster(ed)?|radio edit|single version).*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static string SafeName(string s)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            return s.Trim().TrimEnd('.');
        }

        // ================= widok =================
        private void Render()
        {
            var r = _report;
            _body.Children.Clear();
            if (r.Total == 0) { _body.Children.Add(Note(L.T("Biblioteka jest pusta – dodaj muzykę w Mojej muzyce.", "Your library is empty – add music in My music."))); return; }

            int extra = r.Duplicates.Sum(g => g.Count - 1);
            Section(L.T("Duplikaty", "Duplicates"),
                extra == 0 ? L.T("Brak duplikatów 👌", "No duplicates 👌")
                           : string.Format(L.T("{0} niepotrzebnych kopii. Zostanie wersja w najlepszej jakości, reszta trafi do Kosza (można przywrócić).", "{0} unneeded copies. The best quality version stays, the rest goes to the Recycle Bin (restorable)."), extra),
                r.Duplicates.Take(5).Select(g => g[0].Artist + " – " + g[0].Title + "  ×" + g.Count),
                extra > 0 ? L.T("Usuń duplikaty", "Remove duplicates") : null, RemoveDuplicates);

            Section(L.T("Brakujące informacje", "Missing info"),
                r.Missing.Count == 0 ? L.T("Wszystkie utwory mają tytuł i wykonawcę 👌", "Every track has a title and artist 👌")
                                     : string.Format(L.T("{0} utworów bez tytułu lub wykonawcy w pliku. Isabelle poszuka ich w katalogu muzyki i zapisze w plikach.", "{0} tracks without a title or artist. Isabelle will look them up and save them in the files."), r.Missing.Count),
                r.Missing.Take(5).Select(t => Path.GetFileName(t.Path)),
                r.Missing.Count > 0 ? L.T("Uzupełnij z internetu", "Fill in from the web") : null, FillMissing);

            Section(L.T("Nazwy plików", "File names"),
                r.Rename.Count == 0 ? L.T("Nazwy plików są w porządku 👌", "File names look fine 👌")
                                    : string.Format(L.T("{0} plików ma dziwne nazwy (np. y2mate, _128k, „Official Video”). Zmienię je na „Wykonawca - Tytuł”.", "{0} files have messy names (e.g. y2mate, _128k, “Official Video”). They'll become “Artist - Title”."), r.Rename.Count),
                r.Rename.Take(5).Select(x => Path.GetFileName(x.t.Path) + "  →  " + x.newName),
                r.Rename.Count > 0 ? L.T("Popraw nazwy", "Fix names") : null, RenameFiles);

            var formats = string.Join("   ", r.Formats.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value));
            int hq = r.Formats.Where(kv => kv.Key is "FLAC" or "WAV" or "ALAC").Sum(kv => kv.Value);
            Section(L.T("Jakość", "Quality"),
                string.Format(L.T("{0} utworów · {1} w bezstratnej jakości (FLAC/WAV)", "{0} tracks · {1} lossless (FLAC/WAV)"), r.Total, hq) + "\n" + formats +
                (r.LowQuality.Count > 0 ? "\n" + string.Format(L.T("{0} plików MP3 w słabej jakości (poniżej 150 kb/s) – warto je podmienić:", "{0} low quality MP3s (under 150 kbps) – worth replacing:"), r.LowQuality.Count) : ""),
                r.LowQuality.Take(5).Select(t => t.Artist + " – " + t.Title + "  (" + Math.Round(Kbps(t) ?? 0) + " kb/s)"), null, null);
        }

        private void Section(string title, string text, IEnumerable<string> samples, string action, Func<Task> run)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 12.5, Foreground = Hex("#CCFFFFFF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
            foreach (var line in samples ?? Enumerable.Empty<string>())
                sp.Children.Add(new TextBlock { Text = "• " + line, FontSize = 12, Foreground = Hex("#99FFFFFF"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (action != null)
            {
                var btn = new Border
                {
                    CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 7, 16, 8), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                    Background = _accent, Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = action, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black },
                };
                btn.MouseLeftButtonDown += (s, e) => e.Handled = true;
                btn.MouseLeftButtonUp += async (s, e) =>
                {
                    if (_busy) return;
                    _busy = true;
                    ((TextBlock)btn.Child).Text = L.T("Pracuję…", "Working…");
                    try { await run(); } catch (Exception ex) { Log.Error(ex); }
                    // świeży skan i nowy raport
                    try { await LibraryService.ScanAsync(App.Config.LibraryFolder); } catch { }
                    _busy = false;
                    await Analyze();
                };
                sp.Children.Add(btn);
            }
            _body.Children.Add(new Border { Background = Hex("#26000000"), CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 14, 18, 16), Margin = new Thickness(0, 0, 0, 10), Child = sp });
        }

        private Border Note(string text) => new Border
        {
            Background = Hex("#26000000"), CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 14, 18, 14),
            Child = new TextBlock { Text = text, FontSize = 13.5, TextWrapping = TextWrapping.Wrap },
        };

        // ================= akcje =================
        private async Task RemoveDuplicates()
        {
            var extra = _report.Duplicates.SelectMany(g => g.Skip(1)).Select(t => t.Path).ToList();
            if (extra.Count == 0) return;
            var ok = MessageBox.Show(this, string.Format(L.T("Przenieść {0} kopii do Kosza? Najlepsza wersja każdego utworu zostaje.", "Move {0} copies to the Recycle Bin? The best version of each track stays."), extra.Count),
                                     L.T("Duplikaty", "Duplicates"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;
            await Task.Run(() => { foreach (var p in extra) ToRecycleBin(p); });
        }

        private async Task FillMissing()
        {
            int fixedCount = 0;
            foreach (var t in _report.Missing.Take(200).ToList())
            {
                try
                {
                    string term = LibraryService.CleanName(Path.GetFileNameWithoutExtension(t.Path));
                    if (term.Length < 3) continue;
                    using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://itunes.apple.com/search?media=music&entity=song&limit=3&term=" + Uri.EscapeDataString(term)));
                    var results = doc.RootElement.GetProperty("results");
                    if (results.GetArrayLength() == 0) continue;
                    var it = results[0];
                    string title = it.GetProperty("trackName").GetString(), artist = it.GetProperty("artistName").GetString();
                    string album = it.TryGetProperty("collectionName", out var c) ? c.GetString() : null;
                    // bezpiecznik: tytuł z katalogu musi pasować do nazwy pliku
                    if (!Norm(term).Contains(Norm(StripExtras(title)))) continue;
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(t.Path);
                    var m = await file.Properties.GetMusicPropertiesAsync();
                    m.Title = title;
                    m.Artist = artist;
                    if (string.IsNullOrWhiteSpace(m.Album) && !string.IsNullOrWhiteSpace(album)) m.Album = album;
                    await m.SavePropertiesAsync();
                    fixedCount++;
                }
                catch (Exception ex) { Log.Info("porządki tagi: " + ex.Message); }
                await Task.Delay(150);
            }
            Log.Info("porządki: uzupełniono " + fixedCount);
        }

        private async Task RenameFiles()
        {
            await Task.Run(() =>
            {
                foreach (var (t, newName) in _report.Rename)
                {
                    try
                    {
                        string target = Path.Combine(Path.GetDirectoryName(t.Path) ?? "", newName);
                        if (File.Exists(target) || !File.Exists(t.Path)) continue;
                        File.Move(t.Path, target);
                        // okładka / tekst zapisane obok pliku przenoszą się razem z nim
                        foreach (var side in new[] { ".lrc", ".jpg" })
                        {
                            string a = Path.ChangeExtension(t.Path, side), b = Path.ChangeExtension(target, side);
                            if (File.Exists(a) && !File.Exists(b)) File.Move(a, b);
                        }
                    }
                    catch (Exception ex) { Log.Info("porządki nazwa: " + ex.Message); }
                }
            });
        }

        // ---- Kosz ----
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        private static void ToRecycleBin(string path)
        {
            const uint FO_DELETE = 3;
            const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_SILENT = 0x4, FOF_NOERRORUI = 0x400;
            try
            {
                var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI };
                SHFileOperation(ref op);
            }
            catch (Exception ex) { Log.Info("kosz: " + ex.Message); }
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
