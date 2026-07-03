namespace BovineLabs.Timeline.VFXForge.Data
{
    using BovineLabs.Reaction.Data.Core;
    using BovineLabs.Timeline.EntityLinks.Data;
    using FireAlt.VFXForge.Data;
    using Unity.Entities;

    /// <summary>
    /// Baked from <c>VFXForgeClip</c>. Describes which Fire Alt VFX Forge effect to play and which entity to play it at.
    /// </summary>
    public struct VFXForgeClipData : IComponentData
    {
        /// <summary>The Fire Alt <c>VFXDefinition</c>'s stable key (resolved from the asset at bake).</summary>
        public VFXKey Key;

        /// <summary>Resolves the entity the VFX plays at: a base <see cref="Target"/> slot relative to the track's
        /// bound entity, optionally re-routed through an EntityLink to a linked entity.</summary>
        public EntityLinkRef Route;
    }

    /// <summary>
    /// Per-clip runtime state: the persistent Fire Alt VFX instance spawned on the clip's start edge.
    /// <see cref="TrackedEntity.Null"/> while nothing is spawned. Cleared when the instance is killed on the clip end,
    /// so a re-entry spawns a brand-new instance (plays from the start).
    /// </summary>
    public struct VFXForgeRuntimeState : IComponentData
    {
        public TrackedEntity Tracked;
    }

    /// <summary>
    /// Destruction-surviving mirror of the spawned persistent instance. Added when <see cref="VFXForgeRuntimeState"/>
    /// spawns, removed when the clip kills it normally. If the clip entity is destroyed mid-active (SubScene unload /
    /// world teardown) the plain components vanish but this cleanup component survives, so a reaper can still kill the
    /// orphaned Fire Alt persistent instance and free its fixed-capacity slot. Carries the Key because
    /// <see cref="VFXForgeClipData"/> is gone once the entity dies.
    /// </summary>
    public struct VFXForgeCleanup : ICleanupComponentData
    {
        public VFXKey Key;
        public TrackedEntity Tracked;
    }
}
