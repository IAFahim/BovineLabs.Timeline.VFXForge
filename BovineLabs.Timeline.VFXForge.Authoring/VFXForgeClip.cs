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
        [Tooltip("The Fire Alt VFX to play. Must be a PERSISTENT VFXDefinition: it is spawned on the clip start and killed on the clip end. A HybridVisualEffect for this definition must exist in the loaded scene/subscene so its VFXKey is registered.")]
        public VFXDefinition definition;

        [Tooltip("Which entity the VFX plays at / follows, relative to the track's bound entity: Owner, Source, Target, or Self (the bound entity itself).")]
        public Target routeTo = Target.Self;

        [Tooltip("Optional EntityLink schema; re-routes from the resolved target to a linked entity (e.g. a specific enemy). Leave empty to use the route target directly.")]
        public EntityLinkSchema routeLink;

        /// <inheritdoc/>
        public override double duration => 1;

        // Spawn-on-start / kill-on-end with replay on re-entry. Blending only (no Looping: a baked spawn is edge-driven,
        // a looping clip would not re-fire it).
        public ClipCaps clipCaps => ClipCaps.Blending;

        /// <inheritdoc/>
        public override void Bake(Entity clipEntity, BakingContext context)
        {
            if (this.definition == null)
            {
                Debug.LogWarning($"VFXForgeClip '{this.name}' has no VFXDefinition assigned; it will play nothing.", this);
                base.Bake(clipEntity, context);
                return;
            }

            if (this.definition.vfxType != VFXType.Persistent)
            {
                Debug.LogWarning(
                    $"VFXForgeClip '{this.name}' references VFXDefinition '{this.definition.name}' which is " +
                    $"'{this.definition.vfxType}', but this track spawns and kills a Persistent instance. Set the " +
                    $"definition's vfxType to Persistent or the clip will play nothing.",
                    this);
            }

            // Key 0 = the FireAlt UID postprocessor hasn't stamped this definition yet; ContainsPersistent(0) is
            // false at runtime so the clip silently plays nothing. Tell the designer to re-import/save the definition.
            if (((FireAlt.VFXForge.Data.VFXKey)this.definition).Value == 0)
            {
                Debug.LogWarning(
                    $"VFXForgeClip '{this.name}' references VFXDefinition '{this.definition.name}' whose VFXKey is 0 " +
                    "(unregistered); it will play nothing at runtime. Re-import/save the definition so the FireAlt UID " +
                    "postprocessor assigns it a key.",
                    this);
            }

            EntityLinkAuthoringUtility.TryGetKey(this.routeLink, out var linkKey);
            context.Baker.DependsOn(this.definition);

            context.Baker.AddComponent(clipEntity, new VFXForgeClipData
            {
                Key = this.definition,
                RouteTo = this.routeTo,
                RouteLinkKey = linkKey,
            });
            context.Baker.AddComponent(clipEntity, new VFXForgeRuntimeState { Tracked = TrackedEntity.Null });

            base.Bake(clipEntity, context);
        }
    }
}
