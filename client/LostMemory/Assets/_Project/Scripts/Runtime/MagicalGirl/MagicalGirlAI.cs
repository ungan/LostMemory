using System.Collections;
using LostMemory.Combat;
using LostMemory.TestKhi;
using LostMemory.VFX;
using MoreMountains.TopDownEngine;
using UnityEngine;

namespace LostMemory.MagicalGirl
{
    /// <summary>
    /// CL-204 (CL-144 후속): 개별 미소녀 자동 공격 컴포넌트.
    ///
    /// MagicalGirlSpawner 가 GameObject 생성 후 AddComponent → Init(stat, combat, catalog) → SetVisual(v) 호출.
    /// SetVisual 이 catalog 에서 entry 조회하여 sprite + 공격 파라미터 결정.
    /// Update 에서 attackInterval 마다 가장 가까운 적(Character.AI) 검색 → catalog.kind 분기:
    ///   Projectile → 발사체 prefab Instantiate + Init
    ///   AOEAtTarget / AOEStationary → AOE prefab Instantiate + Init
    /// catalog 미설정/entry 없음 시 fallback = CL-144 즉시 데미지 (placeholder).
    ///
    /// 무적: Health 컴포넌트 없음. 적 공격에 영향 X.
    /// 충돌: Collider 없음. 적과 물리 충돌 X.
    ///
    /// 5세트 강화 (CL-204): SetSetBonusActive(true) → damage ×1.5 + attackInterval ×0.67.
    /// Type=27 (Enhanced) RelicData 매칭 시 SetEnhanced(true) → 추가 ×1.5 / ×0.67.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MagicalGirlAI : MonoBehaviour
    {
        // CL-204: catalog 미설정 시 fallback 값 (placeholder)
        private const float FallbackDamageRatio = 0.30f;
        private const float FallbackAttackInterval = 1.5f;
        private const float FallbackSpriteSize = 0.4f;
        private const int FallbackSortingOrder = 100;

        // 5세트/Enhanced multiplier 상수
        private const float SetBonusDamageMul = 1.5f;
        private const float SetBonusIntervalMul = 0.67f;
        private const float EnhancedDamageMul = 1.5f;
        private const float EnhancedIntervalMul = 0.67f;

        private SpriteRenderer _sr;
        private float _nextAttackAt;
        private PlayerStatModifierContainer _playerStat;
        private KhiMeleeComboController _playerCombat;
        private KhiDownController _ownerDownController;
        private MagicalGirlAttackCatalog _catalog;
        private MagicalGirlVisual _visual = MagicalGirlVisual.Default;
        private bool _setBonusActive;
        private bool _enhanced;
        // 멀티 sync (2026-05-28) — owner 측 본체만 set. Init 시 spawner 가 전달. visual-only clone 은 AI 자체가 없음.
        private MagicalGirlBroadcast _broadcast;

        // 검색 임시 버퍼 (heap alloc 방지)
        private static readonly Collider2D[] _searchBuf = new Collider2D[16];

        private void Awake()
        {
            // 절차적 SpriteRenderer — 이후 SetVisual 가 catalog sprite 로 교체.
            // catalog 미설정 fallback 시 흰색 placeholder 가 보임.
            _sr = gameObject.AddComponent<SpriteRenderer>();
            _sr.sprite = Sprite.Create(
                Texture2D.whiteTexture,
                new Rect(0f, 0f, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit: Texture2D.whiteTexture.width);
            _sr.color = MagicalGirlVisualPalette.Get(MagicalGirlVisual.Default);
            _sr.sortingOrder = FallbackSortingOrder;
            transform.localScale = new Vector3(FallbackSpriteSize, FallbackSpriteSize, 1f);
        }

        public void Init(PlayerStatModifierContainer stat, KhiMeleeComboController combat, MagicalGirlAttackCatalog catalog)
        {
            Init(stat, combat, catalog, null, null);
        }

        public void Init(
            PlayerStatModifierContainer stat,
            KhiMeleeComboController combat,
            MagicalGirlAttackCatalog catalog,
            KhiDownController ownerDownController)
        {
            Init(stat, combat, catalog, ownerDownController, null);
        }

        public void Init(
            PlayerStatModifierContainer stat,
            KhiMeleeComboController combat,
            MagicalGirlAttackCatalog catalog,
            KhiDownController ownerDownController,
            MagicalGirlBroadcast broadcast)
        {
            _playerStat = stat;
            _playerCombat = combat;
            _catalog = catalog;
            _ownerDownController = ownerDownController;
            _broadcast = broadcast;
        }

        public MagicalGirlVisual Visual => _visual;

        public void SetVisual(MagicalGirlVisual visual)
        {
            _visual = visual;
            ApplyVisualAppearance();
        }

        /// <summary>5세트 도달 시 spawner 가 모든 미소녀에 broadcast.</summary>
        public void SetSetBonusActive(bool active)
        {
            _setBonusActive = active;
        }

        /// <summary>Type=27 RelicData 보유 시 spawner 가 해당 visual 미소녀에만 적용.</summary>
        public void SetEnhanced(bool enhanced)
        {
            _enhanced = enhanced;
        }

        private void ApplyVisualAppearance()
        {
            if (_sr == null) return;
            // catalog 에서 entry 찾으면 sprite 교체, 없으면 색상 tint 만 (placeholder).
            // 누락 진단을 위해 각 fallback 단계에 워닝을 남긴다 (전기 미소녀 등 미해결 이슈 추적용).
            if (_catalog == null)
            {
                Debug.LogWarning($"[MagicalGirlAI] catalog 미할당 — {_visual} placeholder 표시", this);
                _sr.color = MagicalGirlVisualPalette.Get(_visual);
                return;
            }
            if (!_catalog.TryGet(_visual, out var entry))
            {
                Debug.LogWarning($"[MagicalGirlAI] catalog 에 {_visual} entry 없음 — placeholder 표시", this);
                _sr.color = MagicalGirlVisualPalette.Get(_visual);
                return;
            }
            if (entry.sprite == null)
            {
                Debug.LogWarning($"[MagicalGirlAI] {_visual} entry.sprite null — placeholder 표시. Catalog asset Inspector 확인 필요.", this);
                _sr.color = MagicalGirlVisualPalette.Get(_visual);
                return;
            }
            _sr.sprite = entry.sprite;
            _sr.color = Color.white;  // sprite 자체 색상 사용
        }

        private void Update()
        {
            if (IsOwnerActionBlocked())
            {
                _nextAttackAt = Mathf.Max(_nextAttackAt, Time.time + ResolveAttackInterval());
                return;
            }

            if (Time.time < _nextAttackAt) return;
            (Health target, Vector2 toTargetDir) = FindClosestEnemy();
            if (target == null) return;
            Attack(target, toTargetDir);

            float interval = ResolveAttackInterval();
            _nextAttackAt = Time.time + interval;
        }

        private (Health, Vector2) FindClosestEnemy()
        {
            float baseRange = ResolveAttackRange();
            float rangeMul = _playerStat != null ? _playerStat.GetTotalMultiplier(StatId.Range) : 1f;
            float effectiveRange = baseRange * rangeMul;
            int hits = Physics2D.OverlapCircleNonAlloc(transform.position, effectiveRange, _searchBuf);
            Health closest = null;
            Vector2 closestDir = Vector2.right;
            float minDistSq = float.MaxValue;
            for (int i = 0; i < hits; i++)
            {
                Collider2D col = _searchBuf[i];
                if (col == null) continue;
                Health h = col.GetComponentInParent<Health>();
                // CL-143 + 멀티 가드 통합: CanBeAutoTargetedEnemy 내부에서
                // IsAuthoritativePlayer (host 측 AI 변환된 게스트 player 제외) 도 함께 처리.
                if (!CombatTargetable.CanBeAutoTargetedEnemy(h)) continue;

                Vector2 to = h.transform.position - transform.position;
                float dSq = to.sqrMagnitude;
                if (dSq < minDistSq)
                {
                    minDistSq = dSq;
                    closest = h;
                    closestDir = to;
                }
            }
            return (closest, closestDir);
        }

        private void Attack(Health target, Vector2 toTargetDir)
        {
            if (IsOwnerActionBlocked()) return;
            if (!CombatTargetable.CanBeAutoTargetedEnemy(target)) return;

            float damage = ComputeDamage();
            if (damage <= 0f) return;

            // catalog driven dispatch
            if (_catalog != null && _catalog.TryGet(_visual, out var entry))
            {
                switch (entry.kind)
                {
                    case MagicalGirlAttackCatalog.AttackKind.Projectile:
                        SpawnProjectile(entry, damage, toTargetDir);
                        break;
                    case MagicalGirlAttackCatalog.AttackKind.AOEAtTarget:
                    case MagicalGirlAttackCatalog.AttackKind.AOEStationary:
                        SpawnAOE(entry, damage, toTargetDir);
                        break;
                }
            }
            else
            {
                // fallback (placeholder): 기존 CL-144 즉시 데미지
                // PvP 미상정 — 다른 player 친아군 skip (CanBeAutoTargetedEnemy 는 위에서 통과했지만 안전벨트).
                if (CombatTargetable.IsFriendlyPlayer(target)) return;
                CombatDamageResult damageResult = CombatDamageResolver.Resolve(
                    new CombatDamageRequest(
                        damage,
                        DamageSourceKind.Summon,
                        0UL,
                        sourceId: (ulong)Mathf.Abs(GetInstanceID()),
                        applyAttackPower: false,
                        hitPoint: target.transform.position,
                        weaponId: nameof(MagicalGirlAI)),
                    _playerStat);
                target.Damage(damageResult.FinalDamage, gameObject, 0f, 0f, Vector3.zero);
            }

            StartCoroutine(FlashCoroutine());
        }

        private void SpawnProjectile(MagicalGirlAttackCatalog.Entry entry, float damage, Vector2 dir)
        {
            if (entry.vfxPrefab == null) return;
            Vector3 spawnPos = transform.position;
            GameObject go = Instantiate(entry.vfxPrefab, spawnPos, Quaternion.identity);
            VFXSpawner.ApplyGameplayEffectSorting(go);
            var proj = go.GetComponent<MagicalGirlProjectile>();
            if (proj == null) proj = go.AddComponent<MagicalGirlProjectile>();
            proj.Init(dir, damage, entry.projectileSpeed, entry.projectileLifetime, entry.hitVfxPrefab, _ownerDownController);

            // 멀티 sync (2026-05-28): owner 측 ID 발급 + broadcast → non-owner clone Instantiate.
            // broadcast null = 솔로 (NM 비활성) 또는 NGO 미연결 → broadcast 호출 no-op, owner local 만.
            if (_broadcast != null)
            {
                int projectileId = MagicalGirlProjectile.AllocateProjectileId();
                proj.SetProjectileId(projectileId);
                proj.SetDespawnBroadcaster(_broadcast);
                _broadcast.RelayMagicalGirlProjectileSpawn(
                    (int)_visual, spawnPos, dir.normalized,
                    entry.projectileSpeed, entry.projectileLifetime, projectileId);
            }
        }

        private void SpawnAOE(MagicalGirlAttackCatalog.Entry entry, float damage, Vector2 dir)
        {
            if (entry.vfxPrefab == null) return;
            // AOEStationary: 미소녀 전방 일정 거리 spawn. AOEAtTarget: 미소녀 위치 spawn 후 Init 에서 적 위치로 1회 snap.
            Vector3 spawnPos = transform.position;
            if (entry.kind == MagicalGirlAttackCatalog.AttackKind.AOEStationary)
            {
                Vector2 forward = dir.sqrMagnitude > 0.0001f ? dir.normalized : Vector2.right;
                spawnPos += (Vector3)(forward * Mathf.Max(2f, entry.aoeRadius * 1.5f));
            }
            GameObject go = Instantiate(entry.vfxPrefab, spawnPos, Quaternion.identity);
            VFXSpawner.ApplyMagicalGirlGroundEffectSorting(go);
            var aoe = go.GetComponent<MagicalGirlAOE>();
            if (aoe == null) aoe = go.AddComponent<MagicalGirlAOE>();
            // tick 데미지 = entry.damageRatio 기반이지만, catalog 에서 이미 1회 전달된 damage 를 tick 당 데미지로 사용.
            // duration 동안 tickInterval 마다 동일 damage 적용 (대략 6~10회 tick).
            aoe.Init(
                entry.kind,
                damage,
                entry.aoeRadius,
                entry.aoeDuration,
                entry.aoeTickInterval,
                entry.pullSpeed,
                entry.slowMagnitude,
                entry.slowDuration,
                _ownerDownController);
        }

        private bool IsOwnerActionBlocked()
        {
            return KhiPlayerActionGate.IsBlocked(_ownerDownController);
        }

        private float ComputeDamage()
        {
            float playerAtk = _playerCombat != null && _playerCombat.WeaponData != null
                ? _playerCombat.WeaponData.BaseDamage
                : 0f;
            if (_playerStat != null)
                playerAtk *= _playerStat.GetTotalMultiplier(StatId.AttackPower);

            float ratio = ResolveDamageRatio();
            float damage = playerAtk * ratio;
            if (_setBonusActive) damage *= SetBonusDamageMul;
            if (_enhanced) damage *= EnhancedDamageMul;
            return damage;
        }

        private float ResolveDamageRatio()
        {
            if (_catalog != null && _catalog.TryGet(_visual, out var entry) && entry.damageRatio > 0f)
                return entry.damageRatio;
            return FallbackDamageRatio;
        }

        private float ResolveAttackInterval()
        {
            float baseInterval;
            if (_catalog != null && _catalog.TryGet(_visual, out var entry) && entry.attackInterval > 0f)
                baseInterval = entry.attackInterval;
            else
                baseInterval = FallbackAttackInterval;
            if (_setBonusActive) baseInterval *= SetBonusIntervalMul;
            if (_enhanced) baseInterval *= EnhancedIntervalMul;
            return baseInterval;
        }

        private float ResolveAttackRange()
        {
            // CL-204: catalog 의 projectileLifetime × projectileSpeed 또는 aoeRadius 로부터 검색 반경 추정.
            // 단순화: 5유닛 (CL-144 default) — 향후 catalog 에 별도 searchRange 필드 추가 가능.
            return 5f;
        }

        private IEnumerator FlashCoroutine()
        {
            if (_sr == null) yield break;
            Color original = _sr.color;
            _sr.color = Color.white;
            yield return new WaitForSeconds(0.1f);
            if (_sr != null) _sr.color = original;
        }
    }
}
