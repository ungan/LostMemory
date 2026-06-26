using System;

namespace LostMemory.Combat
{
    public static class CombatDamageEventDispatcher
    {
        public static event Action<CombatDamageEvent> DamageApplied;

        public static void RaiseDamageApplied(CombatDamageEvent damageEvent)
        {
            DamageApplied?.Invoke(damageEvent);
        }
    }
}
