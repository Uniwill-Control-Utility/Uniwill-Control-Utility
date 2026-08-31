using Wpf.Ui.Abstractions.Controls;

namespace Uniwill_Control_Utility.Views.Pages
{
    public partial class DataPage : INavigableView<ViewModels.DataViewModel>
    {
        public ViewModels.DataViewModel ViewModel
        {
            get;
        }

        public DataPage(ViewModels.DataViewModel viewModel)
        {
            ViewModel = viewModel;

            InitializeComponent();
        }
    }
}
