using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// Zakładka „Podcasty” w Mojej muzyce: po lewej subskrypcje, po prawej wyszukiwanie / popularne
    /// albo odcinki wybranego podcastu. Odcinek gra w Isabelle (wyspa, klawisze), z zapamiętanym miejscem.
    /// </summary>
    public sealed class PodcastView : Grid
    {
        private static readonly Brush TextSub = Hex("#8CFFFFFF");
        private readonly Brush _accent;
        private readonly StackPanel _subs = new StackPanel();
        private readonly StackPanel _rows = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        private readonly TextBlock _heading = new TextBlock(), _status = new TextBlock();
        private readonly TextBox _search = new TextBox();
        private readonly Grid _header = new Grid();
        private int _loadId;
        private PodcastShow _show;
        // ikonki ▶/❚❚ przy odcinkach - zgrane z wyspą
        private readonly Dictionary<string, (Path icon, TextBlock title)> _epIcons = new Dictionary<string, (Path, TextBlock)>();
        private const string PlayGeo = "M0,0 L10,6 L0,12 Z", PauseGeo = "M0,0 H3.5 V12 H0 Z M6.5,0 H10 V12 H6.5 Z";

        public PodcastView(Brush accent)
        {
            _accent = accent;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- lewa: subskrypcje ----
            Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _subs, Margin = new Thickness(0, 0, 18, 0) });

            // ---- prawa ----
            var right = new Grid();
            Grid.SetColumn(right, 1);
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Children.Add(right);

            right.Children.Add(SearchBox());
            _header.Margin = new Thickness(6, 4, 6, 10);
            Grid.SetRow(_header, 1);
            right.Children.Add(_header);

            var listHost = new Grid();
            _scroll.Content = _rows;
            listHost.Children.Add(_scroll);
            _status.FontSize = 13;
            _status.Foreground = TextSub;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.TextAlignment = TextAlignment.Center;
            _status.HorizontalAlignment = HorizontalAlignment.Center;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(40);
            _status.IsHitTestVisible = false;
            listHost.Children.Add(_status);
            Grid.SetRow(listHost, 2);
            right.Children.Add(listHost);

            Action changed = () => Dispatcher.BeginInvoke(new Action(RefreshSubs));
            Action player = () => Dispatcher.BeginInvoke(new Action(RefreshIcons));
            void Hook() { PodcastService.Changed -= changed; PodcastService.Changed += changed; LocalPlayer.Changed -= player; LocalPlayer.Changed += player; }
            Hook();
            Unloaded += (s, e) => { PodcastService.Changed -= changed; LocalPlayer.Changed -= player; };
            Loaded += (s, e) => Hook();

            RefreshSubs();
            PodcastService.Prefetch();
            _ = ShowTop();
        }

        // ================= lewa kolumna =================
        private void RefreshSubs()
        {
            _subs.Children.Clear();
            _subs.Children.Add(new TextBlock { Text = L.T("Odkrywaj", "Discover"), FontSize = 12, Foreground = TextSub, Margin = new Thickness(12, 2, 0, 4) });
            _subs.Children.Add(SideRow(L.T("Popularne", "Popular"), null, () => { _search.Text = ""; _ = ShowTop(); }));
            _subs.Children.Add(new TextBlock { Text = L.T("Subskrypcje", "Subscriptions"), FontSize = 12, Foreground = TextSub, Margin = new Thickness(12, 16, 0, 4) });
            var subs = PodcastService.Subscriptions;
            if (subs.Count == 0)
                _subs.Children.Add(new TextBlock
                {
                    Text = L.T("Znajdź podcast i kliknij „Subskrybuj” – pojawi się tutaj.", "Find a podcast and click “Subscribe” – it shows up here."),
                    FontSize = 12, Foreground = TextSub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 2, 8, 0),
                });
            foreach (var sh in subs)
            {
                var show = sh;
                _subs.Children.Add(SideRow(show.Title, show.Art, () => _ = ShowEpisodes(show)));
            }
        }

        private Border SideRow(string text, string art, Action click)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (art != null)
            {
                var a = Art(art, 26, 6);
                a.Margin = new Thickness(0, 0, 9, 0);
                g.Children.Add(a);
            }
            var t = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 1);
            g.Children.Add(t);
            var b = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = g };
            b.MouseEnter += (s, e) => b.Background = Hex("#0DFFFFFF");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        // ================= wyszukiwanie =================
        private Border SearchBox()
        {
            var g = new Grid();
            var hint = new TextBlock { Text = L.T("Szukaj podcastów", "Search podcasts"), FontSize = 14, Foreground = Hex("#66FFFFFF"), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Margin = new Thickness(2, 0, 0, 0) };
            _search.FontSize = 14;
            _search.Background = Brushes.Transparent;
            _search.Foreground = Brushes.White;
            _search.CaretBrush = Brushes.White;
            _search.BorderThickness = new Thickness(0);
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            g.Children.Add(hint);
            g.Children.Add(_search);
            // szukamy chwilę po ostatniej literze
            var debounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            debounce.Tick += (s, e) =>
            {
                debounce.Stop();
                string q = _search.Text.Trim();
                if (q.Length >= 2) _ = Search(q);
                else if (q.Length == 0) _ = ShowTop();
            };
            _search.TextChanged += (s, e) =>
            {
                hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                debounce.Stop();
                debounce.Start();
            };
            return new Border { CornerRadius = new CornerRadius(14), Background = Hex("#17FFFFFF"), Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 12), Child = g };
        }

        private async Task ShowTop()
        {
            int id = ++_loadId;
            _show = null;
            SetHeader(L.T("Popularne podcasty", "Popular podcasts"), null);
            // lista z dysku od razu, świeża po chwili
            var cached = PodcastService.CachedTop();
            if (cached != null && cached.Count > 0) ShowShows(cached);
            else Status(L.T("Wczytuję…", "Loading…"));
            try
            {
                var list = await PodcastService.TopAsync();
                if (ReferenceEquals(list, cached)) return;
                if (id != _loadId) return;
                ShowShows(list);
            }
            catch (Exception ex) { if (id == _loadId) Status(L.T("Nie udało się wczytać listy. Sprawdź internet.", "Couldn't load the list. Check your connection.")); Log.Info("podcasty: " + ex.Message); }
        }

        private async Task Search(string q)
        {
            int id = ++_loadId;
            _show = null;
            SetHeader(L.T("Wyniki: ", "Results: ") + q, null);
            Status(L.T("Szukam…", "Searching…"));
            try
            {
                var list = await PodcastService.SearchAsync(q);
                if (id != _loadId) return;
                ShowShows(list);
            }
            catch (Exception ex) { if (id == _loadId) Status(L.T("Nie udało się wyszukać.", "Search failed.")); Log.Info("podcasty: " + ex.Message); }
        }

        private void ShowShows(List<PodcastShow> list)
        {
            _rows.Children.Clear();
            Status(list.Count == 0 ? L.T("Nic nie znaleziono", "Nothing found") : null);
            foreach (var sh in list)
            {
                var show = sh;
                _rows.Children.Add(Row(Art(show.Art, 48, 10), show.Title, show.Author, null, () => _ = ShowEpisodes(show)));
            }
            _scroll.ScrollToTop();
        }

        // ================= odcinki =================
        private async Task ShowEpisodes(PodcastShow show)
        {
            int id = ++_loadId;
            _show = show;
            SetHeader(show.Title, show);
            _rows.Children.Clear();
            _epIcons.Clear();
            // odcinki z dysku od razu, świeże dociągamy w tle
            var cached = PodcastService.CachedEpisodes(show);
            if (cached != null && cached.Count > 0) RenderEpisodes(cached);
            else Status(L.T("Wczytuję odcinki…", "Loading episodes…"));
            try
            {
                var eps = await PodcastService.EpisodesAsync(show);
                if (id != _loadId || ReferenceEquals(eps, cached)) return;
                RenderEpisodes(eps);
            }
            catch (Exception ex)
            {
                if (id == _loadId && (cached == null || cached.Count == 0)) Status(L.T("Nie udało się wczytać odcinków tego podcastu.", "Couldn't load this podcast's episodes."));
                Log.Info("podcasty: " + ex.Message);
            }
        }

        private void RenderEpisodes(List<PodcastEpisode> eps)
        {
            double offset = _scroll.VerticalOffset;
            _rows.Children.Clear();
            _epIcons.Clear();
            Status(eps.Count == 0 ? L.T("Brak odcinków", "No episodes") : null);
            var culture = L.En ? new System.Globalization.CultureInfo("en-US") : new System.Globalization.CultureInfo("pl-PL");
            foreach (var e0 in eps.Take(80))
            {
                var ep = e0;
                string meta = (ep.Date != default ? ep.Date.ToString("d MMM yyyy", culture) : "") + (ep.Duration > 0 ? " · " + Dur(ep.Duration) : "");
                string right;
                if (PodcastService.IsDone(ep.Key)) right = "✓";
                else
                {
                    double pos = PodcastService.Position(ep.Key);
                    right = pos > 0 && ep.Duration > 0 ? L.T("zostało ", "") + Dur((int)(ep.Duration - pos)) + L.T("", " left") : null;
                }
                var dot = PlayDot(out var icon);
                // kliknięcie obsługujemy już przy wciśnięciu (niżej) - ten sam odcinek = pauza / wznów, inny = włącz
                var row = Row(dot, ep.Title, meta, right, () => { });
                // najechanie = przygotuj start (rozwiąż przekierowania), kliknięcie od razu przy wciśnięciu
                row.MouseEnter += (s, e) => _ = PodcastService.ResolveAsync(ep.Url);
                row.PreviewMouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    if (LocalPlayer.CurrentEpisode?.Key == ep.Key) LocalPlayer.TogglePause();
                    else { ShowPlaying(ep.Key, true); LocalPlayer.PlayPodcast(ep); }
                };
                var title = (TextBlock)((StackPanel)((Grid)row.Child).Children[1]).Children[0];
                _epIcons[ep.Key] = (icon, title);
                _rows.Children.Add(row);
            }
            RefreshIcons();
            _scroll.ScrollToVerticalOffset(offset);
            // najnowsze odcinki i ten zaczęty przygotowujemy od razu
            foreach (var ep in eps.Take(3)) _ = PodcastService.ResolveAsync(ep.Url);
            foreach (var ep in eps.Take(80).Where(x => PodcastService.Position(x.Key) > 0).Take(2)) _ = PodcastService.ResolveAsync(ep.Url);
        }

        /// <summary>▶ / ❚❚ przy odcinkach według tego, co naprawdę gra (te same zdarzenia co wyspa).</summary>
        private void RefreshIcons() => ShowPlaying(LocalPlayer.CurrentEpisode?.Key, LocalPlayer.IsPlaying);

        private void ShowPlaying(string key, bool playing)
        {
            foreach (var kv in _epIcons)
            {
                bool cur = kv.Key == key;
                kv.Value.icon.Data = Geometry.Parse(cur && playing ? PauseGeo : PlayGeo);
                kv.Value.icon.Margin = new Thickness(cur && playing ? 0 : 3, 0, 0, 0);
                kv.Value.title.Foreground = cur ? _accent : Brushes.White;
            }
        }

        private void SetHeader(string title, PodcastShow show)
        {
            _header.Children.Clear();
            _header.ColumnDefinitions.Clear();
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (show != null)
            {
                // ‹ powrót do listy (popularne albo wyniki wyszukiwania)
                var back = new Border
                {
                    Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Hex("#1FFFFFFF"), Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), ToolTip = L.T("Wróć", "Back"),
                    Child = new Path { Data = Geometry.Parse("M7,0 L0,7 L7,14"), Stroke = Brushes.White, StrokeThickness = 2, Width = 7, Height = 13, Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) },
                };
                back.MouseEnter += (s, e) => back.Background = Hex("#33FFFFFF");
                back.MouseLeave += (s, e) => back.Background = Hex("#1FFFFFFF");
                back.MouseLeftButtonDown += (s, e) => e.Handled = true;
                back.MouseLeftButtonUp += (s, e) => GoBack();
                var a = Art(show.Art, 64, 12);
                a.Margin = new Thickness(0, 0, 14, 0);
                var left = new StackPanel { Orientation = Orientation.Horizontal };
                left.Children.Add(back);
                left.Children.Add(a);
                _header.Children.Add(left);
            }
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (show != null && !string.IsNullOrEmpty(show.Author)) texts.Children.Add(new TextBlock { Text = show.Author, FontSize = 12.5, Foreground = TextSub, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(texts, 1);
            _header.Children.Add(texts);
            if (show != null)
            {
                bool subbed = PodcastService.IsSubscribed(show);
                var btn = new Border
                {
                    CornerRadius = new CornerRadius(15), Padding = new Thickness(14, 6, 14, 7), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center,
                    Background = subbed ? Hex("#26FFFFFF") : _accent,
                    Child = new TextBlock { Text = subbed ? L.T("Subskrybujesz", "Subscribed") : L.T("Subskrybuj", "Subscribe"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = subbed ? Brushes.White : Brushes.Black },
                };
                btn.MouseLeftButtonDown += (s, e) => e.Handled = true;
                btn.MouseLeftButtonUp += (s, e) => { PodcastService.ToggleSubscribe(show); SetHeader(title, show); };
                Grid.SetColumn(btn, 2);
                _header.Children.Add(btn);
            }
        }

        private void GoBack()
        {
            string q = _search.Text.Trim();
            if (q.Length >= 2) _ = Search(q); else _ = ShowTop();
        }

        // ================= elementy =================
        private Border Row(FrameworkElement left, string title, string sub, string right, Action click)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            left.Margin = new Thickness(0, 0, 12, 0);
            g.Children.Add(left);
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(sub)) texts.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = TextSub, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);
            if (!string.IsNullOrEmpty(right))
            {
                var r = new TextBlock { Text = right, FontSize = 12, Foreground = right == "✓" ? _accent : TextSub, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 4, 0) };
                Grid.SetColumn(r, 2);
                g.Children.Add(r);
            }
            var b = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 12, 8), Margin = new Thickness(0, 0, 0, 2), Cursor = Cursors.Hand, Background = Brushes.Transparent, Child = g };
            b.MouseEnter += (s, e) => b.Background = Hex("#12FFFFFF");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        private FrameworkElement PlayDot(out Path icon)
        {
            icon = new Path { Data = Geometry.Parse(PlayGeo), Fill = Brushes.White, Width = 10, Height = 12, Stretch = Stretch.Uniform, Margin = new Thickness(3, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            return new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Background = Hex("#1FFFFFFF"), Child = icon };
        }

        private static Border Art(string url, double size, double radius)
        {
            var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(radius), Background = Hex("#1FFFFFFF") };
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(url);
                    bmp.DecodePixelWidth = (int)(size * 2);
                    bmp.EndInit();
                    b.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                }
                catch { }
            }
            return b;
        }

        private void Status(string text)
        {
            _status.Text = text ?? "";
            _status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string Dur(int sec)
        {
            if (sec < 0) sec = 0;
            int h = sec / 3600, m = sec % 3600 / 60;
            return h > 0 ? h + " h " + m + " min" : Math.Max(1, m) + " min";
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
