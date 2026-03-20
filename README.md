# Football Simulator Accessibility Mod

A BepInEx plugin that makes Football Simulator fully accessible for blind users via NVDA screen reader (or Windows SAPI as a fallback). All menus, play selection, in-game events, and receiver status are announced automatically as you play.

---

## Features

### Menu Accessibility
All menus are read aloud as you navigate them, including:
- Main menu, exhibition setup, team selection
- Roster management (player names, positions, ratings)
- Season mode and franchise mode screens
- Settings and How to Play

### Playbook Reader
- Formation names are announced when browsing the formation list
- All three play names on a page are announced when the play selection screen opens
- The confirmed play name is spoken when you make your selection
- Down and distance is spoken each time the play call screen opens

### In-Game Announcements
- **Down and distance** — announced every time it changes (e.g., "1st and 10", "3rd and 4")
- **Quarter** — announced at each quarter change
- **Score** — announced after every scoring play with team names and totals
- **Play name confirmation** — the selected play is confirmed when chosen

### Pre-Snap Route Reading
When your players walk to the line of scrimmage, the route assignment for each receiver is announced automatically:

> "Routes. WR1: Slant Route. WR2: Go Route. TE: Short Out. HB: Lead Block. FB: Block."

Press **L1 / Left Bumper** at any time while at the line to hear the routes again.

### Receiver Status on Pass Plays
When the QB drops back on a passing play, only **open receivers are announced by their throwing button**:

> "A. X."

Press that button to throw. Covered receivers are not spoken. If a covered receiver breaks open during the play, their button name is announced at that moment.

### On-Demand Status Readout
Press **R3 (right stick click)** at any time during the game to hear a full status summary:

> "Second quarter. 1:45 remaining. Eagles 14, Cowboys 7. 3rd and 8. Eagles 2, Cowboys 1 timeouts."

### Audibles
When you open the audibles screen at the line of scrimmage, the three available plays are announced with their button labels:

> "Audibles. X: HB Toss. A: FB Trap. B: PA Boot."

After selecting an audible and accepting the new play, the routes for that play are announced automatically.

---

## Requirements

- Football Simulator (Steam, Early Access)
- BepInEx 5.4.x — **Mono version, NOT BepInEx 6**
- NVDA screen reader (recommended) — or Windows SAPI will be used automatically as a fallback
- Xbox One or PS4/PS5 controller (the game requires a controller)

---

## Installation

### Step 1: Install BepInEx 5

1. Go to the [BepInEx GitHub releases page](https://github.com/BepInEx/BepInEx/releases)
2. Download `BepInEx_win_x64_5.4.xx.zip` — make sure it is the **5.x Mono** version
3. Extract ALL contents directly into your game folder:
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Football Simulator\
   ```
4. Launch Football Simulator once via Steam, then close it — this lets BepInEx generate its configuration folders

### Step 2: Install the Mod

The release zip contains two files. Copy both into your BepInEx plugins folder:

```
C:\Program Files (x86)\Steam\steamapps\common\Football Simulator\BepInEx\plugins\
```

- `FootballAccessMod.dll` — the mod itself
- `nvdaControllerClient.dll` — routes speech through NVDA directly for faster, more reliable output

If NVDA is not running, the mod falls back to Windows SAPI Text-to-Speech automatically.

### Step 3: Launch

Start Football Simulator normally through Steam. Within a few seconds of the title screen you should hear:

> "Football Simulator accessibility mod loaded."

---

## How It Works

The mod uses Unity reflection to read game state directly from memory each frame — no game files are modified. All speech is routed through NVDA's controller client API (if present) or Windows SAPI.

Speech is triggered by state changes, not timers, so you only hear announcements when something actually changes or when you explicitly request a repeat.

---

## Troubleshooting

**Nothing is spoken at startup**
- Confirm BepInEx is installed correctly — the `BepInEx` folder should exist inside the game folder with a `plugins` subfolder
- Confirm `FootballAccessMod.dll` is in `BepInEx\plugins\`
- Check `BepInEx\LogOutput.log` for error messages

**Speech is slow or uses the wrong voice**
- Install `nvdaControllerClient64.dll` as described in Step 2
- Without it, Windows SAPI is used, which may be slower and use a different voice than NVDA

**Routes are not announced at pre-snap**
- Route assignments come from the play's data; if all routes show as blank, the play may use generic blocking assignments
- Press L1 at the line to attempt a manual re-read

**Audibles are not announced**
- Audibles are announced when the audibles screen first opens each time, not on repeat opens
- If the audibles screen opens but nothing is spoken, check `c:\football\speech_log.txt` for clues

---

## Debug Tools

These are for contributors and troubleshooting — not needed for normal play.

| Key | Action |
|-----|--------|
| F11 (in-game) | Dumps the current UI hierarchy to `c:\football\scene_dump.txt` |
| F12 (in-game) | Dumps all game assembly class names and fields to `BepInEx\logs\GameDiscovery.txt` |

Speech events are also logged to `c:\football\speech_log.txt`.

---

## Known Limitations

- **Defense**: Routes are announced on defense as well as offense. Full defensive accessibility — ball carrier tracking, tackle alerts, interception cues — is the primary focus of the next version.
- **No hot routes**: Football Simulator does not have individual route adjustments at the line. To change your play at the line, use Audibles (Y / Triangle) to switch to a different play entirely.

---

## Roadmap

See [ROADMAP.md](ROADMAP.md) for planned features, including full defensive accessibility, interception detection, running play assists, and more.

---

## Controller Guide

See [CONTROLLER_GUIDE.md](CONTROLLER_GUIDE.md) for the full control scheme and guidance on playing with the accessibility mod.
