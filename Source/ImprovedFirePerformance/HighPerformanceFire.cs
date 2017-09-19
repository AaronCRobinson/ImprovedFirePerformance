using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace ImprovedFirePerformance
{
    public static class HighPerformanceFire
    {
        public const float MinFireSize = 0.1f;
        private const float MaxFireSize = 1.75f;
        private const float TicksBetweenSparksBase = 150f; // NOTE: this seems misnamed... (but is vanilla)
        private const int TicksToBurnFloor = 7500;
        private const int ComplexCalcsInterval = 150;
        private const float MinSizeForIgniteMovables = 0.4f;
        private const float HeatPerFireSizePerInterval = 160f;
        private const float HeatFactorWhenDoorPresent = 0.15f;
        private const float SnowClearRadiusPerFireSize = 3f;
        private const float FireBaseGrowthPerTick = 0.00055f;

        private static List<Thing> flammableList = new List<Thing>();
        private static float flammabilityMax;

        // PERFORMANCE
        private static Map map;
        private static Thing thing;
        private static TerrainDef curTerrain;
        
        // CACHING
        public static int fireCount = 0;

        // NOTE: ticksUntilSmoke is only being used for fire glow now
        public static void Tick(Fire f, ref int ticksUntilSmoke, ref int ticksSinceSpawn, ref int ticksSinceSpread)
        {
#if debug
            if (!f.Spawned) Log.Error("fire not spawned!");
#endif
            map = f.Map;

            if (f.fireSize > 1f)
            {
                ticksSinceSpread++;
                // ticksSinceSpread >= SpreadInterval
                if ((float)ticksSinceSpread >= TicksBetweenSparksBase - (f.fireSize - 1f) * 40f)
                {
                    f.TrySpread();
                    ticksSinceSpread = 0;
                }

            }

            if (f.IsHashIntervalTick(ComplexCalcsInterval))
            {
                if (f.parent == null)
                {
                    f.DoComplexOrphanCalcs();

                    // NOTE: moved here to avoid extra check on parent
                    //private void SpawnSmokeParticles()
                    ticksUntilSmoke--;
                    if (ticksUntilSmoke <= 0)
                    {
                        // RimWorld.Fire.SpawnSmokeParticles()
                        if (fireCount < 15)
                            MoteMaker.ThrowSmoke(f.DrawPos, map, f.fireSize);

                        if (f.fireSize > 0.5f)
                            MoteMaker.ThrowFireGlow(f.Position, map, f.fireSize);

                        float num = 1f - f.fireSize / 2f;
                        if (num < 0 ) num = 0;

                        ticksUntilSmoke = SmokeIntervalRangeLerp(num);
                    }

                }     
                else
                {
                    f.DoComplexParentedCalcs();
                }     
            }

            // TODO: find a better way to do this.
            if (f.parent == null)
            {
                ticksSinceSpawn++;
                if(ticksSinceSpawn >= TicksToBurnFloor)
                {
                    // RimWorld.Fire.TryMakeFloorBurned()
                    curTerrain = f.Position.GetTerrain(map);
                    TerrainDef burnedDef = curTerrain?.burnedDef;

                    if (burnedDef != null && curTerrain.Flammable())
                    {
                        TerrainGrid terrainGrid = map.terrainGrid;
                        terrainGrid.RemoveTopLayer(f.Position, false);
                        terrainGrid.SetTerrain(f.Position, burnedDef);
                    }
                }
            }
        }

        // NOTE: cutting out FireBulwark checks of TerrainFlammableNow
        // NOTE: I have discussed `FireBulwark`s extensively and they appear to be doing nothing... (or so little it's not noticable)
        /*private static bool TerrainFlammable(this IntVec3 c, Map map)
        {
            return c.GetTerrain(map).Flammable()
        }*/

        private const int lerpMin = 130;
        private const float lerpDiff = 70f; // max - min
        private static int SmokeIntervalRangeLerp(float lerpFactor)
        {
            // NOTE: removing some Rand here
            return lerpMin + Mathf.RoundToInt(lerpFactor * lerpDiff); // + (int)(10f * Rand.Value);
        }

        // RimWorld.Fire.TrySpread()
        private static void TrySpread(this Fire f)
        {
            IntVec3 originalPos = f.Position;

            IntVec3 intVec;
            bool flag;            

            if (Rand.Chance(0.8f))
            {
                intVec = originalPos + GenRadial.ManualRadialPattern[Rand.RangeInclusive(1, 8)];
                flag = true;
            }
            else
            {
                intVec = originalPos + GenRadial.ManualRadialPattern[Rand.RangeInclusive(10, 20)];
                flag = false;
            }

            if (!intVec.InBounds(map)) return;

            if (Rand.Chance(FireUtility.ChanceToStartFireIn(intVec, map)))
            {
                if (!flag)
                {
                    if (!GenSight.LineOfSight(originalPos, intVec, map, CellRect.SingleCell(originalPos), CellRect.SingleCell(intVec))) return;

                    Spark spark = (Spark)GenSpawn.Spawn(ThingDefOf.Spark, originalPos, map);
                    spark.Launch(f, intVec, null);
                }
                else
                {
                    FireUtility.TryStartFireIn(intVec, map, Fire.MinFireSize);
                }
            }
        }

        private static void DoComplexParentedCalcs(this Fire f)
        {
            flammabilityMax = 0f;

            if (!f.Position.GetTerrain(f.Map).HasTag("Water"))
            {
                flammabilityMax = f.parent.GetStatValue(StatDefOf.Flammability, true);
            }

            if (flammabilityMax < 0.01f) // min flammability to continue
            {
                f.Destroy(DestroyMode.Vanish);
                return;
            }

            f.DoFireDamage(f.parent);

            if (f.Spawned)
            {
                GenTemperature.PushHeat(f.Position, f.Map, f.fireSize * HeatPerFireSizePerInterval);
                f.DoFireGrowthCalcs();
                if ((double)f.Map.weatherManager.RainRate <= 0.01f || !f.VulnerableToRain())
                    return;
                f.DoRainDamageCalcs();
            }
        }

        private static void DoFireGrowthCalcs(this Fire f)
        {
            // NOTE: reduce change to do snow radial (should be a better way to do this...)
            if (Rand.Value < 0.1f) // if (Rand.Value < 0.4f)
            {
                float radius = f.fireSize * SnowClearRadiusPerFireSize;
                SnowUtility.AddSnowRadial(f.Position, f.Map, radius, -(f.fireSize * 0.1f));
            }
            f.fireSize += FireBaseGrowthPerTick * flammabilityMax * 150f;

            if (f.fireSize > MaxFireSize) f.fireSize = MaxFireSize;
        }

        private static void DoRainDamageCalcs(this Fire f)
        {
            // Verse.Thing.public void TakeDamage(DamageInfo dinfo)
            // Verse.DamageWorker.Apply(DamageInfo dinfo, Thing victim) -- ALL THAT's LEFT MUAWHAHAHAHA
            if (Rand.Value > 0.9f) ImpactSoundUtility.PlayImpactSound(f, DamageDefOf.Extinguish.impactSoundType, f.Map);

            //public override void PostApplyDamage(DamageInfo dinfo, float totalDamageDealt)
            f.fireSize -= 0.1f;
            if (f.fireSize <= MinFireSize) f.Destroy(DestroyMode.Vanish);
        }

        private static void DoComplexOrphanCalcs(this Fire f)
        {
            bool doorPresent = false;
            HighPerformanceFire.flammableList.Clear();
            flammabilityMax = 0f;
            curTerrain = f.Position.GetTerrain(map);

            if (!curTerrain.HasTag("Water"))
            {
                if (curTerrain.Flammable())
                {
                    flammabilityMax = curTerrain.GetStatValueAbstract(StatDefOf.Flammability, null);
                }
                List<Thing> list = map.thingGrid.ThingsListAt(f.Position);
                for (int i = 0; i < list.Count; i++)
                {
                    thing = list[i];
                    if (thing is Building_Door) doorPresent = true;

                    float statValue = thing.GetStatValue(StatDefOf.Flammability, true);
                    if (statValue >= 0.01f) // min flammability to continue
                    {
                        HighPerformanceFire.flammableList.Add(list[i]);
                        if (statValue > flammabilityMax) flammabilityMax = statValue;

                        if (f.fireSize >= MinSizeForIgniteMovables && list[i].def.category == ThingCategory.Pawn)
                        {
                            list[i].TryAttachFire(f.fireSize * 0.2f);
                        }
                    }
                }
            }

            if (flammabilityMax < 0.01f) // min flammability to continue
            {
                f.Destroy(DestroyMode.Vanish);
                return;
            }

            if (HighPerformanceFire.flammableList.Count > 0) // NOTE: why would this be empty?
                thing = HighPerformanceFire.flammableList.RandomElement<Thing>();
            else
                thing = null;

            if (thing != null && (f.fireSize >= MinSizeForIgniteMovables || thing.def.category != ThingCategory.Pawn))
                f.DoFireDamage(thing);

            if (f.Spawned)
            {
                float num = f.fireSize * HeatPerFireSizePerInterval;
                if (doorPresent) num *= HeatFactorWhenDoorPresent;
                GenTemperature.PushHeat(f.Position, map, num);

                f.DoFireGrowthCalcs();

                if ((double)f.Map.weatherManager.RainRate <= 0.01f || !f.VulnerableToRain())
                    return;

                f.DoRainDamageCalcs();
            }
        }

        private static void DoFireDamage(this Fire f, Thing targ)
        {
            //float num = 0.0161f * f.fireSize; //float num = 0.0125f + 0.0036f * f.fireSize;
            //num = Mathf.Clamp(num, 0.0125f, 0.05f);
            //int num2 = GenMath.RoundRandom(num * 150f); // TODO: fix this

            // TODO: any room left for improvement here?
            int num = GenMath.RoundRandom(Mathf.Clamp(0.0161f * f.fireSize, 0.0125f, 0.05f) * 150f); 
            if (num < 1) num = 1;

            if (targ is Pawn)
            {
                Pawn pawn = targ as Pawn;
                DamageInfo dinfo = new DamageInfo(DamageDefOf.Flame, num, -1f, f, null, null, DamageInfo.SourceCategory.ThingOrUnknown);
                dinfo.SetBodyRegion(BodyPartHeight.Undefined, BodyPartDepth.Outside);
                targ.TakeDamage(dinfo);

                if (pawn.apparel != null && pawn.apparel.WornApparel.TryRandomElement(out var apparel))
                    apparel.TakeDamageFast(num);
            }
            else
            {
                targ.TakeDamageFast(num);
            }
        }

        private static void TakeDamageFast(this Thing targ, int num2)
        {
            // RISK: skipping "is destroyed" check. 
            // NOTE: ignoring damageMultipliers
            // NOTE: no PreApplyDamage
            // RISK: skipping "is spawned" check.
            // NOTE: ignoring damageWatcher
            // NOTE: fire is not filthy, it's very very pretty.
            //DamageDefOf.Flame.Worker.Apply(dinfo, targ); STILL TOO MUCH... must go deeper

            targ.HitPoints -= num2;
            if (targ.HitPoints <= 0) targ.Kill(null);

            // NOTE: no PostApplyDamage
        }

        // REFERENCE: RimWorld.Fire.VulnerableToRain()
        private static bool VulnerableToRain(this Fire f)
        {
            // RISK: skipping "is spawned" check.
            RoofDef roofDef = f.Map.roofGrid.RoofAt(f.Position);
            if (roofDef == null) return true;
            if (roofDef.isThickRoof) return false;
            return f.Position.GetEdifice(f.Map)?.def.holdsRoof == true;
        }
    }

}
