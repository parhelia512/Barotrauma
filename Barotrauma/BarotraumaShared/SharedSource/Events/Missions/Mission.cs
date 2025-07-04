﻿using Barotrauma.Abilities;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Barotrauma
{
    abstract partial class Mission
    {
        public readonly MissionPrefab Prefab;
        private bool completed;
        protected bool failed;

        protected Level level;

        protected int state;
        public virtual int State
        {
            get { return state; }
            set
            {
                if (state != value)
                {
                    int previousState = state;
                    state = value;
                    TryTriggerEvents(state);
#if SERVER
                    GameMain.Server?.UpdateMissionState(this);
#elif CLIENT
                    if (Prefab.ShowProgressBar)
                    {
                        CharacterHUD.ShowMissionProgressBar(this);
                    }
#endif
                    ShowMessage(State);
                    OnMissionStateChanged?.Invoke(this);
                    MissionStateChanged(previousState);
                }
            }
        }

        public int TimesAttempted { get; set; }

        protected static bool IsClient => GameMain.NetworkMember != null && GameMain.NetworkMember.IsClient;

        private readonly CheckDataAction completeCheckDataAction;

        public readonly ImmutableArray<LocalizedString> Headers;
        public readonly ImmutableArray<LocalizedString> Messages;

        /// <summary>
        /// The reward that was actually given from completing the mission, taking any talent bonuses into account 
        /// (some of which may not be possible to determine in advance)
        /// </summary>
        private int? finalReward;

        public virtual LocalizedString Name => Prefab.Name;

        private readonly LocalizedString successMessage;
        public virtual LocalizedString SuccessMessage
        {
            get { return successMessage; }
            //private set { successMessage = value; }
        }

        private readonly LocalizedString failureMessage;
        public virtual LocalizedString FailureMessage
        {
            get { return failureMessage; }
            //private set { failureMessage = value; }
        }

        protected LocalizedString description;
        public virtual LocalizedString Description
        {
            get { return description; }
            //private set { description = value; }
        }

        protected LocalizedString descriptionWithoutReward;

        public virtual bool AllowUndocking
        {
            get { return true; }
        }

        public virtual int Reward
        {
            get 
            {
                return Prefab.Reward;
            }
        }

        public ImmutableList<MissionPrefab.ReputationReward> ReputationRewards
        {
            get { return Prefab.ReputationRewards; }
        }

        public bool Completed
        {
            get { return completed; }
            set { completed = value; }
        }

        public bool Failed
        {
            get { return failed; }
        }

        public virtual bool AllowRespawning
        {
            get { return true; }
        }

        public virtual int TeamCount
        {
            get { return 1; }
        }

        public virtual SubmarineInfo EnemySubmarineInfo
        {
            get { return null; }
        }

        public virtual IEnumerable<(LocalizedString Label, Vector2 Position)> SonarLabels
        {
            get { return Enumerable.Empty<(LocalizedString Label, Vector2 Position)>(); }
        }

        public Identifier SonarIconIdentifier => Prefab.SonarIconIdentifier;

        /// <summary>
        /// Where was this mission received from? Affects which faction we give reputation for if the mission is configured to give reputation for the faction that gave the mission.
        /// Defaults to Locations[0]
        /// </summary>
        public Location OriginLocation;

        public readonly Location[] Locations;

        public int? Difficulty
        {
            get { return Prefab.Difficulty; }
        }

        private class DelayedTriggerEvent
        {
            public readonly MissionPrefab.TriggerEvent TriggerEvent;
            public float Delay;

            public DelayedTriggerEvent(MissionPrefab.TriggerEvent triggerEvent, float delay)
            {
                TriggerEvent = triggerEvent;
                Delay = delay;
            }
        }

        private readonly List<DelayedTriggerEvent> delayedTriggerEvents = new List<DelayedTriggerEvent>();

        public Action<Mission> OnMissionStateChanged;
        
        protected readonly ContentXElement characterConfig;
        protected readonly List<Character> characters = new List<Character>();
        protected readonly Dictionary<Character, List<Item>> characterItems = new Dictionary<Character, List<Item>>();

        public Mission(MissionPrefab prefab, Location[] locations, Submarine sub)
        {
            System.Diagnostics.Debug.Assert(locations.Length == 2);

            Prefab = prefab;

            description = prefab.Description.Value;
            successMessage = prefab.SuccessMessage.Value;
            failureMessage = prefab.FailureMessage.Value;
            Headers = prefab.Headers;
            var messages = prefab.Messages.ToArray();

            OriginLocation = locations[0];
            Locations = locations;

            var endConditionElement = prefab.ConfigElement.GetChildElement(nameof(completeCheckDataAction));
            if (endConditionElement != null)
            {
                completeCheckDataAction = new CheckDataAction(endConditionElement, $"Mission ({prefab.Identifier})");
            }

            descriptionWithoutReward = ReplaceVariablesInMissionMessage(description, sub, replaceReward: false);
            description = ReplaceVariablesInMissionMessage(description, sub);
            successMessage = ReplaceVariablesInMissionMessage(successMessage, sub);
            failureMessage = ReplaceVariablesInMissionMessage(failureMessage, sub);
            for (int m = 0; m < messages.Length; m++)
            {
                messages[m] = ReplaceVariablesInMissionMessage(messages[m], sub);
            }
            Messages = messages.ToImmutableArray();
            
            characterConfig = prefab.ConfigElement.GetChildElement("Characters");
            if (prefab.ConfigElement.GetChildElements("Characters").Count() > 1)
            {
                DebugConsole.AddWarning($"Error in mission {Prefab.Identifier}: multiple <Characters> elements found. Only the first one will be used.",
                    contentPackage: prefab.ContentPackage);
            }
        }

        public LocalizedString ReplaceVariablesInMissionMessage(LocalizedString message, Submarine sub, bool replaceReward = true)
        {
            for (int locationIndex = 0; locationIndex < 2; locationIndex++)
            {
                string locationName = $"‖color:gui.orange‖{Locations[locationIndex].DisplayName}‖end‖";
                message = message.Replace("[location" + (locationIndex + 1) + "]", locationName);
            }
            if (replaceReward)
            {
                string rewardText = $"‖color:gui.orange‖{string.Format(CultureInfo.InvariantCulture, "{0:N0}", GetReward(sub))}‖end‖";
                message = message.Replace("[reward]", rewardText);
            }
            return message;
        }
        
        protected virtual void MissionStateChanged(int previousState) {}

        public virtual void SetLevel(LevelData level) { }

        public static Mission LoadRandom(Location[] locations, string seed, bool requireCorrectLocationType, IEnumerable<Identifier> missionTypes, bool isSinglePlayer = false, float? difficultyLevel = null)
        {
            return LoadRandom(locations, new MTRandom(ToolBox.StringToInt(seed)), requireCorrectLocationType, missionTypes, isSinglePlayer, difficultyLevel);
        }

        public static Mission LoadRandom(Location[] locations, MTRandom rand, bool requireCorrectLocationType, IEnumerable<Identifier> missionTypes, bool isSinglePlayer = false, float? difficultyLevel = null)
        {
            List<MissionPrefab> allowedMissions = new List<MissionPrefab>();
            if (missionTypes.None())
            {
                return null;
            }
            else
            {
                allowedMissions.AddRange(MissionPrefab.Prefabs.Where(m => missionTypes.Contains(m.Type)));
            }
            allowedMissions.RemoveAll(m => isSinglePlayer ? m.MultiplayerOnly : m.SingleplayerOnly);
            if (requireCorrectLocationType)
            {
                allowedMissions.RemoveAll(m => !m.IsAllowed(locations[0], locations[1]));
            }
            if (difficultyLevel.HasValue)
            {
                allowedMissions.RemoveAll(m => !m.IsAllowedDifficulty(difficultyLevel.Value));
            }
            if (allowedMissions.Count == 0) { return null; }
            MissionPrefab missionPrefab = ToolBox.SelectWeightedRandom(allowedMissions, m => m.Commonness, rand);
            return missionPrefab.Instantiate(locations, Submarine.MainSub);
        }

        /// <summary>
        /// Calculates the base reward, can be overridden for different mission types
        /// </summary>
        public virtual float GetBaseReward(Submarine sub)
        {
            return Prefab.Reward;
        }

        /// <summary>
        /// Calculates the available monetary reward, taking into account universal modifiers such as campaign settings.
        /// </summary>
        public int GetReward(Submarine sub)
        {
            float reward = GetBaseReward(sub);
            // Some modifiers should apply universally to all implementations of GetBaseReward
            if (GameMain.GameSession?.Campaign is CampaignMode campaign)
            {
                reward *= campaign.Settings.MissionRewardMultiplier;
            }
            return (int)Math.Round(reward);
        }
        
        /// <summary>
        /// Call to load character elements to be spawned. Has to be implemented (and synced) separately per each mission.
        /// </summary>
        protected void InitCharacters(Submarine submarine)
        {
            characters.Clear();
            characterItems.Clear();

            if (characterConfig != null) 
            { 
                foreach (XElement element in characterConfig.Elements())
                {
                    if (GameMain.NetworkMember == null && element.GetAttributeBool("multiplayeronly", false)) { continue; }

                    int defaultCount = element.GetAttributeInt("count", -1);
                    if (defaultCount < 0)
                    {
                        defaultCount = element.GetAttributeInt("amount", 1);
                    }
                    int min = Math.Min(element.GetAttributeInt("min", defaultCount), 255);
                    int max = Math.Min(Math.Max(min, element.GetAttributeInt("max", defaultCount)), 255);
                    int count = Rand.Range(min, max + 1);

                    if (element.Attribute("identifier") != null && element.Attribute("from") != null)
                    {
                        HumanPrefab humanPrefab = GetHumanPrefabFromElement(element); 
                        if (humanPrefab == null)
                        {
                            DebugConsole.ThrowError($"Couldn't spawn a human character for a mission: human prefab \"{element.GetAttributeString("identifier", string.Empty)}\" not found",
                                contentPackage: Prefab.ContentPackage);
                            continue;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            LoadHuman(humanPrefab, element, submarine);
                        }
                    }
                    else
                    {
                        Identifier speciesName = element.GetAttributeIdentifier("character", element.GetAttributeIdentifier("identifier", Identifier.Empty));
                        var characterPrefab = CharacterPrefab.FindBySpeciesName(speciesName);
                        if (characterPrefab == null)
                        {
                            DebugConsole.ThrowError($"Couldn't spawn a character for a mission: character prefab \"{speciesName}\" not found",
                                contentPackage: Prefab.ContentPackage);
                            continue;
                        }
                        for (int i = 0; i < count; i++)
                        {
                            LoadMonster(characterPrefab, element, submarine);
                        }
                    }
                }
            }            
        }
        
        private SpawnAction.SpawnLocationType GetSpawnLocationTypeFromSubmarineType(Submarine sub)
        {
            return sub.Info.Type switch
            {
                SubmarineType.Outpost or SubmarineType.OutpostModule => SpawnAction.SpawnLocationType.Outpost,
                SubmarineType.Wreck => SpawnAction.SpawnLocationType.Wreck,
                SubmarineType.Ruin => SpawnAction.SpawnLocationType.Ruin,
                SubmarineType.BeaconStation => SpawnAction.SpawnLocationType.BeaconStation,
                SubmarineType.Player => SpawnAction.SpawnLocationType.MainSub,
                _ => SpawnAction.SpawnLocationType.Any
            };
        }

        protected virtual Character LoadHuman(HumanPrefab humanPrefab, XElement element, Submarine submarine)
        {
            Identifier[] moduleFlags = element.GetAttributeIdentifierArray("moduleflags", null);
            Identifier[] spawnPointTags = element.GetAttributeIdentifierArray("spawnpointtags", null);
            var spawnPointType = element.GetAttributeEnum("spawnpointtype", SpawnType.Human);
            ISpatialEntity spawnPos = SpawnAction.GetSpawnPos(
                GetSpawnLocationTypeFromSubmarineType(submarine), spawnPointType,
                moduleFlags ?? humanPrefab.GetModuleFlags(),
                spawnPointTags ?? humanPrefab.GetSpawnPointTags(),
                element.GetAttributeBool("asfaraspossible", false));
            spawnPos ??= submarine.GetHulls(alsoFromConnectedSubs: false).GetRandomUnsynced();
            var teamId = element.GetAttributeEnum("teamid", CharacterTeamType.None);
            var originalTeam = Level.Loaded.StartOutpost?.TeamID ?? teamId;
            Character spawnedCharacter = CreateHuman(humanPrefab, characters, characterItems, submarine, originalTeam, spawnPos);
            //consider the NPC to be "originally" from the team of the outpost it spawns in, and change it to the desired (hostile) team afterwards
            //that allows the NPC to fight intruders and otherwise function in the outpost if the mission is configured to spawn the hostile NPCs in a friendly outpost
            if (teamId != originalTeam)
            {
                spawnedCharacter.SetOriginalTeamAndChangeTeam(teamId, processImmediately: true);
            }
            if (element.GetAttribute("color") != null)
            {
                spawnedCharacter.UniqueNameColor = element.GetAttributeColor("color", Color.Red);
            }
            if (submarine.Info is { IsOutpost: true } outPostInfo)
            {
                outPostInfo.AddOutpostNPCIdentifierOrTag(spawnedCharacter, humanPrefab.Identifier);
                foreach (Identifier tag in humanPrefab.GetTags())
                {
                    outPostInfo.AddOutpostNPCIdentifierOrTag(spawnedCharacter, tag);
                }
            }
            if (spawnPos is WayPoint wp)
            {
                spawnedCharacter.GiveIdCardTags(wp);
            }
            InitCharacter(spawnedCharacter, element);
            return spawnedCharacter;
        }

        protected virtual Character LoadMonster(CharacterPrefab monsterPrefab, XElement element, Submarine submarine)
        {
            Identifier[] moduleFlags = element.GetAttributeIdentifierArray("moduleflags", null);
            Identifier[] spawnPointTags = element.GetAttributeIdentifierArray("spawnpointtags", null);
            ISpatialEntity spawnPos = SpawnAction.GetSpawnPos(SpawnAction.SpawnLocationType.Outpost, SpawnType.Enemy, moduleFlags, spawnPointTags, element.GetAttributeBool("asfaraspossible", false));
            spawnPos ??= submarine.GetHulls(alsoFromConnectedSubs: false).GetRandomUnsynced();
            Character spawnedCharacter = Character.Create(monsterPrefab.Identifier, spawnPos.WorldPosition, ToolBox.RandomSeed(8), createNetworkEvent: false);
            characters.Add(spawnedCharacter);
            if (spawnedCharacter.Inventory != null)
            {
                characterItems.Add(spawnedCharacter, spawnedCharacter.Inventory.FindAllItems(recursive: true));
            }
            if (submarine != null && spawnedCharacter.AIController is EnemyAIController enemyAi)
            {
                enemyAi.UnattackableSubmarines.Add(submarine);
                enemyAi.UnattackableSubmarines.Add(Submarine.MainSub);
                foreach (Submarine sub in Submarine.MainSub.DockedTo)
                {
                    enemyAi.UnattackableSubmarines.Add(sub);
                }
            }
            InitCharacter(spawnedCharacter, element);
            return spawnedCharacter;
        }
        
        protected virtual void InitCharacter(Character character, XElement element)
        {
            if (element.GetAttributeBool(Tags.IgnoredByAI.Value, false))
            {
                character.AddAbilityFlag(AbilityFlags.IgnoredByEnemyAI);
            }
            float playDeadProbability = element.GetAttributeFloat("playdeadprobability", -1);
            if (playDeadProbability >= 0)
            {
                character.EvaluatePlayDeadProbability(playDeadProbability);
            }
            float huskProbability = element.GetAttributeFloat("huskprobability", 0);
            if (huskProbability > 0 && Rand.Value() <= huskProbability)
            {
                character.TurnIntoHusk();
            }
            else if (element.GetAttributeBool("corpse", false))
            {
                character.Kill(CauseOfDeathType.Unknown, causeOfDeathAffliction: null, log: false);
            }
        }

        public void Start(Level level)
        {
            state = 0;
#if CLIENT
            shownMessages.Clear();
#endif
            delayedTriggerEvents.Clear();
            foreach (string categoryToShow in Prefab.UnhideEntitySubCategories)
            {
                foreach (MapEntity entityToShow in MapEntity.MapEntityList.Where(me => me.Prefab?.HasSubCategory(categoryToShow) ?? false))
                {
                    entityToShow.IsLayerHidden = false;
                }
            }
            this.level = level;
            TryTriggerEvents(0);
            StartMissionSpecific(level);
        }

        protected virtual void StartMissionSpecific(Level level) { }

        public void Update(float deltaTime)
        {
            for (int i = delayedTriggerEvents.Count - 1; i>=0;i--)
            {
                delayedTriggerEvents[i].Delay -= deltaTime;
                if (delayedTriggerEvents[i].Delay <= 0.0f)
                {
                    TriggerEvent(delayedTriggerEvents[i].TriggerEvent);
                    delayedTriggerEvents.RemoveAt(i);
                }
            }
            UpdateMissionSpecific(deltaTime);
        }

        protected virtual void UpdateMissionSpecific(float deltaTime) { }

        protected void ShowMessage(int missionState)
        {
            ShowMessageProjSpecific(missionState);
        }

        partial void ShowMessageProjSpecific(int missionState);

        protected virtual LocalizedString ModifyMessage(LocalizedString message, bool color = true)
        {
            return message;
        }

        private void TryTriggerEvents(int state)
        {
            foreach (var triggerEvent in Prefab.TriggerEvents)
            {
                if (triggerEvent.State == state)
                {
                    TryTriggerEvent(triggerEvent);
                }
            }
        }

        /// <summary>
        /// Triggers the event or adds it to the delayedTriggerEvents it if it has a delay
        /// </summary>
        private void TryTriggerEvent(MissionPrefab.TriggerEvent trigger)
        {
            if (trigger.CampaignOnly && GameMain.GameSession?.Campaign == null) { return; }
            if (trigger.Delay > 0 || trigger.State == 0)
            {
                if (!delayedTriggerEvents.Any(t => t.TriggerEvent == trigger))
                {
                    delayedTriggerEvents.Add(new DelayedTriggerEvent(trigger, trigger.Delay));
                }
            }
            else
            {
                TriggerEvent(trigger);
            }
        }

        /// <summary>
        /// Triggers the event immediately, ignoring any delays
        /// </summary>
        private void TriggerEvent(MissionPrefab.TriggerEvent trigger)
        {
            if (trigger.CampaignOnly && GameMain.GameSession?.Campaign == null) { return; }
            //clients are not allowed to trigger events, they're handled by the server
            if (GameMain.NetworkMember is { IsClient: true }) { return; }
            EventPrefab eventPrefab = EventPrefab.FindEventPrefab(trigger.EventIdentifier, trigger.EventTag, Prefab.ContentPackage);
            if (eventPrefab == null)
            {
                DebugConsole.ThrowError($"Mission {Prefab.Identifier} failed to trigger an event (identifier: {trigger.EventIdentifier}, tag: {trigger.EventTag}).", contentPackage: Prefab.ContentPackage);
                return;
            }
            if (GameMain.GameSession?.EventManager != null)
            {
                var newEvent = eventPrefab.CreateInstance(GameMain.GameSession.EventManager.RandomSeed);
                GameMain.GameSession.EventManager.ActivateEvent(newEvent);
            }
        }

        /// <summary>
        /// End the mission and give a reward if it was completed successfully
        /// </summary>
        public void End()
        {
            if (GameMain.NetworkMember is not { IsClient: true })
            {
                completed =                 
                    DetermineCompleted() && 
                    (completeCheckDataAction == null ||completeCheckDataAction.GetSuccess());
            }
            if (completed)
            {
                if (Prefab.LocationTypeChangeOnCompleted != null)
                {
                    ChangeLocationType(Prefab.LocationTypeChangeOnCompleted);
                }
                try
                {
                    GiveReward();
                }
                catch (Exception e)
                {
                    string errorMsg = "Unknown error while giving mission rewards.";
                    DebugConsole.ThrowError(errorMsg, e, contentPackage: Prefab.ContentPackage);
                    GameAnalyticsManager.AddErrorEventOnce("Mission.End:GiveReward", GameAnalyticsManager.ErrorSeverity.Error, errorMsg + "\n" + e.StackTrace);
#if SERVER
                    GameMain.Server?.SendChatMessage(errorMsg + "\n" + e.StackTrace, Networking.ChatMessageType.Error);
#endif
                }
            }

            TimesAttempted++;

            EndMissionSpecific(completed);
        }

        protected abstract bool DetermineCompleted();

        protected virtual void EndMissionSpecific(bool completed) { }

        /// <summary>
        /// Get the final reward, taking talent bonuses into account if the mission has concluded and the talents modified the reward accordingly.
        /// </summary>
        public int GetFinalReward(Submarine sub)
        {
            return finalReward ?? GetReward(sub);
        }

        /// <summary>
        /// Calculates the final reward after talent bonuses have been applied. Note that this triggers talent effects of the type OnGainMissionMoney, 
        /// and should only be called once when the mission is completed!
        /// </summary>
        private void CalculateFinalReward(Submarine sub)
        {
            int reward = GetReward(sub);
            IEnumerable<Character> crewCharacters = GameSession.GetSessionCrewCharacters(CharacterType.Both);
            var missionMoneyGainMultiplier = new AbilityMissionMoneyGainMultiplier(this, 1f);
            CharacterTalent.CheckTalentsForCrew(crewCharacters, AbilityEffectType.OnGainMissionMoney, missionMoneyGainMultiplier);
            crewCharacters.ForEach(c => missionMoneyGainMultiplier.Value += c.GetStatValue(StatTypes.MissionMoneyGainMultiplier));
            finalReward = (int)(reward * missionMoneyGainMultiplier.Value);
        }

        private float CalculateDifficultyXPMultiplier()
        {
            const float minMissionDifficulty = 1;
            const float maxMissionDifficulty = 4;
            const float maxXpBonus = 1.3f;
            float selectedMissionDifficulty = MathUtils.InverseLerp(minMissionDifficulty, maxMissionDifficulty, Prefab.Difficulty.GetValueOrDefault());
            float xpBonusMultiplier = MathHelper.Lerp(1.0f, maxXpBonus, selectedMissionDifficulty);

            return xpBonusMultiplier;
        }

        private void GiveReward()
        {
            if (GameMain.GameSession.GameMode is not CampaignMode campaign) { return; }

            float xpReward = GetBaseReward(Submarine.MainSub) * Prefab.ExperienceMultiplier * campaign.Settings.ExperienceRewardMultiplier;
            float xpGain = xpReward * level.LevelData.Biome.ExperienceFromMissionRewards * CalculateDifficultyXPMultiplier();

            IEnumerable<Character> crewCharacters = GameSession.GetSessionCrewCharacters(CharacterType.Both);

            // use multipliers here so that we can easily add them together without introducing multiplicative XP stacking
            var experienceGainMultiplier = new AbilityMissionExperienceGainMultiplier(this, 1f, character: null);
            crewCharacters.ForEach(c => experienceGainMultiplier.Value += c.GetStatValue(StatTypes.MissionExperienceGainMultiplier));

            DistributeExperienceToCrew(crewCharacters, (int)(xpGain * experienceGainMultiplier.Value));

            CalculateFinalReward(Submarine.MainSub);
#if SERVER
            finalReward = DistributeRewardsToCrew(GameSession.GetSessionCrewCharacters(CharacterType.Player), finalReward.Value);
#endif
            bool isSingleplayerOrServer = GameMain.IsSingleplayer || GameMain.NetworkMember is { IsServer: true };
            if (isSingleplayerOrServer)
            {
                if (finalReward > 0)
                {
                    campaign.Bank.Give(finalReward.Value);
                }

                foreach (Character character in crewCharacters)
                {
                    character.Info.MissionsCompletedSinceDeath++;
                }

                foreach (var reputationReward in ReputationRewards)
                {
                    var reputationGainMultiplier = new AbilityMissionReputationGainMultiplier(this, 1f, character: null);
                    foreach (var c in crewCharacters) { c.CheckTalents(AbilityEffectType.OnCrewGainMissionReputation, reputationGainMultiplier); }
                    float amount = reputationReward.Amount * reputationGainMultiplier.Value;
                    
                    if (reputationReward.FactionIdentifier == "location")
                    {
                        OriginLocation.Reputation?.AddReputation(amount);
                        TryGiveReputationForOpposingFaction(OriginLocation.Faction, reputationReward.AmountForOpposingFaction);
                    }
                    else
                    {
                        Faction faction = campaign.Factions.Find(faction1 => faction1.Prefab.Identifier == reputationReward.FactionIdentifier);
                        if (faction != null)
                        {
                            faction.Reputation.AddReputation(amount);
                            TryGiveReputationForOpposingFaction(faction, reputationReward.AmountForOpposingFaction);
                        }
                    }
                }

                void TryGiveReputationForOpposingFaction(Faction thisFaction, float amount)
                {
                    if (MathUtils.NearlyEqual(amount, 0.0f)) { return; }
                    if (thisFaction?.Prefab != null && 
                        !thisFaction.Prefab.OpposingFaction.IsEmpty)
                    {
                        Faction faction = campaign.Factions.Find(faction1 => faction1.Prefab.Identifier == thisFaction.Prefab.OpposingFaction);
                        faction?.Reputation.AddReputation(amount);
                    }
                }
            }

            if (Prefab.DataRewards != null)
            {
                foreach (var (identifier, value, operation) in Prefab.DataRewards)
                {
                    SetDataAction.PerformOperation(campaign.CampaignMetadata, identifier, value, operation);
                }
            }
        }

        partial void DistributeExperienceToCrew(IEnumerable<Character> crew, int experienceGain);

        public static int GetRewardDistibutionSum(IEnumerable<Character> crew, int rewardDistribution = 0) => crew.Sum(c => c.Wallet.RewardDistribution) + rewardDistribution;

        public static (int Amount, int Percentage, float Sum) GetRewardShare(int rewardDistribution, IEnumerable<Character> crew, Option<int> reward)
        {
            float sum = GetRewardDistibutionSum(crew, rewardDistribution);
            if (MathUtils.NearlyEqual(sum, 0)) { return (0, 0, sum); }

            float rewardWeight = sum > 100 ? rewardDistribution / sum : rewardDistribution / 100f;
            int rewardPercentage = (int)(rewardWeight * 100);

            int amount = reward.TryUnwrap(out var a) ? a : 0;

            return ((int)(amount * rewardWeight), rewardPercentage, sum);
        }

        protected void ChangeLocationType(LocationTypeChange change)
        {
            if (change == null) { throw new ArgumentException(); }
            if (GameMain.GameSession.GameMode is CampaignMode campaign && !IsClient)
            {
                int srcIndex = -1;
                for (int i = 0; i < Locations.Length; i++)
                {
                    if (Locations[i].Type.Identifier == change.CurrentType)
                    {
                        srcIndex = i;
                        break;
                    }
                }
                if (srcIndex == -1) { return; }
                var location = Locations[srcIndex];

                if (location.LocationTypeChangesBlocked) { return; }

                if (change.RequiredDurationRange.X > 0)
                {
                    location.PendingLocationTypeChange = (change, Rand.Range(change.RequiredDurationRange.X, change.RequiredDurationRange.Y), Prefab);
                }
                else
                {
                    location.ChangeType(campaign, LocationType.Prefabs[change.ChangeToType]);
                    location.LocationTypeChangeCooldown = change.CooldownAfterChange;
                }
            }
        }

        public virtual void AdjustLevelData(LevelData levelData) { }

        // putting these here since both escort and pirate missions need them. could be tucked away into another class that they can inherit from (or use composition)
        protected HumanPrefab GetHumanPrefabFromElement(XElement element)
        {
            if (element.Attribute("name") != null)
            {
                DebugConsole.ThrowError($"Error in mission \"{Name}\" - use character identifiers instead of names to configure the characters.",
                    contentPackage: Prefab.ContentPackage);
                return null;
            }

            Identifier characterIdentifier = element.GetAttributeIdentifier("identifier", Identifier.Empty);
            Identifier characterFrom = element.GetAttributeIdentifier("from", Identifier.Empty);
            HumanPrefab humanPrefab = NPCSet.Get(characterFrom, characterIdentifier, contentPackageToLogInError: Prefab.ContentPackage);
            if (humanPrefab == null)
            {
                DebugConsole.ThrowError($"Couldn't spawn character for mission: character prefab \"{characterIdentifier}\" not found in the NPC set \"{characterFrom}\".",
                    contentPackage: Prefab.ContentPackage);
                return null;
            }

            return humanPrefab;
        }

        protected static Character CreateHuman(HumanPrefab humanPrefab, List<Character> characters, Dictionary<Character, List<Item>> characterItems, Submarine submarine, CharacterTeamType teamType, ISpatialEntity positionToStayIn = null, Rand.RandSync humanPrefabRandSync = Rand.RandSync.ServerAndClient)
        {
            var characterInfo = humanPrefab.CreateCharacterInfo(Rand.RandSync.ServerAndClient);
            characterInfo.TeamID = teamType;

            positionToStayIn ??= 
                    WayPoint.GetRandom(SpawnType.Human, characterInfo.Job?.Prefab, submarine) ??
                    WayPoint.GetRandom(SpawnType.Human, null, submarine);

            Character spawnedCharacter = Character.Create(characterInfo.SpeciesName, positionToStayIn.WorldPosition, ToolBox.RandomSeed(8), characterInfo, createNetworkEvent: false);
            spawnedCharacter.HumanPrefab = humanPrefab;
            humanPrefab.InitializeCharacter(spawnedCharacter, positionToStayIn);
            humanPrefab.GiveItems(spawnedCharacter, submarine, positionToStayIn as WayPoint, Rand.RandSync.ServerAndClient, createNetworkEvents: false);
            characters.Add(spawnedCharacter);
            characterItems.Add(spawnedCharacter, spawnedCharacter.Inventory.FindAllItems(recursive: true));

            return spawnedCharacter;
        }

        protected ItemPrefab FindItemPrefab(XElement element)
        {
            ItemPrefab itemPrefab;
            if (element.Attribute("name") != null)
            {
                DebugConsole.ThrowError($"Error in mission \"{Name}\" - use item identifiers instead of names to configure the items", 
                    contentPackage: Prefab.ContentPackage);
                string itemName = element.GetAttributeString("name", "");
                itemPrefab = MapEntityPrefab.Find(itemName) as ItemPrefab;
                if (itemPrefab == null)
                {
                    DebugConsole.ThrowError($"Couldn't spawn item for mission \"{Name}\": item prefab \"{itemName}\" not found", 
                        contentPackage: Prefab.ContentPackage);
                }
            }
            else
            {
                string itemIdentifier = element.GetAttributeString("identifier", "");
                itemPrefab = MapEntityPrefab.Find(null, itemIdentifier) as ItemPrefab;
                if (itemPrefab == null)
                {
                    DebugConsole.ThrowError($"Couldn't spawn item for mission \"{Name}\": item prefab \"{itemIdentifier}\" not found",
                        contentPackage: Prefab.ContentPackage);
                }
            }
            return itemPrefab;
        }

        protected Vector2? GetCargoSpawnPosition(ItemPrefab itemPrefab, out Submarine cargoRoomSub)
        {
            cargoRoomSub = null;

            WayPoint cargoSpawnPos = WayPoint.GetRandom(SpawnType.Cargo, null, Submarine.MainSub, useSyncedRand: true);
            if (cargoSpawnPos == null)
            {
                DebugConsole.ThrowError($"Couldn't spawn items for mission \"{Name}\": no waypoints marked as Cargo were found",
                    contentPackage: Prefab.ContentPackage);
                return null;
            }

            var cargoRoom = cargoSpawnPos.CurrentHull;
            if (cargoRoom == null)
            {
                DebugConsole.ThrowError($"Couldn't spawn items for mission \"{Name}\": waypoints marked as Cargo must be placed inside a room",
                    contentPackage: Prefab.ContentPackage);
                return null;
            }

            cargoRoomSub = cargoRoom.Submarine;

            return new Vector2(
                cargoSpawnPos.Position.X + Rand.Range(-20.0f, 20.0f, Rand.RandSync.ServerAndClient),
                cargoRoom.Rect.Y - cargoRoom.Rect.Height + itemPrefab.Size.Y / 2);
        }
    }

    class AbilityMissionMoneyGainMultiplier : AbilityObject, IAbilityValue, IAbilityMission
    {
        public AbilityMissionMoneyGainMultiplier(Mission mission, float moneyGainMultiplier)
        {
            Value = moneyGainMultiplier;
            Mission = mission;
        }
        public float Value { get; set; }
        public Mission Mission { get; set; }
    }

    class AbilityMissionExperienceGainMultiplier : AbilityObject, IAbilityValue, IAbilityMission, IAbilityCharacter
    {
        public AbilityMissionExperienceGainMultiplier(Mission mission, float missionExperienceGainMultiplier, Character character)
        {
            Value = missionExperienceGainMultiplier;
            Mission = mission;
            Character = character;
        }

        public float Value { get; set; }
        public Mission Mission { get; set; }
        public Character Character { get; set; }
    }
    
    class AbilityMissionReputationGainMultiplier : AbilityObject, IAbilityValue, IAbilityMission, IAbilityCharacter
    {
        public AbilityMissionReputationGainMultiplier(Mission mission, float reputationMultiplier, Character character)
        {
            Value = reputationMultiplier;
            Mission = mission;
            Character = character;
        }

        public float Value { get; set; }
        public Mission Mission { get; set; }
        public Character Character { get; set; }
    }

}
