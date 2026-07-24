using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;

namespace MCPTools.Editor
{
    /// <summary>스프라이트 시트의 동작 행(Row) 정의입니다. (동작명 + 프레임 수)</summary>
    [Serializable]
    public sealed class SpriteSheetRowDef
    {
        /// <summary>동작명 (walk/run/attack/idle/death 프리셋 또는 자유 입력). 슬라이스 이름의 접두어로도 사용됩니다.</summary>
        public string action = "walk";

        /// <summary>이 행의 프레임 수 (1 이상).</summary>
        public int frameCount = 8;

        public SpriteSheetRowDef() { }

        public SpriteSheetRowDef(string action, int frameCount)
        {
            this.action = action;
            this.frameCount = frameCount;
        }
    }

    /// <summary>
    /// 레퍼런스 이미지 첨부를 전제로 여러 동작을 행(Row)별로 담은 통합 스프라이트 시트 프롬프트(영어)를
    /// 조립하고 JSON 문서로 저장하는 정적 유틸리티입니다.
    /// 창(<see cref="SpriteSheetPromptWizard"/>)과 MCP 도구(SpriteSheetTool)가 공용으로 사용합니다.
    /// </summary>
    public static class SpriteSheetPromptBuilder
    {
        /// <summary>행당 최대 프레임 수 제한입니다. (프레임이 많을수록 셀이 작아지고 일관성이 떨어져 10으로 제한)</summary>
        public const int MaxFrameCount = 10;

        /// <summary>동작 프리셋 키 목록.</summary>
        public static readonly string[] ActionPresets = { "walk", "run", "attack", "idle", "death" };

        /// <summary>기본 행 구성(walk 8 / run 8 / attack 8 / death 10)을 반환합니다.</summary>
        public static List<SpriteSheetRowDef> DefaultRows()
        {
            return new List<SpriteSheetRowDef>
            {
                new SpriteSheetRowDef("walk", 8),
                new SpriteSheetRowDef("run", 8),
                new SpriteSheetRowDef("attack", 8),
                new SpriteSheetRowDef("death", 10)
            };
        }

        /// <summary>
        /// "walk:8,run:8,attack:8,death:10" 형식 문자열을 행 정의 목록으로 파싱합니다.
        /// </summary>
        /// <exception cref="ArgumentException">형식이 잘못된 경우.</exception>
        public static List<SpriteSheetRowDef> ParseRows(string rows)
        {
            if (string.IsNullOrWhiteSpace(rows))
            {
                throw new ArgumentException("rows 파라미터가 비어 있습니다. 예: \"walk:8,run:8,attack:8,death:10\"");
            }

            var result = new List<SpriteSheetRowDef>();
            foreach (string token in rows.Split(','))
            {
                string trimmed = token.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }

                string[] parts = trimmed.Split(':');
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) ||
                    !int.TryParse(parts[1].Trim(), out int count) || count < 1 || count > MaxFrameCount)
                {
                    throw new ArgumentException(
                        $"rows 항목 형식이 잘못되었습니다: \"{trimmed}\"\n" +
                        $"\"동작명:프레임수\"(프레임수 1~{MaxFrameCount})를 쉼표로 나열해주세요. 예: \"walk:8,run:8,attack:8,death:10\"");
                }

                result.Add(new SpriteSheetRowDef(parts[0].Trim().ToLowerInvariant(), count));
            }

            if (result.Count == 0)
            {
                throw new ArgumentException("rows 파라미터에 유효한 행이 없습니다. 예: \"walk:8,run:8,attack:8,death:10\"");
            }

            return result;
        }

        /// <summary>
        /// 사용자가 검증한 구조를 템플릿화하여 멀티 행 통합 스프라이트 시트 프롬프트(영어)를 조립합니다.
        /// </summary>
        /// <param name="useReferenceImage">레퍼런스 이미지 첨부 전제 여부. false면 characterDescription이 필수입니다.</param>
        /// <param name="characterDescription">캐릭터 특징 서술(선택). 레퍼런스 미사용 시에는 캐릭터 설명(필수).</param>
        /// <param name="rows">동작 행 목록 (1개 이상).</param>
        /// <param name="cellSize">프레임 셀 크기(px, 기본 256).</param>
        /// <param name="faceRight">true면 RIGHT, false면 LEFT를 바라보게 지시합니다.</param>
        /// <param name="whiteBackground">true면 흰색 단색 배경(임포트 시 제거 전제), false면 투명 배경 지시.</param>
        /// <param name="gameGenre">게임 장르(선택, 자유 텍스트, 예: "side-scrolling action"). "for a {genre} game" 문구로 반영됩니다.</param>
        /// <param name="artStyle">아트 스타일/분위기(선택, 자유 텍스트, 예: "SD chibi, dark fantasy"). 스타일 유지 문장에 삽입됩니다.</param>
        /// <param name="extraNotes">추가 참고 사항(선택). 프롬프트 끝부분에 부가 지시로 반영됩니다.</param>
        /// <returns>조립된 영어 프롬프트 문자열.</returns>
        public static string BuildPrompt(
            bool useReferenceImage,
            string characterDescription,
            List<SpriteSheetRowDef> rows,
            int cellSize,
            bool faceRight,
            bool whiteBackground,
            string gameGenre = null,
            string artStyle = null,
            string extraNotes = null)
        {
            if (rows == null || rows.Count == 0)
            {
                throw new ArgumentException("동작 행(rows)이 1개 이상 필요합니다.");
            }

            foreach (SpriteSheetRowDef row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.action))
                {
                    throw new ArgumentException("동작명이 비어 있는 행이 있습니다. 각 행에 동작명을 입력해주세요.");
                }

                if (row.frameCount < 1 || row.frameCount > MaxFrameCount)
                {
                    throw new ArgumentException($"행 \"{row.action}\"의 프레임 수는 1~{MaxFrameCount} 사이여야 합니다.");
                }
            }

            if (!useReferenceImage && string.IsNullOrWhiteSpace(characterDescription))
            {
                throw new ArgumentException(
                    "레퍼런스 이미지를 사용하지 않을 때는 캐릭터 설명(characterDescription)이 필수입니다.");
            }

            if (cellSize < 32)
            {
                throw new ArgumentException("셀 크기(cellSize)는 32 이상이어야 합니다.");
            }

            string direction = faceRight ? "RIGHT" : "LEFT";
            string genre = (gameGenre ?? string.Empty).Trim();
            string style = (artStyle ?? string.Empty).Trim();
            string notes = (extraNotes ?? string.Empty).Trim();
            string genrePhrase = genre.Length > 0 ? $" for a {genre} game" : string.Empty;
            var sb = new StringBuilder();

            // 1) 레퍼런스/캐릭터 지시 (+게임 컨텍스트)
            if (useReferenceImage)
            {
                sb.AppendLine("Use the attached reference image as the exact character design reference.");
                sb.Append($"Generate a full-body 2D side-scrolling sprite sheet{genrePhrase} for Unity using this character. ");
                sb.Append(style.Length > 0
                    ? $"Keep the character in the same style as the reference image, rendered with a {style} art style and mood. "
                    : "Keep the character in the same style as the reference image. ");
                if (!string.IsNullOrWhiteSpace(characterDescription))
                {
                    sb.Append($"Preserve the key design features: {characterDescription.Trim()}. ");
                }
            }
            else
            {
                sb.Append($"Generate a full-body 2D side-scrolling sprite sheet{genrePhrase} for Unity of the following character: ");
                sb.Append(characterDescription.Trim());
                sb.Append(". ");
                if (style.Length > 0)
                {
                    sb.Append($"Render the character in a {style} art style and mood, kept consistent across all frames. ");
                }
            }

            sb.AppendLine("The character must remain visually consistent across all frames.");

            // 2) 카메라/구도 고정 지시
            sb.Append("Create a clean orthographic side-view sprite sheet. ");
            sb.Append($"The character should face {direction} in every frame only. ");
            sb.Append("Full body must be visible in every frame. ");
            sb.Append("Keep the camera fixed, keep the character at a consistent scale, keep the ground level consistent, ");
            sb.Append("and keep the body centered within each frame. ");
            sb.AppendLine("No perspective change, no motion blur, no extra props, no extra characters, no cropped limbs, and no background details.");

            // 3) 그리드/배경 지시 (모든 행에 동일한 셀 크기를 강제하기 위해 최대 프레임 수 기준 단일 그리드 + 격자선)
            int maxFrames = 0;
            foreach (SpriteSheetRowDef row in rows)
            {
                maxFrames = Math.Max(maxFrames, row.frameCount);
            }

            sb.Append($"Output one complete sprite sheet laid out on a single uniform grid of {maxFrames} columns by {rows.Count} rows. ");
            sb.Append($"Every cell in every row must be exactly the same size ({cellSize}x{cellSize} pixels), including the empty padding around the character. ");
            sb.Append("Rows with fewer frames than the grid width must leave their remaining trailing cells completely empty — ");
            sb.Append("never stretch the frames of a shorter row to fill the row width. ");
            sb.Append("Draw thin light gray grid lines exactly on every cell boundary, with perfectly uniform spacing, so each cell is clearly separated and the sheet can be sliced evenly. ");
            sb.Append("CRITICAL: the grid must have ZERO margin outside of it — the outermost grid lines ARE the image edges. ");
            sb.Append("The grid starts exactly at the top-left corner pixel of the image and ends exactly at the bottom-right corner pixel, ");
            sb.Append("with absolutely no empty space, border, or padding between the grid and the image edges on any side. ");
            sb.Append("Place the character at the same position inside every cell: centered horizontally, ");
            sb.Append("and keep the feet on the exact same ground baseline in EVERY cell of EVERY row — ");
            sb.Append("the ground level must be identical across the entire sheet, not just within one row. ");
            sb.Append(whiteBackground
                ? "Use a plain solid pure white (#FFFFFF) background inside every cell, perfectly uniform with no gradient, no vignette, " +
                  "and no shadows, so the background can be removed later. "
                : "Use a fully transparent background with no shadow. ");
            sb.AppendLine("Do not add any text, UI, captions, labels, or decorative borders.");

            // 4) 동작 행 목록
            sb.AppendLine("Arrange the sprite sheet in separate animation rows:");
            for (int i = 0; i < rows.Count; i++)
            {
                sb.AppendLine($"Row {i + 1}: {RowLabel(rows[i].action)}, {rows[i].frameCount} frames. {ActionDirective(rows[i].action)}");
            }

            // 5) 공통 요구사항
            sb.AppendLine("Important requirements:");
            sb.AppendLine("- Keep the character proportions, outfit, and color palette identical in every frame.");
            string loopActions = JoinLoopActions(rows);
            if (!string.IsNullOrEmpty(loopActions))
            {
                sb.AppendLine($"- Make {loopActions} loop cleanly from the last frame back to the first frame.");
            }
            sb.AppendLine("- Keep the feet on one identical ground baseline across every row and every cell of the sheet.");
            sb.AppendLine("- Keep silhouettes clear and readable for gameplay.");
            sb.AppendLine("- Keep each frame fully inside its own cell, never touching or crossing cell boundaries.");
            sb.AppendLine("- All cells must be identical in size, including padding, so the sheet can be sliced evenly on a uniform grid.");
            sb.AppendLine("- The grid must fill the entire image with ZERO outer margin: the outermost grid lines are the image edges themselves, on all four sides.");
            if (notes.Length > 0)
            {
                sb.AppendLine($"- Additional notes: {notes}");
            }

            // 6) 개별 프레임 명명 조건부 요청
            sb.Append("If supported, also export each individual frame separately using names like: ");
            sb.Append(JoinFrameNameRanges(rows));
            sb.Append(". If separate frame export is not supported, generate only the complete sprite sheet.");

            return sb.ToString();
        }

        /// <summary>
        /// 로컬 AI CLI에게 전달할 메타 프롬프트를 조립합니다.
        /// 검증된 템플릿 구조(레퍼런스 지시/카메라·스케일·지면 고정/여백 포함 동일 셀 크기/행별 애니메이션 정의/
        /// Important requirements/개별 프레임 명명)를 예시로 포함하고, 이 구조를 반드시 유지한 채
        /// 게임 컨텍스트(장르/스타일/참고)를 반영한 영어 이미지 생성 프롬프트만 출력하도록 지시합니다.
        /// </summary>
        /// <returns>AI CLI에 stdin으로 전달할 메타 프롬프트 문자열.</returns>
        public static string BuildMetaPrompt(
            bool useReferenceImage,
            string characterDescription,
            List<SpriteSheetRowDef> rows,
            int cellSize,
            bool faceRight,
            bool whiteBackground,
            string gameGenre,
            string artStyle,
            string extraNotes)
        {
            // 게임 컨텍스트 없이 조립한 템플릿을 "검증된 구조 예시"로 포함한다.
            string templateExample = BuildPrompt(
                useReferenceImage, characterDescription, rows, cellSize, faceRight, whiteBackground);

            string genre = (gameGenre ?? string.Empty).Trim();
            string style = (artStyle ?? string.Empty).Trim();
            string notes = (extraNotes ?? string.Empty).Trim();

            var sb = new StringBuilder();
            sb.AppendLine("You are an expert prompt writer for AI image generation of 2D game sprite sheets.");
            sb.AppendLine("Write ONE English image-generation prompt for a multi-row character sprite sheet.");
            sb.AppendLine();
            sb.AppendLine("A verified prompt structure is provided below as an example. You MUST keep every part of that structure intact:");
            sb.AppendLine("- The reference-image / character description instruction at the top (keep it exactly as given).");
            sb.AppendLine("- The fixed camera, consistent scale, consistent ground level, and centered-body instructions.");
            sb.AppendLine($"- The uniform grid instruction with exactly {cellSize}x{cellSize} pixel cells including the empty padding, identical for every cell.");
            sb.AppendLine("- The per-row animation definitions (Row 1, Row 2, ...) with the same actions and frame counts.");
            sb.AppendLine("- The full \"Important requirements:\" list.");
            sb.AppendLine("- The individual frame naming request at the end (walk_01 style names).");
            sb.AppendLine("- The background instruction (white or transparent) exactly as given.");
            sb.AppendLine();
            sb.AppendLine("While keeping that structure, enrich the wording to reflect this game context " +
                          "(motion feel, mood, style adjectives, action staging appropriate to the genre):");
            sb.AppendLine($"- Game genre: {(genre.Length > 0 ? genre : "(not specified)")}");
            sb.AppendLine($"- Art style / mood: {(style.Length > 0 ? style : "(not specified)")}");
            sb.AppendLine($"- Additional notes: {(notes.Length > 0 ? notes : "(none)")}");
            sb.AppendLine();
            sb.AppendLine("Output rules:");
            sb.AppendLine("- Output ONLY the final prompt text.");
            sb.AppendLine("- Do NOT add explanations, headings, markdown code fences, or quotation marks around the prompt.");
            sb.AppendLine();
            sb.AppendLine("=== Verified example structure (keep this structure) ===");
            sb.AppendLine(templateExample.TrimEnd());
            return sb.ToString();
        }

        /// <summary>
        /// AI CLI stdout을 최소한으로 정리합니다: 앞뒤 공백 제거 + 전체를 감싼 마크다운 코드펜스(```) 제거.
        /// </summary>
        public static string CleanAiOutput(string raw)
        {
            string text = (raw ?? string.Empty).Trim();
            if (text.StartsWith("```"))
            {
                int firstLineEnd = text.IndexOf('\n');
                int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLineEnd >= 0 && lastFence > firstLineEnd)
                {
                    text = text.Substring(firstLineEnd + 1, lastFence - firstLineEnd - 1).Trim();
                }
            }

            return text;
        }

        /// <summary>
        /// 답변과 조립된 프롬프트를 <c>Assets/Docs/SpriteSheetPrompt_{id}.json</c>으로 저장합니다.
        /// </summary>
        /// <param name="gameGenre">게임 장르(선택).</param>
        /// <param name="artStyle">아트 스타일/분위기(선택).</param>
        /// <param name="extraNotes">추가 참고 사항(선택).</param>
        /// <param name="promptSource">프롬프트 생성 경로("template" 또는 "ai-cli:{command}").</param>
        /// <returns>저장된 Assets/ 기준 상대 경로.</returns>
        public static string SavePromptJson(
            bool useReferenceImage,
            string characterDescription,
            List<SpriteSheetRowDef> rows,
            int cellSize,
            bool faceRight,
            bool whiteBackground,
            string prompt,
            string gameGenre = null,
            string artStyle = null,
            string extraNotes = null,
            string promptSource = "template")
        {
            MCPToolSettings settings = MCPToolSettings.GetOrCreate();
            string docsRoot = string.IsNullOrEmpty(settings.docsRootPath) ? "Assets/Docs" : settings.docsRootPath;

            string id = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string assetPath = $"{docsRoot}/SpriteSheetPrompt_{id}.json".Replace('\\', '/');

            var rowDocs = new List<object>();
            foreach (SpriteSheetRowDef row in rows)
            {
                rowDocs.Add(new Dictionary<string, object>
                {
                    { "action", row.action },
                    { "frameCount", row.frameCount }
                });
            }

            var doc = new Dictionary<string, object>
            {
                { "id", id },
                { "createdAt", DateTime.Now.ToString("yyyy-MM-dd HH:mm") },
                {
                    "answers", new Dictionary<string, object>
                    {
                        { "useReferenceImage", useReferenceImage },
                        { "characterDescription", characterDescription ?? string.Empty },
                        { "gameGenre", gameGenre ?? string.Empty },
                        { "artStyle", artStyle ?? string.Empty },
                        { "extraNotes", extraNotes ?? string.Empty },
                        { "rows", rowDocs },
                        { "cellSize", cellSize },
                        { "direction", faceRight ? "right" : "left" },
                        { "background", whiteBackground ? "white" : "transparent" },
                        { "layout", "multi-row uniform grid" }
                    }
                },
                { "promptSource", promptSource ?? "template" },
                { "prompt", prompt }
            };

            Directory.CreateDirectory(Path.GetFullPath(docsRoot));
            File.WriteAllText(Path.GetFullPath(assetPath), MiniJson.Serialize(doc));
            AssetDatabase.ImportAsset(assetPath);
            return assetPath;
        }

        /// <summary>행 헤더 표기(WALK cycle / BASIC ATTACK / DEATH animation 등)를 반환합니다.</summary>
        private static string RowLabel(string action)
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "walk": return "WALK cycle";
                case "run": return "RUN cycle";
                case "attack": return "BASIC ATTACK";
                case "idle": return "IDLE cycle";
                case "death": return "DEATH animation";
                default: return action.Trim().ToUpperInvariant() + " animation";
            }
        }

        /// <summary>프리셋별 연출 지시 문구를 반환합니다. (직접 입력 동작은 일반 문구)</summary>
        private static string ActionDirective(string action)
        {
            switch (action.Trim().ToLowerInvariant())
            {
                case "walk":
                    return "Smooth looping side-walk animation.";
                case "run":
                    return "Faster and more energetic than walking, smooth looping animation.";
                case "attack":
                    return "One full attack motion with clear anticipation, action, impact, and recovery, returning to the starting pose.";
                case "idle":
                    return "Subtle breathing and relaxed standing motion, smooth looping animation.";
                case "death":
                    return "Clear hit reaction followed by collapse, ending in a defeated pose on the ground.";
                default:
                    return "Clear and readable motion for this action across the frames.";
            }
        }

        /// <summary>루프가 자연스러워야 하는 동작(walk/run/idle)만 골라 "walk and run" 형태로 결합합니다.</summary>
        private static string JoinLoopActions(List<SpriteSheetRowDef> rows)
        {
            var loops = new List<string>();
            foreach (SpriteSheetRowDef row in rows)
            {
                string key = row.action.Trim().ToLowerInvariant();
                if ((key == "walk" || key == "run" || key == "idle") && !loops.Contains(key))
                {
                    loops.Add(key);
                }
            }

            if (loops.Count == 0) return string.Empty;
            if (loops.Count == 1) return loops[0];
            return string.Join(", ", loops.GetRange(0, loops.Count - 1)) + " and " + loops[loops.Count - 1];
        }

        /// <summary>"walk_01 to walk_08, ... and death_01 to death_10" 형태의 프레임 이름 범위 문구를 만듭니다.</summary>
        private static string JoinFrameNameRanges(List<SpriteSheetRowDef> rows)
        {
            var ranges = new List<string>();
            foreach (SpriteSheetRowDef row in rows)
            {
                string key = SanitizeActionName(row.action);
                ranges.Add($"{key}_01 to {key}_{row.frameCount:00}");
            }

            if (ranges.Count == 1) return ranges[0];
            return string.Join(", ", ranges.GetRange(0, ranges.Count - 1)) + ", and " + ranges[ranges.Count - 1];
        }

        /// <summary>동작명을 슬라이스/파일 이름에 쓸 수 있게 소문자·언더스코어로 정리합니다.</summary>
        public static string SanitizeActionName(string action)
        {
            string trimmed = (action ?? string.Empty).Trim().ToLowerInvariant();
            var sb = new StringBuilder(trimmed.Length);
            foreach (char c in trimmed)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            string result = sb.ToString().Trim('_');
            return result.Length == 0 ? "action" : result;
        }
    }
}
