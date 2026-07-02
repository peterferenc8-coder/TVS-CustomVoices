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

        // Underlying bucket assets we've already appended to. ZNEReactionSet is
        // a cached Resources asset, so the setter fires repeatedly with the same
        // instance; this stops us appending the same clips more than once.
        private static readonly HashSet<int> _injectedBuckets = new HashSet<int>();

        private static readonly Dictionary<string, AudioClip> _clipCache =
            new Dictionary<string, AudioClip>();

        // Voices we've already logged the discovery line for.
        private static readonly HashSet<string> _announced = new HashSet<string>();

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

        public static void Inject(ZNEReactionSet set)
        {
            EnsureBucketFields();

            string voiceName = set.name; // == Resources asset name == folder name
            string voiceDir = Path.Combine(Plugin.VoicesRoot, voiceName);

            // Make sure this voice's folders exist even if it wasn't pre-seeded
            // (e.g. a "safe_" variant), so the user always has a place to drop audio.
            if (ScaffoldEnabled) EnsureVoiceFolders(voiceName);

            if (_announced.Add(voiceName))
            {
                Plugin.Log.LogInfo(
                    $"[CustomVoices] Voice '{voiceName}' in use. Add lines under: {voiceDir}{Path.DirectorySeparatorChar}<bucket>{Path.DirectorySeparatorChar}*.wav");
                Plugin.Log.LogInfo("[CustomVoices] Buckets: " +
                    string.Join(", ", _bucketFields.Select(f => f.Name)));
            }

            if (!Directory.Exists(voiceDir)) return;

            int totalAdded = 0;
            foreach (var field in _bucketFields)
            {
                string bucketDir = Path.Combine(voiceDir, field.Name);
                if (!Directory.Exists(bucketDir)) continue;

                if (!(field.GetValue(set) is ZNEReactionSetData data) || data == null) continue;
                if (!_injectedBuckets.Add(data.GetInstanceID())) continue; // already done

                var extra = new List<ZNEReaction>();
                foreach (var wav in Directory.GetFiles(bucketDir, "*.wav"))
                {
                    var clip = LoadClip(wav);
                    if (clip == null) continue;
                    extra.Add(BuildReaction(field.Name, wav, clip));
                }

                if (extra.Count == 0) continue;

                var merged = new List<ZNEReaction>(data.reactions ?? Array.Empty<ZNEReaction>());
                merged.AddRange(extra);
                data.reactions = merged.ToArray();
                totalAdded += extra.Count;
                Plugin.Log.LogInfo(
                    $"[CustomVoices] +{extra.Count} line(s) -> {voiceName}/{field.Name} (total now {data.reactions.Length})");
            }

            if (totalAdded > 0)
                Plugin.Log.LogInfo($"[CustomVoices] Injected {totalAdded} custom line(s) into '{voiceName}'.");
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

        private static ZNEReaction BuildReaction(string bucket, string wavPath, AudioClip clip)
        {
            return new ZNEReaction
            {
                reactionName = "CV_" + bucket + "_" + Path.GetFileNameWithoutExtension(wavPath),
                // Audio is delivered via the lip-sync path (lipSyncReaction), exactly
                // like the game's normal spoken lines. Leave audioReaction null and the
                // face animation None so nothing double-plays / no gasp pose triggers.
                audioReaction = null,
                faceReactionAnimation = ZNEReactionType.None,
                lipSyncReaction = BuildLipSync(clip),
                subtitleData = BuildSubtitle(wavPath, clip.length),
                priority = 50,
                bypassShouldNotReact = false
            };
        }

        // A LipSyncData carrying only the clip. LipSync.LoadData() refuses to play
        // when phoneme/emotion/gesture data are ALL empty, so we add a single
        // "Rest" phoneme at intensity 0 -> passes the check, no visible mouth motion.
        private static LipSyncData BuildLipSync(AudioClip clip)
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

        // Subtitle comes from a sibling .txt file (same name as the .wav). Always
        // returns a non-null object so ZNESubtitleManager.Show() never gets null.
        private static ZNESubtitleData BuildSubtitle(string wavPath, float clipLength)
        {
            var sd = new ZNESubtitleData { subtitles = new List<ZNESubtitleData.SubtitleLine>() };

            string txtPath = Path.ChangeExtension(wavPath, ".txt");
            if (File.Exists(txtPath))
            {
                string text = File.ReadAllText(txtPath).Trim();
                if (text.Length > 0)
                {
                    float onScreen = Mathf.Max(1.5f, clipLength);
                    sd.subtitles.Add(new ZNESubtitleData.SubtitleLine(text, onScreen, false));
                }
            }
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

        // F8: force-play one of OUR injected ("CV_") lines, bypassing all the
        // game's "should react?" gating. Confirms audio + subtitle end-to-end.
        public static void DebugPlayInjected()
        {
            EnsureBucketFields();
            var ctrl = FindController();
            if (ctrl == null) return;
            var set = ctrl.reactionSet;
            if (set == null) { Plugin.Log.LogWarning("[CustomVoices] Character has no reactionSet."); return; }

            foreach (var field in _bucketFields)
            {
                if (!(field.GetValue(set) is ZNEReactionSetData data) || data?.reactions == null) continue;
                foreach (var r in data.reactions)
                {
                    if (r?.reactionName == null || !r.reactionName.StartsWith("CV_")) continue;
                    string clip = r.lipSyncReaction != null && r.lipSyncReaction.clip != null
                        ? r.lipSyncReaction.clip.name : "NULL";
                    Plugin.Log.LogInfo($"[CustomVoices] F8: playing '{r.reactionName}' from {field.Name} (clip={clip}, len={r.Length():0.00}s).");
                    ctrl.performSpecificReaction(r, true, false);
                    return;
                }
            }
            Plugin.Log.LogWarning(
                $"[CustomVoices] F8: no injected (CV_) line found in voice '{set.name}'. " +
                $"Add WAVs under CustomVoices/{set.name}/<bucket>/ and RESTART the game.");
        }

        // F9: play a random idle line (vanilla or custom). Confirms audio works
        // at all + tells you which mood bucket is active on this character.
        public static void DebugPlayRandomIdle()
        {
            EnsureBucketFields();
            var ctrl = FindController();
            if (ctrl == null) return;
            var set = ctrl.reactionSet;
            if (set == null) { Plugin.Log.LogWarning("[CustomVoices] Character has no reactionSet."); return; }

            foreach (var field in _bucketFields)
            {
                if (!field.Name.EndsWith("IdleReactions")) continue;
                if (!(field.GetValue(set) is ZNEReactionSetData data) || data?.reactions == null || data.reactions.Length == 0) continue;
                Plugin.Log.LogInfo($"[CustomVoices] F9: playing a random line from {field.Name} ({data.reactions.Length} total).");
                ctrl.performRandomReactionFrom(data, true, false);
                return;
            }
            Plugin.Log.LogWarning("[CustomVoices] F9: no non-empty idle bucket found on this character.");
        }
    }
}
