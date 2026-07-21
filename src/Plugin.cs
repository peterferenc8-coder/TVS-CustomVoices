using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace TVSCustomVoices
{
    // Adds extra voicelines to The Villain Simulator by injecting custom audio
    // (+ optional subtitles) into the game's reaction sets at runtime.
    //
    // A "voice" is a ZNEReactionSet (ScriptableObject). It holds many
    // ZNEReactionSetData buckets (dominantGaspReactions, scaredDamageReactions,
    // ...). Each bucket has a ZNEReaction[] the game randomly picks from.
    // We append our own ZNEReaction entries to those arrays.
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.peter.tvs.customvoices";
        public const string NAME = "TVS Custom Voices";
        public const string VERSION = "1.4.0";

        internal static ManualLogSource Log;
        internal static string VoicesRoot;

        private bool _subscribed;

        private void Awake()
        {
            Log = Logger;
            VoicesRoot = Path.Combine(Paths.PluginPath, "CustomVoices");
            try { Directory.CreateDirectory(VoicesRoot); } catch { /* best effort */ }

            var enabledCfg = Config.Bind(
                "General", "Enabled", true,
                "Master switch. When false, this mod does nothing: no voice injection, no swaps, no debug keys. Disable it here without removing the DLL.");
            if (!enabledCfg.Value)
            {
                Log.LogInfo($"[CustomVoices] v{VERSION} disabled via config (General/Enabled=false). Doing nothing.");
                return;
            }

            var scaffoldCfg = Config.Bind(
                "General", "GenerateFolderScaffold", true,
                "On startup, create an empty <voice>/<bucket>/ folder tree for every known voice so you can see where to drop audio. Set false if you don't want the empty folders.");
            VoiceInjector.ScaffoldEnabled = scaffoldCfg.Value;

            var customOnlyCfg = Config.Bind(
                "General", "CustomVoicesOnly", false,
                "When true, the original heroine voice is never heard: any line you've dubbed always plays your audio, and every other vanilla voice line is muted (the face/body animation still plays). When false, use SwapChancePercent.");
            VoiceInjector.CustomVoicesOnly = customOnlyCfg.Value;

            var chanceCfg = Config.Bind(
                "General", "SwapChancePercent", 50,
                "Chance (0-100) that a line you've dubbed plays your audio instead of the vanilla one. Only used when CustomVoicesOnly is false.");
            VoiceInjector.SwapChancePercent = Math.Max(0, Math.Min(100, chanceCfg.Value));

            Log.LogInfo($"[CustomVoices] v{VERSION} loading. Audio root: {VoicesRoot}");
            ApplyPatches();
            if (VoiceInjector.ScaffoldEnabled) VoiceInjector.GenerateScaffold();
            Log.LogInfo("[CustomVoices] Debug keys: + = force-play one of YOUR injected lines; # = play a random idle line.");

            // IMPORTANT: this game never calls this component's Update() (it
            // disables the BepInEx manager object), so we can't poll input in a
            // frame loop. Instead we subscribe ONCE to the Input System's keyboard
            // events, which are driven by the game's own input loop. The game uses
            // the NEW Input System; the legacy UnityEngine.Input returns nothing.
            TrySubscribeKeyboard("Awake");
            InputSystem.onDeviceChange += (device, change) =>
            {
                if (device is Keyboard) TrySubscribeKeyboard("device-change");
            };
        }

        // Patch one class at a time instead of Harmony.PatchAll(): PatchAll aborts
        // on the first failure, so a single method whose signature drifted between
        // game builds would take Awake() — and with it the whole mod — down with it.
        // Here an unpatchable target is logged and the rest still applies.
        private void ApplyPatches()
        {
            GameCompat.Resolve();

            var harmony = new Harmony(GUID);
            int applied = 0, failed = 0;

            foreach (var type in typeof(Plugin).Assembly.GetTypes())
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;
                try
                {
                    var patched = harmony.CreateClassProcessor(type).Patch();
                    if (patched != null && patched.Count > 0) applied++;
                    else failed++;
                }
                catch (Exception e)
                {
                    failed++;
                    Log.LogError($"[CustomVoices] patch '{type.Name}' does not fit this game build: " +
                                 (e is HarmonyException && e.InnerException != null ? e.InnerException.Message : e.Message));
                }
            }

            if (failed == 0)
            {
                Log.LogInfo($"[CustomVoices] Harmony patches applied ({applied}).");
            }
            else
            {
                Log.LogWarning($"[CustomVoices] {applied} patch(es) applied, {failed} skipped.");
                if (!GameCompat.IsSupportedBuild)
                    Log.LogWarning("[CustomVoices] Voice swapping is DISABLED — your game version is older than the one " +
                                   "this build of the mod targets. The mod will otherwise stay out of the way.");
            }
        }

        private void TrySubscribeKeyboard(string ctx)
        {
            if (_subscribed) return;
            var kb = Keyboard.current;
            if (kb == null)
            {
                Log.LogInfo($"[CustomVoices] ({ctx}) no keyboard yet; will retry when one appears.");
                return;
            }
            _subscribed = true;
            kb.onTextInput += OnTextInput;
            Log.LogInfo($"[CustomVoices] ({ctx}) subscribed to keyboard input. Press + or # to test.");
        }

        private void OnTextInput(char c)
        {
            // + / # are the trigger keys. (ö / ü were tried but are dead keys
            // under Wine/NoMachine and never arrive as text input events.)
            // Never let a debug hotkey throw into the game's input loop.
            try
            {
                if (c == '+') VoiceInjector.DebugPlayInjected();
                else if (c == '#') VoiceInjector.DebugPlayRandomIdle();
            }
            catch (Exception e)
            {
                Log.LogError($"[CustomVoices] debug key '{c}' failed: {e}");
            }
        }
    }

    // Fires every time a voice is assigned to a character. We index the freshly
    // loaded ZNEReactionSet: which buckets have custom audio and which vanilla
    // reactions belong to them.
    //
    // Both patches below resolve their target through GameCompat rather than a
    // literal signature, so a game build with a different parameter list still
    // matches. Arguments are taken by index (__0) for the same reason.
    [HarmonyPatch]
    internal static class ReactionSetSetterPatch
    {
        private static MethodBase TargetMethod() => GameCompat.ReactionSetSetterTarget;

        private static void Postfix(ZNEReactionSet __0)
        {
            if (__0 == null) return;
            try { VoiceInjector.Inject(__0); }
            catch (Exception e) { Plugin.Log.LogError($"[CustomVoices] indexing failed: {e}"); }
        }
    }

    // Fires just before any reaction plays. We swap the audio for a custom line
    // (keeping the vanilla face pose / phonemes / emotion / gesture), or mute the
    // vanilla voice in custom-only mode, by replacing the reaction argument.
    [HarmonyPatch]
    internal static class PerformSpecificReactionPatch
    {
        private static MethodBase TargetMethod() => GameCompat.PerformSpecificReactionTarget;

        private static void Prefix(ref ZNEReaction __0)
        {
            try { VoiceInjector.OnBeforeReaction(ref __0); }
            catch (Exception e) { Plugin.Log.LogError($"[CustomVoices] swap failed: {e}"); }
        }
    }
}
