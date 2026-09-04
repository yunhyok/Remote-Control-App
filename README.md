# Remote Monitor Master v0.1.0

> Status: **Win7 실장비 미검증 진단 PoC**. 완성품이나 원격제어 도구가 아닙니다.

Windows 7 + .NET Framework 4.8에서 사용자가 직접 연 KI-Messenger 자기 대화창 하나가 UI Automation을 충분히 노출하는지 확인합니다.

- 입력: `!RM PING <token>`
- 출력: `[RM-OUT] PONG <token>`
- 동일 token은 로컬 SHA-256 기록을 사용해 재시작 후에도 한 번만 처리합니다.

## 안전 경계

이 프로그램은 좌표 클릭, OCR, SendKeys, 대화방 탐색, 관리자 권한, Slave, 인트라넷 제어, LLM, 자연어 명령을 구현하지 않습니다. UIA `TextPattern`, `ValuePattern`, `InvokePattern`만 사용합니다.

Bind 시 foreground 창의 PID/HWND, 프로세스 시작 시각, 실행 파일 경로·버전·길이·수정 시각, offline `WinVerifyTrust` 결과와 Authenticode 인증서 정보, 창 클래스, window/chat header와 transcript/composer/input/send UIA identity를 저장합니다. 각 읽기와 쓰기 단계 직전에 전부 다시 찾고 비교합니다. 요소가 0개/여러 개이거나 identity가 바뀌면 즉시 Disarm하고 `Invoke`하지 않습니다. UIA 요소와 message body/envelope는 모두 bound window와 같은 PID이고 visible이어야 합니다.

평면 transcript 문자열은 명령으로 읽지 않습니다. `AutomationId`/`ClassName`에 의미가 명시된 하나의 non-root chat container가 transcript와 별도의 선택 대화 header를 포함해야 합니다. input과 Send는 그보다 안쪽의 유일한 semantic composer를 공유해야 하므로 `messageSearch` 같은 decoy edit는 거부합니다. 명시적 text-message envelope마다 UIA `TextPattern` body가 정확히 하나일 때만 검사하며 Image/Hyperlink/Button/Invoke형 말풍선과 file/attachment metadata는 제외합니다. 이 구조를 확인할 수 없으면 `UNSUPPORTED_*` reason code를 남기고 Arm하지 않습니다.

한 Windows 세션에서는 버전과 무관하게 인스턴스 하나만 실행됩니다. token 예약은 프로세스 간 mutex와 디스크 재확인 후 전송 전에 기록되어 at-most-once로 동작합니다. Armed 상태에서는 bound messenger 창을 foreground로 만들거나 대화를 전환하거나 입력하지 마십시오. 쓰기 직전 bound 프로세스가 foreground이면 draft 보호를 위해 Disarm합니다. `SetValue` 뒤 실패하면 앱은 정확히 자신이 쓴 문자열일 때만 비우고 다시 읽어 empty를 확인합니다. 안전하게 비우지 못하면 `AUTOMATION_DRAFT_MAY_REMAIN`, Disarm 전에 Invoke가 이미 시작됐으면 `SEND_COMMIT_ALREADY_STARTED`를 크게 표시합니다.

## 빌드와 패키지

개발 PC에서:

```powershell
.\scripts\build-package.ps1
```

결과:

- `dist\Remote-Monitor-Master-v0.1.0-win7-net48.zip`
- `dist\SHA256SUMS.txt`

실행 패키지에는 EXE, .NET 설정 파일, 이 README와 [10분 실장비 절차](WIN7-TEST.md)만 들어갑니다. 대상 PC에는 .NET Framework 4.8이 이미 설치되어 있어야 합니다. EXE는 현재 코드 서명되지 않았습니다.

최소 self-check만 다시 실행하려면:

```powershell
$p = Start-Process .\src\RemoteMonitorMaster\bin\Release\net48\RemoteMonitorMaster.exe -ArgumentList --self-test -Wait -PassThru
$p.ExitCode
```

`0`이면 strict parser, echo 제외, persistent duplicate 차단이 통과한 것입니다.

## 로그와 진단 회수

UI에 현재 로그 파일 경로가 항상 표시됩니다. `Open Log Folder`를 누르면 다음 폴더가 열립니다.

```text
%LOCALAPPDATA%\RemoteMonitorMaster\logs
```

로그에는 UTC/로컬 시각, 앱/버전, OS/CLR/.NET Framework release, 프로세스·파일·서명·HWND·창 클래스, UIA 요소의 길이·ControlType·패턴과 실행마다 키가 바뀌는 HMAC fingerprint, 상태 전이, poll/read/write, 새 PING 후보 hash, poll별 중복 개수, 실패 reason code가 기록됩니다. 전체 대화, raw command token, raw non-command text, raw Name/AutomationId/ClassName, 첨부 파일명, 이미지, 스크린샷은 기록하지 않습니다.

진단 파일을 개발 PC로 가져올 때:

1. 앱에서 `Disarm`을 누르고 종료합니다.
2. `Open Log Folder`에서 가장 최근 `.log` 하나만 복사합니다.
3. 메모장으로 열어 사내 경로·서명 주체 등 공유 금지 정보가 없는지 확인합니다.
4. 저장소에 추가하지 말고 승인된 개인 전달 경로로만 전달합니다.

token hash 상태는 `%LOCALAPPDATA%\RemoteMonitorMaster\state\seen-tokens.txt`에 있으며 로그가 아닙니다. 새 시험을 위해 같은 token을 재사용하지 말고 매번 새 token을 쓰십시오.

## 현재 한계

- KI-Messenger의 실제 UIA tree와 Win7 동작은 아직 확인하지 않았습니다.
- KI-Messenger가 immutable conversation ID를 노출하는지도 확인하지 못했으므로 수동 self-chat 확인이 현재 trust boundary입니다. Armed 중 대화 전환은 No-Go입니다.
- 잠금 화면, 다른 사용자 세션, 권한이 다른 프로세스에서는 동작 대상으로 삼지 않습니다.
- 메신저 서명은 네트워크 조회 없이 Windows `WinVerifyTrust`로 확인하므로 revocation 최신성은 검증하지 않습니다. 이 PoC EXE 자체의 코드 서명은 이번 범위가 아닙니다.
- `InvokePattern.Invoke` 성공은 전송 동작 호출만 뜻하며 실제 메시지 배달 확인은 아닙니다. 10분 시험에서 휴대폰의 실제 수신을 확인해야 합니다.
- ValuePattern/InvokePattern은 compare-and-set 원자성을 제공하지 않습니다. 그래서 Armed 중 messenger 창을 독점적으로 사용하지 않는 환경은 No-Go입니다.
- UIA 구조가 바뀌면 정상적인 fail-closed Disarm이 발생할 수 있습니다.
