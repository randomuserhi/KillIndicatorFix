using BepInEx.Configuration;
using BepInEx;

namespace KillIndicatorFix
{
    public static class ConfigManager
    {
        static ConfigManager()
        {
            string text = Path.Combine(Paths.ConfigPath, "KillIndicatorFix.cfg");
            ConfigFile configFile = new ConfigFile(text, true);

            debug = configFile.Bind(
                "Debug",
                "enable",
                false,
                "Enables debug messages when true.");

            markerLifeTime = configFile.Bind(
                "Settings", 
                "MarkerLifeTime",
                3000, 
                "Indicates how long the mod will keep track of shown kill markers for in milliseconds.");

            tagBufferPeriod = configFile.Bind(
                "Settings",
                "TagBufferPeriod",
                1000,
                "Indicates a lee-way period in milliseconds where a kill indicator will still be shown for a given enemy long after it has been tagged (shot at).");
        }

        public static bool Debug
        {
            get { return debug.Value; }
        }

        public static int MarkerLifeTime
        {
            get { return markerLifeTime.Value; }
        }

        public static int TagBufferPeriod
        {
            get { return tagBufferPeriod.Value; }
        }

        private static ConfigEntry<bool> debug;
        private static ConfigEntry<int> markerLifeTime;
        private static ConfigEntry<int> tagBufferPeriod;
    }
}