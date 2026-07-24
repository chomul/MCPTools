using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace MCPTools.Editor
{
    /// <summary>스프라이트 시트 격자 슬라이스·임포트 결과입니다.</summary>
    public sealed class SpriteSheetImportResult
    {
        /// <summary>정규화된 시트의 Assets/ 기준 상대 경로.</summary>
        public string assetPath;

        /// <summary>검출된 행 수.</summary>
        public int rowCount;

        /// <summary>검출된 전체 프레임 수.</summary>
        public int totalFrameCount;

        /// <summary>행별 검출 프레임 수 (위→아래 순).</summary>
        public int[] framesPerRow;

        /// <summary>실제 슬라이스에 사용된 행별 동작명 (위→아래 순).</summary>
        public string[] rowActions;

        /// <summary>호출 시 기대한 행 수. (정보성)</summary>
        public int expectedRowCount;

        /// <summary>호출 시 기대한 행별 프레임 수 (위→아래 순, 정보성).</summary>
        public int[] expectedFramesPerRow;

        /// <summary>검출된 격자 구성이 행 정의와 다르면 true. (정보성 — 격자 검출 결과가 항상 우선)</summary>
        public bool usedDetectedLayout;

        /// <summary>공통 셀 너비(px).</summary>
        public int cellWidth;

        /// <summary>공통 셀 높이(px).</summary>
        public int cellHeight;
    }

    /// <summary>
    /// 외부 AI가 생성한 멀티 행(동작별 Row) 스프라이트 시트 이미지를 격자선 기준으로 슬라이스하는 유틸리티입니다.
    /// 흰색 배경이면 외곽 시드 BFS(그라데이션 허용 오차 + 근사 흰색 + 무채색 조건)로 배경을 투명화한 뒤,
    /// 시트에 그려진 격자선(무채색·비순백·비암부 픽셀이 셀을 가로지르는 열/행)을 직접 검출해 균일 셀 경계를 만들고,
    /// 재조립 없이 원본 셀 위치 그대로 Sprite Multiple 슬라이스를 적용합니다(프레임 간 정렬 보존).
    /// </summary>
    public static class SpriteSheetImporter
    {
        /// <summary>
        /// Sprite Mode=Multiple 슬라이스 목록을 텍스처 임포터에 기록하는 훅입니다.
        /// 실제 구현은 2D Sprite 패키지(<c>com.unity.2d.sprite</c>)에 의존하므로 별도 어셈블리
        /// <c>MCPTools.Editor.SpriteSlicing</c>에 있으며, 패키지가 설치된 프로젝트에서만
        /// <c>[InitializeOnLoad]</c>로 이 훅에 등록됩니다.
        /// 패키지가 없으면 null로 남고 <see cref="Import"/>가 안내 메시지와 함께 실패합니다.
        /// </summary>
        public static Action<TextureImporter, List<SpriteMetaData>> SpriteRectWriter;

        /// <summary>프레임 검출에 사용하는 알파 임계값(0~255)입니다.</summary>
        public const byte AlphaThreshold = 10;

        /// <summary>
        /// 배경 BFS 확장 시 이웃 픽셀과의 채널별 최대 차 허용값입니다.
        /// (배경 그라데이션·얇은 격자선을 따라 확장하기 위한 값)
        /// </summary>
        public const int BackgroundGradientTolerance = 16;

        /// <summary>근사 흰색으로 간주하는 채널 최소값입니다. (min(R,G,B)가 이 값 이상이면 배경 후보)</summary>
        public const byte NearWhiteThreshold = 235;

        /// <summary>
        /// 배경으로 편입 가능한 최대 채도(max(R,G,B) - min(R,G,B))입니다.
        /// 배경·격자선은 무채색(흰색~회색)이고 글로우 이펙트 등은 유채색이므로,
        /// 이 값을 넘는 픽셀은 어떤 경로로도 배경에 편입하지 않아 이펙트가 보존됩니다.
        /// </summary>
        public const int MaxBackgroundSaturation = 18;

        /// <summary>pocket restore 침식/재팽창 반경(체비쇼프 거리)입니다. 좁은 통로로 샌 제거 영역을 끊어내는 기준.</summary>
        private const int PocketErodeRadius = 2;

        /// <summary>
        /// 에지 페더링 대상 최소 흰색값(min(R,G,B))입니다. 배경에 인접한 전경 픽셀의 min 채널이 이 값 이상이면
        /// 안티에일리어싱 잔여·흰 헤일로로 보고 흰색 정도에 비례해 부분 알파를 줍니다. 미만은 캐릭터 색으로 보존.
        /// </summary>
        private const int EdgeFeatherMinWhite = 200;

        /// <summary>격자선 잔여 제거 밴드 반경(px)입니다. 내부 경계선 ±이 값 이내의 격자선 색 픽셀을 투명화합니다.</summary>
        private const int GridLineClearRadius = 2;

        /// <summary>격자선 잔여 제거 대상 최소 흰색값(min(R,G,B))입니다. 이 값 이상 무채색만 격자선/배경으로 보고 제거합니다.</summary>
        private const int GridLineClearMinWhite = 185;

        /// <summary>격자 셀에 콘텐츠(프레임)가 있다고 판정하는 최소 전경 픽셀 비율입니다. (셀 면적 대비)</summary>
        private const float GridCellContentRatio = 0.005f;

        /// <summary>격자 셀 크기가 중앙값의 이 비율 미만이면 오검출/여백 sliver로 보고 이웃 셀과 합칩니다.</summary>
        private const float MinGridCellMedianRatio = 0.4f;

        /// <summary>
        /// 격자선 픽셀 판정: 채도(max-min)가 이 값 이하인 무채색만 격자선 후보입니다.
        /// (배경·격자선은 흰색~회색이고 캐릭터·이펙트는 유채색)
        /// </summary>
        private const int GridLineMaxSaturation = 16;

        /// <summary>격자선 픽셀의 max(R,G,B) 상한입니다. 이 값 이상은 순백 배경으로 보고 선에서 제외합니다.</summary>
        private const byte GridLineMaxChannel = 248;

        /// <summary>격자선 픽셀의 min(R,G,B) 하한입니다. 이 값 이하는 어두운 캐릭터 픽셀로 보고 선에서 제외합니다.</summary>
        private const byte GridLineMinChannel = 150;

        /// <summary>
        /// 한 열/행이 격자선으로 인정받는 최소 스팬 비율입니다. 격자선 후보 픽셀이 교차 방향 길이의
        /// 이 비율 이상을 채우는 열/행만 격자선으로 봅니다. (AI가 그린 격자선은 셀 전체를 가로지름)
        /// </summary>
        private const float GridLineSpanRatio = 0.6f;

        /// <summary>
        /// 멀티 행 시트 이미지를 격자선 기준으로 슬라이스해 <c>Assets/Generated/Images/{name}_sheet.png</c>로 저장하고
        /// Sprite Multiple 슬라이스(행별 동작명 기반 <c>walk_01</c>~ 이름)를 적용합니다.
        /// 격자선이 곧 정답이므로 행/프레임 수가 행 정의와 달라도 검출된 격자 그대로 임포트합니다
        /// (행 이름은 순서대로 행 정의의 동작명, 초과 행은 <c>row5</c> 같은 자동 이름).
        /// </summary>
        /// <param name="imagePath">시트 png 파일 경로 (프로젝트 밖 절대 경로 허용).</param>
        /// <param name="rows">행 정의 목록 (위에서 아래 순서, 동작명 + 기대 프레임 수 — 이름·정보 표기용).</param>
        /// <param name="whiteBackground">true면 흰색 계열 배경(그라데이션·격자선 포함)을 외곽 BFS로 제거한 뒤 처리합니다.</param>
        /// <param name="showProgress">에디터 진행률 표시 여부 (MCP 호출 시 false 권장).</param>
        /// <param name="pivotAtFeet">
        /// true면 각 스프라이트의 피벗을 셀 중앙이 아니라 콘텐츠의 발밑(수평 중앙 + 최하단 전경 픽셀)에 둡니다.
        /// walk/run 등 이동 애니메이션에서 발이 한 지점에 고정돼 흔들림이 줄어듭니다. 기본 false(셀 중앙).
        /// </param>
        /// <returns>임포트 결과.</returns>
        /// <exception cref="InvalidOperationException">
        /// 파일 누락, 이미지 로드 실패, 격자 검출 실패 등 슬라이스 실패 시. 메시지에 원인과 조치를 포함합니다.
        /// </exception>
        public static SpriteSheetImportResult Import(
            string imagePath, List<SpriteSheetRowDef> rows, bool whiteBackground, bool showProgress = false,
            bool pivotAtFeet = false)
        {
            if (rows == null || rows.Count == 0)
            {
                throw new InvalidOperationException("행 정의(rows)가 1개 이상 필요합니다. 예: walk:8,run:8,attack:8,death:10");
            }

            foreach (SpriteSheetRowDef row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.action) || row.frameCount < 1)
                {
                    throw new InvalidOperationException("행 정의에 동작명이 비었거나 프레임 수가 1 미만인 항목이 있습니다.");
                }
            }

            string fullPath = ResolveFullPath(imagePath);
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    $"시트 이미지 파일을 찾을 수 없습니다: {imagePath}\n경로가 올바른지 확인해주세요.");
            }

            try
            {
                if (showProgress)
                {
                    EditorUtility.DisplayProgressBar("Sprite Sheet Import", "이미지 로드 중...", 0.05f);
                }

                Texture2D source = LoadTexture(fullPath);
                try
                {
                    Color32[] pixels = source.GetPixels32();
                    int width = source.width;
                    int height = source.height;

                    if (whiteBackground)
                    {
                        if (showProgress)
                        {
                            EditorUtility.DisplayProgressBar("Sprite Sheet Import", "배경 제거 중 (외곽 BFS)...", 0.2f);
                        }

                        RemoveWhiteBackground(pixels, width, height);
                    }

                    if (showProgress)
                    {
                        EditorUtility.DisplayProgressBar("Sprite Sheet Import", "격자 경계 검출 중...", 0.4f);
                    }

                    SpriteSheetImportResult result =
                        ImportGrid(pixels, width, height, rows, fullPath, showProgress, pivotAtFeet);
                    if (result == null)
                    {
                        throw new InvalidOperationException(
                            "격자 셀 경계를 검출하지 못했습니다.\n" +
                            "원인: 시트에 격자선/셀 간격이 없거나, 배경 모드가 이미지와 맞지 않을 수 있습니다.\n" +
                            "조치: 격자선이 그려진 시트로 다시 생성하거나(프롬프트의 격자 지시 확인), 배경 모드를 확인해주세요.");
                    }

                    return result;
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(source);
                }
            }
            finally
            {
                if (showProgress)
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        /// <summary>
        /// 격자 기준 슬라이스: AI가 그린 격자선을 직접 검출해 균일 셀 경계를 만들고,
        /// 배경 제거된 원본 이미지를 재조립 없이 그 격자 위치 그대로 슬라이스합니다.
        /// (격자선 = 무채색·비순백·비암부 픽셀이 교차 방향 <see cref="GridLineSpanRatio"/> 이상을 채우는 열/행)
        /// 각 셀은 배경 제거 후 전경 픽셀 비율로 콘텐츠 유무를 판정해, 콘텐츠가 있는 셀만 슬라이스를 만듭니다.
        /// 셀 위치·크기를 원본 그대로 유지하므로 프레임 간 정렬이 보존됩니다.
        /// 격자 검출 실패(셀 행/열 2개 미만 등) 시 null을 반환합니다.
        /// </summary>
        private static SpriteSheetImportResult ImportGrid(
            Color32[] pixels, int width, int height, List<SpriteSheetRowDef> rows, string fullPath,
            bool showProgress, bool pivotAtFeet)
        {
            // 격자선 직접 검출: 배경 제거는 알파만 바꾸므로(RGB 보존) 격자선 RGB는 그대로 남아 있음
            List<int> xBounds = DetectGridBoundaries(pixels, width, height, true);
            List<int> yBounds = DetectGridBoundaries(pixels, width, height, false);
            if (xBounds == null || yBounds == null)
            {
                return null;
            }

            // 여백 sliver 정리 (격자선-이미지 가장자리 사이의 1~2px 셀 등을 이웃과 병합)
            RefineGridBoundaries(xBounds);
            RefineGridBoundaries(yBounds);

            if (xBounds.Count - 1 < 2 || yBounds.Count - 1 < 2)
            {
                return null;
            }

            int cols = xBounds.Count - 1;
            int gridRows = yBounds.Count - 1;

            // 격자선 잔여 제거: 흰 캐릭터 양옆에 갇혀 pocket restore로 복원된 격자선 조각 등을
            // 경계 밴드(±반경)의 무채색 근사흰색 픽셀만 투명화해 제거 (콘텐츠는 경계를 넘지 않음)
            ClearGridLineBands(pixels, width, height, xBounds, yBounds);

            // 셀별 콘텐츠 판정 (전경 알파 픽셀 수 ≥ 셀 면적의 0.5%). 그리드 행은 위→아래 순 (텍스처 y=0은 하단)
            var hasContent = new bool[gridRows, cols];
            for (int gr = 0; gr < gridRows; gr++)
            {
                int yMin = yBounds[gridRows - 1 - gr];
                int yMaxEx = yBounds[gridRows - gr];
                for (int c = 0; c < cols; c++)
                {
                    int xMin = xBounds[c];
                    int xMaxEx = xBounds[c + 1];
                    int minPixels = Mathf.Max(1, (int)((yMaxEx - yMin) * (xMaxEx - xMin) * GridCellContentRatio));
                    int count = 0;
                    for (int y = yMin; y < yMaxEx && count < minPixels; y++)
                    {
                        int rowIdx = y * width;
                        for (int x = xMin; x < xMaxEx; x++)
                        {
                            if (pixels[rowIdx + x].a > AlphaThreshold && ++count >= minPixels)
                            {
                                break;
                            }
                        }
                    }

                    hasContent[gr, c] = count >= minPixels;
                }
            }

            // 콘텐츠가 하나라도 있는 그리드 행만 유지 (위→아래 순)
            var keptRows = new List<int>();       // 그리드 행 인덱스(위→아래)
            var contentCounts = new List<int>();  // 행별 콘텐츠 셀 수 (= 프레임 수)
            for (int gr = 0; gr < gridRows; gr++)
            {
                int count = 0;
                for (int c = 0; c < cols; c++)
                {
                    if (hasContent[gr, c])
                    {
                        count++;
                    }
                }

                if (count == 0)
                {
                    continue; // 완전 빈 그리드 행(여백 밴드 등)은 제외
                }

                keptRows.Add(gr);
                contentCounts.Add(count);
            }

            if (keptRows.Count == 0)
            {
                return null;
            }

            // 격자선이 곧 정답: 검출된 격자 그대로 임포트. 기대 구성과의 차이는 정보로만 기록
            int[] detectedCounts = contentCounts.ToArray();
            bool differs = keptRows.Count != rows.Count;
            if (!differs)
            {
                for (int r = 0; r < detectedCounts.Length; r++)
                {
                    if (detectedCounts[r] != rows[r].frameCount)
                    {
                        differs = true;
                        break;
                    }
                }
            }

            // 행 이름: 순서대로 기존 동작명, 초과 행은 rowN
            var actions = new string[keptRows.Count];
            for (int r = 0; r < keptRows.Count; r++)
            {
                actions[r] = r < rows.Count ? rows[r].action : $"row{r + 1}";
            }

            if (showProgress)
            {
                EditorUtility.DisplayProgressBar("Sprite Sheet Import", "격자 셀 슬라이스 중...", 0.65f);
            }

            // 재조립 없이 배경 제거된 원본을 그대로 저장하고, 격자 셀 위치에 슬라이스 rect를 적용
            var metas = new List<SpriteMetaData>();
            int total = 0;
            for (int r = 0; r < keptRows.Count; r++)
            {
                int gr = keptRows[r];
                // yBounds는 GetPixels32와 동일한 하단 원점(y=0=하단) 좌표. gr은 위→아래 인덱스이므로
                // 콘텐츠 판정과 동일하게 뒤에서부터 매핑한다.
                int yLow = yBounds[gridRows - 1 - gr];  // 셀 하단(포함, 텍스처 y)
                int yHighEx = yBounds[gridRows - gr];   // 셀 상단(배타)
                int cellH = yHighEx - yLow;
                string action = SpriteSheetPromptBuilder.SanitizeActionName(actions[r]);
                int frameNo = 0;

                for (int c = 0; c < cols; c++)
                {
                    if (!hasContent[gr, c])
                    {
                        continue; // 빈 셀: 슬라이스 없음
                    }

                    int xMin = xBounds[c];
                    int cellW = xBounds[c + 1] - xMin;
                    frameNo++;
                    total++;

                    // 피벗: 기본 셀 중앙. pivotAtFeet면 콘텐츠 수평 중앙 + 최하단(발밑)으로 이동해
                    // 이동 애니메이션에서 발이 한 지점에 고정되게 한다.
                    var pivot = new Vector2(0.5f, 0.5f);
                    int alignment = (int)SpriteAlignment.Center;
                    if (pivotAtFeet &&
                        TryComputeFeetPivot(pixels, width, xMin, cellW, yLow, cellH, out Vector2 feet))
                    {
                        pivot = feet;
                        alignment = (int)SpriteAlignment.Custom;
                    }

                    metas.Add(new SpriteMetaData
                    {
                        name = $"{action}_{frameNo:00}",
                        // rect은 하단-좌 원점(픽셀). yLow가 곧 rect.y.
                        rect = new Rect(xMin, yLow, cellW, cellH),
                        alignment = alignment,
                        pivot = pivot
                    });
                }
            }

            if (showProgress)
            {
                EditorUtility.DisplayProgressBar("Sprite Sheet Import", "저장 및 임포트 중...", 0.85f);
            }

            string assetPath = SaveSheet(pixels, width, height, Path.GetFileNameWithoutExtension(fullPath));
            ApplySpriteSlices(assetPath, metas);

            var expected = new int[rows.Count];
            for (int r = 0; r < rows.Count; r++)
            {
                expected[r] = rows[r].frameCount;
            }

            return new SpriteSheetImportResult
            {
                assetPath = assetPath,
                rowCount = keptRows.Count,
                totalFrameCount = total,
                framesPerRow = detectedCounts,
                rowActions = actions,
                expectedRowCount = rows.Count,
                expectedFramesPerRow = expected,
                usedDetectedLayout = differs,
                cellWidth = cols > 0 ? xBounds[1] - xBounds[0] : 0,
                cellHeight = gridRows > 0 ? yBounds[1] - yBounds[0] : 0
            };
        }

        /// <summary>
        /// AI가 그린 격자선을 직접 검출해 셀 경계 좌표(오름차순, 시작 0·끝 length 포함)를 만듭니다.
        /// 격자선 후보 픽셀(무채색·비순백·비암부)이 교차 방향 길이의 <see cref="GridLineSpanRatio"/> 이상을 채우는
        /// 열/행의 연속 run 중앙을 경계로 삼습니다. 격자선을 하나도 못 찾으면 null을 반환합니다.
        /// </summary>
        /// <param name="vertical">true면 세로 격자선(열 경계), false면 가로 격자선(행 경계)을 검출합니다.</param>
        private static List<int> DetectGridBoundaries(Color32[] pixels, int width, int height, bool vertical)
        {
            int lineLen = vertical ? width : height;   // 경계를 찍는 축
            int crossLen = vertical ? height : width;   // 선이 가로지르는 축
            var lineCount = new int[lineLen];
            for (int y = 0; y < height; y++)
            {
                int rowIdx = y * width;
                for (int x = 0; x < width; x++)
                {
                    Color32 c = pixels[rowIdx + x];
                    int mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                    int mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    if (mx - mn <= GridLineMaxSaturation && mx < GridLineMaxChannel && mn > GridLineMinChannel)
                    {
                        lineCount[vertical ? x : y]++;
                    }
                }
            }

            int minSpan = Mathf.Max(1, (int)(crossLen * GridLineSpanRatio));
            var boundaries = new List<int>();
            int runStart = -1;
            for (int i = 0; i <= lineLen; i++)
            {
                bool isLine = i < lineLen && lineCount[i] >= minSpan;
                if (isLine && runStart < 0)
                {
                    runStart = i;
                }
                else if (!isLine && runStart >= 0)
                {
                    boundaries.Add((runStart + i - 1 + 1) / 2); // run 중앙
                    runStart = -1;
                }
            }

            if (boundaries.Count == 0)
            {
                return null;
            }

            if (boundaries[0] != 0)
            {
                boundaries.Insert(0, 0);
            }

            if (boundaries[boundaries.Count - 1] != lineLen)
            {
                boundaries.Add(lineLen);
            }

            return boundaries;
        }

        /// <summary>
        /// 격자 경계를 정리합니다: 셀 크기가 현재 셀 크기 중앙값의 <see cref="MinGridCellMedianRatio"/> 미만이면
        /// 오검출 경계로 보고, 더 작은 이웃 셀 쪽 내부 경계를 제거해 이웃 셀과 합칩니다.
        /// (장식·이펙트 사이 얇은 틈을 행/열 경계로 오인해 행이 쪼개지는 문제 방지)
        /// </summary>
        private static void RefineGridBoundaries(List<int> boundaries)
        {
            while (boundaries.Count - 1 > 1)
            {
                int cellCount = boundaries.Count - 1;
                var sizes = new int[cellCount];
                for (int i = 0; i < cellCount; i++)
                {
                    sizes[i] = boundaries[i + 1] - boundaries[i];
                }

                var sorted = (int[])sizes.Clone();
                Array.Sort(sorted);
                float minSize = sorted[cellCount / 2] * MinGridCellMedianRatio;

                int smallest = -1;
                for (int i = 0; i < cellCount; i++)
                {
                    if (sizes[i] < minSize && (smallest < 0 || sizes[i] < sizes[smallest]))
                    {
                        smallest = i;
                    }
                }

                if (smallest < 0)
                {
                    break; // 모든 셀이 중앙값의 40% 이상 → 정리 완료
                }

                // 더 작은 이웃 셀 쪽 내부 경계를 제거해 병합 (가장자리 셀은 유일한 내부 경계 제거)
                bool mergeLeft = smallest > 0 &&
                                 (smallest == cellCount - 1 || sizes[smallest - 1] <= sizes[smallest + 1]);
                boundaries.RemoveAt(mergeLeft ? smallest : smallest + 1);
            }
        }

        /// <summary>
        /// 내부 격자 경계선 위(±<see cref="GridLineClearRadius"/>)의 무채색 근사 흰색 픽셀을 투명화합니다.
        /// AI가 그린 격자선은 셀 경계에만 있고 캐릭터/이펙트 콘텐츠는 경계를 넘지 않으므로,
        /// 이 밴드의 배경·격자선 색(무채색·근사 흰색) 픽셀만 안전하게 제거해 pocket restore 등으로
        /// 남은 격자선 잔여 줄을 없앱니다. (유채색 이펙트·캐릭터 채색 픽셀은 조건에 걸리지 않아 보존)
        /// </summary>
        private static void ClearGridLineBands(
            Color32[] pixels, int width, int height, List<int> xBounds, List<int> yBounds)
        {
            const int r = GridLineClearRadius;

            bool IsGridColor(int idx)
            {
                Color32 c = pixels[idx];
                int mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                int mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                return mx - mn <= MaxBackgroundSaturation && mn >= GridLineClearMinWhite;
            }

            // 세로 격자선 (내부 경계 x)
            for (int b = 1; b < xBounds.Count - 1; b++)
            {
                int cx = xBounds[b];
                int xMin = Mathf.Max(0, cx - r);
                int xMax = Mathf.Min(width - 1, cx + r);
                for (int y = 0; y < height; y++)
                {
                    int row = y * width;
                    for (int x = xMin; x <= xMax; x++)
                    {
                        int idx = row + x;
                        if (pixels[idx].a > 0 && IsGridColor(idx))
                        {
                            pixels[idx].a = 0;
                        }
                    }
                }
            }

            // 가로 격자선 (내부 경계 y)
            for (int b = 1; b < yBounds.Count - 1; b++)
            {
                int cy = yBounds[b];
                int yMin = Mathf.Max(0, cy - r);
                int yMax = Mathf.Min(height - 1, cy + r);
                for (int y = yMin; y <= yMax; y++)
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = row + x;
                        if (pixels[idx].a > 0 && IsGridColor(idx))
                        {
                            pixels[idx].a = 0;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 셀 안 콘텐츠(알파 &gt; <see cref="AlphaThreshold"/>)의 바운딩 박스를 구해 발밑 피벗(수평 중앙 + 최하단)을
        /// 셀 rect 기준 정규화 좌표(0~1)로 반환합니다. 콘텐츠가 없으면 false.
        /// </summary>
        private static bool TryComputeFeetPivot(
            Color32[] pixels, int width, int xMin, int cellW, int yLow, int cellH, out Vector2 pivot)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue;
            for (int y = yLow; y < yLow + cellH; y++)
            {
                int row = y * width;
                for (int x = xMin; x < xMin + cellW; x++)
                {
                    if (pixels[row + x].a > AlphaThreshold)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                    }
                }
            }

            if (maxX < minX)
            {
                pivot = new Vector2(0.5f, 0.5f);
                return false;
            }

            float centerX = (minX + maxX) * 0.5f + 0.5f; // 콘텐츠 수평 중앙(픽셀)
            float feetY = minY;                          // 최하단 전경 픽셀(텍스처 y, 하단 원점)
            pivot = new Vector2(
                Mathf.Clamp01((centerX - xMin) / cellW),
                Mathf.Clamp01((feetY - yLow) / cellH));
            return true;
        }

        private static string ResolveFullPath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                throw new InvalidOperationException("시트 이미지 경로(imagePath)가 비어 있습니다.");
            }

            string path = imagePath.Trim().Replace('\\', '/');
            if (Path.IsPathRooted(path))
            {
                return path;
            }

            // Assets/ 상대 경로는 프로젝트 루트 기준으로 해석
            return Path.GetFullPath(path);
        }

        private static Texture2D LoadTexture(string fullPath)
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(bytes))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException(
                    $"이미지를 로드할 수 없습니다: {fullPath}\npng/jpg 형식인지 확인해주세요 (권장: 알파 채널이 있는 png).");
            }

            return texture;
        }

        /// <summary>
        /// 흰색 계열 배경을 투명화합니다. 이미지 네 외곽 전체를 시드로 한 4방향 BFS로,
        /// 다음 조건을 만족하는 이웃 픽셀을 배경에 편입합니다 (유채색 픽셀은 어떤 경로로도 편입 금지 — 글로우 이펙트 보존):
        /// <list type="bullet">
        /// <item>현재 픽셀과의 채널별 최대 차가 <see cref="BackgroundGradientTolerance"/> 이하 (그라데이션·격자선 추종), 또는</item>
        /// <item>min(R,G,B)가 <see cref="NearWhiteThreshold"/> 이상(근사 흰색)이고, 그 픽셀의 3x3 윈도(8방향+자신, 경계 밖 무시)
        /// 전체가 근사 흰색 또는 이미 배경 편입 상태 (좁은 틈으로 캐릭터 내부에 새는 것 차단)</item>
        /// </list>
        /// BFS 후 배경과 인접한 잔여 근사 흰색(무채색) 전경 픽셀을 비전파로 1회 정리하고,
        /// 마지막에 pocket restore로 좁은 틈으로 새어 들어간 제거 영역을 복원합니다.
        /// </summary>
        private static void RemoveWhiteBackground(Color32[] pixels, int width, int height)
        {
            // 근사 흰색/무채색 여부 사전 계산 (픽셀당 1회) + pocket restore용 원본 알파 스냅샷
            var nearWhite = new bool[pixels.Length];
            var neutral = new bool[pixels.Length];
            var originalAlpha = new byte[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                nearWhite[i] = c.r >= NearWhiteThreshold && c.g >= NearWhiteThreshold && c.b >= NearWhiteThreshold;
                int maxCh = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                int minCh = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                neutral[i] = maxCh - minCh <= MaxBackgroundSaturation;
                originalAlpha[i] = c.a;
            }

            var visited = new bool[pixels.Length]; // true = 배경 편입됨
            var queue = new Queue<int>();

            void Seed(int x, int y)
            {
                int idx = y * width + x;
                if (visited[idx])
                {
                    return;
                }

                visited[idx] = true;
                queue.Enqueue(idx);
            }

            // 근사 흰색 픽셀의 3x3 윈도(경계 밖 무시) 전체가 근사 흰색 또는 배경 편입인지 확인
            bool WindowAllWhiteOrBackground(int cx, int cy)
            {
                int xMin = Mathf.Max(0, cx - 1);
                int xMax = Mathf.Min(width - 1, cx + 1);
                int yMin = Mathf.Max(0, cy - 1);
                int yMax = Mathf.Min(height - 1, cy + 1);
                for (int wy = yMin; wy <= yMax; wy++)
                {
                    int row = wy * width;
                    for (int wx = xMin; wx <= xMax; wx++)
                    {
                        int wIdx = row + wx;
                        if (!nearWhite[wIdx] && !visited[wIdx])
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            void TryExpand(Color32 current, int nx, int ny)
            {
                int nIdx = ny * width + nx;
                if (visited[nIdx] || !neutral[nIdx]) // 유채색(글로우 이펙트 등)은 배경 편입 금지
                {
                    return;
                }

                Color32 n = pixels[nIdx];
                int diff = Mathf.Max(
                    Mathf.Abs(current.r - n.r), Mathf.Abs(current.g - n.g), Mathf.Abs(current.b - n.b));
                if (diff > BackgroundGradientTolerance &&
                    !(nearWhite[nIdx] && WindowAllWhiteOrBackground(nx, ny)))
                {
                    return;
                }

                visited[nIdx] = true;
                queue.Enqueue(nIdx);
            }

            // 네 외곽 경계 전체를 시드로 사용 (배경이 순백이 아니어도 시작 가능)
            for (int x = 0; x < width; x++)
            {
                Seed(x, 0);
                Seed(x, height - 1);
            }

            for (int y = 0; y < height; y++)
            {
                Seed(0, y);
                Seed(width - 1, y);
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                Color32 current = pixels[idx];
                pixels[idx].a = 0;

                int x = idx % width;
                int y = idx / width;
                if (x > 0) TryExpand(current, x - 1, y);
                if (x < width - 1) TryExpand(current, x + 1, y);
                if (y > 0) TryExpand(current, x, y - 1);
                if (y < height - 1) TryExpand(current, x, y + 1);
            }

            // 비전파 fringe 정리 1회: 배경과 4방향 인접한 잔여 근사 흰색(무채색) 전경 픽셀을 한 번에 편입
            var fringe = new List<int>();
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x;
                    if (visited[idx] || !nearWhite[idx] || !neutral[idx])
                    {
                        continue;
                    }

                    if ((x > 0 && visited[idx - 1]) || (x < width - 1 && visited[idx + 1]) ||
                        (y > 0 && visited[idx - width]) || (y < height - 1 && visited[idx + width]))
                    {
                        fringe.Add(idx);
                    }
                }
            }

            foreach (int idx in fringe)
            {
                visited[idx] = true;
                pixels[idx].a = 0;
            }

            RestorePockets(pixels, visited, originalAlpha, width, height);
            FeatherEdges(pixels, width, height);
        }

        /// <summary>
        /// 배경 제거로 생긴 하드 컷아웃 가장자리(알파 0/255 이분)를 부드럽게 다듬습니다.
        /// 배경(알파 0)에 인접한 전경 픽셀 중 근사 흰색(안티에일리어싱 잔여·흰 헤일로)만 골라
        /// 흰색 정도에 비례해 부분 알파를 주고, 배경 흰색이 섞인 색을 언프리멀티플라이로 되돌려
        /// 흰 테두리를 제거합니다. (유채색·어두운 실루엣 픽셀은 손대지 않아 캐릭터 색이 보존됨)
        /// </summary>
        private static void FeatherEdges(Color32[] pixels, int width, int height)
        {
            // 현재 알파 스냅샷으로 배경 마스크 고정 (진행 중 수정과 분리)
            var isBackground = new bool[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                isBackground[i] = pixels[i].a == 0;
            }

            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x;
                    if (isBackground[idx])
                    {
                        continue; // 배경은 그대로 투명
                    }

                    // 8방향 중 배경과 인접한 전경 픽셀만 대상 (실루엣 가장자리 한 겹)
                    bool touchesBg =
                        (x > 0 && isBackground[idx - 1]) ||
                        (x < width - 1 && isBackground[idx + 1]) ||
                        (y > 0 && isBackground[idx - width]) ||
                        (y < height - 1 && isBackground[idx + width]) ||
                        (x > 0 && y > 0 && isBackground[idx - width - 1]) ||
                        (x < width - 1 && y > 0 && isBackground[idx - width + 1]) ||
                        (x > 0 && y < height - 1 && isBackground[idx + width - 1]) ||
                        (x < width - 1 && y < height - 1 && isBackground[idx + width + 1]);
                    if (!touchesBg)
                    {
                        continue;
                    }

                    Color32 c = pixels[idx];
                    int m = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
                    if (m < EdgeFeatherMinWhite)
                    {
                        continue; // 충분히 유채색/어두운 실루엣 → 불투명 유지 (색 보존)
                    }

                    // 흰색일수록(=m이 255에 가까울수록) 투명. m=255→0, m=EdgeFeatherMinWhite→255
                    int a = (255 - m) * 255 / (255 - EdgeFeatherMinWhite);
                    a = Mathf.Clamp(a, 0, 255);
                    if (a >= 255)
                    {
                        continue;
                    }

                    // 언프리멀티플라이: 관측색 C = a*Fg + (255-a)*White(255) → Fg = (C - (255-a)) * 255 / a
                    if (a > 0)
                    {
                        pixels[idx].r = UnpremultiplyWhite(c.r, a);
                        pixels[idx].g = UnpremultiplyWhite(c.g, a);
                        pixels[idx].b = UnpremultiplyWhite(c.b, a);
                    }

                    pixels[idx].a = (byte)a;
                }
            }
        }

        /// <summary>흰색 배경이 섞인 관측 채널값에서 전경 채널값을 복원합니다 (a는 0~255 커버리지).</summary>
        private static byte UnpremultiplyWhite(byte channel, int a)
        {
            int fg = (channel - (255 - a)) * 255 / a;
            return (byte)Mathf.Clamp(fg, 0, 255);
        }

        /// <summary>
        /// pocket restore 후처리: 좁은 틈으로 캐릭터 내부에 새어 들어가 만들어진 제거 영역(pocket)을 복원합니다.
        /// 원리 — pocket은 바깥 배경과 좁은 통로로만 연결되므로, 제거 마스크를 체비쇼프 거리
        /// <see cref="PocketErodeRadius"/>(K)로 침식하면 통로가 끊긴다. 침식 마스크 위에서 이미지 외곽으로부터
        /// 도달 가능한 영역(reach)을 구하고 제거 마스크 내부로 정확히 K+1 스텝 재팽창한 뒤,
        /// 제거됐지만 reach가 아닌 픽셀의 알파를 원본 값으로 복원합니다. 큐 기반, 재귀 없음.
        /// </summary>
        private static void RestorePockets(
            Color32[] pixels, bool[] removed, byte[] originalAlpha, int width, int height)
        {
            const int k = PocketErodeRadius;

            // 1) 침식: 제거 픽셀 중 체비쇼프 거리 K 이내((2K+1)x(2K+1) 윈도, 경계 밖 무시)에
            //    전경 픽셀이 하나라도 있으면 침식 마스크에서 제외
            var eroded = new bool[removed.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = y * width + x;
                    if (!removed[idx])
                    {
                        continue;
                    }

                    bool nearForeground = false;
                    int wyMax = Mathf.Min(height - 1, y + k);
                    int wxMax = Mathf.Min(width - 1, x + k);
                    for (int wy = Mathf.Max(0, y - k); wy <= wyMax && !nearForeground; wy++)
                    {
                        int row = wy * width;
                        for (int wx = Mathf.Max(0, x - k); wx <= wxMax; wx++)
                        {
                            if (!removed[row + wx])
                            {
                                nearForeground = true;
                                break;
                            }
                        }
                    }

                    eroded[idx] = !nearForeground;
                }
            }

            // 2) 외곽 도달성: 네 외곽의 침식 픽셀을 시드로 침식 마스크 위 4방향 BFS → reach
            var reach = new bool[removed.Length];
            var queue = new Queue<int>();

            void SeedReach(int x, int y)
            {
                int idx = y * width + x;
                if (eroded[idx] && !reach[idx])
                {
                    reach[idx] = true;
                    queue.Enqueue(idx);
                }
            }

            for (int x = 0; x < width; x++)
            {
                SeedReach(x, 0);
                SeedReach(x, height - 1);
            }

            for (int y = 0; y < height; y++)
            {
                SeedReach(0, y);
                SeedReach(width - 1, y);
            }

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int x = idx % width;
                int y = idx / width;
                if (x > 0 && eroded[idx - 1] && !reach[idx - 1]) { reach[idx - 1] = true; queue.Enqueue(idx - 1); }
                if (x < width - 1 && eroded[idx + 1] && !reach[idx + 1]) { reach[idx + 1] = true; queue.Enqueue(idx + 1); }
                if (y > 0 && eroded[idx - width] && !reach[idx - width]) { reach[idx - width] = true; queue.Enqueue(idx - width); }
                if (y < height - 1 && eroded[idx + width] && !reach[idx + width]) { reach[idx + width] = true; queue.Enqueue(idx + width); }
            }

            // 3) 재팽창: reach를 시작 큐로 제거 마스크 내부로 정확히 K+1 스텝만 레벨 단위 확장
            var level = new List<int>();
            for (int i = 0; i < reach.Length; i++)
            {
                if (reach[i])
                {
                    level.Add(i);
                }
            }

            for (int step = 0; step < k + 1; step++)
            {
                var next = new List<int>();

                void TryDilate(int nIdx)
                {
                    if (removed[nIdx] && !reach[nIdx])
                    {
                        reach[nIdx] = true;
                        next.Add(nIdx);
                    }
                }

                foreach (int idx in level)
                {
                    int x = idx % width;
                    int y = idx / width;
                    if (x > 0) TryDilate(idx - 1);
                    if (x < width - 1) TryDilate(idx + 1);
                    if (y > 0) TryDilate(idx - width);
                    if (y < height - 1) TryDilate(idx + width);
                }

                if (next.Count == 0)
                {
                    break;
                }

                level = next;
            }

            // 4) 복원: 제거됐지만 외곽에서 도달 불가능한 픽셀(pocket)은 원본 알파로 복원 (RGB는 보존되어 있음)
            for (int i = 0; i < removed.Length; i++)
            {
                if (removed[i] && !reach[i])
                {
                    pixels[i].a = originalAlpha[i];
                }
            }
        }

        private static string SaveSheet(Color32[] sheet, int width, int height, string baseName)
        {
            MCPToolSettings settings = MCPToolSettings.GetOrCreate();
            string root = string.IsNullOrEmpty(settings.generatedRootPath) ? "Assets/Generated" : settings.generatedRootPath;
            string folder = $"{root}/Images".Replace('\\', '/');
            Directory.CreateDirectory(Path.GetFullPath(folder));

            string safeName = string.IsNullOrEmpty(baseName) ? "spritesheet" : baseName;
            string assetPath = $"{folder}/{safeName}_sheet.png";

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(sheet);
                texture.Apply();
                File.WriteAllBytes(Path.GetFullPath(assetPath), texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            return assetPath;
        }

        /// <summary>텍스처 임포터에 Sprite Multiple 설정과 슬라이스 목록을 적용합니다.</summary>
        private static void ApplySpriteSlices(string assetPath, List<SpriteMetaData> metas)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException(
                    $"TextureImporter를 가져올 수 없습니다: {assetPath}\nUnity가 파일을 임포트했는지 확인해주세요.");
            }

            MCPToolSettings settings = MCPToolSettings.GetOrCreate();

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Multiple;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.spritePixelsPerUnit = Mathf.Max(1, settings.spritePixelsPerUnit);
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.filterMode = FilterMode.Bilinear;
            // 시트 원해상도 보존 (기본 2048 초과 시트가 다운스케일되어 슬라이스가 어긋나는 것 방지)
            importer.maxTextureSize = 8192;
            // 알파 그라데이션(글로우·소프트 에지) 품질 보존을 위해 고품질 압축 사용
            importer.textureCompression = TextureImporterCompression.CompressedHQ;

            // 스프라이트 메시 설정: FullRect(투명 영역 타이트 메시로 잘리는 문제 방지) + 물리 셰이프 생성 비활성(임포트 경량화)
            var texSettings = new TextureImporterSettings();
            importer.ReadTextureSettings(texSettings);
            texSettings.spriteMeshType = SpriteMeshType.FullRect;
            texSettings.spriteGenerateFallbackPhysicsShape = false;
            importer.SetTextureSettings(texSettings);

            // Unity 6에서는 deprecated TextureImporter.spritesheet 설정이 적용되지 않으므로
            // 모던 Sprite Editor 데이터 프로바이더(ISpriteEditorDataProvider)로 슬라이스를 기록해야 한다.
            // 해당 API는 2D Sprite 패키지 소속이라 선택적 어셈블리(MCPTools.Editor.SpriteSlicing)로 분리했고,
            // 여기서는 등록된 훅으로만 호출한다.
            Action<TextureImporter, List<SpriteMetaData>> writer = SpriteRectWriter;
            if (writer != null)
            {
                writer(importer, metas);
            }

            importer.SaveAndReimport();

            if (writer == null)
            {
                throw new InvalidOperationException(
                    "Sprite Mode=Multiple 슬라이스를 적용하려면 2D Sprite 패키지가 필요합니다.\n" +
                    "Window > Package Manager에서 com.unity.2d.sprite (2D Sprite)를 설치한 뒤 다시 실행해주세요.\n" +
                    $"시트 이미지는 {assetPath}에 저장되었고 Sprite 임포트 설정까지는 적용되었지만, " +
                    "프레임 슬라이스는 적용되지 않았습니다.");
            }
        }
    }
}
