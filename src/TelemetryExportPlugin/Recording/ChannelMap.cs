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
    ///   2. `PosX_m`/`PosY_m`/`PosZ_m` from SCHEMA.md's channel table are still NOT
    ///      public properties on the generic StatusDataBase and aren't wired up.
    ///      `SteerAngle_deg` and per-corner `SuspTravel*_mm` WERE in the same boat
    ///      until 2026-08-02: reachable via `StatusDataBase.GetRawDataObject()` cast
    ///      to the sim's raw physics struct (confirmed by reflecting
    ///      ACSharedMemory.dll: `ACSharedMemory.ACR.MMFModels.Physics` - and the
    ///      AC/ACC-family structs alongside it - carry SteerAngle, SuspensionTravel,
    ///      RideHeight, WheelSlip, WheelLoad, WheelsPressure, WheelAngularSpeed).
    ///      GameReaderCommon's public property surface is a lowest-common-denominator
    ///      projection, NOT the source of truth for whether a channel exists on a
    ///      given sim - see RawPhysicsAccessor.cs for the per-sim cast, currently
    ///      AssettoCorsaRally only. `SteerRatio` (originally named `SteerAngle_deg` -
    ///      renamed 2026-08-02 after a live capture proved the field is NOT degrees:
    ///      values clip in a flat plateau at exactly -1.0/1.0, the signature of a
    ///      normalized full-lock ratio) - magnitude/scale confirmed correct this way,
    ///      and sign confirmed live 2026-08-03 via a directed left-then-right
    ///      full-lock test: negative = left, positive = right (see
    ///      RawPhysicsAccessor.cs's SteerRatio comment for the confirming capture).
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
        /// Second delegate parameter is `data.GameName` - only the RawPhysicsAccessor-
        /// backed entries below use it; the rest ignore it.
        /// </summary>
        public static readonly IReadOnlyList<(string Header, Func<StatusDataBase, string, double?> GetValue)> Definitions =
            new List<(string, Func<StatusDataBase, string, double?>)>
            {
                ("Speed_kmh", (d, sim) => TryGet(() => (double?)d.SpeedKmh)),
                ("RPM", (d, sim) => TryGet(() => (double?)d.Rpms)),
                ("Gear", (d, sim) => TryGet(() => ParseGear(d.Gear))),
                ("Throttle_pct", (d, sim) => TryGet(() => (double?)d.Throttle)),
                ("Brake_pct", (d, sim) => TryGet(() => (double?)d.Brake)),
                ("Clutch_pct", (d, sim) => TryGet(() => (double?)d.Clutch)),
                ("LapDistance_m", (d, sim) => TryGet(() => (double?)d.TrackPositionMeters)),
                ("FuelLevel_pct", (d, sim) => TryGet(() => (double?)d.FuelPercent)),
                ("LapNumber", (d, sim) => TryGet(() => (double?)d.CurrentLap)),
                ("LatAccel_g", (d, sim) => TryGet(() => d.AccelerationSway)),
                ("LongAccel_g", (d, sim) => TryGet(() => d.AccelerationSurge)),
                ("VertAccel_g", (d, sim) => TryGet(() => d.AccelerationHeave)),
                ("ABSActive", (d, sim) => TryGet(() => (double?)d.ABSActive)),
                ("AirTemp_C", (d, sim) => TryGet(() => (double?)d.AirTemperature)),
                ("TrackTemp_C", (d, sim) => TryGet(() => (double?)d.RoadTemperature)),
                ("LapDistancePct", (d, sim) => TryGet(() => (double?)d.TrackPositionPercent)),
                ("SteerRatio", (d, sim) => RawPhysicsAccessor.SteerRatio(d, sim)),
                ("SuspTravelFL_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 0)),
                ("SuspTravelFR_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 1)),
                ("SuspTravelRL_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 2)),
                ("SuspTravelRR_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 3)),
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
