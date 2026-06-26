# LostMemory Combat Refactor Branch Plan

작성일: 2026-06-23
기준 문서:

- `combat-refactor-diagnosis.md`
- `combat-refactor-plan.md`
- `combat-damage-source-map.md`
- `combat-damage-source-audit.md`

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
5. `refactor/combat-special-damage-sources`
6. `refactor/combat-magical-girl-damage-sources`
7. `refactor/combat-tarot-damage-source`
8. `refactor/combat-result-onhit-events`
9. `refactor/weapon-runtime-entries`

핵심 원칙:

```text
계산 공통화
-> 누락 보정
-> DOT 구조
-> result/OnHit 이벤트
-> 무기 슬롯/전환 구조
```

네트워크 relay, DOT, OnHit 이벤트, 무기 슬롯 구조를 한 브랜치에 섞지 않는다.

### Audit Checkpoint (2026-06-25)

`refactor/combat-damage-missing-sources` 완료 후에도 resolver를 통과하지 않는 player-origin damage source가 추가로 확인되었다.

확인된 후보:

- Burn DOT
- Parry 반격 데미지
- MagicalGirl projectile/AOE/fusion/fallback 데미지
- Tarot Death 카드

조정된 원칙:

- `refactor/combat-dot-damage-source`는 Burn DOT만 처리한다.
- Parry는 `refactor/combat-special-damage-sources`에서 먼저 처리한다.
- MagicalGirl은 projectile sync, visual-only clone, fusion 로직이 얽혀 있으므로 `refactor/combat-magical-girl-damage-sources`로 분리한다.
- Tarot Death 카드는 `refactor/combat-tarot-damage-source`에서 별도 처리한다.
- 적/보스가 플레이어에게 주는 damage source는 현재 combat damage resolver 범위에서 제외한다.
- 이미 resolver로 최종 damage를 계산한 뒤 `Health.Damage(...)`로 적용하는 코드는 누락으로 보지 않는다.

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

### 현재 구현 상태 (2026-06-25)

- `KhiMeleeComboController`의 검 콤보 데미지/crit 계산을 `CombatDamageResolver` 경로로 이동했다.
- `KhiBowController`의 단발/연사 화살 데미지/crit 계산을 `CombatDamageResolver` 경로로 이동했다.
- `KhiStaffController`의 bolt/fireball 데미지/crit 계산을 `CombatDamageResolver` 경로로 이동했다.
- `KhiMeteor`의 폭발 데미지/crit 계산을 `CombatDamageResolver` 경로로 이동했다.
- 기존 `Health.Damage`, `PlayerDamageRelay`, projectile movement, visual-only clone, popup 호출 흐름은 변경하지 않았다.
- Unity batchmode compile 결과: return code 0.
- `CombatDamageResolverAutomation` 실행 결과: 5개 계산 체크 PASS, return code 0.
- 남은 확인: Unity Editor에서 검/활/스태프/메테오 실제 플레이 smoke test.

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

### 현재 구현 상태 (2026-06-25)

- `KhiDaggerTeleportController`의 도착 공격 데미지 계산을 `CombatDamageResolver` 경로로 이동했다.
- 단검 도착 공격은 `DamageSourceKind.Melee`로 분류하고, sequence id를 공격 단위로 증가시키도록 정리했다.
- `KhiFlameZone`의 direct tick 데미지 계산을 `CombatDamageResolver` 경로로 이동했다.
- 화염방사기 direct tick은 `DamageSourceKind.BeamOrStream`으로 분류하고, `OnHitPolicy.TriggerWithCooldown` 및 tick index 정보를 request에 담도록 준비했다.
- `OnHitEffectRegistry.ApplyChain`과 `ApplyWindBlade`의 추가타 데미지 계산을 `CombatDamageResolver` 경로로 이동했다.
- Chain/Wind 추가타는 `DamageSourceKind.SubEffect`와 `OnHitPolicy.SuppressSubEffectLoop`로 분류해 추가타가 다시 OnHit 루프를 만들지 않도록 의도를 명확히 했다.
- Burn DOT, `EnemyStatusEffect` DOT tick, 공통 `CombatDamageResult` event, weapon slot 구조는 이번 브랜치에서 변경하지 않았다.
- Unity batchmode compile 결과: `Tundra build success`, C# compile error 없음.
- `CombatDamageResolverAutomation` 실행 결과: 5개 계산 체크 PASS, Unity batchmode return code 0.
- 참고: batchmode 로그에 Rider 경로 관련 `DirectoryNotFoundException`과 NGO `NetworkAnimator.OnValidate` 관련 `NullReferenceException`이 출력되지만, 현재 combat damage resolver 검증 실패는 아니며 Unity는 return code 0으로 종료했다.

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

### 현재 조정 상태 (2026-06-25)

- `combat-damage-source-audit.md`를 통해 추가 누락 damage source를 확인했다.
- 이번 브랜치에서는 Burn DOT만 처리한다.
- Parry, MagicalGirl, Tarot은 `refactor/combat-special-damage-sources`로 분리한다.
- `EnemyStatusEffect.cs`는 `git blame`상 송주헌 작성으로 보이지만, 기능 이력 기준으로는 김회인 구현 흐름에 속한다.
- 이용호가 추가한 `KhiPlayerActionGate.IsBlocked` 기반 burn instigator block 보호 로직은 유지한다.

### 현재 구현 상태 (2026-06-25)

- `EnemyStatusEffect`의 Burn tick damage를 `CombatDamageResolver` 경로로 이동했다.
- Burn DOT tick은 `DamageSourceKind.DamageOverTime`으로 분류한다.
- Burn DOT tick은 `OnHitPolicy.Suppress`를 사용해 OnHit을 발동하지 않는다.
- Burn DOT tick마다 `tickIndex`를 증가시켜 request에 포함한다.
- Burn DOT source/attacker/target network object id를 가능한 범위에서 request에 보존한다.
- 기존 `ApplyBurn` 호출부가 넘기던 damagePerTick 값을 유지하기 위해 DOT resolver request는 `applyAttackPower: false`로 처리한다.
- 즉, 기존 AttackPower 반영 여부는 호출부 정책을 유지하고, DOT tick에서는 crit/critical damage만 resolver를 통해 적용한다.
- popup/result event 공통화는 이번 브랜치에서 하지 않는다.
- Unity batchmode compile 결과: `Tundra build success`, return code 0.
- `CombatDamageResolverAutomation` 실행 결과: DamageOverTime 케이스 포함 6개 계산 체크 PASS, return code 0.

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

## 5. refactor/combat-special-damage-sources

### 목표

DOT 이후에도 남아 있는 특수 player-origin damage source를 resolver 경로로 이동한다.

### 포함

- `KhiParryDamageOnTouch` 반격 데미지 resolver 경유
- `MagicalGirlProjectile` resolver 경유
- `MagicalGirlAOE` tick damage resolver 경유
- `MagicalGirlFusion` laser/global AOE resolver 경유
- `MagicalGirlAI` fallback direct damage resolver 경유
- `TarotCards.DeathCard`의 crit 정책 결정 및 resolver 경유 여부 확정

### 현재 구현 상태 (2026-06-26)

- `DamageSourceKind.Counter`를 추가했다.
- `KhiParryDamageOnTouch`의 반격 데미지 crit 계산을 `CombatDamageResolver` 경로로 이동했다.
- 기존 반격 데미지는 이미 감쇠/보호막 처리 후 값이므로 `applyAttackPower: false`로 유지했다.
- 기존 `PlayerDamageRelay`, `Health.Damage`, knockback, feedback, popup 호출 흐름은 변경하지 않았다.
- MagicalGirl 계열은 별도 브랜치 `refactor/combat-magical-girl-damage-sources`에서 처리한다.

### 제외

- Burn DOT
- 공통 OnHit result event 전면 도입
- 무기 슬롯 구조 변경
- 적/보스가 player에게 주는 damage source

### 완료 조건

- Parry 반격이 resolver를 통해 crit을 계산한다.
- MagicalGirl 계열 damage source가 source kind와 정책을 명시한다.
- Tarot Death 카드가 crit 대상인지 명확히 결정된다.
- 특수 source가 DOT/OnHit/result event 작업과 섞이지 않는다.

### 테스트

- Parry 반격 crit 확인
- MagicalGirl projectile crit 확인
- MagicalGirl AOE tick crit 확인
- MagicalGirl fusion laser/global AOE damage 확인
- Tarot Death 카드 damage 정책 확인

### 위험도

중간 이상. MagicalGirl은 projectile sync, visual-only clone, owner action gate가 얽혀 있으므로 DOT와 분리한다.

## 6. refactor/combat-magical-girl-damage-sources

### 목표

MagicalGirl 계열 player-origin damage source를 resolver 경로로 이동한다.

### 포함

- `MagicalGirlProjectile` projectile damage resolver 경유
- `MagicalGirlAOE` tick damage resolver 경유
- `MagicalGirlFusion` laser/global AOE resolver 경유
- `MagicalGirlAI` fallback direct damage resolver 경유
- 기존 projectile sync, visual-only clone, owner action gate 흐름 유지

### 현재 구현 상태 (2026-06-26)

- `MagicalGirlProjectile`의 발사 시점 damage/crit 계산을 `CombatDamageResolver` 경로로 이동했다.
- projectile은 `DamageSourceKind.Projectile`로 분류하고, 기존처럼 발사 시점에 최종 damage와 crit 여부를 저장한다.
- `MagicalGirlAOE`의 tick damage 계산을 `CombatDamageResolver` 경로로 이동했다.
- AOE tick은 `DamageSourceKind.Area`, `OnHitPolicy.Suppress`로 분류한다.
- AOE tick마다 `tickIndex`를 증가시켜 request에 포함한다.
- `MagicalGirlFusion`의 laser tick damage를 `CombatDamageResolver` 경로로 이동했다.
- Fusion laser tick은 `DamageSourceKind.BeamOrStream`, `OnHitPolicy.TriggerWithCooldown`으로 분류한다.
- Fusion laser tick마다 `tickIndex`를 증가시켜 request에 포함한다.
- `MagicalGirlFusion`의 global AOE damage를 `CombatDamageResolver` 경로로 이동했다.
- Fusion global AOE는 `DamageSourceKind.Area`로 분류한다.
- `MagicalGirlAI` catalog fallback direct damage를 `CombatDamageResolver` 경로로 이동했다.
- fallback direct damage는 `DamageSourceKind.Summon`으로 분류한다.
- 기존 `Health.Damage`, popup, slow, pull, visual-only clone, projectile despawn broadcast 흐름은 변경하지 않았다.
- Tarot Death 카드는 별도 브랜치 `refactor/combat-tarot-damage-source`에서 처리한다.

### 제외

- Tarot Death 카드 변경
- result 기반 OnHit 이벤트 공통화
- projectile sync 구조 변경

### 완료 조건

- MagicalGirl projectile/AOE/fusion/fallback damage source가 source kind와 policy를 명시한다.
- visual-only clone이 damage를 적용하지 않는 기존 규칙이 유지된다.
- 지속 장판 tick은 crit을 적용하되 OnHit은 발동하지 않는다.

### 테스트

- MagicalGirl projectile crit 확인
- MagicalGirl AOE tick crit 확인
- AOE slow/pull 유지 확인
- visual-only clone damage 미적용 확인
- Fusion laser/global AOE damage 확인

### 위험도

중간. projectile sync와 visual-only clone은 유지하고 계산 경로만 먼저 바꾼다.

## 7. refactor/combat-tarot-damage-source

### 목표

Tarot Death 카드의 MaxHP 비율 damage를 resolver 경로로 이동하고 crit/OnHit 정책을 명확히 한다.

### 포함

- `TarotCards.DeathCard` damage resolver 경유
- Death 카드 crit 적용
- Death 카드 OnHit 루프 억제
- `TarotContext`에 player stat context 전달

### 현재 구현 상태 (2026-06-27)

- `TarotContext`에 `PlayerStats`를 추가했다.
- `TarotSystem`이 `KhiMeleeComboController` 기준으로 `PlayerStatModifierContainer`를 resolve해 context에 전달한다.
- `DeathCard`의 MaxHP 비율 damage를 `CombatDamageResolver` 경로로 이동했다.
- Death 카드는 `DamageSourceKind.SubEffect`, `OnHitPolicy.SuppressSubEffectLoop`로 분류한다.
- Death 카드의 기존 MaxHP 비율 damage는 이미 최종 base damage 성격이므로 `applyAttackPower: false`로 유지한다.
- 기존 target 필터링과 `Health.Damage` 적용 흐름은 유지한다.

### 제외

- Healing/Reroll 카드 변경
- Tarot 발동 조건 변경
- result 기반 OnHit 이벤트 공통화
- popup 추가

### 완료 조건

- Death 카드 damage가 resolver를 통과한다.
- Death 카드 crit이 player stat 기준으로 적용된다.
- Death 카드 damage가 OnHit 재귀 루프를 만들지 않는다.

### 테스트

- Death 카드 발동 시 적에게 damage 적용 확인
- crit stat이 있을 때 Death 카드 crit 적용 확인
- Tarot 발동 이후 OnHit 루프가 생기지 않는지 확인

### 위험도

낮음~중간. MaxHP 비율 damage에 crit이 추가되므로 밸런스 수치는 올라갈 수 있다.

## 8. refactor/combat-result-onhit-events

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

## 9. refactor/weapon-runtime-entries

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
- MagicalGirl/Fusion/Tarot은 resolver가 안정화된 뒤 source kind별로 편입한다.
