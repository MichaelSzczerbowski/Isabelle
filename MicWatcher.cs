using System;
using Microsoft.Win32;

namespace IslandPlayer
{
    /// <summary>
    /// Czy jakiś program właśnie używa mikrofonu (Discord, Teams, gra...).
    /// Windows zapisuje to w rejestrze: LastUsedTimeStop = 0 oznacza "używa teraz".
    /// </summary>
    public static class MicWatcher
    {
        private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

        public static bool InUse()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(Root))
                {
                    if (key == null) return false;
                    if (Scan(key)) return true;
                    using (var np = key.OpenSubKey("NonPackaged"))
                        if (np != null && Scan(np)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool Scan(RegistryKey parent)
        {
            foreach (string name in parent.GetSubKeyNames())
            {
                if (name.Equals("NonPackaged", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.IndexOf("Isabelle", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try
                {
                    using (var app = parent.OpenSubKey(name))
                    {
                        if (app == null) continue;
                        long start = Convert.ToInt64(app.GetValue("LastUsedTimeStart") ?? 0L);
                        long stop = Convert.ToInt64(app.GetValue("LastUsedTimeStop") ?? 0L);
                        if (start != 0 && stop == 0) return true;
                    }
                }
                catch { }
            }
            return false;
        }
    }
}
