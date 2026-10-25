using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MGSC;

namespace QuasimorphCoop.Patches
{
    // Game code that reads Creatures.Player meaning "the merc this is about". With two mercs the
    // slot holds whoever acted last, so these would pick the wrong one. Every replacement depends
    // only on the creature in question, so both co-op machines still compute the same thing.

    // AiVisionCalculator.FindTargetsInSight drops the first Player it sees if the slot merc is
    // invisible: with two mercs that can be the visible one, while an invisible partner stays a
    // target. Drop exactly the mercs that are invisible instead.
    [HarmonyPatch(typeof(AiVisionCalculator), nameof(AiVisionCalculator.FindTargetsInSight))]
    internal static class AiInvisibilityPatch
    {
        private static readonly MethodInfo IsInvisible = AccessTools.PropertyGetter(typeof(Player), nameof(Player.IsInvisible));

        private static bool SlotInvisible(Player slot) => !CoopSession.Active && slot.IsInvisible;

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replaced = 0;
            foreach (CodeInstruction i in instructions)
            {
                if (i.Calls(IsInvisible))
                {
                    replaced++;
                    yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(AiInvisibilityPatch), nameof(SlotInvisible))).MoveLabelsFrom(i);
                }
                else
                {
                    yield return i;
                }
            }
            if (replaced != 1)
            {
                Log.Warn($"AI invisibility patch: expected one IsInvisible check, found {replaced}.");
            }
        }

        private static void Postfix(List<AiTarget> __result)
        {
            if (CoopSession.Active && __result != null)
            {
                __result.RemoveAll(t => t.Value is Player p && p.IsInvisible);
            }
        }
    }

    // Creature code reaches for `_creatures.Player.Mercenary` for a merc's own data: its curse
    // (wound and infection chances, seizing shots, chaos damage), its resists and look angle. When
    // the creature is itself a merc, use that merc rather than whichever one holds the slot.
    // Monsters keep using the slot, as before.
    [HarmonyPatch]
    internal static class OwnMercenaryPatch
    {
        private static readonly FieldInfo CreaturesField = AccessTools.Field(typeof(Creature), "_creatures");
        private static readonly FieldInfo SlotField = AccessTools.Field(typeof(Creatures), nameof(Creatures.Player));
        private static readonly MethodInfo MercenaryGetter = AccessTools.PropertyGetter(typeof(Player), nameof(Player.Mercenary));
        private static readonly MethodInfo Self = AccessTools.Method(typeof(OwnMercenaryPatch), nameof(MercFor));

        private static Player MercFor(Creatures creatures, Creature self) =>
            CoopSession.Active && self is Player p && p.Mercenary != null ? p : creatures.Player;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (System.Type type in new[] { typeof(Creature), typeof(Player) })
            {
                foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.GetMethodBody() == null || m.IsGenericMethodDefinition)
                    {
                        continue;
                    }
                    List<CodeInstruction> code;
                    try
                    {
                        code = PatchProcessor.GetOriginalInstructions(m);
                    }
                    catch (System.Exception)
                    {
                        continue;
                    }
                    if (Matches(code).Any())
                    {
                        yield return m;
                    }
                }
            }
        }

        // `this._creatures.Player` whose Mercenary is read within the next few instructions (the
        // gap covers `?.`).
        private static IEnumerable<int> Matches(List<CodeInstruction> code)
        {
            for (int i = 2; i < code.Count; i++)
            {
                if (code[i].LoadsField(SlotField) && code[i - 1].LoadsField(CreaturesField) && code[i - 2].IsLdarg(0)
                    && code.Skip(i + 1).Take(6).Any(c => c.Calls(MercenaryGetter)))
                {
                    yield return i;
                }
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            foreach (int i in Matches(code).Reverse().ToList())
            {
                // creatures on the stack -> MercFor(creatures, this)
                CodeInstruction load = code[i];
                code[i] = new CodeInstruction(OpCodes.Ldarg_0).MoveLabelsFrom(load);
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, Self));
            }
            return code;
        }
    }
}
