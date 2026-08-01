using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace TelemetryExportPlugin.Export
{
    /// <summary>
    /// Writes a MoTeC .ldx sidecar - the file i2 actually reads for lap markers.
    /// Ground truth for this schema: a real hardware-logged .ldx sample
    /// (not produced by this repo or MotecLogGenerator), which contains
    /// &lt;MarkerBlock&gt;&lt;MarkerGroup Name="Beacons"&gt;&lt;Marker ClassName="BCN"
    /// Time="..."/&gt; entries. Confirmed the `Time` attribute is elapsed
    /// microseconds since the start of the paired .ld's data: two consecutive
    /// markers in that sample were exactly 120,717,000 apart, matching the
    /// same file's own reported "Fastest Time: 2:00.717" to the millisecond.
    ///
    /// The sample file's &lt;Details&gt; block (Total Laps/Fastest Time/Fastest Lap)
    /// is deliberately not reproduced here - there's real evidence for the
    /// MarkerBlock mechanism driving i2's lap detection, not enough to be
    /// confident Details is required too. Add it only after confirming that's
    /// actually needed against a real i2 load.
    /// </summary>
    public static class MotecLdxWriter
    {
        public static void Write(string path, IReadOnlyList<double> lapBoundaryTimesS)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\"?>\n");
            sb.Append("<LDXFile Locale=\"English_United Kingdom.1252\" DefaultLocale=\"C\" Version=\"1.6\">\n");
            sb.Append(" <Layers>\n");
            sb.Append("  <Layer>\n");
            sb.Append("   <MarkerBlock>\n");
            sb.Append("    <MarkerGroup Name=\"Beacons\" Index=\"0\">\n");

            for (int i = 0; i < lapBoundaryTimesS.Count; i++)
            {
                double micros = lapBoundaryTimesS[i] * 1_000_000.0;
                sb.Append($"     <Marker Version=\"100\" ClassName=\"BCN\" Name=\"{i + 1}, id=99\" Flags=\"13\" Time=\"{FormatTime(micros)}\"/>\n");
            }

            sb.Append("    </MarkerGroup>\n");
            sb.Append("   </MarkerBlock>\n");
            sb.Append("   <RangeBlock/>\n");
            sb.Append("  </Layer>\n");
            sb.Append(" </Layers>\n");
            sb.Append("</LDXFile>\n");

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // Matches the real sample's formatting style (e.g. "7.92000000000000000e+05") -
        // cosmetic, standard XML/double parsing doesn't care about exponent digit count,
        // but kept close to observed output rather than an arbitrary format.
        private static string FormatTime(double microseconds)
        {
            return microseconds.ToString("0.00000000000000000e+00", CultureInfo.InvariantCulture);
        }
    }
}
