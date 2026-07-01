using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LostMemory.Data;
using LostMemory.Enemies;
using LostMemory.Networking.Common;
using LostMemory.Networking.Player;
using LostMemory.Relics;
using LostMemory.TestKhi;
using LostMemory.UI;
using LostMemory.VFX;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.Combat
{
    /// <summary>
    /// CL-142: 평타 적중 시 발동하는 OnHit 효과들의 registry.
    ///
    /// SetEffectApplicator(CL-140) 가 SlowOnHit/FreezeOnHit/ChainOnHit 등을 Register 로 등록 →
    /// KhiMeleeComboController.TargetHit 이벤트에서 모든 등록 효과 순회 적용.
    ///
    /// CL-143 에서 BurnOnHit, WindAOE 추가 case 만 채우면 됨.
    ///
    /// 적 측 Status (Slow/Freeze) 는 EnemyStatusEffect 컴포넌트가 처리. OnHit 시 victim 의
    /// EnemyStatusEffect 를 GetOrAdd (없으면 자동 부착) 후 위임.
    ///
    /// 체인 무한 루프 방지: CombatDamageEventDispatcher 를 구독하되, 체인은 Health.Damage 직접 호출
    /// (TargetHit 안 발화) → 자체 트리거 X.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Lost Memory/Combat/On Hit Effect Registry")]
    public sealed class OnHitEffectRegistry : MonoBehaviour
    {
        [Tooltip("같은 GameObject 의 KhiMeleeComboController. TargetHit 구독 대상.")]
        [SerializeField] private KhiMeleeComboController combat;

        [Tooltip("같은 GameObject 의 PlayerStatModifierContainer. 체인 데미지 = BaseDamage × AttackPower 합산.")]
        [SerializeField] private PlayerStatModifierContainer statContainer;
        [SerializeField] private KhiDownController downController;

        [Header("Chain (CL-142 사용자 결정: 3마리)")]
        [Tooltip("체인 검색 반경 (유닛). 첫 hit 위치 기준.")]
        [SerializeField, Min(0.1f)] private float _chainRadius = 3f;

        [Tooltip("체인 동시 타격 최대 마리수.")]
        [SerializeField, Min(1)] private int _chainMaxTargets = 3;

        [Tooltip("체인 발동 후 글로벌 쿨다운 (초). 평타 마다 매번 발동 방지.")]
        [SerializeField, Min(0f)] private float _chainCooldown = 0.5f;

        [Tooltip("Player→첫 victim VFX 라인을 그릴 최대 거리(유닛). 이보다 멀면 P→A 생략 (원거리 무기 등 자연스러움 보장).")]
        [SerializeField, Min(0f)] private float _chainPlayerLineMaxDistance = 3f;

        [Tooltip("체인 라인 spawn 간 지연(초). 0이면 동시. 0.05 정도면 전기가 전파되는 느낌.")]
        [SerializeField, Min(0f)] private float _chainSpawnInterval = 0.05f;

        [Header("Slow")]
        [Tooltip("얼음 슬로우 지속시간 (초). 새로 hit 마다 갱신.")]
        [SerializeField, Min(0f)] private float _slowDuration = 2f;

        [Header("Wind Blade (CL-143)")]
        [Tooltip("바람 검기 길이 (유닛). victim 위치에서 공격 방향으로.")]
        [SerializeField, Min(0.1f)] private float _windBladeLength = 2f;

        [Tooltip("바람 검기 너비 (유닛). 좁을수록 직선상 적만 맞음.")]
        [SerializeField, Min(0.1f)] private float _windBladeWidth = 0.6f;

        [Header("VFX Prefabs (CL-201)")]
        [SerializeField] private GameObject _chainHitVFXPrefab;
        [SerializeField] private GameObject _windAOEVFXPrefab;
        [Tooltip("Wind VFX 인스턴스 자동 삭제 시간(초). 파티클 라이프타임보다 길어야 잘림 없음.")]
        [SerializeField, Min(0.1f)] private float _windVFXLifetime = 1.2f;

        [Header("SFX (CL-201)")]
        [SerializeField] private AudioClip _chainHitSfx;
        [SerializeField] private AudioClip _windBladeSfx;
        [SerializeField, Range(0f, 1f)] private float _sfxVolume = 0.7f;

        [Header("Hit Stop (CL-201)")]
        [Tooltip("Chain 적중 1회 hitstop 지속(unscaled). 0이면 비활성.")]
        [SerializeField, Min(0f)] private float _chainHitStopDuration = 0.05f;
        [Tooltip("Wind 적중 1회 hitstop 지속(unscaled). 0이면 비활성.")]
        [SerializeField, Min(0f)] private float _windHitStopDuration = 0.08f;
        [Tooltip("Hit stop 동안 Time.timeScale 값. 0=완전 정지, 0.05=5% 슬로우, 1=정상속도(효과 없음).")]
        [SerializeField, Range(0f, 1f)] private float _hitStopFrozenScale = 0f;

        [Header("Status VFX Prefabs (CL-202)")]
        [SerializeField] private GameObject _freezeStatusVFXPrefab;
        [SerializeField] private GameObject _burnStatusVFXPrefab;
        [SerializeField] private GameObject _slowStatusVFXPrefab;
        [Tooltip("Slow 활성 시 적 main sprite 에 곱해지는 tint. 인게임 중 변경 시 다음 ApplySlow 부터 적용.")]
        [SerializeField] private Color _slowTintColor = new Color(0.7f, 0.9f, 1f, 1f);
        [Tooltip("Burn 활성 시 적 main sprite 에 곱해지는 tint. 인게임 중 변경 시 다음 ApplyBurn 부터 적용.")]
        [SerializeField] private Color _burnTintColor = new Color(1f, 0.6f, 0.4f, 1f);

        [Header("Status SFX (CL-202)")]
        [SerializeField] private AudioClip _freezeApplySfx;
        [SerializeField] private AudioClip _burnApplySfx;
        [SerializeField] private AudioClip _slowApplySfx;
        [SerializeField, Range(0f, 1f)] private float _statusSfxVolume = 0.6f;

        [Header("Debug")]
        [SerializeField] private bool _logOnHitDispatch = false;
        [Tooltip("CL-202 디버깅: 0보다 크면 ApplyFreeze 호출 시 magnitudeSeconds 를 이 값으로 강제. 시각 검증 후 0으로 되돌릴 것.")]
        [SerializeField, Min(0f)] private float _debugFreezeOverrideSeconds = 0f;

        // F-2: HostAuthority 로 일원화. 싱글 → true, 멀티 호스트 → true, 멀티 게스트 → false.

        private struct OnHitEntry
        {
            public RelicEffectType Type;
            public float Magnitude;
            public float Duration;
            public object Source;
        }

        private readonly List<OnHitEntry> _entries = new();
        private float _nextChainAllowedAt;
        // 멀티: 게스트 owner 측에서 호스트로 데미지 위임 (Chain / WindBlade). OnEnable 시 1회 캐시.
        private PlayerDamageRelay _cachedRelay;
        private readonly Dictionary<string, float> _nextOnHitAllowedByCooldownKey = new();

        // 체인 검색 임시 버퍼 (heap alloc 방지)
        private static readonly Collider2D[] _chainBuf = new Collider2D[16];

        private void OnEnable()
        {
            if (combat == null)
            {
                Debug.LogError($"[OnHitEffectRegistry] combat null. Inspector wiring 필요. host={gameObject.name}", this);
                return;
            }
            KhiPlayerActionGate.TryResolveDownController(this, out downController);
            if (_cachedRelay == null) _cachedRelay = GetComponentInParent<PlayerDamageRelay>();
            CombatDamageEventDispatcher.DamageApplied += HandleDamageApplied;
        }

        private void OnDisable()
        {
            CombatDamageEventDispatcher.DamageApplied -= HandleDamageApplied;
        }

        // ── 등록 API (SetEffectApplicator 가 호출) ──────────

        public void Register(RelicEffectType type, float magnitude, float duration, object source)
        {
            _entries.Add(new OnHitEntry { Type = type, Magnitude = magnitude, Duration = duration, Source = source });
            if (_logOnHitDispatch)
                Debug.Log($"[OnHit] Register {type} mag={magnitude} dur={duration} src={source}");
        }

        public void UnregisterBySource(object source)
        {
            int removed = _entries.RemoveAll(e => Equals(e.Source, source));
            if (_logOnHitDispatch && removed > 0)
                Debug.Log($"[OnHit] Unregister {removed} entries by src={source}");
        }

        /// <summary>
        /// 외부(UI/디버그) 가 보유 중인 OnHit 효과를 읽기 위한 API.
        /// outBuf 를 Clear 한 뒤 _entries 를 평가 무관 모두 채워 넣는다. private struct 노출 회피.
        /// </summary>
        public void CollectActive(List<ActiveOnHitSnapshot> outBuf)
        {
            if (outBuf == null) return;
            outBuf.Clear();
            foreach (OnHitEntry e in _entries)
            {
                outBuf.Add(new ActiveOnHitSnapshot(e.Type, e.Magnitude, e.Duration, e.Source));
            }
        }

        /// <summary>CollectActive 가 외부로 노출하는 read-only 스냅샷.</summary>
        public readonly struct ActiveOnHitSnapshot
        {
            public readonly RelicEffectType Type;
            public readonly float Magnitude;
            public readonly float Duration;
            public readonly object Source;

            public ActiveOnHitSnapshot(RelicEffectType type, float magnitude, float duration, object source)
            {
                Type = type;
                Magnitude = magnitude;
                Duration = duration;
                Source = source;
            }
        }

        // ── 메인 디스패처 ───────────────────────────────────

        private void HandleDamageApplied(CombatDamageEvent damageEvent)
        {
            CombatDamageResult result = damageEvent.Result;
            Health victim = damageEvent.Target;

            // 평타 본 데미지 popup — 호스트 게스트 모두 본인 hit 흐름에서만 발화하므로 owner 체크 불필요.
            if (result.SourceKind == DamageSourceKind.Melee && DamagePopupSpawner.Instance != null)
            {
                DamagePopupSpawner.Instance.NotifyMeleeDamage(victim, result.FinalDamage, result.WasCritical);
            }

            if (!HostAuthority.IsHost) return;
            if (KhiPlayerActionGate.IsBlocked(downController)) return;
            if (victim == null) return;
            if (!ShouldTriggerOnHit(damageEvent)) return;

            foreach (OnHitEntry e in _entries)
            {
                switch (e.Type)
                {
                    case RelicEffectType.SlowOnHit:    ApplySlow(victim, e.Magnitude); break;
                    case RelicEffectType.FreezeOnHit:  ApplyFreeze(victim, e.Magnitude); break;
                    case RelicEffectType.ChainOnHit:   ApplyChain(damageEvent, victim, e.Magnitude); break;
                    case RelicEffectType.BurnOnHit:    ApplyBurn(victim, e.Magnitude, e.Duration); break;
                    case RelicEffectType.WindAOE:      ApplyWindBlade(victim, e.Magnitude); break;
                }
            }
        }

        private bool ShouldTriggerOnHit(CombatDamageEvent damageEvent)
        {
            CombatDamageResult result = damageEvent.Result;
            switch (result.OnHitPolicy)
            {
                case OnHitPolicy.Suppress:
                case OnHitPolicy.SuppressSubEffectLoop:
                    return false;
                case OnHitPolicy.TriggerWithCooldown:
                    return TryConsumeOnHitCooldown(damageEvent);
                case OnHitPolicy.Trigger:
                    return true;
                default:
                    return false;
            }
        }

        private bool TryConsumeOnHitCooldown(CombatDamageEvent damageEvent)
        {
            float cooldown = damageEvent.Result.OnHitCooldownSeconds;
            if (cooldown <= 0f) return true;

            string key = BuildOnHitCooldownKey(damageEvent);
            float now = Time.time;
            if (_nextOnHitAllowedByCooldownKey.TryGetValue(key, out float nextAllowedAt)
                && now < nextAllowedAt)
            {
                return false;
            }

            _nextOnHitAllowedByCooldownKey[key] = now + cooldown;
            return true;
        }

        private static string BuildOnHitCooldownKey(CombatDamageEvent damageEvent)
        {
            CombatDamageResult result = damageEvent.Result;
            string sourceKey = result.SourceId != 0UL
                ? result.SourceId.ToString()
                : (damageEvent.Attacker != null ? Mathf.Abs(damageEvent.Attacker.GetInstanceID()).ToString() : "0");
            string targetKey = result.TargetNetworkObjectId != 0UL
                ? result.TargetNetworkObjectId.ToString()
                : (damageEvent.Target != null ? Mathf.Abs(damageEvent.Target.GetInstanceID()).ToString() : "0");
            return $"{sourceKey}:{targetKey}:{result.SourceKind}:{result.WeaponId}";
        }

        // ── 효과 처리 ───────────────────────────────────────

        private void ApplySlow(Health victim, float magnitude)
        {
            EnemyStatusEffect status = GetOrAddStatus(victim);
            if (status == null) return;
            status.EnsureVFXPrefabs(_freezeStatusVFXPrefab, _burnStatusVFXPrefab, _slowStatusVFXPrefab);
            status.SetSlowTintColor(_slowTintColor); // 인스펙터 변경 즉시 반영용 매번 push
            status.ApplySlow(magnitude, _slowDuration);

            if (_slowApplySfx != null)
                AudioSource.PlayClipAtPoint(_slowApplySfx, victim.transform.position, _statusSfxVolume);

            if (_logOnHitDispatch)
                Debug.Log($"[OnHit] Slow {magnitude:P0} for {_slowDuration}s → {victim.name}");
        }

        private void ApplyFreeze(Health victim, float magnitudeSeconds)
        {
            EnemyStatusEffect status = GetOrAddStatus(victim);
            if (status == null) return;
            status.EnsureVFXPrefabs(_freezeStatusVFXPrefab, _burnStatusVFXPrefab, _slowStatusVFXPrefab);

            float effectiveSeconds = _debugFreezeOverrideSeconds > 0f ? _debugFreezeOverrideSeconds : magnitudeSeconds;
            status.ApplyFreeze(effectiveSeconds);

            if (_freezeApplySfx != null)
                AudioSource.PlayClipAtPoint(_freezeApplySfx, victim.transform.position, _statusSfxVolume);

            if (_logOnHitDispatch)
                Debug.Log($"[OnHit] Freeze for {effectiveSeconds}s → {victim.name}");
        }

        private void ApplyChain(CombatDamageEvent damageEvent, Health victim, float magnitude)
        {
            if (Time.time < _nextChainAllowedAt) return;
            _nextChainAllowedAt = Time.time + _chainCooldown;

            // 본인 공격력 (StatModifier 합산 적용)
            float chainDamage = combat.WeaponData != null ? combat.WeaponData.BaseDamage * magnitude : 0f;
            if (chainDamage <= 0f) return;

            // CL-146: Range multiplier 적용 — 체인 검색 반경 확장. 상한 200%.
            float rangeMul = statContainer != null ? statContainer.GetCappedMultiplier(StatId.Range, 1f) : 1f;
            List<Health> targets = FindNearbyEnemies(victim.transform.position, victim, _chainRadius * rangeMul, _chainMaxTargets);
            if (targets.Count == 0) return;

            // 데미지는 즉시 적용 (게임플레이 일관성). 시각은 sequential 코루틴으로 전파.
            // Transform 참조를 모아 두면 라인이 적/플레이어 이동을 매 프레임 추적.
            Vector3 victimPos = victim.transform.position;
            List<Transform> chainTransforms = new() { victim.transform };
            int hitCount = 0;
            foreach (Health t in targets)
            {
                if (t == null) continue;
                // PvP 미상정 — chain 후보가 player 면 skip (FindNearbyEnemies 도 가드 있지만 안전벨트).
                if (CombatTargetable.IsFriendlyPlayer(t)) continue;
                // 체인 데미지 각 대상별 크리티컬 판정 (평타와 같은 stat 공유).
                CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                    new CombatDamageRequest(
                        chainDamage,
                        DamageSourceKind.SubEffect,
                        0UL,
                        sourceId: damageEvent.Result.SourceId,
                        onHitPolicy: OnHitPolicy.SuppressSubEffectLoop,
                        applyAttackPower: true,
                        hitPoint: t.transform.position,
                        weaponId: "ChainOnHit"),
                    statContainer);
                float appliedChainDamage = damageResult.FinalDamage;
                bool chainCrit = damageResult.WasCritical;
                if (_cachedRelay != null)
                {
                    _cachedRelay.RelayDamage(t, appliedChainDamage, gameObject, 0f, 0f, Vector2.zero);
                }
                else
                {
                    t.Damage(appliedChainDamage, gameObject, 0f, 0f, Vector3.zero);
                }
                if (DamagePopupSpawner.Instance != null)
                {
                    DamagePopupSpawner.Instance.NotifySubEffectDamage(t, appliedChainDamage, chainCrit);
                }
                chainTransforms.Add(t.transform);
                hitCount++;
            }

            // 체인 시각: P → A → B → C 순차 spawn (한 hop 당 _chainSpawnInterval 초 지연)
            if (hitCount > 0)
            {
                StartCoroutine(SpawnChainVFXSequence(combat.transform, chainTransforms));

                if (_chainHitSfx != null)
                    AudioSource.PlayClipAtPoint(_chainHitSfx, victimPos, _sfxVolume);

                if (_chainHitStopDuration > 0f && KhiHitStopController.Instance != null)
                    KhiHitStopController.Instance.RequestFreeze(_chainHitStopDuration, _hitStopFrozenScale);
            }

            if (_logOnHitDispatch)
                Debug.Log($"[OnHit] Chain {magnitude:P0} → {targets.Count} targets, {chainDamage:F1} each");
        }

        private void ApplyBurn(Health victim, float magnitude, float duration)
        {
            // magnitude 의미: ATK 비례 계수 (0.1 = 초당 ATK 의 10%).
            // 화상 발동 시점의 ATK 를 캐시 → 도트가 끝날 때까지 그대로 사용.
            if (duration <= 0f || magnitude <= 0f) return;
            EnemyStatusEffect status = GetOrAddStatus(victim);
            if (status == null) return;
            status.EnsureVFXPrefabs(_freezeStatusVFXPrefab, _burnStatusVFXPrefab, _slowStatusVFXPrefab);
            status.SetBurnTintColor(_burnTintColor); // 인스펙터 변경 즉시 반영용 매번 push

            float playerAttack = combat != null && combat.WeaponData != null ? combat.WeaponData.BaseDamage : 0f;
            if (statContainer != null)
                playerAttack *= statContainer.GetTotalMultiplier(StatId.AttackPower);
            float damagePerTick = playerAttack * magnitude;
            if (damagePerTick <= 0f) return;

            status.ApplyBurn(damagePerTick, duration, gameObject);

            if (_burnApplySfx != null)
                AudioSource.PlayClipAtPoint(_burnApplySfx, victim.transform.position, _statusSfxVolume);

            if (_logOnHitDispatch)
                Debug.Log($"[OnHit] Burn {magnitude:P0}×ATK={damagePerTick:F1}/tick for {duration}s → {victim.name}");
        }

        private void ApplyWindBlade(Health victim, float magnitude)
        {
            if (combat == null) return;

            Vector3 victimPos = victim.transform.position;
            Vector3 playerPos = combat.transform.position;
            Vector2 dir = (Vector2)(victimPos - playerPos);
            // 겹쳤을 때 0벡터 fallback — player.right
            if (dir.sqrMagnitude < 0.0001f)
                dir = (Vector2)combat.transform.right;
            dir.Normalize();

            // CL-146: Range multiplier 적용 — Wind 검기 길이 확장 (너비는 유지). 상한 200%.
            float rangeMul = statContainer != null ? statContainer.GetCappedMultiplier(StatId.Range, 1f) : 1f;
            float effectiveLength = _windBladeLength * rangeMul;
            // 박스 영역: victim 위치에서 dir 방향으로 length/2 만큼 이동한 지점이 박스 중심
            Vector2 boxCenter = (Vector2)victimPos + dir * (effectiveLength * 0.5f);
            Vector2 boxSize = new Vector2(effectiveLength, _windBladeWidth);
            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

            // 본인 공격력 (StatModifier 합산)
            float bladeDamage = combat.WeaponData != null ? combat.WeaponData.BaseDamage * magnitude : 0f;
            if (bladeDamage <= 0f) return;

            int hitCount = 0;
            Collider2D[] hits = Physics2D.OverlapBoxAll(boxCenter, boxSize, angle);
            foreach (Collider2D col in hits)
            {
                if (col == null) continue;
                Health h = col.GetComponentInParent<Health>();
                if (h == null) continue;
                if (h == victim) continue;        // 본인 제외 (이중 데미지 방지)
                if (h.CurrentHealth <= 0f) continue;
                Character ch = h.GetComponentInParent<Character>();
                if (ch != null && ch.CharacterType == Character.CharacterTypes.Player) continue;
                // 멀티 가드 — host 측 AI 변환된 게스트 player 제외.
                if (CombatTargetable.IsAuthoritativePlayer(h)) continue;

                // 풍속 검기 — 각 대상별 크리티컬 판정.
                CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                    new CombatDamageRequest(
                        bladeDamage,
                        DamageSourceKind.SubEffect,
                        0UL,
                        sourceId: (ulong)Mathf.Abs(victim.GetInstanceID()),
                        onHitPolicy: OnHitPolicy.SuppressSubEffectLoop,
                        applyAttackPower: true,
                        hitDirection: dir,
                        hitPoint: h.transform.position,
                        weaponId: "WindBlade"),
                    statContainer);
                float appliedBladeDamage = damageResult.FinalDamage;
                bool bladeCrit = damageResult.WasCritical;
                if (_cachedRelay != null)
                {
                    _cachedRelay.RelayDamage(h, appliedBladeDamage, gameObject, 0f, 0f, Vector2.zero);
                }
                else
                {
                    h.Damage(appliedBladeDamage, gameObject, 0f, 0f, Vector3.zero);
                }
                if (DamagePopupSpawner.Instance != null)
                {
                    DamagePopupSpawner.Instance.NotifySubEffectDamage(h, appliedBladeDamage, bladeCrit);
                }
                hitCount++;
            }

            // VFX — victim 위치에서 dir 방향으로 슬래시 + Whirlwind 광역 표시 (Range 반영된 length)
            SpawnWindVFX(victimPos, dir, effectiveLength);

            if (hitCount > 0)
            {
                if (_windBladeSfx != null)
                    AudioSource.PlayClipAtPoint(_windBladeSfx, victimPos, _sfxVolume);

                if (_windHitStopDuration > 0f && KhiHitStopController.Instance != null)
                    KhiHitStopController.Instance.RequestFreeze(_windHitStopDuration, _hitStopFrozenScale);
            }

            if (_logOnHitDispatch)
                Debug.Log($"[OnHit] WindBlade {magnitude:P0} dir={dir} → {hitCount} hits, {bladeDamage:F1} each");
        }

        // ── VFX 스폰 (CL-201) ──────────────────────────────

        /// <summary>
        /// CL-201: Player → A → B → C 순차 체인 라인 spawn. 데미지는 호출자가 즉시 처리,
        /// 본 코루틴은 시각만 전파 효과로 표현. Transform 추적으로 라인 양 끝이 적/플레이어 이동을 따라감.
        /// P→A 거리가 임계값 초과 시 P→A 생략 (원거리 자연스러움).
        /// chainTransforms[0] 은 첫 victim, [1..]은 추가 체인 타겟.
        /// </summary>
        private IEnumerator SpawnChainVFXSequence(Transform playerT, List<Transform> chainTransforms)
        {
            if (chainTransforms == null || chainTransforms.Count == 0) yield break;

            // P → 첫 victim. 거리 임계값 안일 때만.
            Transform firstT = chainTransforms[0];
            if (playerT != null && firstT != null
                && Vector3.Distance(playerT.position, firstT.position) <= _chainPlayerLineMaxDistance)
            {
                SpawnChainVFX(playerT, firstT);
                if (_chainSpawnInterval > 0f) yield return new WaitForSeconds(_chainSpawnInterval);
            }

            // 순차 jump: i-1 → i
            for (int i = 1; i < chainTransforms.Count; i++)
            {
                SpawnChainVFX(chainTransforms[i - 1], chainTransforms[i]);
                if (_chainSpawnInterval > 0f) yield return new WaitForSeconds(_chainSpawnInterval);
            }
        }

        /// <summary>Transform 추적: JaggedLightningLine 이 매 프레임 양 끝점 갱신.</summary>
        private void SpawnChainVFX(Transform fromT, Transform toT)
        {
            if (_chainHitVFXPrefab == null || fromT == null || toT == null) return;
            Vector3 spawnPos = fromT.position;
            spawnPos.z = 0f;

            GameObject go = VFXSpawner.Spawn(_chainHitVFXPrefab, spawnPos, Quaternion.identity, 0.3f);
            if (go == null) return;
            VFXSpawner.ApplyGameplayEffectSorting(go);

            JaggedLightningLine jagged = go.GetComponentInChildren<JaggedLightningLine>();
            if (jagged != null)
            {
                jagged.Init(fromT, toT);
                return;
            }

            // 폴백 (JaggedLightningLine 미부착 prefab): 단순 2점 직선, 정적
            LineRenderer lr = go.GetComponentInChildren<LineRenderer>();
            if (lr != null)
            {
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.SetPosition(0, fromT.position);
                lr.SetPosition(1, toT.position);
            }
        }

        /// <summary>정적 Vector3 버전 — 호출자가 Transform 없이 위치만 알 때 (현재 미사용, 호환용 유지).</summary>
        private void SpawnChainVFX(Vector3 from, Vector3 to)
        {
            if (_chainHitVFXPrefab == null) return;
            from.z = 0f;
            to.z = 0f;

            GameObject go = VFXSpawner.Spawn(_chainHitVFXPrefab, from, Quaternion.identity, 0.3f);
            if (go == null) return;
            VFXSpawner.ApplyGameplayEffectSorting(go);

            JaggedLightningLine jagged = go.GetComponentInChildren<JaggedLightningLine>();
            if (jagged != null)
            {
                jagged.Init(from, to);
                return;
            }

            LineRenderer lr = go.GetComponentInChildren<LineRenderer>();
            if (lr != null)
            {
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.SetPosition(0, from);
                lr.SetPosition(1, to);
            }
        }

        private void SpawnWindVFX(Vector3 origin, Vector2 dir, float length)
        {
            if (_windAOEVFXPrefab == null) return;
            origin.z = 0f;

            float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            GameObject go = VFXSpawner.Spawn(
                _windAOEVFXPrefab,
                origin,
                Quaternion.Euler(0f, 0f, angle),
                _windVFXLifetime);
            if (go == null) return;
            VFXSpawner.ApplyGameplayEffectSorting(go);

            LineRenderer lr = go.GetComponentInChildren<LineRenderer>();
            if (lr != null)
            {
                Vector3 endPoint = origin + (Vector3)(dir * length);
                lr.useWorldSpace = true;
                lr.positionCount = 2;
                lr.SetPosition(0, origin);
                lr.SetPosition(1, endPoint);
            }

            // Whirlwind 자식 ParticleSystem 의 Shape.radius 동적 조정 (Range 스탯 반영)
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>())
            {
                if (ps.gameObject.name == "Whirlwind")
                {
                    ParticleSystem.ShapeModule shape = ps.shape;
                    shape.radius = _windBladeWidth * 1.5f;
                    break;
                }
            }
        }

        private static EnemyStatusEffect GetOrAddStatus(Health victim)
        {
            if (victim == null) return null;
            // Health 가 적 root 또는 하위 노드에 있을 수 있음 → root 부터 검색
            GameObject host = victim.gameObject;
            EnemyStatusEffect status = host.GetComponent<EnemyStatusEffect>()
                                    ?? host.GetComponentInParent<EnemyStatusEffect>();
            if (status == null)
                status = host.AddComponent<EnemyStatusEffect>();
            return status;
        }

        private static List<Health> FindNearbyEnemies(Vector3 origin, Health exclude, float radius, int maxCount)
        {
            int hits = Physics2D.OverlapCircleNonAlloc(origin, radius, _chainBuf);
            var candidates = new List<(Health h, float distSq)>();
            var seen = new HashSet<Health>();
            for (int i = 0; i < hits; i++)
            {
                Collider2D col = _chainBuf[i];
                if (col == null) continue;
                Health h = col.GetComponentInParent<Health>();
                if (h == null) continue;
                if (h == exclude) continue;
                if (h.CurrentHealth <= 0f) continue;
                // 같은 Health 의 여러 collider 가 잡혀도 1회만 카운트.
                if (!seen.Add(h)) continue;
                // Player 자신 제외 — Player Health 도 잡힐 수 있으니 Character.CharacterType 체크
                Character ch = h.GetComponentInParent<Character>();
                if (ch != null && ch.CharacterType == Character.CharacterTypes.Player) continue;
                // 멀티 가드 — host 측 AI 변환된 게스트 player 제외 (B-2 의 convertNonOwnerToAi 우회).
                if (CombatTargetable.IsAuthoritativePlayer(h)) continue;
                // PvP 미상정 — 4인 안전벨트: PlayerHealthSync 명시 체크.
                if (CombatTargetable.IsFriendlyPlayer(h)) continue;

                float dSq = (h.transform.position - origin).sqrMagnitude;
                candidates.Add((h, dSq));
            }
            candidates.Sort((a, b) => a.distSq.CompareTo(b.distSq));
            return candidates.Take(maxCount).Select(t => t.h).ToList();
        }
    }
}
