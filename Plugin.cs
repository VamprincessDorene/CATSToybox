using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CATSToybox;

/// <summary>Saved Toybox-wide settings. Each toy keeps its own settings file next to this one.</summary>
public sealed class ToyboxConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Toy key -> on/off. Missing means on.</summary>
    public Dictionary<string, bool> Enabled { get; set; } = new();
}

/// <summary>One toy that can be switched on and off.</summary>
public sealed class ToyDef
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Blurb { get; init; }
    public required string Commands { get; init; }

    /// <summary>Starts the toy and returns it plus the tabs it shows.</summary>
    public required Func<RunningToy> Start { get; init; }

    public RunningToy? Running { get; set; }
    public string Error { get; set; } = "";
}

public sealed class RunningToy(IDisposable instance, params (string Tab, Action Draw)[] tabs)
{
    public IDisposable Instance { get; } = instance;
    public (string Tab, Action Draw)[] Tabs { get; } = tabs;
}

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/toybox";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    internal ToyboxConfig Config { get; }
    internal List<ToyDef> Toys { get; } = new();

    private readonly WindowSystem windows = new("CATSToybox");
    internal ToyboxWindow Window { get; }

    public Plugin()
    {
        Toybox.Host = this;
        Config = PluginInterface.GetPluginConfig() as ToyboxConfig ?? new ToyboxConfig();

        Toys.Add(new ToyDef
        {
            Key = "FlipCup", Title = "Flip Cup", Commands = "/flipcup",
            Blurb = "Play flip cup with friends on a counter in your house.",
            Start = () =>
            {
                var m = Create<FlipCup.Plugin>();
                return new RunningToy(m, (FlipCup.Plugin.TabName, m.DrawTab));
            },
        });
        Toys.Add(new ToyDef
        {
            Key = "SignSpeak", Title = "SignSpeak", Commands = "/signspeak, /sign",
            Blurb = "Type a message and your character fingerspells it in ASL.",
            Start = () =>
            {
                var m = Create<SignSpeak.Plugin>();
                return new RunningToy(m, (SignSpeak.Plugin.TabName, m.DrawTab));
            },
        });
        Toys.Add(new ToyDef
        {
            Key = "CursedIdentify", Title = "Cursed Identify + Visor Flash", Commands = "/cursed, /visor, /visorflash",
            Blurb = "The cursed smallclothes lock on during /study, and /visor flashes the Charm Shirt Halter.",
            Start = () =>
            {
                var m = Create<CursedIdentify.Plugin>();
                return new RunningToy(m,
                    (CursedIdentify.Plugin.TabName, m.DrawTab),
                    (CursedIdentify.Plugin.VisorTabName, m.DrawVisorTab));
            },
        });
        Toys.Add(new ToyDef
        {
            Key = "MoveDuplicates", Title = "Move Duplicates", Commands = "right-click an item",
            Blurb = "Move duplicate stacks between your inventory and a retainer or the FC chest.",
            Start = () =>
            {
                var m = Create<MoveDuplicates.Plugin>();
                return new RunningToy(m, (MoveDuplicates.Plugin.TabName, m.DrawTab));
            },
        });

        foreach (var toy in Toys)
            if (IsEnabled(toy)) StartToy(toy);

        Window = new ToyboxWindow(this);
        windows.AddWindow(Window);
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenHome;
        PluginInterface.UiBuilder.OpenConfigUi += OpenHome;

        CommandManager.AddHandler(Command, new CommandInfo((_, _) => Window.Toggle())
        {
            HelpMessage = "Open the CATS Toybox.",
        });
    }

    private static T Create<T>() where T : class =>
        PluginInterface.Create<T>() ?? throw new InvalidOperationException($"{typeof(T).FullName} failed to start; see /xllog.");

    private void OpenHome() => Toybox.Open(ToyboxWindow.HomeTab);

    internal bool IsEnabled(ToyDef toy) => !Config.Enabled.TryGetValue(toy.Key, out var on) || on;

    internal void SetEnabled(ToyDef toy, bool on)
    {
        Config.Enabled[toy.Key] = on;
        PluginInterface.SavePluginConfig(Config);
        if (on) StartToy(toy);
        else StopToy(toy);
    }

    private void StartToy(ToyDef toy)
    {
        if (toy.Running != null) return;
        try
        {
            toy.Running = toy.Start();
            toy.Error = "";
        }
        catch (Exception e)
        {
            Log.Error(e, $"Starting {toy.Title} failed");
            toy.Error = $"{e.GetType().Name}: {e.Message}";
        }
    }

    private void StopToy(ToyDef toy)
    {
        if (toy.Running == null) return;
        try { toy.Running.Instance.Dispose(); }
        catch (Exception e) { Log.Error(e, $"Stopping {toy.Title} failed"); }
        toy.Running = null;
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenHome;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenHome;
        windows.RemoveAllWindows();
        foreach (var toy in Toys) StopToy(toy);
        Toybox.Host = null;
    }
}
