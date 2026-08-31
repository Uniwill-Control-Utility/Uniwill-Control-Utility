using System.Threading.Tasks;

namespace Uniwill_Control_Utility.ViewModels
{
    public partial class DataViewModel
    {
        public Task OnNavigatedToAsync()
        {
            OnNavigatedTo();
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            OnNavigatedFrom();
            return Task.CompletedTask;
        }
    }

    public partial class SettingsViewModel
    {
        public Task OnNavigatedToAsync()
        {
            OnNavigatedTo();
            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            OnNavigatedFrom();
            return Task.CompletedTask;
        }
    }
}