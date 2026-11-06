---
name: debug-wpf-winrt-deadlock
description: Diagnose and fix UI thread deadlocks from blocking on WinRT async APIs (.Result/.Wait()) in WPF apps
---

# Debugging WPF + WinRT Async Deadlocks

## Problem Pattern

A WPF app freezes or crashes when navigating to a page, loading settings, or performing I/O. The root cause is calling `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()` on an async operation that uses the current `SynchronizationContext` (WinRT APIs, some .NET async methods) while running on the UI thread.

**Classic symptom:** App freezes on page navigation → never recovers → may crash with a deadlock exception.

## How to Diagnose

1. **Check constructors and synchronous entry points** — Look for `.AsTask().Result`, `.Wait()`, or blocking calls in:
   - Page/view constructors (called during DI resolution on UI thread)
   - `Loaded`/`Initialized` event handlers
   - Synchronous service methods called from the UI layer

2. **Identify WinRT API usage** — These APIs capture and resume on their own synchronization context:
   - `Windows.Storage.ApplicationData.Current.RoamingFolder.CreateFolderAsync()`
   - `Windows.Storage.StorageFile.OpenAsync()` / `CreateFileAsync()`
   - Any `IAsyncAction`/`IAsyncOperation` converted via `.AsTask()`

3. **Confirm the deadlock** — The pattern is: UI thread calls `.Result` → blocks → WinRT async completes → tries to resume on captured context → context is blocked → deadlock.

## Fix Strategies (in order of preference)

### 1. Replace WinRT APIs with standard .NET equivalents (best fix)

WinRT storage APIs are unnecessary in WPF — use `System.IO` directly:

```csharp
// ❌ BAD — deadlocks on UI thread
var directory = ApplicationData.Current.RoamingFolder;
var folderTask = directory.CreateFolderAsync("UXTU", CreationCollisionOption.OpenIfExists).AsTask().Result;
var filePath = Path.Combine(folderTask.Path, "settings.json");

// ✅ GOOD — pure .NET, no async context issues
private static readonly string SettingsFolder = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyApp");

Directory.CreateDirectory(SettingsFolder);  // idempotent, synchronous
var filePath = Path.Combine(SettingsFolder, "settings.json");
```

**Mapping table:**

| WinRT API | .NET equivalent |
|---|---|
| `ApplicationData.Current.RoamingFolder` | `%APPDATA%` (`Environment.SpecialFolder.ApplicationData`) |
| `ApplicationData.Current.LocalFolder` | `%LOCALAPPDATA%` (`Environment.SpecialFolder.LocalApplicationData`) |
| `StorageFile.CreateAsync()` | `File.Create()` / `Directory.CreateDirectory()` |
| `StorageFile.ReadTextAsync().AsTask().Result` | `File.ReadAllText()` |
| `StorageFile.WriteTextAsync().AsTask().Result` | `File.WriteAllText()` |

### 2. Use async all the way (if WinRT API is unavoidable)

Make the calling chain fully async:

```csharp
// Constructor — don't do blocking work here
public MyPage(MyService service)
{
    InitializeComponent();
    _ = LoadSettingsAsync();  // fire-and-forget for initialization
}

private async Task LoadSettingsAsync()
{
    var directory = ApplicationData.Current.RoamingFolder;
    var folder = await directory.CreateFolderAsync("UXTU", CreationCollisionOption.OpenIfExists);
    var filePath = Path.Combine(folder.Path, "settings.json");
    // ... use filePath
}
```

### 3. Detach from synchronization context (last resort)

If you must block and can't make it async:

```csharp
var result = Task.Run(async () => await someAsyncOperation()).Result;
```

This runs the async work on a thread-pool thread, avoiding the UI context capture. Risky — exceptions are swallowed in `.Result`.

## Prevention Checklist for WPF Projects

- [ ] No `.AsTask().Result` or `.Wait()` in page constructors
- [ ] No WinRT storage APIs when `System.IO` suffices
- [ ] Service initialization that involves async I/O is either fully async or uses synchronous .NET APIs
- [ ] DI-resolved pages don't perform blocking work in their constructors
