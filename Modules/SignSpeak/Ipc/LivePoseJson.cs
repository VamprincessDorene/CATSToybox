using System.Globalization;
using System.Numerics;
using System.Text;
using SignSpeak.Signs;

namespace SignSpeak.Ipc;

/// <summary>
/// Builds the JSON that <c>LivePose.SetPose</c> expects: a
/// <c>LivePoseCharacterData</c> carrying a <c>CursedPose</c>.
///
/// This is the payload that makes hand-authored letters exact. It is the same
/// structure the <c>.livepose</c> files themselves store, so rotation,
/// translation and scale all pass straight through with no conversion:
///
/// <code>
/// {"CursedPose":[
///    {"BonePoseInfoId":{"BoneName":"j_hito_a_r","Partial":0,"Slot":0},
///     "Stacks":[{"Transform":{"Position":{...},"Rotation":{...},"Scale":{...}},
///                "Propogate":3}]}
/// ]}
/// </code>
///
/// <c>CursedPose</c> specifically, rather than <c>BodyPoses</c>: body poses are
/// keyed by animation timeline id, whereas a cursed pose applies whatever the
/// character happens to be doing. Signing has to work while walking, idling or
/// emoting, so it must not be tied to one timeline.
///
/// Two details that are easy to get wrong:
/// <list type="bullet">
/// <item><c>Scale</c> is an offset, not a multiplier. LivePose's own
/// <c>Transform.Identity</c> has <c>Scale = Vector3.Zero</c>, and it applies
/// stacks with <c>temp.Scale += info.Transform.Scale</c>.</item>
/// <item><c>Propogate</c> is spelled that way in the format, and 3 is
/// Position|Rotation from its TransformComponents flags -- the same value the
/// files use.</item>
/// </list>
///
/// Kept free of any Dalamud reference so it can be tested outside the game.
/// </summary>
public static class LivePoseJson
{
    /// <summary>Position | Rotation, matching what LivePose writes into its own files.</summary>
    private const int PropagatePositionAndRotation = 3;

    public static string Build(StringBuilder buffer, ResolvedPose pose)
    {
        buffer.Clear();
        buffer.Append("{\"CursedPose\":[");

        var first = true;
        foreach (var (bone, value) in pose.Bones)
        {
            if (IsNeutral(value))
                continue;

            if (!first)
                buffer.Append(',');
            first = false;

            buffer.Append("{\"BonePoseInfoId\":{\"BoneName\":\"").Append(bone)
                  .Append("\",\"Partial\":0,\"Slot\":0},\"Stacks\":[{\"Transform\":{");

            AppendVector3(buffer, "Position", value.Translation);
            buffer.Append(',');
            AppendQuaternion(buffer, "Rotation", value.Rotation);
            buffer.Append(',');
            AppendVector3(buffer, "Scale", value.ScaleOffset);

            buffer.Append("},\"Propogate\":").Append(PropagatePositionAndRotation).Append("}]}");
        }

        buffer.Append("]}");
        return buffer.ToString();
    }

    /// <summary>An empty payload, which makes LivePose drop everything we applied.</summary>
    public const string ClearPayload = "{}";

    private static bool IsNeutral(in BoneOverride value)
        => System.MathF.Abs(System.MathF.Abs(value.Rotation.W) - 1f) < 1e-5f
           && value.Translation.LengthSquared() < 1e-12f
           && value.ScaleOffset.LengthSquared() < 1e-10f;

    private static void AppendVector3(StringBuilder buffer, string name, Vector3 v)
    {
        buffer.Append('"').Append(name).Append("\":{\"X\":");
        AppendFloat(buffer, v.X);
        buffer.Append(",\"Y\":");
        AppendFloat(buffer, v.Y);
        buffer.Append(",\"Z\":");
        AppendFloat(buffer, v.Z);
        buffer.Append('}');
    }

    private static void AppendQuaternion(StringBuilder buffer, string name, Quaternion q)
    {
        buffer.Append('"').Append(name).Append("\":{\"X\":");
        AppendFloat(buffer, q.X);
        buffer.Append(",\"Y\":");
        AppendFloat(buffer, q.Y);
        buffer.Append(",\"Z\":");
        AppendFloat(buffer, q.Z);
        buffer.Append(",\"W\":");
        AppendFloat(buffer, q.W);
        buffer.Append('}');
    }

    private static void AppendFloat(StringBuilder buffer, float value)
    {
        // Invariant culture matters: on a locale with a comma decimal separator
        // this would emit JSON with the wrong number of fields.
        // Rotations need more precision than the Customize+ path, since these
        // are raw quaternion components rather than degrees.
        buffer.Append(value.ToString("0.#######", CultureInfo.InvariantCulture));
    }
}
