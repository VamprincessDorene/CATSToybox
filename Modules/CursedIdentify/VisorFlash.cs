using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Shell;
using GameCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace CursedIdentify;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // Visor Flash
    public bool VisorFlashEnabled = true;
    public bool Flash;                                         // current style
    public bool SyncPenumbra;                                  // also switch the Penumbra option (others see it; causes a redraw)
    public string FlashModName = "Charm Shirt Halter";         // matched against Penumbra's mod list (name or folder)
    public string FlashGroup = "Style";
    public string NormalOption = "Normal";
    public string FlashOption = "Flash";
    public string NormalAttribute = "atr_tv_a";               // the shirt's Normal part
    public string FlashAttribute = "atr_tv_b";                // the shirt's Flash part
}

/// <summary>
/// Visor Flash (bundled since v1.1.0): /visor flips the "Charm Shirt Halter" between its Normal and Flash shirt bodies.
///
/// v1.2.0 - SEAMLESS: both shirt bodies live in the same model as tagged parts (atr_tv_a = Normal, atr_tv_b = Flash).
/// Every frame the local player's body-slot model gets its EnabledAttributeIndexMask set so only the chosen part is
/// drawn - the same mechanism the game uses to hide sleeves under gloves - so nothing reloads and nothing redraws.
/// (Only you see this; others see the Penumbra option, which can optionally be switched too.)
///
/// v1.3.0 - OTHERS see it too: the style is published as a Simple Heels tag (CursedIdentify.Flash) that Mare/PSync/
/// Snowcloak already carry; every Cursed Identify reads it for nearby players wearing the Charm shirt and applies the same
/// part switch on their model. No Penumbra sync, no redraw on anyone's side.
///
/// v1.2.0 - /visor is a real game command, so Dalamud's command manager never receives it (v1.1.x registered it and
/// nothing happened). It is caught by hooking ShellCommandModule.ExecuteCommandInner, which sees every typed command
/// before the game runs it; the original is always called so the game's own visor toggle still happens.
/// </summary>
internal sealed unsafe class VisorFlashFeature : IDisposable
{
    private const string MainCommand = "/visorflash";
    private const int BodySlot = 1;                            // Human model slots: 0 head, 1 body, 2 hands, 3 legs, 4 feet ...

    private delegate void ExecuteCommandInnerDelegate(ShellCommandModule* self, Utf8String* message, UIModule* uiModule);
    private readonly Hook<ExecuteCommandInnerDelegate>? _commandHook;

    // Penumbra IPC (labels confirmed in Penumbra 1.7.2.1; enums travel as int) - only for the optional sync
    private readonly ICallGateSubscriber<Dictionary<string, string>> _getModList;
    private readonly ICallGateSubscriber<int, (bool, bool, (Guid, string))> _collectionForObject;
    private readonly ICallGateSubscriber<Guid, string, string, string, string, int> _trySetModSetting;
    private readonly ICallGateSubscriber<int, int, object> _redrawObject;
    private static readonly string[] Ec =
    [
        "Success", "NothingChanged", "CollectionMissing", "ModMissing", "OptionGroupMissing", "OptionMissing",
        "CharacterCollectionExists", "LowerPriority", "InvalidGamePath", "FileMissing", "InvalidManipulation",
        "InvalidArgument", "PathRenameFailed", "CollectionExists", "UnknownError", "SystemDisposed", "AssetsMissing",
    ];

    // Simple Heels tags: the style travels to other players with your synced data (Mare/PSync/Snowcloak)
    private const string FlashTag = "CursedIdentify.Flash";
    private readonly ICallGateSubscriber<int, string, string, object?> _heelsSetTag;
    private readonly ICallGateSubscriber<int, string, object?> _heelsRemoveTag;
    private readonly ICallGateSubscriber<int, string, string?> _heelsGetTag;
    private readonly Dictionary<int, (bool Flash, DateTime Read)> _remote = new();
    private DateTime _lastPublish = DateTime.MinValue;
    internal string SyncStatus { get; private set; } = "-";
    internal int RemoteShown { get; private set; }

    private readonly Configuration _config;
    private readonly Action _openWindow;
    internal string LastResult { get; private set; } = "-";
    internal string ModelStatus { get; private set; } = "-";
    internal bool CommandHooked => _commandHook != null;

    public VisorFlashFeature(Configuration config, Action openWindow)
    {
        _config = config;
        _openWindow = openWindow;
        var pi = Plugin.PluginInterface;
        _getModList = pi.GetIpcSubscriber<Dictionary<string, string>>("Penumbra.GetModList");
        _collectionForObject = pi.GetIpcSubscriber<int, (bool, bool, (Guid, string))>("Penumbra.GetCollectionForObject.V5");
        _trySetModSetting = pi.GetIpcSubscriber<Guid, string, string, string, string, int>("Penumbra.TrySetModSetting.V5");
        _redrawObject = pi.GetIpcSubscriber<int, int, object>("Penumbra.RedrawObject.V5");
        _heelsSetTag = pi.GetIpcSubscriber<int, string, string, object?>("SimpleHeels.SetTag");
        _heelsRemoveTag = pi.GetIpcSubscriber<int, string, object?>("SimpleHeels.RemoveTag");
        _heelsGetTag = pi.GetIpcSubscriber<int, string, string?>("SimpleHeels.GetTag");

        try
        {
            _commandHook = Plugin.GameInterop.HookFromAddress<ExecuteCommandInnerDelegate>(
                (nint)ShellCommandModule.MemberFunctionPointers.ExecuteCommandInner, OnExecuteCommand);
            _commandHook.Enable();
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Visor Flash: could not hook the command handler; /visor will not switch the style");
        }

        Plugin.CommandManager.AddHandler(MainCommand, new CommandInfo(OnMain)
        {
            HelpMessage = "Visor Flash: opens the Cursed Identify window. Also: /visorflash on | off | normal | flash | toggle",
        });
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.CommandManager.RemoveHandler(MainCommand);
        _commandHook?.Dispose();
        RestoreMask();
        try { _heelsRemoveTag.InvokeAction(0, FlashTag); } catch (Exception) { }
    }

    private void Save() => CATSToybox.ModuleConfig.Save("CursedIdentify", _config);

    // ------------------------------------------------------------------ /visor
    private void OnExecuteCommand(ShellCommandModule* self, Utf8String* message, UIModule* uiModule)
    {
        try
        {
            var text = message != null ? message->ToString().Trim() : string.Empty;
            if (_config.VisorFlashEnabled && (text.Equals("/visor", StringComparison.OrdinalIgnoreCase)
                                              || text.StartsWith("/visor ", StringComparison.OrdinalIgnoreCase)))
                SetStyle(!_config.Flash);
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Visor Flash: command detour failed");
        }
        _commandHook!.Original(self, message, uiModule);           // the game still runs /visor itself
    }

    private void OnMain(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on": SetEnabled(true); Plugin.ChatGui.Print("[Visor Flash] /visor now switches the style."); break;
            case "off": SetEnabled(false); Plugin.ChatGui.Print("[Visor Flash] /visor only toggles the visor now."); break;
            case "flash": SetStyle(true); break;
            case "normal": SetStyle(false); break;
            case "toggle": SetStyle(!_config.Flash); break;
            default: _openWindow(); break;
        }
    }

    internal void SetEnabled(bool on)
    {
        _config.VisorFlashEnabled = on; Save();
        if (!on) RestoreMask();
        PublishTag();
    }

    internal void SetStyle(bool flash)
    {
        _config.Flash = flash; Save();
        LastResult = flash ? _config.FlashOption : _config.NormalOption;
        if (_config.SyncPenumbra) SyncPenumbra(flash);
        PublishTag();
    }

    /// <summary>Tell other players (with Cursed Identify) which style you show: a Simple Heels tag on yourself.</summary>
    private void PublishTag()
    {
        _lastPublish = DateTime.UtcNow;
        try
        {
            if (_config.VisorFlashEnabled && _config.Flash) _heelsSetTag.InvokeAction(0, FlashTag, "1");
            else _heelsRemoveTag.InvokeAction(0, FlashTag);
            SyncStatus = "shared through Simple Heels";
        }
        catch (Exception)
        {
            SyncStatus = "Simple Heels not available - only you see the switch";
        }
    }

    /// <summary>Remote player's style from their Simple Heels tag (polled twice a second per player).</summary>
    private bool RemoteFlash(int objectIndex)
    {
        if (_remote.TryGetValue(objectIndex, out var r) && (DateTime.UtcNow - r.Read).TotalSeconds < 0.5) return r.Flash;
        var flash = false;
        try { flash = _heelsGetTag.InvokeFunc(objectIndex, FlashTag) == "1"; }
        catch (Exception) { /* Simple Heels missing or no data for that player */ }
        _remote[objectIndex] = (flash, DateTime.UtcNow);
        return flash;
    }

    // ------------------------------------------------------------------ seamless: part visibility on the live model
    /// <summary>The local player's body-slot model, if it is the Charm shirt (has both style attributes).</summary>
    private FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model* BodyModel(out short idxNormal, out short idxFlash)
    {
        idxNormal = idxFlash = -1;
        var local = Plugin.ObjectTable.LocalPlayer;
        return local == null ? null : BodyModel((GameCharacter*)local.Address, out idxNormal, out idxFlash);
    }

    private FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model* BodyModel(GameCharacter* chara, out short idxNormal, out short idxFlash)
    {
        idxNormal = idxFlash = -1;
        if (chara == null) return null;
        var draw = (CharacterBase*)chara->GameObject.DrawObject;
        if (draw == null || draw->GetModelType() != CharacterBase.ModelType.Human || draw->SlotCount <= BodySlot) return null;
        var model = draw->Models[BodySlot];
        if (model == null || model->ModelResourceHandle == null) return null;
        idxNormal = model->GetAttributeIndex(_config.NormalAttribute);
        idxFlash = model->GetAttributeIndex(_config.FlashAttribute);
        return idxNormal >= 0 && idxFlash >= 0 ? model : null;
    }

    private static void Show(FFXIVClientStructs.FFXIV.Client.Graphics.Render.Model* model, short a, short b, bool flash)
    {
        var mask = model->EnabledAttributeIndexMask;
        var want = flash ? (mask & ~(1u << a)) | (1u << b) : (mask | (1u << a)) & ~(1u << b);
        if (want != mask) model->EnabledAttributeIndexMask = want;
    }

    private void OnUpdate(IFramework _)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null) return;

        // you
        if (_config.VisorFlashEnabled)
        {
            var model = BodyModel((GameCharacter*)local.Address, out var a, out var b);
            if (model == null) ModelStatus = "Charm shirt not worn (or not loaded)";
            else
            {
                Show(model, a, b, _config.Flash);
                ModelStatus = $"Charm shirt found - showing {(_config.Flash ? _config.FlashOption : _config.NormalOption)}";
            }
            if ((DateTime.UtcNow - _lastPublish).TotalSeconds > 10) PublishTag();   // keep the tag alive across zones
        }

        // other players wearing the Charm shirt (their copy of the mod reaches you through their sync)
        var shown = 0;
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj == null || obj.ObjectKind != ObjectKind.Pc || obj.ObjectIndex == local.ObjectIndex || obj.ObjectIndex >= 200) continue;
            var m = BodyModel((GameCharacter*)obj.Address, out var ra, out var rb);
            if (m == null) continue;
            Show(m, ra, rb, RemoteFlash(obj.ObjectIndex));
            shown++;
        }
        RemoteShown = shown;
    }

    /// <summary>Hand control back to the mod's own (Penumbra) setting.</summary>
    private void RestoreMask()
    {
        var model = BodyModel(out var a, out var b);
        if (model == null) return;
        var normal = !_config.Flash;    // best effort: leave the currently chosen part visible
        var mask = model->EnabledAttributeIndexMask;
        model->EnabledAttributeIndexMask = normal ? (mask | (1u << a)) & ~(1u << b) : (mask & ~(1u << a)) | (1u << b);
    }

    // ------------------------------------------------------------------ optional: Penumbra option (others see it)
    private void SyncPenumbra(bool flash)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null) return;
        try
        {
            var key = _config.FlashModName.Trim();
            var mods = _getModList.InvokeFunc()
                .Where(kv => kv.Value.Contains(key, StringComparison.OrdinalIgnoreCase) || kv.Key.Contains(key, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (mods.Count == 0) { Report($"mod '{_config.FlashModName}' not found in Penumbra"); return; }
            var (valid, _, collection) = _collectionForObject.InvokeFunc(local.ObjectIndex);
            if (!valid) { Report("Penumbra has no collection for your character"); return; }
            var option = flash ? _config.FlashOption : _config.NormalOption;
            var done = new List<string>();
            var refused = new List<string>();
            foreach (var (dir, name) in mods)
            {
                var ec = _trySetModSetting.InvokeFunc(collection.Item1, dir, string.Empty, _config.FlashGroup, option);
                if (ec is 0 or 1) done.Add(name);
                else refused.Add($"{name}: {(ec >= 0 && ec < Ec.Length ? Ec[ec] : ec.ToString())}");
            }
            if (done.Count > 0)
            {
                _redrawObject.InvokeAction(local.ObjectIndex, 0);          // RedrawType.Redraw (needed for others' view)
                LastResult = $"{option} + Penumbra ({string.Join(", ", done)})";
            }
            else Report($"no matching mod has a '{_config.FlashGroup}' option ({string.Join("; ", refused)})");
        }
        catch (Exception e)
        {
            Plugin.Log.Error(e, "Visor Flash: Penumbra call failed");
            Report("Penumbra is not available (is it installed and enabled?)");
        }
    }

    private void Report(string msg)
    {
        LastResult = msg;
        Plugin.ChatGui.PrintError($"[Visor Flash] {msg}");
    }

    /// <summary>Section drawn inside the Cursed Identify window.</summary>
    internal void DrawSection()
    {
        ImGui.TextUnformatted("Visor Flash (Charm Shirt Halter)");
        var enabled = _config.VisorFlashEnabled;
        if (ImGui.Checkbox("/visor switches Normal <-> Flash", ref enabled)) SetEnabled(enabled);
        ImGui.TextUnformatted($"Current style: {(_config.Flash ? _config.FlashOption : _config.NormalOption)}");
        if (ImGui.Button("Normal")) SetStyle(false);
        ImGui.SameLine();
        if (ImGui.Button("Flash")) SetStyle(true);
        ImGui.TextUnformatted(ModelStatus);
        ImGui.TextUnformatted($"Others: {SyncStatus}; Charm shirts on nearby players: {RemoteShown}");
        var sync = _config.SyncPenumbra;
        if (ImGui.Checkbox("Also switch the Penumbra option (others see it; causes a quick refresh)", ref sync)) { _config.SyncPenumbra = sync; Save(); }
        if (sync)
        {
            var name = _config.FlashModName;
            if (ImGui.InputText("Mod name", ref name, 128)) { _config.FlashModName = name; Save(); }
        }
        ImGui.TextUnformatted($"Last result: {LastResult}");
        if (!CommandHooked) ImGui.TextWrapped("Note: the command hook failed, so /visor can't switch the style - use the buttons or /visorflash toggle.");
    }
}
