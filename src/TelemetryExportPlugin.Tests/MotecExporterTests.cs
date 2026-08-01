using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using TelemetryExportPlugin.Export;
using TelemetryExportPlugin.Recording;

namespace TelemetryExportPlugin.Tests
{
    public class MotecExporterTests : IDisposable
    {
        private const int HeaderPtr = 11336;

        private readonly string _dir;

        public MotecExporterTests()
        {
            _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "tep_tests_motec_export_" + Guid.NewGuid())).FullName;
        }

        public void Dispose()
        {
            Directory.Delete(_dir, recursive: true);
        }

        [Fact]
        public void Export_ReadsFinishedTsvPair_WritesLdWithMatchingChannelsAndSampleCounts()
        {
            string baseName = "fh6_greecetest_20260727_120000";
            string tsvPath = Path.Combine(_dir, $"{baseName}.tsv");
            File.WriteAllText(tsvPath,
                "Time_s\tPaused\tDiscontinuity\tSpeed_kmh\tRPM\n" +
                "0.000\t0\t0\t0\t900\n" +
                "0.010\t0\t0\t12.5\t1200\n" +
                "0.020\t0\t0\t35.2\t2400\n",
                new UTF8Encoding(false));

            var sidecar = new RecordingSidecar
            {
                Sim = "FH6",
                SessionType = "stint",
                Context = "Greece Test",
                Car = "Car_2038",
                Driver = "Annalise",
                StartTimeUtc = "2026-07-27T12:00:00Z",
                EndTimeUtc = "2026-07-27T12:00:05Z",
                SampleRateHz = 100,
                Channels = new List<string> { "Paused", "Discontinuity", "Speed_kmh", "RPM" },
                PluginVersion = "0.1.0",
            };

            string ldPath = MotecExporter.Export(tsvPath, sidecar, _dir, baseName);

            Assert.Equal(Path.Combine(_dir, $"{baseName}.ld"), ldPath);
            Assert.True(File.Exists(ldPath));

            using (var stream = new FileStream(ldPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                stream.Seek(8, SeekOrigin.Begin); // past marker(I) + pad(4x)
                uint metaPtr = reader.ReadUInt32();
                reader.ReadUInt32(); // dataPtr
                reader.ReadBytes(20); // pad
                reader.ReadUInt32(); // auxPtr
                reader.ReadBytes(24); // pad
                reader.ReadBytes(6); // HHH
                reader.ReadUInt32(); // device serial
                reader.ReadBytes(8); // device type
                reader.ReadUInt16(); // device version
                reader.ReadUInt16(); // unknown
                uint numChanns = reader.ReadUInt32();
                Assert.Equal((uint)4, numChanns); // Paused, Discontinuity, Speed_kmh, RPM

                stream.Seek(metaPtr, SeekOrigin.Begin);
                reader.ReadUInt32(); // prev
                reader.ReadUInt32(); // next
                reader.ReadUInt32(); // data_ptr
                uint len0 = reader.ReadUInt32();
                Assert.Equal((uint)3, len0); // 3 data rows in the fixture
            }
        }

        [Fact]
        public void Export_RowWithMissingCell_HoldsLastKnownValueInsteadOfThrowing()
        {
            string baseName = "fh6_greecetest_20260727_130000";
            string tsvPath = Path.Combine(_dir, $"{baseName}.tsv");
            // Speed_kmh is empty on the second row - a channel absent from
            // SampleTimer's held state for that tick (see RecordingSession.WriteRow).
            File.WriteAllText(tsvPath,
                "Time_s\tSpeed_kmh\tRPM\n" +
                "0.000\t10\t900\n" +
                "0.010\t\t1200\n" +
                "0.020\t35.2\t2400\n",
                new UTF8Encoding(false));

            var sidecar = new RecordingSidecar
            {
                Sim = "FH6",
                SessionType = "stint",
                Context = "Greece Test",
                SampleRateHz = 100,
                Channels = new List<string> { "Speed_kmh", "RPM" },
                PluginVersion = "0.1.0",
            };

            string ldPath = MotecExporter.Export(tsvPath, sidecar, _dir, baseName);
            Assert.True(File.Exists(ldPath));

            using (var stream = new FileStream(ldPath, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                stream.Seek(8, SeekOrigin.Begin);
                uint metaPtr = reader.ReadUInt32();

                stream.Seek(metaPtr, SeekOrigin.Begin);
                reader.ReadUInt32(); // prev
                reader.ReadUInt32(); // next
                uint dataPtr0 = reader.ReadUInt32();

                stream.Seek(dataPtr0, SeekOrigin.Begin);
                Assert.Equal(10.0f, reader.ReadSingle());
                Assert.Equal(10.0f, reader.ReadSingle()); // held from row 0, not a parse failure
                Assert.Equal(35.2f, reader.ReadSingle());
            }
        }

        [Fact]
        public void Export_CircuitStintWithLapChanges_WritesLdxWithMarkerPerTransition()
        {
            string baseName = "fh6_greecetest_20260727_140000";
            string tsvPath = Path.Combine(_dir, $"{baseName}.tsv");
            // LapNumber goes 1 -> 1 -> 2 -> 2 -> 3: two transitions, at t=0.020 and t=0.030.
            File.WriteAllText(tsvPath,
                "Time_s\tSpeed_kmh\tLapNumber\n" +
                "0.000\t10\t1\n" +
                "0.010\t20\t1\n" +
                "0.020\t30\t2\n" +
                "0.030\t40\t3\n",
                new UTF8Encoding(false));

            var sidecar = new RecordingSidecar
            {
                Sim = "FH6",
                SessionType = "stint",
                Context = "Greece Test",
                SampleRateHz = 100,
                Channels = new List<string> { "Speed_kmh", "LapNumber" },
                PluginVersion = "0.1.0",
            };

            MotecExporter.Export(tsvPath, sidecar, _dir, baseName);

            string ldxPath = Path.Combine(_dir, $"{baseName}.ldx");
            Assert.True(File.Exists(ldxPath));

            var doc = System.Xml.Linq.XDocument.Load(ldxPath);
            var markers = System.Linq.Enumerable.ToList(doc.Descendants("Marker"));
            Assert.Equal(2, markers.Count);

            var times = System.Linq.Enumerable.Select(markers,
                m => double.Parse(m.Attribute("Time").Value, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(new double[] { 20_000, 30_000 }, times);
        }

        [Fact]
        public void Export_RallyStage_NeverWritesLdxEvenIfLapNumberColumnPresent()
        {
            string baseName = "rbr_stage1_20260727_150000";
            string tsvPath = Path.Combine(_dir, $"{baseName}.tsv");
            File.WriteAllText(tsvPath,
                "Time_s\tSpeed_kmh\tLapNumber\n" +
                "0.000\t10\t1\n" +
                "0.010\t20\t2\n",
                new UTF8Encoding(false));

            var sidecar = new RecordingSidecar
            {
                Sim = "rbr",
                SessionType = "stage", // rally - no lap concept, per SCHEMA.md
                Context = "Stage 1",
                SampleRateHz = 100,
                Channels = new List<string> { "Speed_kmh", "LapNumber" },
                PluginVersion = "0.1.0",
            };

            MotecExporter.Export(tsvPath, sidecar, _dir, baseName);

            Assert.False(File.Exists(Path.Combine(_dir, $"{baseName}.ldx")));
        }

        [Fact]
        public void Export_CircuitStintWithoutLapNumberColumn_DoesNotWriteLdx()
        {
            string baseName = "fh6_greecetest_20260727_160000";
            string tsvPath = Path.Combine(_dir, $"{baseName}.tsv");
            File.WriteAllText(tsvPath,
                "Time_s\tSpeed_kmh\n" +
                "0.000\t10\n" +
                "0.010\t20\n",
                new UTF8Encoding(false));

            var sidecar = new RecordingSidecar
            {
                Sim = "FH6",
                SessionType = "stint",
                Context = "Greece Test",
                SampleRateHz = 100,
                Channels = new List<string> { "Speed_kmh" },
                PluginVersion = "0.1.0",
            };

            MotecExporter.Export(tsvPath, sidecar, _dir, baseName);

            Assert.False(File.Exists(Path.Combine(_dir, $"{baseName}.ldx")));
        }

        [Fact]
        public void Export_EmptyTsv_Throws()
        {
            string baseName = "empty_20260727_120000";
            string tsvPath = Path.Combine(_dir, $"{baseName}.tsv");
            File.WriteAllText(tsvPath, "", new UTF8Encoding(false));

            var sidecar = new RecordingSidecar { SampleRateHz = 100, Channels = new List<string>() };

            Assert.Throws<InvalidOperationException>(() => MotecExporter.Export(tsvPath, sidecar, _dir, baseName));
        }
    }
}
