using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using SignSpeak.Signs;

namespace SignSpeak.Ipc;

/// <summary>
/// Talks to Customize+.
///
/// This is the whole reason the plugin works outside of GPose. Customize+ bone
/// rotations are applied as a delta on top of whatever animation the character
/// is currently playing, every frame, in the open world. So the character keeps
/// walking, idling and emoting normally while we drive only the finger bones.
///
/// Temporary profiles live in memory only, are never written to the user's
/// Customize+ config, and vanish when the plugin unloads.
/// </summary>
public sealed class CustomizePlusBridge : IPoseBackend
{
    // Customize+ registers its IPC through EzIPC with a "CustomizePlus" prefix.
    private const string ApiVersionLabel = "CustomizePlus.General.GetApiVersion";
    private const string SetTemporaryProfileLabel = "CustomizePlus.Profile.SetTemporaryProfileOnCharacter";
    private const string DeleteTemporaryProfileLabel = "CustomizePlus.Profile.DeleteTemporaryProfileOnCharacter";

    /// <summary>The Customize+ IPC major version this bridge was written against.</summary>
    private const int ExpectedMajorVersion = 6;

    private readonly IPluginLog _log;
    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<ushort, string, (int, Guid?)> _setTemporaryProfile;
    private readonly ICallGateSubscriber<ushort, int> _deleteTemporaryProfile;

    private readonly StringBuilder _json = new(4096);

    /// <summary>Object indices we currently hold a temporary profile on, so we can always clean up.</summary>
    private readonly HashSet<ushort> _applied = [];

    public CustomizePlusBridge(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        _log = log;
        _apiVersion = pluginInterface.GetIpcSubscriber<(int, int)>(ApiVersionLabel);
        _setTemporaryProfile = pluginInterface.GetIpcSubscriber<ushort, string, (int, Guid?)>(SetTemporaryProfileLabel);
        _deleteTemporaryProfile = pluginInterface.GetIpcSubscriber<ushort, int>(DeleteTemporaryProfileLabel);
    }

    public string Name => "Customize+";

    /// <summary>
    /// False. Customize+ rotates a bone translation by the bone.s own model
    /// rotation before adding it, while pose files store it as a raw model-space
    /// offset, so positional offsets cannot be reproduced through this backend.
    /// </summary>
    public bool SupportsTranslations => false;

    /// <summary>Human-readable reason the bridge is unusable, or null if all is well.</summary>
    public string? UnavailableReason { get; private set; } = "Not checked yet.";

    public bool IsAvailable => UnavailableReason is null;

    /// <summary>
    /// Re-checks whether Customize+ is loaded and speaks a version we understand.
    /// Cheap enough to call on a timer; the UI does so every couple of seconds.
    /// </summary>
    public bool Refresh()
    {
        try
        {
            var (major, minor) = _apiVersion.InvokeFunc();

            UnavailableReason = major != ExpectedMajorVersion
                ? $"Customize+ IPC is v{major}.{minor}, this plugin expects v{ExpectedMajorVersion}.x."
                : null;
        }
        catch (Exception)
        {
            // The call gate throws when the far side is not registered, which is
            // the normal state when Customize+ is disabled or still loading.
            UnavailableReason = "Customize+ is not installed or not enabled.";
        }

        return IsAvailable;
    }

    /// <summary>
    /// Pushes a set of bone rotations onto a character. Replacing an existing
    /// temporary profile is how we animate: call this each tick with the newly
    /// interpolated pose.
    /// </summary>
    /// <param name="objectIndex">Game object table index of the character.</param>
    /// <param name="pose">The resolved bone offsets to apply.</param>
    public bool Apply(ushort objectIndex, ResolvedPose pose)
    {
        if (!IsAvailable)
            return false;

        try
        {
            var payload = ProfileJson.Build(_json, pose);
            var (code, _) = _setTemporaryProfile.InvokeFunc(objectIndex, payload);

            if (code != 0)
            {
                _log.Warning($"Customize+ rejected the temporary profile (error {code}).");
                return false;
            }

            _applied.Add(objectIndex);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Failed to apply a Customize+ temporary profile.");
            UnavailableReason = "Lost contact with Customize+.";
            return false;
        }
    }

    /// <summary>Removes our temporary profile, handing the character back to its normal animation.</summary>
    public void Clear(ushort objectIndex)
    {
        if (!_applied.Remove(objectIndex))
            return;

        try
        {
            _deleteTemporaryProfile.InvokeFunc(objectIndex);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to remove a Customize+ temporary profile.");
        }
    }

    public void ClearAll()
    {
        foreach (var index in new List<ushort>(_applied))
            Clear(index);
    }

    public void Dispose() => ClearAll();
}
