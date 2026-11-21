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
        public const string Version = "1.0.1";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Torso;
        internal static ConfigEntry<bool> Sleeves;
        internal static ConfigEntry<bool> Armor;
        internal static ConfigEntry<bool> Rig;
        internal static ConfigEntry<bool> Backpack;
        internal static ConfigEntry<bool> Pistol;
        internal static ConfigEntry<bool> Knife;

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
                new ConfigDescription("Show your own body in first person. Off puts everything back to how the game shows it.",
                    null, Order(100)));

            Torso = Config.Bind("2. Body", "Torso", true,
                new ConfigDescription("Show your upper body clothing. Your legs are already shown by the game.", null, Order(90)));
            Sleeves = Config.Bind("2. Body", "Upper arms", true,
                new ConfigDescription("Show the upper part of your sleeves, and of any gear on them, above where the game's first person arms end. "
                    + "Off folds them into your shoulders: no seam at the elbow, but a gap can show between your arms and shoulders at a wide FOV.",
                    null, Order(89)));

            Armor = Config.Bind("3. Gear", "Body armor", true,
                new ConfigDescription("Show the armor in your body armor slot. Turn it off if a high collar blocks your view.", null, Order(80)));
            Rig = Config.Bind("3. Gear", "Chest rig", true,
                new ConfigDescription("Show your chest rig, armored or not.", null, Order(79)));
            Backpack = Config.Bind("3. Gear", "Backpack", false,
                new ConfigDescription("Show your backpack and its straps. Off by default: a large backpack can reach into view when you look up or sprint.",
                    null, Order(78)));

            Pistol = Config.Bind("4. Weapons", "Holstered pistol", true,
                new ConfigDescription("Show the pistol in your holster.", null, Order(70)));
            Knife = Config.Bind("4. Weapons", "Sheathed knife", true,
                new ConfigDescription("Show the knife in your sheath, for knives that show on the body.", null, Order(69)));

            Enabled.SettingChanged += OnSettingChanged;
            Torso.SettingChanged += OnSettingChanged;
            Sleeves.SettingChanged += OnSettingChanged;
            Armor.SettingChanged += OnSettingChanged;
            Rig.SettingChanged += OnSettingChanged;
            Backpack.SettingChanged += OnSettingChanged;
            Pistol.SettingChanged += OnSettingChanged;
            Knife.SettingChanged += OnSettingChanged;

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

        private static void OnSettingChanged(object sender, EventArgs e) => Patches.OnSettingsChanged();

        private static ConfigurationManagerAttributes Order(int order) => new ConfigurationManagerAttributes { Order = order };
    }
}
