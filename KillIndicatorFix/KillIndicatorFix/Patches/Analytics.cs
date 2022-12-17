﻿using HarmonyLib;

using GameData;
using UnityEngine.Analytics;

namespace BetterHitMarkers.Patches
{
    [HarmonyPatch(typeof(GameDataInit))]
    internal class GameDataInit_Patches
    {
        [HarmonyPatch(nameof(GameDataInit.Initialize))]
        [HarmonyWrapSafe]
        [HarmonyPostfix]
        public static void Initialize_Postfix()
        {
            Analytics.enabled = false;
        }
    }
}
