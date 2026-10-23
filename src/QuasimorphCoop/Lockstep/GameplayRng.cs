using System;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Lockstep
{
    // Lockstep needs both machines to draw the same gameplay random numbers, but the game uses the
    // one global UnityEngine.Random for gameplay and for visuals/audio, and the visual side draws a
    // frame-rate-dependent amount. When enabled, the simulation entry points below run on their own
    // saved Random.state and everything else (rendering, UI, sound) runs on a separate one.
    internal static class GameplayRng
    {
        public static bool Enabled;

        private static UnityEngine.Random.State _gameplay;
        private static UnityEngine.Random.State _outside;
        private static int _depth;

        public static void Reset(int seed)
        {
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            _gameplay = UnityEngine.Random.state;
            UnityEngine.Random.state = saved;
            _depth = 0;
        }

        public static UnityEngine.Random.State GameplayState => _depth > 0 || !Enabled ? UnityEngine.Random.state : _gameplay;

        public static void Enter()
        {
            if (!Enabled)
            {
                return;
            }
            if (_depth++ == 0)
            {
                _outside = UnityEngine.Random.state;
                UnityEngine.Random.state = _gameplay;
            }
        }

        public static void Exit()
        {
            if (!Enabled || _depth == 0)
            {
                return;
            }
            if (--_depth == 0)
            {
                _gameplay = UnityEngine.Random.state;
                UnityEngine.Random.state = _outside;
            }
        }
    }

    [HarmonyPatch(typeof(TurnController), nameof(TurnController.Tick))]
    internal static class TurnControllerTickRngPatch
    {
        private static void Prefix() => GameplayRng.Enter();

        private static Exception Finalizer(Exception __exception)
        {
            GameplayRng.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(GameLoop), nameof(GameLoop.Tick_Dungeon))]
    internal static class GameLoopTickRngPatch
    {
        private static void Prefix() => GameplayRng.Enter();

        private static Exception Finalizer(Exception __exception)
        {
            GameplayRng.Exit();
            return __exception;
        }
    }

    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.ProcessCmd))]
    internal static class ProcessCmdRngPatch
    {
        private static void Prefix() => GameplayRng.Enter();

        private static Exception Finalizer(Exception __exception)
        {
            GameplayRng.Exit();
            return __exception;
        }
    }
}
