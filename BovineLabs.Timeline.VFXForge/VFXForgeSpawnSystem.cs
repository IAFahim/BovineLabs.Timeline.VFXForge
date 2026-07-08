namespace BovineLabs.Timeline.VFXForge
{
    using BovineLabs.Core;
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

        // Clip entities missing their (ICleanupComponentData) cleanup mirror. Bakers can't add a cleanup component,
        // so it's added here in a single batched structural pass instead of per-spawn in the hot path (H3).
        private EntityQuery addCleanupQuery;

        /// <inheritdoc/>
        [BurstCompile]
        public void OnCreate(ref SystemState state)
        {
            this.targetsLookup = state.GetUnsafeComponentLookup<Targets>(true);
            this.linkSourceLookup = state.GetUnsafeComponentLookup<EntityLinkSource>(true);
            this.linkLookup = state.GetUnsafeBufferLookup<EntityLinkEntry>(true);

            this.addCleanupQuery = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<VFXForgeClipData>()
                .WithNone<VFXForgeCleanup>()
                .Build(ref state);

            state.RequireForUpdate<VFXSingleton>();

            // Only run when there is something to drive: live clips (VFXForgeClipData) OR orphaned cleanup shadows
            // (VFXForgeCleanup surviving a destroyed clip entity). The reaper must still run after the last clip entity
            // is destroyed to free their orphaned persistent instances, so the cleanup query is included in the OR.
            var clipQuery = state.GetEntityQuery(ComponentType.ReadOnly<VFXForgeClipData>());
            var cleanupQuery = state.GetEntityQuery(ComponentType.ReadOnly<VFXForgeCleanup>());
            state.RequireAnyForUpdate(clipQuery, cleanupQuery);
        }

        /// <inheritdoc/>
        public void OnUpdate(ref SystemState state)
        {
            // H3: ensure every clip entity carries the destruction-surviving cleanup mirror from frame zero. A single
            // batched structural add (main thread, before jobs) replaces the per-spawn ECB add — no structural change
            // in the spawn/kill hot path, and no destroyed-clip-entity ECB playback hazard. Done before the lookup
            // updates below because the structural change invalidates them.
            if (!this.addCleanupQuery.IsEmptyIgnoreFilter)
            {
                state.EntityManager.AddComponent<VFXForgeCleanup>(this.addCleanupQuery);
            }

            this.targetsLookup.Update(ref state);
            this.linkSourceLookup.Update(ref state);
            this.linkLookup.Update(ref state);

            var vfx = SystemAPI.GetSingletonRW<VFXSingleton>().ValueRW.AsParallelWriter();
            var hasLogger = SystemAPI.TryGetSingleton<BLLogger>(out var logger);

            // Spawn allocates instance indices off the entry, so keep it single-threaded (mirrors the decal InitJob).
            state.Dependency = new SpawnJob
            {
                Vfx = vfx,
                TargetsLookup = this.targetsLookup,
                LinkSources = this.linkSourceLookup,
                Links = this.linkLookup,
                LogEnabled = hasLogger,
                Logger = logger,
            }.Schedule(state.Dependency);

            state.Dependency = new KillJob { Vfx = vfx }.Schedule(state.Dependency);

            // Reap orphans: a destroyed clip entity leaves only the cleanup shadow (ICleanupComponentData survives
            // destruction). Kill its persistent instance and remove the shadow so the entity is finally freed. The ECB
            // only runs post-destruction, where the cleanup component keeps the entity alive, so the playback is safe.
            var ecb = SystemAPI.GetSingleton<EndSimulationEntityCommandBufferSystem.Singleton>()
                .CreateCommandBuffer(state.WorldUnmanaged);
            state.Dependency = new ReapJob { Vfx = vfx, Ecb = ecb }.Schedule(state.Dependency);
        }

        [BurstCompile]
        [WithAll(typeof(ClipActive))]
        private partial struct SpawnJob : IJobEntity
        {
            private const byte WarnBinding = 1 << 0;
            private const byte WarnKey = 1 << 1;
            private const byte WarnRoute = 1 << 2;
            private const byte WarnTarget = 1 << 3;

            public VFXSingleton.ParallelWriter Vfx;

            [ReadOnly]
            public UnsafeComponentLookup<Targets> TargetsLookup;

            [ReadOnly]
            public UnsafeComponentLookup<EntityLinkSource> LinkSources;

            [ReadOnly]
            public UnsafeBufferLookup<EntityLinkEntry> Links;

            public bool LogEnabled;
            public BLLogger Logger;

            private void Execute(
                in TrackBinding binding,
                in VFXForgeClipData data,
                ref VFXForgeRuntimeState rt,
                ref VFXForgeCleanup cleanup)
            {
                // Retry the spawn every active frame until it succeeds, instead of only on the rising edge —
                // a one-frame resolution miss (binding/key/link not ready) no longer silently kills the whole
                // clip. The latch below self-clears on deactivation (KillJob) so re-activations spawn fresh.
                // Gate on IsValid, not !=Null: on pool exhaustion Spawn returns an invalid-but-non-Null handle
                // that must NOT be treated as a live instance.
                if (rt.Tracked.IsValid)
                {
                    // H6 liveness-aware latch: still own a handle — but only keep it if the instance is actually
                    // alive. If the registry no longer has the key (subscene with the graph unloaded) or the
                    // instance was reaped (routed target died mid-clip), the handle is stale: drop it and fall
                    // through to respawn against the re-resolved target. Short-circuit protects GetPersistent's
                    // registered-key assert.
                    //
                    // Postpone disambiguation: an UNRESOLVED previous-version deferred handle (SyncVFXSystem's
                    // HasPendingTransform postpone path) also reports !IsAlive — it is indistinguishable from a
                    // reaped handle through IsAlive alone. While the entry still has pending spawn requests our
                    // spawn may be among them, so treat the handle as live-in-flight (return, no respawn — else
                    // we'd double-spawn). Once a resolve pass has run (no pending requests) an unresolvable
                    // handle is provably dead and safe to clear.
                    if (this.Vfx.ContainsPersistent(data.Key))
                    {
                        ref var latchedEntry = ref this.Vfx.GetPersistent(data.Key);
                        if (latchedEntry.IsAlive(rt.Tracked) || latchedEntry.HasPendingRequests)
                        {
                            return;
                        }
                    }

                    rt.Tracked = TrackedEntity.Null;
                    cleanup.Tracked = TrackedEntity.Null;
                }

                if (binding.Value == Entity.Null)
                {
                    this.Warn(ref rt, WarnBinding, (FixedString512Bytes)
                        "VFXForgeClip: track binding is Entity.Null. Bind the VFXForgeTrack to a TargetsAuthoring entity so the effect has somewhere to play.");
                    return;
                }

                // Definition not registered as a persistent VFX in this world (no HybridVisualEffect for its key, or
                // it is an Instant definition). Nothing valid to spawn.
                if (!this.Vfx.ContainsPersistent(data.Key))
                {
                    if (this.ShouldWarn(ref rt, WarnKey) && this.LogEnabled)
                    {
                        var msg = new FixedString512Bytes();
                        msg.Append((FixedString512Bytes)"VFXForgeClip: VFXKey ");
                        msg.Append((int)data.Key.Value);
                        msg.Append((FixedString512Bytes)
                            " is not registered. Load a HybridVisualEffect for this VFXDefinition in a subscene (and set the definition type to Persistent) so its key is registered.");
                        this.Logger.LogWarning512(msg);
                    }

                    return;
                }

                if (!VFXForgeTargetResolver.TryResolveLinkedTarget(
                        data.Route, binding.Value,
                        this.TargetsLookup, this.LinkSources, this.Links, out var target))
                {
                    if (this.ShouldWarn(ref rt, WarnRoute) && this.LogEnabled)
                    {
                        var msg = new FixedString512Bytes();
                        msg.Append((FixedString512Bytes)"VFXForgeClip: could not resolve the target route for VFXKey ");
                        msg.Append((int)data.Key.Value);
                        msg.Append((FixedString512Bytes)
                            ". The EntityLink route did not resolve to an entity - check the clip Route/link on the bound target.");
                        this.Logger.LogWarning512(msg);
                    }

                    return;
                }

                if (target == Entity.Null)
                {
                    if (this.ShouldWarn(ref rt, WarnTarget) && this.LogEnabled)
                    {
                        var msg = new FixedString512Bytes();
                        msg.Append((FixedString512Bytes)"VFXForgeClip: the resolved target for VFXKey ");
                        msg.Append((int)data.Key.Value);
                        msg.Append((FixedString512Bytes)
                            " is Entity.Null. The route resolved but points at nothing - check the linked target still exists.");
                        this.Logger.LogWarning512(msg);
                    }

                    return;
                }

                // trackingDuration 0 = lifetime fully controlled by the clip (killed on the end edge below).
                var spawned = this.Vfx.GetPersistent(data.Key).Spawn(target, 0f);
                if (!spawned.IsValid)
                {
                    return; // pool exhausted — leave rt.Tracked unset so we retry next frame (no warn/latch, intentional)
                }

                rt.Tracked = spawned;

                // Write the destruction-surviving shadow's value in place (component was added in OnUpdate). Carries
                // the Key because VFXForgeClipData is gone once the entity dies, and ReapJob keys off Tracked != Null.
                cleanup.Key = data.Key;
                cleanup.Tracked = spawned;
            }

            // One-shot cause latch: returns true only the first time a cause is seen on this clip entity.
            private bool ShouldWarn(ref VFXForgeRuntimeState rt, byte bit)
            {
                if ((rt.WarnedMask & bit) != 0)
                {
                    return false;
                }

                rt.WarnedMask |= bit;
                return true;
            }

            // Convenience for constant-message causes: latch + log in one call.
            private void Warn(ref VFXForgeRuntimeState rt, byte bit, in FixedString512Bytes message)
            {
                if (this.ShouldWarn(ref rt, bit) && this.LogEnabled)
                {
                    this.Logger.LogWarning512(message);
                }
            }
        }

        [BurstCompile]
        [WithDisabled(typeof(ClipActive))]
        private partial struct KillJob : IJobEntity
        {
            public VFXSingleton.ParallelWriter Vfx;

            private void Execute(in VFXForgeClipData data, ref VFXForgeRuntimeState rt, ref VFXForgeCleanup cleanup)
            {
                if (rt.Tracked.Equals(TrackedEntity.Null))
                {
                    return;
                }

                // C1/H6: only clear the handle when the kill actually landed (or the instance is provably gone).
                // TryKill legitimately returns false for a previous-version deferred handle whose spawn has not yet
                // been resolved (SyncVFXSystem's HasPendingTransform postpone path); clearing then would orphan an
                // immortal VFX and leak its pool slot. Treat "entry no longer registered" as killed.
                //
                // An unresolved-deferred handle and a reaped handle BOTH report TryKill=false + IsAlive=false, but
                // need opposite handling (retain-and-retry vs clear). Disambiguate with HasPendingRequests: while
                // the entry still has unresolved spawn requests ours may be among them — retry next frame. After a
                // resolve pass (no pending requests) an unresolvable handle is provably dead: clear it.
                var killed = true;
                if (this.Vfx.ContainsPersistent(data.Key))
                {
                    ref var entry = ref this.Vfx.GetPersistent(data.Key);
                    killed = entry.TryKill(rt.Tracked) || (!entry.IsAlive(rt.Tracked) && !entry.HasPendingRequests);
                }

                if (!killed)
                {
                    return; // spawn still unresolved — keep the handle + shadow and retry next frame
                }

                rt.Tracked = TrackedEntity.Null;
                cleanup.Tracked = TrackedEntity.Null;
            }
        }

        // Reaps orphaned instances: the clip entity was destroyed while active, leaving only the cleanup shadow
        // (VFXForgeRuntimeState/VFXForgeClipData are gone). Kill the persistent instance and strip the shadow so the
        // entity is finally freed. This is the only remaining structural (ECB) consumer.
        [BurstCompile]
        [WithNone(typeof(VFXForgeRuntimeState))]
        private partial struct ReapJob : IJobEntity
        {
            public VFXSingleton.ParallelWriter Vfx;
            public EntityCommandBuffer Ecb;

            private void Execute(Entity entity, in VFXForgeCleanup cleanup)
            {
                // Nothing was ever spawned on this clip (default shadow added in OnUpdate) — strip immediately.
                if (cleanup.Tracked.Equals(TrackedEntity.Null))
                {
                    this.Ecb.RemoveComponent<VFXForgeCleanup>(entity);
                    return;
                }

                // C1: keep the shadow and retry next frame if the kill failed on a still-live handle (unresolved
                // spawn in flight — see KillJob for the HasPendingRequests disambiguation). If the kill landed,
                // the entry is gone, or the instance is provably dead after a resolve pass, it's reaped.
                var reaped = true;
                if (this.Vfx.ContainsPersistent(cleanup.Key))
                {
                    ref var entry = ref this.Vfx.GetPersistent(cleanup.Key);
                    reaped = entry.TryKill(cleanup.Tracked) || (!entry.IsAlive(cleanup.Tracked) && !entry.HasPendingRequests);
                }

                if (!reaped)
                {
                    return;
                }

                this.Ecb.RemoveComponent<VFXForgeCleanup>(entity);
            }
        }
    }
}
