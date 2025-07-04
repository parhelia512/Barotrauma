﻿using Barotrauma.Extensions;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using FarseerPhysics;
using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Xml.Linq;
using System.Linq;

namespace Barotrauma
{
    public enum AIState { Idle, Attack, Escape, Eat, Flee, Avoid, Aggressive, PassiveAggressive, Protect, Observe, Freeze, Follow, FleeTo, Patrol, PlayDead, HideTo, Hiding }

    public enum AttackPattern { Straight, Sweep, Circle }

    public enum CirclePhase { Start, CloseIn, FallBack, Advance, Strike }

    public enum WallTargetingMethod
    {
        Target = 0x1,
        Heading = 0x2,
        Steering = 0x4
    }
    
    [Flags]
    public enum EnemyTargetingRestrictions
    {
        None = 0x0,
        PlayerCharacters = 0x1,
        PlayerSubmarines = 0x2
    }

    partial class EnemyAIController : AIController
    {
        public static bool DisableEnemyAI;
        
        public static EnemyTargetingRestrictions TargetingRestrictions = EnemyTargetingRestrictions.None;
        private EnemyTargetingRestrictions previousTargetingRestrictions;

        private AIState _state;
        public AIState State
        {
            get { return _state; }
            set
            {
                if (_state == value) { return; }
                if (_state == AIState.PlayDead && value == AIState.Idle)
                {
                    // Don't allow to switch to Idle from PlayDead.
                    return;
                }
                PreviousState = _state;
                OnStateChanged(_state, value);
                _state = value;
            }
        }

        public AIState PreviousState { get; private set; }

        /// <summary>
        /// Enable the character to attack the outposts and the characters inside them. Disabled by default in normal levels, enabled in outpost levels.
        /// </summary>
        public bool TargetOutposts;

        private readonly float updateTargetsInterval = 1;
        private readonly float updateMemoriesInverval = 1;
        private readonly float attackLimbSelectionInterval = 3;
        // Min priority for the memorized targets. The actual value fades gradually, unless kept fresh by selecting the target.
        private const float minPriority = 10;

        private IndoorsSteeringManager PathSteering => insideSteering as IndoorsSteeringManager;
        private SteeringManager outsideSteering, insideSteering;

        private float updateTargetsTimer;
        private float updateMemoriesTimer;
        private float attackLimbSelectionTimer;

        private bool IsAttackRunning => AttackLimb != null && AttackLimb.attack.IsRunning;
        private bool IsCoolDownRunning => AttackLimb != null && AttackLimb.attack.CoolDownTimer > 0 || _previousAttackLimb != null && _previousAttackLimb.attack.CoolDownTimer > 0;
        public float CombatStrength => AIParams.CombatStrength;
        private float Sight => GetPerceptionRange(AIParams.Sight);
        private float Hearing => GetPerceptionRange(AIParams.Hearing);
        
        private float GetPerceptionRange(float range)
        {
            // TODO: make adjustable
            if (State is AIState.PlayDead or AIState.Hiding)
            {
                // Intentionally constant
                return 0.2f;
            }
            if (PreviousState is AIState.PlayDead or AIState.Hiding)
            {
                // Significantly buffed
                return range * 1.5f;
            }
            return range;
        }

        private float FleeHealthThreshold => AIParams.FleeHealthThreshold;
        private bool IsAggressiveBoarder => AIParams.AggressiveBoarding;

        private FishAnimController FishAnimController => Character.AnimController as FishAnimController;

        private Limb _attackLimb;
        private Limb _previousAttackLimb;
        public Limb AttackLimb
        {
            get { return _attackLimb; }
            private set
            {
                if (_attackLimb != value)
                {
                    _previousAttackLimb = _attackLimb;
                    if (_previousAttackLimb != null)
                    {
                        Character.DeselectCharacter();
                        if (_previousAttackLimb.attack.SnapRopeOnNewAttack)
                        {
                            _previousAttackLimb.AttachedRope?.Snap();
                        }
                    }
                }
                else if (_attackLimb != null && _attackLimb.attack.CoolDownTimer <= 0)
                {
                    Character.DeselectCharacter();
                    if (_attackLimb.attack.SnapRopeOnNewAttack)
                    {
                        _attackLimb.AttachedRope?.Snap();
                    }
                }
                _attackLimb = value;
                attackVector = null;
                Reverse = _attackLimb != null && _attackLimb.attack.Reverse;
            }
        }

        private double lastAttackUpdateTime;

        private Attack _activeAttack;
        public Attack ActiveAttack
        {
            get
            {
                if (_activeAttack == null) { return null; }
                return lastAttackUpdateTime > Timing.TotalTime - _activeAttack.Duration ? _activeAttack : null;
            }
            private set  
            { 
                _activeAttack = value; 
                lastAttackUpdateTime = Timing.TotalTime; 
            }
        }

        public AITargetMemory CurrentTargetMemory => currentTargetMemory;
        private AITargetMemory currentTargetMemory;
        private float targetValue;
        private CharacterParams.TargetParams currentTargetingParams;
                
        private Dictionary<AITarget, AITargetMemory> targetMemories;
        
        private readonly int requiredHoleCount;
        private bool canAttackWalls;
        public bool CanAttackDoors => canAttackDoors;
        private bool canAttackDoors;
        private bool canAttackItems;
        private bool canAttackCharacters;

        public float PriorityFearIncrement => priorityFearIncreasement;
        private readonly float priorityFearIncreasement = 2;
        private readonly float memoryFadeTime = 0.5f;

        private float avoidTimer;
        private float observeTimer;
        private float sweepTimer;
        private float circleRotation;
        private float circleDir;
        private bool inverseDir;
        private bool breakCircling;
        private float circleRotationSpeed;
        private Vector2 circleOffset;
        private float circleFallbackDistance;
        private float strikeTimer;
        private float aggressionIntensity;
        private CirclePhase CirclePhase;
        private float currentAttackIntensity;
        
        private float playDeadTimer;
        /// <summary>
        /// How long the character has to idle without a target before it can start playing dead (again).
        /// </summary>
        private const float PlayDeadCoolDown = 60;

        private CoroutineHandle disableTailCoroutine;

        private readonly List<Body> myBodies;

        public LatchOntoAI LatchOntoAI { get; private set; }
        public SwarmBehavior SwarmBehavior { get; private set; }
        public PetBehavior PetBehavior { get; private set; }

        public CharacterParams.TargetParams CurrentTargetingParams => currentTargetingParams;

        public bool AttackHumans
        {
            get
            {
                return GetTargetParams(Tags.Human).Any(static tp => tp is { Priority: > 0.0f, State: AIState.Attack or AIState.Aggressive });
            }
        }

        public bool AttackRooms
        {
            get
            {
                return GetTargetParams(Tags.Room).Any(static tp => tp is { Priority: > 0.0f, State: AIState.Attack or AIState.Aggressive });
            }
        }

        public override CanEnterSubmarine CanEnterSubmarine
        {
            get
            {
                //can't enter a submarine when attached to one
                if (LatchOntoAI is { IsAttachedToSub: true }) { return CanEnterSubmarine.False; }
                return Character.AnimController.CanEnterSubmarine;
            }
        }

        public override bool CanFlip
        {
            get
            {
                //can't flip when attached to something, when eating, or reversing or in a (relatively) small room
                return !Reverse &&
                    (State != AIState.Eat || Character.SelectedCharacter == null) &&
                    (LatchOntoAI == null || !LatchOntoAI.IsAttachedToSub) && 
                    (Character.CurrentHull == null || !Character.AnimController.InWater || Math.Min(Character.CurrentHull.Size.X, Character.CurrentHull.Size.Y) > ConvertUnits.ToDisplayUnits(Math.Max(colliderLength, colliderWidth)));
            }
        }

        /// <summary>
        /// The monster won't try to damage these submarines
        /// </summary>
        public HashSet<Submarine> UnattackableSubmarines
        {
            get;
            private set;
        } = new HashSet<Submarine>();

        public static bool IsTargetBeingChasedBy(Character target, Character character)
            => character?.AIController is EnemyAIController enemyAI && enemyAI.SelectedAiTarget?.Entity == target && enemyAI.State is AIState.Attack or AIState.Aggressive;
        public bool IsBeingChasedBy(Character c) => IsTargetBeingChasedBy(Character, c);
        private bool IsBeingChased => IsBeingChasedBy(SelectedAiTarget?.Entity as Character);

        private static bool IsTargetInPlayerTeam(AITarget target) => target?.Entity?.Submarine != null && target.Entity.Submarine.Info.IsPlayer || target?.Entity is Character { IsOnPlayerTeam: true };

        private bool IsAttackingOwner(Character other) =>
            PetBehavior != null && PetBehavior.Owner != null &&
            !other.IsUnconscious && !other.IsHandcuffed &&
            other.AIController is HumanAIController humanAI &&
            humanAI.ObjectiveManager.CurrentObjective is AIObjectiveCombat combat &&
            combat.Enemy != null && combat.Enemy == PetBehavior.Owner;

        private bool reverse;
        public bool Reverse 
        { 
            get { return reverse; }
            private set
            {
                reverse = value;
                if (FishAnimController != null)
                {
                    FishAnimController.Reverse = reverse;
                }
            }
        }

        private readonly float maxSteeringBuffer = 5000;
        private readonly float minSteeringBuffer = 500;
        private readonly float steeringBufferIncreaseSpeed = 100;
        private float steeringBuffer;

        public EnemyAIController(Character c, string seed) : base(c)
        {
            if (c.IsHuman)
            {
                throw new Exception($"Tried to create an enemy ai controller for human!");
            }
            var mainElement = c.Params.OriginalElement.IsOverride() ? c.Params.OriginalElement.FirstElement() : c.Params.OriginalElement;
            targetMemories = new Dictionary<AITarget, AITargetMemory>();
            steeringManager = outsideSteering;
            //allow targeting outposts and outpost NPCs in outpost levels
            TargetOutposts = 
                (Level.Loaded != null && Level.Loaded.Type == LevelData.LevelType.Outpost) ||
                //the main sub can be an outpost in the editor
                Submarine.MainSub is { Info.Type: SubmarineType.Outpost };

            List<XElement> aiElements = new List<XElement>();
            List<float> aiCommonness = new List<float>();
            foreach (var element in mainElement.Elements())
            {
                if (!element.Name.ToString().Equals("ai", StringComparison.OrdinalIgnoreCase)) { continue; }                
                aiElements.Add(element);
                aiCommonness.Add(element.GetAttributeFloat("commonness", 1.0f));                
            }
            
            if (aiElements.Count == 0)
            {
                DebugConsole.ThrowError("Error in file \"" + c.Params.File.Path + "\" - no AI element found.",
                    contentPackage: c.Prefab?.ContentPackage);
                outsideSteering = new SteeringManager(this);
                insideSteering = new IndoorsSteeringManager(this, false, false);
                return;
            }
            
            //choose a random ai element
            MTRandom random = new MTRandom(ToolBox.StringToInt(seed));
            XElement aiElement = aiElements.Count == 1 ? aiElements[0] : ToolBox.SelectWeightedRandom(aiElements, aiCommonness, random);
            foreach (var subElement in aiElement.Elements())
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "chooserandom":
                        var subElements = subElement.Elements();
                        if (subElements.Any())
                        {
                            LoadSubElement(subElements.ToArray().GetRandom(random));
                        }
                        break;
                    default:
                        LoadSubElement(subElement);
                        break;
                }
            }

            void LoadSubElement(XElement subElement)
            {
                switch (subElement.Name.ToString().ToLowerInvariant())
                {
                    case "latchonto":
                        LatchOntoAI = new LatchOntoAI(subElement, this);
                        break;
                    case "swarm":
                    case "swarmbehavior":
                        SwarmBehavior = new SwarmBehavior(subElement, this);
                        break;
                    case "petbehavior":
                        PetBehavior = new PetBehavior(subElement, this);
                        break;
                }
            }
            //pets are friendly!
            if (PetBehavior != null || Character.Group == "human")
            {
                Character.TeamID = CharacterTeamType.FriendlyNPC;
            }
            ReevaluateAttacks();
            outsideSteering = new SteeringManager(this);
            insideSteering = new IndoorsSteeringManager(this, AIParams.CanOpenDoors, canAttackDoors);
            steeringManager = outsideSteering;
            State = AIState.Idle;
            requiredHoleCount = (int)Math.Ceiling(ConvertUnits.ToDisplayUnits(colliderWidth) / Structure.WallSectionSize);
            myBodies = Character.AnimController.Limbs.Select(l => l.body.FarseerBody).ToList();
            myBodies.Add(Character.AnimController.Collider.FarseerBody);
            if (AIParams.PlayDeadProbability > 0)
            {
                Character.EvaluatePlayDeadProbability();
            }
            CreatureMetrics.UnlockInEditor(Character.SpeciesName);
        }

        private CharacterParams.AIParams _aiParams;
        /// <summary>
        /// Shorthand for <see cref="Character.Params.AI"/> with null checking.
        /// </summary>
        /// <returns><see cref="Character.Params.AI"/> or an empty params. Does not return nulls.</returns>
        public CharacterParams.AIParams AIParams
        {
            get
            {
                if (_aiParams == null)
                {
                    _aiParams = Character.Params.AI;
                    if (_aiParams == null)
                    {
                        DebugConsole.ThrowError($"No AI Params defined for {Character.SpeciesName}. AI disabled.",
                            contentPackage: Character.Prefab.ContentPackage);
                        Enabled = false;
                        _aiParams = new CharacterParams.AIParams(null, Character.Params);
                    }
                }
                return _aiParams;
            }
        }
        private IEnumerable<CharacterParams.TargetParams> GetTargetParams(Identifier targetTag) => AIParams.GetTargets(targetTag);
        
        private IEnumerable<CharacterParams.TargetParams> GetTargetParams(IEnumerable<Identifier> targetingTags)
        {
            foreach (Identifier tag in targetingTags)
            {
                foreach (var tp in GetTargetParams(tag))
                {
                    yield return tp;
                }
            }
        }
        
        private readonly List<Identifier> _targetingTags = new List<Identifier>();

        private IEnumerable<Identifier> GetTargetingTags(AITarget aiTarget)
        {
            _targetingTags.Clear();
            if (aiTarget?.Entity == null) { return _targetingTags; }
            if (aiTarget.Entity is Character targetCharacter)
            {
                if (targetCharacter.IsDead)
                {
                    _targetingTags.Add(Tags.Dead);
                }
                else if (AIParams.TryGetHighestPriorityTarget(targetCharacter.CharacterHealth.GetActiveAfflictionTags(), out CharacterParams.TargetParams tp) && tp.Threshold >= Character.GetDamageDoneByAttacker(targetCharacter))
                {
                    _targetingTags.Add(tp.Tag);
                }
                else if (PetBehavior != null && aiTarget.Entity == PetBehavior.Owner) 
                { 
                    _targetingTags.Add(Tags.Owner);
                }
                else if (PetBehavior != null && (!Character.IsOnFriendlyTeam(targetCharacter) || IsAttackingOwner(targetCharacter)))
                {
                    _targetingTags.Add(Tags.Hostile);
                }
                else if (AIParams.TryGetHighestPriorityTarget(targetCharacter, out CharacterParams.TargetParams tP))
                {
                    _targetingTags.Add(tP.Tag);
                }
                else if (targetCharacter.AIController is EnemyAIController enemy)
                {
                    if (enemy.PetBehavior != null && (PetBehavior != null || AIParams.HasTag(Tags.Pet)))
                    {
                        // Pets see other pets as pets by default.
                        // Monsters see them only as pet only when they have a matching ai target. Otherwise they use the other tags, specified below.
                        _targetingTags.Add(Tags.Pet);
                    }
                    else if (targetCharacter.IsHusk && AIParams.HasTag(Tags.Husk))
                    {
                        _targetingTags.Add(Tags.Husk);
                    }
                    else if (!Character.IsSameSpeciesOrGroup(targetCharacter))
                    {
                        if (enemy.CombatStrength > CombatStrength)
                        {
                            _targetingTags.Add(Tags.Stronger);
                        }
                        else if (enemy.CombatStrength < CombatStrength)
                        {
                            _targetingTags.Add(Tags.Weaker);
                        }
                        else
                        {
                            _targetingTags.Add(Tags.Equal);
                        }
                    }
                }
            }
            else if (aiTarget.Entity is Item targetItem)
            {
                foreach (var prio in AIParams.Targets)
                {
                    if (targetItem.HasTag(prio.Tag))
                    {
                        _targetingTags.Add(prio.Tag);
                    }
                }
                if (_targetingTags.None())
                {
                    if (targetItem.GetComponent<Sonar>() != null)
                    {
                        _targetingTags.Add(Tags.Sonar);
                    }
                    if (targetItem.GetComponent<Door>() != null)
                    {
                        _targetingTags.Add(Tags.Door);
                    }
                }
            }
            else if (aiTarget.Entity is Structure)
            {
                _targetingTags.Add(Tags.Wall);
            }
            else if (aiTarget.Entity is Hull)
            {
                _targetingTags.Add(Tags.Room);
            }
            return _targetingTags;
        }

        public override void SelectTarget(AITarget target) => SelectTarget(target, 100);

        public void SelectTarget(AITarget target, float priority)
        {
            SelectedAiTarget = target;
            currentTargetMemory = GetTargetMemory(target, addIfNotFound: true);
            currentTargetMemory.Priority = priority;
            ignoredTargets.Remove(target);
        }

        private float movementMargin;

        private void ReleaseDragTargets()
        {
            AttackLimb?.AttachedRope?.Snap();
            if (Character.Params.CanInteract && Character.Inventory != null)
            {
                Character.HeldItems.ForEach(i => i.GetComponent<Holdable>()?.GetRope()?.Snap());
            }
        }
        
        public void EvaluatePlayDeadProbability(float? probability = null)
        {
            if (probability.HasValue)
            {
                AIParams.PlayDeadProbability = probability.Value;
            } 
            Character.AllowPlayDead = Rand.Value() <= AIParams.PlayDeadProbability;
        }

        public override void Update(float deltaTime)
        {
            if (DisableEnemyAI) { return; }
            base.Update(deltaTime);
            UpdateTriggers(deltaTime);
            Character.ClearInputs();
            IsTryingToSteerThroughGap = false;
            Reverse = false;

            //doesn't do anything usually, but events may sometimes change monsters' (or pets' that use enemy AI) teams
            Character.UpdateTeam();

            bool ignorePlatforms = Character.AnimController.TargetMovement.Y < -0.5f && (-Character.AnimController.TargetMovement.Y > Math.Abs(Character.AnimController.TargetMovement.X));
            if (steeringManager == insideSteering)
            {
                var currPath = PathSteering.CurrentPath;
                if (currPath != null && currPath.CurrentNode != null)
                {
                    if (currPath.CurrentNode.SimPosition.Y < Character.AnimController.GetColliderBottom().Y)
                    {
                        // Don't allow to jump from too high.
                        float allowedJumpHeight = Character.AnimController.ImpactTolerance / 2;
                        float height = Math.Abs(currPath.CurrentNode.SimPosition.Y - Character.SimPosition.Y);
                        ignorePlatforms = height < allowedJumpHeight;
                    }
                }
                if (Character.IsClimbing && PathSteering.IsNextLadderSameAsCurrent)
                {
                    Character.AnimController.TargetMovement = new Vector2(0.0f, Math.Sign(Character.AnimController.TargetMovement.Y));
                }
            }
            Character.AnimController.IgnorePlatforms = ignorePlatforms;

            if (Math.Abs(Character.AnimController.movement.X) > 0.1f && !Character.AnimController.InWater &&
                (GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer || Character.Controlled == Character))
            {
                if (SelectedAiTarget?.Entity != null || EscapeTarget != null)
                {
                    Entity t = SelectedAiTarget?.Entity ?? EscapeTarget;
                    float referencePos = Vector2.DistanceSquared(Character.WorldPosition, t.WorldPosition) > 100 * 100 && HasValidPath() ? PathSteering.CurrentPath.CurrentNode.WorldPosition.X : t.WorldPosition.X;
                    Character.AnimController.TargetDir = Character.WorldPosition.X < referencePos ? Direction.Right : Direction.Left;
                }
                else
                {
                    Character.AnimController.TargetDir = Character.AnimController.movement.X > 0.0f ? Direction.Right : Direction.Left;
                }
            }
            if (isStateChanged)
            {
                if (State == AIState.Idle || State == AIState.Patrol)
                {
                    stateResetTimer -= deltaTime;
                    if (stateResetTimer <= 0)
                    {
                        ResetOriginalState();
                    }
                }
            }
            if (targetIgnoreTimer > 0)
            {
                targetIgnoreTimer -= deltaTime;
            }
            else
            {
                ignoredTargets.Clear();
                targetIgnoreTimer = targetIgnoreTime;
            }
            avoidTimer -= deltaTime;
            if (avoidTimer < 0)
            {
                avoidTimer = 0;
            }
            UpdateCurrentMemoryLocation();
            if (updateMemoriesTimer > 0)
            {
                updateMemoriesTimer -= deltaTime;
            }
            else
            {
                FadeMemories(updateMemoriesInverval);
                updateMemoriesTimer = updateMemoriesInverval;
            }
            if (Math.Max(Character.HealthPercentage, 0) < FleeHealthThreshold && SelectedAiTarget != null)
            {
                Character target = SelectedAiTarget.Entity as Character;
                if (target == null && SelectedAiTarget.Entity is Item targetItem)
                {
                    target = GetOwner(targetItem);
                }
                bool shouldFlee = false;
                if (target != null)
                {
                    // Keep fleeing if being chased or if we see a human target (that don't have enemy ai).
                    shouldFlee = target.IsHuman && CanPerceive(SelectedAiTarget) || IsBeingChasedBy(target);
                }
                // If we should not flee, just idle. Don't allow any other AI state when below the health threshold.
                State = shouldFlee ? AIState.Flee : AIState.Idle;
                wallTarget = null;
                if (State != AIState.Flee)
                {
                    SelectedAiTarget = null;
                    _lastAiTarget = null;
                }
            }
            else
            {
                if (TargetingRestrictions != previousTargetingRestrictions)
                {
                    previousTargetingRestrictions = TargetingRestrictions;
                    // update targeting instantly when there's a change in targeting restrictions
                    updateTargetsTimer = 0;
                    SelectedAiTarget = null;
                }
                
                if (updateTargetsTimer > 0)
                {
                    updateTargetsTimer -= deltaTime;
                }
                else if (avoidTimer <= 0 || activeTriggers.Any() && returnTimer <= 0)
                {
                    UpdateTargets();
                }
            }

            if (Character.Params.UsePathFinding && AIParams.UsePathFindingToGetInside && AIParams.CanOpenDoors)
            {
                // Meant for monsters outside the player sub that target something inside the sub and can use the doors to access the sub (Husk).
                bool IsCloseEnoughToTargetSub(float threshold) => SelectedAiTarget?.Entity?.Submarine is Submarine sub && sub != null && Vector2.DistanceSquared(Character.WorldPosition, sub.WorldPosition) < MathUtils.Pow(Math.Max(sub.Borders.Size.X, sub.Borders.Size.Y) / 2 + threshold, 2);

                if (Character.Submarine != null || HasValidPath() && IsCloseEnoughToTargetSub(maxSteeringBuffer) || IsCloseEnoughToTargetSub(steeringBuffer))
                {
                    if (steeringManager != insideSteering)
                    {
                        insideSteering.Reset();
                    }
                    steeringManager = insideSteering;
                    steeringBuffer += steeringBufferIncreaseSpeed * deltaTime;
                }
                else
                {
                    if (steeringManager != outsideSteering)
                    {
                        outsideSteering.Reset();
                    }
                    steeringManager = outsideSteering;
                    steeringBuffer = minSteeringBuffer;
                }
                steeringBuffer = Math.Clamp(steeringBuffer, minSteeringBuffer, maxSteeringBuffer);
            }
            else
            {
                // Normally the monsters only use pathing inside submarines, not outside.
                if (Character.Submarine != null && Character.Params.UsePathFinding)
                {
                    if (steeringManager != insideSteering)
                    {
                        insideSteering.Reset();
                    }
                    steeringManager = insideSteering;
                }
                else
                {
                    if (steeringManager != outsideSteering)
                    {
                        outsideSteering.Reset();
                    }
                    steeringManager = outsideSteering;
                }
            }
            
            bool useSteeringLengthAsMovementSpeed = State == AIState.Idle && Character.AnimController.InWater;
            bool run = false;
            switch (State)
            {
                case AIState.Freeze:
                    SteeringManager.Reset();
                    break;
                case AIState.Idle:
                    UpdateIdle(deltaTime);
                    break;
                case AIState.PlayDead:
                    Character.IsRagdolled = true;
                    break;
                case AIState.Patrol:
                    UpdatePatrol(deltaTime);
                    break;
                case AIState.Attack:
                    run = !IsCoolDownRunning || AttackLimb != null && AttackLimb.attack.FullSpeedAfterAttack;
                    UpdateAttack(deltaTime);
                    break;
                case AIState.Eat:
                    UpdateEating(deltaTime);
                    break;
                case AIState.Escape:
                case AIState.Flee:
                    run = true;
                    Escape(deltaTime);
                    break;
                case AIState.Avoid:
                case AIState.PassiveAggressive:
                case AIState.Aggressive:
                    if (SelectedAiTarget?.Entity == null || SelectedAiTarget.Entity.Removed)
                    {
                        State = AIState.Idle;
                        return;
                    }
                    float squaredDistance = Vector2.DistanceSquared(WorldPosition, SelectedAiTarget.WorldPosition);
                    var attackLimb = AttackLimb ?? GetAttackLimb(SelectedAiTarget.WorldPosition);
                    if (attackLimb != null && squaredDistance <= Math.Pow(attackLimb.attack.Range, 2))
                    {
                        run = true;
                        if (State == AIState.Avoid)
                        {
                            Escape(deltaTime);
                        }
                        else
                        {
                            UpdateAttack(deltaTime);
                        }
                    }
                    else
                    {
                        bool isBeingChased = IsBeingChased;
                        float reactDistance = !isBeingChased && currentTargetingParams is { ReactDistance: > 0 } ? currentTargetingParams.ReactDistance : GetPerceivingRange(SelectedAiTarget);
                        if (squaredDistance <= Math.Pow(reactDistance, 2))
                        {
                            float halfReactDistance = reactDistance / 2;
                            float attackDistance = currentTargetingParams is { AttackDistance: > 0 } ? currentTargetingParams.AttackDistance : halfReactDistance;
                            if (State == AIState.Aggressive || State == AIState.PassiveAggressive && squaredDistance < Math.Pow(attackDistance, 2))
                            {
                                run = true;
                                UpdateAttack(deltaTime);
                            }
                            else
                            {
                                run = isBeingChased || squaredDistance < Math.Pow(halfReactDistance, 2);
                                State = AIState.Escape;
                                avoidTimer = AIParams.AvoidTime * 0.5f * Rand.Range(0.75f, 1.25f);
                            }
                        }
                        else
                        {
                            UpdateIdle(deltaTime);
                        }
                    }
                    break;
                case AIState.Protect:
                case AIState.Follow:
                case AIState.FleeTo:
                case AIState.HideTo:
                case AIState.Hiding:
                    if (SelectedAiTarget?.Entity == null || SelectedAiTarget.Entity.Removed)
                    {
                        State = AIState.Idle;
                        return;
                    }
                    if (State == AIState.Protect)
                    {
                        if (SelectedAiTarget.Entity is Character targetCharacter)
                        {
                            bool ShouldRetaliate(Character.Attacker a)
                            {
                                Character c = a.Character;
                                if (c == null || c.IsUnconscious || c.Removed) { return false; }
                                // Can't target characters of same species/group because that would make us hostile to all friendly characters in the same species/group.
                                if (Character.IsSameSpeciesOrGroup(c)) { return false; }
                                if (targetCharacter.IsSameSpeciesOrGroup(c)) { return false; }
                                //don't try to attack targets in a sub that belongs to a different team
                                //(for example, targets in an outpost if we're in the main sub)
                                if (c.Submarine?.TeamID != Character.Submarine?.TeamID) { return false; }
                                if (c.IsPlayer || Character.IsOnFriendlyTeam(c))
                                {
                                    return a.Damage >= currentTargetingParams.Threshold;
                                }
                                return true;
                            }
                            Character attacker = targetCharacter.LastAttackers.LastOrDefault(ShouldRetaliate)?.Character;
                            if (attacker?.AiTarget != null)
                            {
                                ChangeTargetState(attacker, AIState.Attack, currentTargetingParams.Priority * 2);
                                SelectTarget(attacker.AiTarget);
                                State = AIState.Attack;
                                UpdateWallTarget(requiredHoleCount);
                                return;
                            }
                        }
                    }
                    float distX = Math.Abs(WorldPosition.X - SelectedAiTarget.WorldPosition.X);
                    float distY = Math.Abs(WorldPosition.Y - SelectedAiTarget.WorldPosition.Y);
                    if (Character.Submarine != null && distY > 50 && SelectedAiTarget.Entity is Character targetC && !VisibleHulls.Contains(targetC.CurrentHull))
                    {
                        // Target not visible, and possibly on a different floor.
                        distY *= 3;
                    }
                    float dist = distX + distY;
                    float reactDist = GetPerceivingRange(SelectedAiTarget);
                    Vector2 offset = Vector2.Zero;
                    if (currentTargetingParams != null)
                    {
                        if (currentTargetingParams.ReactDistance > 0)
                        {
                            reactDist = currentTargetingParams.ReactDistance;
                        }
                        offset = currentTargetingParams.Offset;
                    }
                    if (offset != Vector2.Zero)
                    {
                        reactDist += offset.Length();
                    }
                    if (dist > reactDist + movementMargin)
                    {
                        movementMargin = State is AIState.FleeTo or AIState.HideTo or AIState.Hiding ? 0 : reactDist;
                        if (State == AIState.Hiding)
                        {
                            // Too far to hide.
                            State = AIState.HideTo;
                        }
                        run = true;
                        UpdateFollow(deltaTime);
                    }
                    else
                    {
                        if (State == AIState.HideTo)
                        {
                            // Close enough to hide.
                            State = AIState.Hiding;
                        }
                        movementMargin = MathHelper.Clamp(movementMargin -= deltaTime, 0, reactDist);
                        if (State is AIState.FleeTo or AIState.Hiding)
                        {
                            SteeringManager.Reset();
                            Character.AnimController.TargetMovement = Vector2.Zero;
                            if (Character.AnimController.InWater)
                            {
                                float force = Character.AnimController.Collider.Mass / 10;
                                Character.AnimController.Collider.MoveToPos(SelectedAiTarget.Entity.SimPosition + ConvertUnits.ToSimUnits(offset), force);
                                if (SelectedAiTarget.Entity is Item item)
                                {
                                    float rotation = item.Rotation;
                                    Character.AnimController.Collider.SmoothRotate(rotation, Character.AnimController.SwimFastParams.SteerTorque);
                                    var mainLimb = Character.AnimController.MainLimb;
                                    if (mainLimb.type == LimbType.Head)
                                    {
                                        mainLimb.body.SmoothRotate(rotation, Character.AnimController.SwimFastParams.HeadTorque);
                                    }
                                    else
                                    {
                                        mainLimb.body.SmoothRotate(rotation, Character.AnimController.SwimFastParams.TorsoTorque);
                                    }
                                }
                                if (disableTailCoroutine == null && SelectedAiTarget.Entity is Item i && i.HasTag(Tags.GuardianShelter))
                                {
                                    if (!CoroutineManager.IsCoroutineRunning(disableTailCoroutine))
                                    {
                                        disableTailCoroutine = CoroutineManager.Invoke(() => 
                                        {
                                            if (Character is { Removed: false })
                                            {
                                                Character.AnimController.HideAndDisable(LimbType.Tail, ignoreCollisions: false);
                                            }
                                        }, 1f);
                                    }    
                                }
                                Character.AnimController.ApplyPose(
                                    new Vector2(0, -1),
                                    new Vector2(0, -1),
                                    new Vector2(0, -1),
                                    new Vector2(0, -1), footMoveForce: 1);
                            }
                        }
                        else
                        {
                            UpdateIdle(deltaTime);
                        }
                    }
                    break;
                case AIState.Observe:
                    if (SelectedAiTarget?.Entity == null || SelectedAiTarget.Entity.Removed)
                    {
                        State = AIState.Idle;
                        return;
                    }
                    run = false;
                    float sqrDist = Vector2.DistanceSquared(WorldPosition, SelectedAiTarget.WorldPosition);
                    reactDist = currentTargetingParams is { ReactDistance: > 0 } ? currentTargetingParams.ReactDistance : GetPerceivingRange(SelectedAiTarget);
                    float halfReactDist = reactDist / 2;
                    float attackDist = currentTargetingParams is { AttackDistance: > 0 } ? currentTargetingParams.AttackDistance : halfReactDist;
                    if (sqrDist > Math.Pow(reactDist, 2))
                    {
                        // Too far to react
                        UpdateIdle(deltaTime);
                    }
                    else if (sqrDist < Math.Pow(attackDist + movementMargin, 2))
                    {
                        movementMargin = attackDist;
                        SteeringManager.Reset();
                        if (Character.AnimController.InWater)
                        {
                            useSteeringLengthAsMovementSpeed = true;
                            Vector2 dir = Vector2.Normalize(SelectedAiTarget.WorldPosition - Character.WorldPosition);
                            if (sqrDist < Math.Pow(attackDist * 0.75f, 2))
                            {
                                // Keep the distance, if too close
                                dir = -dir;
                                useSteeringLengthAsMovementSpeed = false;
                                Reverse = true;
                                run = true;
                            }
                            SteeringManager.SteeringManual(deltaTime, dir * 0.2f);
                        }
                        else
                        {
                            // TODO: doesn't work right here
                            FaceTarget(SelectedAiTarget.Entity);
                        }
                        observeTimer -= deltaTime;
                        if (observeTimer < 0)
                        {
                            IgnoreTarget(SelectedAiTarget);
                            State = AIState.Idle;
                            ResetAITarget();
                        }
                    }
                    else
                    {
                        run = sqrDist > Math.Pow(attackDist * 2, 2);
                        movementMargin = MathHelper.Clamp(movementMargin -= deltaTime, 0, attackDist);
                        UpdateFollow(deltaTime);
                    }
                    break;
                default:
                    throw new NotImplementedException();
            }

            if (!Character.AnimController.SimplePhysicsEnabled)
            {
                LatchOntoAI?.Update(this, deltaTime);
            }
            IsSteeringThroughGap = false;
            if (SwarmBehavior != null)
            {
                SwarmBehavior.IsActive = SwarmBehavior.ForceActive || State == AIState.Idle && Character.CurrentHull == null;
                SwarmBehavior.Refresh();
                SwarmBehavior.UpdateSteering(deltaTime);
            }
            // Ensure that the creature keeps inside the level
            SteerInsideLevel(deltaTime);
            float speed = Character.AnimController.GetCurrentSpeed(run && Character.CanRun);
            // Doesn't work if less than 1, when we use steering length as movement speed.
            steeringManager.Update(Math.Max(speed, 1.0f));
            float movementSpeed = useSteeringLengthAsMovementSpeed ? Steering.Length() : speed;
            Character.AnimController.TargetMovement = Character.ApplyMovementLimits(Steering, movementSpeed);
            if (Character.CurrentHull != null && Character.AnimController.InWater)
            {
                // Limit the swimming speed inside the sub.
                Character.AnimController.TargetMovement = Character.AnimController.TargetMovement.ClampLength(5);
            }
        }

        #region Idle

        private void UpdateIdle(float deltaTime, bool followLastTarget = true)
        {
            if (Character.AllowPlayDead && Character.Submarine != null)
            {
                if (playDeadTimer > 0)
                {
                    playDeadTimer -= deltaTime;
                }
                else
                {
                    State = AIState.PlayDead;
                }
            }
            else if (AIParams.PatrolFlooded || AIParams.PatrolDry)
            {
                State = AIState.Patrol;
            }
            var pathSteering = SteeringManager as IndoorsSteeringManager;
            if (pathSteering == null)
            {
                if (Level.Loaded != null && Level.Loaded.GetRealWorldDepth(WorldPosition.Y) > Character.CharacterHealth.CrushDepth * 0.75f)
                {
                    // Steer straight up if very deep
                    SteeringManager.SteeringManual(deltaTime, Vector2.UnitY);
                    SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 5);
                    return;
                }
            }
            if (followLastTarget)
            {
                var target = SelectedAiTarget ?? _lastAiTarget;
                if (target?.Entity is { Removed: false } && 
                    PreviousState == AIState.Attack && Character.CurrentHull == null && 
                    (_previousAttackLimb?.attack == null || 
                     _previousAttackLimb?.attack is Attack previousAttack && (previousAttack.AfterAttack != AIBehaviorAfterAttack.FallBack || previousAttack.CoolDownTimer <= 0)))
                {
                    // Keep heading to the last known position of the target
                    var memory = GetTargetMemory(target);
                    if (memory != null)
                    {
                        var location = memory.Location;
                        float dist = Vector2.DistanceSquared(WorldPosition, location);
                        if (dist < 50 * 50 || !IsPositionInsideAllowedZone(WorldPosition, out _))
                        {
                            // Target is gone
                            ResetAITarget();
                        }
                        else
                        {
                            // Steer towards the target
                            SteeringManager.SteeringSeek(Character.GetRelativeSimPosition(target.Entity, location), 5);
                            SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 15);
                            return;
                        }
                    }
                    else
                    {
                        ResetAITarget();
                    }
                }
            }
            if (!Character.IsClimbing)
            {
                if (pathSteering != null && !Character.AnimController.InWater)
                {
                    // Wander around inside
                    pathSteering.Wander(deltaTime, Math.Max(ConvertUnits.ToDisplayUnits(colliderLength), 100.0f), stayStillInTightSpace: false);
                }
                else
                {
                    // Wander around outside or swimming
                    steeringManager.SteeringWander(avoidWanderingOutsideLevel: true);
                    if (Character.AnimController.InWater)
                    {
                        SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 5);
                    }
                }   
            }
        }

        private readonly List<Hull> targetHulls = new List<Hull>();
        private readonly List<float> hullWeights = new List<float>();

        private Hull patrolTarget;
        private float newPatrolTargetTimer;
        private float patrolTimerMargin;
        private readonly float newPatrolTargetIntervalMin = 5;
        private readonly float newPatrolTargetIntervalMax = 30;
        private bool searchingNewHull;

        private void UpdatePatrol(float deltaTime, bool followLastTarget = true)
        {
            if (SteeringManager is IndoorsSteeringManager pathSteering)
            {
                if (patrolTarget == null || IsCurrentPathUnreachable || IsCurrentPathFinished)
                {
                    newPatrolTargetTimer = Math.Min(newPatrolTargetTimer, newPatrolTargetIntervalMin);
                }
                if (newPatrolTargetTimer > 0)
                {
                    newPatrolTargetTimer -= deltaTime;
                }
                else
                {
                    if (!searchingNewHull)
                    {
                        searchingNewHull = true;
                        FindTargetHulls();
                    }
                    else if (targetHulls.Any())
                    {
                        patrolTarget = ToolBox.SelectWeightedRandom(targetHulls, hullWeights, Rand.RandSync.Unsynced);
                        var path = PathSteering.PathFinder.FindPath(Character.SimPosition, patrolTarget.SimPosition, Character.Submarine, minGapSize: minGapSize * 1.5f, nodeFilter: n => PatrolNodeFilter(n));
                        if (path.Unreachable)
                        {
                            //can't go to this room, remove it from the list and try another room
                            int index = targetHulls.IndexOf(patrolTarget);
                            targetHulls.RemoveAt(index);
                            hullWeights.RemoveAt(index);
                            PathSteering.Reset();
                            patrolTarget = null;
                            patrolTimerMargin += 0.5f;
                            patrolTimerMargin = Math.Min(patrolTimerMargin, newPatrolTargetIntervalMin);
                            newPatrolTargetTimer = Math.Min(newPatrolTargetIntervalMin, patrolTimerMargin);
                        }
                        else
                        {
                            PathSteering.SetPath(patrolTarget.SimPosition, path);
                            patrolTimerMargin = 0;
                            newPatrolTargetTimer = newPatrolTargetIntervalMax * Rand.Range(0.5f, 1.5f);
                            searchingNewHull = false;
                        }
                    }
                    else
                    {
                        // Couldn't find a valid hull
                        newPatrolTargetTimer = newPatrolTargetIntervalMax;
                        searchingNewHull = false;
                    }
                }
                if (patrolTarget != null && pathSteering.CurrentPath is { Finished: false, Unreachable: false })
                {
                    pathSteering.SteeringSeek(Character.GetRelativeSimPosition(patrolTarget), weight: 1, minGapWidth: minGapSize * 1.5f, nodeFilter: PatrolNodeFilter);
                    return;
                }
            }

            bool PatrolNodeFilter(PathNode n) =>
                AIParams.PatrolFlooded && (Character.CurrentHull == null || n.Waypoint.CurrentHull == null || n.Waypoint.CurrentHull.WaterPercentage >= 80) ||
                AIParams.PatrolDry && Character.CurrentHull != null && n.Waypoint.CurrentHull != null && n.Waypoint.CurrentHull.WaterPercentage <= 50;

            UpdateIdle(deltaTime, followLastTarget);
        }

        private void FindTargetHulls()
        {
            if (Character.Submarine == null) { return; }
            if (Character.CurrentHull == null) { return; }
            targetHulls.Clear();
            hullWeights.Clear();
            float hullMinSize = ConvertUnits.ToDisplayUnits(Math.Max(colliderLength, colliderWidth) * 2);
            bool checkWaterLevel = !AIParams.PatrolFlooded || !AIParams.PatrolDry;
            foreach (var hull in Hull.HullList)
            {
                if (hull.Submarine == null) { continue; }
                if (hull.Submarine.TeamID != Character.Submarine.TeamID) { continue; }
                if (!Character.Submarine.IsConnectedTo(hull.Submarine)) { continue; }
                if (hull.RectWidth < hullMinSize || hull.RectHeight < hullMinSize) { continue; }
                if (checkWaterLevel)
                {
                    if (AIParams.PatrolDry)
                    {
                        if (hull.WaterPercentage > 50) { continue; }
                    }
                    if (AIParams.PatrolFlooded)
                    {
                        if (hull.WaterPercentage < 80) { continue; }
                    }
                }
                if (AIParams.PatrolDry && hull.WaterPercentage < 80)
                {
                    if (Math.Abs(Character.CurrentHull.WorldPosition.Y - hull.WorldPosition.Y) > Character.CurrentHull.CeilingHeight / 2)
                    {
                        // Ignore dry hulls that are on a different level
                        continue;
                    }
                }
                if (!targetHulls.Contains(hull))
                {
                    targetHulls.Add(hull);
                    float weight = hull.Size.Combine();
                    float dist = Vector2.Distance(Character.WorldPosition, hull.WorldPosition);
                    float optimal = 1000;
                    float max = 3000;
                    // Prefer rooms that are far but not too far.
                    float distanceFactor = dist > optimal ? MathHelper.Lerp(1, 0, MathUtils.InverseLerp(optimal, max, dist)) : MathHelper.Lerp(0, 1, MathUtils.InverseLerp(0, optimal, dist));
                    float waterFactor = 1;
                    if (checkWaterLevel)
                    {
                        waterFactor = AIParams.PatrolDry ? MathHelper.Lerp(1, 0, MathUtils.InverseLerp(0, 100, hull.WaterPercentage)) : MathHelper.Lerp(0, 1, MathUtils.InverseLerp(0, 100, hull.WaterPercentage));
                    }
                    weight *= distanceFactor * waterFactor;
                    hullWeights.Add(weight);
                }
            }
        }

        #endregion

        #region Attack

        private Vector2 attackWorldPos;
        private Vector2 attackSimPos;
        private float reachTimer;
        // How long the monster tries to reach out for the target when it's close to it before ignoring it.
        private const float reachTimeOut = 10;

        private bool IsSameTarget(AITarget target, AITarget otherTarget)
        {
            if (target?.Entity == otherTarget?.Entity) { return true; }
            if (IsItemInCharacterInventory(target, otherTarget) || IsItemInCharacterInventory(otherTarget, target)) { return true; }
            return false;

            bool IsItemInCharacterInventory(AITarget potentialItem, AITarget potentialCharacter)
            {
                if (potentialItem?.Entity is Item item && potentialCharacter?.Entity is Character character)
                {
                    return item.ParentInventory?.Owner == character;
                }
                return false;
            }
        }

        private void UpdateAttack(float deltaTime)
        {
            if (SelectedAiTarget?.Entity == null || SelectedAiTarget.Entity.Removed || currentTargetingParams == null)
            {
                State = AIState.Idle;
                return;
            }

            attackWorldPos = SelectedAiTarget.WorldPosition;
            attackSimPos = SelectedAiTarget.SimPosition;

            if (SelectedAiTarget.Entity is Item item)
            {
                // If the item is held by a character, attack the character instead.
                Character owner = GetOwner(item);
                if (owner != null)
                {
                    if (Character.IsFriendly(owner) || owner.HasAbilityFlag(AbilityFlags.IgnoredByEnemyAI))
                    {
                        ResetAITarget();
                        State = AIState.Idle;
                        return;
                    }
                    else
                    {
                        SelectedAiTarget = owner.AiTarget;
                    }
                }
            }

            if (wallTarget != null)
            {
                attackWorldPos = wallTarget.Position;
                if (wallTarget.Structure.Submarine != null)
                {
                    attackWorldPos += wallTarget.Structure.Submarine.Position;
                }
                attackSimPos = Character.Submarine == wallTarget.Structure.Submarine ? wallTarget.Position : attackWorldPos;
                attackSimPos = ConvertUnits.ToSimUnits(attackSimPos);
            }
            else
            {
                attackSimPos = Character.GetRelativeSimPosition(SelectedAiTarget.Entity);
            }

            if (Character.AnimController.CanEnterSubmarine == CanEnterSubmarine.True)
            {
                if (TrySteerThroughGaps(deltaTime))
                {
                    return;
                }
            }
            else if (SelectedAiTarget.Entity is Structure w && wallTarget == null)
            {
                bool isBroken = true;
                for (int i = 0; i < w.Sections.Length; i++)
                {
                    if (!w.SectionBodyDisabled(i))
                    {
                        isBroken = false;
                        Vector2 sectionPos = w.SectionPosition(i, world: true);
                        attackWorldPos = sectionPos;
                        attackSimPos = ConvertUnits.ToSimUnits(attackWorldPos);
                        break;
                    }
                }
                if (isBroken)
                {
                    IgnoreTarget(SelectedAiTarget);
                    State = AIState.Idle;
                    ResetAITarget();
                    return;
                }
            }
            attackLimbSelectionTimer -= deltaTime;
            if (AttackLimb == null || attackLimbSelectionTimer <= 0)
            {
                attackLimbSelectionTimer = attackLimbSelectionInterval * Rand.Range(0.9f, 1.1f);
                if (!IsAttackRunning && !IsCoolDownRunning)
                {
                    AttackLimb = GetAttackLimb(attackWorldPos);
                }
            }
            Character targetCharacter = SelectedAiTarget.Entity as Character;
            IDamageable damageTarget = wallTarget != null ? wallTarget.Structure : SelectedAiTarget.Entity as IDamageable;
            bool canAttack = !Character.IsClimbing;
            bool pursue = false;
            if (IsCoolDownRunning && (_previousAttackLimb == null || AttackLimb == null || AttackLimb.attack.CoolDownTimer > 0))
            {
                var currentAttackLimb = AttackLimb ?? _previousAttackLimb;
                if (currentAttackLimb.attack.CoolDownTimer >= 
                    currentAttackLimb.attack.CoolDown + currentAttackLimb.attack.CurrentRandomCoolDown - currentAttackLimb.attack.AfterAttackDelay)
                {
                    return;
                }
                currentAttackLimb.attack.AfterAttackTimer += deltaTime;
                AIBehaviorAfterAttack activeBehavior =
                    currentAttackLimb.attack.AfterAttackSecondaryDelay > 0 && currentAttackLimb.attack.AfterAttackTimer > currentAttackLimb.attack.AfterAttackSecondaryDelay ?
                    currentAttackLimb.attack.AfterAttackSecondary :
                    currentAttackLimb.attack.AfterAttack;
                switch (activeBehavior)
                {
                    case AIBehaviorAfterAttack.Eat:
                        if (currentAttackLimb.IsSevered)
                        {
                            ReleaseEatingTarget();
                        }
                        else
                        {
                            UpdateEating(deltaTime);
                        }
                        return;
                    case AIBehaviorAfterAttack.Pursue:
                    case AIBehaviorAfterAttack.PursueIfCanAttack:
                        if (currentAttackLimb.attack.SecondaryCoolDown <= 0)
                        {
                            // No (valid) secondary cooldown defined.
                            if (activeBehavior == AIBehaviorAfterAttack.Pursue)
                            {
                                canAttack = false;
                                pursue = true;
                            }
                            else
                            {
                                UpdateFallBack(attackWorldPos, deltaTime, followThrough: true);
                                return;
                            }
                        }
                        else
                        {
                            if (currentAttackLimb.attack.SecondaryCoolDownTimer <= 0)
                            {
                                // Don't allow attacking when the attack target has just changed.
                                if (_previousAiTarget != null && !IsSameTarget(SelectedAiTarget, _previousAiTarget))
                                {
                                    canAttack = false;
                                    if (activeBehavior == AIBehaviorAfterAttack.PursueIfCanAttack)
                                    {
                                        // Fall back if cannot attack.
                                        UpdateFallBack(attackWorldPos, deltaTime, followThrough: true);
                                        return;
                                    }
                                    AttackLimb = null;
                                }
                                else
                                {
                                    // If the secondary cooldown is defined and expired, check if we can switch the attack
                                    var newLimb = GetAttackLimb(attackWorldPos, currentAttackLimb);
                                    if (newLimb != null)
                                    {
                                        // Attack with the new limb
                                        AttackLimb = newLimb;
                                    }
                                    else
                                    {
                                        // No new limb was found.
                                        if (activeBehavior == AIBehaviorAfterAttack.Pursue)
                                        {
                                            canAttack = false;
                                            pursue = true;
                                        }
                                        else
                                        {
                                            UpdateFallBack(attackWorldPos, deltaTime, followThrough: true);
                                            return;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Cooldown not yet expired, cannot attack -> steer towards the target
                                canAttack = false;
                            }
                        }
                        break;
                    case AIBehaviorAfterAttack.FallBackUntilCanAttack:
                    case AIBehaviorAfterAttack.FollowThroughUntilCanAttack:
                    case AIBehaviorAfterAttack.ReverseUntilCanAttack:
                        if (activeBehavior == AIBehaviorAfterAttack.ReverseUntilCanAttack)
                        {
                            Reverse = true;
                        }
                        if (currentAttackLimb.attack.SecondaryCoolDown <= 0)
                        {
                            // No (valid) secondary cooldown defined.
                            UpdateFallBack(attackWorldPos, deltaTime, activeBehavior == AIBehaviorAfterAttack.FollowThroughUntilCanAttack);
                            return;
                        }
                        else
                        {
                            if (currentAttackLimb.attack.SecondaryCoolDownTimer <= 0)
                            {
                                // Don't allow attacking when the attack target has just changed.
                                if (_previousAiTarget != null && !IsSameTarget(SelectedAiTarget, _previousAiTarget))
                                {
                                    UpdateFallBack(attackWorldPos, deltaTime, activeBehavior == AIBehaviorAfterAttack.FollowThroughUntilCanAttack);
                                    return;
                                }
                                // If the secondary cooldown is defined and expired, check if we can switch the attack
                                var newLimb = GetAttackLimb(attackWorldPos, currentAttackLimb);
                                if (newLimb != null)
                                {
                                    // Attack with the new limb
                                    AttackLimb = newLimb;
                                }
                                else
                                {
                                    // No new limb was found.
                                    UpdateFallBack(attackWorldPos, deltaTime, activeBehavior == AIBehaviorAfterAttack.FollowThroughUntilCanAttack);
                                    return;
                                }                                
                            }
                            else
                            {
                                // Cooldown not yet expired -> steer away from the target
                                UpdateFallBack(attackWorldPos, deltaTime, activeBehavior == AIBehaviorAfterAttack.FollowThroughUntilCanAttack);
                                return;
                            }
                        }
                        break;
                    case AIBehaviorAfterAttack.IdleUntilCanAttack:
                        if (currentAttackLimb.attack.SecondaryCoolDown <= 0)
                        {
                            // No (valid) secondary cooldown defined.
                            UpdateIdle(deltaTime, followLastTarget: false);
                            return;
                        }
                        else
                        {
                            if (currentAttackLimb.attack.SecondaryCoolDownTimer <= 0)
                            {
                                // Don't allow attacking when the attack target has just changed.
                                if (_previousAiTarget != null && !IsSameTarget(SelectedAiTarget, _previousAiTarget))
                                {
                                    UpdateIdle(deltaTime, followLastTarget: false);
                                    return;
                                }
                                // If the secondary cooldown is defined and expired, check if we can switch the attack
                                var newLimb = GetAttackLimb(attackWorldPos, currentAttackLimb);
                                if (newLimb != null)
                                {
                                    // Attack with the new limb
                                    AttackLimb = newLimb;
                                }
                                else
                                {
                                    // No new limb was found.
                                    UpdateIdle(deltaTime, followLastTarget: false);
                                    return;
                                }                                
                            }
                            else
                            {
                                // Cooldown not yet expired
                                UpdateIdle(deltaTime, followLastTarget: false);
                                return;
                            }
                        }
                        break;
                    case AIBehaviorAfterAttack.FollowThrough:
                        UpdateFallBack(attackWorldPos, deltaTime, followThrough: true);
                        return;
                    case AIBehaviorAfterAttack.FollowThroughWithoutObstacleAvoidance:
                        UpdateFallBack(attackWorldPos, deltaTime, followThrough: true, avoidObstacles: false);
                        return;
                    case AIBehaviorAfterAttack.FallBack:
                    case AIBehaviorAfterAttack.Reverse:
                    default:
                        if (activeBehavior == AIBehaviorAfterAttack.Reverse)
                        {
                            Reverse = true;
                        }
                        UpdateFallBack(attackWorldPos, deltaTime, followThrough: false);
                        return;
                }
            }
            else
            {
                attackVector = null;
            }

            if (canAttack)
            {
                if (AttackLimb is { IsSevered: true })
                {
                    AttackLimb = null;
                }
                if (AttackLimb == null || !IsValidAttack(AttackLimb, Character.GetAttackContexts(), SelectedAiTarget?.Entity))
                {
                    AttackLimb = GetAttackLimb(attackWorldPos);
                }
                canAttack = AttackLimb != null && AttackLimb.attack.CoolDownTimer <= 0;
            }

            if (!AIParams.CanOpenDoors)
            {
                if (!Character.AnimController.SimplePhysicsEnabled && SelectedAiTarget.Entity.Submarine != null && Character.Submarine == null && (!canAttackDoors || !canAttackWalls || !AIParams.TargetOuterWalls))
                {
                    if (wallTarget == null && Vector2.DistanceSquared(Character.WorldPosition, attackWorldPos) < 2000 * 2000)
                    {
                        // Check that we are not bumping into a door or a wall
                        Vector2 rayStart = SimPosition;
                        if (Character.Submarine == null)
                        {
                            rayStart -= SelectedAiTarget.Entity.Submarine.SimPosition;
                        }
                        Vector2 dir = SelectedAiTarget.WorldPosition - WorldPosition;
                        Vector2 rayEnd = rayStart + dir.ClampLength(Character.AnimController.Collider.GetLocalFront().Length() * 2);
                        Body closestBody = Submarine.CheckVisibility(rayStart, rayEnd, ignoreSubs: true);
                        if (Submarine.LastPickedFraction != 1.0f && closestBody != null &&
                            ((!AIParams.TargetOuterWalls || !canAttackWalls) && closestBody.UserData is Structure s && s.Submarine != null || !canAttackDoors && closestBody.UserData is Item i && i.Submarine != null && i.GetComponent<Door>() != null))
                        {
                            // Target is unreachable, there's a door or wall ahead
                            State = AIState.Idle;
                            IgnoreTarget(SelectedAiTarget);
                            ResetAITarget();
                            return;
                        }
                    }
                }
            }

            float distance = 0;
            Limb attackTargetLimb = null;
            if (canAttack)
            {
                if (!Character.AnimController.SimplePhysicsEnabled)
                {
                    // Target a specific limb instead of the target center position
                    if (wallTarget == null && targetCharacter != null)
                    {
                        var targetLimbType = AttackLimb.Params.Attack.Attack.TargetLimbType;
                        attackTargetLimb = GetTargetLimb(AttackLimb, targetCharacter, targetLimbType);
                        if (attackTargetLimb == null)
                        {
                            State = AIState.Idle;
                            IgnoreTarget(SelectedAiTarget);
                            ResetAITarget();
                            return;
                        }
                        attackWorldPos = attackTargetLimb.WorldPosition;
                        attackSimPos = Character.GetRelativeSimPosition(attackTargetLimb);
                    }
                }
                Vector2 attackLimbPos = Character.AnimController.SimplePhysicsEnabled ? Character.WorldPosition : AttackLimb.WorldPosition;
                Vector2 toTarget = attackWorldPos - attackLimbPos;
                Vector2 toTargetOffset = toTarget;
                // Add a margin when the target is moving away, because otherwise it might be difficult to reach it if the attack takes some time to execute
                if (Character.Submarine == null)
                {
                    if (wallTarget != null)
                    {
                        if (wallTarget.Structure.Submarine != null)
                        {
                            Vector2 margin = CalculateMargin(wallTarget.Structure.Submarine.Velocity);
                            toTargetOffset += margin;
                        }
                    }
                    else if (targetCharacter != null)
                    {
                        Vector2 margin = CalculateMargin(targetCharacter.AnimController.Collider.LinearVelocity);
                        toTargetOffset += margin;
                    }
                    else if (SelectedAiTarget.Entity is MapEntity { Submarine: not null } e)
                    {
                        Vector2 margin = CalculateMargin(e.Submarine.Velocity);
                        toTargetOffset += margin;
                    }
                }

                Vector2 CalculateMargin(Vector2 targetVelocity)
                {
                    if (targetVelocity == Vector2.Zero) { return Vector2.Zero; }
                    float diff = AttackLimb.attack.Range - AttackLimb.attack.DamageRange;
                    if (diff <= 0 || toTargetOffset.LengthSquared() <= MathUtils.Pow2(AttackLimb.attack.DamageRange)) { return Vector2.Zero; }
                    float dot = Vector2.Dot(Vector2.Normalize(targetVelocity), Vector2.Normalize(Character.AnimController.Collider.LinearVelocity));
                    if (dot <= 0 || !MathUtils.IsValid(dot)) { return Vector2.Zero; }
                    float distanceOffset = diff * AttackLimb.attack.Duration;
                    // Intentionally omit the unit conversion because we use distanceOffset as a multiplier.
                    return targetVelocity * distanceOffset * dot;
                }

                // Check that we can reach the target
                distance = toTargetOffset.Length();
                if (canAttack)
                {
                    canAttack = distance < AttackLimb.attack.Range;
                }
                if (canAttack && !Character.InWater && Character.AnimController.CanWalk)
                {
                    // On ground, ensure that the monster is facing the target, so that they don't hit the target while standing with their back towards it.
                    // In water, we don't want such checks, because it's ok for the monsters to attack targets on their sides and even behind them.
                    if (!Character.IsFacing(attackWorldPos))
                    {
                        canAttack = false;
                    }
                }
                if (canAttack)
                {
                    reachTimer = 0;
                    if (IsAggressiveBoarder)
                    {
                        if (SelectedAiTarget.Entity is Item i && i.GetComponent<Door>() is Door { CanBeTraversed: true })
                        {
                            // Don't attack open doors, just steer through them.
                            canAttack = false;
                        }   
                    }
                }
                else if (currentTargetingParams.AttackPattern == AttackPattern.Straight && distance < AttackLimb.attack.Range * 5)
                {
                    Vector2 targetVelocity = Vector2.Zero;
                    Submarine targetSub = SelectedAiTarget.Entity.Submarine;
                    if (targetSub != null)
                    {
                        targetVelocity = targetSub.Velocity;
                    }
                    else if (targetCharacter != null)
                    {
                        targetVelocity = targetCharacter.AnimController.Collider.LinearVelocity;
                    }
                    else if (SelectedAiTarget.Entity is Item i && i.body != null)
                    {
                        targetVelocity = i.body.LinearVelocity;
                    }
                    float mySpeed = Character.AnimController.Collider.LinearVelocity.LengthSquared();
                    float targetSpeed = targetVelocity.LengthSquared();
                    if (mySpeed < 0.1f || mySpeed > targetSpeed)
                    {
                        reachTimer += deltaTime;
                        if (reachTimer > reachTimeOut)
                        {
                            reachTimer = 0;
                            IgnoreTarget(SelectedAiTarget);
                            State = AIState.Idle;
                            ResetAITarget();
                            return;
                        }
                    }
                }

                // Crouch if the target is down (only humanoids), so that we can reach it.
                if (Character.AnimController is HumanoidAnimController humanoidAnimController && distance < AttackLimb.attack.Range * 2)
                {
                    if (targetCharacter?.CurrentHull is Hull targetHull && targetHull == Character.CurrentHull && toTarget.Y < 0)
                    {
                        if (Math.Abs(toTarget.Y) > AttackLimb.attack.Range / 2 && Math.Abs(toTarget.X) <= AttackLimb.attack.Range)
                        {
                            humanoidAnimController.Crouch();
                        }
                    }
                }
                
                if (canAttack)
                {
                    if (AttackLimb.attack.Ranged)
                    {
                        // Check that is facing the target
                        float offset = AttackLimb.Params.GetSpriteOrientation() - MathHelper.PiOver2;
                        Vector2 forward = VectorExtensions.Forward(AttackLimb.body.TransformedRotation - offset * Character.AnimController.Dir);
                        float angle = VectorExtensions.Angle(forward, toTarget);
                        canAttack = angle < MathHelper.ToRadians(AttackLimb.attack.RequiredAngle);
                        if (canAttack && AttackLimb.attack.AvoidFriendlyFire)
                        {
                            canAttack = !IsBlocked(Character.GetRelativeSimPosition(SelectedAiTarget.Entity));
                            bool IsBlocked(Vector2 targetPosition)
                            {
                                foreach (var body in Submarine.PickBodies(AttackLimb.SimPosition, targetPosition, myBodies, Physics.CollisionCharacter))
                                {
                                    Character hitTarget = null;
                                    if (body.UserData is Character c)
                                    {
                                        hitTarget = c;
                                    }
                                    else if (body.UserData is Limb limb)
                                    {
                                        hitTarget = limb.character;
                                    }
                                    if (hitTarget != null && !hitTarget.IsDead && Character.IsFriendly(hitTarget) && !IsAttackingOwner(hitTarget))
                                    {
                                        return true;
                                    }
                                }
                                return false;
                            }
                        }
                    }
                    else if (wallTarget == null && Character.CurrentHull != null && targetCharacter != null)
                    {
                        canAttack = Submarine.PickBody(SimPosition, attackSimPos, collisionCategory: Physics.CollisionWall) == null;
                    }
                }
            }
            Limb steeringLimb = canAttack && !AttackLimb.attack.Ranged ? AttackLimb : null;
            bool updateSteering = true;
            if (steeringLimb == null)
            {
                // If the attacking limb is a hand or claw, for example, using it as the steering limb can end in the result where the character circles around the target.
                steeringLimb = Character.AnimController.GetLimb(LimbType.Head) ?? Character.AnimController.GetLimb(LimbType.Torso);
            }
            if (steeringLimb == null)
            {
                State = AIState.Idle;
                return;
            }
            // Note that this returns null when we don't currently use IndoorsSteeringManager, even if we are able to path!
            // -> Don't use PathSteering, because that returns the reference even if we currently don't use it.
            var pathSteering = SteeringManager as IndoorsSteeringManager;
            if (AttackLimb != null && AttackLimb.attack.Retreat)
            {
                UpdateFallBack(attackWorldPos, deltaTime, followThrough: false);
            }
            else
            {
                if (pathSteering != null)
                {
                    // Attack doors
                    if (canAttackDoors && HasValidPath())
                    {
                        // If the target is in the same hull, there shouldn't be any doors blocking the path
                        if (targetCharacter == null || targetCharacter.CurrentHull != Character.CurrentHull)
                        {
                            var door = pathSteering.CurrentPath.CurrentNode?.ConnectedDoor ?? pathSteering.CurrentPath.NextNode?.ConnectedDoor;
                            if (door is { CanBeTraversed: false } && (!Character.IsInFriendlySub || !door.HasAccess(Character)))
                            {
                                if (door.Item.AiTarget != null && SelectedAiTarget != door.Item.AiTarget)
                                {
                                    SelectTarget(door.Item.AiTarget, currentTargetMemory.Priority);
                                    State = AIState.Attack;
                                    return;
                                }
                            }
                        }
                    }
                    // When pursuing, we don't want to pursue too close
                    float max = 300;
                    float margin = AttackLimb != null ? Math.Min(AttackLimb.attack.Range * 0.9f, max) : max;
                    if ((!canAttack || distance > margin) && !IsTryingToSteerThroughGap)
                    {
                        // Steer towards the target if in the same room and swimming
                        // Ruins have walls/pillars inside hulls and therefore we should navigate around them using the path steering.
                        if (Character.CurrentHull != null && 
                            Character.Submarine != null && !Character.Submarine.Info.IsRuin &&
                            (Character.AnimController.InWater || pursue || !Character.AnimController.CanWalk) &&
                            targetCharacter != null && VisibleHulls.Contains(targetCharacter.CurrentHull))
                        {
                            Vector2 myPos = Character.AnimController.SimplePhysicsEnabled ? Character.SimPosition : steeringLimb.SimPosition;
                            SteeringManager.SteeringManual(deltaTime, Vector2.Normalize(attackSimPos - myPos));
                        }
                        else
                        {
                            Func<PathNode, bool> nodeFilter = null;
                            float outsideNodePenalty = 0;
                            if (Character.CurrentHull != null && Character.IsInPlayerSub)
                            {
                                // Prefer not to path back outside, if we are inside player sub.
                                // Fixes monsters sometimes pathing from the airlock to another airlock on the other side of the sub, because the path is technically cheaper than the path through the interiors.
                                outsideNodePenalty = 50;
                            }
                            pathSteering.SteeringSeek(Character.GetRelativeSimPosition(SelectedAiTarget.Entity), weight: 2,
                                minGapWidth: minGapSize,
                                startNodeFilter: n => (n.Waypoint.CurrentHull == null) == (Character.CurrentHull == null),
                                nodeFilter: nodeFilter,
                                checkVisiblity: true,
                                outsideNodePenalty: outsideNodePenalty);

                            if (pathSteering.CurrentPath != null)
                            {
                                if (pathSteering.IsPathDirty)
                                {
                                    if (Character.CurrentHull is Hull hull && hull.ConnectedGaps.Any(static g => !g.IsRoomToRoom && g.Open >= 1.0f && g.ConnectedDoor != null))
                                    {
                                        // Reset in the airlock, because otherwise the character may be too slow to change the steering and keep moving outside. 
                                        SteeringManager.Reset();
                                    }
                                    // Steer towards the target
                                    SteeringManager.SteeringManual(deltaTime, Vector2.Normalize(SelectedAiTarget.Entity.WorldPosition - Character.WorldPosition));
                                }
                                else if (pathSteering.CurrentPath.Unreachable)
                                {
                                    State = AIState.Idle;
                                    IgnoreTarget(SelectedAiTarget);
                                    ResetAITarget();
                                    return;
                                }
                            }
                        }
                    }
                    else if (!IsTryingToSteerThroughGap)
                    {
                        if (AttackLimb.attack.Ranged)
                        {
                            float dir = Character.AnimController.Dir;
                            if (dir > 0 && attackWorldPos.X > AttackLimb.WorldPosition.X + margin || dir < 0 && attackWorldPos.X < AttackLimb.WorldPosition.X - margin)
                            {
                                SteeringManager.Reset();
                            }
                            else
                            {
                                // Too close
                                UpdateFallBack(attackWorldPos, deltaTime, followThrough: false);
                            }
                        }
                        else
                        {
                            // Close enough
                            SteeringManager.Reset();
                        }
                    }
                    else
                    {
                        SteeringManager.SteeringManual(deltaTime, Vector2.Normalize(SelectedAiTarget.Entity.WorldPosition - Character.WorldPosition));
                    }
                }
                else
                {
                    Vector2 steerPos = attackSimPos;
                    if (!Character.AnimController.SimplePhysicsEnabled)
                    {
                        // Offset so that we don't overshoot the movement
                        Vector2 offset = Character.SimPosition - steeringLimb.SimPosition;
                        steerPos += offset;
                    }
                    // Sweeping and circling doesn't work well inside
                    if (Character.CurrentHull == null)
                    {
                        switch (currentTargetingParams.AttackPattern)
                        {
                            case AttackPattern.Sweep:
                                if (currentTargetingParams.SweepDistance > 0)
                                {
                                    if (distance <= 0)
                                    {
                                        distance = (attackWorldPos - WorldPosition).Length();
                                    }
                                    float amplitude = MathHelper.Lerp(0, currentTargetingParams.SweepStrength, MathUtils.InverseLerp(currentTargetingParams.SweepDistance, 0, distance));
                                    if (amplitude > 0)
                                    {
                                        sweepTimer += deltaTime * currentTargetingParams.SweepSpeed;
                                        float sin = (float)Math.Sin(sweepTimer) * amplitude;
                                        steerPos = MathUtils.RotatePointAroundTarget(attackSimPos, SimPosition, sin);
                                    }
                                    else
                                    {
                                        sweepTimer = Rand.Range(-1000f, 1000f) * currentTargetingParams.SweepSpeed;
                                    }
                                }
                                break;
                            case AttackPattern.Circle:
                                if (IsCoolDownRunning) { break; }
                                if (IsAttackRunning && CirclePhase != CirclePhase.Strike) { break; }
                                if (currentTargetingParams == null) { break; }
                                var targetSub = SelectedAiTarget.Entity?.Submarine;
                                ISpatialEntity spatialTarget = targetSub ?? SelectedAiTarget.Entity;
                                float targetSize = 0;
                                if (!currentTargetingParams.IgnoreTargetSize)
                                {
                                    targetSize =
                                        targetSub != null ? Math.Max(targetSub.Borders.Width, targetSub.Borders.Height) / 2 :
                                            targetCharacter != null ? ConvertUnits.ToDisplayUnits(targetCharacter.AnimController.Collider.GetSize().X) : 100;
                                }
                                float sqrDistToTarget = Vector2.DistanceSquared(WorldPosition, spatialTarget.WorldPosition);
                                bool isProgressive = AIParams.MaxAggression - AIParams.StartAggression > 0;
                                switch (CirclePhase)
                                {
                                    case CirclePhase.Start:
                                        currentAttackIntensity = MathUtils.InverseLerp(AIParams.StartAggression, AIParams.MaxAggression, ClampIntensity(aggressionIntensity));
                                        inverseDir = false;
                                        circleDir = GetDirFromHeadingInRadius();
                                        circleRotation = 0;
                                        strikeTimer = 0;
                                        blockCheckTimer = 0;
                                        breakCircling = false;
                                        float minFallBackDistance = currentTargetingParams.CircleStartDistance * 0.5f;
                                        float maxFallBackDistance = currentTargetingParams.CircleStartDistance;
                                        float maxRandomOffset = currentTargetingParams.CircleMaxRandomOffset;
                                        // The lower the rotation speed, the slower the progression. Also the distance to the target stays longer.
                                        // So basically if the value is higher, the creature will strike the sub more quickly and with more precision.
                                        float ClampIntensity(float intensity) => MathHelper.Clamp(intensity * Rand.Range(0.9f, 1.1f), AIParams.StartAggression, AIParams.MaxAggression);
                                        if (isProgressive)
                                        {
                                            float intensity = ClampIntensity(currentAttackIntensity);
                                            float minRotationSpeed = 0.01f * currentTargetingParams.CircleRotationSpeed;
                                            float maxRotationSpeed = 0.5f * currentTargetingParams.CircleRotationSpeed;
                                            circleRotationSpeed = MathHelper.Lerp(minRotationSpeed, maxRotationSpeed, intensity);
                                            circleFallbackDistance = MathHelper.Lerp(maxFallBackDistance, minFallBackDistance, intensity);
                                            circleOffset = Rand.Vector(MathHelper.Lerp(maxRandomOffset, 0, intensity));
                                        }
                                        else
                                        {
                                            circleRotationSpeed = currentTargetingParams.CircleRotationSpeed;
                                            circleFallbackDistance = maxFallBackDistance;
                                            circleOffset = Rand.Vector(maxRandomOffset);
                                        }
                                        circleRotationSpeed *= Rand.Range(1 - currentTargetingParams.CircleRandomRotationFactor, 1 + currentTargetingParams.CircleRandomRotationFactor);
                                        aggressionIntensity = Math.Clamp(aggressionIntensity, AIParams.StartAggression, AIParams.MaxAggression);
                                        DisableAttacksIfLimbNotRanged();
                                        if (targetSub is { Borders.Width: < 1000 } && AttackLimb?.attack is { Ranged: false })
                                        {
                                            breakCircling = true;
                                            CirclePhase = CirclePhase.CloseIn;
                                        }
                                        else if (sqrDistToTarget > MathUtils.Pow2(targetSize + currentTargetingParams.CircleStartDistance))
                                        {
                                            CirclePhase = CirclePhase.CloseIn;
                                        }
                                        else if (sqrDistToTarget < MathUtils.Pow2(targetSize + circleFallbackDistance))
                                        {
                                            CirclePhase = CirclePhase.FallBack;
                                        }
                                        else
                                        {
                                            CirclePhase = CirclePhase.Advance;
                                        }
                                        break;
                                    case CirclePhase.CloseIn:
                                        Vector2 targetVelocity = GetTargetVelocity();
                                        float targetDistance = currentTargetingParams.IgnoreTargetSize ? currentTargetingParams.CircleStartDistance * 0.9f:
                                            targetSize + currentTargetingParams.CircleStartDistance / 2;
                                        if (AttackLimb != null && distance > 0 && distance < AttackLimb.attack.Range * GetStrikeDistanceMultiplier(targetVelocity))
                                        {
                                            strikeTimer = AttackLimb.attack.CoolDown;
                                            CirclePhase = CirclePhase.Strike;
                                        }
                                        else if (!breakCircling && sqrDistToTarget <= MathUtils.Pow2(targetDistance) && targetVelocity.LengthSquared() <= MathUtils.Pow2(GetTargetMaxSpeed()))
                                        {
                                            CirclePhase = CirclePhase.Advance;
                                        }
                                        DisableAttacksIfLimbNotRanged();
                                        break;
                                    case CirclePhase.FallBack:
                                        updateSteering = false;
                                        bool isBlocked = !UpdateFallBack(attackWorldPos, deltaTime, followThrough: false, checkBlocking: true);
                                        if (isBlocked || sqrDistToTarget > MathUtils.Pow2(targetSize + circleFallbackDistance))
                                        {
                                            CirclePhase = CirclePhase.Advance;
                                            break;
                                        }
                                        DisableAttacksIfLimbNotRanged();
                                        break;
                                    case CirclePhase.Advance:
                                        Vector2 targetVel = GetTargetVelocity();
                                        // If the target is moving fast, just steer towards the target
                                        if (breakCircling || targetVel.LengthSquared() > MathUtils.Pow2(GetTargetMaxSpeed()))
                                        {
                                            CirclePhase = CirclePhase.CloseIn;
                                        }
                                        else if (sqrDistToTarget > MathUtils.Pow2(targetSize + currentTargetingParams.CircleStartDistance * 1.2f))
                                        {
                                            if (currentTargetingParams.DynamicCircleRotationSpeed && circleRotationSpeed < 100)
                                            {
                                                circleRotationSpeed *= 1 + deltaTime;
                                            }
                                            else
                                            {
                                                CirclePhase = CirclePhase.CloseIn;
                                            }
                                        }
                                        else
                                        {
                                            float rotationStep = circleRotationSpeed * deltaTime * circleDir;
                                            if (isProgressive)
                                            {
                                                circleRotation += rotationStep;
                                            }
                                            else
                                            {
                                                circleRotation = rotationStep;
                                            }
                                            Vector2 targetPos = attackSimPos + circleOffset;
                                            float targetDist = targetSize;
                                            if (targetDist <= 0)
                                            {
                                                targetDist = circleFallbackDistance;
                                            }
                                            if (targetSub != null && AttackLimb?.attack is { Ranged: true })
                                            {
                                                targetDist += circleFallbackDistance / 2;
                                            }
                                            if (Vector2.DistanceSquared(SimPosition, targetPos) < ConvertUnits.ToSimUnits(targetDist))
                                            {
                                                // Too close to the target point
                                                // When the offset position is outside of the sub it happens that the creature sometimes reaches the target point, 
                                                // which makes it continue circling around the point (as supposed)
                                                // But when there is some offset and the offset is too near, this is not what we want.
                                                if (canAttack && AttackLimb?.attack is { Ranged: false } && sqrDistToTarget < MathUtils.Pow2(targetSize + circleFallbackDistance))
                                                {
                                                    CirclePhase = CirclePhase.Strike;
                                                    strikeTimer = AttackLimb.attack.CoolDown;
                                                }
                                                else
                                                {
                                                    CirclePhase = CirclePhase.Start;
                                                }
                                                break;
                                            }
                                            steerPos = MathUtils.RotatePointAroundTarget(SimPosition, targetPos, circleRotation);
                                            if (IsBlocked(deltaTime, steerPos))
                                            {
                                                if (!inverseDir)
                                                {
                                                    // First try changing the direction
                                                    circleDir = -circleDir;
                                                    inverseDir = true;
                                                }
                                                else if (circleRotationSpeed < 1)
                                                {
                                                    // Then try increasing the rotation speed to change the movement curve
                                                    circleRotationSpeed *= 1 + deltaTime;
                                                }
                                                else if (circleOffset.LengthSquared() > 0.1f)
                                                {
                                                    // Then try removing the offset
                                                    circleOffset = Vector2.Zero;
                                                }
                                                else
                                                {
                                                    // If we still fail, just steer towards the target
                                                    breakCircling = AttackLimb?.attack is { Ranged: false };
                                                    if (!breakCircling)
                                                    {
                                                        CirclePhase = CirclePhase.FallBack;
                                                    }
                                                }
                                            }
                                        }
                                        if (AttackLimb?.attack is { Ranged: false })
                                        {
                                            canAttack = false;
                                            float requiredDistMultiplier = GetStrikeDistanceMultiplier(targetVel);
                                            if (distance > 0 && distance < AttackLimb.attack.Range * requiredDistMultiplier && IsFacing(margin: MathHelper.Lerp(0.5f, 0.9f, currentAttackIntensity)))
                                            {
                                                strikeTimer = AttackLimb.attack.CoolDown;
                                                CirclePhase = CirclePhase.Strike;
                                            }
                                        }
                                        break;
                                    case CirclePhase.Strike:
                                        strikeTimer -= deltaTime;
                                        // just continue the movement forward to make it possible to evade the attack
                                        steerPos = SimPosition + Steering;
                                        if (strikeTimer <= 0)
                                        {
                                            CirclePhase = CirclePhase.Start;
                                            aggressionIntensity += AIParams.AggressionCumulation;
                                        }
                                        break;
                                }
                                break;

                                bool IsFacing(float margin)
                                {
                                    float offset = steeringLimb.Params.GetSpriteOrientation() - MathHelper.PiOver2;
                                    Vector2 forward = VectorExtensions.Forward(steeringLimb.body.TransformedRotation - offset * Character.AnimController.Dir);
                                    return Vector2.Dot(Vector2.Normalize(attackWorldPos - WorldPosition), forward) > margin;
                                }

                                float GetStrikeDistanceMultiplier(Vector2 targetVelocity)
                                {
                                    if (currentTargetingParams.CircleStrikeDistanceMultiplier < 1) { return 0; }
                                    float requiredDistMultiplier = 2;
                                    bool isHeading = Vector2.Dot(Vector2.Normalize(attackWorldPos - WorldPosition), Vector2.Normalize(Steering)) > 0.9f;
                                    if (isHeading)
                                    {
                                        requiredDistMultiplier = currentTargetingParams.CircleStrikeDistanceMultiplier;
                                        float targetVelocityHorizontal = Math.Abs(targetVelocity.X);
                                        if (targetVelocityHorizontal > 1)
                                        {
                                            // Reduce the required distance if the target is moving.
                                            requiredDistMultiplier -= MathHelper.Lerp(0, Math.Max(currentTargetingParams.CircleStrikeDistanceMultiplier - 1, 1), Math.Clamp(targetVelocityHorizontal / 10, 0, 1));
                                            if (requiredDistMultiplier < 2)
                                            {
                                                requiredDistMultiplier = 2;
                                            }
                                        }
                                    }
                                    return requiredDistMultiplier;
                                }

                                float GetDirFromHeadingInRadius()
                                {
                                    Vector2 heading = VectorExtensions.Forward(Character.AnimController.Collider.Rotation);
                                    float angle = MathUtils.VectorToAngle(heading);
                                    return angle > MathHelper.Pi || angle < -MathHelper.Pi ? -1 : 1;
                                }

                                Vector2 GetTargetVelocity()
                                {
                                    if (targetSub != null)
                                    {
                                        return targetSub.Velocity;
                                    }
                                    else if (targetCharacter != null)
                                    {
                                        return targetCharacter.AnimController.Collider.LinearVelocity;
                                    }
                                    return Vector2.Zero;
                                }

                                float GetTargetMaxSpeed() => Character.ApplyTemporarySpeedLimits(Character.AnimController.SwimFastParams.MovementSpeed * (targetSub != null ? 0.3f : 0.5f));
                        }
                    }
                    if (updateSteering)
                    {
                        if (currentTargetingParams.AttackPattern == AttackPattern.Straight && AttackLimb is Limb attackLimb && attackLimb.attack.Ranged)
                        {
                            bool advance = !canAttack && Character.CurrentHull == null || distance > attackLimb.attack.Range * 0.9f;
                            bool fallBack = canAttack && distance < Math.Min(250, attackLimb.attack.Range * 0.25f);
                            if (fallBack)
                            {
                                Reverse = true;
                                UpdateFallBack(attackWorldPos, deltaTime, followThrough: false);
                            }
                            else if (advance)
                            {
                                SteeringManager.SteeringSeek(steerPos, 10);                                
                            }
                            else
                            {
                                if (Character.CurrentHull == null && !canAttack)
                                {
                                    SteeringManager.SteeringWander(avoidWanderingOutsideLevel: true);
                                    SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 5);
                                }
                                else
                                {
                                    SteeringManager.Reset();
                                    FaceTarget(SelectedAiTarget.Entity);
                                }
                            }
                        }
                        else if (!canAttack || distance > Math.Min(AttackLimb.attack.Range * 0.9f, 100))
                        {
                            if (pathSteering != null)
                            {
                                pathSteering.SteeringSeek(steerPos, weight: 10, minGapWidth: minGapSize);
                            }
                            else
                            {
                                SteeringManager.SteeringSeek(steerPos, 10);
                            }
                        }
                        if (Character.CurrentHull == null && (SelectedAiTarget?.Entity is Character c && c.Submarine == null ||
                            distance == 0 ||
                            distance > ConvertUnits.ToDisplayUnits(avoidLookAheadDistance * 2) ||
                            AttackLimb != null && AttackLimb.attack.Ranged))
                        {
                            SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 30);
                        }
                    }
                }
            }
            Entity targetEntity = wallTarget?.Structure ?? SelectedAiTarget?.Entity;
            if (AttackLimb?.attack is Attack { Ranged: true } attack)
            {
                AimRangedAttack(attack, attackTargetLimb as ISpatialEntity ?? targetEntity);
            }
            if (canAttack)
            {
                if (!UpdateLimbAttack(deltaTime, attackSimPos, damageTarget, distance, attackTargetLimb))
                {
                    IgnoreTarget(SelectedAiTarget);
                }
            }
            else if (IsAttackRunning)
            {
                AttackLimb.attack.ResetAttackTimer();
            }

            void DisableAttacksIfLimbNotRanged()
            {
                if (AttackLimb?.attack is { Ranged: false })
                {
                    canAttack = false;
                }
            }
        }

        public void AimRangedAttack(Attack attack, ISpatialEntity targetEntity)
        {
            if (attack is not { Ranged: true }) { return; }
            if (targetEntity is Entity { Removed: true }) { return; }
            Character.SetInput(InputType.Aim, false, true);
            if (attack.AimRotationTorque <= 0) { return; }
            Limb limb = GetLimbToRotate(attack);
            if (limb != null)
            {
                Vector2 toTarget = targetEntity.WorldPosition - limb.WorldPosition;
                float offset = limb.Params.GetSpriteOrientation() - MathHelper.PiOver2;
                limb.body.SuppressSmoothRotationCalls = false;
                float angle = MathUtils.VectorToAngle(toTarget);
                limb.body.SmoothRotate(angle + offset, attack.AimRotationTorque);
                limb.body.SuppressSmoothRotationCalls = true;
            }
        }

        private bool IsValidAttack(Limb attackingLimb, IEnumerable<AttackContext> currentContexts, Entity target)
        {
            if (attackingLimb == null) { return false; }
            if (target == null) { return false; }
            var attack = attackingLimb.attack;
            if (attack == null) { return false; }
            if (attack.CoolDownTimer > 0) { return false; }
            if (!attack.IsValidContext(currentContexts)) { return false; }
            if (!attack.IsValidTarget(target)) { return false; }
            if (!attackingLimb.attack.Ranged)
            {
                switch (target)
                {
                    case Item when attackingLimb.attack.ItemDamage <= 0:
                    case Structure when attackingLimb.attack.StructureDamage <= 0:
                        return false;
                }
            }
            if (target is ISerializableEntity se and Barotrauma.Character)
            {
                if (attack.Conditionals.Any(c => !c.TargetSelf && !c.Matches(se))) { return false; }
            }
            if (attack.Conditionals.Any(c => c.TargetSelf && !c.Matches(Character))) { return false; }
            if (attack.Ranged)
            {
                // Check that is approximately facing the target
                Vector2 attackLimbPos = Character.AnimController.SimplePhysicsEnabled ? Character.WorldPosition : attackingLimb.WorldPosition;
                Vector2 toTarget = attackWorldPos - attackLimbPos;
                if (attack.MinRange > 0 && toTarget.LengthSquared() < MathUtils.Pow2(attack.MinRange)) { return false; }
                float offset = attackingLimb.Params.GetSpriteOrientation() - MathHelper.PiOver2;
                Vector2 forward = VectorExtensions.Forward(attackingLimb.body.TransformedRotation - offset * Character.AnimController.Dir);
                float angle = MathHelper.ToDegrees(VectorExtensions.Angle(forward, toTarget));
                if (angle > attack.RequiredAngle) { return false; }
            }
            if (attack.RootForceWorldEnd.LengthSquared() > 1)
            {
                // Don't allow root motion attacks, if we are not on the same level with the target, because it can cause warping.
                switch (target)
                {
                    case Character targetCharacter when Character.CurrentHull != targetCharacter.CurrentHull || targetCharacter.IsKnockedDownOrRagdolled:
                    case Item targetItem when Character.CurrentHull != targetItem.CurrentHull:
                        return false;
                }
                float verticalDistance = Math.Abs(attackWorldPos.Y - Character.WorldPosition.Y);
                if (verticalDistance > 50)
                {
                    return false;
                }
            }
            return true;
        }

        private readonly List<Limb> attackLimbs = new List<Limb>();
        private readonly List<float> weights = new List<float>();
        private Limb GetAttackLimb(Vector2 attackWorldPos, Limb ignoredLimb = null)
        {
            var currentContexts = Character.GetAttackContexts();
            Entity target = wallTarget != null ? wallTarget.Structure : SelectedAiTarget?.Entity;
            if (target == null) { return null; }
            Limb selectedLimb = null;
            float currentPriority = -1;
            foreach (Limb limb in Character.AnimController.Limbs)
            {
                if (limb == ignoredLimb) { continue; }
                if (limb.IsSevered || limb.IsStuck) { continue; }
                if (!IsValidAttack(limb, currentContexts, target)) { continue; }
                if (AIParams.RandomAttack)
                {
                    attackLimbs.Add(limb);
                    weights.Add(limb.attack.Priority);
                }
                else
                {
                    float priority = CalculatePriority(limb, attackWorldPos);
                    if (priority > currentPriority)
                    {
                        currentPriority = priority;
                        selectedLimb = limb;
                    }
                }
            }
            if (AIParams.RandomAttack)
            {
                selectedLimb = ToolBox.SelectWeightedRandom(attackLimbs, weights, Rand.RandSync.Unsynced);
                attackLimbs.Clear();
                weights.Clear();
            }
            return selectedLimb;

            float CalculatePriority(Limb limb, Vector2 attackPos)
            {
                float prio = 1 + limb.attack.Priority;
                if (Character.AnimController.SimplePhysicsEnabled) { return prio; }
                float dist = Vector2.Distance(limb.WorldPosition, attackPos);
                float distanceFactor = 1;
                if (limb.attack.Ranged)
                {
                    float min = 100;
                    distanceFactor = MathHelper.Lerp(1, 0, MathUtils.InverseLerp(min, Math.Max(limb.attack.Range / 2, min), dist));
                }
                else
                {
                    // The limb is ignored if the target is not close. Prevents character going in reverse if very far away from it.
                    // We also need a max value that is more than the actual range.
                    distanceFactor = MathHelper.Lerp(1, 0, MathUtils.InverseLerp(0, limb.attack.Range * 3, dist));
                }
                return prio * distanceFactor;
            }
        }

        public override void OnAttacked(Character attacker, AttackResult attackResult)
        {
            float reactionTime = Rand.Range(0.1f, 0.3f);
            updateTargetsTimer = Math.Min(updateTargetsTimer, reactionTime);
            bool wasLatched = IsLatchedOnSub;
            Character.AnimController.ReleaseStuckLimbs();
            if (attackResult.Damage > 0)
            {
                LatchOntoAI?.DeattachFromBody(reset: true, cooldown: 1);
            }
            if (attacker?.AiTarget == null || attacker.Removed || attacker.IsDead) { return; }
            AITargetMemory targetMemory = GetTargetMemory(attacker.AiTarget, addIfNotFound: true, keepAlive: true);
            targetMemory.Priority += GetRelativeDamage(attackResult.Damage, Character.Vitality) * AIParams.AggressionHurt;
            if (attackResult.Damage >= AIParams.DamageThreshold)
            {
                ReleaseDragTargets();
            }
            bool isFriendly = Character.IsFriendly(attacker);
            if (wasLatched)
            {
                State = AIState.Escape;
                avoidTimer = AIParams.AvoidTime * 0.5f * Rand.Range(0.75f, 1.25f);
                if (!isFriendly)
                {
                    SelectTarget(attacker.AiTarget);
                }
                return;
            }
            if (State == AIState.Flee)
            {
                if (!isFriendly)
                {
                    SelectTarget(attacker.AiTarget);
                }
                return;
            }
            
            if (!isFriendly && attackResult.Damage > 0.0f)
            {
                bool canAttack = attacker.Submarine == Character.Submarine && canAttackCharacters || attacker.Submarine != null && canAttackWalls;
                if (canAttack)
                {
                    switch (State)
                    {
                        case AIState.PlayDead when Rand.Value() < 0.5f:
                            // 50% chance of not reacting when playing dead.
                            return;
                        case AIState.PlayDead:
                        case AIState.Hiding:
                            SelectTarget(attacker.AiTarget);
                            State = AIState.Attack;
                            break;
                    }
                }
                if (AIParams.AttackWhenProvoked && canAttack)
                {
                    if (ignoredTargets.Contains(attacker.AiTarget))
                    {
                        ignoredTargets.Remove(attacker.AiTarget);
                    }
                    if (attacker.IsHusk)
                    {
                        ChangeTargetState(Tags.Husk, AIState.Attack, 100);
                    }
                    else
                    {
                        ChangeTargetState(attacker, AIState.Attack, 100);
                    }
                }
                else if (!AIParams.HasTag(attacker.SpeciesName))
                {
                    if (attacker.IsHusk)
                    {
                        ChangeTargetState(Tags.Husk, canAttack ? AIState.Attack : AIState.Escape, 100);
                    }
                    else if (attacker.AIController is EnemyAIController enemyAI)
                    {
                        if (enemyAI.CombatStrength > CombatStrength)
                        {
                            if (!AIParams.HasTag("stronger"))
                            {
                                ChangeTargetState(attacker, canAttack ? AIState.Attack : AIState.Escape, 100);
                            }
                        }
                        else if (enemyAI.CombatStrength < CombatStrength)
                        {
                            if (!AIParams.HasTag("weaker"))
                            {
                                ChangeTargetState(attacker, canAttack ? AIState.Attack : AIState.Escape, 100);
                            }
                        }
                        else if (!AIParams.HasTag("equal"))
                        {
                            ChangeTargetState(attacker, canAttack ? AIState.Attack : AIState.Escape, 100);
                        }
                    }
                    else
                    {
                        ChangeTargetState(attacker, canAttack ? AIState.Attack : AIState.Escape, 100);
                    }
                }
                else if (canAttack && attacker.IsHuman && 
                    AIParams.TryGetTargets(attacker, out IEnumerable<CharacterParams.TargetParams> targetingParams))
                {
                    //use a temporary list, because changing the state may change the targetingParams returned by TryGetTargets,
                    //which would cause a "collection was modified" exception
                    tempParamsList.Clear();
                    tempParamsList.AddRange(targetingParams);
                    foreach (var tp in tempParamsList)
                    {
                        if (tp.State is AIState.Aggressive or AIState.PassiveAggressive)
                        {
                            ChangeTargetState(attacker, AIState.Attack, 100);
                        }
                    }
                }
            }

            // Only allow to react once. Otherwise would attack the target with only a fraction of a cooldown
            bool retaliate = !isFriendly && SelectedAiTarget != attacker.AiTarget && attacker.Submarine == Character.Submarine;
            bool avoidGunFire = AIParams.AvoidGunfire && attacker.Submarine != Character.Submarine;

            if (State == AIState.Attack && (IsAttackRunning || IsCoolDownRunning))
            {
                retaliate = false;
                if (IsAttackRunning)
                {
                    avoidGunFire = false;
                }
            }
            if (retaliate)
            {
                // Reduce the cooldown so that the character can react
                foreach (var limb in Character.AnimController.Limbs)
                {
                    if (limb.attack != null)
                    {
                        limb.attack.CoolDownTimer *= reactionTime;
                    }
                }
            }
            else if (avoidGunFire && attackResult.Damage >= AIParams.DamageThreshold)
            {
                State = AIState.Escape;
                avoidTimer = AIParams.AvoidTime * Rand.Range(0.75f, 1.25f);
                SelectTarget(attacker.AiTarget);
            }
            if (Math.Max(Character.HealthPercentage, 0) < FleeHealthThreshold)
            {
                State = AIState.Flee;
                avoidTimer = AIParams.MinFleeTime * Rand.Range(0.75f, 1.25f);
                SelectTarget(attacker.AiTarget);
            }
        }

        private Item GetEquippedItem(Limb limb)
        {
            InvSlotType GetInvSlotForLimb()
            {
                return limb.type switch
                {
                    LimbType.RightHand => InvSlotType.RightHand,
                    LimbType.LeftHand => InvSlotType.LeftHand,
                    LimbType.Head => InvSlotType.Head,
                    _ => InvSlotType.None,
                };
            }
            var slot = GetInvSlotForLimb();
            if (slot != InvSlotType.None)
            {
                return Character.Inventory.GetItemInLimbSlot(slot);
            }
            return null;
        }

        // 10 dmg, 100 health -> 0.1
        private static float GetRelativeDamage(float dmg, float vitality) => dmg / Math.Max(vitality, 1.0f);

        private bool UpdateLimbAttack(float deltaTime, Vector2 attackSimPos, IDamageable damageTarget, float distance = -1, Limb targetLimb = null)
        {
            if (SelectedAiTarget?.Entity == null) { return false; }
            if (AttackLimb?.attack == null) { return false; }
            ISpatialEntity spatialTarget = wallTarget != null ? wallTarget.Structure : SelectedAiTarget.Entity as ISpatialEntity;
            if (spatialTarget == null) { return false; }
            ActiveAttack = AttackLimb.attack;
            if (wallTarget != null)
            {
                // If the selected target is not the wall target, make the wall target the selected target.
                var aiTarget = wallTarget.Structure.AiTarget;
                if (aiTarget != null && SelectedAiTarget != aiTarget)
                {
                    SelectTarget(aiTarget, GetTargetMemory(SelectedAiTarget, addIfNotFound: true).Priority);
                    State = AIState.Attack;
                    return true;
                }
            }
            if (damageTarget == null) { return false; }
            ActiveAttack = AttackLimb.attack;
            if (ActiveAttack.Ranged && ActiveAttack.RequiredAngleToShoot > 0)
            {
                Limb referenceLimb = GetLimbToRotate(ActiveAttack);
                if (referenceLimb != null)
                {
                    Vector2 toTarget = attackWorldPos - referenceLimb.WorldPosition;
                    float offset = referenceLimb.Params.GetSpriteOrientation() - MathHelper.PiOver2;
                    Vector2 forward = VectorExtensions.Forward(referenceLimb.body.TransformedRotation - offset * referenceLimb.Dir);
                    float angle = MathHelper.ToDegrees(VectorExtensions.Angle(forward, toTarget));
                    if (angle > ActiveAttack.RequiredAngleToShoot)
                    {
                        return true;
                    }
                }
            }
            if (Character.Params.CanInteract && Character.Inventory != null)
            {
                // Use equipped items (weapons)
                Item item = GetEquippedItem(AttackLimb);
                if (item != null)
                {
                    if (item.RequireAimToUse)
                    {
                        if (!Aim(deltaTime, spatialTarget, item))
                        {
                            // Valid target, but can't shoot -> return true so that it will not be ignored.
                            return true;
                        }
                    }
                    if (damageTarget != null)
                    {
                        Character.SetInput(item.IsShootable ? InputType.Shoot : InputType.Use, false, true);
                        item.Use(deltaTime, user: Character);
                    }
                }
            }
            if (damageTarget == null) { return true; }
            //simulate attack input to get the character to attack client-side
            Character.SetInput(InputType.Attack, true, true);
            if (!ActiveAttack.IsRunning)
            {
#if SERVER
                GameMain.NetworkMember.CreateEntityEvent(Character, new Character.SetAttackTargetEventData(
                    AttackLimb,
                    damageTarget,
                    targetLimb,
                    SimPosition));
#else
                Character.PlaySound(CharacterSound.SoundType.Attack, maxInterval: 3);
#endif
            }
            if (AttackLimb.UpdateAttack(deltaTime, attackSimPos, damageTarget, out AttackResult attackResult, distance, targetLimb))
            {
                if (ActiveAttack.CoolDownTimer > 0)
                {
                    SetAimTimer(Math.Min(ActiveAttack.CoolDown, 1.5f));
                }
                if (LatchOntoAI != null && SelectedAiTarget.Entity is Character targetCharacter)
                {
                    LatchOntoAI.SetAttachTarget(targetCharacter);
                }
                if (!ActiveAttack.Ranged)
                {
                    if (damageTarget.Health > 0 && attackResult.Damage > 0)
                    {
                        // Managed to hit a living/non-destroyed target. Increase the priority more if the target is low in health -> dies easily/soon
                        float greed = AIParams.AggressionGreed;
                        if (damageTarget is not Barotrauma.Character)
                        {
                            // Halve the greed for attacking non-characters.
                            greed /= 2;
                        }
                        currentTargetMemory.Priority += GetRelativeDamage(attackResult.Damage, damageTarget.Health) * greed;
                    }
                    else
                    {
                        currentTargetMemory.Priority -= Math.Max(currentTargetMemory.Priority / 2, 1);
                        return currentTargetMemory.Priority > 1;
                    }
                }
            }
            return true;
        }

        private float aimTimer;
        private float visibilityCheckTimer;
        private bool canSeeTarget;
        private float sinTime;
        private bool Aim(float deltaTime, ISpatialEntity target, Item weapon)
        {
            if (target == null || weapon == null) { return false; }
            if (AttackLimb == null) { return false; }
            Vector2 toTarget = target.WorldPosition - weapon.WorldPosition;
            float dist = toTarget.Length();
            Character.CursorPosition = target.WorldPosition;
            if (AttackLimb.attack.SwayAmount > 0)
            {
                sinTime += deltaTime * AttackLimb.attack.SwayFrequency;
                Character.CursorPosition += VectorExtensions.Forward(weapon.body.TransformedRotation + (float)Math.Sin(sinTime) / 2, dist / 2 * AttackLimb.attack.SwayAmount);
            }
            if (Character.Submarine != null)
            {
                Character.CursorPosition -= Character.Submarine.Position;
            }
            visibilityCheckTimer -= deltaTime;
            if (visibilityCheckTimer <= 0.0f)
            {
                canSeeTarget = Character.CanSeeTarget(target);
                visibilityCheckTimer = 0.2f;
            }
            if (!canSeeTarget)
            {
                SetAimTimer();
                return false;
            }
            Character.SetInput(InputType.Aim, false, true);
            if (aimTimer > 0)
            {
                aimTimer -= deltaTime;
                return false;
            }
            float angle = VectorExtensions.Angle(VectorExtensions.Forward(weapon.body.TransformedRotation), toTarget);
            float minDistance = 300;
            float distanceFactor = MathHelper.Lerp(1.0f, 0.1f, MathUtils.InverseLerp(minDistance, 1000, dist));
            float margin = MathHelper.PiOver4 * distanceFactor;
            if (angle < margin || dist < minDistance)
            {
                var collisionCategories = Physics.CollisionCharacter | Physics.CollisionWall | Physics.CollisionLevel | Physics.CollisionItemBlocking;
                var pickedBody = Submarine.PickBody(weapon.SimPosition, Character.GetRelativeSimPosition(target), myBodies, collisionCategories, allowInsideFixture: true);
                if (pickedBody != null)
                {
                    if (target is MapEntity)
                    {
                        if (pickedBody.UserData is Submarine sub && sub == target.Submarine)
                        {
                            return true;
                        }
                        else if (target == pickedBody.UserData)
                        {
                            return true;
                        }
                    }
                    Character t = null;
                    if (pickedBody.UserData is Character c)
                    {
                        t = c;
                    }
                    else if (pickedBody.UserData is Limb limb)
                    {
                        t = limb.character;
                    }
                    if (t != null && (t == target || (!Character.IsFriendly(t) || IsAttackingOwner(t))))
                    {
                        return true;
                    }
                    if (pickedBody.UserData is Item item && item.Prefab.DamagedByProjectiles)
                    {
                        // Target behind an item -> allow shooting.
                        return true;
                    }
                    if (pickedBody.UserData is Holdable holdable && holdable.Item.Prefab.DamagedByProjectiles)
                    {
                        // Target behind a blocking but destructible item -> allow shooting.
                        return true;
                    }
                }
            }
            return false;
        }

        private void SetAimTimer(float timer = 1.5f) => aimTimer = timer * Rand.Range(0.75f, 1.25f);

        private readonly float blockCheckInterval = 0.1f;
        private float blockCheckTimer;
        private bool isBlocked;
        private bool IsBlocked(float deltaTime, Vector2 steerPos, Category collisionCategory = Physics.CollisionLevel)
        {
            blockCheckTimer -= deltaTime;
            if (blockCheckTimer <= 0)
            {
                blockCheckTimer = blockCheckInterval;
                isBlocked = Submarine.PickBodies(SimPosition, steerPos, collisionCategory: collisionCategory).Any();
            }
            return isBlocked;
        }

        private Vector2? attackVector = null;
        private bool UpdateFallBack(Vector2 attackWorldPos, float deltaTime, bool followThrough, bool checkBlocking = false, bool avoidObstacles = true)
        {
            if (attackVector == null)
            {
                attackVector = attackWorldPos - WorldPosition;
            }
            Vector2 dir = Vector2.Normalize(followThrough ? attackVector.Value : -attackVector.Value);
            if (!MathUtils.IsValid(dir))
            {
                dir = Vector2.UnitY;
            }
            steeringManager.SteeringManual(deltaTime, dir);
            if (Character.AnimController.InWater && !Reverse && avoidObstacles)
            {
                SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 15);
            }
            if (checkBlocking)
            {
                return !IsBlocked(deltaTime, SimPosition + dir * (avoidLookAheadDistance / 2));
            }
            return true;
        }

        private Limb GetLimbToRotate(Attack attack)
        {
            Limb limb = AttackLimb;
            if (attack.RotationLimbIndex > -1 && attack.RotationLimbIndex < Character.AnimController.Limbs.Length)
            {
                limb = Character.AnimController.Limbs[attack.RotationLimbIndex];
            }
            return limb;
        }

        #endregion

        #region Eat

        private void UpdateEating(float deltaTime)
        {
            if (SelectedAiTarget?.Entity == null || SelectedAiTarget.Entity.Removed)
            {
                ReleaseEatingTarget();
                return;
            }
            if (SelectedAiTarget.Entity is Barotrauma.Character or Item)
            {
                Limb mouthLimb = Character.AnimController.GetLimb(LimbType.Head);
                if (mouthLimb == null)
                {
                    DebugConsole.ThrowError("Character \"" + Character.SpeciesName + "\" failed to eat a target (No head limb found)",
                        contentPackage: Character.Prefab.ContentPackage);
                    IgnoreTarget(SelectedAiTarget);
                    ReleaseEatingTarget();
                    ResetAITarget();
                    return;
                }
                Vector2 mouthPos = Character.AnimController.SimplePhysicsEnabled ? SimPosition : Character.AnimController.GetMouthPosition() ?? Vector2.Zero;
                Vector2 attackSimPosition = Character.GetRelativeSimPosition(SelectedAiTarget.Entity);
                Vector2 limbDiff = attackSimPosition - mouthPos;
                float extent = Math.Max(mouthLimb.body.GetMaxExtent(), 2);
                bool tooFar = Character.InWater ? limbDiff.LengthSquared() > extent * extent : limbDiff.X > extent;
                if (tooFar)
                {
                    steeringManager.SteeringSeek(attackSimPosition - (mouthPos - SimPosition), 2);
                    if (Character.InWater)
                    {
                        SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 15);
                    }
                }
                else
                {
                    if (SelectedAiTarget.Entity is Character targetCharacter)
                    {
                        Character.SelectCharacter(targetCharacter);
                    }
                    else if (SelectedAiTarget.Entity is Item item)
                    {
                        if (!item.Removed && item.body != null)
                        {
                            float itemBodyExtent = item.body.GetMaxExtent() * 2;
                            if (Math.Abs(limbDiff.X) < itemBodyExtent &&
                                Math.Abs(limbDiff.Y) < Character.AnimController.Collider.GetMaxExtent() + Character.AnimController.ColliderHeightFromFloor)
                            {
                                Vector2 velocity = limbDiff;
                                if (limbDiff.LengthSquared() > 0.01f) { velocity = Vector2.Normalize(velocity); }
                                item.body.LinearVelocity *= 0.9f;
                                item.body.LinearVelocity -= velocity * 0.25f;
                                bool wasBroken = item.Condition <= 0.0f;
                                item.LastEatenTime = (float)Timing.TotalTimeUnpaused;
                                item.AddDamage(Character,
                                    item.WorldPosition,
                                    new Attack(0.0f, 0.0f, 0.0f, 0.0f, 0.02f * Character.Params.EatingSpeed),
                                    impulseDirection: Vector2.Zero,
                                    deltaTime);
                                Character.ApplyStatusEffects(ActionType.OnEating, deltaTime);
                                if (item.Condition <= 0.0f)
                                {
                                    if (!wasBroken) { PetBehavior?.OnEat(item); }
                                    Entity.Spawner.AddItemToRemoveQueue(item);
                                }
                            }
                        }
                    }
                    steeringManager.SteeringManual(deltaTime, Vector2.Normalize(limbDiff) * 3);
                    Character.AnimController.Collider.ApplyForce(limbDiff * mouthLimb.Mass * 50.0f, mouthPos);
                }
            }
            else
            {
                IgnoreTarget(SelectedAiTarget);
                ReleaseEatingTarget();
                ResetAITarget();
            }
        }
        
        private void ReleaseEatingTarget()
        {
            State = AIState.Idle;
            Character.DeselectCharacter();
        }

        #endregion

        private void UpdateFollow(float deltaTime)
        {
            if (SelectedAiTarget == null || SelectedAiTarget.Entity == null || SelectedAiTarget.Entity.Removed)
            {
                State = AIState.Idle;
                return;
            }
            if (Character.CurrentHull != null && steeringManager == insideSteering)
            {
                // Inside, but not inside ruins
                if ((Character.AnimController.InWater || !Character.AnimController.CanWalk) &&
                    Character.Submarine != null && !Character.Submarine.Info.IsRuin &&
                    SelectedAiTarget.Entity is Character c && VisibleHulls.Contains(c.CurrentHull))
                {
                    // Steer towards the target if in the same room and swimming
                    SteeringManager.SteeringManual(deltaTime, Vector2.Normalize(SelectedAiTarget.Entity.WorldPosition - Character.WorldPosition));
                }
                else
                {
                    // Use path finding
                    PathSteering.SteeringSeek(Character.GetRelativeSimPosition(SelectedAiTarget.Entity), weight: 2, minGapWidth: minGapSize);
                }
            }
            else
            {
                // Outside
                SteeringManager.SteeringSeek(Character.GetRelativeSimPosition(SelectedAiTarget.Entity), 5);
            }
            if (steeringManager is IndoorsSteeringManager pathSteering)
            {
                if (!pathSteering.IsPathDirty && pathSteering.CurrentPath != null && pathSteering.CurrentPath.Unreachable)
                {
                    // Can't reach
                    State = AIState.Idle;
                    IgnoreTarget(SelectedAiTarget);
                }
            }
            else if (Character.AnimController.InWater)
            {
                SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 15);
            }
        }

        #region Targeting
        public static bool IsLatchedTo(Character target, Character character)
        {
            if (target.AIController is EnemyAIController enemyAI && enemyAI.LatchOntoAI != null)
            {
                return enemyAI.LatchOntoAI.IsAttached && enemyAI.LatchOntoAI.TargetCharacter == character;
            }
            return false;
        }

        public static bool IsLatchedToSomeoneElse(Character target, Character character)
        {
            if (target.AIController is EnemyAIController enemyAI && enemyAI.LatchOntoAI != null)
            {
                return enemyAI.LatchOntoAI.IsAttached && enemyAI.LatchOntoAI.TargetCharacter != null && enemyAI.LatchOntoAI.TargetCharacter != character;
            }
            return false;
        }

        private bool IsLatchedOnSub => LatchOntoAI != null && LatchOntoAI.IsAttachedToSub;

        //goes through all the AItargets, evaluates how preferable it is to attack the target,
        //whether the Character can see/hear the target and chooses the most preferable target within
        //sight/hearing range
        public void UpdateTargets()
        {
            targetValue = 0;
            AITarget newTarget = null;
            CharacterParams.TargetParams selectedTargetParams = null;
            AITargetMemory targetMemory = null;
            bool isAnyTargetClose = false;
            bool isBeingChased = IsBeingChased;
            const float priorityValueMaxModifier = 5;
            bool isCharacterInside = Character.CurrentHull != null;
            bool tryToGetInside =
                Character.AnimController.CanEnterSubmarine == CanEnterSubmarine.True ||
                //characters that are aggressive boarders can partially enter the sub can attempt to push through holes
                (Character.AnimController.CanEnterSubmarine == CanEnterSubmarine.Partial && IsAggressiveBoarder);
            
            foreach (AITarget aiTarget in AITarget.List)
            {
                if (aiTarget.ShouldBeIgnored()) { continue; }
                if (ignoredTargets.Contains(aiTarget)) { continue; }
                if (aiTarget.Type == AITarget.TargetType.HumanOnly) { continue; }
                if (!TargetOutposts && GameMain.GameSession?.GameMode is not TestGameMode)
                {
                    if (aiTarget.Entity.Submarine != null && aiTarget.Entity.Submarine.Info.IsOutpost) { continue; }
                }
                Character targetCharacter = aiTarget.Entity as Character;
                //ignore the aitarget if it is the Character itself
                if (targetCharacter == Character) { continue; }
                if (TargetingRestrictions.HasFlag(EnemyTargetingRestrictions.PlayerCharacters))
                {
                    if (targetCharacter is { IsPlayer: true }) { continue; }

                    // monsters can ignore a player character, but attack a diving suit equipped by the character
                    if (aiTarget.Entity is Item item && item.GetRootInventoryOwner() is Character { IsPlayer: true}) { continue; }
                }
                if (TargetingRestrictions.HasFlag(EnemyTargetingRestrictions.PlayerSubmarines))
                {
                    if (aiTarget.Entity.Submarine?.Info is { IsPlayer: true }) { continue; }
                }
                var targetingTags = GetTargetingTags(aiTarget);

                #region Filter out targets by entity type, based on contextual information.
                Door door = null;
                if (targetCharacter != null)
                {
                    // ignore if target is tagged to be explicitly ignored (Feign Death)
                    if (targetCharacter.HasAbilityFlag(AbilityFlags.IgnoredByEnemyAI)) { continue; }
                    if (AIParams.Targets.None() && Character.IsFriendly(targetCharacter))
                    {
                        continue;
                    }
                }
                else
                {
                    // Ignore all structures, items, and hulls inside these subs.
                    if (aiTarget.Entity.Submarine != null) 
                    {
                        if (aiTarget.Entity.Submarine.Info.IsWreck ||
                            aiTarget.Entity.Submarine.Info.IsBeacon ||
                            UnattackableSubmarines.Contains(aiTarget.Entity.Submarine))
                        {
                            continue;
                        }
                        //ignore the megaruin in end levels
                        if (aiTarget.Entity.Submarine.Info.OutpostGenerationParams != null &&
                            aiTarget.Entity.Submarine.Info.OutpostGenerationParams.ForceToEndLocationIndex > -1)
                        {
                            continue;
                        }
                    }
                    if (aiTarget.Entity is Hull hull)
                    {
                        // Ignore the target if it's a room and the character is already inside a sub
                        if (Character.CurrentHull != null) { continue; }
                        // Ignore ruins
                        if (hull.Submarine == null) { continue; }
                        if (hull.Submarine.Info.IsRuin) { continue; }
                    }
                    else if (aiTarget.Entity is Item item)
                    {
                        door = item.GetComponent<Door>();
                        bool targetingFromOutsideToInside = item.CurrentHull != null && !isCharacterInside;
                        if (targetingFromOutsideToInside)
                        {
                            if (door != null && (!canAttackDoors && !AIParams.CanOpenDoors) || !canAttackWalls)
                            {
                                // Can't reach
                                continue;
                            }
                        }
                        if (door == null && targetingFromOutsideToInside)
                        {
                            if (item.Submarine?.Info is { IsRuin: true })
                            {
                                // Ignore ruin items when the creature is outside.
                                continue;
                            }
                        }
                        else if (targetingTags.Contains(Tags.Nasonov))
                        {
                            if ((item.Submarine == null || !item.Submarine.Info.IsPlayer) && item.ParentInventory == null)
                            {
                                // Only target nasonovartifacts when they are held be a player or inside the playersub
                                continue;
                            }
                        }
                        // Ignore the target if it's a decoy and the character is already inside a sub
                        if (Character.CurrentHull != null && targetingTags.Contains(Tags.Decoy))
                        {
                            continue;
                        }
                    }
                    else if (aiTarget.Entity is Structure s)
                    {
                        if (!s.HasBody)
                        {
                            // Ignore structures that doesn't have a body (not walls)
                            continue;
                        }
                        if (s.IsPlatform) { continue; }
                        if (s.Submarine == null) { continue; }
                        if (s.Submarine.Info.IsRuin) { continue; }
                        bool isInnerWall = s.Prefab.Tags.Contains("inner");
                        if (isInnerWall && !isCharacterInside)
                        {
                            // Ignore inner walls when outside (walltargets still work)
                            continue;
                        }
                        if (!tryToGetInside && IsWallDisabled(s))
                        {
                            continue;
                        }
                    }
                    if (door != null)
                    {
                        if (door.Item.Submarine == null) { continue; }
                        bool isOutdoor = door.LinkedGap is { FlowTargetHull: not null, IsRoomToRoom: false };
                        // Ignore inner doors when outside
                        if (Character.CurrentHull == null && !isOutdoor) { continue; }
                        bool isOpen = door.CanBeTraversed;
                        if (!isOpen)
                        {
                            if (!canAttackDoors) { continue; }
                        }
                        else if (Character.AnimController.CanEnterSubmarine != CanEnterSubmarine.True)
                        {
                            // Ignore broken and open doors, if cannot enter submarine
                            // Also ignore them if the monster can only partially enter the sub:
                            // these monsters tend to be too large to get through doors anyway.
                            continue;
                        }
                    }
                    else if (aiTarget.Entity is IDamageable { Health: <= 0.0f })
                    {
                         continue;
                    }
                }
                #endregion

                #region Choose valid targeting params.
                if (targetingTags.None()) { continue; }
                CharacterParams.TargetParams matchingTargetParams = null;
                foreach (var targetParams in GetTargetParams(targetingTags))
                {
                    if (matchingTargetParams != null)
                    {
                        if (matchingTargetParams.Priority > targetParams.Priority)
                        {
                            // Valid higher priority params already found.
                            continue;
                        }
                    }
                    if (targetParams.IgnoreInside && Character.CurrentHull != null) { continue; }
                    if (targetParams.IgnoreOutside && Character.CurrentHull == null) { continue; }
                    if (targetParams.IgnoreIncapacitated && targetCharacter is { IsIncapacitated: true }) { continue; }
                    if (targetParams.IgnoreTargetInside && aiTarget.Entity.Submarine != null) { continue; }
                    if (targetParams.IgnoreTargetOutside && aiTarget.Entity.Submarine == null) { continue; }
                    if (aiTarget.Entity is ISerializableEntity se)
                    {
                        if (targetParams.Conditionals.Any(c => !c.TargetSelf && !c.Matches(se))) { continue; }
                    }
                    if (targetParams.Conditionals.Any(c => c.TargetSelf && !c.Matches(Character))) { continue; }
                    if (targetParams.IgnoreIfNotInSameSub)
                    {
                        if (aiTarget.Entity.Submarine != Character.Submarine) { continue; }
                        var targetHull = targetCharacter != null ? targetCharacter.CurrentHull : aiTarget.Entity is Item it ? it.CurrentHull : null;
                        if (targetHull == null != (Character.CurrentHull == null)) { continue; }
                    }
                    if (targetParams.State is AIState.Observe or AIState.Eat)
                    {
                        if (targetCharacter != null && targetCharacter.Submarine != Character.Submarine)
                        {
                            // Never allow observing or eating characters that are inside a different submarine / outside when we are inside.
                            continue;
                        }
                    }
                    if (aiTarget.Entity is Item targetItem)
                    {
                        if (targetParams.IgnoreContained && targetItem.ParentInventory != null) { continue; }
                        
                        switch (targetParams.State)
                        {
                            case AIState.FleeTo:
                            {
                                float healthThreshold = targetParams.Threshold;
                                if (targetParams.ThresholdMin > 0 && targetParams.ThresholdMax > 0)
                                {
                                    // If both min and max thresholds are defined, use ThresholdMax when currently fleeing to the target and ThresholdMin when doing something else.
                                    // This is used to make the fractal guardians target the guardian repair pods with a different health threshold: when they are already targeting the pod, they will use ThresholdMax to keep them generating until (nearly) full health.
                                    // And when the guardians are outside the pod, they use ThresholdMin, so that they don't target the pod before falling under certain level of health.
                                    healthThreshold = currentTargetingParams == targetParams && State == AIState.FleeTo ? targetParams.ThresholdMax : targetParams.ThresholdMin;
                                }
                                if (Character.HealthPercentage > healthThreshold)
                                {
                                    continue;
                                }
                                break;
                            }
                            case AIState.Attack or AIState.Aggressive:
                                if (!canAttackItems)
                                {
                                    continue;
                                }
                                break;
                        }
                        if (targetItem.HasTag(Tags.GuardianShelter))
                        {
                            // Ignore guardian pods completely, if they are targeted by someone else (is or will be occupied).  
                            bool ignore = false;
                            foreach (Character otherCharacter in Character.CharacterList)
                            {
                                if (otherCharacter == Character) { continue; }
                                if (otherCharacter.AIController?.SelectedAiTarget != aiTarget) { continue; }
                                if (!Character.IsFriendly(otherCharacter)) { continue; }
                                ignore = true;
                                break;
                            }
                            if (ignore) { continue; }
                        }
                    }
                    matchingTargetParams = targetParams;
                }
                if (matchingTargetParams == null) { continue; }
                #endregion
                
                #region Modify the priority dynamically
                float valueModifier = 1.0f;
                if (targetCharacter != null)
                {
                    if (targetCharacter.AIController is EnemyAIController)
                    {
                        if (matchingTargetParams.Tag == Tags.Stronger && State is AIState.Avoid or AIState.Escape or AIState.Flee)
                        {
                            if (SelectedAiTarget == aiTarget)
                            {
                                // Frightened -> hold on to the target
                                valueModifier *= 2;
                            }
                            if (IsBeingChasedBy(targetCharacter))
                            {
                                valueModifier *= 2;
                            }
                            if (Character.CurrentHull != null && !VisibleHulls.Contains(targetCharacter.CurrentHull))
                            {
                                // Inside but in a different room
                                valueModifier /= 2;
                            }
                        }
                    }
                }
                else
                {
                    if (aiTarget.Entity is Structure s)
                    {
                        bool isInnerWall = s.Prefab.Tags.Contains("inner");
                        // Prefer weaker walls (200 is the default for normal hull walls)
                        valueModifier = 200f / s.MaxHealth;
                        for (int i = 0; i < s.Sections.Length; i++)
                        {
                            var section = s.Sections[i];
                            if (section.gap == null) { continue; }
                            bool leadsInside = !section.gap.IsRoomToRoom && section.gap.FlowTargetHull != null;
                            if (tryToGetInside)
                            {
                                if (!isCharacterInside)
                                {
                                    if (CanPassThroughHole(s, i))
                                    {
                                        valueModifier *= leadsInside ? (IsAggressiveBoarder ? priorityValueMaxModifier : 1) : 0;
                                    }
                                    else if (IsAggressiveBoarder && leadsInside && canAttackWalls)
                                    {
                                        // Up to 100% priority increase for every gap in the wall when an aggressive boarder is outside
                                        valueModifier *= 1 + section.gap.Open;
                                    }
                                }
                                else
                                {
                                    // Inside
                                    if (IsAggressiveBoarder)
                                    {
                                        if (!isInnerWall)
                                        {
                                            // Only interested in getting inside (aggressive boarder) -> don't target outer walls when already inside
                                            valueModifier = 0;
                                            break;
                                        }
                                        else if (CanPassThroughHole(s, i))
                                        {
                                            valueModifier *= isInnerWall ? 0.5f : 0;
                                        }
                                        else if (!canAttackWalls)
                                        {
                                            valueModifier = 0;
                                            break;
                                        }
                                        else
                                        {
                                            valueModifier = 0.1f;
                                        }
                                    }
                                    else
                                    {
                                        if (!canAttackWalls)
                                        {
                                            valueModifier = 0;
                                            break;
                                        }
                                        // We are actually interested in breaking things -> reduce the priority when the wall is already broken
                                        // (Terminalcells)
                                        valueModifier *= 1 - section.gap.Open * 0.25f;
                                        valueModifier = Math.Max(valueModifier, 0.1f);
                                    }
                                }
                            }
                            else
                            {
                                // Cannot enter
                                if (isInnerWall || !canAttackWalls)
                                {
                                    // Ignore inner walls and all walls if cannot do damage on walls.
                                    valueModifier = 0;
                                    break;
                                }
                                else if (IsAggressiveBoarder)
                                {
                                    // Up to 100% priority increase for every gap in the wall when an aggressive boarder is outside
                                    // (Bonethreshers)
                                    valueModifier *= 1 + section.gap.Open;
                                }
                            }
                            valueModifier = Math.Clamp(valueModifier, 0, priorityValueMaxModifier);
                        }
                    }
                    if (door != null)
                    {
                        if (IsAggressiveBoarder)
                        {
                            if (Character.CurrentHull == null)
                            {
                                // Increase the priority if the character is outside and the door is from outside to inside
                                if (door.CanBeTraversed)
                                {
                                    valueModifier = priorityValueMaxModifier;
                                }
                                else if (door.LinkedGap != null)
                                {
                                    valueModifier = 1 + door.LinkedGap.Open * (priorityValueMaxModifier - 1);
                                }
                            }
                            else
                            {
                                // Inside -> ignore open doors and outer doors
                                bool isOpen = door.CanBeTraversed;
                                bool isOutdoor = door.LinkedGap is { FlowTargetHull: not null, IsRoomToRoom: false };
                                valueModifier = isOpen || isOutdoor ? 0 : 1;
                            }
                        }
                    }
                    else if (aiTarget.Entity is IDamageable { Health: <= 0.0f })
                    {
                         continue;
                    }
                }
                //no need to eat if the character is already in full health (except if it's a pet - pets actually need to eat to stay alive, not just to regain health)
                if (matchingTargetParams.State == AIState.Eat && Character.Params.Health.HealthRegenerationWhenEating > 0 && !Character.IsPet)
                {
                    valueModifier *= MathHelper.Lerp(1f, 0.1f, Character.HealthPercentage / 100f);
                }
                valueModifier *= matchingTargetParams.Priority;
                if (valueModifier == 0.0f) { continue; }
                if (matchingTargetParams.Tag != Tags.Decoy)
                {
                    if (SwarmBehavior != null && SwarmBehavior.Members.Any())
                    {
                        // Halve the priority for each swarm mate targeting the same target -> reduces stacking
                        foreach (AICharacter otherCharacter in SwarmBehavior.Members)
                        {
                            if (otherCharacter == Character) { continue; }
                            if (otherCharacter.AIController?.SelectedAiTarget != aiTarget) { continue; }
                            valueModifier /= 2;
                        }
                    }
                    else
                    {
                        // The same as above, but using all the friendly characters in the level.
                        foreach (Character otherCharacter in Character.CharacterList)
                        {
                            if (otherCharacter == Character) { continue; }
                            if (otherCharacter.AIController?.SelectedAiTarget != aiTarget) { continue; }
                            if (!Character.IsFriendly(otherCharacter)) { continue; }
                            valueModifier /= 2;
                        }
                    }
                }
                #endregion
                
                if (!aiTarget.IsWithinSector(WorldPosition)) { continue; }
                Vector2 toTarget = aiTarget.WorldPosition - Character.WorldPosition;
                float dist = toTarget.Length();
                float nonModifiedDist = dist;
                //if the target has been within range earlier, the character will notice it more easily
                if (targetMemories.ContainsKey(aiTarget))
                {
                    dist *= 0.9f;
                }
                if (matchingTargetParams.PerceptionDistanceMultiplier > 0.0f)
                {
                    dist /= matchingTargetParams.PerceptionDistanceMultiplier;
                }

                if (matchingTargetParams.MaxPerceptionDistance > 0.0f && 
                    dist * dist > matchingTargetParams.MaxPerceptionDistance * matchingTargetParams.MaxPerceptionDistance)
                {
                    continue;
                }
                
                if (State is AIState.PlayDead && targetCharacter == null)
                {
                    // Only react to characters, when playing dead.
                    continue;
                }
                else if (!CanPerceive(aiTarget, dist, checkVisibility: SelectedAiTarget != aiTarget || State is AIState.PlayDead or AIState.Hiding))
                {
                    continue;
                }

                if (SelectedAiTarget == aiTarget)
                {
                    if (Character.Submarine == null && aiTarget.Entity is ISpatialEntity { Submarine: not null } spatialEntity)
                    {
                        if (matchingTargetParams.Tag == Tags.Door || matchingTargetParams.Tag == Tags.Wall)
                        {
                            Vector2 rayStart = Character.SimPosition;
                            Vector2 rayEnd = aiTarget.SimPosition + spatialEntity.Submarine.SimPosition;
                            Body closestBody = Submarine.PickBody(rayStart, rayEnd, collisionCategory: Physics.CollisionWall | Physics.CollisionLevel, allowInsideFixture: true);
                            if (closestBody is { UserData: ISpatialEntity hit })
                            {
                                Vector2 hitPos = hit.SimPosition;
                                if (closestBody.UserData is Submarine)
                                {
                                    hitPos = Submarine.LastPickedPosition;
                                }
                                else if (hit.Submarine != null)
                                {
                                    hitPos += hit.Submarine.SimPosition;
                                }
                                float subHalfWidth = spatialEntity.Submarine.Borders.Width / 2f;
                                float subHalfHeight = spatialEntity.Submarine.Borders.Height / 2f;
                                Vector2 diff = ConvertUnits.ToDisplayUnits(rayEnd - hitPos);
                                bool isOtherSideOfTheSub = Math.Abs(diff.X) > subHalfWidth || Math.Abs(diff.Y) > subHalfHeight;
                                if (isOtherSideOfTheSub)
                                {
                                    IgnoreTarget(aiTarget);
                                    ResetAITarget();
                                    continue;
                                }
                            }
                        }
                    }
                    // Stick to the current target
                    valueModifier *= 1.1f;
                }
                if (!isBeingChased)
                {
                    if (matchingTargetParams.State is AIState.Avoid or AIState.PassiveAggressive or AIState.Aggressive)
                    {
                        float reactDistance = matchingTargetParams.ReactDistance;
                        if (reactDistance > 0 && reactDistance < dist)
                        {
                            // The target is too far and should be ignored.
                            continue;
                        }
                    }
                }

                //if the target is very close, the distance doesn't make much difference 
                // -> just ignore the distance and target whatever has the highest priority
                dist = Math.Max(dist, 100.0f);
                targetMemory = GetTargetMemory(aiTarget, addIfNotFound: true, keepAlive: SelectedAiTarget != aiTarget);
                if (Character.Submarine != null && !Character.Submarine.Info.IsRuin && Character.CurrentHull != null)
                {
                    float diff = Math.Abs(toTarget.Y) - Character.CurrentHull.Size.Y;
                    if (diff > 0)
                    {
                        // Inside the sub, treat objects that are up or down, as they were farther away.
                        dist *= MathHelper.Clamp(diff / 100, 2, 3);
                    }
                }

                if (Character.Submarine == null && aiTarget.Entity?.Submarine != null && targetCharacter == null)
                {
                    if (matchingTargetParams.PrioritizeSubCenter || matchingTargetParams.AttackPattern is AttackPattern.Circle or AttackPattern.Sweep)
                    {
                        if (!isAnyTargetClose)
                        {
                            if (Submarine.MainSubs.Contains(aiTarget.Entity.Submarine))
                            {
                                // Prioritize targets that are near the horizontal center of the sub, but only when none of the targets is reachable.
                                float horizontalDistanceToSubCenter = Math.Abs(aiTarget.WorldPosition.X - aiTarget.Entity.Submarine.WorldPosition.X);
                                dist *= MathHelper.Lerp(1f, 5f, MathUtils.InverseLerp(0, 10000, horizontalDistanceToSubCenter));
                            }
                            else if (matchingTargetParams.AttackPattern == AttackPattern.Circle)
                            {
                                dist *= 5;
                            }
                        }
                    }
                }

                if (targetCharacter != null && Character.CurrentHull != null && Character.CurrentHull == targetCharacter.CurrentHull)
                {
                    // In the same room with the target character
                    dist /= 2;
                }

                // Don't target characters that are outside of the allowed zone, unless chasing or escaping.
                switch (matchingTargetParams.State)
                {
                    case AIState.Escape:
                    case AIState.Avoid:
                        break;
                    default:
                        if (matchingTargetParams.State == AIState.Attack)
                        {
                            // In the attack state allow going into non-allowed zone only when chasing a target.
                            if (State == matchingTargetParams.State && SelectedAiTarget == aiTarget) { break; }
                        }
                        bool insideSameSub = aiTarget?.Entity?.Submarine != null && aiTarget.Entity.Submarine == Character.Submarine;
                        if (!insideSameSub && !IsPositionInsideAllowedZone(aiTarget.WorldPosition, out _))
                        {
                            // If we have recently been damaged by the target (or another player/bot in the same team) allow targeting it even when we are in the idle state.
                            bool isTargetInPlayerTeam = IsTargetInPlayerTeam(aiTarget);
                            if (Character.LastAttackers.None(a => a.Damage > 0 && a.Character != null && (a.Character == aiTarget.Entity || a.Character.IsOnPlayerTeam && isTargetInPlayerTeam)))
                            {
                                continue;
                            }
                        }
                        break;
                }

                valueModifier *= 
                    targetMemory.Priority / 
                    //sqrt = the further the target is, the less the distance matters
                    MathF.Sqrt(dist);

                if (valueModifier > targetValue)
                {
                    if (aiTarget.Entity is Item i)
                    {
                        Character owner = GetOwner(i);
                        if (owner == Character) { continue; }
                        if (owner != null)
                        {
                            if (owner.AiTarget != null && ignoredTargets.Contains(owner.AiTarget)) { continue; }
                            if (Character.IsFriendly(owner))
                            {
                                // Don't target items that we own. This is a rare case, and almost entirely related to Humanhusks (in the vanilla game).
                                continue;
                            }
                            if (owner.HasAbilityFlag(AbilityFlags.IgnoredByEnemyAI))
                            {
                                // ignore if owner is tagged to be explicitly ignored (Feign Death)
                                continue;
                            }
                            // if the enemy is configured to ignore the target character, ignore the provocative item they're holding/wearing too
                            if (GetTargetParams(GetTargetingTags(owner.AiTarget)).Any(t => t.State == AIState.Idle)) { continue; }
                        }
                    }
                    if (targetCharacter != null)
                    {
                        if (Character.CurrentHull != null && targetCharacter.CurrentHull != Character.CurrentHull)
                        {
                            if (matchingTargetParams.State is AIState.Observe or AIState.Eat || 
                                (matchingTargetParams.State is AIState.Follow or AIState.Protect && (!Character.CanClimb || !Character.CanInteract || !AIParams.CanOpenDoors || !Character.Params.UsePathFinding)))
                            {
                                if (!VisibleHulls.Contains(targetCharacter.CurrentHull))
                                {
                                    // Probably can't get to the target -> ignore.
                                    continue;
                                }
                            }
                        }
                        if (targetCharacter.Submarine != Character.Submarine || (targetCharacter.CurrentHull == null) != (Character.CurrentHull == null))
                        {
                            if (targetCharacter.Submarine != null)
                            {
                                // Target is inside -> reduce the priority
                                valueModifier *= 0.5f;
                                if (Character.Submarine != null)
                                {
                                    // Both inside different submarines -> can ignore safely
                                    continue;
                                }
                            }
                            else if (Character.CurrentHull != null)
                            {
                                // Target outside, but we are inside -> Ignore the target but allow to keep target that is currently selected.
                                if (SelectedAiTarget?.Entity != targetCharacter)
                                {
                                    continue;
                                }
                            }
                        }
                        else if (targetCharacter.Submarine == null && Character.Submarine == null)
                        {
                            // Ignore the target when it's far enough and blocked by the level geometry, because the steering avoidance probably can't get us to the target.
                            if (dist > Math.Clamp(ConvertUnits.ToDisplayUnits(colliderLength) * 10, 1000, 5000))
                            {
                                if (Submarine.PickBodies(SimPosition, targetCharacter.SimPosition, collisionCategory: Physics.CollisionLevel).Any())
                                {
                                    continue;
                                }
                            }
                        }
                    }
                    newTarget = aiTarget;
                    selectedTargetParams = matchingTargetParams;
                    targetValue = valueModifier;
                    if (!isAnyTargetClose)
                    {
                        isAnyTargetClose = ConvertUnits.ToDisplayUnits(colliderLength) > nonModifiedDist;
                    }
                }
            }
            
            currentTargetingParams = selectedTargetParams;
            currentTargetMemory = targetMemory;
            State = currentTargetingParams?.State ?? AIState.Idle;
            SelectedAiTarget = newTarget;
            if ((LatchOntoAI is not { IsAttached: true } || wallTarget != null) && State is AIState.Attack or AIState.Aggressive or AIState.PassiveAggressive)
            {
                UpdateWallTarget(requiredHoleCount);
            }
            updateTargetsTimer = updateTargetsInterval * Rand.Range(0.75f, 1.25f);
        }

        class WallTarget
        {
            public Vector2 Position;
            public Structure Structure;
            public int SectionIndex;

            public WallTarget(Vector2 position, Structure structure = null, int sectionIndex = -1)
            {
                Position = position;
                Structure = structure;
                SectionIndex = sectionIndex;
            }
        }

        private WallTarget wallTarget;
        private readonly List<(Body, int, Vector2)> wallHits = new List<(Body, int, Vector2)>(3);
        private void UpdateWallTarget(int requiredHoleCount)
        {
            wallTarget = null;
            if (SelectedAiTarget == null) { return; }
            if (SelectedAiTarget.Entity == null) { return; }
            if (!canAttackWalls) { return; }
            if (HasValidPath(requireNonDirty: true)) { return; }
            wallHits.Clear();
            Structure wall = null;
            Vector2 refPos = AttackLimb != null ? AttackLimb.SimPosition : SimPosition;
            if (AIParams.WallTargetingMethod.HasFlag(WallTargetingMethod.Target))
            {
                Vector2 rayStart = refPos;
                Vector2 rayEnd = SelectedAiTarget.SimPosition;
                if (SelectedAiTarget.Entity.Submarine != null && Character.Submarine == null)
                {
                    rayStart -= SelectedAiTarget.Entity.Submarine.SimPosition;
                }
                else if (SelectedAiTarget.Entity.Submarine == null && Character.Submarine != null)
                {
                    rayEnd -= Character.Submarine.SimPosition;
                }
                DoRayCast(rayStart, rayEnd);
            }
            if (AIParams.WallTargetingMethod.HasFlag(WallTargetingMethod.Heading))
            {
                Vector2 rayStart = refPos;
                Vector2 rayEnd = rayStart + VectorExtensions.Forward(Character.AnimController.Collider.Rotation + MathHelper.PiOver2, avoidLookAheadDistance * 5);
                if (SelectedAiTarget.Entity.Submarine != null && Character.Submarine == null)
                {
                    rayStart -= SelectedAiTarget.Entity.Submarine.SimPosition;
                    rayEnd -= SelectedAiTarget.Entity.Submarine.SimPosition;
                }
                else if (SelectedAiTarget.Entity.Submarine == null && Character.Submarine != null)
                {
                    rayStart -= Character.Submarine.SimPosition;
                    rayEnd -= Character.Submarine.SimPosition;
                }
                DoRayCast(rayStart, rayEnd);
            }
            if (AIParams.WallTargetingMethod.HasFlag(WallTargetingMethod.Steering))
            {
                Vector2 rayStart = refPos;
                Vector2 rayEnd = rayStart + Steering * 5;
                if (SelectedAiTarget.Entity.Submarine != null && Character.Submarine == null)
                {
                    rayStart -= SelectedAiTarget.Entity.Submarine.SimPosition;
                    rayEnd -= SelectedAiTarget.Entity.Submarine.SimPosition;
                }
                else if (SelectedAiTarget.Entity.Submarine == null && Character.Submarine != null)
                {
                    rayStart -= Character.Submarine.SimPosition;
                    rayEnd -= Character.Submarine.SimPosition;
                }
                DoRayCast(rayStart, rayEnd);
            }
            if (wallHits.Any())
            {
                Vector2 targetdiff = ConvertUnits.ToSimUnits(SelectedAiTarget.WorldPosition - (AttackLimb != null ? AttackLimb.WorldPosition : WorldPosition));
                float targetDistance = targetdiff.LengthSquared();
                Body closestBody = null;
                float closestDistance = 0;
                int sectionIndex = -1;
                Vector2 sectionPos = Vector2.Zero;
                foreach ((Body body, int index, Vector2 sectionPosition) in wallHits)
                {
                    Structure structure = body.UserData as Structure;
                    float distance = Vector2.DistanceSquared(
                        refPos, 
                        Submarine.GetRelativeSimPosition(ConvertUnits.ToSimUnits(sectionPosition), Character.Submarine, structure.Submarine));
                    //if the wall is further than the target (e.g. at the other side of the sub?), we shouldn't be targeting it
                    if (distance > targetDistance) { continue; }
                    if (closestBody == null || closestDistance == 0 || distance < closestDistance)
                    {
                        closestBody = body;
                        closestDistance = distance;
                        wall = structure;
                        sectionPos = sectionPosition;
                        sectionIndex = index;
                    }
                }
                if (closestBody == null || sectionIndex == -1) { return; }
                Vector2 attachTargetNormal;
                if (wall.IsHorizontal)
                {
                    attachTargetNormal = new Vector2(0.0f, Math.Sign(WorldPosition.Y - wall.WorldPosition.Y));
                    sectionPos.Y += (wall.BodyHeight <= 0.0f ? wall.Rect.Height : wall.BodyHeight) / 2 * attachTargetNormal.Y;
                }
                else
                {
                    attachTargetNormal = new Vector2(Math.Sign(WorldPosition.X - wall.WorldPosition.X), 0.0f);
                    sectionPos.X += (wall.BodyWidth <= 0.0f ? wall.Rect.Width : wall.BodyWidth) / 2 * attachTargetNormal.X;
                }
                LatchOntoAI?.SetAttachTarget(wall, ConvertUnits.ToSimUnits(sectionPos), attachTargetNormal);
                if (Character.AnimController.CanEnterSubmarine == CanEnterSubmarine.True || 
                    !wall.SectionBodyDisabled(sectionIndex) && !IsWallDisabled(wall))
                {
                    if (wall.NoAITarget && Character.AnimController.CanEnterSubmarine == CanEnterSubmarine.True)
                    {
                        bool isTargetingDoor = SelectedAiTarget.Entity is Item i && i.GetComponent<Door>() != null;
                        // Blocked by a wall that shouldn't be targeted. The main intention here is to prevent monsters from entering the the tail and the nose pieces.
                        if (!isTargetingDoor)
                        {
                            //TODO: this might cause problems: many wall pieces (like smaller shuttle pieces
                            //and small decorative wall structures are currently marked as having no AI target,
                            //which can mean a monster very frequently ignores targets inside because they're blocked by those structures
                            IgnoreTarget(SelectedAiTarget);
                            ResetAITarget();
                        }
                    }
                    else
                    {
                        wallTarget = new WallTarget(sectionPos, wall, sectionIndex);
                    }
                }
                else
                {
                    // Blocked by a disabled wall.
                    IgnoreTarget(SelectedAiTarget);
                    ResetAITarget();
                }
            }

            void DoRayCast(Vector2 rayStart, Vector2 rayEnd)
            {
                Body hitTarget = Submarine.CheckVisibility(rayStart, rayEnd, ignoreSubs: true, 
                    ignoreSensors: CanEnterSubmarine != CanEnterSubmarine.False, 
                    ignoreDisabledWalls: CanEnterSubmarine != CanEnterSubmarine.False);
                if (hitTarget != null && IsValid(hitTarget, out wall))
                {
                    int sectionIndex = wall.FindSectionIndex(ConvertUnits.ToDisplayUnits(Submarine.LastPickedPosition));
                    if (sectionIndex >= 0)
                    {
                        wallHits.Add((hitTarget, sectionIndex, GetSectionPosition(wall, sectionIndex)));
                    }
                }
            }

            Vector2 GetSectionPosition(Structure wall, int sectionIndex)
            {
                float sectionDamage = wall.SectionDamage(sectionIndex);
                for (int i = sectionIndex - 2; i <= sectionIndex + 2; i++)
                {
                    if (wall.SectionBodyDisabled(i))
                    {
                        if (Character.AnimController.CanEnterSubmarine != CanEnterSubmarine.False && 
                            CanPassThroughHole(wall, i, requiredHoleCount))
                        {
                            sectionIndex = i;
                            break;
                        }
                        else
                        {
                            // Ignore and keep breaking other sections
                            continue;
                        }
                    }
                    if (wall.SectionDamage(i) > sectionDamage)
                    {
                        sectionIndex = i;
                    }
                }
                return wall.SectionPosition(sectionIndex, world: false);
            }

            bool IsValid(Body hit, out Structure wall)
            {
                wall = null;
                if (Submarine.LastPickedFraction == 1.0f) { return false; }
                if (hit.UserData is not Structure w) { return false; }
                if (w.Submarine == null) { return false; }
                if (w.Submarine != SelectedAiTarget.Entity.Submarine) { return false; }
                if (Character.Submarine == null)
                {
                    if (w.Prefab.Tags.Contains("inner"))
                    {
                        if (Character.AnimController.CanEnterSubmarine == CanEnterSubmarine.False) { return false; }
                    }
                    else if (!AIParams.TargetOuterWalls)
                    {
                        return false;
                    }
                }
                wall = w;
                return true;
            }
        }

        private bool TrySteerThroughGaps(float deltaTime)
        {
            if (wallTarget != null && wallTarget.SectionIndex > -1 && CanPassThroughHole(wallTarget.Structure, wallTarget.SectionIndex, requiredHoleCount))
            {
                WallSection section = wallTarget.Structure.GetSection(wallTarget.SectionIndex);
                Vector2 targetPos = wallTarget.Structure.SectionPosition(wallTarget.SectionIndex, world: true);
                return section?.gap != null && SteerThroughGap(wallTarget.Structure, section, targetPos, deltaTime);
            }
            else if (SelectedAiTarget != null)
            {
                if (SelectedAiTarget.Entity is Structure wall)
                {
                    for (int i = 0; i < wall.Sections.Length; i++)
                    {
                        WallSection section = wall.Sections[i];
                        if (CanPassThroughHole(wall, i, requiredHoleCount) && section?.gap != null)
                        {
                            return SteerThroughGap(wall, section, wall.SectionPosition(i, true), deltaTime);
                        }
                    }
                }
                else if (SelectedAiTarget.Entity is Item i)
                {
                    var door = i.GetComponent<Door>();
                    // Don't try to enter dry hulls if cannot walk or if the gap is too narrow
                    if (door?.LinkedGap?.FlowTargetHull != null && !door.LinkedGap.IsRoomToRoom && door.CanBeTraversed)
                    {
                        if (Character.AnimController.CanWalk || door.LinkedGap.FlowTargetHull.WaterPercentage > 25)
                        {
                            if (door.LinkedGap.Size > ConvertUnits.ToDisplayUnits(colliderWidth))
                            {
                                float maxDistance = Math.Max(ConvertUnits.ToDisplayUnits(colliderLength), 100);
                                return SteerThroughGap(door.LinkedGap, door.LinkedGap.FlowTargetHull.WorldPosition, deltaTime, maxDistance: maxDistance);
                            }
                        }
                    }
                }
            }
            return false;
        }

        private AITargetMemory GetTargetMemory(AITarget target, bool addIfNotFound = false, bool keepAlive = false)
        {
            if (!targetMemories.TryGetValue(target, out AITargetMemory memory))
            {
                if (addIfNotFound)
                {
                    memory = new AITargetMemory(target, minPriority);
                    targetMemories.Add(target, memory);
                }
            }
            if (keepAlive)
            {
                memory.Priority = Math.Max(memory.Priority, minPriority);
            }
            return memory;
        }

        private void UpdateCurrentMemoryLocation()
        {
            if (_selectedAiTarget != null)
            {
                if (_selectedAiTarget.Entity == null || _selectedAiTarget.Entity.Removed)
                {
                    _selectedAiTarget = null;
                }
                else if (CanPerceive(_selectedAiTarget, checkVisibility: false))
                {
                    var memory = GetTargetMemory(_selectedAiTarget);
                    if (memory != null)
                    {
                        memory.Location = _selectedAiTarget.WorldPosition;
                    }
                }
            }
        }

        private readonly List<AITarget> removals = new List<AITarget>();
        private void FadeMemories(float deltaTime)
        {
            removals.Clear();
            foreach (var kvp in targetMemories)
            {
                var target = kvp.Key;
                var memory = kvp.Value;
                // Slowly decrease all memories
                float fadeTime = memoryFadeTime;
                if (target == SelectedAiTarget)
                {
                    // Don't decrease the current memory
                    fadeTime = 0;
                }
                else if (target == _lastAiTarget)
                {
                    // Halve the latest memory fading. 
                    fadeTime /= 2;
                }
                memory.Priority -= fadeTime * deltaTime;
                // Remove targets that have no priority or have been removed
                if (memory.Priority <= 1 || target.Entity == null || target.Entity.Removed || !AITarget.List.Contains(target))
                {
                    removals.Add(target);
                }
            }
            removals.ForEach(r => targetMemories.Remove(r));
        }

        private readonly float targetIgnoreTime = 10;
        private float targetIgnoreTimer;
        private readonly HashSet<AITarget> ignoredTargets = new HashSet<AITarget>();
        public void IgnoreTarget(AITarget target)
        {
            if (target == null) { return; }
            ignoredTargets.Add(target);
            targetIgnoreTimer = targetIgnoreTime * Rand.Range(0.75f, 1.25f);
        }
        #endregion

        #region State switching
        /// <summary>
        /// How long do we hold on to the current state after losing a target before we reset back to the original state.
        /// In other words, how long do we have to idle before the original state is restored.
        /// </summary>
        private readonly float stateResetCooldown = 10;
        private float stateResetTimer;
        private bool isStateChanged;
        private readonly Dictionary<StatusEffect.AITrigger, CharacterParams.TargetParams> activeTriggers = new Dictionary<StatusEffect.AITrigger, CharacterParams.TargetParams>();
        private readonly HashSet<StatusEffect.AITrigger> inactiveTriggers = new HashSet<StatusEffect.AITrigger>();

        public void LaunchTrigger(StatusEffect.AITrigger trigger)
        {
            if (trigger.IsTriggered) { return; }
            if (activeTriggers.ContainsKey(trigger)) { return; }
            if (activeTriggers.ContainsValue(currentTargetingParams))
            {
                if (!trigger.AllowToOverride) { return; }
                var existingTrigger = activeTriggers.FirstOrDefault(kvp => kvp.Value == currentTargetingParams && kvp.Key.AllowToBeOverridden);
                if (existingTrigger.Key == null) { return; }
                activeTriggers.Remove(existingTrigger.Key);
            }
            trigger.Launch();
            activeTriggers.Add(trigger, currentTargetingParams);
            ChangeParams(currentTargetingParams, trigger.State);
        }

        private void UpdateTriggers(float deltaTime)
        {
            foreach (var triggerObject in activeTriggers)
            {
                StatusEffect.AITrigger trigger = triggerObject.Key;
                if (trigger.IsPermanent) { continue; }
                trigger.UpdateTimer(deltaTime);
                if (!trigger.IsActive)
                {
                    trigger.Reset();
                    ResetParams(triggerObject.Value);
                    inactiveTriggers.Add(trigger);
                }
            }
            foreach (StatusEffect.AITrigger trigger in inactiveTriggers)
            {
                activeTriggers.Remove(trigger);
            }
            inactiveTriggers.Clear();
        }

        /// <summary>
        /// Resets the target's state to the original value defined in the xml.
        /// </summary>
        private bool TryResetOriginalState(Identifier tag)
        {
            if (!modifiedParams.ContainsKey(tag)) { return false; }
            if (AIParams.TryGetTargets(tag, out IEnumerable<CharacterParams.TargetParams> matchingParams))
            {
                foreach (var targetParams in matchingParams)
                {
                    modifiedParams.Remove(tag);
                    if (tempParams.ContainsKey(tag))
                    {
                        tempParams.Values.ForEach(t => AIParams.RemoveTarget(t));
                        tempParams.Remove(tag);
                    }
                    ResetParams(targetParams);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Parameters originally defined in the AI params and modified temporarily.
        /// </summary>
        private readonly Dictionary<Identifier, IEnumerable<CharacterParams.TargetParams>> modifiedParams = new Dictionary<Identifier, IEnumerable<CharacterParams.TargetParams>>();
        /// <summary>
        /// Parameters created temporarily. Not originally defined in the AI params at all.
        /// </summary>
        private readonly Dictionary<Identifier, CharacterParams.TargetParams> tempParams = new Dictionary<Identifier, CharacterParams.TargetParams>();
        private readonly List<CharacterParams.TargetParams> tempParamsList = new List<CharacterParams.TargetParams>();

        private void ChangeParams(CharacterParams.TargetParams targetParams, AIState state, float? priority = null)
        {
            if (targetParams == null) { return; }
            if (priority.HasValue)
            {
                targetParams.Priority = priority.Value;
            }
            targetParams.State = state;
        }

        private void ResetParams(CharacterParams.TargetParams targetParams)
        {
            targetParams?.Reset();
            if (currentTargetingParams == targetParams || State is AIState.Idle or AIState.Patrol)
            {
                ResetAITarget();
                State = AIState.Idle;
                PreviousState = AIState.Idle;
            }
        }
        
        private void ChangeParams(Identifier tag, AIState state, float? priority = null, bool onlyExisting = false, bool ignoreAttacksIfNotInSameSub = false)
        {
            var existingTargetParams = GetTargetParams(tag);
            if (existingTargetParams.None())
            {
                if (!onlyExisting && !tempParams.ContainsKey(tag))
                {
                    if (AIParams.TryAddNewTarget(tag, state, priority ?? minPriority, out CharacterParams.TargetParams targetParams))
                    {
                        tempParams.Add(tag, targetParams);
                    }
                }
            }
            else
            {
                foreach (var targetParams in existingTargetParams)
                {
                    if (priority.HasValue)
                    {
                        targetParams.Priority = Math.Max(targetParams.Priority, priority.Value);
                    }
                    targetParams.State = state;
                    if (state == AIState.Attack)
                    {
                        targetParams.IgnoreIfNotInSameSub = ignoreAttacksIfNotInSameSub;
                        targetParams.IgnoreInside = false;
                        targetParams.IgnoreOutside = false;
                        targetParams.IgnoreTargetInside = false;
                        targetParams.IgnoreTargetOutside = false;
                        targetParams.IgnoreIncapacitated = false;
                    }
                }
                modifiedParams.TryAdd(tag, existingTargetParams);
            }
        }

        private void ChangeTargetState(Identifier tag, AIState state, float? priority = null)
        {
            isStateChanged = true;
            SetStateResetTimer();
            ChangeParams(tag, state, priority);
        }

        /// <summary>
        /// Temporarily changes the predefined state for a target. Eg. Idle -> Attack.
        /// Note: does not change the current AIState!
        /// </summary>
        private void ChangeTargetState(Character target, AIState state, float? priority = null)
        {
            isStateChanged = true;
            SetStateResetTimer();
            if (!Character.IsPet || !target.IsHuman)
            {
                //don't turn pets hostile to all humans when attacked by one
                ChangeParams(target.SpeciesName, state, priority, ignoreAttacksIfNotInSameSub: !target.IsHuman);
            }
            if (target.IsHuman)
            {
                if (AIParams.TryGetHighestPriorityTarget(Tags.Human, out CharacterParams.TargetParams targetParams))
                {
                    priority = targetParams.Priority;
                }
                // Target also items, because if we are blind and the target doesn't move, we can only perceive the target when it uses items
                if (state is AIState.Attack or AIState.Escape)
                {
                    ChangeParams(Tags.Weapon, state, priority);
                    ChangeParams(Tags.ToolItem, state, priority);
                }
                if (state == AIState.Attack)
                {
                    // If the target is shooting from the submarine, we might not perceive it because it doesn't move.
                    // --> Target the submarine too.
                    if (target.Submarine != null && Character.Submarine == null && (canAttackDoors || canAttackWalls))
                    {
                        ChangeParams(Tags.Room, state, priority / 2);
                        if (canAttackWalls)
                        {
                            ChangeParams(Tags.Wall, state, priority / 2);
                        }
                        if (canAttackDoors && IsAggressiveBoarder)
                        {
                            ChangeParams(Tags.Door, state, priority / 2);
                        }
                    }
                    ChangeParams(Tags.Provocative, state, priority, onlyExisting: true);
                }
            }
        }

        private void ResetOriginalState()
        {
            isStateChanged = false;
            modifiedParams.Keys.ForEachMod(tag => TryResetOriginalState(tag));
        }
        #endregion

        protected override void OnTargetChanged(AITarget previousTarget, AITarget newTarget)
        {
            base.OnTargetChanged(previousTarget, newTarget);
            if ((newTarget != null || wallTarget != null) && IsLatchedOnSub)
            {
                if (newTarget?.Entity is not Structure wall)
                {
                    wall = wallTarget?.Structure;
                }
                // The target is not a wall or it's not the same as we are attached to -> release
                bool releaseTarget = wall?.Bodies == null || (!wall.Bodies.Contains(LatchOntoAI.AttachJoints[0].BodyB) && wall.Submarine?.PhysicsBody?.FarseerBody != LatchOntoAI.AttachJoints[0].BodyB);
                if (!releaseTarget)
                {
                    for (int i = 0; i < wall.Sections.Length; i++)
                    {
                        if (CanPassThroughHole(wall, i))
                        {
                            releaseTarget = true;
                        }
                    }
                }
                if (releaseTarget)
                {
                    wallTarget = null;
                    LatchOntoAI.DeattachFromBody(reset: true, cooldown: 1);
                }
            }
            else
            {
                wallTarget = null;
            }
            if (newTarget == null) { return; }
            if (currentTargetingParams != null)
            {
                observeTimer = currentTargetingParams.Timer * Rand.Range(0.75f, 1.25f);   
            }
            reachTimer = 0;
            sinTime = 0;
            if (breakCircling && strikeTimer <= 0 && CirclePhase != CirclePhase.CloseIn)
            {
                CirclePhase = CirclePhase.Start;
            }
        }

        protected override void OnStateChanged(AIState from, AIState to)
        {
            LatchOntoAI?.DeattachFromBody(reset: true);
            if (disableTailCoroutine != null)
            {
                bool isInTransition = from is AIState.HideTo or AIState.Hiding && to is AIState.HideTo or AIState.Hiding;
                if (!isInTransition)
                {
                    CoroutineManager.StopCoroutines(disableTailCoroutine);
                    Character.AnimController.RestoreTemporarilyDisabled();
                    disableTailCoroutine = null;   
                }
            }
            if (to is AIState.Hiding)
            {
                ReleaseDragTargets();
            }
            Character.AnimController.ReleaseStuckLimbs();
            AttackLimb = null;
            movementMargin = 0;
            ResetEscape();
            if (isStateChanged && to == AIState.Idle && from != to)
            {
                SetStateResetTimer();
            }
            blockCheckTimer = 0;
            reachTimer = 0;
            sinTime = 0;
            if (breakCircling && strikeTimer <= 0 && CirclePhase != CirclePhase.CloseIn)
            {
                CirclePhase = CirclePhase.Start;
            }
            if (to != AIState.Idle)
            {
                playDeadTimer = PlayDeadCoolDown;
            }
#if CLIENT
            if (to == AIState.Attack)
            {
                Character.PlaySound(CharacterSound.SoundType.Attack, maxInterval: 3);
            }
#endif
        }

        private void SetStateResetTimer() => stateResetTimer = stateResetCooldown * Rand.Range(0.75f, 1.25f);

        private float GetPerceivingRange(AITarget target)
        {
            float maxSightOrSoundRange = Math.Max(target.SightRange * Sight, target.SoundRange * Hearing);
            if (AIParams.MaxPerceptionDistance >= 0 && maxSightOrSoundRange > AIParams.MaxPerceptionDistance) { return AIParams.MaxPerceptionDistance; }
            return maxSightOrSoundRange;
        }

        private bool CanPerceive(AITarget target, float dist = -1, float distSquared = -1, bool checkVisibility = false)
        {
            if (target?.Entity == null) { return false; }
            bool insideSightRange;
            bool insideSoundRange;
            if (checkVisibility)
            {
                Submarine mySub = Character.Submarine;
                Submarine targetSub = target.Entity.Submarine;
                // We only want to check the visibility when the target is in ruins/wreck/similiar place where sneaking should be possible.
                // When the monsters attack the player sub, they wall hack so that they can be more aggressive.
                // Pets should always check the visibility, unless the pet and the target are both outside the submarine -> shouldn't target when they can't perceive (= no wall hack)
                checkVisibility =
                    (Character.IsPet && (mySub != null || targetSub != null)) || 
                    (mySub != null && (targetSub == null || (targetSub == mySub && !targetSub.Info.IsPlayer)));
            }
            if (dist > 0)
            {
                if (AIParams.MaxPerceptionDistance >= 0 && dist > AIParams.MaxPerceptionDistance) { return false; }
                insideSightRange = IsInRange(dist, target.SightRange, Sight);
                if (!checkVisibility && insideSightRange) { return true; }
                insideSoundRange = IsInRange(dist, target.SoundRange, Hearing);
            }
            else
            {
                if (distSquared < 0)
                {
                    distSquared = Vector2.DistanceSquared(Character.WorldPosition, target.WorldPosition);
                }
                if (AIParams.MaxPerceptionDistance >= 0 && distSquared > AIParams.MaxPerceptionDistance * AIParams.MaxPerceptionDistance) { return false; }
                insideSightRange = IsInRangeSqr(distSquared, target.SightRange, Sight);
                if (!checkVisibility && insideSightRange) { return true; }
                insideSoundRange = IsInRangeSqr(distSquared, target.SoundRange, Hearing);
            }
            if (!checkVisibility)
            {
                return insideSightRange || insideSoundRange;
            }
            else
            {
                if (!insideSightRange && !insideSoundRange) { return false; }
                // Inside the same submarine -> check whether the target is behind a wall
                if (target.Entity is Character c && VisibleHulls.Contains(c.CurrentHull) || target.Entity is Item i && VisibleHulls.Contains(i.CurrentHull))
                {
                    return insideSightRange || insideSoundRange;
                }
                else
                {
                    // No line of sight to the target -> Ignore sight and use only half of the sound range
                    if (dist > 0)
                    {
                        return IsInRange(dist, target.SoundRange, Hearing / 2);
                    }
                    else
                    {
                        if (distSquared < 0)
                        {
                            distSquared = Vector2.DistanceSquared(Character.WorldPosition, target.WorldPosition);
                        }
                        return IsInRangeSqr(distSquared, target.SoundRange, Hearing / 2);
                    }
                }
            }

            bool IsInRange(float dist, float range, float perception) => dist <= range * perception;
            bool IsInRangeSqr(float distSquared, float range, float perception) => distSquared <= MathUtils.Pow2(range * perception);
        }

        public void ReevaluateAttacks()
        {
            canAttackWalls = LatchOntoAI is { AttachToSub: true };
            canAttackDoors = false;
            canAttackCharacters = false;
            canAttackItems = false;
            foreach (var limb in Character.AnimController.Limbs)
            {
                if (limb.IsSevered) { continue; }
                if (limb.Disabled) { continue; }
                if (limb.attack == null) { continue; }
                if (!canAttackWalls)
                {
                    canAttackWalls = (limb.attack.StructureDamage > 0 || limb.attack.Ranged && limb.attack.IsValidTarget(AttackTarget.Structure));
                }
                if (!canAttackDoors)
                {
                    // Doors are technically items, but intentionally treated as structures here.
                    canAttackDoors = (limb.attack.ItemDamage > 0 || limb.attack.Ranged) && limb.attack.IsValidTarget(AttackTarget.Structure);
                }
                if (!canAttackItems)
                {
                    // AttackTarget.Structure is also accepted for backwards support.
                    canAttackItems = canAttackDoors || (limb.attack.ItemDamage > 0 || limb.attack.Ranged) && limb.attack.IsValidTarget(AttackTarget.Structure | AttackTarget.Item);
                }
                if (!canAttackCharacters)
                {
                    canAttackCharacters = limb.attack.IsValidTarget(AttackTarget.Character);
                }
            }
            if (PathSteering != null)
            {
                PathSteering.CanBreakDoors = canAttackDoors;
            }
        }

        private bool IsPositionInsideAllowedZone(Vector2 pos, out Vector2 targetDir)
        {
            targetDir = Vector2.Zero;
            if (Level.Loaded == null) { return true; }
            if (Level.Loaded.LevelData.Biome.IsEndBiome) { return true; }
            if (AIParams.AvoidAbyss)
            {
                if (pos.Y < Level.Loaded.AbyssStart)
                {
                    // Too far down
                    targetDir = Vector2.UnitY;
                }
            }
            else if (AIParams.StayInAbyss)
            {
                if (pos.Y > Level.Loaded.AbyssStart)
                {
                    // Too far up
                    targetDir = -Vector2.UnitY;
                }
                else if (pos.Y < Level.Loaded.AbyssEnd)
                {
                    // Too far down
                    targetDir = Vector2.UnitY;
                }
            }
            float margin = Level.OutsideBoundsCurrentMargin;
            if (pos.X < -margin)
            {
                // Too far left
                targetDir = Vector2.UnitX;
            }
            else if (pos.X > Level.Loaded.Size.X + margin)
            {
                // Too far right
                targetDir = -Vector2.UnitX;
            }
            return targetDir == Vector2.Zero;
        }

        private Vector2 returnDir;
        private float returnTimer;
        private void SteerInsideLevel(float deltaTime)
        {
            if (SteeringManager is IndoorsSteeringManager) { return; }
            if (Level.Loaded == null) { return; }
            if (State == AIState.Attack && returnTimer <= 0) { return; }
            float returnTime = 5;
            if (!IsPositionInsideAllowedZone(WorldPosition, out Vector2 targetDir))
            {
                returnDir = targetDir;
                returnTimer = returnTime * Rand.Range(0.75f, 1.25f);
            }
            if (returnTimer > 0)
            {
                returnTimer -= deltaTime;
                SteeringManager.Reset();
                SteeringManager.SteeringManual(deltaTime, returnDir * 10);
                SteeringManager.SteeringAvoid(deltaTime, avoidLookAheadDistance, 15);
            }
        }

        public override bool SteerThroughGap(Structure wall, WallSection section, Vector2 targetWorldPos, float deltaTime)
        {
            IsTryingToSteerThroughGap = true;
            wallTarget = null;
            LatchOntoAI?.DeattachFromBody(reset: true, cooldown: 2);
            Character.AnimController.ReleaseStuckLimbs();
            bool success = base.SteerThroughGap(wall, section, targetWorldPos, deltaTime);
            if (success)
            {
                // If already inside, target the hull, else target the wall.
                SelectedAiTarget = Character.CurrentHull != null ? section.gap.AiTarget : wall.AiTarget;
                SteeringManager.SteeringAvoid(deltaTime, avoidLookAheadDistance, weight: 1);
            }
            IsSteeringThroughGap = success;
            return success;
        }

        public override bool SteerThroughGap(Gap gap, Vector2 targetWorldPos, float deltaTime, float maxDistance = -1)
        {
            bool success = base.SteerThroughGap(gap, targetWorldPos, deltaTime, maxDistance);
            if (success)
            {
                wallTarget = null;
                LatchOntoAI?.DeattachFromBody(reset: true, cooldown: 2);
                Character.AnimController.ReleaseStuckLimbs();
                SteeringManager.SteeringAvoid(deltaTime, avoidLookAheadDistance, weight: 1);
            }
            IsSteeringThroughGap = success;
            return success;
        }

        public bool CanPassThroughHole(Structure wall, int sectionIndex) => CanPassThroughHole(wall, sectionIndex, requiredHoleCount);

        public override bool Escape(float deltaTime)
        {
            if (SelectedAiTarget != null && (SelectedAiTarget.Entity == null || SelectedAiTarget.Entity.Removed))
            {
                State = AIState.Idle;
                return false;
            }
            else if (CurrentTargetMemory is AITargetMemory targetMemory && SelectedAiTarget?.Entity is Character)
            {
                targetMemory.Priority += deltaTime * PriorityFearIncrement;
            }
            bool isSteeringThroughGap = UpdateEscape(deltaTime, canAttackDoors);
            if (!isSteeringThroughGap)
            {
                if (SelectedAiTarget?.Entity is Character targetCharacter && targetCharacter.CurrentHull == Character.CurrentHull)
                {
                    SteerAwayFromTheEnemy();
                }
                else if (canAttackDoors && HasValidPath())
                {
                    var door = PathSteering.CurrentPath.CurrentNode?.ConnectedDoor ?? PathSteering.CurrentPath.NextNode?.ConnectedDoor;
                    if (door is { CanBeTraversed: false } && !door.HasAccess(Character) && door.Item.AiTarget is { } doorAiTarget)
                    {
                        if (SelectedAiTarget != doorAiTarget || State != AIState.Attack)
                        {
                            SelectTarget(doorAiTarget, CurrentTargetMemory.Priority);
                            State = AIState.Attack;
                            return false;
                        }
                    }
                }
            }
            if (EscapeTarget == null)
            {
                if (SelectedAiTarget?.Entity is Character)
                {
                    SteerAwayFromTheEnemy();
                }
                else
                {
                    SteeringManager.SteeringWander(avoidWanderingOutsideLevel: Character.CurrentHull == null);
                    if (Character.CurrentHull == null)
                    {
                        SteeringManager.SteeringAvoid(deltaTime, lookAheadDistance: avoidLookAheadDistance, weight: 5);
                    }
                }
            }
            return isSteeringThroughGap;

            void SteerAwayFromTheEnemy()
            {
                if (SelectedAiTarget == null) { return; }
                Vector2 escapeDir = Vector2.Normalize(WorldPosition - SelectedAiTarget.WorldPosition);
                if (!MathUtils.IsValid(escapeDir))
                {
                    escapeDir = Vector2.UnitY;
                }
                if (Character.CurrentHull != null && !Character.AnimController.InWater)
                {
                    // Inside
                    escapeDir = new Vector2(Math.Sign(escapeDir.X), 0);
                }
                SteeringManager.Reset();
                SteeringManager.SteeringManual(deltaTime, escapeDir);
            }
        }

        private readonly List<Limb> targetLimbs = new List<Limb>();
        public Limb GetTargetLimb(Limb attackLimb, Character target, LimbType targetLimbType = LimbType.None)
        {
            targetLimbs.Clear();
            foreach (var limb in target.AnimController.Limbs)
            {
                if (limb.type == targetLimbType || targetLimbType == LimbType.None)
                {
                    targetLimbs.Add(limb);
                }
            }
            if (targetLimbs.None())
            {
                // If no limbs of given type was found, accept any limb.
                targetLimbs.AddRange(target.AnimController.Limbs);
            }
            float closestDist = float.MaxValue;
            Limb targetLimb = null;
            foreach (Limb limb in targetLimbs)
            {
                if (limb.IsSevered) { continue; }
                if (limb.Hidden) { continue; }
                float dist = Vector2.DistanceSquared(limb.WorldPosition, attackLimb.WorldPosition) / Math.Max(limb.AttackPriority, 0.1f);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    targetLimb = limb;
                }
            }
            return targetLimb;
        }

        private static Character GetOwner(Item item)
        {
            var pickable = item.GetComponent<Pickable>();
            if (pickable != null)
            {
                Character owner = pickable.Picker ?? item.FindParentInventory(i => i.Owner is Character)?.Owner as Character;
                if (owner != null)
                {
                    var target = owner.AiTarget;
                    if (target?.Entity != null && !target.Entity.Removed)
                    {
                        return owner;
                    }
                }
            }
            return null;
        }
    }

    //the "memory" of the Character 
    //keeps track of how preferable it is to attack a specific target
    //(if the Character can't inflict much damage the target, the priority decreases
    //and if the target attacks the Character, the priority increases)
    class AITargetMemory
    {
        public readonly AITarget Target;
        public Vector2 Location { get; set; }

        private float priority;
        
        public float Priority
        {
            get { return priority; }
            set { priority = MathHelper.Clamp(value, 1.0f, 100.0f); }
        }

        public AITargetMemory(AITarget target, float priority)
        {
            Target = target;
            Location = target.WorldPosition;
            this.priority = priority;
        }
    }
}
