using UnityEngine;
using HarmonyLib;

using API;
using Enemies;
using Agents;
using Gear;
using FX_EffectSystem;
using AK;
using Player;

// TODO:: Add config value for delay allowed between client hit marker and server

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
            public long timestamp;
            public Vector3 localHitPosition; // Store local position to prevent desync when enemy moves since hit position is relative to world not enemy.

            public tag(long timestamp, Vector3 localHitPosition)
            {
                this.timestamp = timestamp;
                this.localHitPosition = localHitPosition;
            }
        }

        private static Dictionary<int, tag> taggedEnemies = new Dictionary<int, tag>();
        private static Dictionary<int, long> markers = new Dictionary<int, long>();

        public static void OnRundownStart()
        {
            if (ConfigManager.Debug) APILogger.Debug(Module.Name, "OnRundownStart => Reset Trackers and Markers.");

            markers.Clear();
            taggedEnemies.Clear();

            if (ConfigManager.Debug)
            {
                BackpackItem item = PlayerBackpackManager.GetLocalItem(InventorySlot.GearClass);
                APILogger.Debug(Module.Name, $"Player has item of ID {item.ItemID} called {item.Name} with gear name {item.GearIDRange.PublicGearName}");
            }
        }

        // TODO:: Test that this works => new patch suggested by Dex
        [HarmonyPatch(typeof(ES_Hitreact), nameof(ES_Hitreact.RecieveStateData))]
        [HarmonyPrefix]
        public static void OnHitReact(ES_Hitreact __instance, pES_HitreactData data)
        {
            if (SNetwork.SNet.IsMaster) return;

            if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Received ES_HitreactType.{data.ReactionType.ToString()} at position {data.StartPosition}");//, Sentry is at {localSentryPosition} and placed : {localPlayerSentryDeployed}");

            if (data.ReactionType != ES_HitreactType.ToDeath) return;

            try
            {
                EnemyAgent owner = __instance.m_enemyAgent;
                int instanceID = owner.GetInstanceID();
                long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

                if (taggedEnemies.ContainsKey(instanceID))
                {
                    tag t = taggedEnemies[instanceID];

                    if (ConfigManager.Debug)
                        if (t.timestamp <= now)
                            APILogger.Debug(Module.Name, $"Received kill update {now - t.timestamp} milliseconds after tag.");
                        else 
                            APILogger.Debug(Module.Name, $"Received kill update for enemy that was tagged in the future? Possibly long overflow...");

                    if (t.timestamp <= now && now - t.timestamp < ConfigManager.TagBufferPeriod)
                    {
                        if (!markers.ContainsKey(instanceID))
                        {
                            if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Client side marker was not shown, showing server side one.");

                            //GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.EyePosition);
                            //GuiManager.CrosshairLayer?.ShowDeathIndicator(owner.transform.position + t.localHitPosition);
                            GuiManager.CrosshairLayer?.ShowDeathIndicator(data.DamagePos); //TODO:: test that this works => If so then remove t.localHitPosition and other struct clutter
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

        [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.BulletDamage))]
        [HarmonyPrefix]
        public static bool BulletDamage(Dam_EnemyDamageBase __instance, float dam, Agent sourceAgent, Vector3 position, Vector3 direction, bool allowDirectionalBonus, int limbID, float precisionMulti)
        {
            if (SNetwork.SNet.IsMaster) return true;

            EnemyAgent owner = __instance.Owner;
            int instanceID = owner.GetInstanceID(); 
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

            // Apply damage modifiers (head, occiput etc...)
            float num = AgentModifierManager.ApplyModifier(owner, AgentModifier.ProjectileResistance, Mathf.Clamp(dam, 0, __instance.HealthMax));
            __instance.Health -= num;

            Vector3 localHit = position - owner.transform.position;
            tag t = new tag(now, localHit);
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = t;
            else taggedEnemies.Add(instanceID, t);

            if (ConfigManager.Debug)
            {
                APILogger.Debug(Module.Name, $"Bullet Damage: {num}");
                APILogger.Debug(Module.Name, $"Tracked current HP: {__instance.Health}, [{owner.GetInstanceID()}]");
                APILogger.Debug(Module.Name, $"Shot was fired by sentry: {sentryType != SentryType.None}");
            }

            // Stop normal function from calling if this was called by a sentry shot to stop client sentries sending damage packets to host
            // - Refactor code to not use this prefix bool condition by making sentry do the calculation in their sentry Bullet Hit function
            // - Also refactor UpdateClient to not use prefix bool condition (not sure yet how to do this)
            return sentryType == SentryType.None; 
            //return true;
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
            __instance.Health -= num;

            Vector3 localHit = position - owner.transform.position;
            tag t = new tag(now, localHit);
            if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = t; 
            else taggedEnemies.Add(instanceID, t);

            if (ConfigManager.Debug)
            {
                APILogger.Debug(Module.Name, $"Melee Damage: {num}");
                APILogger.Debug(Module.Name, $"Tracked current HP: {__instance.Health}, [{owner.GetInstanceID()}]");
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

        // Sentry fixes => Might refactor weapons to use a similar system in the future (not sure how I will integrate melees right now)
        /*private static Vector3 localSentryPosition = default(Vector3);
        private static bool localPlayerSentryDeployed
        {
            get
            {
                BackpackItem item = PlayerBackpackManager.GetLocalItem(InventorySlot.GearClass);
                if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Player has item of ID {item.ItemID} called {item.Name} with gear name {item.GearIDRange.PublicGearName}");
                return PlayerBackpackManager.LocalBackpack.IsDeployed(InventorySlot.GearClass) && item.ItemID == 97u;
            }
        }
        // use PlayerBackpackManager.LocalBackpack.IsDeployed(InventorySlot.GearClass) to check if sentry is deployed
        // Need to somehow check that the item in gear slot is a sentry tho first

        [HarmonyPatch(typeof(SentryGunFirstPerson), nameof(SentryGunFirstPerson.PlaceOnGround))]
        [HarmonyPrefix]
        public static void FirstPerson_SentryPlace(SentryGunFirstPerson __instance)
        {
            if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"First person sentry placement called!");

            if (__instance.Owner.IsLocallyOwned)
            {
                if (__instance.CheckCanPlace() && !PlayerBackpackManager.LocalBackpack.IsDeployed(InventorySlot.GearClass))
                {
                    localSentryPosition = __instance.m_placePos;
                    if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Sentry placed by local player at position {localSentryPosition}");
                }
                else if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Sentry cannot be placed here!");
            }
        }*/

        private enum SentryType
        {
            None,
            Semi,
            Auto,
            Burst,
            ShotgunSemi
        }
        private static SentryType sentryType = SentryType.None;

        // Determine if its a sentry firing
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireSemi))]
        [HarmonyPrefix]
        public static void FireSemi_Pre()
        {
            sentryType = SentryType.Semi;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireAuto))]
        [HarmonyPrefix]
        public static void FireAuto_Pre()
        {
            sentryType = SentryType.Auto;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireBurst))]
        [HarmonyPrefix]
        public static void FireBurst_Pre()
        {
            sentryType = SentryType.Burst;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireShotgunSemi))]
        [HarmonyPrefix]
        public static void FireShotgunSemi_Pre()
        {
            sentryType = SentryType.ShotgunSemi;
        }

        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireSemi))]
        [HarmonyPostfix]
        public static void FireSemi_Post()
        {
            sentryType = SentryType.None;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireAuto))]
        [HarmonyPostfix]
        public static void FireAuto_Post()
        {
            sentryType = SentryType.None;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireBurst))]
        [HarmonyPostfix]
        public static void FireBurst_Post()
        {
            sentryType = SentryType.None;
        }
        [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireShotgunSemi))]
        [HarmonyPostfix]
        public static void FireShotgunSemi_Post()
        {
            sentryType = SentryType.None;
        }

        // Works for burst sentry, shotgun and sniper are dodgy
        [HarmonyPatch(typeof(SentryGunInstance), nameof(SentryGunInstance.UpdateClient))]
        [HarmonyPrefix]
        public static bool UpdateClient(SentryGunInstance __instance)
        {
            try
            {
                iSentrygunInstance_Detection m_detection = __instance.m_detection;

                m_detection.UpdateDetection();

                // Tag what enemy detection would point at
                if (m_detection.HasTarget)
                {
                    EnemyAgent owner = m_detection.Target.GetComponent<EnemyAgent>();
                    int instanceID = owner.GetInstanceID();
                    long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

                    if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = new tag(now, taggedEnemies[instanceID].localHitPosition);
                    else taggedEnemies.Add(instanceID, new tag(now, owner.EyePosition));
                }

                __instance.m_sync.UpdateClient();

                Vector3 targetDir = m_detection.DetectionSource.forward;
                if (__instance.m_sync.LastSyncData.hasTarget) // (m_detection.HasTarget)
                {
                    if (!__instance.m_lastHasTarget)
                    {
                        __instance.Sound.Post(EVENTS.SENTRYGUN_DETECT);
                        __instance.m_visuals.SetVisualStatus(eSentryGunStatus.HasTarget);
                    }

                    targetDir = __instance.m_sync.LastSyncData.targetDir;
                    if (m_detection.TargetAimTrans != null)
                    {
                        Vector3 position = m_detection.TargetAimTrans.position;
                        targetDir = (position - __instance.GearPartHolder.m_aRotationPivot.position).normalized; 
                    }
                }
                __instance.m_lastHasTarget = __instance.m_sync.LastSyncData.hasTarget; //__instance.m_lastHasTarget = m_detection.HasTarget;
                __instance.UpdateRotation(__instance.m_lastHasTarget, __instance.m_sync.LastSyncData.targetIsTagged, targetDir);
                __instance.CostOfBullet = __instance.CalcCostOfBullet(__instance.m_sync.LastSyncData.targetIsTagged);
                if (__instance.m_sync.LastSyncData.firing && !__instance.m_isFiring)
                {
                    __instance.StartFiring();
                }
                else if (__instance.m_isFiring && !__instance.m_sync.LastSyncData.firing)
                {
                    __instance.StopFiring();
                }
                if (!__instance.m_isFiring && __instance.m_sync.LastSyncData.scanning && __instance.WantToScan())
                {
                    __instance.StartScanning();
                    m_detection.StartDetection();
                }
                else if (__instance.m_isScanning && !__instance.m_sync.LastSyncData.scanning)
                {
                    __instance.StopScanning();
                }
                if (__instance.m_isFiring)
                {
                    __instance.m_firing.UpdateFireClient();
                }
                else if (__instance.Ammo != __instance.m_sync.LastSyncData.ammo)
                {
                    __instance.Ammo = __instance.m_sync.LastSyncData.ammo;
                    __instance.m_firing.UpdateAmmo();
                }
            }
            catch (Exception err)
            {
                //APILogger.Debug(Module.Name, err.ToString() + "\n" + err.StackTrace); // TODO:: Figure out what causes this error
            }

            return false;
        }

        [HarmonyPatch(typeof(BulletWeapon), nameof(BulletWeapon.BulletHit))]
        [HarmonyPrefix]
        public static void BulletHit(Weapon.WeaponHitData weaponRayData, bool doDamage, float additionalDis, uint damageSearchID)
        {
            if (SNetwork.SNet.IsMaster) return;
            if (sentryType == SentryType.None) return;

            if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Received BulletHit from sentryType.{sentryType.ToString()}");

            GameObject gameObject = weaponRayData.rayHit.collider.gameObject;
            bool flag = false;
            if (gameObject != null)
            {
                IDamageable damageable = null;
                BulletWeapon.s_tempColliderInfo = gameObject.GetComponent<ColliderMaterial>();
                bool playLocalVersion = !(weaponRayData.owner != null) && false;
                bool isDecalsAllowed = (LayerManager.MASK_VALID_FOR_DECALS & gameObject.gameObject.layer) == 0;
                if (BulletWeapon.s_tempColliderInfo != null)
                {
                    FX_Manager.PlayEffect(playLocalVersion, (FX_GroupName)BulletWeapon.s_tempColliderInfo.MaterialId, null, weaponRayData.rayHit.point, Quaternion.LookRotation(weaponRayData.rayHit.normal), isDecalsAllowed);
                    damageable = BulletWeapon.s_tempColliderInfo.Damageable;
                    _ = BulletWeapon.s_tempColliderInfo.PhysicsBody;
                }
                else
                {
                    FX_Manager.PlayEffect(playLocalVersion, FX_GroupName.Impact_Concrete, null, weaponRayData.rayHit.point, Quaternion.LookRotation(weaponRayData.rayHit.normal), isDecalsAllowed);
                }

                float num = weaponRayData.rayHit.distance + additionalDis;
                float num2 = weaponRayData.damage;
                if (num > weaponRayData.damageFalloff.x)
                {
                    float num3 = (num - weaponRayData.damageFalloff.x) / (weaponRayData.damageFalloff.y - weaponRayData.damageFalloff.x);
                    num3 = Mathf.Max(1f - num3, BulletWeapon.s_falloffMin);
                    num2 *= num3;
                }
                if (Weapon.SuperWeapons)
                {
                    num2 *= 100f;
                }
                if (damageable == null)
                {
                    damageable = gameObject.GetComponent<IDamageable>();
                }
                flag = damageable != null;
                if (flag && damageSearchID != 0)
                {
                    IDamageable damageable2 = damageable.GetBaseDamagable();
                    if (damageable2 == null)
                    {
                        damageable2 = damageable;
                    }
                    flag = damageable2.TempSearchID != damageSearchID;
                    damageable2.TempSearchID = damageSearchID;
                }
                if (flag)
                {
                    if (ConfigManager.Debug) APILogger.Debug(Module.Name, $"Sentry is owned by local player: {weaponRayData.owner.IsLocallyOwned}");
                    if (weaponRayData.owner.IsLocallyOwned)
                    {
                        damageable?.BulletDamage(num2, weaponRayData.owner, weaponRayData.rayHit.point, weaponRayData.fireDir.normalized, weaponRayData.rayHit.normal, allowDirectionalBonus: true, weaponRayData.staggerMulti, weaponRayData.precisionMulti);
                    }

                    /*Dam_EnemyDamageLimb damageableLimb = damageable.TryCast<Dam_EnemyDamageLimb>();
                    if (damageableLimb != null)
                    {
                        damageableLimb?.BulletDamage(num2, weaponRayData.owner, weaponRayData.rayHit.point, weaponRayData.fireDir.normalized, weaponRayData.rayHit.normal, allowDirectionalBonus: true, weaponRayData.staggerMulti, weaponRayData.precisionMulti);

                        // Simulate damage
                        Dam_EnemyDamageBase m_base = damageableLimb.m_base;
                        EnemyAgent owner = damageableLimb.m_base.Owner;
                        int instanceID = owner.GetInstanceID();
                        long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();

                        float calculatedDam = AgentModifierManager.ApplyModifier(owner, AgentModifier.ProjectileResistance, Mathf.Clamp(num2, 0, m_base.HealthMax));
                        m_base.Health -= calculatedDam;

                        // Tag enemy
                        Vector3 localHit = weaponRayData.rayHit.point - owner.transform.position;
                        tag t = new tag(now, localHit);
                        if (taggedEnemies.ContainsKey(instanceID)) taggedEnemies[instanceID] = t;
                        else taggedEnemies.Add(instanceID, t);

                        if (ConfigManager.Debug)
                        {
                            APILogger.Debug(Module.Name, $"Sentry Bullet Damage: {calculatedDam}");
                            APILogger.Debug(Module.Name, $"Tracked current HP: {m_base.Health}, [{owner.GetInstanceID()}]");
                        }
                    }*/
                }
            }
        }
    }
}