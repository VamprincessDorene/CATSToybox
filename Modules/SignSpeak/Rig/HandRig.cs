using System;
using System.Collections.Generic;
using System.Numerics;
using SignSpeak.Signs;

namespace SignSpeak.Rig;

/// <summary>
/// Which way a given joint bends, and how far.
///
/// We cannot know from outside the game which local axis curls a finger; it
/// depends on how the skeleton was authored. So instead of baking euler angles
/// into every letter, each letter stores an abstract 0..1 curl and this class
/// turns that into degrees. Calibrate these once in game and all 36 characters
/// snap into place at the same time.
/// </summary>
public sealed class JointAxis
{
    /// <summary>Unit axis (in bone local space) that folds the finger toward the palm.</summary>
    public Vector3 CurlAxis { get; set; } = new(0f, 0f, -1f);

    /// <summary>Degrees of rotation about <see cref="CurlAxis"/> at curl = 1.</summary>
    public float CurlDegrees { get; set; } = 80f;

    /// <summary>Unit axis that splays the finger sideways, away from the thumb.</summary>
    public Vector3 SpreadAxis { get; set; } = new(0f, 1f, 0f);

    /// <summary>Degrees of rotation about <see cref="SpreadAxis"/> at spread = 1.</summary>
    public float SpreadDegrees { get; set; } = 12f;

    public Vector3 Evaluate(float curl, float spread)
        => CurlAxis * (curl * CurlDegrees) + SpreadAxis * (spread * SpreadDegrees);

    public JointAxis Clone() => new()
    {
        CurlAxis = CurlAxis,
        CurlDegrees = CurlDegrees,
        SpreadAxis = SpreadAxis,
        SpreadDegrees = SpreadDegrees,
    };
}

/// <summary>The five fingers, in the order we always iterate them.</summary>
public enum Finger
{
    Thumb,
    Index,
    Middle,
    Ring,
    Pinky,
}

/// <summary>
/// Bone names and the curl calibration for one hand.
///
/// FFXIV finger bone naming is Japanese: oya = thumb, hito = index
/// (hitosashi), naka = middle, kusu = ring (kusuri), ko = pinky. Each finger
/// has two posable joints, _a (proximal) and _b (distal). Side suffix is
/// _l / _r.
/// </summary>
public static class HandRig
{
    public const string ThumbStem = "j_oya";
    public const string IndexStem = "j_hito";
    public const string MiddleStem = "j_naka";
    public const string RingStem = "j_kusu";
    public const string PinkyStem = "j_ko";

    public static string Stem(Finger finger) => finger switch
    {
        Finger.Thumb => ThumbStem,
        Finger.Index => IndexStem,
        Finger.Middle => MiddleStem,
        Finger.Ring => RingStem,
        Finger.Pinky => PinkyStem,
        _ => throw new ArgumentOutOfRangeException(nameof(finger)),
    };

    public static string Suffix(bool rightHand) => rightHand ? "_r" : "_l";

    public static string JointBone(Finger finger, bool distal, bool rightHand)
        => Stem(finger) + (distal ? "_b" : "_a") + Suffix(rightHand);

    public static string WristBone(bool rightHand) => "j_te" + Suffix(rightHand);

    public static string ForearmBone(bool rightHand) => "j_ude_b" + Suffix(rightHand);

    public static string UpperArmBone(bool rightHand) => "j_ude_a" + Suffix(rightHand);

    public static string ClavicleBone(bool rightHand) => "j_sako" + Suffix(rightHand);

    /// <summary>Every bone this plugin writes to, for one hand.</summary>
    public static IEnumerable<string> AllBones(bool rightHand)
    {
        foreach (Finger finger in Enum.GetValues<Finger>())
        {
            yield return JointBone(finger, false, rightHand);
            yield return JointBone(finger, true, rightHand);
        }

        yield return WristBone(rightHand);
        yield return ForearmBone(rightHand);
        yield return UpperArmBone(rightHand);
        yield return ClavicleBone(rightHand);
    }

    /// <summary>
    /// Turns an abstract <see cref="HandPose"/> into concrete per-bone euler
    /// angles in degrees, ready to hand to Customize+.
    /// </summary>
    /// <param name="pose">The shape to build.</param>
    /// <param name="calibration">Per-joint axis calibration, indexed [finger, distal ? 1 : 0].</param>
    /// <param name="posture">Base arm offset that lifts the hand into signing space.</param>
    /// <param name="rightHand">Which hand to build for. Spread mirrors on the left.</param>
    /// <param name="into">Destination map, cleared before writing.</param>
    public static void Build(
        in HandPose pose,
        JointAxis[,] calibration,
        SigningPosture posture,
        bool rightHand,
        Dictionary<string, Vector3> into)
    {
        into.Clear();

        // The left hand is a mirror, so sideways splay has to flip or the
        // fingers splay into each other instead of apart.
        var spreadSign = rightHand ? 1f : -1f;
        var mirror = rightHand ? 1f : -1f;

        WriteFinger(Finger.Thumb, pose.Thumb);
        WriteFinger(Finger.Index, pose.Index);
        WriteFinger(Finger.Middle, pose.Middle);
        WriteFinger(Finger.Ring, pose.Ring);
        WriteFinger(Finger.Pinky, pose.Pinky);

        // Arm chain: the posture holds the hand up in front of the chest for the
        // whole message, and the per-letter offsets ride on top of it.
        Accumulate(into, ClavicleBone(rightHand), MirrorEuler(posture.Clavicle, mirror));
        Accumulate(into, UpperArmBone(rightHand), MirrorEuler(posture.UpperArm + pose.UpperArm, mirror));
        Accumulate(into, ForearmBone(rightHand), MirrorEuler(posture.Forearm + pose.Forearm, mirror));
        Accumulate(into, WristBone(rightHand), MirrorEuler(posture.Wrist + pose.Wrist, mirror));

        void WriteFinger(Finger finger, FingerPose fingerPose)
        {
            var proximal = calibration[(int)finger, 0];
            var distal = calibration[(int)finger, 1];

            Accumulate(into, JointBone(finger, false, rightHand),
                proximal.Evaluate(fingerPose.CurlA, fingerPose.Spread * spreadSign));

            // Splay comes entirely from the knuckle; the tip joint only curls.
            Accumulate(into, JointBone(finger, true, rightHand),
                distal.Evaluate(fingerPose.CurlB, 0f));
        }
    }

    /// <summary>
    /// Mirroring an euler triple across the sagittal plane negates the two axes
    /// that would otherwise send the left arm the wrong way.
    /// </summary>
    private static Vector3 MirrorEuler(Vector3 euler, float mirror)
        => mirror >= 0f ? euler : new Vector3(euler.X, -euler.Y, -euler.Z);

    private static void Accumulate(Dictionary<string, Vector3> into, string bone, Vector3 euler)
    {
        if (into.TryGetValue(bone, out var existing))
            into[bone] = existing + euler;
        else
            into[bone] = euler;
    }

    /// <summary>
    /// Sensible starting calibration. Almost certainly needs tuning in game;
    /// that is what the Calibrate tab is for.
    /// </summary>
    public static JointAxis[,] DefaultCalibration()
    {
        var calibration = new JointAxis[5, 2];

        foreach (Finger finger in Enum.GetValues<Finger>())
        {
            var isThumb = finger == Finger.Thumb;

            calibration[(int)finger, 0] = new JointAxis
            {
                CurlAxis = new Vector3(0f, 0f, -1f),
                // The thumb knuckle has far less range than the other fingers.
                CurlDegrees = isThumb ? 45f : 80f,
                SpreadAxis = new Vector3(0f, 1f, 0f),
                SpreadDegrees = isThumb ? 30f : 12f,
            };

            calibration[(int)finger, 1] = new JointAxis
            {
                CurlAxis = new Vector3(0f, 0f, -1f),
                CurlDegrees = isThumb ? 50f : 90f,
                SpreadAxis = new Vector3(0f, 1f, 0f),
                SpreadDegrees = 0f,
            };
        }

        return calibration;
    }
}

/// <summary>
/// Optional arm offset that lifts the signing hand up in front of the chest for
/// the duration of a message. Applies to the built-in shapes only.
///
/// This defaults to ZERO, deliberately. An earlier build shipped guessed
/// values here and they were wrong in a specific and ugly way: in the
/// Customize+ euler convention the Z component is roll about the bone's own
/// axis, so a 52-degree Z on the upper arm did not raise the arm, it twisted it
/// half a turn along its length. Arm angles cannot be derived from outside the
/// game, so the honest default is to not move the arm at all and let anyone who
/// wants a raise dial it in against the model with the sliders.
///
/// Imported poses supply their own arm bones and ignore this entirely.
/// </summary>
public sealed class SigningPosture
{
    public Vector3 Clavicle { get; set; } = Vector3.Zero;
    public Vector3 UpperArm { get; set; } = Vector3.Zero;
    public Vector3 Forearm { get; set; } = Vector3.Zero;
    public Vector3 Wrist { get; set; } = Vector3.Zero;

    public SigningPosture Clone() => new()
    {
        Clavicle = Clavicle,
        UpperArm = UpperArm,
        Forearm = Forearm,
        Wrist = Wrist,
    };
}
