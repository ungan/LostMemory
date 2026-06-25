using UnityEngine;

namespace LostMemory.Combat
{
    /// <summary>
    /// 모든 공격(평타/화살/마법탄/메테오/패리/보조효과/동료)의 크리티컬 판정 공통 헬퍼.
    /// 단일 진실 소스 — 무기별로 흩어진 critical 로직을 여기 한 곳에서 관리.
    ///
    /// 사용법:
    ///   float finalDamage = CriticalRoller.Roll(stats, baseDamage, out bool wasCritical);
    ///
    /// stats null 또는 baseDamage 0 이하면 그대로 반환 (no crit).
    /// </summary>
    public static class CriticalRoller
    {
        public const float DefaultCritDamageBonus = 0.5f; // 기본 치명타 추가 피해 50%

        public static float Roll(PlayerStatModifierContainer stats, float baseDamage, out bool wasCritical)
        {
            return Roll(stats, baseDamage, Random.value, out wasCritical);
        }

        public static float Roll(PlayerStatModifierContainer stats, float baseDamage, float rollValue, out bool wasCritical)
        {
            wasCritical = false;
            if (baseDamage <= 0f) return baseDamage;

            float chance = stats != null ? Mathf.Max(0f, stats.GetTotalMultiplier(StatId.Critical) - 1f) : 0f;
            if (chance <= 0f) return baseDamage;
            float roll = Mathf.Clamp01(rollValue);
            if (roll >= chance) return baseDamage;

            float dmgBonus = DefaultCritDamageBonus
                + (stats != null ? Mathf.Max(0f, stats.GetTotalMultiplier(StatId.CriticalDamage) - 1f) : 0f);
            wasCritical = true;
            return baseDamage * (1f + dmgBonus);
        }
    }
}
