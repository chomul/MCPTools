# Changelog

이 프로젝트의 주요 변경 사항을 이 파일에 기록한다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/)를 따르고, 버전은 [Semantic Versioning](https://semver.org/lang/ko/)을 따른다.

<!-- 버전은 package.json·MCPToolsInfo.Version·git 태그 vX.Y.Z를 항상 함께 올린다. -->

## [Unreleased]

### Added

- 1단계: 저장해 둔 `AssetList_*.json`을 불러와 이어서 편집(항목 추가/수정/삭제)하고 같은 파일에 덮어쓰는 기능
- 3단계: 후보 개수를 생성 창에서 바로 조절하는 슬라이더(1~12, 설정 에셋 `candidateCount`와 공유)
- 3단계: 이 세션이 시작하지 않은 브리지 서버를 내리는 **[원격 종료]** 버튼, 브리지 서버 `POST /shutdown` 엔드포인트
- Unity 에디터 종료 시 이 도구로 시작한 브리지 서버를 자동 종료 (설정 `shutdownBridgeOnEditorQuit`, 기본 켬)
- 단계별 산출물 하위 폴더 — `Docs/1_AssetList`·`Docs/2_PromptSet`·`Docs/SpriteSheetPrompt`, `Generated/3_Candidates`·`Generated/3_Confirmed`(`Images`/`Audio`/`SpriteSheets`). 기존 프로젝트의 구 위치 파일도 계속 인식됩니다(읽기는 양쪽, 쓰기는 새 위치)

### Changed

- 브리지 서버 버전 0.1.0 → 0.2.0 (`/shutdown` 추가)

### Fixed

- 다른 Unity 프로젝트나 이전 에디터 세션이 띄운 브리지 서버가 포트를 쓰고 있으면 [서버 시작]·[서버 종료]가 아무 안내 없이 동시에 비활성화되던 문제 — 원인과 조치를 안내하고 [원격 종료]로 해소할 수 있게 함

## [0.1.0] - 2026-07-25

### Added

- 기획서 기반 AI 에셋 생성 4단계 파이프라인 에디터 툴 (AssetListup → PromptBuilder → ComfyUIGenerator → AssetApplier)
- ComfyUI 브리지 서버(Python): 원본 워크플로 JSON에 변수 덮어쓰기 방식으로 이미지/사운드 생성, 후보 4개 생성 및 선택 적용
- 스프라이트 시트 지원: 레퍼런스 이미지 기반 동작별 Row 통합 시트 생성·슬라이스·적용
- MCP 도구 노출: 각 파이프라인 단계를 unity-mcp(선택 의존)를 통해 MCP 도구로 호출 가능
- UPM 배포 대응: 경로 독립화, 설정 에셋 Assets 분리, preflight 검증
