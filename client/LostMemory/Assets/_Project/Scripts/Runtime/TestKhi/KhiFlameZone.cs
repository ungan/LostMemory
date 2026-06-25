using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.Enemies;
using LostMemory.Networking.Player;
using MoreMountains.TopDownEngine;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LostMemory.TestKhi
{
    /// <summary>화염방사기 분사 모드 — 좌클릭(기본) / 우클릭(강화).</summary>
    public enum FlameMode
    {
        Primary = 0,
        Secondary = 1,
    }

    /// <summary>
    /// 화염방사기 콘 영역 데미지·화상 적용. 컨트롤러가 SetStreaming(true, mode) 호출 시점부터
    /// 매 tickInterval 마다 콘 안의 적을 스캔하여 직접 데미지 + EnemyStatusEffect.ApplyBurn 호출.
    ///
    /// 두 모드 지원 (Primary = 좌클릭, Secondary = 우클릭) — 각 모드별로 range/coneHalfAngleDeg/
    /// damage/burn 파라미터가 독립. 시각 효과(ParticleSystem)는 컨트롤러가 모드별로 따로 관리.
    /// 콘 방향은 transform.position → 마우스 worldPos. 본 컴포넌트는 어디에 부착해도 동작.
    /// </summary>
    [AddComponentMenu("Lost Memory/Test Khi/Khi Flame Zone")]
    public class KhiFlameZone : MonoBehaviour
    {
        [Header("Primary Cone (Left Click Hold)")]
        [Tooltip("좌클릭 분사 최대 거리(월드 유닛).")]
        [SerializeField, Min(0.5f)] private float range = 3.5f;
        [Tooltip("좌클릭 콘 절반 너비(중심선 기준). 30 = 총 60° 콘.")]
        [SerializeField, Range(5f, 80f)] private float coneHalfAngleDeg = 30f;

        [Header("Secondary Cone (Right Click Hold)")]
        [Tooltip("우클릭 강화 분사 최대 거리.")]
        [SerializeField, Min(0.5f)] private float secondaryRange = 5.5f;
        [Tooltip("우클릭 콘 절반 너비. 40 = 총 80° 콘.")]
        [SerializeField, Range(5f, 80f)] private float secondaryConeHalfAngleDeg = 40f;

        [Header("Targeting")]
        [Tooltip("적 Health 콜라이더 Layer. 활/스태프와 동일 Layer 사용 권장.")]
        [SerializeField] private LayerMask enemyLayer = ~0;
        [Tooltip("틱당 최대 후보 콜라이더 수(GC 방지용 고정 크기 버퍼).")]
        [SerializeField, Min(1)] private int maxTargetsPerTick = 16;

        [Header("VFX (선택)")]
        [Tooltip("화상 상태이상 VFX prefab. 비어있어도 데미지·DoT 는 적용되지만 화상 시각 효과는 안 보일 수 있음.\n프로젝트에 Fire_VFX.prefab 있음 — 그대로 사용 가능.")]
        [SerializeField] private GameObject burnVFXPrefab;

        [Header("Camera")]
        [Tooltip("마우스 worldPos 변환용. 비어있으면 Camera.main.")]
        [SerializeField] private Camera aimCamera;

        [Header("Debug")]
        [SerializeField] private bool drawGizmos = true;
        [SerializeField] private Color primaryGizmoColor = new Color(1f, 0.5f, 0f, 0.45f);
        [SerializeField] private Color secondaryGizmoColor = new Color(1f, 0.2f, 0.4f, 0.45f);

        // ── 런타임 파라미터 (컨트롤러가 Initialize 로 주입) ─────
        private struct ModeParams
        {
            public float damagePerSec;
            public float tickInterval;
            public float burnDamagePerSec;
            public float burnDuration;
        }

        private ModeParams _primary;
        private ModeParams _secondary;
        private GameObject _instigator;
        // 멀티: 게스트 owner 측에서 호스트로 데미지 위임. Initialize 시점에 instigator 의 PlayerDamageRelay 1회 캐시.
        // burn DOT 는 EnemyStatusEffect 자체 tick 이므로 본 경로로 sync 안 됨 — 별도 follow-up 필요.
        private PlayerDamageRelay _cachedRelay;
        private PlayerStatModifierContainer _instigatorStats;

        private bool _streaming;
        private FlameMode _activeMode;
        private float _nextTickAt;
        private int _tickIndex;

        private Collider2D[] _hitBuffer;
        private readonly HashSet<Health> _appliedThisTick = new HashSet<Health>();

        /// <summary>
        /// 컨트롤러가 Awake 시 1회 호출. Primary/Secondary 모드 파라미터를 한 번에 주입.
        /// </summary>
        public void Initialize(
            float primaryDamagePerSec, float primaryTickInterval, float primaryBurnDps, float primaryBurnDuration,
            float secondaryDamagePerSec, float secondaryTickInterval, float secondaryBurnDps, float secondaryBurnDuration,
            GameObject instigator)
        {
            _primary = new ModeParams
            {
                damagePerSec = primaryDamagePerSec,
                tickInterval = Mathf.Max(0.02f, primaryTickInterval),
                burnDamagePerSec = primaryBurnDps,
                burnDuration = primaryBurnDuration,
            };
            _secondary = new ModeParams
            {
                damagePerSec = secondaryDamagePerSec,
                tickInterval = Mathf.Max(0.02f, secondaryTickInterval),
                burnDamagePerSec = secondaryBurnDps,
                burnDuration = secondaryBurnDuration,
            };
            _instigator = instigator;
            _cachedRelay = instigator != null ? instigator.GetComponentInParent<PlayerDamageRelay>() : null;
            _instigatorStats = instigator != null ? instigator.GetComponentInParent<PlayerStatModifierContainer>() : null;
            _hitBuffer = new Collider2D[maxTargetsPerTick];
        }

        /// <summary>분사 활성/비활성 토글. 활성 시 활성 모드도 함께 지정.</summary>
        public void SetStreaming(bool streaming, FlameMode mode = FlameMode.Primary)
        {
            _streaming = streaming;
            _activeMode = mode;
            if (streaming) _nextTickAt = Time.time;
        }

        private void FixedUpdate()
        {
            if (!_streaming) return;
            if (Time.time < _nextTickAt) return;

            ref ModeParams p = ref GetActiveParams();
            _nextTickAt = Time.time + p.tickInterval;
            _tickIndex++;

            float damageThisTick = p.damagePerSec * p.tickInterval;
            ScanAndApply(damageThisTick, p.burnDamagePerSec, p.burnDuration, GetActiveRange(), GetActiveHalfAngle(), _tickIndex);
        }

        private ref ModeParams GetActiveParams()
        {
            if (_activeMode == FlameMode.Secondary) return ref _secondary;
            return ref _primary;
        }

        private float GetActiveRange() => _activeMode == FlameMode.Secondary ? secondaryRange : range;
        private float GetActiveHalfAngle() => _activeMode == FlameMode.Secondary ? secondaryConeHalfAngleDeg : coneHalfAngleDeg;

        private void ScanAndApply(float directDamage, float burnDps, float burnSec, float useRange, float useHalfAngle, int tickIndex)
        {
            if (_hitBuffer == null) _hitBuffer = new Collider2D[maxTargetsPerTick];

            Vector2 origin = transform.position;
            Vector2 aim = ComputeAim(origin);
            float aimAngleDeg = Mathf.Atan2(aim.y, aim.x) * Mathf.Rad2Deg;

            // 콘을 감싸는 회전된 박스: 길이=useRange, 폭=2*useRange*tan(half).
            float halfAngleRad = useHalfAngle * Mathf.Deg2Rad;
            float boxHeight = 2f * useRange * Mathf.Tan(halfAngleRad);
            Vector2 boxCenter = origin + aim * (useRange * 0.5f);
            Vector2 boxSize = new Vector2(useRange, Mathf.Max(0.1f, boxHeight));

            int count = Physics2D.OverlapBoxNonAlloc(boxCenter, boxSize, aimAngleDeg, _hitBuffer, enemyLayer);
            _appliedThisTick.Clear();

            for (int i = 0; i < count; i++)
            {
                Collider2D col = _hitBuffer[i];
                if (col == null) continue;

                Health victim = col.GetComponent<Health>() ?? col.GetComponentInParent<Health>();
                if (victim == null || victim.CurrentHealth <= 0f) continue;
                if (victim.gameObject == _instigator) continue;
                // PvP 미상정 — 다른 player 친아군 skip.
                if (CombatTargetable.IsFriendlyPlayer(victim)) continue;
                if (!_appliedThisTick.Add(victim)) continue; // 다중 콜라이더 중복 방지

                // 박스 내부 → 각도 필터로 부채꼴 정확도 향상.
                Vector2 toTarget = (Vector2)victim.transform.position - origin;
                if (toTarget.sqrMagnitude > useRange * useRange) continue;
                if (Vector2.Angle(aim, toTarget) > useHalfAngle) continue;

                if (directDamage > 0f)
                {
                    CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                        new CombatDamageRequest(
                            directDamage,
                            DamageSourceKind.BeamOrStream,
                            0UL,
                            sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                            tickIndex: tickIndex,
                            onHitPolicy: OnHitPolicy.TriggerWithCooldown,
                            applyAttackPower: true,
                            onHitCooldownSeconds: 0.5f,
                            hitDirection: aim,
                            hitPoint: victim.transform.position,
                            weaponId: _activeMode == FlameMode.Secondary ? "FlamethrowerSecondary" : "FlamethrowerPrimary"),
                        _instigatorStats);
                    float resolvedDirectDamage = damageResult.FinalDamage;

                    if (_cachedRelay != null)
                    {
                        _cachedRelay.RelayDamage(victim, resolvedDirectDamage, _instigator, 0f, 0f, Vector2.zero);
                    }
                    else
                    {
                        victim.Damage(resolvedDirectDamage, _instigator, 0f, 0f, Vector3.zero);
                    }
                }

                if (burnDps > 0f && burnSec > 0f)
                {
                    EnemyStatusEffect status = victim.GetComponent<EnemyStatusEffect>()
                                              ?? victim.GetComponentInParent<EnemyStatusEffect>();
                    if (status == null)
                    {
                        status = victim.gameObject.AddComponent<EnemyStatusEffect>();
                    }
                    if (burnVFXPrefab != null)
                    {
                        status.EnsureVFXPrefabs(null, burnVFXPrefab, null);
                    }
                    status.ApplyBurn(burnDps, burnSec, _instigator);
                }
            }
        }

        private Vector2 ComputeAim(Vector2 origin)
        {
            Camera cam = aimCamera != null ? aimCamera : Camera.main;
            Mouse mouse = Mouse.current;
            if (cam == null || mouse == null) return Vector2.right;
            Vector2 screenPos = mouse.position.ReadValue();
            Vector3 worldPos = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, -cam.transform.position.z));
            Vector2 dir = (Vector2)worldPos - origin;
            return dir.sqrMagnitude > Mathf.Epsilon ? dir.normalized : Vector2.right;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!drawGizmos) return;
            Vector2 origin = transform.position;
            Vector2 aim = Application.isPlaying ? ComputeAim(origin) : (Vector2)transform.right;
            DrawConeGizmo(origin, aim, range, coneHalfAngleDeg, primaryGizmoColor);
            DrawConeGizmo(origin, aim, secondaryRange, secondaryConeHalfAngleDeg, secondaryGizmoColor);
        }

        private static void DrawConeGizmo(Vector2 origin, Vector2 aim, float useRange, float useHalfAngle, Color color)
        {
            float halfRad = useHalfAngle * Mathf.Deg2Rad;
            float c = Mathf.Cos(halfRad), s = Mathf.Sin(halfRad);
            Vector2 edgeA = new Vector2(aim.x * c - aim.y * s, aim.x * s + aim.y * c);
            Vector2 edgeB = new Vector2(aim.x * c + aim.y * s, -aim.x * s + aim.y * c);

            Gizmos.color = color;
            Gizmos.DrawLine(origin, origin + edgeA * useRange);
            Gizmos.DrawLine(origin, origin + edgeB * useRange);
            Gizmos.DrawLine(origin + edgeA * useRange, origin + edgeB * useRange);
            Gizmos.DrawLine(origin, origin + aim * useRange);
        }
#endif
    }
}
