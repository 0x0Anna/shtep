using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Owns the file lifecycle for one recording (one rally stage or one circuit
    /// stint): {TempDir}/{base}.tsv.partial -> sidecar written -> sidecar moved
    /// first -> {OutputDir}/{base}.tsv moved last. See SCHEMA.md "Write lifecycle".
    ///
    /// Not thread-safe; callers (SampleTimer's tick, RewindIndex's truncate path)
    /// must serialize access to a single instance.
    /// </summary>
    public class RecordingSession : IDisposable
    {
        // No BOM, per SCHEMA.md "Encoding: UTF-8, no BOM".
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        public string TempDir { get; }
        public string OutputDir { get; }
        public string BaseName { get; }
        public IReadOnlyList<string> Channels { get; }

        public string PartialPath { get; }
        private FileStream _stream;

        // Always present regardless of the configurable Channels list, per
        // SCHEMA.md: "Paused"/"Discontinuity" columns are never conditional on
        // channel selection. SampleTimer.Tick() always sets both keys in the
        // values dict passed to WriteRow, so this is just declaring them as
        // fixed leading columns rather than leaving them to the caller-supplied
        // Channels list (which deliberately excludes them - see PluginSettings.cs).
        private static readonly string[] FixedColumns = { "Paused", "Discontinuity" };

        public bool IsOpen => _stream != null;

        /// <summary>Byte offset the writer is currently positioned at (after the last flushed row).</summary>
        public long CurrentByteOffset => _stream?.Position ?? 0;

        public RecordingSession(string tempDir, string outputDir, string baseName, IReadOnlyList<string> channels)
        {
            TempDir = tempDir;
            OutputDir = outputDir;
            BaseName = baseName;
            Channels = channels;
            PartialPath = Path.Combine(TempDir, $"{BaseName}.tsv.partial");
        }

        public void Open()
        {
            if (IsOpen) throw new InvalidOperationException("Session already open.");

            Directory.CreateDirectory(TempDir);
            _stream = new FileStream(PartialPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            WriteLine(string.Join("\t", new[] { "Time_s" }.Concat(FixedColumns).Concat(Channels)));
        }

        public void WriteRow(double timeS, IReadOnlyDictionary<string, double> values)
        {
            if (!IsOpen) throw new InvalidOperationException("Session not open.");

            var sb = new StringBuilder();
            sb.Append(timeS.ToString("0.000", CultureInfo.InvariantCulture));
            foreach (var channel in FixedColumns.Concat(Channels))
            {
                sb.Append('\t');
                if (values.TryGetValue(channel, out var v))
                {
                    sb.Append(NumberFormat.FormatValue(v));
                }
                // Channel present in the file's column set but not available this
                // tick shouldn't happen in steady state (SampleTimer holds last
                // known value) - if it does, this would misalign columns; leave
                // it visible rather than silently padding, per "no sentinel" rule
                // upstream in SCHEMA.md (this is a hold-value bug, not a schema case).
            }
            WriteLine(sb.ToString());
        }

        private void WriteLine(string line)
        {
            var bytes = Utf8NoBom.GetBytes(line + "\n");
            _stream.Write(bytes, 0, bytes.Length);
        }

        public void Flush() => _stream?.Flush();

        /// <summary>
        /// Truncates the currently-open data file to the given byte offset and
        /// repositions the writer there, for RewindIndex's truncate-and-resume.
        /// </summary>
        public void TruncateTo(long byteOffset)
        {
            if (!IsOpen) throw new InvalidOperationException("Session not open.");
            _stream.Flush();
            _stream.SetLength(byteOffset);
            _stream.Seek(byteOffset, SeekOrigin.Begin);
        }

        /// <summary>
        /// Closes the data file, writes the sidecar, then moves sidecar-first,
        /// data-file-last into OutputDir - per SCHEMA.md this ordering is the
        /// converter-facing contract, not an implementation detail.
        /// </summary>
        public void Close(RecordingSidecar sidecar)
        {
            if (!IsOpen) throw new InvalidOperationException("Session not open.");

            _stream.Flush();
            _stream.Dispose();
            _stream = null;

            Directory.CreateDirectory(OutputDir);

            var sidecarTempPath = Path.Combine(TempDir, $"{BaseName}.meta.json");
            var sidecarJson = JsonConvert.SerializeObject(sidecar, Formatting.Indented);
            File.WriteAllText(sidecarTempPath, sidecarJson, Utf8NoBom);

            var sidecarOutPath = Path.Combine(OutputDir, $"{BaseName}.meta.json");
            var dataOutPath = Path.Combine(OutputDir, $"{BaseName}.tsv");

            MoveAcrossVolumes(sidecarTempPath, sidecarOutPath);
            MoveAcrossVolumes(PartialPath, dataOutPath);
        }

        /// <summary>
        /// File.Move throws across volumes on some .NET Framework versions; copy
        /// then delete covers that case uniformly (SCHEMA.md allows this explicitly
        /// when TempDir/OutputDir are on different volumes).
        /// </summary>
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

        /// <summary>
        /// Closes and deletes the in-progress .tsv.partial without writing a
        /// sidecar or moving anything into OutputDir - for a session too short to
        /// be worth keeping (see PluginSettings.MinSessionDurationS).
        /// </summary>
        public void Discard()
        {
            if (!IsOpen) throw new InvalidOperationException("Session not open.");

            _stream.Dispose();
            _stream = null;

            if (File.Exists(PartialPath))
            {
                File.Delete(PartialPath);
            }
        }

        public void Dispose()
        {
            _stream?.Dispose();
            _stream = null;
        }
    }
}
