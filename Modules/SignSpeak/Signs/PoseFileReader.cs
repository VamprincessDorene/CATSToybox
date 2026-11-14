using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace SignSpeak.Signs;

public enum PoseFileKind
{
    /// <summary>
    /// Simple Heels <c>.livepose</c>. Stores bone *offsets* applied on top of
    /// the running animation, which is the same thing Customize+ consumes, so
    /// these import exactly with no reference pose and no guesswork.
    /// </summary>
    LivePose,

    /// <summary>
    /// Brio or Anamnesis <c>.pose</c>. Stores *absolute* model-space
    /// transforms, so a reference capture is needed to recover an offset.
    /// </summary>
    AbsolutePose,
}

public sealed class PoseImportResult
{
    public PoseFileKind Kind { get; init; }

    /// <summary>Bone offsets, ready to use. Populated for <see cref="PoseFileKind.LivePose"/>.</summary>
    public Dictionary<string, BoneOverride> Offsets { get; } = new();

    /// <summary>Absolute model-space rotations. Populated for <see cref="PoseFileKind.AbsolutePose"/>.</summary>
    public Dictionary<string, Quaternion> Absolute { get; } = new();

    public string? Error { get; init; }

    public bool Ok => Error is null;

    public bool HasRightHand { get; set; }
    public bool HasLeftHand { get; set; }
}

/// <summary>
/// Reads the two pose formats people actually have on disk.
///
/// Uses System.Text.Json rather than the Newtonsoft that Dalamud ships, purely
/// so this file carries no plugin dependency and can be tested against the
/// real files outside the game.
/// </summary>
public static class PoseFileReader
{
    public static PoseImportResult Read(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            var root = doc.RootElement;

            if (root.TryGetProperty("Data", out var data) && data.ValueKind == JsonValueKind.Array)
                return ReadLivePose(data);

            if (root.TryGetProperty("Bones", out var bones) && bones.ValueKind == JsonValueKind.Object)
                return ReadAbsolutePose(bones);

            return new PoseImportResult
            {
                Error = "Not a recognised pose file: expected a \"Data\" array (.livepose) "
                        + "or a \"Bones\" object (.pose).",
            };
        }
        catch (JsonException ex)
        {
            return new PoseImportResult { Error = $"Could not parse the file as JSON: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new PoseImportResult { Error = $"Could not read the file: {ex.Message}" };
        }
    }

    private static PoseImportResult ReadLivePose(JsonElement data)
    {
        var result = new PoseImportResult { Kind = PoseFileKind.LivePose };

        foreach (var entry in data.EnumerateArray())
        {
            if (!entry.TryGetProperty("BonePoseInfoId", out var id))
                continue;

            if (!id.TryGetProperty("BoneName", out var nameElement))
                continue;

            var bone = nameElement.GetString();
            if (string.IsNullOrEmpty(bone))
                continue;

            // Partial 0 is the base character skeleton. Non-zero partials are
            // gear and hair sub-skeletons, which have nothing we want.
            if (id.TryGetProperty("Partial", out var partial)
                && partial.ValueKind == JsonValueKind.Number
                && partial.GetInt32() != 0)
                continue;

            if (!entry.TryGetProperty("Stacks", out var stacks) || stacks.ValueKind != JsonValueKind.Array)
                continue;

            // Stacks compose. LivePose walks them in order applying
            // `temp.Position += p`, `temp.Rotation *= q`, `temp.Scale += s`
            // (SkeletonService.cs), so translation and scale sum while rotation
            // right-multiplies in file order.
            var rotation = Quaternion.Identity;
            var translation = Vector3.Zero;
            var scaleOffset = Vector3.Zero;
            var any = false;

            foreach (var stack in stacks.EnumerateArray())
            {
                if (!stack.TryGetProperty("Transform", out var transform))
                    continue;

                if (TryReadVector(transform, "Position", out var position))
                    translation += position;

                if (TryReadQuaternion(transform, "Rotation", out var q))
                    rotation = Quaternion.Normalize(rotation * q);

                if (TryReadVector(transform, "Scale", out var scale))
                    scaleOffset += scale;

                any = true;
            }

            if (!any)
                continue;

            result.Offsets[bone] = new BoneOverride(translation, rotation, scaleOffset);
            NoteSide(result, bone);
        }

        if (result.Offsets.Count == 0)
            return new PoseImportResult { Error = "The .livepose file contains no usable bones." };

        return result;
    }

    private static PoseImportResult ReadAbsolutePose(JsonElement bones)
    {
        var result = new PoseImportResult { Kind = PoseFileKind.AbsolutePose };

        foreach (var property in bones.EnumerateObject())
        {
            var bone = property.Name;
            if (!property.Value.TryGetProperty("Rotation", out var rotation))
                continue;

            // Brio and Anamnesis both write these as "x, y, z, w" strings.
            if (rotation.ValueKind == JsonValueKind.String)
            {
                if (TryParseCsvQuaternion(rotation.GetString(), out var q))
                {
                    result.Absolute[bone] = q;
                    NoteSide(result, bone);
                }

                continue;
            }

            if (TryReadQuaternion(property.Value, "Rotation", out var objectQ))
            {
                result.Absolute[bone] = objectQ;
                NoteSide(result, bone);
            }
        }

        if (result.Absolute.Count == 0)
            return new PoseImportResult { Error = "The .pose file contains no usable bone rotations." };

        return result;
    }

    private static void NoteSide(PoseImportResult result, string bone)
    {
        if (bone.EndsWith("_r", StringComparison.Ordinal))
            result.HasRightHand = true;
        else if (bone.EndsWith("_l", StringComparison.Ordinal))
            result.HasLeftHand = true;
    }

    private static bool TryReadVector(JsonElement parent, string name, out Vector3 value)
    {
        value = Vector3.Zero;

        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return false;

        value = new Vector3(
            ReadFloat(element, "X"),
            ReadFloat(element, "Y"),
            ReadFloat(element, "Z"));

        return true;
    }

    private static bool TryReadQuaternion(JsonElement parent, string name, out Quaternion value)
    {
        value = Quaternion.Identity;

        if (!parent.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.Object)
            return false;

        var q = new Quaternion(
            ReadFloat(element, "X"),
            ReadFloat(element, "Y"),
            ReadFloat(element, "Z"),
            ReadFloat(element, "W"));

        if (q.LengthSquared() < 1e-9f)
            return false;

        value = Quaternion.Normalize(q);
        return true;
    }

    private static float ReadFloat(JsonElement element, string name)
        => element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetSingle()
            : 0f;

    private static bool TryParseCsvQuaternion(string? text, out Quaternion value)
    {
        value = Quaternion.Identity;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text.Split(',');
        if (parts.Length != 4)
            return false;

        var numbers = new float[4];
        for (var i = 0; i < 4; i++)
        {
            if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        var q = new Quaternion(numbers[0], numbers[1], numbers[2], numbers[3]);
        if (q.LengthSquared() < 1e-9f)
            return false;

        value = Quaternion.Normalize(q);
        return true;
    }

    /// <summary>
    /// Parent of each bone SignSpeak touches, for turning absolute model-space
    /// rotations into local ones.
    ///
    /// The finger and arm chains here are certain. <c>n_hhiji</c> (the elbow
    /// twist helper) is deliberately absent: its parent is not something this
    /// can confirm offline, and guessing would quietly skew the forearm. It is
    /// therefore skipped on <c>.pose</c> import, and only <c>.livepose</c>,
    /// which needs no hierarchy at all, carries it.
    /// </summary>
    private static readonly Dictionary<string, string> Parents = BuildParents();

    private static Dictionary<string, string> BuildParents()
    {
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var side in new[] { "_l", "_r" })
        {
            parents["j_sako" + side] = "j_sebo_c";
            parents["j_ude_a" + side] = "j_sako" + side;
            parents["j_ude_b" + side] = "j_ude_a" + side;
            parents["j_te" + side] = "j_ude_b" + side;
            parents["n_hte" + side] = "j_te" + side;

            foreach (var stem in new[] { "oya", "hito", "naka", "kusu", "ko" })
            {
                parents["j_" + stem + "_a" + side] = "j_te" + side;
                parents["j_" + stem + "_b" + side] = "j_" + stem + "_a" + side;

                // Extended skeletons (IVCS and friends) add a third phalanx per
                // finger, named iv_<finger>_c_<side>. Real poses on this machine
                // use them, and the parent follows unambiguously from the chain.
                parents["iv_" + stem + "_c" + side] = "j_" + stem + "_b" + side;
            }
        }

        return parents;
    }

    /// <summary>
    /// Converts an absolute <c>.pose</c> into bone offsets by differencing it
    /// against a reference capture of the same character.
    ///
    /// For a bone B with parent P, the local rotation is
    /// <c>inverse(model_P) * model_B</c>, and the offset Customize+ wants is
    /// <c>inverse(local_reference) * local_pose</c>. Both files supply the
    /// parent, so the only external knowledge needed is which bone the parent
    /// is, hence <see cref="Parents"/>.
    /// </summary>
    /// <returns>Bones that could not be converted, for reporting.</returns>
    public static List<string> ToOffsets(
        PoseImportResult pose,
        PoseImportResult reference,
        IEnumerable<string> wanted,
        Dictionary<string, BoneOverride> into)
    {
        into.Clear();
        var skipped = new List<string>();

        foreach (var bone in wanted)
        {
            if (!Parents.TryGetValue(bone, out var parent))
            {
                skipped.Add(bone);
                continue;
            }

            if (!pose.Absolute.TryGetValue(bone, out var poseBone)
                || !pose.Absolute.TryGetValue(parent, out var poseParent)
                || !reference.Absolute.TryGetValue(bone, out var refBone)
                || !reference.Absolute.TryGetValue(parent, out var refParent))
            {
                skipped.Add(bone);
                continue;
            }

            var localPose = Quaternion.Inverse(poseParent) * poseBone;
            var localReference = Quaternion.Inverse(refParent) * refBone;
            var offset = Quaternion.Normalize(Quaternion.Inverse(localReference) * localPose);

            into[bone] = new BoneOverride(Vector3.Zero, offset);
        }

        return skipped;
    }
}
