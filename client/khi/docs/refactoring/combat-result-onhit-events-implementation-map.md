# Combat Result OnHit Events Implementation Map

작성일: 2026-06-27
브랜치: `refactor/combat-result-onhit-events`

## 목적

지금까지의 작업으로 대부분의 player-origin damage source는 `CombatDamageResolver`를 통과한다.

하지만 OnHit 발동은 아직 `KhiMeleeComboController.TargetHit` 중심이다. 그래서 활, 스태프 projectile, meteor, 일부 area/direct hit source는 damage/crit 계산은 resolver를 통과해도 OnHit 효과 발동 경로에는 들어오지 않는다.

이번 브랜치의 목적은 `CombatDamageResult`를 기준으로 OnHit 발동 여부를 판단하는 공통 경로를 만들고, 기존 근접 전용 OnHit 흐름을 그 경로로 옮기는 것이다.

## 현재 구조 요약

### 현재 OnHit 진입점

- `KhiMeleeComboController.TargetHit`
- `OnHitEffectRegistry.HandleHit(...)`
- `TarotSystem.HandleHit(...)`
- `KhiCombatFeedbackBinder.HandleTargetHit(...)`
- `KhiSfxBinder.HandleTargetHit(...)`

현재 `TargetHit`은 다음 역할을 동시에 한다.

- 근접 공격 적중 이벤트
- melee damage popup 표시
- OnHit relic 효과 발동
- Tarot hit count 증가
- 근접 hit feedback/SFX 발동

이 때문에 projectile과 area damage를 OnHit에 연결하려면 `TargetHit`에 억지로 맞추기보다, damage result 기준의 새 이벤트를 추가하는 편이 안전하다.

### 이미 resolver를 통과하는 주요 player-origin source

- `KhiMeleeComboController`: melee combo
- `KhiBowController`: bow single/rapid projectile
- `KhiStaffController`: staff bolt/fireball projectile
- `KhiMeteor`: meteor area damage
- `KhiDaggerTeleportController`: dagger arrival attack
- `KhiFlameZone`: flame direct tick
- `EnemyStatusEffect`: burn DOT tick
- `KhiParryDamageOnTouch`: parry counter
- `OnHitEffectRegistry`: chain/wind sub effects
- `MagicalGirlProjectile`
- `MagicalGirlAOE`
- `MagicalGirlFusion`
- `MagicalGirlAI` fallback
- `TarotCards.DeathCard`

## 정책

### OnHit 발동

다음 source는 OnHit 발동 후보로 본다.

- `DamageSourceKind.Melee`
- `DamageSourceKind.Projectile`
- 단발성 `DamageSourceKind.Area`
- `DamageSourceKind.Counter`
- `DamageSourceKind.BeamOrStream` 중 `OnHitPolicy.TriggerWithCooldown`

### OnHit 미발동

다음 source는 OnHit을 발동하지 않는다.

- `DamageSourceKind.DamageOverTime`
- 지속 장판 tick
- `OnHitPolicy.Suppress`
- `OnHitPolicy.SuppressSubEffectLoop`
- Chain/Wind/Tarot Death 같은 sub effect

### Beam/Stream cooldown

`DamageSourceKind.BeamOrStream`은 source/target 단위 cooldown이 필요하다.

권장 key:

```text
attacker/source + target + weapon/source kind
```

우선 구현에서는 다음 값 조합을 사용한다.

```text
SourceId, TargetNetworkObjectId, target instance id fallback, WeaponId
```

`TargetNetworkObjectId`가 없는 local/solo 상황에서는 target `GetInstanceID()` fallback을 사용한다.

## 영향 파일 지도

### 새로 추가할 가능성이 높은 파일

- `Runtime/Combat/Damage/CombatDamageEvent.cs`
- `Runtime/Combat/Damage/CombatDamageEventDispatcher.cs`
- 필요 시 `Runtime/Combat/Damage/CombatDamageTargetContext.cs`

### 수정 가능성이 높은 파일

- `Runtime/Combat/OnHitEffectRegistry.cs`
- `Runtime/TestKhi/KhiMeleeComboController.cs`
- `Runtime/TestKhi/KhiMeleeHitbox.cs`
- `Runtime/TestKhi/KhiArrowProjectile.cs`
- `Runtime/TestKhi/KhiMeteor.cs`
- `Runtime/TestKhi/KhiFlameZone.cs`
- `Runtime/TestKhi/KhiDaggerTeleportController.cs`
- `Runtime/Tarot/TarotSystem.cs`
- `Runtime/UI/DamagePopup/DamagePopupSpawner.cs`는 직접 수정하지 않는 것을 우선한다.

### 가능하면 이번 브랜치에서 피할 파일

- `PlayerDamageRelay.cs`
- `AttackBroadcast.cs`
- `PlayerHealthSync.cs`
- `MonsterHealthSync.cs`

네트워크 relay/result 전파는 이 브랜치에서 구조를 크게 건드리지 않는다.

## 권장 구현 순서

### 1단계: dispatcher 추가

목표:

- `CombatDamageResult`와 target/attacker context를 전달하는 얇은 이벤트를 추가한다.
- 아직 기존 동작을 바꾸지 않는다.

예상 타입:

```text
CombatDamageEvent
CombatDamageEventDispatcher
```

이벤트에 포함할 정보:

- `CombatDamageResult Result`
- `Health Target`
- `GameObject Attacker`
- `Vector2 HitDirection`
- `Vector3 HitPoint`
- 선택적으로 `KhiAttackRequest`, `AttackStepData`

완료 조건:

- 컴파일 성공
- 기존 동작 변경 없음

구현 상태 (2026-06-27):

- `CombatDamageEvent`를 추가했다.
- `CombatDamageEventDispatcher`를 추가했다.
- 아직 구독자는 없다.

### 2단계: melee를 dispatcher로 복제 발행

목표:

- `KhiMeleeComboController.TargetHit`은 유지한다.
- melee hit 시 `CombatDamageEventDispatcher.RaiseDamageApplied(...)`도 같이 호출한다.
- `OnHitEffectRegistry`는 아직 기존 `TargetHit` 구독 유지.

완료 조건:

- 기존 melee popup/OnHit/Tarot/SFX가 그대로 동작한다.
- 새 dispatcher 이벤트가 로그 또는 테스트에서 발행됨을 확인할 수 있다.

구현 상태 (2026-06-27):

- `KhiMeleeComboController`가 기존 `TargetHit`을 유지하면서 `CombatDamageEventDispatcher.RaiseDamageApplied(...)`도 호출한다.
- 아직 `OnHitEffectRegistry`는 기존 `TargetHit` 구독을 유지한다.

### 3단계: OnHitEffectRegistry를 dispatcher 구독으로 이전

목표:

- `OnHitEffectRegistry`가 `TargetHit` 대신 dispatcher 이벤트를 구독한다.
- `OnHitPolicy`에 따라 발동/미발동을 판단한다.
- melee에서 기존과 동일하게 OnHit이 발동한다.

주의:

- popup은 이 단계에서 분리하지 않는다.
- `TargetHit`을 바로 제거하지 않는다. Tarot/SFX/feedback이 아직 사용한다.

완료 조건:

- melee OnHit 효과가 기존처럼 발동한다.
- DOT/sub effect는 OnHit을 발동하지 않는다.
- chain/wind 재귀 루프가 생기지 않는다.

구현 상태 (2026-07-01):

- `OnHitEffectRegistry`가 `CombatDamageEventDispatcher.DamageApplied`를 구독한다.
- 기존 `KhiMeleeComboController.TargetHit` 구독은 제거했다.
- melee popup 표시는 기존처럼 `OnHitEffectRegistry`에서 유지하되, `DamageSourceKind.Melee`에만 반응한다.
- `OnHitPolicy.Suppress`와 `SuppressSubEffectLoop`은 OnHit 발동을 차단한다.
- `OnHitPolicy.TriggerWithCooldown`은 source/target/weapon key 기준 cooldown을 적용한다.

### 4단계: projectile/area source 발행 추가

목표:

- bow arrow hit
- staff bolt/fireball hit
- meteor hit
- dagger arrival hit
- flame direct tick

위 source들이 damage 적용 후 dispatcher event를 발행하게 한다.

우선순위:

1. `KhiArrowProjectile`
2. `KhiMeteor`
3. `KhiDaggerTeleportController`
4. `KhiFlameZone`

완료 조건:

- 활/스태프 projectile OnHit 발동 확인
- meteor OnHit 발동 확인
- flame direct tick은 cooldown 정책에 따라 제한 발동
- burn DOT는 미발동

### 5단계: Tarot/SFX/feedback 이전 여부 결정

현재 `TarotSystem`, `KhiCombatFeedbackBinder`, `KhiSfxBinder`는 melee `TargetHit`에 묶여 있다.

이번 브랜치에서 모두 이전하면 범위가 커질 수 있으므로 우선 다음처럼 나눈다.

- `OnHitEffectRegistry` 이전은 이번 브랜치 핵심.
- `TarotSystem`은 melee hit count 유지가 우선이므로 이번 브랜치에서 유지 가능.
- projectile도 Tarot hit count에 포함할지는 별도 밸런스 결정이 필요하다.
- SFX/feedback은 melee 연출 전용 성격이 강하므로 이번 브랜치에서 유지 가능.

## 리스크

- projectile hit에 OnHit이 붙으면 relic proc 빈도가 크게 올라갈 수 있다.
- flame direct tick이 OnHit을 너무 자주 발동하면 밸런스가 흔들릴 수 있다.
- popup을 dispatcher로 같이 옮기면 기존 중복 표시가 생길 수 있다.
- guest/client visual-only clone이 event를 발행하면 중복 OnHit이 생길 수 있다.

## 안전장치

- visual-only clone은 event를 발행하지 않는다.
- host/server가 아닌 쪽에서는 OnHit 효과 적용을 하지 않는다.
- `OnHitPolicy.Suppress`와 `SuppressSubEffectLoop`는 무조건 발동 차단한다.
- `TriggerWithCooldown`은 source/target key 기준으로 제한한다.
- 기존 `TargetHit`은 바로 삭제하지 않는다.

## 첫 커밋 목표

첫 구현 커밋은 다음 범위만 권장한다.

```text
dispatcher 타입 추가
melee hit에서 dispatcher 발행
OnHitEffectRegistry는 아직 기존 구독 유지
compile/automation 통과
```

그 다음 커밋에서 `OnHitEffectRegistry`를 dispatcher 구독으로 옮기는 편이 안전하다.
