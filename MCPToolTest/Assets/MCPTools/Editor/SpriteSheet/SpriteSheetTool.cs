using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>
    /// Task 6 스프라이트 시트 MCP 도구를 등록합니다.
    /// - mcptools_spritesheet_build_prompt: 멀티 행(동작별 Row) 통합 시트 프롬프트 조립 + JSON 저장.
    /// - mcptools_spritesheet_import: 외부 AI 멀티 행 시트 png → 배경 제거·정규화 + 동작명 기반 Sprite Multiple 슬라이스.
    /// - mcptools_spritesheet_build_clips: 슬라이스된 시트 → 동작별 AnimationClip(+ AnimatorController·프리팹 연결).
    /// </summary>
    [InitializeOnLoad]
    public static class SpriteSheetTool
    {
        static SpriteSheetTool()
        {
            McpToolRegistry.Register(
                "mcptools_spritesheet_build_prompt",
                "레퍼런스 이미지 첨부 전제의 멀티 행(동작별 Row) 통합 스프라이트 시트 프롬프트(영어)를 조립하고 " +
                "Assets/Docs/SpriteSheetPrompt_{id}.json으로 저장합니다. 반환한 prompt를 레퍼런스 이미지와 함께 " +
                "외부 AI(이미지 생성)에 붙여넣어 시트를 만든 뒤 mcptools_spritesheet_import로 가져옵니다. " +
                "파라미터: rows(선택, \"walk:8,run:8,attack:8,death:10\" 형식 — 동작명:프레임수 쉼표 나열, 기본값 동일), " +
                "useReferenceImage(선택, 기본 true), characterDescription(선택 — 보존할 특징 서술, useReferenceImage=false면 필수), " +
                "genre(선택 — 게임 장르 영어 자유 텍스트, 예: \"side-scrolling action\" — \"for a {genre} game\" 문구로 반영), " +
                "artStyle(선택 — 아트 스타일/분위기 영어 자유 텍스트, 예: \"SD chibi, dark fantasy\"), " +
                "notes(선택 — 추가 참고 사항, Important requirements에 부가 지시로 반영), " +
                "cellSize(선택, 기본 256), direction(선택, right/left/front, 기본 right — front는 정면 시점이라 " +
                "side-view 대신 front-view 문구로 조립됩니다), background(선택, white/transparent, 기본 white).",
                ExecuteBuildPrompt);

            McpToolRegistry.Register(
                "mcptools_spritesheet_import",
                "외부 AI가 생성한 멀티 행 스프라이트 시트 png를 격자선 기준으로 슬라이스합니다. 배경 모드가 white면 " +
                "외곽 시드 BFS(그라데이션 허용 오차 + 근사 흰색 + 무채색 조건 — 유채색 글로우 이펙트 보존)로 " +
                "배경을 투명화한 뒤, 시트에 그려진 격자선을 직접 검출해 균일 셀 경계를 만들고 재조립 없이 원본 셀 " +
                "위치 그대로 Assets/Generated/3_Confirmed/SpriteSheets/{name}_sheet.png로 저장하고 행 동작명 기반(walk_01~) " +
                "Sprite Mode=Multiple 슬라이스를 적용합니다. 격자선이 곧 정답이므로 행/프레임 수가 rows와 달라도 " +
                "검출된 격자 그대로 임포트하되, 자동 행 이름(rowN)은 붙이지 않습니다 — 검출 행 수보다 rows가 적으면 " +
                "어느 행의 이름이 비었는지 알리며 실패합니다. 전경 픽셀이 거의 없어 비어 보이는 셀은 자동으로 제외되고, " +
                "그 결과 프레임이 하나도 남지 않은 행(여백 밴드 등)은 통째로 빠져 rows에 이름을 넣을 필요가 없습니다. " +
                "먼저 dryRun=true로 검출 결과를 받아 rows를 채운 뒤 다시 호출하세요. " +
                "파라미터: imagePath(필수, 절대 경로 또는 Assets/ 상대 경로 png), " +
                "rows(dryRun=false일 때 필수, \"walk:8,run:8,attack:8,death:10\" 형식 — 시트의 위 행부터 순서대로), " +
                "dryRun(선택, 기본 false — true면 배경 제거·격자 검출까지만 하고 행 수/행별 프레임 수/셀 크기/자동 제외된 " +
                "빈 셀 정보만 반환하며 파일 저장·슬라이스를 하지 않습니다), " +
                "backgroundMode(선택, white/transparent, 기본 white), " +
                "pivotMode(선택, center/bottom, 기본 center — bottom이면 각 스프라이트 피벗을 발밑(콘텐츠 수평 중앙+최하단)에 두어 이동 애니메이션 흔들림 감소).",
                ExecuteImport);

            McpToolRegistry.Register(
                "mcptools_spritesheet_build_clips",
                "슬라이스가 끝난 스프라이트 시트의 서브 스프라이트({동작}_{번호})를 동작별로 묶어 " +
                "Assets/Generated/3_Confirmed/Animations/{시트이름}/{동작}.anim AnimationClip을 만듭니다. " +
                "같은 경로에 클립이 있으면 새로 만들지 않고 커브·프레임 레이트·루프 설정만 덮어씁니다 " +
                "(Animator 참조와 붙여 둔 애니메이션 이벤트 보존). createController=true면 " +
                "{시트이름}.controller를 만들고 동작별 State를 배치하며(기존 컨트롤러는 없는 State만 추가), " +
                "targetPrefabPath를 함께 주면 프리팹 루트에 Animator를 붙이고 컨트롤러를 할당합니다. " +
                "파라미터: sheetPath(필수, Assets/ 상대 시트 텍스처 경로), frameRate(선택, 기본 12), " +
                "targetComponent(선택, SpriteRenderer|Image, 기본 SpriteRenderer), " +
                "loopActions(선택, 루프 ON으로 둘 동작명 쉼표 나열 — 미지정 시 idle/walk/run ON·그 외 OFF 기본 규칙), " +
                "createController(선택, 기본 false), targetPrefabPath(선택), " +
                "targetObjectPath(선택 — 스프라이트 컴포넌트가 있는 오브젝트의 프리팹 루트 기준 경로, 커브 경로로도 사용).",
                ExecuteBuildClips);
        }

        private static object ExecuteBuildPrompt(Dictionary<string, object> parameters)
        {
            string rowsText = GetString(parameters, "rows");
            List<SpriteSheetRowDef> rows = string.IsNullOrWhiteSpace(rowsText)
                ? SpriteSheetPromptBuilder.DefaultRows()
                : SpriteSheetPromptBuilder.ParseRows(rowsText);

            bool useReferenceImage = GetBool(parameters, "useReferenceImage", true);
            string characterDescription = GetString(parameters, "characterDescription") ?? string.Empty;
            int cellSize = GetInt(parameters, "cellSize", 256);
            SpriteSheetFacing facing = SpriteSheetPromptBuilder.ParseFacing(GetString(parameters, "direction"));
            bool whiteBackground = ParseBackground(GetString(parameters, "background"));
            string genre = GetString(parameters, "genre") ?? string.Empty;
            string artStyle = GetString(parameters, "artStyle") ?? string.Empty;
            string notes = GetString(parameters, "notes") ?? string.Empty;

            // MCP 경로는 호출 주체가 이미 AI이므로 CLI를 재호출하지 않고 템플릿 방식으로 게임 컨텍스트를 반영한다.
            string prompt = SpriteSheetPromptBuilder.BuildPrompt(
                useReferenceImage, characterDescription, rows, cellSize, facing, whiteBackground,
                genre, artStyle, notes);
            string savedPath = SpriteSheetPromptBuilder.SavePromptJson(
                useReferenceImage, characterDescription, rows, cellSize, facing, whiteBackground, prompt,
                genre, artStyle, notes, "template");

            var rowSummaries = new List<object>();
            foreach (SpriteSheetRowDef row in rows)
            {
                rowSummaries.Add(new Dictionary<string, object>
                {
                    { "action", row.action },
                    { "frameCount", row.frameCount }
                });
            }

            return new Dictionary<string, object>
            {
                { "prompt", prompt },
                { "savedPath", savedPath },
                { "rows", rowSummaries },
                { "background", whiteBackground ? "white" : "transparent" }
            };
        }

        private static object ExecuteImport(Dictionary<string, object> parameters)
        {
            string imagePath = GetString(parameters, "imagePath");
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new ArgumentException("imagePath 파라미터가 필요합니다 (시트 png의 절대 경로 또는 Assets/ 상대 경로).");
            }

            bool whiteBackground = ParseBackground(GetString(parameters, "backgroundMode"));
            bool pivotAtFeet = string.Equals(
                GetString(parameters, "pivotMode"), "bottom", StringComparison.OrdinalIgnoreCase);

            if (GetBool(parameters, "dryRun", false))
            {
                return ExecuteDetectOnly(imagePath, whiteBackground);
            }

            string rowsText = GetString(parameters, "rows");
            if (string.IsNullOrWhiteSpace(rowsText))
            {
                throw new ArgumentException(
                    "rows 파라미터가 필요합니다. 시트의 위 행부터 \"동작명:프레임수\"를 쉼표로 나열합니다. " +
                    "예: \"walk:8,run:8,attack:8,death:10\"\n" +
                    "행 구성을 모르면 먼저 dryRun=true로 호출해 검출 결과를 확인하세요.");
            }

            List<SpriteSheetRowDef> rows = SpriteSheetPromptBuilder.ParseRows(rowsText);

            SpriteSheetImportResult result =
                SpriteSheetImporter.Import(imagePath, rows, whiteBackground, false, pivotAtFeet);

            var framesPerRow = new List<object>();
            for (int i = 0; i < result.framesPerRow.Length; i++)
            {
                framesPerRow.Add(new Dictionary<string, object>
                {
                    { "row", i + 1 },
                    { "action", result.rowActions[i] },
                    { "detectedFrameCount", result.framesPerRow[i] },
                    { "expectedFrameCount", i < result.expectedFramesPerRow.Length ? result.expectedFramesPerRow[i] : (object)null }
                });
            }

            return new Dictionary<string, object>
            {
                { "dryRun", false },
                { "applied", true },
                { "assetPath", result.assetPath },
                // MCP 경로는 확인 다이얼로그 없이 덮어쓰므로, 덮어썼다는 사실을 응답에 명시한다 (Task 10 R5).
                { "overwroteExisting", result.overwroteExisting },
                { "rowCount", result.rowCount },
                { "expectedRowCount", result.expectedRowCount },
                { "usedDetectedLayout", result.usedDetectedLayout },
                { "totalFrameCount", result.totalFrameCount },
                { "framesPerRow", framesPerRow },
                { "cellWidth", result.cellWidth },
                { "cellHeight", result.cellHeight },
                { "spriteMode", "Multiple" }
            };
        }

        /// <summary>
        /// dryRun 경로: 배경 제거 + 격자 검출까지만 하고 검출 결과만 반환합니다.
        /// 파일 저장·슬라이스 적용을 하지 않으므로 프로젝트에 아무것도 기록하지 않습니다.
        /// </summary>
        private static object ExecuteDetectOnly(string imagePath, bool whiteBackground)
        {
            SpriteSheetDetection detection = SpriteSheetImporter.Detect(imagePath, whiteBackground, false);

            // 비어 보이는 셀은 검출 단계에서 이미 자동 제외됐다. 이름을 붙일 행 = 프레임이 남은 행만.
            var rowInfos = new List<object>();
            int rowNo = 0;
            foreach (SpriteSheetDetectedRow row in detection.rows)
            {
                if (row.IncludedFrameCount == 0)
                {
                    continue; // 전 프레임이 자동 제외된 행(여백 밴드 등)은 슬라이스되지 않으므로 이름도 필요 없음
                }

                var autoExcluded = new List<object>();
                for (int i = 0; i < row.cells.Count; i++)
                {
                    if (row.cells[i].looksEmpty)
                    {
                        autoExcluded.Add(new Dictionary<string, object>
                        {
                            { "cell", i + 1 },
                            { "contentRatio", (float)Math.Round(row.cells[i].contentRatio, 4) }
                        });
                    }
                }

                rowNo++;
                rowInfos.Add(new Dictionary<string, object>
                {
                    { "row", rowNo },
                    { "detectedFrameCount", row.IncludedFrameCount },
                    { "autoExcludedEmptyCells", autoExcluded }
                });
            }

            return new Dictionary<string, object>
            {
                { "dryRun", true },
                { "applied", false },
                { "rowCount", detection.IncludedRowCount },
                { "totalFrameCount", detection.TotalFrameCount - detection.LooksEmptyFrameCount },
                { "autoExcludedEmptyFrameCount", detection.LooksEmptyFrameCount },
                { "framesPerRow", rowInfos },
                { "cellWidth", detection.cellWidth },
                { "cellHeight", detection.cellHeight },
                { "note",
                    "검출만 수행했고 슬라이스는 적용하지 않았습니다. 위 행 수·행별 프레임 수에 맞춰 " +
                    "rows(\"동작명:프레임수\" 쉼표 나열)를 채운 뒤 dryRun 없이 다시 호출하세요. " +
                    "전경 픽셀 비율이 낮아 비어 보이는 셀(autoExcludedEmptyCells)은 자동으로 제외되어 " +
                    "위 프레임 수·행 수에 이미 반영돼 있습니다. 실제 콘텐츠였다면 Sprite Sheet 창에서 다시 체크할 수 있습니다." }
            };
        }

        /// <summary>
        /// 슬라이스된 시트에서 동작별 AnimationClip을 만들고, 옵션에 따라 AnimatorController 생성 +
        /// 대상 프리팹 연결까지 수행합니다.
        /// </summary>
        private static object ExecuteBuildClips(Dictionary<string, object> parameters)
        {
            string sheetPath = GetString(parameters, "sheetPath");
            if (string.IsNullOrWhiteSpace(sheetPath))
            {
                throw new ArgumentException(
                    "sheetPath 파라미터가 필요합니다 (슬라이스가 끝난 시트 텍스처의 Assets/ 기준 상대 경로). " +
                    "예: Assets/Generated/3_Confirmed/SpriteSheets/hero_sheet.png");
            }

            MCPToolSettings settings = MCPToolSettings.GetOrCreate();
            int frameRate = GetInt(parameters, "frameRate", Mathf.Max(1, settings.spriteAnimationFrameRate));
            string targetComponent = GetString(parameters, "targetComponent");
            bool createController = GetBool(parameters, "createController", false);
            string targetPrefabPath = GetString(parameters, "targetPrefabPath") ?? string.Empty;
            string targetObjectPath = GetString(parameters, "targetObjectPath") ?? string.Empty;

            SpriteSheetClipPlan plan = SpriteSheetClipBuilder.Scan(sheetPath);

            // loopActions를 지정하면 그 목록만 루프 ON이 되고 나머지는 OFF가 된다 (미지정 시 기본 규칙 유지).
            string loopActionsText = GetString(parameters, "loopActions");
            if (!string.IsNullOrWhiteSpace(loopActionsText))
            {
                var loopSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entry in loopActionsText.Split(','))
                {
                    string trimmed = entry.Trim();
                    if (trimmed.Length > 0)
                    {
                        loopSet.Add(trimmed);
                    }
                }

                foreach (SpriteSheetActionGroup group in plan.groups)
                {
                    group.loop = loopSet.Contains(group.action);
                }
            }

            SpriteSheetClipBuildResult result = SpriteSheetClipBuilder.Build(
                plan, frameRate, targetComponent, createController, targetPrefabPath, targetObjectPath);

            var clips = new List<object>();
            foreach (SpriteSheetActionGroup group in result.groups)
            {
                clips.Add(new Dictionary<string, object>
                {
                    { "action", group.action },
                    { "clipPath", group.clipPath },
                    { "frameCount", group.frameCount },
                    { "loop", group.loop },
                    { "created", group.created }
                });
            }

            return new Dictionary<string, object>
            {
                { "sheetPath", result.sheetPath },
                { "frameRate", Mathf.Max(1, frameRate) },
                { "targetComponent", SpriteSheetClipBuilder.ResolveTargetType(targetComponent).Name },
                { "objectPath", result.objectPath },
                { "clips", clips },
                { "controllerPath", result.controllerPath },
                { "addedStates", result.addedStates },
                { "prefabPath", result.prefabPath },
                { "prefabLinked", result.prefabLinked },
                { "skipped", result.skipped }
            };
        }

        /// <summary>"white"/"transparent" 문자열을 해석합니다 (기본 white).</summary>
        private static bool ParseBackground(string value)
        {
            if (string.Equals(value, "transparent", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "white", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            throw new ArgumentException($"배경 모드 값이 잘못되었습니다: \"{value}\". \"white\" 또는 \"transparent\"를 사용해주세요.");
        }

        private static string GetString(Dictionary<string, object> parameters, string key)
        {
            return parameters != null && parameters.TryGetValue(key, out object v) && v is string s ? s : null;
        }

        private static int GetInt(Dictionary<string, object> parameters, string key, int defaultValue)
        {
            if (parameters != null && parameters.TryGetValue(key, out object v))
            {
                if (v is long l) return (int)l;
                if (v is int i) return i;
                if (v is double d) return (int)d;
                if (v is string s && int.TryParse(s, out int parsed)) return parsed;
            }

            return defaultValue;
        }

        private static bool GetBool(Dictionary<string, object> parameters, string key, bool defaultValue)
        {
            if (parameters != null && parameters.TryGetValue(key, out object v))
            {
                if (v is bool b) return b;
                if (v is string s && bool.TryParse(s, out bool parsed)) return parsed;
            }

            return defaultValue;
        }
    }
}
