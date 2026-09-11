# Remote Monitor v0.1.42 — 로컬 LLM 통합 확인

> 보관용 이전 절차입니다. v0.1.43부터 현장 시험은 [SLAVE-TEST.md](SLAVE-TEST.md)만 따릅니다. 아래 Master·모바일 시험은 반복하지 않습니다.

이번에는 Output 객체 탐색 재시험이 아닙니다. **PowerSI 화면 → 같은 PC의 LM Studio → 기존 텍스트 요약 답장**을 한 번에 확인합니다.

## 처음 설정

1. 기존 Master/Slave를 종료하고 새 ZIP을 새 폴더에 풉니다. Win7은 `RemoteMonitorMaster.exe`, Win11은 `RemoteMonitorSlave.exe`를 실행합니다. 각각 EXE와 해당 `.exe.config`를 함께 두고 **v0.1.42**를 확인합니다. 연결 정보가 같으면 기존 연결파일을 그대로 사용합니다.
2. PowerSI가 있는 Win11 워크스테이션에서 LM Studio **0.4.0 이상**을 실행하고 로컬 이미지 지원 모델을 로드합니다. 현재 Gemma를 그대로 선택할 수 있으며 모델명을 프로그램에 고정하지 않았습니다. Developer에서 **Start server**, 기본 포트 **1234**를 사용합니다. LM Link/원격 모델은 사용하지 않습니다.
3. Slave의 **LM Studio 설정** → **로드된 모델 확인** → 사용할 모델 선택 → **PowerSI 화면의 로컬 LLM 판독 사용** 체크 → 저장합니다. 토큰은 LM Studio에 설정했을 때만 입력합니다. 모델 확인이 안 되면 서버·버전·이미지 지원/모델 로드 상태부터 확인합니다.
4. 실제 PowerSI의 Output과 하단 상태줄을 보이게 두고 최소화하지 않습니다. 시뮬레이션 재실행/중단, 패널 위치 고정, 마우스 오버는 필요 없습니다.

## 한 번의 확인

1. 기존 방식대로 Slave 수신과 Master의 개별 자기 대화창 인식을 준비합니다. READY 후 휴대폰에서 **`pwrsi` 한 번만** 보냅니다.
2. Slave의 경과 시간과 완료 표시를 봅니다. 기본 모델 제한은60초이며, 캡처/전송 시간이 추가됩니다. 이 모델/PC의 실제 속도는 아직 측정하지 않았습니다. 기본 설정에서 약75초가 지나도 Slave가 끝나지 않으면 **Slave Stop**, Master도 Stop하고 로그를 회수합니다. 설정을90초로 올렸다면 전체115초를 넘겨 기다리지 않습니다.
3. 휴대폰에 `VISION MODEL_READ`와 캡처 UTC 시각이 포함된 텍스트 답장이 오는지 확인합니다. Slave의 **캡처 / 판독 원문 보기**를 열어 Output이 실제 이미지에 나오고 읽은 주파수/재개·중단/경고 문구가 맞는지 비교합니다. 오류가 나도 같은 명령을 반복하지 않습니다.
4. Master와 Slave 로그를 첨부하고, **캡처에 Output이 보였는지 / 판독 텍스트가 맞았는지**만 알려주세요. 이미지 자체를 외부에 첨부할 필요는 없습니다. 로그 첨부를 위해 앱을 종료할 필요도 없습니다.

Master/휴대폰이 준비되지 않았다면 위 원격 확인 대신 Slave의 **PowerSI 확인 한 번**으로 캡처·모델 판독을 확인하고 Slave 로그만 보내도 됩니다. 원격 확인이 성공했으면 로컬 확인을 다시 하지 않습니다.

## 실패 표시

- `VISION_NOT_CONFIGURED`: Slave에서 로컬 판독을 켜고 설정을 저장합니다.
- `VISION_MODEL_UNAVAILABLE/MODEL_AMBIGUOUS`: 이미지 모델이 로드돼 있는지, 저장한 ID가 현재 모델과 같은지 확인합니다.
- `VISION_SERVER_UNAVAILABLE/AUTH_REQUIRED`: LM Studio 서버·포트·버전·토큰을 확인합니다.
- `VISION_CAPTURE_FAILED`: Slave의 Local detail을 확인합니다. 최소화/잠금/복수 창/빈 캡처 등을 구분합니다.
- `VISION_TIMEOUT/INVALID_RESPONSE`: 응답 제한 초과 또는 지정 형식으로 읽지 못한 결과입니다. 같은 시험을 반복하지 말고 로그를 보내주세요.
- Output/Status UNAVAILABLE: 모델이 해당 부분을 읽지 못했다는 뜻입니다. 시뮬레이션 실패/완료를 의미하지 않습니다.

이번에는 M코드·help·total status·마우스 오버·장시간 대기 시험을 추가로 수행하지 않습니다. 프로그램 실행/종료 명령이나 LLM이 생성한 임의 명령도 실행하지 않습니다.
