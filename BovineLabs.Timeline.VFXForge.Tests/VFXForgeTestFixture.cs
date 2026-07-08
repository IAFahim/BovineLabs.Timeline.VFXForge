namespace BovineLabs.Timeline.VFXForge.Tests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using BovineLabs.Reaction.Data.Core;
    using BovineLabs.Timeline.Data;
    using BovineLabs.Timeline.EntityLinks.Data;
    using BovineLabs.Timeline.VFXForge.Data;
    using FireAlt.Core.ObjectManagement;
    using FireAlt.VFXForge;
    using FireAlt.VFXForge.Data;
    using Unity.Entities;
    using Unity.Mathematics;
    using Unity.Transforms;
#if UNITY_EDITOR
    using UnityEditor;
#endif
    using UnityEngine;
    using UnityEngine.VFX;

    /// <summary>
    /// Play-mode harness for <see cref="VFXForgeSpawnSystem"/>. This is a hand-rolled copy of the patterns in Fire Alt's
    /// <c>VFXPlayModeTestFixture</c> (which is <c>internal</c> to <c>FireAlt.VFXForge.Tests</c> and therefore not
    /// referenceable) PLUS the extra glue this timeline layer needs: the timeline spawn system, its
    /// <see cref="EndSimulationEntityCommandBufferSystem"/> for ReapJob playback, and helpers that hand-build clip
    /// entities and toggle <see cref="ClipActive"/> the way the Timeline core would at runtime.
    ///
    /// Update ordering mirrors the real per-frame pipeline:
    ///   TimelineComponentAnimationGroup (VFXForgeSpawnSystem)  ->  UpdateVFXSystemGroup (VFXTransformSystem)
    ///   ->  EndSimulation (ECB playback)  ->  PresentationSystemGroup (SyncVFXSystem, resolves deferred handles).
    /// The helpers below let a test run those stages individually so it can force the C1 "postpone" path (sync without
    /// transform) that is otherwise invisible.
    /// </summary>
    internal sealed class VFXForgeTestFixture : IDisposable
    {
        private const string PersistentVfxAssetPath =
            "Packages/com.firealt.vfx-forge/Shaders/Templates/Persistent(Single).vfx";

        private readonly List<GameObject> gameObjects = new();
        private readonly List<VFXDefinition> definitions = new();
        private readonly World previousWorld;

        private readonly SystemHandle syncSystem;
        private readonly SystemHandle transformSystem;
        private readonly SystemHandle spawnSystem;
        private readonly EndSimulationEntityCommandBufferSystem endSimEcb;

        private VFXForgeTestFixture(string worldName)
        {
            this.previousWorld = World.DefaultGameObjectInjectionWorld;
            this.World = World.DefaultGameObjectInjectionWorld = new World(worldName, WorldFlags.Game);

            // SyncVFXSystem.OnCreate creates the VFXSingleton + VFXGraphicsBuffersSingleton and seeds the cached
            // play-mode flag, so it must be created first. VFXTransformSystem + InitializeVFXSystem match the Fire Alt
            // fixture. HybridVisualEffect.Init() registers into the world synchronously via InitializeVFXSystem.Update.
            this.syncSystem = this.World.CreateSystem<SyncVFXSystem>();
            this.transformSystem = this.World.CreateSystem<VFXTransformSystem>();
            this.World.CreateSystemManaged<InitializeVFXSystem>();

            // Extra glue for the timeline layer. EndSim must exist before VFXForgeSpawnSystem.OnUpdate fetches its
            // singleton (used by ReapJob).
            this.endSimEcb = this.World.CreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
            this.spawnSystem = this.World.CreateSystem<VFXForgeSpawnSystem>();
        }

        internal World World { get; }

        /// <summary>
        /// A route that resolves to the track's bound entity itself.
        /// <para>
        /// IMPORTANT (verified against <c>EntityLinkRefExtensions.TryResolve</c> + <c>Targets.Get</c>):
        /// <c>default(EntityLinkRef)</c> has <c>ReadRootFrom = Target.None</c> (the enum's 0 value) and
        /// <c>LinkKey = 0</c>. With <c>LinkKey == 0</c>, TryResolve does <c>targets.Get(ReadRootFrom, self)</c>, and
        /// <c>Targets.Get(Target.None, self)</c> returns <c>Entity.Null</c> — so a DEFAULT route resolves to nothing
        /// and <see cref="VFXForgeSpawnSystem"/> silently skips the spawn. To resolve to the bound entity we must set
        /// <c>ReadRootFrom = Target.Self</c>, which maps to <c>self</c> regardless of the (optional) Targets component.
        /// </para>
        /// </summary>
        internal static EntityLinkRef SelfRoute => new() { ReadRootFrom = Target.Self, LinkKey = 0 };

        /// <summary>A route that reads the bound entity's <see cref="Targets.Target"/> slot (no link).</summary>
        internal static EntityLinkRef TargetSlotRoute => new() { ReadRootFrom = Target.Target, LinkKey = 0 };

        internal static IEnumerator Run(Action<VFXForgeTestFixture> test, string worldName = "VFX Forge Timeline Test World")
        {
            using (var fixture = new VFXForgeTestFixture(worldName))
            {
                test(fixture);
            }

            yield return null;
        }

        // --- Fire Alt fixture parts (replicated patterns, not the internal type) ------------------------------------

        internal VFXDefinition CreateDefinition(int id, int capacity = 8, float timeoutDuration = 30f)
        {
            var definition = ScriptableObject.CreateInstance<VFXDefinition>();
            ((IUID)definition).ID = id;
#if UNITY_EDITOR
            definition.visualEffectAsset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(PersistentVfxAssetPath);
#else
            throw new InvalidOperationException("VFX Forge play-mode tests require the Unity Editor to load package VFX assets.");
#endif
            definition.capacity = capacity;
            definition.timeoutDuration = timeoutDuration;
            definition.vfxType = VFXType.Persistent;
            definition.vfxDataType = 0;
            definition.vfxArrayDataType = 0;
            this.definitions.Add(definition);
            return definition;
        }

        internal HybridVisualEffect CreateAndRegisterVisualEffect(VFXDefinition definition, string name = "VFX Forge Timeline Test")
        {
            var gameObject = new GameObject(name);
            this.gameObjects.Add(gameObject);

            gameObject.AddComponent<VisualEffect>();
            var hybridVisualEffect = gameObject.AddComponent<HybridVisualEffect>();
            hybridVisualEffect.VFXDefinition = definition;
            hybridVisualEffect.Init();
            return hybridVisualEffect;
        }

        internal Entity CreateTrackedEntity()
        {
            var entity = this.World.EntityManager.CreateEntity(typeof(LocalToWorld));
            this.World.EntityManager.SetComponentData(
                entity, new LocalToWorld { Value = float4x4.TRS(float3.zero, quaternion.identity, 1f) });
            return entity;
        }

        internal VFXSingleton GetSingleton()
        {
            using var query = this.World.EntityManager.CreateEntityQuery(typeof(VFXSingleton));
            return query.GetSingleton<VFXSingleton>();
        }

        internal bool IsAlive(VFXKey key, TrackedEntity handle)
        {
            var singleton = this.GetSingleton();
            if (!singleton.ContainsPersistent(key))
            {
                return false;
            }

            ref var entry = ref singleton.GetPersistent(key);
            return entry.IsAlive(handle);
        }

        // --- Timeline clip helpers ----------------------------------------------------------------------------------

        /// <summary>
        /// Hand-builds the clip entity the Timeline core would bake: the four components <see cref="VFXForgeSpawnSystem"/>
        /// operates on. <see cref="VFXForgeCleanup"/> is intentionally NOT added — the system attaches it on its first
        /// update (H3 batched main-thread AddComponent), matching runtime. Created with <see cref="ClipActive"/>
        /// present-but-disabled; toggle it with <see cref="SetClipActive"/>.
        /// </summary>
        internal Entity CreateClip(VFXKey key, Entity boundEntity, EntityLinkRef route)
        {
            var em = this.World.EntityManager;
            var clip = em.CreateEntity(
                typeof(TrackBinding), typeof(VFXForgeClipData), typeof(VFXForgeRuntimeState), typeof(ClipActive));
            em.SetComponentData(clip, new TrackBinding { Value = boundEntity });
            em.SetComponentData(clip, new VFXForgeClipData { Key = key, Route = route });
            em.SetComponentData(clip, new VFXForgeRuntimeState { Tracked = TrackedEntity.Null });
            em.SetComponentEnabled<ClipActive>(clip, false);
            return clip;
        }

        internal void SetClipActive(Entity clip, bool active)
        {
            this.World.EntityManager.SetComponentEnabled<ClipActive>(clip, active);
        }

        internal TrackedEntity GetTracked(Entity clip)
        {
            return this.World.EntityManager.GetComponentData<VFXForgeRuntimeState>(clip).Tracked;
        }

        internal bool HasCleanup(Entity clip)
        {
            return this.World.EntityManager.HasComponent<VFXForgeCleanup>(clip);
        }

        /// <summary>The cleanup shadow's tracked handle, or <see cref="TrackedEntity.Null"/> when absent.</summary>
        internal TrackedEntity GetCleanupTracked(Entity clip)
        {
            var em = this.World.EntityManager;
            return em.HasComponent<VFXForgeCleanup>(clip)
                ? em.GetComponentData<VFXForgeCleanup>(clip).Tracked
                : TrackedEntity.Null;
        }

        internal int CleanupEntityCount()
        {
            using var query = this.World.EntityManager.CreateEntityQuery(
                new EntityQueryDesc { All = new[] { ComponentType.ReadOnly<VFXForgeCleanup>() } });
            return query.CalculateEntityCount();
        }

        // --- Controlled update stages -------------------------------------------------------------------------------

        /// <summary>
        /// Runs the timeline spawn system (spawn/kill/reap jobs + the H3 main-thread cleanup attach), then plays back
        /// the EndSimulation ECB (ReapJob's cleanup strip). Jobs are completed before playback so the strip can't race
        /// the reap job.
        /// </summary>
        internal void UpdateSpawnSystem()
        {
            this.spawnSystem.Update(this.World.Unmanaged);
            this.World.EntityManager.CompleteAllTrackedJobs();
            this.endSimEcb.Update();
            this.World.EntityManager.CompleteAllTrackedJobs();
        }

        /// <summary>Runs only VFXTransformSystem (stamps DidTransformSystemRun + pose onto deferred spawns).</summary>
        internal void UpdateTransform()
        {
            this.transformSystem.Update(this.World.Unmanaged);
            this.World.EntityManager.CompleteAllTrackedJobs();
        }

        /// <summary>
        /// Runs only SyncVFXSystem (bumps the shared version and resolves deferred->resolved). Running this WITHOUT a
        /// preceding <see cref="UpdateTransform"/> forces the ResolvePersistentJob "postpone" path (the C1 window).
        /// </summary>
        internal void UpdateSync()
        {
            this.syncSystem.Update(this.World.Unmanaged);
            this.World.EntityManager.CompleteAllTrackedJobs();
        }

        /// <summary>Transform then Sync — the normal Fire Alt <c>UpdateSystems()</c> pairing that resolves a spawn.</summary>
        internal void UpdateVFXSystems()
        {
            this.UpdateTransform();
            this.UpdateSync();
        }

        public void Dispose()
        {
            for (var i = this.gameObjects.Count - 1; i >= 0; i--)
            {
                if (this.gameObjects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(this.gameObjects[i]);
                }
            }

            for (var i = this.definitions.Count - 1; i >= 0; i--)
            {
                if (this.definitions[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(this.definitions[i]);
                }
            }

            if (this.World.IsCreated)
            {
                this.World.EntityManager.CompleteAllTrackedJobs();
                this.World.DestroyAllSystemsAndLogException(out _);
                this.World.Dispose();
            }

            World.DefaultGameObjectInjectionWorld = this.previousWorld;
        }
    }
}
