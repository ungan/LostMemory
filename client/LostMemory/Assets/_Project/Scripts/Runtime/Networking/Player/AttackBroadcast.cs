using LostMemory.Combat;
using LostMemory.Data;
using LostMemory.TestKhi;
using Unity.Netcode;
using UnityEngine;

namespace LostMemory.Networking.Player
{
    /// <summary>
    /// 공격 시각 효과 (KhiAttackVisualPresenter / KhiSlashAnimator / KhiWeaponPresenter) 가 의존하는
    /// KhiMeleeComboController 의 AttackStarted / AttackActiveStarted / AttackActiveEnded 이벤트를
    /// owner 측에서 ClientRpc 로 broadcast → non-owner 측이 같은 핸들러를 직접 호출해 시각 효과를 재현.
    ///
    /// 배경 (Bug #19):
    ///   PlayerMovementSync 는 non-owner 측에서 KhiMeleeComboController 를 disable → 위 3개 이벤트
    ///   발화 자체가 막힘. NetworkAnimator 만으로는 Animator state 만 sync 되고 KhiSlashAnimator /
    ///   KhiWeaponPresenter / KhiAttackVisualPresenter 의 sprite/swing/vfx 코루틴 기반 시각 효과는
    ///   비동기됨. 본 컴포넌트가 그 격차를 메움.
    ///
    /// 의존 (클라팀 합의 완료 — private → public 변경):
    ///   - KhiAttackVisualPresenter.HandleAttackStarted(KhiAttackRequest, AttackStepData)
    ///   - KhiSlashAnimator.HandleAttackActiveStarted(KhiAttackRequest, AttackStepData)
    ///   - KhiWeaponPresenter.HandleAttackActiveStarted(KhiAttackRequest, AttackStepData)
    ///   - KhiWeaponPresenter.HandleAttackActiveEnded(KhiAttackRequest, AttackStepData)
    ///
    /// 부착 위치: 플레이어 prefab 의 root (NetworkObject 와 같은 GameObject).
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Lost Memory/Networking/Attack Broadcast")]
    public sealed class AttackBroadcast : NetworkBehaviour
    {
        [Header("Refs (OnNetworkSpawn 자동 해석)")]
        [SerializeField] private KhiMeleeComboController comboController;
        [SerializeField] private KhiAttackVisualPresenter attackVisualPresenter;
        [SerializeField] private KhiSlashAnimator slashAnimator;
        [SerializeField] private KhiWeaponPresenter weaponPresenter;
        [SerializeField] private WeaponData weaponData;
        [Tooltip("스태프 투사체 (Bolt/Fireball/Meteor) 시각 broadcast. non-owner 측 visual-only 재현용. 자식 계층에서 자동 검색.")]
        [SerializeField] private KhiStaffController staffController;
        [Tooltip("활 화살 (Single/Rapid) 시각 broadcast. non-owner 측 visual-only 재현용. 자식 계층에서 자동 검색.")]
        [SerializeField] private KhiBowController bowController;

        [Header("Debug (Phase D)")]
        [Tooltip("guest 공격 sync 진단용. 안정화 후 false 권장.")]
        [SerializeField] private bool verboseLog = false;

        private enum AttackEvent : byte
        {
            Started = 0,
            ActiveStarted = 1,
            ActiveEnded = 2,
        }

        /// <summary>
        /// 스태프 투사체 종류 식별자. RelayStaffProjectileSpawn 경로의 ClientRpc 인자.
        /// KhiStaffController.SpawnVisualOnlyProjectile 가 같은 enum 으로 분기.
        /// </summary>
        public enum StaffProjectileType : byte
        {
            Bolt = 0,
            Fireball = 1,
            Meteor = 2,
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            ResolveRefs();

            if (IsOwner && comboController != null)
            {
                comboController.AttackStarted += OnAttackStartedOwner;
                comboController.AttackActiveStarted += OnAttackActiveStartedOwner;
                comboController.AttackActiveEnded += OnAttackActiveEndedOwner;
            }
        }

        public override void OnNetworkDespawn()
        {
            if (IsOwner && comboController != null)
            {
                comboController.AttackStarted -= OnAttackStartedOwner;
                comboController.AttackActiveStarted -= OnAttackActiveStartedOwner;
                comboController.AttackActiveEnded -= OnAttackActiveEndedOwner;
            }
            base.OnNetworkDespawn();
        }

        private void ResolveRefs()
        {
            if (comboController == null) comboController = GetComponent<KhiMeleeComboController>();
            if (attackVisualPresenter == null) attackVisualPresenter = GetComponent<KhiAttackVisualPresenter>();
            if (slashAnimator == null) slashAnimator = GetComponent<KhiSlashAnimator>();
            if (weaponPresenter == null) weaponPresenter = GetComponentInChildren<KhiWeaponPresenter>(true);
            if (weaponData == null && comboController != null) weaponData = comboController.WeaponData;
            if (staffController == null) staffController = GetComponentInChildren<KhiStaffController>(true);
            if (bowController == null) bowController = GetComponentInChildren<KhiBowController>(true);
        }

        private void OnAttackStartedOwner(KhiAttackRequest req, AttackStepData step)
        {
            if (verboseLog) Debug.Log($"[AttackBroadcast] Owner.OnAttackStarted combo={req.ComboStep} seq={req.SequenceId} IsServer={IsServer} IsHost={IsHost} OwnerClientId={OwnerClientId} → relay ServerRpc", this);
            RelayAttackEventServerRpc(AttackEvent.Started, req.ComboStep, req.AimDirection, req.AimAngleDegrees, req.Origin, req.SequenceId);
        }

        private void OnAttackActiveStartedOwner(KhiAttackRequest req, AttackStepData step)
        {
            RelayAttackEventServerRpc(AttackEvent.ActiveStarted, req.ComboStep, req.AimDirection, req.AimAngleDegrees, req.Origin, req.SequenceId);
        }

        private void OnAttackActiveEndedOwner(KhiAttackRequest req, AttackStepData step)
        {
            RelayAttackEventServerRpc(AttackEvent.ActiveEnded, req.ComboStep, req.AimDirection, req.AimAngleDegrees, req.Origin, req.SequenceId);
        }

        /// <summary>
        /// owner → server 중계. ClientRpc 는 server 만 발화 가능하므로 게스트 owner 가 직접 broadcast 못 함 → 본 ServerRpc 거쳐 server 가 broadcast.
        /// </summary>
        [ServerRpc]
        private void RelayAttackEventServerRpc(
            AttackEvent eventType,
            int comboStep,
            Vector2 aimDirection,
            float aimAngleDegrees,
            Vector3 origin,
            int sequenceId)
        {
            BroadcastAttackEventClientRpc(eventType, comboStep, aimDirection, aimAngleDegrees, origin, sequenceId);
        }

        [ClientRpc]
        private void BroadcastAttackEventClientRpc(
            AttackEvent eventType,
            int comboStep,
            Vector2 aimDirection,
            float aimAngleDegrees,
            Vector3 origin,
            int sequenceId)
        {
            // owner 는 이미 로컬 이벤트 핸들러를 통해 시각 효과 처리. 중복 방지.
            if (verboseLog) Debug.Log($"[AttackBroadcast] ClientRpc received event={eventType} combo={comboStep} seq={sequenceId} IsOwner={IsOwner} (skip if owner)", this);
            if (IsOwner) return;

            // [DiagAttackVfx] 비-owner 측에서 단검 VFX/sprite 가 안 보이는 케이스 진단.
            // wiring 누락인지 (presenter null) GameObject 비활성인지 (active=false) component disable 인지 (enabled=false) 한 줄로 가시화.
            if (verboseLog)
            {
                Debug.Log(
                    $"[DiagAttackVfx] seq={sequenceId} event={eventType} presenters: " +
                    $"attackVfx={(attackVisualPresenter != null ? $"OK(active={attackVisualPresenter.gameObject.activeInHierarchy},enabled={attackVisualPresenter.enabled})" : "NULL")} " +
                    $"slash={(slashAnimator != null ? $"OK(active={slashAnimator.gameObject.activeInHierarchy},enabled={slashAnimator.enabled})" : "NULL")} " +
                    $"weapon={(weaponPresenter != null ? $"OK(active={weaponPresenter.gameObject.activeInHierarchy},enabled={weaponPresenter.enabled})" : "NULL")}",
                    this);
            }

            AttackStepData step = ResolveStep(comboStep);
            if (step == null) return;

            KhiAttackRequest req = new KhiAttackRequest
            {
                SequenceId = sequenceId,
                ComboStep = comboStep,
                AimDirection = aimDirection,
                AimAngleDegrees = aimAngleDegrees,
                Origin = origin,
                StartedAt = Time.time,
                Attacker = gameObject,
            };

            switch (eventType)
            {
                case AttackEvent.Started:
                    if (attackVisualPresenter != null)
                    {
                        attackVisualPresenter.HandleAttackStarted(req, step);
                    }
                    break;
                case AttackEvent.ActiveStarted:
                    if (slashAnimator != null)
                    {
                        slashAnimator.HandleAttackActiveStarted(req, step);
                    }
                    if (weaponPresenter != null)
                    {
                        weaponPresenter.HandleAttackActiveStarted(req, step);
                    }
                    break;
                case AttackEvent.ActiveEnded:
                    if (weaponPresenter != null)
                    {
                        weaponPresenter.HandleAttackActiveEnded(req, step);
                    }
                    break;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Staff projectile relay — Bug #28
        // ─────────────────────────────────────────────────────────────────────────
        //
        // owner KhiStaffController.TryCastBolt/Fireball/Meteor 가 local Instantiate 직후
        // 본 메서드를 호출 → ServerRpc → ClientRpc → non-owner 측이 visual-only clone Instantiate.
        // 단검 Slash 와 같은 host-relay 패턴. KhiStaffController 의 SpawnVisualOnlyProjectile
        // 가 prefab 분기 + SetVisualOnly(true) 호출 담당.

        /// <summary>
        /// owner 측에서 staff 투사체 spawn 직후 호출. ServerRpc 거쳐 모든 non-owner 에 시각 broadcast.
        /// damage 권위는 owner 의 로컬 prefab 만 — non-owner clone 은 SetVisualOnly 로 visual 만 재현.
        /// projectileId: KhiArrowProjectile.AllocateProjectileId() 로 owner 발급. owner kill broadcast 시 매칭 key.
        ///   Meteor case 는 KhiMeteor 자체 코루틴 destroy 라 ID 무관 → 0 전달.
        /// </summary>
        public void RelayStaffProjectileSpawn(StaffProjectileType type, Vector3 spawnPos, Vector2 direction, int projectileId)
        {
            if (!IsOwner) return;
            RelayStaffProjectileSpawnServerRpc(type, spawnPos, direction, projectileId);
        }

        [ServerRpc]
        private void RelayStaffProjectileSpawnServerRpc(StaffProjectileType type, Vector3 spawnPos, Vector2 direction, int projectileId)
        {
            BroadcastStaffProjectileSpawnClientRpc(type, spawnPos, direction, projectileId);
        }

        [ClientRpc]
        private void BroadcastStaffProjectileSpawnClientRpc(StaffProjectileType type, Vector3 spawnPos, Vector2 direction, int projectileId)
        {
            if (verboseLog) Debug.Log($"[AttackBroadcast] StaffProjectile ClientRpc type={type} pos={spawnPos} dir={direction} id={projectileId} IsOwner={IsOwner} (skip if owner)", this);
            // owner 는 이미 로컬 Instantiate 함 — 중복 spawn 방지.
            if (IsOwner) return;

            if (staffController == null)
            {
                ResolveRefs();
                if (staffController == null)
                {
                    if (verboseLog) Debug.LogWarning("[AttackBroadcast] StaffProjectile ClientRpc — staffController null 로 spawn skip.", this);
                    return;
                }
            }
            staffController.SpawnVisualOnlyProjectile(type, spawnPos, direction, projectileId);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Projectile despawn relay — Bug #33 후속
        // ─────────────────────────────────────────────────────────────────────────
        //
        // owner 의 KhiArrowProjectile.PlayImpactAndDestroy 가 호출되는 시점에 ID broadcast.
        // 모든 non-owner 가 KhiArrowProjectile.DespawnVisualOnlyCloneById 호출 → 매칭 clone 즉시 destroy.
        // owner kill → enemy NGO destroy → 게스트 clone 의 trigger 발화 못 함 케이스에서 시각 sync 보장.

        /// <summary>owner 측 KhiArrowProjectile.PlayImpactAndDestroy 가 호출. 모든 non-owner 의 매칭 clone 도 동시 destroy.</summary>
        public void RelayProjectileDespawn(int projectileId)
        {
            if (!IsOwner) return;
            if (projectileId <= 0) return;
            RelayProjectileDespawnServerRpc(projectileId);
        }

        [ServerRpc]
        private void RelayProjectileDespawnServerRpc(int projectileId)
        {
            BroadcastProjectileDespawnClientRpc(projectileId);
        }

        [ClientRpc]
        private void BroadcastProjectileDespawnClientRpc(int projectileId)
        {
            if (verboseLog) Debug.Log($"[AttackBroadcast] ProjectileDespawn ClientRpc id={projectileId} IsOwner={IsOwner} (skip if owner)", this);
            // owner 는 이미 로컬에서 PlayImpactAndDestroy 실행 중 — 자기 자신 broadcast 다시 받아 destroy 시도 차단.
            if (IsOwner) return;
            KhiArrowProjectile.DespawnVisualOnlyCloneById(projectileId);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Projectile damage relay — Bug #35
        // ─────────────────────────────────────────────────────────────────────────
        //
        // 멀티 환경에서 enemy.Health 는 *server (host) 권위*. MonsterHealthSync.OnNetworkSpawn 이 client 측 enemy.Health 를
        // DamageDisabled() (Invulnerable=true) 로 영구 set → 게스트의 client-side health.Damage 호출은 무효.
        // 게스트가 owner 인 진짜 projectile 이 적과 충돌하면 본 ServerRpc 로 server (host) 에 데미지 위임 → host 가 enemy.Health.Damage 호출.
        // host 의 owner projectile 은 이미 server-side direct Damage 호출 (KhiArrowProjectile 측에서 isServer 분기) → 본 relay 불필요.

        /// <summary>
        /// 게스트의 owner projectile 이 적과 충돌 시 호출. server 가 enemy.Health.Damage 권위 호출.
        /// host 의 owner projectile 은 직접 호출하므로 본 메서드 호출 안 함.
        /// </summary>
        public void RelayProjectileDamage(
            ulong enemyNetObjId,
            float damage,
            float flickerDuration,
            float invincibilityDuration,
            Vector2 direction,
            bool wasCritical = false,
            int projectileId = 0)
        {
            if (!IsOwner) return;
            if (damage <= 0f) return;
            RelayProjectileDamageServerRpc(
                enemyNetObjId,
                damage,
                flickerDuration,
                invincibilityDuration,
                direction,
                wasCritical,
                projectileId);
        }

        [ServerRpc]
        private void RelayProjectileDamageServerRpc(
            ulong enemyNetObjId,
            float damage,
            float flickerDuration,
            float invincibilityDuration,
            Vector2 direction,
            bool wasCritical,
            int projectileId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.SpawnManager.SpawnedObjects.TryGetValue(enemyNetObjId, out var netObj))
            {
                if (verboseLog) Debug.LogWarning($"[AttackBroadcast] DamageRequest: enemyNetObjId={enemyNetObjId} not found on server.", this);
                return;
            }
            var health = netObj.GetComponentInChildren<MoreMountains.TopDownEngine.Health>();
            if (health == null)
            {
                if (verboseLog) Debug.LogWarning($"[AttackBroadcast] DamageRequest: Health 컴포넌트 누락 — enemyNetObjId={enemyNetObjId} obj={netObj.name}", this);
                return;
            }
            // PvP 미상정 — 서버 측 검증. 게스트가 enemyNetObjId 자리에 player NetworkObjectId 를 보내도 차단.
            if (CombatTargetable.IsFriendlyPlayer(health))
            {
                if (verboseLog) Debug.LogWarning($"[AttackBroadcast] FriendlyFire blocked at DamageRequest — target={netObj.name} requester={gameObject.name}", this);
                return;
            }

            // server-side direct Damage — host 측 enemy.Health 는 DamageDisabled 호출 안 되어 정상 Invulnerable=false.
            // attacker = 본 AttackBroadcast 의 player root GameObject (server 측의 게스트 player NetworkObject instance).
            health.Damage(damage, gameObject, flickerDuration, invincibilityDuration, new Vector3(direction.x, direction.y, 0f));
            ulong sourceId = projectileId > 0 ? (ulong)projectileId : 0UL;
            var request = new CombatDamageRequest(
                damage,
                DamageSourceKind.Projectile,
                enemyNetObjId,
                attackerNetworkObjectId: NetworkObjectId,
                sourceId: sourceId,
                criticalPolicy: CriticalPolicy.Never,
                applyAttackPower: false,
                hitDirection: direction,
                hitPoint: health.transform.position,
                weaponId: "Projectile");
            var result = new CombatDamageResult(request, damage, wasCritical, health.CurrentHealth <= 0f);
            CombatDamageEventDispatcher.RaiseDamageApplied(new CombatDamageEvent(result, health, gameObject));
            if (verboseLog) Debug.Log($"[AttackBroadcast] DamageRequest applied: enemyNetObjId={enemyNetObjId} dmg={damage} attacker={gameObject.name}", this);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // Bow arrow relay — Bug #30
        // ─────────────────────────────────────────────────────────────────────────
        //
        // owner KhiBowController.SpawnArrow 가 local Instantiate 직후 본 메서드 호출 →
        // ServerRpc → ClientRpc → non-owner 측이 visual-only clone Instantiate + ArrowFired 이벤트
        // 동시 발화 (KhiBowAnimator / KhiBowPresenter 의 Draw/Recoil 시각 재현).
        // 스태프 패턴과 거의 동일. 차이: prefab 1종 + isRapid 플래그.

        /// <summary>
        /// owner 측에서 화살 spawn 직후 호출. ServerRpc 거쳐 모든 non-owner 에 시각 broadcast.
        /// damage 권위는 owner 측 로컬 prefab 만 — non-owner clone 은 SetVisualOnly 로 visual 만 재현.
        /// projectileId: KhiArrowProjectile.AllocateProjectileId() 로 owner 발급. owner kill broadcast 시 매칭 key.
        /// </summary>
        public void RelayArrowProjectileSpawn(Vector3 spawnPos, Vector2 direction, bool isRapid, int projectileId)
        {
            if (!IsOwner) return;
            RelayArrowProjectileSpawnServerRpc(spawnPos, direction, isRapid, projectileId);
        }

        [ServerRpc]
        private void RelayArrowProjectileSpawnServerRpc(Vector3 spawnPos, Vector2 direction, bool isRapid, int projectileId)
        {
            BroadcastArrowProjectileSpawnClientRpc(spawnPos, direction, isRapid, projectileId);
        }

        [ClientRpc]
        private void BroadcastArrowProjectileSpawnClientRpc(Vector3 spawnPos, Vector2 direction, bool isRapid, int projectileId)
        {
            if (verboseLog) Debug.Log($"[AttackBroadcast] Arrow ClientRpc pos={spawnPos} dir={direction} rapid={isRapid} id={projectileId} IsOwner={IsOwner} (skip if owner)", this);
            if (IsOwner) return;

            if (bowController == null)
            {
                ResolveRefs();
                if (bowController == null)
                {
                    if (verboseLog) Debug.LogWarning("[AttackBroadcast] Arrow ClientRpc — bowController null 로 spawn skip.", this);
                    return;
                }
            }
            bowController.SpawnVisualOnlyArrow(spawnPos, direction, isRapid, projectileId);
        }

        private AttackStepData ResolveStep(int comboStep)
        {
            // [Fix Bug #29 단검 VFX 게스트 mismatch]
            // OnNetworkSpawn 시점에 weaponData 를 1회 캡처하면 owner 가 무기 모드 전환해도 stale.
            // 게스트 측 ClientRpc 가 도착할 때 stale 일반검 step 을 반환 → 일반검 frame 으로 시각 재생.
            // WeaponModeController._syncedMode 가 게스트 측 comboController.WeaponData 까지 sync 한다고 가정 →
            // ResolveStep 진입 시 매번 comboController.WeaponData 로 refresh 해 stale 위험 제거.
            if (comboController == null)
            {
                ResolveRefs();
            }
            if (comboController != null)
            {
                WeaponData current = comboController.WeaponData;
                if (current != null) weaponData = current;
            }

            // Phase E 호환: comboController null fallback 시에도 weaponData 가 null 이면 lazy refresh.
            if (weaponData == null)
            {
                ResolveRefs();
                if (weaponData == null && comboController != null)
                {
                    weaponData = comboController.WeaponData;
                }
            }
            if (weaponData == null || weaponData.Steps == null || weaponData.Steps.Length == 0)
            {
                if (verboseLog) Debug.LogWarning($"[AttackBroadcast] ResolveStep null — comboStep={comboStep} weaponDataNull={(weaponData == null)} comboCtrlNull={(comboController == null)}", this);
                return null;
            }
            int index = Mathf.Clamp(comboStep - 1, 0, weaponData.Steps.Length - 1);
            return weaponData.Steps[index];
        }
    }
}
