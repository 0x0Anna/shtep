using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;
using TelemetryExportPlugin.Export;

namespace TelemetryExportPlugin.Tests
{
    public class MotecLdxWriterTests : IDisposable
    {
        private readonly string _path;

        public MotecLdxWriterTests()
        {
            _path = Path.Combine(Path.GetTempPath(), "tep_tests_motec_" + Guid.NewGuid() + ".ldx");
        }

        public void Dispose()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }

        [Fact]
        public void Write_ProducesWellFormedXmlWithOneMarkerPerBoundary_TimeInMicroseconds()
        {
            MotecLdxWriter.Write(_path, new[] { 12.5, 30.0, 47.25 });

            var doc = XDocument.Load(_path);
            var markers = doc.Descendants("Marker").ToList();
            Assert.Equal(3, markers.Count);

            for (int i = 0; i < markers.Count; i++)
            {
                Assert.Equal("BCN", markers[i].Attribute("ClassName").Value);
                Assert.Equal($"{i + 1}, id=99", markers[i].Attribute("Name").Value);
            }

            var times = markers.Select(m => double.Parse(m.Attribute("Time").Value, CultureInfo.InvariantCulture));
            Assert.Equal(new double[] { 12_500_000, 30_000_000, 47_250_000 }, times);
        }

        [Fact]
        public void Write_NoBoundaries_StillProducesValidEmptyMarkerGroup()
        {
            MotecLdxWriter.Write(_path, new double[0]);

            var doc = XDocument.Load(_path);
            Assert.Empty(doc.Descendants("Marker"));
            Assert.NotNull(doc.Descendants("MarkerGroup").FirstOrDefault());
        }
    }
}
