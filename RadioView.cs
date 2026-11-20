using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// zakładka radia w czystym stylu odtwarzacza - kategorie po lewej, spokojna lista stacji,
    /// duża karta „teraz gra” z aktualną piosenką i dyskretnymi statystykami połączenia.
    /// Szczegóły (głosy, popularność, opis jakości) są w podpowiedziach po najechaniu.
    /// </summary>
    public sealed class RadioView : Grid
    {
        private static readonly Brush TextSub = Hex("#8CFFFFFF");
        private static readonly Brush TextDim = Hex("#B3FFFFFF");
        private static readonly Brush Good = Hex("#34C759"), Mid = Hex("#FFCC00"), Bad = Hex("#FF453A"), Off = Hex("#26FFFFFF");

        private readonly Brush _accent;
        private readonly StackPanel _cats = new StackPanel();
        private readonly List<(Border el, TextBlock text, string key)> _catItems = new List<(Border, TextBlock, string)>();
        private readonly StackPanel _rows = new StackPanel();
        private readonly ScrollViewer _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        private readonly TextBlock _status = new TextBlock(), _heading = new TextBlock(), _count = new TextBlock();
        private readonly TextBox _search = new TextBox();
        private readonly Border _now = new Border();
        private readonly List<(RadioStation st, Border row, TextBlock name, Path star, FrameworkElement live)> _rowItems = new List<(RadioStation, Border, TextBlock, Path, FrameworkElement)>();
        private string _category;
        private int _loadId;

        // karta „teraz gra”
        private readonly Grid _nowLogo = new Grid();
        private readonly TextBlock _nowName = new TextBlock(), _nowSong = new TextBlock();
        private readonly StackPanel _nowChips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        private readonly Path _nowStar = StarPath(18), _playIcon = new Path();
        private string _nowLogoFor;

        private const string StarGeo = "M12,2 L14.9,8.6 L22,9.3 L16.6,14 L18.2,21 L12,17.3 L5.8,21 L7.4,14 L2,9.3 L9.1,8.6 Z";
        private const string PlayGeo = "M0,0 L14,8 L0,16 Z";
        private const string PauseGeo = "M0,0 H5 V16 H0 Z M9,0 H14 V16 H9 Z";

        public RadioView(Brush accent)
        {
            _accent = accent;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- kategorie ----
            var catScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _cats, Margin = new Thickness(0, 0, 18, 0) };
            string lastGroup = null;
            foreach (var c in RadioService.Categories)
            {
                string group = c.key.StartsWith("tag-") ? L.T("Gatunki", "Genres") : c.query == null ? L.T("Twoje", "Yours") : L.T("Rankingi", "Charts");
                if (group != lastGroup)
                {
                    _cats.Children.Add(new TextBlock { Text = group, FontSize = 12, Foreground = TextSub, Margin = new Thickness(12, lastGroup == null ? 2 : 16, 0, 4) });
                    lastGroup = group;
                }
                var text = new TextBlock { Text = L.T(c.pl, c.en), FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
                var b = new Border
                {
                    CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 1), Cursor = Cursors.Hand,
                    Background = Brushes.Transparent, Child = text,
                };
                string key = c.key;
                b.MouseEnter += (s, e) => { if (_category != key) b.Background = Hex("#0DFFFFFF"); };
                b.MouseLeave += (s, e) => { if (_category != key) b.Background = Brushes.Transparent; };
                b.MouseLeftButtonDown += (s, e) => e.Handled = true;
                b.MouseLeftButtonUp += (s, e) => { _search.Text = ""; Load(key); };
                _cats.Children.Add(b);
                _catItems.Add((b, text, key));
            }
            Children.Add(catScroll);

            // ---- prawa strona ----
            var right = new Grid();
            Grid.SetColumn(right, 1);
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Children.Add(right);

            BuildNowCard();
            right.Children.Add(_now);

            var searchBox = SearchBox();
            Grid.SetRow(searchBox, 1);
            right.Children.Add(searchBox);

            var head = new Grid { Margin = new Thickness(6, 4, 6, 8) };
            _heading.FontSize = 18;
            _heading.FontWeight = FontWeights.Bold;
            head.Children.Add(_heading);
            _count.FontSize = 12;
            _count.Foreground = TextSub;
            _count.HorizontalAlignment = HorizontalAlignment.Right;
            _count.VerticalAlignment = VerticalAlignment.Bottom;
            head.Children.Add(_count);
            Grid.SetRow(head, 2);
            right.Children.Add(head);

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
            Grid.SetRow(listHost, 3);
            right.Children.Add(listHost);

            LocalPlayer.Changed += OnPlayerChanged;
            // statystyki (zapas bufora) odświeżane co sekundę
            var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            tick.Tick += (s, e) => { if (LocalPlayer.CurrentStation != null && IsVisible) UpdateNow(); };
            tick.Start();
            Unloaded += (s, e) => { LocalPlayer.Changed -= OnPlayerChanged; tick.Stop(); };
            UpdateNow();
            Load(string.IsNullOrEmpty(App.Config.RadioCategory) ? "pl-top" : App.Config.RadioCategory);
            RadioService.Prefetch();
        }

        // ================= lista =================
        private async void Load(string key)
        {
            _category = key;
            foreach (var (el, text, k) in _catItems)
            {
                el.Background = k == key ? Hex("#1FFFFFFF") : Brushes.Transparent;
                text.Foreground = k == key ? Brushes.White : TextDim;
            }
            if (key != "search" && App.Config.RadioCategory != key) { App.Config.RadioCategory = key; App.Config.Save(); }

            var cat = RadioService.Categories.FirstOrDefault(c => c.key == key);
            _heading.Text = key == "search" ? L.T("Wyniki wyszukiwania", "Search results") : L.T(cat.pl ?? "", cat.en ?? "");
            _count.Text = "";
            int id = ++_loadId;
            List<RadioStation> list;
            if (key == "fav") list = (App.Config.RadioFavorites ?? new List<RadioStation>()).ToList();
            else if (key == "recent") list = (App.Config.RadioRecent ?? new List<RadioStation>()).ToList();
            else if (key == "search")
            {
                Show(null, L.T("Szukam…", "Searching…"));
                list = await RadioService.Search(_search.Text.Trim());
                if (id != _loadId) return;   // w międzyczasie wybrano inną kategorię
            }
            else
            {
                // zapamiętana lista od razu, świeża w tle (jeśli stara ma ponad pół godziny)
                var cached = RadioService.Cached(cat.query);
                if (cached != null && cached.Count > 0)
                {
                    _count.Text = cached.Count + " " + L.T("stacji", "stations");
                    Show(cached, null);
                    if (RadioService.IsFresh(cat.query)) return;
                    var fresh = await RadioService.Category(cat.query);
                    if (id != _loadId || fresh.Count == 0 || SameList(cached, fresh)) return;
                    double offset = _scroll.VerticalOffset;
                    Show(fresh, null);
                    _scroll.ScrollToVerticalOffset(offset);
                    _count.Text = fresh.Count + " " + L.T("stacji", "stations");
                    return;
                }
                Show(null, L.T("Ładuję stacje…", "Loading stations…"));
                list = await RadioService.Category(cat.query);
                if (id != _loadId) return;
            }
            string empty = key == "fav" ? L.T("Kliknij gwiazdkę przy stacji, żeby dodać ją do ulubionych.", "Click the star next to a station to add it to favorites.")
                         : key == "recent" ? L.T("Tu pojawią się stacje, których słuchałeś.", "Stations you've listened to will show up here.")
                         : L.T("Nic nie znaleziono (albo brak internetu).", "Nothing found (or no internet).");
            _count.Text = list.Count > 0 ? list.Count + " " + L.T("stacji", "stations") : "";
            Show(list, list.Count == 0 ? empty : null);
        }

        private static bool SameList(List<RadioStation> a, List<RadioStation> b) =>
            a.Count == b.Count && a.Select(x => x.Url).SequenceEqual(b.Select(x => x.Url));

        private void Show(List<RadioStation> list, string status)
        {
            _rows.Children.Clear();
            _rowItems.Clear();
            _scroll.ScrollToTop();
            _status.Text = status ?? "";
            _status.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
            if (list == null) return;
            foreach (var st in list) _rows.Children.Add(Row(st));
            Highlight();
        }

        private Border Row(RadioStation st)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // logo
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // gra / jakość
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // sygnał
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // gwiazdka

            var logo = Logo(st, 40, 10);
            logo.Margin = new Thickness(0, 0, 14, 0);
            g.Children.Add(logo);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = st.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            texts.Children.Add(name);
            texts.Children.Add(new TextBlock { Text = st.Info, FontSize = 12, Foreground = TextSub, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            // po prawej: dyskretna plakietka jakości (albo „GRA” przy aktualnej stacji)
            var right = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
            var quality = QualityPill(st);
            var live = Pill(L.T("GRA", "LIVE"), _accent, Brushes.Black);
            live.Visibility = Visibility.Collapsed;
            right.Children.Add(quality);
            right.Children.Add(live);
            Grid.SetColumn(right, 2);
            g.Children.Add(right);

            var signal = SignalBars(st.Signal);
            signal.Margin = new Thickness(0, 0, 6, 0);
            Grid.SetColumn(signal, 3);
            g.Children.Add(signal);

            var star = StarPath(15);
            var starBtn = new Border { Child = star, Background = Brushes.Transparent, Padding = new Thickness(8), Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center, ToolTip = L.T("Ulubione", "Favorite") };
            starBtn.MouseLeftButtonDown += (s, e) => e.Handled = true;
            starBtn.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                LocalPlayer.ToggleFavorite(st);
                if (_category == "fav") Load("fav");
            };
            Grid.SetColumn(starBtn, 4);
            g.Children.Add(starBtn);

            var row = new Border
            {
                Child = g, CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 8, 6, 8), Margin = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Details(st),
            };
            // gwiazdka widoczna tylko po najechaniu (albo gdy stacja jest w ulubionych) - spokojniejsza lista
            row.MouseEnter += (s, e) => { if (!IsCurrent(st)) row.Background = Hex("#0DFFFFFF"); star.Opacity = 1; };
            row.MouseLeave += (s, e) => { if (!IsCurrent(st)) row.Background = Brushes.Transparent; star.Opacity = LocalPlayer.IsFavorite(st) ? 1 : 0; };
            row.MouseLeftButtonDown += (s, e) => e.Handled = true;
            row.MouseLeftButtonUp += (s, e) => LocalPlayer.PlayStation(st);
            _rowItems.Add((st, row, name, star, live));
            return row;
        }

        private static string Details(RadioStation st) =>
            st.Name + "\n" +
            L.T("Dźwięk: ", "Sound: ") + st.SoundLabel + " (" + st.Format + ")\n" +
            L.T("Sygnał: ", "Signal: ") + st.SignalLabel + "\n" +
            string.Format(L.T("{0} głosów · {1} odsłuchań dziennie", "{0} votes · {1} plays a day"), st.Votes, st.Clicks) +
            (st.Trend > 0 ? L.T(" · zyskuje ▲", " · trending ▲") : st.Trend < 0 ? L.T(" · traci ▼", " · falling ▼") : "");

        /// <summary>Format w małej pigułce; HQ na zielono dla bardzo dobrej jakości.</summary>
        private static FrameworkElement QualityPill(RadioStation st)
        {
            int q = st.SoundQuality;
            string text = q >= 4 ? "HQ  " + st.Format : st.Format;
            if (st.Bitrate <= 0 && st.Codec.Length == 0) text = "—";
            var fg = q >= 4 ? Good : q == 1 ? Mid : TextSub;
            var bg = q >= 4 ? Hex("#1A34C759") : Hex("#12FFFFFF");
            return Pill(text, bg, fg);
        }

        private static Border Pill(string text, Brush bg, Brush fg) => new Border
        {
            Background = bg, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 3, 8, 4), HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = fg },
        };

        /// <summary>Kreski „zasięgu” jak w telefonie.</summary>
        private static FrameworkElement SignalBars(int level)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
            Brush col = level >= 3 ? Hex("#E6FFFFFF") : level == 2 ? Mid : Bad;
            for (int i = 1; i <= 4; i++)
                sp.Children.Add(new Rectangle
                {
                    Width = 3, Height = 3 + i * 3, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1.5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Bottom, Fill = i <= level ? col : Off,
                });
            return sp;
        }

        private static Path StarPath(double size) => new Path
        {
            Data = Geometry.Parse(StarGeo), Width = size, Height = size, Stretch = Stretch.Uniform,
            StrokeThickness = 1.6, Stroke = Hex("#80FFFFFF"), Fill = Brushes.Transparent, StrokeLineJoin = PenLineJoin.Round,
        };

        private static void PaintStar(Path p, bool on)
        {
            p.Fill = on ? Brushes.White : Brushes.Transparent;
            p.Stroke = on ? Brushes.White : Hex("#80FFFFFF");
        }

        /// <summary>Logo stacji z internetu; zanim się wczyta (albo gdy go brak) - pierwsza litera nazwy.</summary>
        private static Grid Logo(RadioStation st, double size, double radius)
        {
            var g = new Grid { Width = size, Height = size };
            g.Clip = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
            g.Children.Add(new Border { Background = Hex("#1FFFFFFF") });
            g.Children.Add(new TextBlock
            {
                Text = st.Name.Length > 0 ? st.Name.Substring(0, 1).ToUpperInvariant() : "?", FontSize = size * 0.42, FontWeight = FontWeights.Bold,
                Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
            _ = LoadLogo(g, st);
            return g;
        }

        /// <summary>Kafelek logo w najlepszej znalezionej jakości (ten sam co w wyspie), wczytany w tle.</summary>
        private static async System.Threading.Tasks.Task LoadLogo(Grid g, RadioStation st)
        {
            try
            {
                string path = await RadioArt.LogoFile(st);
                if (path == null) return;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = (int)Math.Max(64, g.Width * 2.5);
                bmp.CacheOption = BitmapCacheOption.OnLoad;      // bez blokowania pliku
                bmp.EndInit();
                bmp.Freeze();
                var img = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                g.Children.Add(img);
            }
            catch { }
        }

        private static bool IsCurrent(RadioStation st)
        {
            var cur = LocalPlayer.CurrentStation;
            return cur != null && ((cur.Uuid.Length > 0 && cur.Uuid == st.Uuid) || cur.Url == st.Url);
        }

        private void Highlight()
        {
            foreach (var (st, row, name, star, live) in _rowItems)
            {
                bool cur = IsCurrent(st);
                row.Background = cur ? Hex("#17FFFFFF") : Brushes.Transparent;
                name.Foreground = cur ? _accent : Brushes.White;
                live.Visibility = cur ? Visibility.Visible : Visibility.Collapsed;
                ((Grid)live.Parent).Children[0].Visibility = cur ? Visibility.Collapsed : Visibility.Visible;
                bool fav = LocalPlayer.IsFavorite(st);
                PaintStar(star, fav);
                star.Opacity = fav || row.IsMouseOver ? 1 : 0;
            }
        }

        // ================= teraz gra =================
        private void BuildNowCard()
        {
            _now.Background = Hex("#12FFFFFF");
            _now.CornerRadius = new CornerRadius(22);
            _now.Padding = new Thickness(16);
            _now.Margin = new Thickness(0, 0, 0, 14);

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _nowLogo.Margin = new Thickness(0, 0, 16, 0);
            _nowLogo.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(_nowLogo);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _nowName.FontSize = 20;
            _nowName.FontWeight = FontWeights.Bold;
            _nowName.TextTrimming = TextTrimming.CharacterEllipsis;
            texts.Children.Add(_nowName);
            _nowSong.FontSize = 14;
            _nowSong.TextTrimming = TextTrimming.CharacterEllipsis;
            _nowSong.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(_nowSong);
            texts.Children.Add(_nowChips);
            Grid.SetColumn(texts, 1);
            g.Children.Add(texts);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var starBtn = new Border
            {
                Child = _nowStar, Background = Hex("#17FFFFFF"), Width = 44, Height = 44, CornerRadius = new CornerRadius(22), Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 0, 0), ToolTip = L.T("Ulubione", "Favorite"),
            };
            _nowStar.HorizontalAlignment = HorizontalAlignment.Center;
            _nowStar.VerticalAlignment = VerticalAlignment.Center;
            starBtn.MouseLeftButtonDown += (s, e) => e.Handled = true;
            starBtn.MouseLeftButtonUp += (s, e) => { LocalPlayer.ToggleFavorite(LocalPlayer.CurrentStation); if (_category == "fav") Load("fav"); };
            buttons.Children.Add(starBtn);

            _playIcon.Width = _playIcon.Height = 16;
            _playIcon.Stretch = Stretch.Uniform;
            _playIcon.Fill = Brushes.Black;
            _playIcon.HorizontalAlignment = HorizontalAlignment.Center;
            _playIcon.VerticalAlignment = VerticalAlignment.Center;
            var play = new Border
            {
                Background = Brushes.White, Width = 52, Height = 52, CornerRadius = new CornerRadius(26), Cursor = Cursors.Hand,
                Margin = new Thickness(10, 0, 0, 0), Child = _playIcon,
            };
            play.MouseLeftButtonDown += (s, e) => e.Handled = true;
            play.MouseLeftButtonUp += (s, e) => LocalPlayer.TogglePause();
            buttons.Children.Add(play);
            Grid.SetColumn(buttons, 2);
            g.Children.Add(buttons);
            _now.Child = g;
        }

        private void OnPlayerChanged() => Dispatcher.BeginInvoke(new Action(() => { UpdateNow(); Highlight(); }));

        private void UpdateNow()
        {
            var st = LocalPlayer.CurrentStation;
            _now.Visibility = st == null ? Visibility.Collapsed : Visibility.Visible;
            if (st == null) return;
            if (_nowLogoFor != st.Url)
            {
                _nowLogoFor = st.Url;
                _nowLogo.Children.Clear();
                _nowLogo.Children.Add(Logo(st, 76, 18));
            }
            _nowName.Text = st.Name;
            bool hasSong = !string.IsNullOrWhiteSpace(LocalPlayer.StreamTitle);
            _nowSong.Text = hasSong ? LocalPlayer.StreamTitle
                          : LocalPlayer.ConnectSeconds <= 0
                              ? L.T("Łączenie…", "Connecting…")
                          : st.Info;
            _nowSong.Foreground = hasSong ? TextDim : TextSub;

            string geo = LocalPlayer.IsPlaying || LocalPlayer.ConnectSeconds <= 0 ? PauseGeo : PlayGeo;
            if (_playIcon.Tag as string != geo)
            {
                _playIcon.Tag = geo;
                _playIcon.Data = Geometry.Parse(geo);
                _playIcon.Margin = new Thickness(geo == PlayGeo ? 3 : 0, 0, 0, 0);
            }

            // dyskretne „chipy” ze stanem połączenia
            _nowChips.Children.Clear();
            _nowChips.Children.Add(Chip(st.Format, null));
            string live = LocalPlayer.LiveSignal;
            Brush dot = LocalPlayer.ConnectSeconds <= 0 ? TextSub : LocalPlayer.Dropouts == 0 ? Good : LocalPlayer.Dropouts <= 1 ? Good : LocalPlayer.Dropouts <= 4 ? Mid : Bad;
            _nowChips.Children.Add(Chip(L.T("sygnał ", "signal ") + live, dot));
            if (LocalPlayer.BufferTarget > 0 && LocalPlayer.ConnectSeconds > 0)
                _nowChips.Children.Add(Chip(LocalPlayer.BufferSeconds < LiveRadioStream.GoalSeconds - 0.5
                    ? string.Format(L.T("zapas {0:0} → {1:0} s", "buffer {0:0} → {1:0} s"), Math.Floor(LocalPlayer.BufferSeconds), LiveRadioStream.GoalSeconds)
                    : string.Format(L.T("zapas {0:0} s", "buffer {0:0} s"), Math.Floor(LocalPlayer.BufferSeconds)), null));
            if (LocalPlayer.ConnectSeconds > 0)
                _nowChips.Children.Add(Chip(LocalPlayer.Buffering ? L.T("buforuje…", "buffering…")
                    : LocalPlayer.Dropouts == 0 ? L.T("bez przerw", "no dropouts")
                    : string.Format(L.T("przerwy: {0}", "dropouts: {0}"), LocalPlayer.Dropouts), null));
            _nowChips.ToolTip = LocalPlayer.ConnectSeconds > 0
                ? string.Format(L.T("Połączono w {0:0.0} s · jakość dźwięku: {1} · zapas chroni przed chwilowymi zacięciami sieci · ponowne połączenia: {2}",
                                    "Connected in {0:0.0} s · sound quality: {1} · the buffer protects against short network hiccups · reconnects: {2}"),
                                LocalPlayer.ConnectSeconds, st.SoundLabel.ToLowerInvariant(), LocalPlayer.StreamReconnects)
                : null;
            PaintStar(_nowStar, LocalPlayer.IsFavorite(st));
        }

        private static Border Chip(string text, Brush dot)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (dot != null) sp.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = dot, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = TextDim });
            return new Border { Background = Hex("#14FFFFFF"), CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(0, 0, 6, 0), Child = sp };
        }

        // ================= szukanie =================
        private Border SearchBox()
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new Path
            {
                // lupa
                Data = Geometry.Parse("M6.5,1 A5.5,5.5 0 1 1 6.49,1 Z M10.5,10.5 L15,15"), Stroke = TextSub, StrokeThickness = 1.6,
                Width = 14, Height = 14, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            var field = new Grid();
            Grid.SetColumn(field, 1);
            var hint = new TextBlock
            {
                Text = L.T("Szukaj stacji, np. RMF, Eska, BBC, jazz…", "Search stations, e.g. BBC, jazz…"),
                Foreground = Hex("#59FFFFFF"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            };
            _search.Background = Brushes.Transparent;
            _search.Foreground = Brushes.White;
            _search.BorderThickness = new Thickness(0);
            _search.FontSize = 13.5;
            _search.CaretBrush = Brushes.White;
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            _search.TextChanged += (s, e) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _search.KeyDown += (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                if (_search.Text.Trim().Length == 0) Load(App.Config.RadioCategory ?? "pl-top");
                else Load("search");
            };
            field.Children.Add(hint);
            field.Children.Add(_search);
            g.Children.Add(field);
            return new Border
            {
                Background = Hex("#0FFFFFFF"), CornerRadius = new CornerRadius(20),
                Padding = new Thickness(16, 9, 16, 9), Margin = new Thickness(0, 0, 0, 12), Child = g,
            };
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
