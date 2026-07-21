using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RogoDigital.Lipsync;
using UnityEngine;

namespace TVSCustomVoices
{
    internal static class VoiceInjector
    {
        // Public ZNEReactionSetData fields on ZNEReactionSet (the "buckets").
        private static FieldInfo[] _bucketFields;

        private static readonly Dictionary<string, AudioClip> _clipCache =
            new Dictionary<string, AudioClip>();

        // Silent clips (for muting vanilla voice), keyed by sample count so a
        // muted line keeps the original's duration and animation timing.
        private static readonly Dictionary<int, AudioClip> _silentCache =
            new Dictionary<int, AudioClip>();

        // Voices we've already logged the discovery line for.
        private static readonly HashSet<string> _announced = new HashSet<string>();

        // Bucket data instances we've already scanned for custom audio.
        private static readonly HashSet<int> _scanned = new HashSet<int>();

        // A custom line ready to be swapped in.
        private sealed class CustomLine
        {
            public AudioClip clip;
            public ZNESubtitleData subtitle; // null => keep the vanilla subtitle
            public string name;
        }

        // Custom audio available per bucket, keyed "<voice>|<bucket>".
        private static readonly Dictionary<string, List<CustomLine>> _customByBucket =
            new Dictionary<string, List<CustomLine>>();

        // Every vanilla ZNEReaction that lives in a bucket which HAS custom audio,
        // mapped to that bucket key. Reference-keyed (ZNEReaction is a plain class).
        private static readonly Dictionary<ZNEReaction, string> _reactionBucket =
            new Dictionary<ZNEReaction, string>();

        // The character voices (top-level ZNEReactionSet assets). These are the
        // ones that also ship a "safe_" variant. Pre-seeded so their full folder
        // tree exists on first launch; any other voice that actually loads is
        // scaffolded on demand in Inject().
        public static readonly string[] KnownVoices =
        {
            "YoungAdult-ReactionSet",   // default human voice
            "FelineFatale-ReactionSet", // human voice
            "Dalphine-ReactionSet",     // human voice
            "Synth-ReactionSet",        // robot/synthetic voice
        };

        // Set from the BepInEx config in Plugin.Awake.
        public static bool ScaffoldEnabled = true;

        // When true, vanilla voice never plays: buckets with custom audio always
        // swap to a custom line, and every other vanilla voice line is muted.
        public static bool CustomVoicesOnly = false;

        // Chance (0-100) that a reaction with custom audio plays a custom line
        // instead of the vanilla one. Ignored when CustomVoicesOnly is true (100%).
        public static int SwapChancePercent = 50;

        private static void EnsureBucketFields()
        {
            if (_bucketFields != null) return;
            _bucketFields = typeof(ZNEReactionSet)
                .GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => f.FieldType == typeof(ZNEReactionSetData))
                .ToArray();
        }

        // Create every <voice>/<bucket>/ folder so users can see exactly where
        // audio can go. Idempotent — never deletes or overwrites anything.
        public static int EnsureVoiceFolders(string voiceName)
        {
            EnsureBucketFields();
            int made = 0;
            foreach (var field in _bucketFields)
            {
                string dir = Path.Combine(Plugin.VoicesRoot, voiceName, field.Name);
                if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); made++; }
            }
            return made;
        }

        public static void GenerateScaffold()
        {
            EnsureBucketFields();
            int made = 0;
            foreach (var voice in KnownVoices) made += EnsureVoiceFolders(voice);
            Plugin.Log.LogInfo(
                $"[CustomVoices] Folder scaffold ready: {KnownVoices.Length} voices x {_bucketFields.Length} buckets " +
                $"({made} new folders) under {Plugin.VoicesRoot}");
        }

        // Fires when a voice is applied to a character. We don't append anything to
        // the reaction arrays anymore — instead we index which custom audio exists
        // for each bucket and remember every vanilla reaction in those buckets, so
        // performSpecificReaction can swap audio at play time (keeping the vanilla
        // face pose / phonemes / emotion / gesture).
        public static void Inject(ZNEReactionSet set)
        {
            EnsureBucketFields();

            string voiceName = set.name; // == Resources asset name == folder name
            string voiceDir = Path.Combine(Plugin.VoicesRoot, voiceName);

            if (ScaffoldEnabled) EnsureVoiceFolders(voiceName);

            if (_announced.Add(voiceName))
            {
                Plugin.Log.LogInfo(
                    $"[CustomVoices] Voice '{voiceName}' in use. Add lines under: {voiceDir}{Path.DirectorySeparatorChar}<bucket>{Path.DirectorySeparatorChar}*.wav");
            }

            if (!Directory.Exists(voiceDir)) return;

            int bucketsWithAudio = 0, linesIndexed = 0;
            foreach (var field in _bucketFields)
            {
                if (!(field.GetValue(set) is ZNEReactionSetData data) || data == null) continue;
                if (!_scanned.Add(data.GetInstanceID())) continue; // already indexed

                string bucketDir = Path.Combine(voiceDir, field.Name);
                if (!Directory.Exists(bucketDir)) continue;

                var lines = new List<CustomLine>();
                foreach (var wav in Directory.GetFiles(bucketDir, "*.wav"))
                {
                    var clip = LoadClip(wav);
                    if (clip == null) continue;
                    lines.Add(new CustomLine
                    {
                        clip = clip,
                        subtitle = BuildSubtitle(wav, clip.length),
                        name = Path.GetFileNameWithoutExtension(wav),
                    });
                }
                if (lines.Count == 0) continue;

                string key = voiceName + "|" + field.Name;
                _customByBucket[key] = lines;
                bucketsWithAudio++;
                linesIndexed += lines.Count;

                // Remember every vanilla reaction in this bucket so we can catch it
                // at play time and swap its audio.
                if (data.reactions != null)
                    foreach (var r in data.reactions)
                        if (r != null) _reactionBucket[r] = key;

                Plugin.Log.LogInfo(
                    $"[CustomVoices] {lines.Count} custom line(s) ready for {voiceName}/{field.Name}.");
            }

            if (bucketsWithAudio > 0)
                Plugin.Log.LogInfo(
                    $"[CustomVoices] Indexed {linesIndexed} custom line(s) across {bucketsWithAudio} bucket(s) for '{voiceName}'. " +
                    $"Mode: {(CustomVoicesOnly ? "custom-only" : SwapChancePercent + "% swap chance")}.");
        }

        // ---- Play-time swap ----------------------------------------------------

        // Called from the performSpecificReaction Harmony prefix. May replace the
        // reaction with a clone whose audio is a custom line (or silence).
        public static void OnBeforeReaction(ref ZNEReaction reaction)
        {
            if (reaction == null) return;

            if (_reactionBucket.TryGetValue(reaction, out var key) &&
                _customByBucket.TryGetValue(key, out var lines) && lines.Count > 0)
            {
                bool swap = CustomVoicesOnly || RollPercent(SwapChancePercent);
                if (swap)
                    reaction = BuildSwapped(reaction, PickRandom(lines));
                // else: let the vanilla line play (only possible when !CustomVoicesOnly).
                return;
            }

            // No custom audio for this reaction's bucket. In custom-only mode we
            // still silence any vanilla voice it would produce (keeping animation).
            if (CustomVoicesOnly && HasVoice(reaction))
                reaction = BuildMuted(reaction);
        }

        private static bool HasVoice(ZNEReaction r)
        {
            return (r.lipSyncReaction != null && r.lipSyncReaction.clip != null) ||
                   r.audioReaction != null;
        }

        private static bool RollPercent(int percent)
        {
            if (percent >= 100) return true;
            if (percent <= 0) return false;
            return UnityEngine.Random.Range(0, 100) < percent;
        }

        private static CustomLine PickRandom(List<CustomLine> lines)
        {
            return lines[UnityEngine.Random.Range(0, lines.Count)];
        }

        // Clone the vanilla reaction, swapping its audio for our custom clip while
        // keeping its face pose, phonemes, emotion and gesture data.
        private static ZNEReaction BuildSwapped(ZNEReaction vanilla, CustomLine line)
        {
            var clone = CloneReaction(vanilla);

            if (vanilla.lipSyncReaction != null && vanilla.lipSyncReaction.clip != null)
            {
                // Dialogue line: reuse the vanilla LipSyncData's markers (phonemes,
                // emotions, gestures) verbatim, only replacing the audio clip.
                clone.lipSyncReaction = CloneLipSyncWithClip(vanilla.lipSyncReaction, line.clip);
                clone.audioReaction = null; // avoid a second (gasp) audio channel
            }
            else if (vanilla.audioReaction != null)
            {
                // Gasp/grunt line: the face pose plays this clip directly.
                clone.audioReaction = line.clip;
            }
            else
            {
                // Expression-only reaction (Positive/Mixed/Negative/Tickle/None with
                // no audio). Give it a voice via the lip-sync path; keep the pose.
                clone.lipSyncReaction = BuildBareLipSync(line.clip);
            }

            if (line.subtitle != null) clone.subtitleData = line.subtitle;
            return clone;
        }

        // Clone the reaction but replace whatever audio it carries with silence of
        // the same length, so the face pose / animation still plays but no vanilla
        // voice is heard.
        private static ZNEReaction BuildMuted(ZNEReaction vanilla)
        {
            var clone = CloneReaction(vanilla);
            if (vanilla.lipSyncReaction != null && vanilla.lipSyncReaction.clip != null)
                clone.lipSyncReaction = CloneLipSyncWithClip(
                    vanilla.lipSyncReaction, SilentLike(vanilla.lipSyncReaction.clip));
            if (vanilla.audioReaction != null)
                clone.audioReaction = SilentLike(vanilla.audioReaction);
            return clone;
        }

        private static ZNEReaction CloneReaction(ZNEReaction src)
        {
            return new ZNEReaction
            {
                reactionName = src.reactionName,
                lipSyncReaction = src.lipSyncReaction,
                audioReaction = src.audioReaction,
                faceReactionAnimation = src.faceReactionAnimation,
                bypassShouldNotReact = src.bypassShouldNotReact,
                priority = src.priority,
                subtitleData = src.subtitleData,
            };
        }

        // A copy of the vanilla LipSyncData carrying a different clip. Marker arrays
        // are reused by reference (the game only reads them), so the mouth / emotion
        // / gesture animation is exactly the vanilla one — "copy vanilla phonemes".
        private static LipSyncData CloneLipSyncWithClip(LipSyncData src, AudioClip clip)
        {
            var data = ScriptableObject.CreateInstance<LipSyncData>();
            data.clip = clip;
            data.length = src.length; // keep vanilla timing so markers line up
            data.version = src.version;
            data.isPreprocessed = src.isPreprocessed;
            data.transcript = src.transcript;
            data.phonemeData = src.phonemeData;
            data.emotionData = src.emotionData;
            data.gestureData = src.gestureData;
            return data;
        }

        // A LipSyncData that just carries a clip (single intensity-0 "Rest" phoneme
        // so LipSync.LoadData accepts it). Used for expression-only reactions.
        private static LipSyncData BuildBareLipSync(AudioClip clip)
        {
            var data = ScriptableObject.CreateInstance<LipSyncData>();
            data.clip = clip;
            data.length = clip.length;
            data.version = 1f;
            data.isPreprocessed = false;
            data.transcript = string.Empty;
            data.phonemeData = new[]
            {
                new PhonemeMarker((int)Phoneme.Rest, 0f, 0f, false) { phoneme = Phoneme.Rest, intensity = 0f }
            };
            data.emotionData = Array.Empty<EmotionMarker>();
            data.gestureData = Array.Empty<GestureMarker>();
            return data;
        }

        // A silent clip matching another clip's shape, so a muted line keeps the
        // original duration (and thus animation timing).
        private static AudioClip SilentLike(AudioClip src)
        {
            int samples = Mathf.Max(1, src.samples);
            if (_silentCache.TryGetValue(samples, out var cached) && cached != null)
                return cached;
            var silent = AudioClip.Create("cv_silent_" + samples, samples,
                Mathf.Max(1, src.channels), Mathf.Max(1000, src.frequency), false);
            _silentCache[samples] = silent; // data defaults to zeros = silence
            return silent;
        }

        private static AudioClip LoadClip(string path)
        {
            if (_clipCache.TryGetValue(path, out var cached) && cached != null) return cached;
            try
            {
                var clip = WavLoader.Load(path);
                _clipCache[path] = clip;
                return clip;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[CustomVoices] failed to load WAV '{path}': {e.Message}");
                return null;
            }
        }

        // Subtitle comes from a sibling .txt file (same name as the .wav). Returns
        // null when there's no .txt, so the caller keeps the vanilla subtitle.
        private static ZNESubtitleData BuildSubtitle(string wavPath, float clipLength)
        {
            string txtPath = Path.ChangeExtension(wavPath, ".txt");
            if (!File.Exists(txtPath)) return null;

            string text = File.ReadAllText(txtPath).Trim();
            if (text.Length == 0) return null;

            var sd = new ZNESubtitleData { subtitles = new List<ZNESubtitleData.SubtitleLine>() };
            float onScreen = Mathf.Max(1.5f, clipLength);
            sd.subtitles.Add(new ZNESubtitleData.SubtitleLine(text, onScreen, false));
            return sd;
        }

        // ---- Debug hotkeys -----------------------------------------------------

        private static ZNECharacterReactionController FindController()
        {
            var ctrl = UnityEngine.Object.FindObjectOfType<ZNECharacterReactionController>();
            if (ctrl == null)
                Plugin.Log.LogWarning("[CustomVoices] No character in the scene yet — load a scene with a character first.");
            return ctrl;
        }

        // '+': force-play one of YOUR custom lines for the active voice, bypassing
        // gating and the swap chance. Confirms audio + subtitle end-to-end.
        public static void DebugPlayInjected()
        {
            var ctrl = FindController();
            if (ctrl == null) return;
            var set = GameCompat.GetReactionSet(ctrl);
            if (set == null) { Plugin.Log.LogWarning("[CustomVoices] Character has no reactionSet."); return; }

            string prefix = set.name + "|";
            foreach (var kv in _customByBucket)
            {
                if (!kv.Key.StartsWith(prefix) || kv.Value.Count == 0) continue;
                var line = kv.Value[0];
                var reaction = new ZNEReaction
                {
                    reactionName = "CV_debug_" + line.name,
                    lipSyncReaction = BuildBareLipSync(line.clip),
                    faceReactionAnimation = ZNEReactionType.None,
                    subtitleData = line.subtitle,
                    priority = 50,
                };
                Plugin.Log.LogInfo($"[CustomVoices] +: playing '{line.name}' from {kv.Key} (len={line.clip.length:0.00}s).");
                GameCompat.PerformSpecificReaction(ctrl, reaction, set.name);
                return;
            }
            Plugin.Log.LogWarning(
                $"[CustomVoices] +: no custom line found for voice '{set.name}'. " +
                $"Add WAVs under CustomVoices/{set.name}/<bucket>/ and RESTART the game.");
        }

        // '#': play a random idle line (vanilla or, via the swap prefix, custom) and
        // report which mood bucket is active on this character.
        public static void DebugPlayRandomIdle()
        {
            EnsureBucketFields();
            var ctrl = FindController();
            if (ctrl == null) return;
            var set = GameCompat.GetReactionSet(ctrl);
            if (set == null) { Plugin.Log.LogWarning("[CustomVoices] Character has no reactionSet."); return; }

            foreach (var field in _bucketFields)
            {
                if (!field.Name.EndsWith("IdleReactions")) continue;
                if (!(field.GetValue(set) is ZNEReactionSetData data) || data?.reactions == null || data.reactions.Length == 0) continue;
                Plugin.Log.LogInfo($"[CustomVoices] #: playing a random line from {field.Name} ({data.reactions.Length} total).");
                GameCompat.PerformRandomReactionFrom(ctrl, data, set.name);
                return;
            }
            Plugin.Log.LogWarning("[CustomVoices] #: no non-empty idle bucket found on this character.");
        }
    }
}
