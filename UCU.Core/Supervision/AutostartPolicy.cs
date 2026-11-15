namespace UCU.Core.Supervision;

/// <summary>What the startup pass should do about the logon task.</summary>
public enum AutostartReconcileAction
{
    /// <summary>State and intent agree; leave the machine alone.</summary>
    None,

    /// <summary>Intent is "on" and the task is missing or points at another exe.</summary>
    Register,

    /// <summary>Intent is "off" and the task is present.</summary>
    Unregister,
}

/// <summary>
/// Reconciles the saved autostart toggle with the task the machine actually has.
///
/// The two are deliberately independent stores, and an uninstall separates them:
/// the removal routes delete "UCU Autostart" while <c>%LOCALAPPDATA%\UCU</c> (the
/// settings file with <c>StartOnBoot</c>) survives. A reinstall therefore starts
/// with intent and no state - the Settings row reads the setting and shows ON,
/// while nothing would start the app at logon (field 2026-11-15: a tester's
/// autostart was silently off after uninstall/reinstall until he toggled the row).
///
/// The setting is the user's intent and the task is only the machine's state, so
/// intent wins: a missing task is re-registered, and a task left behind by a
/// disabled toggle is removed. Both directions are idempotent, which is what lets
/// the startup pass call this on every launch.
/// </summary>
public static class AutostartPolicy
{
    public static AutostartReconcileAction Decide(
        bool enabled,
        string? registeredActionPath,
        string expectedActionPath)
    {
        bool exists = !string.IsNullOrEmpty(registeredActionPath);

        if (!enabled)
        {
            // The task name is ours by definition, so a present one is stale whatever
            // path it carries (a moved install leaves the old exe path behind).
            return exists ? AutostartReconcileAction.Unregister : AutostartReconcileAction.None;
        }

        if (!exists)
        {
            return AutostartReconcileAction.Register;
        }

        return string.Equals(registeredActionPath, expectedActionPath, StringComparison.OrdinalIgnoreCase)
            ? AutostartReconcileAction.None
            : AutostartReconcileAction.Register;
    }
}
