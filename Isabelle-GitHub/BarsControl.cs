using System;
using System.Windows;
using System.Windows.Media;

namespace IslandPlayer
{
    /// <summary>Cztery animowane słupki (jak w Apple Music), reagujące na głośność muzyki.</summary>
    public sealed class BarsControl : FrameworkElement
    {
        private readonly double[] _amp = { 0.28, 0.28, 0.28, 0.28 };
        private Brush _fill = Brushes.White;

        public Brush Fill
        {
            get => _fill;
            set
            {
                _fill = value;
                InvalidateVisual();
            }
        }

        public BarsControl()
        {
            IsHitTestVisible = false;
        }

        /// <param name="t">czas w sekundach</param>
        /// <param name="playing">czy muzyka gra</param>
        /// <param name="level">wygładzony poziom (peakS)</param>
        /// <param name="levelMax">wolno opadające maksimum (peakMax)</param>
        public void Update(double t, bool playing, double level, double levelMax)
        {
            bool live = levelMax > 0.02;
            for (int i = 0; i < 4; i++)
            {
                double a;
                if (playing)
                {
                    double b = 0.3 + 0.7 * Math.Abs(Math.Sin(t * (4.2 + i * 1.3) + i * 1.1));
                    if (live)
                    {
                        double gain = 0.45 + 0.9 * Math.Min(1, level / Math.Max(0.05, levelMax * 0.8));
                        b *= gain;
                    }
                    a = Math.Clamp(b, 0.2, 1);
                }
                else
                {
                    a = 0.28;
                }
                _amp[i] += (a - _amp[i]) * 0.5;
            }
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0) return;
            double gap = w * 0.12;
            double bw = (w - gap * 3) / 4;
            for (int i = 0; i < 4; i++)
            {
                double bh = Math.Max(bw, h * _amp[i]);
                double x = i * (bw + gap);
                double y = (h - bh) / 2;
                dc.DrawRoundedRectangle(_fill, null, new Rect(x, y, bw, bh), bw / 2, bw / 2);
            }
        }
    }
}
