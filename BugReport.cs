using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace IslandPlayer
{
    /// <summary>
    /// „Zgłoś błąd”: opis problemu + (opcjonalnie) dziennik błędów i podstawowe dane systemu trafiają
    /// na kanał zgłoszeń na serwerze Isabelle (webhook Discorda). Gdy webhook nie jest ustawiony
    /// albo nie działa - otwieramy gotowe zgłoszenie na GitHubie.
    /// </summary>
    public static class BugReport
    {
        /// <summary>Adres webhooka kanału zgłoszeń na Discordzie (pusty = zgłoszenia przez GitHuba).</summary>
        public const string WebhookUrl = "";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static DateTime _lastSent = DateTime.MinValue;

        public static string SystemInfo()
        {
            var s = App.Config;
            return $"Isabelle {UpdateService.Current.ToString(3)} · Windows {Environment.OSVersion.Version} · " +
                   $"{(Environment.Is64BitOperatingSystem ? "x64" : "x86")} · {System.Globalization.CultureInfo.CurrentUICulture.Name} · " +
                   $"monitorów: {System.Windows.Forms.Screen.AllScreens.Length} · krawędź: {s.Edge}";
        }

        private static string LogTail()
        {
            try
            {
                string path = Path.Combine(Settings.Folder, "log.txt");
                if (!File.Exists(path)) return "";
                var text = File.ReadAllText(path);
                return text.Length > 60000 ? text.Substring(text.Length - 60000) : text;
            }
            catch { return ""; }
        }

        /// <summary>Wysyła zgłoszenie. Zwraca (udało się, komunikat dla użytkownika).</summary>
        public static async Task<(bool ok, string message)> SendAsync(string description, string contact, bool attachLog)
        {
            if ((DateTime.UtcNow - _lastSent).TotalSeconds < 60)
                return (false, L.T("Poczekaj chwilę przed wysłaniem kolejnego zgłoszenia.", "Please wait a moment before sending another report."));
            string info = SystemInfo();
            if (string.IsNullOrWhiteSpace(WebhookUrl)) return OpenGitHub(description, contact, info);
            try
            {
                string body = "**🐞 Zgłoszenie błędu**\n" + Trim(description, 1500) +
                              (string.IsNullOrWhiteSpace(contact) ? "" : "\n\n**Kontakt:** " + Trim(contact, 100)) +
                              "\n\n`" + info + "`";
                using var form = new MultipartFormDataContent();
                var payload = JsonSerializer.Serialize(new { username = "Isabelle – zgłoszenia", content = body, allowed_mentions = new { parse = new string[0] } });
                form.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
                if (attachLog)
                {
                    string log = LogTail();
                    if (log.Length > 0) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(log)), "files[0]", "log.txt");
                }
                using var res = await Http.PostAsync(WebhookUrl, form);
                if (!res.IsSuccessStatusCode) throw new HttpRequestException(((int)res.StatusCode).ToString());
                _lastSent = DateTime.UtcNow;
                return (true, L.T("Dzięki! Zgłoszenie zostało wysłane.", "Thanks! Your report has been sent."));
            }
            catch (Exception ex)
            {
                Log.Info("zgłoszenie: " + ex.Message);
                return OpenGitHub(description, contact, info);
            }
        }

        /// <summary>Zapasowo: gotowe zgłoszenie na GitHubie (użytkownik klika tylko „Submit”).</summary>
        private static (bool, string) OpenGitHub(string description, string contact, string info)
        {
            try
            {
                string body = Trim(description, 3000) + (string.IsNullOrWhiteSpace(contact) ? "" : "\n\nKontakt: " + contact) + "\n\n---\n" + info;
                string url = "https://github.com/MichaelSzczerbowski/Isabelle/issues/new?title=" + Uri.EscapeDataString("Błąd: " + Trim(description.Split('\n')[0], 70)) +
                             "&body=" + Uri.EscapeDataString(body);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                _lastSent = DateTime.UtcNow;
                return (true, L.T("Otworzyłem gotowe zgłoszenie na GitHubie - wystarczy je wysłać.", "Opened a ready-made report on GitHub - just submit it."));
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return (false, L.T("Nie udało się wysłać zgłoszenia.", "Couldn't send the report."));
            }
        }

        private static string Trim(string s, int max) => (s ?? "").Length <= max ? s ?? "" : s.Substring(0, max) + "…";
    }

    /// <summary>Okno „Zgłoś błąd” w stylu Isabelle.</summary>
    public sealed class BugReportWindow : Window
    {
        private static BugReportWindow _current;

        public static void ShowWindow()
        {
            if (_current != null) { _current.Activate(); return; }
            _current = new BugReportWindow();
            _current.Closed += (s, e) => _current = null;
            _current.Show();
            _current.Activate();
        }

        private readonly TextBox _desc = new TextBox(), _contact = new TextBox();
        private readonly CheckBox _log = new CheckBox();
        private readonly TextBlock _status = new TextBlock();
        private Border _send;
        private bool _busy;

        private BugReportWindow()
        {
            Title = L.T("Isabelle – zgłoś błąd", "Isabelle – report a bug");
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.Height;
            Width = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;

            var card = new Border
            {
                CornerRadius = new CornerRadius(22), Background = B("#F2141418"), BorderBrush = B("#26FFFFFF"), BorderThickness = new Thickness(1),
                Padding = new Thickness(26, 22, 26, 22), Margin = new Thickness(18),
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.5, Direction = 270 },
            };
            var sp = new StackPanel();
            card.Child = sp;
            Content = card;

            var head = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = Brushes.Transparent };
            head.MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
            head.Children.Add(new TextBlock { Text = "🐞  " + L.T("Zgłoś błąd", "Report a bug"), FontSize = 21, FontWeight = FontWeights.Bold });
            var close = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = B("#17FFFFFF"), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L10,10 M10,0 L0,10"), Stroke = Brushes.White, StrokeThickness = 1.6, Width = 10, Height = 10, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            close.MouseLeftButtonDown += (s, e) => e.Handled = true;
            close.MouseLeftButtonUp += (s, e) => Close();
            head.Children.Add(close);
            sp.Children.Add(head);
            sp.Children.Add(new TextBlock
            {
                Text = L.T("Opisz, co się stało i jak to powtórzyć. Zgłoszenie trafi prosto do twórcy Isabelle.",
                           "Describe what happened and how to reproduce it. The report goes straight to Isabelle's developer."),
                FontSize = 13, Foreground = B("#99FFFFFF"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            });

            sp.Children.Add(Field(_desc, L.T("Np. po zmianie utworu w Spotify okładka się nie zmienia…", "E.g. the cover doesn't change after switching tracks in Spotify…"), 130, true));
            sp.Children.Add(Field(_contact, L.T("Twój nick na Discordzie albo e-mail (opcjonalnie)", "Your Discord name or e-mail (optional)"), 0, false));

            _log.IsChecked = true;
            _log.Foreground = Brushes.White;
            _log.Margin = new Thickness(2, 12, 0, 0);
            _log.Content = new TextBlock
            {
                Text = L.T("Dołącz dziennik błędów i informacje o systemie (bez danych osobowych)", "Attach the error log and system info (no personal data)"),
                FontSize = 12.5, Foreground = B("#CCFFFFFF"), TextWrapping = TextWrapping.Wrap,
            };
            sp.Children.Add(_log);
            sp.Children.Add(new TextBlock { Text = BugReport.SystemInfo(), FontSize = 11, Foreground = B("#66FFFFFF"), Margin = new Thickness(24, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

            var bottom = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            _status.FontSize = 12.5;
            _status.TextWrapping = TextWrapping.Wrap;
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(0, 0, 150, 0);
            bottom.Children.Add(_status);
            _send = new Border
            {
                CornerRadius = new CornerRadius(18), Height = 36, Padding = new Thickness(22, 0, 22, 0), Background = Brushes.White, Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = new TextBlock { Text = L.T("Wyślij", "Send"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, VerticalAlignment = VerticalAlignment.Center, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 15, Margin = new Thickness(0, 1, 0, 0) },
            };
            _send.MouseLeftButtonDown += (s, e) => e.Handled = true;
            _send.MouseLeftButtonUp += async (s, e) => await Send();
            bottom.Children.Add(_send);
            sp.Children.Add(bottom);

            Loaded += (s, e) => _desc.Focus();
            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private async Task Send()
        {
            if (_busy) return;
            if (_desc.Text.Trim().Length < 10)
            {
                _status.Foreground = B("#FF6961");
                _status.Text = L.T("Napisz kilka słów więcej o problemie.", "Please describe the problem in a few more words.");
                return;
            }
            _busy = true;
            _send.Opacity = 0.5;
            _status.Foreground = B("#99FFFFFF");
            _status.Text = L.T("Wysyłam…", "Sending…");
            var (ok, msg) = await BugReport.SendAsync(_desc.Text.Trim(), _contact.Text.Trim(), _log.IsChecked == true);
            _busy = false;
            _send.Opacity = 1;
            _status.Foreground = ok ? B("#34C759") : B("#FF6961");
            _status.Text = msg;
            if (ok)
            {
                _desc.Text = "";
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
                t.Tick += (s, e) => { t.Stop(); Close(); };
                t.Start();
            }
        }

        private static Border Field(TextBox box, string hint, double height, bool multi)
        {
            var g = new Grid();
            var h = new TextBlock { Text = hint, Foreground = B("#55FFFFFF"), FontSize = 13.5, IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap };
            box.Background = Brushes.Transparent;
            box.Foreground = Brushes.White;
            box.CaretBrush = Brushes.White;
            box.BorderThickness = new Thickness(0);
            box.FontSize = 13.5;
            box.TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap;
            box.AcceptsReturn = multi;
            box.MaxLength = multi ? 3000 : 120;
            if (multi) { box.Height = height; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            box.TextChanged += (s, e) => h.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            g.Children.Add(h);
            g.Children.Add(box);
            return new Border { Background = B("#12FFFFFF"), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 10), Child = g };
        }

        private static SolidColorBrush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
