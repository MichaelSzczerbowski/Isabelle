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
        private const string Scopes = "user-modify-playback-state user-read-playback-state user-read-private";

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
        private static TcpListener _listener;
        private static System.Threading.CancellationTokenSource _connectCts;

        /// <summary>Trwa logowanie (czekamy na powrót z przeglądarki).</summary>
        public static bool Connecting => _connectCts != null;

        /// <summary>Przerywa czekanie na logowanie i zwalnia port.</summary>
        private static System.Threading.CancellationTokenSource _userCancelled;

        public static void CancelConnect()
        {
            _userCancelled = _connectCts;
            try { _connectCts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        /// <summary>Client ID ze strony Spotify to 32 znaki 0-9 a-f.</summary>
        public static bool LooksLikeClientId(string id) =>
            !string.IsNullOrWhiteSpace(id) && id.Trim().Length == 32 && id.Trim().All(Uri.IsHexDigit);

        /// <summary>Otwiera logowanie Spotify w przeglądarce i czeka na powrót. Zwraca null albo opis błędu.</summary>
        public static async Task<string> ConnectAsync()
        {
            if (!Configured) return L.T("Najpierw wpisz Client ID.", "Enter a Client ID first.");
            // poprzednia próba jeszcze czeka - kończymy ją, zamiast blokować port
            CancelConnect();
            var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
            _connectCts = cts;
            string verifier = RandomUrlSafe(64), state = RandomUrlSafe(16);
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

            TcpListener listener = null;
            try
            {
                for (int attempt = 0; attempt < 5 && listener == null; attempt++)
                {
                    try
                    {
                        var l = new TcpListener(IPAddress.Loopback, Port);
                        l.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        l.Start();
                        listener = l;
                    }
                    catch (SocketException) { await Task.Delay(300); }
                }
                if (listener == null)
                    return L.T("Port " + Port + " jest zajęty przez inny program. Zamknij go albo uruchom ponownie komputer.",
                               "Port " + Port + " is used by another app. Close it or restart your PC.");
                _listener = listener;

                string url = "https://accounts.spotify.com/authorize?client_id=" + Uri.EscapeDataString(ClientId) +
                             "&response_type=code&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                             "&code_challenge_method=S256&code_challenge=" + challenge +
                             "&state=" + state + "&scope=" + Uri.EscapeDataString(Scopes);
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                // czekamy na powrót z przeglądarki; przeglądarki otwierają też puste połączenia „na zapas” - pomijamy je
                Dictionary<string, string> q = null;
                while (q == null)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(cts.Token); }
                    catch (OperationCanceledException) { return ReferenceEquals(_userCancelled, cts) ? "" : L.T("Nie dokończono logowania w 2 minuty.", "Sign-in wasn't finished within 2 minutes."); }
                    catch (ObjectDisposedException) { return ""; }
                    catch (SocketException) { return ""; }
                    using (client)
                    {
                        var stream = client.GetStream();
                        var buf = new byte[8192];
                        int n;
                        try
                        {
                            using var readCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                            readCts.CancelAfter(TimeSpan.FromSeconds(3));
                            n = await stream.ReadAsync(buf.AsMemory(0, buf.Length), readCts.Token);
                        }
                        catch { continue; }
                        if (n <= 0) continue;
                        string request = Encoding.ASCII.GetString(buf, 0, n);
                        string path = request.Split(' ').Skip(1).FirstOrDefault() ?? "";
                        if (!path.StartsWith("/callback"))
                        {
                            await Respond(stream, "404 Not Found", "");
                            continue;
                        }
                        q = ParseQuery(path.Contains('?') ? path.Substring(path.IndexOf('?') + 1) : "");
                        bool good = q.ContainsKey("code") && q.TryGetValue("state", out var st0) && st0 == state;
                        string page = good
                            ? L.T("Gotowe! Isabelle jest połączona ze Spotify. Możesz zamknąć tę kartę.", "Done! Isabelle is connected to Spotify. You can close this tab.")
                            : L.T("Nie udało się połączyć. Wróć do Isabelle i spróbuj jeszcze raz.", "Couldn't connect. Go back to Isabelle and try again.");
                        string html = "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Isabelle</title></head>" +
                                      "<body style=\"background:#111;color:#fff;font-family:Segoe UI,sans-serif;display:flex;align-items:center;justify-content:center;height:90vh\">" +
                                      "<h2 style=\"font-weight:600\">" + WebUtility.HtmlEncode(page) + "</h2>" +
                                      "<script>setTimeout(function(){window.close()},1500)</script></body></html>";
                        await Respond(stream, "200 OK", html);
                    }
                }
                try { listener.Stop(); } catch { }

                if (!q.TryGetValue("code", out var code) || !q.TryGetValue("state", out var st) || st != state)
                    return q.TryGetValue("error", out var err) ? (err == "access_denied" ? L.T("Anulowano logowanie.", "Sign-in cancelled.") : err) : L.T("Logowanie przerwane.", "Sign-in cancelled.");

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
                BringIsabelleBack();
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
                try { listener?.Stop(); } catch { }
                if (ReferenceEquals(_listener, listener)) _listener = null;
                if (ReferenceEquals(_connectCts, cts)) _connectCts = null;
                cts.Dispose();
            }
        }

        private static async Task Respond(NetworkStream stream, string status, string html)
        {
            try
            {
                byte[] body = Encoding.UTF8.GetBytes(html ?? "");
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, 0, head.Length);
                if (body.Length > 0) await stream.WriteAsync(body, 0, body.Length);
            }
            catch { }
        }

        /// <summary>Po zalogowaniu wracamy do okna ustawień Isabelle (przeglądarka zostaje w tle).</summary>
        private static void BringIsabelleBack()
        {
            try
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    foreach (System.Windows.Window w in System.Windows.Application.Current.Windows)
                        if (w is SettingsWindow && w.IsVisible) { w.Activate(); break; }
                }));
            }
            catch { }
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

        private static bool _profileChecked;

        private static async Task LoadProfile()
        {
            _profileChecked = true;
            using var doc = await Api(HttpMethod.Get, "https://api.spotify.com/v1/me");
            if (doc == null) return;
            var r = doc.RootElement;
            App.Config.SpotifyUser = r.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String ? dn.GetString() : "";
            // rodzaj konta widać tylko z uprawnieniem user-read-private (starsze połączenia go nie mają) -
            // gdy nie wiadomo, zakładamy Premium; Spotify i tak odmówi (403) bez Premium i wtedy to zapamiętamy
            App.Config.SpotifyPremium = !r.TryGetProperty("product", out var pr) || pr.ValueKind != JsonValueKind.String || pr.GetString() == "premium";
            App.Config.Save();
        }

        /// <summary>Raz na uruchomienie odświeża dane konta (np. ktoś kupił Premium albo połączył się starszą wersją).</summary>
        public static async Task EnsureProfileAsync()
        {
            if (_profileChecked || !Connected) return;
            try { await LoadProfile(); Changed?.Invoke(); } catch (Exception ex) { Log.Info("spotify profil: " + ex.Message); }
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

        /// <summary>Ostatni błąd Spotify (do podpowiedzi w wyszukiwaniu), null = wszystko działa.</summary>
        public static string LastError { get; private set; }

        /// <summary>Utwory ze Spotify; null = Spotify nie odpowiedział (powód w LastError).</summary>
        public static async Task<List<SpotifyTrack>> SearchAsync(string query, int limit)
        {
            var list = new List<SpotifyTrack>();
            var (status, text) = await Send(HttpMethod.Get, "https://api.spotify.com/v1/search?type=track&limit=" + limit + "&q=" + Uri.EscapeDataString(query));
            if (status < 200 || status >= 300 || string.IsNullOrWhiteSpace(text))
            {
                LastError = status + (string.IsNullOrWhiteSpace(text) ? "" : ": " + ErrorMessage(text));
                Log.Info("spotify szukaj: " + LastError);
                return null;
            }
            LastError = null;
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks)) return list;
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
        public static Task<string> PlayAsync(string uri) => PlayUrisAsync(new List<string> { uri });

        /// <summary>Włącza listę utworów (pierwszy od razu, reszta po kolei).</summary>
        public static async Task<string> PlayUrisAsync(List<string> uris)
        {
            try
            {
                string body = JsonSerializer.Serialize(new { uris });
                var (status, text) = await Send(HttpMethod.Put, "https://api.spotify.com/v1/me/player/play", body);
                if (status >= 200 && status < 300) return null;
                if (status == 403)
                {
                    // konto bez Premium - od teraz szybkie wyszukiwanie gra fragmenty
                    App.Config.SpotifyPremium = false;
                    App.Config.Save();
                    Changed?.Invoke();
                    return L.T("Spotify pozwala na to tylko z kontem Premium.", "Spotify only allows this with Premium.");
                }
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

        /// <summary>Dodaje utwór do kolejki Spotify (zagra po obecnym). Gdy Spotify nic nie gra - włącza go od razu.</summary>
        public static async Task<string> QueueAsync(string uri, bool playIfIdle = true)
        {
            try
            {
                var (status, text) = await Send(HttpMethod.Post, "https://api.spotify.com/v1/me/player/queue?uri=" + Uri.EscapeDataString(uri));
                if (status >= 200 && status < 300) return null;
                if (status == 404 && playIfIdle) return await PlayAsync(uri);          // brak aktywnego urządzenia
                if (status == 403) return L.T("Spotify pozwala na to tylko z kontem Premium.", "Spotify only allows this with Premium.");
                return "Spotify: " + status;
            }
            catch (Exception ex)
            {
                Log.Error(ex);
                return ex.Message;
            }
        }

        /// <summary>
        /// Włącza utwór od razu, ale przez kolejkę (dodaj + następny), więc potem Spotify wraca do swojej playlisty.
        /// Gdy Spotify nie jest aktywny - zwykłe włączenie.
        /// </summary>
        public static async Task<string> PlayNextAsync(string uri)
        {
            try
            {
                var (status, _) = await Send(HttpMethod.Post, "https://api.spotify.com/v1/me/player/queue?uri=" + Uri.EscapeDataString(uri));
                if (status < 200 || status >= 300) return await PlayAsync(uri);
                await Task.Delay(300);
                (status, _) = await Send(HttpMethod.Post, "https://api.spotify.com/v1/me/player/next");
                if (status < 200 || status >= 300) return await PlayAsync(uri);
                await Task.Delay(400);
                await Send(HttpMethod.Put, "https://api.spotify.com/v1/me/player/play");        // gdyby był na pauzie
                return null;
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

        /// <summary>Czytelny opis błędu z odpowiedzi Spotify ({"error":{"message":...}}).</summary>
        private static string ErrorMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var e = doc.RootElement.GetProperty("error");
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m)) return m.GetString();
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
            }
            catch { }
            return json.Length > 120 ? json.Substring(0, 120) : json;
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
