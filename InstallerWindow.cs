using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>Instalator / deinstalator w stylu odtwarzacza (czarne szkło, kolorowe światła, animowana wyspa).</summary>
    public sealed class InstallerWindow : Window
    {
        // kolor marki Isabelle (różowy, jak w logo)
        private static readonly Color Green = Color.FromRgb(255, 92, 160);
        private static readonly Brush GreenBrush = Freeze(new SolidColorBrush(Green));
        private static readonly Brush TextSub = Hex("#99FFFFFF");
        private static readonly Brush Glass = Hex("#10FFFFFF");
        private static readonly Brush Hairline = Hex("#14FFFFFF");

        private readonly bool _uninstall;
        private readonly StackPanel _body = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Width = 380 };
        private readonly Grid _clipHost = new Grid();
        private readonly RectangleGeometry _clip = new RectangleGeometry { RadiusX = 26, RadiusY = 26 };
        private BarsControl _bars;
        private bool _busy;
        private bool _optDesktop = true, _optAutostart = true, _optRemoveSettings;

        public InstallerWindow(bool uninstall)
        {
            _uninstall = uninstall;
            Title = uninstall ? L.T("Odinstaluj Isabelle", "Uninstall Isabelle") : L.T("Instalator Isabelle", "Isabelle Setup");
            Width = 640;
            Height = 520;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            if (Installer.IsInstalled)
            {
                _optDesktop = Installer.HasDesktopShortcut;
                _optAutostart = Autostart.IsEnabled;
            }

            Content = BuildShell();
            ShowMain();

            KeyDown += (s, e) => { if (e.Key == Key.Escape && !_busy) Close(); };
            Closed += (s, e) =>
            {
                CompositionTarget.Rendering -= AnimateBars;
                Application.Current.Shutdown();
            };
            CompositionTarget.Rendering += AnimateBars;
        }

        // ============================ szkielet ============================
        private UIElement BuildShell()
        {
            var outer = new Grid { Margin = new Thickness(20) };
            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(26), Background = Brushes.Black, Opacity = 0.6, Margin = new Thickness(8, 14, 8, 0),
                Effect = new BlurEffect { Radius = 24, RenderingBias = RenderingBias.Performance }, IsHitTestVisible = false,
            });
            var card = new Border
            {
                CornerRadius = new CornerRadius(26), Background = Hex("#0A0A0C"), BorderBrush = Hex("#1FFFFFFF"),
                BorderThickness = new Thickness(1), Child = _clipHost,
            };
            outer.Children.Add(card);
            _clipHost.Clip = _clip;
            _clipHost.SizeChanged += (s, e) => _clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            _clipHost.Background = Brushes.Transparent;
            _clipHost.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            // płynące, rozmyte światła (zielone i fioletowe)
            _clipHost.Children.Add(Blob(Green, -120, -110, 0, 60, 7));
            _clipHost.Children.Add(Blob(Color.FromRgb(124, 92, 255), 170, 120, -70, -40, 9));
            _clipHost.Children.Add(Blob(Color.FromRgb(255, 92, 160), 200, -150, -50, 70, 11, 0.22));
            _clipHost.Children.Add(new Border
            {
                Background = new LinearGradientBrush(Color.FromArgb(0x70, 10, 10, 12), Color.FromArgb(0xE6, 10, 10, 12), 90),
                IsHitTestVisible = false,
            });

            var close = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = Hex("#1AFFFFFF"), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 16, 0),
                Child = new Path
                {
                    Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Width = 10, Height = 10, Stretch = Stretch.Uniform,
                    Stroke = Brushes.White, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                },
            };
            close.MouseEnter += (s, e) => close.Background = Hex("#33FFFFFF");
            close.MouseLeave += (s, e) => close.Background = Hex("#1AFFFFFF");
            close.MouseLeftButtonDown += (s, e) => e.Handled = true;
            close.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (!_busy) Close(); };
            _clipHost.Children.Add(close);

            var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            center.Children.Add(AnimatedIsland());
            center.Children.Add(_body);
            _clipHost.Children.Add(center);
            return outer;
        }

        private static FrameworkElement Blob(Color c, double x, double y, double dx, double dy, double seconds, double opacity = 0.45)
        {
            var move = new TranslateTransform(x, y);
            var e = new Ellipse
            {
                Width = 340, Height = 340, Fill = new SolidColorBrush(c), Opacity = opacity, IsHitTestVisible = false,
                Effect = new BlurEffect { Radius = 110, RenderingBias = RenderingBias.Performance },
                RenderTransform = move,
            };
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, x + dx, TimeSpan.FromSeconds(seconds))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, y + dy, TimeSpan.FromSeconds(seconds * 0.8))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            return e;
        }

        /// <summary>Mała wyspa z okładką i grającymi słupkami, która "oddycha" jak prawdziwa.</summary>
        private FrameworkElement AnimatedIsland()
        {
            var pill = new Border
            {
                Height = 44, Width = 168, CornerRadius = new CornerRadius(22), Background = Brushes.Black,
                BorderBrush = Hex("#2EFFFFFF"), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Color = Green, Opacity = 0.45 },
            };
            var g = new Grid();
            g.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(8), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(8, 0, 0, 0),
                Background = new LinearGradientBrush(Green, Color.FromRgb(124, 92, 255), 45),
                Child = new Path
                {
                    Data = Geometry.Parse("M6,0 L16,-2 V11 A3,3 0 1 1 14,8.2 V2 L8,3.2 V13 A3,3 0 1 1 6,10.2 Z"),
                    Width = 12, Height = 12, Stretch = Stretch.Uniform, Fill = Hex("#CC000000"),
                },
            });
            _bars = new BarsControl { Width = 20, Height = 16, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 16, 0), Fill = GreenBrush };
            g.Children.Add(_bars);
            pill.Child = g;
            pill.BeginAnimation(WidthProperty, new DoubleAnimation(168, 196, TimeSpan.FromSeconds(1.6))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            return new Grid { Height = 60, Children = { pill } };
        }

        private readonly DateTime _start = DateTime.UtcNow;

        private void AnimateBars(object sender, EventArgs e)
        {
            double t = (DateTime.UtcNow - _start).TotalSeconds;
            double level = 0.55 + 0.45 * Math.Abs(Math.Sin(t * 2.1));
            _bars?.Update(t, true, level, 1);
        }

        // ============================ ekrany ============================
        private void SetBody(params UIElement[] items)
        {
            _body.Children.Clear();
            foreach (var i in items) _body.Children.Add(i);
            var shift = new TranslateTransform(0, 10);
            _body.RenderTransform = shift;
            _body.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _body.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        }

        private void ShowMain()
        {
            if (_uninstall)
            {
                var opts = Group();
                Toggle(opts, L.T("Usuń też ustawienia", "Also remove settings"), L.T("kolory, położenie, opcje", "colors, position, options"),
                    _optRemoveSettings, v => _optRemoveSettings = v);
                var go = Pill(L.T("Odinstaluj", "Uninstall"), Hex("#FF453A"), Brushes.White);
                go.MouseLeftButtonUp += async (s, e) => await RunUninstall();
                SetBody(
                    Heading(L.T("Odinstalować Isabelle?", "Uninstall Isabelle?")),
                    Sub(L.T("Wyspa zniknie z ekranu, a aplikacja zostanie usunięta z komputera.",
                            "The island will disappear and the app will be removed from your PC.")),
                    Wrap(opts), go, Link(L.T("Anuluj", "Cancel"), Close));
                return;
            }

            bool update = Installer.IsInstalled;
            var g = Group();
            Toggle(g, L.T("Skrót na pulpicie", "Desktop shortcut"), null, _optDesktop, v => _optDesktop = v);
            Toggle(g, L.T("Uruchamiaj z Windows", "Start with Windows"), L.T("wyspa pojawi się sama po starcie komputera", "the island appears when your PC starts"),
                _optAutostart, v => _optAutostart = v);

            var install = Pill(update ? L.T("Zaktualizuj", "Update") : L.T("Zainstaluj", "Install"), Brushes.White, Brushes.Black);
            install.MouseLeftButtonUp += async (s, e) => await RunInstall();

            SetBody(
                Heading("Isabelle"),
                Sub(update
                    ? L.T("Wersja ", "Version ") + UpdateService.Current.ToString(3) + L.T(" – nowa wersja zastąpi obecną, ustawienia zostaną.", " – replaces the installed version, your settings stay.")
                    : L.T("Dynamic Island dla Twojej muzyki na Windows.", "Dynamic Island for your music on Windows.")),
                Wrap(g), install,
                Footnote(L.T("Bez uprawnień administratora · ", "No admin rights needed · ") + Installer.Dir.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~")));
        }

        private async Task RunInstall()
        {
            if (_busy) return;
            _busy = true;
            var (panel, setProgress, status) = ProgressView(L.T("Instalowanie…", "Installing…"));
            SetBody(panel);
            try
            {
                setProgress(0.15);
                await Task.Delay(250);
                await Task.Run(Installer.CopyFiles);
                setProgress(0.75);
                status.Text = L.T("Tworzenie skrótów…", "Creating shortcuts…");
                await Task.Delay(200);
                Installer.Finish(_optDesktop, _optAutostart);
                setProgress(1);
                await Task.Delay(450);
                ShowDone();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ShowError(ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task RunUninstall()
        {
            if (_busy) return;
            _busy = true;
            var (panel, setProgress, _) = ProgressView(L.T("Usuwanie…", "Removing…"));
            SetBody(panel);
            try
            {
                setProgress(0.4);
                bool removeSettings = _optRemoveSettings;
                await Task.Run(() => Installer.Uninstall(removeSettings));
                setProgress(1);
                await Task.Delay(400);
                var done = Pill(L.T("Zamknij", "Close"), Brushes.White, Brushes.Black);
                done.MouseLeftButtonUp += (s, e) => Close();
                SetBody(Check(), Heading(L.T("Usunięto", "Removed")),
                    Sub(L.T("Isabelle została usunięta z komputera. Dzięki, że jej używałeś!", "Isabelle has been removed. Thanks for using it!")), done);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                ShowError(ex.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private void ShowDone()
        {
            var run = Pill(L.T("Uruchom Isabelle", "Launch Isabelle"), Brushes.White, Brushes.Black);
            run.MouseLeftButtonUp += (s, e) =>
            {
                Installer.Launch();
                Close();
            };
            SetBody(Check(), Heading(L.T("Gotowe!", "All set!")),
                Sub(L.T("Isabelle jest zainstalowana. Znajdziesz ją w menu Start.", "Isabelle is installed. You'll find it in the Start menu.")),
                run, Link(L.T("Zamknij", "Close"), Close));
        }

        private void ShowError(string message)
        {
            var back = Pill(L.T("Spróbuj ponownie", "Try again"), Brushes.White, Brushes.Black);
            back.MouseLeftButtonUp += (s, e) => ShowMain();
            SetBody(Heading(L.T("Coś poszło nie tak", "Something went wrong")), Sub(message), back);
        }

        private (UIElement panel, Action<double> setProgress, TextBlock status) ProgressView(string text)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 34, 0, 0) };
            var status = new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(status);
            var track = new Border { Width = 300, Height = 6, CornerRadius = new CornerRadius(3), Background = Hex("#1FFFFFFF"), Margin = new Thickness(0, 16, 0, 0) };
            var fill = new Border
            {
                Width = 0, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
                Background = new LinearGradientBrush(Green, Color.FromRgb(124, 92, 255), 0),
            };
            track.Child = fill;
            sp.Children.Add(track);
            void Set(double v) => fill.BeginAnimation(WidthProperty, new DoubleAnimation(300 * v, TimeSpan.FromMilliseconds(400))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            return (sp, Set, status);
        }

        // ============================ elementy ============================
        private static TextBlock Heading(string text) => new TextBlock
        {
            Text = text, FontSize = 30, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 22, 0, 0),
        };

        private static TextBlock Sub(string text) => new TextBlock
        {
            Text = text, FontSize = 13.5, Foreground = TextSub, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        };

        private static TextBlock Footnote(string text) => new TextBlock
        {
            Text = text, FontSize = 11, Foreground = Hex("#66FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 14, 0, 0),
        };

        private static TextBlock Link(string text, Action click)
        {
            var t = new TextBlock
            {
                Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hex("#B3FFFFFF"), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0),
            };
            t.MouseEnter += (s, e) => t.Foreground = Brushes.White;
            t.MouseLeave += (s, e) => t.Foreground = Hex("#B3FFFFFF");
            t.MouseLeftButtonDown += (s, e) => e.Handled = true;
            t.MouseLeftButtonUp += (s, e) => click();
            return t;
        }

        private static Border Pill(string text, Brush bg, Brush fg)
        {
            var scale = new ScaleTransform(1, 1);
            var b = new Border
            {
                CornerRadius = new CornerRadius(22), Height = 44, Margin = new Thickness(0, 20, 0, 0), Cursor = Cursors.Hand,
                Background = bg, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = scale,
                Child = new TextBlock
                {
                    Text = text, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = fg,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            b.MouseEnter += (s, e) => b.Opacity = 0.92;
            b.MouseLeave += (s, e) => { b.Opacity = 1; scale.ScaleX = scale.ScaleY = 1; };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; scale.ScaleX = scale.ScaleY = 0.97; };
            b.MouseLeftButtonUp += (s, e) => scale.ScaleX = scale.ScaleY = 1;
            return b;
        }

        private static FrameworkElement Check()
        {
            var circle = new Border
            {
                Width = 58, Height = 58, CornerRadius = new CornerRadius(29), Background = GreenBrush, HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 16, 0, 0),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 0, Color = Green, Opacity = 0.6 },
                Child = new Path
                {
                    Data = Geometry.Parse("M0,6 L5,11 L15,0"), Width = 22, Height = 16, Stretch = Stretch.Uniform,
                    Stroke = Brushes.White, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                },
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
            var sc = new ScaleTransform(0.4, 0.4);
            circle.RenderTransform = sc;
            var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            return circle;
        }

        private static StackPanel Group() => new StackPanel();

        private static Border Wrap(StackPanel rows) => new Border
        {
            CornerRadius = new CornerRadius(16), Background = Glass, BorderBrush = Hairline, BorderThickness = new Thickness(1),
            Child = rows, Margin = new Thickness(0, 22, 0, 0),
        };

        private static void Toggle(StackPanel rows, string title, string sub, bool value, Action<bool> set)
        {
            if (rows.Children.Count > 0)
                rows.Children.Add(new Border { Height = 1, Background = Hairline, Margin = new Thickness(16, 0, 16, 0) });
            var g = new Grid { Margin = new Thickness(16, 11, 14, 11), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 60, 0) };
            labels.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold });
            if (sub != null) labels.Children.Add(new TextBlock { Text = sub, FontSize = 11.5, Foreground = TextSub, Margin = new Thickness(0, 1, 0, 0) });
            g.Children.Add(labels);

            var knobShift = new TranslateTransform(value ? 18 : 0, 0);
            var sw = new Border
            {
                Width = 44, Height = 26, CornerRadius = new CornerRadius(13), HorizontalAlignment = HorizontalAlignment.Right,
                Background = value ? GreenBrush : Hex("#2EFFFFFF"), IsHitTestVisible = false,
                Child = new Ellipse
                {
                    Width = 22, Height = 22, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0),
                    RenderTransform = knobShift, Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = 0.35 },
                },
            };
            g.Children.Add(sw);
            bool on = value;
            g.MouseLeftButtonDown += (s, e) => e.Handled = true;
            g.MouseLeftButtonUp += (s, e) =>
            {
                on = !on;
                sw.Background = on ? GreenBrush : Hex("#2EFFFFFF");
                knobShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(on ? 18 : 0, TimeSpan.FromMilliseconds(200))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                set(on);
            };
            rows.Children.Add(g);
        }

        private static SolidColorBrush Hex(string hex) => (SolidColorBrush)Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

        private static Brush Freeze(Brush b)
        {
            b.Freeze();
            return b;
        }
    }
}
