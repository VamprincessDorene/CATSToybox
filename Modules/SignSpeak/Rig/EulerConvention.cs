using System;
using System.Numerics;

namespace SignSpeak.Rig;

/// <summary>
/// Conversions between quaternions and the euler triple Customize+ expects.
///
/// This has to match Customize+ exactly or every imported pose comes out
/// subtly wrong, so it is written against its actual source rather than a
/// guess. Customize+ does this (CustomizePlus/Core/Extensions/VectorExtensions.cs):
///
/// <code>
/// public static Quaternion ToQuaternion(this Vector3 rotation)
///     => Quaternion.CreateFromYawPitchRoll(
///            rotation.X * MathF.PI / 180,
///            rotation.Y * MathF.PI / 180,
///            rotation.Z * MathF.PI / 180);
/// </code>
///
/// Note the quirk: it passes <c>X</c> as *yaw* and <c>Y</c> as *pitch*. Since
/// yaw turns about Y and pitch about X, the stored <c>Rotation.X</c> actually
/// rotates about the Y axis and <c>Rotation.Y</c> about the X axis. Getting
/// this backwards is silent and produces plausible-looking garbage, which is
/// why <c>tests/</c> round-trips against a literal transcription of the call
/// above.
///
/// <c>Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll)</c> composes
/// <c>Ry(yaw) * Rx(pitch) * Rz(roll)</c>, so with the argument swap the full
/// rotation is <c>Ry(X) * Rx(Y) * Rz(Z)</c>.
/// </summary>
public static class EulerConvention
{
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float RadiansToDegrees = 180f / MathF.PI;

    /// <summary>
    /// Degrees, in Customize+ order, to a quaternion. The multiply-then-divide
    /// is deliberate: it mirrors the Customize+ expression exactly, so the two
    /// agree bit for bit rather than merely closely.
    /// </summary>
    public static Quaternion ToQuaternion(Vector3 degrees)
        => Quaternion.CreateFromYawPitchRoll(
            degrees.X * MathF.PI / 180f,
            degrees.Y * MathF.PI / 180f,
            degrees.Z * MathF.PI / 180f);

    /// <summary>
    /// A quaternion back to degrees in Customize+ order: the exact inverse of
    /// <see cref="ToQuaternion"/>.
    ///
    /// Decomposing <c>R = Ry(a) * Rx(b) * Rz(c)</c> gives
    /// <c>b = asin(-m12)</c>, <c>c = atan2(m10, m11)</c>, <c>a = atan2(m02, m22)</c>,
    /// where the result is <c>(a, b, c)</c> because of the argument swap above.
    /// </summary>
    public static Vector3 ToEuler(Quaternion q)
    {
        q = Quaternion.Normalize(q);

        var x = q.X;
        var y = q.Y;
        var z = q.Z;
        var w = q.W;

        // Rotation matrix for the column-vector convention, v' = R v.
        var m00 = 1f - 2f * (y * y + z * z);
        var m01 = 2f * (x * y - w * z);
        var m02 = 2f * (x * z + w * y);
        var m10 = 2f * (x * y + w * z);
        var m11 = 1f - 2f * (x * x + z * z);
        var m12 = 2f * (y * z - w * x);
        var m22 = 1f - 2f * (x * x + y * y);

        float a, b, c;

        // |m12| == 1 is gimbal lock: pitch is straight up or down and the other
        // two axes collapse onto one another, so only their sum or difference is
        // recoverable. Pin roll to zero and put everything into yaw.
        if (m12 >= 0.999999f)
        {
            b = -MathF.PI / 2f;
            a = MathF.Atan2(-m01, m00);
            c = 0f;
        }
        else if (m12 <= -0.999999f)
        {
            b = MathF.PI / 2f;
            a = MathF.Atan2(m01, m00);
            c = 0f;
        }
        else
        {
            // asin(-m12) is the textbook form but loses precision as m12
            // approaches +/-1, exactly where finger joints often sit. The
            // atan2 form is equivalent and well-conditioned throughout.
            b = MathF.Atan2(-m12, MathF.Sqrt(m10 * m10 + m11 * m11));
            c = MathF.Atan2(m10, m11);
            a = MathF.Atan2(m02, m22);
        }

        return new Vector3(a * RadiansToDegrees, b * RadiansToDegrees, c * RadiansToDegrees);
    }
}
