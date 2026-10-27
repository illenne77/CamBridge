# CamBridge

멀티벤더 머신비전 카메라 연동 데모 (.NET 8 / WPF)

카메라 제조사가 바뀌어도 획득·검사·UI 로직이 그대로 동작하도록 설계한 카메라 연동 프레임워크와 데모 애플리케이션입니다.

## 주요 기능 (개발 중)

- 공통 카메라 추상화 — 웹캠, 이미지 폴더, Basler pylon(에뮬레이터 포함)
- GenICam 방식의 파라미터 접근, 노출·게인·트리거 제어
- 프레임 드롭 없는 획득 파이프라인과 실시간 통계(FPS, 드롭 수, 큐 길이)
- ROI 기반 양불(OK/NG) 검사와 결과 로그

## 요구 사항

- .NET 8 SDK
- Windows 10/11 (WPF 앱, 웹캠, pylon 연동)
- Basler pylon 연동 시: [Basler pylon SDK](https://www.baslerweb.com/) 별도 설치 (이 저장소에는 SDK 파일을 포함하지 않습니다)

## 진행 상황

작업 계획과 진행 상황은 [docs/PLAN.md](docs/PLAN.md)를 참고하세요.
