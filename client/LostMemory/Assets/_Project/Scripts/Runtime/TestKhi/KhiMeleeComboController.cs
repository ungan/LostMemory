using System;
using System.Collections;
using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.Data;
using MoreMountains.TopDownEngine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LostMemory.TestKhi
{
    [DefaultExecutionOrder(50)]
    public class KhiMeleeComboController : MonoBehaviour
    {
        private const float DefaultHeldAttackBaseInterval = 0.35f;
        private const float MinimumHeldAttackInterval = 0.02f;

        [SerializeField] private KhiPlayerAim aim;
        [SerializeField] private KhiMeleeHitbox hitbox;
        [SerializeField] private KhiAttackVisualPresenter visualPresenter;
        [SerializeField] private KhiDashController dash;
        [SerializeField] private KhiDownController downController;
        [Tooltip("무기 데이터 SO. 필수. baseDamage / 콤보 윈도우 / 콤보 step 데이터를 모두 담음.")]
        [SerializeField] private WeaponData weaponData;
        [SerializeField] private bool allowInputDuringRecovery = true;
        [SerializeField] private bool allowAttackDuringDash = false;
        [SerializeField] private bool bufferAttackDuringDash = false;
        [SerializeField] private bool disableTdeHandleWeapon = true;
        [SerializeField] private bool logHitsToConsole = false;
        [Tooltip("Hold auto-attack interval at AttackSpeed 1x. Current player AttackSpeed divides this value.")]
        [SerializeField, Min(0.02f)] private float heldAttackBaseInterval = DefaultHeldAttackBaseInterval;
        [Tooltip("CL-107: AttackPower / AttackSpeed / FinisherDamage multiplier 조회용. 같은 GameObject 또는 Player root 의 컴포넌트.")]
        [SerializeField] private PlayerStatModifierContainer statContainer;

        private readonly HashSet<Health> _alreadyHitThisSwing = new HashSet<Health>();
        private readonly List<Health> _hitsThisSample = new List<Health>(8);

        private CharacterHandleWeapon _tdeHandleWeapon;
        // Phase E: 비-owner 측 입력 중복 발화 차단용 cached NetworkObject. 부모 chain 에서 1회 resolve.
        private NetworkObject _cachedNetObj;
        private bool _netObjResolved;
        private bool _isAttacking;
        private bool _isInAttackRecovery;
        private bool _externalAbortRequested;
        private int _nextComboStep = 1;
        private int _currentComboStep;
        private int _sequenceId;
        private float _comboExpiresAt = -1f;
        private float _chainInputAllowedAt = -1f;
        private float _bufferedAttackExpiresAt = -1f;
        private float _lastHeldAttackRequestAt = float.NegativeInfinity;

        public event Action<KhiAttackRequest, AttackStepData> AttackStarted;
        public event Action<KhiAttackRequest, AttackStepData> AttackActiveStarted;
        public event Action<KhiAttackRequest, AttackStepData> AttackActiveEnded;
        /// <summary>
        /// 대상 1개에 hit 적용 시 발화. finalDamage / wasCritical 포함 — popup UI 등에서 사용.
        /// 시그니처 변경 시 구독자(OnHitEffectRegistry 등)도 같이 업데이트 필요.
        /// </summary>
        public event Action<KhiAttackRequest, AttackStepData, Health, float, bool> TargetHit;
        public event Action<KhiAttackRequest, AttackStepData> FinisherHit;

        /// <summary>CL-107: 본 컨트롤러의 공격이 대상을 사망시켰을 때 발화. RelicEffectApplier 의 붉은송곳니 등이 구독.</summary>
        public event Action<KhiAttackRequest, AttackStepData, Health> EnemyKilledByPlayer;

        /// <summary>
        /// CL-230: weaponData SO 가 교체된 직후 발화. (previous, next) — 한쪽이 null 일 수 있음.
        /// WeaponUpgradeService / UI / SFX / 도전과제 등이 구독.
        /// </summary>
        public event Action<WeaponData, WeaponData> WeaponDataChanged;

        public bool IsAttacking => _isAttacking;
        public bool IsInAttackRecovery => _isInAttackRecovery;
        public bool BlocksDash => _isAttacking;

        /// <summary>
        /// 외부 시스템에서 본 컨트롤러가 참조하는 WeaponData를 읽기 위한 getter.
        /// 예: KhiSlashAnimator 가 같은 SO를 공유하고 싶을 때.
        /// </summary>
        public WeaponData WeaponData => weaponData;

        /// <summary>
        /// 외부 시스템(예: KhiParryController)이 공격 입력을 일시적으로 차단하기 위한 플래그.
        /// true인 동안 Update의 공격 입력 감지와 RequestAttack 진입을 모두 무시한다.
        /// 기본값 false이며, 설정한 시스템이 반드시 false로 복원해야 한다.
        /// </summary>
        public bool ExternalBlock { get; set; }

        /// <summary>
        /// 진행 중인 공격 코루틴을 즉시 중단 요청한다. (CL-013 피격 경직용)
        /// 공격 중이 아니면 아무 일도 하지 않는다.
        /// 중단 시 콤보 상태는 초기화되고 버퍼된 공격은 폐기된다.
        /// </summary>
        public void AbortCurrentAttack()
        {
            if (_isAttacking)
            {
                _externalAbortRequested = true;
            }
        }

        /// <summary>
        /// CL-230: 런타임에 무기 SO 를 교체한다. WeaponUpgradeService 가 호출.
        ///
        /// finishCurrentSwing=true (기본): 진행 중 swing 은 RunAttack 시작부에 캡처된 baseDamage/postRotationOffset
        ///   로 끝까지 진행 (race 안전). 다음 swing 부터 새 SO 의 데이터 사용.
        /// finishCurrentSwing=false: AbortCurrentAttack 호출 후 교체. 다음 frame 의 RunAttack 마지막에서
        ///   FinalizeAbortedAttack 가 호출되어 _isAttacking 가 reset됨.
        ///
        /// 두 경우 모두 본 메서드 종료 시점에 콤보 진행도는 1타로 초기화된다 (새 무기는 1타부터 시작이 자연스러움).
        ///
        /// 시각 동기화:
        /// - KhiAttackVisualPresenter / KhiMeleeHitbox: 매 swing comboController.WeaponData 를 읽음 → 자동 추종.
        /// - KhiWeaponPresenter: WeaponData 미사용 → 무관.
        /// - KhiSlashAnimator: 자체 [SerializeField] weaponData 필드 보유 → 본 메서드에서 같은 GameObject 의
        ///   KhiSlashAnimator 를 찾아 명시적으로 SetWeaponData(next) 호출로 동기화.
        /// </summary>
        public void SetWeaponData(WeaponData next, bool finishCurrentSwing = true)
        {
            if (next == null)
            {
                Debug.LogWarning("[KhiMeleeComboController] SetWeaponData(null) 무시.", this);
                return;
            }
            if (next == weaponData)
            {
                return;
            }
            if (next.Steps == null || next.Steps.Length == 0)
            {
                Debug.LogError($"[KhiMeleeComboController] SetWeaponData: 새 WeaponData '{next.name}' 의 Steps 가 비어있음. 교체 거부.", this);
                return;
            }

            if (!finishCurrentSwing && _isAttacking)
            {
                AbortCurrentAttack();
                // FinalizeAbortedAttack 은 다음 frame 의 RunAttack 코루틴 안에서 호출됨 → _isAttacking 가 잠시 true 유지.
                // 본 메서드는 SO 교체만 끝내고 빠짐. 다음 swing 은 자동으로 새 SO 1타로 시작.
            }

            WeaponData previous = weaponData;
            weaponData = next;

            // 콤보 진행도 reset — 새 무기는 1타부터 시작이 직관적.
            _nextComboStep = 1;
            _comboExpiresAt = -1f;
            ClearBufferedAttack();

            // 같은 GameObject 의 KhiSlashAnimator 동기화 (자체 weaponData 필드 보유).
            KhiSlashAnimator slashAnim = GetComponent<KhiSlashAnimator>();
            if (slashAnim != null)
            {
                slashAnim.SetWeaponData(next);
            }

            WeaponDataChanged?.Invoke(previous, next);
        }

        private void Awake()
        {
            aim ??= GetComponent<KhiPlayerAim>();
            hitbox ??= GetComponent<KhiMeleeHitbox>();
            visualPresenter ??= GetComponent<KhiAttackVisualPresenter>();
            dash ??= GetComponent<KhiDashController>();
            downController ??= GetComponent<KhiDownController>()
                ?? GetComponentInParent<KhiDownController>()
                ?? GetComponentInChildren<KhiDownController>();
            statContainer ??= GetComponent<PlayerStatModifierContainer>()
                ?? GetComponentInParent<PlayerStatModifierContainer>()
                ?? GetComponentInChildren<PlayerStatModifierContainer>(true);
            _tdeHandleWeapon = GetComponent<CharacterHandleWeapon>();

            if (weaponData == null)
            {
                Debug.LogError($"[KhiMeleeComboController] WeaponData 가 할당되지 않음. {name} 의 Inspector 에서 SO 자산을 드래그하세요.", this);
                enabled = false;
                return;
            }

            if (weaponData.Steps == null || weaponData.Steps.Length == 0)
            {
                Debug.LogError($"[KhiMeleeComboController] WeaponData '{weaponData.name}' 의 Steps 가 비어있음.", this);
                enabled = false;
            }
        }

        private IEnumerator Start()
        {
            if (!disableTdeHandleWeapon)
            {
                yield break;
            }

            yield return null;
            DisableTdeWeaponHandling();
        }

        private void Update()
        {
            // Phase E: 비-owner 측에서 마우스 입력이 자체적으로 RequestAttack 발화 → AttackBroadcast 가
            // 또 한 번 ServerRpc 발사하는 중복 패턴 차단. owner 1명만 입력 처리.
            if (IsRemoteClone())
            {
                return;
            }

            if (IsDownStateBlockingAttack())
            {
                AbortCurrentAttack();
                ClearBufferedAttack();
                return;
            }

            if (ExternalBlock)
            {
                ResetHeldAttackClock();
            }
            else if (ShouldRequestAttackFromInput())
            {
                RequestAttack();
            }

            if (!_isAttacking && _nextComboStep != 1 && Time.time > _comboExpiresAt)
            {
                _nextComboStep = 1;
            }
        }

        public void RequestAttack()
        {
            // Phase E: 외부에서 RequestAttack 을 직접 호출하더라도 비-owner 측은 자체 공격 코루틴 시작 금지.
            if (IsRemoteClone())
            {
                return;
            }

            if (ExternalBlock || IsDownStateBlockingAttack())
            {
                return;
            }

            if (ShouldBlockAttackForDash())
            {
                return;
            }

            if (_isAttacking)
            {
                TryBufferAttack();
                return;
            }

            if (_nextComboStep != 1 && Time.time > _comboExpiresAt)
            {
                _nextComboStep = 1;
            }

            StartCoroutine(RunAttack(_nextComboStep));
        }

        private IEnumerator RunAttack(int comboStep)
        {
            _isAttacking = true;
            _isInAttackRecovery = false;
            _externalAbortRequested = false;
            ClearBufferedAttack();
            _alreadyHitThisSwing.Clear();

            if (IsDownStateBlockingAttack())
            {
                FinalizeAbortedAttack();
                yield break;
            }

            AttackStepData step = GetStep(comboStep);
            _currentComboStep = step.comboStep;

            // CL-107: AttackSpeed multiplier 적용. AttackStepData 는 [Serializable] (WeaponData.Steps 배열 항목)
            // → in-place 변경 시 SO 데이터 변조됨. 반드시 local 변수로 캡처.
            float speedMul = statContainer != null ? statContainer.GetTotalMultiplier(StatId.AttackSpeed) : 1f;
            if (speedMul <= 0f) speedMul = 1f;
            float startupDur = step.startupDuration / speedMul;
            float activeDur = step.activeDuration / speedMul;
            float recoveryDur = step.recoveryDuration / speedMul;

            // CL-230: swing 도중 SetWeaponData 로 SO 가 교체되어도 본 swing 의 데미지/오프셋이 frame 단위로 점프하지
            // 않도록 swing 시작 시점 값을 로컬 캡처. 다음 swing 부터 새 SO 의 BaseDamage 반영.
            float capturedBaseDamage = weaponData.BaseDamage;
            Vector2 capturedPostRotationOffset = weaponData.GlobalHitboxPostRotationOffset;

            Vector2 aimDirection = aim != null ? aim.GetAimDirection() : Vector2.right;
            if (aimDirection.sqrMagnitude <= Mathf.Epsilon)
            {
                aimDirection = Vector2.right;
            }
            float aimAngleDeg = Mathf.Atan2(aimDirection.y, aimDirection.x) * Mathf.Rad2Deg;
            KhiAttackRequest request = new KhiAttackRequest
            {
                SequenceId = ++_sequenceId,
                ComboStep = step.comboStep,
                AimDirection = aimDirection,
                AimAngleDegrees = aimAngleDeg,
                Origin = transform.position,
                StartedAt = Time.time,
                Attacker = gameObject
            };

            AttackStarted?.Invoke(request, step);
            _chainInputAllowedAt = Mathf.Max(
                request.StartedAt + weaponData.MinimumChainInputDelay,
                request.StartedAt + startupDur + activeDur * 0.5f);

            if (startupDur > 0f)
            {
                yield return new WaitForSeconds(startupDur);
            }

            if (_externalAbortRequested || IsDownStateBlockingAttack())
            {
                FinalizeAbortedAttack();
                yield break;
            }

            bool hitAnyTarget = false;
            float activeEndsAt = Time.time + activeDur;
            // active 시작 시점의 위치를 request.Origin에 반영 (windup 중 플레이어 이동 보정).
            request.Origin = transform.position;
            AttackActiveStarted?.Invoke(request, step);

            while (Time.time < activeEndsAt && !_externalAbortRequested && !IsDownStateBlockingAttack())
            {
                KhiAttackRequest sampleRequest = request;
                sampleRequest.Origin = transform.position;
                // CL-107: AttackPower multiplier (전사의끈/전투북 등) + FinisherDamage multiplier (분쇄의팔찌, 3타에만).
                CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                    new CombatDamageRequest(
                        capturedBaseDamage * step.damageMultiplier,
                        DamageSourceKind.Melee,
                        0UL,
                        sourceId: (ulong)request.SequenceId,
                        applyAttackPower: true,
                        applyFinisherDamage: step.comboStep == 3,
                        hitDirection: sampleRequest.AimDirection,
                        hitPoint: sampleRequest.Origin,
                        weaponId: weaponData != null ? weaponData.name : null),
                    statContainer);
                float finalDamage = damageResult.FinalDamage;
                bool wasCritical = damageResult.WasCritical;
                int sampledHitCount = hitbox != null
                    ? hitbox.Sample(sampleRequest, step, capturedPostRotationOffset, finalDamage, _alreadyHitThisSwing, _hitsThisSample)
                    : 0;
                hitAnyTarget |= sampledHitCount > 0;

                for (int i = 0; i < _hitsThisSample.Count; i++)
                {
                    Health hit = _hitsThisSample[i];
                    TargetHit?.Invoke(sampleRequest, step, hit, finalDamage, wasCritical);
                    // CL-107: 본 hit 으로 사망한 적 → EnemyKilledByPlayer 발화 (붉은송곳니용).
                    if (hit != null && hit.CurrentHealth <= 0f)
                    {
                        EnemyKilledByPlayer?.Invoke(sampleRequest, step, hit);
                    }
                }

                yield return null;
            }

            AttackActiveEnded?.Invoke(request, step);
            hitbox?.HideRuntimePreview();

            if (_externalAbortRequested || IsDownStateBlockingAttack())
            {
                FinalizeAbortedAttack();
                yield break;
            }

            if (hitAnyTarget && step.comboStep == 3)
            {
                FinisherHit?.Invoke(request, step);
                if (logHitsToConsole)
                {
                    Debug.Log($"[KhiMelee] Finisher hit sequence={request.SequenceId}");
                }
            }

            _isInAttackRecovery = true;
            float recoveryEndsAt = Time.time + recoveryDur;
            while (Time.time < recoveryEndsAt && !_externalAbortRequested && !IsDownStateBlockingAttack())
            {
                yield return null;
            }

            if (_externalAbortRequested || IsDownStateBlockingAttack())
            {
                FinalizeAbortedAttack();
                yield break;
            }

            bool shouldChainBufferedAttack = HasValidBufferedAttack(step.comboStep);
            ClearBufferedAttack();
            AdvanceCombo(step.comboStep);
            _isAttacking = false;
            _isInAttackRecovery = false;
            _currentComboStep = 0;

            if (shouldChainBufferedAttack)
            {
                RequestAttack();
            }
        }

        private void FinalizeAbortedAttack()
        {
            ClearBufferedAttack();
            _nextComboStep = 1;
            _comboExpiresAt = -1f;
            _isAttacking = false;
            _isInAttackRecovery = false;
            _currentComboStep = 0;
            _externalAbortRequested = false;
        }

        private bool ShouldBlockAttackForDash()
        {
            if (dash == null || !dash.IsDashing || allowAttackDuringDash)
            {
                return false;
            }

            if (bufferAttackDuringDash && _isAttacking)
            {
                TryBufferAttack();
            }

            return true;
        }

        private bool IsDownStateBlockingAttack()
        {
            return downController != null && (downController.IsDown || downController.IsDefeated);
        }

        /// <summary>
        /// Phase E: 본 컨트롤러가 비-owner clone 인지 판정. NetworkObject 가 spawn 됐고 IsOwner 가 아니면 true.
        /// 싱글 환경 (NetworkObject 없음) / NGO 미spawn 시점은 false 반환하여 정상 동작.
        /// </summary>
        private bool IsRemoteClone()
        {
            if (!_netObjResolved)
            {
                _cachedNetObj = GetComponentInParent<NetworkObject>();
                _netObjResolved = true;
            }
            return _cachedNetObj != null && _cachedNetObj.IsSpawned && !_cachedNetObj.IsOwner;
        }

        private void TryBufferAttack()
        {
            if (!allowInputDuringRecovery || Time.time < _chainInputAllowedAt)
            {
                return;
            }

            if (_currentComboStep >= 3)
            {
                return;
            }

            _bufferedAttackExpiresAt = Time.time + weaponData.InputBufferDuration;
        }

        private bool HasValidBufferedAttack(int completedStep)
        {
            return completedStep < 3 && _bufferedAttackExpiresAt >= Time.time;
        }

        private void ClearBufferedAttack()
        {
            _bufferedAttackExpiresAt = -1f;
        }

        private void AdvanceCombo(int completedStep)
        {
            if (completedStep >= 3)
            {
                _nextComboStep = 1;
                _comboExpiresAt = -1f;
                return;
            }

            _nextComboStep = completedStep + 1;
            _comboExpiresAt = Time.time + weaponData.ComboInputWindow;
        }

        private AttackStepData GetStep(int comboStep)
        {
            AttackStepData[] steps = weaponData.Steps;
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] != null && steps[i].comboStep == comboStep)
                {
                    return steps[i];
                }
            }
            // Fallback: 첫 번째 step 사용 (Awake 검증으로 null 보장됨)
            return steps[0];
        }

        private void DisableTdeWeaponHandling()
        {
            _tdeHandleWeapon ??= GetComponent<CharacterHandleWeapon>();
            if (_tdeHandleWeapon == null)
            {
                return;
            }

            _tdeHandleWeapon.ChangeWeapon(null, string.Empty);
            _tdeHandleWeapon.InitialWeapon = null;
            _tdeHandleWeapon.PermitAbility(false);
        }

        private bool ShouldRequestAttackFromInput()
        {
            if (IsAttackInputBlockedByUI())
            {
                ResetHeldAttackClock();
                return false;
            }

            bool isHeld = IsAttackHeldRaw();
            if (!isHeld)
            {
                ResetHeldAttackClock();
                return false;
            }

            float now = Time.time;
            if (WasAttackPressedRawThisFrame() || now >= _lastHeldAttackRequestAt + GetHeldAttackInterval())
            {
                _lastHeldAttackRequestAt = now;
                return true;
            }

            return false;
        }

        private float GetHeldAttackInterval()
        {
            float baseInterval = heldAttackBaseInterval > 0f
                ? heldAttackBaseInterval
                : DefaultHeldAttackBaseInterval;
            float speedMultiplier = GetAttackSpeedMultiplier();
            return Mathf.Max(MinimumHeldAttackInterval, baseInterval / speedMultiplier);
        }

        private float GetAttackSpeedMultiplier()
        {
            float speedMultiplier = statContainer != null
                ? statContainer.GetTotalMultiplier(StatId.AttackSpeed)
                : 1f;
            return speedMultiplier > 0f ? speedMultiplier : 1f;
        }

        private void ResetHeldAttackClock()
        {
            _lastHeldAttackRequestAt = float.NegativeInfinity;
        }

        private static bool IsAttackInputBlockedByUI()
        {
            if (LostMemory.UI.UIInputBlocker.IsBlocked)
            {
                return true;
            }

            var es = UnityEngine.EventSystems.EventSystem.current;
            return es != null && es.IsPointerOverGameObject();
        }

        private static bool WasAttackPressedRawThisFrame()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                return true;
            }

            Gamepad gamepad = Gamepad.current;
            return gamepad != null && gamepad.buttonWest.wasPressedThisFrame;
        }

        private static bool IsAttackHeldRaw()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed)
            {
                return true;
            }

            Gamepad gamepad = Gamepad.current;
            return gamepad != null && gamepad.buttonWest.isPressed;
        }

        // A-1·A-2 (UI 가드) + 공격 입력 게이트 처리.
        // - UI 패널(인벤토리·상점·보상창 등) 열린 동안 공격 입력 무시 (UIInputBlocker).
        // - 추가로 마우스가 UI 위에 있을 때도 차단 (EventSystem) — 두 경로 모두 안전망.
        // - 공격 입력은 새로 누른 순간만 처리한다.
        //   누르고 있는 상태를 매 프레임 처리하면 콤보 버퍼가 자동으로 쌓여 과속 연타가 된다.
        private static bool WasAttackPressedThisFrame()
        {
            // (1) 글로벌 UI 차단 — 인벤토리 등 패널이 열려있으면 키보드/마우스 무관 입력 차단.
            if (LostMemory.UI.UIInputBlocker.IsBlocked)
            {
                return false;
            }

            // (2) UI 위 클릭은 키보드 단축키 외 마우스로 들어와도 차단 (보조 가드).
            var es = UnityEngine.EventSystems.EventSystem.current;
            if (es != null && es.IsPointerOverGameObject())
            {
                return false;
            }

            // Edge-triggered so holding the button cannot auto-buffer every combo step.
            Mouse mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                return true;
            }

            Gamepad gamepad = Gamepad.current;
            return gamepad != null && gamepad.buttonWest.wasPressedThisFrame;
        }
    }
}
