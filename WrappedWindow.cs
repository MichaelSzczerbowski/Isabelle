using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>
    /// Isabelle Wrapped: statystyki słuchania ze wszystkich odtwarzaczy (tydzień / miesiąc / rok / od początku)
    /// i nowości od najczęściej słuchanych wykonawców.
    /// </summary>
    public sealed class WrappedWindow : Window
    {
        private static WrappedWindow _current;

        public static void ShowWindow()
        {
            if (_current != null) { _current.Activate(); return; }
            _current = new WrappedWindow();
            _current.Closed += (s, e) => _current = null;
            _current.Show();
            _current.Activate();
        }

        private readonly StackPanel _body = new StackPanel();
        private readonly WrapPanel _chips = new WrapPanel { Margin = new Thickness(0, 14, 0, 6) };
        private readonly SolidColorBrush _accent;
        private bool _closing;
        // po włączeniu imprezy Windows może pokazać okno zapory - wtedy nie zamykamy
        private DateTime _ignoreDeactivateUntil = DateTime.MinValue;
        private string _period = "week";

        private WrappedWindow()
        {
            Title = "Isabelle Wrapped";
            Width = 600;
            Height = 780;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            var accentColor = App.Island?.AccentColor ?? Color.FromRgb(255, 92, 160);
            _accent = new SolidColorBrush(accentColor);

            var card = new Border
            {
                Margin = new Thickness(18), CornerRadius = new CornerRadius(26),
                BorderBrush = Hex("#2EFFFFFF"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.5, Direction = 270, Color = Colors.Black },
            };
            var root = new Grid();
            var glass = new GlassBackground();
            glass.SetColors(App.Island?.CoverImage, accentColor);
            root.Children.Add(glass);
            // zaokrąglone rogi także dla tła
            // zaokrąglone rogi dla całej zawartości (bez kwadratowych brzegów pod spodem)
            var clip = new RectangleGeometry { RadiusX = 25, RadiusY = 25 };
            root.Clip = clip;
            root.SizeChanged += (s, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            card.Child = root;
            Content = card;

            var layout = new Grid { Margin = new Thickness(28, 22, 28, 24) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(layout);

            // ---- nagłówek ----
            var head = new StackPanel();
            var titleRow = new Grid();
            titleRow.Children.Add(new TextBlock { Text = "Isabelle Wrapped", FontSize = 28, FontWeight = FontWeights.Bold });
            var close = new TextBlock { Text = "✕", FontSize = 16, Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Padding = new Thickness(8, 2, 2, 2) };
            // przeciąganie okna przechwytywało mysz i ✕ nie działał - zamykamy już przy wciśnięciu
            close.MouseLeftButtonDown += (s, e) => { e.Handled = true; Close(); };
            titleRow.Children.Add(close);
            titleRow.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
            head.Children.Add(titleRow);
            head.Children.Add(new TextBlock { Text = L.T("Twoja muzyka ze wszystkich odtwarzaczy – Spotify, przeglądarka, Moja muzyka i radio", "Your music from every player – Spotify, browser, My music and radio"), FontSize = 12.5, Foreground = Hex("#99FFFFFF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            head.Children.Add(_chips);
            layout.Children.Add(head);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _body, Margin = new Thickness(0, 6, 0, 0) };
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);

            // kliknięcie poza okno = zamknij (jak menu)
            Deactivated += (s, e) =>
            {
                if (_closing || DateTime.UtcNow < _ignoreDeactivateUntil) return;
                _closing = true;
                try { Close(); } catch { }
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Render();
        }

        private void Render()
        {
            _chips.Children.Clear();
            foreach (var (key, pl, en) in new[] { ("week", "7 dni", "7 days"), ("month", "30 dni", "30 days"), ("year", "Rok", "Year"), ("all", "Od początku", "All time") })
            {
                string k = key;
                _chips.Children.Add(Chip(L.T(pl, en), k == _period, () => { _period = k; Render(); }));
            }

            _body.Children.Clear();
            DateTime since = _period switch
            {
                "week" => DateTime.UtcNow.AddDays(-7),
                "month" => DateTime.UtcNow.AddDays(-30),
                "year" => DateTime.UtcNow.AddDays(-365),
                _ => DateTime.MinValue,
            };
            var items = ListeningHistory.Since(since);

            if (items.Count == 0)
            {
                _body.Children.Add(Card(new TextBlock
                {
                    Text = L.T("Tu pojawią się Twoje statystyki. Po prostu słuchaj muzyki – utwór liczy się po 30 sekundach grania.",
                               "Your stats will show up here. Just listen to music – a song counts after 30 seconds of playback."),
                    FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = Hex("#CCFFFFFF"),
                }));
            }
            else
            {
                // ---- liczby ----
                double hours = items.Sum(x => x.Sec) / 3600.0;
                int artists = items.Where(x => x.A.Length > 0 && x.S != "Radio").Select(x => ListeningHistory.MainArtist(x.A).ToLowerInvariant()).Distinct().Count();
                var stats = new UniformGrid { Columns = 3, Margin = new Thickness(0, 4, 0, 0) };
                stats.Children.Add(Stat(hours >= 10 ? hours.ToString("0") : hours.ToString("0.0"), L.T("godzin muzyki", "hours of music")));
                stats.Children.Add(Stat(items.Count.ToString("N0"), L.T("odsłuchań", "plays")));
                stats.Children.Add(Stat(artists.ToString("N0"), L.T("wykonawców", "artists")));
                _body.Children.Add(stats);

                // ---- najczęściej słuchani wykonawcy ----
                var top = ListeningHistory.TopArtists(since, 5);
                if (top.Count > 0)
                {
                    var sp = Section(L.T("Najczęściej słuchani", "Top artists"));
                    int max = top[0].count, i = 1;
                    foreach (var (artist, count) in top) sp.Children.Add(Rank(i++, artist, null, count, max));
                }

                // ---- ulubione utwory ----
                var tracks = items.Where(x => x.S != "Radio")
                    .GroupBy(x => (x.T.ToLowerInvariant(), ListeningHistory.MainArtist(x.A).ToLowerInvariant()))
                    .Select(g => (g.First().T, g.First().A, count: g.Count()))
                    .OrderByDescending(x => x.count).Take(5).ToList();
                if (tracks.Count > 0)
                {
                    var sp = Section(L.T("Ulubione utwory", "Top tracks"));
                    int max = tracks[0].count, i = 1;
                    foreach (var t in tracks) sp.Children.Add(Rank(i++, t.T, t.A, t.count, max));
                }

                // ---- skąd słuchasz ----
                var sources = items.GroupBy(x => x.S).Select(g => (src: g.Key, sec: g.Sum(x => x.Sec))).OrderByDescending(x => x.sec).Take(5).ToList();
                double total = Math.Max(1, sources.Sum(x => x.sec));
                var ssp = Section(L.T("Skąd słuchasz", "Where you listen"));
                foreach (var (src, sec) in sources) ssp.Children.Add(Bar(src, sec / total));

                // ---- kiedy ----
                var local = items.Select(x => new DateTime(x.D, DateTimeKind.Utc).ToLocalTime()).ToList();
                var day = local.GroupBy(d => d.DayOfWeek).OrderByDescending(g => g.Count()).First().Key;
                int hour = local.GroupBy(d => d.Hour).OrderByDescending(g => g.Count()).First().Key;
                var culture = !L.En ? new CultureInfo("pl-PL") : new CultureInfo("en-US");
                string dayName = culture.DateTimeFormat.GetDayName(day);
                var wsp = Section(L.T("Kiedy słuchasz najwięcej", "When you listen most"));
                wsp.Children.Add(new TextBlock
                {
                    Text = L.T("Najczęściej w ", "Mostly on ") + dayName + L.T(", około ", ", around ") + hour.ToString("00") + ":00",
                    FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0),
                });
            }

            // ---- nowości ----
            var rsp = Section(L.T("Nowości od Twoich artystów", "New from your artists"));
            var recent = NewReleases.Recent.Take(6).ToList();
            if (recent.Count == 0)
                rsp.Children.Add(new TextBlock
                {
                    Text = App.Config.NewReleases
                        ? L.T("Na razie nic nowego. Isabelle sprawdza raz na dobę wykonawców, których słuchasz najczęściej.", "Nothing new yet. Isabelle checks your most played artists once a day.")
                        : L.T("Sprawdzanie nowości jest wyłączone w ustawieniach.", "Checking for new releases is turned off in settings."),
                    FontSize = 12.5, Foreground = Hex("#99FFFFFF"), TextWrapping = TextWrapping.Wrap,
                });
            foreach (var r in recent) rsp.Children.Add(ReleaseRow(r));
            var check = new TextBlock { Text = L.T("Sprawdź teraz", "Check now"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = _accent, Cursor = Cursors.Hand, Margin = new Thickness(0, 8, 0, 0) };
            check.MouseLeftButtonUp += async (s, e) =>
            {
                check.Text = L.T("Sprawdzam…", "Checking…");
                await NewReleases.CheckAsync();
                Render();
            };
            rsp.Children.Add(check);
        }

        // ================= elementy =================
        private Border Card(UIElement child) => new Border
        {
            Background = Hex("#26000000"), CornerRadius = new CornerRadius(18), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 10, 0, 0), Child = child,
        };

        private StackPanel Section(string title)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hex("#B3FFFFFF"), Margin = new Thickness(0, 0, 0, 8) });
            _body.Children.Add(Card(sp));
            return sp;
        }

        private Border Stat(string value, string label)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = value, FontSize = 30, FontWeight = FontWeights.Bold });
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Hex("#B3FFFFFF") });
            var b = Card(sp);
            b.Margin = new Thickness(0, 10, 8, 0);
            return b;
        }

        private FrameworkElement Rank(int n, string title, string sub, int count, int max)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = n.ToString(), FontSize = n == 1 ? 20 : 15, FontWeight = FontWeights.Bold, Foreground = n == 1 ? _accent : Hex("#99FFFFFF"), VerticalAlignment = VerticalAlignment.Center });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(sub)) texts.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = Hex("#99FFFFFF"), TextTrimming = TextTrimming.CharacterEllipsis });
            // pasek proporcjonalny do liczby odsłuchań
            texts.Children.Add(new Border { Height = 3, CornerRadius = new CornerRadius(1.5), Background = _accent, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Width = Math.Max(6, 330.0 * count / Math.Max(1, max)) });
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            var c = new TextBlock { Text = count + "×", FontSize = 12.5, Foreground = Hex("#99FFFFFF"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(c, 2);
            g.Children.Add(c);
            return g;
        }

        private FrameworkElement Bar(string label, double share)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 3, 0, 5) };
            var row = new Grid();
            row.Children.Add(new TextBlock { Text = label, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = Math.Round(share * 100) + "%", FontSize = 12.5, Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Right });
            sp.Children.Add(row);
            var track = new Grid { Height = 6, Margin = new Thickness(0, 4, 0, 0) };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Hex("#1FFFFFFF") });
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = _accent, HorizontalAlignment = HorizontalAlignment.Left };
            track.SizeChanged += (s, e) => fill.Width = Math.Max(6, e.NewSize.Width * share);
            track.Children.Add(fill);
            sp.Children.Add(track);
            return sp;
        }

        private FrameworkElement ReleaseRow(Release r)
        {
            var g = new Grid { Margin = new Thickness(0, 3, 0, 3), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var art = new Border { Width = 46, Height = 46, CornerRadius = new CornerRadius(8), Background = Hex("#1FFFFFFF"), Margin = new Thickness(0, 0, 12, 0) };
            if (!string.IsNullOrEmpty(r.Art))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(r.Art);
                    bmp.DecodePixelWidth = 92;
                    bmp.EndInit();
                    art.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
            g.Children.Add(art);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = r.Album, FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            string when = DateTime.TryParse(r.Date, out var d) ? d.ToString("d MMM", !L.En ? new CultureInfo("pl-PL") : new CultureInfo("en-US")) : r.Date;
            texts.Children.Add(new TextBlock { Text = r.Artist + " · " + (r.Single ? L.T("singiel", "single") : L.T("album", "album")) + " · " + when, FontSize = 12, Foreground = Hex("#99FFFFFF"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            g.MouseEnter += (s, e) => g.Opacity = 0.8;
            g.MouseLeave += (s, e) => g.Opacity = 1;
            g.MouseLeftButtonUp += (s, e) => NewReleases.Open(r);
            return g;
        }

        private Border Chip(string text, bool selected, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 6, 14, 7), Margin = new Thickness(0, 0, 7, 0), Cursor = Cursors.Hand,
                Background = selected ? (Brush)Brushes.White : Hex("#26FFFFFF"),
                Child = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = selected ? Brushes.Black : Brushes.White },
            };
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
