using System.Windows;
using Uniwill_Control_Utility.Models;
using Wpf.Ui.Controls;

namespace Uniwill_Control_Utility.Views.Windows
{
    /// <summary>
    /// Dialog window for editing a fan curve profile.
    /// </summary>
    public partial class FlydigiCurveEditorWindow : FluentWindow
    {
        public FlydigiFanCurveProfile? EditedProfile { get; private set; }

        public FlydigiCurveEditorWindow(FlydigiFanCurveProfile profile)
        {
            InitializeComponent();
            _editor.SetCurve(profile);
        }

        private void btnSave_Click(object sender, RoutedEventArgs e)
        {
            EditedProfile = _editor.GetCurve();
            DialogResult = true;
            Close();
        }
    }
}
