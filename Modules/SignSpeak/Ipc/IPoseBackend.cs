using System;
using SignSpeak.Signs;

namespace SignSpeak.Ipc;

/// <summary>
/// Something that can put a <see cref="ResolvedPose"/> onto a character every
/// frame.
///
/// There are two, and they are not equivalent. LivePose is the tool that
/// authored the <c>.livepose</c> files in the first place, so it applies them
/// with no translation between formats and no loss. Customize+ is the fallback
/// for people who do not have Simple Heels, and it cannot reproduce positional
/// offsets at all -- see <see cref="SupportsTranslations"/>.
/// </summary>
public interface IPoseBackend : IDisposable
{
    /// <summary>Name shown in the UI.</summary>
    string Name { get; }

    /// <summary>Null when usable, otherwise why not.</summary>
    string? UnavailableReason { get; }

    bool IsAvailable { get; }

    /// <summary>
    /// Whether this backend applies bone translations the way pose files mean
    /// them. False for Customize+, which rotates the offset by the bone's own
    /// model rotation before adding it and so cannot match.
    /// </summary>
    bool SupportsTranslations { get; }

    /// <summary>Re-probes availability. Cheap enough to call on a timer.</summary>
    bool Refresh();

    bool Apply(ushort objectIndex, ResolvedPose pose);

    /// <summary>Hands the character back to its own animation.</summary>
    void Clear(ushort objectIndex);

    void ClearAll();
}
