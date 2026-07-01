using System.Collections;
using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.Data;
using MoreMountains.TopDownEngine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LostMemory.TestKhi
{
    /// <summary>
    /// CL-230: 단검 모드 (Sword_Dagger / Sword_DaggerNinja) 일 때 우클릭 →
    /// aim 방향 짧은 거리 텔레포트 + 도착 지점에 슬래시 + 마나 소비 + 짧은 무적.
    /// 회피기 겸 어쌔신 시그니처 무브.
    ///
    /// 우클릭 충돌: KhiParryController(패링) 가 우클릭 사용 → WeaponUpgradeService 가
    /// 단검 모드 진입 시 swordParry.enabled = false 로 비활성화 (별도 처리).
    /// 본 컴포넌트는 항상 enabled. 단검 모드 체크는 매 Update 마다 WeaponUpgradeService.CurrentKind 로.
    /// </summary>
    [AddComponentMenu("Lost Memory/Test Khi/Khi Dagger Teleport Controller")]
    [DefaultExecutionOrder(60)]
    public class KhiDaggerTeleportController : MonoBehaviour
    {
        [Header("Refs (Awake 에서 자동 resolve 가능)")]
        [SerializeField] private KhiPlayerAim aim;
        [SerializeField] private KhiMeleeComboController combo;
        [SerializeField] private KhiMeleeHitbox hitbox;
        [SerializeField] private KhiSlashAnimator slashAnimator;
        [SerializeField] private LostMemory.Combat.PlayerMana mana;
        [SerializeField] private Health health;
        [SerializeField] private LostMemory.Combat.PlayerStatModifierContainer statContainer;
        [SerializeField] private MoreMountains.TopDownEngine.TopDownController topDownController;

        [Header("Teleport")]
        [Tooltip("aim 방향으로 이동할 거리 (unit). 벽 있으면 벽 직전까지만.")]
        [SerializeField, Min(0f)] private float teleportDistance = 3f;
        [Tooltip("스무스 이동 지속 시간 (초). 0 이면 즉시 이동.")]
        [SerializeField, Min(0f)] private float lungeDuration = 0.12f;
        [Tooltip("이동 직후 무적 시간 (초).")]
        [SerializeField, Min(0f)] private float invulnerabilityDuration = 0.15f;
        [Tooltip("쿨다운 (초). 마지막 사용 이후 이 시간 동안 우클릭 무시.")]
        [SerializeField, Min(0f)] private float cooldown = 0.8f;

        [Header("Obstacle Check (벽 관통 방지)")]
        [Tooltip("벽 체크용 레이어 마스크 (Ground/Wall 등). None(0) 이면 벽 체크 건너뜀.")]
        [SerializeField] private LayerMask obstacleLayerMask = 0;
        [Tooltip("벽 감지 CircleCast 반경. 캐릭터 콜라이더 크기와 비슷하게 설정.")]
        [SerializeField, Min(0.01f)] private float obstacleCheckRadius = 0.3f;
        [Tooltip("벽 앞에서 멈출 때 안전 거리 (벽에 박히는 것 방지).")]
        [SerializeField, Min(0f)] private float obstacleSafetyMargin = 0.05f;

        [Header("Mana")]
        [Tooltip("텔레포트 1회당 마나 소비량. 마나 부족 시 텔레포트 안 됨.")]
        [SerializeField, Min(0)] private int manaCost = 15;

        [Header("Slash")]
        [Tooltip("도착 지점에 표시할 슬래시 step (1/2/3). 단검 1타 슬래시를 재사용 권장. " +
                 "teleportSlashStep 이 valid (slashFrames 비어있지 않음) 면 무시됨.")]
        [SerializeField, Range(1, 3)] private int slashComboStep = 1;
        [Tooltip("ON: 도착 지점에 슬래시 + hitbox 데미지 판정. OFF: 시각만.")]
        [SerializeField] private bool applyDamageOnArrive = true;

        [Header("Teleport-Only Slash Step (CL-230: 우클릭 전용 슬래시)")]
        [Tooltip("우클릭 텔레포트 전용 AttackStepData. 비어있으면 (slashFrames 없음) 위 slashComboStep 의 SO step 사용. " +
                 "여기에 frames/tint/transform/extra slashes/hitbox/damage 설정하면 일반 공격과 독립된 슬래시.")]
        [SerializeField] private LostMemory.Data.AttackStepData teleportSlashStep;

        [Header("Debug")]
        [SerializeField] private bool logTeleport = false;

        private float _nextAllowedAt = -1f;
        private readonly HashSet<Health> _alreadyHit = new HashSet<Health>();
        private readonly List<Health> _hitsThisSample = new List<Health>(8);
        private int _sequenceId;

        // 멀티 호환: 자기 플레이어 트리의 WeaponUpgradeService 캐시 (싱글톤 미사용).
        private LostMemory.Combat.WeaponUpgradeService _weaponUpgrade;

        private void Awake()
        {
            aim ??= GetComponent<KhiPlayerAim>();
            combo ??= GetComponent<KhiMeleeComboController>();
            hitbox ??= GetComponent<KhiMeleeHitbox>();
            slashAnimator ??= GetComponent<KhiSlashAnimator>();
            mana ??= GetComponent<LostMemory.Combat.PlayerMana>() ?? GetComponentInParent<LostMemory.Combat.PlayerMana>();
            health ??= GetComponent<Health>() ?? GetComponentInParent<Health>();
            statContainer ??= GetComponent<LostMemory.Combat.PlayerStatModifierContainer>()
                              ?? GetComponentInParent<LostMemory.Combat.PlayerStatModifierContainer>();
            topDownController ??= GetComponent<MoreMountains.TopDownEngine.TopDownController>();
            // WeaponUpgradeService 는 자식 오브젝트(_WeaponUpgrade)에 있으므로 GetComponentInChildren 사용.
            _weaponUpgrade = GetComponentInChildren<LostMemory.Combat.WeaponUpgradeService>(true);
        }

        private void Update()
        {
            // 1. 단검 모드 체크 (WeaponUpgradeService)
            if (!IsDaggerMode())
            {
                return;
            }

            // 2. 쿨다운 체크
            if (Time.time < _nextAllowedAt)
            {
                return;
            }

            // 3. 우클릭 입력 체크
            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.rightButton.wasPressedThisFrame)
            {
                return;
            }

            // CL-234 (A-1/A-2): UI 패널 열린 동안 텔레포트 차단 (UIInputBlocker + EventSystem 양쪽).
            if (LostMemory.UI.UIInputBlocker.IsBlocked)
            {
                return;
            }
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.IsPointerOverGameObject())
            {
                return;
            }

            // 4. 마나 체크
            if (mana != null && !mana.HasEnough(manaCost))
            {
                if (logTeleport) Debug.Log($"[KhiDaggerTeleport] 마나 부족 ({mana.CurrentMana}/{manaCost}) — 텔레포트 취소", this);
                return;
            }

            ExecuteTeleport();
        }

        private bool IsDaggerMode()
        {
            if (_weaponUpgrade == null) return false;
            return _weaponUpgrade.CurrentKind == WeaponUpgradeService.WeaponKind.Dagger
                || _weaponUpgrade.CurrentKind == WeaponUpgradeService.WeaponKind.DaggerNinja;
        }

        private void ExecuteTeleport()
        {
            StartCoroutine(ExecuteTeleportCoroutine());
        }

        private IEnumerator ExecuteTeleportCoroutine()
        {
            // 1. aim 방향
            Vector2 aimDir = aim != null ? aim.GetAimDirection() : Vector2.right;
            if (aimDir.sqrMagnitude <= Mathf.Epsilon)
            {
                aimDir = Vector2.right;
            }
            aimDir.Normalize();

            // 2. 마나 소비 + 무적 즉시 적용
            if (mana != null) mana.Consume(manaCost);
            if (health != null && invulnerabilityDuration > 0f)
            {
                health.DamageDisabled();
                StartCoroutine(EnableDamageLater());
            }

            // 3. 벽 체크 — CircleCast 로 경로상 첫 장애물까지의 거리 계산
            Vector3 origin = transform.position;
            float actualDistance = teleportDistance;
            if (obstacleLayerMask.value != 0)
            {
                RaycastHit2D hit = Physics2D.CircleCast(origin, obstacleCheckRadius, aimDir, teleportDistance, obstacleLayerMask);
                if (hit.collider != null)
                {
                    actualDistance = Mathf.Max(0f, hit.distance - obstacleSafetyMargin);
                }
            }
            Vector3 destination = origin + (Vector3)(aimDir * actualDistance);

            // 4. 스무스 이동 — FreeMovement 차단으로 CharacterMovement override 방지
            bool prevFreeMovement = true;
            if (topDownController != null)
            {
                prevFreeMovement = topDownController.FreeMovement;
                topDownController.FreeMovement = false;
            }

            if (lungeDuration > 0f && topDownController != null)
            {
                float elapsed = 0f;
                while (elapsed < lungeDuration)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / lungeDuration);
                    float eased = 1f - Mathf.Pow(1f - t, 2f); // ease-out
                    Vector3 newPos = Vector3.Lerp(origin, destination, eased);
                    topDownController.MovePosition(newPos);
                    yield return null;
                }
                // 마지막 보정 — 부동소수점 누적 오차 방지
                topDownController.MovePosition(destination);
            }
            else
            {
                // fallback: 즉시 이동
                if (topDownController != null) topDownController.MovePosition(destination);
                else transform.position = destination;
            }

            if (topDownController != null) topDownController.FreeMovement = prevFreeMovement;

            // 5. 도착 후: 슬래시 + 데미지
            AttackStepData stepToUse = ResolveStep();

            if (slashAnimator != null && stepToUse != null)
            {
                slashAnimator.PlaySlashFromExternal(aimDir, stepToUse);
            }

            if (applyDamageOnArrive && hitbox != null && stepToUse != null && combo != null && combo.WeaponData != null)
            {
                ApplyDamageAtArrive(aimDir, stepToUse);
            }

            // 6. 쿨다운 갱신 (이동 완료 후)
            _nextAllowedAt = Time.time + cooldown;

            if (logTeleport)
            {
                Debug.Log($"[KhiDaggerTeleport] dash → dir={aimDir} dist={actualDistance:F2}/{teleportDistance:F2} mana={mana?.CurrentMana ?? -1}", this);
            }
        }

        /// <summary>
        /// 우클릭 텔레포트 전용 step (teleportSlashStep) 이 valid 면 그걸 사용.
        /// 아니면 SO 의 slashComboStep (1/2/3) 에 해당하는 step fallback.
        /// </summary>
        private AttackStepData ResolveStep()
        {
            // 텔레포트 전용 step 이 valid (slashFrames 있음) 면 우선
            if (teleportSlashStep != null && teleportSlashStep.slashFrames != null && teleportSlashStep.slashFrames.Length > 0)
            {
                return teleportSlashStep;
            }
            // fallback: SO 의 step
            if (combo != null && combo.WeaponData != null && combo.WeaponData.Steps != null && combo.WeaponData.Steps.Length > 0)
            {
                int slotIndex = Mathf.Clamp(slashComboStep - 1, 0, combo.WeaponData.Steps.Length - 1);
                return combo.WeaponData.Steps[slotIndex];
            }
            return null;
        }

        private void ApplyDamageAtArrive(Vector2 aimDir, AttackStepData step)
        {
            // 데미지 계산 (KhiMeleeComboController.RunAttack 패턴 차용).
            // baseDamage 는 combo.WeaponData 기준 (단검 무기의 기본 데미지). teleportSlashStep 의 damageMultiplier 가 그 위에 곱해짐.
            CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                new CombatDamageRequest(
                    combo.WeaponData.BaseDamage * step.damageMultiplier,
                    DamageSourceKind.Area,
                    0UL,
                    sequenceId: _sequenceId + 1,
                    sourceId: (ulong)(_sequenceId + 1),
                    applyAttackPower: true,
                    hitDirection: aimDir,
                    hitPoint: transform.position,
                    weaponId: combo.WeaponData != null ? combo.WeaponData.name : null),
                statContainer);
            float finalDamage = damageResult.FinalDamage;

            // KhiAttackRequest 생성 (가짜 request)
            KhiAttackRequest request = new KhiAttackRequest
            {
                SequenceId = ++_sequenceId,
                ComboStep = step.comboStep,
                AimDirection = aimDir,
                AimAngleDegrees = Mathf.Atan2(aimDir.y, aimDir.x) * Mathf.Rad2Deg,
                Origin = transform.position,
                StartedAt = Time.time,
                Attacker = gameObject
            };

            _alreadyHit.Clear();
            hitbox.Sample(request, step, combo.WeaponData.GlobalHitboxPostRotationOffset, finalDamage, _alreadyHit, _hitsThisSample);
            for (int i = 0; i < _hitsThisSample.Count; i++)
            {
                RaiseTeleportDamageApplied(_hitsThisSample[i], finalDamage, damageResult.WasCritical, request.SequenceId, aimDir);
            }
        }

        private IEnumerator EnableDamageLater()
        {
            yield return new WaitForSeconds(invulnerabilityDuration);
            if (health != null) health.DamageEnabled();
        }

        private void RaiseTeleportDamageApplied(
            Health target,
            float finalDamage,
            bool wasCritical,
            int sequenceId,
            Vector2 direction)
        {
            if (target == null || finalDamage <= 0f)
            {
                return;
            }

            var request = new CombatDamageRequest(
                finalDamage,
                DamageSourceKind.Area,
                ResolveNetworkObjectId(target),
                attackerNetworkObjectId: ResolveNetworkObjectId(gameObject),
                sourceId: (ulong)sequenceId,
                sequenceId: sequenceId,
                criticalPolicy: CriticalPolicy.Never,
                applyAttackPower: false,
                hitDirection: direction,
                hitPoint: target.transform.position,
                weaponId: combo != null && combo.WeaponData != null ? combo.WeaponData.name : "DaggerTeleport");
            var result = new CombatDamageResult(request, finalDamage, wasCritical, target.CurrentHealth <= 0f);
            CombatDamageEventDispatcher.RaiseDamageApplied(new CombatDamageEvent(result, target, gameObject));
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
