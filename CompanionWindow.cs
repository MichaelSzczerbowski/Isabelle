using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>Isabelle w telefonie: włącznik, kod QR do zeskanowania i instrukcja dodania do ekranu.</summary>
    public sealed class CompanionWindow : Window
    {
        private static CompanionWindow _current;

        public static void ShowWindow()
        {
            if (_current != null) { _current.Activate(); return; }
            _current = new CompanionWindow();
            _current.Closed += (s, e) => _current = null;
            _current.Show();
            _current.Activate();
        }

        private readonly StackPanel _body = new StackPanel();
        private readonly SolidColorBrush _accent;
        private readonly Action _onState;
        private bool _closing;
        private DateTime _ignoreDeactivateUntil = DateTime.MinValue;

        private CompanionWindow()
        {
            Title = L.T("Isabelle w telefonie", "Isabelle on your phone");
            Width = 460;
            SizeToContent = SizeToContent.Height;
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
            _body.Margin = new Thickness(26, 20, 26, 24);
            root.Children.Add(_body);
            card.Child = root;
            Content = card;
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) try { DragMove(); } catch { } };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
            Deactivated += (s, e) =>
            {
                if (_closing || DateTime.UtcNow < _ignoreDeactivateUntil) return;
                _closing = true;
                try { Close(); } catch { }
            };
            _onState = () => Dispatcher.BeginInvoke(new Action(Render));
            CompanionServer.StateChanged += _onState;
            Closed += (s, e) => CompanionServer.StateChanged -= _onState;
            Render();
        }

        private void Render()
        {
            _body.Children.Clear();
            var titleRow = new Grid();
            titleRow.Children.Add(new TextBlock { Text = L.T("Isabelle w telefonie 📱", "Isabelle on your phone 📱"), FontSize = 24, FontWeight = FontWeights.Bold });
            var close = new TextBlock { Text = "✕", FontSize = 16, Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Padding = new Thickness(8, 2, 2, 2) };
            close.MouseLeftButtonDown += (s, e) => { e.Handled = true; Close(); };
            titleRow.Children.Add(close);
            _body.Children.Add(titleRow);
            _body.Children.Add(new TextBlock
            {
                Text = L.T("Pilot do komputera, tekst piosenki na żywo, Twoja biblioteka grana w telefonie, wyszukiwanie, Wrapped i tryb imprezy. Działa na iPhonie i Androidzie, w tym samym Wi-Fi.",
                           "Remote for your PC, live lyrics, your library played on the phone, search, Wrapped and party mode. Works on iPhone and Android, on the same Wi-Fi."),
                FontSize = 12.5, Foreground = Hex("#B3FFFFFF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
            });

            if (!CompanionServer.Running)
            {
                if (CompanionServer.Error != null)
                    _body.Children.Add(new TextBlock { Text = CompanionServer.Error, FontSize = 12, Foreground = Hex("#FF8A80"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
                _body.Children.Add(BigButton(L.T("Włącz", "Turn on"), true, () =>
                {
                    _ignoreDeactivateUntil = DateTime.UtcNow.AddSeconds(15);     // okno zapory Windows nie zamyka tego okna
                    App.Config.CompanionEnabled = true;
                    App.Config.Save();
                    CompanionServer.Start();
                    Render();
                }));
                return;
            }

            var qrBox = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(20), Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Image { Source = ToImage(CompanionServer.QrPng(CompanionServer.Url)), Width = 220, Height = 220 },
            };
            RenderOptions.SetBitmapScalingMode(qrBox.Child, BitmapScalingMode.NearestNeighbor);
            _body.Children.Add(qrBox);
            var url = new TextBlock { Text = CompanionServer.Url, FontSize = 11.5, Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 10), Cursor = Cursors.Hand, ToolTip = L.T("Kliknij, aby skopiować", "Click to copy") };
            url.MouseLeftButtonDown += (s, e) => { e.Handled = true; try { Clipboard.SetText(CompanionServer.Url); url.Text = L.T("Skopiowano ✓", "Copied ✓"); } catch { } };
            _body.Children.Add(url);

            var steps = new StackPanel();
            void Step(string n, string text) => steps.Children.Add(new TextBlock { Text = n + "  " + text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) });
            Step("1.", L.T("Zeskanuj kod aparatem telefonu.", "Scan the code with your phone camera."));
            Step("2.", L.T("iPhone: Safari → Udostępnij → „Do ekranu początkowego”.  Android: Chrome → ⋮ → „Dodaj do ekranu głównego”.",
                           "iPhone: Safari → Share → “Add to Home Screen”.  Android: Chrome → ⋮ → “Add to Home screen”."));
            Step("3.", L.T("Gotowe – ikonka Isabelle jest na ekranie telefonu i łączy się sama.", "Done – the Isabelle icon is on your home screen and connects by itself."));
            _body.Children.Add(new Border { Background = Hex("#26000000"), CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 12, 16, 12), Child = steps, Margin = new Thickness(0, 0, 0, 10) });
            _body.Children.Add(new TextBlock
            {
                Text = L.T("Jeśli Windows zapyta o zaporę – zezwól dla sieci prywatnej.", "If Windows asks about the firewall, allow it for private networks."),
                FontSize = 11.5, Foreground = Hex("#80FFFFFF"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 12),
            });

            var buttons = new UniformGrid { Columns = 2 };
            var off = BigButton(L.T("Wyłącz", "Turn off"), false, () =>
            {
                App.Config.CompanionEnabled = false;
                App.Config.Save();
                CompanionServer.Stop();
                Render();
            });
            off.Margin = new Thickness(0, 0, 5, 0);
            var reset = BigButton(L.T("Nowy kod", "New code"), false, () =>
            {
                _ignoreDeactivateUntil = DateTime.UtcNow.AddSeconds(60);
                if (MessageBox.Show(this, L.T("Nowy kod odłączy telefony, które już dodały Isabelle. Kontynuować?", "A new code disconnects phones that already added Isabelle. Continue?"),
                        "Isabelle", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    CompanionServer.ResetKey();
                _ignoreDeactivateUntil = DateTime.UtcNow.AddSeconds(1);
            });
            reset.Margin = new Thickness(5, 0, 0, 0);
            reset.ToolTip = L.T("Gdy zgubisz telefon albo chcesz odłączyć inne urządzenia", "If you lose a phone or want to disconnect other devices");
            buttons.Children.Add(off);
            buttons.Children.Add(reset);
            _body.Children.Add(buttons);
        }

        private static BitmapSource ToImage(byte[] png)
        {
            if (png == null) return null;
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(png))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }

        private Border BigButton(string text, bool accent, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 11, 18, 12), Cursor = Cursors.Hand,
                Background = accent ? _accent : Hex("#26FFFFFF"),
                Child = new TextBlock { Text = text, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = accent ? Brushes.Black : Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
            };
            b.MouseEnter += (s, e) => b.Opacity = 0.88;
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; click(); };
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
