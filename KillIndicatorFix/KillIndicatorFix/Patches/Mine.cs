using API;
using HarmonyLib;
using Player;
using SNetwork;

namespace KillIndicatorFix.Patches {
    [HarmonyPatch]
    internal static class Mine {
        public static PlayerAgent? currentMineOwner = null;
        public static Dictionary<int, PlayerAgent> mineOwners = new Dictionary<int, PlayerAgent>();

        [HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.OnSpawn))]
        [HarmonyPostfix]
        private static void Spawn(MineDeployerInstance __instance, pItemSpawnData spawnData) {
            SNet_Player player;
            if (spawnData.owner.TryGetPlayer(out player)) {
                PlayerAgent owner = player.PlayerAgent.Cast<PlayerAgent>();
                APILogger.Debug($"Mine Spawn ID - {spawnData.itemData.itemID_gearCRC}");
                switch (spawnData.itemData.itemID_gearCRC) {
                case 125: // Mine deployer mine
                    mineOwners.Add(__instance.gameObject.GetInstanceID(), owner);
                    break;
                }
            }
        }

        [HarmonyPatch(typeof(MineDeployerInstance), nameof(MineDeployerInstance.SyncedPickup))]
        [HarmonyPrefix]
        private static void SyncedPickup(MineDeployerInstance __instance) {
            mineOwners.Remove(__instance.gameObject.GetInstanceID());
        }

        [HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), nameof(MineDeployerInstance_Detonate_Explosive.DoExplode))]
        [HarmonyPrefix]
        private static void Prefix_Detonate_Explosive(MineDeployerInstance_Detonate_Explosive __instance) {
            int instance = __instance.gameObject.GetInstanceID();
            if (mineOwners.ContainsKey(instance)) {
                currentMineOwner = mineOwners[instance];
            } else {
                currentMineOwner = null;
            }
            mineOwners.Remove(instance);
        }

        [HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), nameof(MineDeployerInstance_Detonate_Explosive.DoExplode))]
        [HarmonyPostfix]
        private static void Postfix_Detonate_Explosive(MineDeployerInstance_Detonate_Explosive __instance) {
            currentMineOwner = null;
            mineOwners.Remove(__instance.gameObject.GetInstanceID());
        }
    }
}
