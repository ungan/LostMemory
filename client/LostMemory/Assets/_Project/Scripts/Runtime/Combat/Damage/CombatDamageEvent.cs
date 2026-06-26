using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.Combat
{
    public readonly struct CombatDamageEvent
    {
        public readonly CombatDamageResult Result;
        public readonly Health Target;
        public readonly GameObject Attacker;

        public CombatDamageEvent(
            CombatDamageResult result,
            Health target,
            GameObject attacker)
        {
            Result = result;
            Target = target;
            Attacker = attacker;
        }
    }
}
