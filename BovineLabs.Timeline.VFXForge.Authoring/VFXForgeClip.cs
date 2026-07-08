namespace BovineLabs.Timeline.VFXForge.Authoring
{
    using BovineLabs.Reaction.Data.Core;
    using BovineLabs.Timeline.Authoring;
    using BovineLabs.Timeline.EntityLinks.Authoring;
    using BovineLabs.Timeline.VFXForge.Data;
    using FireAlt.VFXForge.Data;
    using Unity.Entities;
    using UnityEngine;
    using UnityEngine.Timeline;

    /// <summary>
    /// Plays one Fire Alt VFX Forge effect for the clip's duration. The bake stores the chosen <see cref="VFXDefinition"/>'s
    /// stable key plus a target route; at runtime <c>VFXForgeSpawnSystem</c> spawns a persistent instance on the clip's
    /// start edge (tracking the resolved target so it follows that entity) and kills it on the clip's end. A finished
    /// instance is never re-shown — re-entry spawns a fresh one, so it always plays from the start.
    /// </summary>
    public sealed class VFXForgeClip : DOTSClip, ITimelineClipAsset
    {
        [Tooltip("Optional EntityLink schema; re-routes from the resolved target to a linked entity (e.g. a specific enemy). Leave empty to use the route target directly.")]
        public EntityLinkSchema routeLink;

        [Tooltip("The Fire Alt VFX to play. Must be a PERSISTENT VFXDefinition: it is spawned on the clip start and killed on the clip end. A HybridVisualEffect for this definition must exist in the loaded scene/subscene so its VFXKey is registered.")]
        public VFXDefinition definition;

        [Tooltip("Which entity the VFX plays at / follows, relative to the track's bound entity. Matches the Reaction Target enum: " +
            "None (no target), Target, Owner, Source, Self (the bound entity itself), or Custom. Default is Self.")]
        public Target routeTo = Target.Self;

        /// <inheritdoc/>
        public override double duration => 1;

        // Spawn-on-start / kill-on-end with replay on re-entry. No Blending (the runtime is a binary ClipActive
        // enable/disable — clip weight is never read, so a crossfade region would be a lie) and no Looping (a baked
        // spawn is edge-driven; a looping clip would not re-fire it).
        public ClipCaps clipCaps => ClipCaps.None;

        /// <inheritdoc/>
        public override void Bake(Entity clipEntity, BakingContext context)
        {
            // Single source of truth: the same check the editor ClipEditor surfaces as a badge. Warn but continue
            // baking, EXCEPT for the null-definition case which has nothing to bake.
            var error = VFXForgeClipValidation.Validate(this);
            if (error != null)
            {
                Debug.LogWarning($"VFXForgeClip '{this.name}': {error}", this);
            }

            if (this.definition == null)
            {
                base.Bake(clipEntity, context);
                return;
            }

            context.Baker.DependsOn(this.definition);

            context.Baker.AddComponent(clipEntity, new VFXForgeClipData
            {
                Key = this.definition,
                Route = EntityLinkAuthoringUtility.BakeRef(context.Baker, this.routeLink, this.routeTo),
            });
            context.Baker.AddComponent(clipEntity, new VFXForgeRuntimeState { Tracked = TrackedEntity.Null });

            base.Bake(clipEntity, context);
        }
    }
}
