# FIH Ghost

Ghost mod for **Flipping is Hard** (BepInEx 6 IL2CPP). Every single-player session is
recorded automatically; your best full run, level and NPC challenge are kept as ghosts that
race next to you, and you can watch, spectate, export and import them. A practice mode lets
you place your own routes and grind them against their PB.

## Installation

1. Install [BepInEx 6 (IL2CPP)](https://github.com/BepInEx/BepInEx) into the game and run it once.
2. Download `FIHGhost.dll` from the [latest release](https://github.com/d1ll3x3/FIH_Ghost/releases)
   and copy it to `BepInEx/plugins/FIHGhost/`.
3. In a level, press `J` to open the menu.

Ghosts are saved in `BepInEx/plugins/FIHGhost/Ghosts/`.

## Controls

All keys are rebindable from the **Settings** tab of the menu (saved in `FIHGhost.cfg`).

| Key (default) | Action |
|---|---|
| `J` | Open / close the menu |
| `C` (menu open) | Switch the mouse between the menu and the camera |
| `K` | Start / stop a manual recording |
| `L` | Play the latest manual recording / stop the ghost being watched |
| `R` (practice mode) | Restart the selected route |
| `WASD`, `E` / `Q`, `Shift` (flying) | Move, up / down, faster |

While the mouse is on the menu the game's own keyboard and mouse are held, so the camera
doesn't turn and clicks don't reach the game.

## Personal bests

The mod records every single-player session on its own and cuts PBs out of it:

- **Full run**: from playtime 0 to the end of the game, also when the run is played over
  several sessions (the unfinished run is kept and continued when you load that save).
- **Levels**: from first reaching a level to reaching a higher one, like the game's splits.
- **NPC challenges**: from the start of a challenge to completing it, with the game's own time.

A PB is only replaced by a better time; the result shows on the HUD. Ghosts race you while
you play, each in its own colour: the full-run PB (blue, synced to the playtime), the
level PB (green, from the moment you reach the level) and the challenge PB (orange). Each
can be switched off in the **PBs** tab.

A quick restart starts the recording over. Teleporting from the menu stops that run from
counting as a full run.

## Menu

- **PBs**: every PB (full run, levels, challenges, practice routes) to **Play** (the ghost
  runs where it was recorded) or **Spectate** (the camera follows it while your phone waits),
  and **Export**.
- **Ghosts**: imported ghosts and manual recordings, with **Import** / **Export** through the
  Windows file dialog.
- **Levels**: teleport to the start of any level. The timer starts when you move and that
  level is timed until you reach the next one.
- **NPCs**: teleport in front of any NPC. Only its challenge is timed after this teleport.
- **Practice**: practice mode (below).
- **Settings**: keys, HUD panel, diagnostics.

The HUD panel in the bottom-left corner shows the recording state, the level and challenge
being timed against their PB, the ghost being watched and the hotkeys.

## Practice mode

Pauses the automatic recording and lets you build routes anywhere on the map:

1. **Fly** to the spot you want (no collisions while flying).
2. **Set start here** saves your phone's position and facing (put on the floor if you are
   flying); **Set end here** places the end zone around the phone, resizable with `-` / `+`.
3. **Start attempt** (`R`) teleports you to the start; the timer starts when you move and
   stops when the phone enters the end zone. Your best attempt is the route's PB ghost
   (purple), which races your next attempts.

Routes are saved per map in `Ghosts/Practice/<map>/routes.cfg`.

## Ghost files

`.fihghost` files are binary: the phone's position and rotation every physics tick (50 Hz)
plus markers for the level, challenge and teleport events. The ghost is a translucent copy
of the bare phone (no cosmetics) with no collisions. Ghosts are single-player only: the mod
stays idle in a multiplayer lobby.

## Building

Requires the .NET SDK and the game with BepInEx 6 IL2CPP run once, so its interop
assemblies exist. The project references them straight from the game folder:

```
dotnet build -c Release -p:GameDir="path\to\Flipping is Hard" -p:Deploy=true
```

`build.bat` does the same for the default Steam path.

## License

[AGPL-3.0](LICENSE)
