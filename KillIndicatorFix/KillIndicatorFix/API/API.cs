using API;
using Enemies;
using Player;
using System.Reflection;
using UnityEngine;

namespace KillIndicatorFix {
    internal static class Utils {
        public const BindingFlags AnyBindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    }

    public static class Kill {
        private static void RegisterMethods(Type t) {
            foreach (MethodInfo method in t.GetMethods(Utils.AnyBindingFlags).Where(m =>
                m.GetCustomAttribute<OnKillIndicator>() != null)
            ) {
                if (method.IsStatic) {
                    try {
                        string type = nameof(OnKillIndicator);
                        if (method.GetCustomAttribute<OnKillIndicator>() != null) {
                            type = nameof(OnKillIndicator);
                            OnKillIndicator += (Action<EnemyAgent, ItemEquippable, long>)method.CreateDelegate(typeof(Action<EnemyAgent, ItemEquippable, long>));
                        }
                        APILogger.Debug($"Registered {type}: '{t.FullName}.{method.Name}'");
                    } catch (Exception ex) {
                        APILogger.Error($"Failed to register method: {ex}");
                    }
                } else {
                    APILogger.Error($"KillIndicatorFix attributes can only be applied to static methods. '{method}' is not static.");
                }
            }
        }

        public static void RegisterAll() {
            foreach (Type t in Assembly.GetCallingAssembly().GetTypes()) {
                RegisterMethods(t);
            }
        }

        public static void RegisterAll(Type type) {
            foreach (Type t in type.GetNestedTypes(Utils.AnyBindingFlags)) {
                RegisterMethods(t);
                RegisterAll(t);
            }
        }

        /// <summary>
        /// Is called whenever a kill indicator is shown for a given enemy.
        /// </summary>
        /// <param name="arg1">Enemy that kill indicator was shown for.</param>  
        /// <param name="arg2">Item used.</param>  
        /// <param name="arg3">On client, if the local player made the kill, provides the delay in milliseconds from player shot to recieving death packet from host.</param>  
        public static Action<EnemyAgent, ItemEquippable?, long>? OnKillIndicator;

        internal static void TriggerOnKillIndicator(EnemyAgent enemy, ItemEquippable? item, long delay) {
            try {
                OnKillIndicator?.Invoke(enemy, item, delay);
            } catch (Exception ex) {
                APILogger.Error($"TriggerOnKillIndicator: {ex}");
            }
        }

        /// <summary>
        /// Tag an enemy for Kill Indicator Fix to handle kills.
        /// </summary>
        /// <param name="enemy">Enemy to tag.</param>  
        /// <param name="item">Item that applied the damage for this tag. If not provided, uses currently equipped weapon.</param>  
        /// <param name="localHitPosition">Where the indicator should appear. Typically this is set to the hit position for bullet hit. If not provided, uses last tag's hit position when available, otherwise uses eye position. NOTE: The position is local to the enemy, not world-space.</param>  
        public static void TagEnemy(EnemyAgent enemy, ItemEquippable? item = null, Vector3? localHitPosition = null) {
            ushort id = enemy.GlobalID;
            long now = ((DateTimeOffset)DateTime.Now).ToUnixTimeMilliseconds();
            if (localHitPosition == null) {
                if (Patches.Kill.taggedEnemies.ContainsKey(id)) {
                    localHitPosition = Patches.Kill.taggedEnemies[id].localHitPosition;
                } else {
                    localHitPosition = enemy.EyePosition - enemy.transform.position;
                }
            }
            if (item == null) {
                item = PlayerManager.GetLocalPlayerAgent().Inventory.WieldedItem;
            }

            if (!Patches.Kill.taggedEnemies.ContainsKey(id)) Patches.Kill.taggedEnemies.Add(id, new Patches.Kill.Tag(enemy.Damage.Health));
            Patches.Kill.Tag t = Patches.Kill.taggedEnemies[id];
            t.timestamp = now;
            t.localHitPosition = localHitPosition.Value;
            t.item = item;
        }
    }
}
