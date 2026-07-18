using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Wpf.Ui;
using Wpf.Ui.Abstractions.Controls;

namespace Universal_x86_Tuning_Utility.ViewModels
{
    public partial class DashboardViewModel :
        ObservableObject,
        INavigationAware
    {
        private readonly INavigationService _navigationService;

        public DashboardViewModel(
            INavigationService navigationService)
        {
            _navigationService = navigationService
                ?? throw new ArgumentNullException(
                    nameof(navigationService));
        }

        [RelayCommand]
        private void Navigate(string? destination)
        {
            if (string.IsNullOrWhiteSpace(destination))
            {
                Debug.WriteLine(
                    "Dashboard navigation was requested without a destination.");

                return;
            }

            switch (destination)
            {
                case "premade":
                case "custom":
                case "adaptive":
                case "games":
                case "auto":
                case "info":
                    NavigateWithinApplication(destination);
                    break;

                case "help":
                    OpenUrl("https://github.com/JamesCJ60/Universal-x86-Tuning-Utility/wiki");
                    break;

                default:
                    Debug.WriteLine(
                        $"Unknown dashboard destination: {destination}");

                    break;
            }
        }

        public Task OnNavigatedToAsync()
        {
            Debug.WriteLine(
                $"INFO | {nameof(DashboardViewModel)} navigated to.");

            return Task.CompletedTask;
        }

        public Task OnNavigatedFromAsync()
        {
            Debug.WriteLine(
                $"INFO | {nameof(DashboardViewModel)} navigated from.");

            return Task.CompletedTask;
        }

        private void NavigateWithinApplication(
            string targetPageTag)
        {
            bool succeeded =
                _navigationService.Navigate(targetPageTag);

            if (!succeeded)
            {
                Debug.WriteLine(
                    $"Dashboard navigation failed for tag " +
                    $"'{targetPageTag}'. Ensure the matching " +
                    $"NavigationViewItem has that TargetPageTag.");
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Could not open '{url}': {exception}");
            }
        }
    }
}
