using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace IslandPlayer
{
    /// <summary>Ikona w zasobniku obok zegara: ustawienia, autostart, zamykanie.</summary>
    public sealed class TrayIcon : IDisposable
    {
        private readonly WinForms.NotifyIcon _icon;
        private readonly Icon _image;

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr handle);

        private readonly WinForms.ToolStripMenuItem _settingsItem, _autoItem, _updateItem, _quitItem;

        public TrayIcon()
        {
            _image = MakeIcon();
            var menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };
            _updateItem = new WinForms.ToolStripMenuItem("", null, async (s, e) => await UpdateService.InstallAsync(UpdateService.Available)) { Visible = false };
            _settingsItem = new WinForms.ToolStripMenuItem("", null, (s, e) => App.ShowSettings());
            _autoItem = new WinForms.ToolStripMenuItem("") { CheckOnClick = true };
            _autoItem.Click += (s, e) => Autostart.Set(_autoItem.Checked);
            _quitItem = new WinForms.ToolStripMenuItem("", null, (s, e) => App.Quit());
            menu.Items.Add(_updateItem);
            menu.Items.Add(_settingsItem);
            menu.Items.Add(_autoItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(_quitItem);
            menu.Opening += (s, e) => RefreshMenu();
            RefreshMenu();

            _icon = new WinForms.NotifyIcon
            {
                Icon = _image,
                Text = "Isabelle",
                ContextMenuStrip = menu,
                Visible = true,
            };
            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left) App.ShowSettings();
            };
            _icon.BalloonTipClicked += async (s, e) => await UpdateService.InstallAsync(UpdateService.Available);
        }

        private void RefreshMenu()
        {
            var up = UpdateService.Available;
            _updateItem.Visible = up != null;
            if (up != null) _updateItem.Text = L.T("Zainstaluj aktualizację ", "Install update ") + up.Version.ToString(3);
            _settingsItem.Text = L.T("Ustawienia", "Settings");
            _autoItem.Text = L.T("Uruchamiaj z Windows", "Start with Windows");
            _autoItem.Checked = Autostart.IsEnabled;
            _quitItem.Text = L.T("Zamknij", "Quit");
        }

        /// <summary>Dymek "dostępna nowa wersja" (kliknięcie = instalacja).</summary>
        public void ShowUpdate(UpdateInfo info)
        {
            if (info == null) return;
            RefreshMenu();
            _icon.ShowBalloonTip(8000, "Isabelle " + info.Version.ToString(3),
                L.T("Dostępna nowa wersja. Kliknij, aby zaktualizować.", "A new version is available. Click to update."),
                WinForms.ToolTipIcon.Info);
        }

        /// <summary>Czarna "wyspa" z zieloną kropką, rysowana w kodzie (bez plików graficznych).</summary>
        /// <summary>Logo Isabelle (słupki, jeden różowy): z pliku .exe, a gdyby się nie dało - rysowane w kodzie.</summary>
        private static Icon MakeIcon()
        {
            try
            {
                var fromExe = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (fromExe != null) return fromExe;
            }
            catch { }

            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (var bg = new SolidBrush(Color.FromArgb(255, 14, 14, 16)))
                    using (var path = RoundRect(new RectangleF(0, 0, 32, 32), 8))
                        g.FillPath(bg, path);
                    float[] hs = { 8.5f, 16.5f, 12.5f, 6f };
                    for (int i = 0; i < 4; i++)
                    {
                        var c = i == 1 ? Color.FromArgb(255, 255, 92, 160) : Color.White;
                        float x = 6.5f + i * 5.2f, h = hs[i];
                        using (var b = new SolidBrush(c))
                        using (var bar = RoundRect(new RectangleF(x, 16 - h / 2, 3.6f, h), 1.8f))
                            g.FillPath(b, bar);
                    }
                }
                IntPtr h2 = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(h2).Clone();
                DestroyIcon(h2);
                return icon;
            }
        }

        private static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _image.Dispose();
        }
    }
}
