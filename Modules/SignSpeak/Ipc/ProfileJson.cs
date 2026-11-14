using System.Collections.Generic;
using System;
using System.Globalization;
using System.Numerics;
using System.Text;
using SignSpeak.Rig;
using SignSpeak.Signs;

namespace SignSpeak.Ipc;

/// <summary>
/// Serialises a Customize+ IPCCharacterProfile by hand.
///
/// This runs on every animation tick, so it deliberately avoids a reflection
/// based serialiser and writes straight into a caller-owned buffer. Customize+
/// parses the result with Newtonsoft, which matches property names case
/// insensitively and reads the public fields of Vector3.
///
/// Kept free of any Dalamud reference so it can be exercised outside the game.
/// </summary>
public static class ProfileJson
{
    /// <summary>
    /// Writes a resolved pose, converting each bone's quaternion into the euler
    /// triple Customize+ expects and carrying translations through.
    /// </summary>
    public static string Build(StringBuilder buffer, ResolvedPose pose)
    {
        Begin(buffer);

        var first = true;
        foreach (var (bone, value) in pose.Bones)
        {
            // Skip bones that are not actually being moved. Since propagation
            // is on, an identity entry would still walk the whole descendant
            // chain for no benefit, and it bloats a payload that is rebuilt
            // dozens of times a second.
            if (IsNeutral(value))
                continue;

            AppendBone(
                buffer,
                bone,
                EulerConvention.ToEuler(value.Rotation),
                value.Translation,
                // Pose files record scale as an offset from the bone's current
                // scale; Customize+ multiplies. With a bone at its normal size
                // those agree, which is the case that matters here.
                Vector3.One + value.ScaleOffset,
                ref first);
        }

        return End(buffer);
    }

    /// <summary>Writes euler rotations directly, with no translation.</summary>
    public static string Build(StringBuilder buffer, IReadOnlyDictionary<string, Vector3> eulerDegrees)
    {
        Begin(buffer);

        var first = true;
        foreach (var (bone, euler) in eulerDegrees)
            AppendBone(buffer, bone, euler, Vector3.Zero, Vector3.One, ref first);

        return End(buffer);
    }

    /// <summary>
    /// Whether a bone override would do nothing. The rotation test uses |W| so
    /// that q and -q, which are the same rotation, both count as identity.
    /// </summary>
    private static bool IsNeutral(in BoneOverride value)
        => MathF.Abs(MathF.Abs(value.Rotation.W) - 1f) < 1e-5f
           && value.Translation.LengthSquared() < 1e-12f
           && value.ScaleOffset.LengthSquared() < 1e-10f;

    private static void Begin(StringBuilder buffer)
    {
        buffer.Clear();
        buffer.Append("{\"Bones\":{");
    }

    private static string End(StringBuilder buffer)
    {
        buffer.Append("}}");
        return buffer.ToString();
    }

    private static void AppendBone(
        StringBuilder buffer, string bone, Vector3 euler, Vector3 translation, Vector3 scaling,
        ref bool first)
    {
        if (!first)
            buffer.Append(',');
        first = false;

        buffer.Append('"').Append(bone).Append("\":{");
        AppendVector(buffer, "Translation", translation.X, translation.Y, translation.Z);
        buffer.Append(',');
        AppendVector(buffer, "Rotation", euler.X, euler.Y, euler.Z);
        buffer.Append(',');
        AppendVector(buffer, "Scaling", scaling.X, scaling.Y, scaling.Z);
        buffer.Append(',');
        AppendVector(buffer, "ChildScaling", 1f, 1f, 1f);
        // Propagation is mandatory here, not optional.
        //
        // Customize+ writes bone transforms in MODEL space
        // (ModelBone.ApplyModelTransform -> SetGameTransform(..., PoseType.Model)),
        // so rotating a bone does not carry its children the way a local-space
        // edit would. Without propagation the forearm and hand stay exactly
        // where the animation left them while the upper arm turns, and the skin
        // stretches between the two: a visibly twisted, mangled limb. The same
        // applies one joint down, where a curled knuckle would leave its
        // fingertip behind.
        //
        // Translation and rotation propagate; scale does not. That is exactly
        // the combination LivePose records ("Propogate": 3, which is
        // Position | Rotation from its TransformComponents flags), so a scaled
        // wrist resizes the hand without dragging the fingers along with it.
        buffer.Append(",\"PropagateTranslation\":true")
              .Append(",\"PropagateRotation\":true")
              .Append(",\"PropagateScale\":false")
              .Append(",\"ChildScaleIndependent\":false")
              .Append('}');
    }

    private static void AppendVector(StringBuilder buffer, string name, float x, float y, float z)
    {
        buffer.Append('"').Append(name).Append("\":{\"X\":");
        AppendFloat(buffer, x);
        buffer.Append(",\"Y\":");
        AppendFloat(buffer, y);
        buffer.Append(",\"Z\":");
        AppendFloat(buffer, z);
        buffer.Append('}');
    }

    private static void AppendFloat(StringBuilder buffer, float value)
    {
        // Invariant culture matters: on a locale with a comma decimal separator
        // this would emit JSON with the wrong number of fields.
        buffer.Append(value.ToString("0.####", CultureInfo.InvariantCulture));
    }
}
