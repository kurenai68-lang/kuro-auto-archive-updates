// KuroMechBuilder - independent mech assembly workshop for Rust / Carbon / Oxide.
// Uses part/prefab candidates observed in MechBossPrototype v0.3.35.
// Does NOT copy or modify existing KuroPrefabLab/MechBossPrototype runtime data.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Collections;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("KuroMechBuilder", "Kuro", "0.4.51")]
    // v0.4.51 module + fast spawn: reusable assembled modules and bounded Native Gib burst restoration.\n    // v0.4.50 MouthJoint preview: test upper/lower jaw motion directly in the free-build editor.
    // v0.4.49 free input spam fix: unknown free-editor actions no longer spam chat or interfere with E/R/mouse controls.
    // v0.4.48 death kick fix: fully dismount pilots on death and pause flyhack detection during death/respawn cleanup.
    // v0.4.47 dedicated MouthJoint: one shared round hinge with explicit upper/lower jaw child lists.
    // v0.4.46 jaw pair: automatically split legacy mouth parts into upper/lower jaws; shared round hinge, opposite motion.
    // v0.4.45 direction fix: lower jaw motion reversed upward; neck look direction invert controls.
    // v0.4.44 joint hierarchy: neck -> head -> mouth inheritance; round joints become true shared pivots for descendants.
    // v0.4.43 round joint editor: place visible spherical free-motion pivots with the crosshair; saved with the design.
    // v0.4.42 neck-look cruise AI: keep the craft cruising; only a selected neck/head group tracks nearby players.
    // v0.4.39 creature AI: slow jaw motion preset + nearest-player facing while swimming.
    // v0.4.38 detail-motion: arbitrary free-build part groups can oscillate around a chosen pivot during swim (tail/fins/wings/jaw).
    // v0.4.37 homing hitbox: keep HybridMini flight unchanged, but restore the embedded Minicopter colliders as a protected projectile/missile hit target.
    // v0.4.35 hybrid-mini swim: Native Gib visuals remain software-rigid at 50 Hz; a separate embedded Minicopter follows the same simulated root only as homing/vehicle target.
    // v0.4.34 mini-root body-center + homing: place the Minicopter root inside the visual assembly and force only KMB swim roots to be valid homing targets.
    // v0.4.33 mini-root swim: embed a Minicopter as the single moving network parent; Native Gib visuals are parented once and are not individually replicated while moving.
    // v0.4.32 smooth swim diagnostics: default swim replication is 30 Hz and runtime reports actual achieved Hz.
    // v0.4.31 stable selector: installed models get persistent deterministic IDs and commands accept blueprint/model names directly.
    // v0.4.30 position-packet swim: keep no-break safe swim, but replicate moving pieces with position-only packets to reduce full-snapshot backlog/jitter.
    // v0.4.29 no-break safe swim: protect managed parts from damage/collision and move the visual rigid set directly; vehicle-root parenting is disabled.
    // v0.4.27 vehicle-root swim: use a submerged rowboat network root and Rigidbody velocity instead of teleporting a static anchor.
    // v0.4.26 stable-anchor swim: Native Gib parts attach to one non-gib hidden motion anchor; only the anchor is network-moved.
    // v0.4.25 software-rigid swim: no Native Gib parenting; all parts keep exact local poses and move together.
    // v0.4.2 experimental native ScrapHeli part extraction; config gated; temporary only.
    [Description("Admin-only mech workshop with verified mounted-pilot control, stable WAIST-root movement, free pilot view, explicit Space dismount, saved punch playback and direct assembly editing.")]
    public class KuroMechBuilder : RustPlugin
    {
        private const string PermUse = "kuromechbuilder.admin";
        private const string DataFile = "KuroMechBuilder_Blueprints";
        private const string UiRoot = "KMB.Root";
        private const string DirectUiRoot = "KMB.Direct";
        private const string FreeUiRoot = "KMB.Free";
        // The contextual catalog opened by right-click uses the same Free UI root.
        // This is a ServerGib source prefab, not a directly spawnable individual mesh.
        private const string NativeScrapPrefab = "assets/content/vehicles/scrap heli carrier/servergibs_scraptransport.prefab";
        private const string NativeMeshPrefix = "ScrapHeliGibs_Part";
        private const string SwimAnchorPrefab = "assets/content/vehicles/boats/rowboat/rowboat.prefab";
        private const string MiniSwimRootPrefab = "assets/content/vehicles/minicopter/minicopter.entity.prefab";
        private readonly Dictionary<string,string> _nativeMeshes = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        // Maps recorded KuroPrefabLab IDs to Native mesh keys; duplicate shapes keep both IDs.
        private readonly Dictionary<int,string> _nativePartIds = new Dictionary<int,string>();
        private readonly List<HelicopterDebris> _nativeBatchCaptured = new List<HelicopterDebris>();
        private bool _nativeCapturing;
        private float _nativeNextAttempt;

        private static readonly string[] Ids = {
            "HEAD", "TORSO", "WAIST", "R_UPPER_ARM", "R_FOREARM", "L_UPPER_ARM", "L_FOREARM",
            "R_THIGH", "R_SHIN", "R_FOOT", "L_THIGH", "L_SHIN", "L_FOOT"
        };
        private static readonly string[] Names = {
            "あたま", "どうたい", "こし", "右うで上", "右うで下", "左うで上", "左うで下",
            "右もも", "右すね", "右あし", "左もも", "左すね", "左あし"
        };
        [PluginReference] private Plugin MechBossPrototype;

        // Built-in fallbacks. On each load / sync, MechBossPrototype config + saved template ExtraParts
        // are merged into the runtime catalog. The runtime catalog is never written back to MechBoss.
        private static readonly Dictionary<string,string> BuiltinCatalog = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"none", ""},
            {"post", "assets/prefabs/deployable/signs/sign.post.single.prefab"},
            {"sign", "assets/prefabs/deployable/signs/sign.small.wood.prefab"},
            {"footplate", "assets/prefabs/deployable/signs/sign.small.wood.prefab"},
            {"legbeam", "assets/prefabs/deployable/signs/sign.post.single.prefab"},
            {"leg_snowmobile", "assets/content/vehicles/snowmobiles/snowmobile.prefab"},
            {"leg_locker", "assets/prefabs/deployable/locker/locker.campermodule.prefab"},
            {"light", "assets/prefabs/deployable/playerioents/lights/simplelight.prefab"},
            {"ceiling", "assets/prefabs/deployable/ceiling light/ceilinglight.deployed.prefab"},
            {"siren", "assets/prefabs/deployable/playerioents/lights/sirenlight/electric.sirenlight.deployed.prefab"},
            {"spike", "assets/prefabs/deployable/spike trap/spikes.trap.prefab"},
            {"turret", "assets/prefabs/npc/autoturret/autoturret_deployed.prefab"},
            {"chair", "assets/prefabs/deployable/chair/chair.deployed.prefab"},
            {"snowmobile", "assets/content/vehicles/snowmobiles/snowmobile.prefab"},
            {"tomaha", "assets/content/vehicles/snowmobiles/tomahasnowmobile.prefab"},
            {"bike", "assets/content/vehicles/bikes/motorbike.prefab"},
            {"cockpit", "assets/content/vehicles/modularcar/module_entities/1module_cockpit.prefab"},
            {"armoredcockpit", "assets/content/vehicles/modularcar/module_entities/1module_cockpit_armored.prefab"},
            {"camper", "assets/content/vehicles/modularcar/module_entities/2module_camper.prefab"},
            {"bbq", "assets/prefabs/deployable/bbq/bbq.campermodule.prefab"},
            {"locker", "assets/prefabs/deployable/locker/locker.campermodule.prefab"},
            {"foot_locker", "assets/prefabs/deployable/locker/locker.campermodule.prefab"},
            {"foot_snowmobile", "assets/content/vehicles/snowmobiles/snowmobile.prefab"},
            {"foot_cockpit", "assets/content/vehicles/modularcar/module_entities/1module_cockpit.prefab"},
            // Keep this alias so old blueprints can be loaded, but it is intentionally non-spawnable.
            {"helicockpit", "assets/content/props/helicopter_parts/heli_cockpit.prefab"},
            {"bradley", "assets/prefabs/npc/m2bradley/bradleyapc.prefab"},
            {"gib018", ""},
            // Actual scrap transport debris entity. It is NOT a selectable per-mesh gib; gated until explicitly enabled in config.
            {"scrapheli_debris", "assets/content/vehicles/scrap heli carrier/servergibs_scraptransport.prefab"}
        };
        private readonly Dictionary<string,string> _catalog = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _catalogOrder = new List<string>();
        // UI-only validation cache, invalidated every time the catalog is rebuilt.
        private readonly Dictionary<string,string> _catalogCheck = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _mechBossImportedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _mechBossImportedCount;
        private string _mechBossSyncStatus = "未同期";
        private class Settings {
            public bool AllowVehiclePreview = true;
            public bool AllowNpcBradleyPreview = false; // Diagnostics only: Bradley AI is never spawned by this prototype.
            public int MaxActiveWorkshops = 2;
            public int MaxEntitiesPerPreview = 13;
            public int MaxFreeParts = 32;
            // Free-build caps are disabled in v0.4.13 test builds; installed works are keyed independently.
            public bool RestoreInstalledModelsOnStartup = true;
            // Emergency stop persisted across oxide.reload. Stops new restoration/repair spawns, not existing entities.
            public bool PauseInstalledAutoRestoration = true; // v0.4.14 upgrade safety: explicit /kmb free resume once
            public float InstalledRepairCheckSeconds = 60f;
            public int MaxInstalledDesigns = 12; // named blueprints per administrator
            public int MaxInstalledNativeParts = 16; // per installed model

            // HelicopterDebris is a gameplay entity with a lifetime and damage hooks, not a durable mesh.
            // Keep disabled unless validating on a separate test server.
            public bool AllowScrapHeliDebrisPreview = false;
            public int MaxScrapHeliDebrisParts = 1;
            // Deliberately disabled by default. KPL's native API creates a whole temporary debris batch.
            public bool EnableNativeScrapGibs = false;
            // EXPERIMENTAL: creates an unnetworked temporary emitter rather than requiring a parked heli.
            // This depends on undocumented Rust internals. Defaults OFF and never switches on automatically.
            public bool ExperimentalNativeWithoutHeli = false;
            // Without-heli mode still creates a full batch (about 47 debris), then keeps one.
            // Do not remove throttling completely; tweak with /kmb gib delay <seconds>.
            public float NativeNoHeliCooldownSeconds = 5f;
            // Fast grouped restoration: a single debris batch can serve multiple distinct Gib shapes.
            public float InstalledNativeBatchCooldownSeconds = 2f;
            // v0.4.51: bounded burst mode. Several CreateGibs batches are consumed in one pass,
            // so repeated copies of the same scrap-heli mesh no longer wait seconds each.
            public bool FastNativeBurstSpawn = true;
            public int FastNativeBurstMaxBatches = 6;
            public float FastNativeBurstIntervalSeconds = 0.12f;
            public int NativeNoHeliMaxParts = 16;
            public int MaxNativeGibParts = 4;
            public float NativeGibCooldownSeconds = 30f; // legacy one-shot mode only
            // Keep native Gib generation available while free-build is active.
            // Still requires EnableNativeScrapGibs=true, a stationary nearby scrap heli,
            // and clear surroundings. No expiring 45-second manual permission.
            public bool NativeContinuousPlacement = true;
            public int NativeContinuousMaxParts = 16;
            public float NativeContinuousCooldownSeconds = 2.5f;
            public int MaxVehiclePreviewParts = 8;
            public float SpawnDistance = 8f;
            public float MinimumOtherPlayerDistance = 35f;
            public float PreviewLifetimeSeconds = 7200f;
            // Free-build workshops have no time limit. Classic 13-part previews retain their timer.
            public bool KeepFreeWorkshopAlive = true;
            // Free assembly is an admin tool, not Rust's BuildingPlan placement.
            // Ignore workshop-only terrain slope and 10m building block exclusion.
            // This does not change building privilege checks for ordinary construction.
            public bool FreeIgnoreBuildingArea = true;
            // Editing radius and editable local offsets for free parts (metres).
            // This is independent of the 13-joint mech controls.
            public float FreeEditRangeMeters = 100f;
            // Low-frequency liveness check; never spawns more than one native gib per pass.
            public float FreeRepairIntervalSeconds = 12f;
            // Cancel late-scheduled Rust debris cleanup during active free-build and permanent installations.
            // This does not force an entity to exist; destroyed GIBs are handled by bounded repairs.
            public float NativeKeepAliveSeconds = 2f;
            // Visual swim replication rate. 20 Hz was visibly choppy on large Native-Gib assemblies.
            public float SwimUpdateHz = 30f;
            // Minicopter network root is buried below the authored model root so its visual body is hidden inside/below the craft.
            public float SwimMiniRootYOffset = 0.0f;
            public bool SwimMiniRootAutoCenter = true;
            // Keep the embedded Minicopter colliders active so rockets/bullets can hit the moving craft.
            // The root remains kinematic and KMB cancels its gameplay damage, so this is a hit target only.
            public bool SwimMiniRootHitbox = true;
            public bool NativeLifetimeDiagnostics = true;
            public float WaistYawLimit = 60f;
            public float PunchDurationSeconds = 1.55f;
            public float PunchUpdateInterval = 0.066f;
            public float PunchWaistTwist = 12f;
            public float PunchShoulderWindup = -18f;
            public float PunchShoulderStrike = 55f;
            public float PunchElbowWindup = 55f;
            public float PunchElbowStrike = 10f;
            public bool ShowPlaceholdersForMissingParts = true;
            public bool SpawnSafeProxyForMissingParts = true;

            // v0.3.3 keeps the stable v0.3.1 WAIST-root movement model.
            // The cockpit position may follow the torso, but cockpit yaw can be decoupled from
            // animated upper-body WaistYaw so the pilot stays facing the mech chassis direction.
            public bool SpawnPilotSeat = true;
            public string PilotSeatPrefab = "assets/bundled/prefabs/static/chair.static.prefab";
            public bool PilotSeatFaceMechForward = true;
            public float PilotSeatForwardYawOffset = 0f;
            public float PilotMoveSpeed = 2.4f;
            public float PilotReverseSpeed = 1.45f;
            public float PilotTurnSpeed = 42f;
            public float PilotSprintMultiplier = 1.35f;
            public float PilotControlInterval = 0.04f;
            public float PilotMaxGroundStep = 0.70f;
            public float PilotBuildingStopRadius = 1.35f;
        }
        private class Vec {
            public float X; public float Y; public float Z;
            public Vec() {} public Vec(float x,float y,float z) {X=x;Y=y;Z=z;}
            public Vector3 V() { return new Vector3(X,Y,Z); }
            public Vec Copy() { return new Vec(X,Y,Z); }
            public static Vec From(Vector3 v) { return new Vec(v.x,v.y,v.z); }
        }
        private class Slot {
            public string Id = "";
            // Joint transform relative to the parent joint. Children follow this transform.
            public Vec Position = new Vec();
            public Vec Rotation = new Vec();
            // Visual-only offset. Direct PART editing changes these without moving child joints.
            public Vec VisualPosition = new Vec();
            public Vec VisualRotation = new Vec();
            public float VisualScale = 1f;
            public string PrefabKey = "none";
            public string SourceKind = "PrefabCandidate";
            public string ShapeId = "";
        }
        private class SlotState {
            public Vec Position = new Vec();
            public Vec Rotation = new Vec();
            public Vec VisualPosition = new Vec();
            public Vec VisualRotation = new Vec();
            public float VisualScale = 1f;
            public SlotState Copy() {
                return new SlotState {
                    Position=Position!=null?Position.Copy():new Vec(),
                    Rotation=Rotation!=null?Rotation.Copy():new Vec(),
                    VisualPosition=VisualPosition!=null?VisualPosition.Copy():new Vec(),
                    VisualRotation=VisualRotation!=null?VisualRotation.Copy():new Vec(),
                    VisualScale=VisualScale
                };
            }
        }
        private class PoseState {
            public float WaistYaw;
            public float RootYaw;
            public Dictionary<string,SlotState> Slots = new Dictionary<string,SlotState>();
        }
        private class Blueprint {
            // Saved-design time (UTC ticks). Zero means saved by an older version.
            public long FreeSavedUtcTicks;
            public int Version = 16;
            public string Name = "二足歩行ロボット";
            public float WaistYaw;
            public float RootYaw;
            public bool HasStandPose;
            public float StandWaistYaw;
            public Dictionary<string,Vec> StandRotations = new Dictionary<string,Vec>();
            // v0.1.9.1+: full pose snapshots include joint position/rotation, visual offset/rotation and scale.
            public PoseState StandPose = new PoseState();

            // Recorded wind-up poses. v0.2.1+: waist yaw is captured too, so the arm can pull back while the torso twists.
            public bool HasRightWindupPose;
            public bool HasLeftWindupPose;
            public SlotState RightWindupShoulderState = new SlotState();
            public SlotState RightWindupElbowState = new SlotState();
            public SlotState LeftWindupShoulderState = new SlotState();
            public SlotState LeftWindupElbowState = new SlotState();
            public bool HasRightWindupWaist;
            public bool HasLeftWindupWaist;
            public float RightWindupWaistYaw;
            public float LeftWindupWaistYaw;

            // Recorded punch end poses. Legacy rotation-only fields are kept for backward compatibility.
            // not hard-coded axis offsets, so arbitrary prefab orientations can animate correctly.
            public bool HasRightPunchPose;
            public Vec RightPunchShoulder = new Vec();
            public Vec RightPunchElbow = new Vec();
            public float RightPunchWaistYaw;
            public bool HasLeftPunchPose;
            public Vec LeftPunchShoulder = new Vec();
            public Vec LeftPunchElbow = new Vec();
            public float LeftPunchWaistYaw;

            public SlotState RightPunchShoulderState = new SlotState();
            public SlotState RightPunchElbowState = new SlotState();
            public SlotState LeftPunchShoulderState = new SlotState();
            public SlotState LeftPunchElbowState = new SlotState();

            // v0.2.3: persistent automatic punch playback. The actual poses above remain the source of truth,
            // so upgrading does not require re-registering the current stand/wind-up/strike settings.
            public bool AutoPunchEnabled = false;
            public string AutoPunchMode = "alternate"; // right / left / alternate
            public float AutoPunchIntervalSeconds = 3.0f;

            // Pilot chair transform, local to the current TORSO visual pose. Keeping it in the
            // blueprint means each robot design can place its cockpit independently.
            public Vec PilotSeatPosition = new Vec(0f,0.35f,-0.15f);
            public Vec PilotSeatRotation = new Vec(0f,0f,0f);

            public Dictionary<string,Slot> Slots = new Dictionary<string,Slot>();
            public bool FreeOnly = false;
            public int FreeSerial = 0;
            public Dictionary<string,FreePart> FreeParts = new Dictionary<string,FreePart>();
            // Generic articulated groups for free-build creatures/robots. Motion is evaluated in model-root local space.
            public Dictionary<string,FreeMotion> FreeMotions = new Dictionary<string,FreeMotion>(StringComparer.OrdinalIgnoreCase);
            // v0.4.47: real dedicated jaw rig. Upper/lower jaws share one physical-looking round editor joint.
            public MouthJointRig MouthJoint = new MouthJointRig();
        }
        // Dedicated mouth articulation, independent from generic FreeMotion groups.
        // Structure: head -> MouthJoint -> upper jaw / lower jaw.
        private class MouthJointRig {
            public bool Enabled = true;
            public bool HasJoint = false;
            public Vec JointPosition = new Vec();
            public float JointRadius = 0.32f;
            public string Axis = "X";
            public List<string> UpperJawParts = new List<string>();
            public List<string> LowerJawParts = new List<string>();
            public float UpperAngle = 4f;
            public float LowerAngle = 10f;
            public float Speed = 0.12f;
            public bool Invert = false;
        }
        private class FreeMotion {
            public string Name = "motion";
            public List<string> PartIds = new List<string>();
            // Optional motion hierarchy. A child inherits the transform of its parent group.
            // Example creature chain: 首 -> 頭 -> 口.
            public string ParentMotion = "";
            // Structural groups (neck/head) can act as selection/joint containers without their own sine oscillation.
            public bool FollowOnly = false;
            public string PivotPartId = "";
            // Optional free-position spherical joint, stored in model-root local space.
            // When enabled it overrides PivotPartId and becomes the true rotation hinge.
            public bool UseJointPivot = false;
            public Vec JointPosition = new Vec();
            public float JointRadius = 0.28f;
            public string Axis = "Y"; // X / Y / Z in model local space
            public float Angle = 20f;   // +/- degrees
            public float Speed = 0.8f;  // cycles per second
            public float Phase = 0f;    // degrees
            // When true, motion runs 0..Angle instead of -Angle..+Angle. Useful for jaws/doors.
            public bool OneWay = false;
            // +1 normal / -1 reverse. Kept separate from Angle so UI can continue using positive degrees.
            public float Direction = 1f;
            public bool Enabled = true;
        }
        // Independent of the legacy humanoid slots: no predefined body/arm/leg requirements.
        private class FreePart {
            public string Id = "";
            public string Name = "";
            public string PrefabKey = "post";
            public string ParentId = ""; // empty: workshop root
            public Vec Position = new Vec(); // local joint translation relative to parent visual
            public Vec Rotation = new Vec(); // local joint rotation
            public Vec VisualPosition = new Vec(); // reserved visual offset for future pivot editor
            public Vec VisualRotation = new Vec();
            public float Scale = 1f;
            // Parts spawned together from a reusable module share this id. They remain separate Rust
            // entities but are manipulated as one KMB assembly.
            public string ModuleId = "";
            // Set only for a native-selected HelicopterDebris; this is not a separate prefab path.
            public string NativeMesh = "";
        }
        private class FreeModule {
            public string Name = "module";
            public long SavedUtcTicks;
            public Dictionary<string,FreePart> Parts = new Dictionary<string,FreePart>(StringComparer.OrdinalIgnoreCase);
        }
        private class InstalledFreeModel {
            public string Name = "default";
            // Persistent user-facing selector. It is derived from owner + design name and survives reload/reinstall.
            public string StableId = "";
            public long CreatedUtcTicks;
            public Vec RootPosition = new Vec();
            public Vec RootRotation = new Vec();
            // Per-design manual tweak added on top of the automatic visible-part center.
            public Vec MiniRootOffset = new Vec();
            // Creature-style tracking. Saved per installed design.
            public bool AiFacePlayers = false;
            // Default AI is LOOK, not chase: stop, face the target, then resume cruise when target is gone.
            public bool AiChasePlayers = false;
            public float AiDetectRadius = 60f;
            public float AiTurnSpeed = 18f;
            public float AiStopDistance = 18f;
            public float AiResumeDistance = 26f;
            public string AiMouthMotion = "口"; // legacy combined mouth group
            public string AiUpperJawMotion = "上顎";
            public string AiLowerJawMotion = "下顎";
            public float AiMouthSpeed = 0.12f; // cycles/sec: ~8.3 seconds per open/close cycle
            public float AiMouthAngle = 8f;    // lower-jaw travel; upper jaw uses a smaller share
            public float AiUpperJawAngle = 4f;
            // Current creature build uses the opposite hinge direction: lower jaw should rise toward the upper jaw.
            public bool AiMouthInvert = true;
            // Default jaw hinge axis. X normally gives up/down opening for the current creature build.
            public string AiMouthAxis = "X";
            // Neck/head tracking group. This is a selection/pivot group, not an oscillating motion while AI is active.
            public string AiNeckMotion = "首";
            public float AiNeckYawLimit = 55f;
            public float AiNeckPitchLimit = 22f;
            public float AiNeckTurnSpeed = 40f;
            public float AiNeckYawOffset = 0f;
            // The current neck/head assembly extends opposite the model-root forward axis.
            // Invert both tracking axes by default; each can be changed independently from chat.
            public bool AiNeckYawInvert = true;
            public bool AiNeckPitchInvert = true;
            // Legacy whole-body look calibration. Used only by explicit CHASE mode.
            public float AiLookYawOffset = 0f;
            public Blueprint Blueprint = new Blueprint();
        }
        private class Store {
            public Dictionary<string,Blueprint> Builders = new Dictionary<string,Blueprint>();
            public Dictionary<string,Dictionary<string,Blueprint>> SavedFreeDesigns = new Dictionary<string,Dictionary<string,Blueprint>>();
            public Dictionary<string,Dictionary<string,FreeModule>> SavedFreeModules = new Dictionary<string,Dictionary<string,FreeModule>>();
            public Dictionary<string,InstalledFreeModel> InstalledFreeModels = new Dictionary<string,InstalledFreeModel>();
        }
        private class Editor {
            public bool UiOpen;
            public string Selected = "TORSO";
            public string FreeSelected = "";
            public string FreeSearch = "";
            public string FreeCatalogScope = "gibs"; // gibs / scrap / mech / all
            public int FreeCatalogPage = 0;
            public int FreePartPage = 0;
            public bool FreeDesignListOpen = false;
            // Display auto-recovery snapshots separately; these are not independent finished models.
            public bool FreeShowBackupDesigns = false;
            public int FreeDesignPage = 0;
            public bool FreeParentPick = false;
            public bool FreeDirect = false;
            public bool FreeGrab = false;
            // Round joint placement mode for free-motion pivots. The sphere is editor-only (ddraw).
            public bool FreeJointPlace = false;
            public string FreeJointMotion = ""; // motion name, or special token "__MOUTHJOINT__"
            public float FreeJointDistance = 6f;
            public bool FreeSwitchOpen = false; // right-click palette: replace aimed part, or add when no target
            public float FreeClearConfirmUntil;
            public float FreeNewConfirmUntil;
            public float FreeUnplaceConfirmUntil;
            public float FreeDuplicateCleanupConfirmUntil;
            public string FreeUnplacePendingId = "";
            // Separate two-click confirmation for uninstalling a saved design from the gallery.
            public string FreeDesignUnplacePendingName = "";
            public float FreeDesignUnplaceConfirmUntil;
            public string FreeManipMode = "MOVE";
            public float FreeGrabDistance = 6f;
            public float FreeGrabStartScale = 1f;
            public Vec FreeGrabStartRotation = new Vec();
            public Vector3 FreeGrabStartView;
            public float FreeLastUpdate = 0f;
            public float FreeLastMarker = 0f;
            public string FreeModuleName = "";
            public HashSet<string> FreeModuleSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string FreeGrabModuleId = "";
            public Dictionary<string,Vec> FreeGroupStartPositions = new Dictionary<string,Vec>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string,Vec> FreeGroupStartRotations = new Dictionary<string,Vec>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string,float> FreeGroupStartScales = new Dictionary<string,float>(StringComparer.OrdinalIgnoreCase);
            public Vector3 FreeGroupStartCenter = Vector3.zero;
            public float NativeGibArmedUntil;
            public int Page;
            public float MoveStep = 0.05f;
            public float AngleStep = 1f;
            public string Message = "";
            public bool DirectEdit;
            public bool Symmetry = true;
            public bool MarkersVisible = true;
            public int DirectCandidateIndex;
            public bool Grabbed;
            public string GrabKind = ""; // PART or JOINT
            public string GrabId = "";
            public string ManipMode = "MOVE"; // MOVE / ROT_Y / ROT_X / ROT_Z
            public float GrabDistance = 6f;
            public Vec GrabStartRotation = new Vec();
            public float GrabStartScale = 1f;
            public float GrabStartWaistYaw;
            public Vector3 GrabViewAngles;
            public float LastDirectUpdate;
            public float LastMarkerDraw;
            public float LastAimProbe;
            public string ManualCandidateKind = "";
            public string ManualCandidateId = "";
            public float ManualCandidateUntil;
            public Vector3 ManualCandidateView;
        }
        private class DirectCandidate {
            public string Kind = "";
            public string Id = "";
            public Vector3 Position;
            public float Along;
            public float RayDistance;
            public float AngleDeg;
            public bool ExactHit;
            public int Priority;
        }
        private class Preview {
            public ulong Owner;
            public Vector3 RootPosition;
            public Quaternion RootRotation;
            public float StartedAt;
            public Timer Lifetime;
            public Timer Markers;
            public Timer EditMarkers;
            public Timer FreeMaintenance;
            public Timer MouthJointPreview;
            // Per-part retry budget prevents a disappearing native gib from generating forever.
            public readonly Dictionary<string,int> FreeRepairAttempts = new Dictionary<string,int>();
            public BaseMountable PilotSeat;
            public BasePlayer Pilot;
            public float LastPilotControlAt;
            public BaseEntity PilotMotionAnchor;
            public bool PilotHierarchyActive;
            public readonly Dictionary<string,BaseEntity> Entities = new Dictionary<string,BaseEntity>();
            public readonly Dictionary<string,BaseEntity> FreeEntities = new Dictionary<string,BaseEntity>();
            public readonly Dictionary<string,string> Results = new Dictionary<string,string>();
        }
        private class MotionTest {
            public Timer Timer;
            public string SlotId = "";
            public string PairId = "";
            public Vec BaseRotation = new Vec();
            public Vec PairBaseRotation = new Vec();
            public string Axis = "X";
            public float Amplitude = 30f;
            public float Speed = 0.65f;
            public float StartedAt;
        }
        private class PunchTest {
            public Timer Timer;
            public bool Quiet;
            public string Side = "R";
            public string ShoulderId = "R_UPPER_ARM";
            public string ElbowId = "R_FOREARM";
            public SlotState BaseShoulder = new SlotState();
            public SlotState BaseElbow = new SlotState();
            public float BaseWaistYaw;
            public SlotState WindupShoulder = new SlotState();
            public SlotState WindupElbow = new SlotState();
            public float WindupWaistYaw;
            public SlotState TargetShoulder = new SlotState();
            public SlotState TargetElbow = new SlotState();
            public float TargetWaistYaw;
            public float StartedAt;
            public float Duration;
        }
        private class AutoPunchLoop {
            public Timer Timer;
            public bool NextRight = true;
            public float NextAt;
        }

        private Settings _config;
        private Store _data;
        private readonly Dictionary<ulong,Editor> _editors = new Dictionary<ulong,Editor>();
        private readonly Dictionary<ulong,Preview> _previews = new Dictionary<ulong,Preview>();
        private readonly Dictionary<string,Preview> _installedPreviews = new Dictionary<string,Preview>();
        private readonly Dictionary<string,Dictionary<string,int>> _installedAttempts = new Dictionary<string,Dictionary<string,int>>();
        // Old v0.4.13 installations may contain multiple identical records after button mashing.
        // Keep the records for safe manual cleanup, but restore only the first unique design.
        private readonly HashSet<string> _installedDuplicateSkipped = new HashSet<string>(StringComparer.Ordinal);
        private Timer _installedRestoreTimer;
        private readonly Dictionary<ulong,Timer> _fastFreeRestoreTimers = new Dictionary<ulong,Timer>();
        // Each swim runtime owns only one permanent world instance; saved blueprints are never moved.
        private class InstalledSwim {
            public Timer Timer;
            // Target speed in real metres/second. CurrentSpeed ramps toward it to avoid a hard snap.
            public float Speed=4f;
            public float CurrentSpeed=0f;
            public float Acceleration=8f;
            public float TurnDegrees=0f;
            public float LastSave;
            public float LastStep;
            public Vector3 SimPosition;
            public Quaternion SimRotation;
            // Do not parent Native debris to another Native debris. A small, stable non-gib entity
            // is placed below the craft and used only as a network motion root. The craft pieces
            // keep their exact world pose when attached, then the client receives one moving root.
            public BaseEntity MotionRoot;
            public float AnchorYOffset=-5.5f;
            public float LastNetworkUpdate;
            public float LastNetworkGroupUpdate;
            public float UpdateHz=30f;
            public float ActualHz;
            public float RateWindowStart;
            public int RateTicks;
            public readonly HashSet<string> ParentPartIds=new HashSet<string>(StringComparer.Ordinal);
            public readonly Dictionary<string,Vector3> LocalPositions=new Dictionary<string,Vector3>(StringComparer.Ordinal);
            public readonly Dictionary<string,Quaternion> LocalRotations=new Dictionary<string,Quaternion>(StringComparer.Ordinal);
            // This particular free-build model was authored with its visual nose opposite Unity +Z.
            // Default to -1 so "start" moves nose-first; /reverse can toggle it per runtime.
            public float Direction=-1f;
            // v0.4.35: when true, MotionRoot is an embedded Minicopter homing target; visuals remain software-rigid and are never parented.
            public bool UseMiniRoot;
            // Root position relative to the authored model root. This is captured once when the rig starts.
            public Vector3 MiniRootLocalOffset;
            // v0.4.39 nearest-player facing runtime state.
            public ulong AiTargetUserId;
            public float LastAiTargetScan;
            public bool AiHoldingPosition;
            // Neck-only tracking runtime. LOOK mode changes these angles while the body keeps cruising.
            public float AiNeckYaw;
            public float AiNeckPitch;
            // Kept for backwards compatibility with older saves/runtimes; no longer needed by neck-only LOOK.
            public float LastAiLookFullUpdate;
        }
        private readonly Dictionary<string,InstalledSwim> _installedSwimmers=new Dictionary<string,InstalledSwim>();
        private Timer _installedRepairTimer;
        private Timer _nativeKeepAliveTimer;
        private bool _nativeShuttingDown;
        private class NativeWatch {
            public string Owner;
            public string PartId;
            public float CreatedAt;
        }
        private readonly Dictionary<HelicopterDebris, NativeWatch> _nativeWatches = new Dictionary<HelicopterDebris, NativeWatch>();
        private readonly Queue<string> _nativeLifetimeEvents = new Queue<string>();
        private readonly Dictionary<ulong,MotionTest> _motionTests = new Dictionary<ulong,MotionTest>();
        private readonly Dictionary<ulong,PunchTest> _punchTests = new Dictionary<ulong,PunchTest>();
        private readonly Dictionary<ulong,AutoPunchLoop> _autoPunchLoops = new Dictionary<ulong,AutoPunchLoop>();
        private int _uiSerial;

        protected override void LoadDefaultConfig() { _config = new Settings(); Config.WriteObject(_config,true); }
        private void Init() {
            permission.RegisterPermission(PermUse,this);
            try { _config = Config.ReadObject<Settings>(); } catch { _config = new Settings(); }
            if (_config == null) _config = new Settings();
            // Unlimited test: retired limit _config.MaxActiveWorkshops = Mathf.Clamp(_config.MaxActiveWorkshops,1,3);
            _config.MaxEntitiesPerPreview = Mathf.Clamp(_config.MaxEntitiesPerPreview,1,13);
            // Unlimited test: retired limit _config.MaxVehiclePreviewParts = Mathf.Clamp(_config.MaxVehiclePreviewParts,0,13);
            _config.SpawnDistance = Mathf.Clamp(_config.SpawnDistance,4f,20f);
            _config.PreviewLifetimeSeconds = Mathf.Clamp(_config.PreviewLifetimeSeconds,120f,14400f);
            _config.FreeRepairIntervalSeconds = Mathf.Clamp(_config.FreeRepairIntervalSeconds,8f,60f);
            _config.NativeKeepAliveSeconds = Mathf.Clamp(_config.NativeKeepAliveSeconds,1f,15f);
            _config.SwimUpdateHz = Mathf.Clamp(_config.SwimUpdateHz,10f,50f);
            _config.SwimMiniRootYOffset = Mathf.Clamp(_config.SwimMiniRootYOffset,-8f,1f);
            _config.FreeEditRangeMeters = Mathf.Clamp(_config.FreeEditRangeMeters,25f,200f);
            _config.WaistYawLimit = Mathf.Clamp(_config.WaistYawLimit,0f,180f);
            _config.PunchDurationSeconds = Mathf.Clamp(_config.PunchDurationSeconds,0.45f,3.0f);
            _config.PunchUpdateInterval = Mathf.Clamp(_config.PunchUpdateInterval,0.04f,0.20f);
            _config.PunchWaistTwist = Mathf.Clamp(_config.PunchWaistTwist,0f,35f);
            _config.PunchShoulderWindup = Mathf.Clamp(_config.PunchShoulderWindup,-75f,75f);
            _config.PunchShoulderStrike = Mathf.Clamp(_config.PunchShoulderStrike,-85f,85f);
            _config.PunchElbowWindup = Mathf.Clamp(_config.PunchElbowWindup,-85f,85f);
            _config.PunchElbowStrike = Mathf.Clamp(_config.PunchElbowStrike,-85f,85f);
            _config.PilotMoveSpeed = Mathf.Clamp(_config.PilotMoveSpeed,0.25f,8f);
            _config.PilotReverseSpeed = Mathf.Clamp(_config.PilotReverseSpeed,0.20f,6f);
            _config.PilotTurnSpeed = Mathf.Clamp(_config.PilotTurnSpeed,5f,120f);
            _config.PilotSprintMultiplier = Mathf.Clamp(_config.PilotSprintMultiplier,1f,2.5f);
            if(_config.PilotControlInterval>=0.095f&&_config.PilotControlInterval<=0.105f)_config.PilotControlInterval=0.04f; // migrate v0.3.0 default
            _config.PilotControlInterval = Mathf.Clamp(_config.PilotControlInterval,0.03f,0.20f);
            _config.PilotSeatForwardYawOffset = Mathf.Clamp(_config.PilotSeatForwardYawOffset,-180f,180f);
            _config.PilotMaxGroundStep = Mathf.Clamp(_config.PilotMaxGroundStep,0.10f,2.0f);
            _config.PilotBuildingStopRadius = Mathf.Clamp(_config.PilotBuildingStopRadius,0.5f,4f);
            if(string.IsNullOrEmpty(_config.PilotSeatPrefab))_config.PilotSeatPrefab="assets/bundled/prefabs/static/chair.static.prefab";
            Config.WriteObject(_config,true);
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<Store>(DataFile); } catch { _data = new Store(); }
            if (_data == null) _data = new Store();
            if (_data.Builders == null) _data.Builders = new Dictionary<string,Blueprint>();
            if (_data.SavedFreeDesigns == null) _data.SavedFreeDesigns = new Dictionary<string,Dictionary<string,Blueprint>>();
            if (_data.InstalledFreeModels == null) _data.InstalledFreeModels = new Dictionary<string,InstalledFreeModel>();
            EnsureInstalledStableIds();
            // Unlimited test: retired limit _config.MaxInstalledNativeParts=Mathf.Clamp(_config.MaxInstalledNativeParts,1,32);
            // Unlimited test: retired limit _config.MaxInstalledDesigns=Mathf.Clamp(_config.MaxInstalledDesigns,1,40);
            _config.InstalledRepairCheckSeconds=Mathf.Clamp(_config.InstalledRepairCheckSeconds,30f,300f);
            // Unlimited test: retired limit _config.MaxFreeParts=Mathf.Clamp(_config.MaxFreeParts,1,64);
            // Unlimited test: retired limit _config.MaxScrapHeliDebrisParts=Mathf.Clamp(_config.MaxScrapHeliDebrisParts,0,3);
            // Unlimited test: retired limit _config.MaxNativeGibParts=Mathf.Clamp(_config.MaxNativeGibParts,0,8);
            _config.NativeGibCooldownSeconds=Mathf.Clamp(_config.NativeGibCooldownSeconds,15f,180f);
            // Unlimited test: retired limit _config.NativeContinuousMaxParts=Mathf.Clamp(_config.NativeContinuousMaxParts,1,32);
            _config.NativeContinuousCooldownSeconds=Mathf.Clamp(_config.NativeContinuousCooldownSeconds,2f,15f);
            _config.NativeNoHeliCooldownSeconds=Mathf.Clamp(_config.NativeNoHeliCooldownSeconds,3f,30f);
            _config.InstalledNativeBatchCooldownSeconds=Mathf.Clamp(_config.InstalledNativeBatchCooldownSeconds,1.5f,30f);
            _config.FastNativeBurstMaxBatches=Mathf.Clamp(_config.FastNativeBurstMaxBatches,1,12);
            _config.FastNativeBurstIntervalSeconds=Mathf.Clamp(_config.FastNativeBurstIntervalSeconds,0.08f,1.0f);
            // Unlimited test: retired limit _config.NativeNoHeliMaxParts=Mathf.Clamp(_config.NativeNoHeliMaxParts,1,32);
            SyncMechBossCatalog();
        }
        private void OnServerInitialized() {
            SyncMechBossCatalog();
            StartNativeKeepAlive();
            if(_config.RestoreInstalledModelsOnStartup&&!_config.PauseInstalledAutoRestoration)
                timer.Once(10f,StartInstalledFreeRestoration);
            else if(_config.PauseInstalledAutoRestoration)Puts("KMB installed restoration is PAUSED. Use /kmb free resume as admin.");
        }
        private void OnPluginLoaded(Plugin plugin) {
            if(plugin != null && string.Equals(plugin.Name,"MechBossPrototype",StringComparison.OrdinalIgnoreCase)) SyncMechBossCatalog();
        }
        private void OnServerSave() { SaveData(); }
        private void Unload() {
            _nativeShuttingDown=true;
            if(_nativeKeepAliveTimer!=null){_nativeKeepAliveTimer.Destroy();_nativeKeepAliveTimer=null;}
            foreach (var user in BasePlayer.activePlayerList) { CuiHelper.DestroyUi(user,UiRoot); CuiHelper.DestroyUi(user,DirectUiRoot); CuiHelper.DestroyUi(user,FreeUiRoot); }
            foreach(var key in _motionTests.Keys.ToList()) StopMotionTest(key,true);
            foreach(var key in _autoPunchLoops.Keys.ToList()) StopAutoPunchRuntime(key);
            foreach(var key in _punchTests.Keys.ToList()) StopPunchTest(key,true,false);
            foreach(var key in _previews.Keys.ToList()) RemovePreview(key);
            if(_installedRestoreTimer!=null){_installedRestoreTimer.Destroy();_installedRestoreTimer=null;}
            if(_installedRepairTimer!=null){_installedRepairTimer.Destroy();_installedRepairTimer=null;}
            foreach(var t in _fastFreeRestoreTimers.Values)if(t!=null)t.Destroy();
            _fastFreeRestoreTimers.Clear();
            foreach(var key in _installedSwimmers.Keys.ToList()) StopInstalledSwim(key);
            foreach(var key in _installedPreviews.Keys.ToList()) RemoveInstalledFreeRuntime(key);
            _nativeCapturing=false;_nativeBatchCaptured.Clear();
            _nativeWatches.Clear();
            SaveData();
        }
        // Keep a single synchronous capture hook for ServerGib.CreateGibs batches.
        private void OnEntitySpawned(BaseNetworkable net) {
            if(!_nativeCapturing)return;
            var gib=net as HelicopterDebris;
            if(gib!=null && string.Equals(gib.PrefabName,NativeScrapPrefab,StringComparison.OrdinalIgnoreCase)
                && !_nativeBatchCaptured.Contains(gib)) _nativeBatchCaptured.Add(gib);
        }
        // Observe native GIB destruction without interfering with Rust/Carbon or other plugins.
        // Useful when a part disappears despite CancelInvoke protection.
        private void OnEntityKill(BaseNetworkable entity) {
            var gib=entity as HelicopterDebris;
            if(gib==null)return;
            NativeWatch watch;
            if(!_nativeWatches.TryGetValue(gib,out watch))return;
            _nativeWatches.Remove(gib);
            if(_nativeShuttingDown)return;
            string detail="[KMB NativeLifetime] LOST "+watch.Owner+"/"+watch.PartId+
                " after "+Mathf.Max(0f,Time.realtimeSinceStartup-watch.CreatedAt).ToString("0.0",CultureInfo.InvariantCulture)+"s"+
                " (OnEntityKill; Rust timer / damage / other plugin are possible; cause not proven)";
            RecordNativeLifetimeEvent(detail);
        }
        // KuroMechBuilder free-build pieces are decorative construction parts. They must not be
        // damaged by vehicle collisions, splash/impact damage, weapons, decay, or other gameplay hits.
        // Only entities that are currently owned by a KMB workshop/permanent model are protected.
        private bool IsKmbManagedFreeEntity(BaseNetworkable entity) {
            var target=entity as BaseEntity;
            if(target==null||target.IsDestroyed)return false;
            foreach(var preview in _previews.Values) {
                if(preview==null)continue;
                foreach(var managed in preview.FreeEntities.Values)
                    if(managed!=null&&!managed.IsDestroyed&&ReferenceEquals(managed,target))return true;
            }
            foreach(var preview in _installedPreviews.Values) {
                if(preview==null)continue;
                foreach(var managed in preview.FreeEntities.Values)
                    if(managed!=null&&!managed.IsDestroyed&&ReferenceEquals(managed,target))return true;
            }
            return false;
        }
        private bool IsKmbSwimRoot(BaseNetworkable entity) {
            var target=entity as BaseEntity;
            if(target==null||target.IsDestroyed)return false;
            foreach(var swim in _installedSwimmers.Values)
                if(swim!=null&&swim.MotionRoot!=null&&!swim.MotionRoot.IsDestroyed&&ReferenceEquals(swim.MotionRoot,target))return true;
            return false;
        }
        private object OnEntityTakeDamage(BaseCombatEntity entity,HitInfo info) {
            if(entity==null||info==null||(!IsKmbManagedFreeEntity(entity)&&!IsKmbSwimRoot(entity)))return null;
            try { if(info.damageTypes!=null)info.damageTypes.ScaleAll(0f); } catch {}
            return true;
        }
        // KMB MiniRoot is deliberately kept engine-off/kinematic, so PlayerHelicopter's default
        // homing validation can return false. Explicitly allow only our active swim roots.
        private object CanBeHomingTargeted(PlayerHelicopter playerHelicopter) {
            if(playerHelicopter!=null&&IsKmbSwimRoot(playerHelicopter))return true;
            return null;
        }

        private void RecordNativeLifetimeEvent(string detail) {
            if(_nativeLifetimeEvents.Count>=12)_nativeLifetimeEvents.Dequeue();
            _nativeLifetimeEvents.Enqueue(detail);
            if(_config!=null&&_config.NativeLifetimeDiagnostics)PrintWarning(detail);
        }
        private void TrackNativeGib(HelicopterDebris gib,string owner,string partId) {
            if(gib==null||gib.IsDestroyed)return;
            _nativeWatches[gib]=new NativeWatch{Owner=owner,PartId=partId,CreatedAt=Time.realtimeSinceStartup};
            // Some versions schedule a disposal callback AFTER spawn hooks return.
            // Cancel once again on the following server tick without creating extra pieces.
            NextTick(()=>{
                NativeWatch watch;
                if(gib!=null&&!gib.IsDestroyed&&_nativeWatches.TryGetValue(gib,out watch))
                    ProtectStaticNativeGib(gib);
            });
        }
        private void StartNativeKeepAlive() {
            if(_nativeKeepAliveTimer!=null){_nativeKeepAliveTimer.Destroy();_nativeKeepAliveTimer=null;}
            _nativeKeepAliveTimer=timer.Every(_config.NativeKeepAliveSeconds,KeepTrackedNativeGibs);
        }
        private void KeepTrackedNativeGibs() {
            foreach(var entry in _nativeWatches.ToArray()) {
                var gib=entry.Key;
                if(gib==null||gib.IsDestroyed) {
                    _nativeWatches.Remove(entry.Key);
                    // If OnEntityKill didn't fire, report that fact instead of inventing a cause.
                    RecordNativeLifetimeEvent("[KMB NativeLifetime] MISSING "+entry.Value.Owner+"/"+entry.Value.PartId+
                        " after "+Mathf.Max(0f,Time.realtimeSinceStartup-entry.Value.CreatedAt).ToString("0.0",CultureInfo.InvariantCulture)+
                        "s (entity handle destroyed; no OnEntityKill received)");
                    continue;
                }
                ProtectStaticNativeGib(gib);
            }
        }
        private void ShowNativeLifetimeStatus(BasePlayer user) {
            int workshop=0,installed=0,missing=0;
            Preview workshopPreview;
            if(_previews.TryGetValue(user.userID,out workshopPreview)) {
                foreach(var part in B(user).FreeParts.Values) {
                    if(part==null||!IsNativePartKey(part.PrefabKey))continue;
                    BaseEntity e;
                    if(workshopPreview.FreeEntities.TryGetValue(part.Id,out e)&&e!=null&&!e.IsDestroyed)workshop++;
                    else missing++;
                }
            }
            foreach(var entry in OwnedInstalledModels(user)) {
                Preview p;
                if(!_installedPreviews.TryGetValue(entry.Key,out p))continue;
                foreach(var part in entry.Value.Blueprint.FreeParts.Values) {
                    if(part==null||!IsNativePartKey(part.PrefabKey))continue;
                    BaseEntity e;
                    if(p.FreeEntities.TryGetValue(part.Id,out e)&&e!=null&&!e.IsDestroyed)installed++;
                    else missing++;
                }
            }
            Say(user,"Native診断: 制作中="+workshop+" / 永久設置="+installed+" / 欠損="+missing+
                " / 監視合計="+_nativeWatches.Count+" / 保護周期="+_config.NativeKeepAliveSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒");
            foreach(var line in _nativeLifetimeEvents.Reverse().Take(4))Say(user,line);
            if(_nativeLifetimeEvents.Count==0)Say(user,"削除イベント未検出。見た目だけ消える場合はクライアントの描画/ネットワークも確認してね。");
        }
        private void OnPlayerDisconnected(BasePlayer user,string reason) {
            if(user==null)return;
            // Original cleanup hook: this already removes the user's preview/seat and therefore
            // clears any pilot runtime associated with that preview. Keep this as the single
            // OnPlayerDisconnected definition to avoid duplicate-hook compile errors.
            StopMotionTest(user.userID,true);
            StopAutoPunchRuntime(user.userID);
            StopPunchTest(user.userID,true,false);
            RemovePreview(user.userID);
            _editors.Remove(user.userID);
        }
        private void SaveData() {
            try { Interface.Oxide.DataFileSystem.WriteObject(DataFile,_data); }
            catch(Exception ex) { PrintError("Blueprint save failed: "+ex.Message); }
        }
        private bool Allowed(BasePlayer user) { return user != null && user.IsConnected && (user.IsAdmin || permission.UserHasPermission(user.UserIDString,PermUse)); }
        private Editor E(BasePlayer user) {
            Editor e;
            if(!_editors.TryGetValue(user.userID,out e)) {e=new Editor();_editors[user.userID]=e;}
            return e;
        }
        private Blueprint B(BasePlayer user) {
            Blueprint b;
            if(!_data.Builders.TryGetValue(user.UserIDString,out b) || b==null) {
                b=DefaultBlueprint();_data.Builders[user.UserIDString]=b;
            }
            if(b.Slots==null)b.Slots=new Dictionary<string,Slot>();
            if(b.FreeParts==null)b.FreeParts=new Dictionary<string,FreePart>();
            foreach(var fp in b.FreeParts.Values) {
                if(fp==null)continue;
                if(fp.Position==null)fp.Position=new Vec();
                if(fp.Rotation==null)fp.Rotation=new Vec();
                if(fp.VisualPosition==null)fp.VisualPosition=new Vec();
                if(fp.VisualRotation==null)fp.VisualRotation=new Vec();
                if(float.IsNaN(fp.Scale)||float.IsInfinity(fp.Scale)||fp.Scale<=0)fp.Scale=1f;
                fp.Scale=Mathf.Clamp(fp.Scale,0.20f,4f);
                if(fp.ParentId==null)fp.ParentId="";
            }
            foreach(var id in Ids) {
                if(!b.Slots.ContainsKey(id)||b.Slots[id]==null) b.Slots[id]=DefaultSlot(id);
                var slot=b.Slots[id];
                if(slot.Position==null)slot.Position=new Vec();
                if(slot.Rotation==null)slot.Rotation=new Vec();
                if(slot.VisualPosition==null)slot.VisualPosition=new Vec();
                if(slot.VisualRotation==null)slot.VisualRotation=new Vec();
                if(slot.VisualScale<=0f||float.IsNaN(slot.VisualScale)||float.IsInfinity(slot.VisualScale))slot.VisualScale=1f;
                slot.VisualScale=Mathf.Clamp(slot.VisualScale,0.20f,4.0f);
            }
            // v0.1.0/0.1.1 default HEAD used heli_cockpit, which is not a BaseEntity. Migrate it automatically.
            if(b.Slots.ContainsKey("HEAD") && string.Equals(b.Slots["HEAD"].PrefabKey,"helicockpit",StringComparison.OrdinalIgnoreCase)) {
                b.Slots["HEAD"].PrefabKey="turret"; b.Slots["HEAD"].SourceKind=SourceForKey("turret"); b.Slots["HEAD"].ShapeId="";
            }
            // v0.1.2 used generic sign keys for the feet. Upgrade only that default to the dedicated foot preset.
            if(b.Version < 2) {
                foreach(var footId in new[]{"R_FOOT","L_FOOT"}) {
                    var foot=b.Slots[footId];
                    if(foot!=null && string.Equals(foot.PrefabKey,"sign",StringComparison.OrdinalIgnoreCase)) {
                        foot.PrefabKey="footplate"; foot.SourceKind="FootPreset"; foot.Rotation=new Vec(90f,0f,0f);
                    }
                }
                b.Version=2;
            }
            if(b.Version < 3) b.Version=3;
            if(b.Version < 4) b.Version=4;
            if(b.Version < 5) b.Version=5;
            if(b.StandRotations==null)b.StandRotations=new Dictionary<string,Vec>();
            if(b.StandPose==null)b.StandPose=new PoseState();
            if(b.StandPose.Slots==null)b.StandPose.Slots=new Dictionary<string,SlotState>();
            if(b.RightWindupShoulderState==null)b.RightWindupShoulderState=new SlotState();
            if(b.RightWindupElbowState==null)b.RightWindupElbowState=new SlotState();
            if(b.LeftWindupShoulderState==null)b.LeftWindupShoulderState=new SlotState();
            if(b.LeftWindupElbowState==null)b.LeftWindupElbowState=new SlotState();
            if(b.RightPunchShoulderState==null)b.RightPunchShoulderState=new SlotState();
            if(b.RightPunchElbowState==null)b.RightPunchElbowState=new SlotState();
            if(b.LeftPunchShoulderState==null)b.LeftPunchShoulderState=new SlotState();
            if(b.LeftPunchElbowState==null)b.LeftPunchElbowState=new SlotState();
            if(string.IsNullOrEmpty(b.AutoPunchMode))b.AutoPunchMode="alternate";
            b.AutoPunchMode=b.AutoPunchMode.ToLowerInvariant();
            if(b.AutoPunchMode!="right"&&b.AutoPunchMode!="left"&&b.AutoPunchMode!="alternate")b.AutoPunchMode="alternate";
            if(float.IsNaN(b.AutoPunchIntervalSeconds)||float.IsInfinity(b.AutoPunchIntervalSeconds)||b.AutoPunchIntervalSeconds<=0f)b.AutoPunchIntervalSeconds=3f;
            b.AutoPunchIntervalSeconds=Mathf.Clamp(b.AutoPunchIntervalSeconds,1.8f,30f);
            if(b.PilotSeatPosition==null)b.PilotSeatPosition=new Vec(0f,0.35f,-0.15f);
            if(b.PilotSeatRotation==null)b.PilotSeatRotation=new Vec();
            if(b.Version < 13) {
                // v0.2.4 migration: if the user already tuned only the right punch, create the missing left side once.
                bool mirrored=MirrorPunchRegistration(b,true,false);
                b.Version=13;
                if(mirrored)NextTick(SaveData);
            }
            if(b.Version < 14) {
                // v0.2.5 changes the mirror math to stand-relative deltas. Existing left records are
                // not overwritten automatically; use /kmb punchmirror right once to rebuild them safely.
                b.Version=14;
                NextTick(SaveData);
            }
            if(b.Version < 15) {
                // v0.3.0 adds the per-blueprint pilot chair transform; v0.3.1 smooths runtime parenting. Existing body/punch tuning is untouched.
                if(b.PilotSeatPosition==null)b.PilotSeatPosition=new Vec(0f,0.35f,-0.15f);
                if(b.PilotSeatRotation==null)b.PilotSeatRotation=new Vec();
                b.Version=15;
                NextTick(SaveData);
            }
            if(b.Version < 16) {
                // v0.3.3 keeps the seat position but removes legacy torso/punch yaw from the
                // cockpit when mech-forward mode is enabled.
                if(b.PilotSeatRotation==null)b.PilotSeatRotation=new Vec();
                if(_config.PilotSeatFaceMechForward)b.PilotSeatRotation.Y=0f;
                b.Version=16;
                NextTick(SaveData);
            }
            return b;
        }
        private static Slot DefaultSlot(string id) {
            var s=new Slot();s.Id=id;
            switch(id) {
                case "WAIST": s.Position=new Vec(0,3.2f,0);s.PrefabKey="cockpit";break;
                case "TORSO": s.Position=new Vec(0,1.2f,0);s.PrefabKey="armoredcockpit";break;
                case "HEAD": s.Position=new Vec(0,1.2f,0);s.PrefabKey="turret";break;
                case "R_UPPER_ARM":s.Position=new Vec(1.3f,0.7f,0);s.PrefabKey="snowmobile";break;
                case "L_UPPER_ARM":s.Position=new Vec(-1.3f,0.7f,0);s.PrefabKey="snowmobile";break;
                case "R_FOREARM":s.Position=new Vec(0,-1.05f,0);s.PrefabKey="post";break;
                case "L_FOREARM":s.Position=new Vec(0,-1.05f,0);s.PrefabKey="post";break;
                case "R_THIGH":s.Position=new Vec(0.65f,-0.5f,0);s.PrefabKey="gib018";s.SourceKind="NativeGibCandidate";s.ShapeId="ScrapHeliGibs_Part018";break;
                case "L_THIGH":s.Position=new Vec(-0.65f,-0.5f,0);s.PrefabKey="gib018";s.SourceKind="NativeGibCandidate";s.ShapeId="ScrapHeliGibs_Part018";break;
                case "R_SHIN":case "L_SHIN":s.Position=new Vec(0,-1.35f,0);s.PrefabKey="gib018";s.SourceKind="NativeGibCandidate";s.ShapeId="ScrapHeliGibs_Part018";break;
                case "R_FOOT":case "L_FOOT":s.Position=new Vec(0,-1.15f,0.28f);s.Rotation=new Vec(90,0,0);s.PrefabKey="footplate";break;
            }
            return s;
        }
        private static Blueprint DefaultBlueprint() {
            var b=new Blueprint();foreach(var id in Ids)b.Slots[id]=DefaultSlot(id);return b;
        }
        private static string Parent(string id) {
            switch(id) {
                case "WAIST":return "";
                case "TORSO":case "R_THIGH":case "L_THIGH":return "WAIST";
                case "HEAD":case "R_UPPER_ARM":case "L_UPPER_ARM":return "TORSO";
                case "R_FOREARM":return "R_UPPER_ARM";
                case "L_FOREARM":return "L_UPPER_ARM";
                case "R_SHIN":return "R_THIGH";
                case "L_SHIN":return "L_THIGH";
                case "R_FOOT":return "R_SHIN";
                case "L_FOOT":return "L_SHIN";
            }
            return "";
        }
        // Virtual kinematic hierarchy. Child joints are attached to the PARENT VISUAL pose, not the
        // parent's hidden joint frame. This keeps elbows/knees attached when a vehicle visual is offset.
        // A slot's Position/Rotation is its proximal joint; VisualPosition/VisualRotation places the mesh
        // around that joint. Moving a pivot can therefore be compensated without moving the visible part.
        private void SlotJointPose(Blueprint b,string id,Vector3 root,Quaternion yawRoot,out Vector3 position,out Quaternion rotation) {
            SlotJointPoseInternal(b,id,root,yawRoot,0,out position,out rotation);
        }
        private void SlotJointPoseInternal(Blueprint b,string id,Vector3 root,Quaternion yawRoot,int depth,out Vector3 position,out Quaternion rotation) {
            if(depth>16 || b==null || b.Slots==null || string.IsNullOrEmpty(id) || !b.Slots.ContainsKey(id)) {position=root;rotation=yawRoot;return;}
            string parent=Parent(id);
            Vector3 basePos; Quaternion baseRot;
            if(string.IsNullOrEmpty(parent)) {basePos=root;baseRot=yawRoot;}
            else SlotPoseInternal(b,parent,root,yawRoot,depth+1,out basePos,out baseRot);
            var slot=b.Slots[id];
            position=basePos + baseRot*slot.Position.V();
            Quaternion localRot=Quaternion.Euler(slot.Rotation.X,slot.Rotation.Y,slot.Rotation.Z);
            rotation=baseRot*localRot;
            // TORSO is the upper-body root. WaistYaw must always rotate around the mech's upright
            // axis, not the WAIST prefab's local Y axis (vehicle prefabs can have unusual local axes).
            // Keeping the joint position fixed while rotating this frame makes torso/head/both arms
            // orbit naturally around the waist pivot without tilting or corkscrewing.
            if(string.Equals(id,"TORSO",StringComparison.OrdinalIgnoreCase) && Mathf.Abs(b.WaistYaw)>0.0001f) {
                Vector3 upright=(yawRoot*Vector3.up).normalized;
                rotation=Quaternion.AngleAxis(b.WaistYaw,upright)*rotation;
            }
        }
        private void SlotPose(Blueprint b,string id,Vector3 root,Quaternion yawRoot,out Vector3 position,out Quaternion rotation) {
            SlotPoseInternal(b,id,root,yawRoot,0,out position,out rotation);
        }
        private void SlotPoseInternal(Blueprint b,string id,Vector3 root,Quaternion yawRoot,int depth,out Vector3 position,out Quaternion rotation) {
            SlotJointPoseInternal(b,id,root,yawRoot,depth+1,out position,out rotation);
            if(b==null || b.Slots==null || !b.Slots.ContainsKey(id))return;
            var slot=b.Slots[id];
            position+=rotation*slot.VisualPosition.V();
            rotation=rotation*Quaternion.Euler(slot.VisualRotation.X,slot.VisualRotation.Y,slot.VisualRotation.Z);
        }
        private void ParentPoseForChild(Blueprint b,string childId,Vector3 root,Quaternion yawRoot,out Vector3 position,out Quaternion rotation) {
            string parent=Parent(childId);
            if(string.IsNullOrEmpty(parent)) {position=root;rotation=yawRoot;return;}
            SlotPose(b,parent,root,yawRoot,out position,out rotation);
        }
        private static string MirrorId(string id) {
            if(string.IsNullOrEmpty(id))return "";
            if(id.StartsWith("R_",StringComparison.OrdinalIgnoreCase))return "L_"+id.Substring(2);
            if(id.StartsWith("L_",StringComparison.OrdinalIgnoreCase))return "R_"+id.Substring(2);
            return "";
        }
        private static Vec MirrorPosition(Vec v) { return v==null?new Vec():new Vec(-v.X,v.Y,v.Z); }
        private static Vec MirrorEuler(Vec v) {
            if(v==null)return new Vec();
            return new Vec(Mathf.Repeat(v.X,360f),Mathf.Repeat(-v.Y,360f),Mathf.Repeat(-v.Z,360f));
        }
        private void CopySideMirror(Blueprint b,bool rightToLeft) {
            if(b==null||b.Slots==null)return;
            string from=rightToLeft?"R_":"L_"; string to=rightToLeft?"L_":"R_";
            foreach(var suffix in new[]{"UPPER_ARM","FOREARM","THIGH","SHIN","FOOT"}) {
                string srcId=from+suffix,dstId=to+suffix;
                Slot src,dst; if(!b.Slots.TryGetValue(srcId,out src)||src==null||!b.Slots.TryGetValue(dstId,out dst)||dst==null)continue;
                dst.Position=MirrorPosition(src.Position);
                dst.Rotation=MirrorEuler(src.Rotation);
                dst.VisualPosition=MirrorPosition(src.VisualPosition);
                dst.VisualRotation=MirrorEuler(src.VisualRotation);
                dst.VisualScale=Mathf.Clamp(src.VisualScale,0.20f,4.0f);
                dst.PrefabKey=src.PrefabKey; dst.SourceKind=src.SourceKind; dst.ShapeId=src.ShapeId;
            }
        }
        private static SlotState MirrorSlotState(SlotState src) {
            if(src==null)return new SlotState();
            return new SlotState {
                Position=MirrorPosition(src.Position),
                Rotation=MirrorEuler(src.Rotation),
                VisualPosition=MirrorPosition(src.VisualPosition),
                VisualRotation=MirrorEuler(src.VisualRotation),
                VisualScale=Mathf.Clamp(src.VisualScale<=0f?1f:src.VisualScale,0.20f,4.0f)
            };
        }
        private static Vector3 ReflectLocalX(Vector3 v) { return new Vector3(-v.x,v.y,v.z); }
        private static Quaternion MirrorQuaternionLocalX(Quaternion q) {
            Vector3 forward=ReflectLocalX(q*Vector3.forward);
            Vector3 up=ReflectLocalX(q*Vector3.up);
            if(forward.sqrMagnitude<0.000001f||up.sqrMagnitude<0.000001f)return Quaternion.identity;
            return Quaternion.LookRotation(forward.normalized,up.normalized);
        }
        private static Vec AddVec(Vec a,Vector3 delta) {
            Vector3 av=a!=null?a.V():Vector3.zero;
            return Vec.From(av+delta);
        }
        private static Vec MirrorRotationDelta(Vec sourceBase,Vec sourceTarget,Vec targetBase) {
            Quaternion qs=Quaternion.Euler(sourceBase!=null?sourceBase.V():Vector3.zero);
            Quaternion qt=Quaternion.Euler(sourceTarget!=null?sourceTarget.V():Vector3.zero);
            Quaternion qb=Quaternion.Euler(targetBase!=null?targetBase.V():Vector3.zero);
            Quaternion delta=Quaternion.Inverse(qs)*qt;
            Quaternion mirroredDelta=MirrorQuaternionLocalX(delta);
            return Vec.From((qb*mirroredDelta).eulerAngles);
        }
        private SlotState StandStateForMirror(Blueprint b,string id) {
            SlotState st=null;
            if(b!=null&&b.StandPose!=null&&b.StandPose.Slots!=null)b.StandPose.Slots.TryGetValue(id,out st);
            if(st!=null)return st.Copy();
            Slot slot;
            if(b!=null&&b.Slots!=null&&b.Slots.TryGetValue(id,out slot)&&slot!=null)return CaptureSlotState(slot);
            return new SlotState();
        }
        // Mirror only the movement DELTA from the saved stand pose. This preserves the user's
        // independently tuned left/right prefab placement while making the animation symmetric.
        private SlotState MirrorPunchStateRelativeToStand(Blueprint b,SlotState sourceTarget,string sourceId,string targetId) {
            if(sourceTarget==null)return new SlotState();
            SlotState sourceBase=StandStateForMirror(b,sourceId);
            SlotState targetBase=StandStateForMirror(b,targetId);
            SlotState result=targetBase.Copy();

            Vector3 sourcePos=sourceTarget.Position!=null?sourceTarget.Position.V():Vector3.zero;
            Vector3 sourceBasePos=sourceBase.Position!=null?sourceBase.Position.V():Vector3.zero;
            result.Position=AddVec(targetBase.Position,ReflectLocalX(sourcePos-sourceBasePos));

            Vector3 sourceVisual=sourceTarget.VisualPosition!=null?sourceTarget.VisualPosition.V():Vector3.zero;
            Vector3 sourceBaseVisual=sourceBase.VisualPosition!=null?sourceBase.VisualPosition.V():Vector3.zero;
            result.VisualPosition=AddVec(targetBase.VisualPosition,ReflectLocalX(sourceVisual-sourceBaseVisual));

            result.Rotation=MirrorRotationDelta(sourceBase.Rotation,sourceTarget.Rotation,targetBase.Rotation);
            result.VisualRotation=MirrorRotationDelta(sourceBase.VisualRotation,sourceTarget.VisualRotation,targetBase.VisualRotation);

            float sb=Mathf.Max(0.001f,sourceBase.VisualScale<=0f?1f:sourceBase.VisualScale);
            float ratio=(sourceTarget.VisualScale<=0f?sb:sourceTarget.VisualScale)/sb;
            float tb=targetBase.VisualScale<=0f?1f:targetBase.VisualScale;
            result.VisualScale=Mathf.Clamp(tb*ratio,0.20f,4.0f);
            return result;
        }
        private float MirrorWaistYawRelativeToStand(Blueprint b,float sourceYaw) {
            float stand=(b!=null&&b.HasStandPose&&b.StandPose!=null)?b.StandPose.WaistYaw:(b!=null?b.StandWaistYaw:0f);
            float delta=Mathf.DeltaAngle(stand,sourceYaw);
            return Mathf.Clamp(stand-delta,-_config.WaistYawLimit,_config.WaistYawLimit);
        }
        // Copies recorded punch animation DELTAS across the body centerline.
        // rightToLeft=true means the tuned right punch movement is applied to the current left stand pose.
        // Existing target-side records are preserved unless overwrite=true.
        private bool MirrorPunchRegistration(Blueprint b,bool rightToLeft,bool overwrite) {
            if(b==null)return false;
            bool changed=false;
            if(rightToLeft) {
                if(b.HasRightWindupPose && (overwrite||!b.HasLeftWindupPose)) {
                    b.LeftWindupShoulderState=MirrorPunchStateRelativeToStand(b,b.RightWindupShoulderState,"R_UPPER_ARM","L_UPPER_ARM");
                    b.LeftWindupElbowState=MirrorPunchStateRelativeToStand(b,b.RightWindupElbowState,"R_FOREARM","L_FOREARM");
                    b.LeftWindupWaistYaw=MirrorWaistYawRelativeToStand(b,b.RightWindupWaistYaw);
                    b.HasLeftWindupWaist=b.HasRightWindupWaist;
                    b.HasLeftWindupPose=true; changed=true;
                }
                if(b.HasRightPunchPose && (overwrite||!b.HasLeftPunchPose)) {
                    b.LeftPunchShoulderState=MirrorPunchStateRelativeToStand(b,b.RightPunchShoulderState,"R_UPPER_ARM","L_UPPER_ARM");
                    b.LeftPunchElbowState=MirrorPunchStateRelativeToStand(b,b.RightPunchElbowState,"R_FOREARM","L_FOREARM");
                    b.LeftPunchShoulder=b.LeftPunchShoulderState.Rotation!=null?b.LeftPunchShoulderState.Rotation.Copy():new Vec();
                    b.LeftPunchElbow=b.LeftPunchElbowState.Rotation!=null?b.LeftPunchElbowState.Rotation.Copy():new Vec();
                    b.LeftPunchWaistYaw=MirrorWaistYawRelativeToStand(b,b.RightPunchWaistYaw);
                    b.HasLeftPunchPose=true; changed=true;
                }
            } else {
                if(b.HasLeftWindupPose && (overwrite||!b.HasRightWindupPose)) {
                    b.RightWindupShoulderState=MirrorPunchStateRelativeToStand(b,b.LeftWindupShoulderState,"L_UPPER_ARM","R_UPPER_ARM");
                    b.RightWindupElbowState=MirrorPunchStateRelativeToStand(b,b.LeftWindupElbowState,"L_FOREARM","R_FOREARM");
                    b.RightWindupWaistYaw=MirrorWaistYawRelativeToStand(b,b.LeftWindupWaistYaw);
                    b.HasRightWindupWaist=b.HasLeftWindupWaist;
                    b.HasRightWindupPose=true; changed=true;
                }
                if(b.HasLeftPunchPose && (overwrite||!b.HasRightPunchPose)) {
                    b.RightPunchShoulderState=MirrorPunchStateRelativeToStand(b,b.LeftPunchShoulderState,"L_UPPER_ARM","R_UPPER_ARM");
                    b.RightPunchElbowState=MirrorPunchStateRelativeToStand(b,b.LeftPunchElbowState,"L_FOREARM","R_FOREARM");
                    b.RightPunchShoulder=b.RightPunchShoulderState.Rotation!=null?b.RightPunchShoulderState.Rotation.Copy():new Vec();
                    b.RightPunchElbow=b.RightPunchElbowState.Rotation!=null?b.RightPunchElbowState.Rotation.Copy():new Vec();
                    b.RightPunchWaistYaw=MirrorWaistYawRelativeToStand(b,b.LeftPunchWaistYaw);
                    b.HasRightPunchPose=true; changed=true;
                }
            }
            return changed;
        }
        private static void ApplyVisualScale(BaseEntity entity,float scale) {
            if(entity==null||entity.IsDestroyed)return;
            scale=Mathf.Clamp(scale,0.20f,4.0f);
            try {
                entity.transform.localScale=Vector3.one*scale;
                entity.TransformChanged();
            } catch {}
        }
        private void PreserveVisualWhileMovingJoint(Blueprint b,string id,Vector3 root,Quaternion rootRot,Vector3 newJointWorld) {
            if(b==null||!b.Slots.ContainsKey(id))return;
            Vector3 oldVisualPos;Quaternion oldVisualRot;SlotPose(b,id,root,rootRot,out oldVisualPos,out oldVisualRot);
            Vector3 parentPos;Quaternion parentRot;ParentPoseForChild(b,id,root,rootRot,out parentPos,out parentRot);
            Vector3 local=Quaternion.Inverse(parentRot)*(newJointWorld-parentPos);
            var slot=b.Slots[id];
            slot.Position=new Vec(ClampLocal(local.x),ClampLocal(local.y),ClampLocal(local.z));
            Vector3 newJointPos;Quaternion newJointRot;SlotJointPose(b,id,root,rootRot,out newJointPos,out newJointRot);
            Vector3 visualLocal=Quaternion.Inverse(newJointRot)*(oldVisualPos-newJointPos);
            Quaternion visualLocalRot=Quaternion.Inverse(newJointRot)*oldVisualRot;
            slot.VisualPosition=Vec.From(visualLocal);
            slot.VisualRotation=Vec.From(visualLocalRot.eulerAngles);
        }
        private void MirrorJointPivotPreserveVisual(Blueprint b,string sourceId,string mirrorId,Vector3 root,Quaternion rootRot) {
            if(b==null||string.IsNullOrEmpty(mirrorId)||!b.Slots.ContainsKey(sourceId)||!b.Slots.ContainsKey(mirrorId))return;
            var source=b.Slots[sourceId];
            Vector3 oldVisualPos;Quaternion oldVisualRot;SlotPose(b,mirrorId,root,rootRot,out oldVisualPos,out oldVisualRot);
            var mirror=b.Slots[mirrorId];
            mirror.Position=MirrorPosition(source.Position);
            Vector3 newJointPos;Quaternion newJointRot;SlotJointPose(b,mirrorId,root,rootRot,out newJointPos,out newJointRot);
            mirror.VisualPosition=Vec.From(Quaternion.Inverse(newJointRot)*(oldVisualPos-newJointPos));
            mirror.VisualRotation=Vec.From((Quaternion.Inverse(newJointRot)*oldVisualRot).eulerAngles);
        }
        private void ResetCatalog() {
            _catalog.Clear(); _catalogOrder.Clear(); _catalogCheck.Clear(); _mechBossImportedKeys.Clear(); _mechBossImportedCount=0;
            foreach(var pair in BuiltinCatalog) AddCatalogEntry(pair.Key,pair.Value,false);
        }
        private static string CleanCatalogKey(string value) {
            if(string.IsNullOrEmpty(value))return "mbpart";
            var chars=value.ToLowerInvariant().Select(c=>(char.IsLetterOrDigit(c)||c=='_')?c:'_').ToArray();
            string key=new string(chars).Trim('_');
            return string.IsNullOrEmpty(key)?"mbpart":key;
        }
        private void AddCatalogEntry(string requestedKey,string prefab,bool fromMechBoss) {
            if(string.IsNullOrEmpty(prefab) && !string.Equals(requestedKey,"none",StringComparison.OrdinalIgnoreCase) && !string.Equals(requestedKey,"gib018",StringComparison.OrdinalIgnoreCase)) return;
            string key=CleanCatalogKey(requestedKey);
            if(string.Equals(requestedKey,"none",StringComparison.OrdinalIgnoreCase))key="none";
            if(string.Equals(requestedKey,"gib018",StringComparison.OrdinalIgnoreCase))key="gib018";
            if(_catalog.ContainsKey(key)) {
                if(string.Equals(_catalog[key],prefab,StringComparison.OrdinalIgnoreCase)) {
                    if(fromMechBoss && _mechBossImportedKeys.Add(key))_mechBossImportedCount++;
                    return;
                }
                int suffix=2; string baseKey="mb_"+key;
                key=baseKey;
                while(_catalog.ContainsKey(key)) key=baseKey+"_"+(suffix++).ToString(CultureInfo.InvariantCulture);
            }
            _catalog[key]=prefab??"";
            _catalogOrder.Add(key);
            if(fromMechBoss && _mechBossImportedKeys.Add(key))_mechBossImportedCount++;
        }
        private string PrefabPath(string key) {
            string path; return !string.IsNullOrEmpty(key)&&_catalog.TryGetValue(key,out path)?path:"";
        }
        private string[] CatalogKeys() { return _catalogOrder.ToArray(); }
        private string SourceForKey(string key) {
            if(string.Equals(key,"gib018",StringComparison.OrdinalIgnoreCase))return "NativeGibCandidate";
            if(string.Equals(key,"scrapheli_debris",StringComparison.OrdinalIgnoreCase))return "ScrapHeliDebrisTestOnly";
            if(key!=null && key.StartsWith("foot",StringComparison.OrdinalIgnoreCase))return "FootPreset";
            if(key!=null && key.StartsWith("leg",StringComparison.OrdinalIgnoreCase))return "LegPreset";
            return _mechBossImportedKeys.Contains(key)?"MechBossPrefab":"PrefabCandidate";
        }
        private static string TokenString(JObject obj,string name) {
            if(obj==null)return ""; JToken token;
            return obj.TryGetValue(name,StringComparison.OrdinalIgnoreCase,out token)&&token!=null?token.ToString():"";
        }
        private void ImportConfigField(JObject cfg,string field,string key) {
            string path=TokenString(cfg,field); if(!string.IsNullOrEmpty(path))AddCatalogEntry(key,path,true);
        }
        private void SyncMechBossCatalog() {
            ResetCatalog();
            int before=_catalog.Count;
            try {
                string cfgPath=Path.Combine(Interface.Oxide.ConfigDirectory,"MechBossPrototype.json");
                JObject cfg=File.Exists(cfgPath)?JObject.Parse(File.ReadAllText(cfgPath)):null;
                if(cfg!=null) {
                    // Preserve familiar aliases and also import *all* build prefab fields from the
                    // active MechBoss config. The on-disk config is not modified.
                    foreach(var prop in cfg.Properties()) {
                        if(!prop.Name.StartsWith("Build",StringComparison.OrdinalIgnoreCase) ||
                           !prop.Name.EndsWith("Prefab",StringComparison.OrdinalIgnoreCase) ||
                           prop.Value==null || prop.Value.Type!=JTokenType.String)continue;
                        string path=(string)prop.Value;
                        if(!string.IsNullOrEmpty(path))AddCatalogEntry("mb_"+prop.Name,path,true);
                    }
                    ImportConfigField(cfg,"BuildSmallSignPrefab","sign");
                    ImportConfigField(cfg,"BuildSignPostPrefab","post");
                    ImportConfigField(cfg,"BuildSimpleLightPrefab","light");
                    ImportConfigField(cfg,"BuildCeilingLightPrefab","ceiling");
                    ImportConfigField(cfg,"BuildSirenLightPrefab","siren");
                    ImportConfigField(cfg,"BuildSpikeTrapPrefab","spike");
                    ImportConfigField(cfg,"BuildSnowmobilePrefab","snowmobile");
                    ImportConfigField(cfg,"BuildTomahaSnowmobilePrefab","tomaha");
                    ImportConfigField(cfg,"BuildCamperPrefab","camper");
                    ImportConfigField(cfg,"BuildCockpitPrefab","cockpit");
                    ImportConfigField(cfg,"BuildArmoredCockpitPrefab","armoredcockpit");
                    ImportConfigField(cfg,"BuildCamperBbqPrefab","bbq");
                    ImportConfigField(cfg,"BuildCamperLockerPrefab","locker");
                    ImportConfigField(cfg,"Phase3SupportBotBodyPrefab","supportbot");
                    ImportConfigField(cfg,"Phase3SupportBotTurretVisualPrefab","turret");
                    ImportConfigField(cfg,"CockpitSeatPrefab","chair");
                    ImportConfigField(cfg,"MainHullPrefab","mainhull");
                    foreach(string partName in new[]{"Head","Body","LeftArm","RightArm","BrokenLeftArm","BrokenRightArm"}) {
                        JToken part; if(!cfg.TryGetValue(partName,StringComparison.OrdinalIgnoreCase,out part)||!(part is JObject))continue;
                        string path=TokenString((JObject)part,"Prefab");
                        if(!string.IsNullOrEmpty(path))AddCatalogEntry("mb_"+partName,path,true);
                    }
                }
            } catch(Exception ex) { PrintWarning("MechBoss config sync: "+ex.Message); }
            try {
                JObject data=Interface.Oxide.DataFileSystem.ReadObject<JObject>("MechBossPrototype_Data");
                JToken templates;
                if(data!=null && data.TryGetValue("Templates",StringComparison.OrdinalIgnoreCase,out templates) && templates is JArray) {
                    int n=0;
                    foreach(var template in (JArray)templates) {
                        if(!(template is JObject))continue; JToken extras;
                        if(!((JObject)template).TryGetValue("ExtraParts",StringComparison.OrdinalIgnoreCase,out extras)||!(extras is JArray))continue;
                        foreach(var extra in (JArray)extras) {
                            if(!(extra is JObject))continue;
                            string path=TokenString((JObject)extra,"Prefab"); if(string.IsNullOrEmpty(path))continue;
                            string raw=TokenString((JObject)extra,"Key"); if(string.IsNullOrEmpty(raw))raw="saved"+(++n).ToString(CultureInfo.InvariantCulture);
                            AddCatalogEntry("mb_"+raw,path,true);
                        }
                    }
                }
            } catch(Exception ex) { PrintWarning("MechBoss data sync: "+ex.Message); }
            ImportKnownNativeMeshes();
            _mechBossSyncStatus="MechBoss同期 "+_mechBossImportedCount+"件 / 総候補 "+_catalog.Count+"件"+(MechBossPrototype!=null?"（稼働中）":"");
            Puts(_mechBossSyncStatus);
        }
        // Read ONLY mesh identifiers observed by KuroPrefabLab. No change to its saved data.
        // Individual ScrapHeliGibs_Part### objects are collider identifiers, not prefab paths.
        private void ImportKnownNativeMeshes() {
            _nativeMeshes.Clear();_nativePartIds.Clear();
            RegisterNativeMesh(NativeMeshPrefix+"018"); // keep support for old saved blueprints
            // Use the current live KuroPrefabLab registry if it exists; otherwise use a
            // minimal standalone snapshot supplied with KuroMechBuilder. Never edit KPL data.
            foreach(string file in new[]{"KuroPrefabLab_Parts","KuroMechBuilder_NativeParts"}) {
                try {
                    var stored=Interface.Oxide.DataFileSystem.ReadObject<JObject>(file);
                    var entries=stored==null?null:stored["Items"] as JArray;
                    if(entries==null||entries.Count==0)continue;
                    int imported=0;
                    foreach(var token in entries) {
                        var item=token as JObject;if(item==null)continue;
                        string prefab=(string)item["Prefab"];
                        if(!string.IsNullOrEmpty(prefab)&&!string.Equals(prefab,NativeScrapPrefab,StringComparison.OrdinalIgnoreCase))continue;
                        string raw=(string)item["ColliderMeshName"];
                        string key=NativeKeyFromMesh(raw);
                        if(string.IsNullOrEmpty(key))continue;
                        RegisterNativeMesh(raw);
                        int id;
                        if(!int.TryParse(TokenString(item,"Id"),out id)||id<1||id>10000)continue;
                        _nativePartIds[id]=key;imported++;
                    }
                    if(imported>0){Puts("KMB: 読込 "+file+" = "+_nativePartIds.Count+" 件（形状 "+_nativeMeshes.Count+" 種）");break;}
                } catch(Exception ex) {PrintWarning("Gib登録表 "+file+" 読込失敗: "+ex.Message);}
            }
        }
        private static string NativeKeyFromMesh(string raw) {
            if(string.IsNullOrEmpty(raw))return "";
            int start=raw.IndexOf(NativeMeshPrefix,StringComparison.OrdinalIgnoreCase);
            if(start<0)return "";
            int i=start+NativeMeshPrefix.Length;int end=i;
            while(end<raw.Length&&char.IsDigit(raw[end])&&end-i<4)end++;
            int index;
            if(end==i||!int.TryParse(raw.Substring(i,end-i),out index)||index<0||index>999)return "";
            return "sg_"+index.ToString("D3",CultureInfo.InvariantCulture);
        }
        private string SpawnRegisteredGib(BasePlayer user,int recordedId) {
            string key;
            if(!_nativePartIds.TryGetValue(recordedId,out key))
                return "登録番号 "+recordedId+" が見つかりません。/kmb gib refresh で再読込してね";
            if(!B(user).FreeOnly)return "先に /kmb free で自由制作を開始してね";
            return NativeAddFree(user,key);
        }
        private void RegisterNativeMesh(string raw) {
            if(string.IsNullOrEmpty(raw))return;
            int start=raw.IndexOf(NativeMeshPrefix,StringComparison.OrdinalIgnoreCase);
            if(start<0)return;
            int i=start+NativeMeshPrefix.Length;int end=i;
            while(end<raw.Length&&char.IsDigit(raw[end])&&end-i<4)end++;
            if(end==i)return;
            int index;
            if(!int.TryParse(raw.Substring(i,end-i),out index)||index<0||index>999)return;
            string mesh=NativeMeshPrefix+index.ToString("D3",CultureInfo.InvariantCulture);
            string key="sg_"+index.ToString("D3",CultureInfo.InvariantCulture);
            if(_nativeMeshes.ContainsKey(key))return;
            _nativeMeshes[key]=mesh;
            if(!_catalog.ContainsKey(key)) {_catalog[key]="";_catalogOrder.Add(key);}
        }
        private static bool IsNativePartKey(string key) {
            return !string.IsNullOrEmpty(key)&&key.StartsWith("sg_",StringComparison.OrdinalIgnoreCase);
        }
        private static string MeshKeyOfGib(HelicopterDebris gib) {
            if(gib==null||gib.IsDestroyed)return "";
            var col=gib.GetComponent<MeshCollider>();
            if(col==null)col=gib.GetComponentInChildren<MeshCollider>(true);
            if(col==null||col.sharedMesh==null)return "";
            string name=col.sharedMesh.name??"";
            int start=name.IndexOf(NativeMeshPrefix,StringComparison.OrdinalIgnoreCase);
            if(start<0)return "";
            int i=start+NativeMeshPrefix.Length;int end=i;
            while(end<name.Length&&char.IsDigit(name[end])&&end-i<4)end++;
            if(end==i)return "";
            int id;if(!int.TryParse(name.Substring(i,end-i),out id))return "";
            return NativeMeshPrefix+id.ToString("D3",CultureInfo.InvariantCulture);
        }
        private static Type FindNativeGibType() {
            var t=typeof(HelicopterDebris).Assembly.GetType("ServerGib",false);
            if(t!=null)return t;
            foreach(var a in AppDomain.CurrentDomain.GetAssemblies()) {
                try{t=a.GetType("ServerGib",false);if(t!=null)return t;}catch{}
            }
            return null;
        }
        private static MethodInfo NativeCreateMethod(Type type) {
            if(type==null)return null;
            foreach(var m in type.GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic)) {
                if(m.Name!="CreateGibs")continue;
                var args=m.GetParameters();
                if(args.Length==5 && args[0].ParameterType==typeof(string) &&
                    args[1].ParameterType==typeof(GameObject) && args[2].ParameterType==typeof(GameObject) &&
                    args[3].ParameterType==typeof(Vector3) && args[4].ParameterType==typeof(float))return m;
            }
            return null;
        }
        private static object NativeMember(object obj,string name) {
            if(obj==null)return null;
            for(Type t=obj.GetType();t!=null;t=t.BaseType) {
                var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly;
                var f=t.GetField(name,flags);if(f!=null)return f.GetValue(obj);
                var p=t.GetProperty(name,flags);
                if(p!=null && p.GetIndexParameters().Length==0)return p.GetValue(obj,null);
            }
            return null;
        }
        private static GameObject NativeGibSource(Type type) {
            if(type==null||GameManager.server==null)return null;
            try {
                var prefab=GameManager.server.FindPrefab(NativeScrapPrefab);
                if(prefab==null)return null;
                var component=prefab.GetComponent(type)??prefab.GetComponentInChildren(type,true);
                return NativeMember(component,"_gibSource") as GameObject;
            } catch {return null;}
        }
        private static HelicopterDebris AsNativeGib(object obj) {
            var direct=obj as HelicopterDebris;if(direct!=null)return direct;
            var c=obj as Component;
            if(c!=null)return c.GetComponent<HelicopterDebris>()??c.GetComponentInParent<HelicopterDebris>();
            var go=obj as GameObject;return go==null?null:go.GetComponent<HelicopterDebris>();
        }
        private BaseEntity FindSafeStationaryScrapHeli(BasePlayer user,out string reason) {
            reason="";
            var near=new List<BaseEntity>();Vis.Entities(user.transform.position,35f,near);
            var heli=near.Where(e=>e!=null&&!e.IsDestroyed && e.GetType().Name=="ScrapTransportHelicopter")
                .OrderBy(e=>Vector3.Distance(user.transform.position,e.transform.position)).FirstOrDefault();
            if(heli==null){reason="35m以内にスクラップ輸送ヘリがありません";return null;}
            var distance=Vector3.Distance(user.transform.position,heli.transform.position);
            if(distance<8f){reason="ヘリから8m以上離れて操作してね";return null;}
            foreach(var other in BasePlayer.activePlayerList)
                if(other!=null&&other.IsConnected&&other.userID!=user.userID&&
                    Vector3.Distance(other.transform.position,heli.transform.position)<45f) {
                    reason="ヘリの45m以内にほかのプレイヤーがいます";return null;
                }
            var rb=heli.GetComponent<Rigidbody>();
            if(rb!=null&&rb.velocity.sqrMagnitude>0.25f){reason="スクラップヘリが動いています";return null;}
            return heli;
        }
        private void GibDiag(BasePlayer user) {
            var t=FindNativeGibType();
            string status="ServerGib="+(t!=null?"あり":"なし")+
                " / CreateGibs5="+(NativeCreateMethod(t)!=null?"あり":"なし")+
                " / _gibSource="+(NativeGibSource(t)!=null?"あり":"なし")+
                " / KPL登録ID="+_nativePartIds.Count+" / 登録形状="+_nativeMeshes.Count+
                " / config.EnableNativeScrapGibs="+_config.EnableNativeScrapGibs+
                " / 連続配置="+_config.NativeContinuousPlacement+
                " / ヘリ無し実験="+_config.ExperimentalNativeWithoutHeli+
                " / 最大保持=無制限"+
                " / ヘリ無し生成間隔="+_config.NativeNoHeliCooldownSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒";
            Say(user,"Native診断: "+status);
        }
        private void GibArm(BasePlayer user) {
            if(!_config.EnableNativeScrapGibs){Say(user,"configのEnableNativeScrapGibsをtrueにしてから、テスト環境で試してね");return;}
            if(_config.NativeContinuousPlacement) {
                Say(user,_config.ExperimentalNativeWithoutHeli
                    ?"Native連続配置有効。ヘリ無しの生成間隔="+_config.NativeNoHeliCooldownSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒 / 個数上限なし。テスト環境専用。"
                    :"Native連続配置は制作中ずっと有効です。再許可は不要。停止中のスクラップ輸送ヘリ1機を8〜35m以内に置いてね（生成ごとに出し直す必要なし）。ほかのプレイヤーは45m外で操作してね");
                return;
            }
            E(user).NativeGibArmedUntil=Time.realtimeSinceStartup+45f;
            Say(user,"旧式Native抽出を45秒許可しました");
        }
        // One explicit experimental extraction per arm. Creates a full native batch then removes
        // every unused fragment in the same call. Never deletes source heli or natural debris.
        private string NativePlaceFree(BasePlayer user,Blueprint b,Preview p,FreePart part) {
            if(!_config.EnableNativeScrapGibs)return "Native抽出は初期OFF。configでEnableNativeScrapGibs=trueが必要";
            if(!_config.NativeContinuousPlacement && Time.realtimeSinceStartup>E(user).NativeGibArmedUntil)
                return "旧式Native許可が切れました。/kmb gib arm を実行してね";
            if(_nativeCapturing||Time.realtimeSinceStartup<_nativeNextAttempt)
                return "Native抽出の連続処理を抑制中。約"+Mathf.CeilToInt(Mathf.Max(0f,_nativeNextAttempt-Time.realtimeSinceStartup))+"秒後にもう一度選んでね";
            if(p==null)return "自由制作プレビューなし";
            BaseEntity oldNative;
            if(p.FreeEntities.TryGetValue(part.Id,out oldNative)) {
                if(oldNative!=null&&!oldNative.IsDestroyed)return "既に生成済み";
                p.FreeEntities.Remove(part.Id); // destroyed handles must not block a safe repair
            }
            // Unlimited test: do not cap the number of held Native parts.
            string expected;
            if(!_nativeMeshes.TryGetValue(part.PrefabKey,out expected))return "未登録Native形状: "+part.PrefabKey;
            // Normally require a stationary, nearby real helicopter. Experimental mode is strictly opt-in.
            string safety="";
            var heli=_config.ExperimentalNativeWithoutHeli ? null : FindSafeStationaryScrapHeli(user,out safety);
            if(!_config.ExperimentalNativeWithoutHeli && heli==null)return safety;
            if(_config.ExperimentalNativeWithoutHeli) {
                // Enforce tighter resource limits for a synthetic source and a clear, isolated workshop.
                foreach(var other in BasePlayer.activePlayerList) {
                    if(other!=null&&other.IsConnected&&other.userID!=user.userID&&
                        Vector3.Distance(other.transform.position,p.RootPosition)<45f)
                        return "作業場の45m以内に別プレイヤーがいます";
                }
            }
            var type=FindNativeGibType();var method=NativeCreateMethod(type);var source=NativeGibSource(type);
            if(method==null||source==null)return "現在のRustに互換性のあるServerGib APIがありません。/kmb gib diag で確認してね";
            if(!_config.NativeContinuousPlacement)E(user).NativeGibArmedUntil=0f;
            // A native extraction briefly spawns a whole Gib batch; global throttling
            // protects the server even when multiple admins use this workshop.
            _nativeNextAttempt=Time.realtimeSinceStartup+(_config.ExperimentalNativeWithoutHeli
                ?_config.NativeNoHeliCooldownSeconds
                :(_config.NativeContinuousPlacement?_config.NativeContinuousCooldownSeconds:_config.NativeGibCooldownSeconds));
            var batch=new List<HelicopterDebris>();HelicopterDebris selected=null;bool keep=false;
            string result="";
            GameObject temporaryEmitter=null;
            try {
                // Experimental fallback: only a temporary Unity GameObject, not a spawned game entity.
                // ServerGib may require the real helicopter's components and throw. In that case,
                // all captured debris is removed below and the actual exception is logged.
                GameObject originObject=heli!=null?heli.gameObject:null;
                if(_config.ExperimentalNativeWithoutHeli) {
                    temporaryEmitter=new GameObject("KMB_TemporaryScrapGibEmitter");
                    temporaryEmitter.transform.position=p.RootPosition+Vector3.up*2f;
                    temporaryEmitter.transform.rotation=p.RootRotation;
                    originObject=temporaryEmitter;
                }
                _nativeBatchCaptured.Clear();_nativeCapturing=true;
                object created=null;
                try{created=method.Invoke(null,new object[]{NativeScrapPrefab,originObject,source,Vector3.zero,0f});}
                finally{_nativeCapturing=false;}
                var enumeration=created as IEnumerable;
                if(enumeration!=null)foreach(object obj in enumeration) {
                    var g=AsNativeGib(obj);if(g!=null&&!batch.Contains(g))batch.Add(g);
                }
                foreach(var g in _nativeBatchCaptured)if(g!=null&&!batch.Contains(g))batch.Add(g);
                if(batch.Count==0||batch.Count>80)result="生成数が異常 ("+batch.Count+")。全破片を撤去";
                else {
                    selected=batch.FirstOrDefault(g=>g!=null&&!g.IsDestroyed&&string.Equals(MeshKeyOfGib(g),expected,StringComparison.OrdinalIgnoreCase));
                    if(selected==null)result=expected+" の形状がバッチ内にありません";
                    else {
                        selected.enableSaving=false;FreezePhysics(selected);
                        ProtectStaticNativeGib(selected);
                        Vector3 at;Quaternion angle;FreePose(b,p,part.Id,out at,out angle);
                        selected.transform.SetPositionAndRotation(at,angle);selected.TransformChanged();
                        ApplyVisualScale(selected,part.Scale);selected.SendNetworkUpdateImmediate();
                        if(selected.IsDestroyed)result="生成したGibが消滅しました";
                        else {p.FreeEntities[part.Id]=selected;p.Results[part.Id]="Native抽出: "+expected;
                              part.NativeMesh=expected;TrackNativeGib(selected,"workshop:"+user.userID,part.Id);keep=true;result=part.Id+" = "+expected+" を一時生成（描画は要目視確認）";}
                    }
                }
            } catch(Exception ex) {
                var inner=ex is TargetInvocationException&&ex.InnerException!=null?ex.InnerException:ex;
                result="Native生成失敗: "+inner.GetType().Name;
                PrintWarning("KMB Native: "+inner.Message);
            } finally {
                _nativeCapturing=false;
                if(temporaryEmitter!=null) {
                    try{UnityEngine.Object.Destroy(temporaryEmitter);}catch(Exception ex){PrintWarning("Native emitter cleanup: "+ex.Message);}
                }
                foreach(var g in _nativeBatchCaptured)if(g!=null&&!batch.Contains(g))batch.Add(g);
                int removed=0;
                foreach(var g in batch)if(g!=null&&!g.IsDestroyed&&(!keep||g!=selected)) {
                    try{g.Kill();removed++;}catch(Exception ex){PrintWarning("Native cleanup: "+ex.Message);}
                }
                _nativeBatchCaptured.Clear();
                Puts("[Native] Batch="+batch.Count+" / removed="+removed+" / kept="+(keep?1:0));
            }
            return result;
        }
        private string NativeAddFree(BasePlayer user,string key) {
            var b=B(user);var ed=E(user);
            if(!b.FreeOnly)return "まず /kmb free で自由組み立てを開始してね";
            Preview preview;if(!EnsureFreeWorkshop(user,false)||!_previews.TryGetValue(user.userID,out preview))
                return "作業場を作れません: "+E(user).Message+"（/kmb free workshop で再試行）";
            string id;
            do{b.FreeSerial++;id="P"+b.FreeSerial.ToString("D3",CultureInfo.InvariantCulture);}while(b.FreeParts.ContainsKey(id));
            var part=new FreePart{Id=id,Name=key,PrefabKey=key,
                Position=new Vec(0.65f*((b.FreeParts.Count%5)-2),2f,1.4f+0.55f*(b.FreeParts.Count/5))};
            b.FreeParts[id]=part;
            string outcome=NativePlaceFree(user,b,preview,part);
            if(!preview.FreeEntities.ContainsKey(id)) {b.FreeParts.Remove(id);return outcome;}
            ed.FreeSelected=id;ed.FreePartPage=Math.Max(0,(b.FreeParts.Count-1)/9);
            SaveData();return outcome;
        }
        private void NativeRestoreFree(BasePlayer user,string id) {
            var b=B(user);Preview p;FreePart part;
            if(!b.FreeOnly||!_previews.TryGetValue(user.userID,out p)||!b.FreeParts.TryGetValue(id,out part)||
                part==null||!IsNativePartKey(part.PrefabKey)){Say(user,"復元対象のNativeパーツIDを指定してね");return;}
            string outcome=NativePlaceFree(user,b,p,part);
            if(p.FreeEntities.ContainsKey(id))SaveData();
            Say(user,outcome);
        }
        private void HandleGibChat(BasePlayer user,string[] args) {
            string action=args.Length==0?"help":args[0].ToLowerInvariant();
            if(action=="diag"){GibDiag(user);return;}
            if(action=="arm"){GibArm(user);return;}
            if(action=="noheli") {
                if(args.Length<2 || (args[1]!="on" && args[1]!="off")) {
                    Say(user,"ヘリ無し実験="+(_config.ExperimentalNativeWithoutHeli?"ON":"OFF")+"。テスト用: /kmb gib noheli on|off");
                    return;
                }
                _config.ExperimentalNativeWithoutHeli=args[1]=="on";
                Config.WriteObject(_config,true);
                Say(user,"ヘリ無し実験を"+(_config.ExperimentalNativeWithoutHeli?"ON":"OFF")+"にしました（未実機検証。テスト環境専用）。");
                return;
            }
            if(action=="continuous") {
                if(args.Length<2||!(args[1].Equals("on",StringComparison.OrdinalIgnoreCase)||args[1].Equals("off",StringComparison.OrdinalIgnoreCase))) {
                    Say(user,"Native連続配置="+(_config.NativeContinuousPlacement?"ON":"OFF")+"。変更: /kmb gib continuous on|off");return;
                }
                _config.NativeContinuousPlacement=args[1].Equals("on",StringComparison.OrdinalIgnoreCase);
                Config.WriteObject(_config,true);
                Say(user,"Native連続配置を"+(_config.NativeContinuousPlacement?"ON":"OFF")+"に変更して保存しました（設定を有効化するにはEnableNativeScrapGibs=trueも必要）");
                return;
            }
            if(action=="delay") {
                if(args.Length<2) {
                    Say(user,"ヘリ無し抽出の最短間隔="+_config.NativeNoHeliCooldownSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒。変更: /kmb gib delay 5（3〜30秒）");return;
                }
                float seconds;
                if(!float.TryParse(args[1],NumberStyles.Float,CultureInfo.InvariantCulture,out seconds) ||
                   float.IsNaN(seconds)||float.IsInfinity(seconds)||seconds<3f||seconds>30f) {
                    Say(user,"3〜30秒の数字を入力してね。例: /kmb gib delay 5");return;
                }
                _config.NativeNoHeliCooldownSeconds=seconds;
                Config.WriteObject(_config,true);
                Say(user,"ヘリ無し抽出の間隔="+seconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒に変更（現在の待ち時間には次回から反映）");return;
            }
            if(action=="limit") {
                Say(user,"v0.4.13ではNative保持数に上限はありません。/kmb gib limit の設定は不要です（生成間隔は維持）。");return;
            }
            if(action=="list") {
                Say(user,"KPLから読込済みのNative形状 "+_nativeMeshes.Count+"件。Part018は標準登録。通常は停止中ヘリが必要です。ヘリ無しは実験モード（初期OFF）。");
                foreach(var x in _nativeMeshes.OrderBy(k=>k.Key).Take(35))Say(user,x.Key+" = "+x.Value);
                return;
            }
            if(action=="add"&&args.Length>1) {
                int number;if(!int.TryParse(args[1],out number)){Say(user,"登録番号は整数で入力してね。例: /kmb gib add 24");return;}
                Say(user,SpawnRegisteredGib(user,number));return;
            }
            if(action=="ids") {
                Say(user,"登録済みGib番号 "+_nativePartIds.Count+"件。/kmb free → /kmb gib add 番号（連続配置は自動許可）");
                foreach(var id in _nativePartIds.Keys.OrderBy(x=>x).Take(70))Say(user,"GIB "+id+" ("+_nativePartIds[id]+")");return;
            }
            if(action=="restore"&&args.Length>1){NativeRestoreFree(user,args[1]);return;}
            if(action=="refresh"){SyncMechBossCatalog();Say(user,"Gib登録ID: "+_nativePartIds.Count+"件 / 形状: "+_nativeMeshes.Count+"種類");return;}
            Say(user,"/kmb gib diag | continuous on|off | noheli on|off（実験用） | delay 5 | ids | add 24 | refresh | restore P001");
        }
        private bool IsVehicle(string key) {
            if(string.IsNullOrEmpty(key))return false;
            string lower=key.ToLowerInvariant(); string path=PrefabPath(key).ToLowerInvariant();
            return lower.Contains("snowmobile")||lower.Contains("tomaha")||lower.Contains("bike")||lower.Contains("cockpit")||lower.Contains("camper")||lower.Contains("vehicle")||lower.Contains("mainhull")||lower=="bradley"
                || path.Contains("/vehicles/")||path.Contains("/module_entities/")||path.Contains("bradleyapc.prefab");
        }
        private bool CanSpawn(string key,out string reason) {
            reason="";
            if(key=="none") {reason="未設定（空スロット）";return false;}
            if(key=="gib018") {reason="ScrapHeliGibs_Part018は個別モデル識別子。BaseEntity用のPrefabパス未確認";return false;}
            if(key=="scrapheli_debris" && !_config.AllowScrapHeliDebrisPreview) {
                reason="残骸Entityは検証用。configのAllowScrapHeliDebrisPreviewをONにした場合だけ試験可能";return false;
            }
            string prefabPath=PrefabPath(key);
            if(string.IsNullOrEmpty(prefabPath)) {reason="Prefab未登録";return false;}
            if(key=="bradley"||key=="mainhull"||prefabPath.IndexOf("bradleyapc.prefab",StringComparison.OrdinalIgnoreCase)>=0) {reason="Bradley本体:プレビュー禁止（AI暴走防止）";return false;}
            if(key=="helicockpit"||prefabPath.EndsWith("/heli_cockpit.prefab",StringComparison.OrdinalIgnoreCase)) {reason="heli_cockpitはBaseEntityではないため生成不可";return false;}
            if(prefabPath.IndexOf("/solarpanel.large.deployed.prefab",StringComparison.OrdinalIgnoreCase)>=0
                || prefabPath.IndexOf("/storageadaptor.deployed.prefab",StringComparison.OrdinalIgnoreCase)>=0
                || prefabPath.IndexOf("/industrialcombiner.deployed.prefab",StringComparison.OrdinalIgnoreCase)>=0) {reason="MechBoss側でも安全のため無効化されているIO装飾";return false;}
            if(IsVehicle(key)&&!_config.AllowVehiclePreview&&!_mechBossImportedKeys.Contains(key)) {reason="車両系:設定で禁止（MechBoss登録Prefabは例外許可）";return false;}
            // FindPrefab only confirms an asset exists; it does NOT mean CreateEntity can spawn it.
            // Many decor and vehicle MODULE prefabs are plain GameObjects, without BaseEntity.
            // Calling CreateEntity on them writes the reported "isn't an entity" warning.
            if (GameManager.server == null) {reason="GameManagerが未準備";return false;}
            GameObject prefab = null;
            try { prefab = GameManager.server.FindPrefab(prefabPath); }
            catch(Exception ex) { reason="Prefab検索失敗: "+ex.GetType().Name;return false; }
            if (prefab == null) {reason="サーバーPrefabなし";return false;}
            if (prefab.GetComponent<BaseEntity>() == null) {
                reason="静的素材（BaseEntityなし）: CreateEntity不可";
                return false;
            }
            if(key=="scrapheli_debris" && prefab.GetComponent<HelicopterDebris>() == null) {
                reason="対象にHelicopterDebrisコンポーネントなし";return false;
            }
            return true;
        }
        private bool SafeArea(BasePlayer user,Vector3 origin,out string error,bool freeBuild=false) {
            error="";
            // Only the admin free editor can bypass workshop-only terrain and building exclusions.
            // Classic preview safety stays unchanged. Normal Rust building permission is untouched.
            if(!(freeBuild && _config.FreeIgnoreBuildingArea)) {
                if (TerrainMeta.HeightMap != null) {
                    float h = TerrainMeta.HeightMap.GetHeight(origin);
                    if(Mathf.Abs(TerrainMeta.HeightMap.GetHeight(origin + Vector3.right*2f)-h)>1.5f ||
                       Mathf.Abs(TerrainMeta.HeightMap.GetHeight(origin + Vector3.forward*2f)-h)>1.5f) {
                        error="急斜面のため生成できません";return false;
                    }
                }
                var nearby = new List<BaseEntity>();
                Vis.Entities(origin, 10f, nearby);
                foreach(var thing in nearby) if(thing is BuildingBlock) {
                    error="建築物に近いため生成できません";return false;
                }
            }
            // Still avoid creating a whole workshop directly beside another connected player.
            foreach(var p in BasePlayer.activePlayerList)
                if(p!=null&&p.IsConnected&&p.userID!=user.userID&&Vector3.Distance(p.transform.position,origin)<_config.MinimumOtherPlayerDistance) {
                    error="近くに別プレイヤーがいるため生成しない";return false;
                }
            return true;
        }
        private void BuildPreview(BasePlayer user) {
            var b=B(user);var e=E(user);
            // Unlimited test: no global workshop-count cap.
            var forward=user.eyes.HeadForward();forward.y=0;
            if(forward.sqrMagnitude<0.01f)forward=Vector3.forward;
            forward.Normalize();
            var origin=user.transform.position+forward*_config.SpawnDistance;
            float terrainY=TerrainMeta.HeightMap!=null?TerrainMeta.HeightMap.GetHeight(origin):user.transform.position.y;
            // Place the free workshop at the editor's floor/altitude, not below a building floor.
            // Classic previews keep terrain-height positioning.
            origin.y=b.FreeOnly?Mathf.Max(terrainY,user.transform.position.y):terrainY;
            string safety;
            if(!SafeArea(user,origin,out safety,b.FreeOnly)){e.Message=safety;return;}
            Quaternion orientation=Quaternion.LookRotation(forward);
            // Cleanup only our own previous workshop, NEVER arbitrary nearby pieces.
            RemovePreview(user.userID);
            var next=new Preview {Owner=user.userID,RootPosition=origin,RootRotation=orientation,StartedAt=Time.realtimeSinceStartup};
            _previews[user.userID]=next;
            int success=0,missing=0,vehicleCount=0;
            if(!b.FreeOnly)foreach(var id in Ids) {
                string reason;
                var slot=b.Slots[id];
                bool real = CanSpawn(slot.PrefabKey,out reason);
                if(real&&IsVehicle(slot.PrefabKey)&&!_mechBossImportedKeys.Contains(slot.PrefabKey)&&vehicleCount>=_config.MaxVehiclePreviewParts){
                    real=false;reason="車両プレビュー同時数の安全上限";
                }
                if(slot.PrefabKey=="none"){next.Results[id]="空スロット";missing++;continue;}
                string chosenKey = slot.PrefabKey;
                string originalReason = reason;
                if(!real) {
                    next.Results[id]=reason;
                    // Native gib018 cannot be recreated by CreateEntity. For full-body layout work,
                    // show a real leg-shaped substitute instead of leaving thighs/shins empty.
                    string proxyReason;
                    if(string.Equals(slot.PrefabKey,"gib018",StringComparison.OrdinalIgnoreCase) && CanSpawn("leg_snowmobile",out proxyReason)) {
                        chosenKey="leg_snowmobile";
                    }
                    else if(_config.SpawnSafeProxyForMissingParts && CanSpawn("post",out proxyReason)) chosenKey="post";
                    else { missing++;continue; }
                }
                if(success>=_config.MaxEntitiesPerPreview) {next.Results[id]="部品上限";missing++;continue;}
                Vector3 pos;Quaternion rot;SlotPose(b,id,origin,orientation*Quaternion.Euler(0,b.RootYaw,0),out pos,out rot);
                BaseEntity entity=null;
                try {
                    entity=GameManager.server.CreateEntity(PrefabPath(chosenKey),pos,rot,true);
                    if(entity==null){next.Results[id]="CreateEntity失敗";missing++;continue;}
                    entity.enableSaving=false;
                    FreezePhysics(entity);
                    entity.Spawn();
                    if(entity.IsDestroyed) {next.Results[id]="Spawn後に消滅";missing++;continue;}
                    // Preview entities must not fall, drive or collide with other players.
                    FreezePhysics(entity);
                    ApplyVisualScale(entity,slot.VisualScale);
                    entity.SendNetworkUpdateImmediate();
                    next.Entities[id]=entity;
                    if(!real){
                        if(string.Equals(slot.PrefabKey,"gib018",StringComparison.OrdinalIgnoreCase) && string.Equals(chosenKey,"leg_snowmobile",StringComparison.OrdinalIgnoreCase))
                            next.Results[id]="脚代替(snowmobile) / 本来=gib018 / Native Gib直接連携待ち";
                        else next.Results[id]="仮部品("+chosenKey+") / 本来="+slot.PrefabKey+" / 理由="+originalReason;
                        missing++;
                    }
                    else next.Results[id]=(_mechBossImportedKeys.Contains(chosenKey)?"MechBoss登録Prefab":"試験生成")+"（描画は要目視）";
                    success++;
                    if(real&&IsVehicle(chosenKey))vehicleCount++;
                } catch(Exception ex) {
                    next.Results[id]="生成例外: "+ex.GetType().Name;missing++;
                    if(entity!=null&&!entity.IsDestroyed)try{entity.Kill();}catch{}
                    PrintWarning("Preview "+id+": "+ex.Message);
                }
            }
            if(!b.FreeOnly && _config.SpawnPilotSeat) SpawnPilotSeat(user,next,b);
            SpawnFreePreviewParts(user,next,b);
            // A two-hour timer used to delete ALL free-build parts while someone was editing.
            // Only classic temporary robot previews should expire automatically.
            if(!b.FreeOnly) {
                ulong owner=user.userID;
                next.Lifetime=timer.Once(_config.PreviewLifetimeSeconds,()=>{
                    Preview active;
                    if(_previews.TryGetValue(owner,out active)&&ReferenceEquals(active,next))RemovePreview(owner);
                });
            } else if(_config.KeepFreeWorkshopAlive) {
                ulong owner=user.userID;
                next.FreeMaintenance=timer.Every(_config.FreeRepairIntervalSeconds,()=>{
                    Preview active;
                    if(!_previews.TryGetValue(owner,out active)||!ReferenceEquals(active,next))return;
                    var admin=BasePlayer.FindByID(owner);
                    if(admin!=null&&admin.IsConnected)MaintainFreeWorkshop(admin,active);
                });
            }
            // Debug markers expire on the client; refresh only for the owning administrator.
            if(_config.ShowPlaceholdersForMissingParts && !b.FreeOnly) {
                ulong owner=user.userID;
                next.Markers=timer.Every(4f,()=>{
                    Preview active;
                    if(!_previews.TryGetValue(owner,out active)||!ReferenceEquals(active,next))return;
                    var admin=BasePlayer.FindByID(owner);
                    if(admin!=null&&admin.IsConnected)DrawPlaceholders(admin,active,B(admin));
                });
            }
            // Direct-edit ddraw markers are transient client drawings. Refresh them independently from
            // player input so they do not disappear while the admin stands still.
            {
                ulong owner=user.userID;
                next.EditMarkers=timer.Every(0.70f,()=>{
                    Preview active;Editor ed;
                    if(!_previews.TryGetValue(owner,out active)||!ReferenceEquals(active,next))return;
                    if(!_editors.TryGetValue(owner,out ed)||!ed.DirectEdit||!ed.MarkersVisible)return;
                    var admin=BasePlayer.FindByID(owner);
                    if(admin!=null&&admin.IsConnected)DrawDirectMarkers(admin);
                });
            }
            e.Message="一括生成: "+success+"個（うち仮部品/欠品 "+missing+"）、未対応素材は代用表示";
            DrawPlaceholders(user,next,b);
            if(b.AutoPunchEnabled) StartAutoPunchRuntime(user,false);
        }
        private Quaternion PilotRootFrame(Blueprint b,Preview p) {
            if(p==null)return Quaternion.identity;
            return p.RootRotation*Quaternion.Euler(0f,b!=null?b.RootYaw:0f,0f);
        }
        private void PilotSeatWorldPose(Blueprint b,Preview p,out Vector3 position,out Quaternion rotation) {
            if(b==null||p==null){position=Vector3.zero;rotation=Quaternion.identity;return;}
            Quaternion rootFrame=PilotRootFrame(b,p);
            Vector3 torsoPos;Quaternion torsoRot;
            SlotPose(b,"TORSO",p.RootPosition,rootFrame,out torsoPos,out torsoRot);
            Vec lp=b.PilotSeatPosition??new Vec(0f,0.35f,-0.15f);
            Vec lr=b.PilotSeatRotation??new Vec();
            position=torsoPos+torsoRot*lp.V();

            // Keep cockpit yaw on chassis/root direction instead of following animated upper body.
            if(_config.PilotSeatFaceMechForward)
                rotation=rootFrame*Quaternion.Euler(lr.X,_config.PilotSeatForwardYawOffset,lr.Z);
            else
                rotation=torsoRot*Quaternion.Euler(lr.X,lr.Y,lr.Z);
        }
        private void StabilizePilotSeat(BaseEntity entity) {
            if(entity==null||entity.IsDestroyed)return;
            foreach(var rb in entity.GetComponentsInChildren<Rigidbody>(true)) {
                if(rb==null)continue;
                if(!rb.isKinematic){rb.velocity=Vector3.zero;rb.angularVelocity=Vector3.zero;}
                rb.useGravity=false;rb.isKinematic=true;rb.detectCollisions=true;
            }
            var mount=entity as BaseMountable;
            if(mount!=null){mount.enabled=true;mount.isMobile=true;}
        }
        private BaseEntity PilotAnchor(Preview p) {
            if(p==null)return null;
            BaseEntity anchor;
            if(p.Entities.TryGetValue("WAIST",out anchor)&&anchor!=null&&!anchor.IsDestroyed)return anchor;
            return null;
        }
        private void ParentEntityPreserveWorld(BaseEntity child,BaseEntity parent) {
            if(child==null||child.IsDestroyed||parent==null||parent.IsDestroyed||ReferenceEquals(child,parent))return;
            Vector3 wp=child.transform.position;Quaternion wr=child.transform.rotation;
            child.SetParent(parent,0);
            child.transform.position=wp;child.transform.rotation=wr;
            child.TransformChanged();
            child.SendNetworkUpdateImmediate();
        }
        private void UnparentEntityPreserveWorld(BaseEntity child) {
            if(child==null||child.IsDestroyed)return;
            Vector3 wp=child.transform.position;Quaternion wr=child.transform.rotation;
            child.SetParent(null,0);
            child.transform.position=wp;child.transform.rotation=wr;
            child.TransformChanged();
            child.SendNetworkUpdateImmediate();
        }
        private void EnablePilotHierarchy(Preview p,Blueprint b) {
            if(p==null||b==null||p.PilotHierarchyActive)return;
            BaseEntity anchor=PilotAnchor(p);
            if(anchor==null)return;
            p.PilotMotionAnchor=anchor;
            // Parent every visual to the lower-body WAIST anchor while piloting. This lets one
            // network transform move the whole machine instead of teleporting 13 entities separately.
            foreach(var kv in p.Entities) {
                if(kv.Value==null||kv.Value.IsDestroyed||ReferenceEquals(kv.Value,anchor))continue;
                ParentEntityPreserveWorld(kv.Value,anchor);
            }
            p.PilotHierarchyActive=true;
            UpdatePilotSeatPose(p,b,true);
        }
        private void DisablePilotHierarchy(Preview p,Blueprint b) {
            if(p==null||!p.PilotHierarchyActive)return;
            foreach(var kv in p.Entities) {
                if(kv.Value==null||kv.Value.IsDestroyed||ReferenceEquals(kv.Value,p.PilotMotionAnchor))continue;
                UnparentEntityPreserveWorld(kv.Value);
            }
            p.PilotHierarchyActive=false;
            p.PilotMotionAnchor=null;
            var owner=BasePlayer.FindByID(p.Owner);
            if(owner!=null&&owner.IsConnected)UpdatePreview(owner,false);
            UpdatePilotSeatPose(p,b,true);
        }
        private void AttachSeatToPilotAnchor(Preview p,Blueprint b,bool forceNetwork) {
            if(p==null||b==null||p.PilotSeat==null||p.PilotSeat.IsDestroyed)return;
            BaseEntity anchor=PilotAnchor(p);
            Vector3 pos;Quaternion rot;PilotSeatWorldPose(b,p,out pos,out rot);
            try {
                if(anchor!=null&&!anchor.IsDestroyed) {
                    if(p.PilotSeat.GetParentEntity()!=anchor)p.PilotSeat.SetParent(anchor,0);
                    p.PilotSeat.transform.localPosition=anchor.transform.InverseTransformPoint(pos);
                    p.PilotSeat.transform.localRotation=Quaternion.Inverse(anchor.transform.rotation)*rot;
                } else {
                    p.PilotSeat.transform.SetPositionAndRotation(pos,rot);
                }
                StabilizePilotSeat(p.PilotSeat);
                p.PilotSeat.TransformChanged();
                if(forceNetwork)p.PilotSeat.SendNetworkUpdateImmediate(); else p.PilotSeat.SendNetworkUpdate();
            } catch(Exception ex){PrintWarning("Pilot seat parent/pose: "+ex.Message);}
        }
        private void SpawnPilotSeat(BasePlayer user,Preview p,Blueprint b) {
            if(user==null||p==null||b==null||string.IsNullOrEmpty(_config.PilotSeatPrefab))return;
            if(p.PilotSeat!=null&&!p.PilotSeat.IsDestroyed)return;
            Vector3 pos;Quaternion rot;PilotSeatWorldPose(b,p,out pos,out rot);
            BaseEntity entity=null;
            try {
                entity=GameManager.server.CreateEntity(_config.PilotSeatPrefab,pos,rot,true);
                if(entity==null){p.Results["PILOT_SEAT"]="CreateEntity失敗";return;}
                entity.enableSaving=false;
                StabilizePilotSeat(entity);
                entity.Spawn();
                var seat=entity as BaseMountable;
                if(seat==null||entity.IsDestroyed){if(!entity.IsDestroyed)entity.Kill();p.Results["PILOT_SEAT"]="BaseMountableではない";return;}
                StabilizePilotSeat(entity);
                p.PilotSeat=seat;
                AttachSeatToPilotAnchor(p,b,true);
                p.Results["PILOT_SEAT"]="操縦席生成済み（腰アンカー追従）";
            } catch(Exception ex) {
                if(entity!=null&&!entity.IsDestroyed)try{entity.Kill();}catch{}
                p.Results["PILOT_SEAT"]="生成例外: "+ex.GetType().Name;
                PrintWarning("Pilot seat spawn: "+ex.Message);
            }
        }
        private void UpdatePilotSeatPose(Preview p,Blueprint b,bool forceNetwork=false) {
            if(p==null||b==null||p.PilotSeat==null||p.PilotSeat.IsDestroyed)return;
            // While mounted the seat is a child of the WAIST anchor and must NOT be teleported every
            // control tick. Only recalc its local pose when the seat setting/parent actually changes.
            BaseEntity anchor=PilotAnchor(p);
            if(anchor!=null&&p.PilotSeat.GetParentEntity()==anchor&&!forceNetwork)return;
            AttachSeatToPilotAnchor(p,b,forceNetwork);
        }
        private Preview FindPreviewBySeat(BaseMountable seat) {
            if(seat==null)return null;
            foreach(var p in _previews.Values)if(p!=null&&p.PilotSeat!=null&&!p.PilotSeat.IsDestroyed&&ReferenceEquals(p.PilotSeat,seat))return p;
            return null;
        }
        private bool IsActuallyMountedOnPilotSeat(BasePlayer player,Preview p) {
            if(player==null||p==null||p.PilotSeat==null||p.PilotSeat.IsDestroyed)return false;
            if(!player.isMounted)return false;
            BaseMountable mounted=null;
            try{mounted=player.GetMounted();}catch{}
            return mounted!=null&&ReferenceEquals(mounted,p.PilotSeat);
        }
        private void ClearPilotRuntime(Preview p,BasePlayer player,bool notify,bool restartAuto) {
            if(p==null)return;
            if(player!=null)StopPunchTest(player.userID,true,false);
            if(p.Pilot==player||player==null)p.Pilot=null;
            if(p.PilotHierarchyActive) {
                BasePlayer owner=BasePlayer.FindByID(p.Owner);
                if(owner!=null&&owner.IsConnected)DisablePilotHierarchy(p,B(owner));
                else {p.PilotHierarchyActive=false;p.PilotMotionAnchor=null;}
            }
            if(player!=null&&player.IsConnected) {
                if(notify)Say(player,"操縦モード OFF");
                if(restartAuto&&B(player).AutoPunchEnabled)StartAutoPunchRuntime(player,false);
            }
        }
        private bool TryGetPilotPreview(BasePlayer player,out Preview preview) {
            preview=null;if(player==null)return false;
            foreach(var p in _previews.Values) {
                if(p==null||p.Pilot!=player)continue;

                // Never trust only the cached Pilot reference. If the player is no longer
                // physically mounted on this exact KMB seat, clear stale pilot state immediately.
                if(!IsActuallyMountedOnPilotSeat(player,p)) {
                    ClearPilotRuntime(p,player,false,true);
                    return false;
                }

                preview=p;
                return true;
            }
            return false;
        }
        private object CanMountEntity(BasePlayer player,BaseMountable mountable) {
            Preview p=FindPreviewBySeat(mountable);
            if(p==null)return null;
            if(player==null||player.userID!=p.Owner)return false;
            return null;
        }
        private void OnEntityMounted(BaseMountable mountable,BasePlayer player) {
            Preview p=FindPreviewBySeat(mountable);
            if(p==null||player==null)return;
            if(player.userID!=p.Owner){NextTick(()=>{if(player!=null&&player.IsConnected)player.DismountObject();});return;}
            p.Pilot=player;p.LastPilotControlAt=Time.realtimeSinceStartup;
            EnablePilotHierarchy(p,B(player));
            StopAutoPunchRuntime(player.userID);
            Editor ed=E(player);
            if(ed.DirectEdit)SetDirectEdit(player,false);
            ed.UiOpen=false;CuiHelper.DestroyUi(player,UiRoot);CuiHelper.DestroyUi(player,DirectUiRoot);
            Say(player,"操縦モード ON：W/S 前後、A/D 旋回、Shift 加速、左クリック=左パンチ、右クリック=右パンチ。視点は自由、Spaceで降車");
        }
        private void OnEntityDismounted(BaseMountable mountable,BasePlayer player) {
            if(player==null)return;
            Preview p=FindPreviewBySeat(mountable);

            // Fallback: some mount/dismount sequences can report a seat that no longer resolves
            // through FindPreviewBySeat. The cached Pilot reference is enough to locate and clear it.
            if(p==null) {
                foreach(var candidate in _previews.Values) {
                    if(candidate!=null&&candidate.Pilot==player){p=candidate;break;}
                }
            }
            if(p==null)return;
            ClearPilotRuntime(p,player,true,true);
        }
        private void OnPlayerDeath(BasePlayer player,HitInfo info) {
            if(player==null)return;
            Preview pilotPreview=null;
            foreach(var p in _previews.Values) {
                if(p!=null&&p.Pilot==player){pilotPreview=p;break;}
            }

            // Death while mounted on a server-moved KMB seat can leave Rust with a stale mounted
            // transform for a frame. That can instantly add a large FlyHack violation on corpse/
            // death transition. Treat death exactly like an explicit safe dismount.
            try{player.PauseFlyHackDetection(5.0f);}catch{}
            try{
                if(player.isMounted)player.DismountObject();
            }catch(Exception ex){
                PrintWarning("Pilot death dismount: "+ex.Message);
            }

            if(pilotPreview!=null)ClearPilotRuntime(pilotPreview,player,false,false);

            // A second cleanup on the next tick catches mount/dismount RPC ordering where Rust
            // reports the death before the mountable has fully released the player.
            NextTick(()=>{
                if(player==null)return;
                try{player.PauseFlyHackDetection(5.0f);}catch{}
                foreach(var p in _previews.Values) {
                    if(p!=null&&p.Pilot==player)ClearPilotRuntime(p,player,false,false);
                }
                try{
                    if(player.isMounted)player.DismountObject();
                }catch{}
            });
        }

        private void OnPlayerRespawned(BasePlayer player) {
            if(player==null)return;
            // Prevent stale seat/corpse transform deltas from becoming a fresh FlyHack VL spike.
            try{player.PauseFlyHackDetection(3.0f);}catch{}
            foreach(var p in _previews.Values) {
                if(p!=null&&p.Pilot==player)ClearPilotRuntime(p,player,false,false);
            }
        }
        private bool CanPilotMoveTo(Preview p,Vector3 candidate,out Vector3 grounded) {
            grounded=candidate;
            if(p==null)return false;
            if(TerrainMeta.HeightMap!=null) {
                float h=TerrainMeta.HeightMap.GetHeight(candidate);
                if(Mathf.Abs(h-p.RootPosition.y)>_config.PilotMaxGroundStep)return false;
                grounded.y=h;
            }
            if(_config.PilotBuildingStopRadius>0f) {
                var nearby=new List<BaseEntity>();
                Vis.Entities(grounded,_config.PilotBuildingStopRadius,nearby);
                foreach(var ent in nearby)if(ent is BuildingBlock)return false;
            }
            return true;
        }
        private void UpdatePilotRootMotion(BasePlayer player,Preview p) {
            if(player==null||p==null)return;
            var b=B(player);
            if(p.PilotHierarchyActive) {
                BaseEntity anchor=PilotAnchor(p);
                if(anchor!=null&&!anchor.IsDestroyed) {
                    Vector3 pos;Quaternion rot;
                    SlotPose(b,"WAIST",p.RootPosition,p.RootRotation*Quaternion.Euler(0,b.RootYaw,0),out pos,out rot);
                    try {
                        anchor.transform.SetPositionAndRotation(pos,rot);
                        ApplyVisualScale(anchor,b.Slots["WAIST"].VisualScale);
                        anchor.TransformChanged();
                        anchor.SendNetworkUpdate();
                        return;
                    } catch(Exception ex){PrintWarning("Pilot root motion: "+ex.Message);}
                }
            }
            UpdatePreview(player,false);
        }
        private void HandlePilotInput(BasePlayer player,InputState input,Preview p) {
            if(player==null||input==null||p==null)return;

            // Explicit dismount. Some static/custom mountables do not reliably process the vanilla
            // jump-to-dismount path while server-side pilot input is being intercepted, so handle
            // Space before punch/movement input and exit immediately.
            if(input.WasJustPressed(BUTTON.JUMP)) {
                try {
                    StopPunchTest(player.userID,true,false);
                    player.PauseFlyHackDetection(1.0f);
                    player.DismountObject();
                    NextTick(()=>{
                        if(player==null)return;
                        if(p.Pilot==player&&!IsActuallyMountedOnPilotSeat(player,p))
                            ClearPilotRuntime(p,player,true,true);
                    });
                } catch(Exception ex) {
                    PrintWarning("Pilot Space dismount: "+ex.Message);
                    ClearPilotRuntime(p,player,true,true);
                }
                return;
            }

            // Punch playback uses the exact poses already tuned by the user.
            // Do not rotate/re-parent the seat during a punch: mounted look direction remains free.
            if(input.WasJustPressed(BUTTON.FIRE_PRIMARY)&&!_punchTests.ContainsKey(player.userID))StartPunchTest(player,false,false);
            if(input.WasJustPressed(BUTTON.FIRE_SECONDARY)&&!_punchTests.ContainsKey(player.userID))StartPunchTest(player,true,false);

            float now=Time.realtimeSinceStartup;
            float elapsed=now-p.LastPilotControlAt;
            if(elapsed<_config.PilotControlInterval)return;
            float dt=Mathf.Clamp(elapsed,0.02f,0.20f);p.LastPilotControlAt=now;
            float moveInput=0f,turnInput=0f;
            if(input.IsDown(BUTTON.FORWARD))moveInput+=1f;
            if(input.IsDown(BUTTON.BACKWARD))moveInput-=1f;
            if(input.IsDown(BUTTON.LEFT))turnInput-=1f;
            if(input.IsDown(BUTTON.RIGHT))turnInput+=1f;
            bool changed=false;
            if(Mathf.Abs(turnInput)>0.001f) {
                p.RootRotation=p.RootRotation*Quaternion.Euler(0f,turnInput*_config.PilotTurnSpeed*dt,0f);
                changed=true;
            }
            if(Mathf.Abs(moveInput)>0.001f) {
                float speed=moveInput>0f?_config.PilotMoveSpeed:_config.PilotReverseSpeed;
                if(moveInput>0f&&input.IsDown(BUTTON.SPRINT))speed*=_config.PilotSprintMultiplier;
                Vector3 forward=p.RootRotation*Vector3.forward;forward.y=0f;if(forward.sqrMagnitude>0.001f)forward.Normalize();
                Vector3 candidate=p.RootPosition+forward*(moveInput*speed*dt);Vector3 grounded;
                if(CanPilotMoveTo(p,candidate,out grounded)){p.RootPosition=grounded;changed=true;}
            }
            if(changed){
                try{player.PauseFlyHackDetection(0.5f);}catch{}
                UpdatePilotRootMotion(player,p);
            }
        }
        private void SetPilotSeatHere(BasePlayer user) {
            Preview p;if(user==null||!_previews.TryGetValue(user.userID,out p)){Say(user,"先に /kmb preview で機体を生成してね");return;}
            if(p.PilotHierarchyActive){Say(user,"操縦席から降りてから位置を変更してね");return;}
            var b=B(user);Quaternion rootFrame=PilotRootFrame(b,p);Vector3 torsoPos;Quaternion torsoRot;
            SlotPose(b,"TORSO",p.RootPosition,rootFrame,out torsoPos,out torsoRot);
            Vector3 world=user.transform.position+Vector3.up*0.15f;
            b.PilotSeatPosition=Vec.From(Quaternion.Inverse(torsoRot)*(world-torsoPos));
            if(b.PilotSeatRotation==null)b.PilotSeatRotation=new Vec();
            if(_config.PilotSeatFaceMechForward)b.PilotSeatRotation.Y=0f;
            else b.PilotSeatRotation=Vec.From((Quaternion.Inverse(torsoRot)*user.transform.rotation).eulerAngles);
            SaveData();UpdatePilotSeatPose(p,b,true);Say(user,"現在位置を操縦席位置として保存しました（向きはロボ正面基準）");
        }
        private void MountPilotSeat(BasePlayer user) {
            Preview p;if(user==null||!_previews.TryGetValue(user.userID,out p)){Say(user,"先に /kmb preview で機体を生成してね");return;}
            if(p.PilotSeat==null||p.PilotSeat.IsDestroyed)SpawnPilotSeat(user,p,B(user));
            if(p.PilotSeat==null||p.PilotSeat.IsDestroyed){Say(user,"操縦席を生成できませんでした。/kmb status を確認してね");return;}
            BasePlayer mounted=null;try{mounted=p.PilotSeat.GetMounted();}catch{}
            if(mounted!=null&&mounted!=user){Say(user,"操縦席は使用中です");return;}
            if(mounted==user){Say(user,"すでに操縦席に乗っています");return;}
            try{p.PilotSeat.MountPlayer(user);}catch(Exception ex){Say(user,"搭乗失敗: "+ex.GetType().Name);PrintWarning("Pilot mount: "+ex.Message);}
        }

        // Native HelicopterDebris schedules its own gameplay cleanup. The retained
        // decorative part must not inherit this timer while it is in our workshop.
        // Not a guarantee against every engine-level death or server restart.
        private void ProtectStaticNativeGib(HelicopterDebris gib) {
            if(gib==null||gib.IsDestroyed)return;
            try { gib.CancelInvoke(); }
            catch(Exception ex) { PrintWarning("KMB gib lifetime cancellation: "+ex.Message); }
            // Keep decorative debris out of Rust physics/damage interactions. This is repeated by
            // keep-alive because some native debris components can re-arm themselves after spawn.
            try { FreezePhysics(gib); }
            catch(Exception ex) { PrintWarning("KMB gib physics protection: "+ex.Message); }
        }

        // Every few seconds, check *existing IDs* rather than adding a new blueprint part.
        // Missing pieces are restored at most twice per part during this editor session.
        // This is intentionally separate from installed-model auto restoration and its queue.
        private void MaintainFreeWorkshop(BasePlayer user,Preview preview) {
            if(user==null||preview==null||!_config.KeepFreeWorkshopAlive)return;
            var bp=B(user);
            if(bp==null||!bp.FreeOnly)return;
            bool nativeAttempted=false;
            foreach(var part in bp.FreeParts.Values.OrderBy(x=>x!=null?x.Id:"")) {
                if(part==null)continue;
                BaseEntity active;
                if(preview.FreeEntities.TryGetValue(part.Id,out active)) {
                    if(active!=null&&!active.IsDestroyed) {
                        // Some Rust versions schedule debris removal after Spawn; clear it
                        // even if it was registered later than the first placement.
                        if(IsNativePartKey(part.PrefabKey))ProtectStaticNativeGib(active as HelicopterDebris);
                        continue;
                    }
                    preview.FreeEntities.Remove(part.Id);
                }
                int tries;
                preview.FreeRepairAttempts.TryGetValue(part.Id,out tries);
                if(tries>=2)continue; // Never create an unbounded spawn/repair cycle.
                if(IsNativePartKey(part.PrefabKey)) {
                    if(nativeAttempted||!_config.EnableNativeScrapGibs||
                        _nativeCapturing||Time.realtimeSinceStartup<_nativeNextAttempt)continue;
                    nativeAttempted=true;
                    preview.FreeRepairAttempts[part.Id]=tries+1;
                    string result=NativePlaceFree(user,bp,preview,part);
                    if(!preview.FreeEntities.ContainsKey(part.Id))
                        PrintWarning("KMB free Native repair "+part.Id+" ("+(tries+1)+"/2): "+result);
                    // One native CreateGibs batch maximum per maintenance pass.
                    break;
                }
                preview.FreeRepairAttempts[part.Id]=tries+1;
                SpawnFreePart(user,preview,bp,part);
            }
        }

        private void FreezePhysics(BaseEntity entity) {
            if(entity==null||entity.IsDestroyed)return;
            foreach(var rb in entity.GetComponentsInChildren<Rigidbody>(true)) {
                if(rb==null)continue;
                if(!rb.isKinematic){rb.velocity=Vector3.zero;rb.angularVelocity=Vector3.zero;}
                rb.useGravity=false; rb.isKinematic=true; rb.detectCollisions=false;
            }
            foreach(var col in entity.GetComponentsInChildren<Collider>(true)) if(col!=null)col.enabled=false;
            var mount=entity as BaseMountable; if(mount!=null)mount.enabled=false;
        }
        private void DrawPlaceholders(BasePlayer user,Preview p,Blueprint b) {
            if(!_config.ShowPlaceholdersForMissingParts || E(user).UiOpen)return;
            foreach(var id in Ids) {
                // Always label substituted pieces as proxies; never imply visual shape reproduction.
                if(p.Entities.ContainsKey(id) && (!p.Results.ContainsKey(id) || !p.Results[id].StartsWith("仮部品")))continue;
                Vector3 pos;Quaternion rot;SlotPose(b,id,p.RootPosition,p.RootRotation*Quaternion.Euler(0,b.RootYaw,0),out pos,out rot);
                try {
                    user.SendConsoleCommand("ddraw.sphere",4.2f,Color.yellow,pos,0.22f);
                    user.SendConsoleCommand("ddraw.text",4.2f,Color.white,pos+Vector3.up*0.28f,id+" (仮)");
                } catch{}
            }
        }
        private void UpdatePreview(BasePlayer user,bool drawDebug=true) {
            Preview p; if(!_previews.TryGetValue(user.userID,out p))return;
            var b=B(user);
            foreach(var id in Ids) {
                BaseEntity entity;
                if(!p.Entities.TryGetValue(id,out entity)||entity==null||entity.IsDestroyed)continue;
                Vector3 pos;Quaternion rot;SlotPose(b,id,p.RootPosition,p.RootRotation*Quaternion.Euler(0,b.RootYaw,0),out pos,out rot);
                try {
                    entity.transform.SetPositionAndRotation(pos,rot);
                    ApplyVisualScale(entity,b.Slots[id].VisualScale);
                    entity.SendNetworkUpdate();
                } catch(Exception ex){PrintWarning("Pose update "+id+": "+ex.Message);}
            }
            UpdatePilotSeatPose(p,b);
            UpdateFreePreview(user);
            if(drawDebug&&!b.FreeOnly)DrawPlaceholders(user,p,b);
        }
        private static bool IsDescendantOrSelf(string id,string rootId) {
            string k=id;
            for(int guard=0;guard<16&&!string.IsNullOrEmpty(k);guard++) {
                if(string.Equals(k,rootId,StringComparison.OrdinalIgnoreCase))return true;
                k=Parent(k);
            }
            return false;
        }
        private void UpdatePreviewBranch(BasePlayer user,string rootId,bool includeDescendants) {
            Preview p;if(!_previews.TryGetValue(user.userID,out p))return;
            var b=B(user);Quaternion rootRot=p.RootRotation*Quaternion.Euler(0,b.RootYaw,0);
            foreach(var id in Ids) {
                if(!string.Equals(id,rootId,StringComparison.OrdinalIgnoreCase) && (!includeDescendants||!IsDescendantOrSelf(id,rootId)))continue;
                BaseEntity entity;if(!p.Entities.TryGetValue(id,out entity)||entity==null||entity.IsDestroyed)continue;
                Vector3 pos;Quaternion rot;SlotPose(b,id,p.RootPosition,rootRot,out pos,out rot);
                try {entity.transform.SetPositionAndRotation(pos,rot);ApplyVisualScale(entity,b.Slots[id].VisualScale);entity.TransformChanged();entity.SendNetworkUpdateImmediate();}
                catch(Exception ex){PrintWarning("Direct pose update "+id+": "+ex.Message);}
            }
            UpdatePilotSeatPose(p,b);
        }
        private void RemovePreview(ulong owner) {
            StopAutoPunchRuntime(owner);
            StopMotionTest(owner,true);
            StopPunchTest(owner,true,false);
            Editor ed;if(_editors.TryGetValue(owner,out ed)){ed.Grabbed=false;ed.GrabId="";ed.ManipMode="MOVE";ed.FreeGrab=false;ClearManualCandidate(ed);}
            Preview p;
            if(!_previews.TryGetValue(owner,out p))return;
            _previews.Remove(owner);
            if(p.Lifetime!=null)p.Lifetime.Destroy();
            if(p.Markers!=null)p.Markers.Destroy();
            if(p.EditMarkers!=null)p.EditMarkers.Destroy();
            if(p.FreeMaintenance!=null)p.FreeMaintenance.Destroy();
            Timer fastFree;
            if(_fastFreeRestoreTimers.TryGetValue(owner,out fastFree)){
                if(fastFree!=null)fastFree.Destroy();
                _fastFreeRestoreTimers.Remove(owner);
            }
            if(p.MouthJointPreview!=null)p.MouthJointPreview.Destroy();
            p.MouthJointPreview=null;
            if(p.Pilot!=null&&p.Pilot.IsConnected)try{p.Pilot.DismountObject();}catch{}
            p.Pilot=null;
            if(p.PilotHierarchyActive){var own=BasePlayer.FindByID(owner);if(own!=null)DisablePilotHierarchy(p,B(own));else {p.PilotHierarchyActive=false;p.PilotMotionAnchor=null;}}
            if(p.PilotSeat!=null&&!p.PilotSeat.IsDestroyed)try{p.PilotSeat.Kill();}catch(Exception ex){PrintWarning("Seat cleanup error: "+ex.Message);}
            p.PilotSeat=null;
            foreach(var entity in p.Entities.Values)
                if(entity!=null&&!entity.IsDestroyed)try{entity.Kill();}catch(Exception ex){PrintWarning("Cleanup error: "+ex.Message);}
            foreach(var entity in p.FreeEntities.Values) {
                var gib=entity as HelicopterDebris; if(gib!=null)_nativeWatches.Remove(gib);
                if(entity!=null&&!entity.IsDestroyed)try{entity.Kill();}catch(Exception ex){PrintWarning("Free cleanup: "+ex.Message);}
            }
        }
        private static string JointLabel(string id) {
            switch(id) {
                case "TORSO":return "腰上"; case "HEAD":return "首";
                case "R_UPPER_ARM":return "右肩"; case "L_UPPER_ARM":return "左肩";
                case "R_FOREARM":return "右肘"; case "L_FOREARM":return "左肘";
                case "R_THIGH":return "右股"; case "L_THIGH":return "左股";
                case "R_SHIN":return "右膝"; case "L_SHIN":return "左膝";
                case "R_FOOT":return "右足首"; case "L_FOOT":return "左足首";
                default:return id;
            }
        }
        private static Vector3 ViewAngles(BasePlayer player) {
            if(player==null)return Vector3.zero;
            var ray=player.eyes.HeadRay();
            if(ray.direction.sqrMagnitude<0.0001f)return Vector3.zero;
            return Quaternion.LookRotation(ray.direction.normalized,Vector3.up).eulerAngles;
        }
        private static float PointDistanceFromRay(Ray ray,Vector3 point,out float along) {
            Vector3 dir=ray.direction.normalized;
            along=Vector3.Dot(point-ray.origin,dir);
            if(along<0f)return 9999f;
            return Vector3.Distance(point,ray.origin+dir*along);
        }
        private static float PointAngleFromRay(Ray ray,Vector3 point) {
            Vector3 to=point-ray.origin;
            if(to.sqrMagnitude<0.0001f)return 180f;
            return Vector3.Angle(ray.direction.normalized,to.normalized);
        }
        private bool TryResolvePreviewPart(Preview p,BaseEntity hitEntity,out string id) {
            id="";
            if(p==null||hitEntity==null)return false;
            BaseEntity current=hitEntity;
            for(int depth=0;depth<8&&current!=null;depth++) {
                foreach(var kv in p.Entities) {
                    if(kv.Value!=null&&!kv.Value.IsDestroyed&&ReferenceEquals(kv.Value,current)) {id=kv.Key;return true;}
                }
                BaseEntity parent=null;
                try {parent=current.GetParentEntity();} catch {}
                if(parent==null||ReferenceEquals(parent,current))break;
                current=parent;
            }
            return false;
        }
        private bool TryRaycastPreviewPart(BasePlayer user,Preview p,out DirectCandidate candidate,out float firstBlockDistance) {
            candidate=null; firstBlockDistance=30f;
            if(user==null||p==null)return false;
            Ray ray=user.eyes.HeadRay();
            RaycastHit[] hits;
            try {hits=Physics.RaycastAll(ray,30f,Physics.DefaultRaycastLayers,QueryTriggerInteraction.Ignore);} catch {return false;}
            if(hits==null||hits.Length==0)return false;
            Array.Sort(hits,(a,b)=>a.distance.CompareTo(b.distance));
            for(int i=0;i<hits.Length;i++) {
                var hit=hits[i];
                if(hit.distance<0.25f)continue;
                BaseEntity ent=null; try {ent=hit.GetEntity();} catch {}
                if(ent!=null&&ReferenceEquals(ent,user))continue;
                string id;
                if(ent!=null&&TryResolvePreviewPart(p,ent,out id)) {
                    firstBlockDistance=hit.distance;
                    candidate=new DirectCandidate{Kind="PART",Id=id,Position=hit.point,Along=hit.distance,RayDistance=0f,AngleDeg=0f,ExactHit=true,Priority=1};
                    return true;
                }
                // Do not select a mech part through terrain, buildings, another entity, etc.
                firstBlockDistance=hit.distance;
                break;
            }
            return false;
        }
        private List<DirectCandidate> DirectCandidates(BasePlayer user) {
            var list=new List<DirectCandidate>();
            Preview p; if(user==null||!_previews.TryGetValue(user.userID,out p))return list;
            var b=B(user); var ray=user.eyes.HeadRay();
            Quaternion rootRot=p.RootRotation*Quaternion.Euler(0,b.RootYaw,0);

            DirectCandidate exactPart; float blockDistance;
            bool hasExactPart=TryRaycastPreviewPart(user,p,out exactPart,out blockDistance);

            // Joint markers are virtual, so use a deliberately tight angular cone. A joint exactly under
            // the crosshair gets priority over a part collider at nearly the same depth.
            foreach(var id in Ids) {
                if(id=="WAIST")continue;
                Vector3 pos;Quaternion rot;SlotJointPose(b,id,p.RootPosition,rootRot,out pos,out rot);
                float along;float d=PointDistanceFromRay(ray,pos,out along);
                if(along<1f||along>30f)continue;
                float angle=PointAngleFromRay(ray,pos);
                if(angle>2.0f)continue;
                // A world object in front blocks remote joints. When a real preview part is under the
                // crosshair, only joints physically near that hit depth are eligible; this prevents
                // accidentally grabbing a knee/shoulder hidden behind another body part.
                if(!hasExactPart&&along>blockDistance+0.45f)continue;
                if(hasExactPart&&Mathf.Abs(along-exactPart.Along)>2.0f)continue;
                int pri=angle<=0.70f?0:2;
                list.Add(new DirectCandidate{Kind="JOINT",Id=id,Position=pos,Along=along,RayDistance=d,AngleDeg=angle,ExactHit=false,Priority=pri});
            }

            if(hasExactPart&&exactPart!=null) list.Add(exactPart);

            // Some visual entities have weak/no useful collider. Only then use a very narrow fallback cone.
            if(!hasExactPart) {
                foreach(var id in Ids) {
                    Vector3 pos;Quaternion rot;SlotPose(b,id,p.RootPosition,rootRot,out pos,out rot);
                    float along;float d=PointDistanceFromRay(ray,pos,out along);
                    if(along<1f||along>30f)continue;
                    float angle=PointAngleFromRay(ray,pos);
                    if(angle>1.25f)continue;
                    if(along>blockDistance+0.30f)continue;
                    list.Add(new DirectCandidate{Kind="PART",Id=id,Position=pos,Along=along,RayDistance=d,AngleDeg=angle,ExactHit=false,Priority=3});
                }
            }

            list.Sort((a,bx)=> {
                int c=a.Priority.CompareTo(bx.Priority); if(c!=0)return c;
                c=a.AngleDeg.CompareTo(bx.AngleDeg); if(c!=0)return c;
                c=a.Along.CompareTo(bx.Along); if(c!=0)return c;
                return a.RayDistance.CompareTo(bx.RayDistance);
            });
            // Avoid duplicated logical candidates from fallback calculations.
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result=new List<DirectCandidate>();
            foreach(var c in list) {
                string key=c.Kind+":"+c.Id;
                if(seen.Add(key))result.Add(c);
            }
            return result;
        }
        private DirectCandidate CurrentManualCandidate(BasePlayer user,List<DirectCandidate> list) {
            var e=E(user);
            if(Time.realtimeSinceStartup>e.ManualCandidateUntil||string.IsNullOrEmpty(e.ManualCandidateId))return null;
            var ray=user.eyes.HeadRay();
            if(Vector3.Angle(ray.direction.normalized,e.ManualCandidateView.normalized)>3.0f)return null;
            return list.FirstOrDefault(c=>string.Equals(c.Kind,e.ManualCandidateKind,StringComparison.OrdinalIgnoreCase)&&string.Equals(c.Id,e.ManualCandidateId,StringComparison.OrdinalIgnoreCase));
        }
        private void ClearManualCandidate(Editor e) {
            if(e==null)return;
            e.DirectCandidateIndex=-1;e.ManualCandidateKind="";e.ManualCandidateId="";e.ManualCandidateUntil=0f;e.ManualCandidateView=Vector3.zero;
        }
        private void DrawAimLock(BasePlayer user) {
            if(user==null||!user.IsConnected)return;
            var e=E(user);if(!e.DirectEdit||e.Grabbed)return;
            float now=Time.realtimeSinceStartup;if(now-e.LastAimProbe<0.18f)return;e.LastAimProbe=now;
            var list=DirectCandidates(user);if(list.Count==0)return;
            var c=CurrentManualCandidate(user,list)??list[0];
            try {
                Color color=c.Kind=="JOINT"?Color.cyan:Color.green;
                user.SendConsoleCommand("ddraw.sphere",0.28f,Color.yellow,c.Position,c.Kind=="JOINT"?0.30f:0.22f);
                user.SendConsoleCommand("ddraw.text",0.28f,color,c.Position+Vector3.up*0.34f,(c.Kind=="JOINT"?"照準: "+JointLabel(c.Id):"照準: "+c.Id)+"  E");
            } catch {}
        }
        private void DrawDirectMarkers(BasePlayer user) {
            if(user==null||!user.IsConnected)return;
            var e=E(user); if(!e.DirectEdit)return;
            Preview p; if(!_previews.TryGetValue(user.userID,out p))return;
            var b=B(user);Quaternion rootRot=p.RootRotation*Quaternion.Euler(0,b.RootYaw,0);
            try {
                if(!e.MarkersVisible) {
                    if(e.Grabbed&&!string.IsNullOrEmpty(e.GrabId)) {
                        Vector3 hp;Quaternion hr; if(e.GrabKind=="JOINT")SlotJointPose(b,e.GrabId,p.RootPosition,rootRot,out hp,out hr);else SlotPose(b,e.GrabId,p.RootPosition,rootRot,out hp,out hr);
                        user.SendConsoleCommand("ddraw.sphere",1.25f,Color.yellow,hp,0.30f);
                    }
                    return;
                }
                foreach(var id in Ids) {
                    Vector3 ppos;Quaternion prot;SlotPose(b,id,p.RootPosition,rootRot,out ppos,out prot);
                    user.SendConsoleCommand("ddraw.sphere",1.25f,Color.green,ppos,0.11f);
                }
                foreach(var id in Ids) {
                    if(id=="WAIST")continue;
                    Vector3 jpos;Quaternion jrot;SlotJointPose(b,id,p.RootPosition,rootRot,out jpos,out jrot);
                    user.SendConsoleCommand("ddraw.sphere",1.25f,Color.cyan,jpos,0.22f);
                }
                if(e.Grabbed&&!string.IsNullOrEmpty(e.GrabId)) {
                    Vector3 spos;Quaternion srot;
                    if(e.GrabKind=="JOINT")SlotJointPose(b,e.GrabId,p.RootPosition,rootRot,out spos,out srot);
                    else SlotPose(b,e.GrabId,p.RootPosition,rootRot,out spos,out srot);
                    user.SendConsoleCommand("ddraw.sphere",1.25f,Color.yellow,spos,0.34f);
                    user.SendConsoleCommand("ddraw.text",1.25f,Color.white,spos+Vector3.up*0.4f,(e.GrabKind=="JOINT"?"J "+JointLabel(e.GrabId):"P "+e.GrabId)+" / "+e.ManipMode);
                }
            } catch {}
        }
        private void ShowDirectCandidate(BasePlayer user,bool cycle) {
            var e=E(user);var list=DirectCandidates(user);
            if(list.Count==0){ClearManualCandidate(e);e.Message="照準上に部位/関節がありません";Say(user,e.Message);DrawDirectHud(user);return;}
            int idx=0;
            var current=CurrentManualCandidate(user,list);
            if(cycle&&current!=null) {
                int found=list.FindIndex(c=>string.Equals(c.Kind,current.Kind,StringComparison.OrdinalIgnoreCase)&&string.Equals(c.Id,current.Id,StringComparison.OrdinalIgnoreCase));
                idx=found>=0?(found+1)%list.Count:0;
            }
            var c=list[idx];
            e.DirectCandidateIndex=idx;e.ManualCandidateKind=c.Kind;e.ManualCandidateId=c.Id;
            e.ManualCandidateUntil=Time.realtimeSinceStartup+2.5f;e.ManualCandidateView=user.eyes.HeadRay().direction.normalized;
            try {
                user.SendConsoleCommand("ddraw.sphere",2.6f,Color.yellow,c.Position,c.Kind=="JOINT"?0.40f:0.30f);
                user.SendConsoleCommand("ddraw.text",2.6f,Color.white,c.Position+Vector3.up*0.45f,(c.Kind=="JOINT"?"JOINT "+JointLabel(c.Id):"PART "+c.Id)+"  Eで掴む");
            } catch {}
            e.Message=(c.Kind=="JOINT"?"関節 "+JointLabel(c.Id):"パーツ "+c.Id)+" を照準ロック";
            DrawDirectHud(user);
        }
        private void PickDirectTarget(BasePlayer user) {
            StopMotionTest(user.userID,true);
            StopPunchTest(user.userID,true,false);
            var e=E(user);var list=DirectCandidates(user);
            if(list.Count==0){ClearManualCandidate(e);Say(user,"照準が対象に合っていません。黄色の照準ロックが出ている対象だけ掴めます。");return;}
            var c=CurrentManualCandidate(user,list)??list[0];
            ClearManualCandidate(e);
            e.Grabbed=true;e.GrabKind=c.Kind;e.GrabId=c.Id;e.Selected=c.Id;e.ManipMode="MOVE";
            var eyeRay=user.eyes.HeadRay(); e.GrabDistance=Mathf.Clamp(Vector3.Distance(eyeRay.origin,c.Position),1.5f,24f);
            var slot=B(user).Slots[c.Id];
            e.GrabStartRotation=(c.Kind=="JOINT"?slot.Rotation:slot.VisualRotation).Copy();
            e.GrabStartScale=slot.VisualScale;
            e.GrabStartWaistYaw=B(user).WaistYaw;
            e.GrabViewAngles=ViewAngles(user);e.LastDirectUpdate=0f;
            e.Message=(c.Kind=="JOINT"?"関節 "+JointLabel(c.Id):"パーツ "+c.Id)+" を掴み中";
            DrawDirectHud(user);DrawDirectMarkers(user);
        }
        private void PlaceDirectTarget(BasePlayer user) {
            var e=E(user);if(!e.Grabbed)return;
            string label=e.GrabKind=="JOINT"?"関節 "+JointLabel(e.GrabId):"パーツ "+e.GrabId;
            e.Grabbed=false;e.ManipMode="MOVE";e.Message=label+" を配置しました";
            SaveData();DrawDirectHud(user);DrawDirectMarkers(user);
        }
        private void RotationBaseline(BasePlayer user) {
            var e=E(user);if(!e.Grabbed)return;
            var slot=B(user).Slots[e.GrabId];
            e.GrabStartRotation=(e.GrabKind=="JOINT"?slot.Rotation:slot.VisualRotation).Copy();
            e.GrabStartScale=slot.VisualScale;
            e.GrabStartWaistYaw=B(user).WaistYaw;
            e.GrabViewAngles=ViewAngles(user);
        }
        private void CycleManipMode(BasePlayer user) {
            var e=E(user);if(!e.Grabbed)return;
            if(e.ManipMode=="MOVE")e.ManipMode="ROT_Y";
            else if(e.ManipMode=="ROT_Y")e.ManipMode="ROT_X";
            else if(e.ManipMode=="ROT_X")e.ManipMode="ROT_Z";
            else if(e.ManipMode=="ROT_Z" && e.GrabKind=="PART")e.ManipMode="SCALE";
            else e.ManipMode="MOVE";
            RotationBaseline(user);
            e.Message="操作モード: "+e.ManipMode;
            DrawDirectHud(user);
        }
        private static float ClampLocal(float value) { return Mathf.Clamp(value,-12f,12f); }
        private void UpdateDirectManipulation(BasePlayer user) {
            var e=E(user);if(!e.DirectEdit||!e.Grabbed)return;
            Preview p;if(!_previews.TryGetValue(user.userID,out p))return;
            float now=Time.realtimeSinceStartup;if(now-e.LastDirectUpdate<0.10f)return;e.LastDirectUpdate=now;
            var b=B(user);var slot=b.Slots[e.GrabId];Quaternion rootRot=p.RootRotation*Quaternion.Euler(0,b.RootYaw,0);
            if(e.ManipMode=="MOVE") {
                var ray=user.eyes.HeadRay();Vector3 target=ray.origin+ray.direction.normalized*e.GrabDistance;
                // Keep accidental throws bounded around the current workshop.
                if(Vector3.Distance(target,p.RootPosition)>18f||Vector3.Distance(target,user.transform.position)>26f)return;
                if(e.GrabKind=="JOINT") {
                    // Pivot-edit: move only the articulation point. Keep the visible child and its descendants
                    // visually stationary until the joint is rotated. This makes shoulder/knee calibration sane.
                    PreserveVisualWhileMovingJoint(b,e.GrabId,p.RootPosition,rootRot,target);
                    string pair=MirrorId(e.GrabId);
                    if(e.Symmetry&&!string.IsNullOrEmpty(pair))MirrorJointPivotPreserveVisual(b,e.GrabId,pair,p.RootPosition,rootRot);
                } else {
                    Vector3 jointPos;Quaternion jointRot;SlotJointPose(b,e.GrabId,p.RootPosition,rootRot,out jointPos,out jointRot);
                    Vector3 local=Quaternion.Inverse(jointRot)*(target-jointPos);
                    slot.VisualPosition=new Vec(ClampLocal(local.x),ClampLocal(local.y),ClampLocal(local.z));
                    string pair=MirrorId(e.GrabId);
                    if(e.Symmetry&&!string.IsNullOrEmpty(pair)&&b.Slots.ContainsKey(pair))b.Slots[pair].VisualPosition=MirrorPosition(slot.VisualPosition);
                }
                UpdatePreviewBranch(user,e.GrabId,true);
                string movePair=MirrorId(e.GrabId);if(e.Symmetry&&!string.IsNullOrEmpty(movePair))UpdatePreviewBranch(user,movePair,true);
            } else if(e.ManipMode=="SCALE" && e.GrabKind=="PART") {
                Vector3 current=ViewAngles(user);
                float delta=-Mathf.DeltaAngle(e.GrabViewAngles.x,current.x);
                slot.VisualScale=Mathf.Clamp(e.GrabStartScale+delta*0.018f,0.20f,4.0f);
                string pair=MirrorId(e.GrabId);
                if(e.Symmetry&&!string.IsNullOrEmpty(pair)&&b.Slots.ContainsKey(pair))b.Slots[pair].VisualScale=slot.VisualScale;
                UpdatePreviewBranch(user,e.GrabId,true);
                if(e.Symmetry&&!string.IsNullOrEmpty(pair))UpdatePreviewBranch(user,pair,true);
            } else {
                Vector3 current=ViewAngles(user);
                // Special case: the blue "腰上" marker is the upper-body root. ROT_Y edits the
                // dedicated upright WaistYaw value, not TORSO's prefab-local Y rotation.
                if(e.GrabKind=="JOINT" && string.Equals(e.GrabId,"TORSO",StringComparison.OrdinalIgnoreCase) && e.ManipMode=="ROT_Y") {
                    float delta=Mathf.DeltaAngle(e.GrabViewAngles.y,current.y);
                    b.WaistYaw=Mathf.Clamp(e.GrabStartWaistYaw+delta,-_config.WaistYawLimit,_config.WaistYawLimit);
                    UpdatePreviewBranch(user,"TORSO",true);
                } else {
                    Vec r=e.GrabStartRotation.Copy();
                    float delta;
                    if(e.ManipMode=="ROT_X") {delta=-Mathf.DeltaAngle(e.GrabViewAngles.x,current.x);r.X=Mathf.Repeat(r.X+delta,360f);}
                    else if(e.ManipMode=="ROT_Y") {delta=Mathf.DeltaAngle(e.GrabViewAngles.y,current.y);r.Y=Mathf.Repeat(r.Y+delta,360f);}
                    else {delta=Mathf.DeltaAngle(e.GrabViewAngles.y,current.y);r.Z=Mathf.Repeat(r.Z+delta,360f);}
                    if(e.GrabKind=="JOINT")slot.Rotation=r;else slot.VisualRotation=r;
                    string pair=MirrorId(e.GrabId);
                    if(e.Symmetry&&!string.IsNullOrEmpty(pair)&&b.Slots.ContainsKey(pair)) {
                        if(e.GrabKind=="JOINT")b.Slots[pair].Rotation=MirrorEuler(r);
                        else b.Slots[pair].VisualRotation=MirrorEuler(r);
                    }
                    UpdatePreviewBranch(user,e.GrabId,true);
                    if(e.Symmetry&&!string.IsNullOrEmpty(pair))UpdatePreviewBranch(user,pair,true);
                }
            }
            if(now-e.LastMarkerDraw>0.8f){e.LastMarkerDraw=now;DrawDirectMarkers(user);}
        }
        private void SetDirectEdit(BasePlayer user,bool enabled) {
            var e=E(user);
            if(enabled) {
                if(!_previews.ContainsKey(user.userID))BuildPreview(user);
                if(!_previews.ContainsKey(user.userID)){Say(user,"プレビュー生成に失敗したため直接編集を開始できません");return;}
                e.DirectEdit=true;e.UiOpen=false;e.Grabbed=false;ClearManualCandidate(e);e.ManipMode="MOVE";
                CuiHelper.DestroyUi(user,UiRoot);
                e.Message="直接編集ON：緑=パーツ / 青=関節。青MOVEは支点だけ移動。左右同期="+(e.Symmetry?"ON":"OFF");
                Say(user,"直接編集ON。E=掴む/置く、R=移動→Y→X→Z回転→サイズ(PARTのみ)。青い関節MOVEは支点だけ調整。/kmb markers でマーカー表示切替。");
                DrawDirectHud(user);DrawDirectMarkers(user);
            } else {
                if(e.Grabbed)PlaceDirectTarget(user);
                e.DirectEdit=false;e.Grabbed=false;CuiHelper.DestroyUi(user,DirectUiRoot);SaveData();
                Say(user,"直接編集OFF。/kmb で通常UIを開けます。");
            }
        }
        private void DrawDirectHud(BasePlayer user) {
            if(user==null||!user.IsConnected)return;var e=E(user);
            CuiHelper.DestroyUi(user,DirectUiRoot);if(!e.DirectEdit)return;
            var ui=new CuiElementContainer();
            var root=ui.Add(new CuiPanel{Image={Color="0.04 0.055 0.075 0.82"},RectTransform={AnchorMin="0.735 0.795",AnchorMax="0.985 0.965"},CursorEnabled=false},"Overlay",DirectUiRoot);
            Label(ui,root,"KMB 直接編集",0.05f,0.72f,0.95f,0.96f,13,"0.70 0.94 1 1");
            string target=e.Grabbed?(e.GrabKind=="JOINT"?"関節 "+JointLabel(e.GrabId):"パーツ "+e.GrabId):"照準で対象を選択";
            Label(ui,root,target,0.05f,0.48f,0.95f,0.72f,11);
            string detail=e.Grabbed&&e.GrabKind=="PART"?"  x"+B(user).Slots[e.GrabId].VisualScale.ToString("0.00",CultureInfo.InvariantCulture):"";
            if(e.Grabbed&&e.GrabKind=="JOINT"&&string.Equals(e.GrabId,"TORSO",StringComparison.OrdinalIgnoreCase)&&e.ManipMode=="ROT_Y") detail="  腰上 "+B(user).WaistYaw.ToString("0.0",CultureInfo.InvariantCulture)+"°";
            Label(ui,root,e.Grabbed?"E:置く   R:"+e.ManipMode+"切替"+detail:"黄色ロック→E:掴む   R:重なり候補",0.05f,0.25f,0.95f,0.49f,10,"0.95 0.83 0.48 1");
            Label(ui,root,"左右同期:"+(e.Symmetry?"ON":"OFF")+"  印:"+(e.MarkersVisible?"ON":"OFF")+"  /kmb markers",0.05f,0.05f,0.95f,0.27f,9,"0.72 0.78 0.84 1");
            CuiHelper.AddUi(user,ui);
        }
        private void OnPlayerInput(BasePlayer player,InputState input) {
            if(player==null||input==null||!player.IsConnected||!Allowed(player))return;
            var freeEditor=E(player);
            if(freeEditor.FreeDirect) {
                HandleFreeInput(player,input);
                return; // Never allow free-build keys to fall through into pilot/classic editor logic.
            }
            Preview pilotPreview;
            if(TryGetPilotPreview(player,out pilotPreview)){HandlePilotInput(player,input,pilotPreview);return;}
            Editor e;if(!_editors.TryGetValue(player.userID,out e)||!e.DirectEdit)return;
            if(input.WasJustPressed(BUTTON.USE)) {
                if(e.Grabbed)PlaceDirectTarget(player);else PickDirectTarget(player);
            } else if(input.WasJustPressed(BUTTON.RELOAD)) {
                if(e.Grabbed)CycleManipMode(player);else ShowDirectCandidate(player,true);
            }
            if(e.Grabbed) {
                UpdateDirectManipulation(player);
            } else {
                DrawAimLock(player);
                if(Time.realtimeSinceStartup-e.LastMarkerDraw>1.0f){e.LastMarkerDraw=Time.realtimeSinceStartup;DrawDirectMarkers(player);}
            }
        }
        private static string DefaultMotionAxis(string id) {
            if(string.IsNullOrEmpty(id))return "X";
            if(id=="TORSO"||id=="HEAD")return "Y";
            if(id.EndsWith("_UPPER_ARM",StringComparison.OrdinalIgnoreCase)||id.EndsWith("_THIGH",StringComparison.OrdinalIgnoreCase))return "Z";
            return "X";
        }
        private void StopMotionTest(ulong owner,bool restore) {
            MotionTest mt;if(!_motionTests.TryGetValue(owner,out mt))return;
            _motionTests.Remove(owner);if(mt.Timer!=null)mt.Timer.Destroy();
            if(restore) {
                Blueprint b;if(_data!=null&&_data.Builders!=null&&_data.Builders.TryGetValue(owner.ToString(CultureInfo.InvariantCulture),out b)&&b!=null&&b.Slots!=null) {
                    if(!string.IsNullOrEmpty(mt.SlotId)&&b.Slots.ContainsKey(mt.SlotId))b.Slots[mt.SlotId].Rotation=mt.BaseRotation.Copy();
                    if(!string.IsNullOrEmpty(mt.PairId)&&b.Slots.ContainsKey(mt.PairId))b.Slots[mt.PairId].Rotation=mt.PairBaseRotation.Copy();
                }
                var player=BasePlayer.FindByID(owner);if(player!=null&&player.IsConnected) {
                    UpdatePreviewBranch(player,mt.SlotId,true);
                    if(!string.IsNullOrEmpty(mt.PairId))UpdatePreviewBranch(player,mt.PairId,true);
                }
            }
        }
        private void StartMotionTest(BasePlayer user,string id,string axis,float amplitude,float speed) {
            if(user==null||!_previews.ContainsKey(user.userID)){Say(user,"先に /kmb preview または /kmb edit で全身を生成してね");return;}
            var b=B(user);if(string.IsNullOrEmpty(id)||!b.Slots.ContainsKey(id)||id=="WAIST"){Say(user,"動かす関節を選んでね（肩/肘/股/膝/足首/腰上など）");return;}
            axis=(axis??DefaultMotionAxis(id)).ToUpperInvariant();if(axis!="X"&&axis!="Y"&&axis!="Z")axis=DefaultMotionAxis(id);
            amplitude=Mathf.Clamp(amplitude,2f,85f);speed=Mathf.Clamp(speed,0.10f,2.5f);
            StopPunchTest(user.userID,true,false);
            StopMotionTest(user.userID,true);
            var e=E(user);var mt=new MotionTest{SlotId=id,Axis=axis,Amplitude=amplitude,Speed=speed,StartedAt=Time.realtimeSinceStartup,BaseRotation=b.Slots[id].Rotation.Copy()};
            string pair=e.Symmetry?MirrorId(id):"";if(!string.IsNullOrEmpty(pair)&&b.Slots.ContainsKey(pair)){mt.PairId=pair;mt.PairBaseRotation=b.Slots[pair].Rotation.Copy();}
            mt.Timer=timer.Every(0.05f,()=> {
                if(user==null||!user.IsConnected||!_previews.ContainsKey(user.userID)){StopMotionTest(user!=null?user.userID:0,false);return;}
                var bp=B(user);float wave=Mathf.Sin((Time.realtimeSinceStartup-mt.StartedAt)*mt.Speed*Mathf.PI*2f)*mt.Amplitude;
                Vec rr=mt.BaseRotation.Copy();if(mt.Axis=="X")rr.X=Mathf.Repeat(rr.X+wave,360f);else if(mt.Axis=="Y")rr.Y=Mathf.Repeat(rr.Y+wave,360f);else rr.Z=Mathf.Repeat(rr.Z+wave,360f);
                bp.Slots[mt.SlotId].Rotation=rr;UpdatePreviewBranch(user,mt.SlotId,true);
                if(!string.IsNullOrEmpty(mt.PairId)&&bp.Slots.ContainsKey(mt.PairId)){bp.Slots[mt.PairId].Rotation=MirrorEuler(rr);UpdatePreviewBranch(user,mt.PairId,true);}
            });
            _motionTests[user.userID]=mt;Say(user,JointLabel(id)+" 動作テスト開始："+axis+" ±"+amplitude.ToString("0",CultureInfo.InvariantCulture)+"°  /kmb test stop で停止");
        }

        private static float Smooth01(float t) {
            t=Mathf.Clamp01(t);
            return t*t*(3f-2f*t);
        }
        // v0.2.0 punch is a true multi-stage sequence rather than one straight interpolation:
        // stand -> arm wind-up -> waist drive -> strike -> recoil -> stand.
        private static Vec LerpEuler(Vec a,Vec b,float t) {
            if(a==null)a=new Vec();if(b==null)b=new Vec();
            t=Mathf.Clamp01(t);
            return new Vec(
                Mathf.LerpAngle(a.X,b.X,t),
                Mathf.LerpAngle(a.Y,b.Y,t),
                Mathf.LerpAngle(a.Z,b.Z,t)
            );
        }
        private static Vec LerpVec(Vec a,Vec b,float t) {
            if(a==null)a=new Vec();if(b==null)b=new Vec();
            t=Mathf.Clamp01(t);
            return new Vec(
                Mathf.Lerp(a.X,b.X,t),
                Mathf.Lerp(a.Y,b.Y,t),
                Mathf.Lerp(a.Z,b.Z,t)
            );
        }
        private static SlotState CaptureSlotState(Slot s) {
            if(s==null)return new SlotState();
            return new SlotState {
                Position=s.Position!=null?s.Position.Copy():new Vec(),
                Rotation=s.Rotation!=null?s.Rotation.Copy():new Vec(),
                VisualPosition=s.VisualPosition!=null?s.VisualPosition.Copy():new Vec(),
                VisualRotation=s.VisualRotation!=null?s.VisualRotation.Copy():new Vec(),
                VisualScale=Mathf.Clamp(s.VisualScale,0.20f,4.0f)
            };
        }
        private static void ApplySlotState(Slot s,SlotState st) {
            if(s==null||st==null)return;
            s.Position=st.Position!=null?st.Position.Copy():new Vec();
            s.Rotation=st.Rotation!=null?st.Rotation.Copy():new Vec();
            s.VisualPosition=st.VisualPosition!=null?st.VisualPosition.Copy():new Vec();
            s.VisualRotation=st.VisualRotation!=null?st.VisualRotation.Copy():new Vec();
            s.VisualScale=Mathf.Clamp(st.VisualScale<=0f?1f:st.VisualScale,0.20f,4.0f);
        }
        private static SlotState LerpSlotState(SlotState a,SlotState b,float t) {
            if(a==null)a=new SlotState();if(b==null)b=new SlotState();
            t=Mathf.Clamp01(t);
            return new SlotState {
                Position=LerpVec(a.Position,b.Position,t),
                Rotation=LerpEuler(a.Rotation,b.Rotation,t),
                VisualPosition=LerpVec(a.VisualPosition,b.VisualPosition,t),
                VisualRotation=LerpEuler(a.VisualRotation,b.VisualRotation,t),
                VisualScale=Mathf.Lerp(a.VisualScale<=0f?1f:a.VisualScale,b.VisualScale<=0f?1f:b.VisualScale,t)
            };
        }
        private static float SlotStateDelta(SlotState a,SlotState b) {
            if(a==null||b==null)return 999f;
            float d=0f;
            d+=Vector3.Distance(a.Position!=null?a.Position.V():Vector3.zero,b.Position!=null?b.Position.V():Vector3.zero);
            d+=Vector3.Distance(a.VisualPosition!=null?a.VisualPosition.V():Vector3.zero,b.VisualPosition!=null?b.VisualPosition.V():Vector3.zero);
            if(a.Rotation!=null&&b.Rotation!=null)d+=Mathf.Abs(Mathf.DeltaAngle(a.Rotation.X,b.Rotation.X))+Mathf.Abs(Mathf.DeltaAngle(a.Rotation.Y,b.Rotation.Y))+Mathf.Abs(Mathf.DeltaAngle(a.Rotation.Z,b.Rotation.Z));
            if(a.VisualRotation!=null&&b.VisualRotation!=null)d+=Mathf.Abs(Mathf.DeltaAngle(a.VisualRotation.X,b.VisualRotation.X))+Mathf.Abs(Mathf.DeltaAngle(a.VisualRotation.Y,b.VisualRotation.Y))+Mathf.Abs(Mathf.DeltaAngle(a.VisualRotation.Z,b.VisualRotation.Z));
            d+=Mathf.Abs(a.VisualScale-b.VisualScale)*10f;
            return d;
        }
        private PoseState CapturePose(Blueprint b) {
            var pose=new PoseState{WaistYaw=b!=null?b.WaistYaw:0f,RootYaw=b!=null?b.RootYaw:0f,Slots=new Dictionary<string,SlotState>()};
            if(b!=null&&b.Slots!=null)foreach(var id in Ids){Slot slot;if(b.Slots.TryGetValue(id,out slot)&&slot!=null)pose.Slots[id]=CaptureSlotState(slot);}
            return pose;
        }
        private void ApplyPose(Blueprint b,PoseState pose) {
            if(b==null||pose==null)return;
            if(pose.Slots!=null)foreach(var pair in pose.Slots){Slot slot;if(b.Slots.TryGetValue(pair.Key,out slot)&&slot!=null&&pair.Value!=null)ApplySlotState(slot,pair.Value);}
            b.WaistYaw=Mathf.Clamp(pose.WaistYaw,-_config.WaistYawLimit,_config.WaistYawLimit);
            b.RootYaw=pose.RootYaw;
        }
        private void StopPunchTest(ulong owner,bool restore,bool notify) {
            PunchTest pt;
            if(!_punchTests.TryGetValue(owner,out pt))return;
            _punchTests.Remove(owner);
            if(pt.Timer!=null)pt.Timer.Destroy();
            BasePlayer player=BasePlayer.FindByID(owner);
            Blueprint b=null;
            if(_data!=null&&_data.Builders!=null)_data.Builders.TryGetValue(owner.ToString(CultureInfo.InvariantCulture),out b);
            if(restore&&b!=null&&b.Slots!=null) {
                Slot shoulder,elbow;
                if(b.Slots.TryGetValue(pt.ShoulderId,out shoulder))ApplySlotState(shoulder,pt.BaseShoulder);
                if(b.Slots.TryGetValue(pt.ElbowId,out elbow))ApplySlotState(elbow,pt.BaseElbow);
                b.WaistYaw=pt.BaseWaistYaw;
                if(player!=null&&player.IsConnected&&_previews.ContainsKey(owner))UpdatePreviewBranch(player,"TORSO",true);
            }
            if(notify&&player!=null&&player.IsConnected)Say(player,"パンチテスト終了。開始時の姿勢へ戻しました");
        }
        private bool TryGetRecordedWindupPose(Blueprint b,bool right,float fallbackWaist,out SlotState shoulder,out SlotState elbow,out float waist) {
            shoulder=new SlotState(); elbow=new SlotState(); waist=fallbackWaist;
            if(b==null)return false;
            if(right) {
                if(!b.HasRightWindupPose)return false;
                shoulder=b.RightWindupShoulderState!=null?b.RightWindupShoulderState.Copy():new SlotState();
                elbow=b.RightWindupElbowState!=null?b.RightWindupElbowState.Copy():new SlotState();
                waist=b.HasRightWindupWaist?b.RightWindupWaistYaw:fallbackWaist;
                return true;
            }
            if(!b.HasLeftWindupPose)return false;
            shoulder=b.LeftWindupShoulderState!=null?b.LeftWindupShoulderState.Copy():new SlotState();
            elbow=b.LeftWindupElbowState!=null?b.LeftWindupElbowState.Copy():new SlotState();
            waist=b.HasLeftWindupWaist?b.LeftWindupWaistYaw:fallbackWaist;
            return true;
        }
        private void SaveWindupPose(BasePlayer user,bool right) {
            if(user==null)return;
            var b=B(user);
            string shoulder=right?"R_UPPER_ARM":"L_UPPER_ARM";
            string elbow=right?"R_FOREARM":"L_FOREARM";
            if(!b.Slots.ContainsKey(shoulder)||!b.Slots.ContainsKey(elbow)){Say(user,"腕スロットが見つかりません");return;}
            if(!b.HasStandPose)Say(user,"先に /kmb pose save で立ち姿勢を保存してね");
            SlotState ss=CaptureSlotState(b.Slots[shoulder]);
            SlotState es=CaptureSlotState(b.Slots[elbow]);
            float windWaist=Mathf.Clamp(b.WaistYaw,-_config.WaistYawLimit,_config.WaistYawLimit);
            if(right){
                b.RightWindupShoulderState=ss.Copy();b.RightWindupElbowState=es.Copy();b.RightWindupWaistYaw=windWaist;b.HasRightWindupWaist=true;b.HasRightWindupPose=true;
            } else {
                b.LeftWindupShoulderState=ss.Copy();b.LeftWindupElbowState=es.Copy();b.LeftWindupWaistYaw=windWaist;b.HasLeftWindupWaist=true;b.HasLeftWindupPose=true;
            }
            bool autoMirrored=MirrorPunchRegistration(b,right,false);
            SaveData();
            Say(user,(right?"右":"左")+"腕の『引き姿勢＋腰ひねり』を保存しました"+(autoMirrored?"（反対側も自動ミラー登録）":""));
            if(b.HasStandPose){RestoreStandPoseInternal(user,b,false);SaveData();Say(user,"立ち姿勢へ戻しました。次は腰を戻しながら腕を伸ばしたパンチ終点を作ってね");}
        }
        private void ClearWindupPose(BasePlayer user,bool right) {
            if(user==null)return; var b=B(user);
            if(right){b.HasRightWindupPose=false;b.RightWindupShoulderState=new SlotState();b.RightWindupElbowState=new SlotState();b.HasRightWindupWaist=false;b.RightWindupWaistYaw=0f;}
            else {b.HasLeftWindupPose=false;b.LeftWindupShoulderState=new SlotState();b.LeftWindupElbowState=new SlotState();b.HasLeftWindupWaist=false;b.LeftWindupWaistYaw=0f;}
            SaveData();Say(user,(right?"右":"左")+"腕の引き姿勢を削除しました");
        }

        private bool TryGetRecordedPunchPose(Blueprint b,bool right,SlotState currentShoulder,SlotState currentElbow,out SlotState shoulder,out SlotState elbow,out float waist) {
            shoulder=new SlotState();elbow=new SlotState();waist=0f;
            if(b==null)return false;
            if(right) {
                if(!b.HasRightPunchPose)return false;
                bool hasShoulderState=b.RightPunchShoulderState!=null&&SlotStateDelta(b.RightPunchShoulderState,new SlotState())>=0.001f;
                bool hasElbowState=b.RightPunchElbowState!=null&&SlotStateDelta(b.RightPunchElbowState,new SlotState())>=0.001f;
                shoulder=hasShoulderState?b.RightPunchShoulderState.Copy():currentShoulder.Copy();
                elbow=hasElbowState?b.RightPunchElbowState.Copy():currentElbow.Copy();
                // Backward compatibility for v0.1.9 rotation-only records.
                if(!hasShoulderState&&b.RightPunchShoulder!=null)shoulder.Rotation=b.RightPunchShoulder.Copy();
                if(!hasElbowState&&b.RightPunchElbow!=null)elbow.Rotation=b.RightPunchElbow.Copy();
                waist=b.RightPunchWaistYaw;return true;
            }
            if(!b.HasLeftPunchPose)return false;
            bool hasLeftShoulderState=b.LeftPunchShoulderState!=null&&SlotStateDelta(b.LeftPunchShoulderState,new SlotState())>=0.001f;
            bool hasLeftElbowState=b.LeftPunchElbowState!=null&&SlotStateDelta(b.LeftPunchElbowState,new SlotState())>=0.001f;
            shoulder=hasLeftShoulderState?b.LeftPunchShoulderState.Copy():currentShoulder.Copy();
            elbow=hasLeftElbowState?b.LeftPunchElbowState.Copy():currentElbow.Copy();
            if(!hasLeftShoulderState&&b.LeftPunchShoulder!=null)shoulder.Rotation=b.LeftPunchShoulder.Copy();
            if(!hasLeftElbowState&&b.LeftPunchElbow!=null)elbow.Rotation=b.LeftPunchElbow.Copy();
            waist=b.LeftPunchWaistYaw;return true;
        }
        private void StartPunchTest(BasePlayer user,bool right,bool quiet=false) {
            if(user==null||!user.IsConnected)return;
            if(!_previews.ContainsKey(user.userID)){if(!quiet)Say(user,"先に /kmb preview または /kmb edit で全身を生成してね");return;}
            var e=E(user);
            if(e.Grabbed){if(!quiet)Say(user,"掴んでいるパーツ/関節をEで置いてからパンチを試してね");return;}
            var b=B(user);
            string shoulder=right?"R_UPPER_ARM":"L_UPPER_ARM";
            string elbow=right?"R_FOREARM":"L_FOREARM";
            if(!b.Slots.ContainsKey(shoulder)||!b.Slots.ContainsKey(elbow)){if(!quiet)Say(user,"腕スロットが見つかりません");return;}
            if(!b.HasStandPose){if(!quiet)Say(user,"先に /kmb pose save で立ち姿勢を保存してね");return;}

            // Always start from the recorded stand pose so repeated tests do not drift.
            RestoreStandPoseInternal(user,b,false);
            SlotState baseShoulder=CaptureSlotState(b.Slots[shoulder]);
            SlotState baseElbow=CaptureSlotState(b.Slots[elbow]);
            float baseWaist=b.WaistYaw;

            SlotState windShoulder,windElbow; float windWaist;
            if(!TryGetRecordedWindupPose(b,right,baseWaist,out windShoulder,out windElbow,out windWaist)){
                if(!quiet)Say(user,(right?"右":"左")+"の引き姿勢が未登録です。腕を後ろへ引きながら腰もひねって /kmb winduppose save "+(right?"right":"left")+" で保存してね");
                return;
            }
            SlotState targetShoulder,targetElbow;float targetWaist;
            if(!TryGetRecordedPunchPose(b,right,baseShoulder,baseElbow,out targetShoulder,out targetElbow,out targetWaist)){
                if(!quiet)Say(user,(right?"右":"左")+"パンチ終点が未登録です。腰を入れて腕を伸ばし /kmb punchpose save "+(right?"right":"left"));
                return;
            }
            float windMovement=SlotStateDelta(baseShoulder,windShoulder)+SlotStateDelta(baseElbow,windElbow)+Mathf.Abs(Mathf.DeltaAngle(baseWaist,windWaist));
            float strikeMovement=SlotStateDelta(windShoulder,targetShoulder)+SlotStateDelta(windElbow,targetElbow)+Mathf.Abs(Mathf.DeltaAngle(baseWaist,targetWaist));
            if(windMovement<0.20f){if(!quiet)Say(user,"引き姿勢が立ち姿勢とほぼ同じです。肩/肘の青いジョイントを使って、腕を一度しっかり後ろへ引いて保存してね");return;}
            if(strikeMovement<0.30f){if(!quiet)Say(user,"パンチ終点が引き姿勢とほぼ同じです。腰をひねって腕を前へ伸ばした終点を保存してね");return;}

            StopMotionTest(user.userID,true);
            StopPunchTest(user.userID,true,false);
            var pt=new PunchTest{
                Quiet=quiet,
                Side=right?"R":"L",ShoulderId=shoulder,ElbowId=elbow,
                BaseShoulder=baseShoulder.Copy(),BaseElbow=baseElbow.Copy(),BaseWaistYaw=baseWaist,
                WindupShoulder=windShoulder.Copy(),WindupElbow=windElbow.Copy(),WindupWaistYaw=Mathf.Clamp(windWaist,-_config.WaistYawLimit,_config.WaistYawLimit),
                TargetShoulder=targetShoulder.Copy(),TargetElbow=targetElbow.Copy(),
                TargetWaistYaw=Mathf.Clamp(targetWaist,-_config.WaistYawLimit,_config.WaistYawLimit),
                StartedAt=Time.realtimeSinceStartup,Duration=Mathf.Max(1.45f,_config.PunchDurationSeconds)
            };
            _punchTests[user.userID]=pt;
            pt.Timer=timer.Every(_config.PunchUpdateInterval,()=> {
                if(user==null||!user.IsConnected||!_previews.ContainsKey(user.userID)){StopPunchTest(user!=null?user.userID:0,false,false);return;}
                PunchTest live;if(!_punchTests.TryGetValue(user.userID,out live)||live!=pt)return;
                float elapsed=Time.realtimeSinceStartup-pt.StartedAt;
                float p=Mathf.Clamp01(elapsed/Mathf.Max(0.01f,pt.Duration));
                SlotState ss,es; float waist;
                if(p<0.32f){
                    // Pull the arm back and twist the waist at the same time.
                    float t=Smooth01(p/0.32f);
                    ss=LerpSlotState(pt.BaseShoulder,pt.WindupShoulder,t);
                    es=LerpSlotState(pt.BaseElbow,pt.WindupElbow,t);
                    waist=Mathf.LerpAngle(pt.BaseWaistYaw,pt.WindupWaistYaw,t);
                } else if(p<0.40f){
                    // Briefly load the strike at maximum wind-up.
                    ss=pt.WindupShoulder.Copy(); es=pt.WindupElbow.Copy(); waist=pt.WindupWaistYaw;
                } else if(p<0.62f){
                    // Fire the arm forward while the waist rotates toward the recorded strike yaw.
                    float t=Smooth01((p-0.40f)/0.22f);
                    ss=LerpSlotState(pt.WindupShoulder,pt.TargetShoulder,t);
                    es=LerpSlotState(pt.WindupElbow,pt.TargetElbow,t);
                    waist=Mathf.LerpAngle(pt.WindupWaistYaw,pt.TargetWaistYaw,t);
                } else if(p<0.70f){
                    ss=pt.TargetShoulder.Copy(); es=pt.TargetElbow.Copy(); waist=pt.TargetWaistYaw;
                } else if(p<0.84f){
                    // Retract toward the loaded pose.
                    float t=Smooth01((p-0.70f)/0.14f);
                    ss=LerpSlotState(pt.TargetShoulder,pt.WindupShoulder,t);
                    es=LerpSlotState(pt.TargetElbow,pt.WindupElbow,t);
                    waist=Mathf.LerpAngle(pt.TargetWaistYaw,pt.WindupWaistYaw,t);
                } else {
                    // Relax arm and waist together back to standing.
                    float t=Smooth01((p-0.84f)/0.16f);
                    ss=LerpSlotState(pt.WindupShoulder,pt.BaseShoulder,t);
                    es=LerpSlotState(pt.WindupElbow,pt.BaseElbow,t);
                    waist=Mathf.LerpAngle(pt.WindupWaistYaw,pt.BaseWaistYaw,t);
                }
                var bp=B(user);
                ApplySlotState(bp.Slots[pt.ShoulderId],ss);
                ApplySlotState(bp.Slots[pt.ElbowId],es);
                bp.WaistYaw=Mathf.Clamp(waist,-_config.WaistYawLimit,_config.WaistYawLimit);
                UpdatePreviewBranch(user,"TORSO",true);
                if(p>=1f)StopPunchTest(user.userID,true,!pt.Quiet);
            });
            if(!quiet)Say(user,(right?"右":"左")+"パンチ開始：腕を引きながら腰をひねる → 打つ → 戻す");
        }

        private bool HasSavedPunch(Blueprint b,bool right) {
            if(b==null||!b.HasStandPose)return false;
            return right
                ? (b.HasRightWindupPose&&b.HasRightPunchPose)
                : (b.HasLeftWindupPose&&b.HasLeftPunchPose);
        }
        private void StopAutoPunchRuntime(ulong owner) {
            AutoPunchLoop loop;
            if(!_autoPunchLoops.TryGetValue(owner,out loop))return;
            _autoPunchLoops.Remove(owner);
            if(loop.Timer!=null)loop.Timer.Destroy();
        }
        private bool ResolveAutoPunchSide(Blueprint b,AutoPunchLoop loop,out bool right) {
            right=true;
            if(b==null)return false;
            bool hasR=HasSavedPunch(b,true);
            bool hasL=HasSavedPunch(b,false);
            if(!hasR&&!hasL)return false;
            string mode=string.IsNullOrEmpty(b.AutoPunchMode)?"alternate":b.AutoPunchMode.ToLowerInvariant();
            if(mode=="right") { right=hasR?true:false; if(!hasR&&hasL)right=false; return true; }
            if(mode=="left") { right=hasL?false:true; if(!hasL&&hasR)right=true; return true; }
            if(hasR&&hasL) {
                right=loop.NextRight;
                loop.NextRight=!loop.NextRight;
                return true;
            }
            right=hasR;
            return true;
        }
        private void StartAutoPunchRuntime(BasePlayer user,bool notify) {
            if(user==null||!user.IsConnected)return;
            var b=B(user);
            StopAutoPunchRuntime(user.userID);
            if(!b.AutoPunchEnabled)return;
            if(!_previews.ContainsKey(user.userID)) {
                if(notify)Say(user,"自動パンチは全身プレビュー生成後に開始します");
                return;
            }
            bool hasR=HasSavedPunch(b,true),hasL=HasSavedPunch(b,false);
            if(!hasR&&!hasL) {
                b.AutoPunchEnabled=false;SaveData();
                if(notify)Say(user,"自動パンチを開始できません。保存済みの引き姿勢＋パンチ終点がありません");
                return;
            }
            var loop=new AutoPunchLoop{NextRight=true,NextAt=Time.realtimeSinceStartup+0.35f};
            _autoPunchLoops[user.userID]=loop;
            ulong owner=user.userID;
            loop.Timer=timer.Every(0.25f,()=>{
                BasePlayer live=BasePlayer.FindByID(owner);
                AutoPunchLoop active;
                if(!_autoPunchLoops.TryGetValue(owner,out active)||active!=loop)return;
                if(live==null||!live.IsConnected||!_previews.ContainsKey(owner)){StopAutoPunchRuntime(owner);return;}
                var bp=B(live);
                if(!bp.AutoPunchEnabled){StopAutoPunchRuntime(owner);return;}
                Editor ed;
                if(_editors.TryGetValue(owner,out ed)&&(ed.Grabbed||ed.DirectEdit))return;
                if(_punchTests.ContainsKey(owner)||_motionTests.ContainsKey(owner))return;
                float now=Time.realtimeSinceStartup;
                if(now<loop.NextAt)return;
                bool useRight;
                if(!ResolveAutoPunchSide(bp,loop,out useRight)){StopAutoPunchRuntime(owner);return;}
                StartPunchTest(live,useRight,true);
                loop.NextAt=now+Mathf.Max(bp.AutoPunchIntervalSeconds,Mathf.Max(1.45f,_config.PunchDurationSeconds)+0.20f);
            });
            if(notify)Say(user,"自動パンチ ON（"+b.AutoPunchMode+" / 間隔 "+b.AutoPunchIntervalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒）");
        }
        private void SetAutoPunch(BasePlayer user,bool enabled) {
            if(user==null)return;
            var b=B(user);
            if(enabled) {
                if(!HasSavedPunch(b,true)&&!HasSavedPunch(b,false)) {
                    Say(user,"先にパンチ設定を1組以上保存してね。今のv0.2.2設定はそのまま引き継げます");
                    return;
                }
                b.AutoPunchEnabled=true;SaveData();
                if(!_previews.ContainsKey(user.userID))BuildPreview(user);
                StartAutoPunchRuntime(user,true);
            } else {
                b.AutoPunchEnabled=false;SaveData();
                StopAutoPunchRuntime(user.userID);
                StopPunchTest(user.userID,true,false);
                Say(user,"自動パンチ OFF");
            }
        }
        private void ShowAutoPunchStatus(BasePlayer user) {
            var b=B(user);
            bool hasR=HasSavedPunch(b,true),hasL=HasSavedPunch(b,false);
            Say(user,"自動パンチ="+(b.AutoPunchEnabled?"ON":"OFF")
                +" / mode="+b.AutoPunchMode
                +" / interval="+b.AutoPunchIntervalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒"
                +" / 右="+(hasR?"登録済":"未登録")+" 左="+(hasL?"登録済":"未登録"));
        }

        private void SavePunchPose(BasePlayer user,bool right) {
            if(user==null)return;
            var b=B(user);
            string shoulder=right?"R_UPPER_ARM":"L_UPPER_ARM";
            string elbow=right?"R_FOREARM":"L_FOREARM";
            if(!b.Slots.ContainsKey(shoulder)||!b.Slots.ContainsKey(elbow)){Say(user,"腕スロットが見つかりません");return;}
            if(!b.HasStandPose)Say(user,"先に /kmb pose save で立ち姿勢を保存してからパンチ姿勢を作るのがおすすめです");

            SlotState shoulderState=CaptureSlotState(b.Slots[shoulder]);
            SlotState elbowState=CaptureSlotState(b.Slots[elbow]);
            if(right) {
                b.RightPunchShoulderState=shoulderState.Copy();
                b.RightPunchElbowState=elbowState.Copy();
                b.RightPunchShoulder=shoulderState.Rotation.Copy();
                b.RightPunchElbow=elbowState.Rotation.Copy();
                b.RightPunchWaistYaw=b.WaistYaw;
                b.HasRightPunchPose=true;
            } else {
                b.LeftPunchShoulderState=shoulderState.Copy();
                b.LeftPunchElbowState=elbowState.Copy();
                b.LeftPunchShoulder=shoulderState.Rotation.Copy();
                b.LeftPunchElbow=elbowState.Rotation.Copy();
                b.LeftPunchWaistYaw=b.WaistYaw;
                b.HasLeftPunchPose=true;
            }
            bool autoMirrored=MirrorPunchRegistration(b,right,false);
            SaveData();
            Say(user,(right?"右":"左")+"の『パンチ終点』を保存しました（腰角度＋肩/肘状態）"+(autoMirrored?" / 反対側も自動ミラー登録":""));
            if(b.HasStandPose) {
                RestoreStandPoseInternal(user,b,false);
                SaveData();
                Say(user,"保存済みの立ち姿勢へ自動で戻しました。/kmb punch "+(right?"right":"left")+" でテストできます");
            }
        }
        private void ClearPunchPose(BasePlayer user,bool right) {
            if(user==null)return;
            var b=B(user);
            if(right){
                b.HasRightPunchPose=false;b.RightPunchShoulder=new Vec();b.RightPunchElbow=new Vec();b.RightPunchWaistYaw=0f;
                b.RightPunchShoulderState=new SlotState();b.RightPunchElbowState=new SlotState();
            } else {
                b.HasLeftPunchPose=false;b.LeftPunchShoulder=new Vec();b.LeftPunchElbow=new Vec();b.LeftPunchWaistYaw=0f;
                b.LeftPunchShoulderState=new SlotState();b.LeftPunchElbowState=new SlotState();
            }
            SaveData();Say(user,(right?"右":"左")+"パンチ姿勢を削除しました");
        }

        private void SaveStandPose(BasePlayer user) {
            if(user==null)return;
            var b=B(user);
            b.StandPose=CapturePose(b);
            if(b.StandRotations==null)b.StandRotations=new Dictionary<string,Vec>();
            b.StandRotations.Clear();
            foreach(var id in Ids) {
                Slot s;if(b.Slots.TryGetValue(id,out s)&&s!=null&&s.Rotation!=null)b.StandRotations[id]=s.Rotation.Copy();
            }
            b.StandWaistYaw=b.WaistYaw;
            b.HasStandPose=true;
            SaveData();
            Say(user,"現在の全身姿勢を保存しました（関節位置・見た目位置・角度・サイズを含む）");
        }
        private void RestoreStandPoseInternal(BasePlayer user,Blueprint b,bool notify) {
            if(user==null||b==null)return;
            StopMotionTest(user.userID,true);
            StopPunchTest(user.userID,false,false);
            bool usedFull=b.StandPose!=null&&b.StandPose.Slots!=null&&b.StandPose.Slots.Count>0;
            if(usedFull) {
                ApplyPose(b,b.StandPose);
            } else {
                if(b.StandRotations!=null)foreach(var pair in b.StandRotations) {
                    Slot slot;if(b.Slots.TryGetValue(pair.Key,out slot)&&slot!=null&&pair.Value!=null)slot.Rotation=pair.Value.Copy();
                }
                b.WaistYaw=Mathf.Clamp(b.StandWaistYaw,-_config.WaistYawLimit,_config.WaistYawLimit);
            }
            if(_previews.ContainsKey(user.userID))UpdatePreview(user);
            if(notify)Say(user,usedFull?"保存した全身立ち姿勢へ戻しました":"旧形式の立ち姿勢へ戻しました。位置/見た目も保存したい場合は /kmb pose save をもう一度実行してね");
        }
        private void RestoreStandPose(BasePlayer user) {
            if(user==null)return;
            var b=B(user);
            bool hasFull=b.StandPose!=null&&b.StandPose.Slots!=null&&b.StandPose.Slots.Count>0;
            if(!b.HasStandPose&& !hasFull){Say(user,"立ち姿勢がまだ保存されていません。/kmb pose save");return;}
            RestoreStandPoseInternal(user,b,true);
            SaveData();
        }

        private string GroundReport(Blueprint b,Preview p,string id) {
            Vector3 world;Quaternion angle;
            SlotPose(b,id,p.RootPosition,p.RootRotation*Quaternion.Euler(0,b.RootYaw,0),out world,out angle);
            if(TerrainMeta.HeightMap==null)return id+":地形高度マップなし";
            float height=TerrainMeta.HeightMap.GetHeight(world);
            float gap=world.y-height;
            return id+" 地形高度差="+gap.ToString("0.00",CultureInfo.InvariantCulture)+"m（地形のみ・床や建築物の足場は未判定）";
        }
        private void Say(BasePlayer p,string s) {SendReply(p,"[KMB] "+s);}
        private bool IsFootSlot(string id) {
            return string.Equals(id,"R_FOOT",StringComparison.OrdinalIgnoreCase) || string.Equals(id,"L_FOOT",StringComparison.OrdinalIgnoreCase);
        }
        private bool IsLegSegmentSlot(string id) {
            return id!=null && (id.EndsWith("_THIGH",StringComparison.OrdinalIgnoreCase) || id.EndsWith("_SHIN",StringComparison.OrdinalIgnoreCase));
        }
        private string[] CandidateKeysForSlot(string id) {
            if(!IsFootSlot(id) && !IsLegSegmentSlot(id)) return CatalogKeys();
            var ordered=new List<string>();
            var presets=IsFootSlot(id)
                ? new[]{"footplate","foot_snowmobile","foot_cockpit","foot_locker","sign","post","snowmobile","cockpit","locker","camper"}
                : new[]{"gib018","leg_snowmobile","leg_locker","legbeam","snowmobile","tomaha","locker","cockpit","post","sign"};
            foreach(var key in presets)
                if(_catalog.ContainsKey(key) && !ordered.Contains(key,StringComparer.OrdinalIgnoreCase)) ordered.Add(key);
            foreach(var key in _catalogOrder)
                if(_mechBossImportedKeys.Contains(key) && !ordered.Contains(key,StringComparer.OrdinalIgnoreCase)) ordered.Add(key);
            if(!ordered.Contains("none",StringComparer.OrdinalIgnoreCase)) ordered.Add("none");
            return ordered.ToArray();
        }
        private void ApplyPresetRotationIfNeeded(Slot slot,string key) {
            if(slot==null)return;
            if(string.Equals(key,"footplate",StringComparison.OrdinalIgnoreCase)) slot.Rotation=new Vec(90f,0f,0f);
            else if(string.Equals(key,"foot_snowmobile",StringComparison.OrdinalIgnoreCase)) slot.Rotation=new Vec(0f,90f,0f);
            else if(string.Equals(key,"foot_cockpit",StringComparison.OrdinalIgnoreCase)) slot.Rotation=new Vec(0f,90f,0f);
            else if(string.Equals(key,"foot_locker",StringComparison.OrdinalIgnoreCase)) slot.Rotation=new Vec(0f,90f,0f);
            else if(string.Equals(key,"leg_snowmobile",StringComparison.OrdinalIgnoreCase)) slot.Rotation=new Vec(90f,0f,0f);
            else if(string.Equals(key,"leg_locker",StringComparison.OrdinalIgnoreCase)) slot.Rotation=new Vec(90f,0f,0f);
            else if(string.Equals(key,"legbeam",StringComparison.OrdinalIgnoreCase)) slot.Rotation=new Vec(0f,0f,0f);
        }
        private bool TryUiFloat(string raw,out float value) {
            value=0f;
            if(string.IsNullOrWhiteSpace(raw))return false;
            raw=raw.Trim().Replace(',','.');
            return float.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out value);
        }
        private void ChangePrefab(BasePlayer user,int dir) {
            var b=B(user);var edit=E(user);var s=b.Slots[edit.Selected];
            string[] keys=CandidateKeysForSlot(edit.Selected); if(keys.Length==0){edit.Message="Prefab候補がありません";return;}
            int i=Array.IndexOf(keys,s.PrefabKey);if(i<0)i=0;
            i=(i+dir+keys.Length)%keys.Length;
            string key=keys[i];
            s.PrefabKey=key;s.SourceKind=SourceForKey(key);
            s.ShapeId=key=="gib018"?"ScrapHeliGibs_Part018":"";
            if(IsFootSlot(edit.Selected)||IsLegSegmentSlot(edit.Selected))ApplyPresetRotationIfNeeded(s,key);
            string reason;
            bool canSpawn = CanSpawn(key,out reason);
            edit.Message=canSpawn?"Prefab候補 "+key+"：生成候補。再生成で反映"
                :"Prefab候補 "+key+"："+reason+"（仮部品表示）";
        }
        private float SignedAngle360(float value) {
            return Mathf.DeltaAngle(0f,Mathf.Repeat(value,360f));
        }
        private void FixLegacyTorsoYaw(BasePlayer user) {
            if(user==null)return;
            var b=B(user);
            Slot torso;
            if(!b.Slots.TryGetValue("TORSO",out torso)||torso==null||torso.Rotation==null){Say(user,"TORSOデータがありません");return;}
            float legacy=SignedAngle360(torso.Rotation.Y);
            torso.Rotation.Y=0f;
            b.WaistYaw=Mathf.Clamp(b.WaistYaw+legacy,-_config.WaistYawLimit,_config.WaistYawLimit);
            // Also migrate a saved full stand pose, otherwise /kmb pose load would restore the old bad local-Y twist.
            if(b.StandPose!=null&&b.StandPose.Slots!=null) {
                SlotState standTorso;
                if(b.StandPose.Slots.TryGetValue("TORSO",out standTorso)&&standTorso!=null&&standTorso.Rotation!=null) {
                    float standLegacy=SignedAngle360(standTorso.Rotation.Y);
                    standTorso.Rotation.Y=0f;
                    b.StandPose.WaistYaw=Mathf.Clamp(b.StandPose.WaistYaw+standLegacy,-_config.WaistYawLimit,_config.WaistYawLimit);
                }
            }
            Vec legacyStand;
            if(b.StandRotations!=null&&b.StandRotations.TryGetValue("TORSO",out legacyStand)&&legacyStand!=null) {
                float standLegacy=SignedAngle360(legacyStand.Y);
                legacyStand.Y=0f;
                b.StandWaistYaw=Mathf.Clamp(b.StandWaistYaw+standLegacy,-_config.WaistYawLimit,_config.WaistYawLimit);
            }
            UpdatePreview(user);SaveData();
            Say(user,"旧『腰上』TORSO Y回転 "+legacy.ToString("0.0",CultureInfo.InvariantCulture)+"° を正しい腰上旋回へ移行しました。立ち姿勢とwindupposeは念のため保存し直してね");
        }

        [ChatCommand("kmb")]
        private void ChatKmb(BasePlayer user,string command,string[] args) {
            if(!Allowed(user)){Say(user,"管理者専用です");return;}
            var action=args==null||args.Length==0?"open":args[0].ToLowerInvariant();
            if(action=="free") {HandleFreeChat(user,args.Skip(1).ToArray());return;}
            if(action=="gib") {HandleGibChat(user,args.Skip(1).ToArray());return;}
            if(action=="open"&&B(user).FreeOnly){DrawFreeUi(user);return;}
            if(action=="classic") {EnableClassic(user);return;}
            if(action=="close"){E(user).UiOpen=false;CuiHelper.DestroyUi(user,UiRoot);return;}
            if(action=="edit") {SetDirectEdit(user,!E(user).DirectEdit);return;}
            if(action=="ui") { if(E(user).DirectEdit)SetDirectEdit(user,false); DrawUi(user); return; }
            if(action=="help") {Say(user,"/kmb free = 自由組み立て ( /kmb free help ) | /kmb classic = 従来13部位");Say(user,"/kmb (UI) | /kmb edit | /kmb preview | /kmb pilot mount|dismount|status | /kmb seat here|reset|pos x y z|rot x y z | /kmb pose save|load | /kmb winduppose save right|left | /kmb punchpose save right|left | /kmb punch right|left|stop | /kmb punchmirror right|left | /kmb autopunch ... | /kmb markers | /kmb mirror right|left");Say(user,"v0.3.6.1: v0.3.6で重複していたOnPlayerDisconnectedを統合。降車後Pilot解除・実搭乗確認・Space降車の修正内容はそのまま維持。");return;}
            if(action=="waistfix") {FixLegacyTorsoYaw(user);return;}
            if(action=="pilot") {
                string sub=args.Length>1?args[1].ToLowerInvariant():"status";
                Preview pp;_previews.TryGetValue(user.userID,out pp);
                if(sub=="mount"||sub=="on"){MountPilotSeat(user);return;}
                if(sub=="dismount"||sub=="off"){
                    if(pp!=null&&pp.Pilot==user){
                        StopPunchTest(user.userID,true,false);
                        if(user.isMounted){
                            try{user.PauseFlyHackDetection(1.0f);}catch{}
                            user.DismountObject();
                        }
                        NextTick(()=>{
                            if(user!=null&&pp.Pilot==user&&!IsActuallyMountedOnPilotSeat(user,pp))
                                ClearPilotRuntime(pp,user,true,true);
                        });
                    } else Say(user,"操縦席には乗っていません");
                    return;
                }
                if(pp==null){Say(user,"機体なし。/kmb preview で生成してね");return;}
                Say(user,"操縦席="+(pp.PilotSeat!=null&&!pp.PilotSeat.IsDestroyed?"OK":"なし")+" / Pilot="+(pp.Pilot!=null?pp.Pilot.displayName:"なし")+" / W/S移動 A/D旋回 Shift加速 左Click=左Punch 右Click=右Punch Space=降車");return;
            }
            if(action=="seat") {
                if(args.Length<2){Say(user,"/kmb seat here | forward | reset | pos X Y Z | rot X Y Z | spawn | mount");return;}
                string sub=args[1].ToLowerInvariant();var bp=B(user);Preview pp;_previews.TryGetValue(user.userID,out pp);
                if(sub=="here"){SetPilotSeatHere(user);return;}
                if(sub=="forward"){if(bp.PilotSeatRotation==null)bp.PilotSeatRotation=new Vec();bp.PilotSeatRotation.Y=0f;SaveData();if(pp!=null&&!pp.PilotHierarchyActive)UpdatePilotSeatPose(pp,bp,true);Say(user,"操縦席のYawをロボ正面へ戻しました");return;}
                if(sub=="reset"){bp.PilotSeatPosition=new Vec(0f,0.35f,-0.15f);bp.PilotSeatRotation=new Vec();SaveData();if(pp!=null)UpdatePilotSeatPose(pp,bp,true);Say(user,"操縦席位置を初期値へ戻しました");return;}
                if(sub=="spawn"){if(pp==null){Say(user,"先に /kmb preview");return;}if(pp.PilotSeat!=null&&!pp.PilotSeat.IsDestroyed)pp.PilotSeat.Kill();pp.PilotSeat=null;SpawnPilotSeat(user,pp,bp);Say(user,pp.PilotSeat!=null?"操縦席を再生成しました":"操縦席生成に失敗しました");return;}
                if(sub=="mount"){MountPilotSeat(user);return;}
                if((sub=="pos"||sub=="rot")&&args.Length>=5){float x,y,z;if(!TryUiFloat(args[2],out x)||!TryUiFloat(args[3],out y)||!TryUiFloat(args[4],out z)){Say(user,"数値を指定してね");return;}if(sub=="pos")bp.PilotSeatPosition=new Vec(x,y,z);else bp.PilotSeatRotation=new Vec(x,y,z);SaveData();if(pp!=null)UpdatePilotSeatPose(pp,bp,true);Say(user,"操縦席 "+sub+" = "+x.ToString("0.##",CultureInfo.InvariantCulture)+", "+y.ToString("0.##",CultureInfo.InvariantCulture)+", "+z.ToString("0.##",CultureInfo.InvariantCulture));return;}
                Say(user,"/kmb seat here | forward | reset | pos X Y Z | rot X Y Z | spawn | mount");return;
            }
            if(action=="catalog") {
                Say(user,"候補キー: "+string.Join(", ",CatalogKeys()));
                Say(user,_mechBossSyncStatus+"。車両系は固定化して試験表示、Bradley本体は生成禁止");
                return;
            }
            if(action=="sync") {SyncMechBossCatalog();E(user).Message=_mechBossSyncStatus;Say(user,_mechBossSyncStatus);DrawUi(user);return;}
            if(action=="symmetry") {var ed=E(user);ed.Symmetry=!ed.Symmetry;Say(user,"左右同期 "+(ed.Symmetry?"ON":"OFF"));if(ed.DirectEdit)DrawDirectHud(user);return;}
            if(action=="markers") {
                var ed=E(user);
                if(args.Length>1) {
                    string mv=args[1].ToLowerInvariant();
                    if(mv=="on"||mv=="1"||mv=="true")ed.MarkersVisible=true;
                    else if(mv=="off"||mv=="0"||mv=="false")ed.MarkersVisible=false;
                    else ed.MarkersVisible=!ed.MarkersVisible;
                } else ed.MarkersVisible=!ed.MarkersVisible;
                Say(user,"編集マーカー "+(ed.MarkersVisible?"ON":"OFF")+"（/kmb markers on|off）");
                if(ed.DirectEdit){DrawDirectHud(user);if(ed.MarkersVisible)DrawDirectMarkers(user);}
                return;
            }
            if(action=="mirror"&&args.Length>1) {
                bool right=string.Equals(args[1],"right",StringComparison.OrdinalIgnoreCase)||string.Equals(args[1],"r",StringComparison.OrdinalIgnoreCase)||args[1]=="右";
                bool left=string.Equals(args[1],"left",StringComparison.OrdinalIgnoreCase)||string.Equals(args[1],"l",StringComparison.OrdinalIgnoreCase)||args[1]=="左";
                if(!right&&!left){Say(user,"使い方: /kmb mirror right  （右→左） または /kmb mirror left （左→右）");return;}
                CopySideMirror(B(user),right);UpdatePreview(user);SaveData();Say(user,right?"右側を左側へミラーコピー":"左側を右側へミラーコピー");return;
            }
            if(action=="scale"&&args.Length>1) {
                float v;if(!TryUiFloat(args[1],out v)){Say(user,"使い方: /kmb scale 1.5");return;}v=Mathf.Clamp(v,0.20f,4.0f);
                var ed=E(user);var slot=B(user).Slots[ed.Selected];slot.VisualScale=v;string pair=MirrorId(ed.Selected);if(ed.Symmetry&&!string.IsNullOrEmpty(pair)&&B(user).Slots.ContainsKey(pair))B(user).Slots[pair].VisualScale=v;
                UpdatePreviewBranch(user,ed.Selected,true);if(ed.Symmetry&&!string.IsNullOrEmpty(pair))UpdatePreviewBranch(user,pair,true);SaveData();Say(user,ed.Selected+" サイズ x"+v.ToString("0.00",CultureInfo.InvariantCulture));return;
            }
            if(action=="legsize"&&args.Length>1) {
                float v;if(!TryUiFloat(args[1],out v)){Say(user,"使い方: /kmb legsize 1.5");return;}v=Mathf.Clamp(v,0.20f,4.0f);var bp=B(user);
                foreach(var id in new[]{"R_THIGH","R_SHIN","R_FOOT","L_THIGH","L_SHIN","L_FOOT"})bp.Slots[id].VisualScale=v;
                UpdatePreview(user);SaveData();Say(user,"両脚6部位のサイズを x"+v.ToString("0.00",CultureInfo.InvariantCulture)+" に設定");return;
            }
            if(action=="punchmirror") {
                if(args.Length<2){Say(user,"使い方: /kmb punchmirror right  （右→左） または /kmb punchmirror left （左→右）");return;}
                string side=args[1].ToLowerInvariant();
                bool right=side=="right"||side=="r"||args[1]=="右";
                bool left=side=="left"||side=="l"||args[1]=="左";
                if(!right&&!left){Say(user,"right または left を指定してね");return;}
                var bp=B(user);
                bool changed=MirrorPunchRegistration(bp,right,true);
                if(changed){SaveData();Say(user,right?"右パンチ設定を左へミラー登録しました":"左パンチ設定を右へミラー登録しました");}
                else Say(user,(right?"右":"左")+"側にミラー元のパンチ設定がありません");
                return;
            }
            if(action=="autopunch") {
                if(args.Length<2){ShowAutoPunchStatus(user);return;}
                string sub=args[1].ToLowerInvariant();
                if(sub=="on"||sub=="1"||sub=="true"){SetAutoPunch(user,true);return;}
                if(sub=="off"||sub=="0"||sub=="false"){SetAutoPunch(user,false);return;}
                if(sub=="status"){ShowAutoPunchStatus(user);return;}
                if(sub=="mode") {
                    if(args.Length<3){Say(user,"使い方: /kmb autopunch mode right|left|alternate");return;}
                    string m=args[2].ToLowerInvariant();
                    if(m!="right"&&m!="left"&&m!="alternate"){Say(user,"mode は right / left / alternate");return;}
                    var bp=B(user);bp.AutoPunchMode=m;SaveData();
                    if(bp.AutoPunchEnabled)StartAutoPunchRuntime(user,false);
                    Say(user,"自動パンチ mode="+m);return;
                }
                if(sub=="interval") {
                    if(args.Length<3){Say(user,"使い方: /kmb autopunch interval 3");return;}
                    float sec;if(!TryUiFloat(args[2],out sec)){Say(user,"秒数を指定してね");return;}
                    var bp=B(user);bp.AutoPunchIntervalSeconds=Mathf.Clamp(sec,1.8f,30f);SaveData();
                    Say(user,"自動パンチ間隔="+bp.AutoPunchIntervalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒");return;
                }
                Say(user,"/kmb autopunch on|off|status | mode right|left|alternate | interval 3");return;
            }
            if(action=="winduppose") {
                if(args.Length<3){Say(user,"使い方: /kmb winduppose save right|left  または /kmb winduppose clear right|left");return;}
                string mode=args[1].ToLowerInvariant();
                string side=args[2].ToLowerInvariant();
                bool right=side=="right"||side=="r"||args[2]=="右";
                bool left=side=="left"||side=="l"||args[2]=="左";
                if(!right&&!left){Say(user,"right または left を指定してね");return;}
                if(mode=="save"){SaveWindupPose(user,right);return;}
                if(mode=="clear"||mode=="delete"){ClearWindupPose(user,right);return;}
                Say(user,"使い方: /kmb winduppose save right|left  または /kmb winduppose clear right|left");return;
            }
            if(action=="punchpose") {
                if(args.Length<3){Say(user,"使い方: /kmb punchpose save right|left  または /kmb punchpose clear right|left");return;}
                string mode=args[1].ToLowerInvariant();
                string side=args[2].ToLowerInvariant();
                bool right=side=="right"||side=="r"||args[2]=="右";
                bool left=side=="left"||side=="l"||args[2]=="左";
                if(!right&&!left){Say(user,"right または left を指定してね");return;}
                if(mode=="save"){SavePunchPose(user,right);return;}
                if(mode=="clear"||mode=="delete"){ClearPunchPose(user,right);return;}
                Say(user,"使い方: /kmb punchpose save right|left  または /kmb punchpose clear right|left");return;
            }
            if(action=="punch") {
                if(args.Length>1&&string.Equals(args[1],"stop",StringComparison.OrdinalIgnoreCase)){StopPunchTest(user.userID,true,true);return;}
                bool right=true;
                if(args.Length>1) {
                    string side=args[1].ToLowerInvariant();
                    if(side=="left"||side=="l"||args[1]=="左")right=false;
                    else if(side=="right"||side=="r"||args[1]=="右")right=true;
                    else {Say(user,"使い方: /kmb punch right  または /kmb punch left  /kmb punch stop");return;}
                }
                StartPunchTest(user,right);return;
            }
            if(action=="pose") {
                if(args.Length<2){Say(user,"使い方: /kmb pose save  または /kmb pose load");return;}
                string mode=args[1].ToLowerInvariant();
                if(mode=="save"){SaveStandPose(user);return;}
                if(mode=="load"||mode=="restore"){RestoreStandPose(user);return;}
                Say(user,"使い方: /kmb pose save  または /kmb pose load");return;
            }
            if(action=="test") {
                if(args.Length>1&&string.Equals(args[1],"stop",StringComparison.OrdinalIgnoreCase)){StopMotionTest(user.userID,true);Say(user,"関節動作テスト停止");return;}
                string id=E(user).Grabbed&&E(user).GrabKind=="JOINT"?E(user).GrabId:E(user).Selected;
                int ix=1;if(args.Length>1&&B(user).Slots.ContainsKey(args[1].ToUpperInvariant())){id=args[1].ToUpperInvariant();ix=2;}
                string axis=ix<args.Length?args[ix]:DefaultMotionAxis(id);float amp=35f;float speed=0.65f;
                if(ix+1<args.Length)TryUiFloat(args[ix+1],out amp);if(ix+2<args.Length)TryUiFloat(args[ix+2],out speed);
                StartMotionTest(user,id,axis,amp,speed);return;
            }
            if(action=="ground") {
                Preview p;
                if(!_previews.TryGetValue(user.userID,out p)){Say(user,"先に /kmb preview で一括生成");return;}
                Say(user,GroundReport(B(user),p,"R_FOOT"));
                Say(user,GroundReport(B(user),p,"L_FOOT"));
                return;
            }
            if(action=="status") {
                Preview preview;
                if(!_previews.TryGetValue(user.userID,out preview)){Say(user,"プレビューなし。/kmb preview で一括生成");return;}
                Say(user,"生成済み部位: "+preview.Entities.Count+" / 13（形状の見た目は要確認）");
                Say(user,"操縦席: "+(preview.PilotSeat!=null&&!preview.PilotSeat.IsDestroyed?"OK":"なし")+" / Pilot="+(preview.Pilot!=null?preview.Pilot.displayName:"なし"));
                Say(user,"自動パンチ: "+(B(user).AutoPunchEnabled?"ON":"OFF")+" / "+B(user).AutoPunchMode+" / "+B(user).AutoPunchIntervalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒");
                foreach(var id in Ids) {
                    string result;
                    if(preview.Results.TryGetValue(id,out result))Say(user,id+": "+result);
                }
                return;
            }
            if(action=="set"&&args.Length>2) {
                string id=args[1].ToUpperInvariant();
                string key=args[2].ToLowerInvariant();
                if(!B(user).Slots.ContainsKey(id)||!_catalog.ContainsKey(key)){Say(user,"使い方: /kmb set TORSO cockpit （/kmb catalog で候補確認）");return;}
                var part=B(user).Slots[id]; part.PrefabKey=key;
                part.SourceKind=SourceForKey(key);
                part.ShapeId=key=="gib018"?"ScrapHeliGibs_Part018":"";
                SaveData();Say(user,id+" を "+key+" に設定。次の一括生成で反映");DrawUi(user);return;
            }
            if(action=="pos"&&args.Length>2) {
                string axis=args[1].ToLowerInvariant(); float v;
                if((axis!="x"&&axis!="y"&&axis!="z")||!TryUiFloat(args[2],out v)){Say(user,"使い方: /kmb pos x 1.25  （x/y/z）");return;}
                var edit=E(user); var slot=B(user).Slots[edit.Selected]; v=Mathf.Clamp(v,-20f,20f);
                if(axis=="x")slot.Position.X=v; else if(axis=="y")slot.Position.Y=v; else slot.Position.Z=v;
                UpdatePreview(user); SaveData(); Say(user,edit.Selected+" 位置 "+axis.ToUpperInvariant()+" = "+v.ToString("0.###",CultureInfo.InvariantCulture)); return;
            }
            if(action=="rot"&&args.Length>2) {
                string axis=args[1].ToLowerInvariant(); float v;
                if((axis!="x"&&axis!="y"&&axis!="z")||!TryUiFloat(args[2],out v)){Say(user,"使い方: /kmb rot y 45  （x/y/z）");return;}
                var edit=E(user); var slot=B(user).Slots[edit.Selected];
                if(string.Equals(edit.Selected,"TORSO",StringComparison.OrdinalIgnoreCase)&&axis=="y") {
                    float signed=Mathf.DeltaAngle(0f,Mathf.Repeat(v,360f));
                    B(user).WaistYaw=Mathf.Clamp(signed,-_config.WaistYawLimit,_config.WaistYawLimit);
                    UpdatePreview(user);SaveData();Say(user,"腰上旋回 Y = "+B(user).WaistYaw.ToString("0.###",CultureInfo.InvariantCulture));return;
                }
                v=Mathf.Repeat(v,360f);
                if(axis=="x")slot.Rotation.X=v; else if(axis=="y")slot.Rotation.Y=v; else slot.Rotation.Z=v;
                UpdatePreview(user); SaveData(); Say(user,edit.Selected+" 回転 "+axis.ToUpperInvariant()+" = "+v.ToString("0.###",CultureInfo.InvariantCulture)); return;
            }
                        if(action=="save") {SaveData();Say(user,"設計図を保存");return;}
            if(action=="remove") {RemovePreview(user.userID);Say(user,"この作業場のプレビューを撤去");return;}
            if(action=="preview") {BuildPreview(user);Say(user,E(user).Message);DrawUi(user);return;}
            if(action=="name"&&args.Length>1) {
                string title=string.Join(" ",args.Skip(1).ToArray()).Trim();
                if(title.Length==0){Say(user,"名前を入力してね");return;}
                B(user).Name=title.Substring(0,Math.Min(32,title.Length));
                SaveData();Say(user,"名前を変更しました");DrawUi(user);return;
            }
            if(action=="reset") {if(B(user).FreeOnly){Say(user,"自由制作中は初期構成リセットを使えません。/kmb classic で切り替えてください");return;}RemovePreview(user.userID);var keep=B(user);var fresh=DefaultBlueprint();fresh.FreeParts=keep.FreeParts;fresh.FreeSerial=keep.FreeSerial;
                _data.Builders[user.UserIDString]=fresh;SaveData();Say(user,"従来13部位の構成を初期化。自由制作データは維持しました");DrawUi(user);return;}
            if(E(user).DirectEdit) {E(user).DirectEdit=false;E(user).Grabbed=false;CuiHelper.DestroyUi(user,DirectUiRoot);}
            DrawUi(user);
        }
        [ConsoleCommand("kmb.ui")]
        private void ConsoleKmb(ConsoleSystem.Arg arg) {
            var user=arg.Player();if(!Allowed(user))return;
            string[] a=arg.Args==null?new string[0]:arg.Args.Select(x=>x.ToString()).ToArray();
            if(a.Length==0){DrawUi(user);return;}
            var edit=E(user);var b=B(user);var action=a[0];
            if(action=="close") {edit.UiOpen=false;CuiHelper.DestroyUi(user,UiRoot);return;}
            if(action=="direct") {SetDirectEdit(user,true);return;}
            if(action=="select"&&a.Length>1&&b.Slots.ContainsKey(a[1]))edit.Selected=a[1];
            else if(action=="prefab"&&a.Length>1) ChangePrefab(user,a[1]=="next"?1:-1);
            else if(action=="step") {
                float[] v={0.01f,0.05f,0.10f,0.25f,0.50f}; int idx=Array.FindIndex(v,x=>Mathf.Approximately(x,edit.MoveStep)); edit.MoveStep=v[(idx+1+v.Length)%v.Length];
            }
            else if(action=="stepvalue"&&a.Length>1) { float v; if(TryUiFloat(a[1],out v))edit.MoveStep=Mathf.Clamp(v,0.005f,1f); }
            else if(action=="angle") {
                float[] v={0.5f,1f,5f,15f,45f}; int idx=Array.FindIndex(v,x=>Mathf.Approximately(x,edit.AngleStep)); edit.AngleStep=v[(idx+1+v.Length)%v.Length];
            }
            else if(action=="anglevalue"&&a.Length>1) { float v; if(TryUiFloat(a[1],out v))edit.AngleStep=Mathf.Clamp(v,0.1f,90f); }
            else if(action=="setpos"&&a.Length>2) {
                float v; if(!TryUiFloat(a[2],out v)){edit.Message="位置の数値が読めません";} else {
                    var slot=b.Slots[edit.Selected]; v=Mathf.Clamp(v,-20f,20f);
                    if(a[1]=="x")slot.Position.X=v; if(a[1]=="y")slot.Position.Y=v; if(a[1]=="z")slot.Position.Z=v;
                    edit.Message="位置を直接入力しました"; UpdatePreview(user);
                }
            }
            else if(action=="setrot"&&a.Length>2) {
                float v; if(!TryUiFloat(a[2],out v)){edit.Message="回転の数値が読めません";} else {
                    var slot=b.Slots[edit.Selected];
                    if(string.Equals(edit.Selected,"TORSO",StringComparison.OrdinalIgnoreCase)&&a[1]=="y") {
                        b.WaistYaw=Mathf.Clamp(Mathf.DeltaAngle(0f,Mathf.Repeat(v,360f)),-_config.WaistYawLimit,_config.WaistYawLimit);
                        edit.Message="腰上旋回を直接入力しました";UpdatePreview(user);
                    } else {
                        v=Mathf.Repeat(v,360f);
                        if(a[1]=="x")slot.Rotation.X=v; if(a[1]=="y")slot.Rotation.Y=v; if(a[1]=="z")slot.Rotation.Z=v;
                        edit.Message="回転を直接入力しました"; UpdatePreview(user);
                    }
                }
            }
            else if(action=="move"&&a.Length>2) {
                int direction=a[2]=="plus"?1:-1;
                var slot=b.Slots[edit.Selected];
                if(a[1]=="x")slot.Position.X=Mathf.Clamp(slot.Position.X+direction*edit.MoveStep,-8,8);
                if(a[1]=="y")slot.Position.Y=Mathf.Clamp(slot.Position.Y+direction*edit.MoveStep,-8,8);
                if(a[1]=="z")slot.Position.Z=Mathf.Clamp(slot.Position.Z+direction*edit.MoveStep,-8,8);
                UpdatePreview(user);
            }
            else if(action=="rotate"&&a.Length>2) {
                int direction=a[2]=="plus"?1:-1;
                var slot=b.Slots[edit.Selected];
                if(string.Equals(edit.Selected,"TORSO",StringComparison.OrdinalIgnoreCase)&&a[1]=="y") {
                    b.WaistYaw=Mathf.Clamp(b.WaistYaw+direction*edit.AngleStep,-_config.WaistYawLimit,_config.WaistYawLimit);
                } else {
                    if(a[1]=="x")slot.Rotation.X=Mathf.Repeat(slot.Rotation.X+direction*edit.AngleStep,360f);
                    if(a[1]=="y")slot.Rotation.Y=Mathf.Repeat(slot.Rotation.Y+direction*edit.AngleStep,360f);
                    if(a[1]=="z")slot.Rotation.Z=Mathf.Repeat(slot.Rotation.Z+direction*edit.AngleStep,360f);
                }
                UpdatePreview(user);
            }
            else if(action=="waist"&&a.Length>1) {
                int direction=a[1]=="plus"?1:-1;
                b.WaistYaw=Mathf.Clamp(b.WaistYaw+direction*edit.AngleStep,-_config.WaistYawLimit,_config.WaistYawLimit);
                UpdatePreview(user);
            }
            else if(action=="root"&&a.Length>1) {
                int dir=a[1]=="plus"?1:-1;
                b.RootYaw=Mathf.Repeat(b.RootYaw+dir*edit.AngleStep,360f);
                UpdatePreview(user);
            }
            else if(action=="zero") {b.WaistYaw=0f;UpdatePreview(user);}
            else if(action=="save") {SaveData();edit.Message="設計図を保存";}
            else if(action=="sync") {SyncMechBossCatalog();edit.Message=_mechBossSyncStatus;}
            else if(action=="preview")BuildPreview(user);
            else if(action=="pilotmount") {MountPilotSeat(user);return;}
            else if(action=="seathere") {SetPilotSeatHere(user);return;}
            else if(action=="remove") {RemovePreview(user.userID);edit.Message="プレビューを撤去";}
            else if(action=="reset") {if(b.FreeOnly){edit.Message="自由制作中はリセット禁止。旧モードに戻してね";}else {RemovePreview(user.userID);var keep=B(user);var fresh=DefaultBlueprint();fresh.FreeParts=keep.FreeParts;fresh.FreeSerial=keep.FreeSerial;
                _data.Builders[user.UserIDString]=fresh;SaveData();edit.Message="従来13部位を初期化 (自由制作データは維持)";}}
            DrawUi(user);
        }
        private static void Rect(CuiElementContainer ui,string parent,float x0,float y0,float x1,float y1,string color) {
            ui.Add(new CuiPanel{Image={Color=color},RectTransform={AnchorMin=$"{x0.ToString(CultureInfo.InvariantCulture)} {y0.ToString(CultureInfo.InvariantCulture)}",AnchorMax=$"{x1.ToString(CultureInfo.InvariantCulture)} {y1.ToString(CultureInfo.InvariantCulture)}"}},parent);
        }
        private static void Label(CuiElementContainer ui,string parent,string text,float x0,float y0,float x1,float y1,int size=12,string color="0.93 0.96 0.99 1") {
            ui.Add(new CuiLabel{Text={Text=text,FontSize=size,Color=color,Align=TextAnchor.MiddleLeft},RectTransform={AnchorMin=$"{x0.ToString(CultureInfo.InvariantCulture)} {y0.ToString(CultureInfo.InvariantCulture)}",AnchorMax=$"{x1.ToString(CultureInfo.InvariantCulture)} {y1.ToString(CultureInfo.InvariantCulture)}"}},parent);
        }
        private static void Button(CuiElementContainer ui,string parent,string text,string command,float x0,float y0,float x1,float y1,string color="0.18 0.30 0.41 1",int size=12) {
            ui.Add(new CuiButton{Button={Color=color,Command=command},Text={Text=text,FontSize=size,Color="1 1 1 1",Align=TextAnchor.MiddleCenter},RectTransform={AnchorMin=$"{x0.ToString(CultureInfo.InvariantCulture)} {y0.ToString(CultureInfo.InvariantCulture)}",AnchorMax=$"{x1.ToString(CultureInfo.InvariantCulture)} {y1.ToString(CultureInfo.InvariantCulture)}"}},parent);
        }
        private void DrawUi(BasePlayer user) {
            if(!Allowed(user))return;
            CuiHelper.DestroyUi(user,UiRoot);
            var b=B(user);var e=E(user);
            if(b==null||b.Slots==null)return;
            if(string.IsNullOrEmpty(e.Selected)||!b.Slots.ContainsKey(e.Selected))e.Selected=Ids[0];
            e.UiOpen = true;
            var ui=new CuiElementContainer();
            var root=ui.Add(new CuiPanel{Image={Color="0.045 0.065 0.09 0.97"},RectTransform={AnchorMin="0.08 0.07",AnchorMax="0.92 0.93"},CursorEnabled=true},"Overlay",UiRoot);
            Label(ui,root,"KURO MECH BUILDER  v0.4.0 | "+b.Name,0.02f,0.935f,0.83f,0.99f,18);
            Button(ui,root,"X","kmb.ui close",0.925f,0.94f,0.985f,0.99f,"0.5 0.17 0.18 1",15);
            Label(ui,root,"左:部位選択  中央:部位図  右:位置/角度/Prefab  |  左右は機体基準",0.02f,0.885f,0.97f,0.931f,11);
            Rect(ui,root,0.015f,0.145f,0.33f,0.875f,"0.085 0.13 0.19 0.90");
            Rect(ui,root,0.343f,0.145f,0.67f,0.875f,"0.077 0.12 0.17 0.90");
            Rect(ui,root,0.684f,0.145f,0.986f,0.875f,"0.085 0.13 0.19 0.90");
            Label(ui,root,"13部位",0.03f,0.826f,0.30f,0.869f,14);
            for(int i=0;i<Ids.Length;i++){
                var id=Ids[i];float top=0.817f-i*0.050f;
                var s=b.Slots[id];
                Button(ui,root,(e.Selected==id?"● ":"  ")+Names[i]+"  ["+s.PrefabKey+"]","kmb.ui select "+id,
                    0.026f,top-0.045f,0.318f,top,e.Selected==id?"0.17 0.44 0.48 1":"0.15 0.22 0.30 1",10);
            }
            // Clickable humanoid diagram, screen x/y in UI-root coordinates.
            Button(ui,root,"頭","kmb.ui select HEAD",0.459f,0.753f,0.550f,0.816f,"0.26 0.37 0.47 1",12);
            Button(ui,root,"胴体","kmb.ui select TORSO",0.430f,0.608f,0.577f,0.745f,"0.25 0.39 0.52 1",13);
            Button(ui,root,"腰","kmb.ui select WAIST",0.454f,0.543f,0.553f,0.601f,"0.40 0.35 0.29 1",12);
            Button(ui,root,"右上腕","kmb.ui select R_UPPER_ARM",0.365f,0.643f,0.425f,0.710f,"0.23 0.33 0.42 1",9);
            Button(ui,root,"右前腕","kmb.ui select R_FOREARM",0.365f,0.548f,0.425f,0.636f,"0.23 0.33 0.42 1",9);
            Button(ui,root,"左上腕","kmb.ui select L_UPPER_ARM",0.582f,0.643f,0.645f,0.710f,"0.23 0.33 0.42 1",9);
            Button(ui,root,"左前腕","kmb.ui select L_FOREARM",0.582f,0.548f,0.645f,0.636f,"0.23 0.33 0.42 1",9);
            Button(ui,root,"右もも","kmb.ui select R_THIGH",0.437f,0.435f,0.486f,0.533f,"0.28 0.34 0.42 1",9);
            Button(ui,root,"左もも","kmb.ui select L_THIGH",0.520f,0.435f,0.570f,0.533f,"0.28 0.34 0.42 1",9);
            Button(ui,root,"右すね","kmb.ui select R_SHIN",0.437f,0.309f,0.486f,0.423f,"0.28 0.34 0.42 1",9);
            Button(ui,root,"左すね","kmb.ui select L_SHIN",0.520f,0.309f,0.570f,0.423f,"0.28 0.34 0.42 1",9);
            Button(ui,root,"右足","kmb.ui select R_FOOT",0.420f,0.258f,0.488f,0.300f,"0.30 0.36 0.42 1",9);
            Button(ui,root,"左足","kmb.ui select L_FOOT",0.519f,0.258f,0.586f,0.300f,"0.30 0.36 0.42 1",9);
            Button(ui,root,"操縦席へ搭乗","kmb.ui pilotmount",0.352f,0.178f,0.502f,0.229f,"0.18 0.43 0.30 1",10);
            Button(ui,root,"席=現在地","kmb.ui seathere",0.510f,0.178f,0.660f,0.229f,"0.28 0.35 0.46 1",10);
            var sel=b.Slots[e.Selected];string reason;
            bool available=CanSpawn(sel.PrefabKey,out reason);
            int selectedIndex=Array.IndexOf(Ids,e.Selected);
            string selectedName=selectedIndex>=0&&selectedIndex<Names.Length?Names[selectedIndex]:e.Selected;
            Label(ui,root,"選択: "+selectedName,0.697f,0.814f,0.976f,0.860f,14);
            Label(ui,root,"Prefab: "+sel.PrefabKey+(IsFootSlot(e.Selected)?"  [足専用候補]":IsLegSegmentSlot(e.Selected)?"  [脚専用候補]":"")+"  サイズx"+sel.VisualScale.ToString("0.00",CultureInfo.InvariantCulture),0.697f,0.761f,0.97f,0.807f,11);
            Button(ui,root,"◀ 前","kmb.ui prefab prev",0.699f,0.704f,0.826f,0.754f);
            Button(ui,root,"次 ▶","kmb.ui prefab next",0.840f,0.704f,0.972f,0.754f);
            Label(ui,root,available?"候補: 試験生成可能":"保留: "+reason,0.699f,0.651f,0.852f,0.697f,9,available?"0.55 0.90 0.72 1":"1 0.75 0.44 1");
            Button(ui,root,"MB再読込","kmb.ui sync",0.858f,0.655f,0.974f,0.694f,"0.32 0.27 0.48 1",9);
            Label(ui,root,"位置 X="+sel.Position.X.ToString("0.00",CultureInfo.InvariantCulture)+"  Y="+sel.Position.Y.ToString("0.00",CultureInfo.InvariantCulture)+"  Z="+sel.Position.Z.ToString("0.00",CultureInfo.InvariantCulture),0.699f,0.606f,0.976f,0.647f,10);
            Label(ui,root,"移動刻み "+e.MoveStep.ToString("0.###",CultureInfo.InvariantCulture)+"m",0.699f,0.565f,0.825f,0.604f,10);
            Button(ui,root,"刻み","kmb.ui step",0.826f,0.565f,0.880f,0.604f,size:9);
            Button(ui,root,"0.01","kmb.ui stepvalue 0.01",0.883f,0.565f,0.927f,0.604f,size:8);
            Button(ui,root,"0.05","kmb.ui stepvalue 0.05",0.930f,0.565f,0.976f,0.604f,size:8);
            DrawAxis(ui,root,0.511f,"move");
            Label(ui,root,"直接指定: /kmb pos x 1.25   （x/y/z）",0.699f,0.468f,0.976f,0.505f,9,"0.62 0.84 1 1");
            Label(ui,root,"回転 X="+sel.Rotation.X.ToString("0.0",CultureInfo.InvariantCulture)+"  Y="+sel.Rotation.Y.ToString("0.0",CultureInfo.InvariantCulture)+"  Z="+sel.Rotation.Z.ToString("0.0",CultureInfo.InvariantCulture),0.699f,0.428f,0.976f,0.465f,10);
            Label(ui,root,"回転刻み "+e.AngleStep.ToString("0.0",CultureInfo.InvariantCulture)+"°",0.699f,0.391f,0.825f,0.426f,10);
            Button(ui,root,"刻み","kmb.ui angle",0.826f,0.391f,0.880f,0.426f,size:9);
            Button(ui,root,"1°","kmb.ui anglevalue 1",0.883f,0.391f,0.927f,0.426f,size:8);
            Button(ui,root,"5°","kmb.ui anglevalue 5",0.930f,0.391f,0.976f,0.426f,size:8);
            DrawAxis(ui,root,0.342f,"rotate");
            Label(ui,root,"直接指定: /kmb rot y 45   （x/y/z）",0.699f,0.299f,0.976f,0.336f,9,"0.62 0.84 1 1");
            Label(ui,root,"腰より上だけ左右旋回  "+b.WaistYaw.ToString("0.0",CultureInfo.InvariantCulture)+"°",0.694f,0.252f,0.98f,0.293f,10);
            Button(ui,root,"◀ -","kmb.ui waist minus",0.701f,0.214f,0.780f,0.254f);
            Button(ui,root,"0°","kmb.ui zero",0.795f,0.214f,0.885f,0.254f);
            Button(ui,root,"+ ▶","kmb.ui waist plus",0.898f,0.214f,0.977f,0.254f);
            Label(ui,root,"機体向き "+b.RootYaw.ToString("0.0",CultureInfo.InvariantCulture)+"°",0.697f,0.171f,0.842f,0.211f,10);
            Button(ui,root,"◀","kmb.ui root minus",0.847f,0.171f,0.904f,0.211f);
            Button(ui,root,"▶","kmb.ui root plus",0.914f,0.171f,0.974f,0.211f);
            Button(ui,root,"直接編集","kmb.ui direct",0.021f,0.065f,0.182f,0.132f,"0.12 0.48 0.52 1",13);
            Button(ui,root,"自由制作", "kmb.freeui open",0.780f,0.890f,0.920f,0.933f,"0.25 0.40 0.27 1",11);
            Button(ui,root,"一括生成","kmb.ui preview",0.190f,0.065f,0.335f,0.132f,"0.15 0.46 0.30 1",12);
            Button(ui,root,"設計図保存","kmb.ui save",0.343f,0.065f,0.492f,0.132f,"0.18 0.40 0.55 1",12);
            Button(ui,root,"全撤去","kmb.ui remove",0.500f,0.065f,0.625f,0.132f,"0.53 0.18 0.18 1",11);
            Button(ui,root,"初期姿勢","kmb.ui zero",0.633f,0.065f,0.755f,0.132f,size:11);
            Button(ui,root,"初期構成","kmb.ui reset",0.763f,0.065f,0.982f,0.132f,"0.33 0.29 0.18 1",11);
            if(!string.IsNullOrEmpty(e.Message))Label(ui,root,e.Message,0.02f,0.015f,0.75f,0.061f,10,"0.95 0.78 0.45 1");
            Label(ui,root,_mechBossSyncStatus,0.755f,0.015f,0.98f,0.061f,9,"0.62 0.84 1 1");
            CuiHelper.AddUi(user,ui);
        }


        private Dictionary<string,FreeModule> FreeModules(BasePlayer user,bool create) {
            if(user==null)return null;
            if(_data.SavedFreeModules==null)_data.SavedFreeModules=new Dictionary<string,Dictionary<string,FreeModule>>();
            Dictionary<string,FreeModule> modules;
            if(!_data.SavedFreeModules.TryGetValue(user.UserIDString,out modules)||modules==null) {
                if(!create)return null;
                modules=new Dictionary<string,FreeModule>(StringComparer.OrdinalIgnoreCase);
                _data.SavedFreeModules[user.UserIDString]=modules;
            }
            return modules;
        }
        private void ClearFreeGroupGrab(Editor ed) {
            if(ed==null)return;
            ed.FreeGrabModuleId="";
            ed.FreeGroupStartPositions.Clear();ed.FreeGroupStartRotations.Clear();ed.FreeGroupStartScales.Clear();
            ed.FreeGroupStartCenter=Vector3.zero;
        }
        private void CaptureFreeGroupGrab(Editor ed,Blueprint b,string moduleId) {
            ClearFreeGroupGrab(ed);
            if(ed==null||b==null||b.FreeParts==null||string.IsNullOrEmpty(moduleId))return;
            var members=b.FreeParts.Values.Where(x=>x!=null&&string.Equals(x.ModuleId,moduleId,StringComparison.OrdinalIgnoreCase)).ToList();
            if(members.Count==0)return;
            ed.FreeGrabModuleId=moduleId;Vector3 sum=Vector3.zero;int count=0;
            foreach(var m in members) {
                if(m.Position==null)m.Position=new Vec();if(m.Rotation==null)m.Rotation=new Vec();
                ed.FreeGroupStartPositions[m.Id]=m.Position.Copy();ed.FreeGroupStartRotations[m.Id]=m.Rotation.Copy();ed.FreeGroupStartScales[m.Id]=m.Scale;
                sum+=m.Position.V();count++;
            }
            if(count>0)ed.FreeGroupStartCenter=sum/count;
        }
        private string SaveFreeModule(BasePlayer user,string requested,IEnumerable<string> rawIds) {
            Blueprint b=B(user);Preview p;
            if(b==null||!b.FreeOnly||b.FreeParts==null||!_previews.TryGetValue(user.userID,out p))return "先に /kmb free で作業場を開いてね";
            string name=FreeDesignName(requested);if(name.Length==0)return "モジュール名は英数字・日本語・空白・_・-のみ";
            var ids=(rawIds??Enumerable.Empty<string>()).Where(id=>!string.IsNullOrEmpty(id)&&b.FreeParts.ContainsKey(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if(ids.Count==0)return "モジュールに入れるパーツがありません";
            Quaternion rootFrame=p.RootRotation*Quaternion.Euler(0f,b.RootYaw,0f);Quaternion invRoot=Quaternion.Inverse(rootFrame);
            var localPos=new Dictionary<string,Vector3>(StringComparer.OrdinalIgnoreCase);var localRot=new Dictionary<string,Quaternion>(StringComparer.OrdinalIgnoreCase);
            Vector3 center=Vector3.zero;
            foreach(var id in ids){Vector3 wp;Quaternion wr;FreePose(b,p,id,out wp,out wr);Vector3 lp=invRoot*(wp-p.RootPosition);localPos[id]=lp;localRot[id]=invRoot*wr;center+=lp;}
            center/=Mathf.Max(1,ids.Count);
            var module=new FreeModule{Name=name,SavedUtcTicks=DateTime.UtcNow.Ticks};int serial=0;
            foreach(var id in ids) {
                var source=b.FreeParts[id];string mid="M"+(++serial).ToString("D3",CultureInfo.InvariantCulture);
                module.Parts[mid]=new FreePart{Id=mid,Name=source.Name,PrefabKey=source.PrefabKey,ParentId="",
                    Position=Vec.From(localPos[id]-center),Rotation=Vec.From(localRot[id].eulerAngles),
                    VisualPosition=new Vec(),VisualRotation=new Vec(),Scale=source.Scale,ModuleId="",NativeMesh=source.NativeMesh};
            }
            FreeModules(user,true)[name]=module;SaveData();
            return "モジュール『"+name+"』を保存（"+module.Parts.Count+"パーツ）。/kmb free module spawn "+name+" で一括生成";
        }
        private string SpawnFreeModule(BasePlayer user,string requested) {
            string name=FreeDesignName(requested);Dictionary<string,FreeModule> modules=FreeModules(user,false);FreeModule module;
            if(modules==null||!modules.TryGetValue(name,out module)||module==null||module.Parts==null||module.Parts.Count==0)return "モジュール『"+name+"』がありません";
            if(!EnsureFreeWorkshop(user,true))return "作業場を作れません: "+E(user).Message;
            Preview p;if(!_previews.TryGetValue(user.userID,out p))return "自由制作プレビューがありません";
            Blueprint b=B(user);Quaternion rootFrame=p.RootRotation*Quaternion.Euler(0f,b.RootYaw,0f);
            Ray ray=user.eyes.HeadRay();Vector3 targetWorld=ray.origin+ray.direction.normalized*8f;Vector3 targetLocal=Quaternion.Inverse(rootFrame)*(targetWorld-p.RootPosition);
            string instance="mod-"+DateTime.UtcNow.Ticks.ToString("x",CultureInfo.InvariantCulture);int added=0,native=0;
            foreach(var source in module.Parts.Values.OrderBy(x=>x!=null?x.Id:"")) {
                if(source==null)continue;string id;do{b.FreeSerial++;id="P"+b.FreeSerial.ToString("D3",CultureInfo.InvariantCulture);}while(b.FreeParts.ContainsKey(id));
                var part=new FreePart{Id=id,Name=source.Name,PrefabKey=source.PrefabKey,ParentId="",
                    Position=Vec.From(targetLocal+(source.Position??new Vec()).V()),Rotation=source.Rotation!=null?source.Rotation.Copy():new Vec(),
                    VisualPosition=new Vec(),VisualRotation=new Vec(),Scale=source.Scale,ModuleId=instance,NativeMesh=source.NativeMesh};
                b.FreeParts[id]=part;added++;
                if(IsNativePartKey(part.PrefabKey)){native++;p.Results[id]="Module Native: 高速生成待ち";}else SpawnFreePart(user,p,b,part);
            }
            SaveData();UpdateFreePreview(user);if(native>0)StartFastFreeNativeRestore(user);
            return "モジュール『"+name+"』を1個として生成（"+added+"パーツ / Native "+native+"）。Eで1部品を掴むとMODULE全体を操作";
        }
        private string HandleFreeModule(BasePlayer user,string[] args) {
            var ed=E(user);Blueprint b=B(user);Preview p;if(b==null||!b.FreeOnly)return "先に /kmb free";
            string cmd=args!=null&&args.Length>1?args[1].ToLowerInvariant():"status";
            if(cmd=="new"){if(args.Length<3)return "使い方: /kmb free module new 名前";string name=FreeDesignName(string.Join(" ",args.Skip(2).ToArray()));if(name.Length==0)return "モジュール名が不正です";ed.FreeModuleName=name;ed.FreeModuleSelection.Clear();return "モジュール選択『"+name+"』開始。パーツを狙って /kmb free module add";}
            if(cmd=="add"||cmd=="remove"){if(string.IsNullOrEmpty(ed.FreeModuleName))return "先に /kmb free module new 名前";if(!_previews.TryGetValue(user.userID,out p))return "作業場がありません";string id=FreeAimPart(user,p,b);if(string.IsNullOrEmpty(id))return "パーツへ照準を合わせてね";if(cmd=="add"){ed.FreeModuleSelection.Add(id);return id+" を追加（"+ed.FreeModuleSelection.Count+"個）";}ed.FreeModuleSelection.Remove(id);return id+" を解除（"+ed.FreeModuleSelection.Count+"個）";}
            if(cmd=="clear"){ed.FreeModuleSelection.Clear();return "モジュール選択をクリア";}
            if(cmd=="save"){if(string.IsNullOrEmpty(ed.FreeModuleName))return "先に /kmb free module new 名前";return SaveFreeModule(user,ed.FreeModuleName,ed.FreeModuleSelection);}
            if(cmd=="saveall"){if(args.Length<3)return "使い方: /kmb free module saveall 名前";return SaveFreeModule(user,string.Join(" ",args.Skip(2).ToArray()),b.FreeParts.Keys);}
            if(cmd=="spawn"){if(args.Length<3)return "使い方: /kmb free module spawn 名前";return SpawnFreeModule(user,string.Join(" ",args.Skip(2).ToArray()));}
            if(cmd=="delete"){if(args.Length<3)return "使い方: /kmb free module delete 名前";string name=FreeDesignName(string.Join(" ",args.Skip(2).ToArray()));var modules=FreeModules(user,false);if(modules==null||!modules.Remove(name))return "モジュールがありません";SaveData();return "モジュール『"+name+"』を削除";}
            if(cmd=="list"){var modules=FreeModules(user,false);if(modules==null||modules.Count==0)return "保存モジュールなし";return string.Join(" | ",modules.OrderBy(x=>x.Key).Select(x=>x.Key+"["+(x.Value!=null&&x.Value.Parts!=null?x.Value.Parts.Count:0)+"個]").ToArray());}
            if(cmd=="status")return "編集中="+(string.IsNullOrEmpty(ed.FreeModuleName)?"なし":ed.FreeModuleName)+" / 選択="+ed.FreeModuleSelection.Count+"個";
            return "module: new 名前 / add / remove / clear / save / saveall 名前 / spawn 名前 / list / delete 名前 / status";
        }
        // -------------------- v0.4.12 saved free designs and installed world models --------------------
        // A free editing preview is transient. An installed model is a separate tracked entity set
        // with its WORLD root and a snapshot of all joints. It does not depend on the owner staying online.
        private static string FreeDesignName(string raw) {
            string name=(raw??"default").Trim();
            if(name.Length==0)name="default";
            if(name.Length>32)name=name.Substring(0,32);
            if(name.Any(c=>!(char.IsLetterOrDigit(c)||c=='_'||c=='-'||c==' ')))return "";
            return name;
        }
        private static Blueprint CloneFreeBlueprint(Blueprint original) {
            if(original==null)return null;
            Blueprint copy=JsonConvert.DeserializeObject<Blueprint>(JsonConvert.SerializeObject(original));
            if(copy==null)return null;
            copy.FreeOnly=true;
            copy.FreeParts=copy.FreeParts??new Dictionary<string,FreePart>();
            if(copy.MouthJoint==null)copy.MouthJoint=new MouthJointRig();
            if(copy.MouthJoint.UpperJawParts==null)copy.MouthJoint.UpperJawParts=new List<string>();
            if(copy.MouthJoint.LowerJawParts==null)copy.MouthJoint.LowerJawParts=new List<string>();
            // Installed assemblies do not run AI punch animations or a pilot seat.
            copy.AutoPunchEnabled=false;
            return copy;
        }
        private string SaveNamedFreeDesign(BasePlayer user,string requested) {
            Blueprint current=B(user);
            if(!current.FreeOnly||current.FreeParts.Count==0)return "自由制作パーツがありません";
            string name=FreeDesignName(requested);if(name.Length==0)return "設計図名は英数字・日本語・空白・_・-のみ使用可能";
            Dictionary<string,Blueprint> designs;
            if(!_data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs)||designs==null) {
                designs=new Dictionary<string,Blueprint>(StringComparer.OrdinalIgnoreCase);_data.SavedFreeDesigns[user.UserIDString]=designs;
            }
            string mouthMigrationInfo;
            MigrateDedicatedMouthJoint(current,false,out mouthMigrationInfo);
            EnsureCreatureMotionHierarchy(current);
            Blueprint snapshot=CloneFreeBlueprint(current);
            snapshot.FreeSavedUtcTicks=DateTime.UtcNow.Ticks;
            designs[name]=snapshot;
            SaveData();return "設計図『"+name+"』を保存（"+current.FreeParts.Count+"パーツ）。UIの「保存作品一覧」から生成可能";
        }
        // Compares the serialized parts (prefab, position, rotation, scale, parent, etc.).
        // It does not assume sequential part counts mean an identical or complete design.
        private string CompareSavedFreeDesigns(BasePlayer user,string first,string second) {
            Dictionary<string,Blueprint> designs;Blueprint a,b;
            if(!_data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs)||designs==null||
                !designs.TryGetValue(first,out a)||a==null||a.FreeParts==null||
                !designs.TryGetValue(second,out b)||b==null||b.FreeParts==null)
                return "比較する設計図が見つかりません。/kmb free designs で名前を確認してね";
            int unchanged=0,changed=0,added=0,removed=0;
            var changes=new List<string>();
            foreach(var entry in a.FreeParts) {
                FreePart target;
                if(!b.FreeParts.TryGetValue(entry.Key,out target)) {removed++;if(changes.Count<8)changes.Add("-"+entry.Key);continue;}
                if(JsonConvert.SerializeObject(entry.Value)==JsonConvert.SerializeObject(target))unchanged++;
                else {changed++;if(changes.Count<8)changes.Add("変更:"+entry.Key);}
            }
            foreach(var id in b.FreeParts.Keys)if(!a.FreeParts.ContainsKey(id)) {
                added++;if(changes.Count<8)changes.Add("+"+id);
            }
            string result="『"+first+"』"+a.FreeParts.Count+"個 → 『"+second+"』"+b.FreeParts.Count+
                "個 / 完全一致:"+unchanged+" / 変更:"+changed+" / 追加:"+added+" / 削除:"+removed;
            if(changes.Count>0)result+=" / 差分="+string.Join("、",changes.ToArray());
            return result;
        }
        // UI save always creates a fresh recognizable entry; the old UI overwrote "default" every time.
        private string NextSavedFreeDesignName(BasePlayer user) {
            Dictionary<string,Blueprint> designs;
            _data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs);
            for(int i=1;i<1000000;i++) {
                string candidate="設計図"+i.ToString("D3",CultureInfo.InvariantCulture);
                if(designs==null||!designs.Keys.Any(k=>string.Equals(k,candidate,StringComparison.OrdinalIgnoreCase)))return candidate;
            }
            return "設計図"+DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        // Compare COMPLETE editor layouts, not only counts or lists of material IDs.
        // Saved timestamps and other irrelevant metadata are intentionally excluded.
        private static string FreeWorkspaceSignature(Blueprint design) {
            if(design==null||design.FreeParts==null)return "";
            var ordered=new SortedDictionary<string,FreePart>(design.FreeParts,StringComparer.Ordinal);
            return design.RootYaw.ToString("R",CultureInfo.InvariantCulture)+"|"+JsonConvert.SerializeObject(ordered);
        }
        private string FindMatchingSavedFreeDesign(BasePlayer user,Blueprint current) {
            if(current==null||current.FreeParts==null||current.FreeParts.Count==0)return "";
            Dictionary<string,Blueprint> designs;
            if(!_data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs)||designs==null)return "";
            string sig=FreeWorkspaceSignature(current);
            // Prefer recognizable manually-saved entries to automatic backups.
            foreach(var saved in designs.OrderBy(kv=>IsAutomaticFreeBackupName(kv.Key)?1:0).ThenBy(kv=>kv.Key,StringComparer.OrdinalIgnoreCase)) {
                if(saved.Value!=null&&saved.Value.FreeParts!=null&&
                    string.Equals(sig,FreeWorkspaceSignature(saved.Value),StringComparison.Ordinal))
                    return saved.Key;
            }
            return "";
        }
        private static bool IsAutomaticFreeBackupName(string name) {
            return !string.IsNullOrEmpty(name)&&
                (name.StartsWith("制作退避",StringComparison.OrdinalIgnoreCase)||
                 name.StartsWith("読込前退避",StringComparison.OrdinalIgnoreCase));
        }
        // Take a never-overwriting snapshot before switching to a different project.
        // Existing named blueprints and permanently installed models are unaffected.
        private string BackupCurrentFreeWorkspace(BasePlayer user,string prefix) {
            Blueprint current=B(user);
            if(!current.FreeOnly||current.FreeParts==null||current.FreeParts.Count==0)return "";
            // If all current placements are already saved, creating an identical backup is unnecessary.
            string matching=FindMatchingSavedFreeDesign(user,current);
            if(!string.IsNullOrEmpty(matching))return matching;
            Dictionary<string,Blueprint> designs;
            _data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs);
            string name="";
            for(int i=1;i<1000000;i++) {
                string candidate=prefix+i.ToString("D3",CultureInfo.InvariantCulture);
                if(designs==null||!designs.Keys.Any(k=>string.Equals(k,candidate,StringComparison.OrdinalIgnoreCase))) {
                    name=candidate;break;
                }
            }
            if(name.Length==0)return "";
            string outcome=SaveNamedFreeDesign(user,name);
            return outcome.StartsWith("設計図『",StringComparison.Ordinal)?name:"";
        }
        // Starts an empty independent assembly. Never touches SavedFreeDesigns or InstalledFreeModels.
        private string StartNewFreeProject(BasePlayer user) {
            Blueprint current=B(user);
            if(!current.FreeOnly)return "先に /kmb free で自由制作モードを開いてね";
            var ed=E(user);
            if(Time.realtimeSinceStartup>ed.FreeNewConfirmUntil) {
                ed.FreeNewConfirmUntil=Time.realtimeSinceStartup+12f;
                return "新しい空の作品を開始します。12秒以内にもう一度『新規制作』を押して確定。今の部品は自動退避します";
            }
            ed.FreeNewConfirmUntil=0f;
            int previous=current.FreeParts!=null?current.FreeParts.Count:0;
            string backup="";
            bool alreadySaved=previous>0&&!string.IsNullOrEmpty(FindMatchingSavedFreeDesign(user,current));
            if(previous>0) {
                backup=BackupCurrentFreeWorkspace(user,"制作退避");
                if(string.IsNullOrEmpty(backup))return "現在の作品を退避できなかったため新規制作を中止しました。データは変更していません";
            }
            // Preview disposal removes ONLY this administrator's transient editor entities.
            // No world-installed models or named blueprints are changed.
            RemovePreview(user.userID);
            current.FreeParts=new Dictionary<string,FreePart>();
            current.FreeSerial=0;
            ed.FreeSelected="";ed.FreeGrab=false;ed.FreeDirect=false;
            ed.FreeSwitchOpen=false;ed.FreeParentPick=false;
            ed.FreePartPage=0;ed.FreeClearConfirmUntil=0f;
            ed.FreeDesignListOpen=false;
            CuiHelper.DestroyUi(user,DirectUiRoot);
            SaveData();
            // Workshop may be recreated by normal editing next; failure doesn't undo saved work.
            bool ready=EnsureFreeWorkshop(user,false);
            string result="新規制作開始: 0パーツ";
            if(previous>0)result+=alreadySaved
                ?" / 前の"+previous+"パーツは『"+backup+"』に保存済み（重複退避なし）"
                :" / 前の"+previous+"パーツを『"+backup+"』として退避済み";
            if(!ready)result+=" / 作業場は /kmb free workshop で再作成してね";
            return result;
        }
        private List<KeyValuePair<string,Blueprint>> SavedFreeDesignList(BasePlayer user) {
            Dictionary<string,Blueprint> designs;
            if(!_data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs)||designs==null)
                return new List<KeyValuePair<string,Blueprint>>();
            bool showBackups=E(user).FreeShowBackupDesigns;
            return designs.Where(kv=>kv.Value!=null&&kv.Value.FreeParts!=null&&
                    (showBackups||!IsAutomaticFreeBackupName(kv.Key)))
                .OrderByDescending(kv=>kv.Value.FreeSavedUtcTicks)
                .ThenBy(kv=>kv.Key,StringComparer.OrdinalIgnoreCase).ToList();
        }
        private bool TrySavedFreeDesignByIndex(BasePlayer user,string rawIndex,out string name) {
            name="";int index;
            if(!int.TryParse(rawIndex,NumberStyles.Integer,CultureInfo.InvariantCulture,out index))return false;
            var list=SavedFreeDesignList(user);
            if(index<0||index>=list.Count)return false;
            name=list[index].Key;
            return true;
        }
        private static string SavedFreeSample(Blueprint saved) {
            if(saved==null||saved.FreeParts==null)return "内容なし";
            var parts=saved.FreeParts.Values.Where(x=>x!=null).ToList();
            var keys=parts.Select(x=>!string.IsNullOrEmpty(x.PrefabKey)?x.PrefabKey:"不明")
                .GroupBy(x=>x).OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key,StringComparer.OrdinalIgnoreCase)
                .Take(3).Select(g=>g.Key+(g.Count()>1?" x"+g.Count():""));
            return string.Join(" / ",keys.ToArray());
        }
        private static string SavedFreeDate(Blueprint saved) {
            if(saved==null||saved.FreeSavedUtcTicks<=0)return "旧保存（日時記録なし）";
            try { return new DateTime(saved.FreeSavedUtcTicks,DateTimeKind.Utc).AddHours(9).ToString("yyyy/MM/dd HH:mm",CultureInfo.InvariantCulture)+" JST"; }
            catch { return "旧保存（日時記録なし）"; }
        }
        // Spawn from a saved snapshot without replacing the editor or overwriting the source blueprint.
        // Same-name installations are idempotent, including during Native queued restoration.
        private string InstallSavedFreeDesign(BasePlayer user,string name) {
            Dictionary<string,Blueprint> designs;Blueprint source;
            if(!_data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs)||designs==null||
                !designs.TryGetValue(name,out source)||source==null||source.FreeParts==null||source.FreeParts.Count==0)
                return "保存済み設計図が見つかりません";
            if(_config.PauseInstalledAutoRestoration)
                return "自動復元は停止中です。重複確認後に /kmb free resume で再開してください";
            int natives=source.FreeParts.Values.Count(x=>x!=null&&IsNativePartKey(x.PrefabKey));
            if(natives>0&&(!_config.EnableNativeScrapGibs||!_config.ExperimentalNativeWithoutHeli))
                return "Native生成には EnableNativeScrapGibs=true と /kmb gib noheli on が必要です";
            var existing=OwnedInstalledModels(user).FirstOrDefault(kv=>
                string.Equals(kv.Value.Name,name,StringComparison.OrdinalIgnoreCase));
            if(!string.IsNullOrEmpty(existing.Key))
                return "『"+name+"』は既に設置済み（ID "+InstalledModelShortId(existing.Key)+"）。連打による二重生成を防ぎました";
            Vector3 forward=user.eyes.HeadForward();forward.y=0f;
            if(forward.sqrMagnitude<0.01f)forward=Vector3.forward;
            forward.Normalize();
            Vector3 origin=user.transform.position+forward*_config.SpawnDistance;
            float terrainY=TerrainMeta.HeightMap!=null?TerrainMeta.HeightMap.GetHeight(origin):user.transform.position.y;
            origin.y=Mathf.Max(terrainY,user.transform.position.y);
            string safety;
            if(!SafeArea(user,origin,out safety,true))return "生成位置を変更してください: "+safety;
            var placed=new InstalledFreeModel {
                Name=name,StableId=BuildStableInstalledId(user.UserIDString,name),CreatedUtcTicks=DateTime.UtcNow.Ticks,
                RootPosition=Vec.From(origin),RootRotation=Vec.From(Quaternion.LookRotation(forward).eulerAngles),
                Blueprint=CloneFreeBlueprint(source)
            };
            string installKey;
            do{installKey=user.UserIDString+":"+Guid.NewGuid().ToString("N").Substring(0,12);}
            while(_data.InstalledFreeModels.ContainsKey(installKey));
            _data.InstalledFreeModels[installKey]=placed;
            SaveData();RefreshInstalledDuplicateSkips();
            try { BuildInstalledFreeRuntime(installKey,placed);StartInstalledSpawnTimers(); }
            catch(Exception ex) {
                PrintWarning("KMB saved design installation: "+ex.Message);
                RemoveInstalledFreeRuntime(installKey);
                _data.InstalledFreeModels.Remove(installKey);
                SaveData();RefreshInstalledDuplicateSkips();
                return "生成処理に失敗しました。設計図はそのまま残っています";
            }
            return "『"+name+"』を前方に生成開始（"+source.FreeParts.Count+"パーツ、うちNative "+natives+"個）。同名の重複設置は禁止";
        }

        private string LoadNamedFreeDesign(BasePlayer user,string requested) {
            string name=FreeDesignName(requested);if(name.Length==0)return "設計図名が不正です";
            Dictionary<string,Blueprint> designs;Blueprint saved;
            if(!_data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs)||designs==null||
                !designs.TryGetValue(name,out saved)||saved==null)return "設計図『"+name+"』がありません";
            // Preserve the active workspace before replacing it; older builds silently lost
            // modifications when 'Select' loaded another saved design.
            Blueprint current=B(user);
            string backup="";
            bool alreadySaved=current.FreeOnly&&current.FreeParts!=null&&current.FreeParts.Count>0&&
                !string.IsNullOrEmpty(FindMatchingSavedFreeDesign(user,current));
            if(current.FreeOnly && current.FreeParts!=null && current.FreeParts.Count>0) {
                backup=BackupCurrentFreeWorkspace(user,"読込前退避");
                if(string.IsNullOrEmpty(backup))return "現在の作品の自動退避に失敗したため読み込みを中止しました";
            }
            // Loading never touches installed world objects. It replaces ONLY the editor's free model.
            RemovePreview(user.userID);
            _data.Builders[user.UserIDString]=CloneFreeBlueprint(saved);
            var ed=E(user);ed.FreeSelected="";ed.FreeGrab=false;ed.FreeSwitchOpen=false;
            ed.FreeNewConfirmUntil=0f;
            SaveData();
            if(!EnsureFreeWorkshop(user,false))return "設計図を読み込みましたが、作業場を作れません: "+ed.Message;
            StartFastFreeNativeRestore(user);
            return "設計図『"+name+"』を全体読み込み（"+saved.FreeParts.Count+"パーツ）"+
                (backup.Length>0?(alreadySaved?"。以前の制作内容は『"+backup+"』に保存済み（重複退避なし）":"。直前の制作内容は『"+backup+"』に退避"):"")+
                "。Nativeプレビューは必要に応じて手動抽出してね";
        }
        private void ListFreeDesigns(BasePlayer user) {
            Dictionary<string,Blueprint> designs;
            if(!_data.SavedFreeDesigns.TryGetValue(user.UserIDString,out designs)||designs==null||designs.Count==0){Say(user,"保存済み設計図なし。/kmb free save 名前");return;}
            foreach(var kv in designs.OrderBy(x=>x.Key))Say(user,kv.Key+" / "+(kv.Value!=null&&kv.Value.FreeParts!=null?kv.Value.FreeParts.Count:0)+"パーツ");
        }
        // Legacy v0.4.12 installed models were keyed by owner SteamID. Keep them readable.
        // v0.4.13 uses ownerSteamID:uniqueId, allowing any number of installed assemblies.
        private bool IsInstalledOwnerKey(string key,string owner) {
            return string.Equals(key,owner,StringComparison.Ordinal)||
                   (key!=null&&key.StartsWith(owner+":",StringComparison.Ordinal));
        }
        private List<KeyValuePair<string,InstalledFreeModel>> OwnedInstalledModels(BasePlayer user) {
            string owner=user.UserIDString;
            return _data.InstalledFreeModels.Where(kv=>IsInstalledOwnerKey(kv.Key,owner)&&kv.Value!=null)
                .OrderBy(kv=>kv.Value.CreatedUtcTicks).ThenBy(kv=>kv.Key,StringComparer.Ordinal).ToList();
        }
        private string InstalledModelShortId(string key) {
            InstalledFreeModel model;
            if(_data!=null&&_data.InstalledFreeModels!=null&&_data.InstalledFreeModels.TryGetValue(key,out model)&&model!=null){
                if(string.IsNullOrEmpty(model.StableId)) model.StableId=BuildStableInstalledId(InstalledOwnerId(key),model.Name);
                return model.StableId;
            }
            int at=key.LastIndexOf(':');return at>=0?key.Substring(at+1):"旧作品";
        }
        private static string InstalledOwnerId(string key) {
            if(string.IsNullOrEmpty(key))return "";
            int split=key.IndexOf(':');return split<0?key:key.Substring(0,split);
        }
        private static string BuildStableInstalledId(string owner,string name) {
            string input=(owner??"")+"|"+(name??"default").Trim().ToLowerInvariant();
            ulong hash=1469598103934665603UL;
            unchecked { for(int i=0;i<input.Length;i++){ hash^=(ushort)input[i]; hash*=1099511628211UL; } }
            return "kmb-"+(hash&0xffffffffUL).ToString("x8",CultureInfo.InvariantCulture);
        }
        private void EnsureInstalledStableIds() {
            if(_data==null||_data.InstalledFreeModels==null)return;
            bool changed=false;
            foreach(var kv in _data.InstalledFreeModels){
                if(kv.Value==null)continue;
                string expected=BuildStableInstalledId(InstalledOwnerId(kv.Key),kv.Value.Name);
                if(string.IsNullOrEmpty(kv.Value.StableId)){kv.Value.StableId=expected;changed=true;}
            }
            if(changed)SaveData();
        }
        private bool TryResolveInstalledModel(BasePlayer user,string selector,out KeyValuePair<string,InstalledFreeModel> result,out string error) {
            result=default(KeyValuePair<string,InstalledFreeModel>);error="";
            string s=(selector??"").Trim();
            if(s.Length==0){error="作品名または固定IDを指定してね";return false;}
            var owned=OwnedInstalledModels(user);
            var matches=owned.Where(kv=>
                string.Equals(InstalledModelShortId(kv.Key),s,StringComparison.OrdinalIgnoreCase)||
                string.Equals(kv.Key,s,StringComparison.OrdinalIgnoreCase)||
                string.Equals(kv.Value.Name??"",s,StringComparison.OrdinalIgnoreCase)).ToList();
            if(matches.Count==1){result=matches[0];return true;}
            if(matches.Count==0){error="作品『"+s+"』が見つかりません。作品名をそのまま使うか /kmb free installed で確認してね";return false;}
            error="同じ作品名が複数あります。固定IDを指定してね: "+string.Join(", ",matches.Select(x=>InstalledModelShortId(x.Key)).Distinct().Take(6).ToArray());
            return false;
        }
        // Stable representation of all parts, independent of Dictionary insertion order and world root.
        private static string InstalledShapeSignature(InstalledFreeModel model) {
            if(model==null||model.Blueprint==null||model.Blueprint.FreeParts==null)return "";
            var sorted=new SortedDictionary<string,FreePart>(model.Blueprint.FreeParts,StringComparer.Ordinal);
            return JsonConvert.SerializeObject(sorted);
        }
        private static string InstalledDuplicateKey(string key,InstalledFreeModel model) {
            return InstalledOwnerId(key)+"|"+(model.Name??"default").ToLowerInvariant()+"|"+InstalledShapeSignature(model);
        }
        // Does not change saved data: old duplicate entries stay visible for manual review/cleanup.
        private int RefreshInstalledDuplicateSkips() {
            _installedDuplicateSkipped.Clear();
            var first=new HashSet<string>(StringComparer.Ordinal);
            foreach(var kv in _data.InstalledFreeModels.Where(x=>x.Value!=null)
                .OrderBy(x=>x.Value.CreatedUtcTicks).ThenBy(x=>x.Key,StringComparer.Ordinal)) {
                string fingerprint=InstalledDuplicateKey(kv.Key,kv.Value);
                if(!first.Add(fingerprint))_installedDuplicateSkipped.Add(kv.Key);
            }
            return _installedDuplicateSkipped.Count;
        }
        private void StopInstalledSpawnTimers() {
            if(_installedRestoreTimer!=null){_installedRestoreTimer.Destroy();_installedRestoreTimer=null;}
            if(_installedRepairTimer!=null){_installedRepairTimer.Destroy();_installedRepairTimer=null;}
        }
        private void StartInstalledSpawnTimers() {
            if(_config.PauseInstalledAutoRestoration)return;
            if(_installedRestoreTimer==null)_installedRestoreTimer=timer.Every(_config.FastNativeBurstSpawn?_config.FastNativeBurstIntervalSeconds:0.75f,RestoreOneInstalledFreePart);
            if(_installedRepairTimer==null)_installedRepairTimer=timer.Every(_config.InstalledRepairCheckSeconds,RepairInstalledFreeParts);
        }
        private string SetInstalledSpawnPause(bool pause) {
            _config.PauseInstalledAutoRestoration=pause;
            Config.WriteObject(_config,true);
            if(pause){
                StopInstalledSpawnTimers();
                return "永久設置の自動生成・自動修復を停止しました（配置済み実体と設計図はそのまま）。再開: /kmb free resume";
            }
            // Only previously missing / skipped runtimes are initialized. No rebuild of healthy parts.
            int skipped=RefreshInstalledDuplicateSkips();
            foreach(var kv in _data.InstalledFreeModels.ToArray()) {
                if(_installedDuplicateSkipped.Contains(kv.Key))continue;
                // Never pose-lock or repair an installed model while its vehicle root is moving.
                // That maintenance path writes saved world transforms and can visibly fight swim motion.
                if(_installedSwimmers.ContainsKey(kv.Key))continue;
                if(!_installedPreviews.ContainsKey(kv.Key)){
                    try{BuildInstalledFreeRuntime(kv.Key,kv.Value);}
                    catch(Exception ex){PrintWarning("KMB resume "+kv.Key+": "+ex.Message);}
                }
            }
            StartInstalledSpawnTimers();
            return "永久設置の自動復元を再開しました。重複保存 "+skipped+" 件はスキップ。";
        }
        private string ListDuplicateInstalledFree(BasePlayer user) {
            int duplicates=RefreshInstalledDuplicateSkips();
            var own=OwnedInstalledModels(user);
            int mine=own.Count(x=>_installedDuplicateSkipped.Contains(x.Key));
            if(mine==0)return "同じ名前・同じパーツ構成の重複作品はありません。全体: "+duplicates+"件";
            var ids=own.Where(x=>_installedDuplicateSkipped.Contains(x.Key))
                .Select(x=>InstalledModelShortId(x.Key)).Take(12).ToArray();
            return "重複 "+mine+"件（自動復元は停止済み）: "+string.Join(", ",ids)+
                (mine>12?" ...":"")+"。撤去するなら /kmb free cleanup を12秒以内に2回（重複分だけ削除）";
        }
        private string CleanupDuplicateInstalledFree(BasePlayer user) {
            RefreshInstalledDuplicateSkips();
            var duplicates=OwnedInstalledModels(user).Where(x=>_installedDuplicateSkipped.Contains(x.Key)).ToList();
            if(duplicates.Count==0)return "削除対象の重複作品はありません";
            var ed=E(user);
            if(Time.realtimeSinceStartup>ed.FreeDuplicateCleanupConfirmUntil) {
                ed.FreeDuplicateCleanupConfirmUntil=Time.realtimeSinceStartup+12f;
                return "同じ名前・同じ構成の重複 "+duplicates.Count+"作品を削除しますか？12秒以内に /kmb free cleanup を再実行（最古の1作品と設計図は残す）";
            }
            ed.FreeDuplicateCleanupConfirmUntil=0f;
            foreach(var kv in duplicates){
                _data.InstalledFreeModels.Remove(kv.Key);
                RemoveInstalledFreeRuntime(kv.Key);
                _installedAttempts.Remove(kv.Key);
            }
            SaveData();RefreshInstalledDuplicateSkips();
            return "重複 "+duplicates.Count+"作品の保存記録・管理中実体を削除。最古の1作品と設計図は保持しました";
        }
        private void ListInstalledFree(BasePlayer user) {
            var list=OwnedInstalledModels(user);
            if(list.Count==0){Say(user,"永久設置作品なし");return;}
            RefreshInstalledDuplicateSkips();
            Say(user,"永久設置 "+list.Count+"作品 / 重複復元停止 "+list.Count(x=>_installedDuplicateSkipped.Contains(x.Key))+"作品 / 自動復元 "+(_config.PauseInstalledAutoRestoration?"停止中":"ON"));
            Say(user,"撤去: /kmb free unplace 作品ID（二度確認） / 重複確認: /kmb free duplicates");
            foreach(var entry in list) {
                Preview runtime;_installedPreviews.TryGetValue(entry.Key,out runtime);
                int shown=runtime!=null?runtime.FreeEntities.Values.Count(e=>e!=null&&!e.IsDestroyed):0;
                int total=entry.Value.Blueprint!=null&&entry.Value.Blueprint.FreeParts!=null?entry.Value.Blueprint.FreeParts.Count:0;
                Say(user,"固定ID="+InstalledModelShortId(entry.Key)+" 『"+entry.Value.Name+"』 実体 "+shown+"/"+total+
                    (_installedDuplicateSkipped.Contains(entry.Key)?" [重複のため復元停止]":""));
            }
        }
        private string InstallFreeDesign(BasePlayer user,string requestedName) {
            var current=B(user);
            if(!current.FreeOnly||current.FreeParts.Count==0)return "先に自由制作でパーツを配置してね";
            Preview edit;
            if(!_previews.TryGetValue(user.userID,out edit)||edit==null)return "先に /kmb free workshop で作業場を作ってね";
            string name=FreeDesignName(string.IsNullOrEmpty(requestedName)?"default":requestedName);
            if(name.Length==0)return "作品名が不正です";
            int natives=current.FreeParts.Values.Count(x=>x!=null&&IsNativePartKey(x.PrefabKey));
            if(natives>0&&(!_config.EnableNativeScrapGibs||!_config.ExperimentalNativeWithoutHeli))
                return "Native永久復元にはEnableNativeScrapGibs=true と /kmb gib noheli on が必要です";
            if(_config.PauseInstalledAutoRestoration)
                return "永久設置の自動生成が一時停止中。/kmb free duplicates で重複確認後、/kmb free resume で再開してね";
            // Name is the install idempotency key: the UI uses 'default', so repeated clicks,
            // even with a slightly changed preview pose, cannot silently create more models.
            var already=OwnedInstalledModels(user).FirstOrDefault(x=>
                string.Equals(x.Value.Name,name,StringComparison.OrdinalIgnoreCase));
            if(!string.IsNullOrEmpty(already.Key))
                return "作品名『"+name+"』は設置済み（ID="+InstalledModelShortId(already.Key)+"）。二重登録はしません。新規作品は /kmb free install 別名 を使ってね";
            InstalledFreeModel saved=new InstalledFreeModel{
                Name=name,StableId=BuildStableInstalledId(user.UserIDString,name),CreatedUtcTicks=DateTime.UtcNow.Ticks,
                RootPosition=Vec.From(edit.RootPosition),RootRotation=Vec.From(edit.RootRotation.eulerAngles),
                Blueprint=CloneFreeBlueprint(current)
            };
            string installKey;
            do { installKey=user.UserIDString+":"+Guid.NewGuid().ToString("N").Substring(0,12); }
            while(_data.InstalledFreeModels.ContainsKey(installKey));
            _data.InstalledFreeModels[installKey]=saved;
            SaveNamedFreeDesign(user,name);
            SaveData();
            RefreshInstalledDuplicateSkips();
            if(!_config.PauseInstalledAutoRestoration){
                BuildInstalledFreeRuntime(installKey,saved);
                StartInstalledSpawnTimers();
            }
            // The edit preview is separate; leave all OTHER installed models untouched.
            RemovePreview(user.userID);
            return "作品『"+name+"』を永久設置（ID="+InstalledModelShortId(installKey)+" / "+current.FreeParts.Count+"パーツ）。同名の再設置は防止。/kmb free installed で確認";
        }
        // Gallery operation: remove installed WORLD instances of this saved blueprint, never the blueprint.
        // Pre-v0.4.14 installations may contain several identical-name entries; remove them together
        // so the design becomes spawnable again. A two-click confirmation is required per name.
        private string UnplaceSavedFreeDesign(BasePlayer user,string name) {
            var matching=OwnedInstalledModels(user)
                .Where(kv=>kv.Value!=null&&string.Equals(kv.Value.Name,name,StringComparison.OrdinalIgnoreCase))
                .ToList();
            var ed=E(user);
            if(matching.Count==0) {
                ed.FreeDesignUnplacePendingName="";
                ed.FreeDesignUnplaceConfirmUntil=0f;
                return "『"+name+"』は設置されていません。『生成』から新規設置できます";
            }
            if(Time.realtimeSinceStartup>ed.FreeDesignUnplaceConfirmUntil||
                !string.Equals(ed.FreeDesignUnplacePendingName,name,StringComparison.OrdinalIgnoreCase)) {
                ed.FreeDesignUnplacePendingName=name;
                ed.FreeDesignUnplaceConfirmUntil=Time.realtimeSinceStartup+12f;
                return "『"+name+"』の設置済み "+matching.Count+"件を撤去します。12秒以内に同じ『撤去』ボタンをもう一度押して確定。設計図は残ります";
            }
            ed.FreeDesignUnplacePendingName="";
            ed.FreeDesignUnplaceConfirmUntil=0f;
            // Remove the saved installation records FIRST. Any 5-second restore tick that runs
            // afterwards cannot enqueue these parts again.
            foreach(var kv in matching)_data.InstalledFreeModels.Remove(kv.Key);
            SaveData();
            foreach(var kv in matching) {
                RemoveInstalledFreeRuntime(kv.Key); // remove actual tracked world entities
                _installedAttempts.Remove(kv.Key); // drop queued/retry counters
            }
            RefreshInstalledDuplicateSkips();
            if(_data.InstalledFreeModels.Count==0)StopInstalledSpawnTimers();
            return "『"+name+"』を "+matching.Count+"件撤去しました。設計図は保存済みなので、一覧の『生成』からもう一度置けます";
        }
        private string UnplaceFreeDesign(BasePlayer user,string requestedId="") {
            var items=OwnedInstalledModels(user);
            if(items.Count==0)return "永久設置した作品はありません";
            KeyValuePair<string,InstalledFreeModel> target=items[items.Count-1];
            string id=(requestedId??"").Trim();
            if(id.Length>0) {
                var matches=items.Where(kv=>string.Equals(InstalledModelShortId(kv.Key),id,StringComparison.OrdinalIgnoreCase)||
                    string.Equals(kv.Key,id,StringComparison.OrdinalIgnoreCase)).ToList();
                if(matches.Count!=1)return "作品IDが見つからないか曖昧です。/kmb free installed でIDを確認してね";
                target=matches[0];
            }
            var ed=E(user);
            if(Time.realtimeSinceStartup>ed.FreeUnplaceConfirmUntil||ed.FreeUnplacePendingId!=target.Key){
                ed.FreeUnplacePendingId=target.Key;
                ed.FreeUnplaceConfirmUntil=Time.realtimeSinceStartup+12f;
                return "作品『"+target.Value.Name+"』（ID="+InstalledModelShortId(target.Key)+"）を撤去しますか？12秒以内に同じ操作でもう一度確定（設計図は残ります）";
            }
            ed.FreeUnplaceConfirmUntil=0f;ed.FreeUnplacePendingId="";
            _data.InstalledFreeModels.Remove(target.Key);
            RemoveInstalledFreeRuntime(target.Key);
            _installedAttempts.Remove(target.Key);
            RefreshInstalledDuplicateSkips();
            SaveData();return "永久設置『"+target.Value.Name+"』（"+InstalledModelShortId(target.Key)+"）を撤去しました。ほかの作品と設計図は残ります";
        }
        private void RemoveInstalledFreeRuntime(string owner) {
            StopInstalledSwim(owner);
            Preview p;
            if(!_installedPreviews.TryGetValue(owner,out p))return;
            _installedPreviews.Remove(owner);
            foreach(var e in p.FreeEntities.Values) {
                var gib=e as HelicopterDebris; if(gib!=null)_nativeWatches.Remove(gib);
                if(e!=null&&!e.IsDestroyed)try{e.Kill();}catch(Exception ex){PrintWarning("Installed cleanup: "+ex.Message);}
            }
            p.FreeEntities.Clear();
        }
        private void BuildInstalledFreeRuntime(string owner,InstalledFreeModel installed) {
            if(installed==null||installed.Blueprint==null||installed.Blueprint.FreeParts==null||
                installed.RootPosition==null||installed.RootRotation==null)return;
            RemoveInstalledFreeRuntime(owner);
            var p=new Preview{RootPosition=installed.RootPosition.V(),RootRotation=Quaternion.Euler(installed.RootRotation.V())};
            _installedPreviews[owner]=p;
            foreach(var part in installed.Blueprint.FreeParts.Values.OrderBy(x=>x!=null?x.Id:"")) {
                if(part==null||IsNativePartKey(part.PrefabKey))continue;
                // SpawnFreePart handles safe entity construction, static physics and exact joint transforms.
                SpawnFreePart(null,p,installed.Blueprint,part);
            }
            Puts("KMB permanent model "+owner+" started: "+p.FreeEntities.Count+"/"+installed.Blueprint.FreeParts.Count+" immediate. Native queued.");
        }
        private void StartInstalledFreeRestoration() {
            if(_data==null||_data.InstalledFreeModels==null||_config.PauseInstalledAutoRestoration)return;
            int skipped=RefreshInstalledDuplicateSkips();
            if(skipped>0)PrintWarning("KMB: "+skipped+" duplicate installed models were NOT restored. Review /kmb free duplicates and /kmb free cleanup.");
            foreach(var kv in _data.InstalledFreeModels.ToArray()) {
                if(_installedDuplicateSkipped.Contains(kv.Key))continue;
                if(_installedPreviews.ContainsKey(kv.Key))continue; // do not rebuild a running installation
                try{BuildInstalledFreeRuntime(kv.Key,kv.Value);}
                catch(Exception ex){PrintWarning("Permanent restore "+kv.Key+": "+ex.Message);}
            }
            StartInstalledSpawnTimers();
        }
        // Grouped restoration: one ServerGib.CreateGibs call yields about 47 DIFFERENT meshes.
        // Use every suitable mesh in that batch before destroying the unused debris.
        // Only one batch is made per cooldown window; missing duplicate shapes wait for the next batch.
        private class PendingInstalledGib {
            public string Owner;
            public Preview Runtime;
            public Blueprint Blueprint;
            public FreePart Part;
            public string Mesh;
        }

        private void RestoreOneInstalledFreePart() {
            if(_config.PauseInstalledAutoRestoration||_nativeCapturing||Time.realtimeSinceStartup<_nativeNextAttempt)return;
            if(!_config.EnableNativeScrapGibs||!_config.ExperimentalNativeWithoutHeli)return;
            var pending=new List<PendingInstalledGib>();
            foreach(var entry in _data.InstalledFreeModels.ToArray()){
                if(_installedDuplicateSkipped.Contains(entry.Key))continue;InstalledFreeModel model=entry.Value;Preview preview;
                if(model==null||model.Blueprint==null||model.Blueprint.FreeParts==null||!_installedPreviews.TryGetValue(entry.Key,out preview))continue;
                foreach(var part in model.Blueprint.FreeParts.Values.OrderBy(x=>x!=null?x.Id:"")){if(part==null||!IsNativePartKey(part.PrefabKey))continue;BaseEntity old;if(preview.FreeEntities.TryGetValue(part.Id,out old)&&old!=null&&!old.IsDestroyed)continue;if(old!=null&&old.IsDestroyed)preview.FreeEntities.Remove(part.Id);Dictionary<string,int> tries;if(!_installedAttempts.TryGetValue(entry.Key,out tries)){tries=new Dictionary<string,int>();_installedAttempts[entry.Key]=tries;}int attempts;tries.TryGetValue(part.Id,out attempts);if(attempts>=3)continue;string mesh;if(!_nativeMeshes.TryGetValue(part.PrefabKey,out mesh))continue;pending.Add(new PendingInstalledGib{Owner=entry.Key,Runtime=preview,Blueprint=model.Blueprint,Part=part,Mesh=mesh});}
            }
            if(pending.Count==0)return;var type=FindNativeGibType();var method=NativeCreateMethod(type);var source=NativeGibSource(type);if(method==null||source==null){_nativeNextAttempt=Time.realtimeSinceStartup+10f;return;}
            int burstLimit=_config.FastNativeBurstSpawn?_config.FastNativeBurstMaxBatches:1;float interval=_config.FastNativeBurstSpawn?_config.FastNativeBurstIntervalSeconds:_config.InstalledNativeBatchCooldownSeconds;_nativeNextAttempt=Time.realtimeSinceStartup+interval;int totalPlaced=0;
            for(int burst=0;burst<burstLimit&&pending.Count>0;burst++){
                GameObject emitter=null;var batch=new List<HelicopterDebris>();var retained=new HashSet<HelicopterDebris>();int placedThis=0;
                try{
                    emitter=new GameObject("KMB_FastInstalledGibBurst");emitter.transform.position=pending[0].Runtime.RootPosition+Vector3.up*2f;emitter.transform.rotation=pending[0].Runtime.RootRotation;_nativeBatchCaptured.Clear();_nativeCapturing=true;object created=null;try{created=method.Invoke(null,new object[]{NativeScrapPrefab,emitter,source,Vector3.zero,0f});}finally{_nativeCapturing=false;}
                    var seq=created as IEnumerable;if(seq!=null)foreach(object value in seq){var g=AsNativeGib(value);if(g!=null&&!batch.Contains(g))batch.Add(g);}foreach(var g in _nativeBatchCaptured)if(g!=null&&!batch.Contains(g))batch.Add(g);if(batch.Count<1||batch.Count>80)break;
                    var pools=new Dictionary<string,Queue<HelicopterDebris>>(StringComparer.OrdinalIgnoreCase);foreach(var g in batch){if(g==null||g.IsDestroyed)continue;string mesh=MeshKeyOfGib(g);if(string.IsNullOrEmpty(mesh))continue;Queue<HelicopterDebris> q;if(!pools.TryGetValue(mesh,out q)){q=new Queue<HelicopterDebris>();pools[mesh]=q;}q.Enqueue(g);}
                    foreach(var request in pending.ToArray()){Queue<HelicopterDebris> q;if(!pools.TryGetValue(request.Mesh,out q)||q.Count==0)continue;var chosen=q.Dequeue();if(chosen==null||chosen.IsDestroyed)continue;try{chosen.enableSaving=false;FreezePhysics(chosen);ProtectStaticNativeGib(chosen);Vector3 pos;Quaternion rot;FreePose(request.Blueprint,request.Runtime,request.Part.Id,out pos,out rot);chosen.transform.SetPositionAndRotation(pos,rot);chosen.TransformChanged();ApplyVisualScale(chosen,request.Part.Scale);chosen.SendNetworkUpdateImmediate();if(chosen.IsDestroyed)continue;request.Runtime.FreeEntities[request.Part.Id]=chosen;request.Runtime.Results[request.Part.Id]="高速バースト復元: "+request.Mesh;TrackNativeGib(chosen,"installed-fast",request.Part.Id);retained.Add(chosen);Dictionary<string,int> tries=_installedAttempts[request.Owner];int attempts;tries.TryGetValue(request.Part.Id,out attempts);tries[request.Part.Id]=attempts+1;pending.Remove(request);placedThis++;totalPlaced++;}catch(Exception ex){PrintWarning("KMB installed burst "+request.Part.Id+": "+ex.Message);}}
                }catch(Exception ex){var root=ex is TargetInvocationException&&ex.InnerException!=null?ex.InnerException:ex;PrintWarning("KMB installed burst: "+root.GetType().Name+" "+root.Message);}
                finally{_nativeCapturing=false;foreach(var g in _nativeBatchCaptured)if(g!=null&&!batch.Contains(g))batch.Add(g);foreach(var g in batch)if(g!=null&&!g.IsDestroyed&&!retained.Contains(g))try{g.Kill();}catch{} _nativeBatchCaptured.Clear();if(emitter!=null)try{UnityEngine.Object.Destroy(emitter);}catch{}}
                if(placedThis==0)break;
            }
            if(totalPlaced==0&&pending.Count>0)CountFailedInstalledBatch(pending);
        }
        private void CountFailedInstalledBatch(List<PendingInstalledGib> pending) {
            if(pending==null||pending.Count==0)return;
            var first=pending[0];
            Dictionary<string,int> tries;
            if(!_installedAttempts.TryGetValue(first.Owner,out tries)){
                tries=new Dictionary<string,int>();_installedAttempts[first.Owner]=tries;
            }
            int count;tries.TryGetValue(first.Part.Id,out count);
            tries[first.Part.Id]=count+1;
        }
        // Installed restoration progress, shown without changing save data or creating Gibs.
        private string InstalledRestoreProgress(BasePlayer user) {
            int all=0,live=0,missing=0;
            foreach(var kv in OwnedInstalledModels(user)) {
                if(_installedDuplicateSkipped.Contains(kv.Key))continue;
                Preview preview;_installedPreviews.TryGetValue(kv.Key,out preview);
                if(kv.Value.Blueprint==null||kv.Value.Blueprint.FreeParts==null)continue;
                foreach(var part in kv.Value.Blueprint.FreeParts.Values) {
                    if(part==null)continue;
                    all++;
                    BaseEntity entity;
                    if(preview!=null&&preview.FreeEntities.TryGetValue(part.Id,out entity)&&entity!=null&&!entity.IsDestroyed)live++;
                    else missing++;
                }
            }
            return "永久設置復元 "+live+"/"+all+"、残り "+missing+"。高速バースト="+(_config.FastNativeBurstSpawn?"ON":"OFF")+" / 最大"+_config.FastNativeBurstMaxBatches+"バッチ / 間隔 "+(_config.FastNativeBurstSpawn?_config.FastNativeBurstIntervalSeconds:_config.InstalledNativeBatchCooldownSeconds).ToString("0.00",CultureInfo.InvariantCulture)+"秒。自動復元="+
                (_config.PauseInstalledAutoRestoration?"停止中 (/kmb free resume)":"稼働中");
        }
        private static readonly MethodInfo KmbSendPositionUpdateMethod = typeof(BaseNetworkable).GetMethod(
            "SendNetworkUpdate_Position", BindingFlags.Instance | BindingFlags.NonPublic);
        private void SendKmbPositionUpdate(BaseEntity entity) {
            if(entity==null||entity.IsDestroyed)return;
            try {
                if(KmbSendPositionUpdateMethod!=null) {
                    KmbSendPositionUpdateMethod.Invoke(entity,null);
                    return;
                }
            } catch(Exception ex) {
                PrintWarning("KMB position-only update failed, fallback full update: "+ex.GetType().Name);
            }
            try { entity.SendNetworkUpdate(); } catch {}
        }

        private void StabilizeMiniSwimRoot(BaseEntity root) {
            if(root==null||root.IsDestroyed)return;
            try { root.enableSaving=false; } catch {}

            // Do not let the Minicopter physics drive the model. It is only a networked
            // homing/hitbox proxy that follows the software-rigid visual assembly.
            foreach(var rb in root.GetComponentsInChildren<Rigidbody>(true)) {
                if(rb==null)continue;
                try {
                    rb.velocity=Vector3.zero;
                    rb.angularVelocity=Vector3.zero;
                    rb.useGravity=false;
                    rb.isKinematic=true;
                    // v0.4.37: keep collision detection enabled only when we want an actual
                    // projectile / rocket hit target inside the body.
                    rb.detectCollisions=_config==null||_config.SwimMiniRootHitbox;
                } catch {}
            }

            // v0.4.35 disabled every collider to avoid physical break-up. That also removed
            // the only reliable server-side hit target. Re-enable the Minicopter's native
            // collider set while keeping the Rigidbody kinematic and all damage cancelled.
            bool hitbox=_config==null||_config.SwimMiniRootHitbox;
            foreach(var col in root.GetComponentsInChildren<Collider>(true)) {
                if(col==null)continue;
                try { col.enabled=hitbox; } catch {}
            }
        }
        private bool BuildInstalledSwimMiniRig(string key,InstalledSwim motion,InstalledFreeModel model,Preview preview,out string error) {
            error=null;
            if(motion==null||model==null||model.Blueprint==null||model.Blueprint.FreeParts==null||preview==null){error="設計図/実体なし";return false;}
            motion.ParentPartIds.Clear();motion.LocalPositions.Clear();motion.LocalRotations.Clear();motion.MotionRoot=null;

            // Hybrid mode:
            // - Native Gib visuals are NEVER parented to the Minicopter. On this Rust build,
            //   network parenting causes the Minicopter to move ahead while debris visually lags behind.
            // - The visual assembly stays software-rigid at 50 Hz.
            // - The Minicopter is only an embedded homing/vehicle target and follows the same simulated root.
            Quaternion invModelRoot=Quaternion.Inverse(preview.RootRotation);
            Vector3 rootPos=preview.RootPosition+preview.RootRotation*(Vector3.up*_config.SwimMiniRootYOffset);
            if(_config.SwimMiniRootAutoCenter) {
                Vector3 sum=Vector3.zero;int count=0;
                foreach(var p in model.Blueprint.FreeParts.Values) {
                    if(p==null)continue;
                    BaseEntity pe;
                    if(preview.FreeEntities.TryGetValue(p.Id,out pe)&&pe!=null&&!pe.IsDestroyed){sum+=pe.transform.position;count++;}
                }
                if(count>0)rootPos=sum/count;
            }
            Vector3 manualOffset=(model.MiniRootOffset!=null?model.MiniRootOffset.V():Vector3.zero);
            rootPos+=preview.RootRotation*manualOffset;
            motion.MiniRootLocalOffset=invModelRoot*(rootPos-preview.RootPosition);

            BaseEntity root=null;
            try {
                // Capture every visual part relative to the authored model root.
                foreach(var part in model.Blueprint.FreeParts.Values) {
                    if(part==null)continue;
                    BaseEntity e;
                    if(!preview.FreeEntities.TryGetValue(part.Id,out e)||e==null||e.IsDestroyed){error="未生成パーツがあります: "+part.Id;throw new Exception(error);}
                    // Clean up parent state left by v0.4.33/v0.4.34 before starting hybrid motion.
                    UnparentEntityPreserveWorld(e);
                    FreezePhysics(e);
                    var gib=e as HelicopterDebris;if(gib!=null)ProtectStaticNativeGib(gib);
                    motion.LocalPositions[part.Id]=invModelRoot*(e.transform.position-preview.RootPosition);
                    motion.LocalRotations[part.Id]=invModelRoot*e.transform.rotation;
                }
                if(motion.LocalPositions.Count!=model.Blueprint.FreeParts.Count){error="固定グループのパーツ数が一致しません";throw new Exception(error);}

                root=GameManager.server.CreateEntity(MiniSwimRootPrefab,rootPos,preview.RootRotation,true);
                if(root==null){error="ミニヘリRoot生成失敗";return false;}
                root.enableSaving=false;root.Spawn();StabilizeMiniSwimRoot(root);

                motion.MotionRoot=root;
                motion.UseMiniRoot=true; // now means HybridMini target, not parented visuals
                motion.SimPosition=preview.RootPosition;
                motion.SimRotation=preview.RootRotation;
                motion.CurrentSpeed=0f;
                root.SendNetworkUpdateImmediate();
                return true;
            } catch(Exception ex) {
                motion.ParentPartIds.Clear();motion.LocalPositions.Clear();motion.LocalRotations.Clear();
                if(root!=null&&!root.IsDestroyed)try{root.Kill();}catch{}
                motion.MotionRoot=null;motion.UseMiniRoot=false;
                if(string.IsNullOrEmpty(error))error="ハイブリッドMiniRoot準備失敗: "+ex.Message;
                return false;
            }
        }
        private void ReleaseInstalledSwimMiniRig(string key,InstalledSwim motion) {
            if(motion==null||!motion.UseMiniRoot)return;
            // Hybrid mode has no visual parenting. Only remove the embedded target Minicopter.
            motion.ParentPartIds.Clear();
            motion.LocalPositions.Clear();
            motion.LocalRotations.Clear();
            if(motion.MotionRoot!=null&&!motion.MotionRoot.IsDestroyed)try{motion.MotionRoot.Kill();}catch{}
            motion.MotionRoot=null;motion.UseMiniRoot=false;
        }

        private void FreezeSwimAnchor(BaseEntity entity) {
            // v0.4.29: vehicle-root mode is intentionally disabled. Kept only for compatibility
            // with older runtime state; any leftover root is frozen before it is removed.
            if(entity==null||entity.IsDestroyed)return;
            FreezePhysics(entity);
        }
        private bool BuildInstalledSwimSoftwareRig(string key,InstalledSwim motion,InstalledFreeModel model,Preview preview,out string error) {
            error=null;
            if(motion==null||model==null||model.Blueprint==null||model.Blueprint.FreeParts==null||preview==null){error="設計図/実体なし";return false;}
            motion.ParentPartIds.Clear();
            motion.LocalPositions.Clear();
            motion.LocalRotations.Clear();
            motion.MotionRoot=null;
            Quaternion invRoot=Quaternion.Inverse(preview.RootRotation);
            foreach(var part in model.Blueprint.FreeParts.Values) {
                if(part==null)continue;
                BaseEntity e;
                if(!preview.FreeEntities.TryGetValue(part.Id,out e)||e==null||e.IsDestroyed){
                    error="未生成パーツがあります: "+part.Id;
                    motion.LocalPositions.Clear();motion.LocalRotations.Clear();return false;
                }
                try {
                    // Never parent native debris to a moving vehicle/root. Parenting proved unsafe on
                    // this Rust build and could detach/kill pieces at speed.
                    UnparentEntityPreserveWorld(e);
                    FreezePhysics(e);
                    var gib=e as HelicopterDebris;if(gib!=null)ProtectStaticNativeGib(gib);
                    motion.LocalPositions[part.Id]=invRoot*(e.transform.position-preview.RootPosition);
                    motion.LocalRotations[part.Id]=invRoot*e.transform.rotation;
                } catch(Exception ex) {
                    motion.LocalPositions.Clear();motion.LocalRotations.Clear();
                    error="安全固定準備失敗: "+ex.Message;return false;
                }
            }
            if(motion.LocalPositions.Count!=model.Blueprint.FreeParts.Count){
                error="固定グループのパーツ数が一致しません";
                motion.LocalPositions.Clear();motion.LocalRotations.Clear();return false;
            }
            motion.SimPosition=preview.RootPosition;
            motion.SimRotation=preview.RootRotation;
            motion.CurrentSpeed=0f;
            return true;
        }
        private void ReleaseInstalledSwimSoftwareRig(string key,InstalledSwim motion) {
            if(motion==null)return;
            if(motion.UseMiniRoot){ReleaseInstalledSwimMiniRig(key,motion);return;}
            // No network parent is used in safe mode, so there is nothing physical to detach/kill.
            motion.ParentPartIds.Clear();
            motion.LocalPositions.Clear();
            motion.LocalRotations.Clear();
            if(motion.MotionRoot!=null&&!motion.MotionRoot.IsDestroyed)try{motion.MotionRoot.Kill();}catch{}
            motion.MotionRoot=null;
        }
        private void StopInstalledSwim(string key) {
            InstalledSwim motion;
            if(!_installedSwimmers.TryGetValue(key,out motion))return;
            _installedSwimmers.Remove(key);
            if(motion.Timer!=null)motion.Timer.Destroy();
            ReleaseInstalledSwimSoftwareRig(key,motion);
            SaveData();
        }
        private void StepInstalledSwim(string key) {
            InstalledSwim motion;InstalledFreeModel model;Preview preview;
            if(!_installedSwimmers.TryGetValue(key,out motion)||
                !_data.InstalledFreeModels.TryGetValue(key,out model)||
                !_installedPreviews.TryGetValue(key,out preview)||model==null||model.Blueprint==null||
                model.Blueprint.FreeParts==null){StopInstalledSwim(key);return;}

            float now=Time.realtimeSinceStartup;
            motion.RateTicks++;
            if(motion.RateWindowStart<=0f)motion.RateWindowStart=now;
            float rateWindow=now-motion.RateWindowStart;
            if(rateWindow>=1f){motion.ActualHz=motion.RateTicks/rateWindow;motion.RateTicks=0;motion.RateWindowStart=now;}
            float dt=motion.LastStep>0f?Mathf.Clamp(now-motion.LastStep,0.005f,0.12f):(1f/Mathf.Max(10f,motion.UpdateHz));
            motion.LastStep=now;
            float maxDelta=Mathf.Max(0.25f,motion.Acceleration)*dt;

            Quaternion newRoot;
            BasePlayer aiTarget=null;
            float aiDistance=float.MaxValue;
            if(model.AiFacePlayers) {
                // Keep the current target while it is still valid and roughly in range. This prevents
                // two nearby players from making the craft rapidly swap headings every scan.
                if(motion.AiTargetUserId!=0UL) {
                    aiTarget=FindActivePlayerById(motion.AiTargetUserId);
                    if(aiTarget!=null) {
                        Vector3 keepDelta=aiTarget.transform.position-motion.SimPosition;keepDelta.y=0f;
                        aiDistance=keepDelta.magnitude;
                        if(aiDistance>Mathf.Clamp(model.AiDetectRadius,5f,200f)*1.15f)aiTarget=null;
                    }
                }
                if(aiTarget==null&&now-motion.LastAiTargetScan>=0.50f) {
                    motion.LastAiTargetScan=now;
                    aiTarget=FindNearestAiTarget(motion.SimPosition,Mathf.Clamp(model.AiDetectRadius,5f,200f));
                    motion.AiTargetUserId=aiTarget!=null?aiTarget.userID:0UL;
                    if(aiTarget!=null) {
                        Vector3 d=aiTarget.transform.position-motion.SimPosition;d.y=0f;aiDistance=d.magnitude;
                    }
                }
            }

            float desiredSpeed=Mathf.Abs(motion.Speed);
            if(aiTarget!=null) {
                Vector3 targetDir=aiTarget.transform.position-motion.SimPosition;targetDir.y=0f;
                aiDistance=targetDir.magnitude;

                if(!model.AiChasePlayers) {
                    // LOOK mode: body keeps its normal cruise/turn. Only the neck/head group tracks the player.
                    newRoot=Quaternion.Euler(0f,motion.TurnDegrees*dt,0f)*motion.SimRotation;
                    desiredSpeed=Mathf.Abs(motion.Speed);
                    motion.AiHoldingPosition=false;
                } else {
                    if(targetDir.sqrMagnitude>0.01f) {
                        targetDir.Normalize();
                        Vector3 desiredRootForward=targetDir*motion.Direction;
                        Quaternion desiredRoot=Quaternion.LookRotation(desiredRootForward,Vector3.up) * Quaternion.Euler(0f,-model.AiLookYawOffset,0f);
                        newRoot=Quaternion.RotateTowards(motion.SimRotation,desiredRoot,Mathf.Clamp(model.AiTurnSpeed,1f,90f)*dt);
                    } else newRoot=motion.SimRotation;
                    float stop=Mathf.Clamp(model.AiStopDistance,2f,80f);
                    float resume=Mathf.Max(stop+2f,Mathf.Clamp(model.AiResumeDistance,4f,120f));
                    if(aiDistance<=stop)motion.AiHoldingPosition=true;
                    else if(aiDistance>=resume)motion.AiHoldingPosition=false;
                    desiredSpeed=motion.AiHoldingPosition?0f:Mathf.Abs(motion.Speed);
                }
            } else {
                motion.AiTargetUserId=0UL;
                motion.AiHoldingPosition=false;
                newRoot=Quaternion.Euler(0f,motion.TurnDegrees*dt,0f)*motion.SimRotation;
            }
            motion.CurrentSpeed=Mathf.MoveTowards(motion.CurrentSpeed,desiredSpeed,maxDelta);
            Vector3 forward=newRoot*Vector3.forward;forward.y=0f;
            if(forward.sqrMagnitude<0.0001f)forward=Vector3.forward;
            forward.Normalize();
            Vector3 newPos=motion.SimPosition+forward*(motion.CurrentSpeed*motion.Direction*dt);
            if(TerrainMeta.HeightMap!=null&&TerrainMeta.HeightMap.GetHeight(newPos)>newPos.y+0.25f) {
                motion.CurrentSpeed=0f;
                PrintWarning("KMB swim stopped: model would intersect terrain, id="+InstalledModelShortId(key));
                StopInstalledSwim(key);return;
            }

            // Never repair or continue if one part disappears during motion. Stop first so the
            // normal restoration path can safely rebuild it while stationary.
            foreach(var partId in motion.LocalPositions.Keys.ToList()) {
                BaseEntity e;
                if(!preview.FreeEntities.TryGetValue(partId,out e)||e==null||e.IsDestroyed) {
                    PrintWarning("KMB swim stopped: protected part disappeared, id="+InstalledModelShortId(key)+", part="+partId);
                    StopInstalledSwim(key);return;
                }
                FreezePhysics(e);
                var gib=e as HelicopterDebris;if(gib!=null)ProtectStaticNativeGib(gib);
            }

            motion.SimPosition=newPos;motion.SimRotation=newRoot;
            preview.RootPosition=newPos;preview.RootRotation=newRoot;
            model.RootPosition=Vec.From(newPos);model.RootRotation=Vec.From(newRoot.eulerAngles);

            // LOOK AI no longer changes the body course. It only steers the selected neck/head group.
            UpdateAiNeckLook(model,motion,model.AiFacePlayers?aiTarget:null,newPos,newRoot,dt);

            // v0.4.35 HybridMini mode:
            // The Minicopter follows the exact same simulated root, but Native Gib visuals are NOT its children.
            // This keeps the homing target embedded in the body without letting vehicle-parent replication pull ahead.
            if(motion.UseMiniRoot) {
                if(motion.MotionRoot==null||motion.MotionRoot.IsDestroyed){PrintWarning("KMB hybrid mini target disappeared: "+InstalledModelShortId(key));StopInstalledSwim(key);return;}
                Vector3 miniPos=newPos+newRoot*motion.MiniRootLocalOffset;
                try {
                    motion.MotionRoot.transform.SetPositionAndRotation(miniPos,newRoot);
                    motion.MotionRoot.TransformChanged();
                    if(now-motion.LastNetworkGroupUpdate>=0.5f)try{motion.MotionRoot.UpdateNetworkGroup();}catch{}
                    SendKmbPositionUpdate(motion.MotionRoot);
                } catch(Exception ex){PrintWarning("KMB hybrid mini move failed: "+ex.Message);StopInstalledSwim(key);return;}
            }

            // Apply the same root pose to every visual piece. HybridMini and softstart both use this path.
            var changed=new List<BaseEntity>(motion.LocalPositions.Count);
            foreach(var kv in motion.LocalPositions) {
                BaseEntity e;
                if(!preview.FreeEntities.TryGetValue(kv.Key,out e)||e==null||e.IsDestroyed)continue;
                Quaternion localRot;if(!motion.LocalRotations.TryGetValue(kv.Key,out localRot))continue;
                Vector3 animatedLocalPos=kv.Value;Quaternion animatedLocalRot=localRot;
                ApplyDedicatedMouthJoint(model.Blueprint,kv.Key,now,ref animatedLocalPos,ref animatedLocalRot);
                ApplyFreeDetailMotion(model.Blueprint,motion,kv.Key,now,ref animatedLocalPos,ref animatedLocalRot);
                ApplyAiNeckLook(model,motion,kv.Key,ref animatedLocalPos,ref animatedLocalRot);
                e.transform.SetPositionAndRotation(newPos+newRoot*animatedLocalPos,newRoot*animatedLocalRot);
                e.TransformChanged();changed.Add(e);
            }
            // Position-only packets are much smaller than full snapshots. Replicate at the configured
            // swim rate (30 Hz by default) and refresh network groups only twice per second.
            float networkInterval=1f/Mathf.Clamp(motion.UpdateHz,10f,50f);
            if(now-motion.LastNetworkUpdate>=networkInterval*0.90f) {
                motion.LastNetworkUpdate=now;
                bool refreshGroups=now-motion.LastNetworkGroupUpdate>=0.5f;
                if(refreshGroups)motion.LastNetworkGroupUpdate=now;
                foreach(var e in changed) {
                    try { if(refreshGroups)e.UpdateNetworkGroup(); } catch {}
                    SendKmbPositionUpdate(e);
                }
            }
            if(now-motion.LastSave>=10f){motion.LastSave=now;SaveData();}
        }
        private static string NormalizeMotionAxis(string axis) {
            axis=(axis??"Y").Trim().ToUpperInvariant();
            return axis=="X"||axis=="Z"?axis:"Y";
        }
        private static Vector3 MotionAxisVector(string axis) {
            axis=NormalizeMotionAxis(axis);
            return axis=="X"?Vector3.right:(axis=="Z"?Vector3.forward:Vector3.up);
        }
        private FreeMotion FindFreeMotionByNames(Blueprint bp,params string[] names) {
            if(bp==null||bp.FreeMotions==null||names==null)return null;
            foreach(var wanted in names) {
                if(string.IsNullOrEmpty(wanted))continue;
                foreach(var g in bp.FreeMotions.Values)
                    if(g!=null&&string.Equals(g.Name,wanted,StringComparison.OrdinalIgnoreCase))return g;
            }
            return null;
        }
        private FreeMotion CloneMotionSettings(FreeMotion source,string name) {
            var g=new FreeMotion();
            g.Name=name;
            if(source!=null) {
                g.PivotPartId=source.PivotPartId??"";
                g.UseJointPivot=source.UseJointPivot;
                g.JointPosition=source.JointPosition==null?new Vec():source.JointPosition.Copy();
                g.JointRadius=source.JointRadius;
                g.Axis=source.Axis;
                g.Angle=source.Angle;
                g.Speed=source.Speed;
                g.Phase=source.Phase;
                g.OneWay=source.OneWay;
                g.Direction=source.Direction;
            }
            return g;
        }
        private MouthJointRig MouthRig(Blueprint bp) {
            if(bp==null)return null;
            if(bp.MouthJoint==null)bp.MouthJoint=new MouthJointRig();
            if(bp.MouthJoint.UpperJawParts==null)bp.MouthJoint.UpperJawParts=new List<string>();
            if(bp.MouthJoint.LowerJawParts==null)bp.MouthJoint.LowerJawParts=new List<string>();
            bp.MouthJoint.Axis=NormalizeMotionAxis(bp.MouthJoint.Axis);
            return bp.MouthJoint;
        }
        private bool IsMouthJointPart(Blueprint bp,string partId) {
            var rig=MouthRig(bp);
            if(rig==null||string.IsNullOrEmpty(partId))return false;
            return rig.UpperJawParts.Contains(partId)||rig.LowerJawParts.Contains(partId);
        }
        private bool TryGetLegacyMouthJoint(Blueprint bp,out Vec joint,out float radius) {
            joint=new Vec();radius=0.32f;
            if(bp==null||bp.FreeMotions==null)return false;
            string[] names={"口","mouth","jaw","上顎","下顎","upperjaw","lowerjaw"};
            foreach(var name in names) {
                var g=FindFreeMotionByNames(bp,name);
                if(g!=null&&g.UseJointPivot&&g.JointPosition!=null) {
                    joint=g.JointPosition.Copy();
                    radius=Mathf.Clamp(g.JointRadius,0.12f,0.8f);
                    return true;
                }
            }
            return false;
        }
        private bool MigrateDedicatedMouthJoint(Blueprint bp,bool force,out string info) {
            info="";
            if(bp==null||bp.FreeParts==null){info="設計図がありません";return false;}
            var rig=MouthRig(bp);
            if(!force&&rig.UpperJawParts.Count>0&&rig.LowerJawParts.Count>0) {
                info="専用MouthJointは設定済み";
                return true;
            }

            List<string> upperIds=null,lowerIds=null;
            var upper=FindFreeMotionByNames(bp,"上顎","upperjaw","upper jaw");
            var lower=FindFreeMotionByNames(bp,"下顎","lowerjaw","lower jaw");
            if(upper!=null&&lower!=null&&upper.PartIds!=null&&lower.PartIds!=null&&
               upper.PartIds.Count>0&&lower.PartIds.Count>0) {
                upperIds=upper.PartIds.Where(id=>bp.FreeParts.ContainsKey(id)).Distinct().ToList();
                lowerIds=lower.PartIds.Where(id=>bp.FreeParts.ContainsKey(id)).Distinct().ToList();
                info="既存の上顎/下顎グループから移行";
            } else {
                var mouth=FindFreeMotionByNames(bp,"口","mouth","jaw");
                if(mouth==null||mouth.PartIds==null||mouth.PartIds.Count<2) {
                    info="元の口グループが無いため自動移行できません。upper add / lower add で手動登録してね";
                    return false;
                }
                var candidates=new List<KeyValuePair<string,float>>();
                foreach(var id in mouth.PartIds.Distinct()) {
                    FreePart p;
                    if(bp.FreeParts.TryGetValue(id,out p)&&p!=null&&p.Position!=null)
                        candidates.Add(new KeyValuePair<string,float>(id,p.Position.Y));
                }
                if(candidates.Count<2){info="口パーツ位置が不足";return false;}
                candidates=candidates.OrderBy(x=>x.Value).ToList();
                int split=-1;float best=float.MinValue;
                for(int i=0;i<candidates.Count-1;i++) {
                    float gap=candidates[i+1].Value-candidates[i].Value;
                    if(gap>best){best=gap;split=i;}
                }
                if(split<0){info="上下顎の境界を判定できません";return false;}
                lowerIds=candidates.Take(split+1).Select(x=>x.Key).ToList();
                upperIds=candidates.Skip(split+1).Select(x=>x.Key).ToList();
                info="旧『口』グループを高さで自動分割";
            }

            // Never allow the same piece to belong to both jaws.
            var lowerSet=new HashSet<string>(lowerIds,StringComparer.OrdinalIgnoreCase);
            upperIds=upperIds.Where(id=>!lowerSet.Contains(id)).ToList();
            if(upperIds.Count==0||lowerIds.Count==0){info="上下顎の片方が空です";return false;}

            rig.UpperJawParts=upperIds;
            rig.LowerJawParts=lowerIds;

            Vec jp;float jr;
            if(!rig.HasJoint&&TryGetLegacyMouthJoint(bp,out jp,out jr)) {
                rig.JointPosition=jp;rig.JointRadius=jr;rig.HasJoint=true;
            }

            // Disable old jaw oscillators so the dedicated MouthJoint is the only jaw animator.
            if(bp.FreeMotions!=null) {
                foreach(var g in bp.FreeMotions.Values) {
                    if(g==null)continue;
                    string n=(g.Name??"").ToLowerInvariant();
                    if(n=="口"||n=="mouth"||n=="jaw"||n=="上顎"||n=="下顎"||n=="upperjaw"||n=="lowerjaw"||n=="upper jaw"||n=="lower jaw") {
                        g.Enabled=false;g.FollowOnly=true;
                    }
                }
            }
            info+=" / 上顎"+rig.UpperJawParts.Count+"個 / 下顎"+rig.LowerJawParts.Count+"個 / Joint="+(rig.HasJoint?"あり":"未配置");
            return true;
        }
        private void ApplyDedicatedMouthJoint(Blueprint bp,string partId,float now,ref Vector3 localPos,ref Quaternion localRot) {
            var rig=MouthRig(bp);
            if(rig==null||!rig.Enabled||!rig.HasJoint||rig.JointPosition==null||string.IsNullOrEmpty(partId))return;
            bool upper=rig.UpperJawParts.Contains(partId);
            bool lower=rig.LowerJawParts.Contains(partId);
            if(!upper&&!lower)return;

            float wave=(Mathf.Sin(now*Mathf.PI*2f*Mathf.Clamp(rig.Speed,0.03f,2f))+1f)*0.5f; // 0=closed, 1=open
            float sign=rig.Invert?-1f:1f;
            float angle=upper?(wave*Mathf.Abs(rig.UpperAngle)*-sign):(wave*Mathf.Abs(rig.LowerAngle)*sign);
            Quaternion q=Quaternion.AngleAxis(angle,MotionAxisVector(rig.Axis));
            Vector3 pivot=rig.JointPosition.V();
            localPos=pivot+q*(localPos-pivot);
            localRot=q*localRot;
        }
        private bool EnsureUpperLowerJaws(Blueprint bp,out string info) {
            info="";
            if(bp==null||bp.FreeMotions==null||bp.FreeParts==null)return false;
            var upper=FindFreeMotionByNames(bp,"上顎","upperjaw","upper jaw");
            var lower=FindFreeMotionByNames(bp,"下顎","lowerjaw","lower jaw");
            if(upper!=null&&lower!=null&&upper.PartIds!=null&&lower.PartIds!=null&&upper.PartIds.Count>0&&lower.PartIds.Count>0) {
                info="既存の上顎/下顎を使用";
                return true;
            }
            var mouth=FindFreeMotionByNames(bp,"口","mouth","jaw");
            if(mouth==null||mouth.PartIds==null||mouth.PartIds.Count<2) {
                info="元の『口』グループが無いか、パーツが2個未満";
                return false;
            }

            // Split the current creature jaw by vertical position. We choose the largest Y gap,
            // rather than a hard-coded threshold, so it survives moderate blueprint edits.
            var candidates=new List<KeyValuePair<string,float>>();
            foreach(var id in mouth.PartIds.Distinct()) {
                FreePart p;
                if(bp.FreeParts.TryGetValue(id,out p)&&p!=null&&p.Position!=null)
                    candidates.Add(new KeyValuePair<string,float>(id,p.Position.Y));
            }
            if(candidates.Count<2) {
                info="口パーツの位置情報が不足";
                return false;
            }
            candidates=candidates.OrderBy(x=>x.Value).ToList();
            int splitIndex=-1;float bestGap=float.MinValue;
            for(int i=0;i<candidates.Count-1;i++) {
                float gap=candidates[i+1].Value-candidates[i].Value;
                if(gap>bestGap){bestGap=gap;splitIndex=i;}
            }
            if(splitIndex<0) {
                info="上顎/下顎の境界を判定できません";
                return false;
            }

            var lowerIds=candidates.Take(splitIndex+1).Select(x=>x.Key).ToList();
            var upperIds=candidates.Skip(splitIndex+1).Select(x=>x.Key).ToList();
            if(lowerIds.Count==0||upperIds.Count==0) {
                info="上顎/下顎の自動分割に失敗";
                return false;
            }

            if(upper==null) {
                upper=CloneMotionSettings(mouth,"上顎");
                bp.FreeMotions["上顎"]=upper;
            }
            if(lower==null) {
                lower=CloneMotionSettings(mouth,"下顎");
                bp.FreeMotions["下顎"]=lower;
            }
            upper.PartIds=upperIds;
            lower.PartIds=lowerIds;

            // Both jaws use exactly the same visible round hinge.
            upper.UseJointPivot=mouth.UseJointPivot;
            lower.UseJointPivot=mouth.UseJointPivot;
            upper.JointPosition=mouth.JointPosition==null?new Vec():mouth.JointPosition.Copy();
            lower.JointPosition=mouth.JointPosition==null?new Vec():mouth.JointPosition.Copy();
            upper.JointRadius=mouth.JointRadius;
            lower.JointRadius=mouth.JointRadius;
            upper.Axis=NormalizeMotionAxis(mouth.Axis);
            lower.Axis=NormalizeMotionAxis(mouth.Axis);
            upper.OneWay=true;lower.OneWay=true;
            upper.Enabled=true;lower.Enabled=true;
            upper.FollowOnly=false;lower.FollowOnly=false;

            // Disable the old combined mouth rotation; otherwise both halves rotate together again.
            mouth.Enabled=false;
            mouth.FollowOnly=true;

            info="自動分割: 上顎"+upperIds.Count+"個 / 下顎"+lowerIds.Count+"個 / 境界Y="+
                ((candidates[splitIndex].Value+candidates[splitIndex+1].Value)*0.5f).ToString("0.000",CultureInfo.InvariantCulture);
            return true;
        }
        private void EnsureCreatureMotionHierarchy(Blueprint bp) {
            if(bp==null||bp.FreeMotions==null||bp.FreeMotions.Count==0)return;
            var neck=FindFreeMotionByNames(bp,"首","neck");
            var head=FindFreeMotionByNames(bp,"頭","head");
            if(neck!=null) {
                neck.FollowOnly=true;
                if(head!=null&&!string.Equals(head.Name,neck.Name,StringComparison.OrdinalIgnoreCase)) {
                    head.ParentMotion=neck.Name;
                    head.FollowOnly=true;
                }
            }
            // Jaw articulation is handled by Blueprint.MouthJoint from v0.4.47 onward.
            // Keep legacy mouth/jaw groups disabled so they cannot double-rotate the dedicated jaw parts.
            foreach(var g in bp.FreeMotions.Values) {
                if(g==null)continue;
                string n=(g.Name??"").ToLowerInvariant();
                if(n=="口"||n=="mouth"||n=="jaw"||n=="上顎"||n=="下顎"||n=="upperjaw"||n=="lowerjaw"||n=="upper jaw"||n=="lower jaw") {
                    g.Enabled=false;g.FollowOnly=true;
                }
            }
        }
        private int FreeMotionDepth(Blueprint bp,FreeMotion g) {
            if(bp==null||bp.FreeMotions==null||g==null)return 0;
            int depth=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string parent=(g.ParentMotion??"").Trim();
            while(!string.IsNullOrEmpty(parent)&&depth<16&&seen.Add(parent)) {
                FreeMotion p;
                if(!bp.FreeMotions.TryGetValue(parent,out p)||p==null)break;
                depth++;parent=(p.ParentMotion??"").Trim();
            }
            return depth;
        }
        private bool FreeMotionTreeContainsPart(Blueprint bp,FreeMotion root,string partId) {
            if(bp==null||bp.FreeMotions==null||root==null||string.IsNullOrEmpty(partId))return false;
            var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack=new Stack<FreeMotion>();stack.Push(root);
            while(stack.Count>0) {
                var g=stack.Pop();if(g==null||!seen.Add(g.Name??""))continue;
                if(g.PartIds!=null&&g.PartIds.Contains(partId))return true;
                foreach(var child in bp.FreeMotions.Values) {
                    if(child==null)continue;
                    if(string.Equals((child.ParentMotion??"").Trim(),g.Name,StringComparison.OrdinalIgnoreCase))
                        stack.Push(child);
                }
            }
            return false;
        }
        private FreeMotion FindInstalledNeckMotion(InstalledFreeModel installed) {
            if(installed==null||installed.Blueprint==null||installed.Blueprint.FreeMotions==null)return null;
            string preferred=(installed.AiNeckMotion??"").Trim();
            if(!string.IsNullOrEmpty(preferred)) {
                foreach(var g in installed.Blueprint.FreeMotions.Values)
                    if(g!=null&&string.Equals(g.Name,preferred,StringComparison.OrdinalIgnoreCase))return g;
            }
            string[] fallbacks={"首","neck","head"};
            foreach(var fallback in fallbacks) {
                foreach(var g in installed.Blueprint.FreeMotions.Values)
                    if(g!=null&&string.Equals(g.Name,fallback,StringComparison.OrdinalIgnoreCase))return g;
            }
            return null;
        }
        private bool TryGetFreeMotionPivot(FreeMotion group,InstalledSwim swim,out Vector3 pivot) {
            pivot=Vector3.zero;
            if(group==null||swim==null||group.PartIds==null)return false;
            if(group.UseJointPivot&&group.JointPosition!=null){pivot=group.JointPosition.V();return true;}
            if(!string.IsNullOrEmpty(group.PivotPartId)&&swim.LocalPositions.TryGetValue(group.PivotPartId,out pivot))return true;
            int count=0;Vector3 sum=Vector3.zero;
            foreach(var id in group.PartIds) {
                Vector3 v;
                if(swim.LocalPositions.TryGetValue(id,out v)){sum+=v;count++;}
            }
            if(count<=0)return false;
            pivot=sum/count;return true;
        }
        private void UpdateAiNeckLook(InstalledFreeModel model,InstalledSwim swim,BasePlayer target,Vector3 rootPos,Quaternion rootRot,float dt) {
            if(model==null||swim==null)return;
            var neck=FindInstalledNeckMotion(model);
            float wantedYaw=0f,wantedPitch=0f;
            Vector3 pivot;
            if(target!=null&&neck!=null&&TryGetFreeMotionPivot(neck,swim,out pivot)) {
                Vector3 targetPos=target.transform.position+Vector3.up*1.35f;
                Vector3 pivotWorld=rootPos+rootRot*pivot;
                Vector3 localDir=Quaternion.Inverse(rootRot)*(targetPos-pivotWorld);
                float horizontal=Mathf.Sqrt(localDir.x*localDir.x+localDir.z*localDir.z);
                if(localDir.sqrMagnitude>0.01f) {
                    wantedYaw=Mathf.Atan2(localDir.x,localDir.z)*Mathf.Rad2Deg+model.AiNeckYawOffset;
                    wantedPitch=-Mathf.Atan2(localDir.y,Mathf.Max(0.001f,horizontal))*Mathf.Rad2Deg;
                    if(model.AiNeckYawInvert)wantedYaw=-wantedYaw;
                    if(model.AiNeckPitchInvert)wantedPitch=-wantedPitch;
                    wantedYaw=Mathf.Clamp(Mathf.DeltaAngle(0f,wantedYaw),-Mathf.Abs(model.AiNeckYawLimit),Mathf.Abs(model.AiNeckYawLimit));
                    wantedPitch=Mathf.Clamp(wantedPitch,-Mathf.Abs(model.AiNeckPitchLimit),Mathf.Abs(model.AiNeckPitchLimit));
                }
            }
            float step=Mathf.Clamp(model.AiNeckTurnSpeed,1f,180f)*dt;
            swim.AiNeckYaw=Mathf.MoveTowardsAngle(swim.AiNeckYaw,wantedYaw,step);
            swim.AiNeckPitch=Mathf.MoveTowards(swim.AiNeckPitch,wantedPitch,step);
        }
        private void ApplyAiNeckLook(InstalledFreeModel model,InstalledSwim swim,string partId,ref Vector3 localPos,ref Quaternion localRot) {
            if(model==null||swim==null)return;
            var neck=FindInstalledNeckMotion(model);
            if(neck==null||model.Blueprint==null)return;
            bool followsNeck=FreeMotionTreeContainsPart(model.Blueprint,neck,partId)||IsMouthJointPart(model.Blueprint,partId);
            if(!followsNeck)return;
            Vector3 pivot;
            if(!TryGetFreeMotionPivot(neck,swim,out pivot))return;
            Quaternion q=Quaternion.Euler(swim.AiNeckPitch,swim.AiNeckYaw,0f);
            localPos=pivot+q*(localPos-pivot);
            localRot=q*localRot;
        }
        private void ApplyFreeDetailMotion(Blueprint bp,InstalledSwim swim,string partId,float now,ref Vector3 localPos,ref Quaternion localRot) {
            if(bp==null||bp.FreeMotions==null||bp.FreeMotions.Count==0||swim==null)return;
            EnsureCreatureMotionHierarchy(bp);
            // Child motion first, then parent motion. Example: jaw opens around the mouth hinge,
            // then the entire head/mouth assembly inherits the neck transform.
            var groups=bp.FreeMotions.Values
                .Where(g=>g!=null&&g.Enabled&&!g.FollowOnly&&FreeMotionTreeContainsPart(bp,g,partId))
                .OrderByDescending(g=>FreeMotionDepth(bp,g))
                .ToList();
            foreach(var group in groups) {
                Vector3 pivot;
                if(!TryGetFreeMotionPivot(group,swim,out pivot))continue;
                float wave=Mathf.Sin(now*Mathf.PI*2f*Mathf.Clamp(group.Speed,0.01f,10f)+group.Phase*Mathf.Deg2Rad);
                float maxAngle=Mathf.Clamp(Mathf.Abs(group.Angle),0f,180f);
                float direction=group.Direction<0f?-1f:1f;
                float angle=(group.OneWay?((wave+1f)*0.5f*maxAngle):(wave*maxAngle))*direction;
                Quaternion q=Quaternion.AngleAxis(angle,MotionAxisVector(group.Axis));
                localPos=pivot+q*(localPos-pivot);
                localRot=q*localRot;
            }
        }
        private string HandleFreeMotion(BasePlayer user,string[] args) {
            var bp=B(user);if(bp==null||!bp.FreeOnly)return "先に /kmb free で自由制作モードへ";
            if(bp.FreeMotions==null)bp.FreeMotions=new Dictionary<string,FreeMotion>(StringComparer.OrdinalIgnoreCase);
            if(args==null||args.Length<2)return "motion: new/add/remove/joint/jointclear/pivot/axis/angle/speed/phase/direction/on/off/delete / jawsplit / chain(首→頭→上顎+下顎) / parent 子 親 / unparent 子 / follow 名前 on|off / list";
            string cmd=args[1].ToLowerInvariant();
            if(cmd=="list") {
                if(bp.FreeMotions.Count==0)return "細部モーションはまだありません";
                return string.Join(" | ",bp.FreeMotions.Values.OrderBy(x=>x.Name).Select(x=>x.Name+"["+(x.Enabled?"ON":"OFF")+(x.FollowOnly?"/FOLLOW":"")+"] parts="+(x.PartIds==null?0:x.PartIds.Count)+" parent="+(string.IsNullOrEmpty(x.ParentMotion)?"-":x.ParentMotion)+" pivot="+(x.UseJointPivot?"丸JOINT":(string.IsNullOrEmpty(x.PivotPartId)?"auto":x.PivotPartId))+" axis="+NormalizeMotionAxis(x.Axis)+" dir="+(x.Direction<0f?"REV":"NORM")+" ±"+x.Angle.ToString("0.0",CultureInfo.InvariantCulture)+"° "+x.Speed.ToString("0.00",CultureInfo.InvariantCulture)+"Hz"));
            }
            if(cmd=="jawsplit") {
                string info;bool ok=MigrateDedicatedMouthJoint(bp,true,out info);
                if(ok)EnsureCreatureMotionHierarchy(bp);
                SaveData();return (ok?"顎分割OK: ":"顎分割失敗: ")+info;
            }
            if(cmd=="chain") {
                string info;EnsureUpperLowerJaws(bp,out info);
                EnsureCreatureMotionHierarchy(bp);
                SaveData();
                var neck=FindFreeMotionByNames(bp,"首","neck");
                var head=FindFreeMotionByNames(bp,"頭","head");
                var upper=FindFreeMotionByNames(bp,"上顎","upperjaw","upper jaw");
                var lower=FindFreeMotionByNames(bp,"下顎","lowerjaw","lower jaw");
                return "階層設定: "+(neck!=null?neck.Name:"首なし")+" → "+(head!=null?head.Name:"頭なし")+
                    " → 専用MouthJoint。首JOINTで頭と顎全体が追従し、上下顎は1個のMOUTH JOINTで逆方向に開閉します";
            }
            if(cmd=="parent") {
                if(args.Length<4)return "使い方: /kmb free motion parent 子グループ 親グループ";
                string childName=args[2].Trim(),parentName=args[3].Trim();
                FreeMotion child,parent;
                if(!bp.FreeMotions.TryGetValue(childName,out child)||child==null)return "子グループ『"+childName+"』がありません";
                if(!bp.FreeMotions.TryGetValue(parentName,out parent)||parent==null)return "親グループ『"+parentName+"』がありません";
                if(string.Equals(child.Name,parent.Name,StringComparison.OrdinalIgnoreCase))return "自分自身を親にはできません";
                child.ParentMotion=parent.Name;SaveData();return "『"+child.Name+"』→親『"+parent.Name+"』に設定";
            }
            if(cmd=="unparent") {
                if(args.Length<3)return "使い方: /kmb free motion unparent グループ";
                FreeMotion child;if(!bp.FreeMotions.TryGetValue(args[2].Trim(),out child)||child==null)return "グループがありません";
                child.ParentMotion="";SaveData();return "『"+child.Name+"』の親グループを解除";
            }
            if(cmd=="follow") {
                if(args.Length<4)return "使い方: /kmb free motion follow グループ on|off";
                FreeMotion fg;if(!bp.FreeMotions.TryGetValue(args[2].Trim(),out fg)||fg==null)return "グループがありません";
                fg.FollowOnly=args[3].Equals("on",StringComparison.OrdinalIgnoreCase);
                SaveData();return "『"+fg.Name+"』 FOLLOW="+(fg.FollowOnly?"ON":"OFF");
            }
            if(args.Length<3)return "グループ名を指定してね";
            string name=args[2].Trim();if(string.IsNullOrEmpty(name))return "グループ名が空です";
            FreeMotion g;bp.FreeMotions.TryGetValue(name,out g);
            if(cmd=="new") {
                if(g!=null)return name+" はすでにあります";
                g=new FreeMotion{Name=name};bp.FreeMotions[name]=g;
                EnsureCreatureMotionHierarchy(bp);
                SaveData();return "細部モーション『"+name+"』を作成。照準をパーツに合わせて /kmb free motion add "+name;
            }
            if(g==null)return "モーション『"+name+"』がありません。先に /kmb free motion new "+name;
            if(g.PartIds==null)g.PartIds=new List<string>();
            if(cmd=="joint") {
                Preview p;if(!EnsureFreeWorkshop(user,true)||!_previews.TryGetValue(user.userID,out p))return "作業場がありません。/kmb free workshop";
                StartFreeMotionJointPlace(user,g,p,bp);
                return "丸ジョイント『"+name+"』配置中: 照準で位置、E=手前、R=奥、左クリック=固定、右クリック=取消";
            }
            if(cmd=="jointclear") {g.UseJointPivot=false;SaveData();return "丸ジョイント『"+name+"』を解除。"+(!string.IsNullOrEmpty(g.PivotPartId)?("パーツ支点 "+g.PivotPartId+" に戻しました"):"自動支点に戻しました");}
            if(cmd=="delete") {var ed=E(user);if(ed.FreeJointPlace&&string.Equals(ed.FreeJointMotion,name,StringComparison.OrdinalIgnoreCase)){ed.FreeJointPlace=false;ed.FreeJointMotion="";}bp.FreeMotions.Remove(name);SaveData();return "モーション『"+name+"』を削除しました（パーツ自体は残ります）";}
            if(cmd=="on"||cmd=="off") {g.Enabled=cmd=="on";SaveData();return name+" を "+(g.Enabled?"ON":"OFF")+" にしました";}
            if(cmd=="add"||cmd=="remove"||cmd=="pivot") {
                Preview p;if(!_previews.TryGetValue(user.userID,out p))return "作業場がありません。/kmb free workshop";
                string id=FreeAimPart(user,p,bp);if(string.IsNullOrEmpty(id))return "照準の近くにパーツがありません";
                if(cmd=="add") {if(!g.PartIds.Contains(id))g.PartIds.Add(id);SaveData();return id+" を『"+name+"』へ追加（"+g.PartIds.Count+"個）";}
                if(cmd=="remove") {g.PartIds.Remove(id);if(g.PivotPartId==id)g.PivotPartId="";SaveData();return id+" を『"+name+"』から外しました";}
                g.PivotPartId=id;g.UseJointPivot=false;SaveData();return "『"+name+"』の支点を "+id+" に設定しました（丸ジョイントはOFF）";
            }
            if(args.Length<4)return "値を指定してね";
            if(cmd=="axis") {g.Axis=NormalizeMotionAxis(args[3]);SaveData();return name+" axis="+g.Axis;}
            float value;if(!float.TryParse(args[3],NumberStyles.Float,CultureInfo.InvariantCulture,out value)||float.IsNaN(value)||float.IsInfinity(value))return "数値を半角で入力してね";
            if(cmd=="angle") {g.Angle=Mathf.Clamp(Mathf.Abs(value),0f,180f);SaveData();return name+" 角度=±"+g.Angle.ToString("0.0",CultureInfo.InvariantCulture)+"°";}
            if(cmd=="speed") {g.Speed=Mathf.Clamp(Mathf.Abs(value),0.01f,10f);SaveData();return name+" 速度="+g.Speed.ToString("0.00",CultureInfo.InvariantCulture)+"Hz";}
            if(cmd=="phase") {g.Phase=Mathf.Repeat(value,360f);SaveData();return name+" 位相="+g.Phase.ToString("0",CultureInfo.InvariantCulture)+"°";}
            return "不明なmotion操作です";
        }
        private BasePlayer FindActivePlayerById(ulong userId) {
            if(userId==0UL)return null;
            foreach(var p in BasePlayer.activePlayerList) {
                if(p==null||p.IsDestroyed||!p.IsConnected||p.IsDead()||p.IsSleeping())continue;
                if(p.userID==userId)return p;
            }
            return null;
        }
        private BasePlayer FindNearestAiTarget(Vector3 origin,float radius) {
            BasePlayer best=null;float bestSqr=radius*radius;
            foreach(var p in BasePlayer.activePlayerList) {
                if(p==null||p.IsDestroyed||!p.IsConnected||p.IsDead()||p.IsSleeping())continue;
                Vector3 d=p.transform.position-origin;
                float sqr=d.sqrMagnitude;
                if(sqr>bestSqr)continue;
                best=p;bestSqr=sqr;
            }
            return best;
        }
        private FreeMotion FindInstalledUpperJawMotion(InstalledFreeModel installed) {
            if(installed==null||installed.Blueprint==null||installed.Blueprint.FreeMotions==null)return null;
            string preferred=(installed.AiUpperJawMotion??"").Trim();
            var g=FindFreeMotionByNames(installed.Blueprint,preferred,"上顎","upperjaw","upper jaw");
            return g;
        }
        private FreeMotion FindInstalledLowerJawMotion(InstalledFreeModel installed) {
            if(installed==null||installed.Blueprint==null||installed.Blueprint.FreeMotions==null)return null;
            string preferred=(installed.AiLowerJawMotion??"").Trim();
            var g=FindFreeMotionByNames(installed.Blueprint,preferred,"下顎","lowerjaw","lower jaw");
            return g;
        }
        private FreeMotion FindInstalledMouthMotion(InstalledFreeModel installed) {
            if(installed==null||installed.Blueprint==null||installed.Blueprint.FreeMotions==null)return null;
            string preferred=(installed.AiMouthMotion??"").Trim();
            if(!string.IsNullOrEmpty(preferred)) {
                foreach(var g in installed.Blueprint.FreeMotions.Values)
                    if(g!=null&&string.Equals(g.Name,preferred,StringComparison.OrdinalIgnoreCase))return g;
            }
            string[] fallbacks={"口","mouth","jaw"};
            foreach(var fallback in fallbacks) {
                foreach(var g in installed.Blueprint.FreeMotions.Values)
                    if(g!=null&&string.Equals(g.Name,fallback,StringComparison.OrdinalIgnoreCase))return g;
            }
            return null;
        }
        private string ApplyInstalledNeckPreset(InstalledFreeModel installed) {
            if(installed!=null)EnsureCreatureMotionHierarchy(installed.Blueprint);
            var g=FindInstalledNeckMotion(installed);
            if(g==null)return "首グループ未設定（/kmb free motion new 首 → add/joint → /kmb free ai 作品名 neck 首）";
            // Neck is the parent steering joint. Head and mouth descendants inherit this rotation.
            g.Enabled=false;g.FollowOnly=true;
            installed.AiNeckMotion=g.Name;
            return "首『"+g.Name+"』→頭→口 連動 / yaw±"+installed.AiNeckYawLimit.ToString("0",CultureInfo.InvariantCulture)+"° pitch±"+installed.AiNeckPitchLimit.ToString("0",CultureInfo.InvariantCulture)+"°";
        }
        private string ApplyInstalledMouthPreset(InstalledFreeModel installed) {
            if(installed==null||installed.Blueprint==null)return "作品がありません";
            string migrateInfo;
            MigrateDedicatedMouthJoint(installed.Blueprint,false,out migrateInfo);
            var rig=MouthRig(installed.Blueprint);
            if(rig==null)return "MouthJointを作れませんでした";

            rig.Axis=NormalizeMotionAxis(string.IsNullOrEmpty(installed.AiMouthAxis)?rig.Axis:installed.AiMouthAxis);
            rig.Speed=Mathf.Clamp(Mathf.Abs(installed.AiMouthSpeed),0.03f,2f);
            rig.LowerAngle=Mathf.Clamp(Mathf.Abs(installed.AiMouthAngle),0f,60f);
            rig.UpperAngle=Mathf.Clamp(Mathf.Abs(installed.AiUpperJawAngle),0f,45f);
            rig.Invert=installed.AiMouthInvert;
            rig.Enabled=true;

            EnsureCreatureMotionHierarchy(installed.Blueprint);
            return "専用MouthJoint: "+migrateInfo+" / 上顎="+rig.UpperJawParts.Count+"個 "+rig.UpperAngle.ToString("0.0",CultureInfo.InvariantCulture)+
                "° / 下顎="+rig.LowerJawParts.Count+"個 "+rig.LowerAngle.ToString("0.0",CultureInfo.InvariantCulture)+
                "° / "+rig.Speed.ToString("0.00",CultureInfo.InvariantCulture)+"Hz / axis="+rig.Axis+
                " / Joint="+(rig.HasJoint?"配置済み":"未配置");
        }
        private void StepFreeMouthJointPreview(ulong owner) {
            Preview p;
            if(!_previews.TryGetValue(owner,out p)||p==null)return;
            BasePlayer user=BasePlayer.FindByID(owner);
            if(user==null||!user.IsConnected)return;
            Blueprint bp=B(user);
            if(bp==null||!bp.FreeOnly)return;
            var rig=MouthRig(bp);
            if(rig==null||!rig.Enabled||!rig.HasJoint)return;

            Quaternion rootFrame=p.RootRotation*Quaternion.Euler(0f,bp.RootYaw,0f);
            Quaternion invRoot=Quaternion.Inverse(rootFrame);
            float now=Time.realtimeSinceStartup;

            var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var id in rig.UpperJawParts)if(!string.IsNullOrEmpty(id))ids.Add(id);
            foreach(var id in rig.LowerJawParts)if(!string.IsNullOrEmpty(id))ids.Add(id);

            foreach(var id in ids) {
                FreePart part;
                BaseEntity entity;
                if(!bp.FreeParts.TryGetValue(id,out part)||part==null)continue;
                if(!p.FreeEntities.TryGetValue(id,out entity)||entity==null||entity.IsDestroyed)continue;

                Vector3 baseWorld;Quaternion baseWorldRot;
                FreePose(bp,p,id,out baseWorld,out baseWorldRot);

                Vector3 localPos=invRoot*(baseWorld-p.RootPosition);
                Quaternion localRot=invRoot*baseWorldRot;
                ApplyDedicatedMouthJoint(bp,id,now,ref localPos,ref localRot);

                Vector3 worldPos=p.RootPosition+rootFrame*localPos;
                Quaternion worldRot=rootFrame*localRot;
                try {
                    entity.transform.SetPositionAndRotation(worldPos,worldRot);
                    entity.TransformChanged();
                    SendKmbPositionUpdate(entity);
                } catch(Exception ex) {
                    PrintWarning("MouthJoint preview "+id+": "+ex.Message);
                }
            }
        }
        private string SetFreeMouthJointPreview(BasePlayer user,bool enabled) {
            if(user==null)return "プレイヤーがありません";
            Preview p;
            if(!_previews.TryGetValue(user.userID,out p)||p==null)return "自由制作プレビューがありません";
            Blueprint bp=B(user);
            if(bp==null||!bp.FreeOnly)return "先に /kmb free";

            if(p.MouthJointPreview!=null){p.MouthJointPreview.Destroy();p.MouthJointPreview=null;}
            if(!enabled) {
                UpdateFreePreview(user);
                return "口パクTEST OFF：編集位置へ戻しました";
            }

            var rig=MouthRig(bp);
            if(rig==null)return "MouthJointがありません";
            if(!rig.HasJoint)return "先に /kmb free mouthjoint place で丸JOINTを置いてね";
            if(rig.UpperJawParts.Count==0||rig.LowerJawParts.Count==0)
                return "上顎/下顎が未登録です。upper add / lower add で登録してね";

            rig.Enabled=true;
            p.MouthJointPreview=timer.Every(0.05f,()=>StepFreeMouthJointPreview(user.userID));
            StepFreeMouthJointPreview(user.userID);
            return "口パクTEST ON：上顎"+rig.UpperJawParts.Count+"個 / 下顎"+rig.LowerJawParts.Count+
                "個 / axis="+rig.Axis+" / "+rig.Speed.ToString("0.00",CultureInfo.InvariantCulture)+"Hz";
        }

        private string HandleMouthJoint(BasePlayer user,string[] args) {
            var bp=B(user);
            if(bp==null||!bp.FreeOnly)return "先に /kmb free で自由制作モードへ";
            var rig=MouthRig(bp);
            if(rig==null)return "MouthJointを初期化できません";
            if(args==null||args.Length<2)
                return "mouthjoint: place / upper add|remove|clear / lower add|remove|clear / migrate / test on|off / axis X|Y|Z / upperangle 4 / lowerangle 10 / speed 0.12 / invert on|off / on|off / diag";

            string cmd=args[1].ToLowerInvariant();
            if(cmd=="test") {
                if(args.Length<3)return "使い方: /kmb free mouthjoint test on|off";
                string v=args[2].ToLowerInvariant();
                if(v=="on"||v=="1"||v=="true")return SetFreeMouthJointPreview(user,true);
                if(v=="off"||v=="0"||v=="false")return SetFreeMouthJointPreview(user,false);
                return "test は on または off";
            }
            if(cmd=="place"||cmd=="joint") {
                Preview p;if(!_previews.TryGetValue(user.userID,out p))return "自由制作プレビューがありません";
                if(p.MouthJointPreview!=null){p.MouthJointPreview.Destroy();p.MouthJointPreview=null;UpdateFreePreview(user);}
                var e=E(user);e.FreeGrab=false;e.FreeJointPlace=true;e.FreeJointMotion="__MOUTHJOINT__";
                float distance=6f;
                if(rig.HasJoint&&rig.JointPosition!=null) {
                    Vector3 wp=p.RootPosition+p.RootRotation*rig.JointPosition.V();
                    distance=Vector3.Distance(user.eyes.HeadRay().origin,wp);
                }
                e.FreeJointDistance=Mathf.Clamp(distance,1f,25f);
                if(!e.FreeDirect)SetFreeDirect(user,true);else DrawFreeHud(user);
                return "口Joint配置開始。中央の丸を上顎/下顎の付け根へ合わせて左クリック。E=手前 / R=奥 / 右クリック=取消";
            }
            if(cmd=="migrate") {
                string info;bool ok=MigrateDedicatedMouthJoint(bp,true,out info);
                EnsureCreatureMotionHierarchy(bp);SaveData();DrawFreeHud(user);
                return (ok?"移行OK: ":"移行失敗: ")+info;
            }
            if(cmd=="diag") {
                Preview diagPreview;
                bool testOn=_previews.TryGetValue(user.userID,out diagPreview)&&diagPreview!=null&&diagPreview.MouthJointPreview!=null;
                return "MouthJoint="+(rig.HasJoint?"配置済み":"未配置")+
                    " pos="+(rig.JointPosition==null?"-":rig.JointPosition.X.ToString("0.00",CultureInfo.InvariantCulture)+","+
                    rig.JointPosition.Y.ToString("0.00",CultureInfo.InvariantCulture)+","+rig.JointPosition.Z.ToString("0.00",CultureInfo.InvariantCulture))+
                    " / axis="+rig.Axis+" / 上顎="+string.Join(",",rig.UpperJawParts.ToArray())+
                    " / 下顎="+string.Join(",",rig.LowerJawParts.ToArray())+
                    " / angles="+rig.UpperAngle.ToString("0.0",CultureInfo.InvariantCulture)+"/"+rig.LowerAngle.ToString("0.0",CultureInfo.InvariantCulture)+
                    " / speed="+rig.Speed.ToString("0.00",CultureInfo.InvariantCulture)+" / invert="+(rig.Invert?"ON":"OFF")+
                    " / TEST="+(testOn?"ON":"OFF");
            }
            if(cmd=="on"){rig.Enabled=true;SaveData();return "MouthJoint ON";}
            if(cmd=="off"){rig.Enabled=false;SaveData();return "MouthJoint OFF";}
            if(cmd=="invert") {
                if(args.Length<3)return "使い方: /kmb free mouthjoint invert on|off";
                string v=args[2].ToLowerInvariant();
                if(v=="on"||v=="true"||v=="1"||v=="反転")rig.Invert=true;
                else if(v=="off"||v=="false"||v=="0"||v=="通常")rig.Invert=false;
                else return "invert は on または off";
                SaveData();return "MouthJoint invert="+(rig.Invert?"ON":"OFF");
            }
            if(cmd=="axis") {
                if(args.Length<3)return "使い方: /kmb free mouthjoint axis X|Y|Z";
                rig.Axis=NormalizeMotionAxis(args[2]);SaveData();return "MouthJoint axis="+rig.Axis;
            }
            if(cmd=="upperangle"||cmd=="lowerangle"||cmd=="speed") {
                if(args.Length<3)return "数値を指定してね";
                float v;if(!float.TryParse(args[2],NumberStyles.Float,CultureInfo.InvariantCulture,out v)||float.IsNaN(v)||float.IsInfinity(v))return "数値を半角で指定してね";
                if(cmd=="upperangle"){rig.UpperAngle=Mathf.Clamp(Mathf.Abs(v),0f,45f);SaveData();return "上顎角="+rig.UpperAngle.ToString("0.0",CultureInfo.InvariantCulture)+"°";}
                if(cmd=="lowerangle"){rig.LowerAngle=Mathf.Clamp(Mathf.Abs(v),0f,60f);SaveData();return "下顎角="+rig.LowerAngle.ToString("0.0",CultureInfo.InvariantCulture)+"°";}
                rig.Speed=Mathf.Clamp(Mathf.Abs(v),0.03f,2f);SaveData();return "口パク速度="+rig.Speed.ToString("0.00",CultureInfo.InvariantCulture)+"Hz";
            }
            if(cmd=="upper"||cmd=="lower") {
                Preview mouthPreview;
                if(_previews.TryGetValue(user.userID,out mouthPreview)&&mouthPreview!=null&&mouthPreview.MouthJointPreview!=null){
                    mouthPreview.MouthJointPreview.Destroy();mouthPreview.MouthJointPreview=null;UpdateFreePreview(user);
                }
                if(args.Length<3)return "使い方: /kmb free mouthjoint "+cmd+" add|remove|clear";
                string sub=args[2].ToLowerInvariant();
                var list=cmd=="upper"?rig.UpperJawParts:rig.LowerJawParts;
                var other=cmd=="upper"?rig.LowerJawParts:rig.UpperJawParts;
                if(sub=="clear"){list.Clear();SaveData();return (cmd=="upper"?"上顎":"下顎")+"登録を全解除";}
                Preview p;if(!_previews.TryGetValue(user.userID,out p))return "自由制作プレビューがありません";
                string id=FreeAimPart(user,p,bp);
                if(string.IsNullOrEmpty(id))return "登録したいパーツへ照準を合わせてね";
                if(sub=="add") {
                    other.RemoveAll(x=>string.Equals(x,id,StringComparison.OrdinalIgnoreCase));
                    if(!list.Any(x=>string.Equals(x,id,StringComparison.OrdinalIgnoreCase)))list.Add(id);
                    SaveData();DrawFreeHud(user);
                    return id+" を "+(cmd=="upper"?"上顎":"下顎")+" に登録";
                }
                if(sub=="remove") {
                    list.RemoveAll(x=>string.Equals(x,id,StringComparison.OrdinalIgnoreCase));SaveData();DrawFreeHud(user);
                    return id+" を "+(cmd=="upper"?"上顎":"下顎")+" から解除";
                }
                return "add / remove / clear を指定してね";
            }
            return "不明なmouthjoint操作。place / upper / lower / migrate / test / axis / upperangle / lowerangle / speed / invert / on / off / diag";
        }

        private string HandleInstalledAi(BasePlayer user,string[] args) {
            if(args==null||args.Length<3)return "使い方: /kmb free ai 作品名 on|off|mode look|chase|radius 60|neck 首|neckyaw 55|neckpitch 22|neckspeed 40|neckoffset 0|neckinvert on|off|yaw|pitch|mouth 口|mouthspeed 0.12|mouthangle 8|upperangle 4|mouthaxis X|Y|Z|mouthinvert on|off|jawsplit|jawdiag|diag";
            string id=args[1];string action=args[2].ToLowerInvariant();
            KeyValuePair<string,InstalledFreeModel> selected;string resolveError;
            if(!TryResolveInstalledModel(user,id,out selected,out resolveError))return resolveError;
            string key=selected.Key;var installed=selected.Value;
            if(installed==null)return "作品がありません";
            if(action=="on") {
                installed.AiFacePlayers=true;
                string mouth=ApplyInstalledMouthPreset(installed);
                string neck=ApplyInstalledNeckPreset(installed);
                SaveData();
                return "索敵ON: モード="+(installed.AiChasePlayers?"追跡":"首だけ注視")+" / 半径"+installed.AiDetectRadius.ToString("0",CultureInfo.InvariantCulture)+"m / "+neck+" / "+mouth;
            }
            if(action=="off") {
                installed.AiFacePlayers=false;
                InstalledSwim sw;if(_installedSwimmers.TryGetValue(key,out sw)){sw.AiTargetUserId=0UL;sw.AiHoldingPosition=false;}
                SaveData();return "索敵OFF（首は正面へ戻ります。口モーションはそのまま）";
            }
            if(action=="diag") {
                InstalledSwim sw;string target="なし";
                if(_installedSwimmers.TryGetValue(key,out sw)&&sw.AiTargetUserId!=0UL) {
                    var p=FindActivePlayerById(sw.AiTargetUserId);
                    if(p!=null)target=p.displayName+"("+p.userID+")";
                }
                var mouth=FindInstalledMouthMotion(installed);
                var neck=FindInstalledNeckMotion(installed);
                string neckRuntime="";
                if(_installedSwimmers.TryGetValue(key,out sw))neckRuntime=" yaw="+sw.AiNeckYaw.ToString("0.0",CultureInfo.InvariantCulture)+" pitch="+sw.AiNeckPitch.ToString("0.0",CultureInfo.InvariantCulture);
                return "AI="+(installed.AiFacePlayers?"ON":"OFF")+" / モード="+(installed.AiChasePlayers?"追跡":"首だけ注視")+" / 半径="+installed.AiDetectRadius.ToString("0",CultureInfo.InvariantCulture)+
                    "m / 対象="+target+" / 首="+(neck!=null?neck.Name:"未設定")+neckRuntime+
                    " invert(Yaw="+(installed.AiNeckYawInvert?"ON":"OFF")+",Pitch="+(installed.AiNeckPitchInvert?"ON":"OFF")+")"+
                    " / 顎="+ApplyInstalledMouthPreset(installed)+" / invert="+(installed.AiMouthInvert?"ON":"OFF");
            }
            if(action=="mode") {
                if(args.Length<4)return "使い方: /kmb free ai "+id+" mode look|chase";
                string mode=args[3].ToLowerInvariant();
                if(mode=="look"||mode=="watch"||mode=="注視")installed.AiChasePlayers=false;
                else if(mode=="chase"||mode=="追跡")installed.AiChasePlayers=true;
                else return "mode は look または chase を指定してね";
                InstalledSwim runtime;if(_installedSwimmers.TryGetValue(key,out runtime))runtime.AiHoldingPosition=false;
                SaveData();return "AIモード="+(installed.AiChasePlayers?"追跡":"首だけ注視（本体は巡航継続）");
            }
            if(action=="neck") {
                if(args.Length<4)return "使い方: /kmb free ai "+id+" neck 首";
                installed.AiNeckMotion=string.Join(" ",args.Skip(3).ToArray()).Trim();
                string result=ApplyInstalledNeckPreset(installed);SaveData();return result;
            }
            if(action=="mouth") {
                if(args.Length<4)return "使い方: /kmb free ai "+id+" mouth 口";
                installed.AiMouthMotion=string.Join(" ",args.Skip(3).ToArray()).Trim();
                string result=ApplyInstalledMouthPreset(installed);SaveData();return result;
            }
            if(action=="jawsplit") {
                string info;
                bool ok=MigrateDedicatedMouthJoint(installed.Blueprint,true,out info);
                if(ok){EnsureCreatureMotionHierarchy(installed.Blueprint);ApplyInstalledMouthPreset(installed);SaveData();}
                return (ok?"MouthJoint移行OK: ":"MouthJoint移行失敗: ")+info;
            }
            if(action=="jawdiag") {
                string info;MigrateDedicatedMouthJoint(installed.Blueprint,false,out info);
                var rig=MouthRig(installed.Blueprint);
                return info+" / Joint="+(rig!=null&&rig.HasJoint?"配置済み":"未配置")+
                    " / 上顎="+(rig==null?"":string.Join(",",rig.UpperJawParts.ToArray()))+
                    " / 下顎="+(rig==null?"":string.Join(",",rig.LowerJawParts.ToArray()));
            }
            if(action=="mouthaxis") {
                if(args.Length<4)return "使い方: /kmb free ai "+id+" mouthaxis X|Y|Z";
                installed.AiMouthAxis=NormalizeMotionAxis(args[3]);
                string result=ApplyInstalledMouthPreset(installed);SaveData();return result;
            }
            if(action=="mouthinvert") {
                if(args.Length<4)return "使い方: /kmb free ai "+id+" mouthinvert on|off";
                string v=args[3].ToLowerInvariant();
                if(v=="on"||v=="true"||v=="1"||v=="reverse"||v=="反転")installed.AiMouthInvert=true;
                else if(v=="off"||v=="false"||v=="0"||v=="normal"||v=="通常")installed.AiMouthInvert=false;
                else return "mouthinvert は on または off";
                string result=ApplyInstalledMouthPreset(installed);SaveData();return result;
            }
            if(action=="neckinvert") {
                if(args.Length<4)return "使い方: /kmb free ai "+id+" neckinvert on|off|yaw|pitch";
                string v=args[3].ToLowerInvariant();
                if(v=="on"||v=="both"||v=="true"||v=="1"||v=="反転") {installed.AiNeckYawInvert=true;installed.AiNeckPitchInvert=true;}
                else if(v=="off"||v=="normal"||v=="false"||v=="0"||v=="通常") {installed.AiNeckYawInvert=false;installed.AiNeckPitchInvert=false;}
                else if(v=="yaw"||v=="左右") {installed.AiNeckYawInvert=!installed.AiNeckYawInvert;}
                else if(v=="pitch"||v=="上下") {installed.AiNeckPitchInvert=!installed.AiNeckPitchInvert;}
                else return "neckinvert は on / off / yaw / pitch";
                SaveData();
                return "首反転: Yaw="+(installed.AiNeckYawInvert?"ON":"OFF")+" / Pitch="+(installed.AiNeckPitchInvert?"ON":"OFF");
            }
            if(args.Length<4)return "値を指定してね";
            float value;
            if(!float.TryParse(args[3],NumberStyles.Float,CultureInfo.InvariantCulture,out value)||float.IsNaN(value)||float.IsInfinity(value))
                return "数値を半角で入力してね";
            if(action=="neckyaw") {
                installed.AiNeckYawLimit=Mathf.Clamp(Mathf.Abs(value),0f,120f);SaveData();
                return "首左右上限=±"+installed.AiNeckYawLimit.ToString("0",CultureInfo.InvariantCulture)+"°";
            }
            if(action=="neckpitch") {
                installed.AiNeckPitchLimit=Mathf.Clamp(Mathf.Abs(value),0f,60f);SaveData();
                return "首上下上限=±"+installed.AiNeckPitchLimit.ToString("0",CultureInfo.InvariantCulture)+"°";
            }
            if(action=="neckspeed") {
                installed.AiNeckTurnSpeed=Mathf.Clamp(Mathf.Abs(value),1f,180f);SaveData();
                return "首追従速度="+installed.AiNeckTurnSpeed.ToString("0",CultureInfo.InvariantCulture)+"°/秒";
            }
            if(action=="neckoffset") {
                installed.AiNeckYawOffset=Mathf.DeltaAngle(0f,value);SaveData();
                return "首前方向補正="+installed.AiNeckYawOffset.ToString("0",CultureInfo.InvariantCulture)+"°";
            }
            if(action=="radius") {
                installed.AiDetectRadius=Mathf.Clamp(value,5f,200f);SaveData();
                return "索敵半径="+installed.AiDetectRadius.ToString("0",CultureInfo.InvariantCulture)+"m";
            }
            if(action=="turnspeed") {
                installed.AiTurnSpeed=Mathf.Clamp(value,1f,90f);SaveData();
                return "索敵旋回="+installed.AiTurnSpeed.ToString("0",CultureInfo.InvariantCulture)+"°/秒";
            }
            if(action=="lookoffset") {
                installed.AiLookYawOffset=Mathf.DeltaAngle(0f,value);SaveData();
                return "見た目前方向補正="+installed.AiLookYawOffset.ToString("0",CultureInfo.InvariantCulture)+"°";
            }
            if(action=="stopdistance") {
                installed.AiStopDistance=Mathf.Clamp(value,2f,80f);
                if(installed.AiResumeDistance<installed.AiStopDistance+2f)installed.AiResumeDistance=installed.AiStopDistance+2f;
                SaveData();return "追跡停止距離="+installed.AiStopDistance.ToString("0",CultureInfo.InvariantCulture)+"m";
            }
            if(action=="resumedistance") {
                installed.AiResumeDistance=Mathf.Clamp(value,installed.AiStopDistance+2f,120f);SaveData();
                return "追跡再開距離="+installed.AiResumeDistance.ToString("0",CultureInfo.InvariantCulture)+"m";
            }
            if(action=="mouthspeed") {
                installed.AiMouthSpeed=Mathf.Clamp(Mathf.Abs(value),0.03f,2f);
                string result=ApplyInstalledMouthPreset(installed);SaveData();return result;
            }
            if(action=="mouthangle") {
                installed.AiMouthAngle=Mathf.Clamp(Mathf.Abs(value),0f,60f);
                string result=ApplyInstalledMouthPreset(installed);SaveData();return result;
            }
            if(action=="upperangle") {
                installed.AiUpperJawAngle=Mathf.Clamp(Mathf.Abs(value),0f,45f);
                string result=ApplyInstalledMouthPreset(installed);SaveData();return result;
            }
            return "不明なAI操作です";
        }
        private string HandleInstalledSwim(BasePlayer user,string[] args) {
            if(args.Length<3)return "使い方: /kmb free swim 作品名 start|softstart|stop|speed 数値|turn 数値|lift 数値|hz 10-50|root x y z|rootauto|diag|reverse。固定IDでも指定可";
            string id=args[1];string action=args[2].ToLowerInvariant();
            KeyValuePair<string,InstalledFreeModel> selected;string resolveError;
            if(!TryResolveInstalledModel(user,id,out selected,out resolveError))return resolveError;
            string key=selected.Key;var installed=selected.Value;
            if(action=="stop") {StopInstalledSwim(key);return "航行停止。現在位置を保存しました";}
            Preview runtime;
            if(!_installedPreviews.TryGetValue(key,out runtime))return "作品は生成されていません。/kmb free resume と /kmb free queue で確認してね";
            if(installed.Blueprint==null||installed.Blueprint.FreeParts==null)return "設計図がありません";
            if(action=="start"||action=="softstart") {
                if(_installedSwimmers.ContainsKey(key))return "この機体はすでに航行中。/kmb free swim "+id+" stop で停止";
                int total=installed.Blueprint.FreeParts.Count;
                int ready=installed.Blueprint.FreeParts.Keys.Count(partId=>{
                    BaseEntity e;return runtime.FreeEntities.TryGetValue(partId,out e)&&e!=null&&!e.IsDestroyed;
                });
                if(ready<total)return "まだ生成中: "+ready+"/"+total+"。/kmb free queue で確認してね";
                bool mini=action=="start";
                var state=new InstalledSwim{Speed=4f,CurrentSpeed=0f,Acceleration=8f,TurnDegrees=0f,LastSave=Time.realtimeSinceStartup,LastStep=Time.realtimeSinceStartup,Direction=-1f,UpdateHz=(mini?50f:_config.SwimUpdateHz),RateWindowStart=Time.realtimeSinceStartup};
                string rigError;
                bool rigOk=mini?BuildInstalledSwimMiniRig(key,state,installed,runtime,out rigError):BuildInstalledSwimSoftwareRig(key,state,installed,runtime,out rigError);
                if(!rigOk)return "航行開始できません: "+rigError+(mini?"（従来方式は /kmb free swim "+id+" softstart）":"");
                _installedSwimmers[key]=state;
                state.Timer=timer.Every(1f/Mathf.Clamp(state.UpdateHz,10f,50f),()=>StepInstalledSwim(key));
                return (mini?"ハイブリッドMini航行開始: ":"ソフト固定航行開始: ")+id+"。速度4.0m/s・同期"+state.UpdateHz.ToString("0",CultureInfo.InvariantCulture)+"Hz"+(mini?"・Mini=胴体内追従/ホーミング":"")+"。diagで確認";
            }
            if(action=="root"&&args.Length>=6) {
                float x,y,z;
                if(!float.TryParse(args[3],NumberStyles.Float,CultureInfo.InvariantCulture,out x)||
                   !float.TryParse(args[4],NumberStyles.Float,CultureInfo.InvariantCulture,out y)||
                   !float.TryParse(args[5],NumberStyles.Float,CultureInfo.InvariantCulture,out z))
                    return "使い方: /kmb free swim "+id+" root X Y Z （胴体中心からのローカル微調整m）";
                installed.MiniRootOffset=Vec.From(new Vector3(Mathf.Clamp(x,-20f,20f),Mathf.Clamp(y,-20f,20f),Mathf.Clamp(z,-20f,20f)));
                SaveData();
                if(_installedSwimmers.ContainsKey(key))return "MiniRoot位置を保存しました。反映するには stop → start してね";
                return "MiniRoot微調整を保存: ("+x.ToString("0.00",CultureInfo.InvariantCulture)+", "+y.ToString("0.00",CultureInfo.InvariantCulture)+", "+z.ToString("0.00",CultureInfo.InvariantCulture)+")m。次回startで反映";
            }
            if(action=="rootauto") {
                installed.MiniRootOffset=new Vec();SaveData();
                if(_installedSwimmers.ContainsKey(key))return "MiniRoot微調整を0に戻しました。反映するには stop → start してね";
                return "MiniRootを自動中心＋微調整0に戻しました";
            }
            if(action=="diag") {
                InstalledSwim diag;
                if(!_installedSwimmers.TryGetValue(key,out diag))return "先に start で航行開始してね";
                string aiTarget="なし";
                if(diag.AiTargetUserId!=0UL){var tp=FindActivePlayerById(diag.AiTargetUserId);if(tp!=null)aiTarget=tp.displayName;}
                return "航行診断: 方式="+(diag.UseMiniRoot?"HybridMini":"Software")+" / Hitbox="+((diag.UseMiniRoot&&(_config==null||_config.SwimMiniRootHitbox))?"ON":"OFF")+" / 設定="+diag.UpdateHz.ToString("0.0",CultureInfo.InvariantCulture)+"Hz / 実測="+diag.ActualHz.ToString("0.0",CultureInfo.InvariantCulture)+"Hz / 速度="+diag.CurrentSpeed.ToString("0.0",CultureInfo.InvariantCulture)+"m/s / AI="+(installed.AiFacePlayers?"ON":"OFF")+" / 対象="+aiTarget+" / パーツ="+diag.LocalPositions.Count;
            }
            if(action=="reverse") {
                InstalledSwim rev;
                if(!_installedSwimmers.TryGetValue(key,out rev))return "先に start で航行開始してね";
                rev.Direction*=-1f;
                return "進行方向を反転しました";
            }
            if(args.Length<4)return "値を指定: speed 4、turn 5、lift 0.2、hz 30（診断は diag、方向反転は reverse）";
            float value;
            if(!float.TryParse(args[3],NumberStyles.Float,CultureInfo.InvariantCulture,out value)||
                float.IsNaN(value)||float.IsInfinity(value))return "数値を半角で入力してね (例: 4)";
            if(action=="lift") {
                if(_installedSwimmers.ContainsKey(key))return "航行を停止してから高さを変更してね";
                if(value< -5f||value>5f)return "高さ調整は -5〜5m";
                runtime.RootPosition+=Vector3.up*value;
                installed.RootPosition=Vec.From(runtime.RootPosition);
                foreach(var part in installed.Blueprint.FreeParts.Values){
                    if(part==null)continue;
                    BaseEntity e;
                    if(!runtime.FreeEntities.TryGetValue(part.Id,out e)||e==null||e.IsDestroyed)continue;
                    Vector3 pos;Quaternion rot;FreePose(installed.Blueprint,runtime,part.Id,out pos,out rot);
                    e.transform.SetPositionAndRotation(pos,rot);e.TransformChanged();e.SendNetworkUpdate();
                }
                SaveData();return "設置済み機体の高さを "+value.ToString("0.00",CultureInfo.InvariantCulture)+"m 変更しました";
            }
            InstalledSwim moving;
            if(!_installedSwimmers.TryGetValue(key,out moving))return "先に start で航行開始してね";
            if(action=="hz") {
                moving.UpdateHz=Mathf.Clamp(value,10f,50f);
                if(moving.Timer!=null)moving.Timer.Destroy();
                moving.LastNetworkUpdate=0f;moving.ActualHz=0f;moving.RateTicks=0;moving.RateWindowStart=Time.realtimeSinceStartup;
                moving.Timer=timer.Every(1f/moving.UpdateHz,()=>StepInstalledSwim(key));
                return "航行同期を "+moving.UpdateHz.ToString("0.0",CultureInfo.InvariantCulture)+"Hz に変更。/kmb free swim "+id+" diag で実測を確認";
            }
            if(action=="speed") {
                moving.Speed=Mathf.Clamp(Mathf.Abs(value),0f,20f);
                return "目標航行速度を "+moving.Speed.ToString("0.0",CultureInfo.InvariantCulture)+"m/s に変更（現在 "+
                    moving.CurrentSpeed.ToString("0.0",CultureInfo.InvariantCulture)+"m/s、加速して追従）";
            }
            if(action=="turn") {
                moving.TurnDegrees=Mathf.Clamp(value,-25f,25f);
                return "旋回速度を "+moving.TurnDegrees.ToString("0.0",CultureInfo.InvariantCulture)+"度/秒 に変更（0で直進）";
            }
            return "使い方: start(HybridMini) / softstart(従来) / stop / speed 0-20 / turn 数値 / lift 数値 / hz 10-50 / root X Y Z / rootauto / diag / reverse";
        }

        private int CountMissingFreeNative(Preview preview,Blueprint bp) {
            if(preview==null||bp==null||bp.FreeParts==null)return 0;int n=0;
            foreach(var part in bp.FreeParts.Values){if(part==null||!IsNativePartKey(part.PrefabKey))continue;BaseEntity e;if(!preview.FreeEntities.TryGetValue(part.Id,out e)||e==null||e.IsDestroyed)n++;}return n;
        }
        private int FastRestoreFreeNativeBurst(BasePlayer user,Preview preview,Blueprint bp) {
            if(user==null||preview==null||bp==null||!_config.EnableNativeScrapGibs||!_config.ExperimentalNativeWithoutHeli)return 0;
            if(_nativeCapturing||Time.realtimeSinceStartup<_nativeNextAttempt)return 0;
            var type=FindNativeGibType();var method=NativeCreateMethod(type);var source=NativeGibSource(type);if(method==null||source==null)return 0;
            int totalPlaced=0,burstLimit=_config.FastNativeBurstSpawn?_config.FastNativeBurstMaxBatches:1;float interval=_config.FastNativeBurstSpawn?_config.FastNativeBurstIntervalSeconds:_config.NativeNoHeliCooldownSeconds;_nativeNextAttempt=Time.realtimeSinceStartup+interval;
            for(int burst=0;burst<burstLimit;burst++){
                var missing=bp.FreeParts.Values.Where(part=>{if(part==null||!IsNativePartKey(part.PrefabKey))return false;BaseEntity e;return !preview.FreeEntities.TryGetValue(part.Id,out e)||e==null||e.IsDestroyed;}).OrderBy(x=>x.Id).ToList();if(missing.Count==0)break;
                GameObject emitter=null;var batch=new List<HelicopterDebris>();var retained=new HashSet<HelicopterDebris>();int placedThis=0;
                try{
                    emitter=new GameObject("KMB_FastFreeGibBurst");emitter.transform.position=preview.RootPosition+Vector3.up*2f;emitter.transform.rotation=preview.RootRotation;
                    _nativeBatchCaptured.Clear();_nativeCapturing=true;object created=null;try{created=method.Invoke(null,new object[]{NativeScrapPrefab,emitter,source,Vector3.zero,0f});}finally{_nativeCapturing=false;}
                    var seq=created as IEnumerable;if(seq!=null)foreach(object value in seq){var g=AsNativeGib(value);if(g!=null&&!batch.Contains(g))batch.Add(g);}foreach(var g in _nativeBatchCaptured)if(g!=null&&!batch.Contains(g))batch.Add(g);
                    if(batch.Count<1||batch.Count>80)break;var pools=new Dictionary<string,Queue<HelicopterDebris>>(StringComparer.OrdinalIgnoreCase);
                    foreach(var g in batch){if(g==null||g.IsDestroyed)continue;string mesh=MeshKeyOfGib(g);if(string.IsNullOrEmpty(mesh))continue;Queue<HelicopterDebris> q;if(!pools.TryGetValue(mesh,out q)){q=new Queue<HelicopterDebris>();pools[mesh]=q;}q.Enqueue(g);}
                    foreach(var part in missing){string mesh;if(!_nativeMeshes.TryGetValue(part.PrefabKey,out mesh))continue;Queue<HelicopterDebris> q;if(!pools.TryGetValue(mesh,out q)||q.Count==0)continue;var chosen=q.Dequeue();if(chosen==null||chosen.IsDestroyed)continue;chosen.enableSaving=false;FreezePhysics(chosen);ProtectStaticNativeGib(chosen);Vector3 pos;Quaternion rot;FreePose(bp,preview,part.Id,out pos,out rot);chosen.transform.SetPositionAndRotation(pos,rot);chosen.TransformChanged();ApplyVisualScale(chosen,part.Scale);chosen.SendNetworkUpdateImmediate();if(chosen.IsDestroyed)continue;preview.FreeEntities[part.Id]=chosen;preview.Results[part.Id]="高速生成: "+mesh;TrackNativeGib(chosen,"free-fast",part.Id);retained.Add(chosen);placedThis++;totalPlaced++;}
                }catch(Exception ex){var root=ex is TargetInvocationException&&ex.InnerException!=null?ex.InnerException:ex;PrintWarning("KMB free burst: "+root.GetType().Name+" "+root.Message);}
                finally{_nativeCapturing=false;foreach(var g in _nativeBatchCaptured)if(g!=null&&!batch.Contains(g))batch.Add(g);foreach(var g in batch)if(g!=null&&!g.IsDestroyed&&!retained.Contains(g))try{g.Kill();}catch{} _nativeBatchCaptured.Clear();if(emitter!=null)try{UnityEngine.Object.Destroy(emitter);}catch{}}
                if(placedThis==0)break;
            }return totalPlaced;
        }
        private void StopFastFreeNativeRestore(ulong owner){Timer t;if(_fastFreeRestoreTimers.TryGetValue(owner,out t)){if(t!=null)t.Destroy();_fastFreeRestoreTimers.Remove(owner);}}
        private void StartFastFreeNativeRestore(BasePlayer user){
            if(user==null||!user.IsConnected)return;StopFastFreeNativeRestore(user.userID);if(!_config.EnableNativeScrapGibs||!_config.ExperimentalNativeWithoutHeli)return;Preview preview;if(!_previews.TryGetValue(user.userID,out preview))return;Blueprint bp=B(user);if(CountMissingFreeNative(preview,bp)==0)return;
            Action tick=null;tick=()=>{BasePlayer p=BasePlayer.FindByID(user.userID);Preview pr;if(p==null||!p.IsConnected||!_previews.TryGetValue(user.userID,out pr)){StopFastFreeNativeRestore(user.userID);return;}Blueprint design=B(p);if(CountMissingFreeNative(pr,design)==0){StopFastFreeNativeRestore(user.userID);return;}if(_nativeCapturing||Time.realtimeSinceStartup<_nativeNextAttempt)return;int placed=FastRestoreFreeNativeBurst(p,pr,design);if(placed==0)StopFastFreeNativeRestore(user.userID);};
            _fastFreeRestoreTimers[user.userID]=timer.Every(_config.FastNativeBurstSpawn?_config.FastNativeBurstIntervalSeconds:0.75f,tick);tick();
        }
        private void RepairInstalledFreeParts() {
            if(_config.PauseInstalledAutoRestoration)return;
            foreach(var kv in _data.InstalledFreeModels.ToArray()) {
                if(_installedDuplicateSkipped.Contains(kv.Key))continue;
                // Do not write saved world poses while the vehicle root is moving the craft.
                if(_installedSwimmers.ContainsKey(kv.Key))continue;
                InstalledFreeModel model=kv.Value;Preview preview;
                if(model==null||model.Blueprint==null||model.Blueprint.FreeParts==null||
                    !_installedPreviews.TryGetValue(kv.Key,out preview))continue;
                foreach(var part in model.Blueprint.FreeParts.Values) {
                    if(part==null)continue;
                    BaseEntity current;
                    if(preview.FreeEntities.TryGetValue(part.Id,out current)&&current!=null&&!current.IsDestroyed) {
                        // Lock the installed model to the saved world-space transform.
                        Vector3 expectedPosition;Quaternion expectedRotation;
                        FreePose(model.Blueprint,preview,part.Id,out expectedPosition,out expectedRotation);
                        if((current.transform.position-expectedPosition).sqrMagnitude>0.0025f ||
                           Quaternion.Angle(current.transform.rotation,expectedRotation)>0.5f) {
                            try {
                                current.transform.SetPositionAndRotation(expectedPosition,expectedRotation);
                                current.TransformChanged();current.SendNetworkUpdate();
                            }catch(Exception ex){PrintWarning("Permanent pose lock: "+ex.Message);}
                        }
                        // Never reset an auto-respawn attempt counter merely because a part was alive
                        // for one check. An expiring gib otherwise causes unbounded spawn/repair cycles.
                        continue;
                    }
                    preview.FreeEntities.Remove(part.Id);
                    if(!IsNativePartKey(part.PrefabKey)) {
                        Dictionary<string,int> retries;
                        if(!_installedAttempts.TryGetValue(kv.Key,out retries)){
                            retries=new Dictionary<string,int>();_installedAttempts[kv.Key]=retries;
                        }
                        int count;retries.TryGetValue(part.Id,out count);
                        if(count<3){retries[part.Id]=count+1;SpawnFreePart(null,preview,model.Blueprint,part);}
                    }
                    // Native restoration is handled by the 5-second queue, maximum 3 attempts
                    // per part per plugin runtime (until an explicit /kmb free repair).
                }
            }
        }
        private string SpawnInstalledNativePart(Preview p,Blueprint b,FreePart part) {
            if(!_config.EnableNativeScrapGibs||!_config.ExperimentalNativeWithoutHeli)return "ヘリ無しNativeが設定OFF";
            string mesh;
            if(!_nativeMeshes.TryGetValue(part.PrefabKey,out mesh))return "未登録Native: "+part.PrefabKey;
            var type=FindNativeGibType();var method=NativeCreateMethod(type);var source=NativeGibSource(type);
            if(method==null||source==null)return "Rust ServerGib APIが利用不可";
            _nativeNextAttempt=Time.realtimeSinceStartup+_config.NativeNoHeliCooldownSeconds;
            GameObject emitter=null;var batch=new List<HelicopterDebris>();HelicopterDebris chosen=null;bool keep=false;
            try {
                emitter=new GameObject("KMB_PermanentGibRestorer");
                emitter.transform.position=p.RootPosition+Vector3.up*2f;
                emitter.transform.rotation=p.RootRotation;
                _nativeBatchCaptured.Clear();_nativeCapturing=true;
                object created=null;
                try{created=method.Invoke(null,new object[]{NativeScrapPrefab,emitter,source,Vector3.zero,0f});}
                finally{_nativeCapturing=false;}
                var seq=created as IEnumerable;
                if(seq!=null)foreach(object value in seq){var gib=AsNativeGib(value);if(gib!=null&&!batch.Contains(gib))batch.Add(gib);}
                foreach(var gib in _nativeBatchCaptured)if(gib!=null&&!batch.Contains(gib))batch.Add(gib);
                if(batch.Count<1||batch.Count>80)return "Gibバッチ数異常: "+batch.Count;
                chosen=batch.FirstOrDefault(g=>g!=null&&!g.IsDestroyed&&string.Equals(MeshKeyOfGib(g),mesh,StringComparison.OrdinalIgnoreCase));
                if(chosen==null)return "Gib形状なし: "+mesh;
                chosen.enableSaving=false;FreezePhysics(chosen);
                ProtectStaticNativeGib(chosen);
                Vector3 pos;Quaternion rot;FreePose(b,p,part.Id,out pos,out rot);
                chosen.transform.SetPositionAndRotation(pos,rot);chosen.TransformChanged();
                ApplyVisualScale(chosen,part.Scale);chosen.SendNetworkUpdateImmediate();
                if(chosen.IsDestroyed){chosen=null;return "Gibが生成後に消滅";}
                p.FreeEntities[part.Id]=chosen;p.Results[part.Id]="永続復元: "+mesh;
                TrackNativeGib(chosen,"installed",part.Id);
                keep=true;return "復元成功";
            } catch(Exception ex) {
                var root=ex is TargetInvocationException&&ex.InnerException!=null?ex.InnerException:ex;
                return "復元例外: "+root.GetType().Name+" "+root.Message;
            } finally {
                _nativeCapturing=false;
                if(emitter!=null)try{UnityEngine.Object.Destroy(emitter);}catch{}
                foreach(var gib in _nativeBatchCaptured)if(gib!=null&&!batch.Contains(gib))batch.Add(gib);
                foreach(var gib in batch)if(gib!=null&&!gib.IsDestroyed&&(!keep||gib!=chosen))try{gib.Kill();}catch{}
                _nativeBatchCaptured.Clear();
            }
        }
        // -------------------- v0.4 free assembly (experimental) --------------------
        // Preview is server-side BaseEntity only. Native mesh/Gib prefabs without BaseEntity are not supported.
        private void EnableClassic(BasePlayer user) {
            var ed=E(user);ed.FreeDirect=false;ed.FreeGrab=false;ed.FreeParentPick=false;ed.FreeSwitchOpen=false;
            CuiHelper.DestroyUi(user,FreeUiRoot);CuiHelper.DestroyUi(user,DirectUiRoot);
            B(user).FreeOnly=false;SaveData();BuildPreview(user);DrawUi(user);
            Say(user,"従来13部位モードへ戻りました。自由制作データは消えません。");
        }
        // Runtime previews are removed by oxide.reload/unload while FreeOnly stays saved.
        // Rebuild on demand instead of leaving the editor in a 'workshop missing' state.
        // No native debris batches are spawned here: native pieces keep their restore status.
        private bool EnsureFreeWorkshop(BasePlayer user,bool notify) {
            if(user==null||!user.IsConnected)return false;
            Preview active;
            if(_previews.TryGetValue(user.userID,out active)&&active!=null)return true;
            if(!B(user).FreeOnly) {
                if(notify)Say(user,"自由制作モードではありません。/kmb free を実行してね");
                return false;
            }
            BuildPreview(user);
            if(_previews.TryGetValue(user.userID,out active)&&active!=null) {
                if(notify)Say(user,"自由制作作業場を復旧しました。Native残骸は必要なものだけ再抽出してね");
                return true;
            }
            string reason=E(user).Message;
            if(string.IsNullOrEmpty(reason))reason="作業場の生成に失敗";
            if(notify)Say(user,"自由制作の作業場を生成できません: "+reason+"。/kmb free workshop で再試行できます");
            return false;
        }
        private void EnableFree(BasePlayer user) {
            var ed=E(user);ed.FreeDirect=false;ed.FreeGrab=false;ed.FreeParentPick=false;ed.FreeSwitchOpen=false;
            if(ed.DirectEdit) {ed.DirectEdit=false;ed.Grabbed=false;CuiHelper.DestroyUi(user,DirectUiRoot);}
            CuiHelper.DestroyUi(user,UiRoot);CuiHelper.DestroyUi(user,DirectUiRoot);
            var bp=B(user);bool needsPreview=!bp.FreeOnly||!_previews.ContainsKey(user.userID);
            bp.FreeOnly=true;SaveData();
            if(needsPreview&&!EnsureFreeWorkshop(user,false)) {
                Say(user,"自由制作作業場を作れません: "+ed.Message+"。場所を変えて /kmb free workshop を試してね");
            }
            DrawFreeUi(user);
        }
        private void FreePose(Blueprint b,Preview p,string id,out Vector3 pos,out Quaternion rot) {
            var visited=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            FreePoseRec(b,p,id,visited,0,out pos,out rot);
        }
        private void FreePoseRec(Blueprint b,Preview p,string id,HashSet<string> seen,int depth,out Vector3 pos,out Quaternion rot) {
            pos=p.RootPosition;rot=p.RootRotation*Quaternion.Euler(0,b.RootYaw,0);
            FreePart part;
            if(depth>24||!b.FreeParts.TryGetValue(id,out part)||part==null||!seen.Add(id))return;
            Vector3 basePos=pos;Quaternion baseRot=rot;
            if(!string.IsNullOrEmpty(part.ParentId)&&b.FreeParts.ContainsKey(part.ParentId)) {
                FreePoseRec(b,p,part.ParentId,seen,depth+1,out basePos,out baseRot);
            }
            Quaternion jointRot=baseRot*Quaternion.Euler(part.Rotation.V());
            pos=basePos+baseRot*part.Position.V()+jointRot*part.VisualPosition.V();
            rot=jointRot*Quaternion.Euler(part.VisualRotation.V());
        }
        private void FreeParentPose(Blueprint b,Preview p,string parent,out Vector3 pos,out Quaternion rot) {
            if(!string.IsNullOrEmpty(parent)&&b.FreeParts.ContainsKey(parent))FreePose(b,p,parent,out pos,out rot);
            else {pos=p.RootPosition;rot=p.RootRotation*Quaternion.Euler(0,b.RootYaw,0);}
        }
        private bool WouldFreeCycle(Blueprint b,string child,string parent) {
            string walk=parent;
            for(int i=0;i<=b.FreeParts.Count;i++) {
                if(string.IsNullOrEmpty(walk))return false;
                if(string.Equals(walk,child,StringComparison.OrdinalIgnoreCase))return true;
                FreePart f;if(!b.FreeParts.TryGetValue(walk,out f)||f==null)return false;
                walk=f.ParentId;
            }
            return true;
        }
        private void FreeReparent(BasePlayer user,string id,string parent) {
            var b=B(user);FreePart part;
            if(!b.FreeParts.TryGetValue(id,out part)||part==null)return;
            if(!string.IsNullOrEmpty(parent)&&!b.FreeParts.ContainsKey(parent)){Say(user,"親パーツがありません");return;}
            if(WouldFreeCycle(b,id,parent)){Say(user,"循環する親子関係は作れません");return;}
            Preview p;if(!_previews.TryGetValue(user.userID,out p)){Say(user,"先に自由制作プレビューを作ってね");return;}
            Vector3 before;Quaternion beforeRot;FreePose(b,p,id,out before,out beforeRot);
            Vector3 origin;Quaternion parentRot;FreeParentPose(b,p,parent,out origin,out parentRot);
            part.ParentId=parent;
            part.Position=Vec.From(Quaternion.Inverse(parentRot)*(before-origin));
            part.Rotation=Vec.From((Quaternion.Inverse(parentRot)*beforeRot).eulerAngles);
            part.VisualPosition=new Vec();part.VisualRotation=new Vec();
            SaveData();UpdateFreePreview(user);
            Say(user,id+" を "+(string.IsNullOrEmpty(parent)?"ルート":parent)+" に接続しました（位置維持）");
        }
        private string FreeAdd(BasePlayer user,string key) {
            var b=B(user);var ed=E(user);
            if(IsNativePartKey(key))return NativeAddFree(user,key);
            string reason;if(!CanSpawn(key,out reason))return "Prefab使用不可: "+reason;
            Preview p;if(!EnsureFreeWorkshop(user,false)||!_previews.TryGetValue(user.userID,out p))
                return "作業場を作れません: "+E(user).Message+"（/kmb free workshop で再試行）";
            string id="";
            do {b.FreeSerial++;id="P"+b.FreeSerial.ToString("D3",CultureInfo.InvariantCulture);} while(b.FreeParts.ContainsKey(id));
            var part=new FreePart{Id=id,Name=key,PrefabKey=key,Position=new Vec(0.65f*((b.FreeParts.Count%5)-2),2f,1.4f+0.55f*(b.FreeParts.Count/5))};
            b.FreeParts[id]=part;ed.FreeSelected=id;ed.FreePartPage=Math.Max(0,(b.FreeParts.Count-1)/9);
            SpawnFreePart(user,p,b,part);
            if(!p.FreeEntities.ContainsKey(id)){b.FreeParts.Remove(id);return "生成に失敗しました。Prefabがエンティティとして動くか確認してね ("+key+")";}
            SaveData();return id+" を追加しました ("+key+")";
        }
        private void SpawnFreePreviewParts(BasePlayer user,Preview p,Blueprint b) {
            if(!b.FreeOnly)return;
            foreach(var part in b.FreeParts.Values.OrderBy(x=>x!=null?x.Id:"")) {
                if(part==null)continue;
                SpawnFreePart(user,p,b,part);
            }
        }
        private void SpawnFreePart(BasePlayer user,Preview p,Blueprint b,FreePart part) {
            if(part==null||p==null)return;
            if(IsNativePartKey(part.PrefabKey)) {p.Results[part.Id]="Native未生成。/kmb gib restore "+part.Id;return;}
            BaseEntity present;
            if(p.FreeEntities.TryGetValue(part.Id,out present)) {
                if(present!=null&&!present.IsDestroyed)return;
                p.FreeEntities.Remove(part.Id);
            }
            string reason;if(!CanSpawn(part.PrefabKey,out reason)) {p.Results[part.Id]="Free: "+reason;return;}
            // Unlimited test: no per-type free entity cap (vehicle/debris included).
            // Never use proxy parts: the saved design must match the actual visible prefab.
            Vector3 pos;Quaternion rot;FreePose(b,p,part.Id,out pos,out rot);
            BaseEntity entity=null;
            try {
                entity=GameManager.server.CreateEntity(PrefabPath(part.PrefabKey),pos,rot,true);
                if(entity==null){p.Results[part.Id]="CreateEntity failed";return;}
                entity.enableSaving=false;
                FreezePhysics(entity);entity.Spawn();
                if(entity.IsDestroyed){p.Results[part.Id]="Spawn destroyed";return;}
                FreezePhysics(entity);ApplyVisualScale(entity,part.Scale);entity.SendNetworkUpdateImmediate();
                p.FreeEntities[part.Id]=entity;
                p.Results[part.Id]="Free: " + part.PrefabKey;
            } catch(Exception ex) {
                p.Results[part.Id]="Free: "+ex.GetType().Name;
                if(entity!=null&&!entity.IsDestroyed)try{entity.Kill();}catch{}
                PrintWarning("Free part "+part.Id+": "+ex.Message);
            }
        }
        private void UpdateFreePreview(BasePlayer user) {
            if(user==null)return;
            Preview p;if(!_previews.TryGetValue(user.userID,out p))return;
            var b=B(user);if(!b.FreeOnly)return;
            foreach(var kv in p.FreeEntities) {
                FreePart part;if(!b.FreeParts.TryGetValue(kv.Key,out part)||part==null)continue;
                BaseEntity entity=kv.Value;if(entity==null||entity.IsDestroyed)continue;
                Vector3 pos;Quaternion rot;FreePose(b,p,kv.Key,out pos,out rot);
                try {entity.transform.SetPositionAndRotation(pos,rot);ApplyVisualScale(entity,part.Scale);entity.TransformChanged();entity.SendNetworkUpdate();}
                catch(Exception ex){PrintWarning("Free update "+kv.Key+": "+ex.Message);}
            }
        }
        private void SetFreeDirect(BasePlayer user,bool enabled) {
            var e=E(user);
            if(enabled) {
                if(!B(user).FreeOnly){EnableFree(user);}
                if(!EnsureFreeWorkshop(user,true))return;
                e.FreeDirect=true;e.FreeGrab=false;e.FreeSwitchOpen=false;e.FreeManipMode="MOVE";
                CuiHelper.DestroyUi(user,FreeUiRoot);CuiHelper.DestroyUi(user,UiRoot);
                Say(user,"自由編集ON: 右クリック=Prefab選択/切替、E=パーツを掴む、左クリック=配置して固定、R=移動/回転/サイズ切替。/kmb free ui で一覧");
                DrawFreeHud(user);
            } else {
                if(e.FreeGrab)FreePlace(user);
                e.FreeJointPlace=false;e.FreeJointMotion="";
                ClearFreeGroupGrab(e);
                e.FreeDirect=false;CuiHelper.DestroyUi(user,DirectUiRoot);SaveData();
                DrawFreeUi(user);
            }
        }
        private string FreeAimPart(BasePlayer user,Preview p,Blueprint b) {
            var ray=user.eyes.HeadRay();float closest=9999f;string id="";
            // Objects have physics colliders disabled in preview, so use strict center-point targeting.
            foreach(var kv in p.FreeEntities) {
                if(kv.Value==null||kv.Value.IsDestroyed)continue;
                Vector3 pos;Quaternion rot;FreePose(b,p,kv.Key,out pos,out rot);
                float along=Vector3.Dot(pos-ray.origin,ray.direction.normalized);
                if(along<1f||along>25f)continue;
                float rayDist=Vector3.Distance(pos,ray.origin+ray.direction.normalized*along);
                float threshold=Mathf.Clamp(along*0.034f,0.25f,0.60f);
                if(rayDist>threshold)continue;
                if(along<closest){closest=along;id=kv.Key;}
            }
            return id;
        }
        private void DrawFreeMarker(BasePlayer user) {
            var e=E(user);Preview p;if(!_previews.TryGetValue(user.userID,out p))return;
            var b=B(user);
            DrawFreeMotionJointMarkers(user,p,b);
            string id=e.FreeGrab?e.FreeSelected:FreeAimPart(user,p,b);
            if(string.IsNullOrEmpty(id)||!b.FreeParts.ContainsKey(id))return;
            Vector3 pos;Quaternion rot;FreePose(b,p,id,out pos,out rot);
            try {
                user.SendConsoleCommand("ddraw.sphere",0.32f,Color.yellow,pos,0.22f);
                user.SendConsoleCommand("ddraw.text",0.32f,Color.cyan,pos+Vector3.up*0.3f,id+" (E) "+e.FreeManipMode);
            }catch{}
        }
        private bool TryGetFreeJointTarget(BasePlayer user,out Vector3 target) {
            target=Vector3.zero;if(user==null)return false;
            var e=E(user);Ray ray=user.eyes.HeadRay();
            float distance=Mathf.Clamp(e.FreeJointDistance,1.0f,25f);
            target=ray.origin+ray.direction.normalized*distance;
            return true;
        }
        private void DrawFreeMotionJointMarkers(BasePlayer user,Preview p,Blueprint b) {
            if(user==null||p==null||b==null)return;
            try {
                if(b.FreeMotions!=null)foreach(var g in b.FreeMotions.Values) {
                    if(g==null||!g.UseJointPivot||g.JointPosition==null)continue;
                    Vector3 wp=p.RootPosition+p.RootRotation*g.JointPosition.V();
                    float r=Mathf.Clamp(g.JointRadius,0.12f,0.8f);
                    user.SendConsoleCommand("ddraw.sphere",0.36f,Color.cyan,wp,r);
                    user.SendConsoleCommand("ddraw.text",0.36f,Color.white,wp+Vector3.up*(r+0.12f),"JOINT "+g.Name);
                }
                var mouth=MouthRig(b);
                if(mouth!=null&&mouth.HasJoint&&mouth.JointPosition!=null) {
                    Vector3 mw=p.RootPosition+p.RootRotation*mouth.JointPosition.V();
                    float mr=Mathf.Clamp(mouth.JointRadius,0.12f,0.8f);
                    user.SendConsoleCommand("ddraw.sphere",0.36f,Color.yellow,mw,mr);
                    user.SendConsoleCommand("ddraw.text",0.36f,Color.yellow,mw+Vector3.up*(mr+0.12f),"MOUTH JOINT");
                }
                var e=E(user);
                if(e.FreeJointPlace&&!string.IsNullOrEmpty(e.FreeJointMotion)) {
                    Vector3 target;if(TryGetFreeJointTarget(user,out target)) {
                        user.SendConsoleCommand("ddraw.sphere",0.18f,Color.green,target,0.32f);
                        user.SendConsoleCommand("ddraw.text",0.18f,Color.green,target+Vector3.up*0.45f,(e.FreeJointMotion=="__MOUTHJOINT__"?"MOUTH JOINT":e.FreeJointMotion+" JOINT")+" / 左クリック固定");
                    }
                }
            }catch{}
        }
        private void StartFreeMotionJointPlace(BasePlayer user,FreeMotion g,Preview p,Blueprint bp) {
            if(user==null||g==null||p==null||bp==null)return;
            var e=E(user);
            e.FreeGrab=false;e.FreeJointPlace=true;e.FreeJointMotion=g.Name;
            float distance=6f;
            if(g.UseJointPivot&&g.JointPosition!=null) {
                Vector3 wp=p.RootPosition+p.RootRotation*g.JointPosition.V();
                distance=Vector3.Distance(user.eyes.HeadRay().origin,wp);
            } else {
                string aimed=FreeAimPart(user,p,bp);
                if(!string.IsNullOrEmpty(aimed)&&bp.FreeParts.ContainsKey(aimed)) {
                    Vector3 wp;Quaternion wr;FreePose(bp,p,aimed,out wp,out wr);
                    distance=Vector3.Distance(user.eyes.HeadRay().origin,wp);
                }
            }
            e.FreeJointDistance=Mathf.Clamp(distance,1.0f,25f);
            if(!e.FreeDirect)SetFreeDirect(user,true);else DrawFreeHud(user);
        }
        private void CancelFreeMotionJointPlace(BasePlayer user) {
            if(user==null)return;var e=E(user);
            e.FreeJointPlace=false;e.FreeJointMotion="";
            DrawFreeHud(user);
        }
        private void CommitFreeMotionJointPlace(BasePlayer user) {
            if(user==null)return;var e=E(user);if(!e.FreeJointPlace)return;
            var bp=B(user);Preview p;if(bp==null||!_previews.TryGetValue(user.userID,out p)){CancelFreeMotionJointPlace(user);return;}
            Vector3 world;if(!TryGetFreeJointTarget(user,out world))return;
            Vector3 local=Quaternion.Inverse(p.RootRotation)*(world-p.RootPosition);
            if(e.FreeJointMotion=="__MOUTHJOINT__") {
                var rig=MouthRig(bp);
                rig.JointPosition=Vec.From(local);rig.HasJoint=true;
                e.FreeJointPlace=false;e.FreeJointMotion="";SaveData();DrawFreeHud(user);
                Say(user,"MOUTH JOINTを固定しました。上顎/下顎はこの1個の丸JOINTを共有します");
                return;
            }
            FreeMotion g;if(bp.FreeMotions==null||!bp.FreeMotions.TryGetValue(e.FreeJointMotion,out g)||g==null){CancelFreeMotionJointPlace(user);return;}
            g.JointPosition=Vec.From(local);g.UseJointPivot=true;
            e.FreeJointPlace=false;e.FreeJointMotion="";SaveData();DrawFreeHud(user);
            Say(user,"丸ジョイント『"+g.Name+"』を固定しました。再配置: /kmb free motion joint "+g.Name);
        }

        private bool UpdateFreeModuleGroupPose(BasePlayer user,Editor e,Preview p,Blueprint b) {
            if(e==null||p==null||b==null||string.IsNullOrEmpty(e.FreeGrabModuleId)||e.FreeGroupStartPositions.Count==0)return false;
            var members=b.FreeParts.Values.Where(x=>x!=null&&string.Equals(x.ModuleId,e.FreeGrabModuleId,StringComparison.OrdinalIgnoreCase)&&e.FreeGroupStartPositions.ContainsKey(x.Id)).ToList();
            if(members.Count==0){ClearFreeGroupGrab(e);return false;}
            Quaternion rootFrame=p.RootRotation*Quaternion.Euler(0f,b.RootYaw,0f);
            if(e.FreeManipMode=="MOVE") {
                Ray ray=user.eyes.HeadRay();Vector3 target=ray.origin+ray.direction.normalized*e.FreeGrabDistance;if(Vector3.Distance(target,p.RootPosition)>_config.FreeEditRangeMeters)return true;
                Vector3 wantedCenter=Quaternion.Inverse(rootFrame)*(target-p.RootPosition);Vector3 delta=wantedCenter-e.FreeGroupStartCenter;
                foreach(var m in members)m.Position=Vec.From(e.FreeGroupStartPositions[m.Id].V()+delta);
            } else if(e.FreeManipMode=="SCALE") {
                float delta=-Mathf.DeltaAngle(e.FreeGrabStartView.x,ViewAngles(user).x);float factor=Mathf.Clamp(1f+delta*0.012f,0.2f,4f);
                foreach(var m in members){Vector3 start=e.FreeGroupStartPositions[m.Id].V();m.Position=Vec.From(e.FreeGroupStartCenter+(start-e.FreeGroupStartCenter)*factor);float s;e.FreeGroupStartScales.TryGetValue(m.Id,out s);m.Scale=Mathf.Clamp(s*factor,0.2f,4f);}
            } else {
                Vector3 current=ViewAngles(user);float angle=0f;Vector3 axis=Vector3.up;
                if(e.FreeManipMode=="ROT_X"){angle=-Mathf.DeltaAngle(e.FreeGrabStartView.x,current.x);axis=Vector3.right;}
                if(e.FreeManipMode=="ROT_Y"){angle=Mathf.DeltaAngle(e.FreeGrabStartView.y,current.y);axis=Vector3.up;}
                if(e.FreeManipMode=="ROT_Z"){angle=Mathf.DeltaAngle(e.FreeGrabStartView.y,current.y);axis=Vector3.forward;}
                Quaternion q=Quaternion.AngleAxis(angle,axis);
                foreach(var m in members){Vector3 start=e.FreeGroupStartPositions[m.Id].V();m.Position=Vec.From(e.FreeGroupStartCenter+q*(start-e.FreeGroupStartCenter));Vec rr;e.FreeGroupStartRotations.TryGetValue(m.Id,out rr);m.Rotation=Vec.From((q*Quaternion.Euler((rr??new Vec()).V())).eulerAngles);}
            }
            UpdateFreePreview(user);return true;
        }
        private void FreePick(BasePlayer user) {
            var e=E(user);var b=B(user);Preview p;if(!_previews.TryGetValue(user.userID,out p))return;
            string id=FreeAimPart(user,p,b);if(string.IsNullOrEmpty(id)){Say(user,"照準の近くに自由パーツがありません");return;}
            FreePart part=b.FreeParts[id];e.FreeSelected=id;e.FreeGrab=true;e.FreeManipMode="MOVE";e.FreeGrabStartView=ViewAngles(user);
            if(!string.IsNullOrEmpty(part.ModuleId)){
                CaptureFreeGroupGrab(e,b,part.ModuleId);Quaternion rootFrame=p.RootRotation*Quaternion.Euler(0f,b.RootYaw,0f);Vector3 centerWorld=p.RootPosition+rootFrame*e.FreeGroupStartCenter;
                e.FreeGrabDistance=Mathf.Clamp(Vector3.Distance(user.eyes.HeadRay().origin,centerWorld),1.5f,25f);e.FreeGrabStartRotation=part.Rotation.Copy();e.FreeGrabStartScale=part.Scale;e.Message="MODULE全体を選択";
            }else{
                ClearFreeGroupGrab(e);Vector3 wp;Quaternion wr;FreePose(b,p,id,out wp,out wr);e.FreeGrabDistance=Mathf.Clamp(Vector3.Distance(user.eyes.HeadRay().origin,wp),1.5f,25f);e.FreeGrabStartRotation=part.Rotation.Copy();e.FreeGrabStartScale=part.Scale;
            }
            DrawFreeHud(user);
        }
        // Newly added pieces from the right-click palette attach to the edit cursor.
        // This is intentionally only used after a successful spawn, never on replacing an old part.
        private void BeginFreeMoveNewPart(BasePlayer user) {
            var ed=E(user);var b=B(user);Preview p;FreePart part;
            if(!ed.FreeDirect||!_previews.TryGetValue(user.userID,out p)||
                !b.FreeParts.TryGetValue(ed.FreeSelected,out part)||part==null||
                !p.FreeEntities.ContainsKey(ed.FreeSelected))return;
            Vector3 world;Quaternion rotation;FreePose(b,p,part.Id,out world,out rotation);
            ed.FreeGrab=true;ed.FreeManipMode="MOVE";
            ed.FreeGrabDistance=Mathf.Clamp(Vector3.Distance(user.eyes.HeadRay().origin,world),1.5f,25f);
            ed.FreeGrabStartRotation=part.Rotation.Copy();ed.FreeGrabStartScale=part.Scale;
            ed.FreeGrabStartView=ViewAngles(user);
            DrawFreeHud(user);
            Say(user,"新パーツを掴みました。視線で移動、左クリックで配置・固定、Rで回転/サイズ切替");
        }
        private void FreePlace(BasePlayer user) {
            var e=E(user);if(!e.FreeGrab)return;
            // Commit the final aim / rotation / scale on the SAME input tick as left-click.
            // The ordinary drag update is throttled to 100ms, so without this flush the
            // saved transform could lag behind the last visible cursor movement.
            UpdateFreeGrabPose(user,true);
            e.FreeGrab=false;
            ClearFreeGroupGrab(e);
            SaveData();
            DrawFreeHud(user);
        }
        private void FreeCycle(BasePlayer user) {
            var e=E(user);if(!e.FreeGrab)return;
            if(e.FreeManipMode=="MOVE")e.FreeManipMode="ROT_Y";
            else if(e.FreeManipMode=="ROT_Y")e.FreeManipMode="ROT_X";
            else if(e.FreeManipMode=="ROT_X")e.FreeManipMode="ROT_Z";
            else if(e.FreeManipMode=="ROT_Z")e.FreeManipMode="SCALE";
            else e.FreeManipMode="MOVE";
            FreePart part;var b=B(user);if(!b.FreeParts.TryGetValue(e.FreeSelected,out part))return;
            if(!string.IsNullOrEmpty(part.ModuleId))CaptureFreeGroupGrab(e,b,part.ModuleId);
            e.FreeGrabStartRotation=part.Rotation.Copy();e.FreeGrabStartScale=part.Scale;
            e.FreeGrabStartView=ViewAngles(user);DrawFreeHud(user);
        }
        // A right-click opens a safe picker instead of spawning dozens of Gibs while cycling.
        // Select the piece under the crosshair (when possible) to swap it in-place.
        // If no piece is aimed at, picker buttons add a new piece.
        private void OpenFreeSwitch(BasePlayer user) {
            var b=B(user);var ed=E(user);Preview p;
            if(!b.FreeOnly){Say(user,"先に /kmb free で自由制作モードを開いてね");return;}
            if(!EnsureFreeWorkshop(user,true)||!_previews.TryGetValue(user.userID,out p))return;
            string aimed=ed.FreeGrab&&b.FreeParts.ContainsKey(ed.FreeSelected)
                ? ed.FreeSelected : FreeAimPart(user,p,b);
            if(ed.FreeGrab)FreePlace(user);
            ed.FreeSelected=!string.IsNullOrEmpty(aimed)?aimed:"";
            ed.FreeSwitchOpen=true;
            ed.FreeDirect=false; // modal UI owns cursor; no E/R gameplay input while it is visible
            ed.FreeCatalogScope="gibs";ed.FreeCatalogPage=0;
            ed.Message=string.IsNullOrEmpty(aimed)
                ? "対象なし: 素材ボタンで新規追加"
                : aimed+" の素材を切替（位置・角度は維持）";
            CuiHelper.DestroyUi(user,DirectUiRoot);
            // KPL saves newly registered items to data; refresh only when opening the picker,
            // never inside input ticks. Also refreshes MechBoss preset catalog.
            SyncMechBossCatalog();
            DrawFreeUi(user);
        }
        private string FreeSwitchPart(BasePlayer user,string key) {
            var b=B(user);var ed=E(user);
            if(!b.FreeOnly)return "先に /kmb free で自由制作へ";
            // Aim at empty space -> append a new part. Exact placement is done afterwards with E.
            FreePart current;
            if(string.IsNullOrEmpty(ed.FreeSelected)||!b.FreeParts.TryGetValue(ed.FreeSelected,out current)||current==null){
                string added=FreeAdd(user,key);
                var selected=E(user).FreeSelected;
                Preview checkPreview;
                if(_previews.TryGetValue(user.userID,out checkPreview)&&!string.IsNullOrEmpty(selected)&&checkPreview.FreeEntities.ContainsKey(selected))
                    return "追加成功 "+selected+" => "+key;
                return added;
            }
            if(string.Equals(current.PrefabKey,key,StringComparison.OrdinalIgnoreCase))
                return current.Id+" はすでに "+key;
            if(!IsNativePartKey(key)){
                string reason;if(!CanSpawn(key,out reason))return "生成不可: "+reason;
            }
            Preview preview;
            if(!EnsureFreeWorkshop(user,false)||!_previews.TryGetValue(user.userID,out preview))
                return "作業場を作れません: "+E(user).Message+"（/kmb free workshop で再試行）";
            BaseEntity previous=null;
            preview.FreeEntities.TryGetValue(current.Id,out previous);
            string previousResult="";bool hadResult=preview.Results.TryGetValue(current.Id,out previousResult);
            // Stage replacement without destroying the original. On any failure restore the
            // blueprint AND its old visible entity. No extra piece is left behind.
            var replacement=new FreePart{
                Id=current.Id,Name=key,PrefabKey=key,ParentId=current.ParentId,
                Position=current.Position!=null?current.Position.Copy():new Vec(),
                Rotation=current.Rotation!=null?current.Rotation.Copy():new Vec(),
                VisualPosition=current.VisualPosition!=null?current.VisualPosition.Copy():new Vec(),
                VisualRotation=current.VisualRotation!=null?current.VisualRotation.Copy():new Vec(),
                Scale=current.Scale,ModuleId=current.ModuleId
            };
            b.FreeParts[current.Id]=replacement;
            preview.FreeEntities.Remove(current.Id);
            preview.Results.Remove(current.Id);
            string result="";
            try{
                if(IsNativePartKey(key))result=NativePlaceFree(user,b,preview,replacement);
                else {SpawnFreePart(user,preview,b,replacement);result="Prefab切替 "+current.Id+" => "+key;}
            }catch(Exception ex){result="切替時の例外: "+ex.GetType().Name;PrintWarning("KMB Free switch: "+ex.Message);}
            BaseEntity created=null;
            bool succeeded=preview.FreeEntities.TryGetValue(current.Id,out created)&&created!=null&&!created.IsDestroyed;
            if(!succeeded){
                b.FreeParts[current.Id]=current;
                preview.FreeEntities.Remove(current.Id);
                if(previous!=null&&!previous.IsDestroyed)preview.FreeEntities[current.Id]=previous;
                if(hadResult)preview.Results[current.Id]=previousResult;
                else preview.Results.Remove(current.Id);
                return "切替失敗。元のパーツは維持: "+result;
            }
            if(previous!=null&&!previous.IsDestroyed&&previous!=created){try{previous.Kill();}catch(Exception ex){PrintWarning("KMB Free old part cleanup: "+ex.Message);}}
            // A replacement is a fresh physical part; do not inherit an old repair budget.
            preview.FreeRepairAttempts.Remove(current.Id);
            SaveData();UpdateFreePreview(user);
            return "切替成功 "+current.Id+" => "+key+(IsNativePartKey(key)?" (Native一時保持)":"");
        }
        private void ClearFreeBuild(BasePlayer user) {
            var b=B(user);var ed=E(user);
            if(Time.realtimeSinceStartup>ed.FreeClearConfirmUntil){
                ed.FreeClearConfirmUntil=Time.realtimeSinceStartup+12f;
                ed.Message="自由パーツを全削除します。12秒以内にもう一度『自由パーツ全消去』を押して確定";
                return;
            }
            ed.FreeClearConfirmUntil=0f;
            int count=b.FreeParts.Count;
            Preview p;
            if(_previews.TryGetValue(user.userID,out p)){
                foreach(var entity in p.FreeEntities.Values.ToArray())
                    if(entity!=null&&!entity.IsDestroyed)try{entity.Kill();}catch(Exception ex){PrintWarning("KMB Free clear: "+ex.Message);}
                p.FreeEntities.Clear();
                p.FreeRepairAttempts.Clear();
                // Results of classic slots are retained only in classic mode; free selection is now empty.
                foreach(var id in b.FreeParts.Keys)p.Results.Remove(id);
            }
            b.FreeParts.Clear();b.FreeSerial=0;
            ed.FreeSelected="";ed.FreeGrab=false;ed.FreePartPage=0;
            SaveData();
            ed.Message="自由制作パーツ "+count+" 個を撤去。従来ロボの13部位は保存したままです";
        }
        // Update the held part from the current view. Force=true is used on placement
        // to avoid losing the last <100ms of movement due to the normal input throttle.
        private void UpdateFreeGrabPose(BasePlayer user,bool force) {
            var e=E(user);
            if(!e.FreeGrab)return;
            float now=Time.realtimeSinceStartup;
            if(!force&&now-e.FreeLastUpdate<0.10f)return;
            e.FreeLastUpdate=now;
            Preview p;if(!_previews.TryGetValue(user.userID,out p))return;
            var b=B(user);FreePart part;
            if(!b.FreeParts.TryGetValue(e.FreeSelected,out part)){e.FreeGrab=false;ClearFreeGroupGrab(e);return;}
            if(!string.IsNullOrEmpty(e.FreeGrabModuleId)&&UpdateFreeModuleGroupPose(user,e,p,b))return;
            if(e.FreeManipMode=="MOVE") {
                Ray ray=user.eyes.HeadRay();Vector3 target=ray.origin+ray.direction.normalized*e.FreeGrabDistance;
                if(Vector3.Distance(target,p.RootPosition)>_config.FreeEditRangeMeters) {
                    // Prevent silently moving outside the editable volume.
                    // Parts already placed are not modified or deleted by this check.
                    return;
                }
                Vector3 basePos;Quaternion baseRot;FreeParentPose(b,p,part.ParentId,out basePos,out baseRot);
                Quaternion ownJoint=baseRot*Quaternion.Euler(part.Rotation.V());
                Vector3 targetJoint=target-ownJoint*part.VisualPosition.V();
                Vector3 loc=Quaternion.Inverse(baseRot)*(targetJoint-basePos);
                float limit=_config.FreeEditRangeMeters;
                part.Position=Vec.From(new Vector3(Mathf.Clamp(loc.x,-limit,limit),Mathf.Clamp(loc.y,-limit,limit),Mathf.Clamp(loc.z,-limit,limit)));
            } else if(e.FreeManipMode=="SCALE") {
                float delta=-Mathf.DeltaAngle(e.FreeGrabStartView.x,ViewAngles(user).x);
                part.Scale=Mathf.Clamp(e.FreeGrabStartScale+delta*0.018f,0.2f,4f);
            } else {
                Vec rr=e.FreeGrabStartRotation.Copy();Vector3 current=ViewAngles(user);
                if(e.FreeManipMode=="ROT_X")rr.X=Mathf.Repeat(rr.X-Mathf.DeltaAngle(e.FreeGrabStartView.x,current.x),360f);
                if(e.FreeManipMode=="ROT_Y")rr.Y=Mathf.Repeat(rr.Y+Mathf.DeltaAngle(e.FreeGrabStartView.y,current.y),360f);
                if(e.FreeManipMode=="ROT_Z")rr.Z=Mathf.Repeat(rr.Z+Mathf.DeltaAngle(e.FreeGrabStartView.y,current.y),360f);
                part.Rotation=rr;
            }
            UpdateFreePreview(user);
        }
        private void HandleFreeInput(BasePlayer user,InputState input) {
            var e=E(user);
            if(e.FreeJointPlace) {
                if(input.WasJustPressed(BUTTON.FIRE_SECONDARY)){
                    CancelFreeMotionJointPlace(user);
                    e.Message="ジョイント配置キャンセル";
                    DrawFreeHud(user);
                    return;
                }
                if(input.WasJustPressed(BUTTON.FIRE_PRIMARY)){CommitFreeMotionJointPlace(user);return;}
                if(input.WasJustPressed(BUTTON.USE)){e.FreeJointDistance=Mathf.Clamp(e.FreeJointDistance-0.25f,1.0f,25f);DrawFreeHud(user);}
                else if(input.WasJustPressed(BUTTON.RELOAD)){e.FreeJointDistance=Mathf.Clamp(e.FreeJointDistance+0.25f,1.0f,25f);DrawFreeHud(user);}
                if(Time.realtimeSinceStartup-e.FreeLastMarker>0.12f){e.FreeLastMarker=Time.realtimeSinceStartup;Preview jp;if(_previews.TryGetValue(user.userID,out jp))DrawFreeMotionJointMarkers(user,jp,B(user));}
                return;
            }
            if(input.WasJustPressed(BUTTON.FIRE_SECONDARY)){OpenFreeSwitch(user);return;}
            // In free-build only: E picks a piece; left-click places/fixes the piece being held.
            // Other game modes (including mech piloting / punch controls) are unaffected.
            if(input.WasJustPressed(BUTTON.FIRE_PRIMARY) && e.FreeGrab) {FreePlace(user);return;}
            if(input.WasJustPressed(BUTTON.USE) && !e.FreeGrab) FreePick(user);
            else if(input.WasJustPressed(BUTTON.RELOAD))FreeCycle(user);
            if(e.FreeGrab) {
                UpdateFreeGrabPose(user,false);
            } else if(Time.realtimeSinceStartup-e.FreeLastMarker>0.3f) {
                e.FreeLastMarker=Time.realtimeSinceStartup;DrawFreeMarker(user);
            }
        }

        private void DrawFreeHud(BasePlayer user) {
            CuiHelper.DestroyUi(user,DirectUiRoot);
            var e=E(user);if(!e.FreeDirect)return;
            var ui=new CuiElementContainer();
            string root=ui.Add(new CuiPanel{Image={Color="0.04 0.055 0.08 0.85"},RectTransform={AnchorMin="0.73 0.82",AnchorMax="0.985 0.972"},CursorEnabled=false},"Overlay",DirectUiRoot);
            Label(ui,root,"自由制作 直接編集",0.04f,0.69f,0.98f,0.99f,12);
            string freeStatus=e.FreeJointPlace?("丸ジョイント: "+(e.FreeJointMotion=="__MOUTHJOINT__"?"口専用JOINT":e.FreeJointMotion)+" 距離 "+e.FreeJointDistance.ToString("0.00",CultureInfo.InvariantCulture)+"m"):e.FreeGrab?("選択: "+e.FreeSelected+(string.IsNullOrEmpty(e.FreeGrabModuleId)?"":" [MODULE全体]")+" "+e.FreeManipMode+" / 左クリックで固定"):"照準で選択 / Eで掴む";
            Label(ui,root,freeStatus,0.04f,0.37f,0.98f,0.68f,10);
            string freeHelp=e.FreeJointPlace?"左クリック=ジョイント固定   E=手前   R=奥   右クリック=取消":"右クリック=素材切替   E=掴む   左クリック=固定   R=操作切替";
            Label(ui,root,freeHelp,0.04f,0.10f,0.98f,0.36f,9,"0.95 0.85 0.53 1");
            CuiHelper.AddUi(user,ui);
        }
        private void FreeDuplicate(BasePlayer user) {
            var b=B(user);var e=E(user);FreePart from;
            if(!b.FreeParts.TryGetValue(e.FreeSelected,out from)){Say(user,"複製するパーツを選んでね");return;}
            if(IsNativePartKey(from.PrefabKey)){Say(user,"Native部品は直接複製できません。右クリックのGib番号からもう1個追加してね");return;}
            b.FreeSerial++;string id="P"+b.FreeSerial.ToString("D3",CultureInfo.InvariantCulture);
            while(b.FreeParts.ContainsKey(id)){b.FreeSerial++;id="P"+b.FreeSerial.ToString("D3",CultureInfo.InvariantCulture);}
            var part=new FreePart {Id=id,Name=from.Name,PrefabKey=from.PrefabKey,ParentId=from.ParentId,
                Position=from.Position.Copy(),Rotation=from.Rotation.Copy(),VisualPosition=from.VisualPosition.Copy(),VisualRotation=from.VisualRotation.Copy(),Scale=from.Scale};
            part.Position.X+=0.55f;b.FreeParts[id]=part;e.FreeSelected=id;
            Preview p;if(_previews.TryGetValue(user.userID,out p))SpawnFreePart(user,p,b,part);
            if(p==null||!p.FreeEntities.ContainsKey(id)){b.FreeParts.Remove(id);e.FreeSelected=from.Id;Say(user,"複製に失敗しました（Prefabの生成可否を確認）");return;}
            SaveData();Say(user,id+" に複製しました");
        }
        private void FreeDelete(BasePlayer user,string id) {
            var b=B(user);FreePart part;if(!b.FreeParts.TryGetValue(id,out part))return;
            if(!_previews.ContainsKey(user.userID)){Say(user,"作業場を生成してから削除してね");return;}
            // Keep children visually fixed when removing their parent.
            foreach(var child in b.FreeParts.Values.Where(x=>x!=null&&string.Equals(x.ParentId,id,StringComparison.OrdinalIgnoreCase)).ToList())
                FreeReparent(user,child.Id,part.ParentId);
            b.FreeParts.Remove(id);Preview p;
            if(_previews.TryGetValue(user.userID,out p)) {
                BaseEntity ent;if(p.FreeEntities.TryGetValue(id,out ent)&&ent!=null&&!ent.IsDestroyed)ent.Kill();
                p.FreeEntities.Remove(id);p.Results.Remove(id);
                p.FreeRepairAttempts.Remove(id);
            }
            var e=E(user);if(e.FreeSelected==id)e.FreeSelected="";e.FreeGrab=false;
            SaveData();UpdateFreePreview(user);
        }
        private void FreeNudge(BasePlayer user,string field,string axis,float amount) {
            var b=B(user);FreePart part;if(!b.FreeParts.TryGetValue(E(user).FreeSelected,out part))return;
            Vec v=field=="pos"?part.Position:part.Rotation;
            float val=axis=="x"?v.X:axis=="y"?v.Y:v.Z;
            val=field=="pos"?Mathf.Clamp(val+amount,-_config.FreeEditRangeMeters,_config.FreeEditRangeMeters):Mathf.Repeat(val+amount,360f);
            if(axis=="x")v.X=val;else if(axis=="y")v.Y=val;else v.Z=val;
            SaveData();UpdateFreePreview(user);
        }
        private static bool IsScrapPartKey(string key) {
            return !string.IsNullOrEmpty(key) && (IsNativePartKey(key) || key=="gib018" || key=="helicockpit" ||
                key.StartsWith("scrapheli_",StringComparison.OrdinalIgnoreCase) ||
                key.IndexOf("helicockpit",StringComparison.OrdinalIgnoreCase)>=0 ||
                key.IndexOf("heli_cockpit",StringComparison.OrdinalIgnoreCase)>=0);
        }
        private bool IsCatalogScope(string key,string scope) {
            return scope=="all" || (scope=="scrap"?IsScrapPartKey(key):!IsScrapPartKey(key));
        }
        private string CatalogSpawnState(string key) {
            string state;
            if(_catalogCheck.TryGetValue(key,out state))return state;
            string reason;
            state=IsNativePartKey(key)?"Native抽出":(CanSpawn(key,out reason)?"OK":"不可: "+reason);
            _catalogCheck[key]=state;
            return state;
        }
        private void PrintCatalogChecks(BasePlayer user,string scope) {
            var keys=_catalogOrder.Where(k=>IsCatalogScope(k,scope)).ToArray();
            int ok=0;int blocked=0;
            foreach(var k in keys){if(CatalogSpawnState(k)=="OK")ok++;else blocked++;}
            Say(user,"候補診断: "+scope+" / "+keys.Length+"件 / Entity生成候補 "+ok+"件 / 不可 "+blocked+"件");
            foreach(var k in keys.Take(16))Say(user,k+" => "+CatalogSpawnState(k));
            if(keys.Length>16)Say(user,"残り "+(keys.Length-16)+"件。/kmb free search キーワードで絞ってね");
            Say(user,"※OKはBaseEntity存在確認のみ。実際の表示・残存・安全性はゲーム内テストが必要");
        }
        private void FreeStatus(BasePlayer user) {
            var bp=B(user);Say(user,"自由制作 "+bp.FreeParts.Count+"パーツ（上限なし） / 現在のモード "+(bp.FreeOnly?"自由":"従来"));
            foreach(var f in bp.FreeParts.Values.OrderBy(x=>x.Id).Take(32))Say(user,f.Id+" ["+f.PrefabKey+"] "+f.Name+" 親="+(string.IsNullOrEmpty(f.ParentId)?"root":f.ParentId));
        }
        private void HandleFreeChat(BasePlayer user,string[] args) {
            if(args==null||args.Length==0){EnableFree(user);return;}
            string action=args[0].ToLowerInvariant();var bp=B(user);var ed=E(user);
            if(action=="help") {
                Say(user,"/kmb free [ui|edit|workshop|list|search word|verify scrap|mech|all|add key|select ID|rename name|duplicate|delete|parent ID|root|pos x y z|rot x y z|scale 1.0|save]");
                Say(user,"/kmb free は自由制作画面。/kmb classic で旧モード。E=掴む、左クリック=配置/固定、R=操作モード切替、右クリック=パーツ切替UI。");
                Say(user,"/kmb free new は二度実行で空の新作品を開始（現作品を自動退避）。/kmb free clear は二度実行で現在の自由パーツを全撤去。");
                Say(user,"保存: /kmb free save 名前 | /kmb free load 名前 | /kmb free designs | /kmb free compare 作品A 作品B | /kmb free spawn 保存名 | /kmb free installed | /kmb free unplace 作品ID (2回確認)");
                Say(user,"増殖対策: /kmb free pause | /kmb free resume | /kmb free duplicates | /kmb free cleanup (2回確認)");
                Say(user,"Native消滅診断: /kmb free nativehealth。復元進捗: /kmb free queue");
                Say(user,"海上移動テスト: /kmb free swim 作品名 start / speed 4 / hz 30 / diag / turn 5 / stop（固定IDでも可）");
                Say(user,"細部可動: /kmb free motion new 尾 → add 尾 → joint 尾 → axis 尾 Y / angle 尾 25 / speed 尾 0.8 / on 尾");
                Say(user,"口専用JOINT: /kmb free mouthjoint place → 上顎を狙って upper add / 下顎を狙って lower add → axis X / upperangle 4 / lowerangle 10 / speed 0.12");
                Say(user,"生物AI: /kmb free ai 作品名 on / neck 首 / neckyaw 55 / neckpitch 22 / neckspeed 40 / mouth 口 / mouthaxis X / mouthspeed 0.12 / mouthangle 8 / diag");
                Say(user,"高速復元: /kmb free queue / /kmb free batchdelay 2 （重い場合4〜8秒に変更）");
                Say(user,"UI保存は設計図001など連番で新規保存。保存一覧から1クリック生成可能（同名は重複設置しない）。再設置前にバックアップ推奨。");return;
            }
            if(action=="workshop") {
                if(!bp.FreeOnly){EnableFree(user);return;}
                EnsureFreeWorkshop(user,true);DrawFreeUi(user);return;
            }
            if(action=="ui"){ed.FreeDirect=false;ed.FreeGrab=false;ed.FreeSwitchOpen=false;CuiHelper.DestroyUi(user,DirectUiRoot);EnableFree(user);return;}
            if(action=="switch"){OpenFreeSwitch(user);return;}
            if(action=="new") {Say(user,StartNewFreeProject(user));DrawFreeUi(user);return;}
            if(action=="clear"){
                if(!bp.FreeOnly){Say(user,"先に /kmb free を実行してね");return;}
                ClearFreeBuild(user);Say(user,ed.Message);DrawFreeUi(user);return;
            }
            if(action=="designs"){ed.FreeDesignListOpen=true;EnableFree(user);return;}
            if(action=="compare"&&args.Length>=3){Say(user,CompareSavedFreeDesigns(user,args[1],args[2]));return;}
            if(action=="spawn"&&args.Length>1){Say(user,InstallSavedFreeDesign(user,string.Join(" ",args.Skip(1).ToArray())));return;}
            if(action=="save"){Say(user,SaveNamedFreeDesign(user,args.Length>1?string.Join(" ",args.Skip(1).ToArray()):"default"));return;}
            if(action=="load"){Say(user,LoadNamedFreeDesign(user,args.Length>1?string.Join(" ",args.Skip(1).ToArray()):"default"));DrawFreeUi(user);return;}
            if(action=="install"){Say(user,InstallFreeDesign(user,args.Length>1?string.Join(" ",args.Skip(1).ToArray()):""));DrawFreeUi(user);return;}
            if(action=="unplace"){Say(user,UnplaceFreeDesign(user,args.Length>1?args[1]:""));DrawFreeUi(user);return;}
            if(action=="installed"){ListInstalledFree(user);return;}
            if(action=="swim"){Say(user,HandleInstalledSwim(user,args));return;}
            if(action=="ai"){Say(user,HandleInstalledAi(user,args));return;}
            if(action=="motion"){Say(user,HandleFreeMotion(user,args));return;}
            if(action=="mouthjoint"){Say(user,HandleMouthJoint(user,args));return;}
            if(action=="module"){Say(user,HandleFreeModule(user,args));return;}
            if(action=="fastspawn"){StartFastFreeNativeRestore(user);Say(user,"Native高速生成を開始しました");return;}
            if(action=="queue"){Say(user,InstalledRestoreProgress(user));return;}
            if(action=="batchdelay"&&args.Length>1){
                float secs;
                if(!float.TryParse(args[1],NumberStyles.Float,CultureInfo.InvariantCulture,out secs)||
                    float.IsNaN(secs)||float.IsInfinity(secs)) {Say(user,"数値を半角で入力してね");return;}
                _config.InstalledNativeBatchCooldownSeconds=Mathf.Clamp(secs,1.5f,30f);
                Config.WriteObject(_config,true);
                Say(user,"永久設置の抽出間隔="+_config.InstalledNativeBatchCooldownSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"秒（同じ形は次の抽出で復元）");return;
            }
            if(action=="nativehealth"){ShowNativeLifetimeStatus(user);return;}
            if(action=="pause"){Say(user,SetInstalledSpawnPause(true));return;}
            if(action=="resume"){Say(user,SetInstalledSpawnPause(false));return;}
            if(action=="duplicates"){Say(user,ListDuplicateInstalledFree(user));return;}
            if(action=="cleanup"){Say(user,CleanupDuplicateInstalledFree(user));return;}
            if(action=="repair"){
                foreach(var installed in OwnedInstalledModels(user))_installedAttempts.Remove(installed.Key);
                Say(user,"自分が永久設置した全作品のNative再生成カウンタをリセットしました。欠けたパーツは復元キューで順番に再生成します");return;
            }
            if(action=="edit"){SetFreeDirect(user,true);return;}
            if(action=="list"){FreeStatus(user);return;}
            if(action=="verify") {PrintCatalogChecks(user,args.Length>1 && (args[1]=="all"||args[1]=="mech")?args[1]:"scrap");return;}
            if(action=="search") {
                if(ed.FreeDirect){Say(user,"先に /kmb free ui で編集画面に戻ってね");return;}
                ed.FreeSearch=string.Join(" ",args.Skip(1).ToArray()).Trim();ed.FreeCatalogPage=0;DrawFreeUi(user);return;
            }
            if(!bp.FreeOnly){Say(user,"先に /kmb free で自由制作モードに切り替えてね");return;}
            if(action=="add"&&args.Length>1){Say(user,FreeAdd(user,args[1]));DrawFreeUi(user);return;}
            if(action=="select"&&args.Length>1&&bp.FreeParts.ContainsKey(args[1])){ed.FreeSelected=args[1];DrawFreeUi(user);return;}
            if(action=="duplicate"){FreeDuplicate(user);DrawFreeUi(user);return;}
            if(action=="delete") {FreeDelete(user,ed.FreeSelected);DrawFreeUi(user);return;}
            if(action=="root") {FreeReparent(user,ed.FreeSelected,"");DrawFreeUi(user);return;}
            if(action=="parent"&&args.Length>1){FreeReparent(user,ed.FreeSelected,args[1]);DrawFreeUi(user);return;}
            // Named save is processed before the FreeOnly gate above.
            if(action=="rename"&&args.Length>1){FreePart named;if(bp.FreeParts.TryGetValue(ed.FreeSelected,out named)){named.Name=string.Join(" ",args.Skip(1).ToArray()).Trim();if(named.Name.Length>30)named.Name=named.Name.Substring(0,30);SaveData();DrawFreeUi(user);}return;}
            FreePart part; if(!bp.FreeParts.TryGetValue(ed.FreeSelected,out part)){Say(user,"先にパーツを選んでね");return;}
            if((action=="pos"||action=="rot")&&args.Length>=4) {
                float x,y,z;if(!TryUiFloat(args[1],out x)||!TryUiFloat(args[2],out y)||!TryUiFloat(args[3],out z)){Say(user,"数値を確認してね");return;}
                if(action=="pos"){
                    float limit=_config.FreeEditRangeMeters;
                    part.Position=new Vec(Mathf.Clamp(x,-limit,limit),Mathf.Clamp(y,-limit,limit),Mathf.Clamp(z,-limit,limit));
                }
                else part.Rotation=new Vec(Mathf.Repeat(x,360),Mathf.Repeat(y,360),Mathf.Repeat(z,360));
                SaveData();UpdateFreePreview(user);DrawFreeUi(user);return;
            }
            if(action=="scale"&&args.Length>1){float v;if(TryUiFloat(args[1],out v)){part.Scale=Mathf.Clamp(v,0.20f,4f);SaveData();UpdateFreePreview(user);DrawFreeUi(user);}return;}
            // Do not spam chat while direct/free input mode is active.
            // E/R/mouse controls are handled by OnPlayerInput and should remain completely silent.
            if(ed.FreeDirect) {
                DrawFreeHud(user);
                return;
            }
            // Outside direct mode, keep unknown commands silent too; explicit /kmb free help shows the list.
            ed.Message="不明な操作です。必要なら /kmb free help";
        }
        [ConsoleCommand("kmb.freeui")]
        private void ConsoleFreeUi(ConsoleSystem.Arg arg) {
            BasePlayer user=arg.Player();if(!Allowed(user))return;
            // ConsoleSystem.Arg.Args is StringView[] on current Rust, not string[].
            // Convert explicitly so all subsequent commands operate on ordinary strings.
            string[] a = arg.Args == null ? new string[0] : Array.ConvertAll(arg.Args, v => v.ToString());
            if(a.Length==0)return;
            var ed=E(user);var bp=B(user);string action=a[0];
            if(action=="open"){EnableFree(user);return;}
            if(action=="classic"){EnableClassic(user);return;}
            if(action=="close"){
                bool returnToEdit=ed.FreeSwitchOpen;
                ed.FreeSwitchOpen=false;CuiHelper.DestroyUi(user,FreeUiRoot);
                if(returnToEdit)SetFreeDirect(user,true);
                return;
            }
            if(action=="edit"){SetFreeDirect(user,true);return;}
            if(action=="workshop") {
                if(!bp.FreeOnly){EnableFree(user);return;}
                if(EnsureFreeWorkshop(user,true))ed.Message="作業場の準備OK";
                DrawFreeUi(user);return;
            }
            if(action=="new"){ed.Message=StartNewFreeProject(user);DrawFreeUi(user);return;}
            if(action=="clear"){ClearFreeBuild(user);DrawFreeUi(user);return;}
            if(action=="save"){ed.Message=SaveNamedFreeDesign(user,NextSavedFreeDesignName(user));if(ed.Message.StartsWith("設計図『"))ed.FreeDesignListOpen=true;ed.FreeDesignPage=0;DrawFreeUi(user);return;}
            if(action=="designs"){ed.FreeDesignListOpen=true;ed.FreeDesignPage=0;DrawFreeUi(user);return;}
            if(action=="designbackups") {
                ed.FreeShowBackupDesigns=!ed.FreeShowBackupDesigns;
                ed.FreeDesignPage=0;DrawFreeUi(user);return;
            }
            if(action=="designback"){
                ed.FreeDesignListOpen=false;
                ed.FreeDesignUnplacePendingName="";
                ed.FreeDesignUnplaceConfirmUntil=0f;
                DrawFreeUi(user);return;
            }
            if(action=="designpage"&&a.Length>1){ed.FreeDesignPage=Math.Max(0,ed.FreeDesignPage+(a[1]=="next"?1:-1));DrawFreeUi(user);return;}
            if(action=="designload"&&a.Length>1){
                ed.FreeDesignUnplacePendingName="";
                ed.FreeDesignUnplaceConfirmUntil=0f;
                string name;
                ed.Message=TrySavedFreeDesignByIndex(user,a[1],out name)?LoadNamedFreeDesign(user,name):"設計図が見つかりません";
                ed.FreeDesignListOpen=false;DrawFreeUi(user);return;
            }
            if(action=="designspawn"&&a.Length>1){
                ed.FreeDesignUnplacePendingName="";
                ed.FreeDesignUnplaceConfirmUntil=0f;
                string name;
                ed.Message=TrySavedFreeDesignByIndex(user,a[1],out name)?InstallSavedFreeDesign(user,name):"設計図が見つかりません";
                DrawFreeUi(user);return;
            }
            if(action=="designunplace"&&a.Length>1){
                string name;
                ed.Message=TrySavedFreeDesignByIndex(user,a[1],out name)
                    ?UnplaceSavedFreeDesign(user,name):"設計図が見つかりません";
                DrawFreeUi(user);return;
            }
            if(action=="load"){ed.FreeDesignListOpen=true;DrawFreeUi(user);return;}
            if(action=="install"){ed.Message=InstallFreeDesign(user,"");DrawFreeUi(user);return;}
            if(action=="unplace"){ed.Message=UnplaceFreeDesign(user);DrawFreeUi(user);return;}
            if(action=="switch"&&a.Length>1){
                bool adding=string.IsNullOrEmpty(ed.FreeSelected)||!bp.FreeParts.ContainsKey(ed.FreeSelected);
                ed.Message=FreeSwitchPart(user,a[1]);
                if(ed.Message.StartsWith("切替成功")||ed.Message.StartsWith("追加成功")){
                    Say(user,ed.Message);SetFreeDirect(user,true);
                    if(adding)BeginFreeMoveNewPart(user);
                    return;
                }
                DrawFreeUi(user);return;
            }
            if(action=="switchgib"&&a.Length>1){
                int number;
                if(!int.TryParse(a[1],out number)){ed.Message="不正なGib番号";DrawFreeUi(user);return;}
                string key;
                if(!_nativePartIds.TryGetValue(number,out key)){ed.Message="Gib番号がありません。/kmb gib refresh で再読込";DrawFreeUi(user);return;}
                bool adding=string.IsNullOrEmpty(ed.FreeSelected)||!bp.FreeParts.ContainsKey(ed.FreeSelected);
                ed.Message=FreeSwitchPart(user,key);
                if(ed.Message.StartsWith("切替成功")||ed.Message.Contains(" を一時生成")||ed.Message.StartsWith("追加成功")){
                    Say(user,ed.Message);SetFreeDirect(user,true);
                    if(adding)BeginFreeMoveNewPart(user);
                    return;
                }
                DrawFreeUi(user);return;
            }
            if(action=="page"&&a.Length>1){ed.FreeCatalogPage=Math.Max(0,ed.FreeCatalogPage+(a[1]=="next"?1:-1));}
            if(action=="partpage"&&a.Length>1){ed.FreePartPage=Math.Max(0,ed.FreePartPage+(a[1]=="next"?1:-1));}
            if(action=="filter"&&a.Length>1){
                ed.FreeCatalogScope=(a[1]=="gibs"||a[1]=="scrap"||a[1]=="mech"||a[1]=="all")?a[1]:"all";
                ed.FreeCatalogPage=0;
            }
            if(action=="verify"){PrintCatalogChecks(user,ed.FreeCatalogScope);return;}
            if(action=="gibarm"){GibArm(user);ed.Message="Native連続生成="+(_config.EnableNativeScrapGibs&&_config.NativeContinuousPlacement?"ON":"OFF");DrawFreeUi(user);return;}
            if(action=="gibdiag"){GibDiag(user);ed.Message="Native="+(_config.EnableNativeScrapGibs?"有効":"設定OFF")+" / "+(_config.NativeContinuousPlacement?"連続配置":"45秒方式");DrawFreeUi(user);return;}
            if(action=="gibadd"&&a.Length>1) {
                int number;
                ed.Message=int.TryParse(a[1],out number)?SpawnRegisteredGib(user,number):"番号を確認してね";
                if(ed.Message.Contains(" を一時生成")) {
                    Say(user,ed.Message);SetFreeDirect(user,true);BeginFreeMoveNewPart(user);return;
                }
                DrawFreeUi(user);return;
            }
            if(action=="gibrestore"){NativeRestoreFree(user,ed.FreeSelected);DrawFreeUi(user);return;}
            if(action=="add"&&a.Length>1){
                ed.Message=FreeAdd(user,a[1]);
                if(ed.Message.Contains(" を追加しました ")||ed.Message.Contains(" を一時生成")) {
                    Say(user,ed.Message);SetFreeDirect(user,true);BeginFreeMoveNewPart(user);return;
                }
            }
            if(action=="pick"&&a.Length>1&&bp.FreeParts.ContainsKey(a[1])) {
                if(ed.FreeParentPick){FreeReparent(user,ed.FreeSelected,a[1]);ed.FreeParentPick=false;}
                else ed.FreeSelected=a[1];
            }
            if(action=="parentpick")ed.FreeParentPick=!ed.FreeParentPick;
            if(action=="root"&&bp.FreeParts.ContainsKey(ed.FreeSelected)){FreeReparent(user,ed.FreeSelected,"");ed.FreeParentPick=false;}
            if(action=="duplicate")FreeDuplicate(user);
            if(action=="delete")FreeDelete(user,ed.FreeSelected);
            if(action=="nudge"&&a.Length>3) {
                string field=a[1];string axis=a[2];float d;
                if((field=="pos"||field=="rot")&&(axis=="x"||axis=="y"||axis=="z")&&TryUiFloat(a[3],out d))
                    FreeNudge(user,field,axis,d);
            }
            if(action=="scale"&&a.Length>1) {
                FreePart fp;float d;
                if(bp.FreeParts.TryGetValue(ed.FreeSelected,out fp)&&TryUiFloat(a[1],out d)){
                    fp.Scale=Mathf.Clamp(fp.Scale+d,0.2f,4f);SaveData();UpdateFreePreview(user);
                }
            }
            DrawFreeUi(user);
        }
        private void DrawSavedFreeDesignUi(CuiElementContainer ui,string root,BasePlayer user,Editor ed) {
            var all=SavedFreeDesignList(user);
            var installed=OwnedInstalledModels(user);
            Dictionary<string,Blueprint> allStored;
            _data.SavedFreeDesigns.TryGetValue(user.UserIDString,out allStored);
            int backupCount=allStored==null?0:allStored.Count(kv=>IsAutomaticFreeBackupName(kv.Key)&&kv.Value!=null);
            Label(ui,root,"保存作品 "+all.Count+"件 / 退避 "+backupCount+"件",0.03f,0.93f,0.41f,0.99f,16);
            Button(ui,root,"新規制作","kmb.freeui new",0.415f,0.93f,0.527f,0.99f,"0.15 0.42 0.30 1",10);
            Button(ui,root,ed.FreeShowBackupDesigns?"退避を隠す":"退避も表示","kmb.freeui designbackups",0.535f,0.93f,0.705f,0.99f,"0.22 0.31 0.39 1",10);
            Button(ui,root,"制作に戻る","kmb.freeui designback",0.72f,0.93f,0.97f,0.99f,"0.18 0.34 0.43 1",12);
            Label(ui,root,"一覧の『制作退避』は新しい作品ではなく編集状態のバックアップ。選択=編集に読込 / 生成=設置",0.035f,0.865f,0.96f,0.92f,11,"0.75 0.86 0.97 1");
            if(all.Count==0) {
                Label(ui,root,"まだ設計図がありません。制作画面に戻り、パーツを配置して『新規保存』を押してね。",0.05f,0.55f,0.95f,0.66f,13);
            } else {
                int maxPage=Math.Max(0,(all.Count-1)/7);
                ed.FreeDesignPage=Mathf.Clamp(ed.FreeDesignPage,0,maxPage);
                for(int i=0;i<7;i++) {
                    int index=ed.FreeDesignPage*7+i;
                    if(index>=all.Count)break;
                    string name=all[index].Key;
                    Blueprint design=all[index].Value;
                    int total=design.FreeParts.Count;
                    int gib=design.FreeParts.Values.Count(x=>x!=null&&IsNativePartKey(x.PrefabKey));
                    int installedCount=installed.Count(kv=>kv.Value!=null&&string.Equals(kv.Value.Name,name,StringComparison.OrdinalIgnoreCase));
                    bool isInstalled=installedCount>0;
                    bool unplaceConfirm=isInstalled&&Time.realtimeSinceStartup<=ed.FreeDesignUnplaceConfirmUntil&&
                        string.Equals(ed.FreeDesignUnplacePendingName,name,StringComparison.OrdinalIgnoreCase);
                    float y=0.82f-i*0.091f;
                    Rect(ui,root,0.032f,y-0.078f,0.967f,y,"0.10 0.17 0.22 0.96");
                    string label=name.Length>23?name.Substring(0,23)+"...":name;
                    Label(ui,root,label+(IsAutomaticFreeBackupName(name)?" [自動退避]":"")+
                        (isInstalled?"  [設置済み "+installedCount+"件]":""),0.047f,y-0.031f,0.62f,y-0.002f,12,isInstalled?"0.54 0.87 0.72 1":"0.94 0.96 1 1");
                    Label(ui,root,"部品 "+total+" / Gib "+gib+"  |  "+SavedFreeDate(design)+"  |  "+SavedFreeSample(design),
                        0.047f,y-0.072f,0.715f,y-0.034f,9,"0.73 0.81 0.92 1");
                    Button(ui,root,"選択","kmb.freeui designload "+index,0.730f,y-0.067f,0.831f,y-0.012f,"0.17 0.34 0.46 1",11);
                    // Installed designs can be unplaced from THIS row, without typing IDs.
                    Button(ui,root,isInstalled?(unplaceConfirm?"確定撤去":"撤去"):"生成",
                        isInstalled?"kmb.freeui designunplace "+index:"kmb.freeui designspawn "+index,
                        0.844f,y-0.067f,0.950f,y-0.012f,
                        isInstalled?(unplaceConfirm?"0.64 0.20 0.12 1":"0.48 0.25 0.20 1"):"0.16 0.46 0.28 1",11);
                }
                Button(ui,root,"◀ 前","kmb.freeui designpage prev",0.33f,0.122f,0.43f,0.169f,size:10);
                Label(ui,root,(ed.FreeDesignPage+1)+" / "+(maxPage+1),0.467f,0.123f,0.57f,0.169f,11);
                Button(ui,root,"次 ▶","kmb.freeui designpage next",0.61f,0.122f,0.71f,0.169f,size:10);
            }
            Label(ui,root,_config.PauseInstalledAutoRestoration?
                "自動復元: 停止中。重複確認後 /kmb free resume で再開":"自動復元: 高速復元ON（異なるGibを1バッチでまとめて生成）",
                0.04f,0.065f,0.96f,0.106f,10,_config.PauseInstalledAutoRestoration?"1 0.79 0.52 1":"0.65 0.94 0.76 1");
            if(!string.IsNullOrEmpty(ed.Message))Label(ui,root,ed.Message,0.04f,0.012f,0.96f,0.063f,10,"1 0.82 0.55 1");
        }

        private void DrawFreeUi(BasePlayer user) {
            if(user==null||!Allowed(user))return;
            CuiHelper.DestroyUi(user,FreeUiRoot);
            var bp=B(user);var ed=E(user);if(!bp.FreeOnly)return;
            var ui=new CuiElementContainer();
            string root=ui.Add(new CuiPanel{Image={Color="0.04 0.055 0.075 0.97"},RectTransform={AnchorMin="0.10 0.09",AnchorMax="0.90 0.91"},CursorEnabled=true},"Overlay",FreeUiRoot);
            if(ed.FreeDesignListOpen){
                DrawSavedFreeDesignUi(ui,root,user,ed);
                CuiHelper.AddUi(user,ui);
                return;
            }
            Label(ui,root,"KMB FREE BUILDER v0.4.18  |  制作中 "+bp.FreeParts.Count+"パーツ（新規制作で0から開始）",0.025f,0.93f,0.70f,0.99f,15);
            Button(ui, root, "作業場復旧", "kmb.freeui workshop", 0.71f, 0.93f, 0.91f, 0.99f, "0.17 0.35 0.46 1", 11);
            Button(ui,root,"X","kmb.freeui close",0.92f,0.93f,0.99f,0.99f,"0.50 0.18 0.18 1",13);
            Rect(ui,root,0.02f,0.16f,0.43f,0.92f,"0.08 0.13 0.18 .94");
            Rect(ui,root,0.45f,0.16f,0.98f,0.92f,"0.08 0.13 0.18 .94");
            Label(ui,root,ed.FreeSwitchOpen?
                (string.IsNullOrEmpty(ed.FreeSelected)?"右クリック: パーツを新規追加":"右クリック: "+ed.FreeSelected+" の素材を切替"):
                "素材リスト (実体可否は都度検証)",0.035f,0.85f,0.42f,0.90f,12);
            string[] scopes={"gibs","scrap","mech","all"};
            string[] names={"Gib番号","スクラップ","ロボボス","全候補"};
            for(int j=0;j<scopes.Length;j++) {
                string filter=scopes[j];float left=0.035f+j*0.095f;
                Button(ui,root,names[j],"kmb.freeui filter "+filter,left,0.805f,left+0.091f,0.845f,
                    ed.FreeCatalogScope==filter?"0.19 0.46 0.37 1":"0.16 0.26 0.33 1",9);
            }
            bool numberMode=ed.FreeCatalogScope=="gibs";
            // In Gib-number mode the left list refers to KuroPrefabLab's saved ID,
            // not the internal mesh number. No naming or recapture workflow required.
            var matches=numberMode
                ? _nativePartIds.Keys.OrderBy(x=>x).Select(x=>"kplid_"+x.ToString(CultureInfo.InvariantCulture))
                    .Where(x=>string.IsNullOrEmpty(ed.FreeSearch)||x.IndexOf(ed.FreeSearch,StringComparison.OrdinalIgnoreCase)>=0).ToArray()
                : _catalogOrder.Where(k=>IsCatalogScope(k,ed.FreeCatalogScope) &&
                    (string.IsNullOrEmpty(ed.FreeSearch)||k.IndexOf(ed.FreeSearch,StringComparison.OrdinalIgnoreCase)>=0||
                     (PrefabPath(k)??"").IndexOf(ed.FreeSearch,StringComparison.OrdinalIgnoreCase)>=0)).ToArray();
            int lastPage=Math.Max(0,(matches.Length-1)/10);ed.FreeCatalogPage=Mathf.Clamp(ed.FreeCatalogPage,0,lastPage);
            for(int i=0;i<10;i++) {
                int ix=ed.FreeCatalogPage*10+i;if(ix>=matches.Length)break;
                string k=matches[ix];float y=0.755f-i*0.055f;
                string label;string command;string color;
                if(numberMode) {
                    int registeredId=int.Parse(k.Substring("kplid_".Length),CultureInfo.InvariantCulture);
                    string mappedKey=_nativePartIds[registeredId];
                    label="GIB "+registeredId.ToString("D3",CultureInfo.InvariantCulture)+"  ["+mappedKey+"]";
                    command="kmb.freeui "+(ed.FreeSwitchOpen?"switchgib ":"gibadd ")+registeredId.ToString(CultureInfo.InvariantCulture);
                    color="0.42 0.31 0.12 1";
                } else {
                    bool native=IsNativePartKey(k);bool spawnable=CatalogSpawnState(k)=="OK";
                    label=(native?"[N] ":(spawnable?"[OK] ":"[×] "))+k;
                    command="kmb.freeui "+(ed.FreeSwitchOpen?"switch ":"add ")+k;
                    color=native?"0.42 0.31 0.12 1":(spawnable?"0.18 0.29 0.34 1":"0.27 0.19 0.20 1");
                }
                if(label.Length>43)label=label.Substring(0,43);
                Button(ui,root,label,command,0.035f,y-0.047f,0.415f,y,color,10);
            }
            Button(ui,root,"Native生成診断","kmb.freeui gibdiag",0.035f,0.115f,0.223f,0.157f,"0.24 0.35 0.37 1",10);
            Button(ui,root,"選択Native復元","kmb.freeui gibrestore",0.227f,0.115f,0.415f,0.157f,"0.43 0.30 0.14 1",9);
            Button(ui,root,"◀","kmb.freeui page prev",0.03f,0.19f,0.12f,0.238f,"0.18 0.30 0.41 1",12);
            Label(ui,root,(ed.FreeCatalogPage+1)+"/"+(lastPage+1),0.172f,0.19f,0.26f,0.24f,11);
            Button(ui,root,"▶","kmb.freeui page next",0.32f,0.19f,0.42f,0.238f,"0.18 0.30 0.41 1",12);
            Label(ui,root,"パーツ一覧  "+(ed.FreeParentPick?"親を選択中":"編集中の全パーツが保存対象"),0.463f,0.84f,0.72f,0.90f,10);
            Button(ui,root,"新規制作","kmb.freeui new",0.725f,0.85f,0.839f,0.894f,"0.12 0.41 0.35 1",9);
            Button(ui,root,"パーツ全消去","kmb.freeui clear",0.845f,0.85f,0.965f,0.894f,"0.46 0.18 0.18 1",8);
            var parts=bp.FreeParts.Values.Where(x=>x!=null).OrderBy(x=>x.Id).ToArray();
            int partPages=Math.Max(0,(parts.Length-1)/9);ed.FreePartPage=Mathf.Clamp(ed.FreePartPage,0,partPages);
            for(int i=0;i<9;i++){
                int ix=ed.FreePartPage*9+i;if(ix>=parts.Length)break;
                var part=parts[ix];float y=0.805f-i*0.055f;
                Button(ui,root,(ed.FreeSelected==part.Id?"● ":"")+part.Id+" ["+part.PrefabKey+"]"+(string.IsNullOrEmpty(part.ParentId)?"":" >"+part.ParentId),
                    "kmb.freeui pick "+part.Id,0.466f,y-0.044f,0.967f,y,ed.FreeSelected==part.Id?"0.17 0.43 0.40 1":"0.16 0.26 0.33 1",10);
            }
            Button(ui,root,"◀","kmb.freeui partpage prev",0.465f,0.295f,0.54f,0.337f,size:11);
            Label(ui,root,(ed.FreePartPage+1)+"/"+(partPages+1),0.58f,0.295f,0.67f,0.337f,10);
            Button(ui,root,"▶","kmb.freeui partpage next",0.7f,0.295f,0.77f,0.337f,size:11);
            Button(ui,root,"親を選択","kmb.freeui parentpick",0.775f,0.295f,0.873f,0.337f,size:9);
            Button(ui,root,"親解除","kmb.freeui root",0.875f,0.295f,0.965f,0.337f,size:9);
            FreePart sel; if(bp.FreeParts.TryGetValue(ed.FreeSelected,out sel)&&sel!=null) {
                Label(ui,root,"選択 "+sel.Id+"  サイズ x"+sel.Scale.ToString("0.00",CultureInfo.InvariantCulture),0.468f,0.256f,0.8f,0.292f,10);
                Button(ui,root,"-","kmb.freeui scale -0.1",0.81f,0.255f,0.88f,0.289f,size:11);
                Button(ui,root,"+","kmb.freeui scale 0.1",0.89f,0.255f,0.965f,0.289f,size:11);
                string[] axes={"x","y","z"};
                for(int i=0;i<3;i++){
                    string a=axes[i];float x=0.466f+i*0.168f;
                    Button(ui,root,a.ToUpperInvariant()+"-","kmb.freeui nudge pos "+a+" -0.05",x,0.213f,x+0.075f,0.253f,size:9);
                    Button(ui,root,a.ToUpperInvariant()+"+","kmb.freeui nudge pos "+a+" 0.05",x+0.079f,0.213f,x+0.155f,0.253f,size:9);
                    Button(ui,root,a.ToUpperInvariant()+"↶","kmb.freeui nudge rot "+a+" -5",x,0.168f,x+0.075f,0.207f,size:9);
                    Button(ui,root,a.ToUpperInvariant()+"↷","kmb.freeui nudge rot "+a+" 5",x+0.079f,0.168f,x+0.155f,0.207f,size:9);
                }
            }
            Button(ui,root,"直接編集(E/左Click/R)","kmb.freeui edit",0.03f,0.06f,0.22f,0.105f,"0.12 0.47 0.49 1",11);
            Button(ui,root,"複製","kmb.freeui duplicate",0.23f,0.06f,0.38f,0.105f,size:11);
            Button(ui,root,"パーツ削除","kmb.freeui delete",0.39f,0.06f,0.54f,0.105f,"0.51 0.19 0.19 1",10);
            Button(ui,root,"全パーツ保存","kmb.freeui save",0.55f,0.06f,0.73f,0.105f,size:10);
            Button(ui,root,"旧ロボモード","kmb.freeui classic",0.74f,0.06f,0.965f,0.105f,size:10);
            Button(ui,root,"永久設置","kmb.freeui install",0.55f,0.112f,0.675f,0.155f,"0.14 0.42 0.30 1",9);
            Button(ui,root,"保存作品一覧","kmb.freeui designs",0.68f,0.112f,0.815f,0.155f,"0.16 0.29 0.42 1",9);
            Button(ui,root,"最新設置を撤去","kmb.freeui unplace",0.82f,0.112f,0.965f,0.155f,"0.42 0.21 0.18 1",9);
            Label(ui,root,ed.FreeSwitchOpen?"右クリックで素材選択: 対象があれば置換、なければ新規追加":"別作品を作るときは『新規制作』で0パーツに。『全パーツ保存』は今の制作内容を丸ごと保存",0.025f,0.015f,0.88f,0.052f,9,"0.75 0.87 1 1");
            if(!string.IsNullOrEmpty(ed.Message))Label(ui,root,ed.Message,0.55f,0.015f,0.965f,0.053f,8);
            CuiHelper.AddUi(user,ui);
        }

        private static void DrawAxis(CuiElementContainer ui,string parent,float start,string verb) {
            for(int k=0;k<3;k++){
                string axis=k==0?"x":k==1?"y":"z";
                float x=0.699f+k*0.094f;
                Button(ui,parent,axis.ToUpperInvariant()+"-","kmb.ui "+verb+" "+axis+" minus",x,start,x+0.043f,start+0.049f,size:10);
                Button(ui,parent,axis.ToUpperInvariant()+"+","kmb.ui "+verb+" "+axis+" plus",x+0.045f,start,x+0.089f,start+0.049f,size:10);
            }
        }
    }
}