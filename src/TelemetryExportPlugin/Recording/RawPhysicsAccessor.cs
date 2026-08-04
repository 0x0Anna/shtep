using System;
using GameReaderCommon;

namespace TelemetryExportPlugin.Recording
{
    /// <summary>
    /// Per-sim access to fields StatusDataBase doesn't expose as public properties.
    /// StatusDataBase.GetRawDataObject() returns the active sim's raw physics
    /// struct - each sim family needs its own cast. See ChannelMap.cs's header
    /// comment for how this was discovered (a reflected *method*, not a property).
    ///
    /// v1 scope: AssettoCorsaRally only (data.GameName confirmed against a real
    /// recorded .meta.json, not guessed). ACSharedMemory.dll's base AC and ACC
    /// Physics structs are field-identical by reflection, but their exact
    /// GameName strings aren't confirmed against a live capture yet - don't add
    /// them to SupportedGameNames until they are (see
    /// feedback_simhub_research_method memory).
    /// </summary>
    internal static class RawPhysicsAccessor
    {
        private static readonly string[] SupportedGameNames = { "AssettoCorsaRally" };

        private static bool TryGetPhysics(StatusDataBase d, string gameName,
            out ACSharedMemory.ACR.MMFModels.Physics physics)
        {
            physics = default;
            if (gameName == null || Array.IndexOf(SupportedGameNames, gameName) < 0) return false;
            try
            {
                // GetRawDataObject() for ACR returns an ACRRawData wrapper (Physics/
                // Graphics/StaticInfo/ReadAt fields), not the Physics struct directly -
                // confirmed by reflecting ACSharedMemory.dll's actual field list. The
                // straight `is Physics` cast this used to do never matched, so every
                // channel below was silently null in every live capture until now.
                if (d.GetRawDataObject() is ACSharedMemory.ACR.Reader.ACRRawData raw)
                {
                    physics = raw.Physics;
                    return true;
                }
            }
            catch
            {
                // GetRawDataObject() not supported by the active adapter.
            }
            return false;
        }

        // Confirmed live 2026-08-02 against a real ACR capture: values clip in a
        // flat plateau at exactly -1.0/1.0 (41 rows at exactly -1.0, 23 at exactly
        // 1.0 in one session) - this is a normalized ratio of full lock, NOT
        // degrees, despite the field being named "SteerAngle" on the raw struct.
        // Sign confirmed live 2026-08-03: a directed test (wheel turned left
        // first, full lock, twice) recorded SteerRatio = -1.0 for every left
        // excursion and +1.0 for every right excursion
        // (AssettoCorsaRally_Greece_New_Loutraki_20260803_171002.tsv, t=8.1-31.6s,
        // stationary). Convention is settled: negative = left, positive = right.
        public static double? SteerRatio(StatusDataBase d, string gameName) =>
            TryGetPhysics(d, gameName, out var p) ? (double?)p.SteerAngle : null;

        // Corner order is Kunos's documented FL/FR/RL/RR convention, consistent
        // across AC/ACC/ACR's shared-memory Physics struct (MarshalAs
        // SizeConst=4, confirmed by reflection). Field unit is meters -> *1000
        // for the mm header.
        public static double? SuspTravelMm(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.SuspensionTravel != null && corner < p.SuspensionTravel.Length
                ? (double?)(p.SuspensionTravel[corner] * 1000.0)
                : null;

        // Same FL/FR/RL/RR, Single[4] convention as SuspensionTravel above - confirmed
        // by reflection only so far, NOT yet against a live capture. Unit is assumed
        // Newtons per Kunos's public AC SDK docs, but per feedback_simhub_research_method
        // this repo doesn't trust docs alone - treat as unconfirmed until a live test
        // shows plausible magnitudes (e.g. load rising under braking/cornering).
        public static double? WheelLoadN(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.WheelLoad != null && corner < p.WheelLoad.Length
                ? (double?)p.WheelLoad[corner]
                : null;

        // Same per-corner convention. Unit unconfirmed (rad/s is the commonly cited
        // value for this field in Kunos's SDK docs, but that's not verified here) -
        // channel is named without a unit suffix until a live test settles it.
        public static double? WheelAngularSpeedRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.WheelAngularSpeed != null && corner < p.WheelAngularSpeed.Length
                ? (double?)p.WheelAngularSpeed[corner]
                : null;

        // Same per-corner convention. Unit unconfirmed (PSI vs kPa vs bar).
        public static double? WheelPressureRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.WheelsPressure != null && corner < p.WheelsPressure.Length
                ? (double?)p.WheelsPressure[corner]
                : null;

        // Same per-corner convention. Unit unconfirmed (deg vs rad) - same trap
        // SteerAngle turned out to have (see SteerRatio above), so don't assume
        // degrees without a live test.
        public static double? SlipAngleRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.slipAngle != null && corner < p.slipAngle.Length
                ? (double?)p.slipAngle[corner]
                : null;

        // Same per-corner convention. Dimensionless ratio by definition (no unit
        // suffix needed), but magnitude/sign convention still unconfirmed live.
        public static double? SlipRatio(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.slipRatio != null && corner < p.slipRatio.Length
                ? (double?)p.slipRatio[corner]
                : null;

        // Same per-corner convention. No generic StatusDataBase equivalent (unlike
        // BrakeTemperature*, which is already generic and preferred there) - this is
        // the actual hydraulic pressure at the caliper, distinct from the pedal
        // Brake_pct input. Unit unconfirmed (bar vs PSI vs kPa).
        public static double? BrakePressureRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.brakePressure != null && corner < p.brakePressure.Length
                ? (double?)p.brakePressure[corner]
                : null;

        // Same per-corner convention. No generic equivalent. Unit unconfirmed (the
        // raw field name says radians, but per feedback_simhub_research_method this
        // repo doesn't trust field names alone anymore after the SteerAngle/SteerRatio
        // lesson - named _raw rather than _rad until a live test confirms it).
        public static double? CamberRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.CamberRad != null && corner < p.CamberRad.Length
                ? (double?)p.CamberRad[corner]
                : null;

        // Same per-corner convention. No generic equivalent. Unit/range unconfirmed
        // (likely 0-1 damage fraction per Kunos's docs, not verified here).
        //
        // CONFIRMED DEAD for ACR, 2026-08-03: flat 0.000 across a 376s session that
        // included a real off-course excursion and confirmed terminal damage
        // (AssettoCorsaRally_Greece_Zeli_20260803_205542.tsv) - not just "never
        // triggered", genuinely never populated by SimHub's ACR adapter. Left wired
        // rather than removed: ACR is still early access and its telemetry surface
        // isn't finalized, so this could start reporting real values after a future
        // SimHub/ACR update. A possible future path if this matters enough to chase
        // further: ACR may expose damage over its own UDP telemetry stream
        // independent of GameReaderCommon's adapter - worth sniffing that stream
        // directly in a future session rather than assuming SimHub's reflection
        // surface is the ceiling.
        public static double? SuspensionDamageRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.suspensionDamage != null && corner < p.suspensionDamage.Length
                ? (double?)p.suspensionDamage[corner]
                : null;

        // Not per-corner - a single scalar count (0-4) of wheels currently off the
        // track surface. No generic StatusDataBase equivalent; useful groundwork for
        // the still-unwired RallyBoundary off-track/cut detection (see project memory
        // on rally stage detection).
        //
        // CONFIRMED DEAD for ACR, 2026-08-03: same 376s session as SuspensionDamageRaw
        // above, same off-course event, flat 0.000 throughout despite the car
        // genuinely leaving the track surface. Same "left wired, not removed" reasoning
        // and same future-UDP-stream idea applies - see that comment.
        public static double? TyresOutCount(StatusDataBase d, string gameName) =>
            TryGetPhysics(d, gameName, out var p) ? (double?)p.NumberOfTyresOut : null;

        // Same per-corner convention. No generic equivalent. Brake disc wear
        // fraction, distinct from padLife below and from BrakeTemperature* (already
        // generic). Range/unit unconfirmed (likely 0-1 remaining-life fraction).
        public static double? DiscLifeRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.discLife != null && corner < p.discLife.Length
                ? (double?)p.discLife[corner]
                : null;

        // Same per-corner convention. Brake pad wear, distinct from discLife above.
        public static double? PadLifeRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.padLife != null && corner < p.padLife.Length
                ? (double?)p.padLife[corner]
                : null;

        // Same per-corner convention. Tyre contact-patch force vector components -
        // fx/fy/mz are the raw struct's own field names; which physical axis (car
        // longitudinal vs lateral, tyre-frame vs car-frame) each maps to is NOT
        // confirmed here, so don't assume fx = longitudinal without a live test that
        // correlates it against a known maneuver (e.g. fy should track LatAccel_g
        // under sustained cornering if it really is lateral force).
        public static double? TyreForceFxRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.fx != null && corner < p.fx.Length
                ? (double?)p.fx[corner]
                : null;

        public static double? TyreForceFyRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.fy != null && corner < p.fy.Length
                ? (double?)p.fy[corner]
                : null;

        // Self-aligning moment per corner.
        public static double? TyreMomentMzRaw(StatusDataBase d, string gameName, int corner) =>
            TryGetPhysics(d, gameName, out var p) && p.mz != null && corner < p.mz.Length
                ? (double?)p.mz[corner]
                : null;

        // Car-frame velocity vector (Single[3], not per-corner - `axis` is 0/1/2).
        // Which axis is longitudinal/lateral/vertical is UNCONFIRMED - don't assume
        // axis 0 = forward without a live test (e.g. axis magnitude should track
        // Speed_kmh closely under straight-line driving if it's the longitudinal one).
        public static double? LocalVelocityRaw(StatusDataBase d, string gameName, int axis) =>
            TryGetPhysics(d, gameName, out var p) && p.LocalVelocity != null && axis < p.LocalVelocity.Length
                ? (double?)p.LocalVelocity[axis]
                : null;
    }
}
