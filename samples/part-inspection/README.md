# part-inspection 샘플

`tools/CamBridge.SampleGenerator`로 생성한 합성 이미지입니다(320x240, Mono8). 공개 데이터셋을 쓰지 않았으므로 라이선스 제약이 없습니다.

| 파일 | 내용 |
|---|---|
| `images/*_ok*.png` | 정상 부품. 기준 마크 위치가 ±6px 흔들림(부품 위치 편차) |
| `images/*_ng*.png` | 얼룩 1개 / 2개 / 큰 얼룩 / ROI 가장자리 얼룩 / 기준 마크 누락 |
| `images/007_ok_dust.png` | 1px 먼지 — `minBlobArea` 미만이라 OK |
| `images/012_ok_spot_outside_roi.png` | 검사 ROI 밖의 얼룩 — OK |
| `part.recipe.json` | `Fiducial`(템플릿 매칭, 점수 0.8 이상) + `Stain`(어두운 블롭 0개) |
| `fiducial.png` | 템플릿 |

재생성: `dotnet run --project tools/CamBridge.SampleGenerator` (저장소 루트에서 실행)
