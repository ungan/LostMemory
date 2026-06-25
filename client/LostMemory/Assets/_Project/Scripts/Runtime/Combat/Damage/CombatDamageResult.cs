using UnityEngine;

namespace LostMemory.Combat
{
    public readonly struct CombatDamageResult
    {
        public readonly int SequenceId;
        public readonly ulong SourceId;
        public readonly int TickIndex;
        public readonly ulong AttackerNetworkObjectId;
        public readonly ulong TargetNetworkObjectId;
        public readonly DamageSourceKind SourceKind;
        public readonly OnHitPolicy OnHitPolicy;
        public readonly float OnHitCooldownSeconds;
        public readonly float FinalDamage;
        public readonly bool WasCritical;
        public readonly bool TargetKilled;
        public readonly Vector2 HitDirection;
        public readonly Vector3 HitPoint;
        public readonly string WeaponId;

        public CombatDamageResult(
            CombatDamageRequest request,
            float finalDamage,
            bool wasCritical,
            bool targetKilled = false)
        {
            SequenceId = request.SequenceId;
            SourceId = request.SourceId;
            TickIndex = request.TickIndex;
            AttackerNetworkObjectId = request.AttackerNetworkObjectId;
            TargetNetworkObjectId = request.TargetNetworkObjectId;
            SourceKind = request.SourceKind;
            OnHitPolicy = request.OnHitPolicy;
            OnHitCooldownSeconds = request.OnHitCooldownSeconds;
            FinalDamage = finalDamage;
            WasCritical = wasCritical;
            TargetKilled = targetKilled;
            HitDirection = request.HitDirection;
            HitPoint = request.HitPoint;
            WeaponId = request.WeaponId;
        }
    }
}
