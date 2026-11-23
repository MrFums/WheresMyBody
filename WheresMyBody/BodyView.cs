using System;
using System.Collections.Generic;
using EFT;
using EFT.CameraControl;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.Visual;
using UnityEngine;
using UnityEngine.Rendering;

namespace WheresMyBody
{
    // Shows your own body in first person.
    //
    // The game keeps your torso and gear in the scene in first person, set to cast shadows only. Those renderers are
    // left exactly as the game sets them, so your shadow stays the game's. For each one this adds a copy that is
    // drawn but casts no shadow. The copy shares the original's mesh and materials and is skinned to the same
    // skeleton, with three changes:
    //  - the forearms and hands fold away (the game's first person arms are already drawn on the same bones);
    //    the upper sleeves stay (unless turned off), since the game's arm stops partway up; with body arms on,
    //    the whole arm stays and the game's first person arms are hidden instead,
    //  - the neck and head fold away (the camera sits there),
    //  - the shoulders and anything else on the ribcage follow the chest as modelled. In first person the game
    //    turns the ribcage with your aim and squashes it to suit the field of view, which would bend the torso.
    // Per frame, only the camera check that sets how far the torso sinks and a short check of each copy against its
    // original run.
    internal sealed class BodyView : MonoBehaviour
    {
        private const int TorsoKey = -1;
        private const float Folded = 1e-5f;

        private static readonly Dictionary<PlayerBody, BodyView> Active = new Dictionary<PlayerBody, BodyView>();
        private static readonly Dictionary<Renderer, Clone> BySource = new Dictionary<Renderer, Clone>();
        private static readonly MaterialPropertyBlock Block = new MaterialPropertyBlock();
        private static readonly List<Material> MaterialBuffer = new List<Material>();

        private static readonly EquipmentSlot[] Slots =
        {
            EquipmentSlot.ArmorVest, EquipmentSlot.TacticalVest, EquipmentSlot.Backpack, EquipmentSlot.ArmBand,
            EquipmentSlot.Holster, EquipmentSlot.Scabbard, EquipmentSlot.FirstPrimaryWeapon, EquipmentSlot.SecondPrimaryWeapon
        };

        // your own body last seen in first person, so turning the mod back on can find it
        private static PlayerBody _lastBody;

        internal static int ActiveCount => Active.Count;
        internal static int CloneCount => BySource.Count;

        private PlayerBody _body;
        private Transform _anchor;
        private Transform _ribcage;
        private Transform _neck;
        private Transform _leftCollar;
        private Transform _rightCollar;
        private Transform _leftArm;
        private Transform _rightArm;
        private readonly List<Clone> _clones = new List<Clone>();

        // The first person camera sits inside the top of the chest, so the copies sink down the spine until the
        // camera clears it: each spine bone has a stand-in that sits a little lower, more the higher up the spine.
        // Shared by every copy.
        private const float CameraClearance = 0.04f;
        private const float MaxSink = 0.3f;
        private const float RiseSpeed = 0.3f;
        private const float BackWhenLevel = 0.15f;
        // how far down you look, as the sine of the angle below level: about 35 degrees, and about 6
        private const float LookDownShown = 0.57f;
        private const float LookDownHidden = 0.1f;
        // how far in front of the neck the front of the chest and vest reaches, and the distance the sink fades over
        private const float ChestFront = 0.25f;
        private const float ChestFrontBlend = 0.05f;
        private static readonly float[] SinkWeights = { 1f / 3f, 2f / 3f, 1f };
        private readonly Transform[] _spine = new Transform[3];
        private readonly Transform[] _lean = new Transform[3];
        private float _sink;
        private bool _leaning;

        // how much thinner the upper sleeves are than the real arm, and how small the folded forearm gets
        private const float SleeveInset = 0.97f;
        private const float ElbowFold = 1e-3f;
        private readonly Dictionary<Transform, Transform> _sleeves = new Dictionary<Transform, Transform>();
        private readonly Transform[] _elbows = new Transform[2];
        private Transform _leftForearm;
        private Transform _rightForearm;

        // With body arms on, the copies keep their whole arms on the real bones, and the game's first person arms
        // are hidden. Only the renderers this switched off are switched back on.
        private bool _bodyArms;
        private readonly List<Renderer> _hiddenHands = new List<Renderer>();
        // TEST-ONLY: remove before release
        private static bool _loggedBones;

        private sealed class Clone
        {
            public int Key;
            public SkinnedMeshRenderer Source;
            public GameObject SourceObject;
            public SkinnedMeshRenderer Copy;
            public Mesh Mesh;
            public Material FirstMaterial;
            public int MaterialCount;
            public bool Shown;
            public readonly List<Transform> Proxies = new List<Transform>();
        }

        private enum Part
        {
            Keep,
            Rigid,
            LeftArm,
            RightArm,
            Neck
        }

        internal static BodyView Get(PlayerBody body)
        {
            if (body == null || !Active.TryGetValue(body, out BodyView view))
                return null;
            // a body handed on to someone else (a pooled player object) loses everything of ours
            if (!Patches.IsYourPlayer(body))
            {
                Deactivate(body);
                return null;
            }
            return view;
        }

        internal static bool IsWeaponSlot(EquipmentSlot slot)
        {
            return slot == EquipmentSlot.Holster || slot == EquipmentSlot.Scabbard
                || slot == EquipmentSlot.FirstPrimaryWeapon || slot == EquipmentSlot.SecondPrimaryWeapon;
        }

        // the same test the game uses before it hides a weapon model on your own body
        internal static bool HasWeaponRoot(GameObject model)
        {
            WeaponPrefab prefab = model.GetComponent<WeaponPrefab>();
            if (prefab == null || prefab._objectInstance == null)
                return false;
            TransformLinks links = prefab._objectInstance.GetComponent<TransformLinks>();
            return links != null && links.GetTransform(ECharacterWeaponBones.Weapon_root) != null;
        }

        internal static void OnPointOfView(PlayerBody body, EPointOfView pointOfView)
        {
            if (pointOfView == EPointOfView.FirstPerson && IsOwnPlayer(body))
            {
                _lastBody = body;
                if (Plugin.Enabled.Value)
                {
                    Activate(body);
                    return;
                }
            }
            Deactivate(body);
        }

        internal static void OnDispose(PlayerBody body)
        {
            Deactivate(body);
            foreach (PlayerBody.SlotView slot in body.SlotViews.GetValuesEnumerator())
                Patches.Snapshots.Remove(slot);
            if (_lastBody == body)
                _lastBody = null;
        }

        // a setting changed in F12: show what it now asks for, on the body in view
        internal static void OnSettingsChanged()
        {
            if (!Plugin.Enabled.Value)
            {
                if (Active.Count == 0)
                    return;
                foreach (PlayerBody active in new List<PlayerBody>(Active.Keys))
                    Deactivate(active);
                return;
            }
            PlayerBody body = _lastBody;
            if (body != null && body.PointOfView.Value == EPointOfView.FirstPerson && IsOwnPlayer(body))
                Activate(body);
            else
                _lastBody = null;
        }

        internal static void OnSourceBlockChanged(Renderer source)
        {
            if (BySource.TryGetValue(source, out Clone clone))
                CopyBlocks(clone);
        }

        // you, in a raid or in the hideout; not the character in a cutscene
        private static bool IsOwnPlayer(PlayerBody body)
        {
            if (body == null || !Patches.IsYourPlayer(body))
                return false;
            Player player = body.PlayerBones != null ? body.PlayerBones.Player : null;
            if (player == null)
                player = body.GetComponentInParent<Player>();
            if (player == null || !player.IsYourPlayer || player is NarratePlayer)
                return false;
            IHealthController health = player.HealthController;
            return health == null || health.IsAlive;
        }

        private static void Activate(PlayerBody body)
        {
            Active.TryGetValue(body, out BodyView view);
            if (view == null)
            {
                var holder = new GameObject("WheresMyBody");
                holder.transform.SetParent(body.transform, false);
                view = holder.AddComponent<BodyView>();
                if (!view.Init(body))
                {
                    Destroy(holder);
                    return;
                }
                Active[body] = view;
            }
            view.RefreshAll();
        }

        internal static void Deactivate(PlayerBody body)
        {
            if (body == null || !Active.TryGetValue(body, out BodyView view))
                return;
            // out of the list first, so the hooks give the game's own result while this puts things back
            Active.Remove(body);
            view.Restore();
            Destroy(view.gameObject);
        }

        private bool Init(PlayerBody body)
        {
            _body = body;
            PlayerBones bones = body.PlayerBones;
            _ribcage = bones != null && bones.Ribcage != null ? bones.Ribcage.Original : null;
            _neck = bones != null ? bones.Neck : null;
            if (_ribcage == null || _ribcage.parent == null)
            {
                Plugin.Log.LogWarning($"{Plugin.Name}: this body has no ribcage bone, so it stays hidden.");
                return false;
            }
            _anchor = _ribcage.parent;
            _leftCollar = _ribcage.Find("Base HumanLCollarbone");
            _rightCollar = _ribcage.Find("Base HumanRCollarbone");
            _leftArm = FirstChild(_leftCollar, "Base HumanLUpperarm");
            _rightArm = FirstChild(_rightCollar, "Base HumanRUpperarm");
            _leftForearm = ChildNamed(_leftArm, "Forearm");
            _rightForearm = ChildNamed(_rightArm, "Forearm");

            _spine[2] = _anchor;
            _spine[1] = IsSpine(_anchor.parent) ? _anchor.parent : null;
            _spine[0] = _spine[1] != null && IsSpine(_spine[1].parent) ? _spine[1].parent : null;
            for (int i = 0; i < _spine.Length; i++)
            {
                if (_spine[i] == null)
                    continue;
                _lean[i] = new GameObject("WheresMyBody spine").transform;
                _lean[i].SetParent(_spine[i], false);
            }
            return true;
        }

        private static bool IsSpine(Transform bone)
        {
            return bone != null && bone.name.IndexOf("Spine", StringComparison.Ordinal) >= 0;
        }

        // the stand-in a copy uses for a bone: the leaning one for the spine, the bone itself otherwise
        private Transform Leaned(Transform bone)
        {
            for (int i = 0; i < _spine.Length; i++)
            {
                if (bone == _spine[i] && _lean[i] != null)
                    return _lean[i];
            }
            return bone;
        }

        // How far the torso sinks: just enough that the top of the chest, at the base of the neck, is below the
        // camera, measured along your spine so it holds crouched, prone and leaning. The camera moves with your
        // aim and with the game's FOV adjustment, so this is measured every frame. It sinks at once when the
        // camera needs more room and rises back slowly.
        private float SinkForCamera(out Vector3 up, out Vector3 back, out float lookDown)
        {
            up = Vector3.zero;
            back = Vector3.zero;
            lookDown = 0f;
            Camera camera = CameraManager.Exist ? CameraManager.Instance.Camera : null;
            if (camera == null || _neck == null)
                return 0f;
            Vector3 neck = _neck.position;
            Vector3 toNeck = neck - camera.transform.position;
            // the camera is somewhere else, such as looking at a hideout area: the body stays as modelled
            if (toNeck.sqrMagnitude > 1f)
                return 0f;
            up = neck - _anchor.position;
            if (up.sqrMagnitude < 1e-6f)
                return 0f;
            up.Normalize();
            Vector3 look = camera.transform.forward;
            lookDown = -Vector3.Dot(look, up);
            // shorter the more your view runs along your spine, as when prone, so it never flips direction
            back = Vector3.ProjectOnPlane(-look, up);
            float sink = Mathf.Clamp(Vector3.Dot(toNeck, up) + camera.nearClipPlane + CameraClearance, 0f, MaxSink);
            // Looking down, the camera swings forward with your aim and can end up in front of the chest rather
            // than inside it. There it needs no room, and sinking would only pull the shoulders off the arms.
            if (sink > 0f && _leftArm != null && _rightArm != null)
            {
                Vector3 front = Vector3.Cross(_rightArm.position - _leftArm.position, up);
                if (front.sqrMagnitude > 1e-6f)
                {
                    float ahead = Vector3.Dot(-toNeck, front.normalized) - camera.nearClipPlane;
                    sink *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(ChestFront, ChestFront + ChestFrontBlend, ahead));
                }
            }
            return sink;
        }

        private void UpdateLean()
        {
            float target = SinkForCamera(out Vector3 up, out Vector3 back, out float lookDown);
            _sink = target >= _sink ? target : Mathf.MoveTowards(_sink, target, RiseSpeed * Time.deltaTime);
            // Looking level or up, a wide view would catch the shoulders at the edge of the screen, so the torso
            // also moves back out of sight. That eases off as you look down, where you see all of it.
            float away = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(LookDownShown, LookDownHidden, lookDown)) * BackWhenLevel;
            if ((_sink <= 0f && away <= 0f) || up == Vector3.zero)
            {
                if (_leaning)
                {
                    for (int i = 0; i < _lean.Length; i++)
                    {
                        if (_lean[i] != null)
                            _lean[i].localPosition = Vector3.zero;
                    }
                    _leaning = false;
                }
                return;
            }
            Vector3 shift = up * -_sink + back * away;
            for (int i = 0; i < _lean.Length; i++)
            {
                if (_lean[i] != null)
                    _lean[i].localPosition = _spine[i].InverseTransformVector(shift * SinkWeights[i]);
            }
            _leaning = true;
        }

        // The game's first person arm ends partway up the upper arm, so the copies keep their upper sleeves: each
        // upper arm bone gets a stand-in on the real bone, a little thinner so the game's sleeve stays on top where
        // both are drawn. Shared by every copy, and nothing about them changes per frame.
        private Transform Sleeve(Transform bone)
        {
            if (_sleeves.TryGetValue(bone, out Transform proxy) && proxy != null)
                return proxy;
            proxy = new GameObject("WheresMyBody sleeve").transform;
            proxy.SetParent(bone, false);
            proxy.localScale = new Vector3(SleeveInset, SleeveInset, SleeveInset);
            _sleeves[bone] = proxy;
            return proxy;
        }

        // the forearm and hand, which the game's arm already shows, shrink into the elbow of the sleeve stand-in
        private Transform Elbow(int side)
        {
            if (_elbows[side] != null)
                return _elbows[side];
            Transform upper = side == 0 ? _leftArm : _rightArm;
            Transform forearm = side == 0 ? _leftForearm : _rightForearm;
            Transform proxy = new GameObject("WheresMyBody elbow").transform;
            proxy.SetParent(Sleeve(upper), false);
            proxy.localPosition = upper.InverseTransformPoint(forearm.position);
            proxy.localScale = new Vector3(ElbowFold, ElbowFold, ElbowFold);
            _elbows[side] = proxy;
            return proxy;
        }

        private void DestroyLean()
        {
            for (int i = 0; i < _elbows.Length; i++)
            {
                if (_elbows[i] != null)
                    Destroy(_elbows[i].gameObject);
                _elbows[i] = null;
            }
            foreach (Transform proxy in _sleeves.Values)
            {
                if (proxy != null)
                    Destroy(proxy.gameObject);
            }
            _sleeves.Clear();
            for (int i = 0; i < _lean.Length; i++)
            {
                if (_lean[i] != null)
                    Destroy(_lean[i].gameObject);
                _lean[i] = null;
            }
        }

        private static Transform FirstChild(Transform parent, string name)
        {
            if (parent == null)
                return null;
            Transform child = parent.Find(name);
            return child != null ? child : parent.childCount > 0 ? parent.GetChild(0) : null;
        }

        private static Transform ChildNamed(Transform parent, string part)
        {
            if (parent == null)
                return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name.IndexOf(part, StringComparison.Ordinal) >= 0)
                    return child;
            }
            return null;
        }

        internal void RefreshAll()
        {
            RefreshTorso();
            foreach (EquipmentSlot slot in Slots)
                RefreshSlot(slot);
        }

        internal void RefreshTorso()
        {
            RemoveClones(TorsoKey);
            bool hasSkin = Plugin.Torso.Value && _body.BodySkins.TryGetValue(EBodyModelPart.Body, out LoddedSkin skin) && skin != null;
            _bodyArms = hasSkin && Plugin.BodyArms.Value;
            if (hasSkin)
            {
                foreach (Renderer renderer in _body.BodySkins[EBodyModelPart.Body].GetRenderers())
                    AddClone(renderer as SkinnedMeshRenderer, TorsoKey);
            }
            if (_bodyArms && !HasClones(TorsoKey))
                _bodyArms = false;
            UpdateHands();
        }

        private bool HasClones(int key)
        {
            foreach (Clone clone in _clones)
            {
                if (clone.Key == key)
                    return true;
            }
            return false;
        }

        // the game's first person arms, hidden while the body's own arms stand in for them
        private void UpdateHands()
        {
            ShowHands();
            if (!_bodyArms || !_body.BodySkins.TryGetValue(EBodyModelPart.Hands, out LoddedSkin hands) || hands == null)
                return;
            LogBonesOnce(hands);
            foreach (Renderer renderer in hands.GetRenderers())
            {
                if (renderer == null || renderer.forceRenderingOff)
                    continue;
                renderer.forceRenderingOff = true;
                _hiddenHands.Add(renderer);
            }
        }

        private void ShowHands()
        {
            foreach (Renderer renderer in _hiddenHands)
            {
                if (renderer != null)
                    renderer.forceRenderingOff = false;
            }
            _hiddenHands.Clear();
        }

        // TEST-ONLY: remove before release. Whether the first person arms and the body share their arm bones.
        private void LogBonesOnce(LoddedSkin hands)
        {
            if (_loggedBones)
                return;
            _loggedBones = true;
            SkinnedMeshRenderer handSkin = null;
            foreach (Renderer renderer in hands.GetRenderers())
            {
                if ((handSkin = renderer as SkinnedMeshRenderer) != null)
                    break;
            }
            SkinnedMeshRenderer bodySkin = null;
            foreach (Clone clone in _clones)
            {
                if (clone.Key == TorsoKey && (bodySkin = clone.Source) != null)
                    break;
            }
            if (handSkin == null || bodySkin == null)
            {
                Plugin.Log.LogInfo($"{Plugin.Name} TEST: no skin to compare (hands {handSkin != null}, body {bodySkin != null})");
                return;
            }
            var handBones = new HashSet<Transform>(handSkin.bones);
            var bodyBones = new HashSet<Transform>(bodySkin.bones);
            var handOnly = new List<string>();
            int shared = 0;
            foreach (Transform bone in handBones)
            {
                if (bone == null)
                    continue;
                if (bodyBones.Contains(bone))
                    shared++;
                else
                    handOnly.Add(bone.name);
            }
            var bodyArmOnly = new List<string>();
            foreach (Transform bone in bodyBones)
            {
                if (bone == null || handBones.Contains(bone))
                    continue;
                Part part = Classify(bone);
                if (part == Part.LeftArm || part == Part.RightArm)
                    bodyArmOnly.Add(bone.name);
            }
            Plugin.Log.LogInfo($"{Plugin.Name} TEST: hands {handSkin.name} / body {bodySkin.name}: {shared} shared bones. "
                + $"Hands only ({handOnly.Count}): {string.Join(", ", handOnly)}. "
                + $"Body arm only ({bodyArmOnly.Count}): {string.Join(", ", bodyArmOnly)}");
        }

        internal void RefreshSlot(EquipmentSlot slot)
        {
            if (Array.IndexOf(Slots, slot) < 0)
                return;
            RemoveClones((int)slot);
            if (!_body.SlotViews.TryGetByKey(slot, out PlayerBody.SlotView view) || view == null)
                return;

            if (IsWeaponSlot(slot))
            {
                ShowWeapon(view, WeaponWanted(slot));
                return;
            }

            if (GearWanted(slot) && view.Dresses != null)
            {
                foreach (Dress dress in view.Dresses)
                {
                    if (dress == null)
                        continue;
                    foreach (Renderer renderer in dress.GetRenderers())
                        AddClone(renderer as SkinnedMeshRenderer, (int)slot);
                }
            }
            ApplyGameShadows(view);
        }

        internal void RemoveSlot(EquipmentSlot slot)
        {
            RemoveClones((int)slot);
        }

        // after the game sets a gear piece to shadow only in first person
        internal void OnDressShadow(Dress dress)
        {
            EquipmentSlot? slot = SlotOf(dress);
            if (slot == null)
                return;
            if (!GearWanted(slot.Value))
                return;
            foreach (Renderer renderer in dress.GetRenderers())
            {
                // gear pieces are copied, except any that aren't skinned: those are shown as they are
                if (renderer != null && !(renderer is SkinnedMeshRenderer))
                    renderer.shadowCastingMode = ShadowCastingMode.On;
            }
        }

        private EquipmentSlot? SlotOf(Dress dress)
        {
            for (int i = 0; i < 4; i++)
            {
                if (!_body.SlotViews.TryGetByKey(Slots[i], out PlayerBody.SlotView view) || view?.Dresses == null)
                    continue;
                if (Array.IndexOf(view.Dresses, dress) >= 0)
                    return Slots[i];
            }
            return null;
        }

        private static bool GearWanted(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.ArmorVest:
                    return Plugin.Armor.Value;
                case EquipmentSlot.TacticalVest:
                    return Plugin.Rig.Value;
                case EquipmentSlot.Backpack:
                    return Plugin.Backpack.Value;
                default:
                    return false;
            }
        }

        // the holstered pistol and the knife; long guns slung on the back stay as the game has them
        private static bool WeaponWanted(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.Holster:
                    return Plugin.Pistol.Value;
                case EquipmentSlot.Scabbard:
                    return Plugin.Knife.Value;
                default:
                    return false;
            }
        }

        // the game hides weapon models on your own body; shown puts back each renderer as it was before that
        private static void ShowWeapon(PlayerBody.SlotView view, bool show)
        {
            if (!Patches.Snapshots.TryGetValue(view, out Patches.WeaponSnapshot snapshot) || snapshot.Model != view.Model)
                return;
            Renderer[] renderers = view.Renderers;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer != null)
                    renderer.enabled = show && i < snapshot.Enabled.Length && snapshot.Enabled[i];
            }
        }

        // asks each gear piece to set its shadow mode again; the hook adds ours on top while this body is active
        private void ApplyGameShadows(PlayerBody.SlotView view)
        {
            if (view.Dresses == null)
                return;
            EPointOfView pointOfView = _body.PointOfView.Value;
            foreach (Dress dress in view.Dresses)
            {
                if (dress != null)
                    dress.AdjustShadowMode(pointOfView);
            }
        }

        private void Restore()
        {
            for (int i = _clones.Count - 1; i >= 0; i--)
                Kill(_clones[i]);
            _clones.Clear();
            DestroyLean();
            ShowHands();
            if (_body == null)
                return;
            foreach (EquipmentSlot slot in Slots)
            {
                if (!_body.SlotViews.TryGetByKey(slot, out PlayerBody.SlotView view) || view == null)
                    continue;
                if (IsWeaponSlot(slot))
                    ShowWeapon(view, false);
                else
                    ApplyGameShadows(view);
            }
        }

        private void OnDestroy()
        {
            // the player object can go away without telling us; leave nothing behind on its bones
            for (int i = _clones.Count - 1; i >= 0; i--)
                Kill(_clones[i]);
            _clones.Clear();
            DestroyLean();
            ShowHands();
            if (_body != null && Active.TryGetValue(_body, out BodyView view) && view == this)
                Active.Remove(_body);
        }

        private void LateUpdate()
        {
            UpdateLean();
            for (int i = _clones.Count - 1; i >= 0; i--)
            {
                Clone clone = _clones[i];
                SkinnedMeshRenderer source = clone.Source;
                if (source == null)
                {
                    Kill(clone);
                    _clones.RemoveAt(i);
                    continue;
                }
                // the game swaps meshes on the same renderer as gear goes on and off
                if (!ReferenceEquals(source.sharedMesh, clone.Mesh))
                    Bind(clone);
                // blood on clothes gives the renderer its own material
                if (!ReferenceEquals(source.sharedMaterial, clone.FirstMaterial))
                    CopyMaterials(clone);
                bool shown = clone.Mesh != null && source.enabled && !source.forceRenderingOff && clone.SourceObject.activeInHierarchy;
                if (shown != clone.Shown)
                {
                    clone.Copy.enabled = shown;
                    clone.Shown = shown;
                }
            }
        }

        private void AddClone(SkinnedMeshRenderer source, int key)
        {
            if (source == null || BySource.ContainsKey(source) || !IsFirstLod(source))
                return;

            var go = new GameObject("WheresMyBody " + source.name) { layer = source.gameObject.layer };
            go.transform.SetParent(transform, false);
            SkinnedMeshRenderer copy = go.AddComponent<SkinnedMeshRenderer>();
            copy.enabled = false;
            copy.shadowCastingMode = ShadowCastingMode.Off;
            copy.receiveShadows = source.receiveShadows;
            copy.quality = source.quality;
            copy.updateWhenOffscreen = source.updateWhenOffscreen;
            copy.skinnedMotionVectors = source.skinnedMotionVectors;
            copy.motionVectorGenerationMode = source.motionVectorGenerationMode;
            copy.lightProbeUsage = source.lightProbeUsage;
            copy.reflectionProbeUsage = source.reflectionProbeUsage;
            copy.probeAnchor = source.probeAnchor;
            copy.allowOcclusionWhenDynamic = source.allowOcclusionWhenDynamic;
            copy.renderingLayerMask = source.renderingLayerMask;
            copy.sortingLayerID = source.sortingLayerID;
            copy.sortingOrder = source.sortingOrder;

            var clone = new Clone { Key = key, Source = source, SourceObject = source.gameObject, Copy = copy };
            _clones.Add(clone);
            BySource[source] = clone;
            Bind(clone);
            CopyMaterials(clone);
        }

        private void RemoveClones(int key)
        {
            for (int i = _clones.Count - 1; i >= 0; i--)
            {
                if (_clones[i].Key != key)
                    continue;
                Kill(_clones[i]);
                _clones.RemoveAt(i);
            }
        }

        private static void Kill(Clone clone)
        {
            // a destroyed renderer keeps its instance id, so it still finds its own entry
            if (!ReferenceEquals(clone.Source, null) && BySource.TryGetValue(clone.Source, out Clone current) && ReferenceEquals(current, clone))
                BySource.Remove(clone.Source);
            DestroyProxies(clone);
            if (clone.Copy != null)
                Destroy(clone.Copy.gameObject);
            clone.Copy = null;
        }

        private static void DestroyProxies(Clone clone)
        {
            foreach (Transform proxy in clone.Proxies)
            {
                if (proxy != null)
                    Destroy(proxy.gameObject);
            }
            clone.Proxies.Clear();
        }

        // only the closest level of detail is copied; first person is always close
        private bool IsFirstLod(Renderer renderer)
        {
            for (Transform t = renderer.transform; t != null && t != _body.transform; t = t.parent)
            {
                if (!t.TryGetComponent(out LODGroup group))
                    continue;
                LOD[] lods = group.GetLODs();
                if (lods.Length == 0 || Array.IndexOf(lods[0].renderers, renderer) >= 0)
                    return true;
                for (int i = 1; i < lods.Length; i++)
                {
                    if (Array.IndexOf(lods[i].renderers, renderer) >= 0)
                        return false;
                }
                return true;
            }
            return true;
        }

        // points the copy at the source's mesh and bones, with the folded and steadied bones swapped in
        private void Bind(Clone clone)
        {
            DestroyProxies(clone);
            SkinnedMeshRenderer source = clone.Source;
            SkinnedMeshRenderer copy = clone.Copy;
            Mesh mesh = source.sharedMesh;
            clone.Mesh = mesh;

            Transform[] bones = mesh != null ? source.bones : null;
            Matrix4x4[] bindposes = mesh != null ? mesh.bindposes : null;
            if (bones == null || bindposes == null || bones.Length == 0 || bones.Length != bindposes.Length)
            {
                // nothing to skin yet; LateUpdate binds again when the game gives it a mesh
                clone.Mesh = null;
                copy.sharedMesh = null;
                copy.enabled = false;
                clone.Shown = false;
                return;
            }

            // the chest bone the steadied bones follow, and where it was when this mesh was modelled
            Transform anchor = null;
            Matrix4x4 anchorBind = Matrix4x4.identity;
            for (Transform t = _anchor; t != null && t != _body.transform; t = t.parent)
            {
                int index = Array.IndexOf(bones, t);
                if (index >= 0)
                {
                    anchor = t;
                    anchorBind = bindposes[index];
                    break;
                }
            }
            bool modelled = anchor != null;
            if (!modelled)
                anchor = _anchor;
            // the steadied bones ride on the leaning chest
            anchor = Leaned(anchor);

            var mapped = new Transform[bones.Length];
            Transform leftFold = null, rightFold = null, neckFold = null;
            // with body arms on, each whole arm stays on the real bones; with the upper arms off, it folds into its shoulder
            bool bodyArms = _bodyArms;
            bool sleeves = Plugin.Sleeves.Value;
            bool leftSleeve = sleeves && _leftForearm != null;
            bool rightSleeve = sleeves && _rightForearm != null;
            for (int i = 0; i < bones.Length; i++)
            {
                Transform bone = bones[i];
                if (bone == null)
                    continue;
                switch (Classify(bone))
                {
                    case Part.Keep:
                        mapped[i] = Leaned(bone);
                        break;
                    case Part.Rigid:
                        mapped[i] = RigidProxy(clone, anchor, modelled, anchorBind, bone, bindposes[i]);
                        break;
                    // only the upper arm unfolds; anything else on the arm folds into the elbow
                    case Part.LeftArm:
                        mapped[i] = bodyArms ? bone : leftSleeve
                            ? IsUpperArm(bone) ? Sleeve(bone) : Elbow(0)
                            : leftFold ?? (leftFold = FoldProxy(clone, anchor, modelled, anchorBind, _leftArm, bones, bindposes, i));
                        break;
                    case Part.RightArm:
                        mapped[i] = bodyArms ? bone : rightSleeve
                            ? IsUpperArm(bone) ? Sleeve(bone) : Elbow(1)
                            : rightFold ?? (rightFold = FoldProxy(clone, anchor, modelled, anchorBind, _rightArm, bones, bindposes, i));
                        break;
                    case Part.Neck:
                        mapped[i] = neckFold ?? (neckFold = FoldProxy(clone, anchor, modelled, anchorBind, _neck, bones, bindposes, i));
                        break;
                }
            }

            // mesh off while the bones change, so the bone count never disagrees with the mesh
            copy.sharedMesh = null;
            copy.bones = mapped;
            copy.rootBone = source.rootBone;
            copy.sharedMesh = mesh;
            copy.localBounds = source.localBounds;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                copy.SetBlendShapeWeight(i, source.GetBlendShapeWeight(i));
            if (clone.Shown)
                copy.enabled = true;
        }

        private static bool IsUpperArm(Transform bone)
        {
            return bone.name.IndexOf("Upperarm", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private Part Classify(Transform bone)
        {
            if (_neck != null && bone.IsChildOf(_neck))
                return Part.Neck;
            if (!bone.IsChildOf(_ribcage))
                return Part.Keep;
            if (_leftCollar != null && bone.IsChildOf(_leftCollar))
                return bone == _leftCollar ? Part.Rigid : Part.LeftArm;
            if (_rightCollar != null && bone.IsChildOf(_rightCollar))
                return bone == _rightCollar ? Part.Rigid : Part.RightArm;
            return Part.Rigid;
        }

        // a bone that holds its modelled place on the chest
        private static Transform RigidProxy(Clone clone, Transform anchor, bool modelled, Matrix4x4 anchorBind, Transform bone, Matrix4x4 bind)
        {
            Transform proxy = NewProxy(clone, anchor);
            if (modelled)
            {
                Matrix4x4 local = anchorBind * bind.inverse;
                proxy.localPosition = local.GetColumn(3);
                proxy.localRotation = local.rotation;
                proxy.localScale = local.lossyScale;
            }
            else
            {
                proxy.SetPositionAndRotation(bone.position, bone.rotation);
            }
            return proxy;
        }

        // a point every vertex on a folded part shrinks into: the shoulder joint for an arm, the neck joint for the head
        private static Transform FoldProxy(Clone clone, Transform anchor, bool modelled, Matrix4x4 anchorBind, Transform joint,
            Transform[] bones, Matrix4x4[] bindposes, int fallback)
        {
            Transform proxy = NewProxy(clone, anchor);
            int index = joint != null ? Array.IndexOf(bones, joint) : -1;
            if (index < 0)
                index = fallback;
            if (modelled)
                proxy.localPosition = (anchorBind * bindposes[index].inverse).GetColumn(3);
            else
                proxy.position = (joint != null ? joint : bones[index]).position;
            proxy.localRotation = Quaternion.identity;
            proxy.localScale = new Vector3(Folded, Folded, Folded);
            return proxy;
        }

        private static Transform NewProxy(Clone clone, Transform anchor)
        {
            Transform proxy = new GameObject("WheresMyBody bone").transform;
            proxy.SetParent(anchor, false);
            clone.Proxies.Add(proxy);
            return proxy;
        }

        private static void CopyMaterials(Clone clone)
        {
            SkinnedMeshRenderer source = clone.Source;
            source.GetSharedMaterials(MaterialBuffer);
            clone.Copy.sharedMaterials = MaterialBuffer.ToArray();
            clone.MaterialCount = MaterialBuffer.Count;
            clone.FirstMaterial = source.sharedMaterial;
            MaterialBuffer.Clear();
            CopyBlocks(clone);
        }

        // heat for thermal sights and similar per renderer values
        private static void CopyBlocks(Clone clone)
        {
            SkinnedMeshRenderer source = clone.Source;
            SkinnedMeshRenderer copy = clone.Copy;
            if (source == null || copy == null)
                return;
            if (source.HasPropertyBlock())
            {
                source.GetPropertyBlock(Block);
                copy.SetPropertyBlock(Block);
            }
            else
            {
                copy.SetPropertyBlock(null);
            }
            for (int i = 0; i < clone.MaterialCount; i++)
            {
                source.GetPropertyBlock(Block, i);
                copy.SetPropertyBlock(Block.isEmpty ? null : Block, i);
            }
            Block.Clear();
        }
    }
}
