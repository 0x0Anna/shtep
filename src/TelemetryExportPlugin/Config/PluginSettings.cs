using System.Collections.Generic;
using Newtonsoft.Json;

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
    /// Automatic: current behavior, sessions are opened/closed purely from
    /// CircuitBoundary/RallyBoundary's own pit-lane/stage heuristics.
    /// SimHubRecording: sessions instead follow SimHub's own record toggle
    /// (Plugin.cs's LoggingLastMessage edge-trigger latch - see its comments;
    /// this is a fragile string-matched signal, no confirmed boolean property
    /// exists in the SDK for "is SimHub currently recording").
    /// </summary>
    public enum RecordingTriggerMode
    {
        Automatic,
        SimHubRecording
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

        public RecordingTriggerMode RecordingTrigger { get; set; } = RecordingTriggerMode.Automatic;

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
        /// checklist UI later once channel availability per-sim is known. There's no
        /// settings-UI channel picker yet, so this hardcoded default is the only way
        /// a channel actually gets recorded.
        /// "Paused"/"Discontinuity" are not listed here - they're written to every
        /// row unconditionally regardless of this list (SCHEMA.md: always present).
        /// Limited to channels ChannelMap.cs actually confirmed against the
        /// installed GameReaderCommon.dll/ACSharedMemory.dll; see ChannelMap.cs's
        /// header comment and RawPhysicsAccessor.cs for what's still missing
        /// (PosX/Y/Z) and what's sim-gated (SteerRatio, SuspTravel*_mm -
        /// AssettoCorsaRally only; other sims just always report null for these,
        /// same as any other unsupported channel).
        ///
        /// [JsonProperty(ObjectCreationHandling = Replace)] documents the intent
        /// (stop plain Json.NET's default Auto behavior from appending deserialized
        /// items onto this non-null default list) and is verified correct against
        /// bare Newtonsoft.Json.JsonConvert.PopulateObject in isolation - but SimHub's
        /// actual ReadCommonSettings doesn't go through that path unchanged: this list
        /// kept growing on live restarts even with the attribute applied and deployed
        /// (confirmed via SimHub.txt's plugin-init timestamps - 97 entries became 117,
        /// exactly +20, the hardcoded default's own length, after one more restart).
        /// Whatever SimHub does internally for List{T} properties isn't something this
        /// repo can fix - see Plugin.Init()'s Settings.EnabledChannels.Distinct() call
        /// for the actual enforced fix, which doesn't depend on trusting that path.
        /// </summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
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
            "LatAccel_g",
            "LongAccel_g",
            "VertAccel_g",
            "ABSActive",
            "AirTemp_C",
            "TrackTemp_C",
            "LapDistancePct",
            "SteerRatio",
            "SuspTravelFL_mm",
            "SuspTravelFR_mm",
            "SuspTravelRL_mm",
            "SuspTravelRR_mm",
        };
    }
}