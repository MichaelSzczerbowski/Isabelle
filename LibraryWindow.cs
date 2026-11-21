using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>
    /// okno „Moja muzyka” - pliki z dysku i radio internetowe. Odtwarzanie idzie przez
    /// <see cref="LocalPlayer"/>, a sterowanie (pauza, następny, tekst, okładka) przejmuje wyspa.
    /// </summary>
    public sealed class LibraryWindow : Window
    {
        private static LibraryWindow _current;

        /// <summary>
        /// Otwarcie z wyspy: okno pojawia się tuż pod nią i „wysuwa się” z niej (powiększenie od góry + pojawienie).
        /// </summary>
        public static void OpenFromIsland(Point screenPx)
        {
            // już otwarte - tylko wyciągamy na wierzch (bez ponownej animacji, która dawała mignięcie)
            if (_current != null)
            {
                if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
                _current.Activate();
                return;
            }
            var w = new LibraryWindow();
            _current = w;
            w.Closed += (s, e) => _current = null;
            // piksele ekranu → jednostki WPF (skala monitora)
            var src = PresentationSource.FromVisual(App.Island);
            double sx = src?.CompositionTarget?.TransformFromDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformFromDevice.M22 ?? 1;
            double x = screenPx.X * sx, y = screenPx.Y * sy;
            var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)screenPx.X, (int)screenPx.Y)).WorkingArea;
            double areaL = area.Left * sx, areaT = area.Top * sy, areaR = area.Right * sx, areaB = area.Bottom * sy;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            // okno ma 18 px marginesu na cień - wyrównujemy samą kartę
            w.Left = Math.Clamp(x - w.Width / 2, areaL, Math.Max(areaL, areaR - w.Width));
            w.Top = Math.Clamp(y - 8, areaT, Math.Max(areaT, areaB - w.Height));
            // stan początkowy ustawiamy PRZED pokazaniem - pierwsza klatka jest niewidoczna i już pomniejszona,
            // więc nie ma mignięcia pełnego okna przed startem animacji
            var origin = new Point(x - w.Left, Math.Max(0, y - w.Top));
            w.Opacity = 0;
            w.PrepareIn(origin);
            w.ContentRendered += (s, e) =>
                // start dopiero po pierwszym wyrenderowaniu (budowa list, szkło), żeby animacja nie gubiła klatek
                w.Dispatcher.BeginInvoke(new Action(w.PlayIn), System.Windows.Threading.DispatcherPriority.Background);
            w.Show();
            w.Activate();
        }

        private ScaleTransform _inScale;

        /// <summary>Karta pomniejszona przy punkcie pod wyspą, gotowa do „wysunięcia się”.</summary>
        private void PrepareIn(Point origin)
        {
            if (!(Content is FrameworkElement root)) return;
            root.RenderTransformOrigin = new Point(Math.Clamp(origin.X / Width, 0, 1), Math.Clamp(origin.Y / Height, 0, 1));
            _inScale = new ScaleTransform(0.86, 0.6);
            root.RenderTransform = _inScale;
        }

        /// <summary>Wejście: karta rośnie z punktu pod wyspą i się pojawia.</summary>
        private void PlayIn()
        {
            var d = TimeSpan.FromMilliseconds(340);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            var back = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.2 };
            if (_inScale != null)
            {
                var ax = new System.Windows.Media.Animation.DoubleAnimation(1, d) { EasingFunction = back };
                ax.Completed += (s, e) => { if (Content is FrameworkElement r) r.RenderTransform = Transform.Identity; };
                _inScale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
                _inScale.BeginAnimation(ScaleTransform.ScaleYProperty, new System.Windows.Media.Animation.DoubleAnimation(1, d) { EasingFunction = back });
            }
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            fade.Completed += (s, e) => { BeginAnimation(OpacityProperty, null); Opacity = 1; };
            BeginAnimation(OpacityProperty, fade);
        }

        public static void ShowWindow()
        {
            if (_current != null) { _current.Activate(); return; }
            _current = new LibraryWindow();
            _current.Closed += (s, e) => _current = null;
            _current.Show();
            _current.Activate();
        }

        private static readonly Brush TextSub = Hex("#8CFFFFFF");
        private static readonly Brush Glass = Hex("#0DFFFFFF");
        private static readonly Brush GlassHover = Hex("#17FFFFFF");
        private static readonly Brush Hairline = Hex("#12FFFFFF");

        private readonly SolidColorBrush _accent;
        private readonly GlassBackground _glass = new GlassBackground();
        private string _glassPath;
        private BitmapSource _glassLogo;
        private readonly Border _tabMusic, _tabRadio;
        private readonly Grid _pageMusic = new Grid(), _pageRadio = new Grid();
        private readonly ListBox _tracksList = MakeList();
        private readonly TextBox _search = MakeBox();
        private readonly TextBlock _folderText = new TextBlock(), _status = new TextBlock();
        private List<LibraryTrack> _shown = new List<LibraryTrack>();

        private LibraryWindow()
        {
            Title = L.T("Isabelle – moja muzyka", "Isabelle – my music");
            Width = 960;
            Height = 690;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            _accent = new SolidColorBrush(App.Island?.AccentColor ?? Color.FromRgb(255, 92, 160));

            var outer = new Grid { Margin = new Thickness(18) };
            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(24), Background = Brushes.Black, Opacity = 0.55, Margin = new Thickness(6, 12, 6, 0),
                Effect = new BlurEffect { Radius = 22 }, IsHitTestVisible = false,
            });
            // szkło: płynące kolory okładki / logo pod przyciemnioną szybą, z jasną krawędzią
            var card = new Border
            {
                CornerRadius = new CornerRadius(26), Background = Brushes.Transparent, BorderBrush = Hex("#2EFFFFFF"),
                BorderThickness = new Thickness(1),
            };
            outer.Children.Add(card);
            Content = outer;
            var inner = new Grid();
            var clip = new RectangleGeometry { RadiusX = 25, RadiusY = 25 };
            inner.Clip = clip;
            inner.SizeChanged += (s, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            inner.Children.Add(_glass);
            card.Child = inner;

            var root = new Grid { Margin = new Thickness(26, 20, 26, 22) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            inner.Children.Add(root);

            // ---- nagłówek: tytuł, zakładki, zamknij ----
            var head = new Grid { Margin = new Thickness(0, 0, 0, 16), Background = Brushes.Transparent };
            head.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = L.T("Moja muzyka", "My music"), FontSize = 24, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(titleRow);

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 44, 0) };
            _tabMusic = Tab(L.T("Pliki", "Files"), () => ShowTab(0));
            _tabRadio = Tab(L.T("Radio", "Radio"), () => ShowTab(1));
            tabs.Children.Add(_tabMusic);
            tabs.Children.Add(_tabRadio);
            head.Children.Add(tabs);

            var close = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Hex("#17FFFFFF"), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Child = new System.Windows.Shapes.Path
                {
                    Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stroke = Brushes.White, StrokeThickness = 1.6,
                    Width = 10, Height = 10, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            close.MouseLeftButtonDown += (s, e) => e.Handled = true;
            close.MouseLeftButtonUp += (s, e) => Close();
            head.Children.Add(close);
            root.Children.Add(head);

            var pages = new Grid();
            Grid.SetRow(pages, 1);
            pages.Children.Add(_pageMusic);
            pages.Children.Add(_pageRadio);
            root.Children.Add(pages);

            BuildMusic();
            BuildRadio();
            ShowTab(App.Config.LibraryTab);

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            // kolory tła za tym, co gra: logo stacji albo okładka z wyspy
            var colorTick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            colorTick.Tick += (s, e) => UpdateGlass();
            colorTick.Start();
            Closed += (s, e) => { colorTick.Stop(); App.TrimMemorySoon(); };
            UpdateGlass();
            LibraryService.Changed += OnLibraryChanged;
            Closed += (s, e) => LibraryService.Changed -= OnLibraryChanged;
        }

        // ================= pliki =================
        private void BuildMusic()
        {
            _pageMusic.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _pageMusic.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _pageMusic.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _pageMusic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
            _pageMusic.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- lewy pasek: wszystkie / polubione / playlisty ----
            var sideScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = _side, Margin = new Thickness(0, 0, 16, 0) };
            Grid.SetRowSpan(sideScroll, 3);
            _pageMusic.Children.Add(sideScroll);
            PlaylistStore.Changed += OnPlaylistsChanged;
            Closed += (s, e) => PlaylistStore.Changed -= OnPlaylistsChanged;
            PlayStats.Changed += OnStatsChanged;
            Closed += (s, e) => PlayStats.Changed -= OnStatsChanged;

            // folder + przyciski
            var bar = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _folderText.FontSize = 12.5;
            _folderText.Foreground = TextSub;
            _folderText.TextTrimming = TextTrimming.CharacterEllipsis;
            _folderText.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(_folderText);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            Grid.SetColumn(buttons, 1);
            buttons.Children.Add(Pill(L.T("Dodaj pliki", "Add files"), false, AddFiles));
            buttons.Children.Add(Pill(L.T("Wybierz folder", "Choose folder"), false, ChooseFolder));
            Border watchPill = null, eqPill = null;
            watchPill = Pill(L.T("Śledź folder", "Watch folder"), false, () => WatchMenu(watchPill));
            watchPill.ToolTip = L.T("Nowa muzyka z wybranego folderu (np. Pobrane) sama trafi do biblioteki", "New music from a chosen folder (e.g. Downloads) is added to your library automatically");
            buttons.Children.Add(watchPill);
            void OpenEq()
            {
                var menu = new GlassMenu(_accent);
                menu.Custom(EqualizerPanel.Build(_accent, this, () => Dispatcher.BeginInvoke(new Action(OpenEq))));
                menu.Show(eqPill);
            }
            eqPill = Pill(L.T("Korektor", "Equalizer"), false, OpenEq);
            buttons.Children.Add(eqPill);
            buttons.Children.Add(PlayButton());
            bar.Children.Add(buttons);
            Grid.SetColumn(bar, 1);
            _pageMusic.Children.Add(bar);

            // szukanie
            var searchRow = SearchRow(_search, L.T("Szukaj utworu lub wykonawcy…", "Search tracks or artists…"));
            Grid.SetRow(searchRow, 1);
            Grid.SetColumn(searchRow, 1);
            _search.TextChanged += (s, e) => Filter();
            _pageMusic.Children.Add(searchRow);

            // lista
            var listHost = ListHost(_tracksList, _status);
            Grid.SetRow(listHost, 2);
            Grid.SetColumn(listHost, 1);
            _pageMusic.Children.Add(listHost);

            // przeciąganie plików: nakładka „upuść tutaj”
            _dropHint.Child = new TextBlock
            {
                Text = L.T("Upuść pliki muzyczne, żeby dodać je do biblioteki", "Drop music files to add them to your library"),
                FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            _dropHint.Background = Hex("#CC0B0B0D");
            _dropHint.BorderBrush = _accent;
            _dropHint.BorderThickness = new Thickness(2);
            _dropHint.CornerRadius = new CornerRadius(16);
            _dropHint.Visibility = Visibility.Collapsed;
            _dropHint.IsHitTestVisible = false;
            Grid.SetRow(_dropHint, 2);
            Grid.SetColumn(_dropHint, 1);
            _pageMusic.Children.Add(_dropHint);
            AllowDrop = true;
            DragEnter += OnDrag;
            DragOver += OnDrag;
            DragLeave += (s, e) => _dropHint.Visibility = Visibility.Collapsed;
            Drop += OnDrop;
            _tracksList.ItemTemplate = TrackTemplate();
            // jedno kliknięcie w utwór = od razu gra (dalej leci kolejnymi z listy)
            _tracksList.PreviewMouseLeftButtonUp += (s, e) =>
            {
                if (IsInsideHeart(e.OriginalSource as DependencyObject)) return;   // serduszko - nie odtwarzamy
                if (ItemsControl.ContainerFromElement(_tracksList, e.OriginalSource as DependencyObject) is ListBoxItem item)
                {
                    int index = _tracksList.ItemContainerGenerator.IndexFromContainer(item);
                    if (index >= 0) Play(index, false);
                }
            };
            _tracksList.KeyDown += (s, e) => { if (e.Key == Key.Enter && _tracksList.SelectedIndex >= 0) Play(_tracksList.SelectedIndex, false); };
            _tracksList.PreviewMouseRightButtonUp += TrackMenu;

            LibraryService.LoadCache();
            UpdateFolder();
            RefreshSidebar();
            Filter();
            if (!string.IsNullOrEmpty(App.Config.LibraryFolder)) _ = LibraryService.ScanAsync(App.Config.LibraryFolder);
        }

        private readonly Border _dropHint = new Border();

        // ================= polubione i playlisty =================
        private readonly StackPanel _side = new StackPanel();
        private string _view = "all";     // all | liked | pl:{id}

        /// <summary>Inteligentne playlisty - układają się same ze statystyk słuchania i dat dodania.</summary>
        private static readonly (string key, string pl, string en, string icon)[] SmartLists =
        {
            ("smart:top", "Najczęściej grane", "Most played", "M12,2 L14.9,8.6 L22,9.3 L16.6,14 L18.2,21 L12,17.3 L5.8,21 L7.4,14 L2,9.3 L9.1,8.6 Z"),
            ("smart:new", "Ostatnio dodane", "Recently added", "M12,2 C12.8,7.5 16.5,11.2 22,12 C16.5,12.8 12.8,16.5 12,22 C11.2,16.5 7.5,12.8 2,12 C7.5,11.2 11.2,7.5 12,2 Z"),
            ("smart:old", "Dawno niesłuchane", "Not played lately", "M6,2 H18 V6.5 L13.6,12 L18,17.5 V22 H6 V17.5 L10.4,12 L6,6.5 Z"),
        };
        private const int SmartLimit = 50;

        private void OnStatsChanged() => Dispatcher.BeginInvoke(new Action(() => { if (_view.StartsWith("smart:")) Filter(); }));

        /// <summary>Menu „Śledź folder”: Pobrane jednym kliknięciem, inny folder albo wyłączenie.</summary>
        private void WatchMenu(FrameworkElement anchor)
        {
            var menu = new GlassMenu(_accent);
            string cur = App.Config.WatchFolder ?? "";
            menu.Header(cur.Length > 0
                ? L.T("Śledzony: ", "Watching: ") + System.IO.Path.GetFileName(cur.TrimEnd('\\'))
                : L.T("Nowa muzyka z folderu sama trafi do biblioteki", "New music from a folder joins your library"));
            string downloads = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (System.IO.Directory.Exists(downloads))
                menu.Check(L.T("Pobrane", "Downloads"), GlassMenu.ListIcon, string.Equals(cur, downloads, StringComparison.OrdinalIgnoreCase), on => SetWatch(on ? downloads : ""));
            menu.Item(L.T("Wybierz inny folder…", "Choose another folder…"), GlassMenu.PlusIcon, () =>
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = L.T("Folder, z którego nowa muzyka ma trafiać do biblioteki", "Folder whose new music should join your library"),
                    UseDescriptionForTitle = true,
                    SelectedPath = cur.Length > 0 ? cur : downloads,
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) SetWatch(dlg.SelectedPath);
            });
            if (cur.Length > 0)
            {
                menu.Separator();
                menu.Item(L.T("Przestań śledzić", "Stop watching"), GlassMenu.MinusIcon, () => SetWatch(""), danger: true);
            }
            menu.Show(anchor);
        }

        private void SetWatch(string folder)
        {
            App.Config.WatchFolder = folder ?? "";
            App.Config.Save();
            LibraryService.StartWatching();
            _folderText.Text = folder?.Length > 0
                ? L.T("Śledzę: ", "Watching: ") + folder + L.T(" - nowa muzyka sama się doda", " - new music will be added automatically")
                : (App.Config.LibraryFolder ?? "");
        }

        private void OnPlaylistsChanged() => Dispatcher.BeginInvoke(new Action(() =>
        {
            RefreshSidebar();
            if (_view != "all") Filter();
            foreach (var t in _shown) t.NotifyLiked();     // ikonki „+” / ✓ po zmianie playlist
        }));

        private void RefreshSidebar()
        {
            _side.Children.Clear();
            int all = LibraryService.Tracks.Count;
            _side.Children.Add(SideItem(L.T("Wszystkie utwory", "All tracks"), all, "all", null));
            _side.Children.Add(SideItem(L.T("Polubione", "Liked"), PlaylistStore.LikedCount, "liked", "M12,21.35 L10.55,20.03 C5.4,15.36 2,12.28 2,8.5 C2,5.42 4.42,3 7.5,3 C9.24,3 10.91,3.81 12,5.09 C13.09,3.81 14.76,3 16.5,3 C19.58,3 22,5.42 22,8.5 C22,12.28 18.6,15.36 13.45,20.04 Z"));
            _side.Children.Add(new TextBlock { Text = L.T("Dla Ciebie", "Made for you"), FontSize = 12, Foreground = TextSub, Margin = new Thickness(12, 16, 0, 4) });
            foreach (var (key, pl, en, icon) in SmartLists)
            {
                var smart = SideItem(L.T(pl, en), -1, key, icon);
                string k = key;
                smart.MouseRightButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    var menu = new GlassMenu(_accent);
                    menu.Item(L.T("Odtwórz", "Play"), GlassMenu.PlayIcon, () => { _view = k; RefreshSidebar(); Filter(); Play(0, false); });
                    menu.Item(L.T("Odtwórz losowo", "Shuffle"), GlassMenu.ShuffleIcon, () => { _view = k; RefreshSidebar(); Filter(); Play(0, true); });
                    menu.Show(smart, atMouse: true);
                };
                _side.Children.Add(smart);
            }
            _side.Children.Add(new TextBlock { Text = L.T("Playlisty", "Playlists"), FontSize = 12, Foreground = TextSub, Margin = new Thickness(12, 16, 0, 4) });
            foreach (var pl in PlaylistStore.Playlists)
            {
                var item = SideItem(pl.Name, pl.Paths.Count, "pl:" + pl.Id, null);
                string id = pl.Id, name = pl.Name;
                item.MouseRightButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    var menu = new GlassMenu(_accent);
                    menu.Item(L.T("Odtwórz", "Play"), GlassMenu.PlayIcon, () => { _view = "pl:" + id; RefreshSidebar(); Filter(); Play(0, false); });
                    menu.Item(L.T("Odtwórz losowo", "Shuffle"), GlassMenu.ShuffleIcon, () => { _view = "pl:" + id; RefreshSidebar(); Filter(); Play(0, true); });
                    menu.Item(L.T("Zmień nazwę…", "Rename…"), GlassMenu.EditIcon, () =>
                    {
                        var n = InputDialog.Ask(this, L.T("Zmień nazwę playlisty", "Rename playlist"), name, L.T("Zapisz", "Save"));
                        if (n != null) PlaylistStore.Rename(id, n);
                    });
                    menu.Separator();
                    menu.Item(L.T("Usuń playlistę", "Delete playlist"), GlassMenu.TrashIcon, () =>
                    {
                        if (_view == "pl:" + id) _view = "all";
                        PlaylistStore.Delete(id);
                        Filter();
                    }, danger: true);
                    menu.Show(item, atMouse: true);
                };
                _side.Children.Add(item);
            }
            var add = SideItem("+  " + L.T("Nowa playlista", "New playlist"), -1, null, null);
            ((TextBlock)((DockPanel)add.Child).Children[^1]).Foreground = TextSub;
            add.MouseLeftButtonUp += (s, e) =>
            {
                var n = InputDialog.Ask(this, L.T("Nowa playlista", "New playlist"), "", L.T("Utwórz", "Create"));
                if (n == null) return;
                var pl = PlaylistStore.Create(n);
                _view = "pl:" + pl.Id;
                RefreshSidebar();
                Filter();
            };
            _side.Children.Add(add);
        }

        /// <summary>Pozycja paska: nazwa, liczba utworów, opcjonalna ikonka; key = widok do pokazania po kliknięciu.</summary>
        private Border SideItem(string text, int count, string key, string icon)
        {
            var dock = new DockPanel();
            if (icon != null)
            {
                var ic = new System.Windows.Shapes.Path { Data = Geometry.Parse(icon), Fill = _accent, Width = 13, Height = 13, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(ic, Dock.Left);
                dock.Children.Add(ic);
            }
            bool selected = key != null && key == _view;
            dock.Children.Add(new TextBlock
            {
                Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = selected ? Brushes.White : Hex("#B3FFFFFF"), VerticalAlignment = VerticalAlignment.Center,
            });
            var b = new Border
            {
                Child = dock, CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 1),
                Background = selected ? Hex("#1FFFFFFF") : Brushes.Transparent, Cursor = Cursors.Hand,
            };
            b.MouseEnter += (s, e) => { if (!selected) b.Background = Hex("#0DFFFFFF"); };
            b.MouseLeave += (s, e) => { if (!selected) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            if (key != null)
                b.MouseLeftButtonUp += (s, e) => { _view = key; RefreshSidebar(); Filter(); };
            return b;
        }

        private static bool IsInsideHeart(DependencyObject d)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.Tag as string == "heart") return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        /// <summary>
        /// Kliknięcie „+”: menu jak w Spotify - zaznaczasz, gdzie ma być utwór (Polubione i dowolne playlisty),
        /// można zaznaczyć kilka naraz, menu zostaje otwarte. Na dole „Nowa playlista”.
        /// </summary>
        private void OnHeart(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!(sender is FrameworkElement el) || !(el.DataContext is LibraryTrack t)) return;
            var menu = new GlassMenu(_accent);
            menu.Header(L.T("Zapisz w", "Save to"));
            menu.Check(L.T("Polubione", "Liked"), GlassMenu.HeartIcon, t.Liked, on =>
            {
                if (on != t.Liked) PlaylistStore.ToggleLike(t.Path);
                t.NotifyLiked();
                Pop(el, t.Saved);
            });
            if (PlaylistStore.Playlists.Count > 0) menu.Separator();
            foreach (var pl in PlaylistStore.Playlists)
            {
                string id = pl.Id;
                menu.Check(pl.Name, GlassMenu.ListIcon, pl.Paths.Contains(t.Path, StringComparer.OrdinalIgnoreCase), on =>
                {
                    if (on) PlaylistStore.Add(id, t.Path); else PlaylistStore.Remove(id, t.Path);
                    t.NotifyLiked();
                    Pop(el, t.Saved);
                });
            }
            menu.Separator();
            menu.Item(L.T("Nowa playlista…", "New playlist…"), GlassMenu.PlusIcon, () =>
            {
                var n = InputDialog.Ask(this, L.T("Nowa playlista", "New playlist"), "", L.T("Utwórz", "Create"));
                if (n == null) return;
                PlaylistStore.Create(n, new[] { t.Path });
                t.NotifyLiked();
                Pop(el, true);
            });
            menu.Show(el);
        }

        /// <summary>„Pyknięcie” ikonki jak w Spotify: szybkie powiększenie i powrót ze sprężynką.</summary>
        private static void Pop(FrameworkElement el, bool added)
        {
            if (!(el is Border b) || !(b.Child is FrameworkElement icon)) return;
            var sc = new ScaleTransform(1, 1);
            icon.RenderTransform = sc;
            var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(380) };
            anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(added ? 1.3 : 0.8, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
                new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }));
            anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)),
                new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.5 }));
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        /// <summary>Menu pod prawym przyciskiem na utworze: polub, dodaj do playlisty, usuń z playlisty.</summary>
        private void TrackMenu(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(_tracksList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item?.DataContext is not LibraryTrack t) return;
            e.Handled = true;
            var menu = new GlassMenu(_accent);
            menu.Item(L.T("Odtwórz", "Play"), GlassMenu.PlayIcon, () => Play(_shown.IndexOf(t), false));
            menu.Check(L.T("Polubione", "Liked"), GlassMenu.HeartIcon, t.Liked, on =>
            {
                if (on != t.Liked) PlaylistStore.ToggleLike(t.Path);
                t.NotifyLiked();
            });
            if (PlaylistStore.Playlists.Count > 0)
            {
                menu.Separator();
                menu.Header(L.T("Dodaj do playlisty", "Add to playlist"));
                foreach (var pl in PlaylistStore.Playlists)
                {
                    string id = pl.Id;
                    menu.Check(pl.Name, GlassMenu.ListIcon, pl.Paths.Contains(t.Path, StringComparer.OrdinalIgnoreCase), on =>
                    {
                        if (on) PlaylistStore.Add(id, t.Path); else PlaylistStore.Remove(id, t.Path);
                        t.NotifyLiked();
                    });
                }
            }
            menu.Separator();
            menu.Item(L.T("Nowa playlista z tym utworem…", "New playlist with this track…"), GlassMenu.PlusIcon, () =>
            {
                var n = InputDialog.Ask(this, L.T("Nowa playlista", "New playlist"), "", L.T("Utwórz", "Create"));
                if (n != null) { PlaylistStore.Create(n, new[] { t.Path }); t.NotifyLiked(); }
            });
            if (_view.StartsWith("pl:"))
            {
                string id = _view.Substring(3);
                menu.Item(L.T("Usuń z tej playlisty", "Remove from this playlist"), GlassMenu.MinusIcon, () => { PlaylistStore.Remove(id, t.Path); t.NotifyLiked(); }, danger: true);
            }
            menu.Show(_tracksList, atMouse: true);
        }

        private void OnDrag(object sender, DragEventArgs e)
        {
            bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            if (files && _pageMusic.Visibility != Visibility.Visible) ShowTab(0);
            _dropHint.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            _dropHint.Visibility = Visibility.Collapsed;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Import(paths);
        }

        private void AddFiles()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("Dodaj muzykę do biblioteki", "Add music to your library"),
                Multiselect = true,
                Filter = L.T("Muzyka", "Music") + "|*.mp3;*.flac;*.m4a;*.aac;*.wav;*.wma|" + L.T("Wszystkie pliki", "All files") + "|*.*",
            };
            if (dlg.ShowDialog(this) == true) Import(dlg.FileNames);
        }

        private async void Import(string[] paths)
        {
            _status.Text = L.T("Dodaję muzykę…", "Adding music…");
            _status.Visibility = Visibility.Visible;
            int n = await LibraryService.ImportAsync(paths);
            UpdateFolder();
            Filter();
            _folderText.Text = (n > 0
                ? string.Format(L.T("Dodano {0} utw. · ", "Added {0} tracks · "), n)
                : L.T("Nic nowego do dodania · ", "Nothing new to add · ")) + App.Config.LibraryFolder;
        }

        private void ChooseFolder()
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = L.T("Wybierz folder z muzyką", "Choose your music folder"),
                UseDescriptionForTitle = true,
                SelectedPath = string.IsNullOrEmpty(App.Config.LibraryFolder)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) : App.Config.LibraryFolder,
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            App.Config.LibraryFolder = dlg.SelectedPath;
            App.Config.Save();
            LibraryService.StartWatching();
            UpdateFolder();
            _ = LibraryService.ScanAsync(dlg.SelectedPath);
        }

        private void UpdateFolder() =>
            _folderText.Text = string.IsNullOrEmpty(App.Config.LibraryFolder)
                ? L.T("Nie wybrano folderu z muzyką", "No music folder chosen")
                : App.Config.LibraryFolder;

        private void OnLibraryChanged() => Dispatcher.BeginInvoke(new Action(() => { Filter(); RefreshSidebar(); }));

        private void Filter()
        {
            string q = (_search.Text ?? "").Trim();
            var all = LibraryService.Tracks.ToList();
            IEnumerable<LibraryTrack> view = all;
            if (_view == "liked") view = all.Where(t => t.Liked);
            else if (_view == "smart:top")
                view = all.Where(t => PlayStats.Plays(t.Path) > 0)
                          .OrderByDescending(t => PlayStats.Plays(t.Path)).ThenByDescending(t => PlayStats.Last(t.Path)).Take(SmartLimit);
            else if (_view == "smart:new") view = all.OrderByDescending(t => t.Added).Take(SmartLimit);
            else if (_view == "smart:old")
            {
                // nie grane od miesiąca albo nigdy - najdawniej słuchane na górze
                long monthAgo = DateTime.UtcNow.AddDays(-30).Ticks;
                view = all.Where(t => PlayStats.Last(t.Path) < monthAgo).OrderBy(t => PlayStats.Last(t.Path)).ThenBy(t => t.Added).Take(SmartLimit);
            }
            else if (_view.StartsWith("pl:"))
            {
                // playlista: w jej kolejności, pomijając pliki, których już nie ma
                var byPath = all.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var pl = PlaylistStore.Find(_view.Substring(3));
                view = pl == null ? Enumerable.Empty<LibraryTrack>() : pl.Paths.Where(byPath.ContainsKey).Select(x => byPath[x]);
            }
            var list = view.ToList();
            _shown = q.Length == 0 ? list
                : list.Where(t => (t.Title + " " + t.Artist + " " + t.Album).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            _tracksList.ItemsSource = _shown;
            _status.Text = all.Count == 0
                ? (string.IsNullOrEmpty(App.Config.LibraryFolder)
                    ? L.T("Przeciągnij tu pliki MP3, kliknij „Dodaj pliki” albo wskaż folder, w którym trzymasz muzykę.", "Drag MP3 files here, click “Add files” or choose the folder where your music is.")
                    : LibraryService.Scanning ? L.T("Szukam muzyki…", "Looking for music…") : L.T("Brak plików muzycznych w tym folderze.", "No music files in this folder."))
                : _shown.Count == 0 && q.Length == 0 && _view == "liked" ? L.T("Kliknij serduszko przy utworze, żeby go polubić.", "Click the heart next to a track to like it.")
                : _shown.Count == 0 && q.Length == 0 && _view == "smart:top" ? L.T("Tu pojawią się utwory, których słuchasz najczęściej. Posłuchaj czegoś z biblioteki!", "Your most played tracks will show up here. Play something from your library!")
                : _shown.Count == 0 && q.Length == 0 && _view == "smart:old" ? L.T("Wszystko słuchane w ostatnim miesiącu 👌", "You've played everything in the last month 👌")
                : _shown.Count == 0 && q.Length == 0 && _view.StartsWith("pl:") ? L.T("Playlista jest pusta. Kliknij prawym przyciskiem utwór i wybierz „Dodaj do playlisty”.", "This playlist is empty. Right-click a track and choose “Add to playlist”.")
                : "";
            _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Play(int index, bool shuffle)
        {
            if (_shown.Count == 0) return;
            if (shuffle) index = new Random().Next(_shown.Count);
            LocalPlayer.PlayTracks(_shown, Math.Clamp(index, 0, _shown.Count - 1), shuffle);
        }

        private sealed class VisibleConverter : System.Windows.Data.IValueConverter
        {
            private readonly bool _whenTrue;
            public VisibleConverter(bool whenTrue) => _whenTrue = whenTrue;
            public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) =>
                (value is bool b && b) == _whenTrue ? Visibility.Visible : Visibility.Collapsed;
            public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
        }

        /// <summary>Kolor serduszka: polubione = pełne w kolorze akcentu, inaczej samo jasne obramowanie.</summary>
        private sealed class HeartConverter : System.Windows.Data.IValueConverter
        {
            private readonly Brush _accent;
            private readonly bool _fill;
            public HeartConverter(Brush accent, bool fill) { _accent = accent; _fill = fill; }
            public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
            {
                bool liked = value is bool b && b;
                if (_fill) return liked ? _accent : Brushes.Transparent;
                return liked ? _accent : Hex("#59FFFFFF");
            }
            public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
        }

        /// <summary>Miniatura okładki z pliku (mała, bez blokowania pliku); pusta ścieżka = brak obrazka.</summary>
        private sealed class CoverConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
            {
                if (value is string path && path.Length > 0 && System.IO.File.Exists(path))
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(path);
                        bmp.DecodePixelWidth = 96;
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        return bmp;
                    }
                    catch { }
                }
                return null;
            }
            public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
        }

        private DataTemplate TrackTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            var c0 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c0.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            var c1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c1.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            var c2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            c2.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            grid.AppendChild(c0);
            grid.AppendChild(c1);
            grid.AppendChild(c2);

            // okładka 42 px (zaokrąglona), pod spodem nutka gdy okładki brak
            var art = new FrameworkElementFactory(typeof(Grid));
            art.SetValue(FrameworkElement.WidthProperty, 42.0);
            art.SetValue(FrameworkElement.HeightProperty, 42.0);
            art.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0));
            art.SetValue(UIElement.ClipProperty, new RectangleGeometry(new Rect(0, 0, 42, 42), 8, 8));
            var artBg = new FrameworkElementFactory(typeof(Border));
            artBg.SetValue(Border.BackgroundProperty, Hex("#1FFFFFFF"));
            art.AppendChild(artBg);
            var note = new FrameworkElementFactory(typeof(TextBlock));
            note.SetValue(TextBlock.TextProperty, "♪");
            note.SetValue(TextBlock.FontSizeProperty, 18.0);
            note.SetValue(TextBlock.ForegroundProperty, Hex("#66FFFFFF"));
            note.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            note.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            art.AppendChild(note);
            var img = new FrameworkElementFactory(typeof(Image));
            img.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding("CoverPath") { Converter = new CoverConverter() });
            img.SetValue(Image.StretchProperty, Stretch.UniformToFill);
            art.AppendChild(img);
            grid.AppendChild(art);

            var texts = new FrameworkElementFactory(typeof(StackPanel));
            texts.SetValue(Grid.ColumnProperty, 1);
            texts.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Title"));
            title.SetValue(TextBlock.FontSizeProperty, 14.0);
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            var sub = new FrameworkElementFactory(typeof(TextBlock));
            sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Artist"));
            sub.SetValue(TextBlock.FontSizeProperty, 12.0);
            sub.SetValue(TextBlock.ForegroundProperty, TextSub);
            sub.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            texts.AppendChild(title);
            texts.AppendChild(sub);
            grid.AppendChild(texts);

            // plakietka „LRC” / „tekst”, gdy utwór ma zapisany tekst
            var right = new FrameworkElementFactory(typeof(StackPanel));
            right.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            right.SetValue(Grid.ColumnProperty, 2);
            right.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            // „+” jak w Spotify: kółko z plusem, a gdy utwór jest polubiony / na playliście - pełne kółko z ptaszkiem
            var heartBtn = new FrameworkElementFactory(typeof(Border));
            heartBtn.SetValue(FrameworkElement.TagProperty, "heart");
            heartBtn.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            heartBtn.SetValue(Border.PaddingProperty, new Thickness(6));
            heartBtn.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
            heartBtn.SetValue(FrameworkElement.CursorProperty, Cursors.Hand);
            heartBtn.SetValue(FrameworkElement.ToolTipProperty, L.T("Polub albo dodaj do playlisty", "Like or add to a playlist"));
            heartBtn.AddHandler(UIElement.MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnHeart));
            var icon = new FrameworkElementFactory(typeof(Grid));
            icon.SetValue(FrameworkElement.WidthProperty, 18.0);
            icon.SetValue(FrameworkElement.HeightProperty, 18.0);
            icon.SetValue(UIElement.RenderTransformOriginProperty, new Point(0.5, 0.5));
            var plus = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            plus.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M1,10 A9,9 0 1 1 19,10 A9,9 0 1 1 1,10 Z M10,6 L10,14 M6,10 L14,10"));
            plus.SetValue(System.Windows.Shapes.Shape.StretchProperty, Stretch.Uniform);
            plus.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.6);
            plus.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            plus.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            plus.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Hex("#8CFFFFFF"));
            plus.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Saved") { Converter = new VisibleConverter(false) });
            icon.AppendChild(plus);
            var done = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            done.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M10,0 A10,10 0 1 1 9.99,0 Z"));
            done.SetValue(System.Windows.Shapes.Shape.StretchProperty, Stretch.Uniform);
            done.SetValue(System.Windows.Shapes.Shape.FillProperty, _accent);
            done.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Saved") { Converter = new VisibleConverter(true) });
            icon.AppendChild(done);
            var check = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
            check.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M5.5,10.2 L8.6,13.2 L14.5,7"));
            check.SetValue(FrameworkElement.WidthProperty, 10.0);
            check.SetValue(FrameworkElement.HeightProperty, 8.0);
            check.SetValue(System.Windows.Shapes.Shape.StretchProperty, Stretch.Uniform);
            check.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 2.0);
            check.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
            check.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
            check.SetValue(System.Windows.Shapes.Shape.StrokeLineJoinProperty, PenLineJoin.Round);
            check.SetValue(System.Windows.Shapes.Shape.StrokeProperty, Brushes.Black);
            check.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Saved") { Converter = new VisibleConverter(true) });
            icon.AppendChild(check);
            heartBtn.AppendChild(icon);
            right.AppendChild(heartBtn);
            var badge = new FrameworkElementFactory(typeof(TextBlock));
            badge.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("LyricsBadge"));
            badge.SetValue(TextBlock.FontSizeProperty, 10.5);
            badge.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            badge.SetValue(TextBlock.ForegroundProperty, Hex("#8CFFFFFF"));
            badge.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            badge.SetValue(FrameworkElement.ToolTipProperty, L.T("Tekst piosenki zapisany - w wyspie pojawi się od razu", "Lyrics saved - they show up in the island instantly"));
            right.AppendChild(badge);
            var time = new FrameworkElementFactory(typeof(TextBlock));
            time.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Time"));
            time.SetValue(TextBlock.FontSizeProperty, 12.0);
            time.SetValue(TextBlock.ForegroundProperty, TextSub);
            time.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            time.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 4, 0));
            right.AppendChild(time);
            grid.AppendChild(right);
            return new DataTemplate { VisualTree = grid };
        }

        // ================= radio =================
        private void BuildRadio() => _pageRadio.Children.Add(new RadioView(_accent));

        private void UpdateGlass()
        {
            BitmapSource src = null;
            string path = LocalPlayer.LogoPath;
            if (path != null)
            {
                if (path != _glassPath)
                {
                    _glassPath = path;
                    try
                    {
                        var b = new BitmapImage();
                        b.BeginInit();
                        b.UriSource = new Uri(path);
                        b.DecodePixelWidth = 96;
                        b.CacheOption = BitmapCacheOption.OnLoad;
                        b.EndInit();
                        b.Freeze();
                        _glassLogo = b;
                    }
                    catch { _glassLogo = null; }
                }
                src = _glassLogo;
            }
            src ??= App.Island?.CoverImage;
            _glass.SetColors(src, App.Island?.AccentColor ?? Color.FromRgb(255, 92, 160));
        }

        // ================= wspólne =================
        private void ShowTab(int i)
        {
            _pageMusic.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            _pageRadio.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            PaintTab(_tabMusic, i == 0);
            PaintTab(_tabRadio, i == 1);
            if (App.Config.LibraryTab != i) { App.Config.LibraryTab = i; App.Config.Save(); }
        }

        private Border Tab(string text, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(15), Height = 30, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(4, 0, 0, 0), Cursor = Cursors.Hand,
                Child = CenteredText(text, 13, Brushes.White),
            };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        private void PaintTab(Border b, bool on)
        {
            b.Background = on ? Brushes.White : Glass;
            ((TextBlock)b.Child).Foreground = on ? Brushes.Black : Brushes.White;
        }

        private System.Windows.Shapes.Path _playIcon;
        private Border _playBtn;

        private void OnPlayerChangedForButton() => Dispatcher.BeginInvoke(new Action(RefreshPlayIcon));

        /// <summary>▶ gdy nic nie gra (albo pauza), ❚❚ gdy gra muzyka z biblioteki.</summary>
        private void RefreshPlayIcon()
        {
            if (_playIcon == null) return;
            bool playing = LocalPlayer.CurrentTrack != null && LocalPlayer.IsPlaying;
            string geo = playing ? "M0,0 H5 V16 H0 Z M9,0 H14 V16 H9 Z" : "M0,0 L14,8 L0,16 Z";
            if (_playIcon.Tag as string == geo) return;
            _playIcon.Tag = geo;
            _playIcon.Data = Geometry.Parse(geo);
            _playIcon.Margin = new Thickness(playing ? 0 : 3, 0, 0, 0);
            _playBtn.ToolTip = playing ? L.T("Pauza", "Pause")
                             : LocalPlayer.CurrentTrack != null ? L.T("Wznów", "Resume") : L.T("Odtwórz losowo", "Play shuffled");
        }

        /// <summary>Okrągły przycisk „graj” w kolorze akcentu (jak w Spotify) - odpala losowy utwór z biblioteki.</summary>
        private Border PlayButton()
        {
            var icon = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L14,8 L0,16 Z"), Fill = Brushes.Black, Width = 14, Height = 16, Stretch = Stretch.Uniform,
                Margin = new Thickness(3, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var b = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = _accent, Child = icon,
                Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand, ToolTip = L.T("Odtwórz losowo", "Play shuffled"),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            };
            var sc = (ScaleTransform)b.RenderTransform;
            b.MouseEnter += (s, e) => sc.ScaleX = sc.ScaleY = 1.06;
            b.MouseLeave += (s, e) => sc.ScaleX = sc.ScaleY = 1;
            b.MouseLeftButtonDown += (s, e) => { sc.ScaleX = sc.ScaleY = 0.94; e.Handled = true; };
            b.MouseLeftButtonUp += (s, e) =>
            {
                sc.ScaleX = sc.ScaleY = 1.06;
                e.Handled = true;
                // gra coś z biblioteki → pauza / wznowienie; nic nie gra → losowy utwór
                if (LocalPlayer.CurrentTrack != null) LocalPlayer.TogglePause();
                else Play(0, true);
                RefreshPlayIcon();
            };
            _playIcon = icon;
            _playBtn = b;
            LocalPlayer.Changed += OnPlayerChangedForButton;
            Closed += (s, e) => LocalPlayer.Changed -= OnPlayerChangedForButton;
            RefreshPlayIcon();
            return b;
        }

        /// <summary>
        /// Napis idealnie na środku przycisku: czcionka Segoe UI Variable ma duży zapas pod literami,
        /// więc ściskamy wysokość linii do rozmiaru liter i centrujemy w pionie.
        /// </summary>
        private static TextBlock CenteredText(string text, double size, Brush fg) => new TextBlock
        {
            Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = fg,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = size + 1,
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        };

        private Border Pill(string text, bool accent, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(17), Height = 34, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                Background = accent ? (Brush)_accent : Hex("#1FFFFFFF"), VerticalAlignment = VerticalAlignment.Center,
                Child = CenteredText(text, 13, accent ? Brushes.Black : Brushes.White),
            };
            b.MouseEnter += (s, e) => b.Opacity = 0.85;
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        private static Border SearchRow(TextBox box, string placeholder)
        {
            var g = new Grid();
            var hint = new TextBlock { Text = placeholder, Foreground = Hex("#55FFFFFF"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            box.TextChanged += (s, e) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            g.Children.Add(hint);
            g.Children.Add(box);
            return new Border
            {
                Background = Glass, BorderBrush = Hairline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 0, 0, 10), Child = g,
            };
        }

        private static Border ListHost(ListBox list, TextBlock status)
        {
            var g = new Grid();
            g.Children.Add(list);
            status.FontSize = 13;
            status.Foreground = TextSub;
            status.TextWrapping = TextWrapping.Wrap;
            status.TextAlignment = TextAlignment.Center;
            status.HorizontalAlignment = HorizontalAlignment.Center;
            status.VerticalAlignment = VerticalAlignment.Center;
            status.Margin = new Thickness(30);
            status.IsHitTestVisible = false;
            g.Children.Add(status);
            return new Border
            {
                Background = Glass, BorderBrush = Hairline, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                Padding = new Thickness(6), Child = g,
            };
        }

        private static TextBox MakeBox() => new TextBox
        {
            Background = Brushes.Transparent, Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 13.5,
            CaretBrush = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center,
        };

        private static ListBox MakeList()
        {
            var list = new ListBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            // ciemne, zaokrąglone wiersze zamiast systemowego niebieskiego zaznaczenia
            var item = new Style(typeof(ListBoxItem));
            item.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 7, 12, 7)));
            item.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            var tpl = new ControlTemplate(typeof(ListBoxItem));
            var bd = new FrameworkElementFactory(typeof(Border), "Bd");
            bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            bd.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            bd.SetValue(Border.PaddingProperty, new System.Windows.TemplateBindingExtension(Control.PaddingProperty));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            bd.AppendChild(cp);
            tpl.VisualTree = bd;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Hex("#12FFFFFF"), "Bd"));
            var sel = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            sel.Setters.Add(new Setter(Border.BackgroundProperty, Hex("#24FFFFFF"), "Bd"));
            tpl.Triggers.Add(hover);
            tpl.Triggers.Add(sel);
            item.Setters.Add(new Setter(Control.TemplateProperty, tpl));
            list.ItemContainerStyle = item;
            return list;
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
