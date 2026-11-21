using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace IslandPlayer
{
    /// <summary>Tryb imprezy: kod QR dla gości, włącznik i lista dodanych piosenek.</summary>
    public sealed class PartyWindow : Window
    {
        private static PartyWindow _current;

        public static void ShowWindow()
        {
            if (_current != null) { _current.Activate(); return; }
            _current = new PartyWindow();
            _current.Closed += (s, e) => _current = null;
            _current.Show();
            _current.Activate();
        }

        private readonly StackPanel _body = new StackPanel();
        private readonly SolidColorBrush _accent;
        private bool _closing;
        // po włączeniu imprezy Windows może pokazać okno zapory - wtedy nie zamykamy
        private DateTime _ignoreDeactivateUntil = DateTime.MinValue;
        private readonly Action<PartyRequest> _onAdded;
        private readonly Action _onState;

        private PartyWindow()
        {
            Title = L.T("Isabelle – tryb imprezy", "Isabelle – party mode");
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
                Margin = new Thickness(18), CornerRadius = new CornerRadius(26),
                BorderBrush = Hex("#2EFFFFFF"), BorderThickness = new Thickness(1),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.5, Direction = 270, Color = Colors.Black },
            };
            var root = new Grid();
            var glass = new GlassBackground();
            glass.SetColors(App.Island?.CoverImage, accentColor);
            root.Children.Add(glass);
            // zaokrąglone rogi dla całej zawartości (bez kwadratowych brzegów pod spodem)
            var clip = new RectangleGeometry { RadiusX = 25, RadiusY = 25 };
            root.Clip = clip;
            root.SizeChanged += (s, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            _body.Margin = new Thickness(26, 20, 26, 24);
            root.Children.Add(_body);
            card.Child = root;
            Content = card;
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) try { DragMove(); } catch { } };
            // kliknięcie poza okno = zamknij (jak menu)
            Deactivated += (s, e) =>
            {
                if (_closing || DateTime.UtcNow < _ignoreDeactivateUntil) return;
                _closing = true;
                try { Close(); } catch { }
            };
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };

            _onAdded = r => Dispatcher.BeginInvoke(new Action(Render));
            _onState = () => Dispatcher.BeginInvoke(new Action(Render));
            PartyServer.Added += _onAdded;
            PartyServer.StateChanged += _onState;
            Closed += (s, e) => { PartyServer.Added -= _onAdded; PartyServer.StateChanged -= _onState; };
            Render();
        }

        private void Render()
        {
            _body.Children.Clear();
            var titleRow = new Grid();
            titleRow.Children.Add(new TextBlock { Text = L.T("Tryb imprezy 🎉", "Party mode 🎉"), FontSize = 24, FontWeight = FontWeights.Bold });
            var close = new TextBlock { Text = "✕", FontSize = 16, Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Padding = new Thickness(8, 2, 2, 2) };
            close.MouseLeftButtonUp += (s, e) => Close();
            titleRow.Children.Add(close);
            _body.Children.Add(titleRow);
            _body.Children.Add(new TextBlock
            {
                Text = L.T("Goście skanują kod telefonem i dodają piosenki do kolejki – z Twojej Mojej muzyki",
                           "Guests scan the code with their phone and add songs to the queue – from your My music") +
                       (SpotifyService.CanPlay ? L.T(" i ze Spotify.", " and Spotify.") : L.T(" (ze Spotify też, gdy połączysz konto Premium w ustawieniach).", " (and Spotify, once you connect a Premium account in settings).")),
                FontSize = 12.5, Foreground = Hex("#B3FFFFFF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 14),
            });

            if (!PartyServer.Running)
            {
                if (PartyServer.Error != null)
                    _body.Children.Add(new TextBlock { Text = PartyServer.Error, FontSize = 12, Foreground = Hex("#FF8A80"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
                _body.Children.Add(BigButton(L.T("Zacznij imprezę", "Start the party"), true, () =>
                {
                    _ignoreDeactivateUntil = DateTime.UtcNow.AddSeconds(15);     // okno zapory Windows nie zamyka imprezy
                    PartyServer.Start();
                    Render();
                }));
                return;
            }

            // ---- kod QR ----
            var qrBox = new Border
            {
                Background = Brushes.White, CornerRadius = new CornerRadius(20), Padding = new Thickness(14), HorizontalAlignment = HorizontalAlignment.Center,
                Child = new Image { Source = Qr(PartyServer.Url), Width = 220, Height = 220, SnapsToDevicePixels = true },
            };
            RenderOptions.SetBitmapScalingMode(qrBox.Child, BitmapScalingMode.NearestNeighbor);
            _body.Children.Add(qrBox);
            var url = new TextBlock
            {
                Text = PartyServer.Url, FontSize = 12, Foreground = Hex("#B3FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 2),
                Cursor = Cursors.Hand, ToolTip = L.T("Kliknij, aby skopiować", "Click to copy"),
            };
            url.MouseLeftButtonUp += (s, e) => { try { Clipboard.SetText(PartyServer.Url); url.Text = L.T("Skopiowano ✓", "Copied ✓"); } catch { } };
            _body.Children.Add(url);
            _body.Children.Add(new TextBlock
            {
                Text = L.T("Telefony muszą być w tym samym Wi-Fi. Jeśli Windows zapyta o zaporę – zezwól dla sieci prywatnej.",
                           "Phones must be on the same Wi-Fi. If Windows asks about the firewall, allow it for private networks."),
                FontSize = 11.5, Foreground = Hex("#80FFFFFF"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 12),
            });

            // ---- dodane piosenki ----
            var list = new StackPanel();
            var recent = PartyServer.Recent(6);
            list.Children.Add(new TextBlock { Text = L.T("Dodane przez gości", "Added by guests"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Hex("#B3FFFFFF"), Margin = new Thickness(0, 0, 0, 6) });
            if (recent.Count == 0)
                list.Children.Add(new TextBlock { Text = L.T("Jeszcze nic – czekamy na pierwszą piosenkę.", "Nothing yet – waiting for the first song."), FontSize = 12.5, Foreground = Hex("#99FFFFFF") });
            foreach (var r in recent)
            {
                var row = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                row.Children.Add(new TextBlock { Text = r.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                row.Children.Add(new TextBlock { Text = r.Artist + " · " + r.By + " · " + r.At.ToString("HH:mm"), FontSize = 12, Foreground = Hex("#99FFFFFF"), TextTrimming = TextTrimming.CharacterEllipsis });
                list.Children.Add(row);
            }
            _body.Children.Add(new Border { Background = Hex("#26000000"), CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 12, 16, 12), Child = list, Margin = new Thickness(0, 0, 0, 14) });
            _body.Children.Add(BigButton(L.T("Zakończ imprezę", "End the party"), false, () => { PartyServer.Stop(); Render(); }));
        }

        /// <summary>Kod QR jako obrazek (QRCoder, bez rysowania przez System.Drawing).</summary>
        private static BitmapSource Qr(string text)
        {
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
                byte[] png = new QRCoder.PngByteQRCode(data).GetGraphic(12);
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
            catch (Exception ex) { Log.Error(ex); return null; }
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
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
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
