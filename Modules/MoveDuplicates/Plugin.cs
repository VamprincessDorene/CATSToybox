using System;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace MoveDuplicates;

public sealed class Configuration
{
    public bool ShowMenuItem { get; set; } = true;
    public int RetainerDelayMs { get; set; } = 150;
    public int FcDelayMs { get; set; } = 600;
}

public sealed class Plugin : IDisposable
{
    public const string TabName = "Move Duplicates";

    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal static Configuration Config { get; private set; } = new();

    private readonly DuplicateMover mover = new();

    public Plugin()
    {
        Config = CATSToybox.ModuleConfig.Load<Configuration>("MoveDuplicates");
        ContextMenu.OnMenuOpened += OnMenuOpened;
        Framework.Update += mover.Tick;
    }

    public void Dispose()
    {
        ContextMenu.OnMenuOpened -= OnMenuOpened;
        Framework.Update -= mover.Tick;
    }

    private void OnMenuOpened(IMenuOpenedArgs args)
    {
        if (!Config.ShowMenuItem) return;
        if (args.MenuType != ContextMenuType.Inventory) return;
        if (args.Target is not MenuTargetInventory { TargetItem: { } item }) return;

        var clicked = (InventoryType)(int)item.ContainerType;
        var job = DuplicateMover.PlanFor(clicked);
        if (job == null) return;

        args.AddMenuItem(new MenuItem
        {
            Name = new SeStringBuilder().AddText("Move Duplicates").Build(),
            PrefixChar = 'M',
            IsEnabled = !mover.Busy,
            OnClicked = _ => mover.Start(job),
        });
    }

    /// <summary>Draws the Move Duplicates tab inside the CATS Toybox window.</summary>
    public void DrawTab()
    {
        ImGui.TextWrapped("Right-click any item while a retainer or the FC chest is open and pick \"Move Duplicates\".");
        ImGui.BulletText("In your inventory: stacks the retainer / FC chest already has go to it.");
        ImGui.BulletText("In a retainer: stacks you already have come back to you.");
        ImGui.BulletText("In an FC chest tab: stacks from that tab you already have come back to you.");
        ImGui.TextDisabled("Only stackable items count, and NQ / HQ are kept apart.");
        ImGui.Separator();

        var changed = false;
        var show = Config.ShowMenuItem;
        if (ImGui.Checkbox("Show \"Move Duplicates\" in the right-click menu", ref show)) { Config.ShowMenuItem = show; changed = true; }

        var r = Config.RetainerDelayMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Delay between retainer moves (ms)", ref r, 50, 1000)) { Config.RetainerDelayMs = r; changed = true; }

        var f = Config.FcDelayMs;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderInt("Delay between FC chest moves (ms)", ref f, 300, 2000)) { Config.FcDelayMs = f; changed = true; }
        ImGui.TextDisabled("If moves get skipped, raise the delay.");

        ImGui.Separator();
        ImGui.TextUnformatted(mover.Busy ? "Moving items..." : "Idle.");
        if (mover.Busy && ImGui.Button("Stop")) mover.Cancel();

        if (changed) CATSToybox.ModuleConfig.Save("MoveDuplicates", Config);
    }
}
