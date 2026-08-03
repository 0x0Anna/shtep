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
    }
}
