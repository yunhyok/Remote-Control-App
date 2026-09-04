# Win7 KI-Messenger 10분 실장비 시험

## 준비 (2분)

1. `Remote-Monitor-Master-v0.1.0-win7-net48.zip`의 SHA-256을 `SHA256SUMS.txt`와 비교하고 새 폴더에 압축을 풉니다.
2. KI-Messenger에서 **자기 자신과의 대화창 하나**를 별도 창으로 열고, 입력란을 비웁니다.
3. `RemoteMonitorMaster.exe`를 일반 사용자 권한으로 실행합니다. 제목 표시줄이 `Remote Monitor Master v0.1.0`인지 확인합니다.

## Bind / Arm (3분)

1. `Bind Foreground (3s)`를 누르고 3초 안에 Alt+Tab으로 준비한 자기 대화창을 foreground로 만듭니다.
2. 알림음 뒤 앱으로 돌아와 PID, EXE 경로, 버전, 창 클래스, 서명 정보가 KI-Messenger와 맞는지 확인합니다.
3. 확인 체크박스를 선택하고 `Arm`을 누릅니다.
4. 시험이 끝날 때까지 KI-Messenger 창을 foreground로 만들거나 키보드로 입력하지 않습니다.

`UNSUPPORTED_*` 또는 `DISARMED`가 보이면 우회하지 말고 바로 아래의 로그 회수 단계로 이동합니다.

## 왕복 / 중복 (3분)

1. 휴대폰에서 새 token으로 정확히 `!RM PING win7-a1`을 보냅니다.
2. 5초 안에 같은 창에 `[RM-OUT] PONG win7-a1`이 **한 번만** 오는지 확인합니다.
3. 휴대폰에서 같은 `!RM PING win7-a1`을 다시 보냅니다. PONG이 추가로 오지 않아야 합니다.
4. 새 token `!RM PING win7-a2`를 보내 PONG이 한 번 오는지 확인합니다.

실제 고객명·설계명·파일명·경로는 token에 넣지 마십시오.

## 종료 / 로그 회수 (2분)

1. `Disarm`을 누른 뒤 `Open Log Folder`를 누릅니다.
2. 최신 로그에서 `BIND_OK`, `ARM_COMPLETE`, `PING_CANDIDATE`, `SEND_INVOKED`, 그리고 `POLL_OK`의 `duplicates=1` 이상 또는 실패 reason code를 확인합니다. `SEND_INVOKED`만으로 실제 배달을 판정하지 말고 휴대폰 수신을 기준으로 합니다.
3. 앱을 종료하고 최신 `.log` 한 개만 검토한 뒤 승인된 경로로 개발 PC에 가져옵니다. 저장소/GitHub에는 올리지 마십시오.

## 추가 fail-closed 확인 (선택, 3분)

1. 다시 Bind/Arm한 뒤 KI-Messenger 입력란에 `DO NOT SEND`라는 임시 draft를 직접 입력하고 Remote Monitor Master 창으로 돌아옵니다.
2. 휴대폰에서 새 token PING을 보냅니다. 앱이 `INPUT_NOT_EMPTY`로 Disarm하고 draft와 PONG을 전송하지 않아야 합니다. `AUTOMATION_DRAFT_MAY_REMAIN`이 보이면 KI-Messenger 입력란을 직접 확인해 비운 뒤 시험을 중단합니다.
3. 로그에서 시험 token 원문과 `DO NOT SEND` 원문이 검색되지 않고 SHA-256만 남았는지 확인합니다.

## Go / No-Go

- **PoC 통과:** 두 개의 새 token에 각 PONG 1회, 중복 token에 추가 PONG 0회, 예기치 않은 입력/창 변경 없음.
- **No-Go/수정 필요:** UIA 요소 미노출·중복/모호성, 다른 대화창 입력, 사용자 draft 덮어쓰기, 좌표/OCR 없이는 읽기/전송 불가, 예외·로그 실패·Disarm 후에도 Armed 유지.
