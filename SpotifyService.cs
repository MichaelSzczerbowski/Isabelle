using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace IslandPlayer
{
    /// <summary>
    /// Połączenie z kontem Spotify (logowanie w przeglądarce, bez hasła w Isabelle - PKCE).
    /// Z kontem Premium szybkie wyszukiwanie włącza całe piosenki w Spotify od razu, bez przełączania okna.
    /// </summary>
    public static class SpotifyService
    {
        /// <summary>Client ID aplikacji Isabelle z developer.spotify.com (można też wpisać własny w ustawieniach).</summary>
        public const string DefaultClientId = "";
        public const int Port = 43821;
        public const string RedirectUri = "http://127.0.0.1:43821/callback";
        private const string Scopes = "user-modify-playback-state user-read-playback-state";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private static string _access;
        private static DateTime _accessUntil = DateTime.MinValue;

        public static event Action Changed;

        public static string ClientId => !string.IsNullOrWhiteSpace(App.Config.SpotifyClientId) ? App.Config.SpotifyClientId.Trim() : DefaultClientId;
        public static bool Configured => ClientId.Length > 0;
        public static bool Connected => Configured && !string.IsNullOrEmpty(App.Config.SpotifyRefreshToken);
        public static bool Premium => App.Config.SpotifyPremium;
        public static string UserName => App.Config.SpotifyUser ?? "";
        /// <summary>Można włączać całe piosenki (połączone konto Premium).</summary>
        public static bool CanPlay => Connected && Premium;

        // ================= logowanie =================
        /// <summary>Otwiera logowanie Spotify w przeglądarce i czeka na powrót. Zwraca null albo opis błędu.</summary>
        public static async Task<string> ConnectAsync()
        {
            if (!Configured) return L.T("Najpierw wpisz Client ID.", "Enter a Client ID first.");
            string verifier = RandomUrlSafe(64), state = RandomUrlSafe(16);
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, Port);
                listener.Start();
            }
            catch (Exception ex) { return L.T("Port " + Port + " jest zajęty: ", "Port " + Port + " is busy: ") + ex.Message; }

            try
            {
                string url = "https://accounts.spotify.com/authorize?client_id=" + Uri.EscapeDataString(ClientId) +
                             "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                             "&code_challenge_method=S256&code_challenge=" + challenge +
                             "&state=" + state + "&scope=" + Uri.EscapeDataString(Scopes);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                // czekamy na powrót z przeglądarki (najwyżej 3 minuty)
                var accept = listener.AcceptTcpClientAsync();
                if (await Task.WhenAny(accept, Task.Delay(TimeSpan.FromMinutes(3))) != accept)
                    return L.T("Nie dokończono logowania.", "Sign-in wasn't finished.");
                using var client = await accept;
                using var stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, 0, buf.Length);
                string request = Encoding.ASCII.GetString(buf, 0, n);
                string path = request.Split(' ').Skip(1).FirstOrDefault() ?? "";
                var q = ParseQuery(path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "");

                bool ok = q.TryGetValue("code", out var code) && q.TryGetValue("state", out var st) && st == state;
                string page = ok
                    ? L.T("Połączono Isabelle ze Spotify. Możesz zamknąć tę kartę.", "Isabelle is connected to Spotify. You can close this tab.")
                    : L.T("Nie udało się połączyć. Wróć do Isabelle i spróbuj jeszcze raz.", "Couldn't connect. Go back to Isabelle and try again.");
                string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Isabelle</title></head>" +
                              "<body style=\"background:#111;color:#fff;font-family:Segoe UI,sans-serif;display:flex;align-items:center;justify-content:center;height:90vh\">" +
                              "<h2 style=\"font-weight:600\">" + WebUtility.HtmlEncode(page) + "</h2></body></html>";
                byte[] body = Encoding.UTF8.GetBytes(html);
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, 0, head.Length);
                await stream.WriteAsync(body, 0, body.Length);
                if (!ok) return q.TryGetValue("error", out var err) ? err : L.T("Logowanie przerwane.", "Sign-in cancelled.");

                // kod → klucze
                var tokens = await PostToken(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = RedirectUri,
                    ["client_id"] = ClientId,
                    ["code_verifier"] = verifier,
                });
                if (tokens == null) return L.T("Spotify nie wydał klucza.", "Spotify didn't issue a token.");
                await LoadProfile();
                Changed?.Invoke();
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return ex.Message;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        public static void Disconnect()
        {
            App.Config.SpotifyRefreshToken = "";
            App.Config.SpotifyUser = "";
            App.Config.SpotifyPremium = false;
            App.Config.Save();
            _access = null;
            _accessUntil = DateTime.MinValue;
            Changed?.Invoke();
        }

        private static async Task LoadProfile()
        {
            using var doc = await Api(HttpMethod.Get, "https://api.spotify.com/v1/me");
            if (doc == null) return;
            var r = doc.RootElement;
            App.Config.SpotifyUser = r.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : "";
            App.Config.SpotifyPremium = r.TryGetProperty("product", out var pr) && pr.GetString() == "premium";
            App.Config.Save();
        }

        // ================= klucze =================
        private static async Task<string> PostToken(Dictionary<string, string> form)
        {
            using var res = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
            string json = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
            {
                Log.Info("spotify token: " + (int)res.StatusCode + " " + json);
                // klucz odwołany (np. usunięty dostęp na stronie Spotify) - rozłączamy
                if (form["grant_type"] == "refresh_token" && res.StatusCode == HttpStatusCode.BadRequest) Disconnect();
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            _access = r.GetProperty("access_token").GetString();
            int sec = r.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 3600;
            _accessUntil = DateTime.UtcNow.AddSeconds(sec - 60);
            if (r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
            {
                App.Config.SpotifyRefreshToken = rt.GetString();
                App.Config.Save();
            }
            return _access;
        }

        private static async Task<string> Token()
        {
            if (_access != null && DateTime.UtcNow < _accessUntil) return _access;
            if (!Connected) return null;
            return await PostToken(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = App.Config.SpotifyRefreshToken,
                ["client_id"] = ClientId,
            });
        }

        /// <summary>Zapytanie do Web API; null przy błędzie albo pustej odpowiedzi.</summary>
        private static async Task<JsonDocument> Api(HttpMethod method, string url, string jsonBody = null)
        {
            var (status, body) = await Send(method, url, jsonBody);
            if (status < 200 || status >= 300 || string.IsNullOrWhiteSpace(body)) return null;
            return JsonDocument.Parse(body);
        }

        private static async Task<(int status, string body)> Send(HttpMethod method, string url, string jsonBody = null)
        {
            string token = await Token();
            if (token == null) return (401, null);
            using var req = new HttpRequestMessage(method, url);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var res = await Http.SendAsync(req);
            return ((int)res.StatusCode, await res.Content.ReadAsStringAsync());
        }

        // ================= wyszukiwanie i odtwarzanie =================
        public sealed class SpotifyTrack
        {
            public string Uri, Title, Artist, Art;
        }

        public static async Task<List<SpotifyTrack>> SearchAsync(string query, int limit)
        {
            var list = new List<SpotifyTrack>();
            using var doc = await Api(HttpMethod.Get, "https://api.spotify.com/v1/search?type=track&market=from_token&limit=" + limit + "&q=" + Uri.EscapeDataString(query));
            if (doc == null || !doc.RootElement.TryGetProperty("tracks", out var tracks)) return list;
            foreach (var t in tracks.GetProperty("items").EnumerateArray())
            {
                var item = new SpotifyTrack
                {
                    Uri = t.GetProperty("uri").GetString(),
                    Title = t.GetProperty("name").GetString(),
                    Artist = string.Join(", ", t.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()).Take(2)),
                };
                // okładka ~300 px (środkowa z listy rozmiarów)
                if (t.TryGetProperty("album", out var al) && al.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0)
                    item.Art = imgs[Math.Min(1, imgs.GetArrayLength() - 1)].GetProperty("url").GetString();
                list.Add(item);
            }
            return list;
        }

        /// <summary>
        /// Włącza utwór w Spotify od razu. Gdy Spotify nie gra na żadnym urządzeniu - wybiera ten komputer,
        /// a gdy Spotify jest wyłączony - uruchamia go zminimalizowanego i czeka, aż się zgłosi.
        /// </summary>
        public static async Task<string> PlayAsync(string uri)
        {
            try
            {
                string body = "{\"uris\":[\"" + uri + "\"]}";
                var (status, text) = await Send(HttpMethod.Put, "https://api.spotify.com/v1/me/player/play", body);
                if (status >= 200 && status < 300) return null;
                if (status == 403) return L.T("Spotify pozwala na to tylko z kontem Premium.", "Spotify only allows this with Premium.");
                if (status != 404) return "Spotify: " + status;

                // brak aktywnego urządzenia
                string device = await PickDevice();
                if (device == null)
                {
                    try { Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized }); } catch { }
                    for (int i = 0; i < 20 && device == null; i++)
                    {
                        await Task.Delay(700);
                        device = await PickDevice();
                    }
                }
                if (device == null) return L.T("Nie znaleziono Spotify na tym komputerze.", "Couldn't find Spotify on this PC.");
                (status, text) = await Send(HttpMethod.Put, "https://api.spotify.com/v1/me/player/play?device_id=" + Uri.EscapeDataString(device), body);
                return status >= 200 && status < 300 ? null : "Spotify: " + status;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return ex.Message;
            }
        }

        private static async Task<string> PickDevice()
        {
            using var doc = await Api(HttpMethod.Get, "https://api.spotify.com/v1/me/player/devices");
            if (doc == null || !doc.RootElement.TryGetProperty("devices", out var devs)) return null;
            string active = null, computer = null, any = null;
            foreach (var d in devs.EnumerateArray())
            {
                string id = d.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                if (id == null) continue;
                any ??= id;
                if (d.TryGetProperty("is_active", out var a) && a.GetBoolean()) active = id;
                if (d.TryGetProperty("type", out var t) && t.GetString() == "Computer" &&
                    d.TryGetProperty("name", out var nm) && string.Equals(nm.GetString(), Environment.MachineName, StringComparison.OrdinalIgnoreCase)) computer = id;
                else if (computer == null && d.TryGetProperty("type", out var t2) && t2.GetString() == "Computer") computer = id;
            }
            return computer ?? active ?? any;
        }

        // ================= pomocnicze =================
        private static string RandomUrlSafe(int length)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";
            var bytes = RandomNumberGenerator.GetBytes(length);
            var sb = new StringBuilder(length);
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static Dictionary<string, string> ParseQuery(string q)
        {
            var d = new Dictionary<string, string>();
            foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq), v = eq < 0 ? "" : part.Substring(eq + 1);
                d[Uri.UnescapeDataString(k)] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return d;
        }
    }
}
