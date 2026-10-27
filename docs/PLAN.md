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
- [ ] [로컬] Basler pylon SDK 설치 확인 및 .NET API 참조 경로 확인 (개발자가 설치)
  - **로컬 검증 필요**: `CamBridge.Sources.Pylon.csproj`의 기본 경로
    `$(PYLON_DEV_DIR)\Assemblies\Basler.Pylon\x64\Basler.Pylon.dll`은 추정값. 실제 설치 경로 확인 후 수정
    (임시로 `dotnet build -p:PylonNetAssembly=<경로>`로 지정 가능). SDK 감지 시 빌드 로그에 `pylon SDK found` 출력, `HAS_PYLON` 상수 정의
- [ ] [로컬] Windows에서 `dotnet build CamBridge.sln`, `dotnet test`, WPF 빈 창 실행 확인 (**로컬 검증 필요**)

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

- [ ] `PylonProvider` / `PylonCamera` — Basler pylon .NET API
- [ ] `PYLON_CAMEMU` 환경변수로 에뮬레이션 카메라 열거 확인
- [ ] 노출·게인·트리거 모드·소프트웨어 트리거 매핑
- [ ] 이름 기반 파라미터 접근을 pylon 파라미터 맵에 연결
- [ ] SDK 미설치 환경에서도 솔루션 빌드·실행 가능하도록 처리 (플러그인 로딩 또는 조건부 참조)
- [ ] 카메라 분리·재연결 등 오류 이벤트 처리

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
- [ ] 장시간 실행 테스트 (예: 에뮬레이터 30분 연속) — 메모리 증가 없음 확인
  - [x] [클라우드] 합성 카메라 고속 생성 2초 소크 테스트: 풀 대여=반납(누수 0), 소비자별 처리+드롭=수신 정확 일치. 단일 코어 포함 반복 실행 안정
  - [ ] [로컬] **로컬 검증 필요**: 3단계 완료 후 pylon 에뮬레이터 30분 연속 획득, 프로세스 메모리 추이 확인

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

**완료 기준**: 처음 보는 사람에게 시연 가능한 화면

---

## 7단계 — 포트폴리오 포장 (1일)

- [ ] README: 기능, 구조 다이어그램, 실행 방법, 확장 방법(새 벤더 추가 절차)
- [ ] 에뮬레이터 + 이미지 폴더 기반 시연 GIF
- [ ] GitHub 공개

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
- [ ] pylon SDK 설치 후 `Basler.Pylon.dll` 실제 경로 확인 → `src/CamBridge.Sources.Pylon/CamBridge.Sources.Pylon.csproj`의 `PylonNetAssembly` 기본값 수정(현재 `$(PYLON_DEV_DIR)\Assemblies\Basler.Pylon\x64\Basler.Pylon.dll`은 추정값)
  - 설치판이 NuGet 패키지 형태로 .NET API를 제공하는지도 확인(그렇다면 조건부 PackageReference로 전환 검토)

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

- [ ] `set PYLON_CAMEMU=2` 후 열거 확인, 노출·게인·트리거·소프트웨어 트리거 매핑, 이름 기반 파라미터 → pylon 파라미터 맵
- [ ] pylon의 grab 결과 버퍼를 `Frame.Rent` 버퍼로 복사(Core에 SDK 타입 노출 금지). FrameId는 block ID 사용 → 카메라측 드롭 통계에 그대로 반영
- [ ] 분리·재연결 → `ErrorOccurred(Disconnected)` + `CameraState.Faulted`
- [ ] 실물(acA640-120gm, ace classic GigE) 대비: SFNC 1.x 이름(`ExposureTimeAbs`, `GainRaw`, `AcquisitionFrameRateAbs` 등, 도착 후 pylon Viewer로 확인)과 SFNC 2.x 이름을 노드 존재 여부로 둘 다 매핑. 에뮬레이터만으로는 이 차이가 검증되지 않음
- [ ] 디버그 빌드용 GigE `HeartbeatTimeout` 연장 옵션(브레이크포인트 정지 시 분리 방지)

### D. 4단계 로컬 — 장시간 실행

- [ ] 에뮬레이터 30분 연속 획득 + 검사 소비자 + 저장 소비자. 작업 관리자/`dotnet-counters`로 프로세스 메모리·GC 힙 추이 확인

### E. 6단계 — UI 연결 메모

- 카메라 목록: `CameraRegistry`(ImageFolder는 `samples/part-inspection/images`를 기본 폴더로 등록)
- 라이브뷰: `AcquisitionPipeline.LiveView.TryTake()`를 `CompositionTarget.Rendering` 또는 `DispatcherTimer`에서 호출 → `WriteableBitmap`에 복사 후 프레임 Dispose
- 통계 상태바: `pipeline.GetStatistics()`를 약 4Hz로 폴링(수신 FPS, 소비자별 처리 FPS·큐 길이·드롭, `TotalDroppedFrames`)
- 검사: `InspectionEngine.Load("samples/part-inspection/part.recipe.json")` + `InspectionConsumer`. `ResultProduced`는 워커 스레드에서 발생 → Dispatcher 마샬링. 오버레이는 `InspectionResult.Overlays`(이미지 좌표)
- 실행 중 레시피(ROI) 변경: 현재 `InspectionEngine`은 스레드 안전하지 않음 → 새 엔진 생성 후 소비자에서 원자적 교체하는 방식 설계 필요 — 6c에서 `InspectionConsumer.ReplaceEngine`으로 해결. 검사 결과도 Dispatcher 마샬링 대신 `LatestResult`를 pull

---

## 카메라 도착 후 (1~2일)

구매 모델: Basler **acA640-120gm** (GigE Vision, Mono, 659×494, 최대 약 120fps — 사양은 도착 후 확인). Mono8 기준 약 39MB/s로 1GbE에 여유 있음.

- [ ] 도착 전 준비: 전용 NIC, 점보 프레임(9000), pylon GigE 필터/Performance 드라이버, 방화벽 예외, IP Configurator로 고정 IP 또는 LLA, 전원(PoE 인젝터·스위치 또는 6핀 I/O 12–24V)
- [ ] 실물 카메라 구현 연결 (Basler면 기존 `PylonCamera` 그대로, HIKROBOT이면 `Sources.Hik` 추가)
- [ ] block ID 기반 카메라측 드롭(패킷 손실·재전송) 통계를 실물로 시연 — 웹캠에서는 측정 불가했던 항목
- [ ] 조명·렌즈 세팅, 실물 촬영 시연 영상 제작
- [ ] README·크몽 서비스 페이지에 실물 시연 반영
