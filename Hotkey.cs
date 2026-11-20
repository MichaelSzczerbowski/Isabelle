using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace IslandPlayer
{
    /// <summary>Skróty klawiszowe zapisywane jako tekst, np. "Ctrl+Alt+Right".</summary>
    public static class Hotkey
    {
        /// <summary>Akcje, które można przypisać do skrótu (id dla RegisterHotKey, klucz w ustawieniach).</summary>
        public static readonly (int id, string key, string pl, string en, string def)[] Actions =
        {
            (1, "play", "Pauza / wznów", "Play / pause", "Ctrl+Alt+P"),
            (2, "next", "Następny utwór", "Next track", "Ctrl+Alt+Right"),
            (3, "prev", "Poprzedni utwór", "Previous track", "Ctrl+Alt+Left"),
            (4, "show", "Pokaż / schowaj odtwarzacz", "Show / hide player", "Ctrl+Alt+I"),
            (5, "volup", "Głośniej", "Volume up", "Ctrl+Alt+Up"),
            (6, "voldown", "Ciszej", "Volume down", "Ctrl+Alt+Down"),
        };

        public static string Default(string key)
        {
            foreach (var a in Actions) if (a.key == key) return a.def;
            return "";
        }

        /// <summary>Zamienia naciśnięte klawisze na tekst do zapisania.</summary>
        public static string Format(ModifierKeys mods, Key key)
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
        }

        /// <summary>Tekst → modyfikatory i kod klawisza dla RegisterHotKey. False = pusty albo błędny skrót.</summary>
        public static bool Parse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim())
                {
                    case "Ctrl": mods |= Native.MOD_CONTROL; break;
                    case "Alt": mods |= Native.MOD_ALT; break;
                    case "Shift": mods |= Native.MOD_SHIFT; break;
                    case "Win": mods |= Native.MOD_WIN; break;
                }
            }
            if (!Enum.TryParse(parts[parts.Length - 1].Trim(), out Key key) || key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        /// <summary>Kawałki do pokazania jako "klawisze": Ctrl, Alt, →.</summary>
        public static List<string> Pretty(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            foreach (string raw in text.Split('+'))
            {
                string p = raw.Trim();
                if (Enum.TryParse(p, out Key k)) p = KeyName(k);
                list.Add(p);
            }
            return list;
        }

        public static bool IsModifier(Key k) =>
            k == Key.LeftCtrl || k == Key.RightCtrl || k == Key.LeftAlt || k == Key.RightAlt ||
            k == Key.LeftShift || k == Key.RightShift || k == Key.LWin || k == Key.RWin || k == Key.System;

        /// <summary>Klawisze, które mogą działać bez Ctrl/Alt/Shift (F1-F24 i klawisze multimedialne).</summary>
        public static bool AllowedAlone(Key k) =>
            (k >= Key.F1 && k <= Key.F24) || k == Key.MediaPlayPause || k == Key.MediaNextTrack || k == Key.MediaPreviousTrack ||
            k == Key.Pause || k == Key.Scroll;

        private static string KeyName(Key k)
        {
            switch (k)
            {
                case Key.Left: return "←";
                case Key.Right: return "→";
                case Key.Up: return "↑";
                case Key.Down: return "↓";
                case Key.Space: return L.T("Spacja", "Space");
                case Key.Return: return "Enter";
                case Key.Back: return "Backspace";
                case Key.OemPlus: return "+";
                case Key.OemMinus: return "-";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.PageUp: return "PgUp";
                case Key.PageDown: return "PgDn";
                case Key.MediaPlayPause: return "⏯";
                case Key.MediaNextTrack: return "⏭";
                case Key.MediaPreviousTrack: return "⏮";
            }
            if (k >= Key.D0 && k <= Key.D9) return ((int)(k - Key.D0)).ToString();
            if (k >= Key.NumPad0 && k <= Key.NumPad9) return "Num " + (int)(k - Key.NumPad0);
            return k.ToString();
        }
    }
}
