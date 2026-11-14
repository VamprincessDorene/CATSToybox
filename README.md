# CATS Toybox

A Dalamud plugin for FINAL FANTASY XIV that bundles all the CATS toys into one plugin. Type `/toybox` to open it. Each toy has its own tab, and you can switch any toy off from the **Home** tab.

| Toy | What it does | Commands |
|---|---|---|
| **Flip Cup** | Play flip cup with friends on a counter in your house. | `/flipcup`, `/flipcup join CODE`, `/flipcup leave`, `/flipcup reset` |
| **SignSpeak** | Type a message and your character fingerspells it in ASL. Needs Simple Heels (LivePose) or Customize+. | `/sign hello`, `/signspeak`, `/sign stop` |
| **Cursed Identify** | During `/study`, the cursed smallclothes lock onto you and come off when the skit resets. Needs Glamourer and the Cursed Identify mod. | `/cursed`, `/cursed on`, `/cursed off`, `/cursed toggle` |
| **Visor Flash** | `/visor` flashes the Charm Shirt Halter mod between Normal and Flash. Needs the Charm Shirt Halter mod (v1.7.0+). | `/visor`, `/visorflash` |
| **Move Duplicates** | Right-click an item with a retainer or the FC chest open and choose **Move Duplicates** to move every stack the other side already has. | right-click menu |

## Install

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL, tick **Enabled**, and click **Save**:

   ```
   https://raw.githubusercontent.com/VamprincessDorene/CATSToybox/main/repo.json
   ```

3. Type `/xlplugins`, search for **CATS Toybox**, and click **Install**.

If you had the standalone Flip Cup, SignSpeak, Cursed Identify, Visor Flash or Move Duplicates plugins installed, uninstall them first, because they use the same commands. Your settings from the old plugins are copied into the Toybox the first time it runs.

## Move Duplicates in detail

- Right-click an item **in your inventory** with a retainer open: stacks the retainer already has are moved to the retainer. With the FC chest open, they go to the FC chest instead (untradable items are skipped).
- Right-click an item **in a retainer**: stacks you already have come back to your inventory.
- Right-click an item **in an FC chest tab**: stacks from that tab that you already have come back to your inventory.
- Only stackable items count, and NQ/HQ are kept apart. Items top up the existing stack first, then go to an empty slot.
- FC chest tabs only count once you've opened them, because the game doesn't load a tab until you view it.

## Building and publishing

Requires the .NET SDK and XIVLauncher with Dalamud installed.

- Build: `dotnet build -c Release`
- Publish a new version: bump `<Version>` in `CATSToybox.csproj`, then run `.\publish.ps1`. Dalamud only offers an update when the version goes up.

Each toy's code lives in `Modules\<Toy>\`. To add a new toy, put its code in a new folder there and register it in `Plugin.cs` with a `ToyDef`.
