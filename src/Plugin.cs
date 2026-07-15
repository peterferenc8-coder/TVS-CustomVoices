using System;
using System.IO;
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
        public const string VERSION = "1.3.0";

        internal static ManualLogSource Log;
        internal static string VoicesRoot;

        private bool _subscribed;

        private void Awake()
        {
            Log = Logger;
            VoicesRoot = Path.Combine(Paths.PluginPath, "CustomVoices");
            try { Directory.CreateDirectory(VoicesRoot); } catch { /* best effort */ }

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
            new Harmony(GUID).PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo("[CustomVoices] Harmony patch applied.");
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
            if (c == '+') VoiceInjector.DebugPlayInjected();
            else if (c == '#') VoiceInjector.DebugPlayRandomIdle();
        }
    }

    // Fires every time a voice is assigned to a character. We index the freshly
    // loaded ZNEReactionSet: which buckets have custom audio and which vanilla
    // reactions belong to them.
    [HarmonyPatch(typeof(ZNECharacterReactionController), "reactionSet", MethodType.Setter)]
    internal static class ReactionSetSetterPatch
    {
        private static void Postfix(ZNEReactionSet value)
        {
            if (value == null) return;
            try { VoiceInjector.Inject(value); }
            catch (Exception e) { Plugin.Log.LogError($"[CustomVoices] indexing failed: {e}"); }
        }
    }

    // Fires just before any reaction plays. We swap the audio for a custom line
    // (keeping the vanilla face pose / phonemes / emotion / gesture), or mute the
    // vanilla voice in custom-only mode, by replacing the reaction argument.
    [HarmonyPatch(typeof(ZNECharacterReactionController), "performSpecificReaction",
        new[] { typeof(ZNEReaction), typeof(bool), typeof(bool) })]
    internal static class PerformSpecificReactionPatch
    {
        private static void Prefix(ref ZNEReaction reaction)
        {
            try { VoiceInjector.OnBeforeReaction(ref reaction); }
            catch (Exception e) { Plugin.Log.LogError($"[CustomVoices] swap failed: {e}"); }
        }
    }
}
