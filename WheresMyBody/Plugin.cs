using System;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace WheresMyBody
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.fums.wheresmybody";
        public const string Name = "Fums-WheresMyBody";
        public const string Version = "1.0.0";

        internal static ManualLogSource Log;

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
    }
}
