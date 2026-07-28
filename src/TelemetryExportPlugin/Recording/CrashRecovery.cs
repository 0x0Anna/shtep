using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using TelemetryExportPlugin.Config;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Startup-only scan of TempDir for orphaned *.tsv.partial files left behind
    /// by a crash (SimHub crash, plugin exception, power loss). Never touches a
    /// .partial file actively being written this session - runs once, before any
    /// new recording opens. See SCHEMA.md "Crash recovery / orphaned .partial files".
    /// </summary>
    public static class CrashRecovery
    {
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        private static readonly Regex BaseNamePattern = new Regex(@"^(?<sim>[^_]+)_(?<context>.+)_(?<timestamp>\d{8}_\d{6})$");

        /// <param name="logError">
        /// Injected rather than calling SimHub.Logging directly, so this class
        /// (and its tests) have no SimHub/GameReaderCommon dependency - see
        /// PLUGIN_IMPLEMENTATION_PLAN.md "Testing notes". Plugin.cs passes
        /// SimHub.Logging.Current.Error; pass null to swallow silently (used by tests).
        /// </param>
        public static void RecoverAll(string tempDir, string outputDir, PluginSettings settings, string pluginVersion, Action<string> logError = null)
        {
            if (!Directory.Exists(tempDir)) return;

            foreach (var partialPath in Directory.GetFiles(tempDir, "*.tsv.partial"))
            {
                try
                {
                    if (settings.PurgeIncompleteOnStartup)
                    {
                        File.Delete(partialPath);
                        continue;
                    }

                    RecoverOne(partialPath, tempDir, outputDir, pluginVersion);
                }
                catch (Exception ex)
                {
                    logError?.Invoke($"TelemetryExportPlugin: crash recovery failed for '{partialPath}': {ex}");
                }
            }
        }

        private static void RecoverOne(string partialPath, string tempDir, string outputDir, string pluginVersion)
        {
            string baseName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(partialPath)); // strip .partial then .tsv
            string rawText = File.ReadAllText(partialPath, Utf8NoBom);

            // Every complete row is terminated with '\n' (RecordingSession.WriteLine
            // never omits it); a file crashed mid-write leaves its last line with no
            // trailing '\n' at all - the presence/absence of that terminator, not
            // column counting, is what actually tells complete from partial apart.
            bool endsCleanly = rawText.EndsWith("\n");
            var lines = rawText.Split('\n').Where(l => l.Length > 0).ToArray();
            int completeLineCount = endsCleanly ? lines.Length : lines.Length - 1;

            if (completeLineCount <= 0)
            {
                File.Delete(partialPath);
                return;
            }

            if (completeLineCount <= 1)
            {
                // Header only, no complete data rows - nothing worth salvaging.
                File.Delete(partialPath);
                return;
            }

            var completeLines = lines.Take(completeLineCount).ToArray();
            var header = completeLines[0].Split('\t');
            double lastTimeS = double.Parse(completeLines[completeLineCount - 1].Split('\t')[0], CultureInfo.InvariantCulture);

            // Rewrite file truncated to header + complete rows only.
            File.WriteAllText(partialPath, string.Join("\n", completeLines) + "\n", Utf8NoBom);

            var match = BaseNamePattern.Match(baseName);
            string sim = match.Success ? match.Groups["sim"].Value : "unknown";
            DateTime startUtc = match.Success && DateTime.TryParseExact(
                match.Groups["timestamp"].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed.ToUniversalTime()
                : DateTime.UtcNow;

            var sidecar = new RecordingSidecar
            {
                Sim = sim,
                // Not recoverable from the base filename (SCHEMA.md's
                // {sim}_{context}_{timestamp} convention has no sessionType
                // component). Defaults to "stint" since that's the only session
                // type real recordings currently produce - RallyBoundary has no
                // wired stage-start/stage-end signal yet (see project memory), so
                // no "stage" recording can exist to crash-recover in the first
                // place. Revisit if/when RallyBoundary is ever wired up.
                SessionType = "stint",
                Context = match.Success ? match.Groups["context"].Value : baseName,
                StartTimeUtc = startUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                EndTimeUtc = startUtc.AddSeconds(lastTimeS).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                Channels = header.Skip(1).ToList(),
                RecoveredFromCrash = true,
                PluginVersion = pluginVersion,
            };

            Directory.CreateDirectory(outputDir);
            var sidecarTempPath = Path.Combine(tempDir, $"{baseName}.meta.json");
            File.WriteAllText(sidecarTempPath, JsonConvert.SerializeObject(sidecar, Formatting.Indented), Utf8NoBom);

            var sidecarOutPath = Path.Combine(outputDir, $"{baseName}.meta.json");
            var dataOutPath = Path.Combine(outputDir, $"{baseName}.tsv");
            MoveAcrossVolumes(sidecarTempPath, sidecarOutPath);
            MoveAcrossVolumes(partialPath, dataOutPath);
        }

        private static void MoveAcrossVolumes(string source, string destination)
        {
            try
            {
                File.Move(source, destination);
            }
            catch (IOException)
            {
                File.Copy(source, destination, overwrite: true);
                File.Delete(source);
            }
        }
    }
}
