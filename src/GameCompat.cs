using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

namespace TVSCustomVoices
{
    // The reaction API is not stable across game builds. Observed so far:
    //
    //   v0.48a  performSpecificReaction(ZNEReaction, bool, bool)
    //   v0.49   performSpecificReaction(ZNEReaction, string reactionSetName, bool, bool)
    //
    // Binding to a fixed signature makes the mod hard-fail on any other build:
    // Harmony throws "Undefined target method" and a direct C# call throws
    // MissingMethodException.
    //
    // So: resolve everything we touch by NAME at runtime, and call it through
    // reflection, filling whatever extra parameters the local build declares.
    internal static class GameCompat
    {
        private static readonly Type Controller = typeof(ZNECharacterReactionController);

        private static bool _resolved;
        private static MethodInfo _performSpecificReaction;
        private static MethodInfo _performRandomReactionFrom;
        private static MethodInfo _reactionSetSetter;
        private static MethodInfo _reactionSetGetter;

        // The one patch the mod cannot work without.
        internal static MethodBase PerformSpecificReactionTarget
        {
            get { Resolve(); return _performSpecificReaction; }
        }

        internal static MethodBase ReactionSetSetterTarget
        {
            get { Resolve(); return _reactionSetSetter; }
        }

        internal static bool IsSupportedBuild
        {
            get { Resolve(); return _performSpecificReaction != null && _reactionSetSetter != null; }
        }

        internal static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            _performSpecificReaction = FindMethod("performSpecificReaction", typeof(ZNEReaction));
            _performRandomReactionFrom = FindMethod("performRandomReactionFrom", typeof(ZNEReactionSetData));

            var prop = FindProperty("reactionSet");
            _reactionSetSetter = prop?.GetSetMethod(true);
            _reactionSetGetter = prop?.GetGetMethod(true);

            Plugin.Log.LogInfo("[CustomVoices] Game API: " +
                $"performSpecificReaction={Describe(_performSpecificReaction)}, " +
                $"performRandomReactionFrom={Describe(_performRandomReactionFrom)}, " +
                $"reactionSet={(prop == null ? "MISSING" : "ok")}");

            if (!IsSupportedBuild) LogApiDump();
        }

        // ---- Calls -------------------------------------------------------------

        // performSpecificReaction(reaction, [reactionSetName], bypassShouldReactCheck: true, <rest defaulted>)
        internal static bool PerformSpecificReaction(
            ZNECharacterReactionController ctrl, ZNEReaction reaction, string reactionSetName)
        {
            Resolve();
            return TryInvoke(_performSpecificReaction, "performSpecificReaction", ctrl, reaction, reactionSetName, out _);
        }

        // performRandomReactionFrom(data, [reactionSetName], bypassShouldReactCheck: true, <rest defaulted>)
        internal static bool PerformRandomReactionFrom(
            ZNECharacterReactionController ctrl, ZNEReactionSetData data, string reactionSetName)
        {
            Resolve();
            return TryInvoke(_performRandomReactionFrom, "performRandomReactionFrom", ctrl, data, reactionSetName, out _);
        }

        internal static ZNEReactionSet GetReactionSet(ZNECharacterReactionController ctrl)
        {
            Resolve();
            if (_reactionSetGetter == null)
            {
                Plugin.Log.LogWarning("[CustomVoices] This game build has no ZNECharacterReactionController.reactionSet property.");
                return null;
            }
            return _reactionSetGetter.Invoke(ctrl, null) as ZNEReactionSet;
        }

        // ---- Plumbing ----------------------------------------------------------

        // Calls `method` with `payload` first, then fills every remaining parameter
        // by kind rather than by position: any string gets the reaction-set name
        // (v0.49 added a required `reactionSetName`), the first bool gets `true`
        // (the "bypass the should-react gating" flag in every build seen so far),
        // and anything else gets its compiled default. That makes the call
        // arity-independent, which is the whole point of this class.
        private static bool TryInvoke(MethodInfo method, string name, object instance,
                                      object payload, string reactionSetName, out object result)
        {
            result = null;
            if (method == null)
            {
                Plugin.Log.LogWarning($"[CustomVoices] '{name}' is not available on this game build — skipping.");
                return false;
            }

            var ps = method.GetParameters();
            var args = new object[ps.Length];
            args[0] = payload;
            bool bypassAssigned = false;
            for (int i = 1; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(string))
                {
                    args[i] = reactionSetName ??
                              (ps[i].HasDefaultValue ? ps[i].DefaultValue : string.Empty);
                }
                else if (!bypassAssigned && ps[i].ParameterType == typeof(bool))
                {
                    args[i] = true;
                    bypassAssigned = true;
                }
                else
                {
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : DefaultOf(ps[i].ParameterType);
                }
            }

            try
            {
                result = method.Invoke(instance, args);
                return true;
            }
            catch (TargetInvocationException e)
            {
                Plugin.Log.LogError($"[CustomVoices] '{name}' threw: {e.InnerException}");
                return false;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[CustomVoices] could not call '{name}': {e.Message}");
                return false;
            }
        }

        private static object DefaultOf(Type t)
        {
            return t.IsValueType ? Activator.CreateInstance(t) : null;
        }

        // Matched on name + first parameter type only, so extra trailing
        // parameters (or the lack of them) never break the lookup. Walks base
        // types as well: Harmony's [HarmonyPatch] attribute uses DeclaredMethod,
        // which would miss a method that moved to a base class between builds.
        private static MethodInfo FindMethod(string name, Type firstParamType)
        {
            return AllMethods(Controller)
                .Where(m => m.Name == name)
                .Where(m =>
                {
                    var ps = m.GetParameters();
                    return ps.Length >= 1 && ps[0].ParameterType == firstParamType;
                })
                .OrderBy(m => m.GetParameters().Length)
                .FirstOrDefault();
        }

        private static PropertyInfo FindProperty(string name)
        {
            for (var t = Controller; t != null && t != typeof(object); t = t.BaseType)
            {
                var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic |
                                            BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (p != null) return p;
            }
            return null;
        }

        private static IEnumerable<MethodInfo> AllMethods(Type type)
        {
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                               BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    yield return m;
        }

        private static string Describe(MethodInfo m)
        {
            if (m == null) return "MISSING";
            return "(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name).ToArray()) + ")";
        }

        // Printed when something we need is missing, so an unsupported build
        // identifies itself from the user's log instead of needing a repro.
        private static void LogApiDump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[CustomVoices] Unsupported game build. ZNECharacterReactionController exposes:");
            foreach (var m in AllMethods(Controller)
                         .Where(m => m.Name.StartsWith("perform") || m.Name.Contains("eactionSet"))
                         .OrderBy(m => m.Name))
            {
                sb.AppendLine("    " + m.Name + Describe(m));
            }
            sb.Append("[CustomVoices] Please report this log — the mod is built against a newer game version.");
            Plugin.Log.LogWarning(sb.ToString());
        }
    }
}
