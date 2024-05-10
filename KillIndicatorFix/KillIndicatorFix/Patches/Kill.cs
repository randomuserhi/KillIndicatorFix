using Agents;
using API;
using Enemies;
using HarmonyLib;
using KillIndicatorFix.BepInEx;
using Player;
using UnityEngine;

namespace KillIndicatorFix.Patches {
    [HarmonyPatch]
    internal static class Kill {
        private struct Tag {
            public long timestamp;
            public Vector3 localHitPosition; // Store local position to prevent desync when enemy moves since hit position is relative to world not enemy.
            public ItemEquippable item;

            public Tag(long timestamp, Vector3 localHitPosition, ItemEquippable item) {
                this.timestamp = timestamp;
                this.localHitPosition = localHitPosition;
                this.item = item;
            }
        }

        private static Dictionary<int, Tag> taggedEnemies = new Dictionary<int, Tag>();
        private static Dictionary<int, long> markers = new Dictionary<int, long>();

        public static void OnRundownStart() {
            APILogger.Debug("OnRundownStart => Reset Trackers and Markers.");

            Mine.mineOwners.Clear();
            markers.Clear();
            taggedEnemies.Clear();
        }

        [HarmonyPatch(typeof(EnemyBehaviour), nameof(EnemyBehaviour.ChangeState), new Type[] { typeof(EB_States) })]
        [HarmonyPrefix]
        public static void OnDead(EnemyBehaviour __instance, EB_States state) {
            if (SNetwork.SNet.IsMaster) return;
            if (__instance.m_currentStateName == state || state != EB_States.Dead) return;

            APILogger.Debug("EnemyAppearance.OnDead");

            try {
                EnemyAgent owner = __instance.m_ai.m_enemyAgent;
                int instanceID = owner.GetInstanceID();
                long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

                if (taggedEnemies.ContainsKey(instanceID)) {
                    Tag t = taggedEnemies[instanceID];

                    if (t.timestamp <= now)
                        APILogger.Debug($"Received kill update {now - t.timestamp} milliseconds after tag.");
                    else
                        APILogger.Debug($"Received kill update for enemy that was tagged in the future? Possibly long overflow...");

                    if (t.timestamp <= now && now - t.timestamp < ConfigManager.TagBufferPeriod) {
                        if (!markers.ContainsKey(instanceID)) {
                            APILogger.Debug($"Client side marker was not shown, showing server side one.");

                            KillIndicatorFix.Kill.TriggerOnEnemyDead(owner, PlayerManager.GetLocalPlayerAgent(), t.item, now - t.timestamp);
                            KillIndicatorFix.Kill.TriggerOnKillIndicator(owner, t.item, now - t.timestamp);
                            GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.transform.position + t.localHitPosition);
                        } else {
                            APILogger.Debug($"Client side marker was shown, not showing server side one.");

                            markers.Remove(instanceID);
                        }
                    } else {
                        KillIndicatorFix.Kill.TriggerOnEnemyDead(owner, null, null, 0);
                        APILogger.Debug($"Client was no longer interested in this enemy, marker will not be shown.");
                    }

                    taggedEnemies.Remove(instanceID);
                } else {
                    KillIndicatorFix.Kill.TriggerOnEnemyDead(owner, null, null, 0);
                }
            } catch { APILogger.Debug("Something went wrong."); }
        }

        // Manage OnEnemyDead API
        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveBulletDamage))]
        [HarmonyPrefix]
        public static void Host_BulletDamage(Dam_EnemyDamageBase __instance, pBulletDamageData data) {
            if (!SNetwork.SNet.IsMaster) return;
            if (!__instance.Owner.Alive) return;

            Agent sourceAgent;
            if (!data.source.TryGet(out sourceAgent)) {
                APILogger.Debug($"Unable to find source agent.");
                return;
            }

            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type}.");
                return;
            }

            float damage = AgentModifierManager.ApplyModifier(__instance.Owner, AgentModifier.ProjectileResistance, data.damage.Get(__instance.HealthMax));
            if (__instance.Health <= damage) {
                if (sentryShot && PlayerBackpackManager.TryGetItem(p.Owner, InventorySlot.GearClass, out BackpackItem bpItem)) {
                    KillIndicatorFix.Kill.TriggerOnEnemyDead(__instance.Owner, p, bpItem.Instance.Cast<ItemEquippable>(), 0);
                } else if (PlayerBackpackManager.TryGetItem(p.Owner, InventorySlot.GearStandard, out BackpackItem standardItem) && PlayerBackpackManager.TryGetItem(p.Owner, InventorySlot.GearSpecial, out BackpackItem specialItem)) {
                    ItemEquippable item = p.Inventory.WieldedItem;
                    if (item.GearIDRange.PublicGearName == standardItem.GearIDRange.PublicGearName) {
                        KillIndicatorFix.Kill.TriggerOnEnemyDead(__instance.Owner, p, standardItem.Instance.Cast<ItemEquippable>(), 0);
                    } else {
                        KillIndicatorFix.Kill.TriggerOnEnemyDead(__instance.Owner, p, specialItem.Instance.Cast<ItemEquippable>(), 0);
                    }
                } else {
                    APILogger.Debug("Failed to get bullet weapon from owner.");
                }
            }
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveMeleeDamage))]
        [HarmonyPrefix]
        public static void Host_MeleeDamage(Dam_EnemyDamageBase __instance, pFullDamageData data) {
            if (!SNetwork.SNet.IsMaster) return;
            if (!__instance.Owner.Alive) return;

            Agent sourceAgent;
            if (!data.source.TryGet(out sourceAgent)) {
                APILogger.Debug($"Unable to find source agent.");
                return;
            }

            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type}.");
                return;
            }

            float damage = AgentModifierManager.ApplyModifier(__instance.Owner, AgentModifier.MeleeResistance, data.damage.Get(__instance.DamageMax));
            if (__instance.Owner.Locomotion.CurrentStateEnum == ES_StateEnum.Hibernate) {
                damage *= data.sleeperMulti.Get(10f);
            }
            if (__instance.Health <= damage) {
                if (PlayerBackpackManager.TryGetItem(p.Owner, InventorySlot.GearMelee, out BackpackItem bpItem)) {
                    KillIndicatorFix.Kill.TriggerOnEnemyDead(__instance.Owner, p, bpItem.Instance.Cast<ItemEquippable>(), 0);
                } else {
                    APILogger.Debug("Failed to get melee weapon from owner.");
                }
            }
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ReceiveExplosionDamage))]
        [HarmonyPrefix]
        public static void Host_ExplosiveDamage(Dam_EnemyDamageBase __instance, pExplosionDamageData data) {
            if (!SNetwork.SNet.IsMaster) return;
            if (!__instance.Owner.Alive) return;

            PlayerAgent? p = Mine.currentMineOwner;
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Unable to find source agent.");
                return;
            }

            if (__instance.Health <= data.damage.Get(__instance.HealthMax)) {
                if (PlayerBackpackManager.TryGetItem(p.Owner, InventorySlot.GearClass, out BackpackItem bpItem)) {
                    KillIndicatorFix.Kill.TriggerOnEnemyDead(__instance.Owner, p, bpItem.Instance.Cast<ItemEquippable>(), 0);
                } else {
                    APILogger.Debug("Failed to get mine deployer from mine owner.");
                }
            }
        }

        // Determine if the shot was performed by a sentry or player
        internal static bool sentryShot = false;
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.FireBullet))]
        [HarmonyPrefix]
        private static void Prefix_SentryGunFiringBullet(SentryGunInstance_Firing_Bullets __instance, bool doDamage, bool targetIsTagged) {
            if (!doDamage) return;
            sentryShot = true;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.FireBullet))]
        [HarmonyPostfix]
        private static void Postfix_SentryGunFiringBullet() {
            sentryShot = false;
        }
        // Special case for shotgun sentry
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireShotgunSemi))]
        [HarmonyPrefix]
        private static void Prefix_ShotgunSentryFiring(SentryGunInstance_Firing_Bullets __instance, bool isMaster, bool targetIsTagged) {
            if (!isMaster) return;
            sentryShot = true;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireShotgunSemi))]
        [HarmonyPostfix]
        private static void Postfix_ShotgunSentryFiring() {
            sentryShot = false;
        }
        // Send hitmarkers to clients from sentry shots
        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.BulletDamage))]
        [HarmonyPrefix]
        public static void EnemyLimb_BulletDamage(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, Vector3 normal, bool allowDirectionalBonus, float staggerMulti, float precisionMulti) {
            if (!SNetwork.SNet.IsMaster) return;

            if (!sentryShot) return; // Check that it was a sentry that shot
            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type}.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot
            if (sourceAgent.IsLocallyOwned) return; // Check player is someone else

            Dam_EnemyDamageBase m_base = __instance.m_base;
            EnemyAgent owner = m_base.Owner;
            float num = dam;
            if (!m_base.IsImortal) {
                num = __instance.ApplyWeakspotAndArmorModifiers(dam, precisionMulti);
                num = __instance.ApplyDamageFromBehindBonus(num, position, direction);
                bool willDie = m_base.WillDamageKill(num);
                Network.SendHitIndicator(owner, (byte)__instance.m_limbID, p, num > dam, willDie, position, __instance.m_armorDamageMulti < 1f);
            } else {
                Network.SendHitIndicator(owner, (byte)__instance.m_limbID, p, num > dam, willDie: false, position, true);
            }
        }
        [HarmonyPatch(typeof(Dam_PlayerDamageBase), nameof(Dam_PlayerDamageBase.ReceiveBulletDamage))]
        [HarmonyPrefix]
        public static void Player_BulletDamage(Dam_PlayerDamageBase __instance, pBulletDamageData data) {
            if (!SNetwork.SNet.IsMaster) return;

            if (!data.source.TryGet(out Agent sourceAgent)) return;

            if (!sentryShot) return; // Check that it was a sentry that shot
            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type}.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot
            if (sourceAgent.IsLocallyOwned) return; // Check player is someone else

            PlayerAgent owner = __instance.Owner;

            if (owner != p) Network.SendHitIndicator(owner, 0, p, false, false, Vector3.zero, false);
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.BulletDamage))]
        [HarmonyPrefix]
        public static void BulletDamage(Dam_EnemyDamageBase __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, bool allowDirectionalBonus, int limbID, float precisionMulti) {
            if (SNetwork.SNet.IsMaster) return;
            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type}.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot

            EnemyAgent owner = __instance.Owner;
            int instanceID = owner.GetInstanceID();
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            float num = AgentModifierManager.ApplyModifier(owner, AgentModifier.ProjectileResistance, Mathf.Clamp(dam, 0, __instance.HealthMax));
            __instance.Health -= num;

            Vector3 localHit = position - owner.transform.position;
            Tag t = new Tag(now, localHit, p.Inventory.WieldedItem);
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = t;
            else taggedEnemies.Add(instanceID, t);

            APILogger.Debug($"{num} Bullet Damage done by {p.PlayerName}. IsBot: {p.Owner.IsBot}");
            APILogger.Debug($"Tracked current HP: {__instance.Health}, [{owner.GetInstanceID()}]");
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.MeleeDamage))]
        [HarmonyPrefix]
        public static void MeleeDamage(Dam_EnemyDamageBase __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, int limbID, float staggerMulti, float precisionMulti, float sleeperMulti, bool skipLimbDestruction, DamageNoiseLevel damageNoiseLevel) {
            if (SNetwork.SNet.IsMaster) return;
            PlayerAgent? p = sourceAgent.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent, damage was done by agent of type: {sourceAgent.m_type}.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot

            EnemyAgent owner = __instance.Owner;
            int instanceID = owner.GetInstanceID();
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Apply damage modifiers (head, occiput etc...)
            float num = AgentModifierManager.ApplyModifier(owner, AgentModifier.MeleeResistance, Mathf.Clamp(dam, 0, __instance.DamageMax));
            if (__instance.Owner.Locomotion.CurrentStateEnum == ES_StateEnum.Hibernate) {
                num *= sleeperMulti;
            }
            __instance.Health -= num;

            Vector3 localHit = position - owner.transform.position;
            Tag t = new Tag(now, localHit, p.Inventory.WieldedItem);
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = t;
            else taggedEnemies.Add(instanceID, t);

            APILogger.Debug($"Melee Damage: {num}");
            APILogger.Debug($"Tracked current HP: {__instance.Health}, [{owner.GetInstanceID()}]");
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.ShowHitIndicator))]
        [HarmonyPrefix]
        public static void ShowDeathIndicator(Dam_EnemyDamageLimb __instance, bool hitWeakspot, bool willDie, Vector3 position, bool hitArmor) {
            EnemyAgent owner = __instance.m_base.Owner;
            int instanceID = owner.GetInstanceID();
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Prevents the case where client fails to receive kill confirm from host so marker persists in dictionary
            int[] keys = markers.Keys.ToArray();
            foreach (int id in keys) {
                if (now - markers[id] > ConfigManager.MarkerLifeTime) markers.Remove(id);
            }

            // Only call if GuiManager.CrosshairLayer.ShowDeathIndicator(position); is going to get called (condition is taken from source)
            if (willDie && !__instance.m_base.DeathIndicatorShown) {
                if (!markers.ContainsKey(instanceID)) {
                    PlayerAgent player = PlayerManager.GetLocalPlayerAgent();
                    ItemEquippable item = player.Inventory.WieldedItem;
                    if (sentryShot && PlayerBackpackManager.TryGetItem(player.Owner, InventorySlot.GearClass, out BackpackItem bpItem)) {
                        item = bpItem.Instance.Cast<ItemEquippable>();
                    }
                    if (!SNetwork.SNet.IsMaster && !sentryShot) KillIndicatorFix.Kill.TriggerOnEnemyDead(owner, player, item, 0);
                    KillIndicatorFix.Kill.TriggerOnKillIndicator(owner, item, 0);
                    markers.Add(instanceID, now);
                } else APILogger.Debug($"Marker for enemy was already shown. This should not happen.");
            }
        }
    }
}