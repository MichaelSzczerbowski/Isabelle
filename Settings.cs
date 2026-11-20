using System;
using System.Collections.Generic;
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
        /// <summary>Tytuł piosenki na środku małej wyspy.</summary>
        public bool CompactTitle { get; set; }
        /// <summary>Zawsze mała kropka; pełny odtwarzacz dopiero po kliknięciu.</summary>
        public bool DotMode { get; set; }
        /// <summary>off / breathe / beat / strong</summary>
        public string CoverAnimation { get; set; } = "beat";
        /// <summary>hide / dot / clock</summary>
        public string IdleMode { get; set; } = "dot";

        // dźwięk
        public bool WheelVolume { get; set; } = true;
        /// <summary>Kółko zmienia głośność samego odtwarzacza (np. Spotify), a nie całego komputera.</summary>
        public bool AppVolume { get; set; } = true;
        public bool VolumeOsd { get; set; } = true;
        public bool FadeOnPause { get; set; } = true;

        // system
        public bool HideInFullscreen { get; set; } = true;
        public bool Hotkeys { get; set; } = true;
        public double Scale { get; set; } = 1.0;
        /// <summary>Własne skróty: akcja → np. "Ctrl+Alt+P" (pusty tekst = wyłączony, brak wpisu = domyślny).</summary>
        public Dictionary<string, string> Shortcuts { get; set; } = new Dictionary<string, string>();

        public string Shortcut(string action) =>
            Shortcuts != null && Shortcuts.TryGetValue(action, out var s) ? s : Hotkey.Default(action);
        /// <summary>Krawędź ekranu: top / bottom / left / right</summary>
        public string Edge { get; set; } = "top";
        /// <summary>false = środek krawędzi głównego monitora; true = miejsce, gdzie wyspę przeciągnięto</summary>
        public bool CustomPosition { get; set; }
        public double AnchorX { get; set; }
        public double AnchorY { get; set; }
        /// <summary>Anchor zapisany w pikselach ekranu (nowy format) + nazwa monitora.</summary>
        public bool AnchorPx { get; set; }
        public string MonitorName { get; set; }
        /// <summary>Kliknięcie otwiera odtwarzacz na stałe - zamyka go dopiero kliknięcie gdzie indziej.</summary>
        public bool ClickToPin { get; set; }
        /// <summary>Najechanie myszką nie otwiera odtwarzacza - tylko kliknięcie.</summary>
        public bool ClickToOpen { get; set; }
        /// <summary>Szybkie przesunięcie wyspy w bok (myszą albo dwoma palcami na touchpadzie) zmienia utwór.</summary>
        public bool Gestures { get; set; } = true;
        // ---- personalizacja ----
        /// <summary>cover (z okładki) / pink / green / blue / purple / orange / red / white</summary>
        public string Accent { get; set; } = "cover";
        /// <summary>black / glass / graphite</summary>
        public string IslandStyle { get; set; } = "black";
        /// <summary>bars / dots / none</summary>
        public string BarsStyle { get; set; } = "bars";
        /// <summary>accent / white</summary>
        public string BarsColor { get; set; } = "accent";
        /// <summary>round / soft / square</summary>
        public string Corners { get; set; } = "round";
        /// <summary>s / m / l</summary>
        public string LyricsSize { get; set; } = "m";
        /// <summary>calm / normal / fast</summary>
        public string AnimSpeed { get; set; } = "normal";
        /// <summary>Pasek postępu w kolorze akcentu zamiast białego.</summary>
        public bool AccentProgress { get; set; }
        /// <summary>Wyspa "wyskakuje" z logo przy uruchomieniu.</summary>
        public bool StartupAnimation { get; set; } = true;

        // ---- dodatki ----
        public bool OverlayEnabled { get; set; }
        public int OverlayPort { get; set; } = 4510;
        public bool DiscordEnabled { get; set; }
        public bool DiscordLyrics { get; set; } = true;
        public bool MicDuck { get; set; }
        /// <summary>duck (ścisz) / pause</summary>
        public string MicMode { get; set; } = "duck";
        public bool AlbumWallpaper { get; set; }
        public bool WallpaperActive { get; set; }
        public string OriginalWallpaper { get; set; }
        public string OriginalWallpaperStyle { get; set; }
        public string OriginalTileWallpaper { get; set; }

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
