using System;
using System.Collections.Generic;
using System.Globalization;
using GameReaderCommon;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Maps SCHEMA.md canonical header names to StatusDataBase accessors.
    ///
    /// Verified by reflecting the installed GameReaderCommon.dll (SimHub 9.x) -
    /// NOT from documentation, per PLUGIN_IMPLEMENTATION_PLAN.md's "don't assume
    /// everything correctly documented". Two things fell out of that check that
    /// the plan/schema docs didn't anticipate:
    ///
    ///   1. `Gear` is a string ("R"/"N"/"1".."N"), not a numeric field - parsed below.
    ///   2. `PosX_m`/`PosY_m`/`PosZ_m`, `SteerAngle_deg`, and per-corner
    ///      `SuspTravel*_mm` from SCHEMA.md's channel table do NOT exist on the
    ///      generic StatusDataBase at all. Those live (if anywhere) on sim-specific
    ///      extended data classes that individual SimHub game readers expose, which
    ///      requires a per-sim cast this plugin doesn't do yet - deliberately left
    ///      out of this v1 map rather than faked. Add them only after confirming a
    ///      concrete accessor per sim (see PLUGIN_IMPLEMENTATION_PLAN.md step 9).
    ///
    /// `Throttle`/`Brake`/`Clutch` are passed through as-is (already 0-100) - confirmed
    /// live against FH6 (2026-07-27): values like Throttle_pct=10000 in the diagnostic
    /// log showed the earlier assumed *100 scaling was wrong, since GameReaderCommon
    /// already returns 0-100 for this sim. Re-verify per additional sim if one turns
    /// out to actually use a 0-1 fraction instead.
    /// </summary>
    public static class ChannelMap
    {
        /// <summary>
        /// Ordered so file column order is stable for a given EnabledChannels list.
        /// Value is null when the sim doesn't currently expose this channel -
        /// per SCHEMA.md, a null result means "omit the column", not "write a sentinel".
        /// </summary>
        public static readonly IReadOnlyList<(string Header, Func<StatusDataBase, double?> GetValue)> Definitions =
            new List<(string, Func<StatusDataBase, double?>)>
            {
                ("Speed_kmh", d => TryGet(() => (double?)d.SpeedKmh)),
                ("RPM", d => TryGet(() => (double?)d.Rpms)),
                ("Gear", d => TryGet(() => ParseGear(d.Gear))),
                ("Throttle_pct", d => TryGet(() => (double?)d.Throttle)),
                ("Brake_pct", d => TryGet(() => (double?)d.Brake)),
                ("Clutch_pct", d => TryGet(() => (double?)d.Clutch)),
                ("LapDistance_m", d => TryGet(() => (double?)d.TrackPositionMeters)),
                ("FuelLevel_pct", d => TryGet(() => (double?)d.FuelPercent)),
                ("LapNumber", d => TryGet(() => (double?)d.CurrentLap)),
            };

        private static double? ParseGear(string gear)
        {
            if (string.IsNullOrEmpty(gear)) return null;
            if (gear == "R") return -1;
            if (gear == "N") return 0;
            return double.TryParse(gear, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ? g : (double?)null;
        }

        private static double? TryGet(Func<double?> accessor)
        {
            try
            {
                return accessor();
            }
            catch
            {
                // Property not present/supported for the running sim/game adapter.
                return null;
            }
        }

    }
}
