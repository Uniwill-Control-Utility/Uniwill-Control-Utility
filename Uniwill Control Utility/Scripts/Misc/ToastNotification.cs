using Microsoft.Toolkit.Uwp.Notifications;
using System;
using System.Reflection;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Scripts.Misc
{
    internal static class ToastNotification
    {
        private const string AssemblyFileName = "Uniwill Control Utility.dll";
        private const string IconRelativePath = "Assets\\icon.png";

        public static void ShowToastNotification(string title = "", string body = "")
        {
            try
            {
                var assemblyPath = Assembly.GetEntryAssembly()!.Location;
                var basePath = assemblyPath.Replace(AssemblyFileName, string.Empty);
                var iconUri = basePath + IconRelativePath;

                new ToastContentBuilder()
                   .AddText(LocalizationService.Get(title))
                   .AddText(LocalizationService.Get(body))
                   .AddAppLogoOverride(new Uri(iconUri))
                   .Show();
            }
            catch (UriFormatException ex)
            {
                // Invalid icon URI constructed from assembly path
                DiagnosticLogger.LogError(ex, "Failed to show toast notification");
            }
            catch (Exception ex)
            {
                DiagnosticLogger.LogError(ex, "Failed to show toast notification");
            }
        }
    }
}
