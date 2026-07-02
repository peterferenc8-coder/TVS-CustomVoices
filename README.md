# TVS Custom Voices

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for **The Villain Simulator**
that lets you add your own voicelines (audio + optional subtitles) by dropping
`.wav` files into folders. No game files are modified — lines are injected at
runtime, so it's fully reversible and survives game updates.

- Works in desktop and VR mode.
- Auto-generates a folder tree for every voice/situation so you can see exactly
  where audio can go.
- Optional per-line subtitles.

---

## Installation

### 1. Install BepInEx (one time)

This mod needs **BepInEx 5.4.x, x64, Mono build** (The Villain Simulator is a
Unity Mono game).

1. Download `BepInEx_win_x64_5.4.x.zip` from the
   [BepInEx releases](https://github.com/BepInEx/BepInEx/releases).
2. Extract it into your game folder — the one containing
   `TheVillainSimulator.exe`. You should now have `winhttp.dll` and a `BepInEx/`
   folder next to the exe.
3. Launch the game once, then quit. This makes BepInEx generate its
   `BepInEx/plugins/` folder.

> **Steam / Proton (Linux):** add a launch option so the BepInEx loader is picked
> up: `WINEDLLOVERRIDES="winhttp=n,b" %command%`

### 2. Install this mod

1. Download `TVS-CustomVoices-vX.Y.Z.zip` from this repo's
   **[Releases](../../releases)**.
2. Extract it into your game folder (same place as `TheVillainSimulator.exe`).
   It places `CustomVoices.dll` into `BepInEx/plugins/`.
   *(Or just copy `CustomVoices.dll` into `BepInEx/plugins/` manually.)*
3. Launch the game. On startup the plugin creates the folder tree under
   `BepInEx/plugins/CustomVoices/`.

That's it — you're ready to add voicelines.

---

## Adding voicelines

### Where files go

```
BepInEx/plugins/CustomVoices/
  <VoiceName>/
    <bucket>/
      myline.wav      <- your audio
      myline.txt      <- OPTIONAL subtitle text (same base name as the .wav)
```

The folders are created for you — just drop files into the one that fits.

### The voices

| Folder | Who |
|---|---|
| `YoungAdult-ReactionSet`   | default human voice |
| `FelineFatale-ReactionSet` | human voice |
| `Dalphine-ReactionSet`     | human voice |
| `Synth-ReactionSet`        | robot / synthetic voice |

The game also has `safe_` variants (e.g. `safe_YoungAdult-ReactionSet`) used in
some modes; those folders are created automatically the first time such a voice
loads. The log line `Voice '...' in use` always tells you which voice is active.

### The buckets (what triggers a line)

Bucket names are `<mood><Situation>`. The **mood** matches the character's
emotional state — to make a line play regardless of mood, drop a copy into each
mood's version of the same situation.

- **Moods:** `dominant`, `scared`, `weak`, `aroused`, `submission` (plus `gag…`,
  `neutral…`, `gagged…` for gagged/tickle states).
- **Common situations:** `IdleReactions` (idle chatter — best for "just add more
  talking"), `GaspReactions`, `LightDamageReactions`, `DamageReactions`,
  `HandInteractReactions`, `PickupToolReactions`, `…DickRevealReactions`,
  `CostumeRemove…Reactions`, `InsertionResistance…`.
- **Standalone:** `orgasmLevelOne`…`orgasmLevelFour`, `…CumSplashReactions`,
  `peeReaction`, `postChokeReactions`, `opinionReactions`, `jointStressReactions`,
  `extremeDamageReactions`, and the `…LaughTickle…` / `…TickleStruggle…` set.

Every bucket has its own folder (~100 per voice) — browse the tree to see them all.

### Audio & subtitles

- **Audio:** WAV only. PCM 8/16/24/32-bit or 32-bit float, mono or stereo;
  **16-bit PCM mono** is the safe default. Convert with ffmpeg:
  ```
  ffmpeg -i input.mp3 -ac 1 -ar 44100 -c:a pcm_s16le myline.wav
  ```
- **Subtitle:** a `.txt` with the same base name as the `.wav`
  (`myline.wav` → `myline.txt`) containing the caption. Omit it for audio-only.
- **Multiple lines:** drop several `.wav`s into one bucket — the game randomly
  picks among all of them (yours + the originals).

### Apply & test

1. Add your files, then **restart the game** (clips load when a voice is first
   applied).
2. `BepInEx/LogOutput.log` will show `+N line(s) -> <Voice>/<bucket>`.
3. In a scene with that character, debug keys:
   - **`+`** — force-play one of *your* injected lines (instant).
   - **`#`** — play a random idle line (vanilla or custom).

---

## Configuration

`BepInEx/config/com.peter.tvs.customvoices.cfg`:

- `GenerateFolderScaffold` (default `true`) — create the empty folder tree on
  startup. Set `false` if you don't want the folders.

---

## Limitations

- No lip-sync: the mouth doesn't move for custom lines (audio + subtitle only).
- Adding files requires a game restart.

## Uninstall

Delete `CustomVoices.dll` from `BepInEx/plugins/` (and its `CustomVoices/`
folder). To remove BepInEx too, delete `winhttp.dll`, `doorstop_config.ini`, and
the `BepInEx/` folder from the game directory.

---

## Building from source

Requires the .NET SDK and a game install that has BepInEx (for the reference
assemblies — they are not redistributed here).

```
dotnet build src/CustomVoices.csproj -c Release -p:GameDir="/path/to/TheVillainSimulator-48a"
```

(Or set the `TVS_GAME_DIR` environment variable instead of `-p:GameDir`.) The
output `CustomVoices.dll` is written to `src/bin/Release/`, and also copied into
the game's `BepInEx/plugins/` if that folder exists.

## How it works

A "voice" is a `ZNEReactionSet` scriptable object holding ~100
`ZNEReactionSetData` buckets, each with an array of reactions the game picks from
at random. A Harmony patch on the reaction-set setter appends `ZNEReaction`
entries (built from your WAVs, played via the game's lip-sync audio path, with
subtitles via `ZNESubtitleManager`) whenever a voice is applied to a character.

## License

[MIT](LICENSE).
