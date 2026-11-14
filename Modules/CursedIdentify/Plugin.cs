using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace CursedIdentify;

/// <summary>
/// Companion to the "Cursed Identify (/study)" Penumbra mod.
///
/// While the local player is in /study, the skit loops on a fixed clock. At the moment the cursed
/// smallclothes "snap on" this asks Glamourer to put the real item on the character -- Yakaku Dogi
/// (the model the Succubi Leotard mod replaces), dyed Neon Pink -- and strips head/hands/legs/feet.
/// When the skit resets, or the emote ends, the exact pre-curse Glamourer state is put back.
///
/// v1.0.1: everything goes through Glamourer's JSON state (GetState -> edit -> ApplyState). v1.0.0 used
/// SetItem.V3, whose IReadOnlyList&lt;byte&gt; stain argument cannot cross Dalamud's IPC (a byte[] is
/// serialised as a base64 string and Glamourer's side refuses it) -- every call threw.
/// </summary>
public sealed unsafe class Plugin : IDisposable
{
    private const string CommandName = "/cursed";
    public const string TabName = "Cursed Identify";
    public const string VisorTabName = "Visor Flash";

    /// <summary>Emote row for /study.</summary>
    private const ushort StudyEmote = 296;

    // Timeline, mirrored from curse_build/tools/beats.py (30 fps). The start TMB is 5 frames long.
    private const double Fps = 30.0;
    private const double StartFrames = 5;
    private const double LoopFrames = 882;
    private const double SnapFrame = 320;   // v1.6.0: beats.EQUIP (arms flung wide in the magical-girl transformation)
    private const double OffFrame = 850;

    /// <summary>Yakaku Dogi = body model e6167 v1, which the Succubi Leotard mod replaces.</summary>
    private const ulong LeotardItem = 39510;
    private const int NeonPink = 96;

    // Glamourer's "Nothing" item: uint.MaxValue - 128 - (EquipSlot enum value). Read off saved designs.
    private static readonly (string Slot, ulong Item)[] Stripped =
    [
        ("Head", uint.MaxValue - 128 - 3),
        ("Hands", uint.MaxValue - 128 - 5),
        ("Legs", uint.MaxValue - 128 - 7),
        ("Feet", uint.MaxValue - 128 - 8),
    ];

    // Glamourer IPC (labels/types reflected from Glamourer.Api 1.7.1.3; enums travel as ulong, results as int).
    private const ulong Flags = 1 | 2;          // ApplyFlag.Once | ApplyFlag.Equipment
    private const int Success = 0;              // GlamourerApiEc.Success
    private static readonly string[] Ec =
        ["Success", "NothingDone", "ActorNotFound", "ActorNotHuman", "DesignNotFound", "ItemInvalid", "InvalidKey", "InvalidState", "CouldNotParse"];

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;

    private readonly ICallGateSubscriber<(int, int)> _apiVersion;
    private readonly ICallGateSubscriber<int, uint, (int, JObject?)> _getState;
    private readonly ICallGateSubscriber<object, int, uint, ulong, int> _applyState;

    private readonly MainWindow _window;

    internal bool Enabled = true;
    internal readonly Configuration Config;
    internal readonly VisorFlashFeature VisorFlash;
    private bool _inEmote;
    private DateTime _emoteStart;

    /// <summary>Glamourer state captured right before the curse; restored afterwards.</summary>
    private JObject? _snapshot;

    internal bool Applied { get; private set; }
    internal bool Manual { get; private set; }
    internal string LastResult { get; private set; } = "(nothing yet)";
    internal string Phase { get; private set; } = "";

    /// <summary>After a failure, do not hammer Glamourer every frame.</summary>
    private DateTime _retryAfter = DateTime.MinValue;

    public Plugin()
    {
        _apiVersion = PluginInterface.GetIpcSubscriber<(int, int)>("Glamourer.ApiVersion.V2");
        _getState = PluginInterface.GetIpcSubscriber<int, uint, (int, JObject?)>("Glamourer.GetState");
        _applyState = PluginInterface.GetIpcSubscriber<object, int, uint, ulong, int>("Glamourer.ApplyState");

        Config = CATSToybox.ModuleConfig.Load<Configuration>("CursedIdentify", "CursedIdentify");
        _window = new MainWindow(this);

        Framework.Update += OnUpdate;
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Cursed Identify: '/cursed' opens the window; '/cursed on' / '/cursed off' apply or " +
                          "restore the outfit by hand (for testing); '/cursed toggle' enables/disables the swap during /study.",
        });
        VisorFlash = new VisorFlashFeature(Config, () => CATSToybox.Toybox.Open(VisorTabName));
    }

    public void Dispose()
    {
        Framework.Update -= OnUpdate;
        CommandManager.RemoveHandler(CommandName);
        VisorFlash.Dispose();
        Restore();
    }

    /// <summary>Draws the Cursed Identify tab inside the CATS Toybox window.</summary>
    public void DrawTab() => _window.Draw();

    /// <summary>Draws the Visor Flash tab inside the CATS Toybox window.</summary>
    public void DrawVisorTab() => VisorFlash.DrawSection();

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                ManualOn();
                ChatGui.Print($"[Cursed Identify] apply: {LastResult}");
                break;
            case "off":
                ManualOff();
                ChatGui.Print($"[Cursed Identify] restore: {LastResult}");
                break;
            case "toggle":
                SetEnabled(!Enabled);
                ChatGui.Print($"[Cursed Identify] outfit swap during /study: {(Enabled ? "ON" : "OFF")}");
                break;
            default:
                CATSToybox.Toybox.Toggle(TabName);
                break;
        }
    }

    internal void ManualOn() { Manual = true; _retryAfter = DateTime.MinValue; Apply(); }
    internal void ManualOff() { Manual = false; Restore(); }
    internal void SetEnabled(bool on) { Enabled = on; if (!on) Restore(); }

    private void OnUpdate(IFramework framework)
    {
        if (Manual) return;
        var local = ObjectTable.LocalPlayer;
        if (local == null)
        {
            _inEmote = false;
            return;
        }

        var chara = (GameCharacter*)local.Address;
        var studying = chara->EmoteController.EmoteId == StudyEmote;

        if (studying && !_inEmote) _emoteStart = DateTime.UtcNow;
        _inEmote = studying;

        var want = false;
        Phase = "not in /study";
        if (studying && Enabled)
        {
            var frame = (DateTime.UtcNow - _emoteStart).TotalSeconds * Fps - StartFrames;
            if (frame >= 0)
            {
                var phase = frame % LoopFrames;
                want = phase >= SnapFrame && phase < OffFrame;
                Phase = $"/study frame {phase:0} of {LoopFrames} ({(want ? "cursed" : "normal")})";
            }
        }

        if (want && !Applied && DateTime.UtcNow >= _retryAfter) Apply();
        else if (!want && Applied) Restore();
    }

    private bool GlamourerReady()
    {
        try
        {
            var (major, _) = _apiVersion.InvokeFunc();
            if (major >= 1) return true;
        }
        catch (Exception)
        {
            // not loaded
        }

        LastResult = "Glamourer is not available";
        return false;
    }

    private static string Name(int ec) => ec >= 0 && ec < Ec.Length ? Ec[ec] : ec.ToString();

    private void Fail(string what)
    {
        LastResult = what;
        Log.Warning(what);
        _retryAfter = DateTime.UtcNow.AddSeconds(5);
    }

    private void Apply()
    {
        if (Applied || !GlamourerReady()) return;
        var local = ObjectTable.LocalPlayer;
        if (local == null) return;
        int index = local.ObjectIndex;

        try
        {
            var (ec, state) = _getState.InvokeFunc(index, 0);
            if (ec != Success || state == null)
            {
                Fail($"GetState failed: {Name(ec)}");
                return;
            }

            _snapshot = (JObject)state.DeepClone();
            if (state["Equipment"] is not JObject equip)
            {
                Fail("Glamourer state has no Equipment block");
                return;
            }

            SetSlot(equip, "Body", LeotardItem, NeonPink);
            foreach (var (slot, item) in Stripped) SetSlot(equip, slot, item, 0);

            var r = _applyState.InvokeFunc(state, index, 0, Flags);
            if (r != Success)
            {
                Fail($"ApplyState (curse) returned {Name(r)}");
                _snapshot = null;
                return;
            }

            Applied = true;
            LastResult = "cursed outfit applied";
        }
        catch (Exception e)
        {
            Log.Error(e, "applying the cursed outfit failed");
            Fail($"applying failed: {e.GetType().Name}: {e.Message}");
            _snapshot = null;
        }
    }

    /// <summary>Set one equipment entry of a Glamourer state/design JSON, keeping its other fields.</summary>
    private static void SetSlot(JObject equip, string slot, ulong item, int stain)
    {
        if (equip[slot] is not JObject e)
        {
            e = new JObject();
            equip[slot] = e;
        }

        e["ItemId"] = item;
        e["Apply"] = true;
        e["ApplyStain"] = true;
        e["Stain"] = stain;
        if (e.ContainsKey("Stain2")) e["Stain2"] = stain;
    }

    private void Restore()
    {
        if (!Applied || _snapshot == null)
        {
            Applied = false;
            return;
        }

        try
        {
            var local = ObjectTable.LocalPlayer;
            if (local != null)
            {
                var r = _applyState.InvokeFunc(_snapshot, local.ObjectIndex, 0, Flags);
                LastResult = r == Success ? "previous outfit restored" : $"ApplyState (restore) returned {Name(r)}";
                if (r != Success) Log.Warning(LastResult);
            }
        }
        catch (Exception e)
        {
            Log.Error(e, "restoring the previous outfit failed");
            LastResult = $"restoring failed: {e.GetType().Name}: {e.Message}";
        }

        Applied = false;
        _snapshot = null;
    }
}

/// <summary>Tiny status window: enable toggle, manual test buttons, last Glamourer result.</summary>
internal sealed class MainWindow : Window
{
    private readonly Plugin _plugin;

    public MainWindow(Plugin plugin) : base("Cursed Identify###CursedIdentifyMain", ImGuiWindowFlags.AlwaysAutoResize)
    {
        _plugin = plugin;
    }

    public override void Draw()
    {
        var enabled = _plugin.Enabled;
        if (ImGui.Checkbox("Swap the outfit during /study", ref enabled)) _plugin.SetEnabled(enabled);
        ImGui.TextUnformatted(_plugin.Phase);
        ImGui.Separator();
        if (ImGui.Button("Apply cursed outfit now")) _plugin.ManualOn();
        ImGui.SameLine();
        if (ImGui.Button("Restore my outfit")) _plugin.ManualOff();
        ImGui.TextUnformatted($"Outfit swapped: {(_plugin.Applied ? "yes" : "no")}");
        ImGui.TextUnformatted($"Last result: {_plugin.LastResult}");
        ImGui.Separator();
        ImGui.TextWrapped("Needs Glamourer, and the Succubi Leotard mod enabled in Penumbra (it replaces Yakaku Dogi).");
    }
}
