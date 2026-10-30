---
name: detect-elevated-processes
description: Detect elevated or protected processes (e.g. fullscreen games) via Win32 window enumeration when Process.MainModule.FileName throws access exceptions
---

# Detecting Elevated/Protected Processes via Window Enumeration

## Problem Pattern

`Process.GetProcesses()` combined with `process.MainModule.FileName` fails for processes running in an elevated or protected context (e.g. fullscreen games launched after an anti-cheat overlay, UAC-elevated applications, or processes under Windows Protected Process Light). The access throws a `Win32Exception` (ERROR_ACCESS_DENIED), silently skipping the process.

**Classic symptom:** A polling loop detects a process initially (e.g. EAC launcher window), but loses detection when the actual fullscreen game process starts, causing the application to revert to default settings.

## Why Confidence Counters Don't Help

Adding a "confidence threshold" (detect X consecutive times before committing) only delays the revert — it does not fix the root cause. If the fullscreen process is never detected due to access exceptions, the confidence counter just counts down to zero.

## Solution: Two-Pass Detection with Window Enumeration Fallback

### Pass 1: Process-based detection (unchanged)

```csharp
foreach (Process process in Process.GetProcesses())
{
    try
    {
        string exePath = process.MainModule.FileName;
        // match against known game paths
    }
    catch (Exception ex)
    {
        // silently skip — elevated/protected processes throw here
    }
}
```

### Pass 2: Window-title fallback

When Pass 1 finds nothing, enumerate visible top-level windows and match their titles:

```csharp
[DllImport("user32.dll", SetLastError = true)]
static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

[DllImport("user32.dll", SetLastError = true)]
static extern int GetWindowTextLength(IntPtr hWnd);

[DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr hWnd);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool EnumWindows(WndEnumProc lpEnumFunc, IntPtr lParam);

delegate bool WndEnumProc(IntPtr hWnd, IntPtr lParam);
```

```csharp
var matchedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

EnumWindows((hWnd, lParam) =>
{
    if (!IsWindowVisible(hWnd))
        return true; // continue

    int length = GetWindowTextLength(hWnd);
    if (length == 0)
        return true;

    var sb = new StringBuilder(length + 1);
    GetWindowText(hWnd, sb, sb.Capacity);
    string title = sb.ToString().Trim();

    // Match against known game names
    foreach (var game in knownGames)
    {
        if (title.Contains(game.Name, StringComparison.OrdinalIgnoreCase))
            matchedNames.Add(game.Name);
    }

    return true; // continue enumeration
}, IntPtr.Zero);
```

### Disambiguation

If multiple game names match, prefer the **longest match** (most specific):

```csharp
string detected = matchedNames.OrderByDescending(n => n.Length).First();
```

## Why This Works

- `EnumWindows` + `GetWindowText` operates on window handles, not process handles — no privilege escalation required
- Fullscreen games still create a top-level window with a title bar (even in exclusive fullscreen, the window exists in the desktop heap)
- Window titles are readable from any process context

## Limitations

- Games with blank or generic window titles ("Application", "Unity", empty string) won't match by name
- Games running with `-windowtitle` overrides may produce unexpected matches
- Window enumeration is slightly slower than process enumeration; only use as a fallback when Pass 1 fails

## Prevention Checklist

- [ ] Process-based detection wrapped in try/catch for `Win32Exception`
- [ ] Window enumeration fallback when process-based detection finds nothing
- [ ] Case-insensitive title matching to handle varied casing
- [ ] Longest-match heuristic to avoid partial name collisions
- [ ] `IsWindowVisible` filter to skip hidden/background windows
