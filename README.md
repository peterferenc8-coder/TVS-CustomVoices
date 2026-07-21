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
- **Multiple lines:** drop several `.wav`s into one bucket — when a custom line is
  chosen (see below), one of your clips for that bucket is picked at random.

### How a custom line gets chosen

Custom lines don't add new triggers — they **ride the game's existing
reactions**. Whenever the game plays a reaction from a bucket you've dubbed, the
mod swaps in your audio *on top of the vanilla face pose, emotion, gesture and
lip-sync*, so your voice plays with the real animation. Two modes control how
often that happens (see [Configuration](#configuration)):

- **`SwapChancePercent`** (default `50`) — each time such a reaction fires,
  there's this % chance you hear your line, otherwise the vanilla one.
- **`CustomVoicesOnly`** (default `false`) — when on, your line *always* wins for
  buckets you've dubbed, and **every other vanilla voice line is muted** (its
  face/body animation still plays, just silently). Result: only your voice is
  ever heard.

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

- `SwapChancePercent` (default `50`) — chance (0–100) that a line you've dubbed
  plays your audio instead of the vanilla one. Only used when `CustomVoicesOnly`
  is `false`.
- `CustomVoicesOnly` (default `false`) — when `true`, the original heroine voice
  is never heard: any line you've dubbed always plays your audio, and every other
  vanilla voice line is muted (the face/body animation still plays). Overrides
  `SwapChancePercent`.
- `GenerateFolderScaffold` (default `true`) — create the empty folder tree on
  startup. Set `false` if you don't want the folders.

---

## Limitations

- **Lip-sync isn't matched to your words.** Custom lines ride the vanilla
  reaction's animation, so you get the correct face pose, emotion, gesture and
  mouth movement — but the mouth phonemes are timed to the *original* clip, not
  yours. The mouth moves, it just won't line up with your syllables.
- **No new triggers.** Custom lines only play where the game already plays a
  reaction from that bucket — you're replacing voices, not adding new events. A
  bucket the game never fires (or one you left empty) is never heard.
- Adding or changing files requires a game restart.

## Uninstall

Delete `CustomVoices.dll` from `BepInEx/plugins/` (and its `CustomVoices/`
folder). To remove BepInEx too, delete `winhttp.dll`, `doorstop_config.ini`, and
the `BepInEx/` folder from the game directory.

---

## Building from source

Requires the .NET SDK and a game install that has BepInEx (for the reference
assemblies — they are not redistributed here). There is no default path baked
into the project; point `GameDir` at *your* install, in any of these ways:

```
# 1. one-off
dotnet build src/CustomVoices.csproj -c Release -p:GameDir="/path/to/TheVillainSimulator"

# 2. persistent, per machine (Local.props is gitignored)
cp src/Local.props.example src/Local.props   # then edit GameDir in it
dotnet build src/CustomVoices.csproj -c Release

# 3. environment variable
export TVS_GAME_DIR="/path/to/TheVillainSimulator"
dotnet build src/CustomVoices.csproj -c Release
```

`GameDir` is the folder containing `TheVillainSimulator.exe`. If it's missing or
wrong the build stops with an explanation instead of a wall of "type not found".

The output `CustomVoices.dll` is written to `src/bin/Release/`, and also copied
into that install's `BepInEx/plugins/` — pass `-p:Deploy=false` to skip the copy.

## Game version compatibility

The game's reaction API changes between builds — for example
`performSpecificReaction` gained a third parameter (`arousalException`) at some
point. The mod therefore resolves every game method it touches **by name at
runtime** and adapts to whatever parameter list your build declares, instead of
binding to a fixed signature.

If something still doesn't match, the mod logs what it found and keeps running
with voice swapping disabled, rather than failing to load. Look for these lines
in `BepInEx/LogOutput.log`:

```
[CustomVoices] Game API: performSpecificReaction=(ZNEReaction, Boolean, Boolean), ...
[CustomVoices] Unsupported game build. ZNECharacterReactionController exposes: ...
```

The second block lists your build's actual signatures — include it when
reporting an incompatibility.

## How it works

A "voice" is a `ZNEReactionSet` scriptable object holding ~100
`ZNEReactionSetData` buckets, each with an array of `ZNEReaction`s the game picks
from at random. Every reaction bundles a face pose, emotion/gesture/lip-sync
animation, audio and a subtitle.

The mod doesn't add reactions. A Harmony patch on the reaction-set setter indexes
which buckets have custom WAVs and which vanilla reactions live in them. A second
patch on `performSpecificReaction` then intercepts each reaction as it plays and,
per `SwapChancePercent` / `CustomVoicesOnly`, clones it with your audio swapped in
(reusing the vanilla phoneme/emotion/gesture markers and face pose) — or, in
custom-only mode with no custom line for that bucket, with the audio replaced by
silence so no vanilla voice is heard.

## License

[MIT](LICENSE).
