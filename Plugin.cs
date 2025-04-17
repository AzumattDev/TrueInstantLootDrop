using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace TrueInstantLootDrop;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class TrueInstantLootDropPlugin : BaseUnityPlugin
{
    internal const string ModName = "TrueInstantLootDrop";
    internal const string ModVersion = "1.0.2";
    internal const string Author = "Azumatt";
    private const string ModGUID = $"{Author}.{ModName}";
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource TrueInstantLootDropLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    internal static ConfigEntry<Toggle> ShowDeathPoof = null!;
    public static bool IsDeathPoofEnabled => ShowDeathPoof.Value == Toggle.On;


    public enum Toggle
    {
        On = 1,
        Off = 0,
    }

    public void Awake()
    {
        ShowDeathPoof = Config.Bind("1 - General", "Death Effects", Toggle.On, "If off, disables the smokey poof effect that appears after the ragdoll disappears.");

        Assembly assembly = Assembly.GetExecutingAssembly();
        _harmony.PatchAll(assembly);
    }
}

[HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.Start))]
static class RagdollStartPatch
{
    static void Postfix(Ragdoll __instance)
    {
        Vector3 vector3 = __instance.GetAverageBodyPosition();
        if (__instance.m_lootSpawnJoint != null)
            vector3 = __instance.m_lootSpawnJoint.transform.position;
        __instance.SpawnLoot(vector3);
    }
}

[HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.DestroyNow))]
public static class Ragdoll_DestroyNow_Patch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
    {
        List<CodeInstruction> codes = new(instructions);
 
        FieldInfo? removeEffectField = AccessTools.Field(typeof(Ragdoll), "m_removeEffect");
        MethodInfo? effectCreate = AccessTools.Method(typeof(EffectList), "Create", [typeof(Vector3), typeof(Quaternion), typeof(Transform), typeof(float), typeof(int)]);
        Label skipEffectLabel = il.DefineLabel();
        bool labelAssigned = false;

        MethodInfo? spawnLoot = AccessTools.Method(typeof(Ragdoll), "SpawnLoot", [typeof(Vector3)]);

        List<CodeInstruction> newCodes = [];
        for (int i = 0; i < codes.Count; ++i)
        {
            // 1) CONDITIONAL DEATH‑POOF
            if (i < codes.Count - 1 && codes[i].opcode == OpCodes.Ldarg_0 && codes[i + 1].opcode == OpCodes.Ldfld && codes[i + 1].operand is FieldInfo field && field == removeEffectField)
            {
                // inject: if (!IsDeathPoofEnabled) goto skipEffect
                newCodes.Add(new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TrueInstantLootDropPlugin), nameof(TrueInstantLootDropPlugin.IsDeathPoofEnabled))));
                newCodes.Add(new CodeInstruction(OpCodes.Brfalse, skipEffectLabel));

                // copy through Create(...) + pop
                while (i < codes.Count)
                {
                    newCodes.Add(codes[i]);
                    if (codes[i].opcode == OpCodes.Callvirt && codes[i].operand.Equals(effectCreate))
                    {
                        // include the pop
                        if (i + 1 < codes.Count && codes[i + 1].opcode == OpCodes.Pop)
                        {
                            ++i;
                            newCodes.Add(codes[i]);
                        }

                        break; // End copying the effect block.
                    }

                    ++i;
                }

                // Attach the skip label to the next instruction in the original code,
                // so that if the config check fails, execution jumps here.
                if (i + 1 < codes.Count)
                {
                    codes[i + 1].labels ??= [];
                    codes[i + 1].labels.Add(skipEffectLabel);
                    labelAssigned = true;
                }

                continue;
            }

            //    UNCONDITIONAL REMOVE SpawnLoot
            //    match: ldarg.0; ldloc.0; call SpawnLoot(...)
            if (i + 2 < codes.Count && codes[i].opcode == OpCodes.Ldarg_0 && codes[i + 1].opcode == OpCodes.Ldloc_0 && codes[i + 2].opcode == OpCodes.Call && codes[i + 2].operand.Equals(spawnLoot))
            {
                // gather any labels so they aren't orphaned
                List<Label> labels = [];
                foreach (int j in new[] { i, i + 1, i + 2 })
                {
                    if (codes[j].labels != null)
                    {
                        labels.AddRange(codes[j].labels);
                        codes[j].labels.Clear();
                    }
                }

                // re‑attach to the next instruction
                if (i + 3 < codes.Count && labels.Count > 0)
                {
                    codes[i + 3].labels ??= [];
                    codes[i + 3].labels.AddRange(labels);
                }

                // skip those three ops entirely
                i += 2;
                continue;
            }

            // everything else
            newCodes.Add(codes[i]);
        }

        // if the poof‑skip label never got placed, make a NOP for it
        if (labelAssigned) return newCodes;
        CodeInstruction nop = new(OpCodes.Nop);
        nop.labels.Add(skipEffectLabel);
        newCodes.Add(nop);

        return newCodes;
    }
}