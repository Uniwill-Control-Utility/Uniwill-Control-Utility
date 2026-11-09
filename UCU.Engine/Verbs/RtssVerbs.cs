using Serilog;
using UCU.Contracts;
using UCU.Core.Rtss;

namespace UCU.Engine
{
    /// <summary>
    /// RTSS verbs. RTSS is the game overlay's frame source, and it must live
    /// in the USER's session: the app cannot start it without a consent dialog
    /// on machines where RTSS carries the "Run as administrator" flag
    /// (field-reported: double-clicking it prompts UAC), and the
    /// scheduled-task route puts a GUI process in session 0, where it is
    /// invisible and injects nothing (field-proven). The engine is the only
    /// component that can place it in the active session with the privileges
    /// its flags demand, silently: LocalSystem reads the console user's linked
    /// (full) token and starts the image as that token.
    ///
    /// The verb takes NO path. The engine locates RTSS from its own witnesses
    /// and launches only an image that is named RTSS.exe AND sits under a
    /// Program Files root (IsTrustedLaunchImage) - the same chokepoint rule the
    /// raw-register write gate enforces, and the reason the HKCU witness alone
    /// is not enough: a user-writable image launched with the console user's
    /// linked token would be an unelevated process elevating itself.
    /// </summary>
    internal static class RtssVerbs
    {
        public static void Register(Action<string, VerbHandler> add, Func<string, bool>? launchOverride)
        {
            add(EngineVerbs.Rtss.Start, (p, ct) => Task.FromResult<object?>(Start(launchOverride)));
        }

        private static bool Start(Func<string, bool>? launchOverride)
        {
            string? exe = RtssLocation.Locate();

            if (exe == null)
            {
                Log.Warning("[Rtss] start: no RTSS image located - nothing to launch");
                return false;
            }

            if (!RtssLocation.IsTrustedLaunchImage(exe))
            {
                Log.Warning("[Rtss] start: located image is not a trusted {Exe} (outside Program Files) - refused",
                    RtssLocation.ExeName);
                return false;
            }

            // elevate: the compat-flag shape (RTSS flagged "run as
            // administrator") fails an elevated-token-required launch against
            // the filtered token, so the linked token is the route that works.
            // The override is the hermetic test seam (no real process in a test).
            Func<string, bool> launch = launchOverride
                ?? (image => AppSessionLauncher.Launch(image, string.Empty, elevate: true, logger: null));

            bool started = launch(exe);

            Log.Information("[Rtss] start: {Exe} -> {Result}", exe, started ? "started in the active session" : "launch failed");
            return started;
        }
    }
}
