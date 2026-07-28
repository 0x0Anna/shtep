using System.Globalization;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Split out of ChannelMap so RecordingSession (SimHub-independent, unit
    /// tested without the SDK installed) doesn't need a GameReaderCommon reference
    /// just to format a double. See SCHEMA.md "Number formatting".
    /// </summary>
    public static class NumberFormat
    {
        public static string FormatValue(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
