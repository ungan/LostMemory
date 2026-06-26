using System;
using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.Data;
using LostMemory.Relics;
using LostMemory.Rewards;
using LostMemory.TestKhi;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.Tarot
{
    /// <summary>
    /// CL-147: 타로 시스템 — 평타 hit 누적이 stack 별 threshold 도달 시
    /// 3장 (Death/Healing/Reroll) 중 1장을 균등 추첨 발화.
    ///
    /// 활성/비활성 hook:
    /// - SetEffectApplicator (TarotProc case) → <see cref="OnTarotTierChanged(SetTier)"/>
    /// - TarotEffectMultiplier 효과 → <see cref="SetEffectMultiplier(float)"/>
    ///
    /// Tier index 0~3 = stacks 1/3/5/7 = thresholds 21/17/14/7.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Lost Memory/Tarot/Tarot System")]
    public sealed class TarotSystem : MonoBehaviour
    {
        [Tooltip("Player 의 KhiMeleeComboController. TargetHit 구독 대상.")]
        [SerializeField] private KhiMeleeComboController meleeController;

        [Tooltip("Player 의 TDE Health. 회복/적 제외 판별 용도.")]
        [SerializeField] private Health playerHealth;

        [Tooltip("Player stat container. Death card crit lookup.")]
        [SerializeField] private PlayerStatModifierContainer playerStats;

        [Tooltip("RewardPanelView. 재추첨 카드 발화 대상.")]
        [SerializeField] private RewardPanelView rewardPanelView;

        [Tooltip("타로 발화 / threshold 누적 로그.")]
        [SerializeField] private bool _logTarot = true;

        // tier index 0/1/2/3 = stacks 1/3/5/7
        private static readonly int[] HitThresholds = new[] { 21, 17, 14, 7 };

        private int _hitCount;
        private int _currentTierIndex = -1;
        private float _effectMultiplier;
        private List<ITarotCard> _cards;

        private void Awake()
        {
            _cards = new List<ITarotCard>
            {
                new DeathCard(),
                new HealingCard(),
                new RerollCard(),
            };
        }

        private void OnEnable()
        {
            if (meleeController == null)
            {
                Debug.LogWarning("[TarotSystem] meleeController null — TargetHit 구독 X. Inspector wiring 필요.", this);
                return;
            }
            meleeController.TargetHit += HandleHit;
        }

        private void OnDisable()
        {
            if (meleeController == null) return;
            meleeController.TargetHit -= HandleHit;
        }

        /// <summary>SetEffectApplicator 가 TarotProc 활성 시 호출. RequiredCount 1/3/5/7 → tier index 0/1/2/3.</summary>
        public void OnTarotActivated(SetTier tier)
        {
            _currentTierIndex = tier.RequiredCount switch
            {
                <= 1 => 0,
                <= 3 => 1,
                <= 5 => 2,
                _    => 3,
            };
            if (_logTarot)
                Debug.Log($"[Tarot] 활성 — tier {_currentTierIndex} (필요 평타 {HitThresholds[_currentTierIndex]}타, 현재 누적 {_hitCount})");
        }

        /// <summary>SetEffectApplicator 가 TarotProc 해제 시 호출. hit 카운터 리셋.</summary>
        public void OnTarotDeactivated()
        {
            _currentTierIndex = -1;
            _hitCount = 0;
            if (_logTarot) Debug.Log("[Tarot] 비활성");
        }

        /// <summary>TarotEffectMultiplier 효과 발화 시 호출. 0f = base, 1.0f = 효과 2배.</summary>
        public void SetEffectMultiplier(float mul)
        {
            _effectMultiplier = Mathf.Max(0f, mul);
            if (_logTarot) Debug.Log($"[Tarot] EffectMultiplier = {_effectMultiplier:F2}");
        }

        private void HandleHit(KhiAttackRequest req, AttackStepData step, Health victim, float finalDamage, bool wasCritical)
        {
            if (_currentTierIndex < 0) return;
            if (victim == null) return;

            _hitCount++;
            int threshold = HitThresholds[_currentTierIndex];
            if (_hitCount < threshold)
            {
                if (_logTarot && _hitCount % 5 == 0)
                    Debug.Log($"[Tarot] hit {_hitCount}/{threshold}");
                return;
            }

            _hitCount = 0;
            ITarotCard card = _cards[UnityEngine.Random.Range(0, _cards.Count)];
            var ctx = new TarotContext
            {
                System = this,
                PlayerHealth = playerHealth,
                PlayerStats = ResolvePlayerStats(),
                RewardPanel = rewardPanelView,
                EffectMultiplier = _effectMultiplier,
            };
            if (_logTarot) Debug.Log($"[Tarot] PROC! 카드 = {card.Id} (threshold {threshold} 도달)");
            card.Activate(ctx);
        }

        private PlayerStatModifierContainer ResolvePlayerStats()
        {
            if (playerStats != null) return playerStats;
            if (meleeController == null) return null;

            playerStats = meleeController.GetComponent<PlayerStatModifierContainer>()
                          ?? meleeController.GetComponentInParent<PlayerStatModifierContainer>()
                          ?? meleeController.GetComponentInChildren<PlayerStatModifierContainer>(true);
            return playerStats;
        }
    }
}
