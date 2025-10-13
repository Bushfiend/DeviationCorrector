using Sandbox.Game;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game.VisualScripting.Missions;
using VRageMath;
using VRageRender;

namespace IngameScript
{
    partial class Program : MyGridProgram
    {

        //ArcBotCyberSelfActDestr
        const string MissileTag = "[pve]";
        const string ScriptGroupName = "[udr]";
        const string NukeTag = "[nuke]";
        const string ExcludeTag = "[exclude]";
        int[] guidanceOffset = { 100, 90, 80, 70, 60, 50, 40, 30 };
        int guidanceIndex = 0;

        bool SetupCompleted = false;

        bool HasRaycast = false;
        bool HasHud = false;
        bool InFireSequence = false;
        bool IsUsingRaycast = true;
        double DeltaTime = 0;

        RaycastHoming RC;

        List<MissileClass> ActiveMissiles = new List<MissileClass>();
        HashSet<IMyShipMergeBlock> ProtoMissiles = new HashSet<IMyShipMergeBlock>();
        static List<IMyShipController> Controllers = new List<IMyShipController>();
        List<IMyCameraBlock> RaycastArray = new List<IMyCameraBlock>();
        IMyTextPanel Hud;

        float RaycastRange = 10000;

        Random Rand = new Random();
        public long Ticks = 0;

        public Program()
        {
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }

        public void Main(string argument, UpdateType updateSource)
        {

            if (!Setup())
            {
                return;
            }
            DeltaTime = MathHelper.Clamp(Runtime.TimeSinceLastRun.TotalSeconds, 1.0 / 60.0, 0.25);


            if (Ticks % 20 == 0)
            {
                DisplayInfo();
            }


            if (InFireSequence)
            {
                InFireSequence = false;
                return;
            }

            UpdateMissiles(ActiveMissiles, DeltaTime);

            if (HasRaycast)
                RC.Update(1.0 / 60.0, RaycastArray, Controllers);

            HandleCommands(argument);

            GetMissiles(ref ProtoMissiles);
        }

        void HandleCommands(string argument)
        {
            if (argument == string.Empty)
                return;

            switch (argument)
            {
                case "fire":
                    FireCommand();
                    break;
                case "raycast":
                    Raycast();
                    break;
                case "detonate":
                    DetonateMissiles();
                    break;
            }
        }


        void Raycast()
        {
            if (!HasRaycast)
                return;

            if (RC.IsScanning || RC.Status == RaycastHoming.TargetingStatus.Locked)
                RC.ClearLock();
            else
                RC.LockOn();
        }

        void FireCommand()
        {
            var missile = GetMissile();
            if (missile == null || missile.Closed)
            {
                Echo("There are no missiles ready to fire.");
                return;
            }

            if (guidanceIndex++ == guidanceOffset.Length - 1)
                guidanceIndex = 0;

            var guidanceDelay = guidanceOffset[guidanceIndex];

            FireMissile(missile, guidanceDelay);
            InFireSequence = true;
        }

        string[] Ani = { ".", "..", "...", "....", ".....", };
        int cycle = 0;

        string Animate()
        {
            cycle++;
            if (cycle == Ani.Length)
                cycle = 0;

            return Ani[cycle];
        }
        void DisplayInfo()
        {
            if (!HasHud)
                return;

            var sb = new StringBuilder();
            sb.AppendLine($"-UDR Missile Script-");
            sb.AppendLine($"Active Missiles: {ActiveMissiles.Count}\n Missiles Left: {ProtoMissiles.Count}");
            sb.AppendLine($"DeltaTime {DeltaTime}");
            if (RC.IsScanning && RC.Status == RaycastHoming.TargetingStatus.NotLocked)
            {
                sb.AppendLine($"Scanning{Animate()}");
            }
            else if (RC.IsScanning && RC.Status == RaycastHoming.TargetingStatus.Locked)
            {
                sb.AppendLine($"Target Locked!");
                sb.AppendLine($"Name: {RC.Target.Name}");
                sb.AppendLine($"Distance: {Math.Round(Vector3D.Distance(RC.Target.Position, Me.GetPosition()))} m");
                sb.AppendLine($"Raycast Range: {RaycastRange}");
            }



            if (Hud == null || Hud.Closed)
                return;
            sb.AppendLine($"LastLock {RC.TicksSinceLastLock}");

            Hud.WriteText(sb);
        }


        void DetonateMissiles()
        {
            foreach (var missile in ActiveMissiles)
            {
                missile.ScuttleMissile();
            }
        }


        IMyShipMergeBlock GetMissile()
        {
            return ProtoMissiles.FirstOrDefault();
        }

        void FireMissile(IMyShipMergeBlock mergeBlock, int trackingDelay = 0)
        {
            if (ActiveMissiles.Exists(m => m.Merge == mergeBlock))
            {
                throw new Exception("Attempted to fire a missile that was already fired.");
            }

            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(blocks);

            var missile = new MissileClass(mergeBlock, blocks, $"Missile-{Rand.Next(1, 20000)}", trackingDelay);

            if (!HasRaycast || RC.Status != RaycastHoming.TargetingStatus.Locked)
                return;


            ActiveMissiles.Add(missile);
            ProtoMissiles.Remove(mergeBlock);
            missile.FireMissile();
            Echo("Missile launched!");
        }




        bool Setup()
        {
            if (SetupCompleted)
                return true;

            var scriptGroup = GridTerminalSystem.GetBlockGroupWithName(ScriptGroupName);

            if (scriptGroup == null)
            {
                Echo($"Setup Failed, no group named {ScriptGroupName} found.");
                return false;
            }

            var tempBlocks = new List<IMyTerminalBlock>();
            scriptGroup.GetBlocks(tempBlocks);


            var tempControllers = tempBlocks.FindAll(b => b is IMyShipController);

            if (tempControllers.Count == 0)
            {
                Echo("Setup Failed, No ship controllers found.");
                return false;
            }

            var cameras = tempBlocks.FindAll(b => b is IMyCameraBlock);
            if (cameras.Count > 0)
            {
                HasRaycast = true;
                foreach (var block in cameras)
                {
                    RaycastArray.Add(block as IMyCameraBlock);
                    ((IMyCameraBlock)block).EnableRaycast = true;
                }
                RC = new RaycastHoming(RaycastRange, 3, 250, Me.CubeGrid.EntityId);
                RC.OffsetTargeting = true;
                RC.AddEntityTypeToFilter(MyDetectedEntityType.FloatingObject, MyDetectedEntityType.Planet, MyDetectedEntityType.Asteroid);

                var _mechConnections = new List<IMyMechanicalConnectionBlock>();
                GridTerminalSystem.GetBlocksOfType(_mechConnections);
                RC.ClearIgnoredGridIDs();
                RC.AddIgnoredGridID(Me.CubeGrid.EntityId);
                foreach (var mc in _mechConnections)
                {
                    RC.AddIgnoredGridID(mc.CubeGrid.EntityId);
                    if (mc.TopGrid != null)
                        RC.AddIgnoredGridID(mc.TopGrid.EntityId);
                }
            }
            Hud = tempBlocks.FindAll(b => b is IMyTextPanel).First() as IMyTextPanel;
            if (Hud != null)
            {
                Hud.CustomData = "hudlcd:0.6:0.5::255,165,0";
                Hud.ContentType = ContentType.TEXT_AND_IMAGE;
                HasHud = true;
            }


            GridTerminalSystem.GetBlocksOfType(Controllers);
            Echo($"Setup Complete.");
            SetupCompleted = true;
            return true;
        }


        void UpdateMissiles(List<MissileClass> activeMissiles, double deltaTime)
        {

            activeMissiles.RemoveAll(m => m.Merge.Closed);
            activeMissiles.ForEach(m =>
            {
                m.Update(deltaTime);
                if (RC.Status == RaycastHoming.TargetingStatus.Locked && RC.Target.Position != Vector3D.Zero)
                {

                    m.Target = RC.Target;
                }
            });
        }

        void GetMissiles(ref HashSet<IMyShipMergeBlock> missiles)
        {
            var mergeBlocks = new List<IMyShipMergeBlock>();
            GridTerminalSystem.GetBlocksOfType(mergeBlocks);
            missiles.RemoveWhere(b => b.Closed || b == null);
            foreach (var block in mergeBlocks)
            {
                if (missiles.Contains(block))
                    continue;

                if (!(block is IMyShipMergeBlock))
                    continue;

                if (!IsMissileBlock(block))
                    continue;

                if (ActiveMissiles.Exists(active => active.Merge == block))
                    continue;

                missiles.Add(block);
            }
        }

        bool IsMissileBlock(IMyTerminalBlock block)
        {
            return block.CustomName.IndexOf(MissileTag, StringComparison.OrdinalIgnoreCase) >= 0;
        }


        class MissilePrinter
        {
            private List<IMyShipWelder> _welders = new List<IMyShipWelder>();
            private List<IMyShipConnector> _refillConnectors = new List<IMyShipConnector>();
            private List<IMyShipConnector> _nukeConnectors = new List<IMyShipConnector>();
            private List<IMyGasTank> _missileTanks = new List<IMyGasTank>();


        }

        class MissileClass
        {
            public IMyShipMergeBlock Merge { get; }
            public string Id { get; set; }
            public string HudText { get; set; }
            public long TargetId { get; set; }

            public bool Launched { get; set; }

            public MyDetectedEntityInfo Target { get; set; }

            public List<Vector3D> PathToTarget { get; set; }


            public Vector3D Position
            {
                get
                {
                    if (_remoteControl == null || _remoteControl.Closed)
                        return Vector3D.Zero;
                    return _remoteControl.GetPosition();
                }
            }

            public float DistanceToTarget
            {
                get
                {
                    if (_remoteControl == null || _remoteControl.Closed || Target.IsEmpty())
                        return float.PositiveInfinity;

                    return (float)DistanceToAabbSurface(Target.BoundingBox, Position);
                }
            }         

            private static double DistanceToAabbSurface(BoundingBoxD box, Vector3D p)
            {
                var clamped = Vector3D.Min(Vector3D.Max(p, box.Min), box.Max);
                double outsideDist = (p - clamped).Length();
                if (outsideDist > 1e-6)
                    return outsideDist;

                double dx = Math.Min(box.Max.X - p.X, p.X - box.Min.X);
                double dy = Math.Min(box.Max.Y - p.Y, p.Y - box.Min.Y);
                double dz = Math.Min(box.Max.Z - p.Z, p.Z - box.Min.Z);
                return Math.Max(0.0, Math.Min(dx, Math.Min(dy, dz)));
            }
            public bool MissileReady { get; private set; }

            public MissileClass(IMyShipMergeBlock mergeBlock, List<IMyTerminalBlock> blocks, string id = "Missile", int trackingDelay = 0)
            {
                Merge = mergeBlock;
                _blockList = blocks;
                Id = id;
                Launched = false;
                _trackingDelay = trackingDelay;
            }

            public void ScuttleMissile()
            {
                _nukeOverride = true;
            }

            public void FireMissile()
            {
                Launched = true;
                Merge.Enabled = false;
            }

            private bool Setup()
            {
                if (!_tickWaited)
                {
                    _tickWaited = true;
                    return false;
                }

                _blockList.RemoveAll(b => b == null || b.Closed);
                _blockList.RemoveAll(b => b.CubeGrid != Merge.CubeGrid);
                foreach (var block in _blockList)
                {

                    if (block.CustomName.Contains(ExcludeTag))
                        continue;

                    if (block is IMyShipConnector)
                    {
                        if (block.CustomName.Contains(NukeTag))
                        {
                            _nuke = new ConnectorNuke(block as IMyShipConnector);
                            _connectors.Add(block as IMyShipConnector);
                            continue;
                        }
                        _connectors.Add(block as IMyShipConnector);
                        ((IMyShipConnector)block).Disconnect();

                        continue;
                    }
                    if (block is IMyCargoContainer)
                    {
                        _cargoContainers.Add(block as IMyCargoContainer);

                        continue;
                    }
                    if (block is IMyRemoteControl)
                    {
                        _remotes.Add(block as IMyRemoteControl);
                        _remoteControl = block as IMyRemoteControl;
                        continue;
                    }
                    if (block is IMyGasTank)
                    {
                        _hydrogenTanks.Add(block as IMyGasTank);
                        ((IMyGasTank)block).Enabled = true;
                        ((IMyGasTank)block).Stockpile = false;
                        continue;
                    }
                    if (block is IMyThrust)
                    {
                        _thrusters.Add(block as IMyThrust);
                        ((IMyThrust)block).Enabled = true;
                        ((IMyThrust)block).ThrustOverridePercentage = 1f;
                        continue;
                    }
                    if (block is IMyWarhead)
                    {
                        _warheads.Add(block as IMyWarhead);
                        continue;
                    }
                    if (block is IMyGyro)
                    {
                        _gyros.Add(block as IMyGyro);
                        ((IMyGyro)block).Enabled = true;
                        continue;
                    }
                    if (block is IMyBatteryBlock)
                    {
                        _batteryBlocks.Add(block as IMyBatteryBlock);
                        ((IMyBatteryBlock)block).Enabled = true;
                        ((IMyBatteryBlock)block).ChargeMode = ChargeMode.Discharge;
                        continue;
                    }
                    if (block is IMyProgrammableBlock)
                    {
                        _programmableBlocks.Add(block as IMyProgrammableBlock);
                        ((IMyProgrammableBlock)block).Enabled = true;
                        continue;
                    }
                    if (block is IMySoundBlock)
                    {
                        _soundBlocks.Add(block as IMySoundBlock);
                        ((IMySoundBlock)block).Enabled = true;
                        ((IMySoundBlock)block).SelectedSound = "ArcBotCyberSelfActDestr";
                        ((IMySoundBlock)block).LoopPeriod = 500f;
                        ((IMySoundBlock)block).Play();
                        continue;
                    }
                    if (block is IMyTimerBlock)
                    {
                        _timerBlocks.Add(block as IMyTimerBlock);
                        ((IMyTimerBlock)block).Enabled = true;
                        ((IMyTimerBlock)block).Trigger();
                        continue;
                    }

                }

                if (_warheads.Count != 0 && _nuke != null)
                {
                    _nuke.Warheads = _warheads;
                }

                if (_remotes.Count > 0)
                {
                    _acceleration = _thrusters.Sum(t => t.MaxEffectiveThrust) / _remoteControl.CalculateShipMass().PhysicalMass;
                }
                _connectors.ForEach(c => c.Disconnect());

                return true;
            }

            public void Update(double deltaTime)
            {
                if (!Launched)
                    return;

                if (!_missileSetup)
                {
                    _missileSetup = Setup();
                    if (_missileSetup)
                        _connectors.ForEach(c => c.Enabled = false);
                    return;
                }
                if (_trackingDelay > _ticksSinceLaunch)
                {
                    _ticksSinceLaunch++;
                    return;
                }

                _nukeOverride = _hydrogenTanks.Count == 0 ? true : (_hydrogenTanks.Sum(t => t.FilledRatio) / _hydrogenTanks.Count) <= 0.0;

                _deltatime = deltaTime;
                RangeCheck();

                if (_remoteControl == null || _remoteControl.Closed)
                    ScuttleMissile();

                Guide(_remoteControl);
            }

            private void RangeCheck()
            {
                if (_nuke != null)
                    _nuke.Update();

                if (DistanceToTarget < _detonationDistance || (_nukeOverride && Vector3D.Distance(_remoteControl.GetPosition(), Controllers.FirstOrDefault().GetPosition()) > 300))
                {
                    if (_nuke != null)
                        _nuke.Detonate();
                }
            }


            private void Guide(IMyRemoteControl rc)
            {
                if (rc == null || rc.Closed || Target.IsEmpty())
                    return;

                Vector3D mPos = rc.GetPosition();
                Vector3D mVel = rc.GetShipVelocities().LinearVelocity;

                Vector3 targetAccel = (Target.Velocity - _lastTargetVelocity) * _deltatime;
                _lastTargetVelocity = Target.Velocity;

                Vector3D toTgt = Target.Position - mPos;
                Vector3D toTgtDir = SafeNormalize(toTgt);

                Vector3D gVec = ComputeIntercept(mPos, mVel, Target.Position, Target.Velocity, targetAccel, rc.GetNaturalGravity());

                Vector3D pointDir = SafeNormalize(gVec);
                if (pointDir == Vector3D.Zero)
                    pointDir = toTgtDir;

                Vector3D relVel = Target.Velocity - mVel;
                double Vc = -Vector3D.Dot(relVel, toTgtDir);
                if (Vc <= 0.0)
                {
                    pointDir = SafeNormalize(0.65 * pointDir + 0.35 * toTgtDir);
                }

                ApplyGyroOverride(pointDir, rc.WorldMatrix.Up, rc.WorldMatrix);
                foreach (var thruster in _thrusters)
                {
                    double alignment = Vector3D.Dot(thruster.WorldMatrix.Backward, pointDir);
                    thruster.ThrustOverridePercentage = (alignment > 0.72) ? (float)alignment : 0.001f;
                }
            }


            private void ApplyGyroOverride(Vector3D direction, Vector3D upVector, Matrix reference)
            {
                double yaw, pitch, roll;
                ComputeRotationAngles(direction, upVector, reference, out yaw, out pitch, out roll);

                var shipLocalRot = new Vector3D(-pitch, yaw, roll);
                var worldRot = Vector3D.TransformNormal(shipLocalRot, reference);
                foreach (var gyro in _gyros)
                {
                    var gyroLocal = Vector3D.TransformNormal(worldRot, Matrix.Transpose(gyro.WorldMatrix));

                    gyro.Pitch = (float)(gyroLocal.X * GyroScaler);
                    gyro.Yaw = (float)(gyroLocal.Y * GyroScaler);
                    gyro.Roll = (float)(gyroLocal.Z * GyroScaler);
                    gyro.GyroOverride = true;
                }
            }

            private void ComputeRotationAngles(Vector3D direction, Vector3D upVector, MatrixD worldMatrix, out double yaw, out double pitch, out double roll)
            {
                var localDir = Vector3D.Rotate(direction, MatrixD.Transpose(worldMatrix));
                var flatDir = new Vector3D(localDir.X, 0, localDir.Z);

                yaw = AngleBetween(Vector3D.Forward, flatDir) * Math.Sign(localDir.X);
                if (Math.Abs(yaw) < 1E-6 && localDir.Z > 0) yaw = Math.PI;

                pitch = Vector3D.IsZero(flatDir)
                    ? MathHelper.PiOver2 * Math.Sign(localDir.Y)
                    : AngleBetween(localDir, flatDir) * Math.Sign(localDir.Y);

                if (Vector3D.IsZero(upVector))
                {
                    roll = 0;
                    return;
                }

                var localUp = Vector3D.Rotate(upVector, MatrixD.Transpose(worldMatrix));
                var flatUp = new Vector3D(localUp.X, localUp.Y, 0);

                roll = AngleBetween(flatUp, Vector3D.Up) * Math.Sign(Vector3D.Dot(Vector3D.Right, flatUp));
            }




            private Vector3D ComputeIntercept(Vector3D missilePos, Vector3D missileVel,Vector3D targetPos, Vector3D targetVel, Vector3D targetAccel, Vector3D gravity)
            {
                Vector3D displacement = targetPos - missilePos;
                Vector3D dirToTarget = SafeNormalize(displacement);
                if (dirToTarget == Vector3D.Zero) return Vector3D.Zero;

                Vector3D relVel = targetVel - missileVel;

                Vector3D accelCorrection = targetAccel - Vector3D.Dot(targetAccel, dirToTarget) * dirToTarget;
                Vector3D gravityCorrection = 1.1 * -(gravity - Vector3D.Dot(gravity, dirToTarget) * dirToTarget);

                // Lead term
                Vector3D cross = Vector3D.Cross(displacement, relVel) / Math.Max(displacement.LengthSquared(), 1);
                Vector3D velAlongTarget = Vector3D.Dot(relVel, dirToTarget) * dirToTarget;
                Vector3D lateralVel = relVel - velAlongTarget;

                Vector3D leadVector =
                    SteeringStrength * (relVel.Length() * Vector3D.Cross(cross, dirToTarget) + 0.1 * lateralVel) +
                    CorrectionStrength * accelCorrection +
                    gravityCorrection;

                if (!IsFinite(leadVector) || leadVector.LengthSquared() < 1e-10)
                    return dirToTarget;

                double aMax = Math.Max(_acceleration, 0.1);
                double aLen = leadVector.Length();
                Vector3D aCmd = (aLen > aMax) ? (leadVector * (aMax / aLen)) : leadVector;

                double spareSq = aMax * aMax - aCmd.LengthSquared();
                if (spareSq > 0)
                    aCmd += Math.Sqrt(spareSq) * dirToTarget;

                return aCmd;
            }

            private static bool IsFinite(Vector3D x)
            {
                return !(double.IsNaN(x.X) || double.IsNaN(x.Y) || double.IsNaN(x.Z) ||
                         double.IsInfinity(x.X) || double.IsInfinity(x.Y) || double.IsInfinity(x.Z));
            }

            private static Vector3D SafeNormalize(Vector3D v, double eps = 1e-9)
            {
                double ls = v.LengthSquared();
                if (ls < eps || double.IsNaN(ls) || double.IsInfinity(ls)) return Vector3D.Zero;
                return v / Math.Sqrt(ls);
            }
     
            private static double AngleBetween(Vector3D a, Vector3D b)
            {
                if (Vector3D.IsZero(a) || Vector3D.IsZero(b))
                    return 0;
                return Math.Acos(MathHelper.Clamp(a.Dot(b) / Math.Sqrt(a.LengthSquared() * b.LengthSquared()), -1, 1));
            }

            ConnectorNuke _nuke;

            private Vector3D _tVelFilt = Vector3D.Zero;
            private bool _tVelInit = false;
            private const double _tVelSmoothing = 0.6;

            const double SteeringStrength = 4.5;
            const double CorrectionStrength = 7.5;
            const double GyroScaler = 1.3;

            private double _deltatime;

            private bool _missileSetup = false;

            private double _detonationDistance = 70;

            private bool _nukeOverride = false;
            private int _trackingDelay = 100;
            private int _ticksSinceLaunch = 0;

            private double _acceleration = 0;

            private List<IMyTerminalBlock> _blockList = new List<IMyTerminalBlock>(); //To filter on launch
            private Vector3D _lastTargetVelocity = Vector3D.Zero;
            private IMyRemoteControl _remoteControl = null;
            private List<IMyTimerBlock> _timerBlocks = new List<IMyTimerBlock>();
            private List<IMyRemoteControl> _remotes = new List<IMyRemoteControl>();
            private List<IMySoundBlock> _soundBlocks = new List<IMySoundBlock>();
            private List<IMyProgrammableBlock> _programmableBlocks = new List<IMyProgrammableBlock>();
            private List<IMyShipConnector> _connectors = new List<IMyShipConnector>();
            private List<IMyCargoContainer> _cargoContainers = new List<IMyCargoContainer>();
            private List<IMyGasTank> _hydrogenTanks = new List<IMyGasTank>();
            private List<IMyThrust> _thrusters = new List<IMyThrust>();
            private List<IMyWarhead> _warheads = new List<IMyWarhead>();
            private List<IMyGyro> _gyros = new List<IMyGyro>();
            private List<IMyBatteryBlock> _batteryBlocks = new List<IMyBatteryBlock>();
            private bool _tickWaited = false;
        }  
        public class Nuke 
        {
            private List<IMyWarhead> _warheads = new List<IMyWarhead>();
            private bool _detonating = false;
            public Nuke(List<IMyWarhead> warheads)
            {
                _warheads = warheads;
            }
            public void Prime(IList<IMyWarhead> warheads)
            {
                _warheads.Clear();
                if (warheads != null) _warheads.AddRange(warheads.Where(w => w != null && !w.Closed));
            }
            public void Detonate()
            {
                _detonating = true;
            }
            public void Update()
            {
                if (_detonating)
                    DetonateWarheads();
            }

            private void DetonateWarheads()
            {
                if (_warheads.Count == 0)
                    return;

                _warheads[0].Detonate();
                _warheads.RemoveAt(0);
            }
        }
        public class ConnectorNuke
        {
            public List<IMyWarhead> Warheads
            {
                get { return _warheads; }
                set { _warheads = value; }
            }

            public ConnectorNuke(IMyShipConnector connector)
            {
                _connector = connector;
                _inventory = _connector.GetInventory();
            }

            public void Update()
            {
                DetonateWarheads();
            }
            private void DetonateWarheads()
            {
                if (!_detonating)
                    return;
                _connector.Enabled = true;

                if (_inventory.ItemCount == 0)
                    foreach (var warhead in _warheads)
                        warhead.Detonate();
            }

            public void Detonate()
            {
                _connector.Enabled = true;
                _connector.ThrowOut = true;
                _detonating = true;
            }

            private IMyInventory _inventory;
            private bool _detonating = false;
            private IMyShipConnector _connector;
            private List<IMyWarhead> _warheads = new List<IMyWarhead>();
        }
        class RaycastHoming //thanks whiplash
        {
            public TargetingStatus Status { get; private set; } = TargetingStatus.NotLocked;
            public Vector3D TargetPosition
            {
                get
                {
                    return OffsetTargeting ? OffsetTargetPosition : TargetCenter;
                }
            }
            public MyDetectedEntityInfo Target
            {
                get { return _target; }
            }

            private MyDetectedEntityInfo _target;
            public double SearchScanSpread { get; set; } = 0;
            public Vector3D TargetCenter { get; private set; } = Vector3D.Zero;
            public Vector3D OffsetTargetPosition
            {
                get
                {
                    return TargetCenter + Vector3D.TransformNormal(PreciseModeOffset, _targetOrientation);
                }
            }
            public Vector3D TargetVelocity { get; private set; } = Vector3D.Zero;
            public Vector3D HitPosition { get; private set; } = Vector3D.Zero;
            public Vector3D PreciseModeOffset { get; private set; } = Vector3D.Zero;
            public string TargetName { get; private set; } = "";
            public bool OffsetTargeting = false;
            public bool MissedLastScan { get; private set; } = false;
            public bool LockLost { get; private set; } = false;
            public bool IsScanning { get; private set; } = false;
            public double TimeSinceLastLock { get; private set; } = 0;
            public long TicksSinceLastLock { get; private set; } = 0;
            public double TargetSize { get; private set; } = 0;
            public double MaxRange { get; private set; }
            public double MinRange { get; private set; }
            public long TargetId { get; private set; } = 0;
            public double AutoScanInterval { get; private set; } = 0;
            public double MaxTimeForLockBreak { get; private set; }
            public MyRelationsBetweenPlayerAndBlock TargetRelation { get; private set; }
            public MyDetectedEntityType TargetType { get; private set; }
            public long TimeSinceLastRaycast
            {
                get
                {
                    return (_currentTimeStamp - _lastTimeStamp);
                }
            }

            public enum TargetingStatus { NotLocked, Locked, TooClose };
            enum AimMode { Center, Offset, OffsetRelative };

            AimMode _currentAimMode = AimMode.Center;

            readonly HashSet<MyDetectedEntityType> _targetFilter = new HashSet<MyDetectedEntityType>();
            readonly List<IMyCameraBlock> _availableCameras = new List<IMyCameraBlock>();
            readonly Random _rngeesus = new Random();

            MatrixD _targetOrientation;
            HashSet<long> _gridIDsToIgnore = new HashSet<long>();
            double _timeSinceLastScan = 0;
            bool _manualLockOverride = false;
            bool _fudgeVectorSwitch = false;
            long _lastTimeStamp = 0;
            long _currentTimeStamp = 0;
            long _ticks = 0;
            double AutoScanScaleFactor
            {
                get
                {
                    return MissedLastScan ? 0.8 : 1.1;
                }
            }

            public RaycastHoming(double maxRange, double maxTimeForLockBreak = 3, double minRange = 0, long gridIDToIgnore = 0)
            {
                MinRange = minRange;
                MaxRange = maxRange;
                MaxTimeForLockBreak = maxTimeForLockBreak;
                AddIgnoredGridID(gridIDToIgnore);
            }

            public void SetInitialLockParameters(Vector3D hitPosition, Vector3D targetVelocity, Vector3D offset, double timeSinceLastLock, long targetId)
            {
                TargetCenter = hitPosition;
                HitPosition = hitPosition;
                PreciseModeOffset = offset;
                TargetVelocity = targetVelocity;
                TimeSinceLastLock = timeSinceLastLock;
                _manualLockOverride = true;
                IsScanning = true;
                TargetId = targetId;
            }

            public void AddIgnoredGridID(long id)
            {
                _gridIDsToIgnore.Add(id);
            }

            public void ClearIgnoredGridIDs()
            {
                _gridIDsToIgnore.Clear();
            }

            public void AddEntityTypeToFilter(params MyDetectedEntityType[] types)
            {
                foreach (var type in types)
                {
                    _targetFilter.Add(type);
                }
            }

            public void AcknowledgeLockLost()
            {
                LockLost = false;
            }

            public void LockOn()
            {
                ClearLockInternal();
                LockLost = false;
                IsScanning = true;
            }

            public void ClearLock()
            {
                ClearLockInternal();
                LockLost = false;
            }

            void ClearLockInternal()
            {
                IsScanning = false;
                Status = TargetingStatus.NotLocked;
                MissedLastScan = false;
                TimeSinceLastLock = 0;
                TicksSinceLastLock = 0;
                TargetSize = 0;
                HitPosition = Vector3D.Zero;
                TargetId = 0;
                _timeSinceLastScan = 0;
                _currentAimMode = AimMode.Center;
                TargetRelation = MyRelationsBetweenPlayerAndBlock.NoOwnership;
                TargetType = MyDetectedEntityType.None;
            }

            double RndDbl()
            {
                return 2 * _rngeesus.NextDouble() - 1;
            }

            double GaussRnd()
            {
                return (RndDbl() + RndDbl() + RndDbl()) / 3.0;
            }

            Vector3D CalculateFudgeVector(Vector3D targetDirection, double fudgeFactor = 5)
            {
                _fudgeVectorSwitch = !_fudgeVectorSwitch;

                if (!_fudgeVectorSwitch)
                    return Vector3D.Zero;

                var perpVector1 = Vector3D.CalculatePerpendicularVector(targetDirection);
                var perpVector2 = Vector3D.Cross(perpVector1, targetDirection);
                if (!Vector3D.IsUnit(ref perpVector2))
                    perpVector2.Normalize();

                var randomVector = GaussRnd() * perpVector1 + GaussRnd() * perpVector2;
                return randomVector * fudgeFactor * TimeSinceLastLock;
            }

            Vector3D GetSearchPos(Vector3D origin, Vector3D direction, IMyCameraBlock camera)
            {
                Vector3D scanPos = origin + direction * MaxRange;
                if (SearchScanSpread < 1e-2)
                {
                    return scanPos;
                }
                return scanPos + (camera.WorldMatrix.Left * GaussRnd() + camera.WorldMatrix.Up * GaussRnd()) * SearchScanSpread;
            }

            IMyTerminalBlock GetReference(List<IMyCameraBlock> cameraList, List<IMyShipController> shipControllers, IMyTerminalBlock referenceBlock)
            {
                IMyTerminalBlock controlledCam = GetControlledCamera(cameraList);
                if (controlledCam != null)
                    return controlledCam;

                if (referenceBlock != null)
                    return referenceBlock;

                return GetControlledShipController(shipControllers);
            }

            IMyCameraBlock SelectCamera()
            {
                if (_availableCameras.Count == 0)
                {
                    _timeSinceLastScan = 100000;
                    MissedLastScan = true;
                    return null;
                }

                return GetCameraWithMaxRange(_availableCameras);
            }

            void SetAutoScanInterval(double scanRange, IMyCameraBlock camera)
            {
                AutoScanInterval = scanRange / (1000.0 * camera.RaycastTimeMultiplier) / _availableCameras.Count * AutoScanScaleFactor;
            }

            bool DoLockScan(List<IMyCameraBlock> cameraList, out MyDetectedEntityInfo info, out IMyCameraBlock camera)
            {
                info = default(MyDetectedEntityInfo);

                #region Scan position selection
                Vector3D scanPosition;
                switch (_currentAimMode)
                {
                    case AimMode.Offset:
                        scanPosition = HitPosition;
                        break;
                    case AimMode.OffsetRelative:
                        scanPosition = OffsetTargetPosition;
                        break;
                    default:
                        scanPosition = TargetCenter;
                        break;
                }
                scanPosition += TargetVelocity * TimeSinceLastLock;

                if (MissedLastScan)
                {
                    scanPosition += CalculateFudgeVector(scanPosition - cameraList[0].GetPosition());
                }
                #endregion

                #region Camera selection
                GetCamerasInDirection(cameraList, _availableCameras, scanPosition, true);

                camera = SelectCamera();
                if (camera == null)
                {
                    return false;
                }
                #endregion

                #region Scanning
                // We adjust the scan position to scan a bit past the target so we are more likely to hit if it is moving away
                Vector3D adjustedTargetPos = scanPosition + Vector3D.Normalize(scanPosition - camera.GetPosition()) * 2 * TargetSize;
                double scanRange = (adjustedTargetPos - camera.GetPosition()).Length();

                SetAutoScanInterval(scanRange, camera);

                if (camera.AvailableScanRange >= scanRange &&
                    _timeSinceLastScan >= AutoScanInterval)
                {
                    info = camera.Raycast(adjustedTargetPos);
                    return true;
                }
                return false;
                #endregion
            }

            bool DoSearchScan(List<IMyCameraBlock> cameraList, IMyTerminalBlock reference, out MyDetectedEntityInfo info, out IMyCameraBlock camera)
            {
                info = default(MyDetectedEntityInfo);

                #region Camera selection
                if (reference != null)
                {
                    GetCamerasInDirection(cameraList, _availableCameras, reference.WorldMatrix.Forward);
                }
                else
                {
                    _availableCameras.Clear();
                    _availableCameras.AddRange(cameraList);
                }

                camera = SelectCamera();
                if (camera == null)
                {
                    return false;
                }
                #endregion

                #region Scanning
                SetAutoScanInterval(MaxRange, camera);

                if (camera.AvailableScanRange >= MaxRange &&
                    _timeSinceLastScan >= AutoScanInterval)
                {
                    if (reference != null)
                    {
                        info = camera.Raycast(GetSearchPos(reference.GetPosition(), reference.WorldMatrix.Forward, camera));
                    }
                    else
                    {
                        info = camera.Raycast(MaxRange);
                    }

                    return true;
                }
                return false;
                #endregion
            }

            public void UpdateTargetStateVectors(Vector3D position, Vector3D hitPosition, Vector3D velocity, double timeSinceLock = 0)
            {
                TicksSinceLastLock = 1;
                TargetCenter = position;
                HitPosition = hitPosition;
                TargetVelocity = velocity;
                TimeSinceLastLock = timeSinceLock;
            }

            void ProcessScanData(MyDetectedEntityInfo info, IMyTerminalBlock reference, Vector3D scanOrigin)
            {
                // Validate target and assign values
                if (info.IsEmpty() ||
                    _targetFilter.Contains(info.Type) ||
                    _gridIDsToIgnore.Contains(info.EntityId))
                {
                    MissedLastScan = true;
                    CycleAimMode();
                }
                else
                {
                    if (Vector3D.DistanceSquared(info.Position, scanOrigin) < MinRange * MinRange && Status != TargetingStatus.Locked)
                    {
                        Status = TargetingStatus.TooClose;
                        return;
                    }

                    if (info.EntityId != TargetId)
                    {
                        if (Status == TargetingStatus.Locked)
                        {
                            MissedLastScan = true;
                            CycleAimMode();
                            return;
                        }
                        else if (_manualLockOverride)
                        {
                            MissedLastScan = true;
                            return;
                        }
                    }

                    MissedLastScan = false;
                    UpdateTargetStateVectors(info.Position, info.HitPosition.Value, info.Velocity);

                    _lastTimeStamp = _currentTimeStamp;
                    _currentTimeStamp = _ticks;

                    TargetSize = info.BoundingBox.Size.Length();
                    _targetOrientation = info.Orientation;

                    if (Status != TargetingStatus.Locked)
                    {
                        Status = TargetingStatus.Locked;
                        TargetId = info.EntityId;
                        TargetRelation = info.Relationship;
                        TargetType = info.Type;
                        TargetName = info.Name;
                        TimeSinceLastLock = 0;
                        TicksSinceLastLock = 0;

                        if (!_manualLockOverride)
                        {
                            Vector3D hitPosOffset = reference == null ? Vector3D.Zero
                                : VectorRejection(reference.GetPosition() - scanOrigin, HitPosition - scanOrigin);

                            PreciseModeOffset = Vector3D.TransformNormal(
                                info.HitPosition.Value + hitPosOffset - TargetCenter,
                                MatrixD.Transpose(_targetOrientation));
                        }
                    }

                    _manualLockOverride = false;
                }
            }
            void CycleAimMode()
            {
                _currentAimMode = (AimMode)((int)(_currentAimMode + 1) % 3);
            }

            public void Update(double timeStep, List<IMyCameraBlock> cameraList, List<IMyShipController> shipControllers, IMyTerminalBlock referenceBlock = null)
            {
                _ticks++;

                _timeSinceLastScan += timeStep;
                if (Status == TargetingStatus.Locked || _manualLockOverride)
                    TimeSinceLastLock += timeStep;

                if (!IsScanning) return;

                TicksSinceLastLock++;

                if (cameraList.Count == 0) return;

                if (TimeSinceLastLock > (MaxTimeForLockBreak + AutoScanInterval) && (Status == TargetingStatus.Locked || _manualLockOverride))
                {
                    LockLost = true;
                    ClearLockInternal();
                    return;
                }

                IMyTerminalBlock reference = GetReference(cameraList, shipControllers, referenceBlock);

                IMyCameraBlock camera;
                bool scanned = (Status == TargetingStatus.Locked || _manualLockOverride)
                    ? DoLockScan(cameraList, out _target, out camera)
                    : DoSearchScan(cameraList, reference, out _target, out camera);

                if (!scanned)
                    return;

                _timeSinceLastScan = 0;
                ProcessScanData(_target, reference, camera.GetPosition());
            }

            void GetCamerasInDirection(List<IMyCameraBlock> allCameras, List<IMyCameraBlock> availableCameras, Vector3D testVector, bool vectorIsPosition = false)
            {
                availableCameras.Clear();

                foreach (var c in allCameras)
                {
                    if (c.Closed)
                        continue;

                    if (TestCameraAngles(c, vectorIsPosition ? testVector - c.GetPosition() : testVector))
                        availableCameras.Add(c);
                }
            }

            bool TestCameraAngles(IMyCameraBlock camera, Vector3D direction)
            {
                Vector3D local = Vector3D.Rotate(direction, MatrixD.Transpose(camera.WorldMatrix));

                if (local.Z > 0)
                    return false;

                var yawTan = Math.Abs(local.X / local.Z);
                var localSq = local * local;
                var pitchTanSq = localSq.Y / (localSq.X + localSq.Z);

                return yawTan <= 1 && pitchTanSq <= 1;
            }

            IMyCameraBlock GetCameraWithMaxRange(List<IMyCameraBlock> cameras)
            {
                double maxRange = 0;
                IMyCameraBlock maxRangeCamera = null;
                foreach (var c in cameras)
                {
                    if (c.AvailableScanRange > maxRange)
                    {
                        maxRangeCamera = c;
                        maxRange = maxRangeCamera.AvailableScanRange;
                    }
                }

                return maxRangeCamera;
            }

            IMyCameraBlock GetControlledCamera(List<IMyCameraBlock> cameras)
            {
                foreach (var c in cameras)
                {
                    if (c.Closed)
                        continue;

                    if (c.IsActive)
                        return c;
                }
                return null;
            }

            IMyShipController GetControlledShipController(List<IMyShipController> controllers)
            {
                if (controllers.Count == 0)
                    return null;

                IMyShipController mainController = null;
                IMyShipController controlled = null;

                foreach (var sc in controllers)
                {
                    if (sc.IsUnderControl && sc.CanControlShip)
                    {
                        if (controlled == null)
                        {
                            controlled = sc;
                        }

                        if (sc.IsMainCockpit)
                        {
                            mainController = sc;
                        }
                    }
                }

                if (mainController != null)
                    return mainController;

                if (controlled != null)
                    return controlled;

                return controllers[0];
            }

            public static Vector3D VectorRejection(Vector3D a, Vector3D b)
            {
                if (Vector3D.IsZero(a) || Vector3D.IsZero(b))
                    return Vector3D.Zero;

                return a - a.Dot(b) / b.LengthSquared() * b;
            }
        }


    }
}
