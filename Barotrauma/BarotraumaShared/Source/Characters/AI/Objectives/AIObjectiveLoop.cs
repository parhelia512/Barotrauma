﻿using System.Collections.Generic;
using System.Linq;
using Barotrauma.Extensions;
using Microsoft.Xna.Framework;

namespace Barotrauma
{
    abstract class AIObjectiveLoop<T> : AIObjective
    {
        protected List<T> targets = new List<T>();
        protected Dictionary<T, AIObjective> objectives = new Dictionary<T, AIObjective>();
        protected HashSet<T> ignoreList = new HashSet<T>();
        private float ignoreListTimer;
        private float targetUpdateTimer;

        // By default, doesn't clear the list automatically
        protected virtual float IgnoreListClearInterval => 0;
        protected virtual float TargetUpdateInterval => 2;

        public AIObjectiveLoop(Character character, string option) : base(character, option)
        {
            Reset();
        }

        protected override void Act(float deltaTime) { }
        public override bool IsCompleted() => false;
        public override bool CanBeCompleted => true;

        public override void Update(AIObjectiveManager objectiveManager, float deltaTime)
        {
            base.Update(objectiveManager, deltaTime);
            if (IgnoreListClearInterval > 0)
            {
                if (ignoreListTimer > IgnoreListClearInterval)
                {
                    Reset();
                }
                else
                {
                    ignoreListTimer += deltaTime;
                }
            }
            if (targetUpdateTimer >= TargetUpdateInterval)
            {
                targetUpdateTimer = 0;
                UpdateTargets();
            }
            else
            {
                targetUpdateTimer += deltaTime;
            }
            // Sync objectives, subobjectives and targets
            foreach (var objective in objectives)
            {
                var target = objective.Key;
                if (!objective.Value.CanBeCompleted)
                {
                    ignoreList.Add(target);
                    targetUpdateTimer = TargetUpdateInterval;
                }
                if (!targets.Contains(target))
                {
                    subObjectives.Remove(objective.Value);
                }
            }
            SyncRemovedObjectives(objectives, GetList());
            if (objectives.None() && targets.Any())
            {
                CreateObjectives();
            }
        }

        public override void Reset()
        {
            ignoreList.Clear();
            ignoreListTimer = 0;
            UpdateTargets();
        }

        public override void OnSelected()
        {
            Reset();
        }

        public override float GetPriority(AIObjectiveManager objectiveManager)
        {
            if (character.Submarine == null) { return 0; }
            if (targets.None()) { return 0; }
            float avg = targets.Average(t => Average(t));
            return MathHelper.Lerp(0, AIObjectiveManager.OrderPriority + 20, avg / 100);
        }

        protected void UpdateTargets()
        {
            targets.Clear();
            FindTargets();
            CreateObjectives();
        }

        protected virtual void FindTargets()
        {
            foreach (T item in GetList())
            {
                if (Filter(item)) { continue; }
                if (!targets.Contains(item))
                {
                    targets.Add(item);
                }
            }
        }

        protected virtual void CreateObjectives()
        {
            foreach (T target in targets)
            {
                if (!objectives.TryGetValue(target, out AIObjective objective))
                {
                    objective = ObjectiveConstructor(target);
                    objectives.Add(target, objective);
                    AddSubObjective(objective);
                }
            }
        }

        /// <summary>
        /// List of all possible items of the specified type. Used for filtering the removed objectives.
        /// </summary>
        protected abstract IEnumerable<T> GetList();
        protected abstract float Average(T target);
        protected abstract AIObjective ObjectiveConstructor(T target);
        protected abstract bool Filter(T target);
    }
}
