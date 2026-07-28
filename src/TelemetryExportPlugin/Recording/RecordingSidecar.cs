using System.Collections.Generic;
using Newtonsoft.Json;

namespace TelemetryExportPlugin.Recording
{
    public class DiscontinuityEntry
    {
        [JsonProperty("startTimeS")]
        public double StartTimeS { get; set; }

        [JsonProperty("endTimeS")]
        public double EndTimeS { get; set; }

        /// <summary>One of "reset", "assist", "fast_travel", "unknown" (SCHEMA.md).</summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }
    }

    public class RewindEntry
    {
        [JsonProperty("truncatedFromTimeS")]
        public double TruncatedFromTimeS { get; set; }

        [JsonProperty("truncatedToTimeS")]
        public double TruncatedToTimeS { get; set; }

        [JsonProperty("rowsRemoved")]
        public int RowsRemoved { get; set; }
    }

    /// <summary>
    /// Maps 1:1 to the `{base}.meta.json` sidecar defined in SCHEMA.md.
    /// schemaVersion is the compat contract with the Rust converter - bump only
    /// for changes to TSV column semantics or file lifecycle, never for
    /// additive channel-table rows.
    /// </summary>
    public class RecordingSidecar
    {
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonProperty("sim")]
        public string Sim { get; set; }

        /// <summary>"stage" (rally) or "stint" (circuit).</summary>
        [JsonProperty("sessionType")]
        public string SessionType { get; set; }

        [JsonProperty("context")]
        public string Context { get; set; }

        [JsonProperty("car")]
        public string Car { get; set; }

        [JsonProperty("driver")]
        public string Driver { get; set; }

        [JsonProperty("startTimeUtc")]
        public string StartTimeUtc { get; set; }

        [JsonProperty("endTimeUtc")]
        public string EndTimeUtc { get; set; }

        [JsonProperty("sampleRateHz")]
        public int SampleRateHz { get; set; }

        [JsonProperty("channels")]
        public List<string> Channels { get; set; } = new List<string>();

        [JsonProperty("discontinuities", NullValueHandling = NullValueHandling.Ignore)]
        public List<DiscontinuityEntry> Discontinuities { get; set; }

        [JsonProperty("rewinds", NullValueHandling = NullValueHandling.Ignore)]
        public List<RewindEntry> Rewinds { get; set; }

        [JsonProperty("pluginVersion")]
        public string PluginVersion { get; set; }

        [JsonProperty("recoveredFromCrash", NullValueHandling = NullValueHandling.Ignore)]
        public bool? RecoveredFromCrash { get; set; }
    }
}
