using Agents;
using API;
using Enemies;
using HarmonyLib;
using KillIndicatorFix.BepInEx;
using Player;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace KillIndicatorFix.Patches {
    [HarmonyPatch]
    internal static class Kill {
        public class Tag {
            public long timestamp;
            public Vector3 localHitPosition; // Store local position to prevent desync when enemy moves since hit position is relative to world not enemy.
            public ItemEquippable? item = null;
            public float health;

            public Tag(float health) {
                this.health = health;
            }
        }

        public static Dictionary<ushort, Tag> taggedEnemies = new Dictionary<ushort, Tag>();

        // Used to handle UFloat conversion of damage
        private static pFullDamageData fullDamageData = new();

        public static void OnRundownStart() {
            APILogger.Debug("OnRundownStart => Reset Trackers and Markers.");

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
                ushort id = owner.GlobalID;
                long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

                if (taggedEnemies.ContainsKey(id)) {
                    Tag t = taggedEnemies[id];

                    if (t.timestamp <= now)
                        APILogger.Debug($"Received kill update {now - t.timestamp} milliseconds after tag.");
                    else
                        APILogger.Debug($"Received kill update for enemy that was tagged in the future? Possibly long overflow...");

                    if (t.timestamp <= now && now - t.timestamp < ConfigManager.TagBufferPeriod) {
                        if (!owner.Damage.DeathIndicatorShown) {
                            APILogger.Debug($"Client side marker was not shown, showing server side one.");

                            KillIndicatorFix.Kill.TriggerOnKillIndicator(owner, t.item, now - t.timestamp);
                            GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.transform.position + t.localHitPosition);
                            owner.Damage.DeathIndicatorShown = true;
                        } else {
                            APILogger.Debug($"Client side marker was shown, not showing server side one.");
                        }
                    } else {
                        APILogger.Debug($"Client was no longer interested in this enemy, marker will not be shown.");
                    }

                    taggedEnemies.Remove(id);
                }
            } catch { APILogger.Debug("Something went wrong."); }
        }

        // Determine if the shot was performed by a sentry or player
        internal static bool sentryShot = false;
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.FireBullet))]
        [HarmonyPrefix]
        private static void Prefix_SentryGunFiringBullet(SentryGunInstance_Firing_Bullets __instance, bool doDamage, bool targetIsTagged) {
            if (!doDamage) return;
            if (__instance.m_core.Owner == null) return;
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
            if (__instance.m_core.Owner == null) return;
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
        [HarmonyPriority(Priority.Last)]
        public static void EnemyLimb_BulletDamage(Dam_EnemyDamageLimb __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, Vector3 normal, bool allowDirectionalBonus, float staggerMulti, float precisionMulti) {
            if (!SNetwork.SNet.IsMaster) return;
            if (sourceAgent == null) return;
            if (__instance.m_base.Health <= 0) return;

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
        [HarmonyPriority(Priority.Last)]
        public static void Player_BulletDamage(Dam_PlayerDamageBase __instance, pBulletDamageData data) {
            if (!SNetwork.SNet.IsMaster) return;

            if (!data.source.TryGet(out Agent sourceAgent)) return;
            if (sourceAgent == null) return;

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void CheckMismatch(Tag t, Dam_EnemyDamageBase enemy) {
            if (enemy.Health < t.health) {
                t.health = enemy.Health;
            }
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.BulletDamage))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        public static void BulletDamage(Dam_EnemyDamageBase __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, bool allowDirectionalBonus, int limbID, float precisionMulti) {
            if (SNetwork.SNet.IsMaster) return;
            if (sourceAgent == null) return;

            PlayerAgent? p = sourceAgent?.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot

            EnemyAgent owner = __instance.Owner;
            ushort id = owner.GlobalID;
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            if (!taggedEnemies.ContainsKey(id)) taggedEnemies.Add(id, new Tag(__instance.Health));
            Tag t = taggedEnemies[id];
            t.timestamp = now;
            t.localHitPosition = position - owner.transform.position;
            t.item = p.Inventory.WieldedItem;
            CheckMismatch(t, __instance);

            fullDamageData.damage.Set(dam, __instance.HealthMax);
            float num = AgentModifierManager.ApplyModifier(owner, AgentModifier.ProjectileResistance, fullDamageData.damage.Get(__instance.HealthMax));
            t.health -= num;

            // Show indicator when tracked health assumes enemy is dead
            if (t.health <= 0 && !__instance.DeathIndicatorShown) {
                KillIndicatorFix.Kill.TriggerOnKillIndicator(owner, t.item, now - t.timestamp);
                GuiManager.CrosshairLayer?.ShowDeathIndicator(position);
                __instance.DeathIndicatorShown = true;
            }

            APILogger.Debug($"{num} Bullet Damage done by {p.PlayerName}. IsBot: {p.Owner.IsBot}");
            APILogger.Debug($"Tracked current HP: {t.health}, [{owner.GlobalID}]");
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.MeleeDamage))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        public static void MeleeDamage(Dam_EnemyDamageBase __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, int limbID, float staggerMulti, float precisionMulti, float backstabberMulti, float sleeperMulti, bool skipLimbDestruction, DamageNoiseLevel damageNoiseLevel) {
            if (SNetwork.SNet.IsMaster) return;
            if (sourceAgent == null) return;

            PlayerAgent? p = sourceAgent?.TryCast<PlayerAgent>();
            if (p == null) // Check damage was done by a player
            {
                APILogger.Debug($"Could not find PlayerAgent.");
                return;
            }
            if (p.Owner.IsBot) return; // Check player isnt a bot

            EnemyAgent owner = __instance.Owner;
            ushort id = owner.GlobalID;
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            if (!taggedEnemies.ContainsKey(id)) taggedEnemies.Add(id, new Tag(__instance.Health));
            Tag t = taggedEnemies[id];
            t.timestamp = now;
            t.localHitPosition = position - owner.transform.position;
            t.item = p.Inventory.WieldedItem;
            CheckMismatch(t, __instance);

            // Update timestamp
            t.timestamp = now;

            // Apply damage modifiers (head, occiput etc...)
            fullDamageData.damage.Set(dam, __instance.DamageMax);
            float num = AgentModifierManager.ApplyModifier(owner, AgentModifier.MeleeResistance, Dam_EnemyDamageBase.RoundDamage(fullDamageData.damage.Get(__instance.DamageMax)));
            t.health -= num;

            // Show indicator when tracked health assumes enemy is dead
            if (t.health <= 0 && !__instance.DeathIndicatorShown) {
                KillIndicatorFix.Kill.TriggerOnKillIndicator(owner, t.item, now - t.timestamp);
                GuiManager.CrosshairLayer?.ShowDeathIndicator(position);
                __instance.DeathIndicatorShown = true;
            }

            APILogger.Debug($"Melee Damage: {num}");
            APILogger.Debug($"Tracked current HP: {t.health}, [{owner.GlobalID}]");
        }

        [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.ShowHitIndicator))]
        [HarmonyPrefix]
        public static void ShowDeathIndicator(Dam_EnemyDamageLimb __instance, bool hitWeakspot, bool willDie, Vector3 position, bool hitArmor) {
            EnemyAgent owner = __instance.m_base.Owner;

            // Only call if GuiManager.CrosshairLayer.ShowDeathIndicator(position); is going to get called (condition is taken from source)
            if (willDie && !__instance.m_base.DeathIndicatorShown) {
                PlayerAgent player = PlayerManager.GetLocalPlayerAgent();
                ItemEquippable item = player.Inventory.WieldedItem;
                if (sentryShot && PlayerBackpackManager.TryGetItem(player.Owner, InventorySlot.GearClass, out BackpackItem bpItem)) {
                    item = bpItem.Instance.Cast<ItemEquippable>();
                }
                KillIndicatorFix.Kill.TriggerOnKillIndicator(owner, item, 0);
            }
        }
    }
}