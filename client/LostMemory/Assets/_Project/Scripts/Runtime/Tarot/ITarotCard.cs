using LostMemory.Rewards;
using LostMemory.Combat;
using MoreMountains.TopDownEngine;

namespace LostMemory.Tarot
{
    /// <summary>
    /// CL-147: 타로 카드 발화 시 Activate 에 전달되는 컨텍스트.
    /// </summary>
    public class TarotContext
    {
        public TarotSystem System;
        public Health PlayerHealth;
        public PlayerStatModifierContainer PlayerStats;
        public RewardPanelView RewardPanel;
        /// <summary>0f = base, 1.0f = 효과 2배. TarotEffectMultiplier 효과로 SetEffectMultiplier(mag).</summary>
        public float EffectMultiplier;
    }

    public interface ITarotCard
    {
        TarotCardId Id { get; }
        void Activate(TarotContext ctx);
    }
}
