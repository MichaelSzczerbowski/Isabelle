using System.Globalization;

namespace IslandPlayer
{
    /// <summary>Polski / English. "auto" = język systemu (polski Windows → polski, każdy inny → angielski).</summary>
    public static class L
    {
        public static bool En { get; private set; }

        public static void Init(string setting)
        {
            if (setting == "pl") En = false;
            else if (setting == "en") En = true;
            else En = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "pl";
        }

        public static string T(string pl, string en) => En ? en : pl;
    }
}
