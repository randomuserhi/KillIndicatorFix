using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

using API;
using BetterHitMarkers.Patches;
using Player;

namespace BetterHitMarkers
{
    public static class Module
    {
        public const string GUID = "randomuserhi.KillIndicatorFix";
        public const string Name = "KillIndicatorFix";
        public const string Version = "0.0.1";
    }

    [BepInPlugin(Module.GUID, Module.Name, Module.Version)]
    internal class Entry : BasePlugin
    {
        public override void Load()
        {
            APILogger.Debug(Module.Name, "Loaded KillIndicatorFix");
            harmony = new Harmony(Module.GUID);
            harmony.PatchAll();
        }

        private Harmony harmony;
    }
}