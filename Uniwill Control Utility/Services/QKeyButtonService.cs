using Microsoft.Extensions.Hosting;
using Serilog;
using System.Management;
using System.Windows.Threading;
using System;
using System.Threading;
using System.Threading.Tasks;
using Uniwill_Control_Utility.Models;
using Uniwill_Control_Utility.Properties;
using Uniwill_Control_Utility.Scripts.Misc;
using Uniwill_Control_Utility.Views.Pages;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// Detects presses of the hardware Performance / Q-key button on Uniwill
    /// NB02-platform laptops and cycles UCU's power presets.
    ///
    /// Detection: the EC's ACPI WMI provider — the same one UCU's Display page
    /// instantiates for MiniLED Local Dimming — raises an AcpiTest_EventULong event
    /// whose ULong payload is 176 (0xB0) for every physical press. That echo only
    /// fires while 0x0741 bit0 is set: with bit0 clear the firmware consumes the
    /// press itself (it walks its own 0x0751 cycle of 0xA0 / 0x20 / 0x30) and emits
    /// nothing, so UCU keeps manual control armed permanently and arms it here at
    /// startup. Verified 2026-10-12 by docs\acpi\WmiProbe.
    ///
    /// Because the press arrives as an event from the EC rather than as a register
    /// latch, UCU's own preset applies can never be mistaken for a press and the
    /// button stays live while a Custom preset holds manual control. Saved Custom
    /// presets are therefore cycle members, matching OpenRevo's Office -> Gaming
    /// -> Turbo -> Custom order.
    ///
    /// On a confirmed press the service advances to the next profile from
    /// <see cref="PresetProfileApplier.GetCycleProfileNames"/> (the built-ins plus the
    /// saved Custom slots of the pool Power Boost Mode selects, wrapping), applies it
    /// through the same code path the UI
    /// uses, and persists the selection in Settings so the Presets page and resume
    /// handlers pick it up. A toast notification matching the UI and tray apply
    /// paths announces the switch.
    ///
    /// Q-key LED: the EC firmware drives the LED from its own internal mode state,
    /// and the 0x0751 literal in UniwillECService.ApplyOemStockMode is what recolors
    /// it (Office white / Gaming yellow / Turbo red). When <c>EnableQKeyLedSync</c>
    /// is on, the service additionally mirrors the selected preset into the EC mode
    /// index register (0x07AB) and pokes the trigger register (0x0767) for firmware
    /// that honors the mode index instead.
    /// </summary>
    public sealed class QKeyButtonService : IHostedService, IDisposable
    {
        private const string WmiNamespace = @"root\WMI";
        private const string PressEventQuery = "SELECT * FROM AcpiTest_EventULong";

        /// <summary>
        /// The EC's ULong payload for a physical Performance-button press. The same
        /// provider raises other codes that are echoes of something else (168 = mode
        /// state written programmatically, 240 = keyboard brightness) and they are
        /// ignored.
        /// </summary>
        private const uint PressEchoCode = 176;

        /// OpenRevo ignores presses closer together than this (its log prints
        /// "Q-Key debounced (elapsed < 500ms)"), so a double-tap counts once.
        private const int PressDebounceMs = 500;

        /// Idle wait that only keeps the watcher alive until shutdown; the press
        /// itself arrives through the EventArrived callback, never through this loop.
        private const int ShutdownHeartbeatMs = 1000;

        /// <summary>
        /// Wait this long after host start before subscribing. Phase 1 startup
        /// (startup settings apply) touches the EC; subscribing in parallel with
        /// the handshake would race it.
        /// </summary>
        private const int StartupDelayMs = 4000;

        private readonly UniwillECService _ec;
        private readonly CancellationTokenSource _cts = new();
        private Task? _watchTask;

        private volatile bool _providerUnavailableLogged;
        private volatile bool _handlingPress;
        private DateTime _lastPressAt = DateTime.MinValue;
        private bool _disposed;
        private Presets? _presetsPage;

        public QKeyButtonService(UniwillECService ec)
        {
            _ec = ec;
        }

        /// <summary>
        /// Registers the Presets page instance so a preset applied through the button
        /// updates the page's own selection state, not just the persisted setting.
        /// </summary>
        public void RegisterPresetsPage(Presets page) => _presetsPage = page;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _watchTask = Task.Run(() => WatchLoop(_cts.Token));
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cts.Cancel();
            if (_watchTask is not null)
            {
                try
                {
                    await Task.WhenAny(_watchTask, Task.Delay(3000, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    Log.Warning("[QKey] {ExceptionType} while stopping event watcher: {Message}",
                        ex.GetType().Name, ex.Message);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _cts.Cancel();
            _cts.Dispose();
        }

        private async Task WatchLoop(CancellationToken ct)
        {
            await Task.Delay(StartupDelayMs, ct);

            if (!_ec.Initialize())
            {
                if (!_providerUnavailableLogged)
                {
                    _providerUnavailableLogged = true;
                    Log.Information("[QKey] EC driver unavailable - Performance button intercept disabled on this machine");
                }
                return;
            }

            // The EC raises the press echo only while 0x0741 bit0 is set, so arm
            // manual control before subscribing: a machine that booted at the
            // firmware's own gate (0x04) would otherwise leave the listener deaf.
            _ec.UnlockManualMode();

            try
            {
                using ManagementEventWatcher watcher = new(WmiNamespace, PressEventQuery);
                watcher.EventArrived += (sender, arrived) => OnEventArrived(arrived);
                watcher.Start();

                Log.Information("[QKey] Performance button monitor started (WMI {Namespace}, {Query})",
                    WmiNamespace, PressEventQuery);

                try
                {
                    while (true)
                        await Task.Delay(ShutdownHeartbeatMs, ct);
                }
                catch (OperationCanceledException)
                {
                    // normal shutdown
                }
                finally
                {
                    watcher.Stop();
                }
            }
            catch (ManagementException ex)
            {
                if (!_providerUnavailableLogged)
                {
                    _providerUnavailableLogged = true;
                    Log.Warning("[QKey] WMI event provider unavailable ({ExceptionType}: {Message}) - " +
                        "Performance button intercept needs the EC's AcpiTest_EventULong channel and stays off on this machine",
                        ex.GetType().Name, ex.Message);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QKey] Performance button event watcher failed - intercept stopped");
            }
        }

        private void OnEventArrived(EventArrivedEventArgs arrived)
        {
            using ManagementBaseObject evt = arrived.NewEvent;

            var code = ReadEventCode(evt);
            if (code != PressEchoCode)
            {
                Log.Debug("[QKey] Ignoring AcpiTest_EventULong echo {Code} (not a press)", code);
                return;
            }

            var sinceLastMs = (int)(DateTime.Now - _lastPressAt).TotalMilliseconds;
            if (sinceLastMs < PressDebounceMs)
            {
                Log.Information("[QKey] Performance button press debounced (previous press {Elapsed}ms ago)",
                    sinceLastMs);
                return;
            }

            _lastPressAt = DateTime.Now;
            Log.Information("[QKey] Performance button press detected (AcpiTest_EventULong ULong={Code})", code);
            _ = HandlePressAsync();
        }

        /// <summary>
        /// Reads the ULong payload of an AcpiTest_EventULong event. The MOF binding
        /// hands property values back as <c>object</c>, so accept any integral width
        /// rather than assuming the declared UInt32 arrives boxed as one type.
        /// </summary>
        private static uint ReadEventCode(ManagementBaseObject evt)
        {
            var raw = evt.GetPropertyValue("ULong");
            return raw switch
            {
                uint value => value,
                int value => (uint)value,
                ulong value => (uint)value,
                ushort value => value,
                byte value => value,
                _ => 0,
            };
        }

        private async Task HandlePressAsync()
        {
            if (_handlingPress)
            {
                Log.Information("[QKey] Press arrived while a preset apply is still running - ignored");
                return;
            }

            _handlingPress = true;
            try
            {
                if (!Settings.Default.EnableQKeyButtonIntercept)
                    return;

                if (Settings.Default.isAdaptiveModeRunning)
                {
                    Log.Information("[QKey] Ignoring button press - Adaptive Mode is running");
                    return;
                }

                string[] names = PresetProfileApplier.GetCycleProfileNames();

                string? current = Settings.Default.presetProfile;

                // The Presets page and the tray persist the bare "Custom" instead of the
                // slot-qualified cycle key, so a Custom preset would otherwise restart
                // the walk at Office. Resolve it from the active mode's saved slot.
                if (string.Equals(current, "Custom", StringComparison.OrdinalIgnoreCase))
                {
                    bool boostMode = Settings.Default.EnablePowerBoostMode;
                    current = PresetProfileApplier.CustomProfileKey(
                        boostMode ? Settings.Default.customPresetSlotBoost : Settings.Default.customPresetSlot,
                        boostMode ? Models.PresetMode.Boost : Models.PresetMode.Normal);
                }

                int idx = -1;
                for (int i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], current, StringComparison.OrdinalIgnoreCase))
                    {
                        idx = i;
                        break;
                    }
                }

                // Built-ins plus the saved Custom slots of the active pool, wrapping: the
                // press comes from the EC event rather than a firmware latch, so a Custom
                // preset never takes the button dead and can sit in the cycle.
                string next = names[(idx + 1) % names.Length];

                string displayName = PresetProfileApplier.DisplayName(next);
                await PresetProfileApplier.ApplyProfileAsync(next, displayName);

                Settings.Default.presetProfile = next;
                Settings.Default.Save();

                // The Presets page keeps its own selected-profile state, and its render
                // tree may only be touched from the render thread: post the re-sync
                // through the page's dispatcher, which runs it there. Calling
                // loadSavedProfile directly faults at the first Style assignment in
                // selectProfile, because this handler runs on the WMI callback thread.
                _presetsPage?.Dispatcher.BeginInvoke(
                    () =>
                    {
                        _presetsPage?.loadSavedProfile();

                        // selectProfile persists the page's own profile name, which is
                        // the bare "Custom" for a custom slot; the cycle needs the
                        // slot-qualified key to keep walking.
                        Settings.Default.presetProfile = next;
                        Settings.Default.Save();
                    },
                    DispatcherPriority.Normal);

                if (Settings.Default.EnableQKeyLedSync)
                    SyncQKeyLed(next);

                ToastNotification.ShowToastNotification($"{displayName} Applied!",
                    $"{displayName} has been applied!");

                Log.Information("[QKey] Cycled to {Profile} preset", displayName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[QKey] Failed to apply preset after button press");
            }
            finally
            {
                _handlingPress = false;
            }
        }

        /// <summary>
        /// Mirrors the applied preset into the EC mode index register and pokes
        /// the trigger register so EC firmware that drives the Q-key LED from
        /// the mode index (OpenRevo's sequence on Mechrevo units) updates the
        /// LED. Read-modify-write on the trigger keeps any RGB effect bits
        /// (logo / rainbow) set by the keyboard apply path intact.
        /// Never uses the _Checked write variants: the EC on this platform
        /// reports error code 1 for writes even on success.
        /// </summary>
        private void SyncQKeyLed(string profileName)
        {
            try
            {
                int modeIndex = PresetProfileApplier.OemModeIndexFor(profileName);

                byte prevMode = _ec.ReadECRegister(UniwillECService.REG_MODE_INDEX);
                _ec.WriteECRegister(UniwillECService.REG_MODE_INDEX,
                    (byte)((prevMode & 0xE0) | modeIndex | UniwillECService.MODE_INDEX_ARM));
                Thread.Sleep(10);

                byte trigger = _ec.ReadECRegister(UniwillECService.REG_TRIGGER);
                _ec.WriteECRegister(UniwillECService.REG_TRIGGER,
                    (byte)(trigger | UniwillECService.TRIGGER_RGB_APPLY_COLOR));

                Log.Information("[QKey] LED sync: mode index {Mode} written to 0x7AB", modeIndex);
            }
            catch (Exception ex)
            {
                Log.Warning("[QKey] LED sync failed: {Message}", ex.Message);
            }
        }
    }
}
