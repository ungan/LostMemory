using System.Collections;
using LostMemory.Combat;
using LostMemory.Networking.Player;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.TestKhi
{
    /// <summary>
    /// 메테오 스킬: 마우스 worldPos 에 생성 → warningDuration 동안 예고 표시 → 폭발 (OverlapCircleAll 데미지).
    /// KhiStaffController 가 Instantiate → Detonate(...) 호출로 트리거.
    /// 예고 sprite 와 폭발 sprite 는 선택 — 시각 placeholder 없어도 데미지는 작동.
    /// </summary>
    [AddComponentMenu("Lost Memory/Test Khi/Khi Meteor")]
    public class KhiMeteor : MonoBehaviour
    {
        [Header("Hit Detection")]
        [SerializeField] private LayerMask targetLayers = ~0;
        [SerializeField, Min(0f)] private float targetFlickerDuration = 0f;
        [SerializeField, Min(0f)] private float targetInvincibilityDuration = 0f;

        [Header("Timing")]
        [Tooltip("예고 시간 (초). 이 동안 플레이어가 위치를 보고 회피 가능.")]
        [SerializeField, Min(0.1f)] private float warningDuration = 1f;
        [Tooltip("폭발 sprite 유지 시간.")]
        [SerializeField, Min(0.05f)] private float explosionHold = 0.3f;

        [Header("Shape")]
        [SerializeField, Min(0.1f)] private float damageRadius = 2f;
        [SerializeField, Min(0f)] private float damage = 50f;

        [Header("Visual (optional — sprite 비워둬도 동작)")]
        [Tooltip("비워두면 Awake 에서 자동 생성 (반투명 빨간 원).")]
        [SerializeField] private SpriteRenderer warningSprite;
        [Tooltip("비워두면 Awake 에서 자동 생성 (밝은 주황 원).")]
        [SerializeField] private SpriteRenderer explosionSprite;
        [Tooltip("Sprite 의 기본 크기 (예: 1 unit 짜리 sprite 면 1). damageRadius 와 비례 스케일 계산. 자동 생성 sprite 는 1.")]
        [SerializeField, Min(0.01f)] private float visualUnitSize = 1f;
        [Tooltip("자동 생성된 원 sprite 의 예고 색.")]
        [SerializeField] private Color warningColor = new Color(1f, 0f, 0f, 0.4f);
        [Tooltip("자동 생성된 원 sprite 의 폭발 색.")]
        [SerializeField] private Color explosionColor = new Color(1f, 0.6f, 0f, 0.75f);
        [SerializeField] private int warningSortingOrder = 100;
        [SerializeField] private int explosionSortingOrder = 101;

        [Header("Warning Pulse (커졌다 작아졌다)")]
        [Tooltip("초당 펄스 사이클 속도. 0 이면 펄스 X.")]
        [SerializeField, Min(0f)] private float warningPulseSpeed = 3f;
        [Tooltip("base scale 기준 진동 폭. 0.15 = ±15% 진동.")]
        [SerializeField, Range(0f, 1f)] private float warningPulseAmplitude = 0.15f;

        [Header("Magic Circle (회전 자식 — 옵션)")]
        [Tooltip("회전할 자식 Transform. 비워두면 회전 X.")]
        [SerializeField] private Transform magicCircleTransform;
        [Tooltip("초당 회전 각도 (Z축). 180 = 2 초 1 회전. 음수면 반대 방향.")]
        [SerializeField] private float magicCircleRotationSpeed = 180f;

        [Header("Explosion Animator (옵션 — Fireball.controller 재사용)")]
        [Tooltip("폭발 시 state 전환할 Animator. 비워두면 단순 sprite enable.")]
        [SerializeField] private Animator explosionAnimator;
        [Tooltip("Animator.Play 호출할 state 이름.")]
        [SerializeField] private string explosionStateName = "fireball-destroy";

        [Header("Falling Meteor (Optional — null 이면 기존 동작)")]
        [Tooltip("낙하할 메테오 시각 prefab. KhiFireball.prefab 추천 (자체 Animator 자동 재생).")]
        [SerializeField] private GameObject meteorBodyPrefab;
        [Tooltip("마우스 위치(=transform.position)에서 fallDirection 방향으로 이 거리만큼 떨어진 곳에서 낙하 시작.")]
        [SerializeField, Min(1f)] private float fallHeight = 8f;
        [Tooltip("(0, 1) = 위에서 직선 / (-0.3, 1) = 우상→좌하 사선.")]
        [SerializeField] private Vector2 fallDirection = Vector2.up;
        [Tooltip("낙하 중 Z축 회전 속도 (deg/sec). 0 = 회전 X.")]
        [SerializeField] private float fallRotationSpeed = 0f;

        [Header("Debug")]
        [Tooltip("ApplyDamage 단계별 진단 로그.")]
        [SerializeField] private bool logMeteorEvents = false;

        private GameObject _attacker;
        // 멀티: 게스트 owner 측에서 호스트로 데미지 위임. attacker (player) 의 PlayerDamageRelay 를 Detonate 에서 1회 캐시.
        private PlayerDamageRelay _cachedRelay;
        // 크리티컬 — Detonate 시 1회 판정. AOE 전체가 같은 결과 (평타와 동일 패턴).
        private bool _wasCritical;
        private bool _detonated;
        private bool _warningActive;
        private Vector3 _warningBaseScale = Vector3.one;
        private Vector3 _magicCircleBaseScale = Vector3.one;
        // Multiplayer: non-owner 측 시각 전용. ApplyDamage 만 skip.
        private bool _visualOnly;

        private static Sprite _cachedCircleSprite;

        private void Awake()
        {
            // prefab 의 SpriteRenderer 가 sprite 미할당이면 procedural 원 sprite 자동 fallback.
            // sprite 가 이미 있으면 그걸 그대로 사용 (사용자 교체 우선).
            if (warningSprite != null && warningSprite.sprite == null)
            {
                warningSprite.sprite = GetCircleSprite();
            }
            if (explosionSprite != null && explosionSprite.sprite == null)
            {
                explosionSprite.sprite = GetCircleSprite();
            }

            if (warningSprite != null)
            {
                _warningBaseScale = warningSprite.transform.localScale;
                warningSprite.enabled = false;
            }
            if (explosionSprite != null) explosionSprite.enabled = false;
            if (magicCircleTransform != null)
            {
                _magicCircleBaseScale = magicCircleTransform.localScale;
                magicCircleTransform.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (!_warningActive) return;

            // 1) Warning 펄스
            if (warningSprite != null && warningPulseSpeed > 0f && warningPulseAmplitude > 0f)
            {
                float pulse = 1f + Mathf.Sin(Time.time * warningPulseSpeed * Mathf.PI * 2f) * warningPulseAmplitude;
                warningSprite.transform.localScale = _warningBaseScale * pulse;
            }

            // 2) MagicCircle 회전
            if (magicCircleTransform != null && magicCircleRotationSpeed != 0f)
            {
                magicCircleTransform.Rotate(0f, 0f, magicCircleRotationSpeed * Time.deltaTime);
            }
        }

        /// <summary>procedural 흰 원 sprite (1 unit 직경). 한 번만 생성, 모든 메테오 공유.</summary>
        private static Sprite GetCircleSprite()
        {
            if (_cachedCircleSprite != null) return _cachedCircleSprite;
            const int size = 64;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "KhiMeteorCircle",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            Color clear = new Color(0f, 0f, 0f, 0f);
            float r = size * 0.5f;
            Vector2 c = new Vector2(r, r);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                    float a = Mathf.Clamp01(r - d);
                    tex.SetPixel(x, y, a > 0f ? new Color(1f, 1f, 1f, a) : clear);
                }
            }
            tex.Apply();
            _cachedCircleSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
            _cachedCircleSprite.name = "KhiMeteorCircle";
            return _cachedCircleSprite;
        }

        /// <summary>
        /// 메테오 발동. 사용자 정의 damage/radius 가 0 보다 크면 직렬화 기본값을 덮어쓴다.
        /// </summary>
        public void Detonate(float damageOverride, float radiusOverride, GameObject attacker)
        {
            if (_detonated) return;
            _detonated = true;
            if (damageOverride > 0f) damage = damageOverride;
            if (radiusOverride > 0f) damageRadius = radiusOverride;
            _attacker = attacker;
            _cachedRelay = attacker != null ? attacker.GetComponentInParent<PlayerDamageRelay>() : null;
            // 발동 시 1회 크리티컬 판정 — AOE 전체에 동일 적용.
            PlayerStatModifierContainer stats = attacker != null
                ? attacker.GetComponentInParent<PlayerStatModifierContainer>() : null;
            // AttackPower 적용 — 평타 패턴 통일. 이전 누락분 fix.
            CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                new CombatDamageRequest(
                    damage,
                    DamageSourceKind.Area,
                    0UL,
                    applyAttackPower: true,
                    hitPoint: transform.position,
                    weaponId: "Meteor"),
                stats);
            damage = damageResult.FinalDamage;
            _wasCritical = damageResult.WasCritical;
            StartCoroutine(Sequence());
        }

        /// <summary>
        /// Multiplayer 시각 전용 clone 으로 설정. non-owner 측에서 Detonate 호출 전에 부른다.
        /// ApplyDamage 만 skip — warning/falling/explosion 시각은 그대로 재생.
        /// damage 권위는 owner 측 한 군데서만 처리 → double-hit 방지.
        /// </summary>
        public void SetVisualOnly(bool visualOnly)
        {
            _visualOnly = visualOnly;
        }

        private IEnumerator Sequence()
        {
            ShowWarning(true);
            StartCoroutine(FallMeteor(warningDuration));
            yield return new WaitForSeconds(warningDuration);
            ShowWarning(false);

            ShowExplosion(true);
            ApplyDamage();
            yield return new WaitForSeconds(explosionHold);

            Destroy(gameObject);
        }

        /// <summary>
        /// 낙하 시각 처리. MeteorExplosion 자식 (explosionSprite) 를 직접 이동시킴.
        /// 시작 localPosition = fallDirection * fallHeight, 종료 localPosition = Vector3.zero.
        /// 도착 후 ShowExplosion 이 같은 GameObject 에 폭발 state 재생.
        /// 별도 prefab Instantiate 안 함 — 메모리 효율 + prefab 자체 시각 그대로.
        /// </summary>
        private IEnumerator FallMeteor(float duration)
        {
            if (explosionSprite == null || duration <= 0f) yield break;

            Transform body = explosionSprite.transform;
            Vector3 startLocal = (Vector3)(fallDirection.normalized * fallHeight);
            Vector3 endLocal = Vector3.zero;

            explosionSprite.enabled = true;
            body.localPosition = startLocal;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                body.localPosition = Vector3.Lerp(startLocal, endLocal, t);
                if (fallRotationSpeed != 0f)
                {
                    body.Rotate(0f, 0f, fallRotationSpeed * Time.deltaTime);
                }
                yield return null;
            }
            body.localPosition = endLocal;
        }

        private void ShowWarning(bool show)
        {
            _warningActive = show;
            if (warningSprite != null) warningSprite.enabled = show;
            if (magicCircleTransform != null) magicCircleTransform.gameObject.SetActive(show);
        }

        private void ShowExplosion(bool show)
        {
            if (explosionSprite != null) explosionSprite.enabled = show;
            if (show && explosionAnimator != null && !string.IsNullOrEmpty(explosionStateName))
            {
                explosionAnimator.Play(explosionStateName);
            }
        }

        private void ApplyDamage()
        {
            // Multiplayer 시각 전용 clone — damage 권위는 owner 측만. non-owner 의 OverlapCircleAll 결과는 무시.
            if (_visualOnly) return;

            Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, damageRadius, targetLayers);
            if (logMeteorEvents) Debug.Log($"[Meteor] pos={transform.position} radius={damageRadius} → {hits.Length} colliders");

            // 같은 적이 collider 여러 개로 잡힐 때 이중 hit 방지 (몸 + hitbox 자식 등).
            System.Collections.Generic.HashSet<Health> appliedTargets = new System.Collections.Generic.HashSet<Health>();
            int applied = 0;
            for (int i = 0; i < hits.Length; i++)
            {
                Collider2D c = hits[i];
                if (c == null) continue;

                string lname = LayerMask.LayerToName(c.gameObject.layer);
                Health health = c.GetComponentInParent<Health>();
                if (health == null)
                {
                    if (logMeteorEvents) Debug.Log($"  hit {c.name}(L:{lname}) → no Health");
                    continue;
                }
                if (!appliedTargets.Add(health))
                {
                    if (logMeteorEvents) Debug.Log($"  hit {c.name}(L:{lname}) → already applied to '{health.name}' this detonation, skip");
                    continue;
                }
                if (_attacker != null && IsOwnedByAttacker(health, _attacker))
                {
                    if (logMeteorEvents) Debug.Log($"  hit {c.name}(L:{lname}) → owned by attacker");
                    continue;
                }
                // PvP 미상정 — 다른 player 도 친아군 skip.
                if (CombatTargetable.IsFriendlyPlayer(health))
                {
                    if (logMeteorEvents) Debug.Log($"  hit {c.name}(L:{lname}) → friendly player skip");
                    continue;
                }
                // 게스트가 ServerRpc 로 호스트에 위임할 경로면 자체 CanTakeDamageThisFrame 가드 우회 — 비-server 측 target Health 는
                // MonsterHealthSync 가 DamageDisabled() 호출했기 때문에 항상 false 가 되어 RelayDamage 도달 전에 차단된다.
                bool willRelayToServer = _cachedRelay != null && _cachedRelay.IsSpawned && !_cachedRelay.IsServer;
                if (!willRelayToServer && !health.CanTakeDamageThisFrame())
                {
                    if (logMeteorEvents) Debug.Log($"  hit {c.name}(L:{lname}) → CanTakeDamageThisFrame=false");
                    continue;
                }
                if (_cachedRelay != null)
                {
                    _cachedRelay.RelayDamage(health, damage, _attacker, targetFlickerDuration, targetInvincibilityDuration, Vector2.up);
                }
                else
                {
                    health.Damage(damage, _attacker, targetFlickerDuration, targetInvincibilityDuration, Vector2.up);
                }
                RaiseAreaDamageApplied(health, Vector2.up);
                // 본인 발동 메테오 → popup. Detonate 시 결정된 _wasCritical 적용.
                if (LostMemory.UI.DamagePopupSpawner.Instance != null)
                {
                    LostMemory.UI.DamagePopupSpawner.Instance.NotifyMeleeDamage(health, damage, _wasCritical);
                }
                if (logMeteorEvents) Debug.Log($"  hit {c.name}(L:{lname}) → damage {damage} APPLIED crit={_wasCritical}");
                applied++;
            }

            if (logMeteorEvents) Debug.Log($"[Meteor] applied {applied}/{hits.Length}");
        }

        private static bool IsOwnedByAttacker(Health health, GameObject attacker)
        {
            if (attacker == null || health == null) return false;
            return health.gameObject == attacker || health.transform.IsChildOf(attacker.transform);
        }

        private void RaiseAreaDamageApplied(Health target, Vector2 direction)
        {
            if (target == null || damage <= 0f)
            {
                return;
            }

            var request = new CombatDamageRequest(
                damage,
                DamageSourceKind.Area,
                ResolveNetworkObjectId(target),
                attackerNetworkObjectId: ResolveNetworkObjectId(_attacker),
                sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                criticalPolicy: CriticalPolicy.Never,
                applyAttackPower: false,
                hitDirection: direction,
                hitPoint: target.transform.position,
                weaponId: "Meteor");
            var result = new CombatDamageResult(request, damage, _wasCritical, target.CurrentHealth <= 0f);
            CombatDamageEventDispatcher.RaiseDamageApplied(new CombatDamageEvent(result, target, _attacker));
        }

        private static ulong ResolveNetworkObjectId(Component component)
        {
            if (component == null)
            {
                return 0UL;
            }

            var networkObject = component.GetComponentInParent<Unity.Netcode.NetworkObject>();
            return networkObject != null && networkObject.IsSpawned ? networkObject.NetworkObjectId : 0UL;
        }

        private static ulong ResolveNetworkObjectId(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return 0UL;
            }

            var networkObject = gameObject.GetComponentInParent<Unity.Netcode.NetworkObject>();
            return networkObject != null && networkObject.IsSpawned ? networkObject.NetworkObjectId : 0UL;
        }
    }
}
