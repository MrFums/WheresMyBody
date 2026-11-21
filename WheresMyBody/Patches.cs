using System;
using System.Collections.Generic;
using EFT;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;

namespace WheresMyBody
{
    // Every hook returns at once for anything that isn't your own player's body.
    internal static class Patches
    {
        internal static readonly AccessTools.FieldRef<PlayerBody, bool> IsYourPlayer =
            AccessTools.FieldRefAccess<PlayerBody, bool>("_isYourPlayer");

        private static readonly AccessTools.FieldRef<Dress, PlayerBody> DressBody =
            AccessTools.FieldRefAccess<Dress, PlayerBody>("PlayerBody");

        private static readonly AccessTools.FieldRef<HotObject, Renderer> HotRenderer =
            AccessTools.FieldRefAccess<HotObject, Renderer>("_renderer");

        // renderer on/off states of your own weapon models from before the game hides them, keyed by slot
        internal static readonly Dictionary<PlayerBody.SlotView, WeaponSnapshot> Snapshots =
            new Dictionary<PlayerBody.SlotView, WeaponSnapshot>();

        internal sealed class WeaponSnapshot
        {
            public GameObject Model;
            public bool[] Enabled;
        }

        internal static void Apply(Harmony harmony)
        {
            Type slotView = typeof(PlayerBody.SlotView);
            int inits = 0;
            foreach (System.Reflection.MethodInfo init in AccessTools.GetDeclaredMethods(typeof(PlayerBody)))
            {
                if (init.Name != nameof(PlayerBody.Init))
                    continue;
                Patch(harmony, init, prefix: nameof(InitPrefix));
                inits++;
            }
            if (inits == 0)
                throw new MissingMethodException("PlayerBody.Init is missing");
            Patch(harmony, AccessTools.Method(typeof(PlayerBody), nameof(PlayerBody.UpdatePlayerRenders)),
                postfix: nameof(UpdatePlayerRendersPostfix));
            Patch(harmony, AccessTools.Method(typeof(PlayerBody), nameof(PlayerBody.Dispose)),
                prefix: nameof(DisposePrefix));
            Patch(harmony, AccessTools.Method(typeof(PlayerBody), nameof(PlayerBody.SetSkin)),
                postfix: nameof(SetSkinPostfix));
            Patch(harmony, AccessTools.Method(slotView, nameof(PlayerBody.SlotView.CreateAndParent)),
                prefix: nameof(CreateAndParentPrefix), postfix: nameof(CreateAndParentPostfix));
            Patch(harmony, AccessTools.Method(slotView, nameof(PlayerBody.SlotView.DestroyCurrentModel)),
                prefix: nameof(DestroyCurrentModelPrefix));
            Patch(harmony, AccessTools.Method(typeof(Dress), nameof(Dress.AdjustShadowMode)),
                postfix: nameof(AdjustShadowModePostfix));
            Patch(harmony, AccessTools.Method(typeof(Player), nameof(Player.OnDead)),
                postfix: nameof(OnDeadPostfix));
            Patch(harmony, AccessTools.Method(typeof(HotObject), nameof(HotObject.SetTemperatureToRenderer),
                new[] { typeof(float), typeof(bool) }), postfix: nameof(SetTemperaturePostfix));
        }

        private static void Patch(Harmony harmony, System.Reflection.MethodInfo target, string prefix = null, string postfix = null)
        {
            if (target == null)
                throw new MissingMethodException($"a game method this mod hooks is missing ({prefix ?? postfix})");
            harmony.Patch(target,
                prefix: prefix == null ? null : new HarmonyMethod(typeof(Patches), prefix),
                postfix: postfix == null ? null : new HarmonyMethod(typeof(Patches), postfix));
        }

        internal static void OnSettingsChanged()
        {
            Guard(BodyView.OnSettingsChanged);
        }

        // a body being set up again starts from the game's own state, whoever it is for now
        private static void InitPrefix(PlayerBody __instance)
        {
            if (BodyView.ActiveCount == 0)
                return;
            Guard(() => BodyView.Deactivate(__instance));
        }

        // the game calls this whenever the point of view is set, including the first time in a raid
        private static void UpdatePlayerRendersPostfix(PlayerBody __instance, EPointOfView pointOfView)
        {
            if (!IsYourPlayer(__instance))
            {
                if (BodyView.ActiveCount != 0)
                    Guard(() => BodyView.Deactivate(__instance));
                return;
            }
            Guard(() => BodyView.OnPointOfView(__instance, pointOfView));
        }

        private static void DisposePrefix(PlayerBody __instance)
        {
            if (BodyView.ActiveCount != 0)
                Guard(() => BodyView.Deactivate(__instance));
            if (!IsYourPlayer(__instance))
                return;
            Guard(() => BodyView.OnDispose(__instance));
        }

        private static void SetSkinPostfix(PlayerBody __instance)
        {
            if (BodyView.ActiveCount == 0)
                return;
            BodyView view = BodyView.Get(__instance);
            if (view != null)
                Guard(view.RefreshTorso);
        }

        private static void CreateAndParentPrefix(PlayerBody.SlotView __instance, PlayerBody playerBody, GameObject model)
        {
            if (playerBody == null || model == null || !IsYourPlayer(playerBody) || !BodyView.IsWeaponSlot(__instance.EquipmentSlot))
                return;
            Guard(() => Snapshot(__instance, model));
        }

        private static void CreateAndParentPostfix(PlayerBody.SlotView __instance, PlayerBody playerBody)
        {
            if (BodyView.ActiveCount == 0)
                return;
            BodyView view = BodyView.Get(playerBody);
            if (view != null)
                Guard(() => view.RefreshSlot(__instance.EquipmentSlot));
        }

        private static void DestroyCurrentModelPrefix(PlayerBody.SlotView __instance)
        {
            PlayerBody body = __instance._playerBody;
            if (body == null)
                return;
            // the model goes back to the game's pool next, so nothing of ours may point at it after this
            Snapshots.Remove(__instance);
            if (BodyView.ActiveCount == 0)
                return;
            BodyView view = BodyView.Get(body);
            if (view != null)
                Guard(() => view.RemoveSlot(__instance.EquipmentSlot));
        }

        private static void AdjustShadowModePostfix(Dress __instance, EPointOfView pointOfView)
        {
            if (BodyView.ActiveCount == 0 || pointOfView != EPointOfView.FirstPerson)
                return;
            BodyView view = BodyView.Get(DressBody(__instance));
            if (view != null)
                Guard(() => view.OnDressShadow(__instance));
        }

        private static void OnDeadPostfix(Player __instance)
        {
            if (BodyView.ActiveCount == 0 || !__instance.IsYourPlayer)
                return;
            Guard(() => BodyView.Deactivate(__instance.PlayerBody));
        }

        private static void SetTemperaturePostfix(HotObject __instance)
        {
            if (BodyView.CloneCount == 0)
                return;
            Renderer renderer = HotRenderer(__instance);
            if (renderer != null)
                BodyView.OnSourceBlockChanged(renderer);
        }

        // Weapon models on your own body get every renderer switched off by the game. Keep what each one was
        // before that, so showing them puts back the model as the game builds it for everyone else.
        private static void Snapshot(PlayerBody.SlotView slot, GameObject model)
        {
            // the game re-parents the same model when the backpack changes; keep the first snapshot then
            if (Snapshots.TryGetValue(slot, out WeaponSnapshot old) && old.Model == model)
                return;
            if (slot._slotModelPositionTuner != null || !BodyView.HasWeaponRoot(model))
            {
                Snapshots.Remove(slot);
                return;
            }
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            var enabled = new bool[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
                enabled[i] = renderers[i] != null && renderers[i].enabled;
            Snapshots[slot] = new WeaponSnapshot { Model = model, Enabled = enabled };
        }

        private static bool _loggedError;

        private static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (_loggedError)
                    return;
                _loggedError = true;
                Plugin.Log.LogError($"{Plugin.Name}: {e}");
            }
        }
    }
}
