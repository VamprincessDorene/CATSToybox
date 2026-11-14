using System;
using System.Numerics;

namespace SignSpeak.Signs;

/// <summary>
/// One finger, described in rig-independent terms.
/// Everything is normalised so that the same letter definition works on any
/// race/skeleton -- the mapping from these numbers to actual bone euler angles
/// lives in <see cref="Rig.HandRig"/> and is calibrated once, in game.
/// </summary>
public struct FingerPose
{
    /// <summary>Curl of the proximal joint (the knuckle). 0 = straight, 1 = fully folded.</summary>
    public float CurlA;

    /// <summary>Curl of the distal joint (the tip). 0 = straight, 1 = fully folded.</summary>
    public float CurlB;

    /// <summary>Sideways splay. -1 .. 1, positive = away from the thumb.</summary>
    public float Spread;

    public FingerPose(float curlA, float curlB, float spread = 0f)
    {
        CurlA = curlA;
        CurlB = curlB;
        Spread = spread;
    }

    public static readonly FingerPose Straight = new(0f, 0f);
    public static readonly FingerPose Folded = new(1f, 1f);
    public static readonly FingerPose Relaxed = new(0.18f, 0.22f);

    public static FingerPose Lerp(FingerPose a, FingerPose b, float t) => new(
        a.CurlA + (b.CurlA - a.CurlA) * t,
        a.CurlB + (b.CurlB - a.CurlB) * t,
        a.Spread + (b.Spread - a.Spread) * t);
}

/// <summary>
/// A complete hand shape: five fingers plus the arm chain that carries the hand
/// into signing space. Arm angles are in degrees and are added on top of
/// whatever animation the character is currently playing.
/// </summary>
public struct HandPose
{
    public FingerPose Thumb;
    public FingerPose Index;
    public FingerPose Middle;
    public FingerPose Ring;
    public FingerPose Pinky;

    /// <summary>Extra rotation on the wrist bone, degrees. Used for orientation-carrying letters (G, H, P, Q) and motion letters (J, Z).</summary>
    public Vector3 Wrist;

    /// <summary>Extra rotation on the forearm, degrees. Blended in on top of the base signing posture.</summary>
    public Vector3 Forearm;

    /// <summary>Extra rotation on the upper arm, degrees.</summary>
    public Vector3 UpperArm;

    public HandPose(FingerPose thumb, FingerPose index, FingerPose middle, FingerPose ring, FingerPose pinky)
    {
        Thumb = thumb;
        Index = index;
        Middle = middle;
        Ring = ring;
        Pinky = pinky;
        Wrist = Vector3.Zero;
        Forearm = Vector3.Zero;
        UpperArm = Vector3.Zero;
    }

    /// <summary>A loose, natural hand. This is what we blend to between words and at the end of a message.</summary>
    public static HandPose Neutral => new(
        FingerPose.Relaxed, FingerPose.Relaxed, FingerPose.Relaxed, FingerPose.Relaxed, FingerPose.Relaxed);

    public HandPose With(Vector3? wrist = null, Vector3? forearm = null, Vector3? upperArm = null)
    {
        var copy = this;
        if (wrist.HasValue) copy.Wrist = wrist.Value;
        if (forearm.HasValue) copy.Forearm = forearm.Value;
        if (upperArm.HasValue) copy.UpperArm = upperArm.Value;
        return copy;
    }

    public static HandPose Lerp(HandPose a, HandPose b, float t) => new()
    {
        Thumb = FingerPose.Lerp(a.Thumb, b.Thumb, t),
        Index = FingerPose.Lerp(a.Index, b.Index, t),
        Middle = FingerPose.Lerp(a.Middle, b.Middle, t),
        Ring = FingerPose.Lerp(a.Ring, b.Ring, t),
        Pinky = FingerPose.Lerp(a.Pinky, b.Pinky, t),
        Wrist = Vector3.Lerp(a.Wrist, b.Wrist, t),
        Forearm = Vector3.Lerp(a.Forearm, b.Forearm, t),
        UpperArm = Vector3.Lerp(a.UpperArm, b.UpperArm, t),
    };
}

/// <summary>One held shape within a sign. Most letters are a single key; J and Z are several.</summary>
public struct SignKey
{
    public HandPose Pose;

    /// <summary>Seconds to travel into this key from the previous one, at 1x speed.</summary>
    public float TransitionSeconds;

    /// <summary>Seconds to hold this key once reached, at 1x speed.</summary>
    public float HoldSeconds;

    public SignKey(HandPose pose, float transition = 0.12f, float hold = 0.16f)
    {
        Pose = pose;
        TransitionSeconds = transition;
        HoldSeconds = hold;
    }
}

/// <summary>A complete sign: a letter, a digit, or (later) a whole word.</summary>
public sealed class Sign
{
    public required string Name { get; init; }
    public required SignKey[] Keys { get; init; }

    /// <summary>Human-readable note about how the sign is formed. Shown in the UI.</summary>
    public string Description { get; init; } = string.Empty;

    public static Sign Static(string name, HandPose pose, string description = "", float hold = 0.18f)
        => new() { Name = name, Keys = [new SignKey(pose, 0.12f, hold)], Description = description };
}
