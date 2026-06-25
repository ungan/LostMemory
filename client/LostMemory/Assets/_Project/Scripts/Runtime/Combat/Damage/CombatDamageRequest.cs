using UnityEngine;

namespace LostMemory.Combat
{
    public readonly struct CombatDamageRequest
    {
        public readonly int SequenceId;
        public readonly ulong SourceId;
        public readonly int TickIndex;
        public readonly ulong AttackerNetworkObjectId;
        public readonly ulong TargetNetworkObjectId;
        public readonly DamageSourceKind SourceKind;
        public readonly CriticalPolicy CriticalPolicy;
        public readonly OnHitPolicy OnHitPolicy;
        public readonly float BaseDamage;
        public readonly float DamageMultiplier;
        public readonly bool ApplyAttackPower;
        public readonly bool ApplyFinisherDamage;
        public readonly float OnHitCooldownSeconds;
        public readonly Vector2 HitDirection;
        public readonly Vector3 HitPoint;
        public readonly string WeaponId;

        public CombatDamageRequest(
            float baseDamage,
            DamageSourceKind sourceKind,
            ulong targetNetworkObjectId,
            ulong attackerNetworkObjectId = 0UL,
            ulong sourceId = 0UL,
            int sequenceId = 0,
            int tickIndex = 0,
            CriticalPolicy criticalPolicy = CriticalPolicy.RollEveryDamageTick,
            OnHitPolicy onHitPolicy = OnHitPolicy.Trigger,
            float damageMultiplier = 1f,
            bool applyAttackPower = true,
            bool applyFinisherDamage = false,
            float onHitCooldownSeconds = 0f,
            Vector2 hitDirection = default,
            Vector3 hitPoint = default,
            string weaponId = null)
        {
            SequenceId = sequenceId;
            SourceId = sourceId;
            TickIndex = tickIndex;
            AttackerNetworkObjectId = attackerNetworkObjectId;
            TargetNetworkObjectId = targetNetworkObjectId;
            SourceKind = sourceKind;
            CriticalPolicy = criticalPolicy;
            OnHitPolicy = onHitPolicy;
            BaseDamage = baseDamage;
            DamageMultiplier = damageMultiplier;
            ApplyAttackPower = applyAttackPower;
            ApplyFinisherDamage = applyFinisherDamage;
            OnHitCooldownSeconds = onHitCooldownSeconds;
            HitDirection = hitDirection;
            HitPoint = hitPoint;
            WeaponId = weaponId;
        }
    }
}
