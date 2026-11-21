using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace IslandPlayer
{
    /// <summary>Panel korektora w stylu Isabelle: włącznik, gotowe ustawienia i 10 suwaków.</summary>
    public static class EqualizerPanel
    {
        private const double SliderH = 132, Thumb = 14;

        /// <param name="owner">okno dla okienka z nazwą (panel siedzi w wyskakującym menu)</param>
        /// <param name="reopen">otwiera panel ponownie (menu zamyka się, gdy pokazujemy okienko z nazwą)</param>
        public static FrameworkElement Build(Brush accent, Window owner = null, Action reopen = null)
        {
            var root = new StackPanel { Width = 440, Margin = new Thickness(10, 8, 10, 10) };

            // ---- nagłówek z włącznikiem ----
            var head = new Grid { Margin = new Thickness(2, 0, 0, 2) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel();
            titles.Children.Add(new TextBlock { Text = L.T("Korektor dźwięku", "Equalizer"), FontSize = 16, FontWeight = FontWeights.Bold, Foreground = Brushes.White });
            titles.Children.Add(new TextBlock { Text = L.T("Działa w Mojej muzyce", "Works in My music"), FontSize = 12, Foreground = Hex("#80FFFFFF"), Margin = new Thickness(0, 2, 0, 0) });
            head.Children.Add(titles);
            var toggle = Toggle(accent, Equalizer.Enabled, on => Equalizer.SetEnabled(on));
            Grid.SetColumn(toggle.Item1, 1);
            head.Children.Add(toggle.Item1);
            root.Children.Add(head);

            // ---- gotowe ustawienia ----
            var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 6) };
            root.Children.Add(chips);
            // pod gotowcami: „Własne ustawienie · Zapisz jako…” albo przy własnym gotowcu „Zmień nazwę · Usuń”
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 4) };
            root.Children.Add(actions);

            string AskName(string title, string initial, string ok)
            {
                string n = InputDialog.Ask(owner ?? Application.Current?.MainWindow, title, initial, ok);
                reopen?.Invoke();
                return n;
            }

            // ---- suwaki ----
            var bands = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            var sliders = new List<Action>();
            for (int i = 0; i < Equalizer.Bands.Length; i++)
            {
                bands.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var col = Band(i, accent, out var refresh);
                Grid.SetColumn(col, i);
                bands.Children.Add(col);
                sliders.Add(refresh);
            }
            var bandsHost = new Grid();
            // linia zera
            bandsHost.Children.Add(new Border
            {
                Height = 1, Background = Hex("#1FFFFFFF"), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6, 6 + 18 + SliderH / 2, 6, 0), IsHitTestVisible = false,
            });
            bandsHost.Children.Add(bands);
            root.Children.Add(bandsHost);

            void Refresh()
            {
                chips.Children.Clear();
                string key = Equalizer.PresetKey;
                foreach (var p in Equalizer.Presets)
                {
                    string k = p.key;
                    chips.Children.Add(Chip(L.T(p.pl, p.en), k == key, accent, () => Equalizer.SetPreset(k)));
                }
                // własne gotowce - z gwiazdką, żeby odróżniały się od wbudowanych
                foreach (var u in Equalizer.UserPresets)
                {
                    string k = Equalizer.UserPrefix + u.Name;
                    chips.Children.Add(Chip("★ " + u.Name, string.Equals(k, key, StringComparison.CurrentCultureIgnoreCase), accent, () => Equalizer.SetPreset(k)));
                }

                actions.Children.Clear();
                if (key == "custom")
                {
                    actions.Children.Add(new TextBlock { Text = L.T("Własne ustawienie", "Custom"), FontSize = 11.5, Foreground = Hex("#80FFFFFF"), VerticalAlignment = VerticalAlignment.Center });
                    actions.Children.Add(Dot());
                    actions.Children.Add(Link(L.T("Zapisz jako…", "Save as…"), accent, () =>
                    {
                        string n = AskName(L.T("Nazwa ustawienia korektora", "Equalizer preset name"), "", L.T("Zapisz", "Save"));
                        if (!string.IsNullOrWhiteSpace(n)) Equalizer.SaveUserPreset(n);
                    }));
                }
                else if (key.StartsWith(Equalizer.UserPrefix))
                {
                    string name = key.Substring(Equalizer.UserPrefix.Length);
                    actions.Children.Add(Link(L.T("Zmień nazwę", "Rename"), Hex("#B3FFFFFF"), () =>
                    {
                        string n = AskName(L.T("Zmień nazwę ustawienia", "Rename preset"), name, L.T("Zapisz", "Save"));
                        if (!string.IsNullOrWhiteSpace(n)) Equalizer.RenameUserPreset(name, n);
                    }));
                    actions.Children.Add(Dot());
                    actions.Children.Add(Link(L.T("Usuń", "Delete"), Hex("#FF6961"), () => Equalizer.DeleteUserPreset(name)));
                }
                actions.Visibility = actions.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                foreach (var r in sliders) r();
                bool on = Equalizer.Enabled;
                bandsHost.Opacity = chips.Opacity = actions.Opacity = on ? 1 : 0.4;
                bandsHost.IsHitTestVisible = chips.IsHitTestVisible = actions.IsHitTestVisible = on;
                toggle.Item2(on);
            }
            Action changed = () => root.Dispatcher.BeginInvoke(new Action(Refresh));
            Equalizer.Changed += changed;
            root.Unloaded += (s, e) => Equalizer.Changed -= changed;
            Refresh();
            return root;
        }

        /// <summary>Jedno pasmo: wartość w dB, pionowy suwak, częstotliwość.</summary>
        private static FrameworkElement Band(int band, Brush accent, out Action refresh)
        {
            var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var val = new TextBlock { FontSize = 10.5, Foreground = Hex("#99FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, Height = 18 };
            col.Children.Add(val);

            var canvas = new Canvas { Width = 30, Height = SliderH, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var track = new Border { Width = 4, Height = SliderH - Thumb, CornerRadius = new CornerRadius(2), Background = Hex("#26FFFFFF") };
            Canvas.SetLeft(track, 13);
            Canvas.SetTop(track, Thumb / 2);
            var fill = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = accent };
            Canvas.SetLeft(fill, 13);
            var knob = new Ellipse
            {
                Width = Thumb, Height = Thumb, Fill = Brushes.White,
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.5, Color = Colors.Black },
            };
            Canvas.SetLeft(knob, 8);
            canvas.Children.Add(track);
            canvas.Children.Add(fill);
            canvas.Children.Add(knob);
            col.Children.Add(canvas);
            col.Children.Add(new TextBlock { Text = Equalizer.BandLabels[band], FontSize = 11, Foreground = Hex("#B3FFFFFF"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });

            double Y(double db) => (1 - (db + Equalizer.MaxDb) / (2 * Equalizer.MaxDb)) * (SliderH - Thumb) + Thumb / 2;
            void Paint(double db)
            {
                double y = Y(db), mid = Y(0);
                Canvas.SetTop(knob, y - Thumb / 2);
                Canvas.SetTop(fill, Math.Min(y, mid));
                fill.Height = Math.Abs(y - mid);
                val.Text = (db > 0 ? "+" : "") + db.ToString(Math.Abs(db % 1) > 0.01 ? "0.0" : "0");
            }
            refresh = () => Paint(Equalizer.Gains[band]);

            double FromY(double y) => Math.Clamp((1 - (y - Thumb / 2) / (SliderH - Thumb)) * 2 * Equalizer.MaxDb - Equalizer.MaxDb, -Equalizer.MaxDb, Equalizer.MaxDb);
            canvas.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                if (e.ClickCount == 2) { Equalizer.SetBand(band, 0); Equalizer.SaveCustom(); return; }     // dwuklik = zero
                canvas.CaptureMouse();
                Equalizer.SetBand(band, FromY(e.GetPosition(canvas).Y));
            };
            canvas.MouseMove += (s, e) => { if (canvas.IsMouseCaptured) Equalizer.SetBand(band, FromY(e.GetPosition(canvas).Y)); };
            canvas.MouseLeftButtonUp += (s, e) =>
            {
                if (!canvas.IsMouseCaptured) return;
                canvas.ReleaseMouseCapture();
                Equalizer.SaveCustom();
            };
            canvas.MouseWheel += (s, e) =>
            {
                e.Handled = true;
                Equalizer.SetBand(band, Equalizer.Gains[band] + (e.Delta > 0 ? 0.5 : -0.5));
                Equalizer.SaveCustom();
            };
            return col;
        }

        private static TextBlock Dot() =>
            new TextBlock { Text = "·", FontSize = 12, Foreground = Hex("#59FFFFFF"), Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };

        /// <summary>Tekstowy przycisk (bez tła), podkreśla się po najechaniu.</summary>
        private static TextBlock Link(string text, Brush fg, Action click)
        {
            var t = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = fg, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            t.MouseEnter += (s, e) => t.TextDecorations = TextDecorations.Underline;
            t.MouseLeave += (s, e) => t.TextDecorations = null;
            t.MouseLeftButtonDown += (s, e) => e.Handled = true;
            t.MouseLeftButtonUp += (s, e) => click();
            return t;
        }

        private static Border Chip(string text, bool selected, Brush accent, Action click)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(14), Padding = new Thickness(12, 5, 12, 6), Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
                Background = selected ? accent : Hex("#1AFFFFFF"),
                Child = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = selected ? Brushes.Black : Brushes.White },
            };
            b.MouseEnter += (s, e) => b.Opacity = 0.85;
            b.MouseLeave += (s, e) => b.Opacity = 1;
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => click();
            return b;
        }

        /// <summary>Przełącznik jak w iOS; zwraca element i funkcję do odświeżenia stanu.</summary>
        private static (FrameworkElement, Action<bool>) Toggle(Brush accent, bool initial, Action<bool> changed)
        {
            bool state = initial;
            var knob = new Ellipse { Width = 18, Height = 18, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
            var b = new Border { Width = 42, Height = 24, CornerRadius = new CornerRadius(12), Cursor = Cursors.Hand, Child = knob, VerticalAlignment = VerticalAlignment.Center };
            void Paint(bool on)
            {
                state = on;
                b.Background = on ? accent : Hex("#33FFFFFF");
                knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            }
            Paint(initial);
            b.MouseLeftButtonDown += (s, e) => e.Handled = true;
            b.MouseLeftButtonUp += (s, e) => { Paint(!state); changed(state); };
            return (b, Paint);
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }
    }
}
