using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    /// <summary>
    /// Feature area a card belongs to; drives which navigation-view glyph the card
    /// shows (Presets page item icon, Display page item icon, etc.).
    /// </summary>
    internal enum ToastCategory
    {
        Presets,
        Display,
        HydroUi,
        Flydigi,
        Settings,
    }

    /// <summary>
    /// In-process OSD banner replacing Windows toast notifications.
    ///
    /// Windows kept fabricating a new notification identity (and therefore a new
    /// Settings > Notifications category) for our unpackaged app on every rebuild —
    /// identity resolution is out of our control — and bare WinRT toasts throw
    /// 0x80070490 in this process entirely. An activation-forbidden topmost borderless
    /// window sidesteps the shell completely: no AUMID registration, no registry keys,
    /// no Settings entries, and it never steals focus from the foreground app.
    ///
    /// Only one card is ever on screen, pinned to a spot near the top-left of the
    /// primary work area; further messages wait in a FIFO queue and render in that
    /// exact same spot once the current card has fully faded away. Cards auto-dismiss
    /// after a few seconds with a fade, and can be dismissed early with a click.
    /// </summary>
    internal static class ToastOverlay
    {
        private const int VisibleMs = 4000;
        private const int MaxQueued = 8; // flood guard: oldest queued card is dropped
        // Small and unobtrusive but eye-catching: the card hugs its text exactly
        // (SizeToContent), capped so long messages wrap instead of spanning the screen.
        private const double TextMaxWidth = 340;
        private const double WindowMaxWidth = 460;
        // Offset of the card from the work-area's top-left corner.
        private const double EdgeOffset = 200;

        private static Window? _current;
        private static bool _dismissing;
        private static readonly Queue<(string Title, string Body, string? Accent, ToastCategory Category)> _queue =
            new Queue<(string, string, string?, ToastCategory)>();

        /// <summary>
        /// Shows an OSD card (or queues it if one is already up). Safe from any thread:
        /// window work is marshalled onto the UI dispatcher (startup toasts are
        /// dispatched from parallel startup phases).
        /// </summary>
        public static void Show(string title, string body, string? accent = null, ToastCategory category = ToastCategory.Presets)
        {
            // Settings > Application behavior > "Disable notification pop-up".
            if (Properties.Settings.Default.DisableToastPopups)
                return;

            Application.Current?.Dispatcher.BeginInvoke(
                DispatcherPriority.Normal, new Action(() =>
                {
                    if (_current is not null)
                    {
                        if (_queue.Count >= MaxQueued)
                            _queue.Dequeue();
                        _queue.Enqueue((title, body, accent, category));
                    }
                    else
                    {
                        ShowCore(title, body, accent, category);
                    }
                }));
        }

        private static void ShowCore(string title, string body, string? accent, ToastCategory category)
        {
            var work = SystemParameters.WorkArea;

            // Border carries the window-accent hairline; the profile-name run is painted
            // with the system hotlight color in bold — the OS "highlighted text" hue,
            // chosen to stay readable wherever it lands.
            var accentColor = SystemParameters.WindowGlassColor;
            if (accentColor == Colors.Black || accentColor == Colors.White)
                accentColor = Color.FromRgb(0x00, 0x78, 0xD7); // default Windows blue
            var accentBrush = new SolidColorBrush(accentColor);
            accentBrush.Freeze();

            // Follow the app's effective light/dark setting (same source of truth the
            // windows themselves use) instead of a fixed dark pill.
            bool light = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme()
                == Wpf.Ui.Appearance.ApplicationTheme.Light;

            // The preset-name run approximates WinUI's AccentTextFillColorTertiary —
            // a pale light-blue tint of the accent under dark theme, a deep darkened
            // accent under light theme. WPF apps have no access to that XAML resource,
            // so it is blended from the user's own accent toward the reference values
            // (stock accent #0078D7 -> #99EBFA dark, -> #003155 light).
            var tertiaryColor = light
                ? MixToward(accentColor, Colors.Black, 0.58)
                : MixToward(accentColor, Color.FromRgb(0x99, 0xEB, 0xFA), 0.85);
            var nameBrush = new SolidColorBrush(tertiaryColor);
            nameBrush.Freeze();
            var cardBrush = new SolidColorBrush(light
                ? Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xE6, 0x1B, 0x1B, 0x1B));
            cardBrush.Freeze();
            var titleBrush = new SolidColorBrush(light ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White);
            titleBrush.Freeze();
            var bodyBrush = new SolidColorBrush(light ? Color.FromRgb(0x55, 0x55, 0x55) : Color.FromRgb(0xC8, 0xC8, 0xC8));
            bodyBrush.Freeze();

            var titleText = new TextBlock
            {
                FontSize = 22,
                FontWeight = FontWeights.SemiBold,
                Foreground = titleBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = TextMaxWidth,
            };
            // Tint just the profile-name run of the title (callers build composite runs
            // via ToastNotification.AccentFor so they survive localized word order);
            // an accent that doesn't appear verbatim degrades to a plain title.
            int at = string.IsNullOrEmpty(accent) ? -1 : title.IndexOf(accent!, StringComparison.Ordinal);
            if (at >= 0)
            {
                titleText.Inlines.Add(new Run(title[..at]));
                titleText.Inlines.Add(new Run(title.Substring(at, accent!.Length))
                {
                    Foreground = nameBrush,
                    FontWeight = FontWeights.Bold,
                });
                titleText.Inlines.Add(new Run(title[(at + accent.Length)..]));
            }
            else
            {
                titleText.Text = title;
            }
            var bodyText = new TextBlock
            {
                Text = body,
                FontSize = 18,
                Foreground = bodyBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = TextMaxWidth,
                Margin = new Thickness(0, 2, 0, 0),
                Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible,
            };

            // Icon mirrors the feature's NavigationView item icon exactly: same
            // SymbolRegular glyph (FontIcon) as the nav menu, or the same custom
            // IconElement (Flydigi's Lucide-style drawing) where the nav uses one.
            // Same FontFamily the nav items set explicitly \u2014 FontIcon's default can
            // resolve to a different symbol font, and a mismatch drops the glyph box.
            Wpf.Ui.Controls.IconElement iconElement = category switch
            {
                ToastCategory.Display => new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE7F8" },
                ToastCategory.HydroUi => new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uEB42" },
                ToastCategory.Settings => new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uE713" },
                ToastCategory.Flydigi => new Views.Controls.FlydigiIconElement(26),
                _ => new Wpf.Ui.Controls.FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "\uEF58" }, // Presets
            };
            iconElement.Width = 32;
            iconElement.Height = 32;
            // FontSize lives on FontIcon, not the IconElement base.
            if (iconElement is Wpf.Ui.Controls.FontIcon fontIcon)
                fontIcon.FontSize = 26;
            iconElement.Foreground = titleBrush;
            // IconElement derives from FrameworkElement (IconElement : FrameworkElement).
            var iconFe = (FrameworkElement)iconElement;
            iconFe.VerticalAlignment = VerticalAlignment.Center;
            iconFe.Margin = new Thickness(0, 0, 12, 0);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(titleText);
            textStack.Children.Add(bodyText);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(iconFe, 0);
            Grid.SetColumn(textStack, 1);
            grid.Children.Add(iconFe);
            grid.Children.Add(textStack);

            var border = new Border
            {
                Background = cardBrush,
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 15, 18, 15),
                Child = grid,
            };
            TextOptions.SetTextFormattingMode(border, TextFormattingMode.Display);

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowActivated = false,   // never take focus...
                ShowInTaskbar = false,
                Topmost = true,
                Focusable = false,
                // wpfui's implicit Window style carries MinWidth/MinHeight (460x320)
                // which would otherwise silently dictate the card size; local values
                // beat style setters.
                MinWidth = 0,
                MinHeight = 0,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.Manual,
                MaxWidth = WindowMaxWidth,
                Content = border,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
            };
            // ...and belt-and-braces: WS_EX_NOACTIVATE keeps even click-activation out.
            window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            };

            // Single fixed presentation slot — queued cards render in the exact same spot.
            window.Left = work.Left + EdgeOffset;
            window.Top = work.Top + EdgeOffset;

            // Click anywhere on the card to dismiss it early.
            border.MouseLeftButtonDown += (_, _) => Dismiss(window);

            window.Opacity = 0;
            _current = window;
            _dismissing = false;
            window.Show();
            window.UpdateLayout();
            window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(VisibleMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Dismiss(window);
            };
            timer.Start();
        }

        private static void Dismiss(Window window)
        {
            if (!ReferenceEquals(_current, window) || _dismissing)
                return; // not the live card, or already fading out

            _dismissing = true;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
            fade.Completed += (_, _) =>
            {
                // The slot frees only once the card is fully gone, so the queued next
                // card (if any) renders after — never over — the outgoing one.
                window.Close();
                if (ReferenceEquals(_current, window))
                {
                    _current = null;
                    _dismissing = false;
                }
                if (_current is null && _queue.Count > 0)
                {
                    var (title, body, accent, category) = _queue.Dequeue();
                    ShowCore(title, body, accent, category);
                }
            };
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        private static Color MixToward(Color from, Color toward, double amount) => Color.FromRgb(
            (byte)(from.R + (toward.R - from.R) * amount),
            (byte)(from.G + (toward.G - from.G) * amount),
            (byte)(from.B + (toward.B - from.B) * amount));

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
