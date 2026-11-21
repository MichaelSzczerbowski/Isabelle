using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// Szybkie wyszukiwanie (Ctrl+Alt+Spacja): wpisujesz tytuł, wykonawcę, playlistę albo stację radiową
    /// i Enter od razu gra - bez otwierania Mojej muzyki.
    /// </summary>
    public sealed class QuickSearchWindow : Window
    {
        private static QuickSearchWindow _current;

        public static void Toggle()
        {
            if (_current != null) { _current.SafeClose(); return; }
            _current = new QuickSearchWindow();
            _current.Closed += (s, e) => _current = null;
            _current.Show();
            // otwarte kliknięciem w wyspę (która nie przejmuje fokusu) - wyciągamy okno na wierzch z klawiaturą
            ForceForeground(new System.Windows.Interop.WindowInteropHelper(_current).Handle);
            _current.Activate();
            _current._box.Focus();
            Keyboard.Focus(_current._box);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private static void ForceForeground(IntPtr hwnd)
        {
            try
            {
                uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
                if (fg != 0 && fg != me)
                {
                    AttachThreadInput(me, fg, true);
                    SetForegroundWindow(hwnd);
                    AttachThreadInput(me, fg, false);
                }
                else SetForegroundWindow(hwnd);
            }
            catch { }
        }

        private sealed class Result
        {
            public string Kind;            // track | playlist | station | smart
            public string Title, Subtitle, Badge;
            public LibraryTrack Track;
            public Playlist Playlist;
            public RadioStation Station;
            public string Smart;
            public string ArtUrl;          // okładka z internetu (utwory ze Spotify / sieci)
            public string Artist;          // wykonawca utworu z sieci
            public string SpotifyUri;      // połączone Spotify Premium - cała piosenka od razu
            public string PreviewUrl;      // bez Premium - 30-sekundowy fragment w Isabelle
            public Border Row;
            public Border Art;
            public TextBlock Enter;
        }

        private readonly TextBox _box = new TextBox();
        private readonly StackPanel _rows = new StackPanel();
        private readonly TextBlock _hint = new TextBlock();
        private readonly SolidColorBrush _accent;
        private List<Result> _results = new List<Result>();
        private int _sel;
        private int _searchId;
        private bool _closing;

        private QuickSearchWindow()
        {
            Title = L.T("Isabelle – szukaj", "Isabelle – search");
            Width = 452;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            _accent = new SolidColorBrush(App.Island?.AccentColor ?? Color.FromRgb(255, 92, 160));
            WindowStartupLocation = WindowStartupLocation.Manual;

            // na środku ekranu z kursorem (lekko powyżej środka, bo lista rośnie w dół)
            var isl = App.Island;
            var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            var src = isl != null ? PresentationSource.FromVisual(isl) : null;
            double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
            Left = (area.Left + area.Width / 2.0) * sx - Width / 2;
            Top = (area.Top + area.Height * 0.36) * sy;

            var card = new Border
            {
                Margin = new Thickness(16, 0, 16, 18), CornerRadius = new CornerRadius(26), Background = Brushes.Black,
                Padding = new Thickness(10, 8, 10, 10),
                Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Opacity = 0.45, Direction = 270, Color = Colors.Black },
            };
            var stack = new StackPanel();
            card.Child = stack;
            Content = card;

            // ---- pole wyszukiwania ----
            var field = new Grid { Margin = new Thickness(8, 4, 8, 6) };
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            field.Children.Add(new Path
            {
                Data = Geometry.Parse("M10,2 A8,8 0 1 1 9.99,2 Z M16,16 L22,22"), Stroke = _accent, StrokeThickness = 2.4,
                Width = 15, Height = 15, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            var boxHost = new Grid();
            Grid.SetColumn(boxHost, 1);
            _hint.Text = L.T("Szukaj muzyki i radia", "Search music and radio");
            _hint.FontSize = 15.5;
            _hint.Foreground = Hex("#59FFFFFF");
            _hint.VerticalAlignment = VerticalAlignment.Center;
            _hint.IsHitTestVisible = false;
            boxHost.Children.Add(_hint);
            _box.FontSize = 15.5;
            _box.Background = Brushes.Transparent;
            _box.Foreground = Brushes.White;
            _box.CaretBrush = Brushes.White;
            _box.BorderThickness = new Thickness(0);
            _box.VerticalContentAlignment = VerticalAlignment.Center;
            _box.Padding = new Thickness(-2, 3, 0, 3);
            boxHost.Children.Add(_box);
            field.Children.Add(boxHost);
            stack.Children.Add(field);
            stack.Children.Add(_rows);

            _box.TextChanged += (s, e) =>
            {
                _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                Search(_box.Text);
            };
            PreviewKeyDown += OnKey;
            Deactivated += (s, e) => SafeClose();

            // wejście: pigułka lekko „wyskakuje” na środku ekranu
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(0.94, 0.94);
            card.RenderTransform = scale;
            card.Opacity = 0;
            Loaded += (s, e) =>
            {
                var d = TimeSpan.FromMilliseconds(220);
                var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = back });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = back });
            };

            if (LibraryService.Tracks.Count == 0) LibraryService.LoadCache();
            WatchOutsideClicks();
            WarmUp();
            Search("");
        }

        // ---- zamykanie po kliknięciu poza okno (działa też, gdy okno nie dostało fokusu) ----
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private System.Windows.Threading.DispatcherTimer _outside;

        private void WatchOutsideClicks()
        {
            bool wasDown = true;     // klik, który otworzył okno, jeszcze trwa - liczymy dopiero nowy
            _outside = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _outside.Tick += (s, e) =>
            {
                bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
                if (down && !wasDown)
                {
                    var p = System.Windows.Forms.Cursor.Position;
                    var src = PresentationSource.FromVisual(this);
                    var m = src?.CompositionTarget?.TransformToDevice;
                    double sx = m?.M11 ?? 1, sy = m?.M22 ?? 1;
                    // sama karta (bez marginesu na cień)
                    var card = (FrameworkElement)Content;
                    double l = (Left + card.Margin.Left) * sx, t = (Top + card.Margin.Top) * sy;
                    double r = l + card.ActualWidth * sx, b = t + card.ActualHeight * sy;
                    if (p.X < l || p.X > r || p.Y < t || p.Y > b) SafeClose();
                }
                wasDown = down;
            };
            _outside.Start();
            Closed += (s, e) => _outside.Stop();
        }

        private static bool? _hasSpotify;
        /// <summary>Czy jest zainstalowany Spotify (obsługuje linki spotify:).</summary>
        private static bool HasSpotify
        {
            get
            {
                if (_hasSpotify.HasValue) return _hasSpotify.Value;
                try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("spotify"); _hasSpotify = k != null; }
                catch { _hasSpotify = false; }
                return _hasSpotify.Value;
            }
        }

        private static readonly System.Net.Http.HttpClient Http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        /// <summary>Utwory z internetu (katalog iTunes - te same piosenki są w Spotify i na YouTube).</summary>
        // ostatnie wyniki z sieci - cofnięcie litery albo ponowne wpisanie pokazuje je od razu
        private static readonly Dictionary<string, List<Result>> SongCache = new Dictionary<string, List<Result>>(StringComparer.OrdinalIgnoreCase);

        private static List<Result> CloneResults(List<Result> data) =>
            data.Select(d => new Result
            {
                Kind = d.Kind, Title = d.Title, Subtitle = d.Subtitle, Artist = d.Artist, ArtUrl = d.ArtUrl,
                SpotifyUri = d.SpotifyUri, PreviewUrl = d.PreviewUrl,
            }).ToList();

        /// <summary>Połączenie z katalogiem nawiązujemy już przy otwarciu okna - pierwsze wyniki przychodzą szybciej.</summary>
        private static void WarmUp()
        {
            _ = Task.Run(async () =>
            {
                try { using var r = await Http.GetAsync("https://itunes.apple.com/search?term=a&limit=1&media=music", System.Net.Http.HttpCompletionOption.ResponseHeadersRead); }
                catch { }
            });
        }

        /// <summary>
        /// Utwory z sieci: z połączonym Spotify Premium - prosto ze Spotify (cała piosenka od razu),
        /// bez niego - katalog iTunes z 30-sekundowymi fragmentami (te same piosenki co w Spotify i na YouTube).
        /// </summary>
        private static async Task<List<Result>> OnlineSongs(string q)
        {
            bool spotify = SpotifyService.CanPlay;
            string cacheKey = (spotify ? "s:" : "i:") + q;
            lock (SongCache) { if (SongCache.TryGetValue(cacheKey, out var hit)) return CloneResults(hit); }
            var list = new List<Result>();
            if (spotify)
            {
                foreach (var t in await SpotifyService.SearchAsync(q, 3))
                    list.Add(new Result { Kind = "web", Title = t.Title, Artist = t.Artist, Subtitle = t.Artist + " · Spotify", ArtUrl = t.Art, SpotifyUri = t.Uri });
            }
            else
            {
                string url = "https://itunes.apple.com/search?media=music&entity=song&limit=6&term=" + Uri.EscapeDataString(q);
                using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync(url));
                if (doc.RootElement.TryGetProperty("results", out var arr))
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var it in arr.EnumerateArray())
                    {
                        string title = it.TryGetProperty("trackName", out var tn) ? tn.GetString() : null;
                        string artist = it.TryGetProperty("artistName", out var an) ? an.GetString() : null;
                        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || !seen.Add(artist + "|" + title)) continue;
                        string art = it.TryGetProperty("artworkUrl100", out var au) ? au.GetString() : null;
                        string preview = it.TryGetProperty("previewUrl", out var pu) ? pu.GetString() : null;
                        list.Add(new Result
                        {
                            Kind = "web", Title = title, Artist = artist, ArtUrl = art, PreviewUrl = preview,
                            Subtitle = artist + (preview != null ? " · " + L.T("fragment 30 s", "30 s preview") : ""),
                        });
                        if (list.Count >= 3) break;
                    }
                }
            }
            lock (SongCache)
            {
                if (SongCache.Count > 200) SongCache.Clear();
                SongCache[cacheKey] = CloneResults(list);
            }
            return list;
        }

        /// <summary>Cała piosenka w Spotify / YouTube Music (przycisk obok fragmentu).</summary>
        private static void OpenFull(Result r)
        {
            string term = Uri.EscapeDataString(r.Artist + " " + r.Title);
            string link = HasSpotify ? "spotify:search:" + term : "https://music.youtube.com/search?q=" + term;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true }); } catch { }
        }

        private void SafeClose()
        {
            if (_closing) return;
            _closing = true;
            try { Close(); } catch { }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape: e.Handled = true; SafeClose(); break;
                case Key.Down: e.Handled = true; Select(_sel + 1); break;
                case Key.Up: e.Handled = true; Select(_sel - 1); break;
                case Key.Tab: e.Handled = true; Select(_sel + (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1)); break;
                case Key.Enter:
                    e.Handled = true;
                    if (_sel >= 0 && _sel < _results.Count) Run(_results[_sel]);
                    break;
            }
        }

        // ================= wyszukiwanie =================
        private async void Search(string text)
        {
            int id = ++_searchId;
            string q = (text ?? "").Trim();
            var list = new List<Result>();
            if (q.Length == 0)
            {
                // puste pole: szybkie skróty
                list.Add(new Result { Kind = "smart", Smart = "shuffle", Title = L.T("Losowo z całej biblioteki", "Shuffle my library"), Subtitle = L.T("Moja muzyka", "My music"), Badge = "" });
                foreach (var st in (App.Config.RadioRecent ?? new List<RadioStation>()).Take(3))
                    list.Add(StationResult(st, L.T("Ostatnio słuchane", "Recently played")));
                ShowResults(list);
                return;
            }

            string[] words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Match(string hay) => words.All(w => hay.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0);

            // 1) utwory z biblioteki (najpierw dopasowanie w tytule)
            var tracks = LibraryService.Tracks.ToList()
                .Where(t => Match(t.Title + " " + t.Artist + " " + t.Album))
                .OrderByDescending(t => t.Title.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
                .ThenByDescending(t => PlayStats.Plays(t.Path))
                .Take(3);
            foreach (var t in tracks)
                list.Add(new Result { Kind = "track", Track = t, Title = t.Title, Subtitle = t.Artist, Badge = L.T("Utwór", "Track") });

            // 2) playlisty
            foreach (var pl in PlaylistStore.Playlists.Where(p => Match(p.Name)).Take(1))
                list.Add(new Result { Kind = "playlist", Playlist = pl, Title = pl.Name, Subtitle = string.Format(L.T("{0} utw.", "{0} tracks"), pl.Paths.Count), Badge = L.T("Playlista", "Playlist") });

            // 3) stacje: ulubione i ostatnie (od razu), potem baza radia z internetu
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var st in (App.Config.RadioFavorites ?? new List<RadioStation>()).Concat(App.Config.RadioRecent ?? new List<RadioStation>()))
            {
                if (list.Count(r => r.Kind == "station") >= 2) break;
                if (!Match(st.Name) || !seen.Add(st.Url)) continue;
                list.Add(StationResult(st, LocalPlayer.IsFavorite(st) ? L.T("Ulubiona stacja", "Favorite station") : L.T("Ostatnio słuchane", "Recently played")));
            }
            // od razu widać, że szukamy dalej w sieci - nikt nie uzna, że piosenki nie ma
            _loading = q.Length >= 2;
            ShowResults(list);

            if (q.Length < 2) return;
            await Task.Delay(140);           // krótka chwila, gdy jeszcze piszesz
            if (id != _searchId) return;
            try
            {
                var songsTask = OnlineSongs(q);
                var radioTask = RadioService.Search(q);
                // utwory z sieci (Spotify / YouTube) - po wynikach z biblioteki, przed radiem
                try
                {
                    var songs = await songsTask;
                    if (id != _searchId || _closing) return;
                    var local = new HashSet<string>(list.Where(r => r.Track != null).Select(r => r.Track.Artist + "|" + r.Track.Title), StringComparer.OrdinalIgnoreCase);
                    int at = list.FindIndex(r => r.Kind == "station");
                    if (at < 0) at = list.Count;
                    foreach (var song in songs)
                    {
                        if (local.Contains(song.Artist + "|" + song.Title)) continue;
                        list.Insert(at++, song);
                    }
                    if (songs.Count > 0) ShowResults(list, keepSelection: true);
                }
                catch (Exception ex) { Log.Info("szukaj utworów: " + ex.Message); }
                var found = await radioTask;
                if (id != _searchId || _closing) return;
                int added = 0;
                foreach (var st in found)
                {
                    if (added >= 2) break;
                    if (!st.LastCheckOk || !seen.Add(st.Url)) continue;
                    list.Add(StationResult(st, string.IsNullOrWhiteSpace(st.Country) ? L.T("Radio", "Radio") : st.Country));
                    added++;
                }
            }
            catch (Exception ex) { Log.Info("szukaj: " + ex.Message); }
            finally
            {
                if (id == _searchId && !_closing)
                {
                    _loading = false;
                    ShowResults(list, keepSelection: true);
                }
            }
        }

        private static Result StationResult(RadioStation st, string sub) =>
            new Result { Kind = "station", Station = st, Title = st.Name, Subtitle = sub, Badge = L.T("Radio", "Radio") };

        private bool _loading;

        /// <summary>Wiersz „Szukam…” z pulsującymi kropkami (pod wynikami, nie da się go wybrać).</summary>
        private FrameworkElement LoadingRow()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 5, 10, 5) };
            for (int i = 0; i < 3; i++)
            {
                var dot = new Ellipse { Width = 5, Height = 5, Fill = _accent, Margin = new Thickness(0, 0, 4, 0), Opacity = 0.3, VerticalAlignment = VerticalAlignment.Center };
                var anim = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(420)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 140) };
                dot.BeginAnimation(OpacityProperty, anim);
                panel.Children.Add(dot);
            }
            panel.Children.Add(new TextBlock
            {
                Text = L.T("Szukam w Spotify i radiu…", "Searching Spotify and radio…"), FontSize = 12, Foreground = Hex("#73FFFFFF"),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            return panel;
        }

        private void ShowResults(List<Result> list, bool keepSelection = false)
        {
            int old = _sel;
            _results = list;
            _rows.Children.Clear();
            if (list.Count == 0 && _loading)
            {
                _rows.Children.Add(LoadingRow());
                _sel = -1;
                return;
            }
            if (list.Count == 0)
            {
                _rows.Children.Add(new TextBlock
                {
                    Text = L.T("Nic nie znaleziono", "Nothing found"), FontSize = 12.5, Foreground = Hex("#73FFFFFF"),
                    Margin = new Thickness(10, 2, 10, 6),
                });
                _sel = -1;
                return;
            }
            for (int i = 0; i < list.Count; i++) _rows.Children.Add(MakeRow(list[i], i));
            if (_loading) _rows.Children.Add(LoadingRow());
            Select(keepSelection ? Math.Max(0, Math.Min(old, list.Count - 1)) : 0);
        }

        /// <summary>Wiersz bez ramek i etykiet: miniatura, tytuł, podpis; przy zaznaczonym ↵.</summary>
        private Border MakeRow(Result r, int index)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // miniatura: okładka / logo stacji (okrągłe) / ikonka
            bool round = r.Kind == "station";
            var art = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(round ? 16 : 8), Background = Hex("#1FFFFFFF"),
                Margin = new Thickness(0, 0, 11, 0), ClipToBounds = true,
            };
            string icon = r.Kind switch
            {
                "playlist" => GlassMenu.ListIcon,
                "station" => "M4,9 H20 V20 H4 Z M8,9 L17,4 M8,15 A2,2 0 1 1 7.99,15 Z M13,13 H17 M13,16 H17",
                "smart" => GlassMenu.ShuffleIcon,
                _ => "M9,18 V5 L20,3 V16 M9,18 A3,3 0 1 1 8.99,18 Z M20,16 A3,3 0 1 1 19.99,16 Z",
            };
            art.Child = new Path
            {
                Data = Geometry.Parse(icon), Stroke = Hex("#B3FFFFFF"), StrokeThickness = 1.6, Width = 15, Height = 15, Stretch = Stretch.Uniform,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            r.Art = art;
            if (r.Track != null && !string.IsNullOrEmpty(r.Track.CoverPath)) SetArt(art, r.Track.CoverPath);
            else if (!string.IsNullOrEmpty(r.ArtUrl)) SetArtUrl(art, r.ArtUrl);
            else if (r.Station != null) _ = LoadLogo(r, art);
            g.Children.Add(art);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = r.Title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrWhiteSpace(r.Subtitle))
                texts.Children.Add(new TextBlock { Text = r.Subtitle, FontSize = 11.5, Foreground = Hex("#80FFFFFF"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (r.Kind == "web" && r.SpotifyUri == null)
            {
                // fragment gra w Isabelle - całą piosenkę otwiera mały przycisk
                var full = new Border
                {
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 9, 4), Background = Hex("#1FFFFFFF"), Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = L.T("Cała piosenka", "Full song"),
                    Child = new TextBlock { Text = (HasSpotify ? "Spotify" : "YouTube") + " ↗", FontSize = 11, Foreground = Hex("#CCFFFFFF") },
                };
                full.MouseEnter += (s, e) => full.Background = Hex("#33FFFFFF");
                full.MouseLeave += (s, e) => full.Background = Hex("#1FFFFFFF");
                full.MouseLeftButtonDown += (s, e) => e.Handled = true;
                full.MouseLeftButtonUp += (s, e) => { e.Handled = true; OpenFull(r); SafeClose(); };
                right.Children.Add(full);
            }
            var enter = new TextBlock { Text = "↵", FontSize = 13, Foreground = Hex("#66FFFFFF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0), Opacity = 0 };
            right.Children.Add(enter);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);
            r.Enter = enter;

            var row = new Border { Child = g, CornerRadius = new CornerRadius(15), Padding = new Thickness(7, 6, 10, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (s, e) => Select(index);
            row.MouseLeftButtonDown += (s, e) => e.Handled = true;
            row.MouseLeftButtonUp += (s, e) => Run(r);
            r.Row = row;
            return row;
        }

        private void Select(int i)
        {
            if (_results.Count == 0) { _sel = -1; return; }
            _sel = (i % _results.Count + _results.Count) % _results.Count;
            for (int k = 0; k < _results.Count; k++)
            {
                bool on = k == _sel;
                if (_results[k].Row != null) _results[k].Row.Background = on ? Hex("#17FFFFFF") : Brushes.Transparent;
                if (_results[k].Enter != null) _results[k].Enter.Opacity = on ? 1 : 0;
            }
        }

        private static void SetArt(Border art, string path)
        {
            try
            {
                if (!System.IO.File.Exists(path)) return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 80;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                art.Child = null;
                art.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { }
        }

        private static void SetArtUrl(Border art, string url)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(url);
                bmp.DecodePixelWidth = 80;
                bmp.EndInit();
                art.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                bmp.DownloadCompleted += (s, e) => art.Child = null;
            }
            catch { }
        }

        private async Task LoadLogo(Result r, Border art)
        {
            try
            {
                string path = await RadioArt.LogoFile(r.Station);
                if (path != null && !_closing) SetArt(art, path);
            }
            catch { }
        }

        // ================= odtwarzanie =================
        private void Run(Result r)
        {
            try
            {
                switch (r.Kind)
                {
                    case "track":
                        // cała biblioteka od wybranego utworu - muzyka gra dalej, a nie tylko jedna piosenka
                        var all = LibraryService.Tracks.ToList();
                        int i = all.IndexOf(r.Track);
                        if (i < 0) { all = new List<LibraryTrack> { r.Track }; i = 0; }
                        LocalPlayer.PlayTracks(all, i, false);
                        break;
                    case "playlist":
                        var byPath = LibraryService.Tracks.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                        var tracks = r.Playlist.Paths.Where(byPath.ContainsKey).Select(p => byPath[p]).ToList();
                        if (tracks.Count > 0) LocalPlayer.PlayTracks(tracks, 0, false);
                        break;
                    case "station":
                        LocalPlayer.PlayStation(r.Station);
                        break;
                    case "web":
                        if (r.SpotifyUri != null) _ = PlaySpotify(r);                                  // cała piosenka w tle
                        else if (r.PreviewUrl != null) LocalPlayer.PlayPreview(r.Title, r.Artist, r.PreviewUrl, r.ArtUrl);
                        else OpenFull(r);
                        break;
                    case "smart":
                        var lib = LibraryService.Tracks.ToList();
                        if (lib.Count > 0) LocalPlayer.PlayTracks(lib, new Random().Next(lib.Count), true);
                        else LibraryWindow.ShowWindow();
                        break;
                }
            }
            catch (Exception ex) { Log.Error(ex); }
            SafeClose();
        }

        private static async Task PlaySpotify(Result r)
        {
            string err = await SpotifyService.PlayAsync(r.SpotifyUri);
            if (err == null) return;
            Log.Info("spotify: " + err);
            OpenFull(r);       // nie wyszło (np. brak Spotify) - otwieramy jak dawniej
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
