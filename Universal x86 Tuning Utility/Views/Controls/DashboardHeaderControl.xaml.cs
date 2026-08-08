using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Universal_x86_Tuning_Utility.Views.Controls
{
    public partial class DashboardHeaderControl : UserControl
    {
        public DashboardHeaderControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            // Apply top-left rounded corner clip to the image grid so it matches the border
            ImageGrid.SizeChanged += OnImageGridSizeChanged;
            ApplyCornerClip();
        }

        private void OnImageGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyCornerClip();
        }

        private void ApplyCornerClip()
        {
            var w = ImageGrid.ActualWidth;
            var h = ImageGrid.ActualHeight;
            if (w <= 0 || h <= 0) return;

            const double radius = 8;

            // Build a path that rounds only the top-left corner.
            var figure = new PathFigure
            {
                StartPoint = new Point(0, radius),
                IsClosed = true,
                IsFilled = true,
            };
            // Top-left: arc from top edge to left edge
            figure.Segments.Add(new ArcSegment(
                new Point(radius, 0),
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: false,
                sweepDirection: SweepDirection.Clockwise,
                isStroked: false));
            // Right edge: straight down
            figure.Segments.Add(new LineSegment(new Point(w, 0), isStroked: false));
            // Bottom-right: sharp corner
            figure.Segments.Add(new LineSegment(new Point(w, h), isStroked: false));
            // Bottom-left: sharp corner
            figure.Segments.Add(new LineSegment(new Point(0, h), isStroked: false));

            var path = new PathGeometry { Figures = new PathFigureCollection { figure } };
            ImageGrid.Clip = path;
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
