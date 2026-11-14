using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SignSpeak.Ipc;
using SignSpeak.Playback;
using SignSpeak.Rig;
using SignSpeak.Signs;
using SignSpeak.UI;

namespace SignSpeak;

public sealed class Plugin : IDisposable
{
    private const string CommandName = "/signspeak";
    private const string ShortCommandName = "/sign";
    public const string TabName = "SignSpeak";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private readonly Configuration _config;

    /// <summary>
    /// Both backends are constructed up front, because either can appear or
    /// disappear while the game is running. LivePose is preferred: it is the
    /// plugin that authored the pose files, so it applies them with no
    /// conversion and no loss.
    /// </summary>
    private readonly LivePoseBridge _livePose;

    private readonly CustomizePlusBridge _customizePlus;

    /// <summary>Publishes poses to synced players through Simple Heels' tag system.</summary>
    private readonly HeelsTagBridge _heelsTags;

    private readonly SignLibrary _library = new();
    private readonly SignPlayer _player;
    private readonly MainWindow _mainWindow;

    // Pose buffers, reused so the per-frame path does not allocate.
    private readonly ResolvedPose _from = new();
    private readonly ResolvedPose _target = new();
    private readonly ResolvedPose _blended = new();
    private readonly ResolvedPose _lastEmitted = new();
    private readonly ResolvedPose _final = new();
    private readonly Dictionary<string, Vector3> _euler = new(32);

    private int _lastEpoch = -1;
    private float _pushAccumulator;
    private float _availabilityAccumulator;
    private ushort? _appliedTo;

    /// <summary>Last letter we managed to publish to other players.</summary>
    private int _lastSyncedEpoch = -1;

    /// <summary>
    /// Whatever LivePose had on the character before we started signing.
    /// Captured so a pose the user applied by hand survives a message, both
    /// locally and for anyone synced to them.
    /// </summary>
    private string? _baselinePose;

    private bool _baselineCaptured;

    /// <summary>
    /// Reference capture for absolute <c>.pose</c> imports. Held in memory
    /// rather than in the config because it is bulky and only needed while
    /// importing.
    /// </summary>
    private PoseImportResult? _referencePose;

    public Plugin()
    {
        _config = CATSToybox.ModuleConfig.Load<Configuration>("SignSpeak", "SignSpeak");

        var migrated = _config.Migrate();

        _config.LoadImportsInto(_library);

        _player = new SignPlayer(_library);

        _livePose = new LivePoseBridge(PluginInterface, Log);
        _customizePlus = new CustomizePlusBridge(PluginInterface, Log);
        _heelsTags = new HeelsTagBridge(PluginInterface, Log);
        _livePose.Refresh();
        _customizePlus.Refresh();
        _heelsTags.Refresh();
        SyncLibrary();

        if (migrated)
        {
            CATSToybox.ModuleConfig.Save("SignSpeak", _config);
            Log.Information("Upgraded the SignSpeak configuration.");
        }

        _mainWindow = new MainWindow(this, _config, _library, _player);
        Framework.Update += OnFrameworkUpdate;

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open SignSpeak. \"/signspeak hello\" signs a message straight away.",
        });

        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Shorthand for /signspeak.",
        });
    }

    public bool HasReferencePose => _referencePose is not null;

    public LivePoseBridge LivePose => _livePose;

    public CustomizePlusBridge CustomizePlus => _customizePlus;

    public HeelsTagBridge HeelsTags => _heelsTags;

    /// <summary>
    /// The backend currently in use. LivePose wins when available, because it
    /// applies pose files losslessly. Customize+ is the fallback for anyone
    /// without Simple Heels.
    /// </summary>
    public IPoseBackend Backend
    {
        get
        {
            if (_config.PreferLivePose && _livePose.IsAvailable)
                return _livePose;

            if (_customizePlus.IsAvailable)
                return _customizePlus;

            return _livePose.IsAvailable ? _livePose : _customizePlus;
        }
    }

    /// <summary>Pushes the current settings into the library the player reads from.</summary>
    public void SyncLibrary()
    {
        _library.Calibration = _config.GetCalibration();
        _library.Posture = _config.Posture;
        _library.RightHanded = _config.RightHanded;

        // Positional offsets only mean the right thing on a backend that adds
        // them raw. On Customize+ they come out wrong, so they are withheld
        // there regardless of the user's preference.
        _library.UseImportedTranslations =
            _config.UseImportedTranslations && Backend.SupportsTranslations;
    }

    public void SaveConfig()
    {
        SyncLibrary();
        _config.StoreImportsFrom(_library);
        CATSToybox.ModuleConfig.Save("SignSpeak", _config);
    }

    /// <summary>Translates a message and starts signing it.</summary>
    public void Sign(string text)
    {
        var steps = SignBook.Translate(text);
        if (steps.Count == 0)
        {
            ChatGui.PrintError("[SignSpeak] Nothing in that message can be signed yet.");
            return;
        }

        var backend = Backend;
        if (!backend.IsAvailable && !backend.Refresh())
        {
            ChatGui.PrintError($"[SignSpeak] {backend.UnavailableReason}");
            return;
        }

        // Remember any pose the user already had applied, so a message does not
        // permanently wipe it. Only on the way in: a second message queued
        // mid-flight must not overwrite the baseline with our own letter.
        if (_player.State == PlayerState.Idle)
            CaptureBaseline();

        SyncLibrary();
        _player.Speed = _config.Speed;
        _player.Play(steps);
    }

    private void CaptureBaseline()
    {
        _baselinePose = null;
        _baselineCaptured = false;

        if (!_livePose.IsAvailable)
            return;

        var current = _livePose.ReadCurrent(0);
        _baselinePose = string.IsNullOrWhiteSpace(current) ? null : current;
        _baselineCaptured = true;
    }

    public void StopSigning() => _player.Stop();

    /// <summary>Draws the SignSpeak tab inside the CATS Toybox window.</summary>
    public void DrawTab() => _mainWindow.Draw();

    private void OnCommand(string command, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            CATSToybox.Toybox.Toggle(TabName);
            return;
        }

        if (arguments.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            StopSigning();
            return;
        }

        Sign(arguments);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var delta = (float)framework.UpdateDelta.TotalSeconds;

        // Either backend can be enabled or disabled at any time, so re-probe
        // occasionally rather than sitting on a stale answer.
        _availabilityAccumulator += delta;
        if (_availabilityAccumulator >= 2f)
        {
            _availabilityAccumulator = 0f;
            _livePose.Refresh();
            _customizePlus.Refresh();
            _heelsTags.Refresh();
            SyncLibrary();
        }

        _player.Speed = _config.Speed;
        _player.Advance(delta);

        if (_player.ConsumeRelease())
        {
            ReleaseProfile();
            return;
        }

        if (_player.State == PlayerState.Idle)
            return;

        // If the character goes away mid-message (zoning, logout) drop everything.
        var local = ObjectTable.LocalPlayer;
        if (local is null)
        {
            _player.Reset();
            ReleaseProfile();
            return;
        }

        _pushAccumulator += delta;
        var interval = 1f / Math.Clamp(_config.UpdateHz, 5, 60);
        if (_pushAccumulator < interval)
            return;

        _pushAccumulator = 0f;
        PushPose(local.ObjectIndex);
    }

    private void PushPose(ushort objectIndex)
    {
        // A new transition means the player has moved on to a fresh target, so
        // snapshot whatever is on screen right now and blend from there. Doing
        // it this way is what makes an interrupted message continue smoothly
        // rather than jumping back to the last completed letter.
        if (_player.TransitionEpoch != _lastEpoch)
        {
            _lastEpoch = _player.TransitionEpoch;
            _from.CopyFrom(_lastEmitted);
        }

        _library.Resolve(_player.TargetCharacter, _player.TargetKeyIndex, _target);
        ResolvedPose.Blend(_from, _target, _player.TransitionProgress, _blended);
        _lastEmitted.CopyFrom(_blended);

        _blended.ScaleFromRest(_player.Engage, _final);
        Emit(objectIndex, _final);
        PublishToOtherPlayers();
    }

    /// <summary>
    /// Pushes the pose out to synced players, once per settled letter.
    ///
    /// This is deliberately not per frame. Simple Heels debounces its outbound
    /// broadcast by 250ms and cancels any pending one, so updating faster than
    /// that would mean nothing is ever sent. Publishing when a letter is
    /// reached and held gives remote viewers each shape, crisply, at about the
    /// fastest rate the transport allows.
    /// </summary>
    private void PublishToOtherPlayers()
    {
        if (!_config.SyncToOtherPlayers || !_heelsTags.IsAvailable)
            return;

        // Only LivePose payloads are meaningful to a remote LivePose.
        if (Backend != _livePose || _livePose.LastPayload is not { } payload)
            return;

        // Wait until the hand has actually arrived at the letter, so viewers get
        // the shape rather than a halfway blend.
        if (_player.TransitionProgress < 0.999f)
            return;

        if (_player.TransitionEpoch == _lastSyncedEpoch)
            return;

        if (_heelsTags.Publish(payload))
            _lastSyncedEpoch = _player.TransitionEpoch;
    }

    private void Emit(ushort objectIndex, ResolvedPose pose)
    {
        if (Backend.Apply(objectIndex, pose))
            _appliedTo = objectIndex;
    }

    /// <summary>Holds one character's shape, for the Alphabet and Import tabs.</summary>
    public void PreviewCharacter(char character)
    {
        var local = ObjectTable.LocalPlayer;
        if (local is null || !Backend.IsAvailable)
            return;

        SyncLibrary();
        _library.Resolve(character, 0, _target);

        // Previews sit outside playback, so keep the blender in step or the
        // next message would blend out of a stale snapshot.
        _lastEmitted.CopyFrom(_target);
        _from.CopyFrom(_target);
        Emit(local.ObjectIndex, _target);
    }

    /// <summary>Holds an arbitrary procedural shape, used by the calibration sliders.</summary>
    public void PreviewPose(in HandPose pose)
    {
        var local = ObjectTable.LocalPlayer;
        if (local is null || !Backend.IsAvailable)
            return;

        SyncLibrary();
        HandRig.Build(pose, _library.Calibration, _library.Posture, _library.RightHanded, _euler);

        _target.Clear();
        foreach (var (bone, euler) in _euler)
            _target.Set(bone, new BoneOverride(Vector3.Zero, EulerConvention.ToQuaternion(euler)));

        _lastEmitted.CopyFrom(_target);
        _from.CopyFrom(_target);
        Emit(local.ObjectIndex, _target);
    }

    /// <summary>Hands the character back to its own animation.</summary>
    public void ReleaseProfile()
    {
        _lastEmitted.Clear();
        _from.Clear();
        _lastSyncedEpoch = -1;

        if (_appliedTo is not { } index)
        {
            _baselineCaptured = false;
            return;
        }

        // Clear both, since the active backend may have changed since we applied.
        _livePose.Clear(index);
        _customizePlus.Clear(index);
        _appliedTo = null;

        // Hand the character back whatever it had before the message, and tell
        // synced players the same, so a hand-applied pose survives signing.
        if (_baselineCaptured)
        {
            if (_baselinePose is { } baseline)
                _livePose.Restore(index, baseline);

            _heelsTags.Restore(_baselinePose);
            _baselineCaptured = false;
        }
    }

    // ---- Import ------------------------------------------------------------

    /// <summary>
    /// Loads a reference capture for absolute <c>.pose</c> imports. This should
    /// be your character with hands relaxed, saved from the same plugin you
    /// saved the letters from.
    /// </summary>
    public string SetReferencePose(string path)
    {
        try
        {
            var parsed = PoseFileReader.Read(File.ReadAllText(path));
            if (!parsed.Ok)
                return parsed.Error!;

            if (parsed.Kind != PoseFileKind.AbsolutePose)
                return "That is a .livepose file. Reference captures are only needed for .pose files.";

            _referencePose = parsed;
            _config.ReferencePoseFile = path;
            SaveConfig();
            return $"Reference set from {Path.GetFileName(path)} ({parsed.Absolute.Count} bones).";
        }
        catch (Exception ex)
        {
            return $"Could not read that file: {ex.Message}";
        }
    }

    /// <summary>Imports one pose file and maps it to one character.</summary>
    public string ImportFile(string path, char character)
    {
        if (!SignBook.CanSign(character))
            return $"'{character}' is not a character SignSpeak can sign.";

        try
        {
            var parsed = PoseFileReader.Read(File.ReadAllText(path));
            if (!parsed.Ok)
                return parsed.Error!;

            var offsets = new Dictionary<string, BoneOverride>();
            var note = string.Empty;

            if (parsed.Kind == PoseFileKind.LivePose)
            {
                foreach (var (bone, value) in parsed.Offsets)
                    offsets[bone] = value;
            }
            else
            {
                if (_referencePose is null)
                {
                    return ".pose files store absolute rotations, so they need a reference capture first. "
                           + "Load one under \"Reference pose\", or use .livepose files, which need none.";
                }

                var wanted = new List<string>(parsed.Absolute.Keys);
                var skipped = PoseFileReader.ToOffsets(parsed, _referencePose, wanted, offsets);

                if (skipped.Count > 0)
                    note = $" Skipped {skipped.Count} bone(s) with no known parent.";
            }

            if (offsets.Count == 0)
                return "That file had no bones SignSpeak could use.";

            var sign = _library.Assign(character, Path.GetFileName(path), offsets);

            if (sign.Bones.Count == 0)
            {
                _library.Remove(character);
                SaveConfig();
                return "That file poses no hand or arm bones, so there was nothing to map.";
            }

            SaveConfig();

            var side = sign.RightHand ? "right" : "left";
            var arm = sign.CarriesArm ? ", including the arm" : ", fingers only";
            return $"Mapped {character} from {Path.GetFileName(path)}: "
                   + $"{sign.Bones.Count} {side}-hand bones{arm}.{note}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Import failed.");
            return $"Import failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Imports every pose file in a folder, matching each to a character by
    /// filename. This is the bulk path: name files "Letter A.livepose", "B.pose"
    /// or similar and the whole alphabet lands in one go.
    /// </summary>
    public string ImportFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return "That folder does not exist.";

        var files = new List<string>();
        files.AddRange(Directory.GetFiles(folder, "*.livepose"));
        files.AddRange(Directory.GetFiles(folder, "*.pose"));

        if (files.Count == 0)
            return "No .livepose or .pose files in that folder.";

        files.Sort(StringComparer.OrdinalIgnoreCase);

        var imported = 0;
        var unmatched = new List<string>();
        var failed = new List<string>();

        foreach (var file in files)
        {
            var character = FilenameLetter.Detect(Path.GetFileNameWithoutExtension(file));
            if (character is not { } target)
            {
                unmatched.Add(Path.GetFileName(file));
                continue;
            }

            var message = ImportFile(file, target);
            if (message.StartsWith("Mapped ", StringComparison.Ordinal))
                imported++;
            else
                failed.Add($"{Path.GetFileName(file)}: {message}");
        }

        _config.LastImportFolder = folder;
        SaveConfig();

        var report = $"Imported {imported} of {files.Count} file(s).";
        if (unmatched.Count > 0)
            report += $" Could not tell which letter these are: {string.Join(", ", unmatched)}.";
        if (failed.Count > 0)
            report += " " + string.Join(" ", failed);

        return report;
    }

    public void ClearImport(char character)
    {
        if (_library.Remove(character))
            SaveConfig();
    }

    public void ClearAllImports()
    {
        _library.Imported.Clear();
        SaveConfig();
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);

        _player.Reset();
        _heelsTags.Dispose();
        _livePose.Dispose();
        _customizePlus.Dispose();
    }
}
