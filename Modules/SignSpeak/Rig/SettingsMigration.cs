using System.Numerics;

namespace SignSpeak.Rig;

/// <summary>
/// Version stamping and upgrade steps for saved settings.
///
/// This lives apart from <c>Configuration</c>, which has to implement a Dalamud
/// interface and therefore cannot be exercised outside the game. The upgrade
/// logic is the part worth testing, so it goes here where the harness can reach
/// it.
/// </summary>
public static class SettingsMigration
{
    /// <summary>
    /// 1: initial.
    /// 2: signing posture reset to zero, because the shipped defaults twisted
    ///    the arm rather than raising it.
    /// 3: positional offsets enabled, now that LivePose is the primary backend
    ///    and applies them the way the pose files intend.
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// Applies any outstanding upgrades and reports the version to stamp back.
    /// </summary>
    /// <returns>True if anything changed and the caller should save.</returns>
    public static bool Migrate(
        int version,
        SigningPosture posture,
        ref bool useImportedTranslations,
        out int newVersion)
    {
        newVersion = version;

        if (version >= CurrentVersion)
            return false;

        if (version < 3)
        {
            // Earlier builds could only reach Customize+, which cannot apply a
            // bone translation the way pose files mean it, so these were forced
            // off. LivePose applies them correctly, so turn them back on. The
            // active backend still has the final say (see Plugin.SyncLibrary).
            useImportedTranslations = true;
        }

        if (version < 2)
        {
            // The original defaults put a large roll on the upper arm and
            // forearm. In the Customize+ euler convention the Z component is
            // roll about the bone's own axis, so rather than raising the arm
            // those values twisted it along its length. Anyone who saved them
            // still has them, so clear them out.
            posture.Clavicle = Vector3.Zero;
            posture.UpperArm = Vector3.Zero;
            posture.Forearm = Vector3.Zero;
            posture.Wrist = Vector3.Zero;
        }

        newVersion = CurrentVersion;
        return true;
    }
}
