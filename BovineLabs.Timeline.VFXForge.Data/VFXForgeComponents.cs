namespace BovineLabs.Timeline.VFXForge.Data
{
    using BovineLabs.Reaction.Data.Core;
    using FireAlt.VFXForge.Data;
    using Unity.Entities;

    /// <summary>
    /// Baked from <c>VFXForgeClip</c>. Describes which Fire Alt VFX Forge effect to play and which entity to play it at.
    /// </summary>
    public struct VFXForgeClipData : IComponentData
    {
        /// <summary>The Fire Alt <c>VFXDefinition</c>'s stable key (resolved from the asset at bake).</summary>
        public VFXKey Key;

        /// <summary>Base target relative to the track's bound entity (Owner / Source / Target / Self).</summary>
        public Target RouteTo;

        /// <summary>Optional EntityLink re-route key (0 = none); re-points the resolved target to a linked entity.</summary>
        public ushort RouteLinkKey;
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
}
