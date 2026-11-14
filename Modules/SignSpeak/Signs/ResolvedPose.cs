using System.Collections.Generic;
using System.Numerics;

namespace SignSpeak.Signs;

/// <summary>One bone's offset from whatever the animation is doing.</summary>
public struct BoneOverride
{
    public Vector3 Translation;
    public Quaternion Rotation;

    /// <summary>
    /// Scale offset, where 0 means "leave it alone" — deliberately stored as an
    /// offset rather than a multiplier so that a default-constructed
    /// <see cref="BoneOverride"/> is neutral instead of collapsing the bone to
    /// zero size. It also matches how pose files record it:
    /// LivePose does <c>temp.Scale += info.Transform.Scale</c>.
    ///
    /// Customize+ multiplies instead, so it is emitted as <c>1 + ScaleOffset</c>.
    /// </summary>
    public Vector3 ScaleOffset;

    public BoneOverride(Vector3 translation, Quaternion rotation)
        : this(translation, rotation, Vector3.Zero)
    {
    }

    public BoneOverride(Vector3 translation, Quaternion rotation, Vector3 scaleOffset)
    {
        Translation = translation;
        Rotation = rotation;
        ScaleOffset = scaleOffset;
    }

    public static readonly BoneOverride Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.Zero);
}

/// <summary>
/// A handshape reduced to concrete per-bone local offsets.
///
/// Everything converges here before it reaches Customize+: procedural letters
/// come through the curl calibration, imported letters come straight out of a
/// pose file, and both end up as the same thing. Blending happens in this
/// space using quaternion slerp rather than on euler triples, which is what
/// makes it safe to interpolate between a hand-authored pose and a procedural
/// one, or between two letters that are 120 degrees apart.
/// </summary>
public sealed class ResolvedPose
{
    public Dictionary<string, BoneOverride> Bones { get; } = new(24);

    public void Clear() => Bones.Clear();

    public void Set(string bone, in BoneOverride value) => Bones[bone] = value;

    public void CopyFrom(ResolvedPose other)
    {
        Bones.Clear();
        foreach (var (bone, value) in other.Bones)
            Bones[bone] = value;
    }

    private BoneOverride Get(string bone)
        => Bones.TryGetValue(bone, out var value) ? value : BoneOverride.Identity;

    /// <summary>
    /// Interpolates two poses into <paramref name="into"/>.
    ///
    /// A bone present in only one of the two is treated as identity in the
    /// other, so a letter that poses the pinky blends cleanly against one that
    /// does not, instead of the pinky snapping.
    /// </summary>
    public static void Blend(ResolvedPose from, ResolvedPose to, float t, ResolvedPose into)
    {
        into.Clear();

        foreach (var (bone, a) in from.Bones)
        {
            var b = to.Get(bone);
            into.Bones[bone] = Mix(a, b, t);
        }

        foreach (var (bone, b) in to.Bones)
        {
            if (into.Bones.ContainsKey(bone))
                continue;

            into.Bones[bone] = Mix(from.Get(bone), b, t);
        }
    }

    /// <summary>
    /// Fades the whole pose in from, or out to, the character's untouched
    /// animation. This is what lifts the arm smoothly at the start of a
    /// message and lowers it at the end, and it works identically for
    /// procedural and imported letters.
    /// </summary>
    public void ScaleFromRest(float weight, ResolvedPose into)
    {
        into.Clear();
        foreach (var (bone, value) in Bones)
            into.Bones[bone] = Mix(BoneOverride.Identity, value, weight);
    }

    private static BoneOverride Mix(in BoneOverride a, in BoneOverride b, float t) => new(
        Vector3.Lerp(a.Translation, b.Translation, t),
        Quaternion.Slerp(a.Rotation, b.Rotation, t),
        Vector3.Lerp(a.ScaleOffset, b.ScaleOffset, t));
}
