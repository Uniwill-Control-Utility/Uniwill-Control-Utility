using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Views.Controls;

namespace Uniwill_Control_Utility.Tests.UI;

[Collection("WPF")]
public sealed class EcFanCurveEditorAlignmentTests
{
    [Fact]
    public void CurvePointsAreCentredOnTheRenderedSliderThumbs()
    {
        RunOnSta(() =>
        {
            EnsureApplicationResources();

            var editor = new EcFanCurveEditor();
            var window = new Window
            {
                Width = 900,
                Height = 260,
                Left = -10_000,
                Top = -10_000,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Content = editor
            };
            var curve = new EcFanCurve { Name = "Alignment test" };
            curve.Duties.Clear();
            curve.Duties.AddRange([0, 30, 45, 60, 75, 90, 100, 100, 100, 100, 100]);

            editor.SetCurve(curve);
            window.Show();
            editor.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            var canvas = Assert.IsType<Canvas>(editor.FindName("_canvas"));
            var slidersGrid = Assert.IsType<Grid>(editor.FindName("_slidersGrid"));
            Slider[] sliders = slidersGrid.Children.Cast<Slider>().ToArray();
            AssertCurveMatchesThumbCentres(canvas, sliders);

            Slider changedSlider = sliders[5];
            Assert.True(changedSlider.Focus());
            changedSlider.Value = 55;
            editor.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

            AssertCurveMatchesThumbCentres(canvas, sliders);

            // The EC ignores duties under 25 %: a stored curve is raised on load,
            // while zone 0 keeps its 0 % ("fan off") marker.
            var floored = new EcFanCurve { Name = "Floor test" };
            floored.Duties.Clear();
            floored.Duties.AddRange([0, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70]);
            editor.SetCurve(floored);
            editor.UpdateLayout();
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

            Assert.Equal(0, (int)sliders[0].Value);
            Assert.All(sliders.Skip(1), slider => Assert.InRange(slider.Value, 25.0, 100.0));
            AssertCurveMatchesThumbCentres(canvas, sliders);

            window.Close();
        });
    }

    private static void AssertCurveMatchesThumbCentres(Canvas canvas, Slider[] sliders)
    {
        var curveLine = Assert.Single(canvas.Children.OfType<System.Windows.Shapes.Polyline>());
        Point[] curvePoints = [.. curveLine.Points];

        Assert.Equal(sliders.Length, curvePoints.Length);

        for (int index = 0; index < sliders.Length; index++)
        {
            Slider slider = sliders[index];
            slider.ApplyTemplate();
            var track = Assert.IsType<Track>(slider.Template.FindName("PART_Track", slider));
            Point thumbCentre = track.Thumb.TranslatePoint(
                new Point(track.Thumb.ActualWidth / 2, track.Thumb.ActualHeight / 2),
                canvas);

            Assert.True(Math.Abs(curvePoints[index].X - thumbCentre.X) <= 0.5,
                $"point {index} x={curvePoints[index].X:F2} thumbX={thumbCentre.X:F2} duty={slider.Value}");
            Assert.True(Math.Abs(curvePoints[index].Y - thumbCentre.Y) <= 0.5,
                $"point {index} y={curvePoints[index].Y:F2} thumbY={thumbCentre.Y:F2} duty={slider.Value} min={slider.Minimum}");
        }
    }

    private static void EnsureApplicationResources()
    {
        var application = Application.Current ?? new Application();
        application.Resources["TextFillColorDisabledBrush"] = Brushes.Gray;
        application.Resources["TextFillColorSecondaryBrush"] = Brushes.LightGray;
        application.Resources["ControlFillColorDefaultBrush"] = Brushes.DodgerBlue;
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Application.Current?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
