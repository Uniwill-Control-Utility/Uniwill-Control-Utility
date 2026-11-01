using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The shortcut map (U-204): the chord grammar shared with the overlay
/// toggles, the shortcuts.json sanitise gate (a hand-edited file must never
/// fire a bad action or crash the hook), and the store's corrupt-file
/// quarantine. The hook dispatch itself is field-proven by the existing
/// overlay hotkeys; what is pinned here is everything that decides WHICH
/// action a press can reach.
/// </summary>
public sealed class ShortcutTests
{
    [Theory]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("ctrl+alt+g")]
    [InlineData("Alt+F10")]
    public void ChordGrammar_AcceptsModifierPlusKey(string chord)
    {
        Assert.True(Hotkey.TryParse(chord, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+Shift")]   // modifiers only, no pressable key
    [InlineData("Ctrl+Nope")]    // unknown key name
    [InlineData("Ctrl+A+B")]     // two primary keys
    public void ChordGrammar_RejectsUnpressableChords(string chord)
    {
        Assert.False(Hotkey.TryParse(chord, out _));
    }

    [Fact]
    public void Sanitise_DropsInvalidRows_AndKeepsTheFirstOfADuplicateChord()
    {
        var rows = new List<ShortcutBinding>
        {
            new() { Chord = "Ctrl+Shift+F5", Action = ShortcutAction.SetFanTurbo },
            new() { Chord = "ctrl+shift+f5", Action = ShortcutAction.SetPerformanceMode, PerformanceMode = 2 }, // duplicate (case-insensitive)
            new() { Chord = "Ctrl+Shift", Action = ShortcutAction.SetFanTurbo },                                   // unpressable
            new() { Chord = "Ctrl+Alt+P", Action = ShortcutAction.ApplyPreset },                                   // missing preset
            new() { Chord = "Ctrl+Alt+M", Action = ShortcutAction.SetPerformanceMode, PerformanceMode = 99 },      // out of range
            new() { Chord = " Ctrl+Alt+O ", Action = ShortcutAction.PauseOemTakeover },                            // trimmed, kept
        };

        List<ShortcutBinding> valid = ShortcutStore.Sanitise(rows);

        Assert.Equal(2, valid.Count);
        Assert.Equal("Ctrl+Shift+F5", valid[0].Chord);
        Assert.Equal(ShortcutAction.SetFanTurbo, valid[0].Action);
        Assert.Equal("Ctrl+Alt+O", valid[1].Chord);
    }

    [Fact]
    public void Sanitise_CapsTheBindingCount()
    {
        // Distinct pressable chords (the cap counts distinct bindings, and a
        // duplicate chord is dropped as a duplicate, not counted).
        var keys = new List<string>();
        for (int f = 1; f <= 24; f++)
            keys.Add("F" + f);
        for (int d = 0; d <= 9; d++)
            keys.Add("D" + d);
        for (int n = 0; n <= 7; n++)
            keys.Add("NumPad" + n);

        IEnumerable<ShortcutBinding> rows = keys.Select(k => new ShortcutBinding
        {
            Chord = "Ctrl+" + k,
            Action = ShortcutAction.SetFanTurbo,
        });

        Assert.True(keys.Count > ShortcutStore.MaxBindings);
        Assert.Equal(ShortcutStore.MaxBindings, ShortcutStore.Sanitise(rows).Count);
    }

    [Fact]
    public void Store_RoundTrips_AndQuarantinesACorruptFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "ucu-shortcuts-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new ShortcutStore(path);
            var binding = new ShortcutBinding
            {
                Chord = "Ctrl+Shift+F5",
                Action = ShortcutAction.ApplyPreset,
                PresetName = "Gaming",
            };

            store.Save(new[] { binding });
            List<ShortcutBinding> loaded = new ShortcutStore(path).Load();

            ShortcutBinding only = Assert.Single(loaded);
            Assert.Equal(binding, only);

            // Corrupt content: the file is moved aside, the hub starts empty.
            File.WriteAllText(path, "{ not json");
            Assert.Empty(new ShortcutStore(path).Load());
            Assert.True(File.Exists(path + ".bak"));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
            if (File.Exists(path + ".bak"))
                File.Delete(path + ".bak");
            if (File.Exists(path + ".tmp"))
                File.Delete(path + ".tmp");
        }
    }
}
