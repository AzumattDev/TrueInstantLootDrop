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
    internal const string ModVersion = "1.0.1";
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

[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
public class Character_OnDeath_InstantLootPatch
{
    static void Prefix(Character __instance)
    {
        // Get the CharacterDrop component.
        CharacterDrop drop = __instance.GetComponent<CharacterDrop>();
        if (drop == null || !drop.m_dropsEnabled) return;
        // Calculate the drop position (character center + an upward offset)
        Vector3 dropPos = __instance.GetCenterPoint() + Vector3.up * 0.75f;
        // Generate the drop list.
        List<KeyValuePair<GameObject, int>> loot = drop.GenerateDropList();
        if (loot is not { Count: > 0 }) return;
        // Instantly drop the loot.
        CharacterDrop.DropItems(loot, dropPos, 0.5f);
        // Disable further loot drop for this character.
        drop.SetDropsEnabled(false);
        TrueInstantLootDropPlugin.TrueInstantLootDropLogger.LogDebug($"Instant loot drop executed for {__instance.name} at {dropPos}.");
    }
}

[HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.Setup))]
static class RagdollSetupPatch
{
    static void Prefix(Ragdoll __instance, CharacterDrop characterDrop)
    {
        if (characterDrop == null || characterDrop.m_dropsEnabled) return;
        // Prevent the ragdoll from saving any loot info.
        __instance.m_dropItems = false;
    }
}

[HarmonyPatch(typeof(Ragdoll), nameof(Ragdoll.DestroyNow))]
public static class Ragdoll_DestroyNow_Patch
{
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
    {
        List<CodeInstruction> codes = new(instructions);
        
        FieldInfo removeEffectField = AccessTools.Field(typeof(Ragdoll), "m_removeEffect");
        MethodInfo createMethod = AccessTools.Method(typeof(EffectList), "Create");

        // Define a label for where to jump if the effect should be skipped.
        Label skipEffectLabel = il.DefineLabel();
        bool labelAssigned = false;

        List<CodeInstruction> newCodes = new();
        for (int i = 0; i < codes.Count; ++i)
        {
            // Look for the pattern that begins the effect block:
            //   ldarg.0
            //   ldfld m_removeEffect
            if (i < codes.Count - 1 && codes[i].opcode == OpCodes.Ldarg_0 && codes[i + 1].opcode == OpCodes.Ldfld && codes[i + 1].operand is FieldInfo field && field == removeEffectField)
            {
                newCodes.Add(new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(TrueInstantLootDropPlugin), nameof(TrueInstantLootDropPlugin.IsDeathPoofEnabled))));
                // If false, branch to the skip label.
                newCodes.Add(new CodeInstruction(OpCodes.Brfalse, skipEffectLabel));
                
                while (i < codes.Count)
                {
                    newCodes.Add(codes[i]);
                    if (codes[i].opcode == OpCodes.Callvirt &&
                        codes[i].operand is MethodInfo mi && mi == createMethod)
                    {
                        // If the next instruction is a pop, then include it.
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
                    codes[i + 1].labels ??= new List<Label>();
                    codes[i + 1].labels.Add(skipEffectLabel);
                    labelAssigned = true;
                }

                continue;
            }

            newCodes.Add(codes[i]);
        }

        // If the skip label was never attached, append a NOP with the label.
        if (labelAssigned) return newCodes;
        CodeInstruction nop = new(OpCodes.Nop);
        nop.labels.Add(skipEffectLabel);
        newCodes.Add(nop);

        return newCodes;
    }
}