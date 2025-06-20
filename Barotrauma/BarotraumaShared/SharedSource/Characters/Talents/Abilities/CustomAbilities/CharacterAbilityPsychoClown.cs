﻿namespace Barotrauma.Abilities
{
    class CharacterAbilityPsychoClown : CharacterAbility
    {
        private readonly StatTypes statType;
        private readonly float minValue, maxValue;
        private readonly string afflictionIdentifier;
        private float lastValue = 0f;
        public override bool AllowClientSimulation => true;

        public CharacterAbilityPsychoClown(CharacterAbilityGroup characterAbilityGroup, ContentXElement abilityElement) : base(characterAbilityGroup, abilityElement)
        {
            statType = CharacterAbilityGroup.ParseStatType(abilityElement.GetAttributeString("stattype", ""), CharacterTalent.DebugIdentifier);
            maxValue = abilityElement.GetAttributeFloat(nameof(maxValue), 0f);
            minValue = abilityElement.GetAttributeFloat(nameof(minValue), 0f);
            afflictionIdentifier = abilityElement.GetAttributeString(nameof(afflictionIdentifier), "");
        }

        protected override void VerifyState(bool conditionsMatched, float timeSinceLastUpdate)
        {
            // managing state this way seems liable to cause bugs, maybe instead create abstraction to reset these values more safely
            // talents cannot be removed while in active play because of the lack of this, for example
            Character.ChangeStat(statType, -lastValue);

            if (conditionsMatched)
            {
                var affliction = Character.CharacterHealth.GetAffliction(afflictionIdentifier);

                float afflictionStrength = 0f;
                if (affliction != null)
                {
                    afflictionStrength = affliction.Strength / affliction.Prefab.MaxStrength;
                }

                lastValue = minValue + afflictionStrength * (maxValue - minValue);
                Character.ChangeStat(statType, lastValue);
            }
            else
            {
                lastValue = 0f;
            }
        }
    }
}
