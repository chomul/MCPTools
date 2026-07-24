using System;
using System.Collections.Generic;
using UnityEditor;

namespace MCPTools.Editor
{
    /// <summary>
    /// Task 6 스프라이트 시트 MCP 도구를 등록합니다.
    /// - mcptools_spritesheet_build_prompt: 멀티 행(동작별 Row) 통합 시트 프롬프트 조립 + JSON 저장.
    /// - mcptools_spritesheet_import: 외부 AI 멀티 행 시트 png → 배경 제거·정규화 + 동작명 기반 Sprite Multiple 슬라이스.
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
                "cellSize(선택, 기본 256), direction(선택, right/left, 기본 right), background(선택, white/transparent, 기본 white).",
                ExecuteBuildPrompt);

            McpToolRegistry.Register(
                "mcptools_spritesheet_import",
                "외부 AI가 생성한 멀티 행 스프라이트 시트 png를 격자선 기준으로 슬라이스합니다. 배경 모드가 white면 " +
                "외곽 시드 BFS(그라데이션 허용 오차 + 근사 흰색 + 무채색 조건 — 유채색 글로우 이펙트 보존)로 " +
                "배경을 투명화한 뒤, 시트에 그려진 격자선을 직접 검출해 균일 셀 경계를 만들고 재조립 없이 원본 셀 " +
                "위치 그대로 Assets/Generated/Images/{name}_sheet.png로 저장하고 행 동작명 기반(walk_01~) " +
                "Sprite Mode=Multiple 슬라이스를 적용합니다. 격자선이 곧 정답이므로 행/프레임 수가 rows와 달라도 " +
                "검출된 격자 그대로 임포트합니다(행 이름은 순서대로 rows의 동작명, 초과 행은 rowN 자동 이름). " +
                "파라미터: imagePath(필수, 절대 경로 또는 Assets/ 상대 경로 png), " +
                "rows(필수, \"walk:8,run:8,attack:8,death:10\" 형식 — 시트의 위 행부터 순서대로), " +
                "backgroundMode(선택, white/transparent, 기본 white), " +
                "pivotMode(선택, center/bottom, 기본 center — bottom이면 각 스프라이트 피벗을 발밑(콘텐츠 수평 중앙+최하단)에 두어 이동 애니메이션 흔들림 감소).",
                ExecuteImport);
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
            bool faceRight = !string.Equals(GetString(parameters, "direction"), "left", StringComparison.OrdinalIgnoreCase);
            bool whiteBackground = ParseBackground(GetString(parameters, "background"));
            string genre = GetString(parameters, "genre") ?? string.Empty;
            string artStyle = GetString(parameters, "artStyle") ?? string.Empty;
            string notes = GetString(parameters, "notes") ?? string.Empty;

            // MCP 경로는 호출 주체가 이미 AI이므로 CLI를 재호출하지 않고 템플릿 방식으로 게임 컨텍스트를 반영한다.
            string prompt = SpriteSheetPromptBuilder.BuildPrompt(
                useReferenceImage, characterDescription, rows, cellSize, faceRight, whiteBackground,
                genre, artStyle, notes);
            string savedPath = SpriteSheetPromptBuilder.SavePromptJson(
                useReferenceImage, characterDescription, rows, cellSize, faceRight, whiteBackground, prompt,
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

            string rowsText = GetString(parameters, "rows");
            if (string.IsNullOrWhiteSpace(rowsText))
            {
                throw new ArgumentException(
                    "rows 파라미터가 필요합니다. 시트의 위 행부터 \"동작명:프레임수\"를 쉼표로 나열합니다. " +
                    "예: \"walk:8,run:8,attack:8,death:10\"");
            }

            List<SpriteSheetRowDef> rows = SpriteSheetPromptBuilder.ParseRows(rowsText);
            bool whiteBackground = ParseBackground(GetString(parameters, "backgroundMode"));
            bool pivotAtFeet = string.Equals(
                GetString(parameters, "pivotMode"), "bottom", StringComparison.OrdinalIgnoreCase);

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
                { "assetPath", result.assetPath },
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
