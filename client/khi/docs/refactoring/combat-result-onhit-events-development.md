# Combat Result OnHit Events Development

작성일: 2026-07-01
브랜치: `refactor/combat-result-onhit-events`

## 목적

이번 작업의 목적은 OnHit 효과 발동 기준을 기존 melee 전용 이벤트인 `KhiMeleeComboController.TargetHit`에서 damage result 기반 공통 이벤트인 `CombatDamageEventDispatcher.DamageApplied`로 옮기는 것이다.

장기 목표는 melee, projectile, area, beam/stream, DOT 같은 여러 damage source가 같은 resolver/result 흐름을 타게 만드는 것이다. 다만 이번 브랜치에서는 전체 source를 한 번에 연결하지 않고, `OnHitEffectRegistry`의 구독 기준만 먼저 옮겨서 이후 확장을 받을 수 있게 만든다.

## 이번 개발 범위

수정 파일:

- `client/LostMemory/Assets/_Project/Scripts/Runtime/Combat/OnHitEffectRegistry.cs`
- `client/khi/docs/refactoring/combat-refactor-branch-plan.md`
- `client/khi/docs/refactoring/combat-result-onhit-events-implementation-map.md`

새 문서:

- `client/khi/docs/refactoring/combat-result-onhit-events-development.md`

## 구현 내용

`OnHitEffectRegistry`가 더 이상 `KhiMeleeComboController.TargetHit`을 직접 구독하지 않고, `CombatDamageEventDispatcher.DamageApplied`를 구독하도록 변경했다.

기존 melee damage popup은 유지했다. 단, 모든 damage event에 반응하지 않도록 `DamageSourceKind.Melee`인 경우에만 `DamagePopupSpawner.Instance.NotifyMeleeDamage(...)`를 호출한다. 그래서 이번 변경은 popup 위치나 공격 타이밍을 바꾸는 작업이 아니다.

OnHit 발동 여부는 `CombatDamageResult.OnHitPolicy`를 기준으로 판단한다.

- `OnHitPolicy.Trigger`: OnHit 발동
- `OnHitPolicy.Suppress`: OnHit 차단
- `OnHitPolicy.SuppressSubEffectLoop`: OnHit 차단
- `OnHitPolicy.TriggerWithCooldown`: source/target/weapon key 기준 cooldown 통과 시 발동

`TriggerWithCooldown`을 위해 `OnHitEffectRegistry` 내부에 cooldown key 저장소를 추가했다.

key 구성:

```text
source + target + sourceKind + weaponId
```

우선순위:

- source는 `CombatDamageResult.SourceId`를 우선 사용하고, 없으면 attacker instance id를 fallback으로 사용한다.
- target은 `CombatDamageResult.TargetNetworkObjectId`를 우선 사용하고, 없으면 target instance id를 fallback으로 사용한다.
- source kind와 weapon id를 함께 묶어서 다른 공격 종류 또는 무기 간 cooldown 충돌을 줄인다.

Chain OnHit은 기존처럼 직접 `Health.Damage`를 호출한다. 단, chain damage request에는 `OnHitPolicy.SuppressSubEffectLoop`을 넣어서 chain이 다시 OnHit을 발동하는 재귀 루프를 막는다.

## 의도적으로 유지한 것

이번 브랜치에서는 다음 항목을 바꾸지 않았다.

- 공격 입력 타이밍
- 공격 애니메이션 타이밍
- hitbox 판정 타이밍
- melee popup 위치 계산
- `TarotSystem`의 기존 melee hit count 경로
- melee SFX/feedback 경로
- projectile/area damage source의 dispatcher 발행
- Steam/session 구조
- host/client relay 구조

## 검증 결과

Unity batchmode compile:

- 로그: `client/unity-compile.log`
- 결과: 성공
- `Tundra build success`: 1회
- `error CS`: 0개
- `Scripts have compiler errors`: 0개
- `Exiting batchmode successfully now!` 확인

Combat damage automation:

- 로그: `client/combat-damage-automation.log`
- 실행 메서드: `LostMemory.Tests.Combat.CombatDamageResolverAutomation.Run`
- 결과: 성공
- `CombatDamageResolverAutomation` 6개 case 모두 PASS
- `CombatDamageResolverAutomation] FAIL`: 0개
- `Passed 6 combat damage resolver checks.` 확인
- Unity 종료 코드 0

통과한 자동화 case:

- `CriticalPolicyNever`
- `AttackPower`
- `Finisher`
- `CriticalSuccess`
- `CriticalFail`
- `DamageOverTime`

## 검증 중 발견한 별도 이슈

자동화 테스트 로그에 다음 예외가 보였지만, 이번 리팩토링 테스트 실패는 아니다.

- `DirectoryNotFoundException`: Rider plugin 경로 `C:\Program Files\JetBrains\JetBrains Rider 2026.1.0.1\plugins\rider-unity\EditorPlugin` 없음
- `NullReferenceException`: `Unity.Netcode.Components.NetworkAnimator.OnValidate` 후처리 중 발생

테스트 자체는 모두 PASS했고 batchmode도 성공 종료했다. 위 이슈는 별도 정리 후보로 남긴다.

## 커밋 전 확인

현재 커밋 대상:

```bash
git add client/LostMemory/Assets/_Project/Scripts/Runtime/Combat/OnHitEffectRegistry.cs client/khi/docs/refactoring/combat-refactor-branch-plan.md client/khi/docs/refactoring/combat-result-onhit-events-implementation-map.md client/khi/docs/refactoring/combat-result-onhit-events-development.md
git commit -m "refactor: route on-hit registry through damage events"
```

## 다음 작업 후보

다음 브랜치에서는 projectile/area source가 `CombatDamageEventDispatcher.RaiseDamageApplied(...)`를 발행하도록 연결하는 것이 자연스럽다.

우선순위 후보:

1. `KhiArrowProjectile`
2. `KhiMeteor`
3. `KhiDaggerTeleportController`
4. `KhiFlameZone`

완료 조건:

- projectile/area damage도 result 기반 OnHit 발동 경로에 들어온다.
- DOT와 sub effect는 OnHit을 발동하지 않는다.
- beam/stream 계열은 cooldown 정책을 통과할 때만 OnHit을 발동한다.
- melee 기존 체감은 유지한다.
