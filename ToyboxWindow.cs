using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace CATSToybox;

/// <summary>The one CATS Toybox window: a Home tab, then one tab per running toy.</summary>
public sealed class ToyboxWindow : Window
{
    public const string HomeTab = "Home";

    private readonly Plugin plugin;
    private string? selectNext;

    public string CurrentTab { get; private set; } = HomeTab;

    public ToyboxWindow(Plugin plugin) : base("CATS Toybox###CATSToybox")
    {
        this.plugin = plugin;
        Size = new Vector2(620, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 320), MaximumSize = new Vector2(1600, 1400) };
    }

    public void Show(string tab)
    {
        selectNext = tab;
        IsOpen = true;
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##toyboxTabs")) return;

        DrawTab(HomeTab, DrawHome);
        foreach (var toy in plugin.Toys)
        {
            if (toy.Running == null) continue;
            foreach (var (tab, draw) in toy.Running.Tabs)
                DrawTab(tab, draw);
        }

        ImGui.EndTabBar();
        selectNext = null;
    }

    private void DrawTab(string name, Action draw)
    {
        var flags = selectNext == name ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (!ImGui.BeginTabItem(name, flags)) return;
        CurrentTab = name;

        if (ImGui.BeginChild("##tab_" + name, Vector2.Zero, false))
        {
            try { draw(); }
            catch (Exception e)
            {
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"This tab hit an error: {e.Message}");
                Plugin.Log.Error(e, $"Drawing the {name} tab failed");
            }
        }
        ImGui.EndChild();
        ImGui.EndTabItem();
    }

    private void DrawHome()
    {
        ImGui.TextWrapped("Welcome to the CATS Toybox! Each toy has its own tab. Untick a toy to switch it off completely (its commands and tab go away too).");
        ImGui.Spacing();

        foreach (var toy in plugin.Toys)
        {
            ImGui.Separator();
            var on = plugin.IsEnabled(toy);
            if (ImGui.Checkbox($"{toy.Title}##en_{toy.Key}", ref on))
                plugin.SetEnabled(toy, on);

            ImGui.Indent();
            ImGui.TextWrapped(toy.Blurb);
            ImGui.TextDisabled(toy.Commands);
            if (toy.Error.Length > 0)
                ImGui.TextColored(new Vector4(1, 0.4f, 0.4f, 1), $"Couldn't start: {toy.Error}");
            else if (toy.Running != null && ImGui.SmallButton($"Open##open_{toy.Key}"))
                selectNext = toy.Running.Tabs[0].Tab;
            ImGui.Unindent();
        }

        ImGui.Separator();
        ImGui.TextDisabled("/toybox opens this window.");
    }
}
