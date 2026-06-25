using System.Collections;
using LostMemory.Combat;
using LostMemory.TestKhi;
using LostMemory.VFX;
using MoreMountains.TopDownEngine;
using Unity.Netcode;
using UnityEngine;

namespace LostMemory.Enemies
{
    /// <summary>
    /// CL-142/143: 적의 Slow / Freeze / Burn status 관리.
    /// CL-202: VFX 정식 교체 — placeholder SpriteRenderer 대신 OnHitEffectRegistry 가 주입한
    ///          VFX 프리팹을 SpawnAttached 로 적에 부착. 다중 상태이상 시 정책 D 로 가장 최근 1개만 표시.
    ///
    /// OnHitEffectRegistry 가 첫 hit 시점에 자동 부착 (GetOrAdd) → 적 prefab 수정 불필요.
    /// 동일 시점에 EnsureVFXPrefabs 로 freeze/burn/slow 프리팹 참조 1회 바인딩.
    ///
    /// TDE CharacterMovement.MovementSpeedMultiplier 를 조작해 이속 변화/정지 적용.
    /// 빙결 시 multiplier=0 으로 강제 정지.
    ///
    /// 중첩 정책 (사용자 결정):
    /// - Slow: 더 강한 magnitude 만 유지, 시간은 max 유지
    /// - Freeze: 새 시간으로 갱신
    /// - Burn: 항상 갱신 (가장 최근 화상으로 교체)
    /// - VFX 표시 우선순위: Freeze &gt; Burn &gt; Slow (정책 D — 효과는 모두 적용 / VFX 1개)
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EnemyStatusEffect : MonoBehaviour
    {
        [Tooltip("CL-202: VFX 만료 시 페이드아웃 시간(초). 정책 D 로 다른 효과 VFX 로 전환할 때도 사용.")]
        [SerializeField, Min(0f)] private float _vfxFadeOutSeconds = 0.25f;

        // CL-202: Slow / Burn tint 색은 OnHitEffectRegistry 가 SetXxxTintColor 로 push 한다.
        // (이 컴포넌트는 런타임 AddComponent 라 인스펙터에서 디자인 타임 와이어링 불가)
        private Color _slowTintColor = new Color(0.7f, 0.9f, 1f, 1f);
        private Color _burnTintColor = new Color(1f, 0.6f, 0.4f, 1f);

        private CharacterMovement _movement;
        private Health _health;
        private float _baseSpeedMultiplier = 1f;
        private bool _baseCaptured;

        private float _slowMagnitude;
        private float _slowExpiresAt;
        private float _freezeExpiresAt;

        // CL-143 burn 상태
        private float _burnDamagePerTick;
        private float _burnExpiresAt;
        private float _burnNextTickAt;
        private GameObject _burnInstigator;
        private PlayerStatModifierContainer _burnAttackerStats;
        private ulong _burnAttackerNetworkObjectId;
        private ulong _burnTargetNetworkObjectId;
        private ulong _burnSourceId;
        private int _burnTickIndex;

        // CL-202: VFX 프리팹 참조 (OnHitEffectRegistry.EnsureVFXPrefabs 로 주입)
        private GameObject _freezeVFXPrefab;
        private GameObject _burnVFXPrefab;
        private GameObject _slowVFXPrefab;
        private bool _vfxPrefabsBound;

        // CL-202: 정책 D — 활성 VFX 1개 추적
        private enum StatusVisualType { None, Freeze, Burn, Slow }
        private StatusVisualType _activeVisualType = StatusVisualType.None;
        private GameObject _activeVisualInstance;

        // CL-202: Slow 시 적 main sprite tint 처리 (Slow 는 prefab 대신 sprite 변색)
        private SpriteRenderer _enemyMainSprite;
        private Color _enemyOriginalColor;
        private bool _enemyColorCached;

        // 보스는 모든 상태이상(slow/freeze/burn) 면역. 부모 체인에 "Boss" 태그 GO 가 있으면 차단.
        private const string BossTag = "Boss";
        private bool _isBoss;

        private void Awake()
        {
            _isBoss = IsBossInParents();

            _movement = GetComponentInParent<CharacterMovement>();
            if (_movement != null)
            {
                _baseSpeedMultiplier = _movement.MovementSpeedMultiplier;
                _baseCaptured = true;
            }
            _health = GetComponent<Health>() ?? GetComponentInParent<Health>();

            // CL-202: 적 깨끗한 시점의 sprite 색을 캐시 (Slow tint 후 복원용)
            TryCacheEnemySprite();
        }

        private bool IsBossInParents()
        {
            for (Transform t = transform; t != null; t = t.parent)
            {
                if (t.CompareTag(BossTag)) return true;
            }
            return false;
        }

        private void OnDestroy()
        {
            // 활성 VFX 는 적 자식이라 부모 destroy 시 함께 사라지지만, 명시적 cleanup.
            if (_activeVisualInstance != null) Destroy(_activeVisualInstance);
            // Slow / Burn tint 가 적용된 채 destroy 되어도 적 본체도 함께 사라지므로 색 복원은 안전 차원.
            // 둘 다 originalColor 로 복원하므로 어느 쪽이 active 였든 무관.
            RemoveSlowSpriteTint();
        }

        // ── VFX 프리팹 주입 (OnHitEffectRegistry 호출) ──────────

        /// <summary>
        /// CL-202: OnHitEffectRegistry 가 ApplyXxx 호출 직전 한 번 주입.
        /// idempotent — 두 번째 호출부터는 noop.
        /// </summary>
        public void EnsureVFXPrefabs(GameObject freeze, GameObject burn, GameObject slow)
        {
            if (_vfxPrefabsBound) return;
            _freezeVFXPrefab = freeze;
            _burnVFXPrefab = burn;
            _slowVFXPrefab = slow;
            _vfxPrefabsBound = true;
        }

        /// <summary>
        /// CL-202: Slow 시 적 main sprite 에 적용할 tint 색.
        /// OnHitEffectRegistry 가 ApplySlow 마다 push — 인스펙터 변경 시 다음 ApplySlow 부터 즉시 반영.
        /// LateUpdate 가 매 프레임 이 값을 읽기 때문에 활성 중인 슬로우 적도 다음 프레임에 색 갱신됨.
        /// </summary>
        public void SetSlowTintColor(Color color)
        {
            _slowTintColor = color;
        }

        /// <summary>
        /// CL-202: Burn 시 적 main sprite 에 적용할 tint 색.
        /// OnHitEffectRegistry 가 ApplyBurn 마다 push — 인스펙터 변경 시 다음 ApplyBurn 부터 즉시 반영.
        /// </summary>
        public void SetBurnTintColor(Color color)
        {
            _burnTintColor = color;
        }

        // ── 등록 API (OnHitEffectRegistry 호출) ─────────────

        public void ApplySlow(float magnitude, float duration)
        {
            if (_isBoss) return;

            // 더 강한 슬로우 유지 (또는 기존 만료 시 갱신)
            if (magnitude > _slowMagnitude || Time.time >= _slowExpiresAt)
            {
                _slowMagnitude = magnitude;
            }
            _slowExpiresAt = Mathf.Max(_slowExpiresAt, Time.time + duration);
            ApplyMovementMultiplier();
            RefreshActiveVisual();
        }

        public void ApplyFreeze(float durationSeconds)
        {
            if (_isBoss) return;

            // 갱신 정책: 항상 새 시간으로 (사용자 결정)
            _freezeExpiresAt = Time.time + durationSeconds;
            ApplyMovementMultiplier();
            RefreshActiveVisual();
        }

        public void ApplyBurn(float damagePerTick, float duration, GameObject instigator)
        {
            if (_isBoss) return;
            if (damagePerTick <= 0f || duration <= 0f) return;

            // CL-143: 중첩 없음 정책 — 항상 갱신 (가장 최근 화상으로 교체)
            _burnDamagePerTick = damagePerTick;
            _burnExpiresAt = Time.time + duration;
            _burnNextTickAt = Time.time + 1f;        // 첫 틱은 1초 뒤
            _burnInstigator = instigator;
            _burnAttackerStats = instigator != null
                ? instigator.GetComponentInParent<PlayerStatModifierContainer>()
                : null;
            _burnAttackerNetworkObjectId = ResolveNetworkObjectId(instigator);
            _burnTargetNetworkObjectId = ResolveNetworkObjectId(_health != null ? _health.gameObject : gameObject);
            _burnSourceId = ResolveBurnSourceId(instigator);
            _burnTickIndex = 0;
            RefreshActiveVisual();
        }

        // ── 만료 처리 ───────────────────────────────────────

        private void Update()
        {
            bool moveChanged = false;
            bool visualChanged = false;

            if (_slowMagnitude > 0f && Time.time >= _slowExpiresAt)
            {
                _slowMagnitude = 0f;
                moveChanged = true;
                visualChanged = true;
            }
            if (_freezeExpiresAt > 0f && Time.time >= _freezeExpiresAt)
            {
                _freezeExpiresAt = 0f;
                moveChanged = true;
                visualChanged = true;
            }
            if (moveChanged) ApplyMovementMultiplier();

            // Burn 도트 처리 (CL-143)
            if (_burnExpiresAt > 0f)
            {
                if (IsBurnInstigatorBlocked() || Time.time >= _burnExpiresAt)
                {
                    _burnExpiresAt = 0f;
                    _burnDamagePerTick = 0f;
                    visualChanged = true;
                }
                else if (Time.time >= _burnNextTickAt)
                {
                    _burnNextTickAt = Time.time + 1f;
                    _burnTickIndex++;
                    if (_health != null && _health.CurrentHealth > 0f)
                    {
                        CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                            new CombatDamageRequest(
                                _burnDamagePerTick,
                                DamageSourceKind.DamageOverTime,
                                _burnTargetNetworkObjectId,
                                attackerNetworkObjectId: _burnAttackerNetworkObjectId,
                                sourceId: _burnSourceId,
                                tickIndex: _burnTickIndex,
                                criticalPolicy: CriticalPolicy.RollEveryDamageTick,
                                onHitPolicy: OnHitPolicy.Suppress,
                                applyAttackPower: false,
                                hitPoint: transform.position,
                                weaponId: "Burn"),
                            _burnAttackerStats);
                        _health.Damage(damageResult.FinalDamage, _burnInstigator, 0f, 0f, Vector3.zero);
                    }
                }
            }

            if (visualChanged) RefreshActiveVisual();
        }

        private bool IsBurnInstigatorBlocked()
        {
            return _burnInstigator != null
                && KhiPlayerActionGate.IsBlocked(_burnInstigator.transform);
        }

        private static ulong ResolveNetworkObjectId(GameObject go)
        {
            if (go == null) return 0UL;
            NetworkObject netObj = go.GetComponentInParent<NetworkObject>();
            return netObj != null ? netObj.NetworkObjectId : 0UL;
        }

        private ulong ResolveBurnSourceId(GameObject instigator)
        {
            ulong instigatorId = ResolveNetworkObjectId(instigator);
            if (instigatorId != 0UL) return instigatorId;
            int rawId = instigator != null ? instigator.GetInstanceID() : GetInstanceID();
            return (ulong)Mathf.Abs(rawId);
        }

        /// <summary>
        /// CL-202: Slow / Burn tint 는 LateUpdate 에서 매 프레임 강제 — 다른 시스템(Health 데미지 플리커 등)이
        /// Update 에서 색을 덮어써도 LateUpdate 가 프레임 가장 마지막이라 우리 tint 가 최종 결과.
        /// </summary>
        private void LateUpdate()
        {
            if (_enemyMainSprite == null) return;

            switch (_activeVisualType)
            {
                case StatusVisualType.Slow: _enemyMainSprite.color = _slowTintColor; break;
                case StatusVisualType.Burn: _enemyMainSprite.color = _burnTintColor; break;
            }
        }

        // ── Movement multiplier + Visual 조정 ───────────────

        private void ApplyMovementMultiplier()
        {
            if (_movement == null)
            {
                // Awake 시점에 부모에 CharacterMovement 가 없었을 수 있음 (자동 부착 케이스)
                _movement = GetComponentInParent<CharacterMovement>();
                if (_movement == null) return;
                if (!_baseCaptured)
                {
                    _baseSpeedMultiplier = _movement.MovementSpeedMultiplier;
                    _baseCaptured = true;
                }
            }

            bool isFrozen = Time.time < _freezeExpiresAt;
            float slowFactor = (_slowMagnitude > 0f && Time.time < _slowExpiresAt)
                ? Mathf.Clamp(1f - _slowMagnitude, 0f, 1f)
                : 1f;
            float effectiveMul = isFrozen ? 0f : (_baseSpeedMultiplier * slowFactor);
            _movement.MovementSpeedMultiplier = effectiveMul;
        }

        // ── 정책 D: 활성 VFX 1개 관리 (CL-202) ──────────────

        /// <summary>
        /// 현재 활성 효과를 평가해 가장 우선순위 높은 1개의 VFX 만 표시.
        /// 우선순위: Freeze &gt; Burn &gt; Slow. 효과는 모두 적용되지만 시각만 1개.
        /// </summary>
        private void RefreshActiveVisual()
        {
            bool freezeActive = _freezeExpiresAt > 0f && Time.time < _freezeExpiresAt;
            bool burnActive = _burnExpiresAt > 0f && Time.time < _burnExpiresAt;
            bool slowActive = _slowMagnitude > 0f && Time.time < _slowExpiresAt;

            if (freezeActive)
                SetActiveVisual(StatusVisualType.Freeze, _freezeVFXPrefab);
            else if (burnActive)
                SetActiveVisual(StatusVisualType.Burn, _burnVFXPrefab);
            else if (slowActive)
                SetActiveVisual(StatusVisualType.Slow, _slowVFXPrefab);
            else
                SetActiveVisual(StatusVisualType.None, null);
        }

        private void SetActiveVisual(StatusVisualType type, GameObject prefab)
        {
            // Slow / Burn 은 instance 가 null 일 수 있음 (sprite tint 만 사용). type 만 비교.
            if (_activeVisualType == type) return;

            StatusVisualType prevType = _activeVisualType;

            // 기존 prefab visual 은 자체 페이드아웃 코루틴이 끝나면 destroy. 병렬 실행 무관.
            if (_activeVisualInstance != null)
            {
                StartCoroutine(FadeOutAndDestroy(_activeVisualInstance, _vfxFadeOutSeconds));
            }

            // Sprite tint 진입/이탈 — 떠나는 type 의 tint 제거 후 들어오는 type 의 tint 적용
            OnLeaveVisualType(prevType);
            OnEnterVisualType(type);

            _activeVisualType = type;
            _activeVisualInstance = (type == StatusVisualType.None || prefab == null)
                ? null
                : VFXSpawner.SpawnAttached(prefab, transform);
        }

        private void OnEnterVisualType(StatusVisualType type)
        {
            switch (type)
            {
                case StatusVisualType.Slow: ApplySlowSpriteTint(); break;
                case StatusVisualType.Burn: ApplyBurnSpriteTint(); break;
            }
        }

        private void OnLeaveVisualType(StatusVisualType type)
        {
            switch (type)
            {
                case StatusVisualType.Slow: RemoveSlowSpriteTint(); break;
                case StatusVisualType.Burn: RemoveBurnSpriteTint(); break;
            }
        }

        private void TryCacheEnemySprite()
        {
            if (_enemyColorCached) return;
            if (_isBoss) return;  // 보스는 tint 안 씀 — 캐싱 자체 스킵

            // 1순위: Animator 가 붙은 GameObject 의 SpriteRenderer.
            // "애니메이션이 구동되는 sprite = 본체" 라는 의도가 명확히 드러나며,
            // 휴리스틱(이름 블랙리스트 + bounds 비교) 보다 안정적.
            // 프로젝트 컨벤션상 Animator + 본체 SpriteRenderer 가 같은 GO 에 붙음
            // (e.g. OrcModel, BerthaSprite).
            Animator animator = GetComponentInChildren<Animator>(includeInactive: true);
            SpriteRenderer best = null;
            if (animator != null)
            {
                best = animator.GetComponent<SpriteRenderer>();
                if (best == null)
                    best = animator.GetComponentInChildren<SpriteRenderer>(includeInactive: true);
            }

            // 2순위 fallback: Animator 컨벤션을 따르지 않는 prefab 대비.
            // 기존 로직 — 이름으로 보조 sprite 제외 후 bounds 가장 큰 것 선택.
            if (best == null)
            {
                SpriteRenderer[] candidates = GetComponentsInChildren<SpriteRenderer>(includeInactive: true);
                float bestArea = 0f;

                foreach (SpriteRenderer sr in candidates)
                {
                    if (sr == null || sr.sprite == null) continue;
                    string n = sr.gameObject.name;
                    if (n.IndexOf("shadow", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.IndexOf("vfx", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.IndexOf("effect", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.IndexOf("status", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (n.IndexOf("iceblock", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;

                    Bounds b = sr.bounds;
                    float area = b.size.x * b.size.y;
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = sr;
                    }
                }
            }

            if (best != null)
            {
                _enemyMainSprite = best;
                _enemyOriginalColor = best.color;
                _enemyColorCached = true;
            }
            else
            {
                Debug.LogWarning($"[EnemyStatusEffect] tint 적용할 SpriteRenderer 0개 (Animator 기반 + fallback 모두 실패) host={gameObject.name}");
            }
        }

        private void ApplySlowSpriteTint()
        {
            TryCacheEnemySprite();
            if (_enemyMainSprite != null)
                _enemyMainSprite.color = _slowTintColor;
        }

        private void RemoveSlowSpriteTint()
        {
            if (_enemyColorCached && _enemyMainSprite != null)
                _enemyMainSprite.color = _enemyOriginalColor;
        }

        private void ApplyBurnSpriteTint()
        {
            TryCacheEnemySprite();
            if (_enemyMainSprite != null)
                _enemyMainSprite.color = _burnTintColor;
        }

        private void RemoveBurnSpriteTint()
        {
            if (_enemyColorCached && _enemyMainSprite != null)
                _enemyMainSprite.color = _enemyOriginalColor;
        }

        private IEnumerator FadeOutAndDestroy(GameObject vfx, float duration)
        {
            if (vfx == null) yield break;

            SpriteRenderer[] renderers = vfx.GetComponentsInChildren<SpriteRenderer>();
            ParticleSystem[] particleSystems = vfx.GetComponentsInChildren<ParticleSystem>();

            // ParticleSystem 은 emission 만 멈춰서 자연스럽게 흩어지게
            foreach (ParticleSystem ps in particleSystems)
            {
                if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }

            Color[] startColors = new Color[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null) startColors[i] = renderers[i].color;
            }

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = 1f - Mathf.Clamp01(t / duration);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null) continue;
                    Color c = startColors[i];
                    renderers[i].color = new Color(c.r, c.g, c.b, c.a * k);
                }
                yield return null;
            }

            if (vfx != null) Destroy(vfx);
        }
    }
}
