# CamBridge — 멀티벤더 머신비전 카메라 연동 데모

## 프로젝트 목적

크몽 프리랜서 포트폴리오용 데모 프로그램이다. 의뢰인(공장, 자동화 업체)에게 다음을 증명하는 것이 목표다.

- 제조사 SDK(Basler pylon 등) 및 GenICam 개념 기반 카메라 연동 능력
- 트리거·노출·게인 제어, 프레임 드롭 없는 고속 획득
- 카메라를 교체해도 상위 로직이 그대로 동작하는 멀티벤더 구조
- 간단한 양불(OK/NG) 검사까지 이어지는 완결된 흐름

실물 산업용 카메라는 아직 없다. 웹캠, 이미지 폴더, pylon 카메라 에뮬레이터로 먼저 완성하고, 카메라 도착 후 구현체 하나만 추가해 실물 시연한다.

장기적으로는 노코드 비전 검사 툴로 확장할 기반이므로 구조를 깔끔하게 유지한다.

## 개발자

- C/C++, C# 윈도우 개발 24년차. 산업용 카메라 SDK 연동(GigE Vision / USB3 Vision) 실무 경험 약 10년.
- 기초 설명은 생략하고, 설계 판단의 근거와 트레이드오프 위주로 설명할 것.
- 응답은 한국어 존댓말. 코드 식별자·주석은 영어.

## 기술 스택

- .NET 8, C#, WPF
- MVVM: CommunityToolkit.Mvvm
- 영상 처리: OpenCvSharp4 (Windows: `OpenCvSharp4.runtime.win`, 클라우드 Linux 테스트: Linux용 런타임 패키지를 조건부 참조)
- Basler pylon SDK .NET API (`Basler.Pylon`) — 에뮬레이터 사용
- 테스트: xUnit
- 로깅: Microsoft.Extensions.Logging (콘솔 + 파일)

## 작업 환경 분담 (중요)

이 저장소는 Claude Code 클라우드 세션(Linux)과 로컬 Windows 세션 양쪽에서 작업한다.

| 프로젝트 | 대상 프레임워크 | 작업 위치 |
|---|---|---|
| CamBridge.Core | `net8.0` | 클라우드 가능 |
| CamBridge.Pipeline | `net8.0` | 클라우드 가능 |
| CamBridge.Inspection | `net8.0` | 클라우드 가능 |
| CamBridge.Sources.ImageFolder | `net8.0` | 클라우드 가능 |
| CamBridge.Tests | `net8.0` | 클라우드 가능 (Windows 전용 테스트는 분리) |
| CamBridge.Sources.Webcam | `net8.0-windows` | 로컬 Windows |
| CamBridge.Sources.Pylon | `net8.0-windows` | 로컬 Windows (pylon SDK 필요) |
| CamBridge.App | `net8.0-windows` (WPF) | 로컬 Windows |

- 클라우드에서는 WPF 실행, 웹캠, pylon SDK·에뮬레이터를 사용할 수 없다. 해당 작업은 컴파일 확인까지만 하고, 실행 검증은 로컬 Windows 세션으로 넘긴다.
- Windows 전용 프로젝트는 `EnableWindowsTargeting=true`로 Linux에서도 빌드만은 되도록 유지한다. 단, pylon 참조는 SDK가 없으면 빌드에서 제외한다.
- 클라우드 작업은 `net8.0` 프로젝트와 테스트를 우선 진행한다. 진행 시 현재 환경이 어디인지 먼저 확인하고, 그 환경에서 검증할 수 없는 항목은 PLAN.md에 "로컬 검증 필요"로 표시한다.

## 공개 저장소 주의사항

이 저장소는 public이다.

- 이전·현 직장의 코드, 내부 구조, 노하우를 옮기지 않는다. 모든 코드는 새로 작성한다.
- Basler pylon SDK 바이너리(DLL 등)를 커밋하지 않는다. README에 SDK 설치 안내만 둔다.
- 라이선스가 불명확하거나 비상업 조건인 데이터셋 이미지를 커밋하지 않는다. 다운로드 안내 또는 직접 생성한 샘플만 사용한다.
- 비밀값(토큰, 키)을 커밋하지 않는다.

## 솔루션 구조

```
CamBridge.sln
src/
  CamBridge.Core/                 # 인터페이스·모델만. 벤더 SDK·OpenCV 참조 금지
  CamBridge.Sources.Webcam/       # OpenCvSharp VideoCapture
  CamBridge.Sources.ImageFolder/  # 폴더 이미지를 카메라처럼 재생
  CamBridge.Sources.Pylon/        # Basler pylon (에뮬레이터 포함)
  CamBridge.Pipeline/             # 획득 파이프라인, 링 버퍼, 통계, 저장
  CamBridge.Inspection/           # ROI, 이진화·블롭·템플릿 매칭, 판정, CSV 로그
  CamBridge.App/                  # WPF UI
tests/
  CamBridge.Tests/
docs/
  PLAN.md                         # 단계별 작업 계획 (진행 상황 갱신)
```

## 설계 원칙 (반드시 지킬 것)

1. **Core는 순수하게**: `CamBridge.Core`는 어떤 벤더 SDK나 OpenCV 타입도 노출하지 않는다. 상위 계층은 Core 인터페이스만 의존한다.
2. **GenICam식 파라미터 접근**: 자주 쓰는 노출·게인·트리거는 강타입 속성으로, 그 외는 이름 기반(`GetParameter("ExposureTime")`) 접근을 함께 제공한다. HIKROBOT 등 다른 SDK 추가 시 Core 수정이 없어야 한다.
3. **프레임 콜백에서 블로킹 금지**: 카메라 스레드의 `FrameReceived` 처리 안에서 디스크 I/O, 검사, UI 접근을 하지 않는다. 파이프라인 큐로 넘긴다.
4. **버퍼 소유권 명확히**: `Frame`은 `IDisposable`이며 풀링된 버퍼(ArrayPool 등)를 사용한다. 장시간 실행 시 메모리 증가가 없어야 한다.
5. **UI 스레드 마샬링**: 라이브뷰는 최신 프레임만 표시(중간 프레임 스킵)하고 `Dispatcher`로 갱신한다. 표시 때문에 획득이 느려지면 안 된다.
6. **SDK 없이도 빌드 가능**: pylon SDK가 없는 PC에서도 솔루션이 빌드되도록 `CamBridge.Sources.Pylon`은 조건부 포함 또는 런타임 플러그인 로딩으로 처리한다.
7. **통계는 항상 노출**: 수신 FPS, 처리 FPS, 드롭 프레임 수, 큐 길이를 UI에 상시 표시한다. 이것이 포트폴리오의 핵심 차별점이다.

## 작업 방식

- `docs/PLAN.md`의 단계를 순서대로 진행한다. 한 번에 한 단계만.
- 각 단계 시작 전 해당 단계의 설계(파일 목록, 주요 타입)를 짧게 제시하고 확인을 받는다.
- 각 단계 종료 시: 빌드 성공, 테스트 통과, `docs/PLAN.md` 체크박스 갱신, 단계 단위 git 커밋.
- 불확실한 SDK API는 추측하지 말고 설치된 SDK의 문서·샘플을 확인하거나 개발자에게 물어본다.

## 자주 쓰는 명령

```
dotnet build CamBridge.sln
dotnet test tests/CamBridge.Tests
dotnet run --project src/CamBridge.App
```

pylon 에뮬레이터 활성화 (에뮬레이션 카메라 2대):

```
set PYLON_CAMEMU=2
```
