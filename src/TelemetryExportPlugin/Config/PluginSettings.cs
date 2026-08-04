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
        /// header comment and RawPhysicsAccessor.cs for what's sim-gated
        /// (SteerRatio, SuspTravel*_mm, and most _raw-suffixed channels below -
        /// AssettoCorsaRally only; other sims just always report null for these,
        /// same as any other unsupported channel). CarPosX/Y/Z_raw and
        /// CarRelPosX/Y/Z_raw are a provisional first attempt at the position gap
        /// this list lacked since v1 - array length/axis order/frame unconfirmed,
        /// see ChannelMap.cs's comment on those entries.
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
            "Handbrake_pct",
            "LapDistance_m",
            "FuelLevel_pct",
            "LatAccel_g",
            "LongAccel_g",
            "VertAccel_g",
            "ABSActive",
            "TCActive",
            "AirTemp_C",
            "TrackTemp_C",
            "TyreTempFL_C",
            "TyreTempFR_C",
            "TyreTempRL_C",
            "TyreTempRR_C",
            "TyreTempFL_Inner_C",
            "TyreTempFL_Middle_C",
            "TyreTempFL_Outer_C",
            "TyreTempFR_Inner_C",
            "TyreTempFR_Middle_C",
            "TyreTempFR_Outer_C",
            "TyreTempRL_Inner_C",
            "TyreTempRL_Middle_C",
            "TyreTempRL_Outer_C",
            "TyreTempRR_Inner_C",
            "TyreTempRR_Middle_C",
            "TyreTempRR_Outer_C",
            "CarPosX_raw",
            "CarPosY_raw",
            "CarPosZ_raw",
            "CarRelPosX_raw",
            "CarRelPosY_raw",
            "CarRelPosZ_raw",
            "OrientationYaw_raw",
            "OrientationPitch_raw",
            "OrientationRoll_raw",
            "YawRate_raw",
            "PitchRate_raw",
            "RollRate_raw",
            "TyrePressureFL_raw",
            "TyrePressureFR_raw",
            "TyrePressureRL_raw",
            "TyrePressureRR_raw",
            "TyreWearFL_raw",
            "TyreWearFR_raw",
            "TyreWearRL_raw",
            "TyreWearRR_raw",
            "TyreDirtFL_raw",
            "TyreDirtFR_raw",
            "TyreDirtRL_raw",
            "TyreDirtRR_raw",
            "BrakeTempFL_C",
            "BrakeTempFR_C",
            "BrakeTempRL_C",
            "BrakeTempRR_C",
            "EngineTorque_raw",
            "OilPressure_raw",
            "OilTemp_C",
            "WaterTemp_C",
            "TurboBar_raw",
            "BrakeBias_raw",
            "PitLimiterOn",
            "LapDistancePct",
            "SteerRatio",
            "SuspTravelFL_mm",
            "SuspTravelFR_mm",
            "SuspTravelRL_mm",
            "SuspTravelRR_mm",
            "WheelLoadFL_N",
            "WheelLoadFR_N",
            "WheelLoadRL_N",
            "WheelLoadRR_N",
            "WheelAngularSpeedFL_raw",
            "WheelAngularSpeedFR_raw",
            "WheelAngularSpeedRL_raw",
            "WheelAngularSpeedRR_raw",
            "WheelPressureFL_raw",
            "WheelPressureFR_raw",
            "WheelPressureRL_raw",
            "WheelPressureRR_raw",
            "SlipAngleFL_raw",
            "SlipAngleFR_raw",
            "SlipAngleRL_raw",
            "SlipAngleRR_raw",
            "SlipRatioFL",
            "SlipRatioFR",
            "SlipRatioRL",
            "SlipRatioRR",
            "BrakePressureFL_raw",
            "BrakePressureFR_raw",
            "BrakePressureRL_raw",
            "BrakePressureRR_raw",
            "CamberFL_raw",
            "CamberFR_raw",
            "CamberRL_raw",
            "CamberRR_raw",
            "SuspDamageFL_raw",
            "SuspDamageFR_raw",
            "SuspDamageRL_raw",
            "SuspDamageRR_raw",
            "TyresOutCount",
            "DiscLifeFL_raw",
            "DiscLifeFR_raw",
            "DiscLifeRL_raw",
            "DiscLifeRR_raw",
            "PadLifeFL_raw",
            "PadLifeFR_raw",
            "PadLifeRL_raw",
            "PadLifeRR_raw",
            "TyreForceFxFL_raw",
            "TyreForceFxFR_raw",
            "TyreForceFxRL_raw",
            "TyreForceFxRR_raw",
            "TyreForceFyFL_raw",
            "TyreForceFyFR_raw",
            "TyreForceFyRL_raw",
            "TyreForceFyRR_raw",
            "TyreMomentMzFL_raw",
            "TyreMomentMzFR_raw",
            "TyreMomentMzRL_raw",
            "TyreMomentMzRR_raw",
            "LocalVelocityX_raw",
            "LocalVelocityY_raw",
            "LocalVelocityZ_raw",
        };
    }
}