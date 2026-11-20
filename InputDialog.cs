using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace IslandPlayer
{
    /// <summary>małe okienko w stylu Isabelle do wpisania nazwy (np. playlisty).</summary>
    public sealed class InputDialog : Window
    {
        private readonly TextBox _box = new TextBox();
        public string Value => _box.Text.Trim();

        public static string Ask(Window owner, string title, string initial, string ok)
        {
            var d = new InputDialog(title, initial, ok) { Owner = owner };
            return d.ShowDialog() == true && d.Value.Length > 0 ? d.Value : null;
        }

        private InputDialog(string title, string initial, string ok)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            Foreground = Brushes.White;

            var card = new Border
            {
                Width = 380, CornerRadius = new CornerRadius(20), Background = Brush("#F21A1A20"), BorderBrush = Brush("#2EFFFFFF"),
                BorderThickness = new Thickness(1), Padding = new Thickness(22, 18, 22, 18), Margin = new Thickness(20),
                Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.5, Direction = 270 },
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });
            _box.Text = initial ?? "";
            _box.FontSize = 14;
            _box.Background = Brushes.Transparent;
            _box.Foreground = Brushes.White;
            _box.CaretBrush = Brushes.White;
            _box.BorderThickness = new Thickness(0);
            sp.Children.Add(new Border
            {
                Background = Brush("#14FFFFFF"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 8, 12, 8), Child = _box,
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(Button(L.T("Anuluj", "Cancel"), false, () => { DialogResult = false; }));
            buttons.Children.Add(Button(ok, true, () => { if (Value.Length > 0) DialogResult = true; }));
            sp.Children.Add(buttons);
            card.Child = sp;
            Content = card;

            Loaded += (s, e) => { _box.Focus(); _box.SelectAll(); };
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && Value.Length > 0) DialogResult = true;
                else if (e.Key == Key.Escape) DialogResult = false;
            };
            MouseLeftButtonDown += (s, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        }

        private Border Button(string text, bool primary, System.Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(16), Height = 32, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(8, 0, 0, 0),
                Background = primary ? Brushes.White : Brush("#1FFFFFFF"), Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = primary ? Brushes.Black : Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center, LineStackingStrategy = LineStackingStrategy.BlockLineHeight, LineHeight = 14,
                    Margin = new Thickness(0, 1, 0, 0),
                },
            };
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
    }
}
