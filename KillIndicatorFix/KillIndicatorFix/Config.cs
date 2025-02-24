using BepInEx;
using BepInEx.Configuration;

namespace KillIndicatorFix.BepInEx {
    public static class ConfigManager {
        static ConfigManager() {
            string text = Path.Combine(Paths.ConfigPath, $"{Module.Name}.cfg");
            ConfigFile configFile = new ConfigFile(text, true);

            debug = configFile.Bind(
                "Debug",
                "enable",
                false,
                "Enables debug messages when true.");

            tagBufferPeriod = configFile.Bind(
                "Settings",
                "TagBufferPeriod",
                1000,
                "Indicates a lee-way period in milliseconds where a kill indicator will still be shown for a given enemy long after it has been tagged (shot at).");
        }

        public static bool Debug {
            get { return debug.Value; }
        }

        public static int TagBufferPeriod {
            get { return tagBufferPeriod.Value; }
        }

        private static ConfigEntry<bool> debug;
        private static ConfigEntry<int> tagBufferPeriod;
    }
}