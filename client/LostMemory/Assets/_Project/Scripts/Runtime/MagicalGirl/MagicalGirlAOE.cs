using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.Enemies;
using LostMemory.TestKhi;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.MagicalGirl
{
    /// <summary>
    /// CL-204: 2·4번 미소녀 AOE.
    ///
    /// 2 (Ice / AOEAtTarget): spawn 시점 가장 가까운 적 위치로 1회 snap 후 정지, duration 동안 매 tick OverlapCircle 데미지.
    ///                         반경 내 적 없으면 spawn 즉시 자기파괴 (장판 안 깔림). 적이 빠져나가면 회피 가능.
    /// 4 (Blackhole / AOEStationary): spawn 위치에 정지, duration 동안 tick 데미지 + 적을 코어로 끌어당김.
    ///
    /// MagicalGirlAI.Attack() 가 catalog 의 vfxPrefab 을 Instantiate → Init(kind, damage, radius, duration, tickInterval, pullSpeed, slowMag, slowDur) 호출.
    /// slowMagnitude > 0 이면 매 tick 적에 EnemyStatusEffect.ApplySlow 호출 → sprite 푸르게 + 이속 감소.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MagicalGirlAOE : MonoBehaviour
    {
        private MagicalGirlAttackCatalog.AttackKind _kind;
        private float _damagePerTick;
        private float _radius;
        private float _expiresAt;
        private float _nextTickAt;
        private float _tickInterval;
        private float _pullSpeed;
        private float _slowMagnitude;
        private float _slowDuration;
        private int _tickIndex;
        private KhiDownController _ownerDownController;
        private readonly HashSet<Health> _hitTargetsThisTick = new HashSet<Health>();

        // 검색 임시 버퍼 (heap alloc 방지)
        private static readonly Collider2D[] _hitBuf = new Collider2D[24];

        public void Init(
            MagicalGirlAttackCatalog.AttackKind kind,
            float damagePerTick,
            float radius,
            float duration,
            float tickInterval,
            float pullSpeed,
            float slowMagnitude,
            float slowDuration)
        {
            Init(kind, damagePerTick, radius, duration, tickInterval, pullSpeed, slowMagnitude, slowDuration, null);
        }

        public void Init(
            MagicalGirlAttackCatalog.AttackKind kind,
            float damagePerTick,
            float radius,
            float duration,
            float tickInterval,
            float pullSpeed,
            float slowMagnitude,
            float slowDuration,
            KhiDownController ownerDownController)
        {
            _kind = kind;
            _damagePerTick = damagePerTick;
            _radius = radius;
            _expiresAt = Time.time + duration;
            _tickInterval = Mathf.Max(0.05f, tickInterval);
            _nextTickAt = Time.time;  // 즉시 첫 tick
            _pullSpeed = pullSpeed;
            _slowMagnitude = Mathf.Clamp01(slowMagnitude);
            _slowDuration = Mathf.Max(0f, slowDuration);
            _ownerDownController = ownerDownController;

            if (_kind == MagicalGirlAttackCatalog.AttackKind.AOEAtTarget)
            {
                Transform initialTarget = FindClosestEnemyTransform();
                if (initialTarget == null)
                {
                    // 반경 내 적 없으면 장판 안 깔림 — 발사 자체 취소
                    Destroy(gameObject);
                    return;
                }
                transform.position = initialTarget.position;
            }
        }

        private void Update()
        {
            if (IsOwnerActionBlocked())
            {
                Destroy(gameObject);
                return;
            }

            if (Time.time >= _expiresAt)
            {
                Destroy(gameObject);
                return;
            }

            if (Time.time < _nextTickAt) return;
            _nextTickAt = Time.time + _tickInterval;
            DoTick();
        }

        private void DoTick()
        {
            if (IsOwnerActionBlocked()) return;
            if (_damagePerTick <= 0f && _pullSpeed <= 0f && _slowMagnitude <= 0f) return;
            _hitTargetsThisTick.Clear();
            _tickIndex++;
            int hits = Physics2D.OverlapCircleNonAlloc(transform.position, _radius, _hitBuf);
            for (int i = 0; i < hits; i++)
            {
                Collider2D col = _hitBuf[i];
                if (col == null) continue;
                Health h = col.GetComponentInParent<Health>();
                // 멀티 가드 통합: CanBeAutoTargetedEnemy 내부에서 IsAuthoritativePlayer 도 함께 처리.
                if (!CombatTargetable.CanBeAutoTargetedEnemy(h)) continue;
                if (!_hitTargetsThisTick.Add(h)) continue;

                if (_damagePerTick > 0f)
                {
                    // 동료 AOE 도 주인의 stat 으로 크리티컬 판정 — 매 tick / 대상별 roll (도트 특성).
                    LostMemory.Combat.PlayerStatModifierContainer stats = _ownerDownController != null
                        ? _ownerDownController.GetComponentInParent<LostMemory.Combat.PlayerStatModifierContainer>() : null;
                    // AttackPower 적용 — 평타 패턴 통일. 이전 누락분 fix.
                    CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                        new CombatDamageRequest(
                            _damagePerTick,
                            DamageSourceKind.Area,
                            0UL,
                            sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                            tickIndex: _tickIndex,
                            onHitPolicy: OnHitPolicy.Suppress,
                            applyAttackPower: true,
                            hitPoint: h.transform.position,
                            weaponId: nameof(MagicalGirlAOE)),
                        stats);
                    float appliedDmg = damageResult.FinalDamage;
                    bool aoeCrit = damageResult.WasCritical;
                    h.Damage(appliedDmg, gameObject, 0f, 0f, Vector3.zero);
                    if (LostMemory.UI.DamagePopupSpawner.Instance != null)
                    {
                        LostMemory.UI.DamagePopupSpawner.Instance.NotifySubEffectDamage(h, appliedDmg, aoeCrit);
                    }
                }

                // CL-204: Slow status (sprite 푸르게 + 이속 감소). CL-202 EnemyStatusEffect 위임.
                if (_slowMagnitude > 0f && _slowDuration > 0f)
                {
                    EnemyStatusEffect status = h.gameObject.GetComponent<EnemyStatusEffect>()
                                            ?? h.gameObject.GetComponentInParent<EnemyStatusEffect>();
                    if (status == null) status = h.gameObject.AddComponent<EnemyStatusEffect>();
                    status.ApplySlow(_slowMagnitude, _slowDuration);
                }

                // Blackhole 끌어당김 (AOEStationary 전용)
                if (_pullSpeed > 0f && _kind == MagicalGirlAttackCatalog.AttackKind.AOEStationary)
                {
                    Transform et = h.transform;
                    Vector3 toCore = transform.position - et.position;
                    float dist = toCore.magnitude;
                    if (dist > 0.05f)
                    {
                        Vector3 step = toCore.normalized * Mathf.Min(_pullSpeed * _tickInterval, dist);
                        et.position += step;
                    }
                }
            }
        }

        private Transform FindClosestEnemyTransform()
        {
            // 8유닛 반경 내에서 가장 가까운 Character.AI Health 검색
            const float searchRadius = 8f;
            int hits = Physics2D.OverlapCircleNonAlloc(transform.position, searchRadius, _hitBuf);
            Transform closest = null;
            float minDistSq = float.MaxValue;
            for (int i = 0; i < hits; i++)
            {
                Collider2D col = _hitBuf[i];
                if (col == null) continue;
                Health h = col.GetComponentInParent<Health>();
                // 멀티 가드 통합: CanBeAutoTargetedEnemy 내부에서 IsAuthoritativePlayer 도 함께 처리.
                if (!CombatTargetable.CanBeAutoTargetedEnemy(h)) continue;
                float dSq = ((Vector2)(h.transform.position - transform.position)).sqrMagnitude;
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                    closest = h.transform;
                }
            }
            return closest;
        }

        private bool IsOwnerActionBlocked()
        {
            return KhiPlayerActionGate.IsBlocked(_ownerDownController);
        }
    }
}
