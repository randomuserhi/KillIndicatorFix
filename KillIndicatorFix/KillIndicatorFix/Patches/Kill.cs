﻿#define DEBUG

using UnityEngine;
using HarmonyLib;

using API;
using Enemies;
using Agents;
using static UnityEngine.UI.GridLayoutGroup;

namespace KillIndicatorFix.Patches
{
    /*
     * Tracks HP seperately rather than using client side __instance.Health since I'm unsure if messing with that will change client behaviour.
     */

    [HarmonyPatch]
    internal static class Kill
    {
        private struct tag
        {
            public float hp;
            public long timestamp;
            public Vector3 localHitPosition; // Store local position to prevent desync when enemy moves since hit position is relative to world not enemy.

            public tag(float hp, long timestamp, Vector3 localHitPosition)
            {
                this.hp = hp;
                this.timestamp = timestamp;
                this.localHitPosition = localHitPosition;
            }
        }

        private static Dictionary<int, tag> taggedEnemies = new Dictionary<int, tag>();
        private static Dictionary<int, long> markers = new Dictionary<int, long>();

        private static bool enabled = false;

        public static void OnRundownStart()
        {
#if DEBUG
            APILogger.Debug(Module.Name, "OnRundownStart => Reset Trackers and Markers.");
#endif

            markers.Clear();
            taggedEnemies.Clear();
        }

        private static void ShowKillMarker(int instanceID, Vector3 position)
        {
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            if (!markers.ContainsKey(instanceID))
            {
                enabled = true;
                GuiManager.CrosshairLayer?.ShowDeathIndicator(position);
                enabled = false;

                markers.Add(instanceID, now);
            }

            // Prevents the case where client fails to receive kill confirm from host so marker persists in dictionary
            // - Auto removes the marker if it has existed for longer than 3 seconds
            // TODO:: maybe make the time be some multiple or constant larger than tag timer specified at line 97,
            //        this way the config only needs 1 variable and its easier to understand.
            int[] keys = markers.Keys.ToArray();
            foreach (int id in keys)
            {
                if (now - markers[id] > 3000) markers.Remove(id);
            }
        }

        // TODO:: Change the method of detecting when an enemy dies via network => Either dont use EnemyAppearance and look at what SNet things GTFO uses (refer to GTFO-API Network Receive Hooks) or look
        //        at the proper OnDead event triggers etc (see how to avoid triggering it on head limb kill)
        //        Maybe look at ES_Dead or ES_DeadBase (probs ES_Dead => needs more testing)
        [HarmonyPatch(typeof(EnemyAppearance), nameof(EnemyAppearance.OnDead))]
        [HarmonyPrefix]
        public static void OnDead(EnemyAppearance __instance)
        {
#if DEBUG
            APILogger.Debug(Module.Name, "EnemyAppearance.OnDead");
#endif
            try
            {
                EnemyAgent owner = __instance.m_owner;
                int instanceID = owner.GetInstanceID();
                long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

                if (taggedEnemies.ContainsKey(instanceID))
                {
                    tag t = taggedEnemies[instanceID];

#if DEBUG
                    APILogger.Debug(Module.Name, $"Received kill update {now - t.timestamp} milliseconds after tag.");
#endif

                    if (now - t.timestamp < 1000) // TODO:: move this value to a config file, 1500 ms is generous, 1000 ms is probably most practical
                    {
                        if (!markers.ContainsKey(instanceID))
                        {
#if DEBUG
                            APILogger.Debug(Module.Name, $"Client side marker was not shown, showing server side one.");
#endif

                            enabled = true;
                            //GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.EyePosition);
                            GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.transform.position + t.localHitPosition);
                            enabled = false;
                        }
                        else
                        {
#if DEBUG
                            APILogger.Debug(Module.Name, $"Client side marker was shown, not showing server side one.");
#endif
                            markers.Remove(instanceID);
                        }
                    }
#if DEBUG
                    else APILogger.Debug(Module.Name, $"Client was no longer interested in this enemy, marker will not be shown.");
#endif

                    taggedEnemies.Remove(instanceID);
                }
            }
            catch { APILogger.Debug(Module.Name, "Something went wrong."); }
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.BulletDamage))]
        [HarmonyPrefix]
        public static void BulletDamage(Dam_EnemyDamageBase __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, bool allowDirectionalBonus, int limbID, float precisionMulti)
        {
            if (SNetwork.SNet.IsMaster) return;

            EnemyAgent owner = __instance.Owner;
            int instanceID = owner.GetInstanceID(); 
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Apply damage modifiers (head, occiput etc...)
            // TODO:: Confirmation / Testing on whether these damage numbers work for Tanks and Mother blobs (They are capped by Blob HP)
            float num = AgentModifierManager.ApplyModifier(owner, AgentModifier.ProjectileResistance, Mathf.Clamp(dam, 0, __instance.HealthMax));

            Vector3 localHit = position - owner.transform.position;
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = new tag(taggedEnemies[instanceID].hp - num, now, localHit);
            else taggedEnemies.Add(instanceID, new tag(__instance.HealthMax - num, now, localHit));

#if DEBUG
            APILogger.Debug(Module.Name, $"Bullet Damage: {num}");
            APILogger.Debug(Module.Name, $"Tracked current HP: {taggedEnemies[instanceID].hp}, [{owner.GetInstanceID()}]");
#endif

            if (taggedEnemies[instanceID].hp <= 0)
                ShowKillMarker(instanceID, position);
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.MeleeDamage))]
        [HarmonyPrefix]
        public static void MeleeDamage(Dam_EnemyDamageBase __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, int limbID, float staggerMulti, float precisionMulti, float sleeperMulti, bool skipLimbDestruction, DamageNoiseLevel damageNoiseLevel)
        {
            if (SNetwork.SNet.IsMaster) return;

            EnemyAgent owner = __instance.Owner;
            int instanceID = owner.GetInstanceID();
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Apply damage modifiers (head, occiput etc...)
            // TODO:: Confirmation / Testing on whether these damage numbers work for Tanks and Mother blobs (They are capped by Blob HP)
            float num = AgentModifierManager.ApplyModifier(owner, AgentModifier.MeleeResistance, Mathf.Clamp(dam, 0, __instance.DamageMax));

            Vector3 localHit = position - owner.transform.position;
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = new tag(taggedEnemies[instanceID].hp - num, now, localHit); 
            else taggedEnemies.Add(instanceID, new tag(__instance.HealthMax - num, now, localHit));

#if DEBUG
            APILogger.Debug(Module.Name, $"Melee Damage: {num}");
            APILogger.Debug(Module.Name, $"Tracked current HP: {taggedEnemies[instanceID].hp}, [{owner.GetInstanceID()}]");
#endif

            if (taggedEnemies[instanceID].hp <= 0)
                ShowKillMarker(instanceID, position);
        }

        [HarmonyPatch(typeof(CrosshairGuiLayer), nameof(CrosshairGuiLayer.ShowDeathIndicator))]
        [HarmonyPrefix]
        public static bool ShowDeathIndicator()
        {
            return SNetwork.SNet.IsMaster || enabled;
        }
    }
}