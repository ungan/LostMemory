# LostMemory Combat Damage Source Map

작성일: 2026-06-23
기준 문서:

- `combat-refactor-diagnosis.md`
- `combat-refactor-plan.md`

## 목적

이 문서는 구현 전 지도다. 현재 플레이어/무기/보조 효과 데미지가 어디서 계산되고, 어디서 적용되고, 어떤 네트워크 경로를 타는지 정리한다.

목표는 다음 구현의 안전한 순서를 정하는 것이다.

- `CombatDamageResolver`로 옮길 대상 식별
- crit 누락 지점 식별
- OnHit 정책과 현재 이벤트 구조 차이 식별
- host/server authoritative damage로 가기 위한 gap 식별

## 범위

### 1차 리팩토링 중심 범위

- `Runtime/TestKhi`
- `Runtime/Combat`
- `Runtime/Networking/Player`
- `Runtime/MagicalGirl`
- `Runtime/Tarot` 일부 참고

### 1차 제외 또는 참고 범위

적/보스가 플레이어에게 주는 데미지는 이번 무기/플레이어 전투 리팩토링의 1차 대상이 아니다.

예:

- `Runtime/Enemies/*EnemyController.cs`
- `Runtime/Enemies/Boss/*`
- `Runtime/Combat/Telegraph/*`

이들은 나중에 "enemy damage authority" 정리 단계에서 별도로 다룬다.

## 현재 Damage Source 표

| Source | Current File | Current Function / Path | Current Crit | Current OnHit | Current Damage Apply | Current Network Path | Popup | Target Policy | Gap |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Melee combo | `Runtime/TestKhi/KhiMeleeComboController.cs` | `RunAttack` -> `KhiMeleeHitbox.Sample` | O, active loop에서 `CriticalRoller.Roll` | O, `TargetHit` 이벤트 | `KhiMeleeHitbox`에서 `RelayDamage` 또는 `Health.Damage` | `PlayerDamageRelay` | `OnHitEffectRegistry.NotifyMeleeDamage` | Crit O, OnHit O | resolver 편입 필요, damage result 이벤트 없음 |
| Melee hitbox apply | `Runtime/TestKhi/KhiMeleeHitbox.cs` | `Sample` | 호출자에서 이미 계산됨 | 호출자 이벤트에 의존 | `PlayerDamageRelay.RelayDamage` 또는 `Health.Damage` | `PlayerDamageRelay` | 직접 없음 | 공통 apply adapter 필요 | damage apply 책임 분리 필요 |
| Bow arrow spawn | `Runtime/TestKhi/KhiBowController.cs` | `SpawnArrow` | O, 발사 시점 `CriticalRoller.Roll` | X | projectile에 final damage 전달 | `AttackBroadcast.RelayArrowProjectileSpawn` visual-only | projectile hit 시 popup | Crit O, OnHit O | OnHit 누락, crit host/server 확정 아님 |
| Bow arrow hit | `Runtime/TestKhi/KhiArrowProjectile.cs` | trigger hit path | 발사 시점 결과 사용 | X | host/solo direct 또는 `RelayProjectileDamage` | `AttackBroadcast.RelayProjectileDamage` | `NotifyArrowDamage` | Crit O, OnHit O | damage result broadcast 구조 없음, OnHit 누락 |
| Staff bolt | `Runtime/TestKhi/KhiStaffController.cs` | `TryCastBolt` -> `KhiArrowProjectile` | O, 발사 시점 | X | projectile hit에서 적용 | `RelayStaffProjectileSpawn`, `RelayProjectileDamage` | projectile hit 시 popup | Crit O, OnHit O | bow와 동일 |
| Staff fireball | `Runtime/TestKhi/KhiStaffController.cs` | `TryCastFireball` -> `KhiArrowProjectile` | O, 발사 시점 | X | projectile hit에서 적용 | `RelayStaffProjectileSpawn`, `RelayProjectileDamage` | projectile hit 시 popup | Crit O, OnHit O | bow와 동일 |
| Staff meteor | `Runtime/TestKhi/KhiStaffController.cs`, `KhiMeteor.cs` | `TryCastMeteor` -> `Detonate` -> `ApplyDamage` | O, detonate 시점 1회 | X | `RelayDamage` 또는 `Health.Damage` | `PlayerDamageRelay`, visual-only meteor clone | `NotifyMeleeDamage` | Crit O, OnHit O as one-shot area skill | OnHit 누락, 다중 target result 필요 |
| Dagger teleport arrive | `Runtime/TestKhi/KhiDaggerTeleportController.cs` | `ApplyDamageAtArrive` -> `KhiMeleeHitbox.Sample` | X | X | `KhiMeleeHitbox.Sample` 경유 | `PlayerDamageRelay` | 직접 없음 | Crit O, OnHit O | crit/OnHit 모두 누락 |
| Parry counter damage | `Runtime/TestKhi/KhiParryDamageOnTouch.cs` | `ApplyReducedDamage` | O | X | `RelayDamage`, `Health.Damage`, 또는 TDE DOT path | `PlayerDamageRelay` | `NotifyMeleeDamage` | Crit O, OnHit 정책 결정 필요 | SubEffect/Counter로 분류 필요 |
| Flamethrower direct tick | `Runtime/TestKhi/KhiFlameZone.cs` | `ScanAndApply` | X | X | `RelayDamage` 또는 `Health.Damage` | `PlayerDamageRelay` | X | Crit O, OnHit TriggerWithCooldown 0.5s | tick request/sourceId 필요, popup/result 없음 |
| Flamethrower burn DOT | `Runtime/TestKhi/KhiFlameZone.cs` -> `Runtime/Enemies/EnemyStatusEffect.cs` | `ApplyBurn` -> `EnemyStatusEffect.Update` | X | X | `EnemyStatusEffect`가 `Health.Damage` 직접 호출 | 네트워크 경로 없음 | X | Crit O every tick, OnHit X | 가장 큰 gap: DOT host/server authority 없음 |
| OnHit chain | `Runtime/Combat/OnHitEffectRegistry.cs` | `ApplyChain` | O | 의도적으로 X | `RelayDamage` 또는 `Health.Damage` | `PlayerDamageRelay` | `NotifySubEffectDamage` | Crit O, OnHit SuppressSubEffectLoop | resolver 편입 필요 |
| OnHit wind blade | `Runtime/Combat/OnHitEffectRegistry.cs` | `ApplyWindBlade` | O | 의도적으로 X | `RelayDamage` 또는 `Health.Damage` | `PlayerDamageRelay` | `NotifySubEffectDamage` | Crit O, OnHit SuppressSubEffectLoop | resolver 편입 필요 |
| OnHit burn | `Runtime/Combat/OnHitEffectRegistry.cs` -> `EnemyStatusEffect.cs` | `ApplyBurn` | X for later DOT ticks | X | `EnemyStatusEffect` direct `Health.Damage` | 네트워크 경로 없음 | X | Crit O every tick, OnHit X | DOT 구조 재작업 필요 |
| MagicalGirl projectile | `Runtime/MagicalGirl/MagicalGirlProjectile.cs` | launch/hit path | O | X | `Health.Damage` 직접 | 별도 broadcast는 visual 중심 | `NotifyMeleeDamage` | Crit O, OnHit Summon policy | host/server authority gap |
| MagicalGirl AOE | `Runtime/MagicalGirl/MagicalGirlAOE.cs` | `DoTick` | O, 매 tick/대상별 | X | `Health.Damage` 직접 | 명확한 damage relay 없음 | `NotifySubEffectDamage` | Crit O, OnHit X if persistent AOE | authority/result gap |
| MagicalGirl AI direct | `Runtime/MagicalGirl/MagicalGirlAI.cs` | `Attack` | X | X | `Health.Damage` 직접 | 명확한 relay 없음 | X | Crit O, OnHit Summon policy | crit/authority/popup gap |
| MagicalGirl fusion laser | `Runtime/MagicalGirl/MagicalGirlFusion.cs` | laser tick loop | X | X | `Health.Damage` 직접 | 명확한 relay 없음 | X | Crit O, Beam/Stream or Summon policy | crit/authority/result gap |
| MagicalGirl fusion AOE | `Runtime/MagicalGirl/MagicalGirlFusion.cs` | AOE loop | X | X | `Health.Damage` 직접 | 명확한 relay 없음 | X | Crit O, Area or Summon policy | crit/authority/result gap |
| Tarot damage card | `Runtime/Tarot/TarotCards.cs` | percent max health damage | X | X | `Health.Damage` 직접 | 명확한 relay 없음 | X | SelfOrEnvironment/SubEffect 결정 필요 | 별도 정책 필요 |
| Test damage trap | `Runtime/TestKhi/TestKhiDamageTrap.cs` | trap damage | X | X | `Health.Damage` 직접 | 없음 | X | 테스트/환경 데미지 | 1차 제외 가능 |
| Down debug kill | `Runtime/TestKhi/KhiDownController.cs` | debug/kill path | X | X | `Health.Damage(99999)` 직접 | 없음 | X | 테스트/디버그 | 제외 가능 |

## 주요 흐름 상세

### 1. Melee combo

현재 흐름:

```text
KhiMeleeComboController.RunAttack
  -> AttackPower / FinisherDamage 계산
  -> CriticalRoller.Roll
  -> KhiMeleeHitbox.Sample
  -> PlayerDamageRelay.RelayDamage or Health.Damage
  -> TargetHit event
  -> OnHitEffectRegistry.HandleHit
  -> DamagePopupSpawner.NotifyMeleeDamage
```

목표 흐름:

```text
KhiMeleeComboController
  -> CombatDamageRequest 생성
  -> CombatDamageResolver.Resolve
  -> CombatDamageApplier.Apply
  -> CombatDamageResult event
  -> OnHit / popup / analytics
```

Gap:

- 계산, 적용, 이벤트가 한 프레임 흐름 안에 섞여 있다.
- `TargetHit`가 근접 전용 이벤트라 projectile/area와 공통화되지 않는다.
- owner/client에서 roll되는 crit을 host/server 확정으로 옮겨야 한다.

### 2. Projectile bow/staff

현재 흐름:

```text
KhiBowController / KhiStaffController
  -> 발사 시점 CriticalRoller.Roll
  -> KhiArrowProjectile.Launch(finalDamage, wasCritical)
  -> projectile trigger hit
  -> host/solo Health.Damage or guest RelayProjectileDamage
  -> DamagePopupSpawner.NotifyArrowDamage
```

Gap:

- crit이 발사자 local side에서 먼저 확정된다.
- OnHit 이벤트가 없다.
- damage result가 별도 구조로 broadcast되지 않는다.
- projectile visual-only clone은 존재하지만 damage result와 연결되어 있지 않다.

### 3. Meteor / one-shot area

현재 흐름:

```text
KhiStaffController.TryCastMeteor
  -> KhiMeteor.Detonate
  -> CriticalRoller.Roll once
  -> ApplyDamage OverlapCircleAll
  -> RelayDamage or Health.Damage per target
  -> NotifyMeleeDamage
```

Gap:

- area skill인데 popup은 melee popup 경로를 재사용한다.
- OnHit가 없다.
- target별 `CombatDamageResult`가 없다.
- 다중 target result broadcast 구조가 필요하다.

### 4. Dagger teleport

현재 흐름:

```text
KhiDaggerTeleportController.ExecuteTeleportCoroutine
  -> ApplyDamageAtArrive
  -> finalDamage = weapon base * step multiplier * attack power
  -> KhiMeleeHitbox.Sample
```

Gap:

- crit이 적용되지 않는다.
- OnHit가 발생하지 않는다.
- `SequenceId = 0` 고정이라 result/event 추적이 어렵다.

### 5. Flamethrower direct tick

현재 흐름:

```text
KhiFlameZone.FixedUpdate
  -> tick interval마다 cone scan
  -> directDamage = damagePerSec * tickInterval
  -> RelayDamage or Health.Damage
  -> optional EnemyStatusEffect.ApplyBurn
```

Gap:

- crit 없음.
- popup 없음.
- OnHit 없음.
- target별 OnHit cooldown 없음.
- `sourceId + tickIndex + targetId` 식별자가 없음.

목표:

- direct tick마다 `CombatDamageRequest` 생성
- host/server crit roll
- `OnHitPolicy.TriggerWithCooldown`
- cooldown key: `attackerId + sourceKind + sourceId + targetId`
- default cooldown: 0.5초

### 6. Burn DOT / EnemyStatusEffect

현재 흐름:

```text
EnemyStatusEffect.ApplyBurn
  -> _burnDamagePerTick 저장
  -> Update에서 1초마다 Health.Damage 직접 호출
```

Gap:

- crit 없음.
- 네트워크 relay 없음.
- host/server authority 없음.
- popup 없음.
- source/attacker/tick identity 부족.

목표:

- DOT source를 host/server authoritative하게 관리
- tick마다 `CombatDamageRequest`
- crit every tick
- OnHit suppress
- result broadcast

주의:

- 이 부분은 Phase 1 계산 공통화보다 큰 변경이다.
- Phase 2 또는 Phase 3에서 별도 작업으로 다루는 것이 안전하다.

### 7. OnHit sub effects

현재 흐름:

```text
OnHitEffectRegistry.HandleHit
  -> ApplyChain / ApplyWindBlade / ApplyBurn / ApplySlow / ApplyFreeze
```

현재 좋은 점:

- Chain/Wind는 `Health.Damage` 직접 호출이라 `TargetHit`를 다시 발동하지 않는다.
- 이미 무한 루프 방지 의도가 있다.

Gap:

- Chain/Wind crit은 `CriticalRoller` 직접 호출.
- Burn DOT는 이후 tick crit/authority가 없다.
- 공통 `CombatDamageResult` 이벤트와 연결되어 있지 않다.

목표:

- Chain/Wind는 `DamageSourceKind.SubEffect`
- `OnHitPolicy.SuppressSubEffectLoop`
- crit은 resolver 경유
- OnHit burn은 DOT source 생성으로 이전

## Gap 우선순위

| Priority | Gap | 이유 | 추천 Phase |
| --- | --- | --- | --- |
| P0 | Damage/Crit 계산 위치가 흩어져 있음 | 모든 후속 작업의 기반 | Phase 1 |
| P0 | Dagger teleport crit 누락 | 정책과 현재 동작 불일치 | Phase 2 |
| P0 | Flame direct tick crit 누락 | 정책과 현재 동작 불일치 | Phase 2 |
| P0 | DOT tick crit/authority 없음 | 정책과 현재 동작 불일치, 네트워크 리스크 큼 | Phase 2~3 |
| P1 | Projectile OnHit 누락 | 정책과 현재 동작 불일치 | Phase 4 |
| P1 | Meteor OnHit 누락 | 정책과 현재 동작 불일치 | Phase 4 |
| P1 | Damage result event 없음 | popup/OnHit/analytics 공통화 불가 | Phase 3~4 |
| P1 | MagicalGirl direct/fusion damage가 직접 `Health.Damage` | 보조 공격 정책과 불일치 | Phase 2 이후 |
| P2 | Enemy/player damage 전체 분리 미흡 | 범위 큼, 현재 목표 밖 | 별도 phase |
| P2 | Test/debug damage 경로 | 리팩토링 가치 낮음 | 제외 또는 마지막 |

## Phase 1 추천 구현 후보

Phase 1은 동작 변경을 최소화하고, 계산 경로만 공통화한다.

### 포함

- `CombatDamageRequest`
- `CombatDamageResult`
- `DamageSourceKind`
- `CriticalPolicy`
- `OnHitPolicy`
- `CombatDamageResolver`
- 기존 `CriticalRoller`는 resolver 내부에서 재사용

### 1차 적용 파일

- `Runtime/TestKhi/KhiMeleeComboController.cs`
- `Runtime/TestKhi/KhiBowController.cs`
- `Runtime/TestKhi/KhiStaffController.cs`
- 가능하면 `Runtime/TestKhi/KhiMeteor.cs`

### 아직 유지할 것

- `KhiMeleeHitbox.Sample`의 damage apply 방식
- `PlayerDamageRelay`
- `AttackBroadcast.RelayProjectileDamage`
- visual-only projectile clone 구조
- `OnHitEffectRegistry.TargetHit` 구조

이유:

- 계산 공통화와 네트워크 권위 전환을 동시에 하면 회귀 원인 추적이 어려워진다.
- Phase 1은 final damage 값이 기존과 동일한지 비교하기 쉽다.

## Phase 2 추천 구현 후보

Phase 2는 정책 누락 지점을 보정한다.

- Dagger teleport: resolver 경유, crit 적용, sequence id 부여
- Flame direct tick: resolver 경유, crit 적용, popup/result 준비
- Burn DOT: tick request 설계 시작
- OnHit chain/wind: resolver 경유

## 확인 필요 항목

1. `CombatDamageRequest/Result`의 namespace
   - 추천: `LostMemory.Combat`

2. 파일 위치
   - 추천: `Runtime/Combat/Damage/`

3. `WeaponId` 타입
   - 현재 문서에는 `string WeaponId`로 초안 작성
   - 나중에 `FixedString` 또는 enum/id table이 필요할 수 있음

4. `SourceId` 생성 주체
   - 추천: host/server에서 생성
   - 단, owner visual 예측용 temporary id가 필요할 수 있음

5. DOT source manager 위치
   - 추천: Phase 2 이후 결정
   - 후보: `CombatDotController`, `CombatStatusDamageSource`, `EnemyStatusEffect` 확장

6. MagicalGirl/Fusion/Tarot의 1차 포함 여부
   - 추천: Phase 1에서는 제외, Phase 2 이후 source kind별 편입

## 다음 액션

1. `Runtime/Combat/Damage/` 추가 여부 결정
2. `LostMemory.Combat` namespace 확정
3. Phase 1 branch 생성
4. `CombatDamageResolver` 최소 구현
5. 검/활/스태프 계산 경로만 resolver로 이전
6. Unity Editor에서 기존 damage/crit 동작 확인
