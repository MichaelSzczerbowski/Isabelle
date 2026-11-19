using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace IslandPlayer
{
    /// <summary>
    /// Instalacja bez uprawnień administratora: ten sam plik .exe kopiuje się do
    /// %LOCALAPPDATA%\Programs\Isabelle, tworzy skróty i wpis w "Aplikacje" Windows.
    /// </summary>
    public static class Installer
    {
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Isabelle";

        public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Isabelle");
        public static string Target => Path.Combine(Dir, "Isabelle.exe");
        private static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Isabelle.lnk");
        private static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Isabelle.lnk");

        public static bool IsInstalled => File.Exists(Target);
        public static bool HasDesktopShortcut =>
            File.Exists(DesktopLink) ||
            File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Island Player.lnk"));

        /// <summary>Zamyka działającą wyspę (np. przy aktualizacji), żeby można było podmienić plik.</summary>
        public static void StopRunning()
        {
            Kill("Isabelle");
            Kill("IslandPlayer");
        }

        private static void Kill(string name)
        {
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.Id == self) continue;
                    p.Kill();
                    p.WaitForExit(4000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        /// <summary>Część "ciężka" (można wołać w tle): zamknięcie starej wersji i skopiowanie programu.</summary>
        public static void CopyFiles()
        {
            StopRunning();
            Directory.CreateDirectory(Dir);
            string src = Environment.ProcessPath;
            Exception last = null;
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    File.Copy(src, Target, true);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(300);
                }
            }
            throw last ?? new IOException("copy failed");
        }

        /// <summary>
        /// Sprząta po starej nazwie "Island Player": druga wyspa, stary autostart, skróty, wpis w Aplikacjach i folder.
        /// </summary>
        public static void RemoveLegacy()
        {
            try
            {
                Kill("IslandPlayer");
                using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    run?.DeleteValue("IslandPlayer", false);
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Island Player.lnk"));
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Island Player.lnk"));
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\IslandPlayer", false); } catch { }
                string oldDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "IslandPlayer");
                if (Directory.Exists(oldDir))
                {
                    try { Directory.Delete(oldDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        /// <summary>Skróty, autostart i wpis do odinstalowania (wołać z wątku interfejsu).</summary>
        public static void Finish(bool desktop, bool autostart)
        {
            RemoveLegacy();
            CreateShortcut(StartMenuLink);
            if (desktop) CreateShortcut(DesktopLink);
            else TryDelete(DesktopLink);
            Autostart.SetPath(autostart ? Target : null);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "Isabelle");
                k.SetValue("DisplayVersion", UpdateService.Current.ToString(3));
                k.SetValue("Publisher", "Isabelle");
                k.SetValue("DisplayIcon", Target);
                k.SetValue("InstallLocation", Dir);
                k.SetValue("UninstallString", "\"" + Target + "\" --uninstall");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                try
                {
                    k.SetValue("EstimatedSize", (int)(new FileInfo(Target).Length / 1024), RegistryValueKind.DWord);
                }
                catch { }
            }
        }

        public static void Launch()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Target) { UseShellExecute = true, WorkingDirectory = Dir });
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        /// <summary>Usuwa program. Folder z plikiem .exe kasuje osobny proces chwilę po zamknięciu.</summary>
        public static void Uninstall(bool removeSettings)
        {
            StopRunning();
            Autostart.SetPath(null);
            TryDelete(StartMenuLink);
            TryDelete(DesktopLink);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (removeSettings)
            {
                try
                {
                    string cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Isabelle");
                    if (Directory.Exists(cfg)) Directory.Delete(cfg, true);
                }
                catch { }
            }
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Dir + "\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch { }
        }

        private static void CreateShortcut(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic link = shell.CreateShortcut(path);
                link.TargetPath = Target;
                link.WorkingDirectory = Dir;
                link.IconLocation = Target + ",0";
                link.Description = "Isabelle";
                link.Save();
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
