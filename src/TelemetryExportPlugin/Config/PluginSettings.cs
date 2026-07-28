using System.Collections.Generic;

namespace TelemetryExportPlugin.Config
{
    public enum DiscontinuityDetectionMode
    {
        SimEvent,
        Heuristic,
        Both
    }

    public enum RewindHandlingMode
    {
        Truncate,
        FlagOnly
    }

    /// <summary>
    /// Persisted plugin configuration. Serialized via SimHub's common settings
    /// JSON storage (see Plugin.Init/End) - keep this a plain POCO.
    /// </summary>
    public class PluginSettings
    {
        public string TempDir { get; set; } = "";

        public string OutputDir { get; set; } = "";

        public int SampleRateHz { get; set; } = 100;

        public bool PurgeIncompleteOnStartup { get; set; } = false;

        public int PitLaneDebounceMs { get; set; } = 1500;

        public DiscontinuityDetectionMode DiscontinuityDetection { get; set; } = DiscontinuityDetectionMode.Both;

        public int HeuristicDiscontinuitySpeedKmh { get; set; } = 400;

        public RewindHandlingMode RewindHandling { get; set; } = RewindHandlingMode.Truncate;

        /// <summary>
        /// Fixed list for v1, per PLUGIN_IMPLEMENTATION_PLAN.md - becomes a proper
        /// checklist UI later once channel availability per-sim is known.
        /// "Paused"/"Discontinuity" are not listed here - they're written to every
        /// row unconditionally regardless of this list (SCHEMA.md: always present).
        /// Limited to channels ChannelMap.cs actually confirmed against the
        /// installed GameReaderCommon.dll; see that file's header comment for the
        /// SCHEMA.md channels (SteerAngle_deg, PosX/Y/Z, SuspTravel*) that don't
        /// exist on the generic StatusDataBase and were deliberately left out.
        /// </summary>
        public List<string> EnabledChannels { get; set; } = new List<string>
        {
            "Speed_kmh",
            "RPM",
            "Gear",
            "Throttle_pct",
            "Brake_pct",
            "Clutch_pct",
            "LapDistance_m",
            "FuelLevel_pct",
        };
    }
}