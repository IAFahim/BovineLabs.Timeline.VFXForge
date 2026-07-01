namespace BovineLabs.Timeline.VFXForge.Authoring
{
    using System;
    using System.ComponentModel;
    using BovineLabs.Reaction.Authoring.Core;
    using BovineLabs.Timeline.Authoring;
    using UnityEngine.Timeline;

    /// <summary>
    /// Plays Fire Alt VFX Forge effects from a DOTS Timeline. Bind the track to an actor's <c>TargetsAuthoring</c>;
    /// each <see cref="VFXForgeClip"/> spawns a fresh persistent VFX instance on its start edge and kills it on its
    /// end. Re-entering a clip spawns a brand-new instance, so the effect always plays from the start (unlike the
    /// GameObjects Activation track, which only un-hides a finished/stale companion VFX).
    /// </summary>
    [Serializable]
    [TrackClipType(typeof(VFXForgeClip))]
    [TrackColor(0.6f, 0.2f, 0.85f)]
    [TrackBindingType(typeof(TargetsAuthoring))]
    [DisplayName("BovineLabs/Timeline/VFX Forge/Play VFX")]
    public sealed class VFXForgeTrack : DOTSTrack
    {
    }
}
