using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Uniwill_Control_Utility.Scripts;

namespace Uniwill_Control_Utility.Views.Controls
{
    public partial class DashboardHeaderControl : UserControl
    {
        private string? _currentWallpaperPath;

        public DashboardHeaderControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            ImageGrid.SizeChanged += OnImageGridSizeChanged;
            ApplyCornerClip();
            ApplyFadeMask();

            // Subscribe to window activation to detect wallpaper changes
            var window = Window.GetWindow(this);
            if (window != null)
            {
                window.Activated += OnWindowActivated;
            }

            LoadWallpaper();
        }

        /// <summary>
        /// Re-checks the desktop wallpaper when the user switches back to the app.
        /// This catches wallpaper changes made in Settings without needing a timer or registry watcher.
        /// </summary>
        private void OnWindowActivated(object? sender, EventArgs e)
        {
            LoadWallpaper();
        }

        private void LoadWallpaper()
        {
            string? path = WallpaperHelper.GetWallpaperPath();
            if (string.IsNullOrEmpty(path) || path == _currentWallpaperPath)
                return;

            _currentWallpaperPath = path;

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                HeaderImage.Source = bitmap;
            }
            catch
            {
                // Fall back to no image — the background border will show through
            }
        }

        private void OnImageGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyCornerClip();
            ApplyFadeMask();
        }

        private void ApplyCornerClip()
        {
            var w = ImageGrid.ActualWidth;
            var h = ImageGrid.ActualHeight;
            if (w <= 0 || h <= 0) return;

            const double radius = 8;

            var figure = new PathFigure
            {
                StartPoint = new Point(0, radius),
                IsClosed = true,
                IsFilled = true,
            };
            figure.Segments.Add(new ArcSegment(
                new Point(radius, 0),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: false,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: false));
            figure.Segments.Add(new LineSegment(new Point(w, 0), isStroked: false));
            figure.Segments.Add(new LineSegment(new Point(w, h), isStroked: false));
            figure.Segments.Add(new LineSegment(new Point(0, h), isStroked: false));

            var path = new PathGeometry { Figures = new PathFigureCollection { figure } };
            ImageGrid.Clip = path;
        }

        /// <summary>
        /// Composes a single OpacityMask from the 4 edge fades by rendering each
        /// gradient to a bitmap and multiplying the alpha channels pixel by pixel.
        /// </summary>
        private void ApplyFadeMask()
        {
            var w = ImageGrid.ActualWidth;
            var h = ImageGrid.ActualHeight;
            if (w <= 0 || h <= 0) return;

            int pw = (int)Math.Ceiling(w);
            int ph = (int)Math.Ceiling(h);
            Rect rect = new Rect(0, 0, w, h);

            // Get the 4 gradient brushes from resources
            var brushes = new LinearGradientBrush[]
            {
                (LinearGradientBrush)Resources["HeaderFadeBottom"],
                (LinearGradientBrush)Resources["HeaderFadeLeft"],
                (LinearGradientBrush)Resources["HeaderFadeTop"],
                (LinearGradientBrush)Resources["HeaderFadeRight"],
            };

            // Render first gradient as the base
            var baseRt = RenderGradient(brushes[0], pw, ph, rect);
            var wb = new WriteableBitmap(baseRt);

            // Multiply remaining 3 gradients into the base
            for (int i = 1; i < brushes.Length; i++)
            {
                var maskRt = RenderGradient(brushes[i], pw, ph, rect);
                var maskWb = new WriteableBitmap(maskRt);

                wb.Lock();
                maskWb.Lock();

                int stride = wb.BackBufferStride;
                IntPtr ptr = wb.BackBuffer;
                int mStride = maskWb.BackBufferStride;
                IntPtr mPtr = maskWb.BackBuffer;

                unsafe
                {
                    for (int y = 0; y < ph; y++)
                    {
                        byte* row = (byte*)ptr + y * stride;
                        byte* mRow = (byte*)mPtr + y * mStride;
                        for (int x = 0; x < pw; x++)
                        {
                            int idx = x * 4;
                            byte a = row[idx + 3];
                            byte m = mRow[idx + 3];
                            row[idx + 3] = (byte)((a * m) >> 8); // multiply by 255
                        }
                    }
                }

                maskWb.Unlock();
                wb.AddDirtyRect(new Int32Rect(0, 0, pw, ph));
                wb.Unlock();
            }

            wb.Freeze();
            var maskBrush = new ImageBrush(wb)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            maskBrush.Freeze();
            ImageGrid.OpacityMask = maskBrush;
        }

        private static RenderTargetBitmap RenderGradient(LinearGradientBrush brush, int width, int height, Rect rect)
        {
            var rt = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(brush, null, rect);
            }
            rt.Render(visual);
            return rt;
        }

        // --- Public setters for the DashboardPage code-behind ---

        public void SetLaptopImage(BitmapImage bitmap)
            => LaptopImage.Source = bitmap;

        public void SetLaptopImageVisibility(Visibility visibility)
            => LaptopImage.Visibility = visibility;

        public string LaptopName
        {
            set => LaptopNameText.Text = value;
        }

        public string LaptopCPUName
        {
            set => LaptopCPUNameText.Text = value;
        }

        public string LaptopGPUName
        {
            set => LaptopGPUNameText.Text = value;
        }

        public string LaptopDisplaySpec
        {
            set => LaptopDisplaySpecText.Text = value;
        }

        public string LaptopRam
        {
            set => LaptopRamText.Text = value;
        }

        public string LaptopStorage
        {
            set => LaptopStorageText.Text = value;
        }
    }
}
