using System;
using System.Threading;
using System.Threading.Tasks;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// One-shot signal that the dashboard has applied its first real data
    /// frame. The startup splash's last stage ("Opening dashboard") is gated
    /// on it: the field signature it closes is the window appearing EMPTY -
    /// the splash closed when the window was created, while the dashboard
    /// waited on the first hardware snapshot (the probe's first pass runs
    /// 1-2 s healthy, up to the 6 s startup budget on a guarded machine, so
    /// the user watched an empty dashboard for ~6 s). The splash's whole
    /// purpose is to cover exactly that; it now releases on the first
    /// populated frame, bounded by a budget in the caller so a wedged sensor
    /// probe can never hold it.
    /// </summary>
    public sealed class DashboardFirstFrameSignal
    {
        private readonly TaskCompletionSource _firstFrame =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _signalled;

        /// <summary>Completes when the first real frame has been applied.</summary>
        public Task FirstFrame => _firstFrame.Task;

        /// <summary>
        /// Fires the signal. Idempotent: every later frame is a no-op, so the
        /// caller may call it from the metrics tick without bookkeeping.
        /// </summary>
        public void SignalFirstFrame()
        {
            if (Interlocked.CompareExchange(ref _signalled, 1, 0) == 0)
                _firstFrame.TrySetResult();
        }
    }
}
