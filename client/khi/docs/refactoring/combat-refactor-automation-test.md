# Combat Refactor Automation Test

작성일: 2026-06-25

## 목적

이번 문서는 전투/무기 리팩토링에서 자동 테스트로 확인할 수 있는 범위와 한계를 정리한다.

현재 `refactor/combat-damage-resolver-weapons` 브랜치는 입력, 충돌 판정, projectile 이동, popup, VFX, 네트워크 relay를 바꾸지 않고 데미지 계산 경로만 `CombatDamageResolver`로 옮겼다.

따라서 이번 단계의 핵심 검증 대상은 플레이 감각이 아니라 계산 결과다.

## 자동화 가능 범위

아래 항목은 자동 테스트로 검증 가능하다.

- 기본 데미지가 그대로 유지되는지
- `AttackPower`가 적용되는지
- `FinisherDamage`가 필요한 경우에만 적용되는지
- crit이 발생했을 때 `CriticalDamage`가 적용되는지
- crit이 발생하지 않았을 때 데미지가 증가하지 않는지
- `CriticalPolicy.Never`일 때 crit이 꺼지는지
- `CombatDamageResult.WasCritical`이 계산 결과와 일치하는지

## 자동화가 어려운 범위

아래 항목은 이번 자동 테스트의 직접 대상이 아니다.

- 공격 입력감
- hitbox 타이밍
- projectile 충돌감
- popup 위치와 표시 감각
- VFX/SFX 타이밍
- host/client 지연 체감

이번 브랜치에서 해당 흐름은 의도적으로 변경하지 않았으므로, 자동 테스트는 계산 경로 동등성에 집중한다.

## 랜덤 crit 테스트 방식

기존 `CriticalRoller`는 `Random.value`를 직접 사용했다.

이 방식은 실제 게임에는 적합하지만, 자동 테스트에서는 매번 결과가 달라질 수 있다.

그래서 테스트용 deterministic overload를 추가했다.

```csharp
CriticalRoller.Roll(stats, baseDamage, rollValue, out wasCritical)
```

`CombatDamageResolver`도 같은 목적의 overload를 가진다.

```csharp
CombatDamageResolver.Resolve(request, stats, criticalRollValue)
```

실제 게임 코드는 기존 overload를 그대로 사용한다.

```csharp
CombatDamageResolver.Resolve(request, stats)
```

즉, production 경로는 여전히 `Random.value`를 사용하고, 테스트에서만 crit roll 값을 고정한다.

## 현재 추가된 테스트

위치:

```text
LostMemory/Assets/_Project/Scripts/Editor/Tests/Combat/CombatDamageResolverAutomation.cs
```

검증 항목:

- `Resolve_WhenCriticalPolicyNever_ReturnsBaseDamage`
- `Resolve_WhenAttackPowerEnabled_AppliesAttackPowerMultiplier`
- `Resolve_WhenFinisherEnabled_AppliesFinisherAfterAttackPower`
- `Resolve_WhenCriticalRollSucceeds_AppliesCriticalDamageBonus`
- `Resolve_WhenCriticalRollFails_DoesNotApplyCriticalDamageBonus`

## 권장 실행 방식

Unity Editor 메뉴에서 실행할 수 있다.

```text
Lost Memory > Tests > Combat Damage Resolver
```

batchmode로 실행할 경우 `-executeMethod`를 사용한다.

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor\Unity.exe" `
  -batchmode `
  -projectPath "C:\ssafy\free_project\S14P31C201\client\LostMemory" `
  -executeMethod LostMemory.Tests.Combat.CombatDamageResolverAutomation.Run `
  -logFile "C:\ssafy\free_project\S14P31C201\client\combat-damage-automation.log" `
  -quit
```

성공 기준:

- Unity process return code가 0이다.
- 로그에 `Passed 5 combat damage resolver checks.`가 출력된다.

실패 기준:

- Unity process return code가 0이 아니다.
- 로그에 `[CombatDamageResolverAutomation] FAIL`이 출력된다.

## 현재 실행 결과

2026-06-25 기준 실행 결과:

```text
[CombatDamageResolverAutomation] PASS CriticalPolicyNever
[CombatDamageResolverAutomation] PASS AttackPower
[CombatDamageResolverAutomation] PASS Finisher
[CombatDamageResolverAutomation] PASS CriticalSuccess
[CombatDamageResolverAutomation] PASS CriticalFail
[CombatDamageResolverAutomation] Passed 5 combat damage resolver checks.
Unity batchmode return code: 0
```

참고:

- 로그에 Rider 경로 관련 `DirectoryNotFoundException`이 출력될 수 있다.
- 로그에 NGO `NetworkAnimator.OnValidate` 관련 `NullReferenceException`이 출력될 수 있다.
- 두 로그는 현재 combat damage resolver 검증 실패가 아니며, Unity process는 return code 0으로 종료했다.

## 이후 확장 후보

- 검 3타 request가 `applyFinisherDamage = true`로 들어가는지 검증
- 활 단발/연사가 `DamageSourceKind.Projectile`로 들어가는지 검증
- 스태프 bolt/fireball이 resolver를 통과하는지 검증
- 메테오가 `DamageSourceKind.Area`로 들어가는지 검증
- DOT 단계에서 tick별 crit과 `OnHitPolicy.Suppress` 검증
- Beam/Stream 단계에서 `OnHitPolicy.TriggerWithCooldown` 검증

## 결론

이번 브랜치의 변경은 계산 경로 리팩토링이므로 자동화 검증이 가능하다.

다만 자동 테스트는 계산 결과를 보장하고, 실제 플레이 감각은 Unity Editor smoke test로 보완한다.
