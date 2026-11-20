using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace IslandPlayer
{
    /// <summary>
    /// Okno „Co nowego” - raz, przy pierwszym uruchomieniu po aktualizacji. Lista zmian pochodzi z opisu
    /// wydania na GitHubie (ten sam tekst, który wpisujesz przy publikacji), a bez internetu - z krótkiej wersji wbudowanej.
    /// </summary>
    public sealed class WhatsNewWindow : Window
    {
        private const string Fallback =
            "## ✨ Nowości\n" +
            "- 🎵 **Moja muzyka** – własne pliki MP3/FLAC z okładkami i tekstami szukanymi automatycznie\n" +
            "- 📻 **Radio internetowe** – tysiące stacji, rankingi, gatunki, ulubione i wyrównana głośność\n" +
            "- ❤️ **Polubione i playlisty**\n" +
            "- 🫧 **Nowy wygląd okna** w kolorach tego, co gra\n" +
            "- 🐞 **Zgłoś błąd** prosto z programu";

        /// <summary>Wołane po starcie: pokazuje okno, jeśli wersja jest nowsza niż ostatnio widziana.</summary>
        public static void ShowIfUpdated()
        {
            var c = App.Config;
            string current = UpdateService.Current.ToString(3);
            string seen = c.LastSeenVersion ?? "";
            c.LastSeenVersion = current;
            c.Save();
            // świeża instalacja - nic nie pokazujemy (nie ma „co nowego”, wszystko jest nowe)
            if (seen.Length == 0 || !Version.TryParse(seen, out var old) || old >= UpdateService.Current) return;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            t.Tick += (s, e) => { t.Stop(); new WhatsNewWindow().Show(); };
            t.Start();
        }

        private readonly StackPanel _content = new StackPanel();

        private WhatsNewWindow()
        {
            Title = L.T("Isabelle – co nowego", "Isabelle – what's new");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 560;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;
            ShowActivated = true;

            var card = new Border
            {
                CornerRadius = new CornerRadius(24), BorderBrush = B("#2EFFFFFF"), BorderThickness = new Thickness(1), Margin = new Thickness(18),
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.5, Direction = 270 },
            };
            var inner = new Grid();
            var clip = new RectangleGeometry { RadiusX = 23, RadiusY = 23 };
            inner.Clip = clip;
            inner.SizeChanged += (s, e) => clip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            // to samo „płynne szkło” co w Mojej muzyce - w kolorach Isabelle
            var glass = new GlassBackground();
            glass.SetColors(null, Color.FromRgb(255, 92, 160));
            inner.Children.Add(glass);
            card.Child = inner;
            Content = card;

            var sp = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
            inner.Children.Add(sp);

            var head = new Grid { Background = Brushes.Transparent };
            head.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = L.T("Co nowego", "What's new"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = B("#B3FFFFFF") });
            titles.Children.Add(new TextBlock { Text = "Isabelle " + UpdateService.Current.ToString(3), FontSize = 26, FontWeight = FontWeights.Bold });
            head.Children.Add(titles);
            sp.Children.Add(head);

            var scroll = new ScrollViewer { MaxHeight = 430, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 14, 0, 0), Content = _content };
            sp.Children.Add(scroll);
            _content.Children.Add(new TextBlock { Text = L.T("Wczytuję listę zmian…", "Loading the changelog…"), Foreground = B("#99FFFFFF"), FontSize = 13 });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            buttons.Children.Add(Button(L.T("Na GitHubie", "On GitHub"), false, () =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/MichaelSzczerbowski/Isabelle/releases/tag/v" + UpdateService.Current.ToString(3)) { UseShellExecute = true }); }
                catch { }
            }));
            buttons.Children.Add(Button(L.T("Super!", "Great!"), true, Close));
            sp.Children.Add(buttons);

            KeyDown += (s, e) => { if (e.Key == Key.Escape || e.Key == Key.Enter) Close(); };
            Loaded += async (s, e) => Render(await LoadNotes() ?? Fallback);
        }

        private static async Task<string> LoadNotes()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/" + UpdateService.Current.ToString(3));
                string json = await http.GetStringAsync("https://api.github.com/repos/MichaelSzczerbowski/Isabelle/releases/tags/v" + UpdateService.Current.ToString(3));
                using var doc = JsonDocument.Parse(json);
                string body = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null;
                return string.IsNullOrWhiteSpace(body) ? null : body;
            }
            catch { return null; }
        }

        /// <summary>Prosty Markdown z opisu wydania: nagłówki „##”, punkty „-”, pogrubienia **…**, wcięte drugie linie.</summary>
        private void Render(string md)
        {
            _content.Children.Clear();
            TextBlock lastBullet = null;
            foreach (var raw in md.Replace("\r", "").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0) { lastBullet = null; continue; }
                if (line.StartsWith("#"))
                {
                    _content.Children.Add(new TextBlock
                    {
                        Text = line.TrimStart('#', ' '), FontSize = 15.5, FontWeight = FontWeights.Bold,
                        Margin = new Thickness(0, _content.Children.Count == 0 ? 0 : 16, 0, 8),
                    });
                    lastBullet = null;
                    continue;
                }
                bool bullet = line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* ");
                bool indented = !bullet && raw.StartsWith("  ") && lastBullet != null;
                if (indented)
                {
                    // druga linia punktu (np. tłumaczenie) - mniejsza i przygaszona
                    lastBullet.Inlines.Add(new LineBreak());
                    var run = new Span { Foreground = B("#99FFFFFF"), FontSize = 12.5 };
                    AddFormatted(run.Inlines, line.Trim());
                    lastBullet.Inlines.Add(run);
                    continue;
                }
                var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bullet ? 16 : 0) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (bullet) row.Children.Add(new TextBlock { Text = "•", FontSize = 14, Foreground = B("#99FFFFFF") });
                var tb = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
                AddFormatted(tb.Inlines, bullet ? line.TrimStart().Substring(2) : line.Trim());
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);
                _content.Children.Add(row);
                lastBullet = bullet ? tb : null;
            }
        }

        private static void AddFormatted(InlineCollection inlines, string text)
        {
            text = text.Replace("`", "");
            var parts = Regex.Split(text, @"(\*\*[^*]+\*\*)");
            foreach (var p in parts)
            {
                if (p.Length == 0) continue;
                if (p.StartsWith("**") && p.EndsWith("**") && p.Length > 4) inlines.Add(new Run(p.Substring(2, p.Length - 4)) { FontWeight = FontWeights.SemiBold });
                else inlines.Add(new Run(p));
            }
        }

        private static Border Button(string text, bool primary, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(18), Height = 36, Padding = new Thickness(20, 0, 20, 0), Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand,
                Background = primary ? Brushes.White : B("#22FFFFFF"),
                Child = new TextBlock
                {
                    Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = primary ? Brushes.Black : Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 15, Margin = new Thickness(0, 1, 0, 0),
                },
            };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
