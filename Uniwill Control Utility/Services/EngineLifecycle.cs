using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Uniwill_Control_Utility.Scripts.Misc;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// What <see cref="EngineLifecycle.EnsureAsync"/> did, so the caller can decide
    /// whether hardware work deferred while the engine was down can now proceed.
    /// </summary>
    public enum EngineEnsureOutcome
    {
        /// <summary>Already running: nothing to do.</summary>
        Running,
        /// <summary>Installed and stopped: started unelevated (the DACL grants it).</summary>
        Started,
        /// <summary>Was missing: registered and started through one elevation.</summary>
        Installed,
        /// <summary>Registered with a DACL this process cannot open: re-asserted through one elevation.</summary>
        Repaired,
        /// <summary>Dev layout: no staged engine exe, so there is no service to ensure.</summary>
        DevLayout,
        /// <summary>Repair attempted and failed (declined UAC, SCM failure, start timeout).</summary>
        Failed
    }

    /// <summary>
    /// Keeps the engine service installed, openable and running, the way the XMG
    /// Control Center shell keeps GCUBridge alive: the app owns the engine's
    /// lifecycle, the engine owns the hardware.
    ///
    /// The service is registered <c>start= auto</c>, so at every boot it is already
    /// up; this is the self-heal pass, and it reads the state from
    /// <see cref="EngineAvailability"/> (an SCM query, not an RPC, so asking costs
    /// nothing on the startup path). Each state has its own repair:
    ///
    ///  - missing: one elevated self-launch of "<c>Uniwill Control Utility Engine.exe
    ///    --install-engine</c>" - the single UAC prompt of the product;
    ///  - stopped: started unelevated, because the install grants Authenticated Users
    ///    SERVICE_START (see <c>EngineInstaller.ServiceSddl</c>);
    ///  - not openable (access denied): the registration carries a DACL from a build
    ///    that predates that grant, so the app cannot start the service it owns. The
    ///    same elevated install pass re-asserts the DACL and starts it: one prompt,
    ///    repaired permanently. This is the state an in-place MSI upgrade inherits,
    ///    because the MSI uninstall route leaves the registration (only the
    ///    <c>Uninstall.exe</c> shim deletes it) and the DACL travels with it.
    ///
    /// A missing engine exe (dev checkout, no Service\ staging) is normal and silent:
    /// a dev run targets a console-mode engine
    /// (<c>dotnet run --project UCU.Engine -- --console</c>).
    /// </summary>
    internal static class EngineLifecycle
    {
        public const string ServiceName = EngineAvailability.ServiceName;
        public const string EngineExeFileName = "Uniwill Control Utility Engine.exe";

        /// <summary>The SCM start timeout; the engine reports Running in under a second.</summary>
        private static readonly TimeSpan StartBudget = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The engine executable in the install layout's Service\ subfolder
        /// (staged by the csproj targets), or null in a dev checkout.
        /// </summary>
        public static string? EngineExePath
        {
            get
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Service", EngineExeFileName);
                return File.Exists(path) ? path : null;
            }
        }

        /// <summary>
        /// One ensure at a time, process-wide. The startup EC task and the
        /// post-splash chain both call this; racing, the second call pinged an
        /// engine the first call's elevated pass was still starting, read the
        /// failed ping as "engine dead", elevated a second time, and burned the
        /// once-ever repair marker on a pass that was never needed.
        /// </summary>
        private static readonly SemaphoreSlim EnsureGate = new(1, 1);

        public static async Task<EngineEnsureOutcome> EnsureAsync(
            EngineAvailability availability,
            Func<Task<bool>>? engineReachable = null)
        {
            ArgumentNullException.ThrowIfNull(availability);

            await EnsureGate.WaitAsync().ConfigureAwait(false);
            try
            {
                return await EnsureCoreAsync(availability, engineReachable).ConfigureAwait(false);
            }
            finally
            {
                EnsureGate.Release();
            }
        }

        private static async Task<EngineEnsureOutcome> EnsureCoreAsync(
            EngineAvailability availability,
            Func<Task<bool>>? engineReachable)
        {
            EngineServiceState state = availability.Query();

            if (state == EngineServiceState.Running)
                return EngineEnsureOutcome.Running;

            if (state == EngineServiceState.Stopped)
            {
                EngineEnsureOutcome? started = TryStart(availability);
                if (started is not null)
                    return started.Value;

                // Start was refused with access denied: fall through to the elevated
                // repair, which is the only thing that can fix the DACL.
                state = EngineServiceState.AccessDenied;
            }

            if (state == EngineServiceState.AccessDenied)
            {
                // Grace for the service's own DACL assertion: the engine re-grants
                // the app contract from inside (ServiceSelfSecurityService) because
                // a security product that guards service descriptors reverts the
                // installer's sdset (field-proven: Emsisoft - SetServiceObjectSecurity
                // SUCCESS, read-back is the template, immediately, every time).
                // Just after a service start the assertion may be in flight, and
                // elevation is meant to be once-ever: re-read once before taking it.
                await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                availability.Invalidate();

                state = availability.Query();
                if (state == EngineServiceState.Running)
                    return EngineEnsureOutcome.Running;

                if (state == EngineServiceState.Stopped)
                {
                    EngineEnsureOutcome? retarted = TryStart(availability);
                    if (retarted is not null)
                        return retarted.Value;

                    state = EngineServiceState.AccessDenied;
                }
            }

            if (state == EngineServiceState.AccessDenied)
            {
                // The SCM descriptor is the app's PERFORMANCE contract, not its
                // functional one: the RPC pipe answers regardless of who can read
                // the SCM. So ask the engine itself - a ping answers liveness that
                // the SCM cannot describe. An engine that answers is running, the
                // elevation repairs a descriptor, not the engine, and repairing
                // what works is how "UAC on every launch" was born.
                if (engineReachable is not null && await engineReachable().ConfigureAwait(false))
                {
                    Log.Information("[Engine] The service is not openable by this app, but the engine answers RPC - treating it as running and leaving the descriptor to the service's own assertions.");
                    return EngineEnsureOutcome.Running;
                }

                if (RepairAttempted())
                {
                    // One elevation was already spent on this registration's
                    // descriptor and it did not stick: on a machine whose security
                    // product guards service descriptors, every write is reverted
                    // (installer sdset, service self-assert, all of them), and the
                    // repair would re-elevate on every launch forever. The marker
                    // keeps the elevation once-ever; the runbook's service-protection
                    // entry is the fix on such machines.
                    Log.Warning("[Engine] The engine service is not openable and not answering, and the DACL repair was already attempted - a security product is reverting the service descriptor. The runbook's service-protection entry is the permanent fix.");
                    return EngineEnsureOutcome.Failed;
                }

                MarkRepairAttempted();
            }

            string? exe = EngineExePath;
            if (exe is null)
            {
                Log.Information("[Engine] No engine service and no staged engine exe - dev layout, nothing to ensure.");
                return EngineEnsureOutcome.DevLayout;
            }

            bool repair = state == EngineServiceState.AccessDenied;
            Log.Information(repair
                ? "[Engine] Engine service registration is not openable by this app - re-asserting it (DACL, binPath) with the one-time elevated install."
                : "[Engine] Engine service missing - running the one-time elevated install.");

            int rc = await ElevatedProcess
                .RunAsync(exe, "--install-engine", "Engine service install")
                .ConfigureAwait(false);

            // The install pass re-asserts the registration, the DACL and the start,
            // so the SCM state has certainly changed.
            availability.Invalidate();

            if (rc != 0)
            {
                Log.Warning("[Engine] Elevated engine {Action} exited with {Code}", repair ? "repair" : "install", rc);
                return EngineEnsureOutcome.Failed;
            }

            return repair ? EngineEnsureOutcome.Repaired : EngineEnsureOutcome.Installed;
        }

        /// <summary>
        /// Starts the installed service, returning the outcome, or null when the
        /// failure is the access-denied case the caller repairs with elevation.
        /// </summary>
        private static EngineEnsureOutcome? TryStart(EngineAvailability availability)
        {
            try
            {
                // ScmNative opens for SERVICE_START + SERVICE_QUERY_STATUS only -
                // the rights the install's AU ace grants. ServiceController opens
                // for its full read set and was access-denied on healthy installs.
                ScmNative.StartAndWait(ServiceName, StartBudget);
                availability.Invalidate();
                Log.Information("[Engine] Engine service started.");
                return EngineEnsureOutcome.Started;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == EngineAvailability.ErrorAccessDenied)
            {
                // The install's DACL grants Authenticated Users SERVICE_START, so a
                // registration that refuses the start predates that grant (a build
                // whose SDDL omitted RP). Nothing the unelevated app can do fixes a
                // DACL, and treating this as "the service is unavailable" would leave
                // the machine permanently degraded: the app could never start the
                // engine it owns.
                Log.Warning(ex,
                    "[Engine] Engine service is stopped and this app is not allowed to start it (native code {Code}) - the registration predates the SERVICE_START grant",
                    ex.NativeErrorCode);
                return null;
            }
            catch (Win32Exception ex)
            {
                // Includes 1060: the service stopped existing between the status
                // query and the start (uninstall raced us).
                Log.Warning(ex, "[Engine] Could not start the engine service (native code {Code})", ex.NativeErrorCode);
                return EngineEnsureOutcome.Failed;
            }
            catch (TimeoutException ex)
            {
                Log.Warning(ex, "[Engine] The engine service did not reach Running within {Budget}s", StartBudget.TotalSeconds);
                return EngineEnsureOutcome.Failed;
            }
        }

        /// <summary>
        /// One-ever marker for the elevated DACL repair. Content is a contract tag:
        /// when the repair contract changes (new SDDL, new assertion design), the
        /// marker stops matching and machines get one fresh attempt. A file, not a
        /// setting: it must survive settings resets and be per-user like the service
        /// registration it guards.
        /// </summary>
        internal const string RepairContractTag = "dacl-v2-selfassert";

        private static string RepairMarkerPath =>
            System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UCU", "engine", "dacl-repair-attempted");

        internal static bool RepairAttempted()
        {
            try
            {
                return System.IO.File.Exists(RepairMarkerPath)
                    && System.IO.File.ReadAllText(RepairMarkerPath).Trim() == RepairContractTag;
            }
            catch
            {
                return false;
            }
        }

        internal static void MarkRepairAttempted()
        {
            try
            {
                System.IO.Directory.CreateDirectory(
                    System.IO.Path.GetDirectoryName(RepairMarkerPath)!);
                System.IO.File.WriteAllText(RepairMarkerPath, RepairContractTag);
            }
            catch
            {
                // The marker is a kindness to the user, not a safety property:
                // without it the next launch repeats the elevation, which is the
                // symptom, never a correctness break.
            }
        }
    }
}
