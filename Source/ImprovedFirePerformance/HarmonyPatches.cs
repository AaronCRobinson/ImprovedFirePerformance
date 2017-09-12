using Harmony;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using System.Reflection.Emit;
using Verse.Sound;

namespace NoFirewatcher
{
    [StaticConstructorOnStartup]
    class HarmonyPatches
    {

        static HarmonyPatches()
        {
#if DEBUG
            HarmonyInstance.DEBUG = true;
#endif

            HarmonyInstance harmony = HarmonyInstance.Create("rimworld.whyisthat.nofirewatcher.main");

            harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.DoSingleTick)), new HarmonyMethod(typeof(HarmonyPatches), nameof(TickManagerPrefix)), null);
            harmony.Patch(AccessTools.Method(typeof(Fire), nameof(Fire.Tick)), null, null, new HarmonyMethod(typeof(HarmonyPatches), nameof(FireTickTranspiler)));

            harmony.Patch(AccessTools.Method(typeof(Fire), "TrySpread"), null, null, new HarmonyMethod(typeof(HarmonyPatches), nameof(TrySpread_ManualRadialPatternRangeFix)));

#if DEBUG
            harmony.Patch(AccessTools.Method(typeof(Game), nameof(Game.UpdatePlay)), new HarmonyMethod(typeof(HarmonyPatches), nameof(StartWatch)), new HarmonyMethod(typeof(HarmonyPatches), nameof(StopWatch)));
#endif
        }

        public static void TickManagerPrefix()
        {
            Traverse t = Traverse.Create(typeof(Fire));
            t.Property("fireCount").SetValue(Find.VisibleMap.listerThings.ThingsOfDef(ThingDefOf.Fire).Count);
        }

        private static FieldInfo ticksUntilSmokeFieldInfo = AccessTools.Field(typeof(Fire), "ticksUntilSmoke");
        private static FieldInfo ticksSinceSpawnFieldInfo = AccessTools.Field(typeof(Fire), "ticksSinceSpawn");
        private static FieldInfo ticksSinceSpreadFieldInfo = AccessTools.Field(typeof(Fire), "ticksSinceSpread");
        private static MethodInfo highPerformanceFireTickMethodInfo = AccessTools.Method(typeof(HighPerformanceFire), nameof(HighPerformanceFire.Tick));

        public static IEnumerable<CodeInstruction> FireTickTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            // consider traverse?
            List<CodeInstruction> instructionList = instructions.ToList<CodeInstruction>();

            // if structure => turns on HighPerformanceFire fixes when LargeFireDangerPresent
            yield return new CodeInstruction(OpCodes.Ldarg_0); //this
            yield return new CodeInstruction(OpCodes.Call, AccessTools.Property(typeof(Thing), nameof(Thing.Map)).GetGetMethod());
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Map), nameof(Map.fireWatcher)));
            yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Property(typeof(FireWatcher), nameof(FireWatcher.LargeFireDangerPresent)).GetGetMethod());

            Label elseLabel = il.DefineLabel();
            yield return new CodeInstruction(OpCodes.Brfalse, elseLabel); // branch

            // NOTE: having issues passing sustainer
            yield return new CodeInstruction(OpCodes.Ldarg_0); //this
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Fire), "sustainer"));
            Label jump = il.DefineLabel();
            yield return new CodeInstruction(OpCodes.Brfalse, jump);

            yield return new CodeInstruction(OpCodes.Ldarg_0); //this
            yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Fire), "sustainer"));
            yield return new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(Sustainer), nameof(Sustainer.Maintain)));

            // this begins arguments for call to highPerformanceFireTick
            yield return new CodeInstruction(OpCodes.Ldarg_0) { labels = new List<Label>() { jump } }; //this
            yield return new CodeInstruction(OpCodes.Dup); // this
            yield return new CodeInstruction(OpCodes.Ldflda, ticksUntilSmokeFieldInfo);

            yield return new CodeInstruction(OpCodes.Ldarg_0); // this
            yield return new CodeInstruction(OpCodes.Ldflda, ticksSinceSpawnFieldInfo);

            yield return new CodeInstruction(OpCodes.Ldarg_0); // this
            yield return new CodeInstruction(OpCodes.Ldflda, ticksSinceSpreadFieldInfo);

            yield return new CodeInstruction(OpCodes.Call, highPerformanceFireTickMethodInfo);

            Label returnLabel = il.DefineLabel();
            yield return new CodeInstruction(OpCodes.Br, returnLabel);

            // handle labels
            instructionList[0].labels.Add(elseLabel); // else
            instructionList[instructionList.Count - 1].labels.Add(returnLabel); // end if

            int i;
            for (i = 0; i < instructionList.Count; i++) yield return instructionList[i];
        }
      
        private static FieldInfo FI_ManualRadialPattern = AccessTools.Field(typeof(GenRadial), nameof(GenRadial.ManualRadialPattern));
        public static IEnumerable<CodeInstruction> TrySpread_ManualRadialPatternRangeFix(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> instructionList = instructions.ToList<CodeInstruction>();

            for (int i = 0; i < instructionList.Count; i++)
            {
                yield return instructionList[i];
                if (instructionList[i].opcode == OpCodes.Ldsfld && instructionList[i].operand == FI_ManualRadialPattern)
                {
                    i++;
                    yield return new CodeInstruction(OpCodes.Ldc_I4_S, 9);
                }
            }

        }

        #region debugging
#if DEBUG
        static System.Diagnostics.Stopwatch watch;

        public static void StartWatch()
        {
            watch = new System.Diagnostics.Stopwatch();
            watch.Start();
        }

        public static void StopWatch()
        {
            watch.Stop();
            Log.Message("Time: " + watch.ElapsedTicks.ToString());
        }
#endif
        #endregion

    }

}
