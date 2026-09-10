using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Uniwill_Control_Utility.Models
{
    /// <summary>
    /// A single V-F point from the stock (factory) curve snapshot.
    /// </summary>
    public class VfStockPoint
    {
        public int Index { get; init; }
        public int VoltageMv { get; init; }
        public int FrequencyMhz { get; init; }
    }

    /// <summary>
    /// Persists a stock V-F curve snapshot to AppData.
    /// On first run (when no snapshot exists), the current curve is captured and saved.
    /// On subsequent runs, the saved snapshot is used as the "Reset" baseline.
    /// </summary>
    public static class VfStockCurveStore
    {
        private static string? _filePath;

        private static string FilePath
        {
            get
            {
                if (_filePath != null) return _filePath;
                var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UCU");
                Directory.CreateDirectory(appData);
                _filePath = Path.Combine(appData, "vf_stock.json");
                return _filePath;
            }
        }

        /// <summary>
        /// Loads the stock curve snapshot from disk, or returns null if no snapshot exists yet.
        /// </summary>
        public static List<VfStockPoint>? Load()
        {
            if (!File.Exists(FilePath))
                return null;

            try
            {
                var json = File.ReadAllText(FilePath);
                var points = JsonSerializer.Deserialize<List<VfStockPoint>>(json);
                return points;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Saves a list of V-F points as the stock baseline. Call this on first run
        /// to capture the current (unmodified) curve.
        /// </summary>
        public static void Save(List<VfStockPoint> points)
        {
            try
            {
                var json = JsonSerializer.Serialize(points, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(FilePath, json);
            }
            catch
            {
                // Silent fail — stock curve is in-memory only
            }
        }

        /// <summary>
        /// Deletes the stock snapshot file. Forces a re-capture on next LoadCurve call.
        /// </summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Delete(FilePath);
            }
            catch
            {
                // Stock curve cache file deletion is best-effort; failure is harmless.
            }
        }
    }
}
