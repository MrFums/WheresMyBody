using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WheresMyBody
{
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
    }

    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.wheresmybody";
        public const string Name = "Fums-WheresMyBody";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Torso;
        internal static ConfigEntry<bool> Armor;
        internal static ConfigEntry<bool> Backpack;
        internal static ConfigEntry<bool> Armband;
        internal static ConfigEntry<bool> Pistol;
        internal static ConfigEntry<bool> Knife;
        internal static ConfigEntry<bool> Slung;
        internal static ConfigEntry<float> LeanBack;
        internal static ConfigEntry<bool> LegHits;

        private void Awake()
        {
            Log = Logger;

            // a Fika headless draws nothing, so there is nothing to show
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.fika.headless")
                || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Logger.LogInfo($"{Name} {Version}: headless, not loaded.");
                return;
            }

            Enabled = Config.Bind("1. General", "Enabled", true,
                new ConfigDescription("Show your own body in first person during raids. Off puts everything back to how the game shows it.",
                    null, Order(100)));

            Torso = Config.Bind("2. Body", "Torso", true,
                new ConfigDescription("Show your upper body clothing. Your legs are already shown by the game.", null, Order(90)));
            LeanBack = Config.Bind("2. Body", "Lean back", 0.12f,
                new ConfigDescription("How far back, in metres, your torso and the gear on it are drawn. The first person camera sits at the top of your chest, "
                    + "so with 0 your chest fills the view when you look down and your gun can sink into it. Raise it if your chest still gets in the way.",
                    new AcceptableValueRange<float>(0f, 0.3f), Order(89)));

            Armor = Config.Bind("3. Gear", "Armor and rigs", true,
                new ConfigDescription("Show your body armor and chest rig.", null, Order(80)));
            Backpack = Config.Bind("3. Gear", "Backpack", true,
                new ConfigDescription("Show your backpack and its straps.", null, Order(79)));
            Armband = Config.Bind("3. Gear", "Armband", false,
                new ConfigDescription("Show your armband. Off by default: it is shaped for the third person arm and can float off your first person sleeve.",
                    null, Order(78)));

            Pistol = Config.Bind("4. Weapons", "Holstered pistol", true,
                new ConfigDescription("Show the pistol in your holster.", null, Order(70)));
            Knife = Config.Bind("4. Weapons", "Sheathed knife", true,
                new ConfigDescription("Show the knife in your sheath, for knives that show on the body.", null, Order(69)));
            Slung = Config.Bind("4. Weapons", "Slung weapons", false,
                new ConfigDescription("Show the long guns slung on your back. Off by default: a long gun on your back can poke into view when you look around, "
                    + "and it hangs where the game puts it, so with Lean back above 0 it sits a little away from your leaned backpack.",
                    null, Order(68)));

            LegHits = Config.Bind("5. Gameplay", "Your shots can hit your legs", false,
                new ConfigDescription("Shoot your own leg or foot and it takes the hit, like a shot from anyone else. Off by default, which is how the game works: "
                    + "your own bullet passes through your body. Works in raids, not in the hideout.", null, Order(60)));

            Enabled.SettingChanged += OnSettingChanged;
            Torso.SettingChanged += OnSettingChanged;
            Armor.SettingChanged += OnSettingChanged;
            Backpack.SettingChanged += OnSettingChanged;
            Armband.SettingChanged += OnSettingChanged;
            Pistol.SettingChanged += OnSettingChanged;
            Knife.SettingChanged += OnSettingChanged;
            Slung.SettingChanged += OnSettingChanged;

            var harmony = new Harmony(Guid);
            try
            {
                Patches.Apply(harmony);
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Logger.LogError($"{Name} {Version} couldn't hook the player model, your body stays hidden: {e}");
                return;
            }

            Logger.LogInfo($"{Name} {Version} loaded.");
        }

        private static void OnSettingChanged(object sender, EventArgs e) => BodyView.OnSettingsChanged();

        private static ConfigurationManagerAttributes Order(int order) => new ConfigurationManagerAttributes { Order = order };
    }
}
