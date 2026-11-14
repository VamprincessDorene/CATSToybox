using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace MoveDuplicates;

public enum Side { Player, Retainer, FreeCompany }

public sealed class MoveJob
{
    public required string Description { get; init; }
    public required InventoryType[] Source { get; init; }
    public required InventoryType[] Destination { get; init; }
    /// The window that must stay open for the job to keep running.
    public required Side Remote { get; init; }
    /// FC chest refuses untradable items.
    public bool RequireTradable { get; init; }
    /// Delay between moves. The FC chest needs a server round-trip per move.
    public int DelayMs { get; init; } = 150;
}

public sealed unsafe class DuplicateMover
{
    private static readonly InventoryType[] PlayerBags =
    [
        InventoryType.Inventory1, InventoryType.Inventory2,
        InventoryType.Inventory3, InventoryType.Inventory4,
    ];

    private static readonly InventoryType[] RetainerBags =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3,
        InventoryType.RetainerPage4, InventoryType.RetainerPage5, InventoryType.RetainerPage6,
        InventoryType.RetainerPage7,
    ];

    private static readonly InventoryType[] FcBags =
    [
        InventoryType.FreeCompanyPage1, InventoryType.FreeCompanyPage2, InventoryType.FreeCompanyPage3,
        InventoryType.FreeCompanyPage4, InventoryType.FreeCompanyPage5,
    ];

    private const int MoveTimeoutMs = 3000;

    private MoveJob? job;
    private readonly HashSet<(InventoryType, int)> tried = new();
    private (InventoryType Type, int Slot, uint ItemId, int Qty)? pending;
    private long pendingSince;
    private long nextActionAt;
    private int moved;

    public bool Busy => job != null;

    // ---------------------------------------------------------------- planning

    /// Decide what "Move Duplicates" means based on which container was right-clicked.
    public static MoveJob? PlanFor(InventoryType clicked)
    {
        if (PlayerBags.Contains(clicked))
        {
            // Deposit. The FC chest wins if both happen to be open.
            if (IsOpen(Side.FreeCompany))
                return new MoveJob
                {
                    Description = "inventory → FC chest",
                    Source = PlayerBags, Destination = FcBags,
                    Remote = Side.FreeCompany, RequireTradable = true, DelayMs = Plugin.Config.FcDelayMs,
                };
            if (IsOpen(Side.Retainer))
                return new MoveJob
                {
                    Description = "inventory → retainer",
                    Source = PlayerBags, Destination = RetainerBags, Remote = Side.Retainer, DelayMs = Plugin.Config.RetainerDelayMs,
                };
            return null;
        }

        if (RetainerBags.Contains(clicked) && IsOpen(Side.Retainer))
            return new MoveJob
            {
                Description = "retainer → inventory",
                Source = RetainerBags, Destination = PlayerBags, Remote = Side.Retainer, DelayMs = Plugin.Config.RetainerDelayMs,
            };

        if (FcBags.Contains(clicked) && IsOpen(Side.FreeCompany))
            return new MoveJob
            {
                // Only the tab you right-clicked in.
                Description = "FC chest tab → inventory",
                Source = [clicked], Destination = PlayerBags,
                Remote = Side.FreeCompany, DelayMs = Plugin.Config.FcDelayMs,
            };

        return null;
    }

    private static bool IsOpen(Side side)
    {
        var names = side switch
        {
            Side.Retainer => new[] { "InventoryRetainer", "InventoryRetainerLarge" },
            Side.FreeCompany => new[] { "FreeCompanyChest" },
            _ => Array.Empty<string>(),
        };
        var mgr = RaptureAtkUnitManager.Instance();
        foreach (var n in names)
        {
            var addon = mgr->GetAddonByName(n);
            if (addon != null && addon->IsVisible) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- running

    public void Start(MoveJob newJob)
    {
        if (job != null) return;
        job = newJob;
        tried.Clear();
        pending = null;
        moved = 0;
        nextActionAt = 0;
        Plugin.Chat.Print($"[Move Duplicates] Moving duplicates {job.Description}...");
    }

    public void Cancel() { if (job != null) Finish("Stopped."); }

    private void Finish(string reason)
    {
        Plugin.Chat.Print($"[Move Duplicates] {reason} Moved {moved} stack(s).");
        job = null;
        pending = null;
    }

    public void Tick(IFramework _)
    {
        if (job == null) return;

        if (!IsOpen(job.Remote))
        {
            Finish("Window closed, stopped.");
            return;
        }

        var now = Environment.TickCount64;
        if (now < nextActionAt) return;

        var inv = InventoryManager.Instance();
        if (inv == null) return;

        // Wait for the previous move to land before issuing the next one.
        if (pending is { } p)
        {
            var slot = inv->GetInventoryContainer(p.Type)->GetInventorySlot(p.Slot);
            var changed = slot == null || slot->ItemId != p.ItemId || slot->Quantity != p.Qty;
            if (changed) moved++;
            else if (now - pendingSince < MoveTimeoutMs) return;
            else Plugin.Log.Warning($"Move from {p.Type}#{p.Slot} timed out; skipping.");

            pending = null;
            nextActionAt = now + job.DelayMs;
            return;
        }

        if (!TryIssueNextMove(inv))
            Finish("Done.");
    }

    private bool TryIssueNextMove(InventoryManager* inv)
    {
        var items = Plugin.Data.GetExcelSheet<Item>();

        // Index what the destination already holds, keyed by (itemId, HQ).
        var destStacks = new Dictionary<(uint, bool), List<(InventoryType Type, int Slot, int Qty)>>();
        var destEmpty = new List<(InventoryType Type, int Slot)>();
        foreach (var type in job!.Destination)
        {
            var c = inv->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null) continue;
                if (s->ItemId == 0) { destEmpty.Add((type, i)); continue; }
                var key = (s->ItemId, IsHq(s));
                if (!destStacks.TryGetValue(key, out var list)) destStacks[key] = list = new();
                list.Add((type, i, s->Quantity));
            }
        }

        foreach (var type in job.Source)
        {
            var c = inv->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0) continue;
                if (tried.Contains((type, i))) continue;

                var key = (s->ItemId, IsHq(s));
                if (!destStacks.TryGetValue(key, out var stacks)) continue;

                if (!items.TryGetRow(s->ItemId, out var row)) continue;
                var max = (int)row.StackSize;
                if (max <= 1) continue;                               // gear etc. — not a "stack" duplicate
                if (job.RequireTradable && row.IsUntradable) continue;

                tried.Add((type, i));

                // 1) an existing stack with room for everything
                // 2) an empty slot, preferring the page that already has this item
                // 3) top up an existing stack (remainder stays behind)
                (InventoryType Type, int Slot)? target = null;
                var fits = stacks.FirstOrDefault(d => max - d.Qty >= s->Quantity);
                if (fits != default) target = (fits.Type, fits.Slot);
                else if (destEmpty.Count > 0)
                {
                    var samePage = destEmpty.FindIndex(e => e.Type == stacks[0].Type);
                    target = destEmpty[samePage >= 0 ? samePage : 0];
                }
                else
                {
                    var room = stacks.FirstOrDefault(d => d.Qty < max);
                    if (room != default) target = (room.Type, room.Slot);
                }

                if (target is not { } t) continue;

                pending = (type, i, s->ItemId, s->Quantity);
                pendingSince = Environment.TickCount64;
                inv->MoveItemSlot(type, (ushort)i, t.Type, (ushort)t.Slot, true);
                return true;
            }
        }
        return false;
    }

    private static bool IsHq(InventoryItem* s) =>
        s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
}
