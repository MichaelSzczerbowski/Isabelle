using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace IslandPlayer
{
    public partial class App : Application
    {
        private Mutex _mutex;
        private TrayIcon _tray;
        private SettingsWindow _settingsWindow;

        public static Settings Config { get; private set; }
        public static IslandWindow Island { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // ---- instalator / deinstalator (ten sam plik .exe) ----
            var args = e.Args ?? new string[0];
            bool Has(string a) => Array.Exists(args, x => string.Equals(x, a, StringComparison.OrdinalIgnoreCase));
            string exeName = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            if (Has("--uninstall"))
            {
                Config = Settings.Load();
                L.Init(Config.Language);
                new InstallerWindow(true).Show();
                return;
            }
            if (Has("--install") || exeName.IndexOf("setup", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Config = Settings.Load();
                L.Init(Config.Language);
                if (Has("/SILENT") || Has("--silent"))
                {
                    // cicha aktualizacja z poziomu programu: podmień plik i uruchom nową wersję
                    try
                    {
                        bool desktop = Installer.HasDesktopShortcut, auto = Autostart.IsEnabled;
                        Installer.CopyFiles();
                        Installer.Finish(desktop, auto);
                        Installer.Launch();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex);
                    }
                    Shutdown();
                    return;
                }
                new InstallerWindow(false).Show();
                return;
            }

            // tylko jedna kopia programu
            _mutex = new Mutex(true, "Isabelle_SingleInstance_7F3A9C", out bool createdNew);
            if (!createdNew)
            {
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (s, ex) =>
            {
                Log.Error(ex.Exception);
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Error(ex.ExceptionObject as Exception);

            Config = Settings.Load();
            L.Init(Config.Language);
            // stara wersja (Island Player) nie może działać obok nowej
            Installer.RemoveLegacy();
            Island = new IslandWindow();
            Island.Show();
            _tray = new TrayIcon();

            UpdateService.Found += info => Current.Dispatcher.BeginInvoke(new Action(() => _tray?.ShowUpdate(info)));
            _ = UpdateLoopAsync();

            if (Config.FirstRun)
            {
                Config.FirstRun = false;
                Config.Save();
                ShowSettings(true);
            }
        }

        /// <summary>Sprawdza aktualizacje 20 s po starcie, potem co 12 godzin.</summary>
        private static async Task UpdateLoopAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            while (true)
            {
                if (Config.AutoUpdate) await UpdateService.CheckAsync();
                await Task.Delay(TimeSpan.FromHours(12));
            }
        }

        public static void ShowSettings(bool welcome = false)
        {
            var app = (App)Current;
            if (app._settingsWindow != null)
            {
                app._settingsWindow.Activate();
                return;
            }
            app._settingsWindow = new SettingsWindow(welcome);
            app._settingsWindow.Closed += (s, e) => app._settingsWindow = null;
            app._settingsWindow.Show();
            app._settingsWindow.Activate();
        }

        /// <summary>Po zmianie języka: nowe napisy w wyspie, menu i ustawieniach.</summary>
        public static void RefreshLanguage()
        {
            L.Init(Config.Language);
            Island?.ApplySettings();
            var app = (App)Current;
            var w = app._settingsWindow;
            if (w == null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                double left = w.Left, top = w.Top;
                w.Close();
                ShowSettings(false);
                if (app._settingsWindow != null)
                {
                    app._settingsWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                    app._settingsWindow.Left = left;
                    app._settingsWindow.Top = top;
                }
            }));
        }

        public static void Quit()
        {
            Current.Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _tray?.Dispose(); } catch { }
            try { Island?.Cleanup(); } catch { }
            base.OnExit(e);
        }
    }
}
