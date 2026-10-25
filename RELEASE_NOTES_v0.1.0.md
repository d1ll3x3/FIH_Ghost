# FIH Ghost v0.1.0

## What's New

### Automatic Recording & Personal Bests
- **Automatic Run Recording**: Added recording of every single-player session at 50 Hz, with level, challenge, teleport and game-end markers polled from the game state.
- **Full Run PB**: Added a full-run PB cut from playtime 0 to the end of the game, kept across sessions of the same save through an unfinished-run file that is trimmed to the loaded playtime.
- **Level PBs**: Added a PB per level timed from first reaching it to reaching a higher one, the same rule as the game's splits.
- **Challenge PBs**: Added a PB per NPC challenge timed with the game's own completion time (`LastCompletedTime`), ignoring challenges restored mid-attempt from a save.
- **Quick Restart**: Added a new recording on quick restart through Harmony prefixes on `GameEndManager.RequestRestartGame` and `RequestFullSoloRestart`.

### Ghosts & Playback
- **PB Racing**: Added the full-run (synced to the playtime), level and challenge PB ghosts racing next to the player at the same time, each in its own colour and switchable in the PBs tab.
- **Phone Ghost**: Introduced a translucent copy of the bare phone with every script, collider, rigidbody and cosmetic removed.
- **Spectate Mode**: Added a playback mode where `CameraManager.SetTrackingObject` follows the ghost while the player's phone stays frozen.
- **Export / Import**: Added Export and Import buttons that open the Windows file dialog, with imported ghosts copied to `Ghosts/Imported`.

### Teleports
- **Level Teleports**: Added teleports to every level start, with the level timer starting on the first input after every key is released.
- **NPC Teleports**: Added teleports in front of every NPC, after which only that NPC's challenge is timed.
- **Boss Door**: Placed the L10 and Narrator targets in front of the boss door, preventing the fight's fail-safe respawn triggered by landing inside it.
- **Full Run Guard**: Disabled the full-run PB for any run with a menu teleport.

### Practice Mode
- **Practice Routes**: Added routes per map with a start pose and a resizable end zone, saved in `Ghosts/Practice/<map>/routes.cfg`.
- **Fly**: Added a free-fly mode ported from the Map Editor (WASD, `E` / `Q`, `Shift`) to place route points.
- **Route Attempts**: Added attempts that restart with `R`, time from the first input to the end zone and keep the best one as the route's PB ghost.
- **Recording Pause**: Disabled the automatic run recording while practice mode is on.

### Menu & HUD
- **Tabbed Menu**: Added a menu (`J`) with PBs, Ghosts, Levels, NPCs, Practice and Settings tabs, drawn with the Map Editor's theme.
- **Camera Lock**: Disabled the game's keyboard and mouse while the menu has the mouse, with `C` handing the mouse back to the camera.
- **Rebindable Keys**: Added in-menu rebinding of every hotkey, saved in `FIHGhost.cfg`.
- **HUD Panel**: Added a bottom-left panel with the recording state, the splits being timed against their PB, the ghost being watched and the hotkeys.

### Architecture & Core Refactor
- `.fihghost` format v2: header with map, difficulty, category, category id, final time and world guid; fixed-size samples so file lists read only the header and the first and last sample.
- No Harmony patches besides the two restart hooks, both checked in `fih-address-map.json` to have their own method body.
- Theme, window widgets, file dialog, line boxes and fly controller are copies of the Map Editor's code.

## Installation
1. Install BepInEx 6 (IL2CPP) and launch the game once.
2. Copy FIHGhost.dll into BepInEx/plugins/FIHGhost/.
3. Press J inside a level to open the FIH Ghost menu.
