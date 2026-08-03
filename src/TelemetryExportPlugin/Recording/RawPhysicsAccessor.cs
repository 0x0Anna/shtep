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
        // Magnitude/scale is settled; SIGN (does +1.0 mean left or right) is still
        // unconfirmed - the live full-lock test done so far didn't note which
        // direction was held at which timestamp. Don't assume a sign without
        // another live test that does.
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
    }
}
