using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;
using TelemetryExportPlugin.Export;

namespace TelemetryExportPlugin.Tests
{
    public class MotecLdWriterTests : IDisposable
    {
        private const int HeaderPtr = 11336;
        private const int ChannelHeaderSize = 16 + 2 + 6 + 8 + 32 + 8 + 12 + 40;

        private readonly string _path;

        public MotecLdWriterTests()
        {
            _path = Path.Combine(Path.GetTempPath(), "tep_tests_motec_" + Guid.NewGuid() + ".ld");
        }

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public void Write_ProducesHeaderAndChannelsReadableByOffset()
        {
            var session = new MotecSessionInfo
            {
                Driver = "Annalise",
                VehicleId = "Car_2038",
                Venue = "Greece Test",
                EventName = "Greece Test",
                EventSession = "stint",
                ShortComment = "FH6 via shtep",
                LongComment = "",
                Timestamp = new DateTime(2026, 7, 27, 12, 0, 0),
            };

            var channels = new List<MotecChannel>
            {
                new MotecChannel("Speed_kmh", "", new double[] { 0.0, 12.5, 35.2 }),
                new MotecChannel("RPM", "", new double[] { 900.0, 1200.0, 2400.0 }),
            };

            MotecLdWriter.Write(_path, session, channels, frequencyHz: 100);

            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read))
            using (var reader = new BinaryReader(stream))
            {
                Assert.Equal((uint)0x40, reader.ReadUInt32());
                reader.ReadBytes(4);

                uint metaPtr = reader.ReadUInt32();
                uint dataPtr = reader.ReadUInt32();
                Assert.Equal((uint)HeaderPtr, metaPtr);
                Assert.Equal((uint)(HeaderPtr + 2 * ChannelHeaderSize), dataPtr);
                reader.ReadBytes(20);

                uint auxPtr = reader.ReadUInt32();
                Assert.Equal((uint)8180, auxPtr);
                reader.ReadBytes(24);

                reader.ReadBytes(6); // three unknown H's
                reader.ReadUInt32(); // device serial
                reader.ReadBytes(8); // device type
                reader.ReadUInt16(); // device version
                reader.ReadUInt16(); // unknown

                uint numChanns = reader.ReadUInt32();
                Assert.Equal((uint)2, numChanns);
                reader.ReadBytes(4);

                string date = ReadFixedAscii(reader, 16);
                reader.ReadBytes(16);
                string time = ReadFixedAscii(reader, 16);
                reader.ReadBytes(16);
                Assert.Equal("27/07/2026", date);
                Assert.Equal("12:00:00", time);

                string driver = ReadFixedAscii(reader, 64);
                string vehicleId = ReadFixedAscii(reader, 64);
                reader.ReadBytes(64);
                string venue = ReadFixedAscii(reader, 64);
                reader.ReadBytes(64);
                Assert.Equal("Annalise", driver);
                Assert.Equal("Car_2038", vehicleId);
                Assert.Equal("Greece Test", venue);

                reader.ReadBytes(1024);
                reader.ReadUInt32(); // enable pro logging
                reader.ReadBytes(66);

                string shortComment = ReadFixedAscii(reader, 64);
                Assert.Equal("FH6 via shtep", shortComment);

                // The header's own inline event/session fields (bytes 1762-1890,
                // i.e. right at VehiclePtr) get overwritten by the vehicle struct
                // written immediately afterward - a quirk inherited as-is from the
                // reference format (see MotecLdWriter's class doc). The aux ldEvent
                // struct at EventPtr is the surviving, authoritative copy - checked
                // separately below.

                // ldEvent struct at EventPtr: name(64s) session(64s) comment(1024s) venue_ptr(H)
                stream.Seek(8180, SeekOrigin.Begin);
                string eventName = ReadFixedAscii(reader, 64);
                string eventSession = ReadFixedAscii(reader, 64);
                Assert.Equal("Greece Test", eventName);
                Assert.Equal("stint", eventSession);

                // Channel headers, contiguous starting at metaPtr.
                stream.Seek(metaPtr, SeekOrigin.Begin);

                uint prev0 = reader.ReadUInt32();
                uint next0 = reader.ReadUInt32();
                uint dataPtr0 = reader.ReadUInt32();
                uint len0 = reader.ReadUInt32();
                Assert.Equal(0u, prev0);
                Assert.Equal((uint)(HeaderPtr + ChannelHeaderSize), next0);
                Assert.Equal((uint)3, len0);

                reader.ReadUInt16(); // counter
                ushort dtypeA0 = reader.ReadUInt16();
                ushort dtype0 = reader.ReadUInt16();
                ushort freq0 = reader.ReadUInt16();
                Assert.Equal((ushort)0x07, dtypeA0);
                Assert.Equal((ushort)4, dtype0);
                Assert.Equal((ushort)100, freq0);

                reader.ReadUInt16(); // shift
                reader.ReadUInt16(); // mul
                reader.ReadUInt16(); // scale
                reader.ReadInt16(); // dec

                string name0 = ReadFixedAscii(reader, 32);
                reader.ReadBytes(8);
                string unit0 = ReadFixedAscii(reader, 12);
                Assert.Equal("Speed_kmh", name0);
                Assert.Equal("", unit0);

                reader.ReadBytes(40);

                uint prev1 = reader.ReadUInt32();
                uint next1 = reader.ReadUInt32();
                stream.Seek(-8, SeekOrigin.Current);
                Assert.Equal((uint)HeaderPtr, prev1);
                Assert.Equal(0u, next1);

                // Data region: channel 0's 3 float32 samples, then channel 1's.
                stream.Seek(dataPtr0, SeekOrigin.Begin);
                Assert.Equal(0.0f, reader.ReadSingle());
                Assert.Equal(12.5f, reader.ReadSingle());
                Assert.Equal(35.2f, reader.ReadSingle());
                Assert.Equal(900.0f, reader.ReadSingle());
                Assert.Equal(1200.0f, reader.ReadSingle());
                Assert.Equal(2400.0f, reader.ReadSingle());
            }
        }

        [Fact]
        public void Write_ZeroChannels_DoesNotThrow()
        {
            var session = new MotecSessionInfo();
            MotecLdWriter.Write(_path, session, new List<MotecChannel>(), frequencyHz: 100);
            Assert.True(File.Exists(_path));
        }

        private static string ReadFixedAscii(BinaryReader reader, int length)
        {
            var bytes = reader.ReadBytes(length);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\0');
        }
    }
}
