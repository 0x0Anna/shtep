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
                // GameReaderCommon.StatusDataBase.Handbrake exists generically (confirmed
                // by reflecting the installed GameReaderCommon.dll) but is unwired until
                // now. Scale (0-100 vs 0-1) is UNCONFIRMED - Throttle/Brake/Clutch turned
                // out to already be 0-100 on FH6 despite an initial *100 assumption (see
                // header comment above), so don't assume Handbrake matches without a live
                // capture that actually pulls the handbrake.
                ("Handbrake_pct", (d, sim) => TryGet(() => (double?)d.Handbrake)),
                ("LapDistance_m", (d, sim) => TryGet(() => (double?)d.TrackPositionMeters)),
                ("FuelLevel_pct", (d, sim) => TryGet(() => (double?)d.FuelPercent)),
                ("LapNumber", (d, sim) => TryGet(() => (double?)d.CurrentLap)),
                ("LatAccel_g", (d, sim) => TryGet(() => d.AccelerationSway)),
                ("LongAccel_g", (d, sim) => TryGet(() => d.AccelerationSurge)),
                ("VertAccel_g", (d, sim) => TryGet(() => d.AccelerationHeave)),
                ("ABSActive", (d, sim) => TryGet(() => (double?)d.ABSActive)),
                // Mirror of ABSActive - same generic Int32 flag shape, confirmed present
                // by reflecting GameReaderCommon.dll, unwired until now.
                ("TCActive", (d, sim) => TryGet(() => (double?)d.TCActive)),
                ("AirTemp_C", (d, sim) => TryGet(() => (double?)d.AirTemperature)),
                ("TrackTemp_C", (d, sim) => TryGet(() => (double?)d.RoadTemperature)),
                // TyreTemperatureFront/RearLeft/Right are generic StatusDataBase Doubles
                // (confirmed by reflection) - treated as already-Celsius the same way
                // AirTemperature/RoadTemperature are above (StatusDataBase.TemperatureUnit
                // exists but isn't consulted here, consistent with the existing AirTemp_C/
                // TrackTemp_C channels). SCHEMA.md listed this channel as wanted since v1;
                // this wires it up for the first time.
                ("TyreTempFL_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontLeft)),
                ("TyreTempFR_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontRight)),
                ("TyreTempRL_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearLeft)),
                ("TyreTempRR_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearRight)),
                // Per-tyre temperature spread (inner/middle/outer across the tread),
                // distinct from the single averaged TyreTempFL_C etc. above - genuinely
                // new data, not a duplicate. Same already-Celsius assumption.
                ("TyreTempFL_Inner_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontLeftInner)),
                ("TyreTempFL_Middle_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontLeftMiddle)),
                ("TyreTempFL_Outer_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontLeftOuter)),
                ("TyreTempFR_Inner_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontRightInner)),
                ("TyreTempFR_Middle_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontRightMiddle)),
                ("TyreTempFR_Outer_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureFrontRightOuter)),
                ("TyreTempRL_Inner_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearLeftInner)),
                ("TyreTempRL_Middle_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearLeftMiddle)),
                ("TyreTempRL_Outer_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearLeftOuter)),
                ("TyreTempRR_Inner_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearRightInner)),
                ("TyreTempRR_Middle_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearRightMiddle)),
                ("TyreTempRR_Outer_C", (d, sim) => TryGet(() => (double?)d.TyreTemperatureRearRightOuter)),
                // CarCoordinates/RelativeCarCoordinates are generic StatusDataBase
                // Double[] (confirmed by reflection) - POSSIBLY the answer to the
                // PosX/Y/Z gap SCHEMA.md has flagged as unavailable since v1, but
                // array length/axis order/frame are all UNCONFIRMED (never checked
                // live before now). Wired provisionally with a generic bounds-checked
                // index rather than assuming length 3 - TryGetArrayElement returns
                // null past the array's actual length instead of throwing, so this is
                // safe to ship even if the array turns out to be a different size.
                ("CarPosX_raw", (d, sim) => TryGet(() => TryGetArrayElement(d.CarCoordinates, 0))),
                ("CarPosY_raw", (d, sim) => TryGet(() => TryGetArrayElement(d.CarCoordinates, 1))),
                ("CarPosZ_raw", (d, sim) => TryGet(() => TryGetArrayElement(d.CarCoordinates, 2))),
                ("CarRelPosX_raw", (d, sim) => TryGet(() => TryGetArrayElement(d.RelativeCarCoordinates, 0))),
                ("CarRelPosY_raw", (d, sim) => TryGet(() => TryGetArrayElement(d.RelativeCarCoordinates, 1))),
                ("CarRelPosZ_raw", (d, sim) => TryGet(() => TryGetArrayElement(d.RelativeCarCoordinates, 2))),
                // OrientationYaw/Pitch/Roll are generic StatusDataBase Doubles (confirmed by
                // reflection) but unit (radians vs degrees) is UNCONFIRMED - named with a
                // "_raw" suffix rather than guessing, same lesson as SteerAngle turning out
                // not to be degrees despite its name (see RawPhysicsAccessor.cs). Rename to
                // _deg/_rad once a live test settles it.
                ("OrientationYaw_raw", (d, sim) => TryGet(() => (double?)d.OrientationYaw)),
                ("OrientationPitch_raw", (d, sim) => TryGet(() => (double?)d.OrientationPitch)),
                ("OrientationRoll_raw", (d, sim) => TryGet(() => (double?)d.OrientationRoll)),
                // Actual rotation rates (Nullable<double> on StatusDataBase, same shape as
                // AccelerationSway/Surge/Heave above - no cast needed). Complements the
                // absolute Orientation*_raw channels; unit unconfirmed (deg/s vs rad/s).
                ("YawRate_raw", (d, sim) => TryGet(() => d.YawChangeVelocity)),
                ("PitchRate_raw", (d, sim) => TryGet(() => d.PitchChangeVelocity)),
                ("RollRate_raw", (d, sim) => TryGet(() => d.RollChangeVelocity)),
                // Per-corner tyre/brake health, all generic StatusDataBase Doubles
                // (confirmed by reflection). TyrePressureFL_raw below is CONFIRMED
                // identical to WheelPressureFL_raw further down (from the raw ACR
                // Physics struct) - byte-for-byte equal across a full live session,
                // 2026-08-03 - same underlying source read two ways, safe to drop one
                // eventually. TyreWear*/TyreDirt* below are CONFIRMED DEAD (see their
                // own comment). BrakeTemp* is confirmed live/working (SCHEMA.md).
                // Units still unconfirmed for TyrePressure/BrakeTemp's scale
                // (TyrePressureUnit/OilPressureUnit exist as separate string properties
                // but aren't consulted here, same stance as AirTemp_C/TrackTemp_C above).
                ("TyrePressureFL_raw", (d, sim) => TryGet(() => (double?)d.TyrePressureFrontLeft)),
                ("TyrePressureFR_raw", (d, sim) => TryGet(() => (double?)d.TyrePressureFrontRight)),
                ("TyrePressureRL_raw", (d, sim) => TryGet(() => (double?)d.TyrePressureRearLeft)),
                ("TyrePressureRR_raw", (d, sim) => TryGet(() => (double?)d.TyrePressureRearRight)),
                // TyreWear*/TyreDirt*: CONFIRMED DEAD for ACR, 2026-08-03 - flat 0.000
                // across three separate sessions, including a 163s aggressive drive with
                // real accumulated damage and a session where ABSActive/TCActive (same
                // recording pipeline) showed genuine live values, so this isn't "just
                // never exercised". Left wired since ACR is early access and its
                // telemetry surface isn't finalized - see SCHEMA.md and
                // RawPhysicsAccessor.cs's SuspensionDamageRaw comment for the same
                // reasoning and a possible future UDP-stream investigation path.
                ("TyreWearFL_raw", (d, sim) => TryGet(() => (double?)d.TyreWearFrontLeft)),
                ("TyreWearFR_raw", (d, sim) => TryGet(() => (double?)d.TyreWearFrontRight)),
                ("TyreWearRL_raw", (d, sim) => TryGet(() => (double?)d.TyreWearRearLeft)),
                ("TyreWearRR_raw", (d, sim) => TryGet(() => (double?)d.TyreWearRearRight)),
                ("TyreDirtFL_raw", (d, sim) => TryGet(() => (double?)d.TyreDirtFrontLeft)),
                ("TyreDirtFR_raw", (d, sim) => TryGet(() => (double?)d.TyreDirtFrontRight)),
                ("TyreDirtRL_raw", (d, sim) => TryGet(() => (double?)d.TyreDirtRearLeft)),
                ("TyreDirtRR_raw", (d, sim) => TryGet(() => (double?)d.TyreDirtRearRight)),
                ("BrakeTempFL_C", (d, sim) => TryGet(() => (double?)d.BrakeTemperatureFrontLeft)),
                ("BrakeTempFR_C", (d, sim) => TryGet(() => (double?)d.BrakeTemperatureFrontRight)),
                ("BrakeTempRL_C", (d, sim) => TryGet(() => (double?)d.BrakeTemperatureRearLeft)),
                ("BrakeTempRR_C", (d, sim) => TryGet(() => (double?)d.BrakeTemperatureRearRight)),
                // Engine/drivetrain health, all generic. Units unconfirmed except
                // PitLimiterOn (a plain Int32 flag, same shape as ABSActive/TCActive).
                ("EngineTorque_raw", (d, sim) => TryGet(() => (double?)d.EngineTorque)),
                ("OilPressure_raw", (d, sim) => TryGet(() => (double?)d.OilPressure)),
                ("OilTemp_C", (d, sim) => TryGet(() => (double?)d.OilTemperature)),
                ("WaterTemp_C", (d, sim) => TryGet(() => (double?)d.WaterTemperature)),
                ("TurboBar_raw", (d, sim) => TryGet(() => (double?)d.TurboBar)),
                ("BrakeBias_raw", (d, sim) => TryGet(() => (double?)d.BrakeBias)),
                ("PitLimiterOn", (d, sim) => TryGet(() => (double?)d.PitLimiterOn)),
                ("LapDistancePct", (d, sim) => TryGet(() => (double?)d.TrackPositionPercent)),
                ("SteerRatio", (d, sim) => RawPhysicsAccessor.SteerRatio(d, sim)),
                ("SuspTravelFL_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 0)),
                ("SuspTravelFR_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 1)),
                ("SuspTravelRL_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 2)),
                ("SuspTravelRR_mm", (d, sim) => RawPhysicsAccessor.SuspTravelMm(d, sim, 3)),
                ("WheelLoadFL_N", (d, sim) => RawPhysicsAccessor.WheelLoadN(d, sim, 0)),
                ("WheelLoadFR_N", (d, sim) => RawPhysicsAccessor.WheelLoadN(d, sim, 1)),
                ("WheelLoadRL_N", (d, sim) => RawPhysicsAccessor.WheelLoadN(d, sim, 2)),
                ("WheelLoadRR_N", (d, sim) => RawPhysicsAccessor.WheelLoadN(d, sim, 3)),
                ("WheelAngularSpeedFL_raw", (d, sim) => RawPhysicsAccessor.WheelAngularSpeedRaw(d, sim, 0)),
                ("WheelAngularSpeedFR_raw", (d, sim) => RawPhysicsAccessor.WheelAngularSpeedRaw(d, sim, 1)),
                ("WheelAngularSpeedRL_raw", (d, sim) => RawPhysicsAccessor.WheelAngularSpeedRaw(d, sim, 2)),
                ("WheelAngularSpeedRR_raw", (d, sim) => RawPhysicsAccessor.WheelAngularSpeedRaw(d, sim, 3)),
                ("WheelPressureFL_raw", (d, sim) => RawPhysicsAccessor.WheelPressureRaw(d, sim, 0)),
                ("WheelPressureFR_raw", (d, sim) => RawPhysicsAccessor.WheelPressureRaw(d, sim, 1)),
                ("WheelPressureRL_raw", (d, sim) => RawPhysicsAccessor.WheelPressureRaw(d, sim, 2)),
                ("WheelPressureRR_raw", (d, sim) => RawPhysicsAccessor.WheelPressureRaw(d, sim, 3)),
                ("SlipAngleFL_raw", (d, sim) => RawPhysicsAccessor.SlipAngleRaw(d, sim, 0)),
                ("SlipAngleFR_raw", (d, sim) => RawPhysicsAccessor.SlipAngleRaw(d, sim, 1)),
                ("SlipAngleRL_raw", (d, sim) => RawPhysicsAccessor.SlipAngleRaw(d, sim, 2)),
                ("SlipAngleRR_raw", (d, sim) => RawPhysicsAccessor.SlipAngleRaw(d, sim, 3)),
                ("SlipRatioFL", (d, sim) => RawPhysicsAccessor.SlipRatio(d, sim, 0)),
                ("SlipRatioFR", (d, sim) => RawPhysicsAccessor.SlipRatio(d, sim, 1)),
                ("SlipRatioRL", (d, sim) => RawPhysicsAccessor.SlipRatio(d, sim, 2)),
                ("SlipRatioRR", (d, sim) => RawPhysicsAccessor.SlipRatio(d, sim, 3)),
                ("BrakePressureFL_raw", (d, sim) => RawPhysicsAccessor.BrakePressureRaw(d, sim, 0)),
                ("BrakePressureFR_raw", (d, sim) => RawPhysicsAccessor.BrakePressureRaw(d, sim, 1)),
                ("BrakePressureRL_raw", (d, sim) => RawPhysicsAccessor.BrakePressureRaw(d, sim, 2)),
                ("BrakePressureRR_raw", (d, sim) => RawPhysicsAccessor.BrakePressureRaw(d, sim, 3)),
                ("CamberFL_raw", (d, sim) => RawPhysicsAccessor.CamberRaw(d, sim, 0)),
                ("CamberFR_raw", (d, sim) => RawPhysicsAccessor.CamberRaw(d, sim, 1)),
                ("CamberRL_raw", (d, sim) => RawPhysicsAccessor.CamberRaw(d, sim, 2)),
                ("CamberRR_raw", (d, sim) => RawPhysicsAccessor.CamberRaw(d, sim, 3)),
                ("SuspDamageFL_raw", (d, sim) => RawPhysicsAccessor.SuspensionDamageRaw(d, sim, 0)),
                ("SuspDamageFR_raw", (d, sim) => RawPhysicsAccessor.SuspensionDamageRaw(d, sim, 1)),
                ("SuspDamageRL_raw", (d, sim) => RawPhysicsAccessor.SuspensionDamageRaw(d, sim, 2)),
                ("SuspDamageRR_raw", (d, sim) => RawPhysicsAccessor.SuspensionDamageRaw(d, sim, 3)),
                ("TyresOutCount", (d, sim) => RawPhysicsAccessor.TyresOutCount(d, sim)),
                ("DiscLifeFL_raw", (d, sim) => RawPhysicsAccessor.DiscLifeRaw(d, sim, 0)),
                ("DiscLifeFR_raw", (d, sim) => RawPhysicsAccessor.DiscLifeRaw(d, sim, 1)),
                ("DiscLifeRL_raw", (d, sim) => RawPhysicsAccessor.DiscLifeRaw(d, sim, 2)),
                ("DiscLifeRR_raw", (d, sim) => RawPhysicsAccessor.DiscLifeRaw(d, sim, 3)),
                ("PadLifeFL_raw", (d, sim) => RawPhysicsAccessor.PadLifeRaw(d, sim, 0)),
                ("PadLifeFR_raw", (d, sim) => RawPhysicsAccessor.PadLifeRaw(d, sim, 1)),
                ("PadLifeRL_raw", (d, sim) => RawPhysicsAccessor.PadLifeRaw(d, sim, 2)),
                ("PadLifeRR_raw", (d, sim) => RawPhysicsAccessor.PadLifeRaw(d, sim, 3)),
                ("TyreForceFxFL_raw", (d, sim) => RawPhysicsAccessor.TyreForceFxRaw(d, sim, 0)),
                ("TyreForceFxFR_raw", (d, sim) => RawPhysicsAccessor.TyreForceFxRaw(d, sim, 1)),
                ("TyreForceFxRL_raw", (d, sim) => RawPhysicsAccessor.TyreForceFxRaw(d, sim, 2)),
                ("TyreForceFxRR_raw", (d, sim) => RawPhysicsAccessor.TyreForceFxRaw(d, sim, 3)),
                ("TyreForceFyFL_raw", (d, sim) => RawPhysicsAccessor.TyreForceFyRaw(d, sim, 0)),
                ("TyreForceFyFR_raw", (d, sim) => RawPhysicsAccessor.TyreForceFyRaw(d, sim, 1)),
                ("TyreForceFyRL_raw", (d, sim) => RawPhysicsAccessor.TyreForceFyRaw(d, sim, 2)),
                ("TyreForceFyRR_raw", (d, sim) => RawPhysicsAccessor.TyreForceFyRaw(d, sim, 3)),
                ("TyreMomentMzFL_raw", (d, sim) => RawPhysicsAccessor.TyreMomentMzRaw(d, sim, 0)),
                ("TyreMomentMzFR_raw", (d, sim) => RawPhysicsAccessor.TyreMomentMzRaw(d, sim, 1)),
                ("TyreMomentMzRL_raw", (d, sim) => RawPhysicsAccessor.TyreMomentMzRaw(d, sim, 2)),
                ("TyreMomentMzRR_raw", (d, sim) => RawPhysicsAccessor.TyreMomentMzRaw(d, sim, 3)),
                ("LocalVelocityX_raw", (d, sim) => RawPhysicsAccessor.LocalVelocityRaw(d, sim, 0)),
                ("LocalVelocityY_raw", (d, sim) => RawPhysicsAccessor.LocalVelocityRaw(d, sim, 1)),
                ("LocalVelocityZ_raw", (d, sim) => RawPhysicsAccessor.LocalVelocityRaw(d, sim, 2)),
            };

        // Bounds-checked array index for generic StatusDataBase array properties
        // (CarCoordinates/RelativeCarCoordinates) whose length isn't confirmed -
        // returns null past the array's actual length instead of throwing, same
        // "omit rather than sentinel" contract as every other channel here.
        private static double? TryGetArrayElement(double[] array, int index) =>
            array != null && index < array.Length ? (double?)array[index] : null;

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
