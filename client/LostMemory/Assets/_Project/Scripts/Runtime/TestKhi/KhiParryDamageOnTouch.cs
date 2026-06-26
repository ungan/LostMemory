using LostMemory.Combat;
using LostMemory.Networking.Player;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.TestKhi
{
    /// <summary>
    /// Parry 대응 피해 소스.
    /// 피해 적용 직전에 대상의 KhiParryController를 확인해 패링 성공 시 피해를 스킵하고
    /// 패링 실패 후딜 중이면 감쇠 피해만 적용한다.
    /// 패링과 무관한 대상이면 기본 DamageOnTouch 흐름을 그대로 유지한다.
    ///
    /// CL-108: 모든 데미지 경로에 PlayerShield 차감 hook 추가. 패링 성공은 보호막을 차감하지 않음.
    ///
    /// NOTE: 감쇠 경로는 base.OnCollideWithDamageable(Health)의 로직(TDE DamageOnTouch.cs 라인 634-676)과
    /// 동일 순서로 수동 재현한다. TDE 버전 업그레이드 시 base 구현을 확인해야 한다.
    /// </summary>
    [AddComponentMenu("Lost Memory/Test Khi/Khi Parry Damage On Touch")]
    public class KhiParryDamageOnTouch : DamageOnTouch
    {
        [SerializeField] private bool logParryInteraction = false;
        // 멀티: 게스트 owner 측에서 호스트로 데미지 위임 (패리 반격 직접 Damage 분기). 첫 호출 시 lazy 캐시.
        // DamageOverTime 분기는 PlayerDamageRelay 에 대응 API 없음 → 별도 follow-up.
        private PlayerDamageRelay _cachedRelay;
        private bool _cachedRelayResolved;
        // 크리티컬 — parry attacker(=player) 의 stat container lazy resolve.
        private LostMemory.Combat.PlayerStatModifierContainer _cachedAttackerStats;
        private bool _cachedAttackerStatsResolved;

        protected override void OnCollideWithDamageable(Health health)
        {
            if (health == null)
            {
                if (logParryInteraction) Debug.Log("[KhiParryDoT] health == null, base path");
                base.OnCollideWithDamageable(health);
                return;
            }

            PlayerShield shield = ResolvePlayerShield(health);
            KhiParryController parry = ResolveParryController(health);

            float randomDamage = Random.Range(MinDamageCaused, Mathf.Max(MaxDamageCaused, MinDamageCaused));
            Vector2 incomingDir = (Vector2)(health.transform.position - transform.position);

            // CL-108: 패링 처리 먼저. 성공이면 보호막 차감 없이 즉시 return.
            float damageToApply = randomDamage;
            if (parry != null)
            {
                if (logParryInteraction)
                {
                    Debug.Log($"[KhiParryDoT] calling TryResolve, state={parry.CurrentState}, damage={randomDamage}");
                }

                if (parry.TryResolveIncomingDamage(gameObject, incomingDir, randomDamage, out float resolved))
                {
                    if (resolved <= 0f)
                    {
                        // 패링 성공: 피해 적용 스킵. 보호막도 차감하지 않음.
                        if (logParryInteraction) Debug.Log("[KhiParryDoT] parry SUCCESS, skip damage");
                        return;
                    }
                    // 패링 실패 후딜 감쇠
                    if (logParryInteraction) Debug.Log($"[KhiParryDoT] parry REDUCED, damage={resolved}");
                    damageToApply = resolved;
                }
                else if (logParryInteraction)
                {
                    Debug.Log("[KhiParryDoT] TryResolve returned false, full damage path");
                }
            }

            // CL-108: PlayerShield 차감. 보호막이 완전 흡수하면 피드백/이벤트 스킵 (결정 #4 정책).
            float dmgAfterShield = shield != null ? shield.TryAbsorb(damageToApply) : damageToApply;
            if (dmgAfterShield <= 0f)
            {
                if (logParryInteraction) Debug.Log("[KhiParryDoT] shield fully absorbed, skip damage application");
                return;
            }

            // 모든 경로 ApplyReducedDamage 로 통합 (base 의 부수 효과 재현).
            ApplyReducedDamage(health, dmgAfterShield);
        }

        private static PlayerShield ResolvePlayerShield(Health health)
        {
            if (health == null) return null;
            PlayerShield shield = health.gameObject.GetComponent<PlayerShield>();
            if (shield == null) shield = health.gameObject.GetComponentInParent<PlayerShield>();
            return shield;
        }

        private static KhiParryController ResolveParryController(Health health)
        {
            if (health == null)
            {
                return null;
            }

            KhiParryController comp = health.gameObject.GetComponent<KhiParryController>();
            if (comp == null)
            {
                comp = health.gameObject.GetComponentInParent<KhiParryController>();
            }

            return comp;
        }

        private void ApplyReducedDamage(Health health, float reducedDamage)
        {
            // PvP 미상정 — 패리 반격은 적에게만. 다른 player 친아군 skip.
            if (CombatTargetable.IsFriendlyPlayer(health))
            {
                return;
            }

            _collidingHealth = health;
            _colliderHealth = health;

            if (!_cachedRelayResolved)
            {
                _cachedRelay = GetComponentInParent<PlayerDamageRelay>();
                _cachedRelayResolved = true;
            }
            if (!_cachedAttackerStatsResolved)
            {
                _cachedAttackerStats = GetComponentInParent<LostMemory.Combat.PlayerStatModifierContainer>();
                _cachedAttackerStatsResolved = true;
            }
            // 패리 반격 데미지에도 크리티컬 판정 (평타와 동일 stat).
            CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                new CombatDamageRequest(
                    reducedDamage,
                    DamageSourceKind.Counter,
                    0UL,
                    sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                    onHitPolicy: OnHitPolicy.Trigger,
                    applyAttackPower: false,
                    hitPoint: health.transform.position,
                    weaponId: "ParryCounter"),
                _cachedAttackerStats);
            reducedDamage = damageResult.FinalDamage;
            bool parryCrit = damageResult.WasCritical;
            // 게스트 ServerRpc 위임 경로면 CanTakeDamageThisFrame 가드 우회 — 게스트 측 target Health 는 DamageDisabled() 호출됨.
            bool willRelayToServer = _cachedRelay != null && _cachedRelay.IsSpawned && !_cachedRelay.IsServer;
            if (!willRelayToServer && !health.CanTakeDamageThisFrame())
            {
                return;
            }

            _colliderTopDownController = health.gameObject.GetComponent<TopDownController>();
            if (_colliderTopDownController == null)
            {
                _colliderTopDownController = health.gameObject.GetComponentInParent<TopDownController>();
            }

            HitDamageableFeedback?.PlayFeedbacks(this.transform.position);
            HitDamageableEvent?.Invoke(_colliderHealth);

            ApplyKnockback(reducedDamage, TypedDamages);

            DetermineDamageDirection();

            if (RepeatDamageOverTime)
            {
                _colliderHealth.DamageOverTime(
                    reducedDamage,
                    gameObject,
                    InvincibilityDuration,
                    InvincibilityDuration,
                    _damageDirectionVector,
                    TypedDamages,
                    AmountOfRepeats,
                    DurationBetweenRepeats,
                    DamageOverTimeInterruptible,
                    RepeatedDamageType);
            }
            else
            {
                if (_cachedRelay != null)
                {
                    // NOTE: PlayerDamageRelay 는 TypedDamages 인자 미지원. 패리 반격은 단일 damage 만 sync.
                    _cachedRelay.RelayDamage(
                        _colliderHealth,
                        reducedDamage,
                        gameObject,
                        InvincibilityDuration,
                        InvincibilityDuration,
                        _damageDirectionVector);
                }
                else
                {
                    _colliderHealth.Damage(
                        reducedDamage,
                        gameObject,
                        InvincibilityDuration,
                        InvincibilityDuration,
                        _damageDirectionVector,
                        TypedDamages);
                }
            }

            // 본인 패리 → popup 표시 (NotifyMeleeDamage 재사용 — 평타 카테고리).
            if (LostMemory.UI.DamagePopupSpawner.Instance != null)
            {
                LostMemory.UI.DamagePopupSpawner.Instance.NotifyMeleeDamage(_colliderHealth, reducedDamage, parryCrit);
            }

            if (DamageTakenEveryTime + DamageTakenDamageable > 0f && !_colliderHealth.PreventTakeSelfDamage)
            {
                // base의 SelfDamage는 private virtual이 아닌 protected virtual이라 직접 호출 가능
                SelfDamage(DamageTakenEveryTime + DamageTakenDamageable);
            }
        }

        /// <summary>
        /// base._damageDirection은 protected지만 필드명 노출 여부가 버전마다 다를 수 있어
        /// DetermineDamageDirection이 설정하는 값을 우회 없이 사용하기 위한 래퍼.
        /// base에서는 _damageDirection이라는 protected 필드를 쓰지만, 여기서는 같은 필드를 그대로 참조한다.
        /// </summary>
        private Vector3 _damageDirectionVector => _damageDirection;
    }
}
