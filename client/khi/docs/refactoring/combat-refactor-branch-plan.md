# LostMemory Combat Refactor Branch Plan

작성일: 2026-06-23
기준 문서:

- `combat-refactor-diagnosis.md`
- `combat-refactor-plan.md`
- `combat-damage-source-map.md`

## 목적

이 문서는 전투/무기 리팩토링을 실제 작업 브랜치 단위로 나누기 위한 실행 관리 문서다.

진단/정책/코드 지도는 이미 별도 문서에 정리되어 있으므로, 이 문서는 다음에 집중한다.

- 어떤 브랜치를 어떤 순서로 만들지
- 각 브랜치에서 어디까지 하고 어디부터는 하지 않을지
- 완료 조건은 무엇인지
- 테스트는 무엇을 확인해야 하는지
- 리스크가 큰 작업을 어떤 브랜치로 분리할지

## Epic

```text
combat-damage-refactor
```

이 epic의 최종 목표는 다음과 같다.

- 모든 damage source가 공통 damage/crit 계산 경로를 사용한다.
- host/server가 최종 damage result를 확정한다.
- owner는 입력 즉시 visual을 재생하고, non-owner는 visual-only/result event를 재생한다.
- DOT는 매 tick마다 crit을 적용하되 OnHit은 미발동한다.
- Beam/Stream 계열은 OnHit을 source/target별 cooldown으로 제한한다.
- 무기 시스템은 장기적으로 유동적 무기 풀, 2-slot loadout, active weapon 1개 구조로 이동한다.

## Branch Order

권장 작업 순서:

1. `refactor/combat-damage-resolver-base`
2. `refactor/combat-damage-resolver-weapons`
3. `refactor/combat-damage-missing-sources`
4. `refactor/combat-dot-damage-source`
5. `refactor/combat-result-onhit-events`
6. `refactor/weapon-runtime-entries`

핵심 원칙:

```text
계산 공통화
-> 누락 보정
-> DOT 구조
-> result/OnHit 이벤트
-> 무기 슬롯/전환 구조
```

네트워크 relay, DOT, OnHit 이벤트, 무기 슬롯 구조를 한 브랜치에 섞지 않는다.

## 1. refactor/combat-damage-resolver-base

### 목표

공통 damage/crit 계산을 위한 타입과 resolver를 추가한다. 기존 무기 동작은 최대한 바꾸지 않는다.

### 포함

- `Runtime/Combat/Damage/` 폴더 추가
- `CombatDamageRequest` 추가
- `CombatDamageResult` 추가
- `DamageSourceKind` 추가
- `CriticalPolicy` 추가
- `OnHitPolicy` 추가
- `CombatDamageResolver` 추가
- 기존 `CriticalRoller`는 resolver 내부에서 재사용

### 현재 구현 상태 (2026-06-23)

- `Runtime/Combat/Damage/` 폴더와 Unity `Damage.meta`를 추가했다.
- `DamageSourceKind`, `CriticalPolicy`, `OnHitPolicy`를 추가했다.
- `CombatDamageRequest`, `CombatDamageResult`, `CombatDamageResolver`를 추가했다.
- `CombatDamageResolver.Resolve(...)`는 base damage, `DamageMultiplier`, `AttackPower`, 선택적 `FinisherDamage`, `CriticalRoller.Roll(...)` 순서로 계산한다.
- `CombatDamageResult`는 result 기반 OnHit 처리를 위해 `OnHitCooldownSeconds`를 보존한다.
- 기존 검/활/스태프, DOT, OnHit, 네트워크 relay 코드는 아직 연결하지 않았다.
- 남은 확인: Unity Editor compile, resolver 간단 호출, 기존 무기 smoke test.

### 제외

- 검/활/스태프 실제 연결
- `Health.Damage` 적용 경로 변경
- `PlayerDamageRelay` 변경
- `AttackBroadcast` 변경
- DOT 구조 변경
- OnHit 이벤트 구조 변경
- 무기 슬롯/전환 구조 변경

### 완료 조건

- 새 타입과 resolver가 컴파일된다.
- 기존 게임 동작은 변경되지 않는다.
- resolver가 기존 `CriticalRoller`와 같은 결과 정책을 사용할 수 있다.
- resolver 단위로 base damage, AttackPower, FinisherDamage, CriticalDamage 계산 흐름을 확인할 수 있다.

### 테스트

- Unity compile 확인
- resolver 단위 디버그/간단 호출 확인
- 기존 플레이어 공격 동작이 바뀌지 않았는지 smoke test

### 위험도

낮음. 새 타입 추가가 중심이다.

## 2. refactor/combat-damage-resolver-weapons

### 목표

기존 핵심 무기인 검/활/스태프의 데미지/크리티컬 계산을 `CombatDamageResolver` 경로로 이동한다.

### 포함

- `KhiMeleeComboController` 계산 경로 변경
- `KhiBowController` 계산 경로 변경
- `KhiStaffController` bolt/fireball 계산 경로 변경
- 가능하면 `KhiMeteor` 계산 경로 변경
- 기존 final damage와 crit 결과를 최대한 동일하게 유지

### 제외

- damage apply 방식 변경
- hit 판정 host/server 이전
- projectile movement authority 변경
- visual-only clone 구조 변경
- OnHit 이벤트 공통화
- DOT 구조 변경

### 완료 조건

- 검/활/스태프가 resolver를 통해 최종 데미지와 crit을 계산한다.
- 기존 `CriticalRoller.Roll` 직접 호출이 핵심 무기에서 줄어든다.
- 기존 데미지 수치가 의도치 않게 바뀌지 않는다.
- 기존 popup 표시가 유지된다.

### 테스트

- 검 기본 공격 crit 확인
- 활 단발/연사 crit 확인
- staff bolt/fireball crit 확인
- meteor 포함 시 meteor crit 확인
- solo play smoke test
- 가능하면 host/client editor test

### 위험도

중간. 계산 경로가 바뀌지만 네트워크 적용 경로는 유지한다.

## 3. refactor/combat-damage-missing-sources

### 목표

정책상 crit이 적용되어야 하지만 현재 빠져 있거나 흩어진 플레이어 damage source를 resolver에 편입한다.

### 포함

- `KhiDaggerTeleportController` 도착 공격 crit 적용
- `KhiDaggerTeleportController` sequence id 추적 개선
- `KhiFlameZone` direct tick crit 적용
- `KhiFlameZone` direct tick에 `DamageSourceKind.BeamOrStream` 적용
- `KhiFlameZone` OnHit cooldown 준비 필드 또는 request 값 추가
- `OnHitEffectRegistry.ApplyChain` resolver 경유
- `OnHitEffectRegistry.ApplyWindBlade` resolver 경유
- 필요 시 `KhiParryDamageOnTouch` 분류만 정리

### 제외

- Burn DOT tick 구조 변경
- `EnemyStatusEffect` DOT tick authority 변경
- 공통 `CombatDamageResult` event 도입
- projectile OnHit 적용
- 무기 슬롯 구조 변경

### 완료 조건

- 단검 텔레포트가 crit을 적용한다.
- 화염방사기 direct tick이 crit을 적용한다.
- Chain/Wind 추가타가 resolver를 통해 crit을 계산한다.
- DOT는 아직 기존 구조를 유지한다는 점이 명확히 문서/주석으로 남는다.

### 테스트

- 단검 텔레포트 데미지/crit 확인
- 화염방사기 direct tick crit 확인
- chain/wind 추가타 crit 확인
- OnHit loop가 새로 생기지 않는지 확인

### 위험도

중간 이상. tick형 공격과 추가타가 포함된다.

## 4. refactor/combat-dot-damage-source

### 목표

Burn DOT를 host/server authoritative damage source로 정리하기 시작한다.

### 포함

- DOT source id 설계
- `sourceId + tickIndex + targetNetworkObjectId` 식별자 도입
- Burn DOT tick마다 resolver 경유
- DOT tick마다 crit 적용
- DOT OnHit은 suppress
- DOT popup/result 표시 준비
- `EnemyStatusEffect.ApplyBurn` 책임 재검토

### 제외

- 모든 상태이상 시스템 전면 재작성
- 모든 DOT/장판을 한 번에 이전
- Steam/session 구현
- 무기 슬롯 구조 변경

### 완료 조건

- Burn DOT tick이 host/server 기준으로 crit을 확정하는 구조로 이동한다.
- 클라이언트가 DOT crit을 직접 굴리지 않는다.
- DOT tick마다 중복 적용 방지를 위한 id가 있다.
- DOT OnHit은 발동하지 않는다.

### 테스트

- burn DOT tick별 crit 확인
- host/client HP 결과 일치 확인
- popup이 과도하면 표시 제한 또는 합산 정책 확인
- 같은 target에 여러 burn source가 있을 때 source id 충돌 없는지 확인

### 위험도

높음. 현재 `EnemyStatusEffect`가 직접 `Health.Damage`를 호출하므로 구조 변경 폭이 크다.

## 5. refactor/combat-result-onhit-events

### 목표

근접 전용 `TargetHit` 중심의 OnHit/popup 흐름을 공통 `CombatDamageResult` 이벤트 기반으로 이동하기 시작한다.

### 포함

- `CombatDamageResult` event 또는 dispatcher 추가
- direct hit source의 OnHit 발동을 result 기반으로 정리
- projectile OnHit 적용
- meteor/one-shot area OnHit 적용
- `OnHitPolicy.Trigger` 적용
- `OnHitPolicy.TriggerWithCooldown` 적용
- `OnHitPolicy.SuppressSubEffectLoop` 적용
- popup result 기반화 검토

### 제외

- DOT OnHit 발동
- 지속 장판 OnHit 발동
- 무기 슬롯 구조 변경
- Steam/session 구현

### 완료 조건

- direct melee/projectile/one-shot area skill이 정책에 맞게 OnHit을 발동한다.
- DOT/지속 장판은 OnHit을 발동하지 않는다.
- 화염방사기/beam은 target별 cooldown에 따라 OnHit을 발동한다.
- SubEffect는 재귀 OnHit loop를 만들지 않는다.

### 테스트

- 검 OnHit 발동 확인
- 활/스태프 projectile OnHit 발동 확인
- meteor OnHit 발동 확인
- burn DOT OnHit 미발동 확인
- flame direct tick OnHit cooldown 확인
- chain/wind 재귀 루프 방지 확인

### 위험도

높음. OnHit, popup, analytics, relic 효과가 얽힌다.

## 6. refactor/weapon-runtime-entries

### 목표

무기 전환 구조를 장기적으로 유동적 무기 풀, 2-slot loadout, active weapon 1개 구조로 이전한다.

### 포함

- `WeaponRuntimeEntry` 초안
- `WeaponLoadout` 초안
- `WeaponDebugInventory` 초안
- `ActiveWeaponSlot` 개념 정리
- `WeaponModeController.ApplyMode` entry 기반 전환 준비
- 테스트 모드에서 전체 무기 전환 가능하도록 구조 설계

### 제외

- damage resolver 변경
- DOT 변경
- OnHit 이벤트 변경
- Steam/session 구현

### 완료 조건

- 인게임 2개 장착 규칙과 테스트 전체 무기 전환 규칙이 분리된다.
- 새 무기 추가 시 enum/분기 수정 의존도가 줄어든다.
- 기존 5개 무기 전환이 유지된다.

### 테스트

- 인게임 loadout 2개 전환 확인
- 테스트 모드 전체 무기 전환 확인
- active weapon은 항상 1개만 유지되는지 확인
- non-owner visual mode sync 유지 확인

### 위험도

중간 이상. prefab wiring과 네트워크 mode sync 영향이 있다.

## Cross-Branch Rules

모든 브랜치에 적용한다.

- 한 브랜치에서 계산/네트워크/DOT/무기 슬롯을 동시에 바꾸지 않는다.
- 기존 동작 유지가 목표인 브랜치는 동작 변경을 명확히 기록한다.
- `Health.Damage` 직접 호출 제거는 별도 단계로 한다.
- `AttackBroadcast`와 `PlayerDamageRelay` 변경은 최소화한다.
- visual-only clone 구조는 result/onhit 브랜치 전까지 유지한다.
- Steam/session 구현은 이번 epic 후반 또는 별도 epic으로 둔다.

## Merge / Review Checklist

각 브랜치 완료 전 확인한다.

- Unity compile 성공
- 관련 weapon smoke test 완료
- host/client editor test 가능하면 완료
- crit 적용 여부 확인
- OnHit 발동/미발동 정책 확인
- visual-only clone이 damage를 적용하지 않는지 확인
- 기존 prefab/script reference 깨짐 없음
- 관련 문서 업데이트

## Suggested First Branch

첫 작업 브랜치:

```text
refactor/combat-damage-resolver-base
```

첫 브랜치에서 할 일:

1. `Runtime/Combat/Damage/` 폴더 생성
2. `DamageSourceKind.cs` 추가
3. `CriticalPolicy.cs` 추가
4. `OnHitPolicy.cs` 추가
5. `CombatDamageRequest.cs` 추가
6. `CombatDamageResult.cs` 추가
7. `CombatDamageResolver.cs` 추가
8. 기존 `CriticalRoller`를 resolver 내부에서 호출
9. 아직 기존 무기 코드는 최소한만 건드리기

첫 브랜치에서 하지 않을 일:

- 검/활/스태프 연결
- DOT 변경
- OnHit 변경
- 네트워크 relay 변경
- 무기 슬롯 변경

## Open Notes

- `CombatDamageRequest/Result`는 우선 `LostMemory.Combat` namespace를 사용한다.
- 파일 위치는 `Runtime/Combat/Damage/`를 추천한다.
- `WeaponId`는 초기에 string으로 시작하고, 필요하면 이후 `FixedString` 또는 id table로 바꾼다.
- `SourceId`는 host/server 생성이 원칙이다. owner visual 예측용 temporary id는 result/onhit 단계에서 검토한다.
- MagicalGirl/Fusion/Tarot은 Phase 1에서 제외하고, resolver가 안정화된 뒤 source kind별로 편입한다.
