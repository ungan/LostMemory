using UnityEngine;

namespace LostMemory.Combat
{
    public static class CombatDamageResolver
    {
        public static CombatDamageResult Resolve(
            CombatDamageRequest request,
            PlayerStatModifierContainer attackerStats = null)
        {
            return Resolve(request, attackerStats, null);
        }

        public static CombatDamageResult Resolve(
            CombatDamageRequest request,
            PlayerStatModifierContainer attackerStats,
            float criticalRollValue)
        {
            return Resolve(request, attackerStats, (float?)criticalRollValue);
        }

        private static CombatDamageResult Resolve(
            CombatDamageRequest request,
            PlayerStatModifierContainer attackerStats,
            float? criticalRollValue)
        {
            float damage = ResolveBaseDamage(request, attackerStats);
            bool wasCritical = false;

            if (request.CriticalPolicy == CriticalPolicy.RollEveryDamageTick)
            {
                damage = criticalRollValue.HasValue
                    ? CriticalRoller.Roll(attackerStats, damage, criticalRollValue.Value, out wasCritical)
                    : CriticalRoller.Roll(attackerStats, damage, out wasCritical);
            }

            return new CombatDamageResult(
                request,
                Mathf.Max(0f, damage),
                wasCritical);
        }

        private static float ResolveBaseDamage(
            CombatDamageRequest request,
            PlayerStatModifierContainer attackerStats)
        {
            float damage = request.BaseDamage;
            float multiplier = request.DamageMultiplier > 0f ? request.DamageMultiplier : 1f;

            damage *= multiplier;

            if (attackerStats == null)
            {
                return damage;
            }

            if (request.ApplyAttackPower)
            {
                damage *= attackerStats.GetTotalMultiplier(StatId.AttackPower);
            }

            if (request.ApplyFinisherDamage)
            {
                damage *= attackerStats.GetTotalMultiplier(StatId.FinisherDamage);
            }

            return damage;
        }
    }
}
