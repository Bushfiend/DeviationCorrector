using Sandbox.Game.EntityComponents;
using Sandbox.Game.Weapons;
//using Sandbox.ModAPI;
//using Sandbox.ModAPI;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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


        const string MissileTag = "PVE";
        const string ScriptGroupName = "UDR";

        int GuidanceDelay = 0; // seconds that the missile will fly in a straight line after being fired
        const int SentryRange = 2000;
        double UpdateSpeed = 0.016;
        const int TicksPerSecond = 60;
        const string DebugLCDName = "Debug";
        bool SetupCompleted = false;

        //MyItemType NukeAmmoDef = MyItemType.Parse("MyObjectBuilder_AmmoMagazine/SemiAutoPistolMagazine");
        MyItemType NukeAmmoDef = MyItemType.Parse("MyObjectBuilder_AmmoMagazine/FlareClip");

        int NukeAmmoAmount = 10000;
        HashSet<MyDefinitionId> WeaponcoreDefinitions = new HashSet<MyDefinitionId>();
        Dictionary<long, long> EngagedTargets = new Dictionary<long, long>();

        IMyTextPanel DebugLcd = null;

        WcPbApi Wc;
        bool UsingWeaponcore = false;

        bool HasRaycast = false;

        bool HasHud = false;
        bool InFireSequence = false;

        IMyShipController Reference;

        RaycastHoming RC;
        HUD Hud;
        Dictionary<MyDetectedEntityInfo, float> detectedEntities = new Dictionary<MyDetectedEntityInfo, float>();

        List<DeviationCorrector> ActiveMissiles = new List<DeviationCorrector>();
        HashSet<IMyShipMergeBlock> ProtoMissiles = new HashSet<IMyShipMergeBlock>();
        List<IMyShipController> Controllers = new List<IMyShipController>();
        List<IMyCameraBlock> RaycastArray = new List<IMyCameraBlock>();

        MyDetectedEntityInfo? RaycastTarget = new MyDetectedEntityInfo?();
        Vector3D TargetLocation = Vector3D.Zero;

        float RaycastRange = 10000;

        FireMode Mode = FireMode.Weaponcore;

        enum FireMode { Weaponcore, Raycast, Sentry }

        Random Rand = new Random();

        int tick = 0;

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

            tick++;

            if(Mode == FireMode.Weaponcore)
                UpdateSpeed = 0.016;
            if (Mode == FireMode.Raycast)
                UpdateSpeed = RC.AutoScanInterval;

            if (InFireSequence)
            {
                InFireSequence = false;
                return;
            }

            HandleCommands(argument);
            UpdateMissileTargets();
            UpdateMissiles(ref ActiveMissiles);

            if (HasRaycast)
            {
                RC.Update(UpdateSpeed, RaycastArray, Controllers);
            }

            if (HasHud)
                DisplayInfo();

            GetMissiles(ref ProtoMissiles);

            if (Mode == FireMode.Sentry && tick % 10 == 0)
            {
                SentryUpdate(SentryRange);
            }

            if (tick > 100)
                tick = 0;
        }

        void HandleCommands(string argument)
        {
            if (argument == string.Empty)         
                return;                         
            var argumentParts = argument.Trim().ToLower().Split(new char[] { ' ' }, 2);
            var command = argumentParts[0];
            var commandArguments = argumentParts.Length > 1 && argumentParts[1].Trim().Length > 0 ? argumentParts[1].Trim() : null;

            switch (command)
            {
                case "fire":
                    FireCommand(commandArguments);
                    break;
                case "raycast":
                    Raycast();
                    break;
                case "mode":
                    CycleMode();
                    break;
                case "reload":
                    Reload();
                    break;
                case "reassign":
                    RedirectMissiles();
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

            Mode = FireMode.Raycast;
            if (RC.IsScanning || RC.Status == RaycastHoming.TargetingStatus.Locked)
                RC.ClearLock();
            else
                RC.LockOn();
        }

        void FireCommand(string nameFilter = null)
        {
            var missile = GetMissile(nameFilter);
            if (missile == null)
            {
                Echo("There are no missiles ready to fire.");
                return;
            }

            FireMissile(missile, GuidanceDelay * TicksPerSecond);
            InFireSequence = true;
        }

        string[] Ani = { ".", "..", "...", "..." };
        int cycle = 0;

        void DisplayInfo()
        {
            Hud.Tag = "UDR - PVE";
            Hud.Mode = $"Mode: {Mode}";

            switch (Mode)
            {
                case FireMode.Weaponcore:
                    var target = Wc.GetAiFocus(Me.CubeGrid.EntityId);
                    if (target.Value.EntityId == 0)
                        break;

                    Hud.TargetName = $"Name:{target.Value.Name}";
                    Hud.TargetDistance = $"Distance: {Math.Round(Vector3D.Distance(Me.GetPosition(), target.Value.Position))}";
                    var location1 = target.Value.Position;
                    Hud.TargetLocation = $"Location: X:{Math.Round(location1.X, 2)} Y:{Math.Round(location1.Y, 2)} Z:{Math.Round(location1.Z, 2)} ";
                    Hud.TargetSpeed = $"Speed: {target.Value.Velocity}";
                    break;

                case FireMode.Raycast:
                    if (!HasRaycast)
                        return;
                    if (RC.Status == RaycastHoming.TargetingStatus.NotLocked && RC.IsScanning)
                    {
                        Hud.TargetName = $"Scanning{Ani[cycle]}";
                        Hud.TargetDistance = string.Empty;
                        Hud.TargetLocation = string.Empty;
                        Hud.TargetSpeed = string.Empty;
                        cycle++;
                        if (cycle == Ani.Length)
                            cycle = 0;
                    }
                    else if (RC.Status == RaycastHoming.TargetingStatus.NotLocked && !RC.IsScanning)
                    {
                        Hud.TargetName = "Idle";
                        Hud.TargetDistance = string.Empty;
                        Hud.TargetLocation = string.Empty;
                        Hud.TargetSpeed = string.Empty;
                    }
                    else if (RC.Status == RaycastHoming.TargetingStatus.Locked)
                    {
                        Hud.TargetName = $"Target Locked:{RC.TargetId}";
                        Hud.TargetDistance = $"Distance: {Math.Round(Vector3D.Distance(Me.GetPosition(), RC.HitPosition))}";
                        var location2 = RC.HitPosition;
                        Hud.TargetLocation = $"Location: X:{Math.Round(location2.X, 2)} Y:{Math.Round(location2.Y, 2)} Z:{Math.Round(location2.Z, 2)} ";
                        Hud.TargetSpeed = $"Speed: {RC.TargetVelocity}";
                    }
                    break;
            }

            Hud.TotalMissiles = ProtoMissiles.Count;
            Hud.ActiveMissiles = ActiveMissiles.Count;

            Hud.UpdateLcds();
        }



        void CycleMode()
        {
            int enumLength = Enum.GetNames(typeof(FireMode)).Length;
            Mode = (FireMode)(((int)Mode + 1) % enumLength);
        }

        void DetonateMissiles()
        {
            foreach (var missile in ActiveMissiles)
            {
                missile.NukeOverride = true;
            }
        }

        void RedirectMissiles()
        {
            long entityId = 0;
            Vector3D location = Vector3D.Zero;


            switch (Mode)
            {
                case FireMode.Raycast:
                    TargetLocation = (Vector3D)RaycastTarget.Value.HitPosition;
                    if (TargetLocation == Vector3D.Zero)
                        return;
                    entityId = RaycastTarget.Value.EntityId;
                    location = TargetLocation;
                    break;

                case FireMode.Weaponcore:
                    if (UsingWeaponcore)
                    {
                        var target = Wc.GetAiFocus(Me.CubeGrid.EntityId);
                        if (target.Value.EntityId == 0)
                            return;
                        entityId = target.Value.EntityId;
                        location = target.Value.Position;
                    }
                    break;
            }

            if (location == Vector3D.Zero)
                return;

            foreach (var missile in ActiveMissiles)
            {
                missile.ReassignTarget(entityId, location);
            }

        }

        IMyShipMergeBlock GetMissile(string name = null)
        {
            return ProtoMissiles.FirstOrDefault(missile => name == null || missile.CustomName.ToLower().Contains(name));
        }

        void FireMissile(IMyShipMergeBlock mergeBlock, int trackingDelay = 0, long entityId = 0)
        {
            if (ActiveMissiles.Exists(m => m.Merge == mergeBlock))
            {
                throw new Exception("Attempted to fire a missile that was already fired.");
            }

            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(blocks);

            var missile = new DeviationCorrector(mergeBlock, blocks, Reference, $"Missile-{Rand.Next(1, 20000)}", trackingDelay);

            switch (Mode)
            {
                case FireMode.Weaponcore:
                    if (!UsingWeaponcore)
                        return;
                    var target = Wc.GetAiFocus(Me.CubeGrid.EntityId);
                    if (target.Value.EntityId == 0)
                        return;
                    missile.TargetId = target.Value.EntityId;
                    missile.FireMissile(target.Value.Position);
                    break;

                case FireMode.Raycast:
                    if (!HasRaycast || RC.Status != RaycastHoming.TargetingStatus.Locked)
                        return;

                    missile.TargetId = RC.TargetId;
                    missile.FireMissile(RC.TargetPosition);
                    break;
                case FireMode.Sentry:
                    if (entityId == 0)
                        return;

                    missile.FireMissile(Vector3D.Zero);
                    missile.TargetId = entityId;
                    break;
            }

            int index = Rand.Next(Quotes.Length);
            missile.HudText = Quotes[index];

            ActiveMissiles.Add(missile);
            ProtoMissiles.Remove(mergeBlock);

            Echo("Missile launched!");
        }


        void UpdateMissileTargets()
        {

            if (Mode == FireMode.Raycast)
            {
                if (RC.Status == RaycastHoming.TargetingStatus.Locked)
                {
                    foreach (var missile in ActiveMissiles)
                    {
                        missile.TargetId = RC.TargetId;
                        missile.TargetPosition = RC.TargetPosition;
                    }
                    return;
                }
            }

            if (!UsingWeaponcore)
                return;

            detectedEntities.Clear();
            Wc.GetSortedThreats(Me, detectedEntities);

            foreach (var missile in ActiveMissiles)
            {

                var targetUpdated = false;

                foreach (var entity in detectedEntities)
                {
                    if (missile.TargetId == entity.Key.EntityId)
                    {
                        missile.TargetPosition = entity.Key.Position;
                        targetUpdated = true;
                        break;
                    }
                }

                if (!targetUpdated && missile.HasWcAI && missile.ProgrammableBlocks.Count > 0)
                {
                    var detectedEntitiesMissile = new Dictionary<MyDetectedEntityInfo, float>();

                    foreach (var pb in missile.ProgrammableBlocks)
                    {
                        Wc.GetSortedThreats(pb, detectedEntitiesMissile);
                        var targetFound = false;

                        foreach (var missileEntity in detectedEntitiesMissile)
                        {
                            if (missile.TargetId == missileEntity.Key.EntityId)
                            {
                                missile.TargetPosition = missileEntity.Key.Position;
                                targetFound = true;
                                break;
                            }
                        }

                        if (targetFound)
                            break;
                    }
                }
            }
        }



        bool Setup()
        {
            if (SetupCompleted)
                return true;

            DebugLcd = GridTerminalSystem.GetBlockWithName(DebugLCDName) as IMyTextPanel;
            if (DebugLcd != null)
            {
                DebugLcd.ContentType = ContentType.TEXT_AND_IMAGE;
                Echo += s => DebugLcd.WriteText(s);
            }


            Wc = new WcPbApi();
            try
            {
                Echo("Activating Weaponcore API...");
                Wc.Activate(Me);
                UsingWeaponcore = true;
            }
            catch (Exception e)
            {
                Echo("WeaponCore Api is failing!\nMake sure WeaponCore is enabled!");
                Echo(e.Message);
                Echo(e.StackTrace);
                return false;
            }

            Wc.GetAllCoreWeapons(WeaponcoreDefinitions);

            var tempControllers = new List<IMyShipController>();
            GridTerminalSystem.GetBlocksOfType(tempControllers);
            if (tempControllers.Count == 0)
            {
                Echo("Setup Failed, No ship controllers found.");
                return false;
            }
            Reference = tempControllers.First();

            var scriptGroup = GridTerminalSystem.GetBlockGroupWithName(ScriptGroupName);

            if (scriptGroup == null)
            {
                Echo($"Setup Failed, no group named {ScriptGroupName} found.");
                return false;
            }

            var tempBlocks = new List<IMyTerminalBlock>();
            scriptGroup.GetBlocks(tempBlocks);

            var displays = tempBlocks.FindAll(b => b is IMyTextPanel);
            var cameras = tempBlocks.FindAll(b => b is IMyCameraBlock);

            if (displays.Count > 0)
            {
                HasHud = true;
                var d = new List<IMyTextPanel>();
                foreach (var block in displays)
                {
                    d.Add(block as IMyTextPanel);
                }

                Hud = new HUD(d);

            }
            if (cameras.Count > 0)
            {
                HasRaycast = true;
                foreach (var block in cameras)
                {
                    RaycastArray.Add(block as IMyCameraBlock);
                    ((IMyCameraBlock)block).EnableRaycast = true;
                }
                RC = new RaycastHoming(RaycastRange, 3, 250, Me.CubeGrid.EntityId);
            }

            GridTerminalSystem.GetBlocksOfType(Controllers);

            Echo("Setup Complete");
            SetupCompleted = true;
            return true;
        }


        void UpdateMissiles(ref List<DeviationCorrector> activeMissiles)
        {
            activeMissiles.RemoveAll(m => m.Merge.Closed);
            activeMissiles.ForEach(m => m.Update());
        }



        Dictionary<MyDetectedEntityInfo, float> potentialTargets = new Dictionary<MyDetectedEntityInfo, float>();
        void SentryUpdate(float range = 2000, float threatLevel = 0)
        {
            potentialTargets.Clear();
            Wc.GetSortedThreats(Me, potentialTargets);

            var targetList = new List<MyDetectedEntityInfo>();
            foreach (var target in potentialTargets)
            {
                if (target.Key.Relationship != MyRelationsBetweenPlayerAndBlock.Enemies)
                    continue;
                if (EngagedTargets.ContainsKey(target.Key.EntityId))
                    continue;
                if (target.Value < threatLevel)
                    continue;
                if (Vector3D.Distance(target.Key.Position, Me.GetPosition()) > range)
                    continue;
                if (target.Key.Velocity == Vector3.Zero)
                    continue;

                targetList.Add(target.Key);
            }
            targetList = targetList.OrderByDescending(entityInfo => Vector3D.Distance(entityInfo.Position, Me.GetPosition())).ToList();

            if (targetList.Count == 0)
                return;

            InFireSequence = true;

            var missile = GetMissile();
            if (missile == null)
            {
                Echo("Sentry target acquired, but there are no missiles ready to fire.");
                return;
            }

            EngagedTargets.Add(targetList.First().EntityId, targetList.First().TimeStamp);
            FireMissile(missile, GuidanceDelay * TicksPerSecond + 5, targetList.First().EntityId);
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

                if (!block.CustomName.Contains(MissileTag))
                    continue;

                if (ActiveMissiles.Exists(active => active.Merge == block))
                    continue;

                missiles.Add(block);
            }
        }


        void Reload()
        {

            var tanks = new List<IMyGasTank>();
            GridTerminalSystem.GetBlocksOfType(tanks, c => c.CustomName.Contains(MissileTag));
            tanks.ForEach(c => c.Stockpile = true);


            var connectors = new List<IMyShipConnector>();
            GridTerminalSystem.GetBlocksOfType(connectors, c => c.CustomName.Contains(MissileTag));
            connectors.ForEach(c => c.Connect());

            var missileCargos = new List<IMyCargoContainer>();
            GridTerminalSystem.GetBlocksOfType(missileCargos);
            missileCargos.RemoveAll(c => !c.CustomName.Contains(MissileTag));

            var shipCargos = new List<IMyCargoContainer>();
            GridTerminalSystem.GetBlocksOfType(shipCargos);

            shipCargos.RemoveAll(b => b.CustomName.Contains(MissileTag) || !b.GetInventory().ContainItems(1, NukeAmmoDef));

            var missileInventories = new List<IMyInventory>();

            foreach (var cargo in missileCargos)
                missileInventories.Add(cargo.GetInventory());
            foreach (var connector in connectors)
            {
                var useConnector = false;
                bool.TryParse(connector.CustomData, out useConnector);
                if (useConnector)
                    missileInventories.Add(connector.GetInventory());
            }

            foreach (var shipCargo in shipCargos)
            {
                MyFixedPoint missileItemAmount = 0;
                foreach (var missileInventory in missileInventories)
                {
                    missileItemAmount = missileInventory.GetItemAmount(NukeAmmoDef);
                    if (missileItemAmount >= NukeAmmoAmount)
                        continue;
                    var transferAmount = NukeAmmoAmount - missileItemAmount;
                    var shipCargoInventory = shipCargo.GetInventory();
                    var item = shipCargoInventory.FindItem(NukeAmmoDef);
                    if (item == null)
                        break;

                    shipCargoInventory.TransferItemTo(missileInventory, item.Value, transferAmount);
                }

            }
        }


        class DeviationCorrector
        {
            public IMyShipMergeBlock Merge { get; }
            public string Id { get; set; }
            public string HudText { get; set; }
            public long TargetId { get; set; }
            public bool HasWcAI { get; set; }
            public List<IMyShipConnector> Connectors
            {
                get { return _connectors; }
            }
            public List<IMyCargoContainer> CargoContainers
            {
                get { return _cargoContainers; }
            }
            public List<IMyGasTank> HydrogenTanks
            {
                get { return _hydrogenTanks; }
            }
            public List<IMyThrust> Thrusters
            {
                get { return _thrusters; }
            }
            public List<IMyWarhead> Warheads
            {
                get { return _warheads; }
            }
            public List<IMyGyro> Gyros
            {
                get { return _gyros; }
            }
            public List<IMyBatteryBlock> BatteryBlocks
            {
                get { return _batteryBlocks; }
            }
            public List<IMyProgrammableBlock> ProgrammableBlocks
            {
                get { return _programmableBlocks; }
            }
            public bool NukeOverride
            {
                set { _nukeOverride = value; }
            }

            public List<IMyTerminalBlock> Debug
            {
                get { return _blockList; }
            }
            public bool Launched { get; set; }
            public bool IsNuke { get; set; }
            public Vector3D TargetPosition { get; set; }
            public List<Vector3D> PathToTarget { get; set; }
            public IMyShipController ExternalReference { get; set; }
            public float DistanceToTarget
            {
                get { return (float)Vector3D.Distance(Position, TargetPosition); }
            }
            public Vector3D Position
            {
                get
                {
                    return GetPosition();
                }
            }
            public bool MissileReady { get; private set; }



            public DeviationCorrector(IMyShipMergeBlock mergeBlock, List<IMyTerminalBlock> blocks, IMyShipController reference, string id = "Missile", int trackingDelay = 0)
            {
                Merge = mergeBlock;
                _blockList = blocks;

                Id = id;

                bool nuke = false;
                bool.TryParse(Merge.CustomData, out nuke);
                IsNuke = nuke;
                TargetPosition = Vector3D.Zero;
                Launched = false;
                ExternalReference = reference;
                if (trackingDelay == 0)
                    _trackingDelay = _random.Next(20, 100);
                else
                    _trackingDelay = trackingDelay;
            }

            public void FireMissile(Vector3D target)
            {
                Launched = true;
                TargetPosition = target;
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
                _blockList.RemoveAll(b => b.CubeGrid == ExternalReference.CubeGrid || this.Merge.CubeGrid != b.CubeGrid);

                foreach (var block in _blockList)
                {
                    if (block is IMyShipConnector)
                    {
                        _connectors.Add(block as IMyShipConnector);
                        ((IMyShipConnector)block).Disconnect();
                        ((IMyShipConnector)block).Enabled = false;
                        continue;
                    }
                    if (block is IMyCargoContainer)
                    {
                        _cargoContainers.Add(block as IMyCargoContainer);
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
                        if (_forwardVector == Vector3D.Zero)
                            _forwardVector = block.WorldMatrix.Backward;
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
                    if (block is IMyBeacon)
                    {
                        ((IMyBeacon)block).HudText = HudText;
                        ((IMyBeacon)block).Radius = 1000f;
                        continue;
                    }
                    if (block is IMyProgrammableBlock)
                    {
                        _programmableBlocks.Add(block as IMyProgrammableBlock);
                        ((IMyProgrammableBlock)block).Enabled = true;
                        HasWcAI = true;
                        continue;
                    }

                }

                foreach (var thruster in _thrusters)
                {
                    _acceleration += thruster.MaxEffectiveThrust;
                    thruster.ThrustOverridePercentage = 1f;
                }

                _connectors.ForEach(c => c.Disconnect());
                _blockList.ForEach(b => _mass += b.Mass);

                _acceleration = _acceleration / _mass;


                return true;
            }

            public void Update()
            {
                if (!Launched)
                    return;

                if (!_missileSetup)
                {
                    _missileSetup = Setup();
                    if (_missileSetup)
                        this._connectors.ForEach(c => c.Enabled = false);
                    return;
                }


                if (_trackingDelay > _missileTicks)
                {
                    _missileTicks++;
                    return;
                }
                RangeCheck();
                Guide();
            }


            private Vector3D GetPosition()
            {
                if (Merge == null | Merge.Closed)
                    return Vector3D.Zero;

                return Merge.CubeGrid.WorldVolume.Center;
            }

            private void Nuke()
            {
                if (Vector3D.Distance(ExternalReference.GetPosition(), Position) < 150)
                    return;

                if (_warheads.Count < 2)
                    return;

                if (_detonationDelay == 1)
                {
                    if (_warheadCount == _warheads.Count)
                        return;

                    _warheads[_warheadCount].Detonate();
                    _warheadCount++;
                    _detonationDelay = 0;
                    return;
                }
                _detonationDelay++;

            }

            private void RangeCheck()
            {
                if (this.DistanceToTarget <= this._detonationDistance || this._nukeOverride)
                {
                    Nuke();
                }
            }


            public void ReassignTarget(long entityId, Vector3D targetLocation)
            {
                this.TargetId = entityId;
                this.TargetPosition = targetLocation;
            }

            private void Guide()
            {
                if (TargetPosition == Vector3D.Zero)
                    return;

                var missileVelocity = (Position - this._lastPosition) / GameTick;
                var targetVelocity = (TargetPosition - this._lastTargetPosition) / GameTick;

                this._lastLOS = Vector3D.Normalize(this._lastTargetPosition - this._lastPosition);
                this._currentLOS = Vector3D.Normalize(TargetPosition - this.Position);
                var relativeVelocity = Vector3D.Normalize(targetVelocity - missileVelocity);

                Vector3D am = new Vector3D(1, 0, 0);
                double losRate;
                Vector3D losDelta;

                if (this._lastLOS.Length() == 0)
                {
                    losDelta = new Vector3D(0, 0, 0);
                    losRate = 0.0;
                }
                else
                {
                    losDelta = this._currentLOS - this._lastLOS;
                    losRate = losDelta.Length() / GameTick;
                }

                double closingVelocity = (targetVelocity - missileVelocity).Length();

                Vector3D GravityComp = -ExternalReference.GetNaturalGravity();
                Vector3D LateralDirection = Vector3D.Normalize(Vector3D.Cross(Vector3D.Cross(relativeVelocity, this._currentLOS), relativeVelocity));
                double losRateDerivative = (losRate - this._lastLosRate) / GameTick;
                double dControlTerm = derivativeControlGain * losRateDerivative;

                Vector3D targetAcceleration = (targetVelocity - this._lastTargetVelocity) / GameTick;
                Vector3D missileAcceleration = (missileVelocity - this._lastMissileVelocity) / GameTick;
                Vector3D accelerationDifference = targetAcceleration - missileAcceleration;

                Vector3D accelerationCorrection = _accelerationCorrectionGain * accelerationDifference;

                Vector3D lateralAcceleration = LateralDirection * (this._pngGain * losRate + dControlTerm) * closingVelocity + losDelta * 9.8 * (0.5 * this._pngGain) + accelerationCorrection;

                double oversteerRequirement = (lateralAcceleration).Length() / this._acceleration;
                if (oversteerRequirement > 0.98)
                {
                    lateralAcceleration = this._acceleration * Vector3D.Normalize(lateralAcceleration + (oversteerRequirement * Vector3D.Normalize(-missileVelocity)) * 40);
                }

                Func<Vector3D, Vector3D, double> vectorProjectionScalar = (@in, Axis_norm) =>
                {
                    double @out = Vector3D.Dot(@in, Axis_norm);
                    if (double.IsNaN(@out))
                    {
                        @out = 0;
                    }
                    return @out;
                };

                double thrusterPower = vectorProjectionScalar(_forwardVector, Vector3D.Normalize(lateralAcceleration));

                thrusterPower = (Merge.CubeGrid.GridSizeEnum == MyCubeSize.Large) ? MathHelper.Clamp(thrusterPower, 0.9, 1) : thrusterPower;

                thrusterPower = MathHelper.Clamp(thrusterPower, 0.4, 1);
                foreach (var thruster in this._thrusters)
                {
                    if (thruster.ThrustOverride != (thruster.MaxThrust * thrusterPower))                   
                        thruster.ThrustOverride = (float)(thruster.MaxThrust * thrusterPower);                   
                }

                double RejectedAccel = Math.Sqrt(this._acceleration * this._acceleration - lateralAcceleration.LengthSquared());
                if (double.IsNaN(RejectedAccel))
                {
                    RejectedAccel = 0;
                }
                lateralAcceleration = lateralAcceleration + this._currentLOS * RejectedAccel;

                am = Vector3D.Normalize(lateralAcceleration + GravityComp);

                double Yaw; double Pitch;

                _gyros.RemoveAll(g => g.Closed);
                try
                {
                    CalculateGyroRotation(am, 18, 0.3, _gyros, this._lastYaw, this._lastPitch, out Pitch, out Yaw);
                    this._lastTargetPosition = TargetPosition;
                    this._lastPosition = Position;
                    this._lastYaw = Yaw;
                    this._lastPitch = Pitch;

                    this._lastLosRate = losRate;

                    this._lastTargetVelocity = targetVelocity;
                    this._lastMissileVelocity = missileVelocity;
                }
                catch { }                     
            }


            private void CalculateGyroRotation(Vector3D targetDirection, double proportionalGain, double dampingGain, List<IMyGyro> gyroscopes, double previousYaw, double previousPitch, out double newPitch, out double newYaw)
            {
                newPitch = 0;
                newYaw = 0;

                _upVector = _thrusters.First().WorldMatrix.Up;
                _forwardVector = _thrusters.First().WorldMatrix.Backward;

                Quaternion shipOrientation = Quaternion.CreateFromForwardUp(_forwardVector, _upVector);
                var inverseOrientation = Quaternion.Inverse(shipOrientation);

                Vector3D transformedDirection = targetDirection;
                Vector3D referenceFrameDirection = Vector3D.Transform(transformedDirection, inverseOrientation);

                double shipForwardAzimuth = 0;
                double shipForwardElevation = 0;
                Vector3D.GetAzimuthAndElevation(referenceFrameDirection, out shipForwardAzimuth, out shipForwardElevation);

                newYaw = shipForwardAzimuth;
                newPitch = shipForwardElevation;

                shipForwardAzimuth += dampingGain * ((shipForwardAzimuth - previousYaw) / GameTick);
                shipForwardElevation += dampingGain * ((shipForwardElevation - previousPitch) / GameTick);


                _currentRoll += _spin;
                if (_currentRoll > Math.PI * 2)
                    _currentRoll -= Math.PI * 2;

                var referenceMatrix = MatrixD.CreateWorld(_thrusters.First().GetPosition(), (Vector3)_forwardVector, (Vector3)_upVector).GetOrientation();
                var pitchYawRollVector = Vector3.Transform(new Vector3D(shipForwardElevation, shipForwardAzimuth, _currentRoll), referenceMatrix);               
                foreach (var gyroscope in gyroscopes)
                {
                    var transformedVector = Vector3.Transform(pitchYawRollVector, Matrix.Transpose(gyroscope.WorldMatrix.GetOrientation()));
                    if (double.IsNaN(transformedVector.X) || double.IsNaN(transformedVector.Y) || double.IsNaN(transformedVector.Z))
                    {
                        continue;
                    }
                    gyroscope.Pitch = (float)MathHelper.Clamp((-transformedVector.X) * proportionalGain, -1000, 1000);
                    gyroscope.Yaw = (float)MathHelper.Clamp(((-transformedVector.Y)) * proportionalGain, -1000, 1000);
                    gyroscope.Roll = (float)MathHelper.Clamp(((-transformedVector.Z)) * proportionalGain, -1000, 1000);
                    gyroscope.GyroOverride = true;
                }
            }


            private Random _random = new Random(DateTime.Now.Millisecond);
            private const double GameTick = 0.016;
            private bool _missileSetup = false;
            private double _lastYaw = 0;
            private double _lastPitch = 0;
            private double _currentRoll = 0;
            private double _spin = 0;
            private double _pngGain = 2.5;
            private double _mass = 0;
            private double _detonationDistance = 85;

            private int _detonationDelay = 0;
            private int _warheadCount = 0;
            private bool _nukeOverride = false;
            private int _trackingDelay = 0;
            private int _missileTicks = 0;

            private double derivativeControlGain = 0.2;
            private double _accelerationCorrectionGain = 0.1;

            private double _lastLosRate = 0;
            private Vector3D _lastTargetVelocity = Vector3D.Zero;
            private Vector3D _lastMissileVelocity = Vector3D.Zero;
            private double _acceleration = 0;
            private Vector3D _upVector = Vector3D.Zero;
            private Vector3D _currentLOS = Vector3D.Zero;
            private Vector3D _lastLOS = Vector3D.Zero;
            private Vector3D _lastTargetPosition = Vector3D.Zero;
            private Vector3D _lastPosition = Vector3D.Zero;
            private List<IMyTerminalBlock> _blockList = new List<IMyTerminalBlock>(); //To filter on launch
            private Vector3D _forwardVector = Vector3D.Zero;

            private HashSet<MyDefinitionId> _wcDefinitions = new HashSet<MyDefinitionId>();

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


        class HUD
        {
            public List<IMyTextPanel> Displays
            {
                get { return _displays; }
                set { _displays = value; }
            }
            public string Tag
            {
                get { return _tag; }
                set { _tag = value; }
            }
            public string Mode
            {
                get { return _mode; }
                set { _mode = value; }
            }
            public int ActiveMissiles
            {
                get { return _activeMissileCount; }
                set { _activeMissileCount = value; }
            }
            public int TotalMissiles
            {
                get { return _missileCount; }
                set { _missileCount = value; }
            }
            public string TargetName
            {
                get { return _targetName; }
                set { _targetName = value; }
            }
            public string TargetLocation
            {
                get { return _targetLocation; }
                set { _targetLocation = value; }
            }
            public string TargetSpeed
            {
                get { return _targetSpeed; }
                set { _targetSpeed = value; }
            }
            public string TargetDistance
            {
                get { return _targetDistance; }
                set { _targetDistance = value; }
            }

            public HUD(List<IMyTextPanel> displays)
            {
                Displays = displays;
                SetupLcds();

            }



            private void SetupLcds()
            {
                foreach (var display in _displays)
                {
                    display.ContentType = ContentType.SCRIPT;
                    display.ScriptBackgroundColor = new Color(0, 0, 0, 255);
                }
            }

            public void UpdateLcds()
            {
                if (_ticks < updateFrequency)
                {
                    _ticks++;
                    return;
                }
                _ticks = 0;

                if (_activeMissileColor == Color.Yellow)
                {
                    _activeMissileColor = Color.OrangeRed;
                }
                else
                {
                    _activeMissileColor = Color.Yellow;
                }

                _displays.RemoveAll(d => d.Closed || d == null);

                foreach (var display in _displays)
                {
                    var frame = display.DrawFrame();
                    var drawSurface = display.TextureSize;
                    Vector2 centerPos = new Vector2(drawSurface.X / 2f, drawSurface.Y / 2f);

                    DrawBackground(frame, centerPos, 1);
                    frame.Dispose();
                }


            }

            private void DrawBackground(MySpriteDrawFrame frame, Vector2 centerPos, float scale = 1f)
            {
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -247f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(199, 99, 247, 255), null, TextAlignment.CENTER, 0f)); // b9
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(1f, -167f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(148, 0, 148, 255), null, TextAlignment.CENTER, 0f)); // b8
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -177f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(51, 0, 148, 255), null, TextAlignment.CENTER, 0f)); // b7
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -187f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(0, 148, 197, 255), null, TextAlignment.CENTER, 0f)); // b6
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -197f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(0, 148, 0, 255), null, TextAlignment.CENTER, 0f)); // b5
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -207f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(247, 247, 1, 255), null, TextAlignment.CENTER, 0f)); // b4
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -217f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(246, 148, 0, 255), null, TextAlignment.CENTER, 0f)); // b3
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -227f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(246, 0, 0, 255), null, TextAlignment.CENTER, 0f)); // b2
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(0f, -237f) * scale + centerPos, new Vector2(500f, 10f) * scale, new Color(247, 98, 148, 255), null, TextAlignment.CENTER, 0f)); // b1

                var panelColor = new Color(5, 5, 5, 150);
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(1f, -128f) * scale + centerPos, new Vector2(500f, 50f) * scale, panelColor, null, TextAlignment.CENTER, 0f)); // sprite2
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(1f, 167f) * scale + centerPos, new Vector2(500f, 164f) * scale, panelColor, null, TextAlignment.CENTER, 0f)); // sprite3
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(1f, -8f) * scale + centerPos, new Vector2(500f, 175f) * scale, panelColor, null, TextAlignment.CENTER, 0f)); // sprite4
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareHollow", new Vector2(0f, -206f) * scale + centerPos, new Vector2(500f, 95f) * scale, new Color(42, 42, 42, 255), null, TextAlignment.CENTER, 0f)); // sprite5
                frame.Add(new MySprite(SpriteType.TEXT, _tag, new Vector2(-230f, -257f) * scale + centerPos, null, Color.Black, "DEBUG", TextAlignment.LEFT, 3f * scale)); // Title
                frame.Add(new MySprite(SpriteType.TEXT, _mode, new Vector2(-230f, -143f) * scale + centerPos, null, Color.DarkRed, "DEBUG", TextAlignment.LEFT, 1f * scale)); // mode
                frame.Add(new MySprite(SpriteType.TEXT, _targetName, new Vector2(-230f, -91f) * scale + centerPos, null, Color.DarkRed, "DEBUG", TextAlignment.LEFT, 1f * scale)); // tName
                frame.Add(new MySprite(SpriteType.TEXT, _targetLocation, new Vector2(-230f, -59f) * scale + centerPos, null, Color.DarkRed, "DEBUG", TextAlignment.LEFT, 0.7f * scale)); // tLocation
                frame.Add(new MySprite(SpriteType.TEXT, _targetSpeed, new Vector2(-230f, -30f) * scale + centerPos, null, Color.DarkRed, "DEBUG", TextAlignment.LEFT, 0.7f * scale)); // tSpeed
                frame.Add(new MySprite(SpriteType.TEXT, _targetDistance, new Vector2(-230f, -3f) * scale + centerPos, null, Color.DarkRed, "DEBUG", TextAlignment.LEFT, 0.7f * scale)); // tDistance

                float xOffset = -550f;
                float yOffset = 350f;

                for (int i = 0; i < (_activeMissileCount + _missileCount); i++)
                {
                    if (i == 20)
                        break;

                    if (i == 10)
                    {
                        xOffset = -550f;
                        yOffset += 150f;
                    }
                    if (i < _activeMissileCount)
                        DrawMissileSprite(ref frame, new Vector2(xOffset, yOffset), centerPos, _activeMissileColor, 0.4f);
                    else
                        DrawMissileSprite(ref frame, new Vector2(xOffset, yOffset), centerPos, _protoMissileColor, 0.4f);

                    xOffset += 120f;
                }

            }

            private void DrawMissileSprite(ref MySpriteDrawFrame frame, Vector2 pos, Vector2 centerPos, Color color, float scale)
            {
                frame.Add(new MySprite(SpriteType.TEXTURE, "SemiCircle", pos * scale + centerPos, new Vector2(30f, 90f) * scale, color, null, TextAlignment.CENTER, 0f));
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(pos.X, pos.Y + 22f) * scale + centerPos, new Vector2(30f, 40f) * scale, color, null, TextAlignment.CENTER, 0f));
                frame.Add(new MySprite(SpriteType.TEXTURE, "SquareSimple", new Vector2(pos.X, pos.Y + 64f) * scale + centerPos, new Vector2(30f, 40f) * scale, color, null, TextAlignment.CENTER, 0f));
                frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle", new Vector2(pos.X + 21f, pos.Y + 60f) * scale + centerPos, new Vector2(20f, 30f) * scale, color, null, TextAlignment.CENTER, -0.3491f));
                frame.Add(new MySprite(SpriteType.TEXTURE, "Triangle", new Vector2(pos.X - 21f, pos.Y + 60f) * scale + centerPos, new Vector2(20f, 30f) * scale, color, null, TextAlignment.CENTER, 0.3491f));
            }

            private int _ticks = 0;
            const int updateFrequency = 20;
            private int _missileCount = 0;
            private int _activeMissileCount = 0;
            private Color _protoMissileColor = Color.Green;
            private Color _activeMissileColor = Color.Yellow;
            private string _tag = "";
            private string _mode = "";
            private string _targetName = "";
            private string _targetLocation = "";
            private string _targetSpeed = "";
            private string _targetDistance = "";
            private float _cargoMass = 0;
              

            private List<IMyTextPanel> _displays = new List<IMyTextPanel>();
        }

        string[] Quotes =
        {
            "My name is Van, I'm an Artist.",
            "Deep Dark Fantasies.",
            "AHHHHHHHHH!!!!!",
            "Let's celebrate and drill some ore.",
            "Drilling is 300 bucks.",
            "Now theres a drill I wouldn't mind fucking.",
            "I wanna see six hot loads.",
            "Ass We Can.",
            "It gets bigger when I pull on it.",
            "You ripped my fucking pants.",
            "How do you like that huh?",
            "I'll show you whos boss of this gym.",
            "Ohhh I'm fucking drilling!",
            "I'm a perfomance artist.",
            "Just lube it up.",
            "It's a long process.",
            "Fucking peice of meat.",
            "Yeah, work that tool..",
            "Sir, yes sir.",
            "SHEEEET",
            "Crate of iron Cocks.",
            "Leather Rebel",
            "Dildo Thrower MK-1",
            "GET YOUR ASS BACK HERE",
            "It's wrestling time!",
            "Boy next door.",
            "Ooh, that’s good.",
            "Take it, boy!",
            "Drilling supplies are two aisles down.",
            "It's time for the final drilling session!",
            "Trust me, size matters when it comes to drill bits.",
            "Always wear protective gear when drilling.",
            "You think that's a powerful drill? Try this one.",
            "Feels like the drill's going in deep today."
        };




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
            public bool OffsetTargeting = false;
            public bool MissedLastScan { get; private set; } = false;
            public bool LockLost { get; private set; } = false;
            public bool IsScanning { get; private set; } = false;
            public double TimeSinceLastLock { get; private set; } = 0;
            public double TargetSize { get; private set; } = 0;
            public double MaxRange { get; private set; }
            public double MinRange { get; private set; }
            public long TargetId { get; private set; } = 0;
            public double AutoScanInterval { get; private set; } = 0;
            public double MaxTimeForLockBreak { get; private set; }
            public MyRelationsBetweenPlayerAndBlock TargetRelation { get; private set; }
            public MyDetectedEntityType TargetType { get; private set; }

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
                TargetSize = 0;
                HitPosition = Vector3D.Zero;
                TargetId = 0;
                _timeSinceLastScan = 141;
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
                    TargetSize = info.BoundingBox.Size.Length();
                    _targetOrientation = info.Orientation;

                    if (Status != TargetingStatus.Locked) // Initial lockon
                    {
                        Status = TargetingStatus.Locked;
                        TargetId = info.EntityId;
                        TargetRelation = info.Relationship;
                        TargetType = info.Type;

                        // Compute aim offset
                        if (!_manualLockOverride)
                        {
                            Vector3D hitPosOffset = reference == null ? Vector3D.Zero : VectorRejection(reference.GetPosition() - scanOrigin, HitPosition - scanOrigin);
                            PreciseModeOffset = Vector3D.TransformNormal(info.HitPosition.Value + hitPosOffset - TargetCenter, MatrixD.Transpose(_targetOrientation));
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
                _timeSinceLastScan += timeStep;

                if (!IsScanning)
                    return;

                TimeSinceLastLock += timeStep;

                if (cameraList.Count == 0)
                    return;

                // Check for lock lost
                if (TimeSinceLastLock > (MaxTimeForLockBreak + AutoScanInterval) && (Status == TargetingStatus.Locked || _manualLockOverride))
                {
                    LockLost = true; // TODO: Change this to a callback
                    ClearLockInternal();
                    return;
                }

                IMyTerminalBlock reference = GetReference(cameraList, shipControllers, referenceBlock);

                MyDetectedEntityInfo info;
                IMyCameraBlock camera;
                bool scanned;
                if (Status == TargetingStatus.Locked || _manualLockOverride)
                {
                    scanned = DoLockScan(cameraList, out info, out camera);
                }
                else
                {
                    scanned = DoSearchScan(cameraList, reference, out info, out camera);
                }

                if (!scanned)
                {
                    return;
                }
                _timeSinceLastScan = 0;

                ProcessScanData(info, reference, camera.GetPosition());
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
                            mainController = sc; // Only one per grid so no null check needed
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

        public class WcPbApi
        {
            private Action<Sandbox.ModAPI.Ingame.IMyTerminalBlock, IDictionary<MyDetectedEntityInfo, float>> _getSortedThreats;
            private Action<Sandbox.ModAPI.Ingame.IMyTerminalBlock, ICollection<Sandbox.ModAPI.Ingame.MyDetectedEntityInfo>> _getObstructions;
            private Func<long, int, MyDetectedEntityInfo> _getAiFocus;
            private Action<ICollection<MyDefinitionId>> _getCoreWeapons;

            public bool Activate(Sandbox.ModAPI.Ingame.IMyTerminalBlock pbBlock)
            {
                var dict = pbBlock.GetProperty("WcPbAPI")?.As<IReadOnlyDictionary<string, Delegate>>().GetValue(pbBlock);
                if (dict == null) throw new Exception("WcPbAPI failed to activate");
                return ApiAssign(dict);
            }

            public bool ApiAssign(IReadOnlyDictionary<string, Delegate> delegates)
            {
                if (delegates == null)
                    return false;
                AssignMethod(delegates, "GetSortedThreats", ref _getSortedThreats);
                AssignMethod(delegates, "GetObstructions", ref _getObstructions);
                AssignMethod(delegates, "GetAiFocus", ref _getAiFocus);
                AssignMethod(delegates, "GetCoreWeapons", ref _getCoreWeapons);

                return true;
            }

            private void AssignMethod<T>(IReadOnlyDictionary<string, Delegate> delegates, string name, ref T field) where T : class
            {
                if (delegates == null)
                {
                    field = null;
                    return;
                }

                Delegate del;
                if (!delegates.TryGetValue(name, out del))
                    throw new Exception($"{GetType().Name} :: Couldn't find {name} delegate of type {typeof(T)}");

                field = del as T;
                if (field == null)
                    throw new Exception(
                        $"{GetType().Name} :: Delegate {name} is not type {typeof(T)}, instead it's: {del.GetType()}");
            }
            public void GetAllCoreWeapons(ICollection<MyDefinitionId> collection) => _getCoreWeapons?.Invoke(collection);
            public void GetSortedThreats(Sandbox.ModAPI.Ingame.IMyTerminalBlock pBlock, IDictionary<MyDetectedEntityInfo, float> collection) =>
                _getSortedThreats?.Invoke(pBlock, collection);
            public void GetObstructions(Sandbox.ModAPI.Ingame.IMyTerminalBlock pBlock, ICollection<Sandbox.ModAPI.Ingame.MyDetectedEntityInfo> collection) =>
                _getObstructions?.Invoke(pBlock, collection);
            public MyDetectedEntityInfo? GetAiFocus(long shooter, int priority = 0) => _getAiFocus?.Invoke(shooter, priority);

        }


    }
}
