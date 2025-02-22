﻿using Barotrauma.IO;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    partial class GameSession
    {
        public enum InfoFrameTab { Crew, Mission, MyCharacter, Traitor };

        public readonly EventManager EventManager;

        public GameMode GameMode;

        //two locations used as the start and end in the MP mode
        private Location[] dummyLocations;
        public CrewManager CrewManager;

        public double RoundStartTime;

        private readonly List<Mission> missions = new List<Mission>();
        public IEnumerable<Mission> Missions { get { return missions; } }

        public CharacterTeamType? WinningTeam;

        public bool IsRunning { get; private set; }

        public bool RoundEnding { get; private set; }

        public Level Level { get; private set; }
        public LevelData LevelData { get; private set; }

        public bool MirrorLevel { get; private set; }

        public Map Map
        {
            get
            {
                return (GameMode as CampaignMode)?.Map;
            }
        }

        public CampaignMode Campaign
        {
            get
            {
                return GameMode as CampaignMode;
            }
        }
        

        public Location StartLocation
        {
            get
            {
                if (Map != null) { return Map.CurrentLocation; }
                if (dummyLocations == null) { CreateDummyLocations(); }
                return dummyLocations[0];
            }
        }

        public Location EndLocation
        {
            get
            {
                if (Map != null) { return Map.SelectedLocation; }
                if (dummyLocations == null) { CreateDummyLocations(); }
                return dummyLocations[1];
            }
        }

        public SubmarineInfo SubmarineInfo { get; set; }
        
        public List<SubmarineInfo> OwnedSubmarines = new List<SubmarineInfo>();

        public Submarine Submarine { get; set; }

        public string SavePath { get; set; }

        partial void InitProjSpecific();

        private GameSession(SubmarineInfo submarineInfo, List<SubmarineInfo> ownedSubmarines = null)
        {
            InitProjSpecific();
            SubmarineInfo = submarineInfo;

#if CLIENT
            if (ownedSubmarines == null && GameMode is MultiPlayerCampaign && GameMain.NetLobbyScreen.ServerOwnedSubmarines != null)
            {
                ownedSubmarines = GameMain.NetLobbyScreen.ServerOwnedSubmarines;
            }
#endif

            OwnedSubmarines = ownedSubmarines ?? new List<SubmarineInfo>();
            if (!OwnedSubmarines.Any(s => s.Name == submarineInfo.Name))
            {
                OwnedSubmarines.Add(submarineInfo);
            }
            GameMain.GameSession = this;
            EventManager = new EventManager();
        }

        /// <summary>
        /// Start a new GameSession. Will be saved to the specified save path (if playing a game mode that can be saved).
        /// </summary>
        public GameSession(SubmarineInfo submarineInfo, string savePath, GameModePreset gameModePreset, CampaignSettings settings, string seed = null, MissionType missionType = MissionType.None)
            : this(submarineInfo)
        {
            this.SavePath = savePath;
            CrewManager = new CrewManager(gameModePreset != null && gameModePreset.IsSinglePlayer);
            GameMode = InstantiateGameMode(gameModePreset, seed, submarineInfo, settings, missionType: missionType);
        }

        /// <summary>
        /// Start a new GameSession with a specific pre-selected mission.
        /// </summary>
        public GameSession(SubmarineInfo submarineInfo, GameModePreset gameModePreset, string seed = null, IEnumerable<MissionPrefab> missionPrefabs = null)
            : this(submarineInfo)
        {
            CrewManager = new CrewManager(gameModePreset != null && gameModePreset.IsSinglePlayer);
            GameMode = InstantiateGameMode(gameModePreset, seed, submarineInfo, CampaignSettings.Empty, missionPrefabs: missionPrefabs);
        }

        /// <summary>
        /// Load a game session from the specified XML document. The session will be saved to the specified path.
        /// </summary>
        public GameSession(SubmarineInfo submarineInfo, List<SubmarineInfo> ownedSubmarines, XDocument doc, string saveFile) : this(submarineInfo, ownedSubmarines)
        {
            this.SavePath = saveFile;
            GameMain.GameSession = this;
            //selectedSub.Name = doc.Root.GetAttributeString("submarine", selectedSub.Name);

            foreach (XElement subElement in doc.Root.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
#if CLIENT
                    case "gamemode": //legacy support
                    case "singleplayercampaign":
                        CrewManager = new CrewManager(true);
                        var campaign = SinglePlayerCampaign.Load(subElement);
                        campaign.LoadNewLevel();
                        GameMode = campaign;
                        break;
#endif
                    case "multiplayercampaign":
                        CrewManager = new CrewManager(false);
                        var mpCampaign = MultiPlayerCampaign.LoadNew(subElement);
                        GameMode = mpCampaign;
                        if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsServer) 
                        { 
                            mpCampaign.LoadNewLevel(); 
                            //save to ensure the campaign ID in the save file matches the one that got assigned to this campaign instance
                            SaveUtil.SaveGame(saveFile);
                        }
                        break;
                }
            }
        }

        private GameMode InstantiateGameMode(GameModePreset gameModePreset, string seed, SubmarineInfo selectedSub, CampaignSettings settings, IEnumerable<MissionPrefab> missionPrefabs = null, MissionType missionType = MissionType.None)
        {
            if (gameModePreset.GameModeType == typeof(CoOpMode) || gameModePreset.GameModeType == typeof(PvPMode))
            {
                //don't allow hidden mission types (e.g. GoTo) in single mission modes
                var missionTypes = (MissionType[])Enum.GetValues(typeof(MissionType));
                for (int i = 0; i < missionTypes.Length; i++)
                {
                    if (MissionPrefab.HiddenMissionClasses.Contains(missionTypes[i]))
                    {
                        missionType &= ~missionTypes[i];
                    }
                }
            }
            if (gameModePreset.GameModeType == typeof(CoOpMode))
            {
                return missionPrefabs != null ?
                    new CoOpMode(gameModePreset, missionPrefabs) :
                    new CoOpMode(gameModePreset, missionType, seed ?? ToolBox.RandomSeed(8));
            }
            else if (gameModePreset.GameModeType == typeof(PvPMode))
            {
                return missionPrefabs != null ?
                    new PvPMode(gameModePreset, missionPrefabs) :
                    new PvPMode(gameModePreset, missionType, seed ?? ToolBox.RandomSeed(8));
            }
            else if (gameModePreset.GameModeType == typeof(MultiPlayerCampaign))
            {
                var campaign = MultiPlayerCampaign.StartNew(seed ?? ToolBox.RandomSeed(8), selectedSub, settings);
                if (campaign != null && selectedSub != null)
                {
                    campaign.Money = Math.Max(MultiPlayerCampaign.MinimumInitialMoney, campaign.Money - selectedSub.Price);
                }
                return campaign;
            }
#if CLIENT
            else if (gameModePreset.GameModeType == typeof(SinglePlayerCampaign))
            {
                var campaign = SinglePlayerCampaign.StartNew(seed ?? ToolBox.RandomSeed(8), selectedSub, settings);
                if (campaign != null && selectedSub != null)
                {
                    campaign.Money = Math.Max(SinglePlayerCampaign.MinimumInitialMoney, campaign.Money - selectedSub.Price);
                }
                return campaign;
            }
            else if (gameModePreset.GameModeType == typeof(TutorialMode))
            {
                return new TutorialMode(gameModePreset);
            }
            else if (gameModePreset.GameModeType == typeof(TestGameMode))
            {
                return new TestGameMode(gameModePreset);
            }
#endif
            else if (gameModePreset.GameModeType == typeof(GameMode))
            {
                return new GameMode(gameModePreset);
            }
            else
            {
                throw new Exception($"Could not find a game mode of the type \"{gameModePreset.GameModeType}\"");
            }
        }

        private void CreateDummyLocations(LocationType? forceLocationType = null)
        {
            dummyLocations = new Location[2];

            string seed = "";
            if (GameMain.GameSession != null && GameMain.GameSession.Level != null)
            {
                seed = GameMain.GameSession.Level.Seed;
            }
            else if (GameMain.NetLobbyScreen != null)
            {
                seed = GameMain.NetLobbyScreen.LevelSeed;
            }

            MTRandom rand = new MTRandom(ToolBox.StringToInt(seed));
            for (int i = 0; i < 2; i++)
            {
                dummyLocations[i] = Location.CreateRandom(new Vector2((float)rand.NextDouble() * 10000.0f, (float)rand.NextDouble() * 10000.0f), null, rand, requireOutpost: true, forceLocationType: forceLocationType);
            }
        }

        public void LoadPreviousSave()
        {
            Submarine.Unload();
            SaveUtil.LoadGame(SavePath);
        }

        /// <summary>
        /// Switch to another submarine. The sub is loaded when the next round starts.
        /// </summary>
        public SubmarineInfo SwitchSubmarine(SubmarineInfo newSubmarine, int cost)
        {
            if (!OwnedSubmarines.Any(s => s.Name == newSubmarine.Name))
            {
                OwnedSubmarines.Add(newSubmarine);
            }
            else
            {
                // Fetch owned submarine data as the newSubmarine is just the base submarine
                for (int i = 0; i < OwnedSubmarines.Count; i++)
                {
                    if (OwnedSubmarines[i].Name == newSubmarine.Name)
                    {
                        newSubmarine = OwnedSubmarines[i];
                        break;
                    }
                }
            }

            Campaign.Money -= cost;

            ((CampaignMode)GameMode).PendingSubmarineSwitch = newSubmarine;
            return newSubmarine;
        }

        public void PurchaseSubmarine(SubmarineInfo newSubmarine)
        {
            if (Campaign == null) return;
            if (!OwnedSubmarines.Any(s => s.Name == newSubmarine.Name))
            {
                Campaign.Money -= newSubmarine.Price;
                OwnedSubmarines.Add(newSubmarine);
            }
        }

        public bool IsSubmarineOwned(SubmarineInfo query)
        {
            return 
                Submarine.MainSub.Info.Name == query.Name || 
                (OwnedSubmarines != null && OwnedSubmarines.Any(os => os.Name == query.Name));
        }

        public bool IsCurrentLocationRadiated()
        {
            if (Map?.CurrentLocation == null || Campaign == null) { return false; }

            bool isRadiated = Map.CurrentLocation.IsRadiated();

            if (Level.Loaded?.EndLocation is { } endLocation)
            {
                isRadiated |= endLocation.IsRadiated();
            }

            return isRadiated;
        }

        public void StartRound(string levelSeed, float? difficulty = null)
        {
            LevelData randomLevel = null;
            foreach (Mission mission in Missions.Union(GameMode.Missions))
            {
                MissionPrefab missionPrefab = mission.Prefab;
                if (missionPrefab != null &&
                    missionPrefab.AllowedLocationTypes.Any() &&
                    !missionPrefab.AllowedConnectionTypes.Any())
                {
                    LocationType locationType = LocationType.List.FirstOrDefault(lt => missionPrefab.AllowedLocationTypes.Any(m => m.Equals(lt.Identifier, StringComparison.OrdinalIgnoreCase)));
                    CreateDummyLocations(locationType);
                    randomLevel = LevelData.CreateRandom(levelSeed, difficulty, requireOutpost: true);
                    break;
                }
            }
            randomLevel ??= LevelData.CreateRandom(levelSeed, difficulty);
            StartRound(randomLevel);
        }

        public void StartRound(LevelData levelData, bool mirrorLevel = false, SubmarineInfo startOutpost = null, SubmarineInfo endOutpost = null)
        {
            MirrorLevel = mirrorLevel;
            if (SubmarineInfo == null)
            {
                DebugConsole.ThrowError("Couldn't start game session, submarine not selected.");
                return;
            }
            if (SubmarineInfo.IsFileCorrupted)
            {
                DebugConsole.ThrowError("Couldn't start game session, submarine file corrupted.");
                return;
            }
            if (SubmarineInfo.SubmarineElement.Elements().Count() == 0)
            {
                DebugConsole.ThrowError("Couldn't start game session, saved submarine is empty. The submarine file may be corrupted.");
                return;
            }

            LevelData = levelData;

            Submarine.Unload();
            Submarine = Submarine.MainSub = new Submarine(SubmarineInfo);
            foreach (Submarine sub in Submarine.GetConnectedSubs())
            {
                sub.TeamID = CharacterTeamType.Team1;
                foreach (Item item in Item.ItemList)
                {
                    if (item.Submarine != sub) { continue; }
                    foreach (WifiComponent wifiComponent in item.GetComponents<WifiComponent>())
                    {
                        wifiComponent.TeamID = sub.TeamID;
                    }
                }
            }

            foreach (Mission mission in GameMode.Missions)
            {
                // setting level for missions that may involve difficulty-related submarine creation
                mission.SetLevel(levelData);
            }

            if (Submarine.MainSubs[1] == null)
            {
                var enemySubmarineInfo = GameMode is PvPMode ? SubmarineInfo : GameMode.Missions.FirstOrDefault(m => m.EnemySubmarineInfo != null)?.EnemySubmarineInfo;
                if (enemySubmarineInfo != null)
                {
                    Submarine.MainSubs[1] = new Submarine(enemySubmarineInfo, true);
                }
            }

            if (GameMain.NetworkMember?.ServerSettings?.LockAllDefaultWires ?? false)
            {
                foreach (Item item in Item.ItemList)
                {
                    if (item.Submarine == Submarine.MainSubs[0] ||
                        (Submarine.MainSubs[1] != null && item.Submarine == Submarine.MainSubs[1]))
                    {
                        Wire wire = item.GetComponent<Wire>();
                        if (wire != null && !wire.NoAutoLock && wire.Connections.Any(c => c != null)) { wire.Locked = true; }
                    }
                }
            }

            Level level = null;
            if (levelData != null)
            {
                level = Level.Generate(levelData, mirrorLevel, startOutpost, endOutpost);
            }

            InitializeLevel(level);

#if CLIENT
            if (GameMode is CampaignMode) { SteamAchievementManager.OnBiomeDiscovered(levelData.Biome); }

            var existingRoundSummary = GUIMessageBox.MessageBoxes.Find(mb => mb.UserData is RoundSummary)?.UserData as RoundSummary;
            if (existingRoundSummary?.ContinueButton != null)
            {
                existingRoundSummary.ContinueButton.Visible = true;
            }

            RoundSummary = new RoundSummary(Submarine.Info, GameMode, Missions, StartLocation, EndLocation);

            if (!(GameMode is TutorialMode) && !(GameMode is TestGameMode))
            {
                GUI.AddMessage("", Color.Transparent, 3.0f, playSound: false);
                if (EndLocation != null)
                {
                    GUI.AddMessage(levelData.Biome.DisplayName, Color.Lerp(Color.CadetBlue, Color.DarkRed, levelData.Difficulty / 100.0f), 5.0f, playSound: false);
                    GUI.AddMessage(TextManager.AddPunctuation(':', TextManager.Get("Destination"), EndLocation.Name), Color.CadetBlue, playSound: false);
                    if (missions.Count > 1)
                    {
                        string joinedMissionNames = string.Join(", ", missions.Select(m => m.Name));
                        GUI.AddMessage(TextManager.AddPunctuation(':', TextManager.Get("Mission"), joinedMissionNames), Color.CadetBlue, playSound: false);
                    }
                    else
                    {
                        var mission = missions.FirstOrDefault();
                        GUI.AddMessage(TextManager.AddPunctuation(':', TextManager.Get("Mission"), mission?.Name ?? TextManager.Get("None")), Color.CadetBlue, playSound: false);
                    }
                }
                else
                {
                    GUI.AddMessage(TextManager.AddPunctuation(':', TextManager.Get("Location"), StartLocation.Name), Color.CadetBlue, playSound: false);
                }
            }

            GUI.PreventPauseMenuToggle = false;

            HintManager.OnRoundStarted();
#endif
        }

        private void InitializeLevel(Level level)
        {
            //make sure no status effects have been carried on from the next round
            //(they should be stopped in EndRound, this is a safeguard against cases where the round is ended ungracefully)
            StatusEffect.StopAll();

#if CLIENT
#if !DEBUG
            GameMain.LightManager.LosEnabled = GameMain.Client == null || GameMain.Client.CharacterInfo != null;
#endif
            if (GameMain.LightManager.LosEnabled) { GameMain.LightManager.LosAlpha = 1f; }
            if (GameMain.Client == null) { GameMain.LightManager.LosMode = GameMain.Config.LosMode; }
#endif
            LevelData = level?.LevelData;
            Level = level;

            PlaceSubAtStart(Level);

            foreach (var sub in Submarine.Loaded)
            {
                if (sub.Info.IsOutpost)
                {
                    sub.DisableObstructedWayPoints();
                }
            }

            Entity.Spawner = new EntitySpawner();

            missions.Clear();
            GameMode.AddExtraMissions(LevelData);
            missions.AddRange(GameMode.Missions);
            GameMode.Start();
            foreach (Mission mission in missions)
            {
                int prevEntityCount = Entity.GetEntities().Count();
                mission.Start(Level.Loaded);
                if (GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient && Entity.GetEntities().Count() != prevEntityCount)
                {
                    DebugConsole.ThrowError(
                        $"Entity count has changed after starting a mission ({mission.Prefab.Identifier}) as a client. " +
                        "The clients should not instantiate entities themselves when starting the mission," +
                        " but instead the server should inform the client of the spawned entities using Mission.ServerWriteInitial.");
                }
            }

            EventManager?.StartRound(Level.Loaded);
            SteamAchievementManager.OnStartRound();

            if (GameMode != null)
            {
                GameMode.ShowStartMessage();

                if (GameMain.NetworkMember == null) 
                {
                    //only place items and corpses here in single player
                    //the server does this after loading the respawn shuttle
                    Level?.SpawnNPCs();
                    Level?.SpawnCorpses();
                    Level?.PrepareBeaconStation();
                    AutoItemPlacer.PlaceIfNeeded();
                }
                if (GameMode is MultiPlayerCampaign mpCampaign)
                {
                    mpCampaign.UpgradeManager.ApplyUpgrades();
                    mpCampaign.UpgradeManager.SanityCheckUpgrades(Submarine);
                }
                if (GameMode is CampaignMode)
                {
                    Submarine.WarmStartPower();
                }
            }

            GameMain.Config.RecentlyEncounteredCreatures.Clear();

            GameMain.GameScreen.Cam.Position = Character.Controlled?.WorldPosition ?? Submarine.MainSub.WorldPosition;
            RoundStartTime = Timing.TotalTime;
            GameMain.ResetFrameTime();
            IsRunning = true;
        }

        public void PlaceSubAtStart(Level level)
        {
            if (level == null)
            {
                Submarine.MainSub.SetPosition(Vector2.Zero);
                return;
            }

            var originalSubPos = Submarine.WorldPosition;

            if (level.StartOutpost != null)
            {
                //start by placing the sub below the outpost
                Rectangle outpostBorders = Level.Loaded.StartOutpost.GetDockedBorders();
                Rectangle subBorders = Submarine.GetDockedBorders();

                Submarine.SetPosition(
                    Level.Loaded.StartOutpost.WorldPosition -
                    new Vector2(0.0f, outpostBorders.Height / 2 + subBorders.Height / 2));

                //find the port that's the nearest to the outpost and dock if one is found
                float closestDistance = 0.0f;
                DockingPort myPort = null, outPostPort = null;
                foreach (DockingPort port in DockingPort.List)
                {
                    if (port.IsHorizontal || port.Docked) { continue; }
                    if (port.Item.Submarine == level.StartOutpost)
                    {
                        if (port.DockingTarget == null)
                        {
                            outPostPort = port;
                        }
                        continue;
                    }
                    if (port.Item.Submarine != Submarine) { continue; }

                    //the submarine port has to be at the top of the sub
                    if (port.Item.WorldPosition.Y < Submarine.WorldPosition.Y) { continue; }

                    float dist = Vector2.DistanceSquared(port.Item.WorldPosition, level.StartOutpost.WorldPosition);
                    if ((myPort == null || dist < closestDistance || port.MainDockingPort) && !(myPort?.MainDockingPort ?? false))
                    {
                        myPort = port;
                        closestDistance = dist;
                    }
                }

                if (myPort != null && outPostPort != null)
                {
                    Vector2 portDiff = myPort.Item.WorldPosition - Submarine.WorldPosition;
                    Vector2 spawnPos = (outPostPort.Item.WorldPosition - portDiff) - Vector2.UnitY * outPostPort.DockedDistance;

                    bool startDocked = level.Type == LevelData.LevelType.Outpost;
#if CLIENT
                    startDocked |= GameMode is TutorialMode;
#endif
                    if (startDocked)
                    {
                        Submarine.SetPosition(spawnPos);
                        myPort.Dock(outPostPort);
                        myPort.Lock(isNetworkMessage: true, applyEffects: false);
                    }
                    else
                    {
                        Submarine.SetPosition(spawnPos - Vector2.UnitY * 100.0f);
                        Submarine.NeutralizeBallast(); 
                        Submarine.EnableMaintainPosition();
                    }
                }
                else
                {
                    Submarine.NeutralizeBallast();
                    Submarine.EnableMaintainPosition();
                }
            }
            else
            {
                Submarine.SetPosition(Submarine.FindSpawnPos(level.StartPosition));
                Submarine.NeutralizeBallast();
                Submarine.EnableMaintainPosition();
            }

            // Make sure that linked subs which are NOT docked to the main sub
            // (but still close enough to NOT be considered as 'left behind')
            // are also moved to keep their relative position to the main sub
            var linkedSubs = MapEntity.mapEntityList.FindAll(me => me is LinkedSubmarine);
            foreach (LinkedSubmarine ls in linkedSubs)
            {
                if (ls.Sub == null || ls.Submarine != Submarine) { continue; }
                if (!ls.LoadSub || ls.Sub.DockedTo.Contains(Submarine)) { continue; }
                if (Submarine.Info.LeftBehindDockingPortIDs.Contains(ls.OriginalLinkedToID)) { continue; }
                if (ls.Sub.Info.SubmarineElement.Attribute("location") != null) { continue; }
                ls.Sub.SetPosition(ls.Sub.WorldPosition + (Submarine.WorldPosition - originalSubPos));
            }
        }

        public void Update(float deltaTime)
        {
            EventManager?.Update(deltaTime);
            GameMode?.Update(deltaTime);
            //backwards for loop because the missions may get completed and removed from the list in Update()
            for (int i = missions.Count - 1; i >= 0; i--)
            {
                missions[i].Update(deltaTime);
            }
            UpdateProjSpecific(deltaTime);
        }

        public Mission GetMission(int index)
        {
            if (index < 0 || index >= missions.Count) { return null; }
            return missions[index];
        }

        public int GetMissionIndex(Mission mission)
        {
            return missions.IndexOf(mission);
        }

        public void EnforceMissionOrder(List<string> missionIdentifiers)
        {
            List<Mission> sortedMissions = new List<Mission>();
            foreach (string missionId in missionIdentifiers)
            {
                var matchingMission = missions.Find(m => m.Prefab.Identifier == missionId);
                sortedMissions.Add(matchingMission);
                missions.Remove(matchingMission);
            }
            missions.AddRange(sortedMissions);
        }

        partial void UpdateProjSpecific(float deltaTime);

        public static IEnumerable<Character> GetSessionCrewCharacters()
        {
#if SERVER
            return GameMain.Server.ConnectedClients.Select(c => c.Character).Where(c => c?.Info != null && !c.IsDead);
#else
            if (GameMain.GameSession == null) { return Enumerable.Empty<Character>(); }
            return GameMain.GameSession.CrewManager.GetCharacters().Where(c => c?.Info != null && !c.IsDead);
#endif        
        }

        public void EndRound(string endMessage, List<TraitorMissionResult> traitorResults = null, CampaignMode.TransitionType transitionType = CampaignMode.TransitionType.None)
        {
            RoundEnding = true;

            try
            {
                IEnumerable<Character> crewCharacters = GetSessionCrewCharacters();

                foreach (Mission mission in missions)
                {
                    mission.End();
                }

                foreach (Character character in crewCharacters)
                {
                    character.CheckTalents(AbilityEffectType.OnRoundEnd);
                }

                if (missions.Any())
                {
                    if (missions.Any(m => m.Completed))
                    {
                        foreach (Character character in crewCharacters)
                        {
                            character.CheckTalents(AbilityEffectType.OnAnyMissionCompleted);
                        }
                    }

                    if (missions.All(m => m.Completed))
                    {
                        foreach (Character character in crewCharacters)
                        {
                            character.CheckTalents(AbilityEffectType.OnAllMissionsCompleted);
                        }
                    }
                }

#if CLIENT
                if (GUI.PauseMenuOpen)
                {
                    GUI.TogglePauseMenu();
                }
                GUI.PreventPauseMenuToggle = true;

                if (!(GameMode is TestGameMode) && Screen.Selected == GameMain.GameScreen && RoundSummary != null)
                {
                    GUI.ClearMessages();
                    GUIMessageBox.MessageBoxes.RemoveAll(mb => mb.UserData is RoundSummary);
                    GUIFrame summaryFrame = RoundSummary.CreateSummaryFrame(this, endMessage, traitorResults, transitionType);
                    GUIMessageBox.MessageBoxes.Add(summaryFrame);
                    RoundSummary.ContinueButton.OnClicked = (_, __) => { GUIMessageBox.MessageBoxes.Remove(summaryFrame); return true; };
                }

                if (GameMain.NetLobbyScreen != null) { GameMain.NetLobbyScreen.OnRoundEnded(); }
                TabMenu.OnRoundEnded();
                GUIMessageBox.MessageBoxes.RemoveAll(mb => mb.UserData as string == "ConversationAction" || ReadyCheck.IsReadyCheck(mb));
#endif
                SteamAchievementManager.OnRoundEnded(this);

                GameMode?.End(transitionType);
                EventManager?.EndRound();
                StatusEffect.StopAll();
                missions.Clear();
                IsRunning = false;

#if CLIENT
                HintManager.OnRoundEnded();
#endif
            }
            finally
            {
                RoundEnding = false;
            }
        }

        public void KillCharacter(Character character)
        {
#if CLIENT
            CrewManager.KillCharacter(character);
#endif
        }

        public void ReviveCharacter(Character character)
        {
#if CLIENT
            CrewManager.ReviveCharacter(character);
#endif
        }

        public static bool IsCompatibleWithEnabledContentPackages(IList<string> contentPackagePaths, out string errorMsg)
        {
            errorMsg = "";
            //no known content packages, must be an older save file
            if (!contentPackagePaths.Any()) { return true; }

            List<string> missingPackages = new List<string>();
            foreach (string packagePath in contentPackagePaths)
            {
                if (!GameMain.Config.AllEnabledPackages.Any(cp => cp.Path == packagePath))
                {
                    missingPackages.Add(packagePath);
                }
            }
            List<string> excessPackages = new List<string>();
            foreach (ContentPackage cp in GameMain.Config.AllEnabledPackages)
            {
                if (!cp.HasMultiplayerIncompatibleContent) { continue; }
                if (!contentPackagePaths.Any(p => p == cp.Path))
                {
                    excessPackages.Add(cp.Name);
                }
            }

            bool orderMismatch = false;
            if (missingPackages.Count == 0 && missingPackages.Count == 0)
            {
                var enabledPackages = GameMain.Config.AllEnabledPackages.Where(cp => cp.HasMultiplayerIncompatibleContent).ToList();
                for (int i = 0; i < contentPackagePaths.Count && i < enabledPackages.Count; i++)
                {
                    if (contentPackagePaths[i] != enabledPackages[i].Path)
                    {
                        orderMismatch = true;
                        break;
                    }
                }
            }

            if (!orderMismatch && missingPackages.Count == 0 && excessPackages.Count == 0) { return true; }

            if (missingPackages.Count == 1)
            {
                errorMsg = TextManager.GetWithVariable("campaignmode.missingcontentpackage", "[missingcontentpackage]", missingPackages[0]);
            }
            else if (missingPackages.Count > 1)
            {
                errorMsg = TextManager.GetWithVariable("campaignmode.missingcontentpackages", "[missingcontentpackages]", string.Join(", ", missingPackages));
            }
            if (excessPackages.Count == 1)
            {
                if (!string.IsNullOrEmpty(errorMsg)) { errorMsg += "\n"; }
                errorMsg += TextManager.GetWithVariable("campaignmode.incompatiblecontentpackage", "[incompatiblecontentpackage]", excessPackages[0]);
            }
            else if (excessPackages.Count > 1)
            {
                if (!string.IsNullOrEmpty(errorMsg)) { errorMsg += "\n"; }
                errorMsg += TextManager.GetWithVariable("campaignmode.incompatiblecontentpackages", "[incompatiblecontentpackages]", string.Join(", ", excessPackages));
            }
            if (orderMismatch)
            {
                if (!string.IsNullOrEmpty(errorMsg)) { errorMsg += "\n"; }
                errorMsg += TextManager.GetWithVariable("campaignmode.contentpackageordermismatch", "[loadorder]", string.Join(", ", contentPackagePaths));
            }

            return false;
        }

        public void Save(string filePath)
        {
            if (!(GameMode is CampaignMode))
            {
                throw new NotSupportedException("GameSessions can only be saved when playing in a campaign mode.");
            }

            XDocument doc = new XDocument(new XElement("Gamesession"));

            doc.Root.Add(new XAttribute("savetime", ToolBox.Epoch.NowLocal));
            doc.Root.Add(new XAttribute("version", GameMain.Version));
            var submarineInfo = Campaign?.PendingSubmarineSwitch ?? SubmarineInfo;
            doc.Root.Add(new XAttribute("submarine", submarineInfo == null ? "" : submarineInfo.Name));
            if (OwnedSubmarines != null)
            {
                List<string> ownedSubmarineNames = new List<string>();
                var ownedSubsElement = new XElement("ownedsubmarines");
                doc.Root.Add(ownedSubsElement);
                foreach (var ownedSub in OwnedSubmarines)
                {
                    ownedSubsElement.Add(new XElement("sub", new XAttribute("name", ownedSub.Name)));
                }
            }
            doc.Root.Add(new XAttribute("mapseed", Map.Seed));
            doc.Root.Add(new XAttribute("selectedcontentpackages",
                string.Join("|", GameMain.Config.AllEnabledPackages.Where(cp => cp.HasMultiplayerIncompatibleContent).Select(cp => cp.Path))));

            ((CampaignMode)GameMode).Save(doc.Root);

            try
            {
                doc.SaveSafe(filePath);
            }
            catch (Exception e)
            {
                DebugConsole.ThrowError("Saving gamesession to \"" + filePath + "\" failed!", e);
            }
        }

        /*public void Load(XElement saveElement)
        {
            foreach (XElement subElement in saveElement.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
#if CLIENT
                    case "gamemode": //legacy support
                    case "singleplayercampaign":
                        GameMode = SinglePlayerCampaign.Load(subElement);
                        break;
#endif
                    case "multiplayercampaign":
                        if (!(GameMode is MultiPlayerCampaign mpCampaign))
                        {
                            DebugConsole.ThrowError("Error while loading a save file: the save file is for a multiplayer campaign but the current gamemode is " + GameMode.GetType().ToString());
                            break;
                        }

                        mpCampaign.Load(subElement);
                        break;
                }
            }
        }*/

    }
}
