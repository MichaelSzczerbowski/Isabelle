using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace IslandPlayer
{
    /// <summary>
    /// Świecące krawędzie ekranu w kolorze okładki, pulsujące z muzyką.
    /// Cztery cienkie, "przezroczyste dla myszy" paski zamiast jednego okna na cały ekran - dużo mniej pracy.
    /// </summary>
    public sealed class EdgeGlow
    {
        private const double Thick = 46;
        private readonly Window[] _strips = new Window[4];
        private readonly Border[] _fills = new Border[4];
        private Color _color = Color.FromRgb(255, 92, 160);
        private double _op;
        private bool _shown;

        public EdgeGlow()
        {
            for (int i = 0; i < 4; i++)
            {
                var fill = new Border { IsHitTestVisible = false };
                var w = new Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    Topmost = true,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    ResizeMode = ResizeMode.NoResize,
                    IsHitTestVisible = false,
                    Focusable = false,
                    Content = fill,
                    Opacity = 0,
                };
                w.SourceInitialized += (s, e) =>
                {
                    IntPtr h = new WindowInteropHelper((Window)s).Handle;
                    // przezroczyste dla myszy (WS_EX_TRANSPARENT) + bez aktywacji + bez paska zadań
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                    Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex | 0x20 | 0x80000 | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
                };
                _strips[i] = w;
                _fills[i] = fill;
            }
            Paint();
        }

        /// <summary>Ustawia paski przy krawędziach monitora (współrzędne w DIP).</summary>
        public void Place(Rect screen)
        {
            Set(0, screen.Left, screen.Top, screen.Width, Thick);                      // góra
            Set(1, screen.Left, screen.Bottom - Thick, screen.Width, Thick);           // dół
            Set(2, screen.Left, screen.Top, Thick, screen.Height);                     // lewo
            Set(3, screen.Right - Thick, screen.Top, Thick, screen.Height);            // prawo
        }

        private void Set(int i, double x, double y, double w, double h)
        {
            var s = _strips[i];
            s.Left = x;
            s.Top = y;
            s.Width = w;
            s.Height = h;
        }

        public void SetColor(Color c)
        {
            if (c == _color) return;
            _color = c;
            Paint();
        }

        private void Paint()
        {
            Color solid = Color.FromArgb(200, _color.R, _color.G, _color.B), none = Color.FromArgb(0, _color.R, _color.G, _color.B);
            _fills[0].Background = Freeze(new LinearGradientBrush(solid, none, 90));   // góra: od krawędzi w dół
            _fills[1].Background = Freeze(new LinearGradientBrush(none, solid, 90));   // dół
            _fills[2].Background = Freeze(new LinearGradientBrush(solid, none, 0));    // lewo
            _fills[3].Background = Freeze(new LinearGradientBrush(none, solid, 0));    // prawo
        }

        private static Brush Freeze(Brush b)
        {
            b.Freeze();
            return b;
        }

        /// <summary>Co klatkę: target 0..1 (0 = ukryte). Płynnie dochodzi do celu.</summary>
        public void Update(double target, int steps)
        {
            double k = Math.Min(1, 0.25 * steps);
            _op += (target - _op) * k;
            if (Math.Abs(_op - target) < 0.01) _op = target;

            bool show = _op > 0.005;
            if (show != _shown)
            {
                _shown = show;
                foreach (var s in _strips)
                {
                    if (show) s.Show();
                    else s.Hide();
                }
            }
            if (!show) return;
            foreach (var s in _strips)
                if (Math.Abs(s.Opacity - _op) > 0.004) s.Opacity = _op;
        }

        public bool Visible => _shown;

        /// <summary>Wyciąga paski na wierzch (np. nad film na pełnym ekranie).</summary>
        public void BringToTop()
        {
            if (!_shown) return;
            foreach (var s in _strips)
            {
                IntPtr h = new WindowInteropHelper(s).Handle;
                if (h != IntPtr.Zero) Native.ForceTopmost(h);
            }
        }

        public void Close()
        {
            foreach (var s in _strips)
            {
                try { s.Close(); } catch { }
            }
        }
    }
}
