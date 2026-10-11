using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SevenRail
{
    /// 7 Days moves players and zombies against block bounds from World.GetCollidingBounds, not Unity colliders.
    /// Adding the train's boxes there makes it solid for everyone: walk on its roof, zombies bump into it.
    [HarmonyPatch(typeof(World), nameof(World.GetCollidingBounds))]
    static class TrainCollision
    {
        static void Postfix(Entity _entity, Bounds _aabb, List<Bounds> __result) => Runner.Instance?.AddCollision(_entity, _aabb, __result);
    }

    /// Aboard the train (the cockpit or a wagon room) counts as sheltered and enclosed: EverRail's heated train keeps
    /// 7 Days' biome hazards, weather and temperature out, the same checks a house passes.
    [HarmonyPatch(typeof(EntityPlayerLocal), nameof(EntityPlayerLocal.ShelterFrameUpdate))]
    static class TrainShelter
    {
        static void Postfix(EntityPlayerLocal __instance)
        {
            if (Runner.Instance == null || !Runner.Instance.Aboard(__instance)) return;
            __instance.shelterPercent = 1f;
            __instance.Stats.m_amountEnclosed = 1f;
        }
    }
}
