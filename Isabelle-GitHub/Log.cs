using System;
using System.IO;

namespace IslandPlayer
{
    /// <summary>Prosty dziennik błędów: %APPDATA%\Isabelle\log.txt</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _last;

        public static void Error(Exception ex)
        {
            if (ex == null) return;
            Write(ex.ToString());
        }

        public static void Info(string text) => Write(text);

        private static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    if (text == _last) return; // ten sam błąd tylko raz
                    _last = text;
                    string path = Path.Combine(Settings.Folder, "log.txt");
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                    File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
                }
            }
            catch
            {
                // dziennik nie może wywrócić programu
            }
        }
    }
}
