namespace BovineLabs.Timeline.VFXForge.Editor
{
    using BovineLabs.Timeline.VFXForge.Authoring;
    using UnityEditor.Timeline;
    using UnityEngine.Timeline;

    /// <summary>
    /// Surfaces <see cref="VFXForgeClipValidation"/> in the Timeline window: a misconfigured <see cref="VFXForgeClip"/>
    /// (missing / non-Persistent / unregistered / data-carrying definition) draws Timeline's native error badge with the
    /// validation message as its tooltip, instead of failing silently at runtime.
    /// </summary>
    [CustomTimelineEditor(typeof(VFXForgeClip))]
    public class VFXForgeClipTimelineEditor : ClipEditor
    {
        /// <inheritdoc/>
        public override ClipDrawOptions GetClipOptions(TimelineClip clip)
        {
            var options = base.GetClipOptions(clip);

            // Only override the base errorText (no-playable / broken-script checks) when our validation actually
            // finds a problem, so those built-in checks still show through when the clip itself is otherwise fine.
            var error = VFXForgeClipValidation.Validate(clip?.asset as VFXForgeClip);
            if (!string.IsNullOrEmpty(error))
            {
                options.errorText = error;
            }

            return options;
        }
    }
}
