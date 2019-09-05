using System;
using System.Collections.Generic;
using System.Text;
using Sandbox.Common.ObjectBuilders;
using Sandbox.Game.Entities;
using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;

namespace Digi.GravityCollector
{
    [MyEntityComponentDescriptor(typeof(MyObjectBuilder_Collector), false, "MediumGravityCollector", "LargeGravityCollector")]
    public class GravityCollector : MyGameLogicComponent
    {
        public const float RANGE_MIN = 0;
        public const float RANGE_MAX_MEDIUM = 40;
        public const float RANGE_MAX_LARGE = 60;
        public const float RANGE_OFF_EXCLUSIVE = 1;

        public const float STRENGTH_MIN = 1;
        public const float STRENGTH_MAX = 200;

        public const int APPLY_FORCE_SKIP_TICKS = 3; // how many ticks between applying forces to floating objects
        public const double MAX_VIEW_RANGE_SQ = 500 * 500; // max distance that the cone and pulsing item sprites can be seen from, squared value.

        public const float MASS_MUL = 10; // multiply item mass to get force
        public const float MAX_MASS = 5000; // max mass to multiply

        public const string CONTROLS_PREFIX = "GravityCollector.";
        public readonly Guid SETTINGS_GUID = new Guid("0DFC6F70-310D-4D1C-A55F-C57913E20389");
        public const int SETTINGS_CHANGED_COUNTDOWN = (60 * 1) / 10; // div by 10 because it runs in update10

        public float Range
        {
            get { return Settings.Range; }
            set
            {
                Settings.Range = MathHelper.Clamp((int)Math.Floor(value), RANGE_MIN, maxRange);
                SettingsChanged();
            }
        }

        public float Strength
        {
            get { return Settings.Strength; }
            set
            {
                Settings.Strength = MathHelper.Clamp(value, STRENGTH_MIN / 100f, STRENGTH_MAX / 100f);
                SettingsChanged();
            }
        }

        IMyCollector block;

        public readonly GravityCollectorBlockSettings Settings = new GravityCollectorBlockSettings();
        int syncCountdown;

        double coneAngle;
        float offset;
        float maxRange;

        int skipTicks;
        List<IMyFloatingObject> floatingObjects;

        GravityCollectorMod Mod => GravityCollectorMod.Instance;

        bool DrawCone
        {
            get
            {
                if(MyAPIGateway.Utilities.IsDedicated || !block.ShowOnHUD)
                    return false;

                var relation = block.GetPlayerRelationToOwner();

                return (relation != MyRelationsBetweenPlayerAndBlock.Enemies);
            }
        }

        public override void Init(MyObjectBuilder_EntityBase objectBuilder)
        {
            NeedsUpdate = MyEntityUpdateEnum.BEFORE_NEXT_FRAME;
        }

        public override void UpdateOnceBeforeFrame()
        {
            try
            {
                SetupTerminalControls<IMyCollector>();

                block = (IMyCollector)Entity;

                if(block.CubeGrid?.Physics == null)
                    return;

                floatingObjects = new List<IMyFloatingObject>();

                switch(block.BlockDefinition.SubtypeId)
                {
                    case "MediumGravityCollector":
                        maxRange = RANGE_MAX_MEDIUM;
                        coneAngle = MathHelper.ToRadians(30);
                        offset = 0.75f;
                        break;
                    case "LargeGravityCollector":
                        maxRange = RANGE_MAX_LARGE;
                        coneAngle = MathHelper.ToRadians(25);
                        offset = 1.5f;
                        break;
                }

                NeedsUpdate = MyEntityUpdateEnum.EACH_FRAME | MyEntityUpdateEnum.EACH_10TH_FRAME;

                // set default settings
                Settings.Strength = 1.0f;
                Settings.Range = maxRange;

                if(!LoadSettings())
                {
                    ParseLegacyNameStorage();
                }

                SaveSettings(); // required for IsSerialized()
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

                floatingObjects?.Clear();
                floatingObjects = null;

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
                SyncSettings();
                FindFloatingObjects();
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void FindFloatingObjects()
        {
            var entities = Mod.Entities;
            entities.Clear();
            floatingObjects.Clear();

            if(Range < RANGE_OFF_EXCLUSIVE || !block.IsWorking || !block.CubeGrid.Physics.Enabled)
            {
                if((NeedsUpdate & MyEntityUpdateEnum.EACH_FRAME) != 0)
                {
                    UpdateEmissive(false);
                    NeedsUpdate &= ~MyEntityUpdateEnum.EACH_FRAME;
                }

                return;
            }

            if((NeedsUpdate & MyEntityUpdateEnum.EACH_FRAME) == 0)
                NeedsUpdate |= MyEntityUpdateEnum.EACH_FRAME;

            var collectPos = block.WorldMatrix.Translation + (block.WorldMatrix.Forward * offset);
            var sphere = new BoundingSphereD(collectPos, Range + 10);

            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, entities, MyEntityQueryType.Dynamic);

            foreach(var ent in entities)
            {
                var floatingObject = ent as IMyFloatingObject;

                if(floatingObject != null && floatingObject.Physics != null)
                    floatingObjects.Add(floatingObject);
            }

            entities.Clear();
        }

        private Color prevColor;

        void UpdateEmissive(bool pulling = false)
        {
            var color = Color.Red;
            float strength = 0f;

            if(block.IsWorking)
            {
                strength = 1f;

                if(pulling)
                    color = Color.Cyan;
                else
                    color = new Color(10, 255, 0);
            }

            if(prevColor == color)
                return;

            prevColor = color;
            block.SetEmissiveParts("Emissive", color, strength);
        }

        public override void UpdateAfterSimulation()
        {
            try
            {
                if(Range < RANGE_OFF_EXCLUSIVE)
                    return;

                bool applyForce = false;
                if(++skipTicks >= APPLY_FORCE_SKIP_TICKS)
                {
                    skipTicks = 0;
                    applyForce = true;
                }

                if(!applyForce && MyAPIGateway.Utilities.IsDedicated)
                    return;

                var conePos = block.WorldMatrix.Translation + (block.WorldMatrix.Forward * -offset);
                bool inViewRange = false;

                if(!MyAPIGateway.Utilities.IsDedicated)
                {
                    var cameraMatrix = MyAPIGateway.Session.Camera.WorldMatrix;
                    inViewRange = Vector3D.DistanceSquared(cameraMatrix.Translation, conePos) <= MAX_VIEW_RANGE_SQ;

                    if(inViewRange && DrawCone)
                        DrawInfluenceCone(conePos);
                }

                if(!applyForce && !inViewRange)
                    return;

                if(floatingObjects.Count == 0)
                    return;

                var collectPos = block.WorldMatrix.Translation + (block.WorldMatrix.Forward * offset);
                var blockVel = block.CubeGrid.Physics.GetVelocityAtPoint(collectPos);
                var rangeSq = Range * Range;
                int pulling = 0;

                for(int i = (floatingObjects.Count - 1); i >= 0; --i)
                {
                    var floatingObject = floatingObjects[i];

                    if(floatingObject.MarkedForClose || !floatingObject.Physics.Enabled)
                        continue; // it'll get removed by FindFloatingObjects()

                    var objPos = floatingObject.GetPosition();
                    var distSq = Vector3D.DistanceSquared(collectPos, objPos);

                    if(distSq > rangeSq)
                        continue; // too far from cone

                    var dirNormalized = Vector3D.Normalize(objPos - conePos);
                    var angle = Math.Acos(MathHelper.Clamp(Vector3D.Dot(block.WorldMatrix.Forward, dirNormalized), -1, 1));

                    if(angle > coneAngle)
                        continue; // outside of the cone's FOV

                    if(applyForce)
                    {
                        var collectDir = Vector3D.Normalize(objPos - collectPos);

                        var vel = floatingObject.Physics.LinearVelocity - blockVel;
                        var stop = vel - (collectDir * collectDir.Dot(vel));
                        var force = -(stop + collectDir) * Math.Min(floatingObject.Physics.Mass * MASS_MUL, MAX_MASS) * Strength;

                        force *= APPLY_FORCE_SKIP_TICKS; // multiplied by how many ticks were skipped

                        //MyTransparentGeometry.AddLineBillboard(Mod.MATERIAL_SQUARE, Color.Yellow, objPos, force, 1f, 0.1f);

                        floatingObject.Physics.AddForce(MyPhysicsForceType.APPLY_WORLD_FORCE, force, null, null);
                    }

                    if(inViewRange)
                    {
                        var mul = (float)Math.Sin(DateTime.UtcNow.TimeOfDay.TotalMilliseconds * 0.01);
                        var radius = floatingObject.Model.BoundingSphere.Radius * MinMaxPercent(0.75f, 1.25f, mul);

                        MyTransparentGeometry.AddPointBillboard(Mod.MATERIAL_DOT, Color.LightSkyBlue * MinMaxPercent(0.2f, 0.4f, mul), objPos, radius, 0);
                    }

                    pulling++;
                }

                UpdateEmissive(pulling > 0);
            }
            catch(Exception e)
            {
                Log.Error(e);
            }
        }

        void DrawInfluenceCone(Vector3D conePos)
        {
            Vector4 color = Color.Cyan.ToVector4() * 10;
            Vector4 planeColor = (Color.White * 0.1f).ToVector4();
            const float LINE_THICK = 0.02f;
            const int WIRE_DIV_RATIO = 16;

            var coneMatrix = block.WorldMatrix;
            coneMatrix.Translation = conePos;

            //MyTransparentGeometry.AddPointBillboard(Mod.MATERIAL_DOT, Color.Lime, collectPos, 0.05f, 0);

            float rangeOffset = Range + (offset * 2); // because range check starts from collectPos but cone starts from conePos
            float baseRadius = rangeOffset * (float)Math.Tan(coneAngle);

            //MySimpleObjectDraw.DrawTransparentCone(ref coneMatrix, baseRadius, rangeWithOffset, ref color, 16, Mod.MATERIAL_SQUARE);

            var apexPosition = coneMatrix.Translation;
            var directionVector = coneMatrix.Forward * rangeOffset;
            var maxPosCenter = conePos + coneMatrix.Forward * rangeOffset;
            var baseVector = coneMatrix.Up * baseRadius;

            Vector3 axis = directionVector;
            axis.Normalize();

            float stepAngle = (float)(Math.PI * 2.0 / (double)WIRE_DIV_RATIO);

            var prevConePoint = apexPosition + directionVector + Vector3.Transform(baseVector, Matrix.CreateFromAxisAngle(axis, (-1 * stepAngle)));
            prevConePoint = (apexPosition + Vector3D.Normalize((prevConePoint - apexPosition)) * rangeOffset);

            var quad = default(MyQuadD);

            for(int step = 0; step < WIRE_DIV_RATIO; step++)
            {
                var conePoint = apexPosition + directionVector + Vector3.Transform(baseVector, Matrix.CreateFromAxisAngle(axis, (step * stepAngle)));
                var lineDir = (conePoint - apexPosition);
                lineDir.Normalize();
                conePoint = (apexPosition + lineDir * rangeOffset);

                MyTransparentGeometry.AddLineBillboard(Mod.MATERIAL_SQUARE, color, conePoint, (prevConePoint - conePoint), 1f, LINE_THICK);

                MyTransparentGeometry.AddLineBillboard(Mod.MATERIAL_SQUARE, color, apexPosition, lineDir, rangeOffset, LINE_THICK);

                MyTransparentGeometry.AddLineBillboard(Mod.MATERIAL_SQUARE, color, conePoint, (maxPosCenter - conePoint), 1f, LINE_THICK);

                // Unusable because SQUARE has reflectivity and this method uses materials' reflectivity... making it unable to be made transparent, also reflective xD
                //var normal = Vector3.Up;
                //MyTransparentGeometry.AddTriangleBillboard(
                //    apexPosition, prevConePoint, conePoint,
                //    normal, normal, normal,
                //    new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1),
                //    Mod.MATERIAL_SQUARE, uint.MaxValue, conePoint, planeColor);
                // also NOTE: if triangle is used, color needs .ToLinearRGB().

                quad.Point0 = prevConePoint;
                quad.Point1 = conePoint;
                quad.Point2 = apexPosition;
                quad.Point3 = apexPosition;
                MyTransparentGeometry.AddQuad(Mod.MATERIAL_SQUARE, ref quad, planeColor, ref Vector3D.Zero);

                quad.Point0 = prevConePoint;
                quad.Point1 = conePoint;
                quad.Point2 = maxPosCenter;
                quad.Point3 = maxPosCenter;
                MyTransparentGeometry.AddQuad(Mod.MATERIAL_SQUARE, ref quad, planeColor, ref Vector3D.Zero);

                prevConePoint = conePoint;
            }
        }

        bool LoadSettings()
        {
            if(block.Storage == null)
                return false;

            string rawData;
            if(!block.Storage.TryGetValue(SETTINGS_GUID, out rawData))
                return false;

            try
            {
                var loadedSettings = MyAPIGateway.Utilities.SerializeFromBinary<GravityCollectorBlockSettings>(Convert.FromBase64String(rawData));

                if(loadedSettings != null)
                {
                    Settings.Range = loadedSettings.Range;
                    Settings.Strength = loadedSettings.Strength;
                    return true;
                }
            }
            catch(Exception e)
            {
                Log.Error($"Error loading settings!\n{e}");
            }

            return false;
        }

        bool ParseLegacyNameStorage()
        {
            string name = block.CustomName.TrimEnd(' ');

            if(!name.EndsWith("]", StringComparison.Ordinal))
                return false;

            int startIndex = name.IndexOf('[');

            if(startIndex == -1)
                return false;

            var settingsStr = name.Substring(startIndex + 1, name.Length - startIndex - 2);

            if(settingsStr.Length == 0)
                return false;

            string[] args = settingsStr.Split(';');

            if(args.Length == 0)
                return false;

            string[] data;

            foreach(string arg in args)
            {
                data = arg.Split('=');

                float f;
                int i;

                if(data.Length == 2)
                {
                    switch(data[0])
                    {
                        case "range":
                            if(int.TryParse(data[1], out i))
                                Range = i;
                            break;
                        case "str":
                            if(float.TryParse(data[1], out f))
                                Strength = f;
                            break;
                    }
                }
            }

            block.CustomName = name.Substring(0, startIndex).Trim();
            return true;
        }

        void SaveSettings()
        {
            if(block.Storage == null)
                block.Storage = new MyModStorageComponent();

            block.Storage.SetValue(SETTINGS_GUID, Convert.ToBase64String(MyAPIGateway.Utilities.SerializeToBinary(Settings)));
        }

        void SettingsChanged()
        {
            if(syncCountdown == 0)
                syncCountdown = SETTINGS_CHANGED_COUNTDOWN;
        }

        void SyncSettings()
        {
            if(syncCountdown > 0 && --syncCountdown <= 0)
            {
                SaveSettings();

                Mod.CachedPacketSettings.Send(block.EntityId, Settings);
            }
        }

        public override bool IsSerialized()
        {
            // called when the game iterates components to check if they should be serialized, before they're actually serialized.
            // this does not only include saving but also streaming and blueprinting.
            // NOTE for this to work reliably the MyModStorageComponent needs to already exist in this block with at least one element.

            SaveSettings();
            return false;
        }

        /// <summary>
        /// Returns the specified percentage multiplier (0 to 1) between min and max.
        /// </summary>
        static float MinMaxPercent(float min, float max, float percentMul)
        {
            return min + (percentMul * (max - min));
        }

        #region Terminal controls
        static void SetupTerminalControls<T>()
        {
            var mod = GravityCollectorMod.Instance;

            if(mod.ControlsCreated)
                return;

            mod.ControlsCreated = true;

            var controlRange = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, T>(CONTROLS_PREFIX + "Range");
            controlRange.Title = MyStringId.GetOrCompute("Pull Range");
            controlRange.Tooltip = MyStringId.GetOrCompute("Max distance the cone extends to.");
            controlRange.Visible = Control_Visible;
            controlRange.SupportsMultipleBlocks = true;
            controlRange.SetLimits(Control_Range_Min, Control_Range_Max);
            controlRange.Getter = Control_Range_Getter;
            controlRange.Setter = Control_Range_Setter;
            controlRange.Writer = Control_Range_Writer;
            MyAPIGateway.TerminalControls.AddControl<T>(controlRange);

            var controlStrength = MyAPIGateway.TerminalControls.CreateControl<IMyTerminalControlSlider, T>(CONTROLS_PREFIX + "Strength");
            controlStrength.Title = MyStringId.GetOrCompute("Pull Strength");
            controlStrength.Tooltip = MyStringId.GetOrCompute($"Formula used:\nForce = Min(ObjectMass * {MASS_MUL}, {MAX_MASS}) * Strength");
            controlStrength.Visible = Control_Visible;
            controlStrength.SupportsMultipleBlocks = true;
            controlStrength.SetLimits(STRENGTH_MIN, STRENGTH_MAX);
            controlStrength.Getter = Control_Strength_Getter;
            controlStrength.Setter = Control_Strength_Setter;
            controlStrength.Writer = Control_Strength_Writer;
            MyAPIGateway.TerminalControls.AddControl<T>(controlStrength);
        }

        static GravityCollector GetLogic(IMyTerminalBlock block) => block?.GameLogic?.GetAs<GravityCollector>();

        static bool Control_Visible(IMyTerminalBlock block)
        {
            return GetLogic(block) != null;
        }

        static float Control_Strength_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? STRENGTH_MIN : logic.Strength * 100);
        }

        static void Control_Strength_Setter(IMyTerminalBlock block, float value)
        {
            var logic = GetLogic(block);
            if(logic != null)
                logic.Strength = ((int)value / 100f);
        }

        static void Control_Strength_Writer(IMyTerminalBlock block, StringBuilder writer)
        {
            var logic = GetLogic(block);
            if(logic != null)
                writer.Append((int)(logic.Strength * 100f)).Append('%');
        }

        static float Control_Range_Getter(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? 0 : logic.Range);
        }

        static void Control_Range_Setter(IMyTerminalBlock block, float value)
        {
            var logic = GetLogic(block);
            if(logic != null)
                logic.Range = (int)Math.Floor(value);
        }

        static float Control_Range_Min(IMyTerminalBlock block)
        {
            return RANGE_MIN;
        }

        static float Control_Range_Max(IMyTerminalBlock block)
        {
            var logic = GetLogic(block);
            return (logic == null ? 0 : logic.maxRange);
        }

        static void Control_Range_Writer(IMyTerminalBlock block, StringBuilder writer)
        {
            var logic = GetLogic(block);
            if(logic != null)
            {
                if(logic.Range < RANGE_OFF_EXCLUSIVE)
                    writer.Append("OFF");
                else
                    writer.Append(logic.Range.ToString("N2")).Append(" m");
            }
        }
        #endregion
    }
}