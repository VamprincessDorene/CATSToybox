using System;
using System.Collections.Generic;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using SignSpeak.Signs;

namespace SignSpeak.Ipc;

/// <summary>
/// Drives poses through LivePose, which ships inside Simple Heels.
///
/// This is the preferred backend, for a simple reason: LivePose is the plugin
/// that authored the <c>.livepose</c> files, so handing a pose back to it needs
/// no translation between formats. Rotation, position and scale all mean exactly
/// what the file said they meant.
///
/// The Customize+ route has to convert quaternions to that plugin's particular
/// euler convention and cannot express positional offsets at all, because it
/// rotates them by the bone's own model rotation first. LivePose adds them raw,
/// which is what the files assume.
/// </summary>
public sealed class LivePoseBridge : IPoseBackend
{
    private const string ApiVersionLabel = "LivePose.ApiVersion";
    private const string SetPoseLabel = "LivePose.SetPose";
    private const string GetPoseLabel = "LivePose.GetPose";

    /// <summary>The LivePose IPC major version this bridge was written against.</summary>
    private const int ExpectedMajorVersion = 1;

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<ushort, string, bool> _setPose;
    private readonly ICallGateSubscriber<ushort, string> _getPose;

    private readonly StringBuilder _json = new(8192);
    private readonly HashSet<ushort> _applied = [];

    public LivePoseBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>(ApiVersionLabel);

        // Registered on the far side with RegisterAction, so it is invoked as an
        // action even though the gate is declared with a bool return type.
        _setPose = pluginInterface.GetIpcSubscriber<ushort, string, bool>(SetPoseLabel);
        _getPose = pluginInterface.GetIpcSubscriber<ushort, string>(GetPoseLabel);
    }

    public string Name => "LivePose";

    public string? UnavailableReason { get; private set; } = "Not checked yet.";

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>True: this is the format the pose files were written in.</summary>
    public bool SupportsTranslations => true;

    public bool Refresh()
    {
        try
        {
            var (major, minor) = _apiVersion.InvokeFunc();

            UnavailableReason = major != ExpectedMajorVersion
                ? $"LivePose IPC is v{major}.{minor}, this plugin expects v{ExpectedMajorVersion}.x."
                : null;
        }
        catch (Exception)
        {
            // The call gate throws when the far side is not registered, which is
            // the normal state when Simple Heels is missing or still loading.
            UnavailableReason = "LivePose is unavailable. It ships with Simple Heels.";
        }

        return IsAvailable;
    }

    /// <summary>
    /// The payload from the most recent <see cref="Apply"/>. Reused verbatim
    /// when publishing to other players, so what they see is exactly what was
    /// applied locally.
    /// </summary>
    public string? LastPayload { get; private set; }

    public bool Apply(ushort objectIndex, ResolvedPose pose)
    {
        if (!IsAvailable)
            return false;

        try
        {
            var payload = LivePoseJson.Build(_json, pose);
            _setPose.InvokeAction(objectIndex, payload);
            LastPayload = payload;
            _applied.Add(objectIndex);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to send a pose to LivePose.");
            UnavailableReason = "Lost contact with LivePose.";
            return false;
        }
    }

    public void Clear(ushort objectIndex)
    {
        if (!_applied.Remove(objectIndex))
            return;

        try
        {
            // An empty payload leaves LivePose with nothing to apply after it
            // resets the pose, which is how it releases the character.
            _setPose.InvokeAction(objectIndex, LivePoseJson.ClearPayload);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to clear a LivePose pose.");
        }
    }

    public void ClearAll()
    {
        foreach (var index in new List<ushort>(_applied))
            Clear(index);
    }

    /// <summary>
    /// Puts a previously captured pose back on a character. Used to return
    /// whatever the user had applied by hand before a message started, since
    /// SetPose replaces the whole pose rather than layering onto it.
    /// </summary>
    public void Restore(ushort objectIndex, string poseJson)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(poseJson))
            return;

        try
        {
            _setPose.InvokeAction(objectIndex, poseJson);
            LastPayload = poseJson;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to restore the previous LivePose pose.");
        }
    }

    /// <summary>
    /// Reads back whatever LivePose currently has on a character. Not used by
    /// playback; handy for diagnosing a pose that does not look right.
    /// </summary>
    public string? ReadCurrent(ushort objectIndex)
    {
        try
        {
            return _getPose.InvokeFunc(objectIndex);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => ClearAll();
}
