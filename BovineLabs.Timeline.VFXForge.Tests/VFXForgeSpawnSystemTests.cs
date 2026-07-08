namespace BovineLabs.Timeline.VFXForge.Tests
{
    using System.Collections;
    using BovineLabs.Reaction.Data.Core;
    using FireAlt.VFXForge;
    using FireAlt.VFXForge.Data;
    using NUnit.Framework;
    using Unity.Entities;
    using UnityEngine.TestTools;

    /// <summary>
    /// Play-mode regression suite for <see cref="VFXForgeSpawnSystem"/>. Each test is pinned to a named risk from the
    /// package TODO. Tests drive the pipeline by toggling <see cref="BovineLabs.Timeline.Data.ClipActive"/> on
    /// hand-built clip entities (no Timeline asset needed) and asserting against the Fire Alt persistent-instance
    /// liveness (<c>PersistentVFXEntry.IsAlive</c>) — the deterministic source of truth, independent of the managed
    /// VisualEffect particle read-back.
    /// </summary>
    public class VFXForgeSpawnSystemTests
    {
        // Test 1 — Baseline. Enable a clip, resolve, assert alive; disable, assert dead + handle cleared.
        [UnityTest]
        public IEnumerator SpawnKill_HappyPath()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                var def = f.CreateDefinition(3001, capacity: 4);
                f.CreateAndRegisterVisualEffect(def);
                var bound = f.CreateTrackedEntity();
                var clip = f.CreateClip(def, bound, VFXForgeTestFixture.SelfRoute);

                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();  // spawn queued (deferred), cleanup shadow attached
                f.UpdateVFXSystems();   // transform stamps, sync resolves deferred -> resolved

                var handle = f.GetTracked(clip);
                Assert.IsTrue(handle.IsValid, "clip should latch a valid spawned handle");
                Assert.IsTrue(f.IsAlive(def, handle), "VFX should be alive after spawn + resolve");
                Assert.IsTrue(f.HasCleanup(clip), "cleanup shadow should be attached while alive");
                Assert.IsTrue(f.GetCleanupTracked(clip).Equals(handle), "cleanup shadow mirrors the live handle");

                f.SetClipActive(clip, false);
                f.UpdateSpawnSystem();  // KillJob queues the kill
                f.UpdateVFXSystems();   // sync processes the kill -> instance dead

                Assert.IsFalse(f.IsAlive(def, handle), "VFX should be dead after the clip deactivates");
                Assert.IsTrue(f.GetTracked(clip).Equals(TrackedEntity.Null), "handle should clear on a confirmed kill");
            });
        }

        // Test 2 — C1 (the flagship regression). A kill issued while the spawn is still an unresolved, previous-version
        // deferred handle (the SyncVFXSystem "postpone" path) must be RETRIED, never cleared. Clearing it orphans an
        // immortal VFX and leaks the pool slot. STRICT assertions: this test must be RED on code that clears the handle
        // on a failed kill, GREEN once the kill is truly result-aware for the postpone case.
        [UnityTest]
        public IEnumerator Kill_WhileSpawnUnresolved_StillKillsAfterResolution()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                var def = f.CreateDefinition(3002, capacity: 1);
                f.CreateAndRegisterVisualEffect(def);
                var bound = f.CreateTrackedEntity();
                var clip = f.CreateClip(def, bound, VFXForgeTestFixture.SelfRoute);

                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();          // spawn queued (deferred, version V)
                var handle = f.GetTracked(clip);
                Assert.IsTrue(handle.IsValid, "spawn should latch a deferred handle");

                // Force the postpone path: SyncVFXSystem bumps the version and sees a pending spawn with no
                // DidTransformSystemRun stamp (VFXTransformSystem deliberately skipped), so it defers the whole entry.
                // The handle is now a PREVIOUS-version deferred key that cannot resolve until a transform pass runs.
                f.UpdateSync();                 // NO transform on purpose

                f.SetClipActive(clip, false);
                f.UpdateSpawnSystem();          // KillJob: TryKill fails (unresolved) -> MUST keep handle and retry
                f.UpdateVFXSystems();           // transform + sync: the pending spawn finally resolves
                f.UpdateSpawnSystem();          // KillJob retry: handle now resolves -> kill queued
                f.UpdateVFXSystems();           // sync processes the kill -> instance dead

                Assert.IsFalse(f.IsAlive(def, handle),
                    "orphaned immortal VFX: a failed kill on the unresolved deferred spawn cleared the handle, then " +
                    "the postponed spawn resolved into an instance nothing can ever kill");

                // Capacity-1 slot must be free again. A leaked orphan keeps UsedCapacity at 1, so a fresh Spawn fails.
                var singleton = f.GetSingleton();
                ref var entry = ref singleton.GetPersistent(def);
                Assert.IsTrue(entry.Spawn(bound, 0f).IsValid, "pool slot leaked — capacity-1 entry cannot spawn again");
            });
        }

        // Test 3 — H3 reap. Destroying an active clip entity leaves only the (ICleanupComponentData) shadow; ReapJob
        // must kill the orphaned instance and strip the shadow so the entity is fully freed.
        [UnityTest]
        public IEnumerator Reap_OnClipEntityDestroy()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                var def = f.CreateDefinition(3003, capacity: 4);
                f.CreateAndRegisterVisualEffect(def);
                var bound = f.CreateTrackedEntity();
                var clip = f.CreateClip(def, bound, VFXForgeTestFixture.SelfRoute);

                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();
                var handle = f.GetTracked(clip);
                Assert.IsTrue(f.IsAlive(def, handle), "VFX alive before the clip is destroyed");

                f.World.EntityManager.DestroyEntity(clip);

                // Reap within 2 iterations: ReapJob queues the kill + strips the shadow, sync finalises the kill.
                var dead = false;
                var stripped = false;
                for (var i = 0; i < 2 && !(dead && stripped); i++)
                {
                    f.UpdateSpawnSystem();
                    f.UpdateVFXSystems();
                    dead = !f.IsAlive(def, handle);
                    stripped = f.CleanupEntityCount() == 0;
                }

                Assert.IsTrue(dead, "orphaned VFX should be reaped after the clip entity is destroyed");
                Assert.IsTrue(stripped, "cleanup shadow (and its entity) should be gone once reaped");
            });
        }

        // Test 4 — Pool exhaustion + retry. Capacity 1, two active clips: one wins the slot, the other keeps retrying
        // with an unlatched (Null) handle. Killing the winner frees the slot; the loser becomes alive within 2 frames.
        [UnityTest]
        public IEnumerator PoolExhaustion_RetryUntilFree()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                var def = f.CreateDefinition(3004, capacity: 1);
                f.CreateAndRegisterVisualEffect(def);
                var boundA = f.CreateTrackedEntity();
                var boundB = f.CreateTrackedEntity();
                var clipA = f.CreateClip(def, boundA, VFXForgeTestFixture.SelfRoute);
                var clipB = f.CreateClip(def, boundB, VFXForgeTestFixture.SelfRoute);

                f.SetClipActive(clipA, true);
                f.SetClipActive(clipB, true);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();

                // Exactly one clip wins the single slot; the loser's handle stays Null (no invalid latch, retried).
                var trackedA = f.GetTracked(clipA);
                var trackedB = f.GetTracked(clipB);
                Assert.AreNotEqual(trackedA.IsValid, trackedB.IsValid,
                    "with capacity 1 exactly one of the two clips should own the slot");

                var winner = trackedA.IsValid ? clipA : clipB;
                var loser = trackedA.IsValid ? clipB : clipA;
                var winnerHandle = f.GetTracked(winner);
                Assert.IsTrue(f.IsAlive(def, winnerHandle), "the winning clip's VFX is alive");
                Assert.IsTrue(f.GetTracked(loser).Equals(TrackedEntity.Null),
                    "the losing clip must not latch an invalid handle — it retries with a Null handle");

                // Free the slot; the loser should acquire it within 2 iterations.
                f.SetClipActive(winner, false);
                var loserAlive = false;
                for (var i = 0; i < 2 && !loserAlive; i++)
                {
                    f.UpdateSpawnSystem();
                    f.UpdateVFXSystems();
                    var lh = f.GetTracked(loser);
                    loserAlive = lh.IsValid && f.IsAlive(def, lh);
                }

                Assert.IsTrue(loserAlive, "the retrying clip should spawn once the slot frees");
                Assert.IsFalse(f.IsAlive(def, winnerHandle), "the killed winner's instance stays dead");
            });
        }

        // Test 5 — H6 liveness latch. When the routed target dies mid-clip (core reaps the instance), the clip must not
        // keep a valid-but-dead latch forever: it detects the dead handle via IsAlive and respawns against the
        // re-resolved target. Binding stays alive; its Targets.Target slot is re-pointed from a destroyed entity to a
        // fresh one, so re-resolution yields a live target and a genuine respawn.
        [UnityTest]
        public IEnumerator TargetDestroyed_MidClip_Respawns()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                var def = f.CreateDefinition(3005, capacity: 4);
                f.CreateAndRegisterVisualEffect(def);

                var em = f.World.EntityManager;
                var targetA = f.CreateTrackedEntity();
                var targetC = f.CreateTrackedEntity();

                // Binding carries a Targets component; the clip routes through its Target slot (initially -> A).
                var binding = em.CreateEntity(typeof(Targets));
                em.SetComponentData(binding, new Targets { Target = targetA });
                var clip = f.CreateClip(def, binding, VFXForgeTestFixture.TargetSlotRoute);

                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();
                var handleA = f.GetTracked(clip);
                Assert.IsTrue(f.IsAlive(def, handleA), "VFX tracking target A is alive");

                // Target A dies -> core marks the instance dead (reaped).
                em.DestroyEntity(targetA);
                f.UpdateVFXSystems();
                Assert.IsFalse(f.IsAlive(def, handleA), "instance dies when its tracked target A is destroyed");

                // Re-point the route to a live target C, then let the clip re-run.
                em.SetComponentData(binding, new Targets { Target = targetC });
                f.UpdateSpawnSystem();  // SpawnJob: dead handle detected (IsAlive false) -> clear + respawn against C
                f.UpdateVFXSystems();

                var handleC = f.GetTracked(clip);
                Assert.IsTrue(handleC.IsValid, "clip should respawn a fresh handle after its target died");
                Assert.IsFalse(handleC.Equals(handleA), "the respawn must be a brand-new handle, not the dead latch");
                Assert.IsFalse(f.IsAlive(def, handleA), "no stale valid-but-dead latch survives");
                Assert.IsTrue(f.IsAlive(def, handleC), "the respawned VFX tracks the re-resolved target C and is alive");
            });
        }

        // Test 6 — Loop reuse. enable -> disable -> enable across frames spawns a brand-new instance each time and
        // never leaves two instances alive at once. Capacity 2 makes a double-alive bug observable. The cleanup shadow
        // must track the runtime handle in lockstep at every phase.
        [UnityTest]
        public IEnumerator LoopingClip_SingleInstance()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                var def = f.CreateDefinition(3006, capacity: 2);
                f.CreateAndRegisterVisualEffect(def);
                var bound = f.CreateTrackedEntity();
                var clip = f.CreateClip(def, bound, VFXForgeTestFixture.SelfRoute);

                // Phase 1: enable.
                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();
                var handle1 = f.GetTracked(clip);
                Assert.IsTrue(f.IsAlive(def, handle1), "first activation is alive");
                Assert.IsTrue(f.GetCleanupTracked(clip).Equals(handle1), "cleanup mirrors handle after phase 1");

                // Phase 2: disable.
                f.SetClipActive(clip, false);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();
                Assert.IsFalse(f.IsAlive(def, handle1), "first instance dies on deactivation");
                Assert.IsTrue(f.GetTracked(clip).Equals(TrackedEntity.Null), "handle cleared after phase 2");
                Assert.IsTrue(f.GetCleanupTracked(clip).Equals(f.GetTracked(clip)),
                    "cleanup shadow matches the (Null) runtime handle after phase 2");

                // Phase 3: enable again -> fresh instance, and the first must already be dead (single live instance).
                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();
                var handle2 = f.GetTracked(clip);
                Assert.IsTrue(handle2.IsValid, "re-activation spawns a fresh handle");
                Assert.IsFalse(handle2.Equals(handle1), "re-activation is a brand-new instance");
                Assert.IsFalse(f.IsAlive(def, handle1), "the previous instance is not still alive alongside the new one");
                Assert.IsTrue(f.IsAlive(def, handle2), "the new instance is alive");
                Assert.IsTrue(f.GetCleanupTracked(clip).Equals(handle2), "cleanup mirrors the latest handle after phase 3");
            });
        }

        // Test 7 — Unregistered key. A clip whose key was never registered (no HybridVisualEffect) must be a silent,
        // throw-free no-op that never latches a handle. Warning text is owned by the fix agent, so log failures are
        // ignored rather than asserted.
        [UnityTest]
        public IEnumerator UnregisteredKey_NoThrow()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                LogAssert.ignoreFailingMessages = true;

                var bound = f.CreateTrackedEntity();
                var clip = f.CreateClip((VFXKey)6001, bound, VFXForgeTestFixture.SelfRoute); // never registered

                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();
                Assert.IsTrue(f.GetTracked(clip).Equals(TrackedEntity.Null), "no handle latched for an unregistered key");

                f.SetClipActive(clip, false);
                f.UpdateSpawnSystem();
                f.UpdateVFXSystems();
                Assert.IsTrue(f.GetTracked(clip).Equals(TrackedEntity.Null), "still no handle after a full loop; no throw");
            });
        }

        // Test 8 — Pause / resume (generalises C1). While "paused" the sim group (spawn + transform) is frozen and only
        // presentation (sync) keeps ticking, so a wanted kill can't be issued and the spawn stays postponed across
        // several version bumps. On resume the kill must still eventually land. STRICT: red until the postpone-window
        // kill is truly result-aware.
        [UnityTest]
        public IEnumerator Pause_Resume()
        {
            yield return VFXForgeTestFixture.Run(f =>
            {
                var def = f.CreateDefinition(3008, capacity: 1);
                f.CreateAndRegisterVisualEffect(def);
                var bound = f.CreateTrackedEntity();
                var clip = f.CreateClip(def, bound, VFXForgeTestFixture.SelfRoute);

                f.SetClipActive(clip, true);
                f.UpdateSpawnSystem();          // spawn queued (deferred)
                var handle = f.GetTracked(clip);
                Assert.IsTrue(handle.IsValid, "spawn latched a deferred handle");

                f.SetClipActive(clip, false);   // kill is now wanted...

                // PAUSE: only SyncVFXSystem ticks (version advances); no spawn system (kill never issued) and no
                // transform (spawn never resolves). Generalises the single postpone tick of test 2 to several frames.
                for (var i = 0; i < 3; i++)
                {
                    f.UpdateSync();
                }

                // RESUME.
                f.UpdateSpawnSystem();          // kill attempt on the still-unresolved handle -> must retry, not clear
                f.UpdateVFXSystems();           // pending spawn resolves
                f.UpdateSpawnSystem();          // kill retry lands
                f.UpdateVFXSystems();

                Assert.IsFalse(f.IsAlive(def, handle), "the pending kill must eventually land after resume, not orphan");

                var singleton = f.GetSingleton();
                ref var entry = ref singleton.GetPersistent(def);
                Assert.IsTrue(entry.Spawn(bound, 0f).IsValid, "pool slot must be free again after the delayed kill lands");
            });
        }
    }
}
