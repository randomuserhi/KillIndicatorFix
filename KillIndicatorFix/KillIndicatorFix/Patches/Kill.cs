using UnityEngine;
using HarmonyLib;

using API;
using Enemies;
using Agents;
using Player;
using PlayFab.ClientModels;

// TODO:: Add config value for delay allowed between client hit marker and server

namespace KillIndicatorFix.Patches
{
    /*
     * Tracks HP seperately rather than using client side __instance.Health since I'm unsure if messing with that will change client behaviour.
     */

    [HarmonyPatch]
    internal static class Kill
    {
        private struct Tag
        {
            public long timestamp;
            public Vector3 localHitPosition; // Store local position to prevent desync when enemy moves since hit position is relative to world not enemy.

            public Tag(long timestamp, Vector3 localHitPosition)
            {
                this.timestamp = timestamp;
                this.localHitPosition = localHitPosition;
            }
        }

        private static Dictionary<int, Tag> taggedEnemies = new Dictionary<int, Tag>();
        private static Dictionary<int, long> markers = new Dictionary<int, long>();

        public static void OnRundownStart()
        {
            if (ConfigManager.Debug) APILogger.Debug(Module.Name, "OnRundownStart => Reset Trackers and Markers.");

            markers.Clear();
            taggedEnemies.Clear();
        }

        // TODO:: Change the method of detecting when an enemy dies via network => Either dont use EnemyAppearance and look at what SNet things GTFO uses (refer to GTFO-API Network Receive Hooks) or look
        //        at the proper OnDead event triggers etc (see how to avoid triggering it on head limb kill)
        //        Maybe look at ES_Dead or ES_DeadBase (probs ES_Dead => needs more testing)
        [HarmonyPatch(typeof(EnemyAppearance), nameof(EnemyAppearance.OnDead))]
        [HarmonyPrefix]
        public static void OnDead(EnemyAppearance __instance)
        {
            if (SNetwork.SNet.IsMaster) return;

            if (ConfigManager.Debug) APILogger.Debug(Module.Name, "EnemyAppearance.OnDead");

            try
            {
                EnemyAgent owner = __instance.m_owner;
                int instanceID = owner.GetInstanceID();
                long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

                if (taggedEnemies.ContainsKey(instanceID))
                {
                    Tag t = taggedEnemies[instanceID];

                    if (ConfigManager.Debug)
                        if (t.timestamp <= now)
                            APILogger.Debug(Module.Name, $"Received kill update {now - t.timestamp} milliseconds after tag.");
                        else 
                            APILogger.Debug(Module.Name, $"Received kill update for enemy that was tagged in the future? Possibly long overflow...");

                    if (t.timestamp <= now && now - t.timestamp < ConfigManager.TagBufferPeriod) // TODO:: move this value to a config file, 1500 ms is generous, 1000 ms is probably most practical
                    {
                        if (!markers.ContainsKey(instanceID))
                        {
                            if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Client side marker was not shown, showing server side one.");

                            //GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.EyePosition);
                            GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.transform.position + t.localHitPosition);
                        }
                        else
                        {
                            if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Client side marker was shown, not showing server side one.");

                            markers.Remove(instanceID);
                        }
                    }
                    else if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Client was no longer interested in this enemy, marker will not be shown.");

                    taggedEnemies.Remove(instanceID);
                }
            }
            catch { APILogger.Debug(Module.Name, "Something went wrong."); }
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.BulletDamage))]
        [HarmonyPrefix]
        public static void BulletDamage(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, Vector3 normal, bool allowDirectionalBonus, float staggerMulti, float precisionMulti)
        {
            if (SNetwork.SNet.IsMaster) return;
            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type.ToString()}.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot

            Dam_EnemyDamageBase m_base = __instance.m_base;
            EnemyAgent owner = m_base.Owner;
            int instanceID = owner.GetInstanceID();
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Apply damage modifiers (head, occiput etc...)
            float num = __instance.ApplyWeakspotAndArmorModifiers(dam, precisionMulti);
            num = __instance.ApplyDamageFromBehindBonus(num, position, direction);
            m_base.Health -= num;

            Vector3 localHit = position - owner.transform.position;
            Tag t = new Tag(now, localHit);
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = t;
            else taggedEnemies.Add(instanceID, t);

            if (ConfigManager.Debug)
            {
                APILogger.Debug(Module.Name, $"{num} Bullet Damage done by {p.PlayerName}. IsBot: {p.Owner.IsBot}");
                APILogger.Debug(Module.Name, $"Tracked current HP: {m_base.Health}, [{owner.GetInstanceID()}]");
            }
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.MeleeDamage))]
        [HarmonyPrefix]
        public static void MeleeDamage(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, int limbID, float staggerMulti, float precisionMulti, float environmentMulti, float backstabberMulti, float sleeperMulti, bool skipLimbDestruction, DamageNoiseLevel damageNoiseLevel)
        {
            if (SNetwork.SNet.IsMaster) return;
            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type.ToString()}.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot

            Dam_EnemyDamageBase m_base = __instance.m_base;
            EnemyAgent owner = m_base.Owner;
            int instanceID = owner.GetInstanceID();
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Apply damage modifiers (head, occiput etc...)
            float num = __instance.ApplyWeakspotAndArmorModifiers(dam, precisionMulti);
            num = __instance.ApplyDamageFromBehindBonus(num, position, direction, backstabberMulti);
            m_base.Health -= num;

            Vector3 localHit = position - owner.transform.position;
            Tag t = new Tag(now, localHit);
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = t; 
            else taggedEnemies.Add(instanceID, t);

            if (ConfigManager.Debug)
            {
                APILogger.Debug(Module.Name, $"Melee Damage: {num}");
                APILogger.Debug(Module.Name, $"Tracked current HP: {m_base.Health}, [{owner.GetInstanceID()}]");
            }
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.ShowHitIndicator))]
        [HarmonyPrefix]
        public static void ShowDeathIndicator(Dam_EnemyDamageLimb __instance, bool hitWeakspot, bool willDie, Vector3 position, bool hitArmor)
        {
            EnemyAgent owner = __instance.m_base.Owner;
            int instanceID = owner.GetInstanceID();
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Prevents the case where client fails to receive kill confirm from host so marker persists in dictionary
            // - Auto removes the marker if it has existed for longer than 3 seconds
            // TODO:: maybe make the time be some multiple or constant larger than tag timer specified at line 101,
            //        this way the config only needs 1 variable and its easier to understand.
            int[] keys = markers.Keys.ToArray();
            foreach (int id in keys)
            {
                if (now - markers[id] > ConfigManager.MarkerLifeTime) markers.Remove(id);
            }

            // Only call if GuiManager.CrosshairLayer.ShowDeathIndicator(position); is going to get called (condition is taken from source)
            if (willDie && !__instance.m_base.DeathIndicatorShown)
            {
                if (!markers.ContainsKey(instanceID)) markers.Add(instanceID, now);
                else if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Marker for enemy was already shown. This should not happen.");
            }
        }
    }
}