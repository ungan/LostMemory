using System.Collections.Generic;
using LostMemory.Combat;
using LostMemory.TestKhi;
using LostMemory.VFX;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.MagicalGirl
{
    /// <summary>
    /// CL-204: 1·3·5번 미소녀 발사체. 직선 이동 + 첫 Character.AI 적중 시 데미지 1회 후 destroy.
    ///
    /// MagicalGirlAI.Attack() 가 catalog 의 vfxPrefab 을 Instantiate → Init(direction, damage, speed, lifetime) 호출.
    /// 본체에 Collider2D (Trigger) 가 붙어 있어야 적과 접촉 가능. 시각은 자식 ParticleSystem 으로 분리.
    ///
    /// 멀티 sync (2026-05-28): KhiArrowProjectile 의 _visualOnly + projectileId registry 패턴 동일 복제.
    ///   - owner client: 정상 데미지 + hit 시점에 broadcast 로 ID 전달 → 모든 client clone despawn
    ///   - non-owner client: SetVisualOnly(true) → 데미지 skip, 이동/회전/hit VFX 만 재현
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MagicalGirlProjectile : MonoBehaviour
    {
        private Vector2 _velocity;
        private float _damage;
        private float _expiresAt;
        private bool _hasHit;
        private GameObject _hitVfxPrefab;
        private KhiDownController _ownerDownController;
        // Init 시 owner stats 통해 1회 크리티컬 판정. popup 표시용.
        private bool _wasCritical;

        // 멀티 sync (2026-05-28) — KhiArrowProjectile 패턴 동일.
        private bool _visualOnly;
        private int _projectileId = -1;
        private MagicalGirlBroadcast _despawnBroadcaster;  // owner instance 만 set, non-owner clone 은 null

        // visual-only clone registry — owner 의 hit broadcast 도착 시 매칭 clone destroy.
        private static readonly Dictionary<int, MagicalGirlProjectile> _visualOnlyClones = new Dictionary<int, MagicalGirlProjectile>();
        // owner 측 ID 발급용 모노톤 카운터.
        private static int _nextProjectileId = 1;

        public static int AllocateProjectileId()
        {
            int id = _nextProjectileId++;
            if (_nextProjectileId <= 0) _nextProjectileId = 1; // overflow wrap
            return id;
        }

        public void SetVisualOnly(bool visualOnly)
        {
            _visualOnly = visualOnly;
        }

        /// <summary>spawn 발급 ID 저장. visual-only clone 의 경우 dictionary 등록.</summary>
        public void SetProjectileId(int id)
        {
            _projectileId = id;
            if (_visualOnly && id > 0)
            {
                _visualOnlyClones[id] = this;
            }
        }

        /// <summary>owner instance 전용 — hit 시점에 ID broadcast 호출용.</summary>
        public void SetDespawnBroadcaster(MagicalGirlBroadcast broadcaster)
        {
            _despawnBroadcaster = broadcaster;
        }

        /// <summary>owner 의 hit broadcast (ClientRpc) 도착 시 매칭 clone destroy.</summary>
        public static void DespawnVisualOnlyCloneById(int id)
        {
            if (id <= 0) return;
            if (!_visualOnlyClones.TryGetValue(id, out var clone)) return;
            _visualOnlyClones.Remove(id);
            if (clone == null) return;
            clone.SpawnHitVfx();
            Destroy(clone.gameObject);
        }

        private void OnDestroy()
        {
            if (_visualOnly && _projectileId > 0)
            {
                _visualOnlyClones.Remove(_projectileId);
            }
        }

        public void Init(Vector2 direction, float damage, float speed, float lifetime, GameObject hitVfxPrefab = null)
        {
            Init(direction, damage, speed, lifetime, hitVfxPrefab, null);
        }

        public void Init(
            Vector2 direction,
            float damage,
            float speed,
            float lifetime,
            GameObject hitVfxPrefab,
            KhiDownController ownerDownController)
        {
            _velocity = direction.normalized * speed;
            _expiresAt = Time.time + lifetime;
            _hitVfxPrefab = hitVfxPrefab;
            _ownerDownController = ownerDownController;
            // 동료 공격에도 주인의 크리티컬 stat 적용 — owner GameObject 통해 lazy resolve.
            PlayerStatModifierContainer stats = ownerDownController != null
                ? ownerDownController.GetComponentInParent<PlayerStatModifierContainer>() : null;
            // AttackPower 적용 — 평타 패턴 통일. 이전 누락분 fix.
            CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                new CombatDamageRequest(
                    damage,
                    DamageSourceKind.Projectile,
                    0UL,
                    sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                    applyAttackPower: true,
                    hitDirection: direction,
                    hitPoint: transform.position,
                    weaponId: nameof(MagicalGirlProjectile)),
                stats);
            _damage = damageResult.FinalDamage;
            _wasCritical = damageResult.WasCritical;
            // 회전: 진행 방향으로 sprite 정렬 (오른쪽 = 0도 기준)
            float angleDeg = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(0f, 0f, angleDeg);
        }

        private void SpawnHitVfx()
        {
            if (_hitVfxPrefab == null) return;
            GameObject hitVfx = Instantiate(_hitVfxPrefab, transform.position, Quaternion.identity);
            VFXSpawner.ApplyGameplayEffectSorting(hitVfx);
        }

        private void Update()
        {
            if (_hasHit) return;
            // visual-only clone 은 owner action gate 검사 안 함 (owner local 상태에 의존하지 않음 — broadcast 가 lifetime 결정).
            if (!_visualOnly && IsOwnerActionBlocked())
            {
                Destroy(gameObject);
                return;
            }

            transform.position += (Vector3)(_velocity * Time.deltaTime);
            if (Time.time >= _expiresAt)
            {
                // 만료 시에도 폭발 (공중 폭발) — 적중 외 시각 일관성. catalog hitVfxPrefab=null 이면 no-op.
                SpawnHitVfx();
                Destroy(gameObject);
            }
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (_hasHit) return;
            // visual-only clone 은 데미지 권위 없음 — collider trigger 는 발화하지만 owner broadcast 가 정확한 despawn 담당.
            // 자기 화면에서 적과 시각적으로 충돌해도 데미지/popup 안 적용. 또한 visual-only 는 hit VFX 도 owner broadcast 가 트리거.
            if (_visualOnly) return;

            if (IsOwnerActionBlocked())
            {
                Destroy(gameObject);
                return;
            }

            Health h = other.GetComponentInParent<Health>();
            // CL-143 + 멀티 가드 통합: CanBeAutoTargetedEnemy 내부에서
            // IsAuthoritativePlayer (host 측 AI 변환된 게스트 player 제외) 도 함께 처리.
            if (!CombatTargetable.CanBeAutoTargetedEnemy(h)) return;
            _hasHit = true;
            h.Damage(_damage, gameObject, 0f, 0f, Vector3.zero);
            if (LostMemory.UI.DamagePopupSpawner.Instance != null)
            {
                LostMemory.UI.DamagePopupSpawner.Instance.NotifyMeleeDamage(h, _damage, _wasCritical);
            }
            SpawnHitVfx();  // CL-204 B8: 적중 시 폭발 VFX

            // 멀티 sync (2026-05-28): owner hit → broadcast ID → 모든 client clone despawn.
            if (_despawnBroadcaster != null && _projectileId > 0)
            {
                _despawnBroadcaster.RelayMagicalGirlProjectileHit(_projectileId);
            }
            Destroy(gameObject);
        }

        private bool IsOwnerActionBlocked()
        {
            return KhiPlayerActionGate.IsBlocked(_ownerDownController);
        }
    }
}
