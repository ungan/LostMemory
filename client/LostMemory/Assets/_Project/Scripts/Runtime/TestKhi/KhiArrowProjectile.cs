using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.Networking.Player;
using LostMemory.UI;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.TestKhi
{
    /// <summary>
    /// 한 발의 화살. Rigidbody2D(Kinematic) 로 직선 비행하며 trigger 충돌 시 Health.Damage 호출.
    /// KhiBowController 가 Instantiate → Launch(...) 호출로 생성.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(Collider2D))]
    [AddComponentMenu("Lost Memory/Test Khi/Khi Arrow Projectile")]
    public class KhiArrowProjectile : MonoBehaviour
    {
        [Header("Lifetime")]
        [SerializeField, Min(0.1f)] private float maxLifetime = 3f;

        [Header("Hit Detection")]
        [SerializeField] private LayerMask targetLayers = ~0;
        [SerializeField, Min(0f)] private float sweepRadius = 0.18f;
        [SerializeField] private bool acceptBossTaggedHealthOutsideTargetLayers = true;
        [SerializeField, Min(0f)] private float targetFlickerDuration = 0f;
        [SerializeField, Min(0f)] private float targetInvincibilityDuration = 0f;
        [Tooltip("적과 충돌 시 화살을 즉시 파괴.")]
        [SerializeField] private bool destroyOnHit = true;

        [Header("Visual")]
        [Tooltip("화살 sprite 가 기본 +X 방향(오른쪽)을 향하면 0. 위쪽을 향하면 -90.")]
        [SerializeField] private float spriteAngleOffsetDeg = 0f;

        [Header("Impact Animation (Optional — destroyOnHit=true 일 때만 적용)")]
        [Tooltip("hit 시 state 전환할 Animator. 비워두면 즉시 Destroy.")]
        [SerializeField] private Animator impactAnimator;
        [Tooltip("hit 시 Animator.Play 호출할 state 이름 (예: 'fireball-destroy').")]
        [SerializeField] private string impactStateName = "fireball-destroy";
        [Tooltip("impact animation 재생 후 GameObject Destroy 까지 대기 시간.")]
        [SerializeField, Min(0.05f)] private float impactHoldDuration = 0.5f;

        [Header("Homing (Optional — 0 = 직선, 90 = 약한 유도)")]
        [Tooltip("초당 회전 가능한 최대 각도. 0 = 직선, 60~90 = 약한 유도, 180+ = 강한 유도.")]
        [SerializeField, Min(0f)] private float homingTurnRateDegPerSec = 0f;
        [Tooltip("감지 반경 안의 가장 가까운 적을 추적. 0 보다 커야 작동.")]
        [SerializeField, Min(0f)] private float homingDetectionRadius = 8f;
        [SerializeField] private LayerMask homingTargetLayers = ~0;
        [Tooltip("발사 직후 N 초 동안은 직선 (조준 의도 유지). 그 후 유도 시작.")]
        [SerializeField, Min(0f)] private float homingDelay = 0.05f;

        [Header("Debug")]
        [Tooltip("OnTriggerEnter2D 단계별 진단 로그. 검증 끝나면 끄세요.")]
        [SerializeField] private bool logProjectileEvents = false;

        private Rigidbody2D _rigidbody;
        private Collider2D _collider;
        private static readonly Collider2D[] _homingScanBuffer = new Collider2D[16];
        private static readonly RaycastHit2D[] _sweepHitBuffer = new RaycastHit2D[32];
        private float _damage;
        private Vector2 _direction;
        private float _speed;
        private GameObject _attacker;
        // 발사 시점에 호출자(KhiBow/Staff) 가 CriticalRoller.Roll() 결과를 _damage 에 반영하고
        // wasCritical 플래그를 같이 set. popup 색/라벨 표시용.
        private bool _wasCritical;
        private float _spawnedAt;
        private bool _launched;
        // develop: CircleCast sweep 이 동일 프레임에 중복 hit 발생 방지 + visual-only clone 도
        // PlayImpactAndDestroy 1회 보장.
        private bool _hasHit;

        // Multiplayer: non-owner 측에서 시각만 재현하는 clone 표시.
        // true 면 OnTriggerEnter2D 방어 + Collider/homing disable → owner 측만 damage 권위.
        private bool _visualOnly;

        // Bug #33 후속 — projectile spawn 단위로 발급되는 고유 ID.
        //   owner 가 hit 시 broadcaster 통해 ID broadcast → 게스트 측 dictionary lookup → 매칭 clone destroy.
        //   owner kill 로 적 NGO destroy → 게스트 clone 충돌 못 함 케이스에서 시각 sync 보장 (clone 이 wall 까지 비행 안 함).
        //   ID <= 0 = 미설정 (솔로 또는 broadcast 비활성 — 일반 maxLifetime 으로 자동 cleanup).
        private int _projectileId = -1;
        // owner 측 instance 에서만 set (KhiStaffController/KhiBowController 가 spawn 직후 호출).
        // visual-only clone 은 set 안 됨 → PlayImpactAndDestroy 시 echo broadcast 차단.
        private AttackBroadcast _despawnBroadcaster;
        // visual-only clone registry. key = owner 측 발급 ID. owner 의 RelayProjectileDespawn 도착 시 lookup.
        private static readonly Dictionary<int, KhiArrowProjectile> _visualOnlyClones = new Dictionary<int, KhiArrowProjectile>();
        // owner 측 ID 발급용 모노톤 카운터. KhiStaffController/KhiBowController 가 AllocateProjectileId 호출.
        private static int _nextProjectileId = 1;

        public static int AllocateProjectileId()
        {
            // 0 또는 음수는 미설정 표시값으로 사용 → 1부터 시작.
            int id = _nextProjectileId++;
            if (_nextProjectileId <= 0) _nextProjectileId = 1; // overflow wrap.
            return id;
        }

        /// <summary>spawn 발급 ID 저장. visual-only clone 의 경우 dictionary 등록 (owner hit broadcast 매칭용).</summary>
        public void SetProjectileId(int id)
        {
            _projectileId = id;
            if (_visualOnly && id > 0)
            {
                _visualOnlyClones[id] = this;
            }
        }

        /// <summary>owner instance 전용 — PlayImpactAndDestroy 시점에 ID broadcast 통해 게스트 clone destroy 요청.</summary>
        public void SetDespawnBroadcaster(AttackBroadcast broadcaster)
        {
            _despawnBroadcaster = broadcaster;
        }

        /// <summary>
        /// owner 의 PlayImpactAndDestroy 가 ClientRpc 발화하면 게스트 측 본 메서드가 dictionary lookup → clone destroy.
        /// owner 가 적 kill 시 enemy NGO destroy 로 게스트 clone 의 trigger 발화 못 하는 케이스 (Bug #33 후속) 대응.
        /// </summary>
        public static void DespawnVisualOnlyCloneById(int id)
        {
            if (id <= 0) return;
            if (!_visualOnlyClones.TryGetValue(id, out var clone)) return;
            _visualOnlyClones.Remove(id);
            if (clone == null) return;
            clone.PlayImpactAndDestroy();
        }

        private void OnDestroy()
        {
            // visual-only clone 이 destroy 되면 registry 정리. owner instance 는 등록 안 했으므로 무영향.
            if (_visualOnly && _projectileId > 0)
            {
                _visualOnlyClones.Remove(_projectileId);
            }
        }

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody2D>();
            _rigidbody.gravityScale = 0f;
            _rigidbody.bodyType = RigidbodyType2D.Kinematic;
            _collider = GetComponent<Collider2D>();
        }

        /// <summary>
        /// 화살 발사. 위치는 호출 전 Instantiate 시 결정. 방향은 정규화 입력.
        /// damage 는 호출자가 이미 CriticalRoller.Roll() 통과시킨 최종 값. wasCritical 은 popup 표시용.
        /// </summary>
        public void Launch(Vector2 direction, float speed, float damage, GameObject attacker, bool wasCritical = false)
        {
            _direction = direction.sqrMagnitude > Mathf.Epsilon ? direction.normalized : Vector2.right;
            _speed = Mathf.Max(0f, speed);
            _damage = Mathf.Max(0f, damage);
            _attacker = attacker;
            _wasCritical = wasCritical;
            _spawnedAt = Time.time;
            _launched = true;
            _hasHit = false;

            float angleDeg = Mathf.Atan2(_direction.y, _direction.x) * Mathf.Rad2Deg + spriteAngleOffsetDeg;
            transform.rotation = Quaternion.Euler(0f, 0f, angleDeg);
        }

        /// <summary>
        /// 유도 파라미터 외부 override. KhiStaffController 등에서 launch 직후 호출해 발사체별로 유도 설정.
        /// turnRateDegPerSec=0 이면 직선. 양수면 유도 활성.
        /// </summary>
        public void SetHoming(float turnRateDegPerSec, float detectionRadius)
        {
            homingTurnRateDegPerSec = Mathf.Max(0f, turnRateDegPerSec);
            homingDetectionRadius = Mathf.Max(0f, detectionRadius);
        }

        /// <summary>
        /// Multiplayer 시각 전용 clone 으로 설정. non-owner 측 AttackBroadcast.ClientRpc 에서 Instantiate 직후 호출.
        ///   - Collider2D 유지 → OnTriggerEnter2D 정상 발화 → 적과 충돌 시 PlayImpactAndDestroy 호출 (시각 일치).
        ///   - Health.Damage 호출은 OnTriggerEnter2D 내부의 _visualOnly 가드로 skip → owner 측 단일 데미지 권위.
        ///   - homing 파라미터 0 → 직선 이동 (owner 측 homing 결과와 약간 발산 가능하나 정확성보다 단순성 우선).
        ///   - 이동/sprite rotation 은 그대로 동작 → 시각적으로 동일하게 보임.
        ///   - maxLifetime 단축 (Bug #33 후속) — owner 가 적 kill 시 enemy NGO sync destroy 로
        ///     clone 이 *충돌 대상 없음* → maxLifetime 까지 직선 비행 (사용자 눈에 "통과해 안 사라짐").
        ///     짧은 lifetime 으로 발산 시간 최소화. owner 의 sprite 보다 약간 일찍 사라져도 시각 차 미미.
        ///
        /// Bug #33 — 이전 구현 (collider disable) 은 게스트 화면에서 host 투사체가 적을 통과해 maxLifetime 까지 비행.
        /// 추가 픽스: maxLifetime *명시 단축* — owner kill → enemy 사라지면 trigger 자체 안 됨 케이스 대응.
        /// </summary>
        public void SetVisualOnly(bool visualOnly)
        {
            _visualOnly = visualOnly;
            if (!visualOnly) return;
            // collider 는 유지 — 충돌 trigger 정상 발화 + OnTriggerEnter2D 내부에서 damage 만 skip.
            homingTurnRateDegPerSec = 0f;
            homingDetectionRadius = 0f;
            // Bug #33 후속: maxLifetime 은 prefab 값 그대로 (3초). 단축은 *먼 적* 케이스 깨짐.
            // owner hit 시점 ID despawn broadcast 가 정확한 시점 sync 담당 → 단축 불필요.
        }

        private void FixedUpdate()
        {
            if (!_launched) return;

            if (homingTurnRateDegPerSec > 0f
                && homingDetectionRadius > 0f
                && Time.time - _spawnedAt >= homingDelay)
            {
                ApplyHoming();
            }

            Vector2 currentPosition = _rigidbody.position;
            Vector2 movement = _direction * (_speed * Time.fixedDeltaTime);
            if (TryHitAlongPath(currentPosition, movement))
            {
                return;
            }

            _rigidbody.MovePosition(currentPosition + movement);
        }

        private void ApplyHoming()
        {
            int count = Physics2D.OverlapCircleNonAlloc(
                _rigidbody.position, homingDetectionRadius, _homingScanBuffer, homingTargetLayers);
            if (count <= 0) return;

            Transform nearest = null;
            float bestSqr = float.MaxValue;
            Vector2 myPos = _rigidbody.position;
            for (int i = 0; i < count; i++)
            {
                Collider2D c = _homingScanBuffer[i];
                if (c == null) continue;
                Health h = c.GetComponentInParent<Health>();
                if (h == null) continue;
                if (_attacker != null && IsOwnedByAttacker(h, _attacker)) continue;
                // Phase E: homing 도 다른 player 를 후보에서 제외.
                if (IsPlayerTarget(h)) continue;
                float sqr = ((Vector2)c.transform.position - myPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    nearest = c.transform;
                }
            }
            if (nearest == null) return;

            Vector2 toTarget = ((Vector2)nearest.position - myPos).normalized;
            float currentAngle = Mathf.Atan2(_direction.y, _direction.x) * Mathf.Rad2Deg;
            float targetAngle = Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;
            float maxStep = homingTurnRateDegPerSec * Time.fixedDeltaTime;
            float newAngle = Mathf.MoveTowardsAngle(currentAngle, targetAngle, maxStep);
            float newAngleRad = newAngle * Mathf.Deg2Rad;
            _direction = new Vector2(Mathf.Cos(newAngleRad), Mathf.Sin(newAngleRad));

            transform.rotation = Quaternion.Euler(0f, 0f, newAngle + spriteAngleOffsetDeg);
        }

        private void Update()
        {
            if (!_launched) return;
            if (Time.time - _spawnedAt >= maxLifetime)
            {
                Destroy(gameObject);
            }
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            TryApplyHit(other);
        }

        private bool TryHitAlongPath(Vector2 currentPosition, Vector2 movement)
        {
            float distance = movement.magnitude;
            if (distance <= Mathf.Epsilon)
            {
                return false;
            }

            int count = Physics2D.CircleCastNonAlloc(
                currentPosition,
                GetSweepRadius(),
                _direction,
                _sweepHitBuffer,
                distance,
                GetSweepLayerMask());

            RaycastHit2D nearestHit = default;
            float nearestDistance = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                RaycastHit2D hit = _sweepHitBuffer[i];
                Collider2D hitCollider = hit.collider;
                if (hitCollider == null || hitCollider == _collider)
                {
                    continue;
                }

                if (hit.distance < nearestDistance && CanDamageCollider(hitCollider))
                {
                    nearestHit = hit;
                    nearestDistance = hit.distance;
                }
            }

            if (nearestHit.collider == null)
            {
                return false;
            }

            _rigidbody.position = nearestHit.centroid;
            transform.position = nearestHit.centroid;
            return TryApplyHit(nearestHit.collider);
        }

        private bool TryApplyHit(Collider2D other)
        {
            // Bug #33 진단 — visual-only clone 의 OnTriggerEnter2D 호출 여부 + 어느 가드에 막히는지 강제 isolation.
            // 일반 logProjectileEvents 와 별개로 _visualOnly clone 은 *항상* 로그 (inspector 토글 안 켜져 있어도).
            // prefab inspector 에 logProjectileEvents=false 라 게스트 콘솔에 충돌 로그가 안 떠 root cause 안 잡힘 → 강제 진단.
            bool diag = logProjectileEvents || _visualOnly;

            if (!_launched || _hasHit || other == null)
            {
                if (diag && !_launched) Debug.Log($"[Projectile {name}] OnTrigger {other?.name} → !_launched return (visualOnly={_visualOnly})");
                return false;
            }

            int layer = other.gameObject.layer;
            string lname = LayerMask.LayerToName(layer);

            if (!IsColliderAcceptedByLayerOrBossTag(other, layer))
            {
                if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → blocked by layerMask (mask={targetLayers.value} visualOnly={_visualOnly})");
                return false;
            }

            Health health = other.GetComponentInParent<Health>();
            if (health == null)
            {
                if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → no Health in parent chain (visualOnly={_visualOnly})");
                return false;
            }
            if (_attacker != null && IsOwnedByAttacker(health, _attacker))
            {
                if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → owned by attacker (visualOnly={_visualOnly})");
                return false;
            }
            // Phase E: PvP 미상정 — Player Health 는 발사자 무관 모두 면역.
            // 자기 자신 player 는 IsOwnedByAttacker 가 잡지만 다른 player 는 통과 → 본 가드 필요.
            if (IsPlayerTarget(health))
            {
                if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → friendly player skip (visualOnly={_visualOnly})");
                return false;
            }

            // Bug #33 / #35 — CanTakeDamageThisFrame 가드 우회 조건:
            //   - visual-only clone: damage 어차피 호출 안 함, race 무관.
            //   - client owner (게스트의 진짜 projectile): MonsterHealthSync 가 Invulnerable=true 영구 set →
            //     본 가드 항상 false. 우회해서 server-relay path 진입해야 함.
            //   - host owner / solo: 정상 가드 적용.
            // 솔로/NGO 비활성 상태에선 broadcaster.IsSpawned=false → 직접 데미지 적용 경로(host/solo) 로 빠지도록 가드.
            // (KhiMeteor 의 willRelayToServer 와 동일 패턴 — 이 체크 없으면 솔로에서 RelayProjectileDamage 시도하다 적 NGO 도 미spawn 이라 damage skip 됨.)
            bool isClientOwner = !_visualOnly
                && _despawnBroadcaster != null
                && _despawnBroadcaster.IsSpawned
                && !_despawnBroadcaster.IsServer;
            bool skipCanTakeGuard = _visualOnly || isClientOwner;
            if (!skipCanTakeGuard && !health.CanTakeDamageThisFrame())
            {
                if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → CanTakeDamageThisFrame=false");
                return false;
            }

            // develop: 동일 프레임 sweep 중복 hit 방지 + visual-only clone 도 PlayImpactAndDestroy 1회 보장.
            // damage 분기 진입 전에 set → 분기별로 따로 set 할 필요 없음.
            _hasHit = true;

            // Bug #33 / #35 — Damage 분기:
            //   - visual-only clone: damage skip (시각만), PlayImpactAndDestroy 만.
            //   - host owner / solo: server-side direct Damage.
            //   - client owner: ServerRpc relay → server (host) 가 enemy.Health.Damage 호출. client-side direct 무효.
            if (!_visualOnly)
            {
                if (isClientOwner)
                {
                    Unity.Netcode.NetworkObject netObj = health.GetComponentInParent<Unity.Netcode.NetworkObject>();
                    if (netObj != null && netObj.IsSpawned)
                    {
                        _despawnBroadcaster.RelayProjectileDamage(
                            netObj.NetworkObjectId,
                            _damage,
                            targetFlickerDuration,
                            targetInvincibilityDuration,
                            _direction,
                            _wasCritical,
                            _projectileId);
                        if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → damage {_damage} RELAY (client→server NetObjId={netObj.NetworkObjectId})");
                    }
                    else if (diag)
                    {
                        Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → damage skip (target not NGO-spawned, client)");
                    }
                }
                else
                {
                    // host owner 또는 solo (broadcaster=null) — server-side direct.
                    health.Damage(_damage, _attacker, targetFlickerDuration, targetInvincibilityDuration, _direction);
                    RaiseProjectileDamageApplied(health);
                    if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → damage {_damage} APPLIED (server/solo)");
                }

                // 본인 발사 화살 hit 시 popup. visual-only clone 은 본인 화살이 아니므로 skip.
                // 발사 시점 CriticalRoller 판정 결과(_wasCritical) 사용 — 발사자(KhiBow/Staff) 가 set.
                if (DamagePopupSpawner.Instance != null)
                {
                    DamagePopupSpawner.Instance.NotifyArrowDamage(health, _damage, isCritical: _wasCritical);
                }
            }
            else
            {
                if (diag) Debug.Log($"[Projectile {name}] hit {other.name}(L:{lname}) → visual-only skip damage, PlayImpactAndDestroy 호출 예정 (destroyOnHit={destroyOnHit})");
            }

            if (destroyOnHit)
            {
                PlayImpactAndDestroy();
            }

            return true;
        }

        private bool CanDamageCollider(Collider2D other)
        {
            if (other == null)
            {
                return false;
            }

            Health health = other.GetComponentInParent<Health>();
            if (health == null)
            {
                return false;
            }

            int layer = other.gameObject.layer;
            if (!IsColliderAcceptedByLayerOrBossTag(other, layer))
            {
                return false;
            }

            return (_attacker == null || !IsOwnedByAttacker(health, _attacker))
                   && health.CanTakeDamageThisFrame();
        }

        private float GetSweepRadius()
        {
            if (sweepRadius > 0f)
            {
                return sweepRadius;
            }

            return _collider != null
                ? Mathf.Max(0.05f, Mathf.Min(_collider.bounds.extents.x, _collider.bounds.extents.y))
                : 0.05f;
        }

        private int GetSweepLayerMask()
        {
            int mask = targetLayers.value;
            if (acceptBossTaggedHealthOutsideTargetLayers)
            {
                int defaultLayer = LayerMask.NameToLayer("Default");
                if (defaultLayer >= 0)
                {
                    mask |= 1 << defaultLayer;
                }
            }

            return mask;
        }

        private bool IsColliderAcceptedByLayerOrBossTag(Collider2D other, int layer)
        {
            if (IsLayerAccepted(layer))
            {
                return true;
            }

            if (!acceptBossTaggedHealthOutsideTargetLayers || other == null)
            {
                return false;
            }

            Health health = other.GetComponentInParent<Health>();
            return IsBossTagged(health);
        }

        private bool IsLayerAccepted(int layer)
        {
            return ((1 << layer) & targetLayers.value) != 0;
        }

        private static bool IsBossTagged(Health health)
        {
            if (health == null)
            {
                return false;
            }

            Transform healthTransform = health.transform;
            return health.CompareTag("Boss")
                   || (healthTransform.root != null && healthTransform.root.CompareTag("Boss"));
        }

        /// <summary>
        /// impactAnimator 가 있으면 state 전환 + Collider/이동 정지 + impactHoldDuration 후 Destroy.
        /// 없으면 즉시 Destroy.
        /// owner instance 인 경우 (_despawnBroadcaster != null) → 게스트 clone destroy 위해 ID broadcast 발화.
        /// visual-only clone 이거나 broadcaster 미설정 (솔로) 인 경우 broadcast 안 함.
        /// </summary>
        private void PlayImpactAndDestroy()
        {
            // Bug #33 후속: owner hit → 게스트 clone 시각 sync.
            //   _despawnBroadcaster 는 owner spawn 시 KhiStaffController/KhiBowController 가 set.
            //   _visualOnly clone 은 broadcaster=null → 호출 안 함 → echo loop 차단.
            //   DespawnVisualOnlyCloneById 가 게스트 측에서 다시 PlayImpactAndDestroy 호출하지만 그쪽도 broadcaster=null.
            if (_despawnBroadcaster != null && _projectileId > 0 && !_visualOnly)
            {
                _despawnBroadcaster.RelayProjectileDespawn(_projectileId);
            }

            if (impactAnimator != null && !string.IsNullOrEmpty(impactStateName))
            {
                impactAnimator.Play(impactStateName);
                if (_collider != null) _collider.enabled = false;
                _launched = false;
                Destroy(gameObject, impactHoldDuration);
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private static bool IsOwnedByAttacker(Health health, GameObject attacker)
        {
            if (attacker == null || health == null) return false;
            return health.gameObject == attacker || health.transform.IsChildOf(attacker.transform);
        }

        /// <summary>
        /// Phase E: target Health 가 Player 측 entity 인지 — PlayerHealthSync 컴포넌트 존재로 판정.
        /// host/guest 양측 player NetworkObject 모두 동일 컴포넌트 보유 → 자/타 player 일괄 보호.
        /// </summary>
        private static bool IsPlayerTarget(Health health)
        {
            if (health == null) return false;
            return health.GetComponentInParent<PlayerHealthSync>() != null;
        }

        private void RaiseProjectileDamageApplied(Health target)
        {
            if (target == null || _damage <= 0f)
            {
                return;
            }

            ulong sourceId = _projectileId > 0
                ? (ulong)_projectileId
                : (ulong)Mathf.Abs(GetInstanceID());
            var request = new CombatDamageRequest(
                _damage,
                DamageSourceKind.Projectile,
                ResolveNetworkObjectId(target),
                attackerNetworkObjectId: ResolveNetworkObjectId(_attacker),
                sourceId: sourceId,
                criticalPolicy: CriticalPolicy.Never,
                applyAttackPower: false,
                hitDirection: _direction,
                hitPoint: target.transform.position,
                weaponId: ResolveWeaponId());
            var result = new CombatDamageResult(request, _damage, _wasCritical, target.CurrentHealth <= 0f);
            CombatDamageEventDispatcher.RaiseDamageApplied(new CombatDamageEvent(result, target, _attacker));
        }

        private string ResolveWeaponId()
        {
            string projectileName = string.IsNullOrWhiteSpace(name) ? "Projectile" : name;
            return projectileName.Replace("(Clone)", string.Empty).Trim();
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
