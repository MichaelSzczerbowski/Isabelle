using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>
    /// menu w stylu Isabelle (ciemne szkło, zaokrąglone, z cieniem) zamiast szarego menu Windows.
    /// Pozycje zwykłe zamykają menu, pozycje z zaznaczeniem (kółko / ptaszek) zostawiają je otwarte.
    /// </summary>
    public sealed class GlassMenu
    {
        public const string HeartIcon = "M12,21.35 L10.55,20.03 C5.4,15.36 2,12.28 2,8.5 C2,5.42 4.42,3 7.5,3 C9.24,3 10.91,3.81 12,5.09 C13.09,3.81 14.76,3 16.5,3 C19.58,3 22,5.42 22,8.5 C22,12.28 18.6,15.36 13.45,20.04 Z";
        public const string PlusIcon = "M12,4 L12,20 M4,12 L20,12";
        public const string PlayIcon = "M6,3 L20,12 L6,21 Z";
        public const string ShuffleIcon = "M3,7 H7 C11,7 13,17 17,17 H21 M18,14 L21,17 L18,20 M3,17 H7 C9,17 10.3,14.5 11.4,12.4 M13,9.6 C14.1,8.2 15.4,7 17,7 H21 M18,4 L21,7 L18,10";
        public const string ListIcon = "M4,6 H20 M4,12 H20 M4,18 H14";
        public const string EditIcon = "M4,20 L4,16 L15,5 L19,9 L8,20 Z M13,7 L17,11";
        public const string TrashIcon = "M5,7 H19 M9,7 V4 H15 V7 M7,7 L8,20 H16 L17,7";
        public const string MinusIcon = "M5,12 L19,12";

        private readonly Popup _popup = new Popup { AllowsTransparency = true, StaysOpen = true, PopupAnimation = PopupAnimation.None };
        private readonly StackPanel _items = new StackPanel();
        private readonly Brush _accent;

        public GlassMenu(Brush accent)
        {
            _accent = accent;
            var card = new Border
            {
                Background = Hex("#F51C1C22"), BorderBrush = Hex("#26FFFFFF"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16), Padding = new Thickness(6), MinWidth = 230, Margin = new Thickness(14),
                Effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Opacity = 0.55, Direction = 270, Color = Colors.Black },
                Child = _items,
            };
            System.Windows.Documents.TextElement.SetFontFamily(card, new FontFamily("Segoe UI Variable Display, Segoe UI"));
            _popup.Child = card;
            // wejście: lekkie powiększenie i pojawienie się
            card.RenderTransformOrigin = new Point(0.5, 0);
            card.RenderTransform = new ScaleTransform(0.96, 0.96);
            card.Opacity = 0;
            _popup.Opened += (s, e) =>
            {
                var d = TimeSpan.FromMilliseconds(140);
                card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, d));
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
                ((ScaleTransform)card.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, d) { EasingFunction = ease });
            };
        }

        public void Header(string text) =>
            _items.Children.Add(new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Hex("#80FFFFFF"), Margin = new Thickness(12, 6, 12, 6) });

        /// <summary>Dowolna zawartość (np. panel korektora).</summary>
        public void Custom(UIElement content) => _items.Children.Add(content);

        public void Separator() =>
            _items.Children.Add(new Border { Height = 1, Background = Hex("#1AFFFFFF"), Margin = new Thickness(8, 5, 8, 5) });

        /// <summary>Zwykła pozycja: wykonuje akcję i zamyka menu.</summary>
        public void Item(string text, string icon, Action click, bool danger = false)
        {
            var row = Row(text, icon, danger ? Hex("#FF6961") : Brushes.White, null);
            row.MouseLeftButtonUp += (s, e) => { _popup.IsOpen = false; click?.Invoke(); };
            _items.Children.Add(row);
        }

        /// <summary>Pozycja z zaznaczeniem po prawej (kółko / pełne kółko z ptaszkiem). Menu zostaje otwarte.</summary>
        public void Check(string text, string icon, bool isChecked, Action<bool> toggled)
        {
            var mark = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            bool state = isChecked;
            void Paint()
            {
                mark.Children.Clear();
                if (state)
                {
                    mark.Children.Add(new Ellipse { Fill = _accent });
                    mark.Children.Add(new Path
                    {
                        Data = Geometry.Parse("M5.5,10.2 L8.6,13.2 L14.5,7"), Stroke = Brushes.Black, StrokeThickness = 2, Width = 10, Height = 8,
                        Stretch = Stretch.Uniform, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    });
                }
                else mark.Children.Add(new Ellipse { Stroke = Hex("#59FFFFFF"), StrokeThickness = 1.6 });
            }
            Paint();
            var row = Row(text, icon, Brushes.White, mark);
            row.MouseLeftButtonUp += (s, e) =>
            {
                state = !state;
                Paint();
                // „pyk” zaznaczenia
                var sc = new ScaleTransform(1, 1);
                mark.RenderTransformOrigin = new Point(0.5, 0.5);
                mark.RenderTransform = sc;
                var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(state ? 1.25 : 0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100))));
                anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 }));
                sc.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                sc.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                toggled?.Invoke(state);
            };
            _items.Children.Add(row);
        }

        private Border Row(string text, string icon, Brush fg, FrameworkElement right)
        {
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var ic = new Path
            {
                Width = 15, Height = 15, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
                StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            if (icon != null)
            {
                ic.Data = Geometry.Parse(icon);
                // ikonki pełne (serce, play) - wypełnione, reszta - linie
                bool filled = icon == HeartIcon || icon == PlayIcon;
                if (filled) ic.Fill = fg == Brushes.White ? Hex("#CCFFFFFF") : fg;
                else ic.Stroke = fg == Brushes.White ? Hex("#CCFFFFFF") : fg;
            }
            g.Children.Add(ic);
            var tb = new TextBlock
            {
                Text = text, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = fg, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260,
            };
            Grid.SetColumn(tb, 1);
            g.Children.Add(tb);
            if (right != null)
            {
                right.Margin = new Thickness(16, 0, 0, 0);
                Grid.SetColumn(right, 2);
                g.Children.Add(right);
            }
            var row = new Border { Child = g, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.MouseEnter += (s, e) => row.Background = Hex("#14FFFFFF");
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            row.MouseLeftButtonDown += (s, e) => e.Handled = true;
            return row;
        }

        /// <summary>Pod elementem (wyrównane do prawej krawędzi) albo przy kursorze.</summary>
        public void Show(UIElement target, bool atMouse = false)
        {
            _popup.PlacementTarget = target;
            if (atMouse) _popup.Placement = PlacementMode.MousePoint;
            else
            {
                _popup.Placement = PlacementMode.Custom;
                _popup.CustomPopupPlacementCallback = (popupSize, targetSize, offset) => new[]
                {
                    new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 14, targetSize.Height - 8), PopupPrimaryAxis.Vertical),
                    new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 14, -popupSize.Height + 8), PopupPrimaryAxis.Vertical),
                };
            }
            _popup.IsOpen = true;
            WatchOutside(target);
        }

        /// <summary>
        /// Zamykanie po kliknięciu gdziekolwiek poza menu: w oknie, w innym programie (okno traci fokus),
        /// po przesunięciu okna albo klawiszem Esc. (Wbudowane zamykanie WPF bywa zawodne.)
        /// </summary>
        private void WatchOutside(UIElement target)
        {
            var win = Window.GetWindow(target);
            if (win == null) return;
            MouseButtonEventHandler down = (s, e) => _popup.IsOpen = false;
            EventHandler lost = (s, e) => _popup.IsOpen = false;
            KeyEventHandler key = (s, e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; e.Handled = true; } };
            win.PreviewMouseDown += down;
            win.Deactivated += lost;
            win.LocationChanged += lost;
            win.PreviewKeyDown += key;
            _popup.Closed += (s, e) =>
            {
                win.PreviewMouseDown -= down;
                win.Deactivated -= lost;
                win.LocationChanged -= lost;
                win.PreviewKeyDown -= key;
            };
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
