using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace SignSpeak.Ipc;

/// <summary>
/// Publishes the current pose to other players, using the same route LivePose
/// itself uses.
///
/// LivePose does not have its own network channel. It piggybacks on Simple
/// Heels' tag system (LivePose/IPC/HeelsService.cs): it gzips the pose JSON,
/// base64s it, and stores it against the local player under the tag
/// <c>LivePose_v2</c>. Simple Heels then includes that tag in the character data
/// it hands to whichever sync plugin is running (Mare, PSync, Snowcloak, ...),
/// and on the far side LivePose picks it up from
/// <c>SimpleHeels.TagChanged</c>, decompresses it and applies it.
///
/// So publishing here is enough to make signing visible to synced players, with
/// no cooperation needed from either plugin. The compression has to match
/// theirs exactly, hence gzip and not something newer.
///
/// <para><b>The rate ceiling.</b> This cannot be streamed. Simple Heels
/// debounces its outbound broadcast by 250ms and cancels any pending one
/// (ApiProvider.OnChanged), so setting the tag more often than that means the
/// broadcast never fires at all. That is why SignSpeak syncs once per settled
/// letter rather than once per frame -- see <see cref="MinimumInterval"/>.</para>
/// </summary>
public sealed class HeelsTagBridge : IDisposable
{
    /// <summary>The tag LivePose reads on the receiving end.</summary>
    public const string TagName = TagCompression.TagName;

    private const string ApiVersionLabel = "SimpleHeels.ApiVersion";
    private const string SetTagLabel = "SimpleHeels.SetTag";
    private const string RemoveTagLabel = "SimpleHeels.RemoveTag";

    /// <summary>Shortest gap between tag updates. See TagCompression.MinimumInterval.</summary>
    public static TimeSpan MinimumInterval => TagCompression.MinimumInterval;

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<int, string, string, object?> _setTag;
    private readonly ICallGateSubscriber<int, string, object?> _removeTag;

    private DateTime _lastPublished = DateTime.MinValue;
    private string? _lastPayload;
    private bool _tagSet;

    public HeelsTagBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>(ApiVersionLabel);
        _setTag = pluginInterface.GetIpcSubscriber<int, string, string, object?>(SetTagLabel);
        _removeTag = pluginInterface.GetIpcSubscriber<int, string, object?>(RemoveTagLabel);
    }

    public string? UnavailableReason { get; private set; } = "Not checked yet.";

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>How long ago the last update actually went out, for the UI.</summary>
    public TimeSpan SinceLastPublish => DateTime.UtcNow - _lastPublished;

    public bool Refresh()
    {
        try
        {
            var (major, minor) = _apiVersion.InvokeFunc();
            UnavailableReason = major <= 0
                ? $"Simple Heels reported an unexpected API version ({major}.{minor})."
                : null;
        }
        catch (Exception)
        {
            UnavailableReason = "Simple Heels is not available, so poses cannot be synced.";
        }

        return IsAvailable;
    }

    /// <summary>
    /// Publishes a pose payload if enough time has passed since the last one.
    /// </summary>
    /// <param name="poseJson">
    /// The same <c>LivePoseCharacterData</c> JSON handed to
    /// <c>LivePose.SetPose</c>, so remote viewers see exactly what you see.
    /// </param>
    /// <param name="force">Bypass the interval, for the final pose of a message.</param>
    /// <returns>True if the tag was set.</returns>
    public bool Publish(string poseJson, bool force = false)
    {
        if (!IsAvailable)
            return false;

        if (!force && DateTime.UtcNow - _lastPublished < MinimumInterval)
            return false;

        // Simple Heels ignores a tag set to the value it already holds, so
        // skipping it here just avoids the compression work.
        if (string.Equals(_lastPayload, poseJson, StringComparison.Ordinal))
            return false;

        try
        {
            _setTag.InvokeAction(0, TagName, Compress(poseJson));
            _lastPayload = poseJson;
            _lastPublished = DateTime.UtcNow;
            _tagSet = true;
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to publish a pose tag to Simple Heels.");
            UnavailableReason = "Lost contact with Simple Heels.";
            return false;
        }
    }

    /// <summary>
    /// Restores whatever the character had before we started, so signing does
    /// not permanently wipe a pose the user had applied by hand.
    /// </summary>
    public void Restore(string? baselinePoseJson)
    {
        if (!IsAvailable || !_tagSet)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(baselinePoseJson))
            {
                _removeTag.InvokeAction(0, TagName);
            }
            else
            {
                _setTag.InvokeAction(0, TagName, Compress(baselinePoseJson));
            }

            _lastPayload = baselinePoseJson;
            _lastPublished = DateTime.UtcNow;
            _tagSet = false;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to restore the pose tag.");
        }
    }

    private static string Compress(string text) => TagCompression.Compress(text);

    public void Dispose() => Restore(null);
}
