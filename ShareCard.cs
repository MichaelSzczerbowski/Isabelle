using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// Karta „teraz słucham” w stylu podglądu Spotify: tło w kolorze okładki, na środku karta z okładką,
    /// tytułem, wykonawcą i podpisem isabelle. PNG 1080×1920 (format relacji na Instagramie).
    /// </summary>
    public static class ShareCard
    {
        private const double W = 1080, H = 1920, CardW = 760, Pad = 40, ArtSize = CardW - 2 * Pad;

        /// <summary>Link do utworu: wyszukiwanie w Spotify (albo w YouTube, gdy gra przeglądarka).</summary>
        public static (string url, string label) Link(string appId, string title, string artist)
        {
            string q = Uri.EscapeDataString($"{artist} {title}".Trim());
            bool spotify = (appId ?? "").IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0
                        || (appId ?? "").IndexOf("chrome", StringComparison.OrdinalIgnoreCase) < 0
                           && (appId ?? "").IndexOf("edge", StringComparison.OrdinalIgnoreCase) < 0
                           && (appId ?? "").IndexOf("firefox", StringComparison.OrdinalIgnoreCase) < 0
                           && (appId ?? "").IndexOf("opera", StringComparison.OrdinalIgnoreCase) < 0;
            return spotify
                ? ("https://open.spotify.com/search/" + q, L.T("Posłuchaj na Spotify", "Listen on Spotify"))
                : ("https://www.youtube.com/results?search_query=" + q, L.T("Obejrzyj na YouTube", "Watch on YouTube"));
        }

        public static BitmapSource Render(BitmapSource cover, string title, string artist, Color coverColor)
        {
            // kolory jak w podglądzie Spotify: tło i karta w kolorze okładki, ani za ciemne, ani za jasne
            Color c = coverColor;
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            if (lum < 70) c = Mix(c, Colors.White, (70 - lum) / 255 * 1.6);
            else if (lum > 170) c = Mix(c, Colors.Black, (lum - 170) / 255 * 1.8);
            Color bgTop = c, bgBottom = Mix(c, Colors.Black, 0.6), cardColor = Mix(c, Colors.Black, 0.32);

            var root = new Canvas { Width = W, Height = H, ClipToBounds = true,
                                    Background = new LinearGradientBrush(bgTop, bgBottom, 90) };

            // karta na środku: okładka, tytuł, wykonawca, podpis
            var card = new Border
            {
                Width = CardW, CornerRadius = new CornerRadius(28), Background = new SolidColorBrush(cardColor),
                Padding = new Thickness(Pad),
                Effect = new DropShadowEffect { BlurRadius = 80, ShadowDepth = 24, Opacity = 0.45, Color = Colors.Black, Direction = 270 },
            };
            var stack = new StackPanel();
            card.Child = stack;

            stack.Children.Add(new Border
            {
                Width = ArtSize, Height = ArtSize, CornerRadius = new CornerRadius(14),
                Background = cover != null ? new ImageBrush(cover) { Stretch = Stretch.UniformToFill }
                                           : (Brush)new SolidColorBrush(Mix(c, Colors.Black, 0.5)),
            });
            stack.Children.Add(new TextBlock
            {
                Text = title ?? "", FontSize = 50, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 34, 0, 0),
            });
            stack.Children.Add(new TextBlock
            {
                Text = artist ?? "", FontSize = 36, Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                FontFamily = new FontFamily("Segoe UI"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0),
            });

            // podpis: słupki + odręczne „isabelle”
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 40, 0, 0) };
            foreach (var (h, col) in new[] { (16.0, Colors.White), (30.0, Color.FromRgb(255, 92, 160)), (22.0, Colors.White) })
                footer.Children.Add(new Rectangle { Width = 6, Height = h, RadiusX = 3, RadiusY = 3, Fill = new SolidColorBrush(col),
                                                    Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            footer.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(IslandWindow.HandwritingPath + " M 33 37 L 33.2 37.2"), Stretch = Stretch.Uniform, Height = 34,
                Stroke = Brushes.White, StrokeThickness = 3,
                StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(footer);

            card.Measure(new Size(CardW, double.PositiveInfinity));
            Canvas.SetLeft(card, (W - CardW) / 2);
            Canvas.SetTop(card, (H - card.DesiredSize.Height) / 2);
            root.Children.Add(card);

            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)W, (int)H, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(root);
            rtb.Freeze();
            return rtb;
        }

        /// <summary>Kopiuje sam link tekstem (klikalny na Discordzie, z podglądem utworu).</summary>
        public static void CopyText(string text)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetDataObject(text, true); break; }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(60); }
            }
        }

        /// <summary>Zapisuje kartę w Obrazy\Isabelle i kopiuje do schowka (obraz + plik). Zwraca ścieżkę pliku.</summary>
        public static string SaveAndCopy(BitmapSource card, string title, string artist)
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Isabelle");
            Directory.CreateDirectory(dir);
            string name = Safe($"{artist} - {title}");
            if (name.Length == 0) name = "isabelle";
            string path = System.IO.Path.Combine(dir, name + ".png");
            using (var fs = File.Create(path))
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(card));
                enc.Save(fs);
            }

            var data = new DataObject();
            data.SetImage(card);
            data.SetFileDropList(new StringCollection { path });
            for (int i = 0; i < 5; i++)
            {
                // schowek bywa chwilowo zajęty przez inny program
                try { Clipboard.SetDataObject(data, true); break; }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(60); }
            }
            return path;
        }

        private static string Safe(string s)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars();
            var clean = new string(s.Where(ch => !bad.Contains(ch)).ToArray()).Trim(' ', '-', '.');
            return clean.Length > 80 ? clean.Substring(0, 80).Trim() : clean;
        }

        private static Color Mix(Color a, Color b, double k) =>
            Color.FromRgb((byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
    }
}
