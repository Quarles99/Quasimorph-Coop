using System;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Lockstep
{
    // Lockstep needs both machines to draw the same gameplay random numbers, but the game uses the
    // one global UnityEngine.Random for gameplay and for visuals/audio, and the visual side draws a
    // frame-rate-dependent amount. When enabled, the simulation entry points below run on their own
    // saved Random.state and everything else (rendering, UI, sound) runs on a separate one.
    //
    // SimDepth is tracked even when disabled: "inside the simulation" vs "player input / UI" is how
    // the netcode decides which actions to send to the other machine.
    internal static class GameplayRng
    {
        public static bool Enabled;

        public static int SimDepth { get; private set; }

        public static bool InSimulation => SimDepth > 0;

        private static UnityEngine.Random.State _gameplay;
        private static UnityEngine.Random.State _outside;
        private static bool _swapped;

        // Test aid: when set, every outermost simulation scope that drew random numbers is logged
        // with its entry point and the call stack, so two runs can be diffed.
        public static Action<string> Trace;
        private static string _scope;
        private static uint _before;

        public static void Reset(int seed)
        {
            WeaponInstanceIdPatch.Reset(seed);
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            _gameplay = UnityEngine.Random.state;
            UnityEngine.Random.state = saved;
        }

        public static UnityEngine.Random.State GameplayState => _swapped || !Enabled ? UnityEngine.Random.state : _gameplay;

        public static void Enter(string scope = "op")
        {
            if (SimDepth++ == 0 && Enabled)
            {
                _outside = UnityEngine.Random.state;
                UnityEngine.Random.state = _gameplay;
                _swapped = true;
                _scope = scope;
                if (Trace != null)
                {
                    _before = S0(UnityEngine.Random.state);
                }
            }
        }

        private static readonly AccessTools.StructFieldRef<UnityEngine.Random.State, int> S0Field = AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s0");

        private static uint S0(UnityEngine.Random.State state) => (uint)S0Field(ref state);

        // Cosmetic code called from inside the simulation (sounds) draws random numbers depending on
        // local things like real time; run it on the outside state so it can't shift gameplay rolls.
        private static int _cosmeticDepth;
        private static UnityEngine.Random.State _suspended;

        public static void BeginCosmetic()
        {
            if (_cosmeticDepth++ == 0 && (_swapped || StreamDepth > 0))
            {
                _suspended = UnityEngine.Random.state;
                UnityEngine.Random.state = _outside;
            }
        }

        public static void EndCosmetic()
        {
            if (_cosmeticDepth > 0 && --_cosmeticDepth == 0 && (_swapped || StreamDepth > 0))
            {
                _outside = UnityEngine.Random.state;
                UnityEngine.Random.state = _suspended;
            }
        }

        // A coroutine's private random stream (see SimCoroutines). Counts as simulation.
        public static int StreamDepth { get; private set; }

        public static UnityEngine.Random.State EnterStream(UnityEngine.Random.State stream)
        {
            SimDepth++;
            StreamDepth++;
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            UnityEngine.Random.state = stream;
            return saved;
        }

        public static UnityEngine.Random.State ExitStream(UnityEngine.Random.State saved)
        {
            UnityEngine.Random.State stream = UnityEngine.Random.state;
            UnityEngine.Random.state = saved;
            StreamDepth--;
            SimDepth--;
            return stream;
        }

        public static void Exit()
        {
            if (SimDepth == 0)
            {
                return;
            }
            if (--SimDepth == 0 && _swapped)
            {
                if (Trace != null && S0(UnityEngine.Random.state) != _before)
                {
                    Trace($"rng {_scope} {_before:x8}->{S0(UnityEngine.Random.state):x8}");
                }
                _gameplay = UnityEngine.Random.state;
                UnityEngine.Random.state = _outside;
                _swapped = false;
            }
        }
    }

    [HarmonyPatch(typeof(TurnController), nameof(TurnController.Tick))]
    internal static class TurnControllerTickRngPatch
    {
        private static void Prefix() => GameplayRng.Enter("Tick");

        private static Exception Finalizer(Exception __exception)
        {
            GameplayRng.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GameLoop), nameof(GameLoop.Tick_Dungeon))]
    internal static class GameLoopTickRngPatch
    {
        private static void Prefix(out bool __state)
        {
            GameplayRng.Enter("Tick_Dungeon");
            __state = GameplayRng.Enabled;
            if (__state)
            {
                FixedProjectileStep.Ticks++;
                FixedProjectileStep.Begin();
            }
        }

        private static Exception Finalizer(Exception __exception, bool __state)
        {
            if (__state)
            {
                FixedProjectileStep.End();
            }
            GameplayRng.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.ProcessCmd))]
    internal static class ProcessCmdRngPatch
    {
        private static void Prefix() => GameplayRng.Enter("ProcessCmd");

        private static Exception Finalizer(Exception __exception)
        {
            GameplayRng.Exit();
            return __exception;
        }
    }
}

namespace QuasimorphCoop.Lockstep
{
    // Test aid for GameplayRng.Trace: also report which per-frame dungeon system drew random numbers.
    [HarmonyPatch]
    internal static class DungeonSystemsRngTracePatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (string type in new[] { "BallisticSystem", "TileSpreadSystem", "CollisionSystem", "HitResolveSystem", "DamageSystem", "OnHitEffectSystem", "CleanupSystem" })
            {
                System.Reflection.MethodInfo m = AccessTools.Method(AccessTools.TypeByName("MGSC." + type), "Update");
                if (m != null)
                {
                    yield return m;
                }
            }
        }

        private static readonly AccessTools.StructFieldRef<UnityEngine.Random.State, int> S0Field = AccessTools.StructFieldRefAccess<UnityEngine.Random.State, int>("s0");

        private static int S0(UnityEngine.Random.State state) => S0Field(ref state);

        private static void Prefix(out UnityEngine.Random.State __state) => __state = UnityEngine.Random.state;

        private static void Postfix(System.Reflection.MethodBase __originalMethod, UnityEngine.Random.State __state)
        {
            if (GameplayRng.Trace != null && !__state.Equals(UnityEngine.Random.state))
            {
                GameplayRng.Trace($"  in {__originalMethod.DeclaringType.Name} {(uint)S0(__state):x8}->{(uint)S0(UnityEngine.Random.state):x8}");
            }
        }
    }
}

namespace QuasimorphCoop.Lockstep
{
    [HarmonyPatch]
    internal static class CosmeticRngPatch
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (System.Reflection.MethodInfo m in typeof(SoundController).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (m.Name.StartsWith("Play", StringComparison.Ordinal))
                {
                    yield return m;
                }
            }
            yield return AccessTools.Method(typeof(SoundBank), nameof(SoundBank.GetRandomSoundBank));
        }

        private static void Prefix() => GameplayRng.BeginCosmetic();

        private static Exception Finalizer(Exception __exception)
        {
            GameplayRng.EndCosmetic();
            return __exception;
        }
    }
}
