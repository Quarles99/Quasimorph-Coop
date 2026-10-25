using System;
using HarmonyLib;
using MGSC;
using QuasimorphCoop.Net;
using UnityEngine;

namespace QuasimorphCoop.Patches
{
    // Keyboard/mouse world input only runs for the merc this machine controls.
    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.ProcessInput))]
    internal static class ProcessInputNetPatch
    {
        private static bool Prefix() => NetSession.LocalInputAllowed;
    }

    // Every queued player command (from world input, HUD buttons, hotkeys) passes through here.
    [HarmonyPatch(typeof(PlayerCommandQueue), nameof(PlayerCommandQueue.Add))]
    internal static class CommandQueueAddNetPatch
    {
        private static bool Prefix(ICommand cmd) => NetSession.OnCommandAdded(cmd);
    }

    [HarmonyPatch(typeof(PlayerCommandQueue), nameof(PlayerCommandQueue.AddFirst))]
    internal static class CommandQueueAddFirstNetPatch
    {
        private static bool Prefix(ICommand cmd) => NetSession.OnCommandAdded(cmd);
    }

    // DungeonGameMode.Update clears the queue when any key is pressed mid-move (cancels a path).
    // That happens at a frame-dependent point in the path, so it can't be replayed; disable it in co-op.
    [HarmonyPatch(typeof(PlayerCommandQueue), nameof(PlayerCommandQueue.Clear))]
    internal static class CommandQueueClearNetPatch
    {
        private static bool Prefix()
        {
            if (!NetSession.Engaged || NetSession.Replaying || Lockstep.GameplayRng.InSimulation || !Input.anyKeyDown)
            {
                return true;
            }
            Player player = CoopSession.Creatures?.Player;
            return player == null || !player.CreatureData.Health.Alive || !player.Creature3dView.MoveToPosInProgress;
        }
    }

    // Direct actions: called straight from input handling instead of going through the queue.
    internal static class DirectActionPatch
    {
        public static bool Begin(DirectAction action, int arg, out bool recorded) => NetSession.BeginAction(action, arg, out recorded);

        public static Exception End(Exception exception, bool recorded)
        {
            if (recorded)
            {
                NetSession.EndAction();
            }
            return exception;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.SkipTurn))]
    internal static class SkipTurnNetPatch
    {
        private static bool Prefix(out bool __state) => DirectActionPatch.Begin(DirectAction.SkipTurn, 0, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ChangeMoveStateToNext))]
    internal static class MoveStateNextNetPatch
    {
        private static bool Prefix(out bool __state) => DirectActionPatch.Begin(DirectAction.MoveStateNext, 0, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(Player), nameof(Player.ChangeMoveStateToPrev))]
    internal static class MoveStatePrevNetPatch
    {
        private static bool Prefix(out bool __state) => DirectActionPatch.Begin(DirectAction.MoveStatePrev, 0, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(PlayerInteractionSystem), "SwapMoveMode")]
    internal static class SwapMoveModeNetPatch
    {
        private static bool Prefix(out bool __state) => DirectActionPatch.Begin(DirectAction.SwapMoveMode, 0, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.SwapWeapon))]
    internal static class SwapWeaponNetPatch
    {
        private static bool Prefix(out bool __state) => DirectActionPatch.Begin(DirectAction.SwapWeapon, 0, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.SwapFiremode))]
    internal static class SwapFiremodeNetPatch
    {
        private static bool Prefix(out bool __state) => DirectActionPatch.Begin(DirectAction.SwapFiremode, 0, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    // The plain reload key is a direct action. Reloading with a chosen ammo item (HUD, vest slot) isn't
    // recorded here: the Player.StartReload it leads to is sent as an inventory op.
    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.Reload))]
    internal static class ReloadNetPatch
    {
        private static bool Prefix(BasePickupItem ammo, out bool __state)
        {
            __state = false;
            return ammo != null || DirectActionPatch.Begin(DirectAction.Reload, 0, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(PlayerInteractionSystem), nameof(PlayerInteractionSystem.ActivateUltimatePerk))]
    internal static class UltimateNetPatch
    {
        private static bool Prefix(out bool __state) => DirectActionPatch.Begin(DirectAction.ActivateUltimate, 0, out __state);

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }

    [HarmonyPatch(typeof(Creature), nameof(Creature.ChangeDirection), typeof(Direction), typeof(bool), typeof(bool), typeof(bool))]
    internal static class ChangeDirectionNetPatch
    {
        private static bool Prefix(Creature __instance, Direction direction, out bool __state)
        {
            __state = false;
            if (!(__instance is Player))
            {
                return true;
            }
            return DirectActionPatch.Begin(DirectAction.ChangeDirection, (int)direction, out __state);
        }

        private static Exception Finalizer(Exception __exception, bool __state) => DirectActionPatch.End(__exception, __state);
    }
}
