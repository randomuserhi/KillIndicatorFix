using API;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

// NOTE(randomuserhi): This mod alters the HP values of the enemies directly, this means that other mods that do the same
//                     will conflict with this mod. If you wish to make a mod compatible with this mod, you need to track
//                     enemy health seperately to the internal system.

namespace KillIndicatorFix.BepInEx {
    [BepInPlugin(Module.GUID, Module.Name, Module.Version)]
    internal class Entry : BasePlugin {
        public override void Load() {
            APILogger.Debug($"Loaded {Module.Name} {Module.Version}");
            harmony = new Harmony(Module.GUID);
            harmony.PatchAll();

            APILogger.Debug("Debug is " + (ConfigManager.Debug ? "Enabled" : "Disabled"));

            RundownManager.add_OnExpeditionGameplayStarted((Action)Patches.Kill.OnRundownStart);
        }

        private Harmony? harmony;
    }
}