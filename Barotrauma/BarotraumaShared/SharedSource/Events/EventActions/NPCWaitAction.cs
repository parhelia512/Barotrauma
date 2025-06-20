﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    /// <summary>
    /// Makes an NPC stop and wait.
    /// </summary>
    class NPCWaitAction : EventAction
    {
        [Serialize("", IsPropertySaveable.Yes, description: "Tag of the NPC(s) that should wait.")]
        public Identifier NPCTag { get; set; }

        [Serialize(true, IsPropertySaveable.Yes, description: "Should the NPC start or stop waiting?")]
        public bool Wait { get; set; }

        [Serialize(true, IsPropertySaveable.Yes, description: "The event actions reset when a GoTo action makes the event jump to a different point. Should the NPC stop waiting when the event resets?")]
        public bool AbandonOnReset { get; set; }

        [Serialize(AIObjectiveManager.MaxObjectivePriority, IsPropertySaveable.Yes, description: "AI priority for the action. Uses 100 by default, which is the absolute maximum for any objectives, " +
                                                                                                 "meaning nothing can be prioritized over it, including the emergency objectives, such as find safety and combat." +
                                                                                                 "Setting the priority to 70 would function like a regular order, but with the highest priority." +
                                                                                                 "A priority of 60 would make the objective work like a lowest priority order." +
                                                                                                 "So, if we'll want the character to wait, but still be able to find safety, defend themselves when attacked, or flee from dangers," +
                                                                                                 "it's better to use e.g. 70 instead of 100.")]
        public float Priority
        {
            get => _priority;
            set => _priority = Math.Clamp(value, AIObjectiveManager.LowestOrderPriority, AIObjectiveManager.MaxObjectivePriority);
        }
        
        private float _priority;

        private bool isFinished = false;


        public NPCWaitAction(ScriptedEvent parentEvent, ContentXElement element) : base(parentEvent, element) { }

        private IEnumerable<Character> affectedNpcs;

        public override void Update(float deltaTime)
        {
            if (isFinished) { return; }

            affectedNpcs = ParentEvent.GetTargets(NPCTag).OfType<Character>();

            foreach (var npc in affectedNpcs)
            {
                if (npc.Removed) { continue; }
                if (npc.AIController is not HumanAIController humanAiController) { continue; }

                if (Wait)
                {
                    var gotoObjective = new AIObjectiveGoTo(
                        AIObjectiveGoTo.GetTargetHull(npc) as ISpatialEntity ?? npc, npc, humanAiController.ObjectiveManager, repeat: true)
                    {
                        FaceTargetOnCompleted = false,
                        OverridePriority = Priority,
                        SourceEventAction = this,
                        IsWaitOrder = true,
                        CloseEnough = 100
                    };
                    humanAiController.ObjectiveManager.AddObjective(gotoObjective);
                    humanAiController.ObjectiveManager.WaitTimer = 0.0f;
                }
                else
                {
                    AbandonGoToObjectives(humanAiController);
                }
            }
            isFinished = true;
        }

        public override bool IsFinished(ref string goTo)
        {
            return isFinished;
        }

        public override void Reset()
        {
            if (affectedNpcs != null && AbandonOnReset)
            {
                foreach (var npc in affectedNpcs)
                {
                    if (npc.Removed || npc.AIController is not HumanAIController aiController) { continue; }
                    AbandonGoToObjectives(aiController);
                }
                affectedNpcs = null;
            }
            isFinished = false;
        }

        private void AbandonGoToObjectives(HumanAIController aiController)
        {
            foreach (var objective in aiController.ObjectiveManager.Objectives)
            {
                if (objective is AIObjectiveGoTo gotoObjective && gotoObjective.SourceEventAction?.ParentEvent == ParentEvent)
                {
                    gotoObjective.Abandon = true;
                }
            }
        }

        public override string ToDebugString()
        {
            return $"{ToolBox.GetDebugSymbol(isFinished)} {nameof(NPCWaitAction)} -> (NPCTag: {NPCTag.ColorizeObject()}, Wait: {Wait.ColorizeObject()})";
        }
    }
}