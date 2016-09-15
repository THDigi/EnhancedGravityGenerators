using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using VRage.ObjectBuilders;
using VRage.Game.Components;
using VRage.ModAPI;

using Digi.Utils;

namespace Digi.EnhancedGravityGenerators
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class EnhancedGravityGenerators : MySessionComponentBase
    {
        public override void LoadData()
        {
            Log.SetUp("Enhanced Gravity Generators", 464877997, "EnhancedGravityGenerators");
        }

        public static bool init { get; private set; }
        private int skip = SKIP_SCAN;
        private int skipPlanets = SKIP_PLANETS;

        private static Dictionary<long, GravityGeneratorLogic> gravityGenerators = new Dictionary<long, GravityGeneratorLogic>();
        public static Dictionary<long, MyPlanet> planets = new Dictionary<long, MyPlanet>();
        private static HashSet<IMyEntity> ents = new HashSet<IMyEntity>(); // this is always empty

        private const int SKIP_SCAN = 10;
        private const int SKIP_PLANETS = 60 * 3;
        public const float G = 9.81f;

        public void Init()
        {
            Log.Init();
            init = true;
        }

        protected override void UnloadData()
        {
            try
            {
                if(init)
                {
                    init = false;
                    ents.Clear();
                    gravityGenerators.Clear();
                    GravityGeneratorLogic.CleanStatic();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }

            Log.Close();
        }

        public static void AddGenerator(long entityId, GravityGeneratorLogic obj)
        {
            if(gravityGenerators.ContainsKey(entityId))
                return;

            gravityGenerators.Add(entityId, obj);
        }

        public static void RemoveGenerator(long entityId)
        {
            gravityGenerators.Remove(entityId);
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if(!init)
                {
                    if(MyAPIGateway.Session == null)
                        return;

                    Init();
                }

                if(++skip > SKIP_SCAN)
                {
                    skip = 0;
                    ents.Clear();
                    MyAPIGateway.Entities.GetEntities(ents, e => e.Physics != null && e.Physics.Enabled && e is IMyCubeGrid && !e.IsVolumetric && !e.Physics.IsPhantom);

                    foreach(var gg in gravityGenerators.Values)
                    {
                        gg.SlowUpdate(ents);
                    }

                    ents.Clear();
                }

                if(++skipPlanets % SKIP_PLANETS == 0)
                {
                    skipPlanets = 0;
                    planets.Clear();

                    MyAPIGateway.Entities.GetEntities(ents, delegate (IMyEntity e)
                                                      {
                                                          if(e is MyPlanet && !planets.ContainsKey(e.EntityId))
                                                          {
                                                              planets.Add(e.EntityId, e as MyPlanet);
                                                          }

                                                          return false; // no reason to add to the list
                                                      });
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGenerator))]
    public class GravityGeneratorFlat : GravityGeneratorBlock { }

    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_GravityGeneratorSphere))]
    public class GravityGeneratorSphere : GravityGeneratorBlock { }

    public class GravityGeneratorBlock : MyGameLogicComponent
    {
        private MyObjectBuilder_EntityBase objectBuilder;
        private GravityGeneratorLogic handle = null;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            this.objectBuilder = objectBuilder;
            Entity.NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;
        }

        public override void UpdateAfterSimulation()
        {
            if(handle == null)
            {
                var block = Entity as IMyGravityGeneratorBase;

                if(block != null)
                {
                    handle = new GravityGeneratorLogic(block);
                    EnhancedGravityGenerators.AddGenerator(Entity.EntityId, handle);
                }
            }
            else
            {
                handle.Update();
            }
        }

        public override void Close()
        {
            handle = null;
            objectBuilder = null;
        }

        public override MyObjectBuilder_EntityBase GetObjectBuilder(bool copy = false)
        {
            return copy ? (MyObjectBuilder_EntityBase)objectBuilder.Clone() : objectBuilder;
        }
    }

    public class GravityGeneratorLogic
    {
        private IMyGravityGeneratorBase block;
        private bool affectSmallShips = false;
        private bool affectLargeShips = false;
        private bool counterPush = false;
        private bool spherical = false;
        private float gravity;
        private float radiusSq;
        private MyOrientedBoundingBoxD rangeBox;
        private List<IMyEntity> entsInField = new List<IMyEntity>();

        private static List<IMySlimBlock> slimBlocks = new List<IMySlimBlock>();

        public GravityGeneratorLogic(IMyGravityGeneratorBase block)
        {
            this.block = block;
            block.OnClose += Close;
            var terminalBlock = block as IMyTerminalBlock;
            terminalBlock.CustomNameChanged += NameChanged;
            terminalBlock.AppendingCustomInfo += AppendCustomInfo;
            NameChanged(null);
            SlowUpdate(null);
        }

        public static void CleanStatic()
        {
            slimBlocks.Clear();
        }

        public void Update()
        {
            if(!Enabled() || entsInField.Count == 0)
                return;

            var point = block.WorldMatrix.Translation;
            Vector3D dir;
            float naturalForce = 0;

            // adjust gravity in relation to nearby planets just like the vanilla gravity generators are affected
            if(EnhancedGravityGenerators.planets.Count > 0)
            {
                Vector3 naturalDir = Vector3.Zero;

                foreach(var kv in EnhancedGravityGenerators.planets)
                {
                    var planet = kv.Value;

                    if(planet.Closed || planet.MarkedForClose)
                        continue;

                    var planetDir = planet.PositionComp.GetPosition() - point;
                    var gravComp = planet.Components.Get<MyGravityProviderComponent>() as MySphericalNaturalGravityComponent;

                    if(planetDir.LengthSquared() <= gravComp.GravityLimitSq)
                    {
                        planetDir.Normalize();
                        naturalDir += planetDir * gravComp.GetGravityMultiplier(point);
                    }
                }

                naturalForce = naturalDir.Length();
            }

            foreach(var ent in entsInField)
            {
                if(ent.Closed || ent.MarkedForClose)
                    continue;

                if(spherical)
                    dir = Vector3D.Normalize(point - ent.Physics.CenterOfMassWorld);
                else
                    dir = block.WorldMatrix.Down;

                dir *= (gravity * EnhancedGravityGenerators.G) * ent.Physics.Mass;

                if(naturalForce > 0)
                    dir *= MathHelper.Clamp(1f - naturalForce * 2f, 0f, 1f);

                if(dir.LengthSquared() > 0)
                {
                    ent.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, dir, ent.Physics.CenterOfMassWorld, null);

                    if(counterPush)
                    {
                        (block.CubeGrid as IMyEntity).Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, -dir, ent.Physics.CenterOfMassWorld, null);
                    }
                }
            }
        }

        public void SlowUpdate(HashSet<IMyEntity> ents)
        {
            if(!Enabled())
                return;

            if(block is IMyGravityGeneratorSphere)
            {
                var gg = (block as IMyGravityGeneratorSphere);
                spherical = true;
                gravity = gg.Gravity / EnhancedGravityGenerators.G; // HACK spherical grav gens are 0-9.81 and flat grav gens are 0-1
                radiusSq = gg.Radius;
                radiusSq = radiusSq * radiusSq;
            }
            else if(block is IMyGravityGenerator)
            {
                var gg = (block as IMyGravityGenerator);
                spherical = false;
                gravity = gg.Gravity;
                rangeBox = new MyOrientedBoundingBoxD(gg.WorldMatrix);
                rangeBox.HalfExtent = new Vector3D(gg.FieldWidth / 2, gg.FieldHeight / 2, gg.FieldDepth / 2);
            }

            if(ents != null)
            {
                entsInField.Clear();

                foreach(var ent in ents)
                {
                    AddEntityInRange(ent);
                }
            }
        }

        private void AddEntityInRange(IMyEntity ent)
        {
            if(ent.Physics == null || ent.Physics.IsStatic)
                return;

            var grid = ent as IMyCubeGrid;

            if(grid == null || grid == block.CubeGrid
               || (affectSmallShips == false && grid.GridSizeEnum == MyCubeSize.Small)
               || (affectLargeShips == false && grid.GridSizeEnum == MyCubeSize.Large))
                return;

            var entPos = ent.Physics.CenterOfMassWorld;
            var blockPos = block.GetPosition();

            if(spherical)
            {
                if(Vector3D.DistanceSquared(entPos, blockPos) <= radiusSq)
                {
                    entsInField.Add(ent);
                }
            }
            else
            {
                if(rangeBox.Contains(ref entPos))
                {
                    entsInField.Add(ent);
                }
            }
        }

        public bool Enabled()
        {
            return block.IsWorking && (affectSmallShips || affectLargeShips);
        }

        public void NameChanged(IMyTerminalBlock not_used)
        {
            string name = block.CustomName.ToLower();

            affectSmallShips = name.Contains("@small");
            affectLargeShips = name.Contains("@large");
            counterPush = name.Contains("@counterpush");

            (block as IMyTerminalBlock).RefreshCustomInfo();
        }

        public void AppendCustomInfo(IMyTerminalBlock not_used, StringBuilder info)
        {
            info.AppendLine();
            info.AppendLine("Enhanced Gravity Generators flags:");
            info.Append("@small is ").Append(affectSmallShips ? "On" : "Off").AppendLine();
            info.Append("@large is ").Append(affectLargeShips ? "On" : "Off").AppendLine();
            info.Append("@counterpush is ").Append(counterPush ? "On" : "Off").AppendLine();
            info.AppendLine("Add flags to the block's name to enable them, separated by spaces.");
        }

        public void Close(IMyEntity ent)
        {
            EnhancedGravityGenerators.RemoveGenerator(block.EntityId);
            block.OnClose -= Close;

            var terminalBlock = block as IMyTerminalBlock;
            terminalBlock.CustomNameChanged -= NameChanged;
            terminalBlock.AppendingCustomInfo -= AppendCustomInfo;

            entsInField.Clear();
            slimBlocks.Clear();
            block = null;
        }
    }
}