using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace IslandPlayer
{
    /// <summary>Tapeta z okładki: rozmyta okładka na cały pulpit + ostra okładka z tytułem na środku. Po muzyce wraca stara tapeta.</summary>
    public static class WallpaperService
    {
        private const int SPI_SETDESKWALLPAPER = 0x0014, SPI_GETDESKWALLPAPER = 0x0073;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, string vparam, int winIni);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SystemParametersInfo(int action, int param, StringBuilder vparam, int winIni);

        private static int _flip;

        public static bool Active => App.Config.WallpaperActive;

        /// <summary>Ustawia tapetę z okładki (wołać z wątku interfejsu).</summary>
        public static void Apply(BitmapSource cover, string title, string artist)
        {
            if (cover == null) return;
            try
            {
                var c = App.Config;
                if (!c.WallpaperActive)
                {
                    // zapamiętaj obecną tapetę, żeby ją potem przywrócić
                    var sb = new StringBuilder(520);
                    SystemParametersInfo(SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0);
                    c.OriginalWallpaper = sb.ToString();
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                    {
                        c.OriginalWallpaperStyle = k?.GetValue("WallpaperStyle") as string ?? "10";
                        c.OriginalTileWallpaper = k?.GetValue("TileWallpaper") as string ?? "0";
                    }
                    c.WallpaperActive = true;
                    c.Save();
                }

                var screen = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
                int w = Math.Max(800, screen.Width), h = Math.Max(600, screen.Height);
                BitmapSource img = Render(cover, title, artist, w, h);

                _flip = 1 - _flip; // dwie nazwy na zmianę - Windows czasem nie odświeża tej samej ścieżki
                string path = Path.Combine(Settings.Folder, "wallpaper_" + _flip + ".jpg");
                var enc = new JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(path)) enc.Save(fs);

                Task.Run(() =>
                {
                    try
                    {
                        using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                        {
                            k?.SetValue("WallpaperStyle", "10"); // wypełnienie
                            k?.SetValue("TileWallpaper", "0");
                        }
                        SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, 0x01 | 0x02);
                    }
                    catch (Exception ex) { Log.Error(ex); }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        /// <summary>Przywraca tapetę sprzed włączenia funkcji.</summary>
        public static void Restore(bool wait = false)
        {
            var c = App.Config;
            if (!c.WallpaperActive) return;
            string path = c.OriginalWallpaper ?? "";
            string style = c.OriginalWallpaperStyle ?? "10", tile = c.OriginalTileWallpaper ?? "0";
            c.WallpaperActive = false;
            c.Save();
            void Do()
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", true))
                    {
                        k?.SetValue("WallpaperStyle", style);
                        k?.SetValue("TileWallpaper", tile);
                    }
                    SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, 0x01 | 0x02);
                }
                catch (Exception ex) { Log.Error(ex); }
            }
            if (wait) Do();
            else Task.Run(Do);
        }

        private static BitmapSource Render(BitmapSource cover, string title, string artist, int w, int h)
        {
            // 1) rozmyte tło liczone w małej rozdzielczości (szybko), potem rozciągnięte - wygląda jak mocne rozmycie
            int sw = Math.Max(64, w / 10), sh = Math.Max(36, h / 10);
            var small = new Grid { Width = sw, Height = sh, ClipToBounds = true, Background = Brushes.Black };
            small.Children.Add(new Image
            {
                Source = cover, Stretch = Stretch.UniformToFill, Width = sw * 1.3, Height = sh * 1.3,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Effect = new BlurEffect { Radius = Math.Max(8, sw / 18.0), KernelType = KernelType.Gaussian },
            });
            small.Measure(new Size(sw, sh));
            small.Arrange(new Rect(0, 0, sw, sh));
            var smallBmp = new RenderTargetBitmap(sw, sh, 96, 96, PixelFormats.Pbgra32);
            smallBmp.Render(small);

            // 2) pełna tapeta
            var root = new Grid { Width = w, Height = h, Background = Brushes.Black };
            var bg = new Image { Source = smallBmp, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(bg, BitmapScalingMode.HighQuality);
            root.Children.Add(bg);
            root.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)) });

            double size = h * 0.36;
            var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var art = new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.07),
                Background = new ImageBrush(cover) { Stretch = Stretch.UniformToFill },
                Effect = new DropShadowEffect { BlurRadius = size * 0.18, ShadowDepth = size * 0.03, Opacity = 0.55, Color = Colors.Black },
            };
            center.Children.Add(art);
            center.Children.Add(new TextBlock
            {
                Text = title ?? "", Foreground = Brushes.White, FontSize = h * 0.032, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, h * 0.03, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = w * 0.6,
            });
            center.Children.Add(new TextBlock
            {
                Text = artist ?? "", Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 255, 255, 255)), FontSize = h * 0.022,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, h * 0.006, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = w * 0.6,
            });
            root.Children.Add(center);
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            var full = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            full.Render(root);
            full.Freeze();
            return full;
        }
    }
}
