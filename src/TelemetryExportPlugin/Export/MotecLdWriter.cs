using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TelemetryExportPlugin.Export
{
    /// <summary>One channel's worth of fixed-frequency samples destined for a MoTeC .ld file.</summary>
    public sealed class MotecChannel
    {
        public string Name { get; }
        public string Units { get; }
        public IReadOnlyList<double> Values { get; }

        public MotecChannel(string name, string units, IReadOnlyList<double> values)
        {
            Name = name;
            Units = units ?? "";
            Values = values;
        }
    }

    /// <summary>Session-level metadata that lands in the .ld header/event/venue/vehicle blocks.</summary>
    public sealed class MotecSessionInfo
    {
        public string Driver { get; set; } = "";
        public string VehicleId { get; set; } = "";
        public string Venue { get; set; } = "";
        public string EventName { get; set; } = "";
        public string EventSession { get; set; } = "";
        public string ShortComment { get; set; } = "";
        public string LongComment { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Writes MoTeC .ld files (the binary format i2 Pro/Standard read).
    ///
    /// This is a direct port of the file-layout knowledge in the MotecLogGenerator
    /// reference project (ldparser.py's struct formats + motec_log.py's fixed
    /// pointer constants and channel-pointer chaining), reverse-engineered from
    /// real .ld files rather than documented anywhere by MoTeC. Every hardcoded
    /// offset/constant below (VEHICLE_PTR, VENUE_PTR, EVENT_PTR, HEADER_PTR, the
    /// 0x40/0x4240/0x1f44/"ADL"/0xadb0/0xc81a4/0x2ee1 magic numbers) is preserved
    /// as-is from that source rather than re-derived, since they were only ever
    /// established by inspecting known-good files.
    ///
    /// All channels are written as fixed-frequency float32 data with shift=0,
    /// mul=1, scale=1, dec=0 (matching the reference generator) - no per-channel
    /// unit conversion happens here, values are written as given.
    /// </summary>
    public static class MotecLdWriter
    {
        private const int VehiclePtr = 1762;
        private const int VenuePtr = 5078;
        private const int EventPtr = 8180;
        private const int HeaderPtr = 11336;

        // Size in bytes of the packed ldChan struct: IIII(16) H(2) HHH(6) HHHh(8) 32s 8s 12s 40x
        private const int ChannelHeaderSize = 16 + 2 + 6 + 8 + 32 + 8 + 12 + 40;

        public static void Write(string path, MotecSessionInfo session, IReadOnlyList<MotecChannel> channels, int frequencyHz)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                int n = channels.Count;
                long dataRegionStart = HeaderPtr + (long)n * ChannelHeaderSize;

                WriteHeader(writer, session, n, dataRegionStart);
                WriteEvent(writer, session);
                WriteVenue(writer, session);
                WriteVehicle(writer, session);

                writer.BaseStream.Seek(HeaderPtr, SeekOrigin.Begin);
                long dataPtr = dataRegionStart;
                var dataPtrs = new long[n];
                for (int i = 0; i < n; i++)
                {
                    dataPtrs[i] = dataPtr;
                    dataPtr += channels[i].Values.Count * 4L;
                }

                for (int i = 0; i < n; i++)
                {
                    long metaPtr = HeaderPtr + (long)i * ChannelHeaderSize;
                    long prevMetaPtr = i == 0 ? 0 : metaPtr - ChannelHeaderSize;
                    long nextMetaPtr = i == n - 1 ? 0 : metaPtr + ChannelHeaderSize;
                    WriteChannelHeader(writer, i, prevMetaPtr, nextMetaPtr, dataPtrs[i], channels[i], frequencyHz);
                }

                foreach (var channel in channels)
                {
                    foreach (var value in channel.Values)
                    {
                        writer.Write((float)value);
                    }
                }
            }
        }

        private static void WriteHeader(BinaryWriter w, MotecSessionInfo session, int numChannels, long dataRegionStart)
        {
            w.BaseStream.Seek(0, SeekOrigin.Begin);

            w.Write((uint)0x40);
            WritePad(w, 4);

            w.Write((uint)HeaderPtr); // chann_meta_ptr - first channel meta block
            w.Write((uint)dataRegionStart); // chann_data_ptr - where channel sample data begins, after all channel headers
            WritePad(w, 20);

            w.Write((uint)EventPtr); // aux_ptr (event)
            WritePad(w, 24);

            w.Write((ushort)1);
            w.Write((ushort)0x4240);
            w.Write((ushort)0xf);

            w.Write((uint)0x1f44); // device serial
            WriteFixedAscii(w, "ADL", 8); // device type
            w.Write((ushort)420); // device version
            w.Write((ushort)0xadb0);

            w.Write((uint)numChannels);
            WritePad(w, 4);

            WriteFixedAscii(w, session.Timestamp.ToString("dd/MM/yyyy"), 16);
            WritePad(w, 16);
            WriteFixedAscii(w, session.Timestamp.ToString("HH:mm:ss"), 16);
            WritePad(w, 16);

            WriteFixedAscii(w, session.Driver, 64);
            WriteFixedAscii(w, session.VehicleId, 64);
            WritePad(w, 64);

            WriteFixedAscii(w, session.Venue, 64);
            WritePad(w, 64);

            WritePad(w, 1024);

            w.Write((uint)0xc81a4); // enable "pro logging"
            WritePad(w, 66);

            WriteFixedAscii(w, session.ShortComment, 64);
            WritePad(w, 126);

            WriteFixedAscii(w, session.EventName, 64);
            WriteFixedAscii(w, session.EventSession, 64);
        }

        private static void WriteEvent(BinaryWriter w, MotecSessionInfo session)
        {
            w.BaseStream.Seek(EventPtr, SeekOrigin.Begin);
            WriteFixedAscii(w, session.EventName, 64);
            WriteFixedAscii(w, session.EventSession, 64);
            WriteFixedAscii(w, session.LongComment, 1024);
            w.Write((ushort)VenuePtr);
        }

        private static void WriteVenue(BinaryWriter w, MotecSessionInfo session)
        {
            w.BaseStream.Seek(VenuePtr, SeekOrigin.Begin);
            WriteFixedAscii(w, session.Venue, 64);
            WritePad(w, 1034);
            w.Write((ushort)VehiclePtr);
        }

        private static void WriteVehicle(BinaryWriter w, MotecSessionInfo session)
        {
            w.BaseStream.Seek(VehiclePtr, SeekOrigin.Begin);
            WriteFixedAscii(w, session.VehicleId, 64);
            WritePad(w, 128);
            w.Write((uint)0); // weight
            WriteFixedAscii(w, "", 32); // type
            WriteFixedAscii(w, "", 32); // comment
        }

        private static void WriteChannelHeader(BinaryWriter w, int index, long prevMetaPtr, long nextMetaPtr,
            long dataPtr, MotecChannel channel, int frequencyHz)
        {
            w.Write((uint)prevMetaPtr);
            w.Write((uint)nextMetaPtr);
            w.Write((uint)dataPtr);
            w.Write((uint)channel.Values.Count);

            w.Write((ushort)unchecked((ushort)(0x2ee1 + index)));
            w.Write((ushort)0x07); // dtype_a: float
            w.Write((ushort)4);    // dtype size: float32
            w.Write((ushort)Math.Min(frequencyHz, ushort.MaxValue));

            w.Write((ushort)0); // shift
            w.Write((ushort)1); // mul
            w.Write((ushort)1); // scale
            w.Write((short)0);  // dec

            WriteFixedAscii(w, channel.Name, 32);
            WriteFixedAscii(w, "", 8); // short name - unused, matches reference generator
            WriteFixedAscii(w, channel.Units, 12);

            WritePad(w, 40);
        }

        private static void WritePad(BinaryWriter w, int count)
        {
            if (count > 0) w.Write(new byte[count]);
        }

        private static void WriteFixedAscii(BinaryWriter w, string value, int length)
        {
            var bytes = new byte[length];
            if (!string.IsNullOrEmpty(value))
            {
                var encoded = Encoding.ASCII.GetBytes(value);
                Array.Copy(encoded, bytes, Math.Min(encoded.Length, length));
            }
            w.Write(bytes);
        }
    }
}