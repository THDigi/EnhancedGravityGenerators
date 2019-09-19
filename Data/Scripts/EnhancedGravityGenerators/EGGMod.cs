using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.ModAPI;

// TODO: counterpush should also affect mass blocks
// TODO: a way to enforce counterpush server side
// TODO: block-box resolution field checking? ( GetEntitiesIn*() instead of GetTopMostEntitiesIn*() )
// TODO: parallel just like sensor does it
// TODO: implement terminal controls, saving, synching...

namespace Digi.EnhancedGravityGenerators
{
    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]
    public class EGGMod : MySessionComponentBase
    {
        public static EGGMod Instance = null;

        private bool init = false;
        private int skipPlanets = SKIP_PLANETS;

        public List<MyPlanet> Planets = new List<MyPlanet>();
        public List<MyEntity> Entities = new List<MyEntity>();
        private HashSet<IMyEntity> EntitiesModAPI = new HashSet<IMyEntity>();

        private const int SKIP_PLANETS = 60 * 10;

        public override void LoadData()
        {
            Instance = this;
            Log.SetUp("Enhanced Gravity Generators", 464877997, "EnhancedGravityGenerators");
        }

        protected override void UnloadData()
        {
            Instance = null;
            init = false;
            Planets.Clear();
            Log.Close();
        }

        public override void UpdateBeforeSimulation()
        {
            try
            {
                if(!init)
                {
                    if(MyAPIGateway.Session == null)
                        return;

                    Log.Init();
                    init = true;
                }

                if(++skipPlanets >= SKIP_PLANETS)
                {
                    skipPlanets = 0;
                    Planets.Clear();
                    Entities.Clear();

                    MyAPIGateway.Entities.GetEntities(EntitiesModAPI, e =>
                    {
                        var p = e as MyPlanet;

                        if(p != null)
                            Planets.Add(p);

                        return false; // no reason to add to the list
                    });

                    Entities.Clear();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }
    }
}