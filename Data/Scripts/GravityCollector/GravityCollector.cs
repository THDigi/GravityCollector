using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Definitions;
using Sandbox.Common.ObjectBuilders;
using Sandbox.ModAPI;
using VRage.ObjectBuilders;
using VRage.Game.ModAPI;
using VRage.Game.Components;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Game;

namespace Digi.GravityCollector
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Collector), false, "MediumGravityCollector", "LargeGravityCollector")]
    public class GravityCollector : MyGameLogicComponent
    {
        private IMyCollector block;
        private float offset;
        private int maxDist;
        private double cone;
        private float rangeSq;
        private float strength;
        private bool skip = true;
        private List<MyEntity> entities = new List<MyEntity>();
        private List<IMyFloatingObject> floatingObjects = new List<IMyFloatingObject>();

        private const float MAX_STRENGTH = 2.0f;
        private const int MAX_MASS = 5000;

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            block = Entity as IMyCollector;
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                if(block == null || block.CubeGrid.Physics == null)
                    return;

                NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;

                switch(block.BlockDefinition.SubtypeId)
                {
                    case "MediumGravityCollector":
                        maxDist = 15;
                        cone = 30;
                        offset = 0.75f;
                        break;
                    case "LargeGravityCollector":
                        maxDist = 50;
                        cone = 25;
                        offset = 1.5f;
                        break;
                }

                strength = 1.0f;
                rangeSq = maxDist * maxDist;
                cone = Math.Cos(cone * (Math.PI / 180));

                string name = block.CustomName.Trim();

                if(!name.EndsWith("]", StringComparison.Ordinal))
                    block.CustomName = name + " [range=" + maxDist + ";str=1.0]";

                block.CustomNameChanged += NameChanged;
                block.AppendingCustomInfo += CustomInfo;
                block.CubeGrid.PositionComp.OnPositionChanged += OnPositionChanged;

                NameChanged(block);
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
                if(block == null)
                    return;

                block.CustomNameChanged -= NameChanged;
                block.AppendingCustomInfo -= CustomInfo;
                block.CubeGrid.PositionComp.OnPositionChanged -= OnPositionChanged;
                block = null;
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
                entities.Clear();
                floatingObjects.Clear();

                if(block.IsWorking)
                {
                    var sphere = new BoundingSphereD(block.WorldMatrix.Translation, maxDist + 10);

                    MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, entities, MyEntityQueryType.Dynamic);

                    foreach(var ent in entities)
                    {
                        var floatingObject = ent as IMyFloatingObject;

                        if(floatingObject != null)
                        {
                            floatingObjects.Add(floatingObject);
                        }
                    }

                    entities.Clear();
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                skip = !skip;

                if(skip)
                    return; // skip half of the ticks

                if(!block.IsWorking || !block.CubeGrid.Physics.Enabled)
                    return;

                var conePos = block.WorldMatrix.Translation + (block.WorldMatrix.Forward * -offset);
                var collectPos = block.WorldMatrix.Translation + (block.WorldMatrix.Forward * offset);
                var blockVel = block.CubeGrid.Physics.GetVelocityAtPoint(collectPos);

                foreach(var floatingObject in floatingObjects)
                {
                    if(floatingObject.Closed)
                        continue;

                    var entPos = floatingObject.GetPosition();
                    var distSq = Vector3D.DistanceSquared(collectPos, entPos);

                    if(distSq <= rangeSq)
                    {
                        var dir = Vector3D.Normalize(entPos - conePos);
                        var dot = block.WorldMatrix.Forward.Dot(dir);

                        if(dot > cone)
                        {
                            var vel = floatingObject.Physics.LinearVelocity - blockVel;
                            var stop = vel - (dir * dir.Dot(vel));
                            var force = -(stop + dir) * Math.Min(floatingObject.Physics.Mass * 10, MAX_MASS) * strength;
                            floatingObject.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, force * 2, null, null); // multiplied by 2 because it runs at 30 ticks instead of 60
                        }
                    }
                }
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        public void CustomInfo(IMyTerminalBlock block, StringBuilder info)
        {
            var def = (MyPoweredCargoContainerDefinition)block.SlimBlock.BlockDefinition;

            info.Append("Power usage: ");
            MyValueFormatter.AppendWorkInBestUnit(def.RequiredPowerInput, info);
            info.Append("\n\nGravity settings:\n");
            info.AppendFormat("Range = {0}m of {1}m\n", Math.Round(Math.Sqrt(rangeSq), 2), maxDist);
            info.AppendFormat("Strength = {0}%\n", Math.Round(strength * 100, 0));
            info.Append("Edit the settings in the block's name.\n");
        }

        public void NameChanged(IMyTerminalBlock block)
        {
            try
            {
                string name = block.CustomName.TrimEnd(' ');

                if(!name.EndsWith("]", StringComparison.Ordinal))
                    return;

                int startIndex = name.IndexOf('[');

                if(startIndex == -1)
                    return;

                name = name.Substring(startIndex + 1, name.Length - startIndex - 2);

                if(name.Length == 0)
                    return;

                string[] args = name.Split(';');

                if(args.Length == 0)
                    return;

                string[] data;

                foreach(string arg in args)
                {
                    data = arg.Split('=');

                    if(data.Length == 2)
                    {
                        float f;

                        switch(data[0])
                        {
                            case "range":
                                if(float.TryParse(data[1], out f))
                                {
                                    rangeSq = MathHelper.Clamp(f, 1, maxDist);
                                    rangeSq *= rangeSq;
                                }
                                break;
                            case "str":
                                if(float.TryParse(data[1], out f))
                                    strength = MathHelper.Clamp(f, 0.0f, MAX_STRENGTH);
                                break;
                        }
                    }
                }

                block.RefreshCustomInfo();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        private void OnPositionChanged(MyPositionComponentBase positionComp)
        {
            block?.Physics?.OnWorldPositionChanged(null); // HACK fix for collector's physics not moving with it, bugreport: SE-7720
        }
    }
}