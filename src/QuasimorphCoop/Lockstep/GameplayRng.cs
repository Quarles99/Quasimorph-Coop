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

        public static void Reset(int seed)
        {
            UnityEngine.Random.State saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);
            _gameplay = UnityEngine.Random.state;
            UnityEngine.Random.state = saved;
        }

        public static UnityEngine.Random.State GameplayState => _swapped || !Enabled ? UnityEngine.Random.state : _gameplay;

        public static void Enter()
        {
            if (SimDepth++ == 0 && Enabled)
            {
                _outside = UnityEngine.Random.state;
                UnityEngine.Random.state = _gameplay;
                _swapped = true;
            }
        }

        public static void Exit()
        {
            if (SimDepth == 0)
            {
                return;
            }
            if (--SimDepth == 0 && _swapped)
            {
                _gameplay = UnityEngine.Random.state;
                UnityEngine.Random.state = _outside;
                _swapped = false;
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
