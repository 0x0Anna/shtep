using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TelemetryExportPlugin.Export
{
    /// <summary>
    /// One iRacing telemetry variable's definition, as it appears in an .ibt
    /// var-header record. Name/type/unit/desc are copied verbatim from a genuine
    /// iRacing capture - never invented - so a consumer sees exactly the strings
    /// iRacing itself would emit.
    /// </summary>
    public sealed class IbtVar
    {
        /// <summary>Index into the .ibt type table: 1=bool, 2=int32, 4=float32, 5=float64.</summary>
        public int Type { get; }
        public string Name { get; }
        public string Unit { get; }
        public string Desc { get; }

        public IbtVar(string name, int type, string unit, string desc)
        {
            Name = name;
            Type = type;
            Unit = unit ?? "";
            Desc = desc ?? "";
        }

        public int Size
        {
            get
            {
                switch (Type)
                {
                    case 0: return 1;  // char
                    case 1: return 1;  // bool
                    case 2: return 4;  // int32
                    case 3: return 4;  // bitfield
                    case 4: return 4;  // float32
                    case 5: return 8;  // float64
                    default: throw new NotSupportedException($"Unknown .ibt var type {Type}");
                }
            }
        }
    }

    /// <summary>One variable plus the per-sample values to write for it.</summary>
    public sealed class IbtChannel
    {
        public IbtVar Var { get; }
        public IReadOnlyList<double> Values { get; }

        public IbtChannel(IbtVar v, IReadOnlyList<double> values)
        {
            Var = v;
            Values = values;
        }
    }

    /// <summary>Session metadata that lands in the trailing YAML block.</summary>
    public sealed class IbtSessionInfo
    {
        public string TrackName { get; set; } = "Unknown";
        public string TrackConfigName { get; set; } = "";
        public string CarName { get; set; } = "Unknown";
        public string DriverName { get; set; } = "Me";
        public double TrackLengthM { get; set; }
        public double EstLapTimeS { get; set; }
        public int LapCount { get; set; }
        public int GearCountForward { get; set; } = 6;
        public DateTime StartTimeUtc { get; set; } = DateTime.UtcNow;
        public double DurationS { get; set; }
    }

    /// <summary>
    /// Writes iRacing .ibt telemetry files, which Cosworth Pi Toolbox imports
    /// natively (Import -> iRacing). See PI_TOOLBOX_EXPORT.md for the full
    /// research record; the essentials:
    ///
    /// Layout is three regions back-to-back with no gaps:
    ///     header(144) -> var headers(144 each) -> session YAML -> sample buffer
    ///
    /// Two non-obvious constraints, both established by testing against the real
    /// Pi Toolbox importer rather than from documentation:
    ///
    /// 1. A minimal var table is REJECTED. A 13-channel file produced no telemetry
    ///    at all - not merely missing metadata. The importer
    ///    (Pi.Research.Toolbox.TelemetryConverter.iRacing.dll) references 18
    ///    channels by name; those must be present even when we have no data for
    ///    them, hence the zero-filled placeholders in IbtChannelSet.
    ///
    /// 2. Variables whose unit is exactly "%" are stored as 0..1 RATIOS, not
    ///    0-100. The caller is responsible for that conversion; this writer emits
    ///    values as given.
    ///
    /// The YAML must satisfy the key contract the importer navigates, and uses
    /// iRacing's own dialect: CRLF line endings, --- / ... document wrappers,
    /// single-space indentation.
    /// </summary>
    public static class IbtWriter
    {
        private const int HeaderSize = 144;
        private const int VarHeaderSize = 144;
        private const int VarHeaderOffset = 144;

        public static void Write(string path, IbtSessionInfo session,
            IReadOnlyList<IbtChannel> channels, int tickRateHz)
        {
            if (channels == null || channels.Count == 0)
            {
                throw new ArgumentException("At least one channel is required", nameof(channels));
            }

            int recordCount = channels[0].Values.Count;
            for (int i = 1; i < channels.Count; i++)
            {
                if (channels[i].Values.Count != recordCount)
                {
                    throw new ArgumentException(
                        $"Channel '{channels[i].Var.Name}' has {channels[i].Values.Count} samples, " +
                        $"expected {recordCount} to match '{channels[0].Var.Name}'");
                }
            }

            // Lay out each variable's offset within a sample record, widest first
            // so every value lands on its natural alignment boundary.
            var ordered = new List<IbtChannel>(channels);
            ordered.Sort((a, b) => b.Var.Size.CompareTo(a.Var.Size));

            var offsets = new int[ordered.Count];
            int cursor = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                int size = ordered[i].Var.Size;
                if (cursor % size != 0) cursor += size - (cursor % size);
                offsets[i] = cursor;
                cursor += size;
            }
            int stride = cursor + ((8 - cursor % 8) % 8);

            byte[] yaml = Encoding.UTF8.GetBytes(BuildSessionYaml(session));
            int sessionInfoOffset = VarHeaderOffset + ordered.Count * VarHeaderSize;
            int bufferOffset = sessionInfoOffset + yaml.Length;

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, session, ordered.Count, stride, tickRateHz,
                    yaml.Length, sessionInfoOffset, bufferOffset, recordCount);

                for (int i = 0; i < ordered.Count; i++)
                {
                    WriteVarHeader(writer, ordered[i].Var, offsets[i]);
                }

                writer.Write(yaml);
                writer.Write(BuildSampleBuffer(ordered, offsets, stride, recordCount));
            }
        }

        private static void WriteHeader(BinaryWriter w, IbtSessionInfo session, int numVars,
            int stride, int tickRateHz, int yamlLength, int sessionInfoOffset,
            int bufferOffset, int recordCount)
        {
            w.Write(2);                     // +0   ver
            w.Write(1);                     // +4   status
            w.Write(tickRateHz);            // +8   tick_rate
            w.Write(0);                     // +12  session_info_update
            w.Write(yamlLength);            // +16  session_info_len
            w.Write(sessionInfoOffset);     // +20  session_info_offset
            w.Write(numVars);               // +24  num_vars
            w.Write(VarHeaderOffset);       // +28  var_header_offset
            w.Write(1);                     // +32  num_buf
            w.Write(stride);                // +36  buf_len
            w.Write(0); w.Write(0);         // +40  pad

            // +48 varBuf[4]; only slot 0 is ever used.
            w.Write(recordCount);           //      tick_count
            w.Write(bufferOffset);          //      buf_offset
            w.Write(0); w.Write(0);         //      pad
            for (int i = 0; i < 3 * 4; i++) w.Write(0);

            // +112 irsdk_diskSubHeader. session_start_date surfaces as the outing's
            // "Create date" in Pi Toolbox, so it must reflect the real session.
            w.Write((uint)ToUnixSeconds(session.StartTimeUtc));  // +112
            w.Write(0u);                                          // +116 pad
            w.Write(0.0);                                         // +120 session_start_time
            w.Write(session.DurationS);                           // +128 session_end_time
            w.Write(session.LapCount);                            // +136 session_lap_count
            w.Write(recordCount);                                 // +140 session_record_count
        }

        private static void WriteVarHeader(BinaryWriter w, IbtVar v, int offset)
        {
            w.Write(v.Type);
            w.Write(offset);
            w.Write(1);                     // count - scalars only
            w.Write((byte)0);               // count_as_time
            w.Write(new byte[3]);           // pad
            w.Write(FixedAscii(v.Name, 32));
            w.Write(FixedAscii(v.Desc, 64));
            w.Write(FixedAscii(v.Unit, 32));
        }

        private static byte[] BuildSampleBuffer(List<IbtChannel> channels, int[] offsets,
            int stride, int recordCount)
        {
            var buffer = new byte[(long)stride * recordCount <= int.MaxValue
                ? stride * recordCount
                : throw new InvalidOperationException("Session too large for a single .ibt buffer")];

            for (int c = 0; c < channels.Count; c++)
            {
                var channel = channels[c];
                int offset = offsets[c];
                int type = channel.Var.Type;
                var values = channel.Values;

                for (int r = 0; r < recordCount; r++)
                {
                    int at = r * stride + offset;
                    double value = values[r];
                    switch (type)
                    {
                        case 1:
                            buffer[at] = (byte)(value != 0 ? 1 : 0);
                            break;
                        case 2:
                        case 3:
                            WriteInt32(buffer, at, (int)Math.Round(value));
                            break;
                        case 4:
                            WriteBytes(buffer, at, BitConverter.GetBytes((float)value));
                            break;
                        case 5:
                            WriteBytes(buffer, at, BitConverter.GetBytes(value));
                            break;
                        default:
                            throw new NotSupportedException($"Unsupported .ibt var type {type}");
                    }
                }
            }
            return buffer;
        }

        private static void WriteInt32(byte[] target, int at, int value)
        {
            WriteBytes(target, at, BitConverter.GetBytes(value));
        }

        private static void WriteBytes(byte[] target, int at, byte[] source)
        {
            Buffer.BlockCopy(source, 0, target, at, source.Length);
        }

        /// <summary>
        /// Fixed-width, NUL-padded ASCII. Over-long values are truncated to fit
        /// rather than throwing - a too-long car name shouldn't fail an export.
        /// </summary>
        private static byte[] FixedAscii(string value, int width)
        {
            var bytes = new byte[width];
            if (string.IsNullOrEmpty(value)) return bytes;

            var encoded = Encoding.ASCII.GetBytes(value);
            int length = Math.Min(encoded.Length, width - 1);
            Buffer.BlockCopy(encoded, 0, bytes, 0, length);
            return bytes;
        }

        private static long ToUnixSeconds(DateTime utc)
        {
            var epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            double seconds = (utc.ToUniversalTime() - epoch).TotalSeconds;
            return seconds < 0 ? 0 : (long)seconds;
        }

        /// <summary>
        /// Every key here is one the real importer navigates - established by
        /// extracting the colon-delimited paths from its string table
        /// (e.g. "DriverInfo:Drivers:%ld:CarScreenName"). A file missing them is
        /// rejected with "This dataset contains invalid session properties".
        /// The driver is selected via DriverInfo:DriverCarIdx.
        ///
        /// CarSetup:InCarSystems:GearRatios is deliberately omitted: genuine
        /// captures load without it, so it is not required.
        /// </summary>
        internal static string BuildSessionYaml(IbtSessionInfo s)
        {
            string track = Sanitise(s.TrackName);
            string car = Sanitise(s.CarName);
            string driver = Sanitise(s.DriverName);
            string config = Sanitise(s.TrackConfigName);
            string carPath = car.ToLowerInvariant().Replace(" ", "");
            string lengthKm = (s.TrackLengthM / 1000.0).ToString("F4", Culture);
            string estLap = s.EstLapTimeS.ToString("F4", Culture);

            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append("WeekendInfo:\n");
            sb.Append(" Encoding: ISO_8859_1\n");
            sb.Append(" TrackName: ").Append(track).Append("\n");
            sb.Append(" TrackID: 0\n");
            sb.Append(" TrackLength: ").Append(lengthKm).Append(" km\n");
            sb.Append(" TrackLengthOfficial: ").Append(lengthKm).Append(" km\n");
            sb.Append(" TrackDisplayName: ").Append(track).Append("\n");
            sb.Append(" TrackDisplayShortName: ").Append(track).Append("\n");
            sb.Append(" TrackConfigName: ").Append(config).Append("\n");
            sb.Append(" TrackType: road course\n");
            sb.Append(" TrackDirection: neutral\n");
            sb.Append(" SessionID: 0\n");
            sb.Append(" SubSessionID: 0\n");
            sb.Append(" SeriesID: 0\n");
            sb.Append(" SeasonID: 0\n");
            sb.Append(" LeagueID: 0\n");
            sb.Append(" Official: 0\n");
            sb.Append(" RaceWeek: 0\n");
            sb.Append(" EventType: Test\n");
            sb.Append(" Category: Road\n");
            sb.Append(" SimMode: full\n");
            sb.Append(" TeamRacing: 0\n");
            sb.Append(" NumCarClasses: 1\n");
            sb.Append(" NumCarTypes: 1\n");
            sb.Append(" HeatRacing: 0\n");
            sb.Append(" BuildType: Release\n");
            sb.Append(" BuildTarget: Members\n");
            sb.Append(" BuildVersion: shtep\n");
            sb.Append("\n");
            sb.Append("SessionInfo:\n");
            sb.Append(" CurrentSessionNum: 0\n");
            sb.Append(" Sessions:\n");
            sb.Append(" - SessionNum: 0\n");
            sb.Append("   SessionLaps: unlimited\n");
            sb.Append("   SessionTime: unlimited\n");
            sb.Append("   SessionType: Offline Testing\n");
            sb.Append("   SessionName: TESTING\n");
            sb.Append("   SessionSubType:\n");
            sb.Append("   SessionSkipped: 0\n");
            sb.Append("   ResultsAverageLapTime: ").Append(estLap).Append("\n");
            sb.Append("   ResultsLapsComplete: ").Append(s.LapCount.ToString(Culture)).Append("\n");
            sb.Append("   ResultsOfficial: 0\n");
            sb.Append("\n");
            sb.Append("DriverInfo:\n");
            sb.Append(" DriverCarIdx: 0\n");
            sb.Append(" DriverUserID: 1\n");
            sb.Append(" PaceCarIdx: -1\n");
            sb.Append(" DriverIsAdmin: 1\n");
            sb.Append(" DriverCarIsElectric: 0\n");
            sb.Append(" DriverCarIdleRPM: 1000.000\n");
            sb.Append(" DriverCarRedLine: 10000.000\n");
            sb.Append(" DriverCarEngCylinderCount: 4\n");
            sb.Append(" DriverCarGearNumForward: ").Append(s.GearCountForward.ToString(Culture)).Append("\n");
            sb.Append(" DriverCarGearNeutral: 1\n");
            sb.Append(" DriverCarGearReverse: 1\n");
            sb.Append(" DriverCarEstLapTime: ").Append(estLap).Append("\n");
            sb.Append(" DriverSetupName: default.sto\n");
            sb.Append(" DriverSetupIsModified: 0\n");
            sb.Append(" DriverSetupPassedTech: 1\n");
            sb.Append(" DriverIncidentCount: 0\n");
            sb.Append(" Drivers:\n");
            sb.Append(" - CarIdx: 0\n");
            sb.Append("   UserName: ").Append(driver).Append("\n");
            sb.Append("   AbbrevName:\n");
            sb.Append("   Initials:\n");
            sb.Append("   UserID: 1\n");
            sb.Append("   TeamID: 0\n");
            sb.Append("   TeamName: ").Append(driver).Append("\n");
            sb.Append("   CarNumber: \"00\"\n");
            sb.Append("   CarNumberRaw: 0\n");
            sb.Append("   CarPath: ").Append(carPath).Append("\n");
            sb.Append("   CarClassID: 0\n");
            sb.Append("   CarID: 0\n");
            sb.Append("   CarIsPaceCar: 0\n");
            sb.Append("   CarIsAI: 0\n");
            sb.Append("   CarIsElectric: 0\n");
            sb.Append("   CarScreenName: ").Append(car).Append("\n");
            sb.Append("   CarScreenNameShort: ").Append(car).Append("\n");
            sb.Append("   CarClassShortName:\n");
            sb.Append("   CarClassRelSpeed: 0\n");
            sb.Append("   CarClassLicenseLevel: 0\n");
            sb.Append("   IsSpectator: 0\n");
            sb.Append("\n");
            sb.Append("SplitTimeInfo:\n");
            sb.Append(" Sectors:\n");
            sb.Append(" - SectorNum: 0\n");
            sb.Append("   SectorStartPct: 0.000000\n");
            sb.Append("\n");
            sb.Append("CarSetup:\n");
            sb.Append(" UpdateCount: 1\n");
            sb.Append("...\n");

            // iRacing writes CRLF; yaml-cpp inside the importer accepts either, but
            // matching the real files costs nothing.
            return sb.Replace("\n", "\r\n").ToString();
        }

        private static readonly System.Globalization.CultureInfo Culture =
            System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>
        /// Keeps a value on one line and out of YAML's way. Sim-supplied car and
        /// track names are arbitrary strings; a stray newline or colon would
        /// produce a file the importer rejects wholesale.
        /// </summary>
        private static string Sanitise(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c == '\r' || c == '\n' || c == '\t') sb.Append(' ');
                else if (c == ':') sb.Append('-');
                else sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
