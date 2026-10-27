# CamBridge 작업 계획

목표: 실물 카메라 없이 획득·검사·UI·포장을 약 2주 안에 완료하고, 카메라 도착 시 구현체 하나만 추가해 실물 시연한다.

진행하면서 체크박스를 갱신한다. 각 항목의 작업 위치는 `CLAUDE.md`의 "작업 환경 분담"을 따른다.

- **[클라우드]** Linux 클라우드 세션에서 구현·테스트 가능
- **[로컬]** Windows 로컬 세션에서 실행 검증 필요

권장 순서: 0·1단계(클라우드) → 4·5단계 로직과 테스트(클라우드) → 2·3·6단계(로컬) → 7단계

---

## 0단계 — 환경 준비 (0.5일)

- [ ] [클라우드] 솔루션 및 프로젝트 골격 생성 (`CLAUDE.md`의 구조와 대상 프레임워크대로)
- [ ] [클라우드] NuGet 패키지 추가: CommunityToolkit.Mvvm, OpenCvSharp4(+ OS별 런타임 조건부 참조), xUnit
- [x] `.gitignore` 기본 설정, `README.md` 초안 (초기 커밋에 포함)
- [ ] [클라우드] `Directory.Build.props`(공통 설정, nullable, 경고 수준, Windows 프로젝트의 `EnableWindowsTargeting`)
- [ ] [클라우드] GitHub Actions: `net8.0` 프로젝트 빌드·테스트 (선택)
- [ ] [로컬] Basler pylon SDK 설치 확인 및 .NET API 참조 경로 확인 (개발자가 설치)

**완료 기준**: 빈 솔루션 빌드 성공, 첫 커밋

---

## 1단계 — 카메라 추상화 설계 (1일)

`CamBridge.Core`에 정의한다. 아래는 출발점이며, 개선 제안이 있으면 근거와 함께 제시할 것.

```csharp
public interface ICamera : IAsyncDisposable
{
    CameraInfo Info { get; }
    CameraState State { get; }

    Task OpenAsync(CancellationToken ct = default);
    Task CloseAsync();
    Task StartAcquisitionAsync();
    Task StopAcquisitionAsync();

    // Frequently used parameters (strongly typed)
    double ExposureTimeUs { get; set; }
    double Gain { get; set; }
    TriggerMode TriggerMode { get; set; }      // Off, Software, Hardware
    void ExecuteSoftwareTrigger();

    // GenICam-style generic access
    IReadOnlyList<CameraParameter> GetParameters();
    object? GetParameter(string name);
    void SetParameter(string name, object value);

    event EventHandler<FrameReceivedEventArgs> FrameReceived;
    event EventHandler<CameraErrorEventArgs> ErrorOccurred;
}

public interface ICameraProvider
{
    string VendorName { get; }
    IReadOnlyList<CameraInfo> Enumerate();
    ICamera Create(CameraInfo info);
}

public sealed class Frame : IDisposable
{
    // FrameId, TimestampUtc, Width, Height, PixelFormat (Mono8, Bgr8), Stride
    // Pooled buffer (ArrayPool<byte>), ownership transferred to consumer
}
```

- [ ] `ICamera`, `ICameraProvider`, `CameraInfo`, `CameraState`, `TriggerMode`, `PixelFormat`
- [ ] `Frame` (풀링 버퍼, Dispose 시 반환)
- [ ] `CameraParameter` (이름, 타입, 범위, 읽기/쓰기 가능 여부)
- [ ] `CameraRegistry` — 등록된 Provider들로부터 카메라 목록 통합
- [ ] 단위 테스트: Frame 버퍼 반환, 레지스트리 열거

**완료 기준**: 인터페이스 확정, 테스트 통과

---

## 2단계 — 가상 소스 구현 (1일)

- [ ] `WebcamCamera` / `WebcamProvider` — OpenCvSharp `VideoCapture`, 별도 획득 스레드
- [ ] `ImageFolderCamera` / `ImageFolderProvider` — 폴더 이미지를 지정 FPS로 순환 재생, 소프트웨어 트리거 시 한 장씩 출력
- [ ] 지원하지 않는 파라미터는 명확한 예외 또는 `IsAvailable=false`로 처리
- [ ] 검사 시연용 샘플 이미지 준비 (공개 산업 결함 데이터셋 일부를 `samples/`에 배치, 라이선스 확인)

**완료 기준**: 콘솔 또는 임시 UI에서 두 소스 전환하며 프레임 수신 확인

---

## 3단계 — pylon 에뮬레이터 연동 (2일)

- [ ] `PylonProvider` / `PylonCamera` — Basler pylon .NET API
- [ ] `PYLON_CAMEMU` 환경변수로 에뮬레이션 카메라 열거 확인
- [ ] 노출·게인·트리거 모드·소프트웨어 트리거 매핑
- [ ] 이름 기반 파라미터 접근을 pylon 파라미터 맵에 연결
- [ ] SDK 미설치 환경에서도 솔루션 빌드·실행 가능하도록 처리 (플러그인 로딩 또는 조건부 참조)
- [ ] 카메라 분리·재연결 등 오류 이벤트 처리

**완료 기준**: 에뮬레이터 카메라로 연속 획득 및 소프트웨어 트리거 촬영 성공

---

## 4단계 — 획득 파이프라인 (2일)

- [ ] 생산자/소비자 구조 (`System.Threading.Channels`), 큐 상한 및 초과 시 정책(드롭/대기) 선택 가능
- [ ] 통계 수집: 수신 FPS, 처리 FPS, 드롭 프레임 수, 큐 길이
- [ ] 이미지 저장 소비자 (비동기, 포맷·경로 규칙 설정)
- [ ] 라이브뷰용 "최신 프레임" 슬롯 (중간 프레임 스킵)
- [ ] 장시간 실행 테스트 (예: 에뮬레이터 30분 연속) — 메모리 증가 없음 확인

**완료 기준**: 장시간 실행 시 메모리 안정, 통계 정확

---

## 5단계 — 검사 로직 MVP (2일)

- [ ] ROI 모델 (사각형, 복수 ROI)
- [ ] 검사 도구: 이진화 + 블롭(면적·개수), 템플릿 매칭(점수 임계값)
- [ ] 판정 규칙 → OK/NG + 사유
- [ ] 검사 결과 CSV 로그 (시각, 프레임 ID, 결과, 측정값)
- [ ] 레시피(검사 설정) JSON 저장·불러오기
- [ ] 샘플 이미지 기반 단위 테스트

**완료 기준**: 샘플 이미지 폴더로 양불 판정이 기대대로 나옴

---

## 6단계 — UI 완성 (2일)

- [ ] 카메라 선택·연결 패널 (벤더 구분 표시)
- [ ] 라이브뷰 (확대/축소, 픽셀 좌표·값 표시)
- [ ] 파라미터 패널 (노출·게인·트리거 + 고급 파라미터 목록)
- [ ] ROI 마우스 드로잉 및 편집
- [ ] 검사 결과 오버레이 (OK 녹색 / NG 빨강), 누적 OK/NG 카운트
- [ ] 통계 상태바 (FPS, 드롭, 큐 길이) 상시 표시

**완료 기준**: 처음 보는 사람에게 시연 가능한 화면

---

## 7단계 — 포트폴리오 포장 (1일)

- [ ] README: 기능, 구조 다이어그램, 실행 방법, 확장 방법(새 벤더 추가 절차)
- [ ] 에뮬레이터 + 이미지 폴더 기반 시연 GIF
- [ ] GitHub 공개

**완료 기준**: README만 보고 기능과 구조를 이해할 수 있음

---

## 카메라 도착 후 (1~2일)

- [ ] 실물 카메라 구현 연결 (Basler면 기존 `PylonCamera` 그대로, HIKROBOT이면 `Sources.Hik` 추가)
- [ ] 조명·렌즈 세팅, 실물 촬영 시연 영상 제작
- [ ] README·크몽 서비스 페이지에 실물 시연 반영
