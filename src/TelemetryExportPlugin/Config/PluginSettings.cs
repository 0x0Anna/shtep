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
        /// Post-processing step: after a recording's .tsv/.meta.json pair lands in
        /// OutputDir, also write a MoTeC .ld file from it (see Export/MotecExporter.cs).
        /// Off by default - shtep's primary output is still the TSV/JSON pair;
        /// this is an opt-in convenience for using shtep standalone, without the
        /// shakedown-engineer companion converter. Runs after Close(), not on the
        /// live write path, so it never affects recording itself.
        /// </summary>
        public bool ExportMotecLd { get; set; } = false;

        /// <summary>
        /// Destination directory for generated .ld files. Empty means "same as
        /// OutputDir". Independently configurable since some users may want .ld
        /// files routed straight into i2's watched folder while keeping raw
        /// TSV/JSON elsewhere.
        /// </summary>
        public string MotecOutputDir { get; set; } = "";

        /// <summary>
        /// Throttled raw-channel dump to SimHub's log (not the recorded TSV) for
        /// verifying ChannelMap accessors against a new sim's real telemetry
        /// without attaching a debugger. Off by default - only turn on while
        /// bringing up a new sim adapter (PLUGIN_IMPLEMENTATION_PLAN.md step 9).
        /// </summary>
        public bool VerboseDiagnosticLogging { get; set; } = false;

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