using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop
{
    // Treating the partner: the game's own health screen, showing an adjacent party merc's wounds,
    // with medkits and splints from the slot merc's inventory. HealthScreen reads its patient from
    // Creatures.Player throughout, so inside it that read goes through PlayerFor; everything that
    // takes the whole Creatures (turn checks, ending the turn) still means the healer.
    internal static class Treat
    {
        private static Player _patient;
        private static Player _healer;

        // Set while a received wound click is replayed (the screen may be hidden on this machine).
        public static Player ReplayPatient;

        // Party mercs next to the slot merc that could be treated now.
        public static IEnumerable<Player> Candidates()
        {
            Player healer = CoopSession.Creatures?.Player;
            if (!CoopSession.Active || healer == null || !healer.CreatureData.Health.Alive)
            {
                return Enumerable.Empty<Player>();
            }
            CellPosition me = healer.CreatureData.Position;
            return CoopSession.Party.Where(p => p != healer && p.CreatureData.Health.Alive
                && System.Math.Max(System.Math.Abs(p.CreatureData.Position.X - me.X), System.Math.Abs(p.CreatureData.Position.Y - me.Y)) <= 1);
        }

        public static void Open(Player patient)
        {
            _patient = patient;
            _healer = CoopSession.Creatures.Player;
            Log.Info($"Treating '{patient.Mercenary?.ProfileId}' as '{_healer.Mercenary?.ProfileId}'.");
            UI.BackToDefault();
            UI.Chain<HealthScreen>().HideAll(typeof(DungeonHudScreen)).Show().SetBackOnBackgroundClick(value: true);
        }

        // The merc the health screen is about: the patient while the screen opened for one is up and
        // the healer still has the slot, otherwise the slot merc as usual.
        public static Player PlayerFor(Creatures creatures)
        {
            if (ReplayPatient != null)
            {
                return ReplayPatient;
            }
            if (_patient != null)
            {
                HealthScreen screen = UI.Get<HealthScreen>();
                if (creatures.Player == _healer && screen != null && screen.gameObject.activeInHierarchy && _patient.CreatureData.Health.Alive)
                {
                    return _patient;
                }
                _patient = _healer = null;
            }
            return creatures.Player;
        }

        // Unique id of the patient for a wound click being recorded now (0: the slot merc itself).
        public static int PatientUid(Creatures creatures)
        {
            Player patient = PlayerFor(creatures);
            return patient == creatures.Player ? 0 : patient.CreatureData.UniqueId;
        }
    }

    [HarmonyPatch]
    internal static class HealthScreenPatientPatch
    {
        private static readonly FieldInfo PlayerField = AccessTools.Field(typeof(Creatures), nameof(Creatures.Player));

        // CanAmputate is left alone: the amputation tool comes from the healer's inventory.
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in new[] { "OnEnable", "WoundSlotOnClicked", "ProcessFixateMedkitMode", "ProcessMedkitSideEffects", "ProcessHealMedkitMode" })
            {
                yield return AccessTools.Method(typeof(HealthScreen), name);
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo playerFor = AccessTools.Method(typeof(Treat), nameof(Treat.PlayerFor));
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.LoadsField(PlayerField))
                {
                    yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, playerFor).MoveLabelsFrom(ins).MoveBlocksFrom(ins);
                }
                else
                {
                    yield return ins;
                }
            }
        }
    }

    // The supply list comes from the patient's inventory as written; treating uses your own kit.
    [HarmonyPatch(typeof(HealthScreen), "OnEnable")]
    internal static class HealthScreenSuppliesPatch
    {
        private static void Postfix(HealthScreen __instance, Creatures ____creatures, HealSupplyWindow ____healSupplyWindow, bool ____hasWoundToHeal)
        {
            if (____creatures == null || Treat.PlayerFor(____creatures) == ____creatures.Player || !____hasWoundToHeal
                || !PlayerInteractionSystem.CanUseInventory(____creatures, out LimitsTooltip.LimitType _))
            {
                return;
            }
            ____healSupplyWindow.Initialize(____creatures.Player.CreatureData.Inventory, HealSupplyWindow.FilterMode.MedkitsWithFixation);
        }
    }
}
