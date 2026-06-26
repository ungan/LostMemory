using LostMemory.Combat;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.Tarot
{
    /// <summary>죽음 — 현재 방 모든 적 MaxHP 30% × (1+mul) 데미지.</summary>
    public class DeathCard : ITarotCard
    {
        public TarotCardId Id => TarotCardId.Death;

        public void Activate(TarotContext ctx)
        {
            float damageRatio = 0.30f * (1f + ctx.EffectMultiplier);
            var enemies = Object.FindObjectsByType<Health>(FindObjectsSortMode.None);
            int hit = 0;
            foreach (var h in enemies)
            {
                if (h == null) continue;
                if (h == ctx.PlayerHealth) continue;
                if (!h.gameObject.activeInHierarchy) continue;
                if (h.CurrentHealth <= 0f) continue;

                Character ch = h.GetComponentInParent<Character>();
                if (ch != null && ch.CharacterType == Character.CharacterTypes.Player) continue;
                // PvP 미상정 — 4인 안전벨트: AI 변환된 다른 player 도 제외.
                if (CombatTargetable.IsFriendlyPlayer(h)) continue;

                GameObject source = ctx.System != null ? ctx.System.gameObject : null;
                float baseDamage = h.MaximumHealth * damageRatio;
                CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                    new CombatDamageRequest(
                        baseDamage,
                        DamageSourceKind.SubEffect,
                        0UL,
                        sourceId: source != null ? (ulong)Mathf.Abs(source.GetInstanceID()) : 0UL,
                        onHitPolicy: OnHitPolicy.SuppressSubEffectLoop,
                        applyAttackPower: false,
                        hitPoint: h.transform.position,
                        weaponId: nameof(DeathCard)),
                    ctx.PlayerStats);
                h.Damage(damageResult.FinalDamage, source, 0f, 0f, Vector3.zero);
                hit++;
            }
            Debug.Log($"[Tarot/Death] {hit} 적에게 MaxHP {damageRatio * 100f:F0}% 데미지 (mul={ctx.EffectMultiplier:F2})");
        }
    }

    /// <summary>회복 — Player MaxHP 30% × (1+mul) 회복.</summary>
    public class HealingCard : ITarotCard
    {
        public TarotCardId Id => TarotCardId.Healing;

        public void Activate(TarotContext ctx)
        {
            if (ctx.PlayerHealth == null)
            {
                Debug.LogWarning("[Tarot/Healing] PlayerHealth null — 발화 무시.");
                return;
            }
            float healRatio = 0.30f * (1f + ctx.EffectMultiplier);
            float amount = ctx.PlayerHealth.MaximumHealth * healRatio;
            ctx.PlayerHealth.ReceiveHealth(amount, ctx.System != null ? ctx.System.gameObject : null);
            Debug.Log($"[Tarot/Healing] +{amount:F0} HP ({healRatio * 100f:F0}%, mul={ctx.EffectMultiplier:F2})");
        }
    }

    /// <summary>재추첨 — 다음 보상 패널 카드 (1 + RoundToInt(mul))회 재추첨.</summary>
    public class RerollCard : ITarotCard
    {
        public TarotCardId Id => TarotCardId.Reroll;

        public void Activate(TarotContext ctx)
        {
            if (ctx.RewardPanel == null)
            {
                Debug.LogWarning("[Tarot/Reroll] RewardPanel null — 발화 무시.");
                return;
            }
            int rerolls = 1 + Mathf.RoundToInt(ctx.EffectMultiplier);
            ctx.RewardPanel.RequestReroll(rerolls);
            Debug.Log($"[Tarot/Reroll] 다음 보상 패널 +{rerolls}회 재추첨 예약 (mul={ctx.EffectMultiplier:F2})");
        }
    }
}
