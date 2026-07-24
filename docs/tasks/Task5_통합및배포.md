# Task 5 — 통합 도구 + MCP 노출 정리 + 배포 패키지 검증

> 원본 계획: ../PLAN.md §4 Phase 5

## 1. 목표

4단계 도구를 하나의 통합 창(Pipeline)으로 묶고, 통합 MCP 도구와 진단 도구를 추가한다. README를 작성하고 `Assets/MCPTools/`만으로 .unitypackage 배포가 가능한지(자기완결성) 최종 검증한다.

## 2. 선행 조건

- Task 0~4 산출물 전체: 4단계 개별 도구와 MCP 도구가 모두 동작하는 상태

## 3. 구현 항목

1. **`PipelineWindow : EditorWindow`** — 메뉴 `Tools/MCP/Pipeline (All-in-One)`
   - 4단계를 탭/스텝퍼 UI로 통합, 단계별 산출물 자동 연결 (1 완료 → 2 입력 자동 지정 …)
   - 단계별 상태 표시 (미실행/완료/실패), 중간 단계부터 재시작 가능
2. **통합 MCP 도구** — `mcptools_run_pipeline`
   - `autoSelect:"none"`이면 3단계에서 멈추고 `pendingSelections` 반환 → 사용자가 선택 후 `mcptools_select_candidate` + `mcptools_apply_asset`으로 이어서 진행
3. **`mcptools_status`** — 설정·서버 상태·산출물 현황 조회 도구 (진단용)
4. **README.md 작성** (`Assets/MCPTools/README.md`)
   - 설치 절차(unitypackage import, unity-mcp 연결), ComfyUI 준비(주소 설정, 모델·워크플로 준비), 4단계 사용법, MCP 도구 레퍼런스, 트러블슈팅(서버 미기동, 포트 변경, 방화벽)
5. **배포 패키지 검증**
   - `Assets/MCPTools/`만 .unitypackage로 export
   - **깨끗한 신규 Unity 6 프로젝트에 import**하여 검증: 컴파일 오류 0, 외부 폴더(Generated/Docs/Design) 의존 없음, 첫 실행 시 필요한 폴더·설정 자동 생성 확인
   - 절대 경로 잔재 검색 (코드 내 `C:\` 등 검사)

## 4. 산출물

- `PipelineWindow` (Editor 코드)
- `mcptools_run_pipeline`, `mcptools_status` MCP 도구
- `Assets/MCPTools/README.md`
- 검증 완료된 .unitypackage (배포 패키지)

## 5. MCP 도구

| 도구명 | 파라미터 | 반환 |
|--------|----------|------|
| `mcptools_run_pipeline` | `designDocPath: string`, `autoSelect: "first" \| "none"` (후보 자동 선택 정책), `workflowName: string?` | `{ success, data: { assetListPath, promptSetPath, pendingSelections: [...], applied: [...] } }` |
| `mcptools_status` | 없음 | `{ success, data }` (설정·서버·산출물 상태) |

## 6. 완료 조건

- 체크리스트: [Task5_체크리스트.md](../checklist/Task5_체크리스트.md)
- 체크리스트의 구현 항목과 에디터 테스트(최종 인수 테스트) 항목을 모두 통과한다.
- **사용자 에디터 테스트 통과 시 프로젝트 완료. 이 Task의 테스트가 최종 인수 테스트이다.**
