# Task 6 체크리스트 — SpriteSheet 도구 (외부 AI 기반)

> Task 문서: [Task6_SpriteSheet.md](../tasks/Task6_SpriteSheet.md)
> 2026-07-23 개정: 멀티 행(동작별 Row) 통합 시트 + 레퍼런스 이미지 첨부 방식으로 재작업.

## 1. 구현 체크리스트

- [x] `SpriteSheetPromptWizard : EditorWindow` — 메뉴 `Tools/MCP/Sprite Sheet`, 레퍼런스 이미지 전제 + 동작 행 목록 편집 폼 → 멀티 행 통합 시트 프롬프트 생성·미리보기·클립보드 복사 → `Assets/Docs/SpriteSheetPrompt_{id}.json` 저장
  - 구현 결과: 폼 구성 — 레퍼런스 이미지 사용 토글(기본 on, off면 캐릭터 설명 필수 검증), 캐릭터 특징 서술(선택, "Preserve the key design features: ..." 문장으로 반영), 동작 행 목록 편집(추가/삭제, 프리셋 walk/run/attack/idle/death + 직접 입력, 행별 프레임 수, 기본 walk8/run8/attack8/death10), 셀 크기(기본 256), 방향(기본 RIGHT), 배경(흰색 기본/투명). `SpriteSheetPromptBuilder.BuildPrompt`가 사용자 검증 구조를 템플릿화 — 레퍼런스 지시("Use the attached reference image...") / 카메라 고정·일정 스케일·지면 일정·전신 노출·금지 항목 / {cell}x{cell} 균등 그리드·흰 배경(제거 전제) / "Row N: WALK cycle, 8 frames." 행 목록(프리셋별 연출 문구 내장: attack의 anticipation~recovery, death의 collapse 등) / Important requirements(비율·의상·팔레트 일관성, 루프 클린(walk/run/idle만 대상), 실루엣 가독성, 슬라이스 용이성) / 개별 프레임 명명(`walk_01 to walk_08` 등) 조건부 요청. JSON 저장은 행 목록(`rows: [{action, frameCount}]`)·cellSize·direction·background 포함으로 갱신.
  - 검증 상태: 코드 작성 완료, Unity 컴파일·에디터 동작은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetPromptWizard.cs`, `SpriteSheetPromptBuilder.cs`
- [x] (2026-07-23 추가 개편) 폼 게임 컨텍스트 확장 + 로컬 AI CLI 기반 프롬프트 생성(기본 경로) + 템플릿 폴백
  - 구현 결과: 폼에 게임 컨텍스트 섹션 추가 — 게임 장르(자유 텍스트, "for a {genre} game"으로 반영), 아트 스타일/분위기(자유 텍스트, 스타일 유지 문장에 삽입), 추가 참고 사항(선택, Important requirements에 "- Additional notes: ..."로 반영). 레퍼런스 이미지 토글과 캐릭터 설명 입력란(레퍼런스 미사용 시 필수)은 별도 유지. `SpriteSheetPromptBuilder.BuildMetaPrompt`가 게임 컨텍스트 없이 조립한 검증 템플릿을 "예시 구조"로 포함한 메타 프롬프트를 조립 — 구조(레퍼런스 지시/카메라·스케일·지면 고정/여백 포함 동일 {cell}x{cell} 셀/행별 애니메이션 정의/Important requirements/개별 프레임 명명/배경 지시)를 반드시 유지하며 장르·스타일·참고를 반영하고 프롬프트 본문만 출력하라고 지시. 위저드에 AI CLI 드롭다운(`AiCliRunner.GetInstalledTools` + 새로고침) + 타임아웃 필드 + [AI로 프롬프트 생성] 버튼(async/await 실행, 실행 중 버튼 비활성 + 취소 버튼, 창 닫힘 시 OnDisable에서 취소 — 에디터 블로킹 없음). AI stdout은 `CleanAiOutput`으로 앞뒤 공백/전체 마크다운 코드펜스만 제거 후 미리보기 표시 + JSON 저장(promptSource="ai-cli:{command}"). 폴백: CLI 미설치/실행 실패/타임아웃/빈 응답 시 템플릿 방식으로 생성하고 사유를 HelpBox로 안내. [프롬프트 생성 (템플릿)] 버튼 유지. JSON answers에 gameGenre/artStyle/extraNotes, 문서에 promptSource 필드 추가.
  - 검증 상태: 코드 작성 완료, Unity 컴파일·AI CLI 실행·폴백 동작은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetPromptWizard.cs`, `SpriteSheetPromptBuilder.cs` (AiCliRunner는 기존 공용 유틸 재사용, 수정 없음)
- [x] `SpriteSheetImporter` — 멀티 행 시트 png + 행 정의 + 배경 모드 → 흰 배경 flood-fill 제거 → 행/프레임 검출 → 공통 셀 정규화·재조립 → `Assets/Generated/Images/` 저장 → 동작명 기반 Sprite Multiple 슬라이스
  - 구현 결과: 흰색 배경 모드이면 이미지 네 외곽 경계에서 시작하는 큐 기반 BFS flood-fill(4방향, R/G/B 각 채널 ≥ 240 또는 이미 투명한 픽셀만 통과)로 외곽과 연결된 배경만 알파 0 처리(캐릭터 내부 흰색 보존, 스택 오버플로 없음) → 이후 알파 기준 통일 처리. row-projection으로 행 구간 분리(위→아래, 2px 미만 노이즈 무시) → 각 행 내 column-projection으로 프레임 바운딩 박스 검출 → 전체 프레임 최대 크기 기준 공통 셀(하단 여백 2px 포함, 짝수 올림) → 수평 중앙 + 수직 하단 정렬(지면 유지)로 멀티 행 균등 그리드 재조립 → `Assets/Generated/Images/{name}_sheet.png` 저장 → `TextureImporter`로 Sprite Mode=Multiple, 슬라이스 이름 행 동작명 기반 `walk_01`~`death_10`(2자리 패딩, 1-base), alphaIsTransparency, PPU=`MCPToolSettings.spritePixelsPerUnit` 적용. 에디터 호출 시 진행률 표시.
  - 검증 상태: 코드 작성 완료, 실제 멀티 행 흰 배경 시트 정규화·슬라이스 결과는 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`
- [x] (2026-07-23 실측 개선) 실제 ChatGPT 생성 시트(1983x793, 배경 그라데이션 ~252→134 + 2~3px 회색 격자선, 행당 10프레임) 임포트 실패를 실측 기반으로 개선
  - 구현 결과: (1) 배경 제거를 "RGB≥240 flood-fill"에서 **외곽 전체 픽셀 시드 큐 기반 BFS(4방향)**로 교체 — 이웃 편입 조건: 현재 픽셀과의 채널별 최대 차 ≤ 16(그라데이션·격자선 추종) OR min(R,G,B) ≥ 235(격자선 너머 셀 내부 근사 흰색 진입). 편입 픽셀 알파 0, 캐릭터 내부 흰색은 외곽선에 막혀 보존, 재귀 없음. Python 프로토타입으로 해당 이미지 4행×10프레임 검출 검증 완료. (2) 행 검출 노이즈 내성 — 행 판정 최소 전경 픽셀 max(8, width/100), 최소 밴드 높이 8px. (3) 프레임 검출 — 열 판정 최소 픽셀 max(2, bandHeight/30), 최소 프레임 폭 8px, 열 간 간격 3px 이하 같은 프레임으로 병합(안티앨리어싱 끊김 대응). (4) 불일치 처리 — 하드 실패 대신 창에서는 "행별 검출 결과: 10/10/10/10 (기대: 8/8/8/10)" 요약과 함께 [검출된 구성으로 임포트]/[취소] 2버튼 다이얼로그, 채택 시 행 이름은 순서대로 기존 동작명(초과 행은 rowN 자동 이름)·프레임 수는 검출값 사용. MCP는 `allowDetected`(기본 false) 파라미터 — false면 검출 요약 포함 실패, true면 검출 구성 진행. 결과에 rowActions/expectedRowCount/expectedFramesPerRow/usedDetectedLayout 추가. (5) 프롬프트 템플릿 흰 배경 문구 강화 — "plain solid pure white (#FFFFFF) background, perfectly uniform with no gradient, no vignette, and no shadows" + "Do not draw any visible grid lines, cell borders, or separators between the cells."
  - 검증 상태: 알고리즘은 Python 프로토타입으로 실측 이미지 검증 완료, C# 이식 코드의 Unity 컴파일·실제 임포트는 사용자 테스트 대기
- [x] (2026-07-23 추가 개선) 행별 캐릭터 수직 위치 어긋남 개선 — 강건한 기준선(유효 하단) 정렬
  - 구현 결과: (1) 프레임의 "유효 하단"을 bbox 하단이 아니라 **아래에서 위로 스캔하며 전경 픽셀 수가 max(2, 프레임폭/20) 이상인 첫 스캔라인**으로 판정(`ComputeEffectiveBottom`) — 발밑 먼지/그림자 조각/안티앨리어싱 희소 픽셀은 기준선 판정에서만 무시하고 픽셀 자체는 복사 유지(자기 셀 범위 내로 클램프). (2) 재조립 시 모든 프레임의 유효 하단이 셀 하단 + BottomPadding(2px) 위치에 오도록 정렬해 시트 전체(행 간 포함)에서 지면 기준선 통일. 셀 높이도 유효 하단 위쪽 높이 기준으로 계산해 발밑 노이즈로 셀이 커지지 않음. 수평은 기존 중앙 정렬 유지. (3) 프롬프트 템플릿 강화 — 셀 배치 문장에 "keep the feet on the exact same ground baseline in EVERY cell of EVERY row — the ground level must be identical across the entire sheet, not just within one row" 추가, Important requirements에 "- Keep the feet on one identical ground baseline across every row and every cell of the sheet." 추가.
  - 검증 상태: 코드 작성 완료, 실제 시트 재임포트로 행 간 기준선 통일은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptBuilder.cs`
- [x] (2026-07-23 추가 개선) 흰 머리/흰 옷 캐릭터에서 배경 제거가 과하게 일어나는 문제 수정 — 근사 흰색 편입 조건 강화
  - 구현 결과: (1) BFS 편입 조건을 `delta ≤ 16 OR nearwhite(이웃)`에서 `delta ≤ 16 OR (nearwhite(이웃) AND 이웃의 3x3 윈도(8방향+자신, 경계 밖 무시) 전체가 nearwhite 또는 이미 배경 편입)`으로 변경 — 흐린 외곽선의 좁은 틈으로 캐릭터 내부(얼굴·몸통)에 새는 것을 차단. 3x3 판정은 BFS 진행 중 동적으로 수행(이미 편입된 픽셀을 흰색으로 간주 — 격자선 경계에서 셀 내부로 진입하기 위해 필수). (2) BFS 종료 후 비전파 fringe 정리 1회 — 배경과 4방향 인접한 잔여 근사 흰색 전경 픽셀을 한 번에(전파 없이) 편입해 경계 잔여 흰 픽셀 정리. (3) nearwhite 판정은 픽셀당 1회 사전 계산(bool 배열)해 성능 유지, 임계값 상수(235/16)는 기존 유지. Python 프로토타입으로 잘못 제거되던 3,047픽셀 복원·부작용 0 검증 완료.
  - 검증 상태: 알고리즘은 Python 프로토타입으로 검증 완료, C# 이식 코드의 Unity 컴파일·실제 임포트는 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`
- [x] (2026-07-23 추가 개선) 잔여 캐릭터 침식 최종 해결 — "pocket restore" 후처리 추가
  - 구현 결과: 배경 제거(3x3 가드 BFS + fringe 정리) 마지막에 `RestorePockets` 단계 추가. 원리: 제거가 좁은 틈으로 캐릭터 내부에 새어 만든 주머니(pocket)는 바깥 배경과 좁은 통로로만 연결됨 → (1) 제거 마스크를 체비쇼프 거리 K=2로 침식(각 제거 픽셀의 (2K+1)x(2K+1)=5x5 윈도에 전경 픽셀이 있으면 제외, 경계 밖 무시) — 통로가 끊김. (2) 침식 마스크 위에서 네 외곽 침식 픽셀 시드 4방향 BFS로 외곽 도달성(reach) 계산. (3) reach를 시작 큐로 제거 마스크 내부로 정확히 K+1=3 스텝만 레벨 단위 재팽창. (4) 제거됐지만 reach가 아닌 픽셀(pocket)의 알파를 사전 스냅샷한 원본 알파로 복원(RGB는 알파만 0 처리하는 기존 구조라 이미 보존). 전 과정 큐 기반·재귀 없음, 기존 "배경 제거 중" 진행률 구간에 포함. Python 프로토타입으로 흰 캐릭터 시트에서 침식됐던 12,252픽셀(흩날리는 머리카락 포함) 복원·시각적 부작용 없음 검증 완료.
  - 검증 상태: 알고리즘은 Python 프로토타입으로 검증 완료, C# 이식 코드의 Unity 컴파일·실제 임포트는 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`
- [x] (2026-07-23 추가 개선) 공격 이펙트 등 분리 덩어리로 인한 프레임 초과 검출 — 기대 기반 인접 프레임 병합
  - 구현 결과: `DetectFramesInBand` 결과에 대해 기대 구성 비교 전 `MergeFramesToExpected` 수행 — 각 행 r(r < rows.Count)에서 검출 수 > 기대 수이면, **수평 간격이 가장 작은 인접 프레임 쌍을 union으로 병합**하는 것을 검출 수 == 기대 수가 될 때까지 반복. 단 병합하려는 최소 간격이 해당 행 프레임 폭 중앙값의 60%(`MergeGapMedianRatio = 0.6`)를 초과하면 중단(진짜 프레임 병합 방지) — 남은 불일치는 기존 불일치 다이얼로그/allowDetected 경로로 처리. 행 수 자체가 기대보다 많은 경우는 손대지 않음(기존 경로 유지). 병합 발생 행은 "행 3: 12개 검출 → 인접 병합으로 10개" 형식으로 기록 — `SpriteSheetImportResult.mergeNotes` 필드 추가, 창 결과 메시지·불일치 요약·MCP 결과(`mergeNotes`)에 표기. 효과: 검기/타격 이펙트가 캐릭터 옆 좁은 간격에 있어 가장 먼저 캐릭터 프레임과 병합되고, 프레임 안에 이펙트가 포함된 채 정상 슬라이스됨.
  - 검증 상태: 코드 작성 완료, 실제 공격 이펙트 분리 시트 임포트는 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`
- [x] (2026-07-23 추가 개선) 병합 프레임의 과대 union 폭으로 인한 시트 부풀림·캐릭터 위치 흔들림 수정 — 질량 중심 정렬 + 셀 폭 상한
  - 구현 결과: (1) 수평 배치 기준을 bbox 중앙에서 **전경 픽셀 x좌표 가중 중앙값(median x)**으로 변경(`ComputeMassCenterX`, 열 단위 카운트 배열 O(bbox)) — 이펙트는 픽셀 수가 캐릭터보다 훨씬 적어 중앙값이 캐릭터 본체에 위치하므로, 이펙트가 한쪽으로 뻗은 병합 프레임에서도 캐릭터가 항상 셀 수평 중앙에 정렬됨. 수직은 기존 유효 하단 기준선 유지. (2) 셀 폭 상한 — cellW = max(중앙값, min(전체 최대 프레임 폭, ceil(전체 프레임 폭 중앙값 x 1.6))) (짝수 올림), 병합 프레임의 과대 union 폭(~342px vs 일반 ~150px)이 시트 전체 폭을 부풀리지 않음. (3) 클램프 복사 — 대상 좌표가 자기 셀 수평 범위를 벗어나면 건너뜀(이펙트 꼬리 잘림 허용, 캐릭터는 중앙 정렬이라 안전; 수직 클램프 기존 유지). (4) 잘림 발생 시 `SpriteSheetImportResult.clippedFrames = true` — 창 결과 메시지에 "일부 프레임의 이펙트가 셀 폭에 맞춰 잘렸습니다" 안내, MCP 결과에 `clippedFrames` 포함.
  - 검증 상태: 코드 작성 완료, 흰 캐릭터(대형 검기 이펙트) 시트 재임포트로 시트 폭·캐릭터 정렬 확인은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`
- [x] (2026-07-23 추가 개선) 셀 폭 자체 계산(중앙값x1.6)이 이펙트 병합 프레임(~240px)보다 작아 펀치 이펙트가 셀 경계에서 잘리는 문제 — 지정 셀 크기 최소 보장
  - 구현 결과: `Import(...)`에 `preferredCellSize` 파라미터 추가(0 이하 미지정). 기존 콘텐츠 기반 계산(폭=중앙값x1.6 상한, 높이=유효 하단 기준+여백) 후 `cellW = max(계산값, preferredCellSize)`, `cellH = max(계산값, preferredCellSize)` (짝수 올림 유지) — 프롬프트에 지시한 셀 크기(기본 256)를 최소 보장해 이펙트(~240px)까지 수용하고 시트가 프롬프트의 256 그리드 지시와 일치. 프레임이 지정 셀보다 크면 콘텐츠 기반 값 사용(잘림 방지 우선). 클램프 복사/`clippedFrames` 안내는 안전망으로 유지. 위저드 임포트 섹션은 상단 `_cellSize`를 전달, MCP `mcptools_spritesheet_import`에 `cellSize`(선택, 기본 256) 파라미터 추가·설명 갱신.
  - 검증 상태: 코드 작성 완료, call 시트(3행 공격 펀치 이펙트) 재임포트 확인은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`
- [x] (2026-07-23 추가 개선) preferredCellSize 최소 보장 롤백 — 콘텐츠 기반 셀 크기 복귀 + 무잘림 배치(shift-to-fit)
  - 구현 결과: 실측상 AI가 프롬프트의 256 지시보다 작게 그려(캐릭터 110~150px) 256 셀 강제 시 여백이 과도해지는 역효과 확인 → (1) 셀 크기를 콘텐츠 기반으로 복귀 — cellW = 전체 프레임(병합 union 포함) 최대 폭(중앙값x1.6 상한 제거), cellH = 유효 하단 기준 콘텐츠 높이 + BottomPadding, 짝수 올림 유지. `preferredCellSize` 파라미터·전달 경로(위저드 `_cellSize`, MCP `cellSize`) 완전 제거. (2) 무잘림 배치 — 질량 중심(median x)이 셀 중앙에 오도록 offset 계산 후, union이 셀 범위를 벗어나면 완전히 들어가도록 `Mathf.Clamp(offsetX, cellBaseX, cellBaseX + cellW - f.width)`로 최소 이동(shift-to-fit). cellW ≥ 모든 union 폭이므로 정상 경로에서 잘림 없음(픽셀 클램프·`clippedFrames`는 안전망 유지). (3) 수직 지면 기준선 정렬은 기존 유지. 효과: 셀이 캐릭터 크기에 맞아 여백 정상화, 이펙트 포함 프레임도 온전히 수용, 이펙트가 뻗은 프레임만 캐릭터가 중앙에서 약간 이동.
  - 검증 상태: 코드 작성 완료, call 시트 재임포트로 여백·이펙트 미잘림 확인은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`
- [x] (2026-07-23 추가 개선) 격자 기준 슬라이스 모드 추가 (기본값) — AI가 그린 격자선 간격을 신뢰해 셀 분리, 콘텐츠 정규화는 옵션으로 유지
  - 구현 결과: `Import`에 `useGridSlicing`(기본 true) 추가. 격자 모드: 배경 제거(흰색 모드 기존 로직 — 격자선도 함께 제거됨) 후 (1) 경계 검출 — 전경 픽셀 수가 교차 길이의 1%(`GridGapMaxFgRatio = 0.01`, 최소 1px) 미만인 스캔라인 연속 구간(gap run)의 중앙을 셀 경계로, 이미지 가장자리에 붙은 run은 안쪽 끝을 경계로, 시작/끝 포함. 세로(x)·가로(y) 동일 방식. (2) 셀 그리드 구성 — 셀 콘텐츠 판정은 전경 픽셀 ≥ 셀 면적의 0.5%(`GridCellContentRatio = 0.005`). 완전 빈 그리드 행은 제외, 행별 트레일링 빈 셀만 제거(중간 빈 셀은 위치 유지, 슬라이스 미생성 — 프레임 순서 보존). (3) 재조립 — 출력 셀 = 최대 셀 크기(짝수 올림), 각 셀 콘텐츠는 셀 내 상대 좌표 유지(좌상단 기준)로 그대로 복사(재정렬 없음 — AI가 그린 기준선/중앙 배치 신뢰), 남는 공간은 오른쪽/아래 패딩. 슬라이스는 콘텐츠 있는 셀만 행 동작명+순번으로 생성. (4) 기대 구성 비교/불일치 다이얼로그/allowDetected는 콘텐츠 셀 수 기준으로 동일 적용, 병합 로직은 격자 모드에서 미사용. (5) 격자 검출 실패(셀 행/열 2개 미만, 전체 빈 이미지 등) 시 콘텐츠 정규화로 자동 폴백하고 결과에 표기(`sliceMode`/`gridFallback` 필드). 위저드 임포트 섹션에 "슬라이스 기준" 드롭다운(격자선 기준(기본)/콘텐츠 정규화), MCP `mcptools_spritesheet_import`에 `sliceMode`(grid/content, 기본 grid) 파라미터 추가. 슬라이스 적용은 공용 `ApplySpriteSlices`로 리팩터링(동작 변화 없음), 기존 콘텐츠 정규화 경로 로직은 보존.
  - 검증 상태: 코드 작성 완료, 격자선이 그려진 실제 시트 임포트·폴백 동작은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`
- [x] (2026-07-23 추가 개선) 격자 모드에서 기대 구성 비교/불일치 다이얼로그 제거 + 격자 경계 오검출 보강
  - 구현 결과: (1) 격자 모드(`TryImportGrid`)에서 기대 구성 비교·불일치 다이얼로그·allowDetected 검사 완전 제거 — 격자선이 곧 정답이므로 검출된 격자 그대로 임포트. 행 이름은 순서대로 행 정의의 동작명(초과 행 rowN), 검출 vs 기대 차이는 `usedDetectedLayout`에 정보로만 기록. 콘텐츠 정규화 모드의 불일치 다이얼로그/allowDetected는 기존 그대로 유지(MCP 설명에 content 모드 전용임을 명시). (2) 경계 오검출 보강(실측: 1/8/1/8/8/9로 행이 쪼개짐 — 장식과 본체 사이 얇은 수평 틈을 행 경계로 오인) — 내부 gap run은 최소 두께 2px(`MinGridGapThickness`) 이상만 경계로 인정, 경계로 나뉜 셀 크기가 현재 셀 크기 중앙값의 40%(`MinGridCellMedianRatio = 0.4`) 미만이면 오검출로 보고 더 작은 이웃 셀 쪽 내부 경계를 제거해 병합(반복, 세로/가로 경계 동일 원칙 적용).
  - 검증 상태: 코드 작성 완료, 문제 시트(1/8/1/8/8/9 검출) 재임포트로 4행 정상 검출 확인은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetTool.cs`
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`, `SpriteSheetTool.cs`, `SpriteSheetPromptBuilder.cs`
- [x] 오류 안내 — 행 수/행별 프레임 수 불일치 등 실패 시 원인·조치 다이얼로그 표시
  - 구현 결과: 파일 없음/이미지 로드 실패/행 미검출(배경 모드 불일치 안내 포함)/행 수 불일치(검출 행 수 vs 기대 행 수)/행별 프레임 수 불일치("행 2(run)에서 7개 검출(기대 8개)" 형식 + 원인 후보·조치) 각각 구체적 메시지의 `InvalidOperationException`을 던지고, 창에서는 `EditorUtility.DisplayDialog`로 표시, MCP에서는 실패 메시지로 반환.
  - 검증 상태: 코드 작성 완료, 다이얼로그 표시는 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs`, `SpriteSheetPromptWizard.cs`
- [x] MCP 도구 노출 — `mcptools_spritesheet_build_prompt` / `mcptools_spritesheet_import`
  - 구현 결과: build_prompt — rows("walk:8,run:8,attack:8,death:10" 형식 파싱, 생략 시 기본 구성)/useReferenceImage(기본 true)/characterDescription/genre·artStyle·notes(2026-07-23 추가 — 게임 컨텍스트, 템플릿에 삽입; MCP 경로는 호출 주체가 이미 AI이므로 CLI 재호출 없이 템플릿 방식 유지)/cellSize(기본 256)/direction(right/left)/background(white/transparent) → prompt + savedPath + rows + background 반환. import — imagePath(필수)/rows(필수, 동일 형식)/backgroundMode(white/transparent, 기본 white) → assetPath + rowCount + totalFrameCount + framesPerRow(행별 동작·검출 수) + cellWidth/cellHeight 반환. JSON 직렬화 가능한 파라미터·결과만 사용.
  - 검증 상태: 코드 작성 완료, MCP 호출은 사용자 테스트 대기
  - 관련 파일: `MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetTool.cs`

- [x] (2026-07-23 확정) 임포터 격자 전용 재작성 — 콘텐츠 정규화 경로(질량 중심/기준선 정렬, 프레임 병합, 불일치 다이얼로그/allowDetected, sliceMode) 완전 삭제. 배경 제거에 무채색 조건(채도>18 편입 금지, 유채색 글로우 이펙트 보존) 추가, 격자 경계에 누락 경계 복원(중앙값 피치 1.6배 이상 셀 균등 분할) 추가. 격자 검출 결과 그대로 임포트(확인 없음), 실패 시 재생성 안내 예외.
  - 검증 상태: Python 시뮬레이션으로 3개 실측 시트(흰 캐릭터/검은 캐릭터/파란 캐릭터+글로우) 모두 4행·균일 셀 폭 검출 및 이펙트 보존 확인, Unity 컴파일·에디터 임포트는 사용자 테스트 대기
  - 관련 파일: MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs, SpriteSheetPromptWizard.cs, SpriteSheetTool.cs

- [x] (2026-07-24 확정) **격자선 직접 검출 + in-place 슬라이스로 재작성** — 스프라이트 분리 정확도 개선. 실측 3개 시트(흰/파랑 캐릭터, 6~11 프레임 불균등 행, 빈 셀 포함)에서 기존 "전경 gap run 중앙 + 피치 1.6배 분할" 방식이 이펙트·머리카락이 셀을 넘나들거나 빈 셀이 많을 때 행 병합/오분할되던 문제를 근본 해결.
  - 구현 결과: (1) 경계 검출을 배경 제거 후 전경 희소 구간 추정에서 **AI가 그린 격자선 픽셀 직접 검출**로 교체(`DetectGridBoundaries`) — 격자선 후보 = 무채색(max-min ≤ 16) AND 비순백(max < 248) AND 비암부(min > 150) 픽셀. 이 후보가 교차 방향 길이의 60%(`GridLineSpanRatio`) 이상을 채우는 열/행의 연속 run 중앙을 경계로. 배경 제거는 알파만 바꾸고 RGB(격자선 회색)를 보존하므로 순서 무관하게 동작. (2) 여백 sliver(격자선-이미지 가장자리 1~2px 셀)는 `RefineGridBoundaries`(중앙값 40% 미만 셀 이웃 병합)로 정리. (3) 사용하지 않는 `BuildGridBoundaries`/`SubdivideMergedCells` 및 상수(`GridGapMaxFgRatio`/`MinGridGapThickness`/`GridCellSplitRatio`) 삭제, 격자선 검출 상수(`GridLineMaxSaturation`/`GridLineMaxChannel`/`GridLineMinChannel`/`GridLineSpanRatio`) 추가. (4) **재조립 제거** — 배경 제거된 원본을 그대로 `{name}_sheet.png`로 저장하고 격자 셀 위치에 바로 슬라이스 rect 적용(가변셀→좌상단 정렬 재조립 폐기). 프레임 간 위치·정렬이 원본 그대로 보존됨. 좌표는 `GetPixels32` 하단 원점 기준으로 콘텐츠 판정과 슬라이스 rect가 동일하게 매핑(`yLow = yBounds[gridRows-1-gr]` = rect.y).
  - 검증 상태: Python 충실 프로토타입으로 실측 3개 시트(1983x793, 2115x724) 전부 격자 경계가 원본 격자선과 정확히 일치(10x4/11x4/10x4, 여백 sliver 병합)하고, 배경 제거+격자 슬라이스로 각 콘텐츠 셀이 온전한 단일 스프라이트로 분리(빈 셀 제외, 검/투사체 등 이펙트 보존)됨을 오버레이·컷아웃 몽타주로 확인. Unity 컴파일·에디터 임포트는 사용자 테스트 대기(작업 시점 Unity MCP 브리지 8080 미기동으로 실제 임포트 미실행).
  - 관련 파일: MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs, SpriteSheetTool.cs

- [x] (2026-07-24 확정) **에지 페더링(안티에일리어싱 + 흰 헤일로 제거) 추가** — 배경 제거가 알파 0/255 하드 컷아웃이라 가장자리 계단현상·흰 테두리가 생기고 글로우 이펙트가 딱딱한 덩어리가 되던 "별로" 문제 해결. `RemoveWhiteBackground` 말미에 `FeatherEdges` 호출: 배경(알파0)에 8방향 인접한 전경 픽셀 중 min(R,G,B) ≥ `EdgeFeatherMinWhite`(200)인 근사 흰색(AA 잔여·헤일로)만 골라 흰색 정도에 비례한 부분 알파(`a = (255-min)*255/(255-200)`) 부여 + 흰색 언프리멀티플라이(`UnpremultiplyWhite`)로 흰 테두리 제거. 유채색·어두운 실루엣 픽셀(min<200)은 불투명 유지해 캐릭터 색 보존.
  - 검증 상태: Unity MCP `execute_code`로 실측 3개 시트 재임포트(분홍/흰/파랑 각 4행 40/36/34프레임) 후 결과 PNG 알파 분석·마젠타 합성·3배 확대로 확인 — 알파 고유값 2→56, 반투명 픽셀 발생, 가장자리 매끄러움·흰 헤일로 제거·블루 글로우 이펙트(마법진/화살/폭발/검광) 부드러운 반투명 보존·흰 캐릭터 흰머리 뭉치 침식 없음 확인. 미세한 격자선 잔여 파편(셀 경계, 무해)은 남음.
  - 관련 파일: MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs

- [x] (2026-07-24 확정) **격자선 잔여 줄 제거(`ClearGridLineBands`)** — 흰 캐릭터 시트에서 양옆 흰 실루엣에 갇힌 격자선 조각이 pocket restore로 복원돼 셀 높이만큼 세로줄 3개(x=195/1154/1347, 각 ~185px)가 남던 문제 해결. 내부 격자 경계 ±`GridLineClearRadius`(2px) 밴드의 무채색(채도≤18) 근사 흰색(min≥`GridLineClearMinWhite`=185) 픽셀만 투명화(콘텐츠는 경계를 넘지 않아 안전, 유채색 이펙트·캐릭터 채색 보존). 부수 효과로 잔여 격자선이 만들던 가짜 콘텐츠 셀도 제거돼 흰 캐릭터 검출 프레임 36→35(실제 그려진 6/8/10/11)로 정정됨.
  - 검증 상태: MCP `execute_code` 재임포트 후 3개 시트 프로그램 분석 — 잔여 격자선 0(전 경계 스캔), 행별 콘텐츠 셀 수 정확(10/10/10/10, 6/8/10/11, 9/8/8/9), 경계 인접 러닝 프레임 3배 확대로 트레일 리본까지 무손상 확인.
  - 관련 파일: MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs

- [x] (2026-07-24 확정·중요) **슬라이스 적용을 Unity 6 모던 API로 교체** — Sprite Editor에서 확인 시 슬라이스가 옛 데이터(재조립 시절 202px 피치·6행 row5/row6·텍스처 범위 초과 rect로 26/35개만 로드)로 남던 근본 문제 해결. 원인: Unity 6에서 deprecated `TextureImporter.spritesheet` 설정이 무시되어 새 슬라이스가 반영되지 않음(재임포트 직후에도 stale). 수정: `ApplySpriteSlices`를 `UnityEditor.U2D.Sprites.ISpriteEditorDataProvider`(SpriteDataProviderFactories → SetSpriteRects + ISpriteNameFileIdDataProvider.SetNameFileIdPairs → Apply)로 재작성. `MCPTools.Editor.asmdef`에 `Unity.2D.Sprite.Editor` 참조 추가. 시트 원해상도 보존 위해 `importer.maxTextureSize=8192`(기본 2048 초과 시트 다운스케일→슬라이스 어긋남 방지).
  - 검증 상태: MCP `execute_code` 재임포트 후 실제 Sprite 서브에셋 검사 — 3개 시트 각 40/35/34개 전부 로드(누락 0), 범위 초과(OOB) 0, 이미지2 원해상도 2115x724 유지(다운스케일 해소), 행 그룹 4개(walk/run/attack/death, row5/row6 소멸), rect가 검출 격자(198피치)와 정렬·중앙 피벗 확인. 실제 슬라이스 rect로 크롭한 몽타주로 셀당 캐릭터/이펙트 1개 분리 확인.
  - 관련 파일: MCPToolTest/Assets/MCPTools/Editor/SpriteSheet/SpriteSheetImporter.cs, MCPToolTest/Assets/MCPTools/Editor/MCPTools.Editor.asmdef

- [x] (2026-07-24 확정) **마무리 3종: MCP 노출 + 발밑 피벗 옵션 + 텍스처 임포트 설정**
  - (1) MCP 노출 갭 수정 — 브리지는 `McpToolRegistry`가 아니라 `McpForUnityAdapter.cs`의 `[McpForUnityTool]` 래퍼 클래스를 리플렉션으로 노출하는데 스프라이트 시트 도구 2개만 래퍼가 없어 커스텀 툴 목록(47개)에서 빠져 있었음. `McpToolsSpriteSheetBuildPromptTool`/`McpToolsSpriteSheetImportTool` 래퍼(파라미터 정의 포함) 추가 → `mcptools_spritesheet_build_prompt`/`_import`가 MCP로 호출 가능해짐(실호출 검증).
  - (2) 발밑 피벗 옵션 — `Import(..., bool pivotAtFeet=false)` + `pivotMode`(center/bottom) 파라미터. bottom이면 `TryComputeFeetPivot`으로 셀 콘텐츠 bbox를 구해 피벗을 수평 중앙+최하단(발밑, alignment=Custom)에 둠. 이동 애니메이션에서 발 고정. death 등은 center가 나을 수 있어 기본 center 유지. 검증: 실제 Sprite pivotNorm y≈0.09(발밑), x≈0.5.
  - (3) 텍스처 임포트 설정 — `ApplySpriteSlices`에 `maxTextureSize=8192`(원해상도), `textureCompression=CompressedHQ`(알파 그라데이션·글로우 품질), `spriteMeshType=FullRect`(투명 타이트 메시 클리핑 방지), `spriteGenerateFallbackPhysicsShape=false`(경량), `wrap=Clamp`/`filter=Bilinear` 적용.
  - 관련 파일: SpriteSheetImporter.cs, SpriteSheetTool.cs, Common/McpForUnityAdapter.cs

## 2. 에디터 테스트 체크리스트 (사용자가 Unity 에디터에서 직접 확인)

- [ ] `Tools/MCP/Sprite Sheet` 창에서 행 목록 편집(추가/삭제/프리셋/직접 입력) → 멀티 행 프롬프트 미리보기·클립보드 복사 동작
- [ ] 레퍼런스 이미지 토글 off + 캐릭터 설명 미입력 시 오류 안내, 입력 시 설명 기반 프롬프트 생성
- [ ] 프롬프트 JSON(행 목록 포함)이 `Assets/Docs/`에 저장됨
- [ ] 외부 AI로 생성한 흰 배경 멀티 행 시트 png를 임포터에 넣으면 배경이 제거된 정규화 시트가 `Assets/Generated/Images/`에 생성됨 (캐릭터 내부 흰색 보존 확인)
- [ ] 생성된 시트에 Sprite Mode=Multiple + 행별 동작명 슬라이스(`walk_01`~`death_10`)가 적용되어 Sprite Editor에서 확인 가능
- [ ] 행 수 또는 행별 프레임 수를 틀리게 입력하면 "행 N(동작)에서 M개 검출(기대 K개)" 형식 오류 안내가 표시됨
- [ ] MCP로 build_prompt(rows 파라미터) → import(imagePath/rows/backgroundMode) 호출이 동작함
- [ ] (추가 개편) 게임 장르/아트 스타일/추가 참고 입력이 템플릿 프롬프트에 반영됨 ("for a {genre} game", 스타일 문장, Additional notes)
- [ ] (추가 개편) AI CLI 드롭다운에 설치된 CLI가 표시되고 [AI로 프롬프트 생성] 실행 중 에디터가 멈추지 않으며 취소가 동작함
- [ ] (추가 개편) AI 생성 결과가 미리보기에 표시되고 JSON(promptSource="ai-cli:...")으로 저장됨
- [ ] (추가 개편) CLI 미설치/실행 실패 시 템플릿 방식으로 폴백되고 사유가 HelpBox로 안내됨
- [ ] (추가 개편) MCP build_prompt에 genre/artStyle/notes 파라미터가 반영됨
- [ ] (실측 개선) 배경 그라데이션 + 격자선이 있는 실제 ChatGPT 시트(`Assets/ChatGPT Image 2026년 7월 23일 오후 04_12_54.png`)가 배경 제거·행/프레임 검출에 성공함
- [ ] (실측 개선) 행별 프레임 수가 기대와 다를 때 "행별 검출 결과: ... (기대: ...)" 요약 + [검출된 구성으로 임포트]/[취소] 다이얼로그가 표시되고, 채택 시 검출 구성대로 슬라이스(`walk_01`~`walk_10` 등)가 적용됨
- [ ] (실측 개선) MCP import에서 allowDetected=false면 검출 요약 포함 실패, true면 검출 구성으로 임포트되고 결과에 기대/검출 구성이 모두 포함됨
