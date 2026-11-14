using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SignSpeak.Ipc;

/// <summary>
/// Gzip then base64, matching LivePose's <c>HeelsService.Compress</c> byte for
/// byte.
///
/// This is not a free choice of algorithm. The receiving end is somebody else's
/// LivePose, which decompresses with <c>GZipStream</c> over
/// <c>Convert.FromBase64String</c>, so anything else would arrive as garbage.
///
/// Kept apart from <see cref="HeelsTagBridge"/>, which needs Dalamud, so the
/// round-trip can be tested outside the game.
/// </summary>
public static class TagCompression
{
    /// <summary>
    /// The Simple Heels tag LivePose reads on the receiving end
    /// (LivePose/IPC/HeelsService.cs). Must match exactly or nothing arrives.
    /// </summary>
    public const string TagName = "LivePose_v2";

    /// <summary>
    /// Shortest useful gap between tag updates.
    ///
    /// Simple Heels debounces its outbound broadcast by 250ms and cancels any
    /// pending one on each change (ApiProvider.OnChanged), so updating faster
    /// than that means the broadcast never fires at all. This is a hard
    /// transport limit, not a tuning preference: it is why signing syncs once
    /// per settled letter rather than once per frame.
    /// </summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(300);

    public static string Compress(string text)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
        using var output = new MemoryStream();

        using (var gzip = new GZipStream(output, CompressionMode.Compress))
            input.CopyTo(gzip);

        return Convert.ToBase64String(output.ToArray());
    }

    public static string Decompress(string? compressed)
    {
        if (string.IsNullOrEmpty(compressed))
            return string.Empty;

        using var input = new MemoryStream(Convert.FromBase64String(compressed));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        gzip.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
