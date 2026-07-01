namespace BovineLabs.Timeline.VFXForge
{
    using BovineLabs.Core.Extensions;
    using BovineLabs.Core.Iterators;
    using BovineLabs.Reaction.Data.Core;
    using BovineLabs.Timeline.Data;
    using BovineLabs.Timeline.EntityLinks.Data;
    using BovineLabs.Timeline.VFXForge.Data;
    using FireAlt.VFXForge;
    using FireAlt.VFXForge.Data;
    using Unity.Burst;
    using Unity.Collections;
    using Unity.Entities;

    /// <summary>
    /// Drives <c>VFXForgeClip</c>. On a clip's rising edge (active this frame, inactive last frame) it resolves the
    /// clip's target entity and spawns a fresh Fire Alt persistent VFX instance tracking that entity (so the effect
    /// follows it; Fire Alt's <c>VFXTransformSystem</c> copies the entity's LocalToWorld each frame). When the clip
    /// becomes inactive (its end, or the timeline stopping) the spawned instance is killed. Because every activation
    /// spawns a brand-new instance, the effect always plays from the start — solving the GameObjects-Activation pain
    /// where re-enabling a finished/composite VFX never replayed.
    ///
    /// Runs in <see cref="TimelineComponentAnimationGroup"/> before <c>ClipActivePreviousSystem</c> records the new
    /// state, mirroring the edge pattern used by the Audio Impact Stinger and the Essence event tracks.
    /// </summary>
    [UpdateInGroup(typeof(TimelineComponentAnimationGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation | WorldSystemFilterFlags.ClientSimulation)]
    public partial struct VFXForgeSpawnSystem : ISystem
    {
        private UnsafeComponentLookup<Targets> targetsLookup;
        private UnsafeComponentLookup<EntityLinkSource> linkSourceLookup;
        private UnsafeBufferLookup<EntityLinkEntry> linkLookup;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            this.targetsLookup = state.GetUnsafeComponentLookup<Targets>(true);
            this.linkSourceLookup = state.GetUnsafeComponentLookup<EntityLinkSource>(true);
            this.linkLookup = state.GetUnsafeBufferLookup<EntityLinkEntry>(true);

            state.RequireForUpdate<VFXSingleton>();
            state.RequireForUpdate<VFXForgeClipData>();
        }

        /// <inheritdoc/>
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            this.targetsLookup.Update(ref state);
            this.linkSourceLookup.Update(ref state);
            this.linkLookup.Update(ref state);

            var vfx = SystemAPI.GetSingletonRW<VFXSingleton>().ValueRW.AsParallelWriter();

            // Spawn allocates instance indices off the entry, so keep it single-threaded (mirrors the decal InitJob).
            state.Dependency = new SpawnJob
            {
                Vfx = vfx,
                TargetsLookup = this.targetsLookup,
                LinkSources = this.linkSourceLookup,
                Links = this.linkLookup,
            }.Schedule(state.Dependency);

            state.Dependency = new KillJob { Vfx = vfx }.Schedule(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        [WithPresent(typeof(ClipActivePrevious))]
        private partial struct SpawnJob : IJobEntity
        {
            public VFXSingleton.ParallelWriter Vfx;

            [ReadOnly]
            public UnsafeComponentLookup<Targets> TargetsLookup;

            [ReadOnly]
            public UnsafeComponentLookup<EntityLinkSource> LinkSources;

            [ReadOnly]
            public UnsafeBufferLookup<EntityLinkEntry> Links;

            private void Execute(
                in TrackBinding binding,
                in VFXForgeClipData data,
                ref VFXForgeRuntimeState rt,
                EnabledRefRO<ClipActivePrevious> clipActivePrevious)
            {
                // Rising edge only: active this frame, inactive last frame.
                if (clipActivePrevious.ValueRO)
                {
                    return;
                }

                // Already have a live instance for this activation (defensive against double-fire).
                if (!rt.Tracked.Equals(TrackedEntity.Null))
                {
                    return;
                }

                if (binding.Value == Entity.Null)
                {
                    return;
                }

                // Definition not registered as a persistent VFX in this world (no HybridVisualEffect for its key, or
                // it is an Instant definition). Silently skip — there is nothing valid to spawn.
                if (!this.Vfx.ContainsPersistent(data.Key))
                {
                    return;
                }

                if (!VFXForgeTargetResolver.TryResolveLinkedTarget(
                        data.RouteTo, data.RouteLinkKey, binding.Value,
                        this.TargetsLookup, this.LinkSources, this.Links, out var target)
                    || target == Entity.Null)
                {
                    return;
                }

                // trackingDuration 0 = lifetime fully controlled by the clip (killed on the end edge below).
                rt.Tracked = this.Vfx.GetPersistent(data.Key).Spawn(target, 0f);
            }
        }

        [BurstCompile]
        [WithDisabled(typeof(ClipActive))]
        private partial struct KillJob : IJobEntity
        {
            public VFXSingleton.ParallelWriter Vfx;

            private void Execute(in VFXForgeClipData data, ref VFXForgeRuntimeState rt)
            {
                if (rt.Tracked.Equals(TrackedEntity.Null))
                {
                    return;
                }

                if (this.Vfx.ContainsPersistent(data.Key))
                {
                    this.Vfx.GetPersistent(data.Key).TryKill(rt.Tracked);
                }

                rt.Tracked = TrackedEntity.Null;
            }
        }
    }
}
