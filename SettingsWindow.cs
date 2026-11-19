using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// Ustawienia w stylu odtwarzacza: czarne szkło, rozmyta okładka w tle, akcent w kolorze okładki,
    /// boczne menu. Wszystko budowane w kodzie. Przy pierwszym uruchomieniu - ekran powitalny.
    /// </summary>
    public sealed class SettingsWindow : Window
    {
        private static int _lastPage;

        private static readonly Brush TextSub = Hex("#8CFFFFFF");
        private static readonly Brush Glass = Hex("#0DFFFFFF");
        private static readonly Brush GlassHover = Hex("#17FFFFFF");
        private static readonly Brush Hairline = Hex("#12FFFFFF");

        private readonly Color _accent;
        private readonly SolidColorBrush _accentBrush;
        private readonly TextBlock _pageTitle = new TextBlock { FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White };
        private readonly ScrollViewer _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        private readonly List<Border> _navItems = new List<Border>();
        private readonly Grid _clipHost = new Grid();
        private readonly RectangleGeometry _clip = new RectangleGeometry { RadiusX = 24, RadiusY = 24 };

        private (string pl, string en, string icon, Func<StackPanel> build)[] _pages;
        private Action<Key, ModifierKeys> _capture;
        private Action _cancelCapture;

        public SettingsWindow(bool welcome)
        {
            Title = L.T("Isabelle – ustawienia", "Isabelle – settings");
            Width = 780;
            Height = 560;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            // kolor i okładka z wyspy - ustawienia wyglądają jak odtwarzacz
            var isl = App.Island;
            Color acc = isl?.AccentColor ?? Color.FromRgb(30, 215, 96);
            _accent = acc;
            _accentBrush = new SolidColorBrush(acc);
            _accentBrush.Freeze();

            _pages = new (string, string, string, Func<StackPanel>)[]
            {
                ("Odtwarzacz", "Player", IconNote, PagePlayer),
                ("Wygląd", "Appearance", IconSparkle, PageLook),
                ("Personalizacja", "Customize", IconPalette, PageCustomize),
                ("Dźwięk", "Sound", IconSound, PageSound),
                ("System", "System", IconSliders, PageSystem),
                ("Aktualizacje", "Updates", IconUpdate, PageUpdates),
            };

            Content = BuildShell(isl?.CoverImage);
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            // nagrywanie skrótu: następne naciśnięcie klawiszy trafia do wybranego wiersza
            PreviewKeyDown += (s, e) =>
            {
                if (_capture == null) return;
                e.Handled = true;
                Key k = e.Key == Key.System ? e.SystemKey : e.Key;
                _capture(k, Keyboard.Modifiers);
            };
            if (welcome) ShowWelcome();
            else ShowPage(_lastPage);
        }

        // ============================ szkielet okna ============================
        private UIElement BuildShell(BitmapSource cover)
        {
            var outer = new Grid { Margin = new Thickness(18) };

            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(24),
                Background = Brushes.Black,
                Opacity = 0.55,
                Margin = new Thickness(6, 12, 6, 0),
                Effect = new BlurEffect { Radius = 22, RenderingBias = RenderingBias.Performance },
                IsHitTestVisible = false,
            });

            var card = new Border
            {
                CornerRadius = new CornerRadius(24),
                Background = Hex("#0B0B0D"),
                BorderBrush = Hex("#1CFFFFFF"),
                BorderThickness = new Thickness(1),
                Child = _clipHost,
            };
            outer.Children.Add(card);
            _clipHost.Clip = _clip;
            _clipHost.SizeChanged += (s, e) => _clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

            // rozmyta okładka w tle (jak w odtwarzaczu)
            if (cover != null && App.Config.AlbumBackground)
            {
                _clipHost.Children.Add(new Image
                {
                    Source = cover,
                    Stretch = Stretch.UniformToFill,
                    Opacity = 0.5,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1.35, 1.35),
                    Effect = new BlurEffect { Radius = 90, RenderingBias = RenderingBias.Performance },
                    IsHitTestVisible = false,
                });
            }
            else
            {
                // bez okładki: delikatna poświata w kolorze akcentu
                var glow = new RadialGradientBrush(Color.FromArgb(70, _accent.R, _accent.G, _accent.B), Colors.Transparent)
                {
                    Center = new Point(0.15, 0.1), GradientOrigin = new Point(0.15, 0.1), RadiusX = 0.8, RadiusY = 0.9,
                };
                _clipHost.Children.Add(new Border { Background = glow, IsHitTestVisible = false });
            }
            _clipHost.Children.Add(new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(0xB8, 11, 11, 13), Color.FromArgb(0xF0, 11, 11, 13), 0),
                IsHitTestVisible = false,
            });

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _clipHost.Children.Add(layout);

            // ---- lewy pasek ----
            var side = new DockPanel { Margin = new Thickness(18, 20, 10, 18), LastChildFill = false };
            layout.Children.Add(side);
            var sideBg = new Border { Background = Hex("#08FFFFFF"), IsHitTestVisible = false };
            layout.Children.Insert(0, sideBg);
            layout.Children.Add(new Border { Width = 1, Background = Hairline, HorizontalAlignment = HorizontalAlignment.Right, IsHitTestVisible = false });

            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 18), Background = Brushes.Transparent };
            brand.MouseLeftButtonDown += Drag;
            brand.Children.Add(MiniIsland());
            brand.Children.Add(new TextBlock
            {
                Text = "Isabelle", FontSize = 15, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            DockPanel.SetDock(brand, Dock.Top);
            side.Children.Add(brand);

            var now = NowPlayingCard(cover);
            DockPanel.SetDock(now, Dock.Top);
            side.Children.Add(now);

            for (int i = 0; i < _pages.Length; i++)
            {
                var item = NavItem(i);
                DockPanel.SetDock(item, Dock.Top);
                side.Children.Add(item);
                _navItems.Add(item);
            }

            var footer = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
            var quit = new TextBlock
            {
                Text = L.T("Zamknij program", "Quit Isabelle"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = Hex("#FF6B61"), Cursor = Cursors.Hand,
            };
            quit.MouseLeftButtonUp += (s, e) => App.Quit();
            footer.Children.Add(quit);
            footer.Children.Add(new TextBlock
            {
                Text = L.T("Wersja ", "Version ") + UpdateService.Current.ToString(3), FontSize = 11, Foreground = TextSub, Margin = new Thickness(0, 6, 0, 0),
            });
            DockPanel.SetDock(footer, Dock.Bottom);
            side.Children.Add(footer);

            // ---- prawa część ----
            var right = new Grid();
            Grid.SetColumn(right, 1);
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.Children.Add(right);

            var head = new Grid { Margin = new Thickness(30, 22, 18, 10), Background = Brushes.Transparent };
            head.MouseLeftButtonDown += Drag;
            _pageTitle.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(_pageTitle);
            var close = CircleButton(IconClose);
            close.HorizontalAlignment = HorizontalAlignment.Right;
            // bez tego nagłówek łapał kliknięcie do przeciągania okna i przycisk nie dostawał puszczenia myszy
            close.MouseLeftButtonDown += (s, e) => e.Handled = true;
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; Close(); };
            head.Children.Add(close);
            right.Children.Add(head);

            _scroll.Margin = new Thickness(30, 4, 26, 0);
            Grid.SetRow(_scroll, 1);
            right.Children.Add(_scroll);

            return outer;
        }

        private void Drag(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        }

        // ============================ nawigacja ============================
        private void ShowPage(int i)
        {
            if (i < 0 || i >= _pages.Length) i = 0;
            _lastPage = i;
            _pageTitle.Text = L.T(_pages[i].pl, _pages[i].en);
            for (int k = 0; k < _navItems.Count; k++) PaintNav(_navItems[k], k == i);
            SetPage(_pages[i].build());
        }

        private void SetPage(StackPanel page)
        {
            page.Margin = new Thickness(0, 0, 0, 24);
            var shift = new TranslateTransform(0, 10);
            page.RenderTransform = shift;
            page.Opacity = 0;
            _scroll.Content = page;
            _scroll.ScrollToTop();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        }

        private Border NavItem(int i)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = new Path
            {
                Data = Geometry.Parse(_pages[i].icon), Width = 15, Height = 15, Stretch = Stretch.Uniform,
                Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock
            {
                Text = L.T(_pages[i].pl, _pages[i].en), FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(12, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center,
            });
            var b = new Border
            {
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 3),
                Child = row, Cursor = Cursors.Hand, Background = Brushes.Transparent, Tag = false,
            };
            b.MouseEnter += (s, e) => { if (!(bool)b.Tag) b.Background = Glass; };
            b.MouseLeave += (s, e) => { if (!(bool)b.Tag) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) => ShowPage(i);
            return b;
        }

        private void PaintNav(Border b, bool on)
        {
            b.Tag = on;
            b.Background = on ? Hex("#1FFFFFFF") : Brushes.Transparent;
            var row = (StackPanel)b.Child;
            ((Path)row.Children[0]).Fill = on ? _accentBrush : Hex("#A6FFFFFF");
            ((TextBlock)row.Children[1]).Foreground = on ? Brushes.White : Hex("#B3FFFFFF");
        }

        // ============================ strony ============================
        private StackPanel PagePlayer()
        {
            var c = App.Config;
            var p = new StackPanel();
            var g = Group(p, null);
            Toggle(g, L.T("Tekst piosenki", "Lyrics"), L.T("zsynchronizowany z muzyką, z serwisu LRCLIB", "synced with the music, from LRCLIB"),
                c.Lyrics, v => c.Lyrics = v);
            Toggle(g, L.T("Tytuł w małej wyspie", "Title in the small island"),
                L.T("nazwa piosenki na środku, między okładką a słupkami", "song title in the middle, between the cover and the bars"),
                c.CompactTitle, v => c.CompactTitle = v);
            Toggle(g, L.T("Powiadomienie o utworze", "Track notification"), L.T("nowy utwór pokazuje się na kilka sekund", "a new track shows up for a few seconds"),
                c.TrackToast, v => c.TrackToast = v);
            Toggle(g, L.T("Tryb kropki", "Dot mode"), L.T("zawsze mała kropka – odtwarzacz otwiera się po kliknięciu", "always a tiny dot – the player opens on click"),
                c.DotMode, v => c.DotMode = v);

            var g2 = Group(p, L.T("ANIMACJE", "ANIMATION"));
            Segment(g2, L.T("Okładka", "Cover"), L.T("jak mocno okładka reaguje na muzykę", "how strongly the cover reacts to music"),
                new[] { ("off", L.T("Brak", "Off")), ("breathe", L.T("Oddech", "Breathe")), ("beat", L.T("Rytm", "Beat")), ("strong", L.T("Mocna", "Strong")) },
                c.CoverAnimation, v => c.CoverAnimation = v);
            Segment(g2, L.T("Gdy nic nie gra", "When nothing plays"), null,
                new[] { ("dot", L.T("Kropka", "Dot")), ("clock", L.T("Zegar", "Clock")), ("hide", L.T("Ukryj", "Hide")) },
                c.IdleMode, v => c.IdleMode = v);
            return p;
        }

        private StackPanel PageLook()
        {
            var c = App.Config;
            var p = new StackPanel();
            var g = Group(p, null);
            Toggle(g, L.T("Tło z okładki", "Album background"), L.T("rozmyta okładka za odtwarzaczem", "blurred cover behind the player"),
                c.AlbumBackground, v => c.AlbumBackground = v);
            Toggle(g, L.T("Ruchome tło", "Animated background"), L.T("okładka płynie w tle jak w Apple Music", "the cover flows in the background like Apple Music"),
                c.AnimatedBackground, v => c.AnimatedBackground = v);
            Toggle(g, L.T("Poświata", "Glow"), L.T("delikatne światło w kolorze okładki", "soft light in the cover's color"), c.Glow, v => c.Glow = v);

            var g2 = Group(p, L.T("UKŁAD", "LAYOUT"));
            Segment(g2, L.T("Położenie", "Position"), L.T("możesz też po prostu przeciągnąć wyspę myszką", "you can also just drag the island with the mouse"),
                new[] { ("top", L.T("Góra", "Top")), ("bottom", L.T("Dół", "Bottom")), ("left", L.T("Lewo", "Left")), ("right", L.T("Prawo", "Right")) },
                c.Edge, v => { c.Edge = v; c.CustomPosition = false; });
            Segment(g2, L.T("Róg ekranu", "Screen corner"), null,
                new[] { ("topleft", "↖"), ("topright", "↗"), ("bottomleft", "↙"), ("bottomright", "↘") },
                c.Edge, v => { c.Edge = v; c.CustomPosition = false; });
            Segment(g2, L.T("Rozmiar", "Size"), null,
                new[] { ("0.9", "90%"), ("1", "100%"), ("1.15", "115%"), ("1.3", "130%") },
                ScaleKey(c.Scale), v => c.Scale = double.Parse(v, CultureInfo.InvariantCulture));
            return p;
        }

        private StackPanel PageCustomize()
        {
            var c = App.Config;
            var p = new StackPanel();

            var g = Group(p, L.T("KOLORY", "COLORS"));
            ColorRow(g, L.T("Kolor akcentu", "Accent color"), L.T("słupki, poświata i napisy", "bars, glow and labels"), c.Accent, v => c.Accent = v);
            Segment(g, L.T("Kolor słupków", "Bars color"), null,
                new[] { ("accent", L.T("Akcent", "Accent")), ("white", L.T("Biały", "White")) },
                c.BarsColor, v => c.BarsColor = v);
            Toggle(g, L.T("Pasek postępu w kolorze akcentu", "Accent-colored progress bar"), null, c.AccentProgress, v => c.AccentProgress = v);

            var g2 = Group(p, L.T("WYSPA", "ISLAND"));
            Segment(g2, L.T("Styl wyspy", "Island style"), L.T("szkło lekko prześwituje", "glass is slightly see-through"),
                new[] { ("black", L.T("Czarna", "Black")), ("graphite", L.T("Grafit", "Graphite")), ("glass", L.T("Szkło", "Glass")) },
                c.IslandStyle, v => c.IslandStyle = v);
            Segment(g2, L.T("Zaokrąglenie", "Corners"), null,
                new[] { ("round", L.T("Okrągłe", "Round")), ("soft", L.T("Łagodne", "Soft")), ("square", L.T("Kanciaste", "Square")) },
                c.Corners, v => c.Corners = v);
            Segment(g2, L.T("Animacja muzyki", "Music animation"), null,
                new[] { ("bars", L.T("Słupki", "Bars")), ("dots", L.T("Kropki", "Dots")), ("none", L.T("Brak", "None")) },
                c.BarsStyle, v => c.BarsStyle = v);
            Segment(g2, L.T("Szybkość animacji", "Animation speed"), L.T("jak szybko wyspa się rozwija", "how fast the island expands"),
                new[] { ("calm", L.T("Spokojna", "Calm")), ("normal", L.T("Normalna", "Normal")), ("fast", L.T("Szybka", "Fast")) },
                c.AnimSpeed, v => c.AnimSpeed = v);

            var g3 = Group(p, L.T("TEKST PIOSENKI", "LYRICS"));
            Segment(g3, L.T("Wielkość tekstu", "Text size"), null,
                new[] { ("s", L.T("Mały", "Small")), ("m", L.T("Średni", "Medium")), ("l", L.T("Duży", "Large")) },
                c.LyricsSize, v => c.LyricsSize = v);
            return p;
        }

        /// <summary>Wybór koloru: kółka z kolorami + "Auto" (kolor z okładki).</summary>
        private void ColorRow(StackPanel rows, string title, string sub, string current, Action<string> set)
        {
            var sp = new StackPanel { Margin = new Thickness(18, 13, 16, 14) };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(sub))
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = TextSub, Margin = new Thickness(0, 2, 0, 0) });
            var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var options = new (string key, Color color)[]
            {
                ("cover", Colors.Transparent),
                ("pink", Color.FromRgb(255, 92, 160)), ("purple", Color.FromRgb(160, 120, 255)), ("blue", Color.FromRgb(64, 156, 255)),
                ("green", Color.FromRgb(30, 215, 96)), ("orange", Color.FromRgb(255, 159, 10)), ("red", Color.FromRgb(255, 69, 58)),
                ("white", Color.FromRgb(235, 235, 240)),
            };
            var rings = new Border[options.Length];
            string selected = current;
            void Paint()
            {
                for (int i = 0; i < options.Length; i++)
                    rings[i].BorderBrush = options[i].key == selected ? Brushes.White : Brushes.Transparent;
            }
            for (int i = 0; i < options.Length; i++)
            {
                string key = options[i].key;
                UIElement inner;
                if (key == "cover")
                {
                    // "Auto": kolor z okładki - pokazujemy okładkę albo tęczę
                    var cover = App.Island?.CoverImage;
                    inner = new Border
                    {
                        Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
                        Background = cover != null
                            ? new ImageBrush(cover) { Stretch = Stretch.UniformToFill }
                            : (Brush)new LinearGradientBrush(Color.FromRgb(255, 92, 160), Color.FromRgb(64, 156, 255), 45),
                        Child = new TextBlock { Text = "A", FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                        ToolTip = L.T("Z okładki", "From cover"),
                    };
                }
                else
                {
                    inner = new Ellipse { Width = 26, Height = 26, Fill = new SolidColorBrush(options[i].color) };
                }
                var ring = new Border
                {
                    Width = 34, Height = 34, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(2),
                    Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, Child = inner, Background = Brushes.Transparent,
                };
                ring.MouseLeftButtonUp += (s, e) =>
                {
                    selected = key;
                    Paint();
                    set(key);
                    Changed();
                };
                rings[i] = ring;
                row.Children.Add(ring);
            }
            Paint();
            sp.Children.Add(row);
            AddRow(rows, sp);
        }

        private StackPanel PageSound()
        {
            var c = App.Config;
            var p = new StackPanel();
            var g = Group(p, null);
            Toggle(g, L.T("Głośność kółkiem myszy", "Mouse wheel volume"), L.T("przewiń nad wyspą", "scroll over the island"),
                c.WheelVolume, v => c.WheelVolume = v);
            Toggle(g, L.T("Tylko głośność muzyki", "Music volume only"),
                L.T("kółko zmienia głośność odtwarzacza (np. Spotify), a nie całego komputera", "the wheel changes the player's volume (e.g. Spotify), not the whole PC"),
                c.AppVolume, v => c.AppVolume = v);
            Toggle(g, L.T("Głośność w wyspie", "Volume in the island"), L.T("zamiast systemowego okienka Windows", "instead of the Windows volume popup"),
                c.VolumeOsd, v => c.VolumeOsd = v);
            Toggle(g, L.T("Płynna pauza", "Smooth pause"), L.T("muzyka cichnie, zamiast urywać się", "music fades out instead of stopping abruptly"),
                c.FadeOnPause, v => c.FadeOnPause = v);
            return p;
        }

        private StackPanel PageSystem()
        {
            var c = App.Config;
            var p = new StackPanel();
            var g = Group(p, null);
            Toggle(g, L.T("Uruchamiaj z Windows", "Start with Windows"), L.T("wyspa pojawi się sama po włączeniu komputera", "the island appears when your PC starts"),
                Autostart.IsEnabled, v => Autostart.Set(v));
            Toggle(g, L.T("Chowaj przy pełnym ekranie", "Hide in fullscreen"), L.T("gry i filmy", "games and videos"),
                c.HideInFullscreen, v => c.HideInFullscreen = v);
            Toggle(g, L.T("Skróty klawiszowe", "Keyboard shortcuts"), null, c.Hotkeys, v => c.Hotkeys = v);

            var keys = Group(p, L.T("SKRÓTY – KLIKNIJ, ABY ZMIENIĆ", "SHORTCUTS – CLICK TO CHANGE"));
            foreach (var a in Hotkey.Actions) HotkeyRow(keys, a.key, L.T(a.pl, a.en));
            var reset = new TextBlock
            {
                Text = L.T("Przywróć domyślne skróty", "Restore default shortcuts"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Foreground = _accentBrush, Cursor = Cursors.Hand, Margin = new Thickness(4, 10, 0, 0),
            };
            reset.MouseLeftButtonUp += (s, e) =>
            {
                c.Shortcuts = new System.Collections.Generic.Dictionary<string, string>();
                Changed();
                ShowPage(_lastPage);
            };
            p.Children.Add(reset);

            var g2 = Group(p, L.T("JĘZYK", "LANGUAGE"));
            Segment(g2, L.T("Język aplikacji", "App language"), null,
                new[] { ("auto", "Auto"), ("pl", "Polski"), ("en", "English") },
                c.Language, v => { c.Language = v; App.Config.Save(); App.RefreshLanguage(); }, notify: false);
            return p;
        }

        private StackPanel PageUpdates()
        {
            var c = App.Config;
            var p = new StackPanel();

            // duża karta wersji
            var card = new Border
            {
                CornerRadius = new CornerRadius(18), Padding = new Thickness(20, 18, 18, 18), Margin = new Thickness(0, 0, 0, 14),
                Background = Glass, BorderBrush = Hairline, BorderThickness = new Thickness(1),
            };
            var grid = new Grid();
            card.Child = grid;
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(64, 0, 120, 0) };
            var logo = MiniIsland(1.6);
            logo.HorizontalAlignment = HorizontalAlignment.Left;
            logo.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(logo);
            info.Children.Add(new TextBlock { Text = "Isabelle " + UpdateService.Current.ToString(3), FontSize = 16, FontWeight = FontWeights.Bold });
            var status = new TextBlock { FontSize = 12.5, Foreground = TextSub, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap };
            info.Children.Add(status);
            grid.Children.Add(info);
            var btn = PillButton("", true);
            btn.HorizontalAlignment = HorizontalAlignment.Right;
            btn.VerticalAlignment = VerticalAlignment.Center;
            var btnText = (TextBlock)btn.Child;
            grid.Children.Add(btn);
            p.Children.Add(card);

            void UpdateUi()
            {
                var up = UpdateService.Available;
                if (!UpdateService.Configured)
                {
                    status.Text = L.T("Aktualizacje ruszą po wydaniu programu na GitHubie.", "Updates start once the app is released on GitHub.");
                    btn.Visibility = Visibility.Collapsed;
                }
                else if (up != null)
                {
                    status.Text = L.T("Dostępna nowa wersja ", "New version available: ") + up.Version.ToString(3);
                    btnText.Text = L.T("Zainstaluj", "Install");
                }
                else
                {
                    if (string.IsNullOrEmpty(status.Text)) status.Text = L.T("Masz najnowszą wersję.", "You're up to date.");
                    btnText.Text = L.T("Sprawdź", "Check");
                }
            }

            bool busy = false;
            btn.MouseLeftButtonUp += async (s, e) =>
            {
                if (busy) return;
                busy = true;
                try
                {
                    if (UpdateService.Available != null)
                    {
                        btnText.Text = L.T("Pobieranie…", "Downloading…");
                        await UpdateService.InstallAsync(UpdateService.Available,
                            p => Dispatcher.BeginInvoke(new Action(() => btnText.Text = L.T("Pobieranie… ", "Downloading… ") + (int)(p * 100) + "%")));
                    }
                    else
                    {
                        btnText.Text = "…";
                        var found = await UpdateService.CheckAsync();
                        status.Text = found == null ? L.T("Masz najnowszą wersję.", "You're up to date.") : "";
                    }
                }
                finally
                {
                    busy = false;
                    UpdateUi();
                }
            };
            UpdateUi();

            var g = Group(p, null);
            Toggle(g, L.T("Sprawdzaj automatycznie", "Check automatically"), L.T("w tle, co kilka godzin", "in the background, every few hours"),
                c.AutoUpdate, v => c.AutoUpdate = v);
            return p;
        }

        private void ShowWelcome()
        {
            _pageTitle.Text = "";
            foreach (var n in _navItems) PaintNav(n, false);
            var p = new StackPanel();
            p.Children.Add(new TextBlock
            {
                Text = L.T("Witaj w Isabelle", "Welcome to Isabelle"), FontSize = 30, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 2, 0, 6),
            });
            p.Children.Add(new TextBlock
            {
                Text = L.T("Twoja muzyka w wyspie na krawędzi ekranu – jak Dynamic Island w iPhonie.",
                           "Your music in an island on the edge of your screen – like Dynamic Island on iPhone."),
                FontSize = 14, Foreground = TextSub, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20),
            });
            var g = Group(p, null);
            Tip(g, IconNote, L.T("Najedź myszką na wyspę", "Hover over the island"),
                L.T("zobaczysz pełny odtwarzacz z tekstem piosenki", "to see the full player with lyrics"));
            Tip(g, IconSound, L.T("Przewiń kółkiem nad wyspą", "Scroll over the island"), L.T("zmienisz głośność", "to change the volume"));
            Tip(g, IconMove, L.T("Przeciągnij wyspę", "Drag the island"),
                L.T("na dowolną krawędź ekranu – także w pionie", "to any screen edge – vertical works too"));
            Tip(g, IconSliders, L.T("Prawy przycisk na wyspie", "Right-click the island"),
                L.T("otwiera te ustawienia", "to open these settings"));

            var go = PillButton(L.T("Zaczynamy", "Let's go"), false);
            go.HorizontalAlignment = HorizontalAlignment.Left;
            go.Margin = new Thickness(0, 22, 0, 0);
            go.MouseLeftButtonUp += (s, e) => ShowPage(0);
            p.Children.Add(go);
            SetPage(p);
        }

        // ============================ elementy ============================
        private static void Changed()
        {
            App.Config.Save();
            App.Island?.ApplySettings();
        }

        /// <summary>Szklana grupa z opcjonalnym nagłówkiem; zwraca panel na wiersze.</summary>
        private static StackPanel Group(StackPanel page, string header)
        {
            if (header != null)
            {
                page.Children.Add(new TextBlock
                {
                    Text = header, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = TextSub,
                    Margin = new Thickness(4, page.Children.Count == 0 ? 0 : 22, 0, 8),
                });
            }
            var rows = new StackPanel();
            page.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16), Background = Glass, BorderBrush = Hairline, BorderThickness = new Thickness(1),
                Child = rows, Margin = new Thickness(0, header == null && page.Children.Count > 1 ? 14 : 0, 0, 0),
            });
            return rows;
        }

        private static void AddRow(StackPanel rows, FrameworkElement row)
        {
            if (rows.Children.Count > 0)
                rows.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(18, 0, 18, 0) });
            rows.Children.Add(row);
        }

        private static StackPanel Labels(string title, string sub)
        {
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 70, 0) };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(sub))
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = TextSub, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });
            return sp;
        }

        private void Toggle(StackPanel rows, string title, string sub, bool value, Action<bool> set)
        {
            var g = new Grid { Margin = new Thickness(18, 12, 16, 12), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.Children.Add(Labels(title, sub));
            var sw = new Switch(value, _accentBrush) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            g.Children.Add(sw);
            g.MouseLeftButtonUp += (s, e) =>
            {
                sw.Toggle();
                set(sw.IsOn);
                Changed();
            };
            AddRow(rows, Hover(g));
        }

        private static Border Hover(FrameworkElement content)
        {
            var b = new Border { Child = content, Background = Brushes.Transparent, CornerRadius = new CornerRadius(14) };
            b.MouseEnter += (s, e) => b.Background = Hex("#08FFFFFF");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            return b;
        }

        /// <summary>Wybór jednej z kilku opcji - "pigułki" jak w iOS.</summary>
        private static void Segment(StackPanel rows, string title, string sub, (string key, string label)[] options, string current, Action<string> set, bool notify = true)
        {
            var sp = new StackPanel { Margin = new Thickness(18, 13, 16, 14) };
            sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrEmpty(sub))
                sp.Children.Add(new TextBlock { Text = sub, FontSize = 12, Foreground = TextSub, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap });

            var track = new Border
            {
                CornerRadius = new CornerRadius(12), Background = Hex("#12FFFFFF"), Padding = new Thickness(3),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            track.Child = row;
            var items = new Border[options.Length];
            string selected = current;

            void Paint()
            {
                for (int i = 0; i < options.Length; i++)
                {
                    bool on = options[i].key == selected;
                    items[i].Background = on ? Brushes.White : Brushes.Transparent;
                    ((TextBlock)items[i].Child).Foreground = on ? Brushes.Black : Hex("#CCFFFFFF");
                }
            }

            for (int i = 0; i < options.Length; i++)
            {
                string key = options[i].key;
                var item = new Border
                {
                    CornerRadius = new CornerRadius(9), Padding = new Thickness(14, 6, 14, 7), Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = options[i].label, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
                };
                item.MouseEnter += (s, e) => { if (key != selected) item.Background = Hex("#14FFFFFF"); };
                item.MouseLeave += (s, e) => { if (key != selected) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonUp += (s, e) =>
                {
                    selected = key;
                    Paint();
                    set(key);
                    if (notify) Changed();
                };
                items[i] = item;
                row.Children.Add(item);
            }
            Paint();
            sp.Children.Add(track);
            AddRow(rows, sp);
        }

        /// <summary>Wiersz ze skrótem: kliknij i naciśnij nowe klawisze (Esc = anuluj, Backspace = wyłącz).</summary>
        private void HotkeyRow(StackPanel rows, string action, string title)
        {
            var g = new Grid { Margin = new Thickness(18, 10, 16, 10) };
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = title, FontSize = 13.5 });
            var hint = new TextBlock { FontSize = 11.5, Foreground = Hex("#FF6B61"), Visibility = Visibility.Collapsed, Margin = new Thickness(0, 2, 0, 0) };
            labels.Children.Add(hint);
            g.Children.Add(labels);

            var chip = new Border
            {
                CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 4, 6, 4), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1),
            };
            var keys = new StackPanel { Orientation = Orientation.Horizontal };
            chip.Child = keys;
            g.Children.Add(chip);

            void Render(string text = null)
            {
                keys.Children.Clear();
                if (text != null)
                {
                    keys.Children.Add(new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = _accentBrush, Margin = new Thickness(4, 2, 4, 2) });
                    return;
                }
                var parts = Hotkey.Pretty(App.Config.Shortcut(action));
                if (parts.Count == 0)
                {
                    keys.Children.Add(new TextBlock { Text = L.T("brak", "none"), FontSize = 12, Foreground = TextSub, Margin = new Thickness(4, 2, 4, 2) });
                }
                foreach (var k in parts)
                {
                    keys.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(7), Background = Hex("#1AFFFFFF"), BorderBrush = Hex("#24FFFFFF"),
                        BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(5, 0, 0, 0), MinWidth = 30,
                        Child = new TextBlock { Text = k, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                    });
                }
                bool busy = App.Island != null && App.Island.HotkeyFailures.Contains(action);
                hint.Text = L.T("zajęty przez inny program – wybierz inny", "used by another app – pick another one");
                hint.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            }

            void Stop()
            {
                _capture = null;
                _cancelCapture = null;
                chip.Background = Brushes.Transparent;
                chip.BorderBrush = Brushes.Transparent;
            }

            chip.MouseEnter += (s, e) => { if (_capture == null) chip.Background = Hex("#0FFFFFFF"); };
            chip.MouseLeave += (s, e) => { if (_capture == null) chip.Background = Brushes.Transparent; };
            chip.MouseLeftButtonUp += (s, e) =>
            {
                _cancelCapture?.Invoke();
                Focus();
                Keyboard.Focus(this);
                chip.Background = Hex("#14FFFFFF");
                chip.BorderBrush = _accentBrush;
                Render(L.T("Naciśnij skrót…", "Press keys…"));
                _cancelCapture = () => { Stop(); Render(); };
                _capture = (k, mods) =>
                {
                    if (Hotkey.IsModifier(k)) return;                       // czekamy na właściwy klawisz
                    if (k == Key.Escape) { Stop(); Render(); return; }
                    var c = App.Config;
                    if (c.Shortcuts == null) c.Shortcuts = new System.Collections.Generic.Dictionary<string, string>();
                    if (k == Key.Back || k == Key.Delete)
                    {
                        c.Shortcuts[action] = "";                           // wyłączony
                    }
                    else
                    {
                        if (mods == ModifierKeys.None && !Hotkey.AllowedAlone(k))
                        {
                            Render(L.T("dodaj Ctrl, Alt lub Shift", "add Ctrl, Alt or Shift"));
                            return;
                        }
                        string text = Hotkey.Format(mods, k);
                        // ten sam skrót nie może być przypisany do dwóch akcji
                        foreach (var other in Hotkey.Actions)
                            if (other.key != action && c.Shortcut(other.key) == text) c.Shortcuts[other.key] = "";
                        c.Shortcuts[action] = text;
                    }
                    Stop();
                    Changed();
                    ShowPage(_lastPage);
                };
            };
            Render();
            AddRow(rows, g);
        }

        private static void KeyRow(StackPanel rows, string title, string key)
        {
            var g = new Grid { Margin = new Thickness(18, 11, 16, 11) };
            g.Children.Add(new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            var keys = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            foreach (var k in new[] { "Ctrl", "Alt", key })
            {
                keys.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(7), Background = Hex("#1AFFFFFF"), BorderBrush = Hex("#24FFFFFF"),
                    BorderThickness = new Thickness(1, 1, 1, 2), Padding = new Thickness(9, 3, 9, 4), Margin = new Thickness(5, 0, 0, 0),
                    MinWidth = 30,
                    Child = new TextBlock { Text = k, FontSize = 11.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                });
            }
            g.Children.Add(keys);
            AddRow(rows, g);
        }

        private void Tip(StackPanel rows, string icon, string title, string sub)
        {
            var g = new Grid { Margin = new Thickness(16, 12, 16, 12) };
            var circle = new Border
            {
                Width = 34, Height = 34, CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new SolidColorBrush(Color.FromArgb(46, _accent.R, _accent.G, _accent.B)),
                Child = new Path { Data = Geometry.Parse(icon), Width = 14, Height = 14, Stretch = Stretch.Uniform, Fill = _accentBrush },
            };
            g.Children.Add(circle);
            var labels = Labels(title, sub);
            labels.Margin = new Thickness(48, 0, 0, 0);
            g.Children.Add(labels);
            AddRow(rows, g);
        }

        private Border NowPlayingCard(BitmapSource cover)
        {
            var isl = App.Island;
            string title = isl?.NowTitle;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var art = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = Hex("#2A2A30") };
            if (cover != null) art.Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill };
            g.Children.Add(art);
            var t = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 1);
            t.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(title) ? L.T("Nic nie gra", "Nothing playing") : title,
                FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            t.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(title) ? "Isabelle" : (isl?.NowArtist ?? ""),
                FontSize = 11.5, Foreground = TextSub, TextTrimming = TextTrimming.CharacterEllipsis,
            });
            g.Children.Add(t);
            return new Border
            {
                CornerRadius = new CornerRadius(16), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 18),
                Background = Glass, BorderBrush = Hairline, BorderThickness = new Thickness(1), Child = g,
            };
        }

        private Border PillButton(string text, bool accent)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(18), Padding = new Thickness(20, 8, 20, 9), Cursor = Cursors.Hand,
                Background = accent ? (Brush)_accentBrush : Brushes.White,
                Child = new TextBlock { Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black },
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1),
            };
            b.MouseEnter += (s, e) => b.Opacity = 0.9;
            b.MouseLeave += (s, e) => { b.Opacity = 1; ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 1; };
            b.MouseLeftButtonDown += (s, e) => ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 0.96;
            b.MouseLeftButtonUp += (s, e) => ((ScaleTransform)b.RenderTransform).ScaleX = ((ScaleTransform)b.RenderTransform).ScaleY = 1;
            return b;
        }

        private static Border CircleButton(string icon)
        {
            var b = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Hex("#17FFFFFF"), Cursor = Cursors.Hand,
                Child = new Path
                {
                    Data = Geometry.Parse(icon), Width = 10, Height = 10, Stretch = Stretch.Uniform,
                    Stroke = Brushes.White, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                },
            };
            b.MouseEnter += (s, e) => b.Background = Hex("#2EFFFFFF");
            b.MouseLeave += (s, e) => b.Background = Hex("#17FFFFFF");
            return b;
        }

        /// <summary>Logo Isabelle: ciemny kwadrat z czterema słupkami (drugi różowy).</summary>
        private FrameworkElement MiniIsland(double scale = 1)
        {
            var tile = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = Hex("#0E0E10"),
                BorderBrush = Hex("#24FFFFFF"), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                LayoutTransform = new ScaleTransform(scale, scale),
            };
            var bars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            double[] hs = { 6.5, 13, 10, 4.5 };
            for (int i = 0; i < hs.Length; i++)
            {
                bars.Children.Add(new Border
                {
                    Width = 2.8, Height = hs[i], CornerRadius = new CornerRadius(1.4), Margin = new Thickness(i == 0 ? 0 : 1.3, 0, 0, 0),
                    Background = i == 1 ? Hex("#FF5CA0") : Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                });
            }
            tile.Child = bars;
            return tile;
        }

        private static string ScaleKey(double scale)
        {
            if (Math.Abs(scale - 0.9) < 0.01) return "0.9";
            if (Math.Abs(scale - 1.15) < 0.01) return "1.15";
            if (Math.Abs(scale - 1.3) < 0.01) return "1.3";
            return "1";
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        // ikony (proste kształty, bez plików graficznych)
        private const string IconNote = "M6,0 L16,-2 V11 A3,3 0 1 1 14,8.2 V2 L8,3.2 V13 A3,3 0 1 1 6,10.2 Z";
        private const string IconSparkle = "M8,0 L9.8,6.2 L16,8 L9.8,9.8 L8,16 L6.2,9.8 L0,8 L6.2,6.2 Z";
        private const string IconSound = "M0,5 H4 L9,0 V16 L4,11 H0 Z M11,4.5 H12.8 V11.5 H11 Z M14.4,2 H16.2 V14 H14.4 Z";
        private const string IconSliders = "M0,3 H16 V5 H0 Z M0,11 H16 V13 H0 Z M4,1 A3,3 0 1 1 4,7 A3,3 0 1 1 4,1 Z M12,9 A3,3 0 1 1 12,15 A3,3 0 1 1 12,9 Z";
        private const string IconUpdate = "M7,0 H9 V8 L12,5 L13.4,6.4 L8,11.8 L2.6,6.4 L4,5 L7,8 Z M0,13 H16 V15 H0 Z";
        private const string IconMove = "M8,0 L11,3 H9 V7 H13 V5 L16,8 L13,11 V9 H9 V13 H11 L8,16 L5,13 H7 V9 H3 V11 L0,8 L3,5 V7 H7 V3 H5 Z";
        private const string IconPalette = "M5,0 A5,5 0 1 1 5,10 A5,5 0 1 1 5,0 Z M12,5 A4,4 0 1 1 12,13 A4,4 0 1 1 12,5 Z M4,11 A3.5,3.5 0 1 1 4,18 A3.5,3.5 0 1 1 4,11 Z";
        private const string IconClose = "M0,0 L10,10 M10,0 L0,10";

        /// <summary>Przełącznik jak w iOS, w kolorze okładki.</summary>
        private sealed class Switch : Border
        {
            private readonly TranslateTransform _knobShift = new TranslateTransform();
            private readonly Brush _on;
            private static readonly Brush Off = Hex("#2EFFFFFF");
            public bool IsOn { get; private set; }

            public Switch(bool on, Brush onBrush)
            {
                _on = onBrush;
                Width = 44;
                Height = 26;
                CornerRadius = new CornerRadius(13);
                IsHitTestVisible = false;
                Child = new Ellipse
                {
                    Width = 22, Height = 22, Fill = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0),
                    RenderTransform = _knobShift,
                    Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = 0.35 },
                };
                IsOn = on;
                Background = on ? _on : Off;
                _knobShift.X = on ? 18 : 0;
            }

            public void Toggle()
            {
                IsOn = !IsOn;
                Background = IsOn ? _on : Off;
                _knobShift.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(IsOn ? 18 : 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
        }
    }
}
