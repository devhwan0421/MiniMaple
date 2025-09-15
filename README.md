# Mini Maple

UnityClient(C#)와 Sever(C++)을 활용한 오브젝트 이동 및 상태 동기화 데모

## 폴더 구조
```bash
├── Asset
│   ├── Scripts
│   │   ├── GroundCheck.cs       //바닥 감지(Trigger)
│   │   ├── Ladder.cs            //사다리 상호작용(Trigger)
│   │   ├── MiniClient.cs        //네트워크 클라이언트
│   │   ├── Monster.cs           //몬스터 본체
│   │   ├── MonsterAttack.cs     //몬스터 공격 히트박스(Trigger)
│   │   ├── NetworkManager.cs    //네트워크 송수신 래퍼
│   │   ├── Player.cs            //플레이어 본체
│   │   ├── Weapon.cs            //플레이어 공격 히트박스(Trigger)
├── MiniMapleServer
│   ├── MiniMapleServer
│   │   ├── server.cpp           //게임 서버
└── ...
```

## Packet 구조
```bash
uint32 length   // 4 bytes (opcode + payload 길이)
uint16 opcode   // 2 bytes
bytes  payload  // JSON
```

## Opcodes
```bash
OP	이름         방향    설명
1   OP_LOGIN     C→S    로그인
2   OP_LOGIN_OK  S→C    로그인 승인
4   OP_STATE     S→C    플레이어 상태 브로드 캐스트
10  OP_MSTATE    S→C    몬스터 상태 브로드 캐스트
11  OP_HIT       C→S    피격
100 OP_PING      C→S    PING
101 OP_PONG      S→C    PONG
201	OP_CPOSE     C→S    플레이어 상태
```

## 예시 페이로드
```bash
// C→S: OP_HIT 공격 요청
{ "objType": 0, "objId": 1, "dir": 1, "damage": 9 }
```

## 서버 구현 포인트
```bash
-엔디언 고정: 리틀엔디언 헬퍼(write_u32_le, write_u16_le)
-스냅샷 방송: 공유 상태를 벡터로 복사해 락 해제 후 전송
-몬스터 이동/넉백: knockUntilMs 동안 몬스터 넉백, 순찰 일시 정지
-오브젝트 등록/브로드캐스트: 월드 오브젝트 서버 등록 → 틱마다 상태 전송
```

## 클라이언트 구현 포인트
```bash
-보간 정책: 내 플레이어는 반영 x, 원격 플레이어/몬스터는 Vector2.Lerp(pos, targetPos, dt * 15f)
-플레이어 상태 전송: pos/vel/dir/state 변경 감지 또는 N프레임 간격 보고
-사다리: Climb 상태 동안 리지드바디 중력 0, layer 겹침 허용, transform 변경
```
