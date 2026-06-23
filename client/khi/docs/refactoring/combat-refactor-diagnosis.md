# LostMemory Combat Refactor Diagnosis

작성일: 2026-06-23

## 목적

현재 클라이언트 전투/무기 코드는 솔로 플레이와 제한된 무기 수를 전제로 기능이 빠르게 누적된 형태다. 최근 NGO 멀티플레이 작업, 무기 추가, 크리티컬 적용 확장 과정에서 구조적 한계가 드러났고, 이를 리팩토링하기 전 진단 내용을 정리한다.

리팩토링 원칙은 다음과 같다.

- 대상 범위: `client/LostMemory/Assets/_Project/Scripts`
- 우선 대상: 김회인 작업 비중이 높은 파일
- 제외 대상: 서드파티 에셋 코드, 다른 팀원 소유 파일의 불필요한 수정
- 진행 방식: 먼저 진단, 이후 작은 어댑터부터 단계적으로 이전

## 현재 구조 요약

핵심 전투 구현은 이름상 `Runtime/TestKhi`에 있지만, 실제로는 테스트 코드가 아니라 런타임 전투 시스템의 중심이다.

주요 파일은 다음과 같다.

- `Runtime/TestKhi/KhiMeleeComboController.cs`: 검 콤보, 근접 공격 타이밍, 데미지 계산, 크리티컬, hit 이벤트
- `Runtime/TestKhi/KhiMeleeHitbox.cs`: 근접 히트박스 샘플링, Health.Damage 또는 PlayerDamageRelay 호출
- `Runtime/TestKhi/KhiBowController.cs`: 활 입력, 화살 생성, 데미지/크리티컬 계산, visual-only 브로드캐스트
- `Runtime/TestKhi/KhiStaffController.cs`: 스태프 bolt/fireball/meteor 입력, 생성, 데미지/크리티컬 계산, visual-only 브로드캐스트
- `Runtime/TestKhi/KhiArrowProjectile.cs`: 공통 projectile 이동, 충돌, 데미지 적용/relay, impact/destroy 처리
- `Runtime/TestKhi/KhiMeteor.cs`: 메테오 warning/fall/explosion, 범위 데미지, visual-only 처리
- `Runtime/TestKhi/WeaponModeController.cs`: 무기 모드 전환, 무기별 컴포넌트 enable/disable, NetworkVariable sync
- `Runtime/Combat/CriticalRoller.cs`: 크리티컬 확률/피해 계산 유틸
- `Runtime/Networking/Player/AttackBroadcast.cs`: 근접 VFX, 활/스태프 projectile visual-only clone, projectile damage relay
- `Runtime/Networking/Player/PlayerDamageRelay.cs`: 게스트 공격을 서버/호스트에 위임하는 damage relay

## 확인된 핵심 문제

### 1. 무기별 컨트롤러가 너무 많은 책임을 가진다

각 무기 컨트롤러가 입력 처리, 데미지 계산, 크리티컬 계산, projectile/장판 생성, 네트워크 브로드캐스트를 직접 처리한다.

예시:

- 검: `KhiMeleeComboController`가 `AttackPower`, `FinisherDamage`, `CriticalRoller`, hitbox sampling까지 직접 수행
- 활: `KhiBowController`가 화살 생성, 공격력 배율, 크리티컬, projectile broadcast까지 직접 수행
- 스태프: `KhiStaffController`가 bolt/fireball/meteor를 각각 직접 생성하고 각각 별도 broadcast 호출

결과적으로 무기를 추가할 때 공통 흐름 재사용보다 코드 복사가 발생하기 쉽다.

### 2. 크리티컬 적용 시점과 기준이 무기마다 다르다

현재 크리티컬은 공통 유틸 `CriticalRoller`로 계산되지만, 호출 위치는 무기마다 다르다.

- 근접: active frame loop 안에서 hitbox sample 직전 계산
- 활/스태프 projectile: 발사 시점에 계산
- 메테오: Detonate 시점에 계산
- 단검 텔레포트: `ApplyDamageAtArrive`에서 크리티컬 적용이 빠져 있음
- 화염방사기: 직접 damage tick을 적용하며 크리티컬 흐름과 분리됨

이 때문에 "모든 공격에 동일한 치명타 정책을 적용"하기 어렵다.

### 3. 무기 목록이 코드에 고정되어 있다

`WeaponModeController`는 `WeaponMode` enum과 필드가 `Sword`, `Dagger`, `Bow`, `Staff`, `Flamethrower`에 직접 묶여 있다.

새 무기를 추가하려면 보통 다음 변경이 필요하다.

- enum 값 추가
- inspector field 추가
- `ApplyMode` enable/disable 분기 추가
- NetworkVariable mode sync 영향 확인
- visual/presenter/controller 연결 추가

즉 무기 추가가 데이터 작업이 아니라 코드 수정 작업이 된다.

### 4. NGO 멀티 처리가 무기별 예외로 흩어져 있다

현재 멀티플레이는 대체로 "owner가 로컬에서 공격 생성, non-owner는 visual-only clone 생성, 데미지는 relay로 서버/호스트에 위임" 패턴이다.

하지만 이 패턴이 공통 계층으로 추상화되어 있지 않고, 무기별로 흩어져 있다.

- 근접 VFX: `AttackBroadcast`의 attack event relay
- 활: `RelayArrowProjectileSpawn`
- 스태프: `RelayStaffProjectileSpawn`
- projectile damage: `RelayProjectileDamage`
- 근접/메테오/화염 일부: `PlayerDamageRelay`

새 공격 타입이 늘어날수록 네트워크 분기와 visual-only clone 규칙이 반복될 가능성이 높다.

### 5. OnHit 효과가 근접 공격 중심이다

`OnHitEffectRegistry`는 `KhiMeleeComboController.TargetHit`에 구독한다.

따라서 다음 공격들은 공통 OnHit 효과 경로에서 누락되거나 별도 구현이 필요할 수 있다.

- 활 projectile
- 스태프 projectile
- 메테오
- 화염방사기
- 단검 텔레포트 특수공격
- 추후 추가될 소환물/빔/장판류

장기적으로는 "공격이 적중했다"는 공통 이벤트가 근접 전용 컨트롤러가 아니라 전투 판정 계층에서 발생해야 한다.

### 6. `Runtime/TestKhi` 폴더명이 실제 역할과 맞지 않는다

`TestKhi`는 현재 테스트/실험 코드가 아니라 플레이어 전투 시스템의 핵심이다.

이 이름은 유지보수 시 다음 문제를 만든다.

- 실제 사용 여부 판단이 어렵다
- 삭제/정리 대상으로 오해될 수 있다
- 전투 시스템의 경계가 흐려진다

단, namespace 변경은 prefab/script reference 영향이 있으므로 즉시 대규모 이동보다 단계적 정리가 안전하다.

## 추천 리팩토링 방향

### Phase 1. 공통 데미지 요청 모델 추가

먼저 기존 무기를 크게 건드리지 않고 공통 데이터 모델을 만든다.

예상 구조:

- `CombatDamageRequest`
- `CombatDamageResult`
- `DamageSourceKind`
- `AttackSourceContext`

담을 정보:

- attacker
- target
- base damage
- attack power 적용 여부
- finisher 적용 여부
- critical 적용 정책
- range/knockback/source kind
- weapon id 또는 mode
- hit position/direction

목표는 각 무기 컨트롤러가 직접 `CriticalRoller.Roll`과 `Health.Damage`를 호출하지 않고, 공통 요청을 만들게 하는 것이다.

### Phase 2. 데미지 계산 서비스 분리

`CriticalRoller`를 확장하거나 새 `CombatDamageResolver`를 만든다.

책임:

- AttackPower 적용
- FinisherDamage 적용
- Critical/CriticalDamage 적용
- flat/multiplier 계산 정책 통일
- 결과에 `wasCritical`, `finalDamage`, `sourceKind` 포함

이 단계에서 단검 텔레포트, 화염방사기, projectile의 크리티컬 정책을 명시적으로 정리한다.

### Phase 3. 데미지 적용/relay 계층 분리

`Health.Damage`, `PlayerDamageRelay`, `AttackBroadcast.RelayProjectileDamage` 호출을 공통 적용 계층으로 모은다.

목표:

- 솔로: 직접 적용
- host/server: 직접 적용
- guest owner: ServerRpc relay
- visual-only clone: 데미지 적용 금지
- friendly fire 차단 공통화

이 단계가 되면 무기별 컨트롤러는 "어떻게 데미지를 적용할지"를 몰라도 된다.

### Phase 4. 공통 Hit 이벤트 발행

근접 전용 `TargetHit` 대신 모든 공격 타입이 공통 hit 이벤트를 발행하게 한다.

예상 효과:

- OnHit relic/effect가 근접, 활, 스태프, 메테오, 화염방사기에 공통 적용 가능
- damage popup, analytics, sound, hitstop도 같은 경로로 정리 가능
- 공격 종류별 예외는 request/result의 source kind로 분기 가능

### Phase 5. 무기 모드 시스템을 데이터/슬롯 기반으로 이전

`WeaponModeController`의 enum 고정 구조를 바로 없애기보다, 먼저 어댑터를 둔다.

단계:

1. 현재 enum 구조 유지
2. 각 무기를 `WeaponRuntimeEntry` 같은 직렬화 구조로 감싸기
3. `ApplyMode`의 직접 분기를 entry 순회 방식으로 줄이기
4. 이후 무기 추가를 enum 수정 없이 가능한 구조로 이전

### Phase 6. 폴더/이름 정리

기능 이전이 안정화된 뒤 `Runtime/TestKhi`를 실제 의미에 맞게 정리한다.

후보:

- `Runtime/PlayerCombat`
- `Runtime/Weapons`
- `Runtime/Player/Combat`

초기에는 namespace를 유지하고 폴더만 옮기는 편이 안전하다. 이후 prefab/script reference 영향이 명확할 때 namespace까지 정리한다.

## 우선순위 제안

1. `CombatDamageRequest/Result` 추가
2. `CombatDamageResolver` 추가
3. `KhiMeleeComboController`, `KhiBowController`, `KhiStaffController`에서 데미지 계산만 resolver로 이전
4. 단검 텔레포트와 화염방사기의 크리티컬/OnHit 정책 결정
5. `PlayerDamageRelay`/`AttackBroadcast` 주변에 공통 damage application adapter 추가
6. `OnHitEffectRegistry`를 근접 전용 이벤트에서 공통 hit 이벤트로 이전
7. `WeaponModeController`를 slot/entry 기반으로 점진 이전
8. `TestKhi` 폴더명 정리

## 주의할 점

- `AttackBroadcast`와 `PlayerDamageRelay`는 멀티플레이 경계라 한 번에 크게 바꾸면 회귀 위험이 높다.
- Unity prefab/script reference가 얽혀 있으므로 파일 이동/namespace 변경은 마지막에 하는 것이 안전하다.
- 서드파티 에셋과 다른 팀원 소유 파일은 진단/연동 확인용으로 읽되, 수정은 최소화한다.
- 먼저 "계산만 공통화"하고, 그 다음 "적용/네트워크 공통화"로 가는 순서가 안전하다.

## 대화 중 문서 업데이트 규칙

이 문서는 리팩토링 진행 방향에 대한 살아있는 노트로 사용한다.

대화 중 다음과 같은 변화가 생기면 별도 요청이 없어도 업데이트한다.

- 리팩토링 범위가 바뀐 경우
- 우선순위가 바뀐 경우
- 새 문제나 누락된 구조 문제가 발견된 경우
- "이건 하지 않기로 함" 같은 의사결정이 생긴 경우
- 실제 코드 수정 후 현황이 바뀐 경우

단, 새 대화/새 세션에서 이 문서를 계속 자동 갱신하려면 이 파일을 기준 문서로 다시 알려주거나, 현재 작업 맥락이 이어져 있어야 한다.


## Steam / NGO 전환 방향 메모

결론: Steam 출시 또는 Steam 기반 멀티플레이로 전환하더라도 NGO(Netcode for GameObjects)는 유지하는 방향이 좋다. 다만 Unity Relay/Lobby 또는 현재 세션 코드에 직접 묶인 부분은 분리하고, Steam은 transport/session 계층으로 갈아끼우는 구조를 목표로 한다.

역할을 분리하면 다음과 같다.

- 게임 오브젝트 동기화, NetworkObject, NetworkVariable, ServerRpc/ClientRpc: NGO 유지
- 방 생성, 초대, 참가, 친구 기반 매칭: Steam Lobby / Steam Matchmaking으로 교체 후보
- 실제 패킷 전송 경로: Steam Networking Sockets / Steam Datagram Relay 또는 NGO custom/community transport로 교체 후보
- 게임 전투 권위: 현재처럼 host/listen-server 기반 유지, 단 데미지 최종 적용은 host/server authoritative하게 정리

현재 프로젝트에는 전면 dedicated server보다 Steam host 기반 + NGO 유지가 더 적합하다. 이유는 다음과 같다.

- 현재 코드가 이미 host/listen-server 전제의 NGO 구조로 작성되어 있다.
- 협동 PvE/던전형 세션 게임이라면 dedicated server 비용과 운영 복잡도에 비해 이득이 제한적일 가능성이 높다.
- Steam Lobby + Steam Networking을 붙이면 초대/참가 UX와 NAT/릴레이 문제를 비교적 자연스럽게 해결할 수 있다.
- dedicated server는 나중에 필요해질 수 있으므로, 지금 리팩토링에서는 서버 권위 경계를 명확히 만들어 전환 가능성만 열어둔다.

Dedicated server가 더 나은 경우는 다음 조건이 강해질 때다.

- 경쟁/랭킹/보상 구조가 강해서 치팅 방지가 중요해지는 경우
- host 탈주가 게임 경험을 크게 망치는 경우
- host PC 성능이나 핑 편차를 줄여야 하는 경우
- 방이 플레이어 퇴장 이후에도 유지되어야 하는 persistent server 구조가 필요한 경우
- 장기적으로 자동 매칭, 서버 지역 배치, 관전/재접속/검증 로그가 중요해지는 경우

따라서 현재 리팩토링 원칙은 다음으로 확정한다.

- NGO 자체를 걷어내지 않는다.
- `IsOwner`, `IsServer`, `IsHost`, `ServerRpc`, `ClientRpc` 기반 게임 로직은 유지한다.
- 단, session/lobby/transport 세부 구현은 `INetworkSessionService` 같은 별도 계층 뒤로 숨긴다.
- 전투 리팩토링은 Steam 전환을 대비해 damage/visual/session 책임을 분리한다.
- Steam 전환 시 바꿔야 하는 부분이 전투 코드까지 번지지 않도록, 전투 코드는 "연결 방식"이 아니라 "권위 주체"만 알게 한다.

## 추가 계획과 필요한 결정

### 1. 네트워크 계층 분리 계획

현재 `RelaySession`, lobby/join code, transport 설정, host/client 시작 로직이 Unity 서비스 또는 현재 구현에 직접 묶여 있을 가능성이 있다. Steam 전환을 대비하려면 다음 인터페이스를 먼저 구상한다.

- `INetworkSessionService`: 방 생성, 참가, 나가기, 세션 상태 이벤트
- `INetworkTransportConfigurator`: host/client 시작 전 transport 주소/ID 설정
- `INetworkIdentityProvider`: SteamID, local client id, 표시 이름, 인증 토큰 등 추상화
- `INetworkInviteService`: Steam 초대/친구 참가/로비 콜백 추상화

이 계층은 전투 리팩토링과 별도지만, 전투 코드가 session 구현에 의존하지 않게 만드는 기준점이다.

### 2. 전투 권위 모델 결정

우선 권장안은 host/server authoritative damage다.

- owner client: 입력과 예측 가능한 visual 시작
- host/server: 최종 hit 판정 또는 최종 damage 적용
- non-owner clients: visual-only 재생
- 모든 치명타/데미지 결과: 가능하면 host/server에서 확정하거나, 최소한 확정 결과를 broadcast

현재는 일부 공격에서 owner가 먼저 damage/crit을 계산하고 relay한다. Steam/dedicated 전환 가능성을 생각하면 장기적으로는 `CombatDamageRequest`를 host/server가 검증하고 `CombatDamageResult`를 뿌리는 형태가 더 좋다.

### 3. 무기 시스템 결정

무기를 enum으로 추가하는 방식은 계속 커질수록 부담이 된다. 다음 결정을 해야 한다.

- 무기는 run 중 몇 개까지 가질 수 있는가?
- 동시에 장착 가능한 슬롯은 몇 개인가?
- 무기 변경은 플레이어 입력만 가능한가, 아이템/유물/이벤트도 가능한가?
- 무기별 입력은 공통 입력 슬롯(Primary/Secondary/Skill1/Skill2)으로 통일할 것인가?
- 무기별 projectile/area/summon/beam은 공통 attack source로 볼 것인가?

권장 방향은 `WeaponMode enum` 중심에서 `WeaponRuntimeEntry` 또는 `WeaponDefinition` 기반으로 천천히 이동하는 것이다.

### 4. 치명타/OnHit 정책 결정

다음 공격 타입에 대해 치명타와 OnHit 효과 적용 여부를 명시해야 한다.

- 기본 근접 콤보
- 활 단발/연사
- 스태프 bolt/fireball/meteor
- 단검 텔레포트 도착 공격
- 화염방사기 direct tick
- 화염 DOT
- 유물/소환물/마법소녀 보조 공격

권장안은 공격마다 `DamageSourceKind`와 `CriticalPolicy`, `OnHitPolicy`를 부여하는 것이다.

예시:

- `Melee`: crit 적용, OnHit 적용
- `Projectile`: crit 적용, OnHit 적용 가능
- `Area`: crit 적용 여부 선택, OnHit 제한 적용
- `DamageOverTime`: 매 tick crit 적용, OnHit는 별도 정책으로 제한 가능
- `Summon/SubEffect`: 별도 정책

### 5. 바로 다음 작업 후보

가장 안전한 다음 작업은 코드 동작을 바꾸지 않는 설계 파일 추가다.

1. `combat-refactor-diagnosis.md`를 기준 문서로 유지
2. 별도 `combat-refactor-plan.md` 작성
3. `CombatDamageRequest/Result` 초안 설계
4. 현재 공격 타입별 damage/crit/OnHit 정책 표 작성
5. 그 다음에야 실제 코드에 resolver를 추가

이 순서가 좋은 이유는 멀티플레이 경계가 복잡해서, 먼저 정책을 정하지 않고 코드부터 바꾸면 각 무기별 예외가 다시 생기기 때문이다.

## 2026-06-23 결정 업데이트: 무기/크리티컬/테스트 방향

### 무기 개수와 장착 정책

현재 기준 총 무기 수는 5개지만, 장기적으로는 유동적으로 늘어날 수 있다. 따라서 무기 시스템은 고정 enum 중심보다 데이터/entry 기반 구조를 목표로 한다.

현재 결정:

- 전체 무기 풀: 유동적
- 현재 즉시 대상: 5개
- 인게임에서 플레이어가 들고 들어가 사용할 무기 수: 2개
- 실제 사용 상태: 한 번에 하나의 무기만 active
- 테스트 요구: 개발/테스트 환경에서는 2개 제한을 풀고 여러 무기를 빠르게 전환 가능해야 함

권장 구조:

- `WeaponDefinition`: 무기 정적 데이터, 표시 이름, 아이콘, controller prefab 또는 runtime entry key
- `WeaponRuntimeEntry`: 실제 플레이어 prefab에 붙은 controller/presenter/visual 묶음
- `WeaponLoadout`: 인게임에 들고 들어가는 2개 슬롯
- `WeaponDebugInventory`: 테스트용 전체 무기 전환 목록
- `ActiveWeaponSlot`: 현재 active 무기 1개

즉, 게임 규칙은 "2개 장착, 1개 사용"이고, 개발 편의는 "테스트 모드에서 전체 무기 전환 가능"으로 분리한다.

### 크리티컬 정책

모든 공격에 크리티컬과 크리티컬 데미지를 동일하게 적용한다.

현재 결정:

- 기본 근접 콤보: 적용
- 활 단발/연사: 적용
- 스태프 bolt/fireball/meteor: 적용
- 단검 텔레포트 도착 공격: 적용
- 화염방사기 direct tick: 적용
- 화염 DOT: 매 tick마다 크리티컬과 크리티컬 데미지 적용
- 유물/소환물/보조 공격: 적용

권장 구현:

- `CombatDamageResolver`에서 모든 damage source가 동일한 critical 계산을 통과하게 한다.
- DOT는 매 tick마다 host/server에서 crit을 굴린다. 단, popup/사운드는 과도하게 튀지 않도록 합산 표시나 표시 빈도 제한을 둘 수 있다.
- 기본 정책은 `CriticalPolicy.RollEveryDamageTick`이다. 예외가 필요한 경우에만 명시적으로 다른 policy를 둔다.

### 전투 권위 모델 추천

추천안은 "host/server authoritative damage, owner responsive visual"이다.

역할:

- owner client: 입력 처리, 즉시 시각 효과 시작, 조작감 유지
- host/server: 최종 데미지 계산/적용 권위
- non-owner client: visual-only 재생
- 모든 damage result: host/server에서 확정된 결과를 기준으로 popup, OnHit, death, analytics 처리

이 방식이 좋은 이유:

- 지금 host 기반 NGO 구조와 가장 잘 맞는다.
- Steam host 기반으로 바꿔도 전투 구조를 크게 바꾸지 않아도 된다.
- 나중에 dedicated server로 확장할 때도 `host/server` 자리에 dedicated server가 들어갈 수 있다.
- 치명타, 유물 효과, 적 사망, 보상 같은 결과가 클라이언트마다 달라지는 문제를 줄인다.

주의점:

- hit 판정까지 전부 host/server에서 하면 입력 반응이 둔해질 수 있다.
- 초기 단계에서는 owner가 공격 visual을 즉시 재생하고, host/server가 damage result를 확정하는 절충안이 좋다.
- projectile 위치/충돌까지 완전 서버 권위로 옮기는 것은 다음 단계로 미룬다.

구체 흐름:

1. owner client가 입력을 받으면 즉시 공격 visual을 재생한다.
2. 공격 요청에는 `sequenceId`, `weaponId`, `sourceKind`, `inputFrame/time`, `aim`, `origin`을 포함한다.
3. host/server가 데미지와 크리티컬을 확정한다.
4. host/server가 `CombatDamageResult`를 broadcast한다.
5. owner는 이미 재생한 visual에 확정 결과를 얹고, non-owner는 visual-only와 결과 이벤트를 재생한다.

DOT 흐름:

- DOT도 매 tick `CombatDamageRequest`를 만든다.
- 각 tick에는 `sourceId`, `tickIndex`, `targetNetworkObjectId`를 포함한다.
- host/server가 tick마다 crit을 굴리고 최종 데미지를 적용한다.
- 클라이언트는 확정된 tick result만 popup/피격 연출에 사용한다.
- popup이 과하면 데미지 계산은 매 tick 유지하되 UI만 합산 또는 throttle한다.

### Steam 전환 테스트 전략

Steam으로 바꾼다고 해서 테스트가 불가능해지는 것은 아니다. 다만 현재 Unity Relay처럼 "내가 Relay 서버를 띄우면 끝"인 느낌과는 다르게, 테스트 층을 나눠야 한다.

권장 테스트 단계:

1. Local/Editor 테스트
   - Steam 없이 기존 Unity Transport 또는 local loopback으로 NGO 전투/무기 로직 테스트
   - `INetworkSessionService`의 `LocalSessionService` 구현 사용
   - Unity Editor에서 host/client 흐름을 최대한 재현한다.
   - 가능하면 Multiplayer Play Mode 또는 editor clone 방식으로 빌드 없이 2인 테스트를 먼저 수행한다.
   - 전투 리팩토링의 대부분은 이 단계에서 검증 가능해야 함

2. Editor + 보조 인스턴스 테스트
   - 1차 목표는 빌드 전 Unity Editor 중심으로 host/client 권위 흐름을 검증하는 것
   - 한 PC에서 editor clone이 가능하면 먼저 사용하고, 필요하면 PC 2대를 사용한다.
   - Steam 문제가 아니라 게임 로직 문제가 없는지 확인하는 단계다.

3. LAN 또는 direct transport 테스트
   - Steam 연동 전, 필요할 때만 host/client 빌드로 NGO 기본 연결과 host 권위 흐름 검증
   - 이 단계는 Editor 테스트로 잡히지 않는 빌드/플랫폼 차이를 확인하는 보조 단계다.

4. Steam 개발 AppID 테스트
   - Steamworks 예제 SpaceWar는 예제 AppID 480을 사용한다.
   - 개발 중에는 `steam_appid.txt`와 Steam 클라이언트 실행이 필요하다.
   - 실제 게임 AppID가 나오기 전에는 480/개발 AppID 기반으로 Steam 초기화, Lobby, 친구 초대, P2P 연결을 검증할 수 있다.

5. Steam 계정 2개 또는 PC 2대 테스트
   - Steam P2P/Lobby는 같은 Steam 계정으로 중복 실행 테스트가 제한될 수 있다.
   - 현실적인 검증은 계정 2개, PC 2대, 또는 팀원 PC와 함께 진행하는 방식이 안정적이다.

6. Steam Transport만 교체한 통합 테스트
   - 게임 전투 코드는 그대로 두고, session/transport 구현만 Steam으로 바꿔 테스트한다.
   - 이 단계에서 문제가 생기면 전투 코드가 아니라 session/transport 계층에서 추적 가능해야 한다.

핵심 리스크와 대응:

- 리스크: Steam 전환 후 버그가 전투 문제인지 Steam 연결 문제인지 구분이 어려움
- 대응: 전투/무기 로직은 Steam 없이도 테스트 가능한 local/editor session을 유지한다.
- 리스크: Steam Lobby/P2P는 계정/클라이언트 제약이 있음
- 대응: Steam 통합 테스트는 별도 체크리스트와 계정/빌드 준비를 둔다.
- 리스크: Relay에서 Steam으로 바꾸는 순간 전체가 흔들림
- 대응: `INetworkSessionService`를 먼저 만들고 Unity Relay 구현과 Steam 구현을 교체 가능하게 둔다.

현재 결론:

- 지금 바로 Steam으로 바꾸지 않는다.
- 하지만 지금부터 Steam 전환 시 테스트 가능하도록 session/transport 추상화를 만든다.
- 전투 리팩토링은 Steam 없이도 local/editor/NGO 테스트가 가능해야 한다.
- Steam 통합은 마지막에 별도 단계로 진행한다.

