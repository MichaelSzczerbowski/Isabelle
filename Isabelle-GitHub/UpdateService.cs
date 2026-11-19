using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string InstallerUrl;   // Isabelle-Setup.exe z wydania (może być null)
        public string PageUrl;        // strona wydania na GitHubie
    }

    /// <summary>Sprawdza najnowsze wydanie na GitHubie i instaluje je jednym kliknięciem.</summary>
    public static class UpdateService
    {
        /// <summary>Repozytorium na GitHubie: "login/nazwa". Ustaw po założeniu repozytorium.</summary>
        public const string Repo = "MichaelSzczerbowski/Isabelle";

        public static bool Configured => !Repo.StartsWith("TWOJ-LOGIN", StringComparison.Ordinal);
        public static UpdateInfo Available { get; private set; }
        public static event Action<UpdateInfo> Found;

        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/" + Current.ToString(3));
            c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return c;
        }

        public static Version Current
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        /// <summary>Zwraca informację o nowszej wersji albo null (brak / błąd sieci).</summary>
        public static async Task<UpdateInfo> CheckAsync()
        {
            if (!Configured) return null;
            try
            {
                string json = await Http.GetStringAsync("https://api.github.com/repos/" + Repo + "/releases/latest");
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    string tag = root.GetProperty("tag_name").GetString() ?? "";
                    if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
                    ver = new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build));
                    if (ver <= Current) return null;

                    var info = new UpdateInfo
                    {
                        Version = ver,
                        PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() : null,
                    };
                    if (root.TryGetProperty("assets", out var assets))
                    {
                        foreach (var a in assets.EnumerateArray())
                        {
                            string name = a.GetProperty("name").GetString() ?? "";
                            if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                info.InstallerUrl = a.GetProperty("browser_download_url").GetString();
                                break;
                            }
                        }
                    }
                    bool isNew = Available == null || Available.Version != info.Version;
                    Available = info;
                    if (isNew) Found?.Invoke(info);
                    return info;
                }
            }
            catch (Exception ex)
            {
                Log.Info("aktualizacje: " + ex.Message);
                return null;
            }
        }

        /// <summary>Pobiera instalator, uruchamia go po cichu i zamyka program (instalator włączy nową wersję).</summary>
        public static async Task InstallAsync(UpdateInfo info)
        {
            if (info == null) return;
            if (string.IsNullOrEmpty(info.InstallerUrl))
            {
                OpenPage(info);
                return;
            }
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "Isabelle-Setup-" + info.Version.ToString(3) + ".exe");
                byte[] data = await Http.GetByteArrayAsync(info.InstallerUrl);
                await File.WriteAllBytesAsync(path, data);
                Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART") { UseShellExecute = true });
                App.Quit();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                OpenPage(info);
            }
        }

        public static void OpenPage(UpdateInfo info)
        {
            string url = info?.PageUrl ?? "https://github.com/" + Repo + "/releases/latest";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error(ex); }
        }
    }
}
