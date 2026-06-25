# LostMemory Combat Refactor Plan

작성일: 2026-06-23
기준 문서: `combat-refactor-diagnosis.md`

## 목표

이 문서는 전투/무기 리팩토링을 실제 코드 작업으로 옮기기 위한 실행 계획이다.

핵심 목표는 다음과 같다.

- 무기 수는 유동적으로 확장 가능하게 한다.
- 인게임에서는 2개 무기를 들고 들어가며, 한 번에 하나만 active 상태로 사용한다.
- 개발/테스트 환경에서는 전체 무기를 빠르게 전환 가능하게 한다.
- 모든 데미지 타입에 크리티컬과 크리티컬 데미지를 동일하게 적용한다.
- DOT도 매 tick마다 host/server가 crit을 확정한다.
- owner client는 입력 즉시 visual을 재생하고, host/server가 최종 damage result를 확정한다.
- Steam 전환을 대비하되, 전투/무기 로직은 Steam 없이도 Unity Editor에서 최대한 테스트 가능해야 한다.

## 핵심 결정사항

| 항목 | 결정 |
| --- | --- |
| 전체 무기 개수 | 유동적, 현재 즉시 대상 5개 |
| 인게임 장착 수 | 2개 |
| active 무기 | 한 번에 1개 |
| 테스트 무기 전환 | 2개 제한 없이 전체 무기 전환 가능해야 함 |
| 크리티컬 | 모든 damage source에 적용 |
| DOT 크리티컬 | 매 tick마다 host/server가 roll |
| 최종 데미지 권위 | host/server |
| owner 입력 반응 | owner client에서 즉시 visual 재생 |
| non-owner 표현 | visual-only result/event 재생 |
| Steam 전환 | NGO 유지, session/transport 계층만 교체 가능하게 분리 |
| 우선 테스트 환경 | Unity Editor, local/editor session |

## 공격 타입 정책표

| DamageSourceKind | 예시 | Crit | Crit 주체 | OnHit | Damage 적용 주체 | Popup 기준 | 비고 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `Melee` | 검 콤보, 단검 기본 공격 | 적용 | host/server | 적용 | host/server | `CombatDamageResult` | owner visual은 즉시 재생 |
| `Projectile` | 활 화살, staff bolt/fireball | 적용 | host/server | 적용 | host/server | `CombatDamageResult` | projectile 이동은 초기 단계에서 기존 owner visual 유지 |
| `Area` | meteor explosion 같은 단발 폭발 | 적용 | host/server | 적용 | host/server | `CombatDamageResult` | 스킬 직접 적중으로 취급 |
| `DamageOverTime` | burn, poison 등 DOT | 매 tick 적용 | host/server | 미발동 | host/server | tick별 `CombatDamageResult` | popup은 합산/throttle 가능 |
| `BeamOrStream` | 화염방사기 direct tick | tick마다 적용 | host/server | source/target별 cooldown 적용 | host/server | tick별 `CombatDamageResult` | 연속 타격형 스킬 전용 절충안 |
| `Summon` | 마법소녀, 소환체 공격 | 적용 | host/server | 적용 가능 | host/server | `CombatDamageResult` | attacker/source owner 추적 필요 |
| `SubEffect` | chain, wind blade, relic 추가타 | 적용 | host/server | 재귀 발동 제한 | host/server | `CombatDamageResult` | 무한 OnHit 루프 방지 필요 |
| `SelfOrEnvironment` | 함정, 자해, 환경 데미지 | 정책별 | host/server | 보통 미적용 | host/server | `CombatDamageResult` | 플레이어 stat을 쓸지 별도 결정 필요 |

## 공통 타입 초안

### DamageSourceKind

```csharp
public enum DamageSourceKind
{
    Melee,
    Projectile,
    Area,
    DamageOverTime,
    BeamOrStream,
    Summon,
    SubEffect,
    SelfOrEnvironment,
}
```

### CriticalPolicy

```csharp
public enum CriticalPolicy
{
    RollEveryDamageTick,
    Never,
}
```

현재 기본값은 `RollEveryDamageTick`이다. 예외가 필요할 때만 `Never`를 사용한다.

### OnHitPolicy

```csharp
public enum OnHitPolicy
{
    Trigger,
    Suppress,
    TriggerWithCooldown,
    SuppressSubEffectLoop,
}
```

- `Trigger`: 일반 OnHit 효과 발동
- `Suppress`: popup/damage는 처리하지만 OnHit 효과는 발동하지 않음
- `TriggerWithCooldown`: 연속 타격형 스킬에서 source/target별 쿨타임이 지났을 때만 OnHit 발동
- `SuppressSubEffectLoop`: 추가타, chain, wind blade처럼 다시 OnHit 루프를 만들면 안 되는 공격

### CombatDamageRequest

```csharp
public struct CombatDamageRequest
{
    public int SequenceId;
    public ulong SourceId;
    public int TickIndex;

    public ulong AttackerNetworkObjectId;
    public ulong TargetNetworkObjectId;

    public DamageSourceKind SourceKind;
    public CriticalPolicy CriticalPolicy;
    public OnHitPolicy OnHitPolicy;

    public float BaseDamage;
    public float DamageMultiplier;
    public bool ApplyAttackPower;
    public bool ApplyFinisherDamage;
    public float OnHitCooldownSeconds;

    public Vector2 HitDirection;
    public Vector3 HitPoint;
    public string WeaponId;
}
```

### CombatDamageResult

```csharp
public struct CombatDamageResult
{
    public int SequenceId;
    public ulong SourceId;
    public int TickIndex;

    public ulong AttackerNetworkObjectId;
    public ulong TargetNetworkObjectId;

    public DamageSourceKind SourceKind;
    public OnHitPolicy OnHitPolicy;
    public float OnHitCooldownSeconds;

    public float FinalDamage;
    public bool WasCritical;
    public bool TargetKilled;

    public Vector2 HitDirection;
    public Vector3 HitPoint;
    public string WeaponId;
}
```

## OnHit 정책

Crit은 모든 damage source에 적용한다. OnHit은 데미지 계산이 아니라 추가 효과 발동 트리거이므로 source kind별로 제한한다.

확정 정책:

- 기본 공격: OnHit 적용
- 일반 스킬 직접 적중: OnHit 적용
- 단발 area skill: OnHit 적용
- DOT tick: OnHit 미발동
- 지속 장판 tick: OnHit 미발동
- 화염방사기/beam/stream: `TriggerWithCooldown`
- SubEffect/유물 추가타: `SuppressSubEffectLoop`

화염방사기 같은 연속 타격형 스킬은 데미지 tick마다 crit을 적용하되, OnHit은 source/target별 cooldown이 지났을 때만 발동한다.

권장 cooldown key:

```text
attackerId + sourceKind + sourceId + targetNetworkObjectId
```

예시:

```text
flamethrower damage tick interval: 0.1s
flamethrower crit: every tick
flamethrower OnHit cooldown: target별 기본 0.5s
```

결과:

```text
0.0s damage + crit + OnHit
0.1s damage + crit
0.2s damage + crit
0.3s damage + crit
0.4s damage + crit
0.5s damage + crit + OnHit
```

전역 OnHit cooldown은 사용하지 않는다. 화염방사기가 OnHit cooldown을 소비해서 검/활/다른 스킬의 OnHit을 막으면 조작감이 이상해질 수 있기 때문이다.
## DOT 처리 계획

DOT는 특수 케이스가 아니라 반복되는 `CombatDamageRequest`로 취급한다.

예시 흐름:

1. owner가 적에게 burn을 부여하는 공격을 발생시킨다.
2. host/server가 burn source를 생성한다.
3. burn source는 고유 `SourceId`를 가진다.
4. tick마다 `TickIndex`를 증가시키며 `CombatDamageRequest`를 생성한다.
5. host/server가 tick마다 crit roll을 수행한다.
6. host/server가 target HP를 변경한다.
7. host/server가 `CombatDamageResult`를 broadcast한다.
8. 클라이언트는 result 기준으로 popup, VFX, SFX를 재생한다.

DOT result 식별자는 다음 조합으로 중복 적용을 막는다.

```text
SourceId + TickIndex + TargetNetworkObjectId
```

주의:

- 클라이언트는 DOT crit을 직접 굴리지 않는다.
- 클라이언트는 DOT final damage를 재계산하지 않는다.
- popup이 과하면 UI 표시만 합산/throttle한다.
- 데미지 계산 자체는 매 tick crit을 유지한다.

## 전투 권위 흐름

### 일반 공격

```text
Owner input
  -> owner immediate visual
  -> RequestAttack / RequestDamage intent to host/server
  -> host/server validate and resolve damage
  -> host/server apply Health.Damage
  -> host/server broadcast CombatDamageResult
  -> owner/non-owner render result popup/VFX/SFX
```

### 원칙

- 랜덤 판정은 host/server만 수행한다.
- 클라이언트는 확정 결과를 표시한다.
- owner는 조작감을 위해 공격 visual을 먼저 재생할 수 있다.
- non-owner는 visual-only event와 result event를 재생한다.
- projectile 충돌까지 완전 host/server 판정으로 옮기는 것은 후속 단계로 둔다.

## 무기 구조 계획

### 목표 구조

```text
WeaponDefinition
  -> 정적 데이터: id, displayName, icon, source kind, prefab/runtime key

WeaponRuntimeEntry
  -> 실제 player prefab에 붙은 controller/presenter/visual 묶음

WeaponLoadout
  -> 인게임에 들고 들어가는 2개 슬롯

WeaponDebugInventory
  -> 테스트 모드에서 전체 무기 전환 가능

ActiveWeaponSlot
  -> 현재 active 무기 1개
```

### 1차 접근

바로 `WeaponModeController`를 제거하지 않는다.

1. 기존 enum 구조 유지
2. `WeaponRuntimeEntry` 개념을 추가해 기존 필드를 감싸기
3. `ApplyMode`의 직접 분기를 entry 순회/선택 방식으로 줄이기
4. 테스트 전환용 debug inventory 추가
5. 이후 enum 의존도를 낮추기

## Session / Steam 전환 대비 계획

전투 코드는 Steam, Relay, Lobby를 몰라야 한다.

추상화 후보:

```csharp
public interface INetworkSessionService
{
    void StartHost();
    void StartClient(string joinCodeOrLobbyId);
    void Leave();
}
```

```csharp
public interface INetworkTransportConfigurator
{
    void ConfigureForHost();
    void ConfigureForClient(string endpointOrLobbyId);
}
```

구현 후보:

- `LocalSessionService`: Editor/local loopback 테스트
- `UnityRelaySessionService`: 현재 Relay 기반
- `SteamSessionService`: 나중 Steam Lobby/P2P 기반

## Phase별 작업 계획

### Phase 0. 설계 고정

목표: 코드 수정 전 정책과 범위를 고정한다.

작업:

- 공격 타입 목록 확정
- damage/crit/OnHit 정책표 확정
- request/result 필드 확정
- 1차 적용 대상 무기 확정

완료 조건:

- 이 문서의 열린 질문이 최소화된다.
- 1차 구현 범위가 정해진다.

### Phase 1. 계산 공통화

목표: 기존 동작을 최대한 유지하면서 damage/crit 계산만 공통 경로로 옮긴다.

작업:

- `CombatDamageRequest` 추가
- `CombatDamageResult` 추가
- `DamageSourceKind`, `CriticalPolicy`, `OnHitPolicy` 추가
- `CombatDamageResolver` 추가
- `CriticalRoller`는 내부 유틸로 유지하거나 resolver에서 호출
- `KhiMeleeComboController`, `KhiBowController`, `KhiStaffController`의 damage 계산을 resolver로 이전

완료 조건:

- 검/활/스태프의 최종 데미지가 기존과 동일하거나 의도된 차이만 발생한다.
- 모든 crit roll이 resolver 경로를 통과한다.

### Phase 2. 누락 공격 보정

목표: 기존에 공통 crit 경로에서 빠진 공격을 편입한다.

작업:

- 단검 텔레포트 도착 공격 crit 적용
- 화염방사기 direct tick crit 적용
- 화염 DOT tick crit 적용
- 메테오 damage result 구조 점검
- 유물 추가타/마법소녀 공격 정책 점검

완료 조건:

- 정책표에 있는 모든 source kind가 resolver를 통과한다.

### Phase 3. Damage 적용 어댑터 추가

목표: `Health.Damage`, `PlayerDamageRelay`, `AttackBroadcast.RelayProjectileDamage` 호출을 한 단계 감싼다.

작업:

- `CombatDamageApplier` 또는 `CombatDamageAuthority` 추가
- solo/host/server/guest owner/visual-only 분기 공통화
- friendly fire 차단 공통화
- result broadcast 진입점 설계

완료 조건:

- 각 무기 컨트롤러가 네트워크 적용 방식을 직접 몰라도 된다.

### Phase 4. 공통 Hit 이벤트

목표: `KhiMeleeComboController.TargetHit` 중심의 OnHit 구조를 공통 result event로 이전한다.

작업:

- `CombatDamageResult` 발생 이벤트 추가
- `OnHitEffectRegistry`가 공통 result를 구독하도록 이전
- `SubEffect` 무한 루프 방지 정책 적용
- damage popup도 result 기반으로 이전 검토

완료 조건:

- 근접 외 공격도 정책에 따라 OnHit 효과를 발동할 수 있다.

### Phase 5. 무기 슬롯/전환 구조 개선

목표: 무기 추가가 enum/코드 수정에 덜 의존하게 한다.

작업:

- `WeaponRuntimeEntry` 추가
- `WeaponLoadout` 2 슬롯 모델 추가
- `WeaponDebugInventory` 추가
- `WeaponModeController.ApplyMode`를 entry 기반으로 단계적 이전

완료 조건:

- 인게임 2개 무기 제한과 테스트 전체 전환이 분리된다.

### Phase 6. Session abstraction

목표: Steam 전환 대비 session/transport 의존성을 분리한다.

작업:

- `INetworkSessionService` 초안
- `LocalSessionService` 초안
- 현재 Relay/session 코드 래핑 계획 수립
- Steam 구현은 아직 만들지 않음

완료 조건:

- 전투 테스트는 Steam 없이 local/editor session으로 가능하다.

### Phase 7. 폴더/이름 정리

목표: `Runtime/TestKhi`의 실제 역할을 반영한다.

작업:

- 이동 후보 결정: `Runtime/PlayerCombat`, `Runtime/Weapons`, `Runtime/Player/Combat`
- namespace 변경 여부 결정
- prefab/script reference 영향 확인
- 안전한 순서로 이동

완료 조건:

- 테스트 코드처럼 보이는 핵심 런타임 코드명이 정리된다.

## 테스트 계획

### Editor 중심 테스트

우선순위는 Editor에서 최대한 검증하는 것이다.

- 단일 Editor: resolver 단위 테스트, 무기 전환, damage 계산 확인
- Multiplayer Play Mode 또는 editor clone: host/client 2인 흐름 확인
- PC 2대 Editor: 필요 시 host/client 권위 흐름 확인
- Build 테스트: Editor에서 잡히지 않는 플랫폼/빌드 차이 확인용
- Steam 테스트: session/transport 계층 통합 검증용 마지막 단계

### 확인 시나리오

- 검 기본 공격 crit 적용
- 활 projectile crit 적용
- staff bolt/fireball crit 적용
- meteor area crit 적용
- dagger teleport crit 적용
- flamethrower direct tick crit 적용
- burn DOT tick별 crit 적용
- owner/non-owner popup 일치
- host/server HP 결과 일치
- visual-only clone이 damage를 적용하지 않는지 확인
- OnHit effect가 의도한 source kind에만 발동하는지 확인

## 열린 질문

아래 항목은 사용자 결정 또는 추가 확인이 필요하다.

1. 화염방사기/Beam 계열 OnHit cooldown 기본값
   - 확정: target별 0.5초
   - 비고: 밸런스 값이므로 이후 0.75초 또는 1.0초로 조정 가능

4. 무기 입력 슬롯 이름
   - 확정: `Primary`, `Secondary`, `Skill1`, `Skill2`
   - 현재 마우스 좌/우클릭 기반을 이 슬롯에 매핑하고, 이후 패드/키보드 입력 확장에 대비한다.

5. 1차 구현 대상
   - 확정: 검, 활, 스태프 계산 공통화부터
   - 이유: 현재 핵심 무기이고 리스크가 상대적으로 명확함

6. 테스트 도구
   - 확인 필요: 현재 Unity 버전에서 Multiplayer Play Mode 사용 가능한지
   - 대안: editor clone 또는 PC 2대 Editor

## 다음 액션 제안

1. 코드에서 현재 damage source 목록을 더 정확히 추출한다.
2. `CombatDamageRequest/Result` 실제 C# 위치를 정한다.
3. Phase 1 구현 범위를 확정한다.
4. 구현 전 git branch를 새로 만든다.



