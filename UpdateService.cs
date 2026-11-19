using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    public sealed class UpdateInfo
    {
        public Version Version;
        public string SignatureUrl;   // Isabelle-Setup.exe.sig - podpis instalatora
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
                            string url = a.GetProperty("browser_download_url").GetString();
                            if (name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase)) info.InstallerUrl = url;
                            else if (name.EndsWith("Setup.exe.sig", StringComparison.OrdinalIgnoreCase)) info.SignatureUrl = url;
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
        /// <summary>Do pobierania instalatora (~75 MB): długi limit czasu, żeby działało też na wolnym internecie.</summary>
        private static readonly HttpClient Download = CreateDownloadClient();

        private static HttpClient CreateDownloadClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/" + Current.ToString(3));
            return c;
        }

        /// <summary>Pobiera plik z postępem 0..1.</summary>
        private static async Task<byte[]> DownloadAsync(string url, Action<double> progress)
        {
            using (var resp = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var ms = new MemoryStream(total > 0 ? (int)total : 1 << 20))
                {
                    var buf = new byte[1 << 16];
                    int n;
                    long done = 0;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        done += n;
                        if (total > 0) progress?.Invoke((double)done / total);
                    }
                    return ms.ToArray();
                }
            }
        }

        public static async Task InstallAsync(UpdateInfo info, Action<double> progress = null)
        {
            if (info == null) return;
            if (string.IsNullOrEmpty(info.InstallerUrl))
            {
                OpenPage(info);
                return;
            }
            try
            {
                byte[] data = await DownloadAsync(info.InstallerUrl, progress);

                // instalujemy tylko plik podpisany kluczem autora - podróbka (nawet z przejętego konta) zostanie odrzucona
                if (!await VerifyAsync(data, info))
                {
                    Log.Info("aktualizacje: podpis instalatora jest nieprawidłowy albo go brak - instalacja przerwana");
                    OpenPage(info);
                    return;
                }

                string path = Path.Combine(Path.GetTempPath(), "Isabelle-Setup-" + info.Version.ToString(3) + ".exe");
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

        /// <summary>
        /// Sprawdza podpis RSA instalatora kluczem publicznym wbudowanym w program.
        /// Klucz prywatny jest tylko na komputerze autora (folder keys, nigdy na GitHubie).
        /// </summary>
        private static async Task<bool> VerifyAsync(byte[] data, UpdateInfo info)
        {
            string publicKey = EmbeddedPublicKey();
            if (publicKey == null) return false;               // wersja bez klucza - nie ufamy niczemu
            if (string.IsNullOrEmpty(info.SignatureUrl)) return false;
            try
            {
                string sigText = (await Http.GetStringAsync(info.SignatureUrl)).Trim();
                byte[] signature = Convert.FromBase64String(sigText);
                using (var rsa = RSA.Create())
                {
                    rsa.FromXmlString(publicKey);
                    return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return false;
            }
        }

        private static string EmbeddedPublicKey()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("public.xml"))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s)) return r.ReadToEnd();
                }
            }
            catch
            {
                return null;
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
