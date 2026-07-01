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
            Target targetMode,
            ushort linkKey,
            Entity self,
            in UnsafeComponentLookup<Targets> targetsLookup,
            in UnsafeComponentLookup<EntityLinkSource> sources,
            in UnsafeBufferLookup<EntityLinkEntry> links,
            out Entity resolved)
        {
            resolved = Entity.Null;

            if (!TryResolveTarget(targetMode, self, targetsLookup, out var target))
            {
                return false;
            }

            if (linkKey == 0)
            {
                resolved = target;
                return true;
            }

            if (EntityLinkResolver.TryResolve(target, linkKey, sources, links, out var linked) && linked != Entity.Null)
            {
                resolved = linked;
                return true;
            }

            // Link key present but unresolved: fall back to the base target rather than firing nowhere.
            resolved = target;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryResolveTarget(
            Target target,
            Entity binding,
            in UnsafeComponentLookup<Targets> targets,
            out Entity resolved)
        {
            if (target is Target.Self or Target.None)
            {
                resolved = binding;
                return true;
            }

            if (targets.TryGetComponent(binding, out var t))
            {
                resolved = t.Get(target, binding);
                return resolved != Entity.Null;
            }

            resolved = Entity.Null;
            return false;
        }
    }
}
