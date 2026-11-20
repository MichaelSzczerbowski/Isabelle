using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace IslandPlayer
{
    /// <summary>
    /// „co teraz gra” w radiu. Większość stacji (Icecast/Shoutcast) co kilka kilobajtów strumienia
    /// wplata krótki napis StreamTitle='Wykonawca - Tytuł'. Co kilkanaście sekund zaglądamy na chwilę po ten napis.
    /// </summary>
    public sealed class IcyReader
    {
        private static readonly HttpClient Http = CreateClient();
        private CancellationTokenSource _cts;

        /// <summary>Nowy tytuł (wołane z wątku tła).</summary>
        public event Action<string> TitleChanged;

        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Isabelle/1.5");
            return c;
        }

        public void Start(string url)
        {
            Stop();
            var cts = _cts = new CancellationTokenSource();
            _ = Task.Run(() => Loop(url, cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            _cts = null;
        }

        /// <summary>
        /// Zamiast drugiego, stałego połączenia (które zabierało łącze i potrafiło przerywać radio) łączymy się
        /// co ~15 s na chwilę: czytamy tylko do pierwszego napisu z tytułem i od razu się rozłączamy.
        /// </summary>
        private async Task Loop(string url, CancellationToken ct)
        {
            string last = null;
            int failures = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var (supported, title) = await ReadOnce(url, ct);
                    if (!supported) return;                     // stacja nie podaje tytułów
                    failures = 0;
                    if (title != null && title != last)
                    {
                        last = title;
                        TitleChanged?.Invoke(title);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Log.Info("icy: " + ex.Message);
                    if (++failures >= 4) return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch { return; }
            }
        }

        private static async Task<(bool supported, string title)> ReadOnce(string url, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Icy-MetaData", "1");
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!res.Headers.TryGetValues("icy-metaint", out var vals) || !int.TryParse(string.Join("", vals), out int metaint) || metaint <= 0 || metaint > 512 * 1024)
                return (false, null);
            using var s = await res.Content.ReadAsStreamAsync(timeout.Token);
            var skip = new byte[Math.Min(metaint, 64 * 1024)];
            // czasem pierwszy blok jest pusty (tytuł bez zmian) - sprawdzamy maks. 3 bloki
            for (int block = 0; block < 3; block++)
            {
                int left = metaint;
                while (left > 0)
                {
                    int n = await s.ReadAsync(skip.AsMemory(0, Math.Min(left, skip.Length)), timeout.Token);
                    if (n <= 0) throw new EndOfStreamException();
                    left -= n;
                }
                var lenBuf = new byte[1];
                if (await s.ReadAsync(lenBuf.AsMemory(0, 1), timeout.Token) <= 0) throw new EndOfStreamException();
                int len = lenBuf[0] * 16;
                if (len == 0) continue;
                var meta = new byte[len];
                int got = 0;
                while (got < len)
                {
                    int n = await s.ReadAsync(meta.AsMemory(got, len - got), timeout.Token);
                    if (n <= 0) throw new EndOfStreamException();
                    got += n;
                }
                return (true, Parse(meta));
            }
            return (true, null);
        }

        private static string Parse(byte[] meta)
        {
            // większość stacji używa UTF-8, starsze Latin-1 - wybieramy to, co nie daje „krzaczków”
            string text = Encoding.UTF8.GetString(meta).TrimEnd('\0');
            if (text.Contains('�')) text = Encoding.Latin1.GetString(meta).TrimEnd('\0');
            const string key = "StreamTitle='";
            int i = text.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int j = text.IndexOf("';", i, StringComparison.Ordinal);
            if (j < 0) j = text.LastIndexOf('\'');
            if (j <= i) return null;
            string t = text.Substring(i, j - i).Trim();
            return t.Length > 0 ? t : null;
        }
    }
}
