# TODO.md

Scope: `BovineLabs.Timeline.VFXForge` (timeline integration — the deliverable) reviewed against the full provided `FireAlt.VFXForge` runtime it drives. Timeline-side findings are primary; VFXForge-core findings are included where they directly threaten timeline correctness or where the fix belongs upstream.

---

## Executive Summary

The timeline integration is small and architecturally sound: a deferred-handle spawn (`VFXForgeRuntimeState.Tracked`) + `ICleanupComponentData` reaper is exactly the right shape for driving a pooled, two-phase-resolved VFX system from enableable `ClipActive` state. The core VFXForge lifecycle (deferred spawn → transform-system stamp → sync-system resolution) is well tested on the FireAlt side.

The biggest risks, in order:

1. **Immortal orphaned VFX** — `KillJob` ignores `TryKill`'s return value. If a kill lands in the window where a spawn request is still unresolved *and* its packed system version has already advanced (the `HasPendingTransform` postpone path in `SyncVFXSystem.ResolvePersistentJob`), the kill silently fails, the handle and the `VFXForgeCleanup` are wiped anyway, and the pending spawn later resolves with `trackingDuration = 0` (infinite). Result: a persistent VFX that nothing can ever kill, and one pool slot leaked until subscene unload.
2. **Stale per-spawn data** — the clip spawns with no data payload. For any `VFXDefinition` with `vfxDataType != 0`, the resolved slot uploads whatever bytes the previous occupant left in `DataBuffer`/`DeferredDataBuffer`. Garbage visuals that only appear after pool churn — a classic "works in the demo, breaks in the real fight" bug.
3. **Silent no-op paths everywhere at runtime** — unregistered key, missing `HybridVisualEffect` in the scene, `Entity.Null` binding, unresolved route link: all return silently every frame. Bake-time warnings exist but land in import logs designers never see. This library will generate "the VFX clip does nothing" support tickets; it needs one-shot runtime diagnostics and an editor-time clip inspector.
4. **Core-side hazards inherited by the timeline**: `InitializeVFXSystem`'s duplicate-key error-spam loop, `[BurstCompile]`d `SyncVFXSystem.OnUpdate` calling managed `Application.isPlaying` (likely silent Burst fallback), per-frame `aliveParticleCount` readback, and per-frame `LogException` spam from `HasRequiredProperties()` on a misconfigured graph.

No production blocker is a rewrite; all fixes are local. The single most valuable change is making `KillJob` kill-result-aware (item C1).

---

## System Inventory

| System / File | Responsibility |
| --- | --- |
| `VFXForgeClip` (Authoring) | `DOTSClip` — bakes `VFXForgeClipData {Key, Route}` + `VFXForgeRuntimeState`; bake-time warnings for null definition, non-Persistent type, key 0. `ClipCaps.Blending`, `duration => 1`. |
| `VFXForgeTrack` (Authoring) | Track bound to `TargetsAuthoring`; clip type `VFXForgeClip`. |
| `VFXForgeClipData` / `VFXForgeRuntimeState` / `VFXForgeCleanup` (Data) | Baked key+route; runtime tracked handle; cleanup component (key+handle) surviving entity destruction. |
| `VFXForgeTargetResolver` (Data) | Resolves `EntityLinkRef` route from the bound entity's `Targets` + link buffers. |
| `VFXForgeSpawnSystem` (Runtime) | In `TimelineComponentAnimationGroup`. Three sequential `Schedule`d jobs: `SpawnJob` (`WithAll ClipActive`: resolve target, `Spawn(target, 0f)`, latch handle, add cleanup), `KillJob` (`WithDisabled ClipActive`: `TryKill`, clear handle, remove cleanup), `ReapJob` (`WithNone RuntimeState` + cleanup: kill stragglers on entity destroy). |
| FireAlt core (context) | `PersistentVFXEntry` deferred spawn/kill w/ `TrackedEntity` versioned handles; `VFXTransformSystem` stamps transforms + `DidTransformSystemRun`; `SyncVFXSystem` (PresentationSystemGroup, OrderLast) bumps `SystemVersion`, resolves deferred→resolved, uploads GPU buffers; `VFXKillPersistentSystem` reaps dead-tracked; `InitializeVFXSystem`/`CleanupVFXSystem` register/deregister entries per `HybridVisualEffect`. |

## Dependency & Flow Map

Per-frame happy path:

1. **Simulation / `TimelineComponentAnimationGroup`** — `SpawnJob` sees `ClipActive` enabled, `rt.Tracked` invalid → resolves route → `entry.Spawn(target, 0f)` → deferred `TrackedEntity` (packed `SystemVersion = SyncVFXSystem.SystemVersion`, `IsDeferred = true`, slot in `DeferredTransformBuffer`) → ECB-add `VFXForgeCleanup`.
2. **`LateSimulationSystemGroup` / `UpdateVFXSystemGroup`** — `VFXTransformSystem` stamps `DidTransformSystemRun` + pose on the deferred slot. `VFXKillPersistentSystem` reaps entries whose transform says dead.
3. **`EndSimulationEntityCommandBufferSystem`** — cleanup add/remove plays back.
4. **`PresentationSystemGroup` / `SyncVFXSystem`** — `SystemVersion++`; `ResolvePersistentJob` moves deferred → resolved (`DeferredToResolvedMap`), or **postpones the entire entry** (spawns *and* kills, no clears) if any pending spawn lacks `DidTransformSystemRun`; GPU upload.
5. Next frames — `KillJob` on clip end: `TryKill(rt.Tracked)` resolves via `IsDeferred(currentVersion)` (same-version deferred) or `DeferredToResolvedMap` (resolved). `ReapJob` covers clip-entity destruction mid-clip (subscene unload, director destroy).

Critical cross-system dependencies:
- Handle resolution validity depends on `SyncVFXSystem.SystemVersion` monotonically bracketing spawn→resolve→kill. The postpone path in step 4 breaks the bracket (see C1).
- `SpawnJob` runs before `VFXTransformSystem` in the same frame (required for same-frame resolution). This ordering is assumed, not asserted (see H5).
- `ECB` correctness depends on the clip entity surviving until end-of-simulation playback (see H3).

---

## Critical TODOs

### TODO: Make KillJob kill-result-aware — never clear the handle on a failed kill

**Priority:** Critical
**Certainty:** Confirmed (code path) / Strongly Likely (trigger frequency)
**Lens:** State / Event / Timing
**Files/Systems Involved:** `VFXForgeSpawnSystem.KillJob`; interacts with `SyncVFXSystem.ResolvePersistentJob.HasPendingTransform`, `PersistentVFXEntry.TryKill`
**Problem:** `KillJob` calls `TryKill(rt.Tracked)` and unconditionally sets `rt.Tracked = Null` and removes `VFXForgeCleanup`, ignoring the return value. `TryKill` legitimately returns `false` when the handle is a *previous-version deferred key whose spawn has not yet been resolved* — which happens whenever `ResolvePersistentJob` postponed the whole entry via `HasPendingTransform` (transform system didn't stamp that entry that frame: entry registered late, paused-world sync via `BovineLabsPauseRegistry` while `VFXTransformSystem` is paused, editor-world skew, system-order drift). The pending spawn request is still queued and resolves on a later sync with `trackingDuration = 0` → **an infinite-lifetime persistent VFX with no live handle anywhere**. `VFXForgeCleanup` was already removed, so `ReapJob` can't save it either. Leaks a pool slot (`UsedCapacity`) forever.
**Evidence:** `KillJob.Execute` ignores `TryKill`'s bool; `ResolvePersistentJob.Execute` early-returns *without clearing `SpawnRequests`/`NextIndex`* when `HasPendingTransform(...)` is true; `TryKill → TryResolveCheckIndex → TryResolve` returns false for an old-version deferred key absent from `DeferredToResolvedMap`.
**Why It Matters:** Immortal VFX visually stuck on a target + permanent capacity loss; at capacity 1–4 (common for big persistent effects) one occurrence bricks the effect for the session. Non-reproducible in normal play → brutal to diagnose later.
**Suggested Change:** Only clear state when the kill actually landed (or the handle is provably dead). If `TryKill` fails on a still-valid handle, leave `rt.Tracked` and the cleanup component intact and retry next frame — the job already runs every frame for `WithDisabled(ClipActive)` entities.
**Implementation Path:**
1. In `KillJob.Execute`, capture `var killed = this.Vfx.GetPersistent(data.Key).TryKill(rt.Tracked);` (inside the `ContainsPersistent` guard).
2. Treat "entry no longer registered" (`!ContainsPersistent`) as killed (subscene with the graph unloaded → nothing to leak).
3. Clear `rt.Tracked` + remove cleanup only when `killed`. Otherwise return and retry.
4. Apply the same rule to `ReapJob` — currently it also ignores `TryKill`'s result and removes the cleanup unconditionally. For reap, a bounded retry is enough: keep the cleanup if `TryKill` failed *and* `ContainsPersistent` (see snippet; a frame-count in `VFXForgeCleanup` avoids an unbounded loop).
**Snippet/Pseudocode:**
```csharp
private void Execute(Entity entity, in VFXForgeClipData data, ref VFXForgeRuntimeState rt)
{
    if (rt.Tracked.Equals(TrackedEntity.Null)) return;

    var killed = true; // entry gone == nothing left to kill
    if (this.Vfx.ContainsPersistent(data.Key))
        killed = this.Vfx.GetPersistent(data.Key).TryKill(rt.Tracked);

    if (!killed) return; // spawn not yet resolved — retry next frame, keep cleanup

    rt.Tracked = TrackedEntity.Null;
    this.Ecb.RemoveComponent<VFXForgeCleanup>(entity);
}
```
**How to Verify:** Playmode test: register a persistent definition, spawn via a clip, *skip* `VFXTransformSystem.Update` before the first `SyncVFXSystem.Update` (forces the postpone path), disable `ClipActive`, run the full loop, then assert `IsPersistentAlive == false` after two syncs and that the pool slot is reusable (`Spawn` succeeds at capacity 1).
**Tradeoffs:** A failed-kill handle that is *genuinely* dead (target destroyed → `VFXKillPersistentSystem` already reaped it) also returns `false`; retrying is a harmless repeated `false`, but pair with H6 (dead-handle detection) so `rt.Tracked` doesn't stay latched forever. Cost: trivial.
**Confidence:** High

### TODO: Reject or support data-carrying VFXDefinitions — current spawns upload stale slot bytes

**Priority:** Critical
**Certainty:** Confirmed
**Lens:** Validation / Designer Safety / Other (GPU data)
**Files/Systems Involved:** `VFXForgeClip.Bake`, `VFXForgeSpawnSystem.SpawnJob`; core `PersistentVFXEntry.Spawn(TrackedEntity, float)` / `SyncVFXSystem` upload
**Problem:** The clip spawns via the data-less `Spawn(target, 0f)` overload. For a definition with `vfxDataType != 0` (`DataGpuSize > 0`), neither `DeferredDataBuffer` nor `DataBuffer` is written for the acquired slot — but `SyncVFXSystem.Managed` uploads `DataBuffer` over the resolved slot's range anyway. The slot contains whatever the *previous* occupant wrote (or zeros on first use). The VFX renders with another effect instance's parameters after pool reuse.
**Evidence:** `SpawnJob` calls the parameterless-data overload; core `Spawn(TrackedEntity, float)` never touches data buffers; `InternalAPI.SpawnPersistent` (non-Unsafe path) skips `DataBuffer` writes; buffers are `Allocator.Persistent` and never cleared on free (`KillPersistent` frees array memory only).
**Why It Matters:** Intermittent, pool-order-dependent visual corruption. It will pass every "play the clip once" check and fail in combat.
**Suggested Change:** Two-layer fix: (a) **Bake-time**: warn/error in `VFXForgeClip.Bake` when `definition.DataGpuSize > 0` or `ArrayDataGpuSize > 0` — "VFXForgeClip cannot supply per-spawn data; definition '{name}' declares a data type". (b) **Optional feature**: add a serialized default-data baker to the clip (reuse `VFXDataTypeBakerWrapper`, bake the bytes into a blob on `VFXForgeClipData`) and spawn via `SpawnUnsafe(target, ptr, default, 0f)`. (a) alone is acceptable for v1.
**Implementation Path:**
1. Add the bake warning next to the existing `vfxType`/key-0 warnings (data sizes are available via `definition.DataGpuSize`/`ArrayDataGpuSize`).
2. Mirror the check in the clip inspector (see H2) so it's visible in the Timeline window.
3. (Upstream, optional) In `InternalAPI.SpawnPersistent`, `UnsafeUtility.MemClear` the slot's `DataBuffer` range when the caller supplied no data — defense-in-depth for *every* data-less spawn path, not just timeline.
**How to Verify:** Definition with `VFXDecal` data, capacity 1: spawn+kill with data via API, then spawn via clip; read back `TryGetUpdateDataAsRef` — currently returns the old decal values; after fix, cleared/defaulted or bake rejects the setup.
**Tradeoffs:** MemClear on spawn costs `DataGpuSize` bytes per spawn — negligible. Full data support on the clip is real scope; the warning is the 90% fix.
**Confidence:** High

### TODO: (Upstream) Fix InitializeVFXSystem duplicate-key infinite error loop

**Priority:** Critical
**Certainty:** Confirmed
**Lens:** Validation / Debugging
**Files/Systems Involved:** `FireAlt.VFXForge/Systems/InitializeVFXSystem.cs`
**Problem:** On duplicate `VFXKey` registration, `TryAddVFXEntry` logs an error and returns `false` — but the loop only disables `HybridVisualEffectData` on success, so the entity is re-processed and **the error logs every `InitializeVFXSystem` update, forever**. Duplicate keys are exactly what the timeline clip's key-0 warning predicts (two unregistered definitions both key 0, or a duplicated definition asset), so timeline users will hit this.
**Evidence:** `if (TryAddVFXEntry(...)) { registeredVFX.ValueRW.Key = key; initializeRW.ValueRW = false; }` — no else branch.
**Why It Matters:** Console flood, editor stutter, masks real errors; the duplicate VFX also silently never works.
**Suggested Change:** On failure, still disable `HybridVisualEffectData` (leave `RegisteredVFX.Key = VFXKey.Null`, which `CleanupVFXSystem` already tolerates), so the error logs once per offending object.
**Implementation Path:** Add `else { initializeRW.ValueRW = false; }`; keep `registeredVFX.Key` at `Null`.
**How to Verify:** Register two `HybridVisualEffect`s sharing a definition; assert exactly one error and no per-frame repetition.
**Tradeoffs:** None; retry-forever has no recovery path anyway (the key won't un-collide).
**Confidence:** High

---

## High Priority TODOs

### TODO: One-shot runtime diagnostics for every silent no-op path in SpawnJob

**Priority:** High
**Certainty:** Confirmed (the silences) 
**Lens:** Debugging / Designer Safety
**Files/Systems Involved:** `VFXForgeSpawnSystem.SpawnJob`
**Problem:** Four early returns per frame with zero telemetry: `binding.Value == Entity.Null` (track unbound / `TargetsAuthoring` missing), `!ContainsPersistent(key)` (no `HybridVisualEffect` loaded for this key, or key 0), route resolution failure, and `target == Entity.Null`. "The VFX clip plays nothing" is undiagnosable without stepping Burst jobs.
**Evidence:** All four `return`s in `SpawnJob.Execute` are bare.
**Why It Matters:** This is the #1 designer-facing failure mode of the whole package; every misconfiguration funnels into invisible per-frame returns.
**Suggested Change:** Add a per-entity "warned" latch and log once per clip entity per cause. Cheapest Burst-friendly shape: a `byte WarnedMask` on `VFXForgeRuntimeState` + `BLGlobalLogger`/`UnityEngine.Debug` fixed-string warnings (guard behind `UNITY_EDITOR || DEVELOPMENT_BUILD` if log cost matters).
**Implementation Path:**
1. Extend `VFXForgeRuntimeState` with `byte WarnedMask` (bit per cause). Set the bit when logging; skip logging when set.
2. Messages must name the cause and the key value: `"VFXForge clip: VFXKey {n} not registered — is a HybridVisualEffect for this definition loaded in a subscene?"` etc.
3. Do **not** latch the pool-exhausted retry path (that one is intentional and frequent) — count it instead (see M-item on debug overlay).
**Snippet/Pseudocode:**
```csharp
if (!this.Vfx.ContainsPersistent(data.Key))
{
    if ((rt.WarnedMask & 1) == 0)
    {
        rt.WarnedMask |= 1;
        Debug.LogWarning($"VFXForgeClip: VFXKey {data.Key.Value} not registered; no HybridVisualEffect loaded for it.");
    }
    return;
}
```
**How to Verify:** Play a clip with (a) no track binding, (b) an unloaded definition, (c) a route link that resolves to nothing — each yields exactly one console warning naming the cause.
**Tradeoffs:** `Debug.LogWarning` from Burst requires constant-ish strings or `BLGlobalLogger`; per `shattered-debug-logging`, prefer the BovineLabs logger. +1 byte on runtime state.
**Confidence:** High

### TODO: Custom TimelineEditor/ClipEditor validation for VFXForgeClip (surface bake warnings where designers look)

**Priority:** High
**Certainty:** Confirmed (warnings currently bake-log-only)
**Lens:** Designer Safety / Validation
**Files/Systems Involved:** New `VFXForgeClipEditor` (Editor asm), `VFXForgeClip`
**Problem:** All three misconfiguration checks (null definition, non-Persistent `vfxType`, unregistered key 0) log only during baking — an import-time log stream designers don't watch. The Timeline window shows a healthy-looking clip. There is currently no Editor assembly in this package at all.
**Evidence:** `VFXForgeClip.Bake` warnings; project structure lists only Authoring/Data/Runtime asms.
**Why It Matters:** Editor-time validation prevents 100% of these at the moment of authoring instead of at runtime-silence time. This is the single highest-leverage designer-safety change.
**Suggested Change:** Add `BovineLabs.Timeline.VFXForge.Editor` asm with a `[CustomTimelineEditor(typeof(VFXForgeClip))] ClipEditor` overriding `GetClipOptions`/`GetErrorText` (Timeline draws error badges + red clip tint natively), plus a `[CustomEditor]` inspector repeating the checks with fix hints ("Set vfxType to Persistent", "Re-save the definition to assign a VFXKey").
**Implementation Path:**
1. New asmdef referencing `Unity.Timeline.Editor`, Authoring, `FireAlt.VFXForge.Data`.
2. `GetErrorText(clip)`: return the same three messages `Bake` logs (share a static `VFXForgeClipValidation.Validate(clip)` used by both bake and editor so they never drift).
3. Bonus: `GetClipOptions` tint the clip when `definition == null`.
**How to Verify:** Author each bad state in a Timeline; clip shows the error badge/tooltip without entering play mode or rebaking.
**Tradeoffs:** New asm to maintain; small.
**Confidence:** High

### TODO: Guard the spawn-cleanup ECB against destroyed clip entities

**Priority:** High
**Certainty:** Risk
**Lens:** Event / Edge Case
**Files/Systems Involved:** `VFXForgeSpawnSystem` (SpawnJob/KillJob ECB usage via `EndSimulationEntityCommandBufferSystem`)
**Problem:** `SpawnJob` queues `Ecb.AddComponent(entity, VFXForgeCleanup)` in `TimelineComponentAnimationGroup`; playback is at end of simulation. If anything structurally destroys the clip entity in between (subscene unload command, director teardown, another ECB destroying the timeline hierarchy in the same frame), playback throws `InvalidOperationException` ("entity does not exist") and **aborts the rest of that command buffer**, corrupting unrelated systems' commands sharing the EndSim ECB.
**Evidence:** Standard ECB playback semantics; the spawn→playback window spans the remainder of simulation.
**Why It Matters:** A single unlucky same-frame despawn turns into a frame-wide command loss + exception, far from the cause. Also note: on this path the VFX *did* spawn but the cleanup never attached and the entity is gone → same immortal-VFX leak as C1.
**Suggested Change:** Two options; do both if cheap:
1. Move the cleanup add out of the ECB: since `SpawnJob` structurally can't add components itself, instead **bake `VFXForgeCleanup` at bake time** (with `Key` set, `Tracked = Null`) and have Spawn/Kill only *write its value* (plain component write, no structural change, no ECB at all). `ReapJob` keys off `Tracked != Null`. This eliminates the ECB from the hot path entirely and closes the destroyed-entity window — the cleanup exists from frame zero.
2. If keeping the ECB: playback-safety it isn't fixable from here; at minimum document the constraint and prefer option 1.
**Implementation Path (option 1 — recommended):**
1. `VFXForgeClip.Bake`: `AddComponent(clipEntity, new VFXForgeCleanup { Key = definition, Tracked = TrackedEntity.Null })`.
2. `SpawnJob`: replace `Ecb.AddComponent` with a `ref VFXForgeCleanup cleanup` parameter in `Execute` and set `cleanup.Tracked = spawned`.
3. `KillJob`: set `cleanup.Tracked = Null` instead of `Ecb.RemoveComponent` (respecting C1's killed-gate).
4. `ReapJob`: unchanged query (`WithNone<VFXForgeRuntimeState>` + cleanup) but early-out on `Tracked == Null`; still needs an ECB solely to strip the cleanup after reaping — that path only runs post-destruction where the entity provably exists (cleanup components keep it alive), so it's safe.
5. Delete the ECB from Spawn/Kill jobs; keep it for Reap.
**How to Verify:** Test: activate clip and destroy the clip entity in the same simulation frame (before EndSim playback) — no exception; `ReapJob` kills the spawned VFX on the following frame.
**Tradeoffs:** Cleanup component now exists on all clips including never-played ones (+8 bytes/clip archetype); `ReapJob`'s `WithNone<VFXForgeRuntimeState>` query now matches from bake if state were missing — it isn't (both always baked together). Net: simpler and faster (no per-spawn structural changes).
**Confidence:** High (that option 1 removes the class of bug); Medium (how often the destroy-window fires in this project)

### TODO: Add RequireForUpdate on clip presence; stop scheduling 3 jobs/frame in empty worlds

**Priority:** High
**Certainty:** Confirmed
**Lens:** Performance
**Files/Systems Involved:** `VFXForgeSpawnSystem.OnCreate/OnUpdate`
**Problem:** The system requires only `VFXSingleton`, so in every world with VFXForge but no VFX timeline content it schedules `SpawnJob` + `KillJob` + `ReapJob` (three job scheduls, three lookup updates, one ECB, one singleton RW dependency) every frame.
**Evidence:** `OnCreate` has a single `RequireForUpdate<VFXSingleton>()`.
**Suggested Change:** `state.RequireAnyForUpdate(state.GetEntityQuery(ComponentType.ReadOnly<VFXForgeClipData>()), state.GetEntityQuery(ComponentType.ReadOnly<VFXForgeCleanup>()))` — cleanup query included so reaping still runs after all clips are destroyed.
**Implementation Path:** Build both queries in `OnCreate`, call `RequireAnyForUpdate`.
**How to Verify:** Profiler: system shows as not-updated in a scene without VFX clips; reap still fires after unloading a subscene containing active clips.
**Tradeoffs:** None.
**Confidence:** High

### TODO: Assert/verify system ordering: SpawnSystem → VFXTransformSystem → SyncVFXSystem within one frame

**Priority:** High
**Certainty:** Risk (Unknown group placement of `TimelineComponentAnimationGroup`)
**Lens:** Timing / Architecture
**Files/Systems Involved:** `VFXForgeSpawnSystem` (`TimelineComponentAnimationGroup`), core `VFXTransformSystem` (`UpdateVFXSystemGroup` → `LateSimulationSystemGroup`), `SyncVFXSystem` (`PresentationSystemGroup`, OrderLast)
**Problem:** Same-frame spawn resolution requires the timeline spawn to land **before** `VFXTransformSystem` (which stamps `DidTransformSystemRun`) and both before `SyncVFXSystem`. If `TimelineComponentAnimationGroup` ever runs after `LateSimulationSystemGroup` (or the project reorders groups), every spawn takes the `HasPendingTransform` postpone path — one extra frame of latency *and* a much wider C1 orphan window, silently.
**Evidence:** No ordering attribute or assertion couples the two packages; the coupling is implicit.
**Why It Matters:** The postpone path is this integration's most dangerous code path (C1); ordering drift makes it the *default* path with no visible symptom except +1 frame VFX latency.
**Suggested Change:** (a) Verify once in the actual player loop (`World.Unmanaged.GetAllSystems` dump or Systems window) that the order holds in this project. (b) Add a `DEVELOPMENT_BUILD`-only frame-latency counter: in `SpawnJob`, record `SyncVFXSystem.SystemVersion` at spawn; in a small debug system after sync, warn if handles routinely resolve at version+2 instead of version+1.
**Implementation Path:** Manual verification first; the counter is optional hardening.
**How to Verify:** Editor Systems window / `-systemsdump`; latency counter stays at 1.
**Tradeoffs:** The counter adds a debug-only system; skip if the manual check is documented.
**Confidence:** Medium

### TODO: Handle target-death-mid-clip: detect dead handles and decide respawn semantics

**Priority:** High
**Certainty:** Confirmed (mechanism) / Design decision needed
**Lens:** State / Edge Case
**Files/Systems Involved:** `VFXForgeSpawnSystem.SpawnJob`, core `VFXKillPersistentSystem`, `PersistentVFXEntry.IsAlive`
**Problem:** If the routed target entity dies mid-clip, core `VFXTransformSystem` marks the transform dead and `VFXKillPersistentSystem` reaps it — removing it from `TrackedEntities` and both resolve maps. The clip's `rt.Tracked` still reads `IsValid == true`, so `SpawnJob` early-outs forever: the clip believes it owns a live VFX that no longer exists. Subsequent `KillJob` `TryKill` returns false (harmless with C1's gate, but the handle stays latched if you naïvely "retry forever").
**Evidence:** `SpawnJob`: `if (rt.Tracked.IsValid) return;` — validity is a version check, not liveness. `KillPersistent` removes the maps → `TryResolve` false.
**Why It Matters:** For route modes like `Target`/`Source` where the linked entity can be swapped mid-clip (EntityLink mutate tracks exist in this project!), the intuitive behavior is "VFX follows the current resolution"; currently it's "first resolution wins, silently nothing after target death".
**Suggested Change:** In `SpawnJob`, replace the `IsValid` early-out with a liveness check: `if (rt.Tracked.IsValid && entry.IsAlive(rt.Tracked)) return;` — dead handle → clear and fall through to respawn against the (re-)resolved target. Combine with C1 so Kill clears dead handles too: in `KillJob`, treat `!IsAlive` as `killed = true`.
**Implementation Path:**
1. `SpawnJob`: fetch `ref var entry = ref this.Vfx.GetPersistent(data.Key);` once; use `entry.IsAlive` for the latch check (note: a *deferred, unresolved* handle reports alive via `DeferredTransformBuffer` — correct, no double-spawn).
2. `KillJob` (with C1): `killed = entry.TryKill(rt.Tracked) || !entry.IsAlive(rt.Tracked);`
3. Decide and document whether respawn-on-target-death is desired per clip; if not always, add a `bool respawnIfTargetLost` to `VFXForgeClip` (bake into `VFXForgeClipData`).
**How to Verify:** Test: clip active on entity A; destroy A mid-clip; with respawn enabled and route re-resolving to B, VFX reappears on B within 2 frames; with it disabled, clip stays quiescent and `KillJob` exits cleanly at clip end.
**Tradeoffs:** `IsAlive` per active clip per frame is a couple of map/array reads — cheap. Respawn semantics are a gameplay decision; the *detection* is not optional (needed to un-stick C1's retry).
**Confidence:** High

### TODO: Remove or implement `ClipCaps.Blending`; document `duration => 1`

**Priority:** High
**Certainty:** Confirmed
**Lens:** Designer Safety
**Files/Systems Involved:** `VFXForgeClip`
**Problem:** `clipCaps => ClipCaps.Blending` lets designers crossfade two VFX clips in the Timeline UI, but the runtime is binary (`ClipActive` enable/disable) — the blend region does nothing except (depending on the timeline core's blend handling of enableable flags) possibly keep *both* clips active for the overlap. Designers will author crossfades expecting fades.
**Evidence:** Clip declares Blending; no weight is read anywhere in `VFXForgeSpawnSystem`.
**Suggested Change:** v1: `ClipCaps.None` (honest UI). Later: if the DOTS timeline exposes clip weight, map weight → a well-known exposed float ("Intensity") on the graph via `TrySetUpdateData` — that's the real feature.
**Implementation Path:** One-line change + note in docs; verify overlap behavior of two adjacent clips on one track before/after (does the first clip's Kill fire while the second's Spawn fires the same frame? With `None` and no overlap, yes — spawn/kill order within the frame is Spawn-then-Kill job order; both target different entities, safe).
**How to Verify:** Author two overlapping clips pre-change and observe no visual blend; post-change UI disallows the overlap.
**Tradeoffs:** Removes a (non-functional) UI affordance.
**Confidence:** High

### TODO: (Upstream) SyncVFXSystem `[BurstCompile]` OnUpdate calls managed APIs — verify Burst status

**Priority:** High
**Certainty:** Strongly Likely
**Lens:** Performance / Other
**Files/Systems Involved:** `FireAlt.VFXForge/Systems/SyncVFXSystem.cs`
**Problem:** `OnUpdate` is `[BurstCompile]` but directly calls `UnityEngine.Application.isPlaying` (twice, incl. inside `AddVFX`) — a managed static property Burst cannot compile. Either Burst compilation of the method fails (silent managed fallback → the whole "BurstInterop function pointer" architecture of this file is being bypassed) or errors are being suppressed. Every other managed touchpoint in this file is carefully routed through `SharedStatic<BurstInterop>` — these calls look like an oversight.
**Evidence:** `if (Application.isPlaying)` in `OnUpdate` and `AddVFX` of a `[BurstCompile]` `ISystem`.
**Suggested Change:** Cache `Application.isPlaying` into a `SharedStatic<bool>` (updated from a managed hook / the existing static ctor + playmode callbacks), or hoist it through `ManagedArgs` like everything else. Then confirm in the Burst Inspector that `SyncVFXSystem.OnUpdate` actually compiles.
**How to Verify:** Burst Inspector lists the method with no compile error; Jobs → Burst → "Enable Compilation" logs clean.
**Tradeoffs:** None meaningful.
**Confidence:** Medium (behavior depends on Burst version's failure mode — verify before changing)

---

## Medium Priority TODOs

### TODO: Pool-exhaustion behavior: mid-clip pop-in + phantom activation

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** State / Designer Safety
**Files/Systems Involved:** `VFXForgeSpawnSystem.SpawnJob`, core `PersistentVFXEntry.Spawn`
**Problem:** Two aspects. (a) The retry-until-slot-free comment is correct, but the VFX then pops in partway through the clip with no fade — for long clips that may be worse than skipping; there's no policy control. (b) Upstream: a capacity-failed `Spawn` still does `Interlocked.Increment(ref NextIndex)`, so `HasPendingRequests` goes true with zero queued requests → `SyncVFXSystem.AddVFX` activates the graph GameObject and allocates its `GraphicsBuffers` for nothing (self-corrects next sync, but churns enable/disable + buffer alloc under sustained exhaustion).
**Evidence:** `Spawn`: increment happens before the capacity check's early return; `HasPendingRequests => NextIndex > 0`; `AddVFX` keys on `HasPendingRequests`.
**Suggested Change:** (a) Add `enum ExhaustedPolicy { RetryUntilFree, SkipClip }` on the clip (bake to clip data; `SkipClip` sets a latch bit so the clip stops retrying). (b) Upstream: move the capacity check before the increment, or decrement on failure (`Interlocked.Decrement`) — careful with concurrent spawns; simplest correct form is check-then-increment under the existing monotonic scheme: `if (nextIndex > Cap - Used) { Interlocked.Decrement(ref NextIndex); return ...; }` is *not* safe with concurrent successes; prefer leaving the counter but adding a separate `AnyRequestQueued` flag that `HasPendingRequests` checks.
**How to Verify:** Capacity 1, two simultaneous clips: second clip either pops in when slot frees (RetryUntilFree) or stays skipped; graph GO no longer toggles active with zero requests.
**Tradeoffs:** (b) touches concurrency-sensitive upstream code — test with the existing `PersistentVFXTests` capacity tests.
**Confidence:** High for the diagnosis; Medium for the preferred upstream fix shape

### TODO: (Upstream) Rate-limit per-frame failure logging: HasRequiredProperties + duplicate-property errors

**Priority:** Medium
**Certainty:** Confirmed
**Lens:** Debugging
**Files/Systems Involved:** `FireAlt.VFXForge/Singletons/VFXGraphicsBuffersSingleton.cs` (`HasRequiredProperties`), `SyncVFXSystem.Managed`
**Problem:** A graph missing a required buffer/int property throws inside `HasRequiredProperties()`, which catches and `Debug.LogException`s — **every frame** for as long as the VFX is alive (`UploadDataPacked` calls it per alive entry per frame). Exception construction + logging per frame per bad graph.
**Suggested Change:** Cache the validation result per `VFXKey` (invalidate on `VFXDefinition.OnVFXDefinitionChanged` / re-registration); log once.
**How to Verify:** Delete a buffer property from a template graph, play — one exception, not 60/s.
**Tradeoffs:** Stale cache if the graph asset is edited at runtime — the definition-changed event covers editor flows.
**Confidence:** High

### TODO: (Upstream) `aliveParticleCount` polled per alive VFX per frame

**Priority:** Medium
**Certainty:** Confirmed (call pattern); Risk (actual cost is Unity-version dependent)
**Lens:** Performance
**Files/Systems Involved:** `SyncVFXSystem.RemoveTimedOutVFX` → `GetVFXActivityStatusPacked`
**Problem:** Timeout tracking reads `VisualEffect.aliveParticleCount` per alive graph per frame through the managed interop. On several Unity versions this property forces a readback/sync with the VFX update. With many concurrently-alive graphs this is a hidden main-thread cost attached to *every* timeline VFX.
**Suggested Change:** Poll on an interval (e.g., every 15 frames — timeout durations are seconds) or track activity from the request side (`entry.HasPendingRequests`/tracked-count > 0 keeps it alive without asking the GPU).
**How to Verify:** Profiler with 20+ alive graphs; compare `SyncVFXSystem` main-thread time before/after.
**Tradeoffs:** Interval polling delays deactivation by up to the interval — invisible at 15 frames vs. multi-second timeouts.
**Confidence:** Medium

### TODO: (Upstream) Add index-alignment assertions between EntityIdFrameData fill and consume

**Priority:** Medium
**Certainty:** Risk
**Lens:** Validation / Timing
**Files/Systems Involved:** `VFXTransformSystem` (main-thread fill loop vs `FetchGameObjectTransformJob` consume loop)
**Problem:** The main-thread loop **always** appends `SpawnEntityIdRequests` entries to `EntityIdFrameData`, but the consuming job iterates them only `if (entry.HasPendingRequests)`, sharing one running index `i` with the `TrackedEntityIds` loop. The invariant "requests non-empty ⇒ HasPendingRequests" currently holds (every queued request incremented `NextIndex`), but it's load-bearing, undocumented, and one refactor away from writing transforms into the wrong slots (silent wrong-position VFX).
**Suggested Change:** `Assert.IsTrue(entry.HasPendingRequests || entry.SpawnEntityIdRequests.Length == 0)` at fill time, and assert `i == entry.EntityIdFrameData.Length` at the end of the consume loop.
**How to Verify:** Assertions hold across the existing GameObject-path test suite.
**Tradeoffs:** None (checks-only builds).
**Confidence:** High that the assertion is worth having

### TODO: Timeline debug overlay / diagnostics for VFXForge clips

**Priority:** Medium
**Certainty:** Confirmed (nothing exists)
**Lens:** Debugging
**Files/Systems Involved:** New debug system (Runtime, `UNITY_EDITOR || DEVELOPMENT_BUILD`), Entities Systems/Components windows already show the rest
**Problem:** There is no way to see, per clip entity: resolved target, tracked handle state (deferred/resolved/dead), key registration, retry counters. The runtime state is a versioned opaque handle — inspector shows meaningless ints.
**Suggested Change:** (a) A small `IJobEntity`-free debug system that, when a `ConfigVar` (per `bl-core-config-vars`) is enabled, logs a table: clip entity → key, registered?, binding, resolved target, `IsAlive`, spawn-retries. (b) Optionally a `TrackedEntity` custom formatter/inspector showing `IndexInData / version / deferred / valid` unpacked.
**How to Verify:** Toggle the ConfigVar during a repro; the failing stage is identifiable from the table alone.
**Tradeoffs:** Debug-only code; keep out of player builds.
**Confidence:** High

### TODO: Playmode test suite for the timeline integration (currently zero tests)

**Priority:** Medium (High if C1/H3 fixes land — regression cover)
**Certainty:** Confirmed
**Lens:** Testing
**Files/Systems Involved:** New `BovineLabs.Timeline.VFXForge.Tests` asm; reuse core `VFXPlayModeTestFixture` patterns
**Problem:** FireAlt core has a solid fixture + tests; the timeline layer — which owns the trickiest lifecycle glue in the stack — has none. Every fix above needs a regression net.
**Suggested Change:** Tests driven by manually toggling `ClipActive` on hand-built clip entities (no Timeline asset needed), each pinned to a specific risk:
1. **Spawn/Kill happy path** — enable → alive after transform+sync; disable → dead after next update. (Baseline.)
2. **Kill-before-resolve orphan** (C1) — skip `VFXTransformSystem` one frame to force the postpone path; disable clip; assert dead + slot reusable. *This test fails on current code.*
3. **Reap on entity destroy** (H3) — destroy the clip entity while active; assert VFX killed and cleanup stripped within 2 updates.
4. **Pool exhaustion + retry** — capacity 1, two clips; kill first; second becomes alive; assert graph GO not activated while zero real requests (after the M-fix).
5. **Target destroyed mid-clip** (H6) — destroy target; assert clip either respawns (policy on) or ends cleanly with no latched handle.
6. **Looping clip** — enable/disable/enable across frames; assert exactly one live instance at a time and cleanup value matches the latest handle.
7. **Unregistered key** — no `HybridVisualEffect`; assert no throw, one warning (after H1).
**Implementation Path:** Fixture = core `VFXPlayModeTestFixture` + `world.CreateSystem<VFXForgeSpawnSystem>()` + a helper that creates a clip entity with `VFXForgeClipData/RuntimeState/TrackBinding/ClipActive(enableable)`; per `shattered-unit-tests` conventions.
**How to Verify:** `./Run-UnityTests.ps1` green; test 2 red before the C1 fix, green after.
**Tradeoffs:** `TrackBinding`/`ClipActive` come from the timeline core package — tests take that dependency (they must anyway).
**Confidence:** High

### TODO: (Upstream) SyncVFXSystem.OnDestroy singleton access during world teardown

**Priority:** Medium
**Certainty:** Risk
**Lens:** Edge Case
**Files/Systems Involved:** `SyncVFXSystem.OnDestroy`
**Problem:** `OnDestroy` fetches both singletons; if the singleton entities were already destroyed during teardown (world dispose order, `DestroyAllSystemsAndLogException` after entity purge, or a test tearing entities first), `GetSingleton` throws → the `VFXSingleton.Dispose()` never runs → Persistent-allocator leaks reported on world dispose.
**Suggested Change:** Wrap in `TryGetSingleton` / query-`IsEmpty` guards; dispose what exists.
**How to Verify:** Test disposing the world after manually destroying the singleton entities — no throw, no leak warnings.
**Confidence:** Medium

---

## Low Priority TODOs

### TODO: KillJob iterates every inactive clip every frame

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Performance
**Files/Systems Involved:** `VFXForgeSpawnSystem.KillJob`
**Problem:** `WithDisabled(ClipActive)` matches all dormant clips forever; each does a `TrackedEntity.Equals` early-out. Cheap per entity, but scales with total authored clips, not active ones.
**Suggested Change:** Only worth changing if profiling shows it (hundreds of clips): add an enableable `VFXForgeNeedsKill` tag set by SpawnJob, cleared by KillJob, and query on it. Otherwise document as accepted.
**Confidence:** High

### TODO: (Upstream) 30-bit SystemVersion wrap in PackedData

**Priority:** Low
**Certainty:** Confirmed (math) / negligible in practice
**Files/Systems Involved:** `PackedData`, `SyncVFXSystem.SystemVersion`
**Problem:** Version wraps at 2^30 sync updates (~207 days at 60fps continuous; also version `0` after wrap means valid handles read as `Null`). Long-running editor sessions with domain reload disabled accumulate.
**Suggested Change:** Skip 0 on wrap (`if ((++v & MASK) == 0) v++;`) — one line, closes it forever.
**Confidence:** High

### TODO: (Upstream) HybridVisualEffect.Init silently zeroes the transform in play mode

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Designer Safety
**Files/Systems Involved:** `HybridVisualEffect.Init`
**Problem:** `transform.position = zero / identity / one` on Init — a scene-authored offset silently discarded at runtime; also `OnValidate` renames the GameObject to match the definition. Both surprising; neither documented or tooltipped.
**Suggested Change:** Warn once if the transform was non-identity ("VFXForge graphs are world-space; transform reset"); make the rename opt-in or at least undo-recorded.
**Confidence:** High

### TODO: Naming/format nits

**Priority:** Low
**Certainty:** Confirmed
**Lens:** Other
**Files/Systems Involved:** misc
**Problem/Suggested Change:** (a) `PersistentVFXGraphicsBuffers.SetIndexBuffers` uses `SpawnIndexMarker` for the array-spawn upload too — add a distinct marker. (b) `VFXSettings.DefaultDecalVFX` getter writes EditorPrefs (side-effectful getter) — move the "adopt package default" write to an explicit ensure method. (c) `CleanupVFXSystem` editor `catch { }` swallow-all in `HybridVisualEffect.DeregisterVFX` — at least log in dev. (d) `VFXForgeClip.duration => 1` — intentional default? Add a comment. (e) Extra blank line in `VFXForgeSpawnSystem.OnCreate`.
**Confidence:** High

---

## Designer Safety TODOs

- **[H2]** ClipEditor error badges for null definition / wrong `vfxType` / key 0 — the flagship item.
- **[C2]** Bake + inspector error when the definition declares data types the clip can't supply.
- **[H7]** `ClipCaps.None` until blending does something.
- **Track binding**: `VFXForgeTrack` binds `TargetsAuthoring`; an unbound track silently yields `Entity.Null` binding. Add `GetErrorText`-level "Track has no binding" in the same ClipEditor, plus the H1 runtime one-shot warning as backstop.
- **Tooltip pass**: `routeTo` tooltip says "Owner, Source, Target, or Self" — verify against the actual `Target` enum members in this project (per `unity-targets` there is also `None`/`Custom`); tooltips must match the dropdown.
- **[M-pool]** Expose `ExhaustedPolicy` so designers choose pop-in vs skip instead of inheriting a hidden default.
- **[Low]** Document (package README): definition must be Persistent, a `HybridVisualEffect` for it must be loaded in a subscene, spawn pose comes from tracking the routed entity, `trackingDuration` is unused (clip length is the lifetime).

## Validation & Guard TODOs

- **[C1]** Gate all handle-clearing on kill success (`KillJob`, `ReapJob`).
- **[C2]** Bake-time rejection of data-carrying definitions (until supported).
- **[C3]** (Upstream) disable retry on duplicate-key registration failure.
- **[H6]** Liveness (`IsAlive`) checks wherever `rt.Tracked.IsValid` is used as "we own a live VFX".
- **[M-assert]** (Upstream) fill/consume index assertions in `VFXTransformSystem`.
- **Shared validation source of truth**: one static `VFXForgeClipValidation.Validate(VFXForgeClip)` consumed by `Bake`, the ClipEditor, and (optionally) a build-time content check that scans all `TimelineAsset`s for VFXForge clips with invalid definitions — build-time is the right layer for "definition asset was deleted after the timeline was authored".
- **(Upstream, defense-in-depth)** `MemClear` the data slot in `SpawnPersistent` when no data supplied; skip-0 on version wrap; `TryGetSingleton` in `SyncVFXSystem.OnDestroy`; hash-collision `TryAdd`+error instead of throwing `Add` in `VFXTypeRegistry.Init`.

## Timing / Physics / Animation TODOs

- **[H5]** Verify/pin the Spawn → Transform → Sync same-frame ordering; add the resolve-latency counter.
- **Low-FPS**: sub-frame clips (active window entirely between evaluations) never enable `ClipActive` → VFX skipped. Acceptable for VFX, but document it; for must-fire effects recommend a minimum clip length ≥ 2 frames at target min-FPS.
- **One-frame spawn latency is inherent** (deferred handle resolves at sync, GPU sees it next VFX update): document that VFX visually starts ~1–2 frames after the clip's left edge; for frame-exact sync with animation hits, authors should lead the clip by that amount. A future `SyncVFXSystem`-side same-frame fast path is core-scope, not timeline-scope.
- **Pause**: `SyncVFXSystem` is registered `UpdateWhilePaused` but `VFXForgeSpawnSystem`/`VFXTransformSystem` are not — while paused, kills/spawns freeze mid-pipeline. Post-C1 this is safe (state just waits); verify unpause resumes cleanly with a pause/unpause test (add to the M-test list).

## Architecture TODOs

- **[H3]** Bake `VFXForgeCleanup` at bake time; make Spawn/Kill pure data writes (no ECB, no structural changes in the hot path). This is the one structural change recommended: it removes the destroyed-entity ECB hazard, removes per-spawn archetype churn, and makes the reap path the only structural consumer.
- **Ownership statement (document in code)**: the *clip entity* owns the handle; VFXForge core owns the instance lifetime; the only legal handle transitions are `Null → deferred (SpawnJob)`, `deferred/resolved → Null (KillJob on confirmed kill)`, `any → Null (ReapJob post-destroy)`. C1/H6 exist because transitions currently fire without their guards.
- **Keep the current three-job design** — it is the right decomposition; do not merge Spawn/Kill into one job keyed on enabled-state reads (loses the archetype-level filtering that makes this cheap).
- **Weight → data channel (future)**: if/when blending is implemented, route clip weight through `TrySetUpdateData` on a conventional exposed property rather than inventing a parallel path.

## Debugging / Tooling TODOs

- **[H1]** One-shot cause-named warnings for the four silent SpawnJob returns.
- **[M-overlay]** ConfigVar-gated diagnostic table (clip → key/binding/target/handle-state/retries).
- **(Upstream)** once-per-key `HasRequiredProperties` logging; unpacked `TrackedEntity` debug formatter.
- Logging per `shattered-debug-logging`: use the BovineLabs logger, not raw `Debug.Log*`, for all new runtime logging.

## Testing TODOs

See the consolidated suite under Medium ("Playmode test suite") — seven tests, each pinned to a named risk (C1, H3, H6, pool exhaustion, loop reuse, unregistered key, baseline). Add the pause/unpause resume test from the Timing section. Test 2 (kill-before-resolve) is the regression test that must be red on current code and green after the C1 fix.

## Suggested Architecture Direction

**Current weakness:** the handle lifecycle has unguarded transitions (clear-on-failed-kill, valid-but-dead latch) and a structural-change hot path (ECB add/remove of the cleanup) whose failure window is invisible.

**Target design (small delta):**
- **Ownership:** clip entity owns exactly one nullable handle in `VFXForgeRuntimeState.Tracked`; `VFXForgeCleanup` is a baked, always-present mirror written in lockstep — never added/removed at runtime except the final strip in `ReapJob`.
- **Data flow:** bake → `ClipData{Key, Route, (opt) DefaultDataBlob, ExhaustedPolicy}`; runtime writes only `RuntimeState` and `Cleanup` values.
- **Event flow:** `ClipActive` enable-edge ⇒ (resolve, spawn, latch) | disable-edge ⇒ (confirmed kill ⇒ unlatch); entity-destroy ⇒ reap via cleanup.
- **Validation flow:** one `Validate()` → Bake warnings + ClipEditor badges + optional build scan; runtime one-shot warnings as backstop only.
- **Debugging flow:** ConfigVar overlay reads the same state the jobs write; no shadow bookkeeping.
- **Migration:** land C1 (guarded kill) → H6 (liveness) → H3 (baked cleanup) → H1/H2 (diagnostics/editor) → tests alongside each; every step is independently shippable.
- **Risks:** H3 changes the reap query contract (cleanup exists pre-spawn) — test 3 covers it. Everything else is additive.
- **Verify:** the seven-test suite green + the C1 repro red-then-green is the acceptance bar.

## Implementation Snippets

Guarded kill (C1 + H6 combined):
```csharp
[BurstCompile]
[WithDisabled(typeof(ClipActive))]
private partial struct KillJob : IJobEntity
{
    public VFXSingleton.ParallelWriter Vfx;

    private void Execute(in VFXForgeClipData data, ref VFXForgeRuntimeState rt, ref VFXForgeCleanup cleanup)
    {
        if (rt.Tracked.Equals(TrackedEntity.Null)) return;

        var killed = true;
        if (this.Vfx.ContainsPersistent(data.Key))
        {
            ref var entry = ref this.Vfx.GetPersistent(data.Key);
            killed = entry.TryKill(rt.Tracked) || !entry.IsAlive(rt.Tracked);
        }

        if (!killed) return; // unresolved spawn in flight — retry next frame

        rt.Tracked = TrackedEntity.Null;
        cleanup.Tracked = TrackedEntity.Null; // baked cleanup mirror (H3)
    }
}
```

ClipEditor validation (H2):
```csharp
[CustomTimelineEditor(typeof(VFXForgeClip))]
public class VFXForgeClipTimelineEditor : ClipEditor
{
    public override ClipDrawOptions GetClipOptions(TimelineClip clip)
    {
        var options = base.GetClipOptions(clip);
        options.errorText = VFXForgeClipValidation.Validate((VFXForgeClip)clip.asset); // null when valid
        return options;
    }
}

public static class VFXForgeClipValidation
{
    public static string Validate(VFXForgeClip clip)
    {
        if (clip.definition == null) return "No VFXDefinition assigned.";
        if (clip.definition.vfxType != VFXType.Persistent) return $"'{clip.definition.name}' must be Persistent.";
        if (((VFXKey)clip.definition).Value == 0) return "VFXKey is 0 (unregistered) — re-save the definition.";
        if (clip.definition.DataGpuSize > 0 || clip.definition.ArrayDataGpuSize > 0)
            return "Definition declares per-spawn data; VFXForgeClip cannot supply it.";
        return null;
    }
}
```

Regression test skeleton (kill-before-resolve, C1):
```csharp
[UnityTest]
public IEnumerator ClipKill_WhileSpawnUnresolved_StillKillsAfterResolution()
{
    yield return VFXPlayModeTestFixture.Run(fixture =>
    {
        var def = fixture.CreateDefinition(300, VFXType.Persistent, capacity: 1);
        fixture.CreateAndRegisterVisualEffect(def);
        var clip = CreateClipEntity(fixture.World, def, target: fixture.CreateTrackedEntity());

        SetClipActive(fixture.World, clip, true);
        fixture.SpawnSystem.Update(fixture.World.Unmanaged);   // spawn queued (deferred)
        fixture.SyncVFXSystem.Update(fixture.World.Unmanaged); // NO transform update -> postpone path

        SetClipActive(fixture.World, clip, false);
        fixture.SpawnSystem.Update(fixture.World.Unmanaged);   // kill attempt on unresolved handle
        fixture.UpdateSystems();                                // transform + sync: spawn resolves
        fixture.SpawnSystem.Update(fixture.World.Unmanaged);   // retry kill (post-fix)
        fixture.UpdateSystems();

        ref var entry = ref fixture.GetSingleton().GetPersistent(def);
        Assert.IsFalse(AnyAlive(ref entry), "orphaned immortal VFX");           // red pre-fix
        Assert.IsTrue(fixture.SpawnPersistent(def, Entity.Null).IsValid, "slot leaked"); // red pre-fix
    });
}
```

## Final Ranked TODO List

1. **[Critical]** C1 — Guard `KillJob`/`ReapJob` on kill success; fix the immortal-orphan/leak window.
2. **[Critical]** C2 — Reject (bake+editor) data-carrying definitions on the clip; upstream `MemClear` defense.
3. **[Critical]** C3 — (Upstream) stop the duplicate-key registration error loop.
4. **[High]** H6 — Liveness-aware handle latch (target-death mid-clip; unlatches C1's retry).
5. **[High]** H3 — Bake `VFXForgeCleanup`; remove ECB structural changes from the spawn/kill hot path.
6. **[High]** H1 — One-shot runtime diagnostics for all silent SpawnJob no-ops.
7. **[High]** H2 — Editor asm + ClipEditor validation badges (shared `Validate()`).
8. **[High]** H4 — `RequireAnyForUpdate` clip/cleanup queries.
9. **[High]** H5 — Verify & pin Spawn→Transform→Sync ordering; optional latency counter.
10. **[High]** H7 — Drop `ClipCaps.Blending` (or implement weight→data later).
11. **[High]** H8 — (Upstream) resolve `Application.isPlaying` inside `[BurstCompile]` `SyncVFXSystem.OnUpdate`.
12. **[Medium]** Timeline playmode test suite (7 tests + pause/unpause), C1 repro red→green.
13. **[Medium]** Pool-exhaustion policy on the clip + upstream phantom-activation fix.
14. **[Medium]** (Upstream) once-per-key `HasRequiredProperties` logging.
15. **[Medium]** (Upstream) interval-poll `aliveParticleCount`.
16. **[Medium]** ConfigVar debug overlay + `TrackedEntity` formatter.
17. **[Medium]** (Upstream) `VFXTransformSystem` fill/consume assertions.
18. **[Medium]** (Upstream) `SyncVFXSystem.OnDestroy` teardown guards.
19. **[Low]** KillJob dormant-clip scan (only if profiled), version-wrap skip-0, `HybridVisualEffect` transform-reset warning, README/docs pass (latency, lifetime = clip length, Persistent requirement), naming/marker/getter nits.
