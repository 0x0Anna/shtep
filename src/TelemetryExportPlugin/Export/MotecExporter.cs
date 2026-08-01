using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Export
{
    /// <summary>
    /// Optional post-processor: converts a completed {base}.tsv + {base}.meta.json
    /// pair (per SCHEMA.md) into a MoTeC {base}.ld file, so shtep can be used
    /// standalone without the shakedown-engineer companion converter.
    ///
    /// Runs strictly after RecordingSession.Close() has moved both files into
    /// OutputDir - this reads the finished pair back rather than hooking into the
    /// live write path, keeping the .ld/MoTeC-specific logic entirely out of the
    /// hot recording path (RecordingSession, SampleTimer, RewindIndex, etc. have
    /// no knowledge this exists).
    /// </summary>
    public static class MotecExporter
    {
        public static string Export(string tsvPath, RecordingSidecar sidecar, string outputDir, string baseName)
        {
            var lines = File.ReadAllLines(tsvPath, new UTF8Encoding(false));
            if (lines.Length == 0)
            {
                throw new InvalidOperationException($"TSV file is empty: {tsvPath}");
            }

            var headerCols = lines[0].Split('\t');
            var channelNames = headerCols.Skip(1).ToArray(); // drop Time_s
            var values = new List<double>[channelNames.Length];
            var lastKnown = new double[channelNames.Length];
            for (int c = 0; c < values.Length; c++) values[c] = new List<double>();

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                var cells = lines[i].Split('\t');
                for (int c = 0; c < channelNames.Length; c++)
                {
                    // A missing/empty cell means "no value this row" (see
                    // RecordingSession.WriteRow) - hold the last known value
                    // rather than letting double.Parse throw and losing the
                    // whole .ld export over one gap in one channel.
                    string cell = c + 1 < cells.Length ? cells[c + 1] : "";
                    double value = string.IsNullOrEmpty(cell)
                        ? lastKnown[c]
                        : double.Parse(cell, CultureInfo.InvariantCulture);
                    lastKnown[c] = value;
                    values[c].Add(value);
                }
            }

            var channels = channelNames
                .Select((name, i) => new MotecChannel(name, "", values[i]))
                .ToList();

            var session = new MotecSessionInfo
            {
                Driver = sidecar.Driver ?? "",
                VehicleId = sidecar.Car ?? "",
                Venue = sidecar.Context ?? "",
                EventName = sidecar.Context ?? "",
                EventSession = sidecar.SessionType ?? "",
                ShortComment = string.IsNullOrEmpty(sidecar.Sim) ? "shtep" : $"{sidecar.Sim} via shtep",
                LongComment = BuildLongComment(sidecar),
                Timestamp = ParseTimestamp(sidecar.StartTimeUtc),
            };

            Directory.CreateDirectory(outputDir);
            string ldPath = Path.Combine(outputDir, $"{baseName}.ld");
            MotecLdWriter.Write(ldPath, session, channels, sidecar.SampleRateHz);
            return ldPath;
        }

        private static string BuildLongComment(RecordingSidecar sidecar)
        {
            var parts = new List<string>();
            if (sidecar.Discontinuities != null && sidecar.Discontinuities.Count > 0)
            {
                parts.Add($"{sidecar.Discontinuities.Count} discontinuit{(sidecar.Discontinuities.Count == 1 ? "y" : "ies")}");
            }
            if (sidecar.Rewinds != null && sidecar.Rewinds.Count > 0)
            {
                parts.Add($"{sidecar.Rewinds.Count} rewind{(sidecar.Rewinds.Count == 1 ? "" : "s")}");
            }
            return parts.Count == 0 ? "" : string.Join(", ", parts);
        }

        private static DateTime ParseTimestamp(string isoUtc)
        {
            if (!string.IsNullOrEmpty(isoUtc) &&
                DateTime.TryParse(isoUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                return dt;
            }
            return DateTime.Now;
        }
    }
}
