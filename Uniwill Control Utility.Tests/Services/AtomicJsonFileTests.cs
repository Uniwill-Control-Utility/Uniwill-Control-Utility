using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

/// <summary>
/// The crash-safe settings write (S5): a sibling .tmp is written and moved over
/// the live file, so a reader sees either the old file or the new one, never a
/// half-written one. These pin the two observable promises: the replacement is
/// complete and no .tmp is left behind, and a missing directory is created.
/// </summary>
public sealed class AtomicJsonFileTests
{
    [Fact]
    public void Write_ReplacesTheFile_AndLeavesNoTemp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ucu-tests", $"atomic_{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "settings.json");
        try
        {
            AtomicJsonFile.Write(path, "{\"a\":1}");
            AtomicJsonFile.Write(path, "{\"a\":2}");

            Assert.Equal("{\"a\":2}", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Write_CreatesAMissingDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ucu-tests", $"atomic_{Guid.NewGuid():N}", "nested");
        string path = Path.Combine(dir, "settings.json");
        try
        {
            AtomicJsonFile.Write(path, "{}");

            Assert.True(File.Exists(path));
        }
        finally
        {
            string root = Path.GetDirectoryName(dir)!;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
