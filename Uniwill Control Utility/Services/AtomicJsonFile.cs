using System.IO;
using System.Text;

namespace Uniwill_Control_Utility.Services
{
    /// <summary>
    /// A settings/preset write that survives a crash or power loss.
    ///
    /// Every store used to rewrite its live file in place (truncate, then write), so an
    /// interrupted save left truncated JSON behind and the next load either threw or
    /// silently dropped the user's presets. This writes a sibling .tmp and moves it
    /// over the live file: the move is atomic on one volume, so a reader sees either
    /// the old file or the new one, never a half-written one.
    ///
    /// The pattern is WatcherStore's, applied to every store; the load side keeps its
    /// own parsing (Newtonsoft and System.Text.Json stores differ) and its own
    /// corrupt-file handling.
    /// </summary>
    internal static class AtomicJsonFile
    {
        public static void Write(string path, string json)
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // A .tmp left by a previous crash is simply overwritten.
            string temp = path + ".tmp";

            // Flush the data to the volume *before* the rename. A rename is journaled,
            // so without an explicit flush a power loss can leave the new name pointing
            // at content that was never written (zero-length or partial), which is the
            // one failure the .tmp dance does not cover on its own.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // File.Replace needs an existing destination; the first save of a store has
            // none, so fall back to a move.
            if (File.Exists(path))
                File.Replace(temp, path, destinationBackupFileName: null);
            else
                File.Move(temp, path);
        }
    }
}
