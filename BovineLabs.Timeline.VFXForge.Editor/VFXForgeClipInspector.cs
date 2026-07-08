namespace BovineLabs.Timeline.VFXForge.Editor
{
    using BovineLabs.Timeline.VFXForge.Authoring;
    using UnityEditor;

    /// <summary>
    /// Draws the default <see cref="VFXForgeClip"/> inspector plus an error <c>HelpBox</c> repeating
    /// <see cref="VFXForgeClipValidation"/> so a misconfiguration is visible while editing the clip, not just as the
    /// small Timeline badge tooltip.
    /// </summary>
    [CustomEditor(typeof(VFXForgeClip))]
    public class VFXForgeClipInspector : Editor
    {
        /// <inheritdoc/>
        public override void OnInspectorGUI()
        {
            var error = VFXForgeClipValidation.Validate(this.target as VFXForgeClip);
            if (!string.IsNullOrEmpty(error))
            {
                EditorGUILayout.HelpBox(error, MessageType.Error);
            }

            this.DrawDefaultInspector();
        }
    }
}
