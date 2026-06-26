using System.Collections;
using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.TestKhi;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.MagicalGirl
{
    /// <summary>
    /// CL-145 (Phase 3): 미소녀 5합체 fusion 엔티티 — T/Y 키 분리 발동, 공유 쿨다운.
    ///
    /// MagicalGirlSpawner.EnsureFusion 가 GameObject 생성 후 AddComponent + Init 호출.
    /// Awake 에서 절차적 SpriteRenderer (분홍, 2x 크기).
    ///
    /// 발동 방식 (Phase 3 변경 — 사용자 원래 의도):
    /// - T 키: 레이저 5초 마우스 조준 지속 (오버워치 모이라 궁 풍)
    /// - Y 키: AOE 즉발 화면 전체 (강력 광역)
    /// - 공유 쿨다운 25초 — 한쪽 사용하면 둘 다 25초 막힘 (Spawner-tracked)
    /// - 전략적 선택: 적 모임 → AOE / 보스 단일 → 레이저
    /// - Fusion 진입 시 cooldown=0 (즉시 사용 가능, 5스택 도달 보상감)
    ///
    /// 카메라 흔들림 (Phase 3):
    /// - AOE (Y): 강한 흔들림 0.3초 / 0.3 강도 + 화면 플래시
    /// - 레이저 (T): 시작 약한 흔들림 + 매 1초 펄스 5번 (총 6번 = burst 동안)
    ///
    /// Character.AI 필터 (CL-144 패턴): ArcherArrow 등 발사체 제외.
    /// 무한 루프 방지: Health.Damage 직접 호출 (TargetHit 이벤트 무관).
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MagicalGirlFusion : MonoBehaviour
    {
        // CL-204 후속: SpriteRenderer 생성은 MagicalGirlSpawner.EnsureFusion 가 담당.
        // 기존 절차적 SpriteRenderer 생성 (Awake) 은 spawner 의 SpriteRenderer 와 중복돼 NRE 발생 — 제거됨.

        [Header("Burst (Phase 2 — R 키)")]
        [Tooltip("Burst 지속시간 (초). R 누른 후 레이저 발화 길이.")]
        [SerializeField, Min(0.1f)] private float burstDuration = 5f;

        [Tooltip("Burst 종료 후 다음 R 까지 쿨다운 (초).")]
        [SerializeField, Min(0f)]   private float cooldown = 25f;

        [Header("Laser")]
        [SerializeField, Min(0.05f)]private float laserTickInterval = 0.1f;
        [SerializeField, Min(0f)]   private float laserDamageRatio = 0.5f;
        [SerializeField, Min(0.1f)] private float laserLength = 10f;
        [SerializeField, Min(0.1f)] private float laserWidth = 1f;

        [Header("Global AOE (Burst 시작 시 1회)")]
        [SerializeField, Min(0f)]   private float aoeDamageRatio = 3.0f;

        [Header("Camera Shake")]
        [Tooltip("Burst 시작 (= AOE 폭발) 시 카메라 흔들림 지속 (초).")]
        [SerializeField, Min(0f)]   private float burstShakeDuration = 0.3f;

        [Tooltip("Burst 시작 시 카메라 흔들림 강도 (유닛).")]
        [SerializeField, Min(0f)]   private float burstShakeIntensity = 0.3f;

        [Tooltip("Burst 중 주기적 흔들림 (1초 간격) 지속 (초). 시작 흔들림보다 짧게.")]
        [SerializeField, Min(0f)]   private float periodicShakeDuration = 0.15f;

        [Tooltip("Burst 중 주기적 흔들림 강도 (유닛). 시작보다 약하게 — 두근거리는 느낌.")]
        [SerializeField, Min(0f)]   private float periodicShakeIntensity = 0.15f;

        [Tooltip("Burst 중 주기적 흔들림 간격 (초). 1초 = 매 초마다 한 번씩.")]
        [SerializeField, Min(0.1f)] private float periodicShakeInterval = 1f;

        [Header("Visual Toggle")]
        [Tooltip("T 키 레이저 분홍 LineRenderer 빔 시각 효과 표시 여부. false 면 데미지/카메라 흔들림/Trail VFX 는 그대로지만 분홍 빔이 안 보임. (임시 비활성화용)")]
        [SerializeField] private bool _showLaserLine = false;

        [Header("Debug")]
        [SerializeField] private bool _logFusion = false;

        private bool _burstActive;
        private float _nextReadyAt;          // Time.time 기준, 초기값 0 = 즉시 사용 가능
        private Coroutine _shakeCoroutine;

        private PlayerStatModifierContainer _stat;
        private KhiMeleeComboController _combat;
        private KhiPlayerAim _aim;
        private int _laserTickIndex;
        private KhiDownController _ownerDownController;
        private MagicalGirlSpawner _spawner;        // CL-145 Phase 2: cooldown 공유 source

        // CL-204 후속: 궁극 VFX prefab — Spawner 가 Init 시 전달
        private GameObject _laserMuzzlePrefab;
        private GameObject _laserTrailPrefab;
        private GameObject _aoeExplosionPrefab;
        private GameObject _aoeShockwavePrefab;
        private GameObject _aoeChargePrefab;

        // CL-204 후속: 비정상 종료 (fusion destroy 등) 시 정리하기 위한 instance 참조.
        // Trail/Line 은 fusion 자식이 아닌 root spawn 이라 fusion destroy 시 자동 정리 안 됨.
        private GameObject _activeTrailGO;
        private GameObject _activeLaserLineGO;

        private static Material _laserMaterial;

        // 레이저 visual 색상 (분홍 핑크빛)
        private static readonly Color LaserColor = new Color(1f, 0.4f, 0.8f, 1f);
        private const float LaserBoltWidth = 0.4f;
        private const int LaserBoltSortingOrder = 9999;

        public void Init(PlayerStatModifierContainer stat, KhiMeleeComboController combat, KhiPlayerAim aim, MagicalGirlSpawner spawner)
        {
            Init(stat, combat, aim, spawner, default);
        }

        public void Init(PlayerStatModifierContainer stat, KhiMeleeComboController combat, KhiPlayerAim aim, MagicalGirlSpawner spawner, FusionVfxBundle vfx)
        {
            Init(stat, combat, aim, spawner, vfx, null);
        }

        public void Init(
            PlayerStatModifierContainer stat,
            KhiMeleeComboController combat,
            KhiPlayerAim aim,
            MagicalGirlSpawner spawner,
            FusionVfxBundle vfx,
            KhiDownController ownerDownController)
        {
            _stat = stat;
            _combat = combat;
            _aim = aim;
            _spawner = spawner;
            _ownerDownController = ownerDownController;
            _laserMuzzlePrefab = vfx.laserMuzzle;
            _laserTrailPrefab = vfx.laserTrail;
            _aoeExplosionPrefab = vfx.aoeExplosion;
            _aoeShockwavePrefab = vfx.aoeShockwave;
            _aoeChargePrefab = vfx.aoeCharge;
            // Spawner 가 보관한 이전 cooldown 시점 적용 — 빌드 풀고 재진입해도 cooldown 유지.
            // 첫 fusion 진입 시 spawner._fusionCooldownEndsAt=0 → 즉시 사용 가능.
            if (_spawner != null)
                _nextReadyAt = _spawner.FusionCooldownEndsAt;
        }

        [System.Serializable]
        public struct FusionVfxBundle
        {
            public GameObject laserMuzzle;
            public GameObject laserTrail;
            public GameObject aoeExplosion;
            public GameObject aoeShockwave;
            public GameObject aoeCharge;  // CL-204 후속: Y 키 빌드업 시 작게 등장해 커지는 charge VFX
        }

        // ── CL-204: Spawner-driven 발동 ──────────────────────
        // CL-204: T/Y 폴링은 MagicalGirlSpawner 가 담당. Fusion 은 임시로 spawn 되어 한 번 발화 후 destroy.
        // public Trigger* 메서드만 노출, 쿨다운/cooldown gating 은 Spawner 가 처리.

        /// <summary>CL-204: T 키 발동. 5초 레이저 burst + 매 1초 펄스 흔들림.</summary>
        public void TriggerLaserBurst()
        {
            if (IsOwnerActionBlocked()) return;
            if (_burstActive) return;
            _burstActive = true;
            if (_logFusion) Debug.Log($"[Fusion] LASER BURST START (duration={burstDuration}s, cooldown={cooldown}s after)");

            if (_spawner != null)
                _spawner.NotifyBurstStarted(burstDuration, cooldown);

            TriggerCameraShake(periodicShakeDuration, periodicShakeIntensity);
            StartCoroutine(BurstLaserCoroutine());
            StartCoroutine(PeriodicShakeCoroutine());
        }

        /// <summary>CL-204 후속: Y 키 발동. Charge 빌드업 → AOE 폭발 패턴.</summary>
        public void TriggerAOEPulse()
        {
            if (IsOwnerActionBlocked()) return;
            if (_burstActive) return;
            _burstActive = true;
            if (_logFusion) Debug.Log($"[Fusion] AOE CHARGE START (cooldown={cooldown}s after)");

            if (_spawner != null)
                _spawner.NotifyBurstStarted(0f, cooldown);
            _nextReadyAt = Time.time + cooldown;

            StartCoroutine(ChargeAndFireAOECoroutine());
        }

        // CL-204 후속: AOE 빌드업 코루틴. charge VFX 가 startScale → endScale 로 커지면서 chargeDuration 동안 대기 → 펑.
        private IEnumerator ChargeAndFireAOECoroutine()
        {
            if (IsOwnerActionBlocked())
            {
                _burstActive = false;
                yield break;
            }

            float duration = _spawner != null ? _spawner.AoeChargeDuration : 0.5f;
            float startScale = _spawner != null ? _spawner.AoeChargeStartScale : 0.3f;
            float endScale = _spawner != null ? _spawner.AoeChargeEndScale : 2.0f;

            // Camera 중앙 위치 계산 — charge VFX spawn 위치
            Camera cam = Camera.main;
            Vector3 chargeOrigin = Vector3.zero;
            if (cam != null)
            {
                Vector2 vMin = cam.ViewportToWorldPoint(Vector2.zero);
                Vector2 vMax = cam.ViewportToWorldPoint(Vector2.one);
                chargeOrigin = new Vector3((vMin.x + vMax.x) * 0.5f, (vMin.y + vMax.y) * 0.5f, 0f);
            }

            // Charge VFX spawn (있을 때만) + 즉시 startScale 적용
            GameObject chargeGO = null;
            if (_aoeChargePrefab != null)
            {
                chargeGO = Instantiate(_aoeChargePrefab, chargeOrigin, Quaternion.identity);
                chargeGO.transform.localScale = new Vector3(startScale, startScale, 1f);
            }

            // Scale lerp — duration 동안 startScale → endScale (커지는 빌드업)
            float t = 0f;
            while (t < duration)
            {
                if (IsOwnerActionBlocked())
                {
                    if (chargeGO != null) Destroy(chargeGO);
                    _burstActive = false;
                    yield break;
                }

                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / duration);
                if (chargeGO != null)
                {
                    float scale = Mathf.Lerp(startScale, endScale, k);
                    chargeGO.transform.localScale = new Vector3(scale, scale, 1f);
                }
                yield return null;
            }

            // 펑 — charge VFX destroy + 데미지 + 메인 폭발/링/카메라 흔들림
            if (chargeGO != null) Destroy(chargeGO);

            if (IsOwnerActionBlocked())
            {
                _burstActive = false;
                yield break;
            }

            FireGlobalAOE();
            TriggerCameraShake(burstShakeDuration, burstShakeIntensity);
            _burstActive = false;
            if (_logFusion) Debug.Log("[Fusion] AOE BURST END");
        }

        private IEnumerator BurstLaserCoroutine()
        {
            yield return LaserCoroutine();
            _burstActive = false;
            _nextReadyAt = Time.time + cooldown;
            if (_logFusion) Debug.Log($"[Fusion] BURST END — cooldown {cooldown}s");
        }

        // Burst 중 매 periodicShakeInterval 초마다 흔들림 (시작 0초는 TryFireBurst 가 직접 처리).
        // burstDuration=5, interval=1 이면 +1, +2, +3, +4, +5 초 시점에 5번 발화 (총 6번 = 시작 포함).
        private IEnumerator PeriodicShakeCoroutine()
        {
            int totalPulses = Mathf.FloorToInt(burstDuration / periodicShakeInterval);
            for (int i = 1; i <= totalPulses && _burstActive; i++)
            {
                yield return new WaitForSeconds(periodicShakeInterval);
                if (!_burstActive) yield break;
                TriggerCameraShake(periodicShakeDuration, periodicShakeIntensity);
            }
        }

        // ── Pattern: 레이저 (burstDuration 동안 지속) ─────

        private IEnumerator LaserCoroutine()
        {
            float endsAt = Time.time + burstDuration;
            var damageBuf = new HashSet<Health>();   // 동일 틱 내 동일 적 중복 데미지 방지
            // _showLaserLine=false 면 LineRenderer 생성 자체를 스킵. UpdateLaserLine/OnDestroy 는 null-safe.
            GameObject lineGO = _showLaserLine ? CreateLaserLine() : null;
            _activeLaserLineGO = lineGO;
            int totalHits = 0;

            // CL-204 후속: Muzzle burst — 시작점 1회 fire-and-forget (ParticleSystem Stop Action=Destroy 가 정리)
            if (_laserMuzzlePrefab != null)
            {
                Vector2 muzzleOrigin = _combat != null ? (Vector2)_combat.transform.position : (Vector2)transform.position;
                Instantiate(_laserMuzzlePrefab, muzzleOrigin, Quaternion.identity);
            }

            // CL-204 후속: Trail VFX — root 레벨 spawn (fusion 자식 X) → fusion 의 scale 5x 영향 안 받음.
            // 파티클 sprite 회전은 prefab 의 Renderer Alignment=Local 로 처리.
            GameObject trailGO = null;
            if (_laserTrailPrefab != null)
            {
                trailGO = Instantiate(_laserTrailPrefab);  // parent 없음 = root 레벨
                _activeTrailGO = trailGO;
            }

            while (Time.time < endsAt && this != null && !IsOwnerActionBlocked())
            {
                damageBuf.Clear();
                Vector2 origin = _combat != null ? (Vector2)_combat.transform.position : (Vector2)transform.position;
                Vector2 dir = ComputeLaserDirection();
                Vector2 endPoint = origin + dir * laserLength;
                Vector2 boxCenter = origin + dir * (laserLength * 0.5f);
                Vector2 boxSize = new Vector2(laserLength, laserWidth);
                float angleDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

                float baseDamage = ComputePlayerDamage() * laserDamageRatio;
                if (baseDamage > 0f)
                {
                    _laserTickIndex++;
                    Collider2D[] hits = Physics2D.OverlapBoxAll(boxCenter, boxSize, angleDeg);
                    foreach (Collider2D col in hits)
                    {
                        if (col == null) continue;
                        Health h = col.GetComponentInParent<Health>();
                        if (!CombatTargetable.CanBeAutoTargetedEnemy(h)) continue;
                        if (!damageBuf.Add(h)) continue;
                        CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                            new CombatDamageRequest(
                                baseDamage,
                                DamageSourceKind.BeamOrStream,
                                0UL,
                                sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                                tickIndex: _laserTickIndex,
                                onHitPolicy: OnHitPolicy.TriggerWithCooldown,
                                applyAttackPower: false,
                                onHitCooldownSeconds: laserTickInterval,
                                hitDirection: dir,
                                hitPoint: h.transform.position,
                                weaponId: nameof(MagicalGirlFusion)),
                            _stat);
                        float damage = damageResult.FinalDamage;
                        h.Damage(damage, gameObject, 0f, 0f, Vector3.zero);
                        totalHits++;
                    }
                }

                UpdateLaserLine(lineGO, origin, endPoint);

                if (trailGO != null)
                {
                    // CL-204 후속: 부모 GameObject Z 회전 (emission box 방향) + spawn 위치 보정 (빔 local X/Y → world)
                    // 파티클 sprite 회전은 prefab 의 Renderer Render Alignment=Local 로 처리 (코드 sync 불필요)
                    float parentOffset = _spawner != null ? _spawner.TrailRotationOffset : -45f;
                    float parentAngleDeg = angleDeg + parentOffset;

                    Vector2 spawnOffsetLocal = _spawner != null ? _spawner.TrailSpawnOffset : Vector2.zero;
                    Vector2 perp = new Vector2(-dir.y, dir.x);  // dir 의 90° CCW 수직 벡터
                    Vector2 spawnPos = boxCenter + dir * spawnOffsetLocal.x + perp * spawnOffsetLocal.y;

                    trailGO.transform.SetPositionAndRotation(spawnPos, Quaternion.Euler(0f, 0f, parentAngleDeg));
                }

                yield return new WaitForSeconds(laserTickInterval);
            }

            if (lineGO != null) Destroy(lineGO);
            if (trailGO != null) Destroy(trailGO);
            _activeLaserLineGO = null;
            _activeTrailGO = null;
            if (_logFusion) Debug.Log($"[Fusion] Laser ended — {totalHits} total hits");
        }

        // CL-204 후속: 비정상 종료 (fusion destroy 등) 시에도 trail/line 정리 보장.
        // LaserCoroutine 가 정상 완료되면 위에서 이미 정리됨 (_active*GO = null).
        // 정상 완료 전에 fusion 이 destroy 되면 코루틴이 취소되고 trail/line 이 orphan 됨 → OnDestroy 에서 처리.
        private void OnDestroy()
        {
            if (_activeTrailGO != null) Destroy(_activeTrailGO);
            if (_activeLaserLineGO != null) Destroy(_activeLaserLineGO);
        }

        // ── Pattern B: 전역 AOE ────────────────────────────

        private void FireGlobalAOE()
        {
            if (IsOwnerActionBlocked()) return;

            Camera cam = Camera.main;
            if (cam == null)
            {
                if (_logFusion) Debug.Log("[Fusion] GlobalAOE skipped — Camera.main null");
                return;
            }

            Vector2 viewportMin = cam.ViewportToWorldPoint(Vector2.zero);
            Vector2 viewportMax = cam.ViewportToWorldPoint(Vector2.one);
            Vector2 size = viewportMax - viewportMin;
            Vector2 center = (viewportMin + viewportMax) * 0.5f;

            float baseDamage = ComputePlayerDamage() * aoeDamageRatio;
            int hitCount = 0;
            if (baseDamage > 0f)
            {
                Collider2D[] hits = Physics2D.OverlapBoxAll(center, size, 0f);
                foreach (Collider2D col in hits)
                {
                    if (col == null) continue;
                    Health h = col.GetComponentInParent<Health>();
                    if (!CombatTargetable.CanBeAutoTargetedEnemy(h)) continue;
                    CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                        new CombatDamageRequest(
                            baseDamage,
                            DamageSourceKind.Area,
                            0UL,
                            sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                            onHitPolicy: OnHitPolicy.Trigger,
                            applyAttackPower: false,
                            hitPoint: h.transform.position,
                            weaponId: nameof(MagicalGirlFusion)),
                        _stat);
                    float finalDamage = damageResult.FinalDamage;
                    h.Damage(finalDamage, gameObject, 0f, 0f, Vector3.zero);
                    hitCount++;
                }
            }

            // CL-204 후속: 흰 ScreenFlash 제거 → 폭발 prefab + 링 shockwave prefab 2장 합성
            if (_aoeExplosionPrefab != null)
                Instantiate(_aoeExplosionPrefab, new Vector3(center.x, center.y, 0f), Quaternion.identity);
            if (_aoeShockwavePrefab != null)
                Instantiate(_aoeShockwavePrefab, new Vector3(center.x, center.y, 0f), Quaternion.identity);

            float damage = baseDamage;
            if (_logFusion) Debug.Log($"[Fusion] GlobalAOE → {hitCount} hits, {damage:F1} each");
        }

        // ── 카메라 흔들림 (Phase 2) ────────────────────────

        private void TriggerCameraShake(float duration, float intensity)
        {
            Camera cam = Camera.main;
            if (cam == null || duration <= 0f || intensity <= 0f) return;
            if (_shakeCoroutine != null) StopCoroutine(_shakeCoroutine);
            _shakeCoroutine = StartCoroutine(ShakeCoroutine(cam, duration, intensity));
        }

        private IEnumerator ShakeCoroutine(Camera cam, float duration, float intensity)
        {
            Transform t = cam.transform;
            Vector3 originalLocal = t.localPosition;
            float elapsed = 0f;
            while (elapsed < duration && cam != null)
            {
                elapsed += Time.deltaTime;
                float damper = 1f - Mathf.Clamp01(elapsed / duration);
                Vector2 offset = Random.insideUnitCircle * intensity * damper;
                t.localPosition = originalLocal + (Vector3)offset;
                yield return null;
            }
            if (cam != null) t.localPosition = originalLocal;
            _shakeCoroutine = null;
        }

        // ── 데미지 / 방향 산출 ─────────────────────────────

        private float ComputePlayerDamage()
        {
            float atk = _combat != null && _combat.WeaponData != null ? _combat.WeaponData.BaseDamage : 0f;
            if (_stat != null) atk *= _stat.GetTotalMultiplier(StatId.AttackPower);
            return atk;
        }

        private Vector2 ComputeLaserDirection()
        {
            Vector2 dir = _aim != null ? _aim.GetAimDirection() : Vector2.zero;
            if (dir.sqrMagnitude < 0.0001f)
                dir = _combat != null ? (Vector2)_combat.transform.right : Vector2.right;
            return dir.normalized;
        }

        private bool IsOwnerActionBlocked()
        {
            return KhiPlayerActionGate.IsBlocked(_ownerDownController);
        }

        // ── 레이저 시각화 (CL-142 ChainBolt 패턴) ─────────

        private static Material GetLaserMaterial()
        {
            if (_laserMaterial != null) return _laserMaterial;
            Shader sh = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/2D/Sprite-Lit-Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color");
            if (sh == null)
            {
                Debug.LogError("[Fusion] Laser: shader 0개 발견. LineRenderer 보이지 않음.");
                return null;
            }
            _laserMaterial = new Material(sh) { color = LaserColor };
            return _laserMaterial;
        }

        private GameObject CreateLaserLine()
        {
            var go = new GameObject("FusionLaser");
            // Fusion 자식으로 parent 설정 → fusion destroy 시 자동 정리 (orphaned magenta line 방지).
            // useWorldSpace=true 라 LineRenderer position 은 절대좌표 사용, parent transform 무관.
            go.transform.SetParent(transform, worldPositionStays: false);
            var lr = go.AddComponent<LineRenderer>();
            Material mat = GetLaserMaterial();
            if (mat != null) lr.sharedMaterial = mat;
            lr.startColor = LaserColor;
            lr.endColor = LaserColor;
            lr.startWidth = LaserBoltWidth;
            lr.endWidth = LaserBoltWidth;
            lr.useWorldSpace = true;
            lr.positionCount = 2;
            lr.sortingOrder = LaserBoltSortingOrder;
            lr.alignment = LineAlignment.View;
            return go;
        }

        private static void UpdateLaserLine(GameObject go, Vector3 from, Vector3 to)
        {
            if (go == null) return;
            from.z = 0f;
            to.z = 0f;
            var lr = go.GetComponent<LineRenderer>();
            if (lr == null) return;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
        }
    }
}
