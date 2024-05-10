using API;
using Enemies;
using Player;
using System.Reflection;

namespace KillIndicatorFix {
    internal static class Utils {
        public const BindingFlags AnyBindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    }

    public static class Kill {
        private static void RegisterMethods(Type t) {
            foreach (MethodInfo method in t.GetMethods(Utils.AnyBindingFlags).Where(m =>
                m.GetCustomAttribute<OnEnemyDead>() != null ||
                m.GetCustomAttribute<OnKillIndicator>() != null)
            ) {
                if (method.IsStatic) {
                    try {
                        string type = nameof(OnEnemyDead);
                        if (method.GetCustomAttribute<OnEnemyDead>() != null) {
                            type = nameof(OnEnemyDead);
                            OnEnemyDead += (Action<EnemyAgent, PlayerAgent?, ItemEquippable?, long>)method.CreateDelegate(typeof(Action<EnemyAgent, PlayerAgent?, ItemEquippable?, long>));
                        } else {
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
        /// Is called whenever an enemy dies.
        /// </summary>
        /// <param name="arg1">Enemy that died.</param>  
        /// <param name="arg2">Player that made the kill. Is null if unknown (On client, host does not send who killed an enemy) or not applicable (E.g enemy dies from game event).</param>  
        /// <param name="arg3">Item used to make the kill. NOTE: On host, item may be incorrect for clients due to desync. This only applies for kills performed by guns (main / special). Is null if player is unknown.</param>  
        /// <param name="arg4">On client, if the local player made the kill, provides the delay in milliseconds from player shot to recieving death packet from host.</param>  
        public static Action<EnemyAgent, PlayerAgent?, ItemEquippable?, long>? OnEnemyDead;

        /// <summary>
        /// Is called whenever a kill indicator is shown for a given enemy.
        /// </summary>
        /// <param name="arg1">Enemy that kill indicator was shown for.</param>  
        /// <param name="arg2">Item used.</param>  
        /// <param name="arg3">On client, if the local player made the kill, provides the delay in milliseconds from player shot to recieving death packet from host.</param>  
        public static Action<EnemyAgent, ItemEquippable, long>? OnKillIndicator;

        internal static void TriggerOnEnemyDead(EnemyAgent enemy, PlayerAgent? player, ItemEquippable? item, long delay) {
            try {
                OnEnemyDead?.Invoke(enemy, player, item, delay);
            } catch (Exception ex) {
                APILogger.Error($"TriggerOnEnemyDead: {ex}");
            }
        }

        internal static void TriggerOnKillIndicator(EnemyAgent enemy, ItemEquippable item, long delay) {
            try {
                OnKillIndicator?.Invoke(enemy, item, delay);
            } catch (Exception ex) {
                APILogger.Error($"TriggerOnKillIndicator: {ex}");
            }
        }
    }
}
