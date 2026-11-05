# CamBridge 작업 계획

목표: 실물 카메라 없이 획득·검사·UI·포장을 약 2주 안에 완료하고, 카메라 도착 시 구현체 하나만 추가해 실물 시연한다.

진행하면서 체크박스를 갱신한다. 각 항목의 작업 위치는 `CLAUDE.md`의 "작업 환경 분담"을 따른다.

- **[클라우드]** Linux 클라우드 세션에서 구현·테스트 가능
- **[로컬]** Windows 로컬 세션에서 실행 검증 필요

권장 순서: 0·1단계(클라우드) → 4·5단계 로직과 테스트(클라우드) → 2·3·6단계(로컬) → 7단계

---

## 0단계 — 환경 준비 (0.5일)

- [x] [클라우드] 솔루션 및 프로젝트 골격 생성 (`CLAUDE.md`의 구조와 대상 프레임워크대로)
- [x] [클라우드] NuGet 패키지 추가: CommunityToolkit.Mvvm, OpenCvSharp4(+ OS별 런타임 조건부 참조), xUnit
  - 버전은 `Directory.Packages.props`(중앙 패키지 관리)에서 일괄 관리
  - 라이브러리는 `OpenCvSharp4` 관리 코드만, 네이티브 런타임은 실행 프로젝트(App: win, Tests: OS별)에만 참조
  - OpenCvSharp4 4.13+의 분석기는 Roslyn 4.14 이상을 요구(구버전 SDK에서 CS9057) → `Directory.Build.targets`에서 제거
- [x] `.gitignore` 기본 설정, `README.md` 초안 (초기 커밋에 포함)
- [x] [클라우드] `Directory.Build.props`(공통 설정, nullable, 경고 수준, Windows 프로젝트의 `EnableWindowsTargeting`)
- [x] [클라우드] GitHub Actions: `net8.0` 프로젝트 빌드·테스트 (선택) — `.github/workflows/ci.yml`
- [x] [로컬] Basler pylon SDK 설치 확인 및 .NET API 참조 경로 확인 (개발자가 설치) — pylon 26.09.2, `D:\Program Files\Basler\pylon`
  - **로컬 검증 필요**: `CamBridge.Sources.Pylon.csproj`의 기본 경로
    `$(PYLON_DEV_DIR)\Assemblies\Basler.Pylon\x64\Basler.Pylon.dll`은 추정값. 실제 설치 경로 확인 후 수정
    (임시로 `dotnet build -p:PylonNetAssembly=<경로>`로 지정 가능). SDK 감지 시 빌드 로그에 `pylon SDK found` 출력, `HAS_PYLON` 상수 정의
- [x] [로컬] Windows에서 `dotnet build CamBridge.sln`, `dotnet test`, WPF 빈 창 실행 확인 — 인계 체크리스트 A에서 완료

클라우드 검증 결과(Linux, Microsoft 빌드 .NET SDK 8.0.427): 솔루션 전체(WPF 포함) 빌드 성공, 경고 0, 테스트 1/1 통과(OpenCV Linux 네이티브 런타임 로드 확인).
참고: Ubuntu 기본 apt의 source-built SDK에는 WindowsDesktop SDK가 없어 WPF 프로젝트가 빌드되지 않는다. Microsoft 빌드 SDK가 필요하다.

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

- [x] `ICamera`, `ICameraProvider`, `CameraInfo`, `CameraState`, `TriggerMode`, `PixelFormat`
- [x] `Frame` (풀링 버퍼, Dispose 시 반환)
- [x] `CameraParameter` (이름, 타입, 범위, 읽기/쓰기 가능 여부)
- [x] `CameraRegistry` — 등록된 Provider들로부터 카메라 목록 통합
- [x] 단위 테스트: Frame 버퍼 반환, 레지스트리 열거 (22개 + OpenCV 스모크 1개 통과)

**확정된 변경 사항 (출발안 대비)**

- `Frame`: 참조 카운트 공유. `Share()`가 같은 버퍼를 가리키는 **새 핸들**을 반환하고, 마지막 핸들 Dispose 시 풀에 반납.
  핸들별 중복 Dispose는 무해하고, Dispose 후 접근은 `ObjectDisposedException`. 공유된 프레임은 읽기 전용(`GetWritableData()` 예외).
  `FrameId`(장치 block ID, 드롭 검출용), `DeviceTimestamp`, `TimestampUtc` 분리. 풀은 주입 가능(기본 `ArrayPool<byte>.Shared`)
- `FrameReceived`의 프레임은 핸들러 실행 중에만 유효. 보관하려면 `Share()`
- `ICameraProvider.EnumerateAsync(ct)` — GigE 탐색 지연 대응. `ProviderId`로 `Create` 라우팅
- `CameraRegistry.EnumerateAsync` — Provider 병렬 열거, 실패 Provider는 `Failures`로 격리, 등록 순서 유지
- `ICamera`에 `StateChanged`, `ExecuteCommand(name)`, Start/Stop/Close의 `CancellationToken` 추가.
  `GetParameter`는 null을 반환하지 않음(미지원 이름 `KeyNotFoundException`, 현재 접근 불가 `InvalidOperationException`)
- 파라미터 값 타입 규약: Integer=`long`, Float=`double`, Boolean=`bool`, Enumeration/String=`string`. 이름은 SFNC(`ParameterNames`)
- `CameraState`: Closed / Open / Acquiring / Faulted. `CameraErrorKind`: Disconnected / FrameError / Timeout / Other

**완료 기준**: 인터페이스 확정, 테스트 통과

---

## 2단계 — 가상 소스 구현 (1일)

- [x] [로컬] `WebcamCamera` / `WebcamProvider` — OpenCvSharp `VideoCapture`, 별도 획득 스레드
  - DirectShow 전용. MSMF는 APC940 실측에서 Open 14초, 노출 미반영, 해상도 변경 시 크래시로 제외
  - 열거: `ICreateDevEnum` COM 열거(이름·장치 경로). DeviceId = 장치 경로, Open 시 재열거로 현재 CAP_DSHOW 인덱스 확인
  - `DeviceThread`(actor)가 VideoCapture·DirectShow 필터를 Open~Close 동안 독점. 파라미터 접근은 명령 큐로 마샬링(대기 최대 1프레임), 장치 스레드에서 호출 시 인라인 실행
  - 노출·게인·기타 UVC 컨트롤은 `IAMCameraControl`/`IAMVideoProcAmp` 직접 호출(GetRange로 범위·`IsAvailable`, 자동/수동 플래그). OpenCV는 스트리밍만 담당
  - ExposureTime(µs) ↔ log2 초 단계 변환, SFNC 규약대로 `ExposureAuto`=Continuous 동안 값 쓰기 불가. Gain은 장치 단위(`raw`)
  - Software 트리거는 에뮬레이션(연속 읽기 + 트리거 이후 첫 프레임만 발행). FrameId는 발행 카운터(웹캠은 block ID 없음 → 카메라측 드롭 측정 불가)
  - `AcquisitionFrameRate`는 DSHOW가 협상값을 보고하지 않아 `IsAvailable=false`. APC940: YUY2 고정, 640×480 30fps / 720p 10fps / 1080p 4.8fps
  - 분리 감지: 느린 `Read()`(≥ `SlowReadThreshold`) 시 장치 존재 확인, 또는 읽기 실패가 `DisconnectTimeout`(기본 2초) 지속 시 `ErrorOccurred(Disconnected)` + `Faulted`
  - 테스트: `tests/CamBridge.Tests.Windows`(net8.0-windows, CI 제외, 웹캠 없으면 장치 테스트 Skip) — `dotnet test tests/CamBridge.Tests.Windows`
- [x] [클라우드] `ImageFolderCamera` / `ImageFolderProvider` — 폴더 이미지를 지정 FPS로 순환 재생, 소프트웨어 트리거 시 한 장씩 출력
  - Open 시 전량 디코딩(상한 `MaxMemoryBytes`, 기본 1GB), 모든 이미지 동일 크기 필수(센서와 동일한 제약), 파일명 서수 정렬 순서로 재생
  - 전용 획득 스레드. Free-run은 `AcquisitionFrameRate` 주기(코스 Sleep + 마지막 2ms 스핀, 지연 시 몰아내기 없음), Software 트리거는 1회당 1장
  - 노출·게인 디지털 시뮬레이션(LUT, 기준 10000µs / 0dB) — 파라미터 조작이 검사 결과에 반영되는 시연용
  - 파라미터: ExposureTime, Gain, TriggerMode(Off/Software), TriggerSoftware, AcquisitionFrameRate, PixelFormat(Mono8/Bgr8, 획득 중 변경 불가), Width/Height(읽기 전용), Loop
  - 획득 시작마다 FrameId 1부터(파이프라인은 카운터 리셋으로 인식, 드롭 아님). 버퍼 풀 주입 가능(`ImageFolderOptions.BufferPool`)
- [x] 지원하지 않는 파라미터는 명확한 예외 또는 `IsAvailable=false`로 처리 (ImageFolder 기준. 미지원 이름 `KeyNotFoundException`, Hardware 트리거 `ArgumentException`)
- [x] 검사 시연용 샘플 이미지 준비
  - 공개 데이터셋 대신 직접 생성(`tools/CamBridge.SampleGenerator`) → 라이선스 문제 없음. `samples/part-inspection/`에 커밋(이미지 20장 OK 15 / NG 5, 레시피, 템플릿)
  - 재생성: `dotnet run --project tools/CamBridge.SampleGenerator`
  - 회귀 테스트: 커밋된 샘플을 ImageFolder 카메라 → 파이프라인 → 검사로 흘려 파일명(`_ok`/`_ng`)과 판정 일치 확인

**완료 기준**: 콘솔 또는 임시 UI에서 두 소스 전환하며 프레임 수신 확인

---

## 3단계 — pylon 에뮬레이터 연동 (2일)

- [x] `PylonProvider` / `PylonCamera` — Basler pylon .NET API (pylon 26.09.2, net8.0 x64 API)
  - 장치 스레드 없음: pylon `Camera`는 자체 동기화. 획득은 `OneByOne` + `ProvidedByStreamGrabber`, `ImageGrabbed`(pylon 스레드)에서 `Frame.Rent`로 1회 복사(PaddingX 반영) 후 `FrameReceived`
  - FrameId = BlockID를 `BlockIdUnwrapper`로 연속화(GigE 1..65535 순환 처리, 0=미지원 시 호스트 카운터). 실패한 grab은 FrameError + BlockID 공백 → 파이프라인 카메라측 드롭으로 집계
  - 픽셀: Mono8, BGR8, RGB8(→BGR), Bayer 8bit(OpenCV 디모자이크 → Bgr8). 그 외는 형식당 1회 FrameError
  - Open 시 `AcquisitionMode=Continuous`(에뮬레이터 기본값 SingleFrame이라 1장만 나옴), `MaxNumBuffer`, GigE `HeartbeatTimeout`(기본 1s, 디버거 연결 시 미적용 — pylon이 5분으로 자동 연장)
- [x] `PYLON_CAMEMU` 환경변수로 에뮬레이션 카메라 열거 확인 — 2대(`Emulation (0815-0000/0001)`, SFNC 2.5)
- [x] 노출·게인·트리거 모드·소프트웨어 트리거 매핑
  - `FeatureNames`: ExposureTime→ExposureTimeAbs, Gain→GainRaw, AcquisitionFrameRate→AcquisitionFrameRateAbs(+Enable) — SFNC 2.x 우선, 없으면 1.x
  - TriggerMode: Off / Software / Hardware → `TriggerSelector=FrameStart` + TriggerMode + TriggerSource(Hardware는 지정된 Line 유지, 없으면 Line1). 소프트웨어 트리거 전 `WaitForFrameTriggerReady`
- [x] 이름 기반 파라미터 접근을 pylon 파라미터 맵에 연결 — 노드 순회, Visibility ≤ Expert(`PylonOptions.MaxVisibility`) 목록화, 범위·설명·단위(알려진 노드 보완). 목록은 장치 노드만(카메라 인스턴스 노드 `MaxNumBuffer` 등은 이름 조회 불가라 제외)
- [x] SDK 미설치 환경에서도 솔루션 빌드·실행 가능하도록 처리 (플러그인 로딩 또는 조건부 참조)
  - 조건부 참조(`HAS_PYLON`) + 항상 컴파일되는 `PylonProviderFactory.TryCreate`. SDK 없으면 사유 로그 후 미등록, 에뮬레이터 테스트는 Skip
  - .NET 8엔 GAC가 없어 `Basler.Pylon.dll`을 출력에 복사(gitignore 대상). x64 전용 참조의 MSB3270은 SDK 샘플과 같은 방식으로 억제
  - `PylonRuntime`: 설치 전에 시작된 프로세스용으로 `Runtime\x64`를 프로세스 PATH에, 영구 `PYLON_CAMEMU`를 프로세스 환경에 보충
  - 주의: SDK 있음/없음 빌드를 번갈아 하면 App deps.json에 Basler.Pylon이 빠질 수 있음 → `--no-incremental`로 재빌드
- [x] 카메라 분리·재연결 등 오류 이벤트 처리 — `ConnectionLost` → `Disconnected` + `Faulted` 구현. 에뮬레이터로는 분리 재현 불가, 실물 GigE로 검증(2026-10-31, 약 1.5초에 감지, 재연결 복구)
- 2026-10-27 로컬: 에뮬레이터 테스트 9개 + 순수 로직 11개 통과(전체 Windows 88, Core 98). 앱에서 "Basler pylon" 그룹에 에뮬레이터 2대 표시, 1024x1040 Mono8 수신 95.9fps·드롭 0·검사 96fps, 파라미터 패널(노출 µs·게인 dB·고급 목록) 정상 — 상위 코드 수정 없음

**완료 기준**: 에뮬레이터 카메라로 연속 획득 및 소프트웨어 트리거 촬영 성공

---

## 4단계 — 획득 파이프라인 (2일)

- [x] 생산자/소비자 구조 (`System.Threading.Channels`), 큐 상한 및 초과 시 정책(드롭/대기) 선택 가능
  - 소비자별 독립 bounded 큐(`AcquisitionPipeline.AddConsumer`). 저장 지연이 검사를 막지 않음
  - 정책: `DropOldest`(기본) / `DropNewest` / `Wait`(카메라 스레드를 `WaitTimeout`까지 블로킹 후 드롭 — 트리거 기반 무손실 용도로만)
  - 소비자 예외는 Faults·LastError로 집계하고 계속 진행. `StopAsync(drain)`로 잔여 큐 처리/폐기 선택
- [x] 통계 수집: 수신 FPS, 처리 FPS, 드롭 프레임 수, 큐 길이
  - `GetStatistics()` 스냅샷(UI 폴링용). FPS는 100ms×10 버킷 슬라이딩 윈도우, `TimeProvider` 주입
  - 드롭 출처 분리: 카메라측(FrameId 간격, 카운터 리셋은 제외) / 소비자 큐별 / 라이브뷰 스킵(정상 동작, 드롭 합계에서 제외)
- [x] 이미지 저장 소비자 (비동기, 포맷·경로 규칙 설정)
  - `ImageSaveConsumer`: PNG/BMP/JPEG, 경로 패턴 토큰(`{camera}`, `{frameId:D8}`, `{timestamp:yyyyMMdd}` 등), N장마다 저장, 루트 밖 경로 거부
  - `IFrameEncoder` / `OpenCvFrameEncoder`: 프레임 버퍼를 핀 고정해 Mat으로 래핑(복사 없음)
- [x] 라이브뷰용 "최신 프레임" 슬롯 (중간 프레임 스킵) — `LatestFrameSlot`
- [x] 장시간 실행 테스트 (예: 에뮬레이터 30분 연속) — 메모리 증가 없음 확인
  - [x] [클라우드] 합성 카메라 고속 생성 2초 소크 테스트: 풀 대여=반납(누수 0), 소비자별 처리+드롭=수신 정확 일치. 단일 코어 포함 반복 실행 안정
  - [x] [로컬] 3단계 완료 후 pylon 에뮬레이터 30분 연속 획득, 프로세스 메모리 추이 확인 — 아래 인계 체크리스트 D 참고

**완료 기준**: 장시간 실행 시 메모리 안정, 통계 정확

---

## 5단계 — 검사 로직 MVP (2일)

- [x] ROI 모델 (사각형, 복수 ROI) — `RoiRect`, 도구별 ROI
- [x] 검사 도구: 이진화 + 블롭(면적·개수), 템플릿 매칭(점수 임계값)
  - `BlobTool`: 고정/Otsu 임계값, 극성(Dark/Bright), 최소 면적 노이즈 필터, `ConnectedComponentsWithStats`. 측정: Count/TotalArea/MaxArea/Threshold
  - `TemplateMatchTool`: `TM_CCOEFF_NORMED`. 측정: Score/X/Y. 무특징 영역(NaN)은 점수 0
- [x] 판정 규칙 → OK/NG + 사유
  - 모든 활성 도구 통과 시 OK. 사유 예: `Stain: Count 2 > max 0`
  - ROI가 이미지 밖·템플릿보다 작음 → 예외 없이 NG(`IsConfigurationError`). 템플릿 파일 누락은 엔진 생성 시 예외
  - 결과에 UI 오버레이용 사각형(ROI/블롭/매칭 위치, 이미지 좌표)과 도구별·전체 처리 시간 포함
- [x] 검사 결과 CSV 로그 (시각, 프레임 ID, 결과, 측정값)
  - `CsvResultLogger`: 레시피 기반 고정 열(`도구.측정값`), 행 단위 flush, 기존 파일과 열 구성이 다르면 거부
- [x] 레시피(검사 설정) JSON 저장·불러오기
  - `RecipeSerializer`: `$type` 다형성, enum 문자열, `schemaVersion` 검사, 도구 이름 중복 거부
  - 템플릿은 레시피 옆 별도 PNG(상대 경로)
- [x] 샘플 이미지 기반 단위 테스트
  - 샘플은 테스트 코드가 합성(텍스처 배경 + 기준 마크 + 결함 점) → 라이선스 문제 없음
  - 판정 여유: 템플릿 점수 마크 있음 0.999 / 없음 약 0.1 (임계 0.8), 배경 184~196 / 결함 40 (임계 100)
- [x] 파이프라인 연결: `InspectionConsumer`(OK/NG 카운트, 최신 결과, `ResultProduced` 이벤트 — 워커 스레드에서 발생하므로 UI는 Dispatcher 마샬링)

검증: 샘플 폴더(OK 3 / NG 3 PNG) + 레시피 파일 → 파이프라인 소비자로 실행 → 6장 모두 기대 판정, CSV 7행 확인. 테스트 총 76개 통과.

**완료 기준**: 샘플 이미지 폴더로 양불 판정이 기대대로 나옴

---

## 6단계 — UI 완성 (2일)

설계: UI 갱신은 전부 pull(라이브뷰·검사 결과는 렌더 틱, 통계·로그는 4Hz 타이머). 카메라·파이프라인 스레드에서 Dispatcher 호출 없음. 진행 단위 6a(뼈대·라이브뷰·통계·로깅) → 6b(파라미터) → 6c(검사·ROI·레시피 교체).

- [x] 카메라 선택·연결 패널 (벤더 구분 표시) — 6a. `CameraRegistry` 열거를 Provider `VendorName`으로 그룹 표시, 열거 실패 Provider는 경고로 표시
- [x] 라이브뷰 (확대/축소, 픽셀 좌표·값 표시) — 6a. `LiveView` 컨트롤: `CompositionTarget.Rendering`에서 `TryTake` → `WriteableBitmap`, 휠 줌(커서 기준)·드래그 팬·더블클릭 Fit·1:1, 확대 시 NearestNeighbor, 커서 픽셀 값(Gray/RGB). 변환 계산은 `ViewTransform`(단위 테스트)
  - 2026-10-27 로컬: ImageFolder(수신 31 / 표시 29fps), 웹캠 APC940(수신 30.5 / 표시 29.8fps) 드롭 0. 획득 중 창 닫기 → 정상 종료(코드 0)
  - 로컬 검증 필요: 줌·팬·픽셀 값 표시의 마우스 조작 (UI Automation으로는 확인하지 않음)
- [x] 파라미터 패널 (노출·게인·트리거 + 고급 파라미터 목록) — 6b
  - 노출: 로그 스케일 슬라이더 + 입력/적용 + 자동(ExposureAuto). 게인: 슬라이더 + 입력, 미지원 시 비활성. 트리거 모드 + 소프트웨어 트리거 버튼(획득 중 + Software일 때만)
  - 고급: `GetParameters()` 전체를 타입별 편집기(텍스트/선택/토글/명령/읽기 전용)로 표시, 범위·단위·설명
  - 카메라 호출은 스레드 풀 + 단일 gate. 슬라이더 드래그는 `CoalescingApplier`로 최신 값만 쓰고 드래그가 끝나면 1회 전체 갱신. 거부된 값은 사유 표시 후 카메라 값으로 복귀. 자동 모드일 때만 노출·게인 1Hz 추적
  - Core `ParameterNames`에 `ExposureAuto`/`GainAuto`(SFNC) 추가
  - 웹캠 수정: UVC 컨트롤을 호출 스레드(MTA)에서 lock으로 직접 호출, 해상도 캐시 → 파라미터 읽기가 프레임을 기다리지 않음(노출 250ms에서 목록 갱신 약 10초 → 즉시). STA 호출은 장치 스레드 경유
  - 2026-10-27 로컬(APC940): 자동 끄기 → 500µs(488로 양자화, 30.9fps 검은 영상) → 250000µs(밝은 영상, 2.1fps) → 범위 밖 값 거부 메시지 → 자동 복원, 종료 코드 0
  - 테스트 수정: 노출 변경 확인을 고정 800ms 대기에서 "새 프레임 5장"으로 변경. 어두운 환경(노출 250ms, 약 4fps)에서는 드라이버 버퍼 때문에 반영이 약 1초 늦어 이전 노출을 샘플링하던 문제
- [x] ROI 마우스 드로잉 및 편집 — 6c
  - 라이브뷰 [ROI 편집] 모드: ROI 클릭 선택, 본체 드래그 이동, 8방향 핸들 크기 조절, 빈 곳 드래그로 선택 도구의 ROI 새로 그리기. 드래그 중에는 미리보기만, 놓을 때 엔진 재생성. 이미지 밖으로 나가지 않고 최소 4px 유지(`RoiEditor`, 단위 테스트)
  - 검사 패널: 레시피 불러오기/저장/다른 이름으로(템플릿 경로를 새 위치 기준 상대 경로로 변환), 도구 목록(사용 on/off), Blob 추가·삭제, 선택 도구 설정(이진화·극성·최소 면적·개수/면적 범위, 템플릿 최소 점수)
  - 실행 중 레시피 교체: `InspectionConsumer.ReplaceEngine` — 새 엔진은 대기 슬롯에만 두고 워커가 다음 프레임 전에 교체, 이전 엔진은 워커 스레드에서 해제. 연속 편집은 `CoalescingApplier`로 1개 엔진으로 합침. 엔진 생성(템플릿 디스크 I/O)은 스레드 풀
  - 2026-10-27 로컬: 샘플 폴더에서 Stain ROI를 실제 마우스로 +40px 드래그 → (160,60,160x140)(오른쪽 끝에서 제한), "Recipe applied" 후 검사 계속
- [x] 검사 결과 오버레이 (OK 녹색 / NG 빨강), 누적 OK/NG 카운트 — 6c
  - `OverlayLayer`가 화면 좌표로 그림(확대해도 1px 선). 판정 배너(OK/NG + 프레임 번호), 도구 ROI 통과 녹색/실패 빨강, 블롭 주황·매칭 자홍 점선. 렌더 틱에서 `IInspectionOverlay.Version`이 바뀔 때만 다시 그림(pull)
  - 검사 패널: 최근 판정(색상)·프레임 번호·처리 시간·실패 사유, OK/NG/양품률, 초기화
  - 2026-10-27 로컬: 샘플 20장 순환 재생 OK 91 / NG 30 = 75.2%(샘플 구성 15/5와 일치), 검사 30fps·0.3~0.4ms·드롭 0
  - 미포함: UI에서 CSV 결과 기록(Core `CsvResultLogger`는 있음, `ReplaceEngine`이 로거 교체도 지원 — 같은 파일을 새 로거가 이어 쓰려면 이전 로거가 닫힌 뒤여야 해서 UI 연결은 보류)
- [x] 통계 상태바 (FPS, 드롭, 큐 길이) 상시 표시 — 6a. 상태, 수신 FPS·누적, 표시 FPS·건너뜀, 총 드롭(카메라측), 소비자별 처리 FPS·큐·드롭·오류
- [x] 로깅 (콘솔 + 파일 + UI 로그 줄) — 6a. `FileLoggerProvider`(일별 파일, 백그라운드 Channel로 I/O), `MemoryLogProvider`(최근 200줄, UI가 버전 폴링)
- [x] 임의 이미지 폴더 열기 (단계 완료 후 추가) — 카메라 패널 [폴더 추가]
  - `--images <folder>` 인자 대신 UI로 결정: 코드 차이가 수십 줄인데 인자는 폴더를 바꿀 때마다 재시작이 필요하고 잘못된 경로가 열거에서 조용히 빠짐
  - `ImageFolderProvider.AddFolder`: `Create`는 폴더 목록을 확인하지 않지만 열거는 목록만 돌아 새로고침 때 사라지므로 Provider 변경 필요. 열거가 스레드 풀에서 돌아 배열 교체로 처리. Core 변경 없음. VM은 App.xaml.cs가 주입한 `Func<string, CameraInfo>`만 알고, 추가 시 전체 재열거는 하지 않음(GigE discovery 수 초)
  - 샘플 레시피 보호: 시작 시 불러온 샘플 레시피에서 [저장]은 저장 위치를 묻고, 폴더를 추가했으면 대화상자는 그 폴더에서 시작. 저장소의 추적 파일이 덮어써지거나 다른 이미지용 레시피가 샘플 폴더에 저장되는 것을 막음. 샘플 자체를 고치려면 [다른 이름으로]에서 같은 파일 선택
  - 비 ANSI 경로: `Cv2.ImRead`가 경로를 ANSI 코드 페이지로 마샬링해 CP949 밖 문자(베트남어 폴더로 실측)에서 `ArgumentException`. `File.ReadAllBytes` + `Cv2.ImDecode`로 교체(`ImageFolderCamera`, `InspectionEngine` 템플릿). `ImDecode`는 빈 버퍼에 예외를 던지므로 0바이트 파일은 기존과 같은 디코드 실패로 처리
  - 2026-11-01 로컬(합성 이미지 400x300, 저장소 밖 폴더): 추가 후 새로고침해도 유지, 수신 30fps·드롭 0, Stain ROI 드래그 → (107,68,160x140), 첫 [저장]은 위치 확인 후 외부 폴더에 저장, 이후 [저장]은 같은 파일에 바로 저장, 샘플 레시피 해시 변화 없음. 이미지 없는 폴더 → "이미지 폴더 추가 실패: No supported images…", 크기 혼재 폴더 → "연결 실패: All images must have the same size. z_odd.png is 200x100, expected 400x300.", 이모지·베트남어 이름 폴더 재생 확인
  - 재시작 후 폴더 목록 유지, 목록에서 제거, [새 레시피] 버튼은 미포함 → 다음 항목에서 추가
- [x] 이미지 폴더 목록 유지·제거, 새 레시피 (단계 완료 후 추가)
  - 설정 파일 `%LOCALAPPDATA%\CamBridge\settings.json`(`imageFolders`). 저장소 밖이라 고객 이미지 경로가 작업 트리에 들어가지 않고 클린 빌드에도 남음. 임시 파일에 쓴 뒤 교체(쓰는 중 종료돼도 이전 파일 유지), 저장 전마다 다시 읽어 두 인스턴스가 서로의 추가를 지우지 않음. 읽기 실패는 빈 목록 + 경고 로그(다음 저장이 덮어씀), 저장 실패는 경고 로그만(이번 실행에는 이미 추가됨)
  - 없어진 저장 폴더는 목록에서만 숨기고 설정에는 남김: USB·네트워크 드라이브가 돌아오면 [새로고침]으로 복귀. 한계: 오프라인 UNC 경로는 SMB 타임아웃만큼 새로고침 전체를 늦춤(레지스트리가 모든 Provider를 기다림)
  - [목록에서 제거]: `ImageFolderProvider.RemoveFolder`(추가와 같은 잠금, 배열 교체), 파일은 그대로라 확인 없음. 샘플 폴더(다음 실행에 다시 나타남)와 연결 중인 폴더는 비활성
  - VM은 `Func<string, CameraInfo>` 대신 App 계층 `IImageFolderCatalog`(추가·제거·제거 가능 여부)만 앎. 구현 `ImageFolderCatalog`가 Provider와 설정 저장소를 묶고 App.xaml.cs가 조합. Core 변경 없음
  - 검사 패널 [새로]: 도구를 지우고 이름 "새 레시피", 경로 없음(첫 [저장]은 위치 확인), 템플릿 기준 폴더는 샘플 없이 시작한 상태로. 변경 추적이 없어 도구가 있으면 확인. 도구 0개는 엔진 규칙(활성 도구 전부 통과 = OK)대로 모든 프레임 OK
  - 재시작 후 복원된 폴더는 이번 실행에서 고른 적이 없으므로, 추가한 폴더에 연결할 때도 레시피 대화상자의 시작 폴더를 그 폴더로 설정
  - 도구가 하나도 선택되지 않았을 때 설정 영역에 빈 Blob·템플릿 입력란이 그려지던 문제(기존에도 도구를 모두 삭제하면 발생, [새로]로 드러남): 명시 `ContentTemplate`을 암시적 `DataTemplate`으로 바꿔 선택이 없으면 아무것도 그리지 않음
  - 설정 JSON은 `UnsafeRelaxedJsonEscaping`으로 한글 경로를 그대로 기록(직접 열어 볼 때 읽히도록, HTML에 넣지 않음)
  - 라이브뷰 수정: 연결 해제 후 마지막 판정 배너·ROI·블롭 표시가 "영상 없음" 위에 남던 문제. `LiveView.Clear`가 오버레이 갱신 플래그를 세우지 않아 `UpdateOverlay`가 지우지 않았음
  - 2026-11-03 로컬(합성 이미지 400x300, 저장소 밖 폴더): 획득 중 연결 해제·정지 후 연결 해제 모두 "영상 없음"만 남고 재연결 시 오버레이 다시 표시. 폴더 2개 추가 → settings.json 기록, 연결 중에는 [목록에서 제거] 비활성, 새로고침 유지 → 재시작 후 복원 → 1개 제거(파일 8개 그대로) → 재시작 후에도 빠져 있음. 복원된 폴더에 연결해 수신 30fps·드롭 0으로 획득 중 [새로] → 취소 시 도구 유지, 확인 시 도구 0개 "Recipe applied (0 tools)" 후 모든 프레임 OK → [+ Blob] → 첫 [저장]이 복원된 폴더에서 "다른 이름으로 저장" → 저장 확인. 작업 트리에 시험 파일 없음
  - 같은 날 Windows 테스트: `WebcamCameraDeviceTests.ManualExposure_ChangesBrightnessAndIsLockedWhileAuto` 실패(최소·최대 노출 평균 150.2 → 149.1, 노출 무시). 변경 전 `main`(c73d831)에서도 151.9 → 150.8로 같은 실패라 이번 변경과 무관. 인계 체크리스트 B(웹캠)에 적은 "드라이버가 노출 설정을 무시하는 상태"와 같은 증상으로 보이며, USB 재연결 후 재확인 필요
- [x] 프레임 저장 (단계 완료 후 추가) — 카메라 패널 [저장 폴더], [프레임 저장]
  - 대상물 샘플 수집용. 앱에 저장 경로가 없으면 pylon Viewer와 번갈아 카메라를 열어야 해서 판정을 보며 저장할 수 없음
  - `FrameCaptureConsumer`: 요청이 없으면 `Accepts`가 false라 큐와 풀링 버퍼를 쓰지 않음. 요청 1건에 다음 프레임 1장을 소비자 스레드에서 저장. 요청은 한 번에 하나라 용량 1 큐에서 넘치지 않음. 라이브뷰 슬롯에서 꺼내면 표시할 프레임을 뺏게 되어 파이프라인을 거침. 파일 쓰기는 `ImageSaveConsumer` 재사용
  - PNG만: JPEG 압축은 레시피 임계값에 영향을 줄 수 있음
  - 5초 안에 저장되지 않으면 오류 표시(트리거 대기 등). 시간 초과된 요청의 프레임은 다음 요청에 저장되지 않음. 트레이드오프: 상태바의 소비자 항목이 하나 늘어남
  - 2026-11-03 로컬(이미지 폴더 카메라, Debug): 획득 중에만 활성, 폴더 미지정 시 대화상자(취소하면 저장 안 함), 3장 저장 후 파일 3개와 `3장 저장` 표시, 드롭 0, 저장한 폴더를 [폴더 추가]로 열어 재생
  - 2026-11-03 실물(acA640-120gm, Release, 120fps): 3장 저장, 640×480 8비트 그레이 PNG 약 64 KB, 저장 중 드롭 0

**완료 기준**: 처음 보는 사람에게 시연 가능한 화면

---

## 7단계 — 포트폴리오 포장 (1일)

- [x] README: 기능, 구조 다이어그램, 실행 방법, 확장 방법(새 벤더 추가 절차) — Mermaid 구조도, 설계 원칙별 구현, 30분 측정 결과 포함. 시연 GIF 자리는 비워 둠
- [x] 에뮬레이터 + 이미지 폴더 기반 시연 GIF — `docs/media/inspection.gif`(판정·ROI 편집), `multivendor.gif`(폴더 → pylon 에뮬레이터 전환, 파라미터). UI Automation 녹화, 768x480 8fps. 웹캠 영상은 사생활 보호를 위해 제외
- [x] GitHub 공개 — PR #1 병합으로 main에 반영(2026-10-31)

**완료 기준**: README만 보고 기능과 구조를 이해할 수 있음

---

## 로컬 Windows 세션 인계 체크리스트

클라우드(Linux)에서 할 수 있는 작업은 0·1·2(ImageFolder)·4·5단계까지 완료. 브랜치 `claude/eager-rubin-59e98b`.
아래를 위에서부터 순서대로 진행한다.

### A. 환경 확인 (0단계 로컬 항목)

- [x] .NET 8 SDK 확인(`dotnet --list-sdks`). Visual Studio 2022 또는 SDK 8.0.4xx 이상 권장
  - 2026-10-27 로컬: winget으로 SDK 8.0.427 설치, `global.json`(8.0.100, `latestFeature`)에서 8.0.427 선택 확인
- [x] `dotnet build CamBridge.sln` — 경고 0 기대. 빌드 로그에 `CamBridge.Sources.Pylon: pylon SDK not found/found` 출력 확인
  - 2026-10-27 로컬(SDK 8.0.427, 클린 빌드): 경고 0·오류 0, `pylon SDK not found` 출력 확인
- [x] `dotnet test tests/CamBridge.Tests` — 93개 통과 기대(클라우드 결과). Windows용 OpenCV 런타임(`OpenCvSharp4.runtime.win`) 로드 확인
  - `DllNotFoundException`(OpenCvSharpExtern)이 나면 Visual C++ 재배포 패키지 설치 여부 확인
  - 2026-10-27 로컬: 93/93 통과, `runtimes/win-x64/native/OpenCvSharpExtern.dll` 로드 확인
- [x] `dotnet run --project src/CamBridge.App` — 빈 WPF 창 표시
  - 2026-10-27 로컬: "CamBridge" 창 표시 확인
- [x] pylon SDK 설치 후 `Basler.Pylon.dll` 실제 경로 확인 → `src/CamBridge.Sources.Pylon/CamBridge.Sources.Pylon.csproj`의 `PylonNetAssembly` 기본값 수정(현재 `$(PYLON_DEV_DIR)\Assemblies\Basler.Pylon\x64\Basler.Pylon.dll`은 추정값)
  - 실제: `$(PYLON_DEV_DIR)\Assemblies\Basler.Pylon\net8.0\x64\Basler.Pylon.dll`(SDK net8.0 샘플과 동일). 추정값은 `net8.0` 폴더가 빠져 있었음
  - 설치판이 NuGet 패키지 형태로 .NET API를 제공하는지도 확인(그렇다면 조건부 PackageReference로 전환 검토) — 설치 폴더에 .nupkg 없음, HintPath 참조 유지

### B. 2단계 로컬 — 웹캠

- [x] `WebcamCamera` / `WebcamProvider` (`net8.0-windows`). `ImageFolderCamera`를 참고 구현으로 사용(상태 전이, 파라미터 예외 규약, 획득 스레드, 프레임 소유권)
  - 2026-10-27 로컬(APC940): Windows 테스트 30/30 통과(3회 반복), 기존 93/93 통과
- [x] 노출·게인은 `VideoCapture` 백엔드(MSMF/DSHOW)별 지원이 달라 `IsAvailable` 판정 필요
  - DSHOW 전용 + COM `GetRange` HRESULT로 판정(재설정 성공 여부는 신뢰 불가 — 미지원 AutoExposure도 Set이 True 반환)
- [x] 획득 중 USB 분리 → `Disconnected` + `Faulted` 전환, 재연결 후 Close/Open 복구 (수동 확인)
  - 1차(읽기 실패 지속만 감지): 실패. 분리 후 DSHOW `Read()`가 실패하지 않고 약 1초마다 true 반환 → 오래된 프레임이 1fps로 계속 발행됨
  - 수정: `Read()`가 `SlowReadThreshold`(기본 500ms) 이상이면 발행 전 `DirectShowDevices.IsPresent`로 장치 확인, 없으면 프레임 폐기 후 `Disconnected` + `Faulted`
  - 2026-10-27 2차(APC940): 마지막 프레임 1.0초 후 감지, 이후 발행 프레임 0. Close 정상, 재연결 후 재오픈 3초 82프레임
- 참고: 비정상 종료 직후 드라이버가 노출 설정을 무시하는 상태가 1회 관측됨. 재연결로 해소, 재현 안 됨

### C. 3단계 — pylon 에뮬레이터

- [x] `set PYLON_CAMEMU=2` 후 열거 확인, 노출·게인·트리거·소프트웨어 트리거 매핑, 이름 기반 파라미터 → pylon 파라미터 맵
- [x] pylon의 grab 결과 버퍼를 `Frame.Rent` 버퍼로 복사(Core에 SDK 타입 노출 금지). FrameId는 block ID 사용 → 카메라측 드롭 통계에 그대로 반영
- [x] 분리·재연결 → `ErrorOccurred(Disconnected)` + `CameraState.Faulted` — 구현 완료, 실물 GigE로 검증(2026-10-31, 에뮬레이터는 분리 불가)
- [x] 실물(acA640-120gm, ace classic GigE) 대비: SFNC 1.x 이름(`ExposureTimeAbs`, `GainRaw`, `AcquisitionFrameRateAbs` 등, 도착 후 pylon Viewer로 확인)과 SFNC 2.x 이름을 노드 존재 여부로 둘 다 매핑. 에뮬레이터만으로는 이 차이가 검증되지 않음
  - 매핑 구현·단위 테스트 완료. 에뮬레이터는 신·구 이름을 모두 제공해 1.x 전용 경로는 **실물 검증 필요**
- [x] 디버그 빌드용 GigE `HeartbeatTimeout` 연장 옵션(브레이크포인트 정지 시 분리 방지)
  - 불필요로 확인: pylon이 디버거 감지 시 5분으로 자동 연장(SDK DeviceRemovalHandling 샘플 주석). 대신 `PylonOptions.HeartbeatTimeout`(기본 1s)을 디버거 없을 때만 적용해 분리 감지를 빠르게

### D. 4단계 로컬 — 장시간 실행

- [x] 에뮬레이터 30분 연속 획득 + 검사 소비자 + 저장 소비자. 작업 관리자/`dotnet-counters`로 프로세스 메모리·GC 힙 추이 확인
  - 도구: `tools/CamBridge.Soak`(dotnet-counters 대신 프로세스 내 측정, 반복 가능). `dotnet run -c Release --project tools/CamBridge.Soak -- --emulator --minutes 30` (기본은 실물 카메라, `--serial`로 지정, `--source folder`로 이미지 폴더도 가능). 결과는 `artifacts/soak/`(gitignore)의 samples.csv·summary.txt. 드롭·카메라 오류·Faulted가 있으면 FAIL
  - 구성: 에뮬레이터 640x480 Mono8(120fps 요청, 실제 약 92fps) → 파이프라인(검사 큐 2 + PNG 저장 1/100 큐 4) + 16ms 간격 라이브뷰 TryTake. 10초마다 워킹셋·Private·강제 Full GC 후 관리 힙·핸들·스레드·미반환 프레임 버퍼(계수 풀) 기록, 앞 20%는 워밍업으로 제외하고 기울기로 판정
  - 2026-10-27 결과 **PASS**: 30분 167,184프레임, 카메라 드롭 0 / 전체 드롭 0, 카메라 오류 0, PNG 1,672장. 정상 구간(6~30분) 기울기 — 남은 관리 힙 +0.025 MB/분(7.7~9.3MB 사이 진동), Private +0.012 MB/분(107.9→108.0MB), 핸들 −0.016/분(536 유지), 스레드 −0.035/분. 미반환 버퍼 최대 1, 정지 후 0/167,186
  - GC: gen0=gen1=gen2=541 — 전부 샘플링 때의 강제 GC, 자연 발생 GC 0회(프레임 버퍼 풀링으로 할당이 거의 없음)
  - 남은 관리 힙의 미세한 양(+)은 도구 자신이 쌓는 샘플 목록(10초당 1개) 수준. 실물 카메라 도착 후 같은 도구로 GigE에서 재실행 예정
  - 관측(2026-10-30): 전체 재빌드 직후 첫 실행에서 시작 5초 안에 소비자 큐 드롭 66(검사 57, 저장 9, 카메라측 0) 1회, 재빌드 없이 4회 반복하면 0. 새 DLL의 첫 로드·JIT 지연으로 추정(원인 미증명). 검증 실행은 빌드 직후를 피한다

### E. 6단계 — UI 연결 메모

- 카메라 목록: `CameraRegistry`(ImageFolder는 `samples/part-inspection/images`를 기본 폴더로 등록, 실행 중 [폴더 추가]로 확장, 추가한 폴더는 사용자 설정에서 다음 실행에 복원)
- 라이브뷰: `AcquisitionPipeline.LiveView.TryTake()`를 `CompositionTarget.Rendering` 또는 `DispatcherTimer`에서 호출 → `WriteableBitmap`에 복사 후 프레임 Dispose
- 통계 상태바: `pipeline.GetStatistics()`를 약 4Hz로 폴링(수신 FPS, 소비자별 처리 FPS·큐 길이·드롭, `TotalDroppedFrames`)
- 검사: `InspectionEngine.Load("samples/part-inspection/part.recipe.json")` + `InspectionConsumer`. `ResultProduced`는 워커 스레드에서 발생 → Dispatcher 마샬링. 오버레이는 `InspectionResult.Overlays`(이미지 좌표)
- 실행 중 레시피(ROI) 변경: 현재 `InspectionEngine`은 스레드 안전하지 않음 → 새 엔진 생성 후 소비자에서 원자적 교체하는 방식 설계 필요 — 6c에서 `InspectionConsumer.ReplaceEngine`으로 해결. 검사 결과도 Dispatcher 마샬링 대신 `LatestResult`를 pull

---

## 카메라 도착 후 (1~2일)

카메라: Basler **acA640-120gm** (GigE Vision, Mono, 센서 659×494, 라벨과 연결로 모델 확인). Mono8 기준 약 39MB/s로 1GbE에 여유 있음. 이 카메라는 전원을 켜면 기본 사용자 설정(`UserSetDefaultSelector = UserSet1`)의 값으로 시작: 640×480, 노출 450 µs, 게인 300, 패킷 576바이트, 프레임 레이트 제한 꺼짐, 자유 실행 약 127fps. 고정 IP와 함께 이전 사용 설정으로 보임. 저장값은 바꾸지 않았고, 아래 검증은 모두 640×480 기준. 전원은 PoE(802.3af) 또는 I/O 커넥터 12V 중 하나만(동시 공급 금지).

랜카드: **NEXT-POE3101EX** (Intel WGI210AT, PCIe v2.0 x1, RJ-45 1포트, PoE+ 최대 30W). 인터넷용 어댑터와 분리된 카메라 전용. 표준 802.3af/at 여부와 보조 전원 단자는 판매 페이지에서 확인되지 않아 설치 때 실물로 확인. 케이블: Cat5e, 카드와 카메라 직결.

시리얼·MAC은 공개 저장소에 적지 않는다(카메라 라벨 참고).

### 실물 연결 순서

직접 진행:

1. PC 전원을 끄고 카드 장착. 보조 전원 단자가 있으면 연결. 부팅 후 장치 관리자에서 I210 인식 확인. 이 PC(ASUS EX-B660M-V5 D4)는 BIOS 0419에서 카드를 꽂으면 로고 화면에서 멈췄고, BIOS 4004로 올린 뒤 정상 부팅(BIOS 설정도 함께 바꿨는지는 미확인).
2. 점검: `powershell -ExecutionPolicy Bypass -File tools\camera-nic.ps1` — 어댑터 목록과 현재 설정 표시, 변경 없음.
3. 설정: **관리자 PowerShell**에서 같은 명령에 `-Apply`. 점보 프레임 9014, 수신 버퍼 최대, 고정 IP 192.168.10.1/24(게이트웨이 없음), 이 인터페이스 한정 UDP 방화벽 규칙. 게이트웨이가 있는 어댑터(인터넷용)는 거부. `-WhatIf`로 미리 보기 가능.
4. 렌즈를 끼우고 카메라 연결. 카드 LED: **파랑(PoE 공급) + 노랑(1000M)** 확인. 2번 명령으로 Link Up, 1 Gbps 재확인.
5. pylon IP Configurator: 라벨의 MAC으로 카메라를 찾아 고정 IP 192.168.10.2/24 저장(같은 서브넷, Wi-Fi의 192.168.0.x와 다름).
6. pylon Viewer: 연결 → 라이브 영상 → 초점·조리개 조정 → 120fps 몇 분, 드롭·재전송 확인. 여기까지 되면 SDK와 네트워크는 정상.

문제가 생기면:

| 증상 | 확인 |
|---|---|
| 파란 LED 꺼짐, 카메라 안 켜짐 | 보조 전원 단자 연결, 다른 케이블. 카드가 표준 PoE인지 |
| 노란 LED 꺼짐(100M) | 케이블 교체 |
| IP Configurator에 안 보임 | 어댑터 Link Up 여부, 방화벽 규칙, pylon GigE 드라이버 바인딩(2번 명령) |
| 보이는데 "not reachable" | 카메라 IP를 어댑터와 같은 서브넷으로 설정 |
| 영상이 끊기거나 재전송이 늘어남 | 패킷 크기(시작 시 자동 협상, 고정하려면 `--set GevSCPSPacketSize=8192`), 점보 프레임, 케이블, Performance 드라이버 |
| pylon 검색 0대인데 GVCP 응답은 옴 | 카메라 IP가 다른 서브넷. 이 카메라는 처음에 192.128.0.10이었음 |
| 재연결 후 화면이 검게 나옴 | PoE 재시작으로 카메라가 저장값으로 돌아감(이 카메라는 노출 450 µs). 노출을 다시 설정 |

Claude와 함께 진행(앱·도구 쪽 준비 완료, 실행은 카메라 필요):

- [x] 실물 검증 테스트: `dotnet test tests/CamBridge.Tests.Windows --filter "FullyQualifiedName~PylonRealCamera" --logger "console;verbosity=detailed"` — 12개 통과(2026-10-31, pylon 26.09.2, 전송 손실 관련 4개 포함). SFNC 1.x 노드(`ExposureTimeAbs`, `GainRaw`, `AcquisitionFrameRateAbs`), BlockID 1부터, 소프트웨어·하드웨어 트리거 입력 대기, 전송 통계 확인. 하드웨어 트리거는 입력 없이 프레임이 나오지 않는 것까지만 확인(실제 신호 미입력)
  - 2026-11-03 PR #2와 #3을 병합한 `main`(`c73d831`)에 프레임 저장을 더한 커밋(`58c77ca`)에서 12개 통과, 건너뜀 0. Windows 테스트 전체는 106개 통과, 기존 웹캠 `ManualExposure` 1개 실패
  - 2026-11-05 PR #5까지 병합한 `main`(`a6a244`)에서 Windows 테스트 전체 124개 통과, 실패·건너뜀 0(실물 카메라와 웹캠 연결 상태, 웹캠 `ManualExposure` 포함)
  - FreeRun(100fps 5초)은 22회 중 첫 실행 1회만 실패: 버퍼 언더런 10, 패킷 손실·재전송 0. 드롭 위치 기록 추가 전이라 어디서 끊겼는지 모름. 같은 패킷 크기 576으로 8회 반복은 모두 통과. **원인 미확인**
- [x] 앱 확인: "Basler pylon" 그룹에 표시, 연결·라이브뷰·파라미터 패널 동작. 연속 21분 33초, 127fps, 164,272프레임, 드롭 0(카메라측 0), BlockID 65535 순환 2회 통과. 노출 변경이 영상 밝기에 반영되고 초점을 맞춘 영상에서 글자가 읽힘. 샘플 레시피는 실물 영상과 맞지 않아 전부 NG이며, 실제 대상물 검사는 미확인
- [x] 분리 감지: 획득 중 랜선 분리 → 첫 불완전 프레임 경고 후 약 1.5초에 `Disconnected` → 상태 "오류"(하트비트 1초, Windows 링크 Down 기록보다 0.4초 빠름). 다시 꽂으면 링크가 한 번 더 끊겼다 복구되고(PoE 재시작으로 추정) 저장한 IP 유지. 연결 해제 → 연결 → 시작으로 복구
  - 재연결 10회 반복: private 432~444 MB, 핸들 약 800에서 증가 없음. 첫 재연결에서 390 → 418 MB로 한 번 늘고 유지
- 실물에서 발견해 수정:
  - 패킷 크기: 전원 투입 시 576바이트(프레임당 571패킷). `PylonOptions.AutoPacketSize`(기본 켬)로 시작 때 협상해 9000바이트, 프레임당 37패킷. `GevSCPSPacketSize`를 이름으로 지정하면 다시 열 때까지 협상을 끔. 점보 프레임이 없는 NIC에서는 미확인
  - 픽셀 포맷 목록: `GetAllValues`는 이 모델에 없는 값(Bayer, Mono10)도 돌려줌. 설정 가능한 값(Mono8, Mono12, Mono12Packed, YUV422Packed, YUV422_YUYV_Packed)만 표시하고, 획득 중에는 현재 값만 표시. Mono8 외 형식은 선택 시 "지원하지 않음" 오류를 1회 보고
  - `camera-nic.ps1 -Apply`: `New-NetIPAddress`가 활성 저장소에만 적용하고 영구 저장소에서 실패 → `netsh`로 교체, 부팅 시 읽는 값 검증 추가, 이미 맞는 고급 속성은 건너뜀(어댑터 재시작 방지)
- 미해결: `WebcamCameraDeviceTests.ManualExposure`가 2026-10-31 오후에 5회 연속 실패, 약 1시간 반 뒤 전체 테스트에서는 통과. 실패 때는 노출값이 적용으로 나오지만 최소·최대 모두 평균 밝기 약 148. 웹캠 코드 변경 없음, 나머지 웹캠 테스트 8개는 계속 통과. 간헐 실패로 보이며 **원인 미확인**. 2026-11-03에도 실패했고, 2026-11-05 전체 실행(124개)에서는 통과
- [x] 장시간 실행: `dotnet run -c Release --project tools/CamBridge.Soak -- --minutes 30` — 2026-10-31, Mono8, 120fps. 640×480 **2차 PASS**, 전체 센서 659×494 **3차 PASS**
  - 3차(`--width 659 --height 494`, 링크 감시 없음, 소비자별 최대 큐·처리 시간 계측 추가): 수신 216,170프레임, 전체 드롭 0(카메라측 0), 실패 버퍼·언더런·패킷 손실·재전송 0, 카메라 오류 0. 6~30분 기울기(전부 기준 이내): private +0.029 MB/min, 관리 힙 +0.000 MB/min, 핸들 -0.131/min, 스레드 -0.020/min. 대여 버퍼 최대 1, 정지 후 0
  - 3차 계측: 저장 1회 최대 67ms(40~50초 구간에 1회, 이후 갱신 없음), 저장 큐 최대 1/4. 검사 1회 최대 10ms, 검사 큐 최대 2/2(첫 10초 안, 드롭 없음)
  - 계측으로 본 1차 FAIL: 수정 전 구조(용량 4, 프레임 간격 8.3ms)에서 드롭 2는 저장 1회 약 50ms면 나오는 값(계산). 감시 스크립트가 없는 3차에서도 67ms가 나왔으므로 특별한 장애 없이 일어날 수 있는 크기. 다만 1차의 실제 지연은 측정하지 못했고 67ms의 원인도 **미확인**
  - 2차(저장 큐 수정 후): 수신 216,160프레임, 전체 드롭 0(카메라측 0), 실패 버퍼·언더런·패킷 손실·재전송 0, 카메라 오류 0. BlockID 65535 순환 3회 통과로 FrameId 연속화 확인
  - 2차 메모리(6~30분 기울기, 전부 기준 이내): private -0.030 MB/min, 관리 힙 +0.002 MB/min, 핸들 -0.026/min, 스레드 -0.029/min. 이 구간 대여 버퍼 최대 1, 정지 후 0
  - 1차(FAIL): 수신 216,162프레임, 카메라측 드롭 0, 28분 11초 샘플에서 처음 잡힌 저장 큐 드롭 2. 저장 소비자가 1/100만 저장하면서도 모든 프레임을 용량 4 큐(120fps에서 약 33ms)로 받는 구조였음
  - 수정: `IFrameConsumer.Accepts`로 저장할 프레임만 큐에 넣음. 여유가 약 3초로 늘고, 드롭은 실제로 저장을 놓친 경우만 뜻함. 처리 수도 저장한 장수가 됨
  - 1차에서 저장을 지연시킨 원인 자체는 **미확인**. 2차 PASS는 같은 지연이 다시 없었을 수도 있어 원인 제거의 증거는 아님. 지연이 있어도 드롭이 나지 않는 것은 회귀 테스트(`SaveEveryNth_SlowStorage_DoesNotDropSkippedFrames`)로 확인
  - 측정 조건: 두 실행 모두 분리 감지 때 띄운 링크 감시 스크립트가 끄지 못한 채 함께 돌았음(`Get-NetAdapter` 초당 약 5회, 호출당 약 90ms). 의도한 부하가 아니며 PASS는 이 부하를 포함한 결과. 1차 지연의 후보에서 제외되지 않음. 수정 후에는 약 3초 이내의 지연이 드롭으로 나타나지 않으므로 2차로는 가릴 수 없음
  - 같은 감시 기록에서 두 실행 중 링크 변화 없음(마지막 변화 17:34:09, 1차 시작 17:40). 1차 드롭 구간(18:08:39~18:08:49) 전후 2분의 Windows System·Application·작업 스케줄러·Defender 로그에도 이벤트 없음. 로그에 남지 않는 지연은 이 확인으로 걸러지지 않음
  - 1분 시운전: 1차 빌드 직후 첫 실행은 소비자 드롭 64(검사 54, 저장 10, 카메라측 0), 빌드 없이 바로 재실행하면 0. 2차 빌드 직후 첫 실행은 0. 원인 미증명
  - 2분 20초에 초점 조정 때문에 중단한 실행(드롭 0)은 판정에서 제외
- [x] 도착 전 준비: NIC 점검·설정 스크립트(`tools/camera-nic.ps1`), 실물 검증 테스트, Soak 도구의 실물 카메라 지원, 연결 체크리스트
  - 카드 도착 전에는 스크립트의 점검 경로와 거부 경로(인터넷 어댑터, I210 없음)만 실행 확인. `-Apply`는 카드 장착 후 실제 적용 확인(2026-10-31). 실물 테스트 코드는 에뮬레이터를 실물로 간주해 일시 실행해 통과 확인(되돌림)
- [x] 실물 카메라 구현 연결: 기존 `PylonCamera`로 연결, Core·상위 계층 변경 없음
- [x] 카메라 네트워크 주소(2026-11-06): `CameraInfo.Network`(`NetworkAddress`: IPv4 주소, 서브넷 마스크, 게이트웨이)를 열거 단계에서 채움. 카메라를 열지 않고도 어느 NIC와 같은 서브넷인지 판단하기 위함. Core에는 벤더 중립 필드 하나만 추가
  - pylon 26.09.2의 GigE 열거 정보로 확인: `IpAddress` "192.168.10.2", `SubnetMask` "255.255.255.0", `DefaultGateway` "0.0.0.0"(→ 게이트웨이 없음)
  - 넣지 않은 것: 호스트 NIC 주소 키(`NetworkInterfaceIpAddress`)는 이 카메라의 열거 정보에 없었음. 현재 IP 구성(`IpConfigCurrent` = "5")은 이 카메라(SFNC 1.x)에 영구 IP·DHCP·LLA 불리언 노드가 없어 비트 의미를 실물로 대조할 수 없었음
  - 테스트: 주소 해석 14개, 실물 GigE 카메라의 주소(`192.168.10.2/24`), 에뮬레이터는 주소 없음. Windows 테스트 전체 125개 통과, 건너뜀 0
- [x] 전송 진단용 grab 관찰(2026-11-06): 다른 프로그램의 설정 그대로 전송만 재기 위한 `PylonOptions` 3개. 기본값은 지금 동작 그대로
  - `GrabObserver`: 모든 grab 결과(실패 포함)를 복사·변환 전에 grab 스레드에서 받음. 펼친 FrameId, 오류 코드, 장치·호스트 타임스탬프, payload 크기, 픽셀 포맷 이름. block ID가 0인 실패는 FrameId가 없고 다음 정상 프레임 앞의 공백으로 나타남
  - `DeliverFrames = false`: 복사·변환과 `FrameReceived`를 건너뜀. 이 소스가 화면에 낼 수 없는 포맷(Mono12 등)도 셈
  - `ReplaceUnsupportedPixelFormat = false`: 열 때 Mono8로 바꾸지 않음
  - 실물 확인: Mono12에서 관찰만 켜고 1초 받음, 프레임 전달 0, 모든 grab의 payload가 화소 수 × 2, FrameId 연속. 닫았다 열어도 Mono12 유지
  - 발견: 에뮬레이터는 닫으면 설정이 기본값으로 돌아가, 기존 `Open_ReplacesAnUnsupportedPixelFormatWithMono8`은 에뮬레이터에서 교체 동작을 가려내지 못함(교체가 없어도 Mono8). 다시 열어도 유지되는지는 실물 테스트로 확인
  - Windows 테스트 전체 128개 통과, 건너뜀 0. pylon SDK 없이 빌드 확인
- [x] 카메라측 드롭(패킷 손실·재전송) 통계를 실물로 시연(2026-10-31) — 소켓 드라이버 버퍼를 최소(1 KB)로 줄여 호스트에서 실제 패킷 손실을 일으킴. 케이블·스위치 결함이 아닌 의도적 손실. 파라미터 패널이나 Soak `--set StreamType=SocketDriver,SocketBufferSize=1,GevSCPSPacketSize=6000`으로 재현
  - 측정(640×480, 120fps, 소켓 1 KB): 패킷 9000은 재전송 요청이 프레임당 약 1회(5초 600~602), 실패 0. 6000은 10회 실행에서 재전송 875~2,184, 실패 0~16. 3000은 4회 중 3회 실패 0~2, 1회 실패 271. 4500·1500·1000·576은 전부 실패. 기본값(필터 드라이버, 소켓 4096 KB)과 소켓 16 KB 이상 + 패킷 576은 실패 없음
  - 발견한 결함: 드롭을 다음 정상 프레임의 ID 공백으로만 세어, 모든 프레임이 실패하면 드롭이 0으로 표시됨. 실패한 grab도 유효한 BlockID를 돌려주는 것을 확인
  - 수정: `CameraErrorEventArgs.LostFrameId`(선택 속성, Core), 실패한 grab의 ID 발행(`PylonCamera`), 손실 ID까지 전진해 공백과 이중 집계 없이 즉시 집계(`AcquisitionPipeline`). 프레임 오류 로그는 초당 1줄로 제한(`LogThrottle`), 이전에는 실패 프레임마다 경고. `StreamType`·`SocketBufferSize`를 이름으로 접근 가능(`PylonCamera`, Core 변경 없음)
  - 확인: 실물 테스트에서 수신 + 드롭 = ID 범위. 일부 실패 실행은 수신 480 + 드롭 120 = 600(실패 보고 44, 언더런 76, 보고 없는 손실은 공백으로 집계), 전부 실패 실행은 수신 0 + 드롭 579 = 579
  - 앱: 패킷 6000에서 15초 후 드롭 211(카메라 83, 검사 큐 128), 패킷 4500에서 10초 후 `수신 0.0 fps`, `드롭 1,218 (카메라 1,218)`, 경고 로그 초당 1줄. 검사 큐 드롭은 재전송 뒤 프레임이 몰려 도착한 탓으로 보이나 미확인
  - **미확인**: `EnableResend=false`에서도 재전송 요청이 그대로 나와 이름 접근에서 제외. 패킷 크기와 손실량이 비례하지 않고 같은 조건에서도 손실량이 달라져 테스트는 수량이 아니라 집계 일치만 확인. 실패가 몰리면 `Statistic_Failed_Packet_Count`가 총 패킷 수보다 커지므로(115,007개 중 약 35억) 판단은 `Failed_Buffer_Count` 기준
  - 앱 Debug 빌드의 첫 시작 5초에 손실 유발 없이 검사 큐 드롭 54(카메라측 0). Release Soak 첫 1분은 0. 1분 시운전의 검사 54와 같은 수이나 원인 미증명
  - 2026-11-03 카메라 없이 비슷한 현상 관찰: 이미지 폴더 카메라(30fps), Debug 빌드 직후 첫 실행에서 시작 후 약 4초에 검사 큐 드롭 12, 빌드 없이 다시 실행한 7회는 0. 콜드 스타트로 보이나 원인 미증명
  - 2026-11-03 실물 Release(120fps, 샘플 레시피): 빌드 직후 첫 실행에서 시작 1초 안에 검사 큐 드롭 55(카메라측 0), 이후 20초는 증가 없음. 빌드 없이 다시 실행한 3회는 0. 같은 바이너리를 새 폴더에 복사해 처음 실행한 것과 그 재실행도 0이라 "새 경로의 첫 실행"만으로는 재현되지 않음. 원인 미증명. 시연 녹화는 빌드 후 앱을 한 번 실행하고 닫은 다음 시작한다
- [x] 미확인 항목 재측정(2026-11-06): 위의 "원인 미확인" 네 건(FreeRun 첫 실행 언더런, 1차 Soak 저장 큐 드롭, 빌드 직후 첫 실행의 소비자 드롭, 전송 손실 시연의 검사 큐 드롭)을 `GrabObserver`로 전송만 재는 측정(프레임 전달·복사 끔)으로 다시 봄. 640×480 Mono8, 127fps free-run, 1분(시작 5초는 따로 셈), 따로 적지 않으면 패킷 576
  - 빌드 직후 첫 실행: 내용만 바꾼 빌드(`-p:SourceRevisionId=<임의값>`, 우리 어셈블리 8개만 바이트가 바뀜) 5벌의 첫 실행, 재실행 6회, 실행해 본 빌드를 새 폴더에 복사한 첫 실행 3회 모두 시작 구간 손실·언더런 0. 측정 도구에서는 재현되지 않음
  - 같은 조건에서 Defender 성능 기록(`New-MpPerformanceRecording`, 기록만 하고 설정은 바꾸지 않음): 새로 빌드한 DLL을 처음 여는 프로세스에서 파일마다 동기 검사 49~117 ms, DLL 5개 합 약 460 ms. 바이트가 그대로인 `Basler.Pylon.dll`도 빌드가 새 폴더에 복사한 것은 116 ms. 빌드 중 쓰기 때의 검사는 파일당 약 0.1 ms. 같은 빌드의 재실행과, 그 빌드를 새 폴더에 복사한 첫 실행에서는 DLL 검사가 기록되지 않음(실행 파일 34 ms만)
  - 해석: 측정 도구는 grab 시작 전에 DLL을 모두 열어 이 시간이 손실로 나타나지 않음. 앱에 획득 시작 뒤 처음 여는 어셈블리가 있으면(예: 검사 소비자의 첫 처리) 같은 크기의 정지가 그 스레드에 들어가고, 120fps에서 검사 큐 드롭 55장은 약 0.46초로 크기가 맞음. 11-03의 "복사본 첫 실행 0"과도 맞음. 앱의 첫 실행을 기록한 것은 아니므로 **원인 유력, 앱에서는 미증명**. 시연 전에 한 번 실행해 두는 운영은 그대로 유지
  - FreeRun 첫 실행 언더런 10: 테스트도 빌드 직후 실행이라 같은 범주로 봄. grab 중 처음 여는 어셈블리가 있었는지는 기록하지 않았으므로 미증명
  - 전송 손실 시연의 검사 큐 드롭 128: 소켓 드라이버 1 KB + 패킷 6000에서 재전송을 기다린 프레임 뒤로 프레임이 한꺼번에 도착함. 연속 프레임이 카메라 간격의 절반 미만으로 이어진 묶음이 최대 12~14장(1분 3회, 묶음에 든 프레임 1,102~1,163장). 같은 패킷의 필터 드라이버는 최대 3장. 검사 큐(용량 2)가 받을 수 있는 크기를 넘으므로 "몰려 도착"과 일치. 앱과 동시에 잴 수 없어 128과 수량 대조는 못 함. 이번 손실량(1분에 약 5,200장)은 10-31보다 훨씬 커서 같은 조건으로 보지 않음
  - 1차 Soak 저장 지연(드롭 2, 3차 최대 67 ms): 패킷 9000으로 30분, 228,756프레임 손실·재전송 0. 카메라 간격은 일정한데 grab 스레드 도착 간격은 최대 30.6 ms, 20 ms 초과는 1,800초 중 11초. 부하가 거의 없을 때 PC 전체가 50 ms 넘게 멈춘 일은 없었으므로 67 ms는 저장 경로 자체(인코딩·쓰기·새 파일 검사)의 시간일 가능성이 큼. 원인 확정 아님
  - 새로 본 것: 패킷 576에서 12:16~12:30의 1분 측정 9회 중 5회가 손실 2~5장(합 15, 모두 재전송 동반). 같은 날 10:14의 5분 측정과 13:41 이후 1분 측정 5회는 0, 패킷 9000은 30분 0. 카메라 설정 값과 NIC 설정은 같음. **원인 미확인**
  - 함께 확인: 재전송을 기다린 프레임의 실패 결과가 다음 프레임보다 늦게 나옴(ID X+1 다음에 X). `AcquisitionPipeline`은 마지막보다 낮은 손실 ID를 이미 공백으로 센 것으로 보고 넘기므로 이중 집계되지 않음. 늦게 온 결과가 정상 프레임이면 카운터 재시작으로 처리해 드롭을 두 번 셀 수 있음. 이번에 늦게 온 결과는 모두 실패였으므로(`Failed_Buffer_Count`와 일치) 고치지 않고 기록만 함
- [ ] 조명·렌즈 세팅, 실물 촬영 시연 영상 제작 — 렌즈 초점 확인 완료(2026-10-31), 임시 조명 구성 완료(2026-11-05), 영상 제작은 남음
  - 2026-11-05 임시 조명: 태블릿(아이패드 에어) 흰 화면을 백라이트로 사용, 자동 밝기 끔. 산업용 백라이트를 사기 전 임시 구성이며 영상에도 그렇게 표기
    - 깜빡임: 프레임별 배경 평균의 표준편차 0.27~0.38 gray(0.13~0.16%). 가장 큰 주기 성분은 약 60Hz(화면 주사율로 추정), 진폭 0.3~0.4 gray
    - 화면 화소 격자의 모아레(주기 약 2.5px, 미세 구조 표준편차 4.8 gray)가 배경에 남음. 임계값 100과 멀어 블롭은 생기지 않음
    - 노출 4000µs에서는 배경의 절반 이상이 포화(255)되어, 조명이 바뀌어도 배경값에 드러나지 않으면서 에지 위치는 움직였음. 3000µs로 낮춰 배경 약 195, 포화 0
    - 시험 도중 배경이 계단식으로 밝아진 일이 있었음(자동 밝기로 추정). 이후 촬영 중 배경 변동은 ±2%
  - 셋업 보정: 처음에는 카메라가 약 15° 기울어 와셔가 타원으로 찍히고 에지 폭(10~90%)이 8px. 바로잡은 뒤 기울기 약 3°, 에지 폭 약 4px
  - 배율: 외경 16mm 평와셔 기준 0.0394mm/px, 시야 약 25×19mm. 구멍을 영상으로 재면 4.38mm, 캘리퍼스로는 4.4mm. 와셔가 화면 높이의 84%(405px)를 차지해 위치 허용은 중심 ±30px(약 1.2mm)
  - 실물 반복성(고정 임계값 100):
    - 같은 와셔를 20번 놓았을 때 고리 면적 표준편차 22.5px(0.02%)
    - 다른 와셔 20개는 0.12~0.13%
    - 같은 와셔를 뒤집으면 고리 면적 −134px, 구멍 −28px(10쌍, 방향 일정)
  - 합성 사전 확인과 비교: 실물은 시야가 좁고(25mm) 와셔 사이 지름 편차가 약 0.008mm라 가정보다 유리함. 면적 범위를 평균 ±4σ로 잡으면 외곽 결손 약 1mm²(면적의 약 0.5%)부터 벗어나는 수준(계산값)
  - 굽힘은 실루엣 면적에 거의 나타나지 않음:
    - 굽은 쪽이 기울어 짧아지는 효과와 렌즈에 가까워져 커지는 효과가 약 6°에서 상쇄됨
    - 굽힘 시료로 확인한 결과, 약한 굽힘은 정상과 구별되지 않았고, 센 굽힘은 구멍이 찌그러진 경우만 잡힘
    - 그래서 시연용 결함은 외곽 결손·구멍 확대 같은 치수 결함으로 준비 중
  - 밝기 민감도: 면적 범위가 ±0.5% 수준이면 이미지가 10% 밝아질 때 정상이 불량으로 바뀜. 조명을 ±3% 안으로 유지해야 함
  - 위치·정지 확인 후 자동 촬영과 캡처별 배경 기록은 임시 도구로 했음(저장소에 넣지 않음)
  - 구성 계획(장비 미구입): 책상 위 백라이트, 위에서 내려다보는 카메라, 평와셔 실루엣. 현재 엔진에 위치 보정이 없어 회전과 무관한 원형 대상을 고름. 결함은 직접 만들고 영상에 그렇게 표기. 실루엣이라 표면 결함은 볼 수 없음
  - 합성 영상 사전 확인(코드는 저장소에 넣지 않음, 판정 논리만 확인): `Ring`(어두운 블롭 1개, `MaxArea` 평균 ±4σ) + `Hole`(밝은 블롭 2개), 고정 임계값 100. 가정값은 M8, 시야 폭 약 35mm, 치수 ±0.05mm, 위치 ±40px, 배경 밝기 180~245
    - OK 200장 오판 0. 각 40장에서 구멍 완전·50%·25% 막힘, 결손 반경 4·3mm, M6·M10 혼입, 부품 없음, ROI 이탈은 40장 전부 검출. 구멍 10% 막힘 1장, 5% 0장, 결손 2mm 4장, 1mm·0.5mm 0장, 구멍 지름만 9mm 19장
    - 전체 면적으로 판정해 이 가정에서는 면적이 약 6% 넘게 변해야 안정적으로 잡힘. 결함 부위만 보는 ROI는 위치 보정이 있어야 쓸 수 있음
    - Otsu는 변동이 약간 작지만(1.36%, 고정 1.51%) 부품 없는 영상에서 엉뚱한 임계값을 잡음. 실물 분리 여유, 와셔 두께에 따른 시차, 금속 반사는 **미확인**(2026-11-05 실물 결과는 위 항목)
- [ ] README·크몽 서비스 페이지에 실물 시연 반영
