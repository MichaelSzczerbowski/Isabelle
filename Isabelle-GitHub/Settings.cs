using System;
using System.IO;
using System.Text.Json;

namespace IslandPlayer
{
    /// <summary>Ustawienia zapisywane w %APPDATA%\Isabelle\settings.json</summary>
    public sealed class Settings
    {
        public bool FirstRun { get; set; } = true;

        // odtwarzacz
        public bool Lyrics { get; set; } = true;
        public bool AlbumBackground { get; set; } = true;
        /// <summary>Rozmyta okładka w tle powoli płynie (jak w Apple Music na Macu).</summary>
        public bool AnimatedBackground { get; set; } = true;
        public bool Glow { get; set; } = true;
        public bool TrackToast { get; set; } = true;
        /// <summary>Zawsze mała kropka; pełny odtwarzacz dopiero po kliknięciu.</summary>
        public bool DotMode { get; set; }
        /// <summary>off / breathe / beat / strong</summary>
        public string CoverAnimation { get; set; } = "beat";
        /// <summary>hide / dot / clock</summary>
        public string IdleMode { get; set; } = "dot";

        // dźwięk
        public bool WheelVolume { get; set; } = true;
        public bool VolumeOsd { get; set; } = true;
        public bool FadeOnPause { get; set; } = true;

        // system
        public bool HideInFullscreen { get; set; } = true;
        public bool Hotkeys { get; set; } = true;
        public double Scale { get; set; } = 1.0;
        /// <summary>Krawędź ekranu: top / bottom / left / right</summary>
        public string Edge { get; set; } = "top";
        /// <summary>false = środek krawędzi głównego monitora; true = miejsce, gdzie wyspę przeciągnięto</summary>
        public bool CustomPosition { get; set; }
        public double AnchorX { get; set; }
        public double AnchorY { get; set; }
        /// <summary>auto / pl / en</summary>
        public string Language { get; set; } = "auto";
        public bool AutoUpdate { get; set; } = true;

        public static string Folder
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isabelle");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string FilePath => Path.Combine(Folder, "settings.json");

        public static Settings Load()
        {
            try
            {
                // przeniesienie ustawień z czasów, gdy aplikacja nazywała się Island Player
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandPlayer", "settings.json");
                if (!File.Exists(FilePath) && File.Exists(old)) File.Copy(old, FilePath);

                if (File.Exists(FilePath))
                {
                    var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath));
                    if (s != null) return s;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }
    }
}
