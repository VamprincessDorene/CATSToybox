using System;
using System.IO;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using SignSpeak.Ipc;
using SignSpeak.Playback;
using SignSpeak.Rig;
using SignSpeak.Signs;

namespace SignSpeak.UI;

public sealed class MainWindow : Window
{
    private const string PoseFilters = "Pose files{.livepose,.pose}";

    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.40f, 1f);
    private static readonly Vector4 Warn = new(0.90f, 0.75f, 0.35f, 1f);
    private static readonly Vector4 Dim = new(0.65f, 0.65f, 0.65f, 1f);

    private readonly Plugin _plugin;
    private readonly Configuration _config;
    private readonly SignLibrary _library;
    private readonly SignPlayer _player;
    private readonly FileDialogManager _fileDialog = new();

    private string _message = string.Empty;

    // Draw runs every frame, so the unsupported-character scan is cached and
    // only redone when the text actually changes.
    private string _scannedMessage = string.Empty;
    private string _unsupported = string.Empty;

    // Calibration state.
    private int _selectedFinger;
    private int _selectedJoint;
    private float _testCurl;
    private float _testSpread;

    // Import state.
    private string _importFolder = string.Empty;
    private string _importReport = string.Empty;
    private int _singleTarget;
    private char _previewCharacter = 'A';

    public MainWindow(Plugin plugin, Configuration config, SignLibrary library, SignPlayer player)
        : base("SignSpeak###SignSpeakMain")
    {
        _plugin = plugin;
        _config = config;
        _library = library;
        _player = player;
        _importFolder = config.LastImportFolder;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(470f, 360f),
            MaximumSize = new Vector2(1000f, 1200f),
        };
    }

    public override void Draw()
    {
        DrawStatusBar();
        ImGui.Separator();

        using (var tabs = ImRaii.TabBar("##signspeaktabs"))
        {
            if (tabs)
            {
                DrawSignTab();
                DrawImportTab();
                DrawCalibrateTab();
                DrawAlphabetTab();
            }
        }

        // The file browser draws itself as an overlay and must be pumped every
        // frame, outside the tab bar.
        _fileDialog.Draw();
    }

    private void DrawStatusBar()
    {
        var backend = _plugin.Backend;

        if (backend.IsAvailable)
        {
            ImGui.TextColored(Good, $"{backend.Name} connected");
            ImGui.SameLine();
            ImGui.TextColored(Dim,
                $"|  {_library.Imported.Count} of 36 hand-authored"
                + $"  |  offsets {(backend.SupportsTranslations ? "supported" : "unavailable")}");

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(backend.SupportsTranslations
                    ? "This backend applies bone positions the way pose files store them, so imported\n"
                      + "letters come across exactly."
                    : "This backend cannot reproduce bone positions from a pose file, so imported\n"
                      + "letters use their rotations and scale only. Install Simple Heels for LivePose\n"
                      + "if a letter depends on a moved bone.");
            }

            return;
        }

        ImGui.TextColored(Bad, "No pose backend available");
        ImGui.TextWrapped(backend.UnavailableReason ?? string.Empty);
        ImGui.TextWrapped(
            "SignSpeak needs either LivePose (ships with Simple Heels, preferred) or Customize+ "
            + "installed and enabled. It drives your finger bones through one of them.");
    }

    private void DrawSignTab()
    {
        using var tab = ImRaii.TabItem("Sign");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextUnformatted("Message");

        ImGui.SetNextItemWidth(-1f);
        var submitted = ImGui.InputText(
            "##message", ref _message, 512, ImGuiInputTextFlags.EnterReturnsTrue);

        if (!string.Equals(_scannedMessage, _message, StringComparison.Ordinal))
        {
            _scannedMessage = _message;
            _unsupported = SignBook.Unsupported(_message);
        }

        if (_unsupported.Length > 0)
            ImGui.TextColored(Warn, $"These characters will be skipped: {_unsupported}");

        ImGui.Spacing();

        var canSign = _plugin.Backend.IsAvailable && !string.IsNullOrWhiteSpace(_message);

        using (ImRaii.Disabled(!canSign))
        {
            if (ImGui.Button("Sign it") || (submitted && canSign))
                _plugin.Sign(_message);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(_player.State == PlayerState.Idle))
        {
            if (ImGui.Button("Stop"))
                _plugin.StopSigning();
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            _message = string.Empty;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var speed = _config.Speed;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderFloat("Speed", ref speed, 0.35f, 2.5f, "%.2fx"))
        {
            _config.Speed = speed;
            _plugin.SaveConfig();
        }

        var rightHanded = _config.RightHanded;
        if (ImGui.Checkbox("Sign with the right hand", ref rightHanded))
        {
            _config.RightHanded = rightHanded;
            _plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Affects the built-in shapes only. Imported poses are used exactly as authored, "
                + "on whichever hand the file names.");

        var hz = _config.UpdateHz;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("Update rate", ref hz, 10, 60, "%d Hz"))
        {
            _config.UpdateHz = hz;
            _plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "How often the pose is pushed to the backend. Lower this if you see a frame rate cost.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Pose backend");

        var preferLivePose = _config.PreferLivePose;
        if (ImGui.Checkbox("Prefer LivePose over Customize+", ref preferLivePose))
        {
            _config.PreferLivePose = preferLivePose;
            _plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "LivePose is the plugin that authored your .livepose files, so it applies them\n"
                + "with no format conversion: rotation, position and scale all mean exactly what\n"
                + "the file said. It ships inside Simple Heels.\n\n"
                + "Customize+ is the fallback. It works, but it cannot reproduce bone positions\n"
                + "from a pose file, so hand-authored letters lose any bone the author moved\n"
                + "rather than rotated.");
        }

        ImGui.TextColored(
            _plugin.LivePose.IsAvailable ? Good : Dim,
            $"LivePose: {(_plugin.LivePose.IsAvailable ? "available" : _plugin.LivePose.UnavailableReason)}");

        ImGui.TextColored(
            _plugin.CustomizePlus.IsAvailable ? Good : Dim,
            $"Customize+: {(_plugin.CustomizePlus.IsAvailable ? "available" : _plugin.CustomizePlus.UnavailableReason)}");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Other players");

        var sync = _config.SyncToOtherPlayers;
        if (ImGui.Checkbox("Let synced players see the signing", ref sync))
        {
            _config.SyncToOtherPlayers = sync;
            _plugin.SaveConfig();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Publishes each letter through Simple Heels' tag system, the same route\n"
                + "LivePose uses. Anyone synced to you with Mare, PSync or Snowcloak, and\n"
                + "running Simple Heels themselves, will see your hands move.\n\n"
                + "Requires the LivePose backend, since that is the format remote viewers read.");
        }

        var syncReady = _plugin.HeelsTags.IsAvailable && _plugin.Backend == _plugin.LivePose;

        if (!_plugin.HeelsTags.IsAvailable)
        {
            ImGui.TextColored(Dim, _plugin.HeelsTags.UnavailableReason ?? string.Empty);
        }
        else if (!syncReady)
        {
            ImGui.TextColored(Warn, "Simple Heels is present, but syncing needs the LivePose backend.");
        }
        else if (sync)
        {
            ImGui.TextColored(Good, "Ready. Other players will see each letter.");
            ImGui.TextWrapped(
                "Remote viewers see roughly 3 letters a second at most: Simple Heels debounces "
                + "its broadcast by 250ms, so letters are sent as they settle rather than every "
                + "frame. Above about 1.5x speed some letters will not reach them, so keep the "
                + "speed moderate when you want to be understood.");
        }

        ImGui.Spacing();

        if (_config.ShowLetterReadout && _player.State != PlayerState.Idle)
        {
            var letter = _player.TargetCharacter;
            ImGui.TextUnformatted(letter == SignPlayer.Rest
                ? "Returning to rest..."
                : $"Signing: {letter}    ({_player.Remaining} left)");
        }
    }

    private void DrawImportTab()
    {
        using var tab = ImRaii.TabItem("Import");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Pose your character's hand in game, save it, then map that file to a letter. "
            + "An imported letter replaces the built-in shape completely and ignores the curl "
            + "calibration, because it was authored on your actual skeleton.");

        ImGui.Spacing();
        ImGui.TextColored(Good, ".livepose  (Simple Heels)");
        ImGui.SameLine();
        ImGui.TextWrapped("- recommended. Stores bone offsets, which is exactly what this plugin needs.");

        ImGui.TextColored(Warn, ".pose  (Brio / Anamnesis)");
        ImGui.SameLine();
        ImGui.TextWrapped("- stores absolute rotations, so it needs a reference capture first.");

        ImGui.Spacing();
        var useTranslations = _config.UseImportedTranslations;
        if (ImGui.Checkbox("Also apply positional offsets from the file", ref useTranslations))
        {
            _config.UseImportedTranslations = useTranslations;
            _plugin.SaveConfig();
            _plugin.PreviewCharacter(_previewCharacter);
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "LivePose adds these straight into model space, exactly as the file records them,\n"
                + "so on that backend they are correct and worth having: some letters position a\n"
                + "bone rather than rotating it.\n\n"
                + "Customize+ rotates the value by the bone's own rotation first, which does not\n"
                + "match, so they are withheld there no matter what this is set to.");
        }

        ImGui.SameLine();
        if (!_plugin.Backend.SupportsTranslations)
            ImGui.TextColored(Warn, $"(ignored on {_plugin.Backend.Name})");
        else
            ImGui.TextColored(Dim, useTranslations ? "(applied)" : "(rotations only)");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Import a whole folder");
        ImGui.TextWrapped(
            "Every pose file in the folder is matched to a letter by its filename, so names like "
            + "\"Letter A.livepose\" or \"B.pose\" land automatically. Anything ambiguous is listed "
            + "instead of guessed.");

        ImGui.SetNextItemWidth(-140f);
        ImGui.InputText("##importfolder", ref _importFolder, 1024);
        ImGui.SameLine();

        if (ImGui.Button("Browse##folder"))
        {
            _fileDialog.OpenFolderDialog(
                "Folder of pose files",
                (ok, path) =>
                {
                    if (!ok) return;
                    _importFolder = path;
                    _importReport = _plugin.ImportFolder(path);
                },
                StartPath(_importFolder));
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(string.IsNullOrWhiteSpace(_importFolder)))
        {
            if (ImGui.Button("Import##folder"))
                _importReport = _plugin.ImportFolder(_importFolder);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Import one file");

        var names = Alphabet.Ordered;
        var labels = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
            labels[i] = names[i].ToString();

        ImGui.SetNextItemWidth(70f);
        ImGui.Combo("as##singletarget", ref _singleTarget, labels, labels.Length);
        ImGui.SameLine();

        if (ImGui.Button("Choose a pose file..."))
        {
            var target = names[Math.Clamp(_singleTarget, 0, names.Length - 1)];
            _fileDialog.OpenFileDialog(
                $"Pose file for {target}",
                PoseFilters,
                (ok, path) =>
                {
                    if (!ok || string.IsNullOrEmpty(path)) return;
                    _importReport = _plugin.ImportFile(path, target);
                });
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Reference pose  (only for .pose files)");
        ImGui.TextWrapped(
            "A .pose file records where the bones are, not how far they moved, so it cannot be "
            + "turned into an offset on its own. Save one extra pose of your character standing "
            + "with hands relaxed and load it here; every .pose import is then measured against it.");

        if (_plugin.HasReferencePose)
            ImGui.TextColored(Good, $"Reference loaded: {Path.GetFileName(_config.ReferencePoseFile)}");
        else
            ImGui.TextColored(Dim, "No reference loaded. .livepose files do not need one.");

        if (ImGui.Button("Load a reference .pose..."))
        {
            _fileDialog.OpenFileDialog(
                "Reference pose",
                "Pose files{.pose}",
                (ok, path) =>
                {
                    if (!ok || string.IsNullOrEmpty(path)) return;
                    _importReport = _plugin.SetReferencePose(path);
                });
        }

        if (_importReport.Length > 0)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.TextWrapped(_importReport);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Mapped characters");
        ImGui.TextColored(Dim, "Click one to hold it on your character.");
        ImGui.Spacing();

        DrawCharacterGrid(showImportState: true);

        ImGui.Spacing();
        if (ImGui.Button("Release preview"))
            _plugin.ReleaseProfile();

        ImGui.SameLine();
        using (ImRaii.Disabled(!_library.IsImported(_previewCharacter)))
        {
            if (ImGui.Button($"Clear {_previewCharacter}"))
                _plugin.ClearImport(_previewCharacter);
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(_library.Imported.Count == 0))
        {
            if (ImGui.Button("Clear all imports"))
                _plugin.ClearAllImports();
        }

        if (_library.Imported.TryGetValue(char.ToUpperInvariant(_previewCharacter), out var imported))
        {
            ImGui.Spacing();
            ImGui.TextUnformatted($"{_previewCharacter}  <-  {imported.SourceFile}");
            ImGui.TextColored(Dim,
                $"{imported.Bones.Count} bones, {(imported.RightHand ? "right" : "left")} hand"
                + $"{(imported.CarriesArm ? ", includes the arm" : ", fingers only")}");

            var hold = imported.HoldSeconds;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat($"Hold##{_previewCharacter}", ref hold, 0.05f, 1f, "%.2fs"))
            {
                imported.HoldSeconds = hold;
                _plugin.SaveConfig();
            }

            var transition = imported.TransitionSeconds;
            ImGui.SetNextItemWidth(200f);
            if (ImGui.SliderFloat($"Move in##{_previewCharacter}", ref transition, 0.04f, 0.8f, "%.2fs"))
            {
                imported.TransitionSeconds = transition;
                _plugin.SaveConfig();
            }
        }
    }

    private void DrawCharacterGrid(bool showImportState)
    {
        const int perRow = 9;
        var index = 0;

        foreach (var character in Alphabet.Ordered)
        {
            if (index % perRow != 0)
                ImGui.SameLine();

            var isImported = showImportState && _library.IsImported(character);
            using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.20f, 0.45f, 0.25f, 1f), isImported))
            using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.26f, 0.58f, 0.32f, 1f), isImported))
            {
                if (ImGui.Button(character.ToString(), new Vector2(34f, 30f)))
                {
                    _previewCharacter = character;
                    _plugin.PreviewCharacter(character);
                }
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(_library.IsImported(character)
                    ? $"{character}: hand-authored"
                    : $"{character}: built-in shape");
            }

            index++;
        }
    }

    private void DrawCalibrateTab()
    {
        using var tab = ImRaii.TabItem("Calibrate");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextWrapped(
            "This tunes the built-in shapes only. Characters you have imported ignore all of it, "
            + "so if you intend to hand-author the whole alphabet you can skip this tab entirely.");

        ImGui.Spacing();
        ImGui.TextWrapped(
            "Built-in letters store an abstract curl rather than fixed angles, so the whole "
            + "alphabet is controlled from here. Pick a finger, drag Test curl to 1, and watch "
            + "your character. If the finger bends backwards, negate the matching curl axis. If it "
            + "bends sideways, move the -1 onto a different axis.");

        ImGui.Separator();
        ImGui.Spacing();

        string[] fingerNames = ["Thumb", "Index", "Middle", "Ring", "Pinky"];
        string[] jointNames = ["Knuckle (proximal)", "Tip (distal)"];

        ImGui.SetNextItemWidth(140f);
        ImGui.Combo("Finger", ref _selectedFinger, fingerNames, fingerNames.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180f);
        ImGui.Combo("Joint", ref _selectedJoint, jointNames, jointNames.Length);

        var calibration = _config.GetCalibration();
        var joint = calibration[_selectedFinger, _selectedJoint];
        var changed = false;

        ImGui.Spacing();
        ImGui.TextUnformatted(
            $"Bone: {HandRig.JointBone((Finger)_selectedFinger, _selectedJoint == 1, _config.RightHanded)}");
        ImGui.Spacing();

        var curlAxis = joint.CurlAxis;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.DragFloat3("Curl axis", ref curlAxis, 0.05f, -1f, 1f, "%.2f"))
        {
            joint.CurlAxis = curlAxis;
            changed = true;
        }

        var curlDegrees = joint.CurlDegrees;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderFloat("Curl range", ref curlDegrees, 0f, 140f, "%.0f deg"))
        {
            joint.CurlDegrees = curlDegrees;
            changed = true;
        }

        var spreadAxis = joint.SpreadAxis;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.DragFloat3("Spread axis", ref spreadAxis, 0.05f, -1f, 1f, "%.2f"))
        {
            joint.SpreadAxis = spreadAxis;
            changed = true;
        }

        var spreadDegrees = joint.SpreadDegrees;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.SliderFloat("Spread range", ref spreadDegrees, 0f, 60f, "%.0f deg"))
        {
            joint.SpreadDegrees = spreadDegrees;
            changed = true;
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Live test");

        ImGui.SetNextItemWidth(240f);
        var curlMoved = ImGui.SliderFloat("Test curl", ref _testCurl, 0f, 1f, "%.2f");
        ImGui.SetNextItemWidth(240f);
        var spreadMoved = ImGui.SliderFloat("Test spread", ref _testSpread, -1f, 1f, "%.2f");

        if (changed || curlMoved || spreadMoved)
        {
            _plugin.SaveConfig();
            PreviewSingleFinger();
        }

        ImGui.Spacing();
        if (ImGui.Button("Release preview"))
        {
            _testCurl = 0f;
            _testSpread = 0f;
            _plugin.ReleaseProfile();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reset finger calibration"))
        {
            _config.ResetCalibration();
            _plugin.SaveConfig();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("Signing posture  (optional, starts at zero)");
        ImGui.TextWrapped(
            "An optional arm offset to lift your hand into view for built-in letters. It starts at "
            + "zero, so built-in letters move fingers only and leave your arm wherever the animation "
            + "has it. Imported letters carry their own arm bones and ignore this entirely.");
        ImGui.TextColored(Warn,
            "Raise these in small steps. Z is roll about the bone's own axis, so a large Z twists "
            + "the arm along its length rather than lifting it.");

        var posture = _config.Posture;
        var postureChanged = false;

        var clavicle = posture.Clavicle;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.DragFloat3("Clavicle", ref clavicle, 0.5f, -90f, 90f, "%.0f"))
        {
            posture.Clavicle = clavicle;
            postureChanged = true;
        }

        var upperArm = posture.UpperArm;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.DragFloat3("Upper arm", ref upperArm, 0.5f, -180f, 180f, "%.0f"))
        {
            posture.UpperArm = upperArm;
            postureChanged = true;
        }

        var forearm = posture.Forearm;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.DragFloat3("Forearm", ref forearm, 0.5f, -180f, 180f, "%.0f"))
        {
            posture.Forearm = forearm;
            postureChanged = true;
        }

        var wrist = posture.Wrist;
        ImGui.SetNextItemWidth(240f);
        if (ImGui.DragFloat3("Wrist", ref wrist, 0.5f, -180f, 180f, "%.0f"))
        {
            posture.Wrist = wrist;
            postureChanged = true;
        }

        if (postureChanged)
        {
            _plugin.SaveConfig();
            _plugin.PreviewCharacter(_previewCharacter);
        }

        if (ImGui.Button("Reset posture"))
        {
            _config.ResetPosture();
            _plugin.SaveConfig();
        }
    }

    private void DrawAlphabetTab()
    {
        using var tab = ImRaii.TabItem("Alphabet");
        if (!tab) return;

        ImGui.Spacing();
        ImGui.TextWrapped("Click a character to hold it, so you can check the shape against a reference.");
        ImGui.TextColored(Dim, "Green means you have hand-authored that one.");
        ImGui.Spacing();

        DrawCharacterGrid(showImportState: true);

        ImGui.Spacing();
        if (ImGui.Button("Release preview"))
            _plugin.ReleaseProfile();

        ImGui.Spacing();
        ImGui.Separator();

        ImGui.TextUnformatted(_previewCharacter.ToString());

        if (_library.IsImported(_previewCharacter))
        {
            ImGui.TextColored(Good, "Hand-authored from a pose file.");
        }
        else if (Alphabet.TryGet(_previewCharacter, out var sign))
        {
            ImGui.TextWrapped(sign.Description);

            if (sign.Keys.Length > 1)
                ImGui.TextColored(Dim, $"Motion letter, {sign.Keys.Length} keys.");
        }
    }

    /// <summary>Holds a single finger at the test values so the axis can be read off the model.</summary>
    private void PreviewSingleFinger()
    {
        var pose = HandPose.Neutral;
        var finger = new FingerPose(
            _selectedJoint == 0 ? _testCurl : 0f,
            _selectedJoint == 1 ? _testCurl : 0f,
            _testSpread);

        switch ((Finger)_selectedFinger)
        {
            case Finger.Thumb: pose.Thumb = finger; break;
            case Finger.Index: pose.Index = finger; break;
            case Finger.Middle: pose.Middle = finger; break;
            case Finger.Ring: pose.Ring = finger; break;
            case Finger.Pinky: pose.Pinky = finger; break;
        }

        _plugin.PreviewPose(pose);
    }

    private static string StartPath(string candidate)
        => !string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)
            ? candidate
            : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
}
