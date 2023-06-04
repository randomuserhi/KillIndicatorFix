using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

using API;

// NOTE(randomuserhi): This mod alters the HP values of the enemies directly, this means that other mods that do the same
//                     will conflict with this mod. If you wish to make a mod compatible with this mod, you need to track
//                     enemy health seperately to the internal system.

namespace KillIndicatorFix
{
    public static class Module
    {
        public const string GUID = "randomuserhi.KillIndicatorFix";
        public const string Name = "KillIndicatorFix";
        public const string Version = "0.0.9";
    }

    [BepInPlugin(Module.GUID, Module.Name, Module.Version)]
    internal class Entry : BasePlugin
    {
        public override void Load()
        {
            APILogger.Debug(Module.Name, $"Loaded {Module.Name} {Module.Version}");
            harmony = new Harmony(Module.GUID);
            harmony.PatchAll();

            APILogger.Debug(Module.Name, "Debug is " + (ConfigManager.Debug ? "Enabled" : "Disabled"));

            RundownManager.add_OnExpeditionGameplayStarted((Action)KillIndicatorFix.Patches.Kill.OnRundownStart);
        }

        private Harmony? harmony;
    }
}