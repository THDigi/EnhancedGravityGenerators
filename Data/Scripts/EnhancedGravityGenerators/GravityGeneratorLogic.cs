using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRageMath;

namespace Digi.EnhancedGravityGenerators
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGenerator), false)]
    public class GravityGeneratorFlat : GravityGeneratorBase { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGeneratorSphere), false)]
    public class GravityGeneratorSphere : GravityGeneratorBase { }

    public class GravityGeneratorBase : MyGameLogicComponent
    {
        private IMyGravityGeneratorBase block;
        private bool affectSmallShips = false;
        private bool affectLargeShips = false;
        private bool counterPush = false;
        private bool spherical = false;
        private float gravGenAccel;
        private float naturalForce;
        private List<IMyCubeGrid> gridsInRange = new List<IMyCubeGrid>();

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            block = (IMyGravityGeneratorBase)Entity;
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if(block.CubeGrid.Physics == null)
                    return;

                block.CustomNameChanged += NameChanged;
                block.AppendingCustomInfo += AppendCustomInfo;
                NameChanged(block);

                NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void Close()
        {
            try
            {
                block.CustomNameChanged -= NameChanged;
                block.AppendingCustomInfo -= AppendCustomInfo;
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if(!Enabled() || gridsInRange.Count == 0)
                    return;

                var point = block.WorldMatrix.Translation;

                foreach(var grid in gridsInRange)
                {
                    if(grid.Closed)
                        continue;

                    Vector3D dir;

                    if(spherical)
                        dir = Vector3D.Normalize(point - grid.Physics.CenterOfMassWorld);
                    else
                        dir = block.WorldMatrix.Down;

                    float forceMultiplier = gravGenAccel * grid.Physics.Mass;

                    if(naturalForce > 0)
                        forceMultiplier *= MathHelper.Clamp(1f - naturalForce * 2f, 0f, 1f); // HACK: hardcoded formula from the game for natural gravity affecting gravity generators

                    var applyForceAt = grid.Physics.CenterOfMassWorld;
                    var forceVec = dir * forceMultiplier;

                    grid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, forceVec, applyForceAt, null);

                    if(counterPush)
                        block.CubeGrid.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, -forceVec, applyForceAt, null);
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void UpdateBeforeSimulation10()
        {
            try
            {
                if(!Enabled())
                    return;

                var gravGenPos = block.GetPosition();
                var sphericalGravGen = block as IMyGravityGeneratorSphere;

                spherical = (sphericalGravGen != null);
                gravGenAccel = block.GravityAcceleration;

                if(spherical)
                {
                    var sphere = new BoundingSphereD(gravGenPos, sphericalGravGen.Radius);

                    var ents = EGGMod.Instance.Entities;
                    ents.Clear();
                    gridsInRange.Clear();
                    MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, ents, MyEntityQueryType.Dynamic);

                    foreach(var ent in ents)
                    {
                        var grid = CheckAndGet(ent);

                        if(grid == null)
                            continue;

                        var obb = new MyOrientedBoundingBoxD(grid.LocalAABB, grid.WorldMatrix);

                        if(obb.Intersects(ref sphere))
                        {
                            gridsInRange.Add(grid);
                        }
                    }

                    ents.Clear();
                }
                else
                {
                    var flatGravGen = (IMyGravityGenerator)block;
                    gravGenAccel = flatGravGen.GravityAcceleration;
                    var rangeBox = new MyOrientedBoundingBoxD(flatGravGen.WorldMatrix);
                    rangeBox.HalfExtent = flatGravGen.FieldSize / 2;

                    var worldBB = rangeBox.GetAABB();

                    var ents = EGGMod.Instance.Entities;
                    ents.Clear();
                    gridsInRange.Clear();
                    MyGamePruningStructure.GetTopMostEntitiesInBox(ref worldBB, ents, MyEntityQueryType.Dynamic);

                    foreach(var ent in ents)
                    {
                        var grid = CheckAndGet(ent);

                        if(grid == null)
                            continue;

                        var obb = new MyOrientedBoundingBoxD(grid.LocalAABB, grid.WorldMatrix);

                        if(obb.Intersects(ref rangeBox))
                        {
                            gridsInRange.Add(grid);
                        }
                    }

                    ents.Clear();
                }

                // adjust gravity in relation to nearby planets just like the vanilla gravity generators are affected
                if(EGGMod.Instance.Planets.Count > 0)
                {
                    Vector3 naturalDir = Vector3.Zero;

                    foreach(var planet in EGGMod.Instance.Planets)
                    {
                        if(planet.Closed)
                            continue;

                        var planetDir = planet.PositionComp.WorldVolume.Center - gravGenPos;
                        var gravComp = (MySphericalNaturalGravityComponent)planet.Components.Get<MyGravityProviderComponent>();

                        if(planetDir.LengthSquared() <= gravComp.GravityLimitSq) // distance check
                        {
                            planetDir.Normalize();
                            naturalDir += planetDir * gravComp.GetGravityMultiplier(gravGenPos);
                        }
                    }

                    naturalForce = (Vector3.IsZero(naturalDir) ? 0 : naturalDir.Length());
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private IMyCubeGrid CheckAndGet(MyEntity ent)
        {
            if(ent.Physics == null || !ent.Physics.Enabled || ent.Physics.IsStatic || ent.IsPreview)
                return null;

            var grid = ent as IMyCubeGrid;

            if(grid == null
            || grid == block.CubeGrid
            || (affectSmallShips == false && grid.GridSizeEnum == MyCubeSize.Small)
            || (affectLargeShips == false && grid.GridSizeEnum == MyCubeSize.Large))
                return null;

            return grid;
        }

        public bool Enabled()
        {
            return ((IMyTerminalBlock)block).IsWorking && (affectSmallShips || affectLargeShips);
        }

        public void NameChanged(IMyTerminalBlock block)
        {
            try
            {
                string name = block.CustomName.ToLower();

                affectSmallShips = name.Contains("@small");
                affectLargeShips = name.Contains("@large");
                counterPush = name.Contains("@counterpush");

                block.RefreshCustomInfo();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void AppendCustomInfo(IMyTerminalBlock not_used, StringBuilder info)
        {
            try
            {
                info.AppendLine();
                info.AppendLine("Enhanced Gravity Generators flags:");
                info.Append("@small is ").Append(affectSmallShips ? "On" : "Off").AppendLine();
                info.Append("@large is ").Append(affectLargeShips ? "On" : "Off").AppendLine();
                info.Append("@counterpush is ").Append(counterPush ? "On" : "Off").AppendLine();
                info.AppendLine("Add flags to the block's name to enable them, separated by spaces.");
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}
