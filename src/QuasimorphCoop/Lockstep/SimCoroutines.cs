using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using MGSC;
using UnityEngine;

namespace QuasimorphCoop.Lockstep
{
    // Some gameplay runs in coroutines that Unity resumes later, outside the simulation entry
    // points: bursts roll each bullet's scatter as it's fired, explosions roll damage, stun and fire
    // after a visual delay, throws and melee roll inside their coroutines. Run on the shared
    // simulation state, those draws would interleave with per-frame hit rolls differently at
    // different frame rates; run on the outside state, they'd differ between machines.
    //
    // So every creature/explosion coroutine gets a private random stream. It is seeded from the
    // simulation state at the moment the coroutine is created (read, not drawn from, so even a
    // coroutine created for cosmetic reasons on one machine only doesn't shift anything), plus the
    // coroutine type and how many of that type were created at that same state. Each step of the
    // coroutine runs on its stream, wherever Unity resumes it.
    [HarmonyPatch]
    internal static class CoroutineStreams
    {
        private sealed class Stream
        {
            public UnityEngine.Random.State State;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Stream> Streams = new System.Runtime.CompilerServices.ConditionalWeakTable<object, Stream>();
        private static readonly System.Collections.Generic.Dictionary<string, KeyValuePairStateCount> Counts = new System.Collections.Generic.Dictionary<string, KeyValuePairStateCount>();

        private struct KeyValuePairStateCount
        {
            public string StateKey;
            public int Count;
        }

        private static readonly Type[] Owners = { typeof(Creature), typeof(Player), typeof(Monster), typeof(ExplosionEntity) };

        public static System.Collections.Generic.IEnumerable<Type> IteratorTypes()
        {
            foreach (Type owner in Owners)
            {
                foreach (Type t in owner.GetNestedTypes(System.Reflection.BindingFlags.NonPublic))
                {
                    if (typeof(IEnumerator).IsAssignableFrom(t) && t.Name.StartsWith("<", StringComparison.Ordinal))
                    {
                        yield return t;
                    }
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            IteratorTypes().Select(t => (System.Reflection.MethodBase)AccessTools.Constructor(t, new[] { typeof(int) })).Where(c => c != null);

        private static readonly AccessTools.StructFieldRef<UnityEngine.Random.State, int>[] Fields =
        {
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s0"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s1"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s2"),
            AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s3"),
        };

        // Constructor postfix: created while the simulation (or another stream) is running.
        private static void Postfix(object __instance)
        {
            if (!GameplayRng.Enabled || !GameplayRng.InSimulation)
            {
                return;
            }
            UnityEngine.Random.State current = UnityEngine.Random.state;
            string stateKey = string.Join(":", Fields.Select(f => f(ref current).ToString("x8")));
            string type = __instance.GetType().Name;
            Counts.TryGetValue(type, out KeyValuePairStateCount count);
            count = count.StateKey == stateKey ? new KeyValuePairStateCount { StateKey = stateKey, Count = count.Count + 1 } : new KeyValuePairStateCount { StateKey = stateKey, Count = 0 };
            Counts[type] = count;

            int seed = unchecked((int)ulong.Parse(StateHash.Hash(stateKey + "|" + type + "|" + count.Count).Substring(0, 8), System.Globalization.NumberStyles.HexNumber));
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            Streams.Add(__instance, new Stream { State = UnityEngine.Random.state });
            UnityEngine.Random.state = saved;
        }

        public static bool TryEnter(object iterator, out UnityEngine.Random.State saved)
        {
            if (Streams.TryGetValue(iterator, out Stream stream))
            {
                saved = GameplayRng.EnterStream(stream.State);
                return true;
            }
            saved = default;
            return false;
        }

        public static void Exit(object iterator, UnityEngine.Random.State saved)
        {
            if (Streams.TryGetValue(iterator, out Stream stream))
            {
                stream.State = GameplayRng.ExitStream(saved);
            }
        }
    }

    [HarmonyPatch]
    internal static class CoroutineStreamStepPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
            CoroutineStreams.IteratorTypes().Select(t => (System.Reflection.MethodBase)AccessTools.Method(t, "MoveNext")).Where(m => m != null);

        private static void Prefix(object __instance, out (bool, UnityEngine.Random.State) __state)
        {
            bool entered = CoroutineStreams.TryEnter(__instance, out UnityEngine.Random.State saved);
            __state = (entered, saved);
        }

        private static readonly System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo> CurrentFields = new System.Collections.Generic.Dictionary<Type, System.Reflection.FieldInfo>();
        private static readonly AccessTools.FieldRef<WaitForSeconds, float> Seconds = AccessTools.FieldRefAccess<WaitForSeconds, float>("m_Seconds");

        private static Exception Finalizer(object __instance, Exception __exception, (bool, UnityEngine.Random.State) __state)
        {
            if (__state.Item1)
            {
                CoroutineStreams.Exit(__instance, __state.Item2);
                if (__exception == null)
                {
                    TickWaits(__instance);
                }
            }
            return __exception;
        }

        // Waits inside simulation coroutines (the gap between a burst's shots, an explosion's delay)
        // are game time, while projectiles move a fixed step per Tick_Dungeon: at another frame rate
        // the next shot left when the previous bullet was somewhere else. Count those waits in ticks.
        private static void TickWaits(object iterator)
        {
            Type type = iterator.GetType();
            if (!CurrentFields.TryGetValue(type, out System.Reflection.FieldInfo field))
            {
                field = AccessTools.Field(type, "<>2__current");
                CurrentFields[type] = field;
            }
            if (field?.GetValue(iterator) is WaitForSeconds wait)
            {
                field.SetValue(iterator, new WaitSimTicks(Mathf.Max(1, Mathf.RoundToInt(Seconds(wait) / FixedProjectileStep.Step))));
            }
        }
    }

    [HarmonyPatch(typeof(DungeonGameMode), "Update")]
    internal static class SimTickCountPatch
    {
        private static void Prefix() => FixedProjectileStep.Ticks++;
    }

    internal sealed class WaitSimTicks : CustomYieldInstruction
    {
        private readonly long _until;

        public WaitSimTicks(int ticks)
        {
            _until = FixedProjectileStep.Ticks + ticks;
        }

        public override bool keepWaiting => FixedProjectileStep.Ticks < _until;
    }

    // Projectiles advance by frame time, so how many steps a shot takes (and which rolls happen
    // along the way) depended on the frame rate. Inside the simulation they get a fixed step, using
    // the override the game itself has for instant resolution.
    internal static class FixedProjectileStep
    {
        public const float Step = 1f / 30f;

        // DungeonGameMode.Update calls so far (see WaitSimTicks): Tick_Dungeon runs at most once in
        // each, so this keeps pace with the projectile step, and it keeps counting when the update
        // returns early (dead merc, mission finished) or lockstep has stopped, so no wait hangs.
        public static long Ticks;

        private static bool _savedOverride;
        private static float _savedDelta;

        public static void Begin()
        {
            _savedOverride = BallisticSystem.IsOverridenDeltaTime;
            _savedDelta = BallisticSystem.OverridenDeltaTime;
            if (!_savedOverride)
            {
                BallisticSystem.IsOverridenDeltaTime = true;
                BallisticSystem.OverridenDeltaTime = Step;
            }
        }

        public static void End()
        {
            BallisticSystem.IsOverridenDeltaTime = _savedOverride;
            BallisticSystem.OverridenDeltaTime = _savedDelta;
        }
    }
}
