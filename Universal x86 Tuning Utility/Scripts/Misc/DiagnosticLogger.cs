using Serilog;
using Serilog.Core;
using Serilog.Events;
using System;
using System.Runtime.CompilerServices;

namespace Universal_x86_Tuning_Utility.Scripts.Misc
{
    internal static class DiagnosticLogger
    {
        // Event fires for every log message regardless of level gating — allows UI to capture logs.
        public static event Action<string>? OnMessage;

        private static void Fire(string line)
        {
            OnMessage?.Invoke(line);
        }

        public static readonly LoggingLevelSwitch LevelSwitch = new(LogEventLevel.Warning);

        public static void ApplySettingsLevel()
        {
            LevelSwitch.MinimumLevel = Properties.Settings.Default.DiagnosticLogLevel switch
            {
                0 => LogEventLevel.Fatal + 1,
                1 => LogEventLevel.Warning,
                2 => LogEventLevel.Information,
                3 => LogEventLevel.Debug,
                _ => LogEventLevel.Warning
            };
        }

        public static void LogError(
            Exception ex,
            string context,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
        {
            var source = System.IO.Path.GetFileNameWithoutExtension(file);
            Fire($"[{source}.{caller}] ERROR: {context}");
            Log.Warning(ex, "[{Source}.{Caller}] {Context}", source, caller, context);
        }

        public static void LogInfo(
            string message,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
        {
            var source = System.IO.Path.GetFileNameWithoutExtension(file);
            Fire($"[{source}.{caller}] {message}");
            Log.Information("[{Source}.{Caller}] {Message}", source, caller, message);
        }

        public static void LogDebug(
            string message,
            [CallerMemberName] string caller = "",
            [CallerFilePath] string file = "")
        {
            var source = System.IO.Path.GetFileNameWithoutExtension(file);
            Fire($"[{source}.{caller}] DEBUG: {message}");
            Log.Debug("[{Source}.{Caller}] {Message}", source, caller, message);
        }
    }
}
