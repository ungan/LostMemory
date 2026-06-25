using System;
using LostMemory.Combat;
using LostMemory.Networking.Player;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LostMemory.TestKhi
{
    /// <summary>
    /// 활 모드의 입력·발사 컨트롤러. 검의 KhiMeleeComboController 와 같은 위치에 부착.
    /// - 좌클릭(ShootButton 위치): 단발 — singleShotInterval 간격마다 1회 가능
    /// - 우클릭(SecondaryShoot 위치, 이 컴포넌트가 직접 폴링): 누르고 있는 동안 rapidShotInterval 간격으로 연사
    /// WeaponModeController 가 모드 토글 시 enabled = false 로 입력 무시.
    /// 마나 소모(연사): 기본 0 — 추후 활성화 시 rapidShotManaCostPerShot 만 키우면 됨.
    /// </summary>
    [DefaultExecutionOrder(50)]
    [AddComponentMenu("Lost Memory/Test Khi/Khi Bow Controller")]
    public class KhiBowController : MonoBehaviour
    {
        private const float MinimumShotInterval = 0.02f;

        [Header("Refs (Awake 시 자동 resolve)")]
        [SerializeField] private KhiPlayerAim aim;
        [SerializeField] private PlayerMana playerMana;
        [SerializeField] private PlayerStatModifierContainer statContainer;
        [Tooltip("화살이 생성될 위치. 비어있으면 transform 사용.")]
        [SerializeField] private Transform arrowSpawnPoint;
        [Tooltip("화살 방향 계산에 쓰는 카메라. 비어있으면 Camera.main 사용.")]
        [SerializeField] private Camera aimCamera;
        [Tooltip("Multiplayer 시각 broadcast 채널 (Bug #30). 부모 계층에서 자동 검색.")]
        [SerializeField] private AttackBroadcast attackBroadcast;

        [Header("Arrow")]
        [Tooltip("KhiArrowProjectile 컴포넌트가 부착된 화살 prefab.")]
        [SerializeField] private KhiArrowProjectile arrowPrefab;
        [SerializeField, Min(1f)] private float arrowSpeed = 12f;
        [SerializeField, Min(0f)] private float baseDamage = 10f;

        [Header("Single Shot (Left Click)")]
        [SerializeField, Min(0.05f)] private float singleShotInterval = 0.5f;

        [Header("Rapid Shot (Right Click Hold)")]
        [SerializeField, Min(0.02f)] private float rapidShotInterval = 0.12f;
        [SerializeField, Range(0f, 1f)] private float rapidDamageMultiplier = 0.7f;
        [SerializeField, Min(0)] private int rapidShotManaCostPerShot = 0;

        [Header("Debug")]
        [SerializeField] private bool logShotsToConsole = false;

        private float _nextSingleShotAllowedAt;
        private float _nextRapidShotAt;
        private int _sequenceId;
        // Bug #30 — non-owner clone 자체 발사 차단용. KhiStaffController 와 동일 패턴.
        private NetworkObject _cachedNetObj;
        private bool _netObjResolved;

        /// <summary>화살 발사 직전 발화. (request, isRapid)</summary>
        public event Action<KhiAttackRequest, bool> ArrowFired;

        private void Awake()
        {
            aim ??= GetComponent<KhiPlayerAim>();
            playerMana ??= GetComponentInParent<PlayerMana>();
            statContainer ??= GetComponent<PlayerStatModifierContainer>()
                ?? GetComponentInParent<PlayerStatModifierContainer>()
                ?? GetComponentInChildren<PlayerStatModifierContainer>(true);
            if (arrowSpawnPoint == null) arrowSpawnPoint = transform;
            if (attackBroadcast == null) attackBroadcast = GetComponentInParent<AttackBroadcast>();

            if (arrowPrefab == null)
            {
                Debug.LogError($"[KhiBowController] arrowPrefab 미할당. {name} Inspector 에서 화살 prefab 지정 필요.", this);
                enabled = false;
            }
        }

        private void Update()
        {
            // Bug #30 — 비-owner clone 은 자체 발사 금지. owner 만 입력 처리.
            // (이전에는 가드 없어서 게스트 화면에서 *상대 player* 의 활도 게스트 로컬 마우스 입력으로 발사될 위험.)
            if (IsRemoteClone()) return;

            Mouse mouse = Mouse.current;
            if (mouse == null) return;

            // CL-234 (A-1/A-2): UI 패널 열린 동안 활 입력 차단 (UIInputBlocker + EventSystem 양쪽).
            if (LostMemory.UI.UIInputBlocker.IsBlocked)
            {
                if (logShotsToConsole && mouse.leftButton.wasPressedThisFrame)
                    Debug.Log("[KhiBowController] LEFT click blocked by UIInputBlocker.", this);
                return;
            }
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.IsPointerOverGameObject())
            {
                if (logShotsToConsole && mouse.leftButton.wasPressedThisFrame)
                    Debug.Log("[KhiBowController] LEFT click blocked by EventSystem (pointer over UI).", this);
                return;
            }

            // CL-234 (A-4): 검과 동일하게 홀드 자동 공격.
            //   wasPressedThisFrame (1회) → isPressed (누르고 있는 동안 매 프레임 시도).
            //   _nextSingleShotAllowedAt = Time.time + singleShotInterval 로 발사 빈도 자체 제한.
            if (mouse.leftButton.isPressed && Time.time >= _nextSingleShotAllowedAt)
            {
                FireSingleShot();
            }

            if (mouse.rightButton.isPressed && Time.time >= _nextRapidShotAt)
            {
                FireRapidShot();
            }
            else if (!mouse.rightButton.isPressed && _nextRapidShotAt > 0f)
            {
                _nextRapidShotAt = 0f;
            }
        }

        private void OnDisable()
        {
            _nextRapidShotAt = 0f;
        }

        private void FireSingleShot()
        {
            SpawnArrow(baseDamage, isRapid: false);
            _nextSingleShotAllowedAt = Time.time + GetShotInterval(singleShotInterval);
        }

        private void FireRapidShot()
        {
            float shotInterval = GetShotInterval(rapidShotInterval);
            if (rapidShotManaCostPerShot > 0 && playerMana != null)
            {
                if (!playerMana.Consume(rapidShotManaCostPerShot))
                {
                    _nextRapidShotAt = Time.time + shotInterval;
                    return;
                }
            }

            SpawnArrow(baseDamage * rapidDamageMultiplier, isRapid: true);
            _nextRapidShotAt = Time.time + shotInterval;
        }

        private float GetShotInterval(float baseInterval)
        {
            float configuredInterval = baseInterval > 0f ? baseInterval : MinimumShotInterval;
            return Mathf.Max(MinimumShotInterval, configuredInterval / GetAttackSpeedMultiplier());
        }

        private float GetAttackSpeedMultiplier()
        {
            float speedMultiplier = statContainer != null
                ? statContainer.GetTotalMultiplier(StatId.AttackSpeed)
                : 1f;
            return speedMultiplier > 0f ? speedMultiplier : 1f;
        }

        private void SpawnArrow(float damage, bool isRapid)
        {
            Vector3 spawnPos = arrowSpawnPoint != null ? arrowSpawnPoint.position : transform.position;
            Vector2 aimDirection = ComputeAimFromOrigin(spawnPos);
            if (aimDirection.sqrMagnitude <= Mathf.Epsilon) aimDirection = Vector2.right;

            KhiArrowProjectile projectile = Instantiate(arrowPrefab, spawnPos, Quaternion.identity);
            // AttackPower (전사의끈/단단한 주먹 등) 적용 — 평타와 동일 패턴. 이전엔 누락돼서
            // 상태창 +50% 떠도 화살 데미지가 그대로였음.
            CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                new CombatDamageRequest(
                    damage,
                    DamageSourceKind.Projectile,
                    0UL,
                    applyAttackPower: true,
                    hitDirection: aimDirection,
                    hitPoint: spawnPos,
                    weaponId: isRapid ? "BowRapid" : "Bow"),
                statContainer);
            float finalDamage = damageResult.FinalDamage;
            bool wasCritical = damageResult.WasCritical;
            projectile.Launch(aimDirection, arrowSpeed, finalDamage, gameObject, wasCritical);

            KhiAttackRequest request = new KhiAttackRequest
            {
                SequenceId = ++_sequenceId,
                ComboStep = 1,
                AimDirection = aimDirection,
                AimAngleDegrees = Mathf.Atan2(aimDirection.y, aimDirection.x) * Mathf.Rad2Deg,
                Origin = spawnPos,
                StartedAt = Time.time,
                Attacker = gameObject
            };
            ArrowFired?.Invoke(request, isRapid);

            if (logShotsToConsole)
            {
                Debug.Log($"[KhiBow] {(isRapid ? "Rapid" : "Single")} shot seq={request.SequenceId} dmg={damage:F1}");
            }

            // Bug #30 + #33 — non-owner 측 visual-only clone broadcast + owner kill 시 동기 destroy 위한 ID 발급.
            if (attackBroadcast != null)
            {
                int projectileId = KhiArrowProjectile.AllocateProjectileId();
                projectile.SetProjectileId(projectileId);
                projectile.SetDespawnBroadcaster(attackBroadcast);
                attackBroadcast.RelayArrowProjectileSpawn(spawnPos, aimDirection, isRapid, projectileId);
            }
        }

        /// <summary>
        /// Bug #30 — non-owner 측에서 AttackBroadcast.BroadcastArrowProjectileSpawnClientRpc 가 호출.
        /// owner spawn 과 같은 prefab Instantiate + visual-only 마킹 + ArrowFired 이벤트 fire.
        /// KhiBowAnimator (Draw/Release frame) 와 KhiBowPresenter (recoil) 가 이 이벤트 구독 →
        /// non-owner 측에서도 발사 시각 효과 자연스럽게 재생.
        /// damage=0 + attacker=null + SetVisualOnly(true) → collider disable + Health.Damage 호출 차단.
        /// </summary>
        public void SpawnVisualOnlyArrow(Vector3 spawnPos, Vector2 direction, bool isRapid, int projectileId)
        {
            if (arrowPrefab == null) return;

            KhiArrowProjectile clone = Instantiate(arrowPrefab, spawnPos, Quaternion.identity);
            clone.SetVisualOnly(true);
            clone.SetProjectileId(projectileId);  // visualOnly 이미 true → dictionary 등록 (owner hit broadcast 매칭).
            clone.Launch(direction, arrowSpeed, 0f, null);

            // owner 의 ArrowFired 와 동일 시그니처로 fire → animator/presenter 가 같은 코루틴 재생.
            KhiAttackRequest request = new KhiAttackRequest
            {
                SequenceId = ++_sequenceId,
                ComboStep = 1,
                AimDirection = direction,
                AimAngleDegrees = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg,
                Origin = spawnPos,
                StartedAt = Time.time,
                Attacker = gameObject
            };
            ArrowFired?.Invoke(request, isRapid);
        }

        /// <summary>Bug #30 — 비-owner clone 여부 (cached). KhiStaffController.IsRemoteClone 과 동일 패턴.</summary>
        private bool IsRemoteClone()
        {
            if (!_netObjResolved)
            {
                _cachedNetObj = GetComponentInParent<NetworkObject>();
                _netObjResolved = true;
            }
            return _cachedNetObj != null && _cachedNetObj.IsSpawned && !_cachedNetObj.IsOwner;
        }

        /// <summary>
        /// 화살 방향 = (마우스 worldPosition - origin) 정규화.
        /// KhiPlayerAim.GetAimDirection() 은 캐릭터 transform 기준이라 spawnPos 가 발이 아닐 때
        /// 가까운 거리에서 마우스로 정확히 안 향함. 본 메서드는 spawnPos 기준으로 다시 계산.
        /// </summary>
        private Vector2 ComputeAimFromOrigin(Vector3 origin)
        {
            Camera cam = aimCamera != null ? aimCamera : Camera.main;
            Mouse mouse = Mouse.current;
            if (cam == null || mouse == null)
            {
                return aim != null ? aim.GetAimDirection() : Vector2.right;
            }
            Vector2 screenPos = mouse.position.ReadValue();
            Vector3 worldPos = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, -cam.transform.position.z));
            Vector2 dir = (Vector2)(worldPos - origin);
            return dir.sqrMagnitude > Mathf.Epsilon ? dir.normalized : Vector2.right;
        }
    }
}
