using System;
using System.Collections.Generic;
using System.Numerics;
using SignSpeak.Rig;

namespace SignSpeak.Signs;

/// <summary>One bone of a hand-authored sign, in a shape Newtonsoft can store.</summary>
public sealed class StoredBone
{
    public Vector3 Translation { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>Scale offset, 0 meaning unchanged. See <see cref="BoneOverride.ScaleOffset"/>.</summary>
    public Vector3 ScaleOffset { get; set; }
}

/// <summary>
/// A handshape imported from a pose file, mapped to one character.
///
/// An imported sign completely replaces the procedural shape for that
/// character and bypasses the curl calibration, because it was authored on the
/// real skeleton in game and is therefore already correct. That includes any
/// arm bones the file carries, so the plugin must not add its own signing
/// posture on top or the arm doubles up.
/// </summary>
public sealed class ImportedSign
{
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>Which hand the file's bone names refer to, detected on import.</summary>
    public bool RightHand { get; set; } = true;

    /// <summary>True when the file poses the arm chain and not just fingers.</summary>
    public bool CarriesArm { get; set; }

    public Dictionary<string, StoredBone> Bones { get; set; } = new();

    /// <summary>Seconds to travel into this shape, at 1x speed.</summary>
    public float TransitionSeconds { get; set; } = 0.14f;

    /// <summary>Seconds to hold it, at 1x speed.</summary>
    public float HoldSeconds { get; set; } = 0.18f;
}

/// <summary>
/// The single place that answers "what does character X look like right now".
///
/// Imported poses win over the procedural alphabet, so the two can be mixed
/// freely: hand-author the letters you care about and let the rest fall back to
/// the built-in shapes. Everything comes out as a <see cref="ResolvedPose"/>,
/// which is what lets the player blend between a hand-authored letter and a
/// procedural one without special cases.
/// </summary>
public sealed class SignLibrary
{
    public JointAxis[,] Calibration { get; set; } = HandRig.DefaultCalibration();
    public SigningPosture Posture { get; set; } = new();
    public bool RightHanded { get; set; } = true;

    /// <summary>
    /// Whether to send the positional offsets an imported pose carries. Off by
    /// default, because the two plugins apply them differently and Customize+
    /// cannot be made to agree.
    ///
    /// LivePose adds the offset straight into model space, unrotated
    /// (SkeletonService.cs: <c>temp.Position += info.Transform.Position</c>).
    /// Customize+ first rotates it by the bone's own model rotation
    /// (BoneTransform.ModifyExistingTranslationWithRotation:
    /// <c>Vector4.Transform(Translation, tr.Rotation)</c>). Undoing that would
    /// mean knowing each bone's live model rotation, which is not available
    /// through the profile API.
    ///
    /// On the arm chain it costs nothing, since those offsets are zero -- which
    /// is exactly why the arm looked right while the fingers did not. On finger
    /// joints it throws them several millimetres the wrong way, and propagation
    /// then cascades it down the chain.
    ///
    /// Rotation and scale have no such problem and are always applied, and
    /// rotation is what a handshape actually consists of. The one real loss is a
    /// bone the author moved rather than rotated -- the thumb in some letters --
    /// which is called out in the README.
    /// </summary>
    public bool UseImportedTranslations { get; set; }

    /// <summary>Hand-authored overrides, keyed by character.</summary>
    public Dictionary<char, ImportedSign> Imported { get; } = new();

    private readonly Dictionary<string, Vector3> _eulerScratch = new(24);

    public bool IsImported(char character) => Imported.ContainsKey(char.ToUpperInvariant(character));

    public bool Has(char character) => Alphabet.TryGet(character, out _);

    public int KeyCount(char character)
    {
        character = char.ToUpperInvariant(character);

        // Imported shapes are a single held pose; only the built-in motion
        // letters J and Z have more than one key.
        if (Imported.ContainsKey(character))
            return 1;

        return Alphabet.TryGet(character, out var sign) ? sign.Keys.Length : 0;
    }

    public (float Transition, float Hold) Timing(char character, int keyIndex)
    {
        character = char.ToUpperInvariant(character);

        if (Imported.TryGetValue(character, out var imported))
            return (imported.TransitionSeconds, imported.HoldSeconds);

        if (!Alphabet.TryGet(character, out var sign) || keyIndex >= sign.Keys.Length)
            return (0.12f, 0.18f);

        var key = sign.Keys[keyIndex];
        return (key.TransitionSeconds, key.HoldSeconds);
    }

    /// <summary>
    /// Fills <paramref name="into"/> with the bone offsets for one key of one
    /// character. An unknown character resolves to rest, which reads as a pause.
    /// </summary>
    public void Resolve(char character, int keyIndex, ResolvedPose into)
    {
        into.Clear();
        character = char.ToUpperInvariant(character);

        if (Imported.TryGetValue(character, out var imported))
        {
            // Translations are dropped unless explicitly enabled. The file keeps
            // them either way, so the setting can be flipped without re-importing.
            foreach (var (bone, stored) in imported.Bones)
            {
                into.Set(bone, new BoneOverride(
                    UseImportedTranslations ? stored.Translation : Vector3.Zero,
                    stored.Rotation,
                    stored.ScaleOffset));
            }

            return;
        }

        if (!Alphabet.TryGet(character, out var sign))
            return;

        var key = sign.Keys[Math.Clamp(keyIndex, 0, sign.Keys.Length - 1)];

        HandRig.Build(key.Pose, Calibration, Posture, RightHanded, _eulerScratch);

        foreach (var (bone, euler) in _eulerScratch)
            into.Set(bone, new BoneOverride(Vector3.Zero, EulerConvention.ToQuaternion(euler)));
    }

    /// <summary>
    /// Stores an imported pose against a character, keeping only bones that
    /// belong to a hand or arm so an unrelated full-body pose cannot move the
    /// character's legs.
    /// </summary>
    public ImportedSign Assign(char character, string sourceFile, Dictionary<string, BoneOverride> bones)
    {
        var sign = new ImportedSign { SourceFile = sourceFile };
        var right = 0;
        var left = 0;

        foreach (var (bone, value) in bones)
        {
            if (!IsHandOrArmBone(bone))
                continue;

            sign.Bones[bone] = new StoredBone
            {
                Translation = value.Translation,
                Rotation = value.Rotation,
                ScaleOffset = value.ScaleOffset,
            };

            if (bone.EndsWith("_r", StringComparison.Ordinal)) right++;
            else if (bone.EndsWith("_l", StringComparison.Ordinal)) left++;

            if (IsArmBone(bone))
                sign.CarriesArm = true;
        }

        sign.RightHand = right >= left;
        Imported[char.ToUpperInvariant(character)] = sign;
        return sign;
    }

    public bool Remove(char character) => Imported.Remove(char.ToUpperInvariant(character));

    /// <summary>
    /// Bones a signing pose is allowed to touch. Deliberately permissive about
    /// finger naming so extended skeletons (the iv_*_c_* third phalanges that
    /// IVCS-style rigs add) come through, while refusing anything below the
    /// shoulder line.
    /// </summary>
    public static bool IsHandOrArmBone(string bone)
    {
        if (IsArmBone(bone))
            return true;

        foreach (var stem in new[] { "oya", "hito", "naka", "kusu", "ko" })
        {
            if (bone.Contains("_" + stem + "_", StringComparison.Ordinal)
                || bone.StartsWith("j_" + stem + "_", StringComparison.Ordinal)
                || bone.StartsWith("iv_" + stem + "_", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsArmBone(string bone)
        => bone.StartsWith("j_ude_", StringComparison.Ordinal)
           || bone.StartsWith("j_te", StringComparison.Ordinal)
           || bone.StartsWith("n_hte", StringComparison.Ordinal)
           || bone.StartsWith("n_hhiji", StringComparison.Ordinal)
           || bone.StartsWith("j_sako", StringComparison.Ordinal)
           || bone.StartsWith("n_hkata", StringComparison.Ordinal);
}
