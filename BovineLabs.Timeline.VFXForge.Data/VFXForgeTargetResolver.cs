namespace BovineLabs.Timeline.VFXForge.Data
{
    using System.Runtime.CompilerServices;
    using BovineLabs.Core.Iterators;
    using BovineLabs.Reaction.Data.Core;
    using BovineLabs.Timeline.EntityLinks;
    using BovineLabs.Timeline.EntityLinks.Data;
    using Unity.Entities;

    /// <summary>
    /// Resolves the entity a clip should act on: first the base <see cref="Target"/> slot relative to the bound
    /// entity, then an optional EntityLink re-route to a linked entity (e.g. a specific enemy). Mirrors
    /// <c>TimelineEssenceResolver</c> so VFX targeting behaves identically to the Essence event/stat tracks, without
    /// depending on the Essence package.
    /// </summary>
    public static class VFXForgeTargetResolver
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryResolveLinkedTarget(
            in EntityLinkRef route,
            Entity self,
            in UnsafeComponentLookup<Targets> targetsLookup,
            in UnsafeComponentLookup<EntityLinkSource> sources,
            in UnsafeBufferLookup<EntityLinkEntry> links,
            out Entity resolved)
        {
            // Targets is optional on the bound entity: Self/None slots resolve to self regardless; other slots need it.
            targetsLookup.TryGetComponent(self, out var targets);
            return route.TryResolve(self, targets, sources, links, out resolved);
        }
    }
}
