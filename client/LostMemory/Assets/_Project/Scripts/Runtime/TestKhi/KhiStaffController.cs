using System;
using LostMemory.Combat;
using LostMemory.Networking.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LostMemory.TestKhi
{
    /// <summary>
    /// 스태프 모드의 입력·스킬 발동 컨트롤러.
    /// - 좌클릭(hold): 마법탄 자동 연사 — 약한 데미지, 마나 무료, boltInterval 마다 한 발.
    /// - 우클릭(짧게): 파이어볼 — 마나 fireballManaCost 소비, 관통 강타.
    /// - 우클릭(길게 chargeThreshold 이상): 메테오 — 마나 meteorManaCost 소비, 마우스 worldPos 광역.
    /// 우클릭은 release 시점에 누른 시간으로 분기 (MOBA 스타일). 미만 = 파이어볼, 이상 = 메테오.
    /// </summary>
    [DefaultExecutionOrder(50)]
    [AddComponentMenu("Lost Memory/Test Khi/Khi Staff Controller")]
    public class KhiStaffController : MonoBehaviour
    {
        private const float DefaultMinimumBoltInterval = 0.3f;

        [Header("Refs (Awake 시 자동 resolve)")]
        [SerializeField] private PlayerMana playerMana;
        [SerializeField] private PlayerStatModifierContainer statContainer;
        [Tooltip("투사체 발사 위치 (마법탄·파이어볼 공용). 비어있으면 transform 사용.")]
        [SerializeField] private Transform projectileSpawnPoint;
        [Tooltip("마우스 worldPos 변환용. 비어있으면 Camera.main.")]
        [SerializeField] private Camera aimCamera;
        [Tooltip("Multiplayer 시각 broadcast 채널 (Bug #28). 부모 계층에서 자동 검색.")]
        [SerializeField] private AttackBroadcast attackBroadcast;

        [Header("Magic Bolt (Left Click Hold — 평타 자동 연사)")]
        [Tooltip("마법탄 prefab. KhiArrow.prefab(단발) 재사용 추천.")]
        [SerializeField] private KhiArrowProjectile boltPrefab;
        [SerializeField, Min(1f)] private float boltSpeed = 16f;
        [SerializeField, Min(0f)] private float boltDamage = 8f;
        [Tooltip("뿅뿅뿅 — 마법탄 사이 최소 간격(초).")]
        [SerializeField, Min(0.02f)] private float boltInterval = 0.15f;
        [Tooltip("Base lower bound for held-left-click bolt repeats at AttackSpeed 1x.")]
        [SerializeField, Min(0.02f)] private float minimumBoltInterval = DefaultMinimumBoltInterval;
        [SerializeField, Min(0)] private int boltManaCost = 0;
        [Tooltip("마법탄 유도 — 초당 회전 각도. 0 = 직선, 60~90 = 약한 유도, 180+ = 강한 유도.")]
        [SerializeField, Min(0f)] private float boltHomingTurnRate = 90f;
        [Tooltip("유도 감지 반경. 이 안의 가장 가까운 적 추적.")]
        [SerializeField, Min(0f)] private float boltHomingRadius = 8f;

        [Header("Fireball (Right Click Tap — 짧게 누름)")]
        [Tooltip("파이어볼 prefab. KhiFireball.prefab(관통) 사용.")]
        [SerializeField] private KhiArrowProjectile fireballPrefab;
        [SerializeField, Min(1f)] private float fireballSpeed = 14f;
        [SerializeField, Min(0f)] private float fireballDamage = 25f;
        [SerializeField, Min(0)] private int fireballManaCost = 50;
        [Tooltip("연속 입력 방지용 짧은 쿨.")]
        [SerializeField, Min(0.05f)] private float fireballCooldown = 0.3f;

        [Header("Meteor (Right Click Hold — 차지)")]
        [SerializeField] private KhiMeteor meteorPrefab;
        [SerializeField, Min(0f)] private float meteorDamage = 80f;
        [SerializeField, Min(0.5f)] private float meteorRadius = 2f;
        [SerializeField, Min(0)] private int meteorManaCost = 100;
        [SerializeField, Min(0.05f)] private float meteorCooldown = 0.5f;

        [Header("Charge")]
        [Tooltip("우클릭 누른 시간이 이 값 이상이면 메테오, 미만이면 파이어볼. 추후 시각 신호로 표시 예정.")]
        [SerializeField, Min(0.1f)] private float chargeThreshold = 0.5f;

        [Header("Charge UI (Optional)")]
        [Tooltip("자식으로 자동 Instantiate 될 차징바 prefab. 비워두면 UI 없음.")]
        [SerializeField] private GameObject chargeBarPrefab;
        [Tooltip("차징바의 localPosition (캐릭터 root 기준). 머리 위 = (0, 0.8, 0) 권장.")]
        [SerializeField] private Vector3 chargeBarLocalOffset = new Vector3(0f, 0.8f, 0f);

        [Header("Debug")]
        [SerializeField] private bool logSkillsToConsole = false;

        private float _nextBoltAt;
        private float _nextFireballAt;
        private float _nextMeteorAt;
        private float _rightPressStartTime;
        private bool _isHoldingRight;
        // Phase E: 비-owner 측 자체 발사 차단용.
        private NetworkObject _cachedNetObj;
        private bool _netObjResolved;

        /// <summary>우클릭 차징 진행도 0~1. UI 차징바가 구독.</summary>
        public float ChargeProgress01 => _isHoldingRight && chargeThreshold > 0f
            ? Mathf.Clamp01((Time.time - _rightPressStartTime) / chargeThreshold)
            : 0f;
        /// <summary>지금 우클릭 차징 중인가.</summary>
        public bool IsCharging => _isHoldingRight;
        /// <summary>차징 임계값 도달했는가 (release 시 메테오 발동).</summary>
        public bool IsChargeReady => ChargeProgress01 >= 1f;
        private float EffectiveBoltInterval =>
            Mathf.Max(0.02f, GetBaseBoltInterval() / GetAttackSpeedMultiplier());
        private int _sequenceId;

        public event Action<KhiAttackRequest> BoltFired;
        public event Action<KhiAttackRequest> FireballFired;
        public event Action<KhiAttackRequest> MeteorFired;

        // 기존 호환 — 활성 모드 토글 등에서 구독 중인 외부 코드가 있을 수 있음.
        public event Action<KhiAttackRequest> Skill1Fired;
        public event Action<KhiAttackRequest> Skill2Fired;

        private void Awake()
        {
            playerMana ??= GetComponentInParent<PlayerMana>();
            statContainer ??= GetComponent<PlayerStatModifierContainer>()
                ?? GetComponentInParent<PlayerStatModifierContainer>()
                ?? GetComponentInChildren<PlayerStatModifierContainer>(true);
            if (projectileSpawnPoint == null) projectileSpawnPoint = transform;
            if (attackBroadcast == null) attackBroadcast = GetComponentInParent<AttackBroadcast>();

            if (chargeBarPrefab != null)
            {
                GameObject bar = Instantiate(chargeBarPrefab, transform);
                bar.transform.localPosition = chargeBarLocalOffset;
            }
        }

        private void OnDisable()
        {
            // 모드 전환 시 차지 상태 reset.
            _isHoldingRight = false;
        }

        private float GetBaseBoltInterval()
        {
            float configuredInterval = boltInterval > 0f ? boltInterval : DefaultMinimumBoltInterval;
            float configuredMinimum = minimumBoltInterval > 0f ? minimumBoltInterval : DefaultMinimumBoltInterval;
            return Mathf.Max(configuredInterval, configuredMinimum);
        }

        private float GetAttackSpeedMultiplier()
        {
            float speedMultiplier = statContainer != null
                ? statContainer.GetTotalMultiplier(StatId.AttackSpeed)
                : 1f;
            return speedMultiplier > 0f ? speedMultiplier : 1f;
        }

        private void Update()
        {
            // Phase E: 비-owner clone 은 자체 발사 금지. owner 만 입력 처리.
            // (Staff bolt 는 호스트 권위 spawn 이 아니라 owner-local Instantiate 라 다른 클라엔 안 보임 — 향후 NetworkObject 화 별도 작업.)
            if (IsRemoteClone()) return;

            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            // 좌클릭 hold → 자동 연사.
            if (mouse.leftButton.isPressed && Time.time >= _nextBoltAt)
            {
                TryCastBolt();
            }

            // 우클릭 down → 차지 시작.
            if (mouse.rightButton.wasPressedThisFrame)
            {
                _rightPressStartTime = Time.time;
                _isHoldingRight = true;
            }

            // 우클릭 release → 차지 시간 평가.
            if (mouse.rightButton.wasReleasedThisFrame && _isHoldingRight)
            {
                _isHoldingRight = false;
                float heldFor = Time.time - _rightPressStartTime;
                if (heldFor >= chargeThreshold)
                {
                    if (Time.time >= _nextMeteorAt) TryCastMeteor();
                }
                else
                {
                    if (Time.time >= _nextFireballAt) TryCastFireball();
                }
            }
        }

        private void TryCastBolt()
        {
            if (boltPrefab == null) return;
            if (boltManaCost > 0 && playerMana != null && !playerMana.Consume(boltManaCost))
            {
                _nextBoltAt = Time.time + EffectiveBoltInterval;
                return;
            }

            Vector3 spawnPos = projectileSpawnPoint.position;
            Vector2 aimDir = ComputeAimFromOrigin(spawnPos);

            KhiArrowProjectile bolt = Instantiate(boltPrefab, spawnPos, Quaternion.identity);
            // AttackPower 적용 — 평타 패턴 통일. 이전 누락분 fix.
            CombatDamageResult boltDamageResult = CombatDamageResolver.Resolve(
                new CombatDamageRequest(
                    boltDamage,
                    DamageSourceKind.Projectile,
                    0UL,
                    applyAttackPower: true,
                    hitDirection: aimDir,
                    hitPoint: spawnPos,
                    weaponId: "StaffBolt"),
                statContainer);
            float boltFinal = boltDamageResult.FinalDamage;
            bool boltCrit = boltDamageResult.WasCritical;
            bolt.Launch(aimDir, boltSpeed, boltFinal, gameObject, boltCrit);
            if (boltHomingTurnRate > 0f && boltHomingRadius > 0f)
            {
                bolt.SetHoming(boltHomingTurnRate, boltHomingRadius);
            }

            _nextBoltAt = Time.time + EffectiveBoltInterval;

            KhiAttackRequest request = MakeRequest(aimDir, spawnPos);
            BoltFired?.Invoke(request);
            if (logSkillsToConsole) Debug.Log($"[KhiStaff] Bolt seq={request.SequenceId} dmg={boltDamage}");

            // Bug #28 + #33 — non-owner 측 visual-only clone broadcast + owner kill 시 동기 destroy 위한 ID 발급.
            if (attackBroadcast != null)
            {
                int projectileId = KhiArrowProjectile.AllocateProjectileId();
                bolt.SetProjectileId(projectileId);
                bolt.SetDespawnBroadcaster(attackBroadcast);  // owner hit 시 PlayImpactAndDestroy 가 RelayProjectileDespawn 호출.
                attackBroadcast.RelayStaffProjectileSpawn(AttackBroadcast.StaffProjectileType.Bolt, spawnPos, aimDir, projectileId);
            }
        }

        private void TryCastFireball()
        {
            if (fireballPrefab == null) return;
            if (playerMana != null && !playerMana.Consume(fireballManaCost)) return;

            Vector3 spawnPos = projectileSpawnPoint.position;
            Vector2 aimDir = ComputeAimFromOrigin(spawnPos);

            KhiArrowProjectile fireball = Instantiate(fireballPrefab, spawnPos, Quaternion.identity);
            // AttackPower 적용 — 평타 패턴 통일.
            CombatDamageResult fireDamageResult = CombatDamageResolver.Resolve(
                new CombatDamageRequest(
                    fireballDamage,
                    DamageSourceKind.Projectile,
                    0UL,
                    applyAttackPower: true,
                    hitDirection: aimDir,
                    hitPoint: spawnPos,
                    weaponId: "StaffFireball"),
                statContainer);
            float fireFinal = fireDamageResult.FinalDamage;
            bool fireCrit = fireDamageResult.WasCritical;
            fireball.Launch(aimDir, fireballSpeed, fireFinal, gameObject, fireCrit);

            _nextFireballAt = Time.time + fireballCooldown;

            KhiAttackRequest request = MakeRequest(aimDir, spawnPos);
            FireballFired?.Invoke(request);
            Skill1Fired?.Invoke(request);
            if (logSkillsToConsole) Debug.Log($"[KhiStaff] Fireball seq={request.SequenceId} dmg={fireballDamage}");

            // Bug #28 + #33 — non-owner 측 visual-only clone broadcast + owner kill 시 동기 destroy 위한 ID 발급.
            if (attackBroadcast != null)
            {
                int projectileId = KhiArrowProjectile.AllocateProjectileId();
                fireball.SetProjectileId(projectileId);
                fireball.SetDespawnBroadcaster(attackBroadcast);
                attackBroadcast.RelayStaffProjectileSpawn(AttackBroadcast.StaffProjectileType.Fireball, spawnPos, aimDir, projectileId);
            }
        }

        private void TryCastMeteor()
        {
            if (meteorPrefab == null) return;
            if (playerMana != null && !playerMana.Consume(meteorManaCost)) return;

            Vector3 mouseWorld = GetMouseWorld();
            mouseWorld.z = 0f;
            KhiMeteor meteor = Instantiate(meteorPrefab, mouseWorld, Quaternion.identity);
            meteor.Detonate(meteorDamage, meteorRadius, gameObject);

            _nextMeteorAt = Time.time + meteorCooldown;

            KhiAttackRequest request = MakeRequest(Vector2.down, mouseWorld);
            MeteorFired?.Invoke(request);
            Skill2Fired?.Invoke(request);
            if (logSkillsToConsole) Debug.Log($"[KhiStaff] Meteor seq={request.SequenceId} pos={mouseWorld}");

            // Bug #28 — non-owner 측 visual-only clone broadcast.
            // Meteor 는 mouseWorld 위치 자체가 spawn 좌표 (direction 미사용 → Vector2.down placeholder).
            // KhiMeteor 는 자체 코루틴으로 destroy 라 ID 무관 → 0 전달 (Bug #33 후속 path 우회).
            if (attackBroadcast != null)
            {
                attackBroadcast.RelayStaffProjectileSpawn(AttackBroadcast.StaffProjectileType.Meteor, mouseWorld, Vector2.down, 0);
            }
        }

        private KhiAttackRequest MakeRequest(Vector2 dir, Vector3 origin)
        {
            return new KhiAttackRequest
            {
                SequenceId = ++_sequenceId,
                ComboStep = 1,
                AimDirection = dir,
                AimAngleDegrees = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg,
                Origin = origin,
                StartedAt = Time.time,
                Attacker = gameObject
            };
        }

        /// <summary>
        /// Bug #28 — non-owner 측에서 AttackBroadcast.ClientRpc 가 호출. owner 측의 spawn 과 같은 prefab 으로
        /// visual-only clone Instantiate. damage 권위는 owner 측 한 군데서만 → double-hit 없음.
        ///   - Bolt/Fireball: KhiArrowProjectile.Launch(direction, speed, 0, null) + SetVisualOnly(true).
        ///     speed 는 owner 측 값 그대로 (serialized field). 양 클라가 동일 spawnPos + direction 으로 시작해
        ///     각자 짧은 직선 시뮬레이션. damage=0 + attacker=null 은 visualOnly 가드와 함께 안전망.
        ///   - Meteor: KhiMeteor.Detonate(0, 0, null) — 0 은 serialized 기본값 유지 (override 안 함).
        ///     warning/falling/explosion 시각 그대로, ApplyDamage 만 skip.
        /// </summary>
        public void SpawnVisualOnlyProjectile(AttackBroadcast.StaffProjectileType type, Vector3 spawnPos, Vector2 direction, int projectileId)
        {
            switch (type)
            {
                case AttackBroadcast.StaffProjectileType.Bolt:
                    if (boltPrefab == null) return;
                    KhiArrowProjectile boltClone = Instantiate(boltPrefab, spawnPos, Quaternion.identity);
                    boltClone.SetVisualOnly(true);
                    boltClone.SetProjectileId(projectileId);  // visualOnly 이미 true → dictionary 등록.
                    boltClone.Launch(direction, boltSpeed, 0f, null);
                    break;

                case AttackBroadcast.StaffProjectileType.Fireball:
                    if (fireballPrefab == null) return;
                    KhiArrowProjectile fireballClone = Instantiate(fireballPrefab, spawnPos, Quaternion.identity);
                    fireballClone.SetVisualOnly(true);
                    fireballClone.SetProjectileId(projectileId);
                    fireballClone.Launch(direction, fireballSpeed, 0f, null);
                    break;

                case AttackBroadcast.StaffProjectileType.Meteor:
                    // Meteor 는 자체 코루틴 destroy — ID dictionary 미사용.
                    if (meteorPrefab == null) return;
                    KhiMeteor meteorClone = Instantiate(meteorPrefab, spawnPos, Quaternion.identity);
                    meteorClone.SetVisualOnly(true);
                    meteorClone.Detonate(0f, 0f, null);
                    break;
            }
        }

        /// <summary>Phase E: 비-owner clone 여부 (cached).</summary>
        private bool IsRemoteClone()
        {
            if (!_netObjResolved)
            {
                _cachedNetObj = GetComponentInParent<NetworkObject>();
                _netObjResolved = true;
            }
            return _cachedNetObj != null && _cachedNetObj.IsSpawned && !_cachedNetObj.IsOwner;
        }

        private Vector2 ComputeAimFromOrigin(Vector3 origin)
        {
            Vector3 worldPos = GetMouseWorld();
            Vector2 dir = (Vector2)(worldPos - origin);
            return dir.sqrMagnitude > Mathf.Epsilon ? dir.normalized : Vector2.right;
        }

        private Vector3 GetMouseWorld()
        {
            Camera cam = aimCamera != null ? aimCamera : Camera.main;
            Mouse mouse = Mouse.current;
            if (cam == null || mouse == null) return transform.position;
            Vector2 screenPos = mouse.position.ReadValue();
            return cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, -cam.transform.position.z));
        }
    }
}
