# Combat Damage Source Audit

작성일: 2026-06-25

## 목적

`CombatDamageResolver` 적용 이후에도 남아 있는 damage source를 다시 확인한다.

이번 감사의 목적은 리팩토링 범위를 무작정 넓히는 것이 아니라, 다음 작업 순서를 조정하는 것이다.

핵심 기준:

- 플레이어/무기/아이템/스킬/소환수/장판/DOT가 적에게 주는 데미지만 resolver 편입 후보로 본다.
- 적이나 보스가 플레이어에게 주는 데미지는 이번 combat damage resolver 범위에서 제외한다.
- 이미 resolver로 최종 데미지를 계산한 뒤 `Health.Damage(...)`로 적용하는 코드는 누락으로 보지 않는다.
- 테스트 트랩, 다운 처리, 즉사성 디버그 데미지는 우선순위 낮음으로 분류한다.

## 결론

누락 후보는 존재한다.

하지만 이것 때문에 현재 리팩토링 범위를 전면 확대할 필요는 없다. 대신 DOT 구현 전에 audit checkpoint를 추가하고, 이후 특수 source를 별도 브랜치로 분리하는 것이 안전하다.

우선순위:

1. Burn DOT
2. Parry 반격 데미지
3. MagicalGirl projectile/AOE/fusion/fallback 데미지
4. Tarot Death 카드
5. 기타 테스트/디버그성 damage source

## 이미 resolver 경로로 이동한 source

아래 source는 계산 경로가 `CombatDamageResolver`를 통과한다.

- `KhiMeleeComboController`: 검 기본 콤보
- `KhiBowController`: 활 단발/연사 발사 시점 damage
- `KhiStaffController`: bolt/fireball 발사 시점 damage
- `KhiMeteor`: 메테오 폭발 damage
- `KhiDaggerTeleportController`: 단검 도착 공격 damage
- `KhiFlameZone`: 화염방사기 direct tick damage
- `OnHitEffectRegistry.ApplyChain`: chain 추가타
- `OnHitEffectRegistry.ApplyWindBlade`: wind blade 추가타

주의:

- projectile이나 hitbox 내부에서 `Health.Damage(...)`를 호출하는 것은 적용 단계다.
- 발사 시점이나 공격 시작 시점에 resolver가 이미 최종 damage/crit을 확정한 경우, 적용 단계의 `Health.Damage(...)`는 누락으로 보지 않는다.

## 누락 후보 상세

### 1. Burn DOT

파일:

- `Runtime/Combat/OnHitEffectRegistry.cs`
- `Runtime/Enemies/EnemyStatusEffect.cs`
- `Runtime/TestKhi/KhiFlameZone.cs`

현재 흐름:

```text
OnHitEffectRegistry.ApplyBurn
-> EnemyStatusEffect.ApplyBurn(damagePerTick, duration, instigator)
-> EnemyStatusEffect.Update
-> _health.Damage(_burnDamagePerTick, _burnInstigator, ...)
```

문제:

- tick마다 `CombatDamageResolver`를 통과하지 않는다.
- tick마다 crit roll을 하지 않는다.
- host/server authoritative result 구조가 아직 없다.
- popup/crit 표시가 없다.

결정:

- 이번 브랜치의 구현 대상.
- DOT는 매 tick crit 적용.
- DOT는 OnHit 미발동.
- 이용호가 추가한 `KhiPlayerActionGate.IsBlocked` 보호 로직은 유지한다.
- popup/result event 공통화는 후속 `refactor/combat-result-onhit-events`로 넘긴다.

구현 상태 (2026-06-25):

- `EnemyStatusEffect`의 burn tick damage가 `CombatDamageResolver`를 통과한다.
- `DamageSourceKind.DamageOverTime`, `OnHitPolicy.Suppress`, `tickIndex`가 request에 포함된다.
- 기존 호출부가 넘기는 `damagePerTick` 의미를 유지하기 위해 `applyAttackPower: false`로 처리한다.
- `CombatDamageResolverAutomation`에 DamageOverTime 정책 검증 케이스를 추가했다.

권장 구현 방향:

- `EnemyStatusEffect.ApplyBurn`에 source metadata를 추가하거나 내부 상태로 보관한다.
- tick마다 `CombatDamageRequest`를 만든다.
- `DamageSourceKind.Dot`, `OnHitPolicy.Suppress`, `tickIndex`를 사용한다.
- host/server가 damage result를 확정하는 방향으로 확장 가능하게 만든다.

### 2. Parry 반격 데미지

파일:

- `Runtime/TestKhi/KhiParryDamageOnTouch.cs`

현재 흐름:

```text
KhiParryDamageOnTouch.ApplyReducedDamage
-> CriticalRoller.Roll(...)
-> PlayerDamageRelay.RelayDamage(...) 또는 Health.Damage(...)
```

문제:

- crit은 적용되지만 `CombatDamageResolver`를 통과하지 않는다.
- `DamageSourceKind`와 `OnHitPolicy`가 없다.
- relay/apply 흐름이 직접 구현되어 있어 result event로 연결하기 어렵다.

권장 분류:

- `DamageSourceKind.ParryCounter` 또는 `DamageSourceKind.Counter`
- `OnHitPolicy.Trigger`

구현 상태 (2026-06-26):

- `DamageSourceKind.Counter`를 추가했다.
- `KhiParryDamageOnTouch`의 직접 `CriticalRoller.Roll(...)` 호출을 `CombatDamageResolver.Resolve(...)`로 교체했다.
- 기존 감쇠/보호막 처리 이후의 damage 값을 보존하기 위해 `applyAttackPower: false`로 처리한다.
- relay/apply/popup 흐름은 유지했다.

권장 순서:

- Burn DOT 이후 별도 특수 source 브랜치에서 처리한다.

### 3. MagicalGirl projectile/AOE/fusion/fallback

파일:

- `Runtime/MagicalGirl/MagicalGirlAI.cs`
- `Runtime/MagicalGirl/MagicalGirlProjectile.cs`
- `Runtime/MagicalGirl/MagicalGirlAOE.cs`
- `Runtime/MagicalGirl/MagicalGirlFusion.cs`

현재 흐름:

- `MagicalGirlProjectile`: 발사체 초기화 시 `CriticalRoller.Roll(...)` 직접 호출
- `MagicalGirlAOE`: 매 tick/target마다 `CriticalRoller.Roll(...)` 직접 호출
- `MagicalGirlAI` fallback: resolver/crit 없이 `target.Damage(...)` 직접 호출
- `MagicalGirlFusion` laser/AOE: resolver/crit 없이 `Health.Damage(...)` 직접 호출

문제:

- 일부는 crit을 직접 굴리지만 resolver 경로가 아니다.
- 일부는 crit 자체가 없다.
- source kind, tick index, on-hit policy가 없다.
- fusion laser는 beam/stream 성격이고, AOE는 one-shot/global area 성격이다.

권장 분류:

- projectile: `DamageSourceKind.Projectile`
- AOE tick: `DamageSourceKind.Area` 또는 `DamageSourceKind.Dot` 성격. OnHit은 suppress 권장.
- fusion laser: `DamageSourceKind.BeamOrStream`, OnHit cooldown 필요 여부 별도 결정
- fusion global AOE: `DamageSourceKind.Area`

권장 순서:

- Burn DOT 이후 별도 브랜치로 분리한다.
- MagicalGirl은 VFX, projectile sync, visual-only clone, owner action gate가 엮여 있으므로 DOT와 한 브랜치에 섞지 않는다.

### 4. Tarot Death 카드

파일:

- `Runtime/Tarot/TarotCards.cs`

현재 흐름:

```text
DeathCard.Activate
-> target.MaximumHealth * damageRatio
-> h.Damage(...)
```

문제:

- resolver/crit을 통과하지 않는다.
- MaxHP 비율 데미지라 일반 weapon base damage와 성격이 다르다.

정책 결정 필요:

- "모든 damage source crit 적용" 원칙을 따르면 Death 카드도 crit 대상이다.
- 다만 MaxHP 비율 데미지에 crit이 붙으면 밸런스 영향이 크다.

권장:

- 현재 DOT 브랜치에서는 건드리지 않는다.
- 특수 source 브랜치에서 `CriticalPolicy`를 명시적으로 결정한다.
- 기본값은 사용자 정책에 따라 crit 적용 후보로 두되, 밸런스 검토 항목으로 표시한다.

### 5. 제외 대상

아래는 검색에 잡히지만 이번 resolver 리팩토링 대상이 아니다.

- `Runtime/Enemies/**`: 적/보스가 플레이어에게 주는 데미지
- `Runtime/Combat/Telegraph/**`: 주로 적/보스 telegraph damage
- `Runtime/Networking/Player/PlayerDamageRelay.cs`: damage 적용/relay 인프라
- `Runtime/Networking/Player/AttackBroadcast.cs`: projectile damage apply relay 인프라
- `KhiDownController`의 즉사성 처리
- `TestKhiDamageTrap` 같은 테스트 트랩

## 소유권 메모

`EnemyStatusEffect.cs`는 현재 `git blame`만 보면 송주헌 작성으로 보인다.

하지만 확인 결과 송주헌의 `[infra] 배포 환경 준비` 커밋에서 `Enemies` 폴더와 에셋/코드가 대량으로 현재 히스토리에 재추가되면서 blame이 덮인 것이다.

기능 이력 기준:

- 최초 추가와 주요 기능 구현은 김회인 커밋에 있다.
- 이용호는 burn instigator block 보호 로직을 추가했다.
- 송주헌 커밋은 내부 기능 수정이라기보다 대량 재추가/infra성 커밋으로 보는 것이 맞다.

따라서 DOT 리팩토링에서 `EnemyStatusEffect.cs`를 수정하는 것은 가능하다고 판단한다. 단, 이용호가 추가한 `IsBurnInstigatorBlocked()` 보호 로직은 보존해야 한다.

## 조정된 진행 방향

기존 계획을 폐기하지 않는다.

대신 `refactor/combat-dot-damage-source` 브랜치의 첫 작업으로 이 audit을 완료하고, 구현 범위를 Burn DOT로 제한한다.

권장 브랜치 흐름:

1. `refactor/combat-dot-damage-source`
   - Burn DOT만 resolver 경유
   - DOT tick crit 적용
   - DOT OnHit suppress
   - `EnemyStatusEffect` 책임 최소 변경
2. `refactor/combat-special-damage-sources`
   - Parry 반격
   - MagicalGirl projectile/AOE/fusion
   - Tarot Death 카드 정책 결정
3. `refactor/combat-result-onhit-events`
   - result event 기반 OnHit/popup 정리

## 다음 구현 전 확인할 것

- Burn DOT tick popup을 이번 브랜치에서 바로 표시할지, result event 브랜치로 넘길지
- DOT crit 결과를 어디까지 시각화할지
- `EnemyStatusEffect.ApplyBurn` 시그니처를 직접 확장할지, 별도 context struct를 만들지
- host/server authoritative를 이번 브랜치에서 실제 적용할지, 우선 solo/local 구조에서 resolver 경유만 보장할지
