# Changelog

이 프로젝트의 주요 변경 사항을 이 파일에 기록한다.

형식은 [Keep a Changelog](https://keepachangelog.com/ko/1.1.0/)를 따르고, 버전은 [Semantic Versioning](https://semver.org/lang/ko/)을 따른다.

<!-- 버전은 package.json·MCPToolsInfo.Version·git 태그 vX.Y.Z를 항상 함께 올린다. -->

## [0.1.0] - 미배포

### Added

- 기획서 기반 AI 에셋 생성 4단계 파이프라인 에디터 툴 (AssetListup → PromptBuilder → ComfyUIGenerator → AssetApplier)
- ComfyUI 브리지 서버(Python): 원본 워크플로 JSON에 변수 덮어쓰기 방식으로 이미지/사운드 생성, 후보 4개 생성 및 선택 적용
- 스프라이트 시트 지원: 레퍼런스 이미지 기반 동작별 Row 통합 시트 생성·슬라이스·적용
- MCP 도구 노출: 각 파이프라인 단계를 unity-mcp(선택 의존)를 통해 MCP 도구로 호출 가능
- UPM 배포 대응: 경로 독립화, 설정 에셋 Assets 분리, preflight 검증
