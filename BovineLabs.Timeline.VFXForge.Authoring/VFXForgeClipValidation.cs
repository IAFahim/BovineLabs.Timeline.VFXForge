namespace BovineLabs.Timeline.VFXForge.Authoring
{
    using FireAlt.VFXForge.Data;

    /// <summary>
    /// Single source of truth for <see cref="VFXForgeClip"/> misconfiguration checks. Consumed by
    /// <c>VFXForgeClip.Bake</c> (import-time warnings) and the editor <c>VFXForgeClipTimelineEditor</c>
    /// (Timeline error badge + tooltip) so the two never drift.
    /// </summary>
    public static class VFXForgeClipValidation
    {
        /// <summary>
        /// Validates a clip's definition assignment. Returns <c>null</c> when the clip is valid, otherwise the first
        /// error message (checks run in order of severity). Every message is self-contained so it reads correctly
        /// both as an import-log warning and as a Timeline clip tooltip.
        /// </summary>
        /// <param name="clip">The clip to validate. A <c>null</c> clip is treated as valid (nothing to report).</param>
        /// <returns>The first error message, or <c>null</c> when the clip is valid.</returns>
        public static string Validate(VFXForgeClip clip)
        {
            if (clip == null)
            {
                return null;
            }

            var definition = clip.definition;

            if (definition == null)
            {
                return "No VFXDefinition assigned; the clip will play nothing.";
            }

            if (definition.vfxType != VFXType.Persistent)
            {
                return $"VFXDefinition '{definition.name}' is '{definition.vfxType}', but this clip spawns and kills a " +
                    "Persistent instance. Set the definition's vfxType to Persistent or the clip will play nothing.";
            }

            // Key 0 = the FireAlt UID postprocessor has not stamped this definition yet; ContainsPersistent(0) is false
            // at runtime so the clip silently plays nothing.
            if (((VFXKey)definition).Value == 0)
            {
                return $"VFXDefinition '{definition.name}' has a VFXKey of 0 (unregistered); it will play nothing at " +
                    "runtime. Re-save the definition so the FireAlt UID postprocessor assigns a key.";
            }

            // The clip spawns via the data-less Spawn overload, so a definition that declares a data / array-data type
            // would have its pooled slots read whatever the previous occupant left in the GPU buffers. Read the raw
            // serialized ulong type ids rather than DataGpuSize/ArrayDataGpuSize so this never touches the (possibly
            // uninitialized) VFXTypeRegistry at bake/editor time.
            if (definition.vfxDataType != 0 || definition.vfxArrayDataType != 0)
            {
                return $"VFXForgeClip cannot supply per-spawn data; definition '{definition.name}' declares a VFX data " +
                    "type — its spawned instances would read stale buffer data. Use a data-less definition.";
            }

            return null;
        }
    }
}
