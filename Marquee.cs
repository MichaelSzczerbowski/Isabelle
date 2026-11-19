using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandPlayer
{
    /// <summary>
    /// Przewijany napis (jak w radiu samochodowym): jeśli tekst się nie mieści, płynnie jedzie w lewo,
    /// zatrzymuje się na końcu i wraca. Krótki tekst stoi w miejscu.
    /// TextBlock musi leżeć w Canvas z ClipToBounds="True".
    /// </summary>
    public static class Marquee
    {
        private const double Speed = 30;   // piksele na sekundę
        private const double Fade = 14;    // szerokość wygaszenia brzegów

        public static void SetText(Canvas clip, TextBlock text, string value, bool center)
        {
            if (text.Text == value) return;
            text.Text = value;
            clip.Dispatcher.BeginInvoke(new Action(() => Update(clip, text, center)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        public static void Update(Canvas clip, TextBlock text, bool center)
        {
            if (!(text.RenderTransform is TranslateTransform shift))
            {
                shift = new TranslateTransform();
                text.RenderTransform = shift;
            }
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;

            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double tw = text.DesiredSize.Width, cw = clip.ActualWidth;
            Canvas.SetTop(text, Math.Max(0, (clip.ActualHeight - text.DesiredSize.Height) / 2));
            if (cw <= 0) return;

            if (tw <= cw + 0.5)
            {
                Canvas.SetLeft(text, center ? (cw - tw) / 2 : 0);
                clip.OpacityMask = null;
                return;
            }

            Canvas.SetLeft(text, 0);
            // wygaszone brzegi - maska liczona od szerokości okienka (Absolute), nie od długości tekstu
            var mask = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(cw, 0) };
            double f = Math.Min(0.2, Fade / cw);
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, f));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - f));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            mask.Freeze();
            clip.OpacityMask = mask;

            double dist = tw - cw + Fade;
            double move = dist / Speed;
            const double hold = 2.0, holdEnd = 1.4;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + move)), ease));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + move + holdEnd))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + move * 2 + holdEnd)), ease));
            Timeline.SetDesiredFrameRate(a, 30); // wystarczy do płynnego tekstu, mniej pracy dla procesora
            shift.BeginAnimation(TranslateTransform.XProperty, a);
        }
    }
}
