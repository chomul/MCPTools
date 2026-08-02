using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace AIAssetPipeline.Editor.Tests
{
    /// <summary>
    /// <see cref="SpriteSheetImporter.Detect"/>(배경 제거 + 격자 검출)의 결과를 고정하는 테스트입니다.
    /// <para>
    /// <b>Task 8 C23·C24에서 격자 검출 알고리즘을 재작성하더라도 여기 적힌 결과는 그대로 유지돼야 합니다.</b>
    /// 이 값들이 바뀌면 같은 시트 이미지에서 프레임 수·셀 위치가 달라져 이미 만들어 둔 클립이 어긋납니다.
    /// </para>
    /// <para>
    /// 검증 방식: 실제 AI 결과물 대신, 현재 구현의 상수 조건을 만족하도록 합성한 시트를 코드로 만들어
    /// 시스템 임시 폴더에 png로 쓰고 그 절대 경로로 <c>Detect</c>를 호출합니다.
    /// <c>Detect</c>는 파일을 쓰지 않으므로 프로젝트에 아무것도 남지 않습니다
    /// (<c>ApplySlices</c>는 확정본 폴더에 png를 쓰므로 <b>호출하지 않습니다</b>).
    /// </para>
    /// <para>
    /// 합성 시트가 만족하는 현재 구현의 조건 (SpriteSheetImporter.cs의 private 상수 — 테스트에서 직접 참조할 수 없어 값을 옮겨 적음):
    /// <list type="bullet">
    /// <item>격자선: 채도 ≤ GridLineMaxSaturation(16), max(RGB) &lt; GridLineMaxChannel(248),
    /// min(RGB) &gt; GridLineMinChannel(150) → 242 무채색 사용.
    /// 동시에 NearWhiteThreshold(235) 이상이라 외곽 BFS가 격자선을 넘어 셀 안쪽 배경까지 지운다.</item>
    /// <item>격자선 스팬: 교차 방향 길이의 GridLineSpanRatio(0.6) 이상 → 선을 이미지 전체에 걸쳐 그림.</item>
    /// <item>셀 콘텐츠: MaxBackgroundSaturation(18)을 넘는 유채색이라야 배경 제거에 지워지지 않음 → 채도 190인 빨강 사용.</item>
    /// <item>셀 후보 판정: 전경 비율 ≥ GridCellContentRatio(0.005), "비어 보임" 판정: 비율 &lt; EmptyCellContentRatio(0.02).</item>
    /// <item>셀 크기 정리: 셀 크기가 중앙값의 MinGridCellMedianRatio(0.4) 미만이면 병합 → 모든 셀을 같은 크기로 만들어 병합이 일어나지 않게 함.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class SpriteSheetImporterDetectTests
    {
        // ──────────────────── 합성 시트 구성 ────────────────────
        // 200x200, 50px 셀 4열 x 4행. 내부 격자선(3px)만 그리고 이미지 가장자리에는 선을 두지 않는다
        // (가장자리 선을 그리면 폭 1~2px의 sliver 셀이 생겨 경계 정리 로직이 개입한다).

        private const int SheetSize = 200;
        private const int CellSize = 50;
        private const int GridColumnCount = SheetSize / CellSize; // 4
        private const int GridRowCount = SheetSize / CellSize;    // 4

        /// <summary>셀 좌하단에서 콘텐츠 사각형까지의 여백(px). 격자선 밴드(±2px)에서 충분히 떨어뜨린다.</summary>
        private const int ContentInset = 15;

        /// <summary>정상 프레임의 콘텐츠 한 변(px). 20x20=400px → 셀 면적 2500 대비 비율 0.16.</summary>
        private const int FullContentSize = 20;

        /// <summary>"비어 보임" 프레임의 콘텐츠 한 변(px). 5x5=25px → 비율 0.01 (0.005 이상, 0.02 미만).</summary>
        private const int TinyContentSize = 5;

        private const float FullContentRatio = 0.16f;
        private const float TinyContentRatio = 0.01f;

        /// <summary>순백 배경.</summary>
        private static readonly Color32 BackgroundColor = new Color32(255, 255, 255, 255);

        /// <summary>격자선 색: 무채색이며 근사 흰색(235 이상)이면서 순백(248 미만)은 아닌 밝은 회색.</summary>
        private static readonly Color32 GridLineColor = new Color32(242, 242, 242, 255);

        /// <summary>셀 콘텐츠 색: 배경 제거가 건드리지 않도록 채도(220-30=190)가 충분히 높은 유채색.</summary>
        private static readonly Color32 ContentColor = new Color32(220, 30, 30, 255);

        private string _sheetPath;
        private Texture2D _texture;

        [TearDown]
        public void TearDown()
        {
            if (_texture != null)
            {
                UnityEngine.Object.DestroyImmediate(_texture);
                _texture = null;
            }

            if (!string.IsNullOrEmpty(_sheetPath) && File.Exists(_sheetPath))
            {
                File.Delete(_sheetPath);
            }

            _sheetPath = null;
        }

        // ──────────────────── 합성 시트 생성 ────────────────────

        /// <summary>순백 배경 + 내부 격자선(3px)만 그린 픽셀 버퍼를 만듭니다.</summary>
        private static Color32[] BuildSheetPixels()
        {
            var pixels = new Color32[SheetSize * SheetSize];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = BackgroundColor;
            }

            // 정사각 시트라 열/행 경계 개수가 같아 한 번에 그린다.
            // 선 두께 3px의 중앙이 곧 셀 경계 → 구현이 run 중앙을 경계로 잡으므로 경계가 50/100/150에 온다.
            for (int boundary = 1; boundary < GridColumnCount; boundary++)
            {
                int center = boundary * CellSize;
                for (int offset = -1; offset <= 1; offset++)
                {
                    int line = center + offset;
                    for (int i = 0; i < SheetSize; i++)
                    {
                        pixels[i * SheetSize + line] = GridLineColor; // 세로선 (x = line)
                        pixels[line * SheetSize + i] = GridLineColor; // 가로선 (y = line)
                    }
                }
            }

            return pixels;
        }

        /// <summary>지정한 격자 칸 중앙 부근에 정사각형 콘텐츠를 채웁니다.</summary>
        /// <param name="pixels">대상 버퍼.</param>
        /// <param name="gridRow">격자 행 인덱스 (0부터, <b>위→아래</b>).</param>
        /// <param name="column">격자 열 인덱스 (0부터, 좌→우).</param>
        /// <param name="size">콘텐츠 정사각형 한 변(px).</param>
        private static void FillCell(Color32[] pixels, int gridRow, int column, int size)
        {
            int xMin = column * CellSize + ContentInset;

            // 픽셀 버퍼는 하단 원점(y=0=아래)이므로 위→아래 인덱스를 뒤집어 계산한다.
            int yMin = (GridRowCount - 1 - gridRow) * CellSize + ContentInset;

            for (int y = yMin; y < yMin + size; y++)
            {
                int row = y * SheetSize;
                for (int x = xMin; x < xMin + size; x++)
                {
                    pixels[row + x] = ContentColor;
                }
            }
        }

        /// <summary>픽셀 버퍼를 시스템 임시 폴더의 png로 쓰고 그 절대 경로를 돌려줍니다.</summary>
        private string WriteSheet(Color32[] pixels)
        {
            _texture = new Texture2D(SheetSize, SheetSize, TextureFormat.RGBA32, false);
            _texture.SetPixels32(pixels);
            _texture.Apply();

            _sheetPath = Path
                .Combine(Path.GetTempPath(), "AIAssetPipelineSheetTest_" + Guid.NewGuid().ToString("N") + ".png")
                .Replace('\\', '/');
            File.WriteAllBytes(_sheetPath, _texture.EncodeToPNG());
            return _sheetPath;
        }

        /// <summary>
        /// 표준 합성 시트를 만듭니다 (위→아래 기준).
        /// 행 0: 정상 4칸 / 행 1: 정상 3칸 + "비어 보임" 1칸 / 행 2: 정상 2칸 + 완전 공백 2칸 / 행 3: 전부 공백.
        /// </summary>
        private string WriteStandardSheet()
        {
            Color32[] pixels = BuildSheetPixels();

            for (int c = 0; c < GridColumnCount; c++)
            {
                FillCell(pixels, 0, c, FullContentSize);
            }

            for (int c = 0; c < 3; c++)
            {
                FillCell(pixels, 1, c, FullContentSize);
            }

            FillCell(pixels, 1, 3, TinyContentSize); // 비율 0.01 → "비어 보임"으로 자동 제외

            for (int c = 0; c < 2; c++)
            {
                FillCell(pixels, 2, c, FullContentSize);
            }

            // 행 3은 비워 둔다 → 셀 후보가 하나도 없어 검출 결과에서 행 자체가 빠져야 한다.
            return WriteSheet(pixels);
        }

        private static void AssertRect(float x, float y, float width, float height, Rect actual, string label)
        {
            Assert.AreEqual(x, actual.x, 0.001f, $"{label}.x");
            Assert.AreEqual(y, actual.y, 0.001f, $"{label}.y");
            Assert.AreEqual(width, actual.width, 0.001f, $"{label}.width");
            Assert.AreEqual(height, actual.height, 0.001f, $"{label}.height");
        }

        // ──────────────────── 테스트 ────────────────────

        /// <summary>
        /// 격자 구조(행 수·행별 셀 수·셀 크기·셀 rect)가 합성 시트와 정확히 일치하는지 고정합니다.
        /// 콘텐츠가 하나도 없는 격자 행과 빈 칸은 셀 후보에서 빠져야 합니다.
        /// </summary>
        [Test]
        public void Detect_WhiteBackgroundSheet_DetectsGridRowsCellsAndCellSize()
        {
            string path = WriteStandardSheet();

            SpriteSheetDetection detection = SpriteSheetImporter.Detect(path, true, false);

            Assert.IsNotNull(detection);
            Assert.AreEqual(path, detection.sourcePath, "적용 단계의 저장 파일명 기준이 되는 원본 경로");
            Assert.AreEqual(SheetSize, detection.imageWidth);
            Assert.AreEqual(SheetSize, detection.imageHeight);
            Assert.AreEqual(CellSize, detection.cellWidth);
            Assert.AreEqual(CellSize, detection.cellHeight);

            Assert.AreEqual(3, detection.rows.Count, "콘텐츠가 하나도 없는 맨 아래 격자 행은 결과에서 빠집니다.");
            Assert.AreEqual(0, detection.rows[0].gridRow);
            Assert.AreEqual(1, detection.rows[1].gridRow);
            Assert.AreEqual(2, detection.rows[2].gridRow);

            Assert.AreEqual(4, detection.rows[0].cells.Count, "행 0: 정상 4칸");
            Assert.AreEqual(4, detection.rows[1].cells.Count, "행 1: 정상 3칸 + 비어 보임 1칸");
            Assert.AreEqual(2, detection.rows[2].cells.Count, "행 2: 콘텐츠가 없는 칸은 셀 후보가 아닙니다.");

            Assert.AreEqual(10, detection.TotalFrameCount);
            Assert.AreEqual(0, detection.rows[2].cells[0].column);
            Assert.AreEqual(1, detection.rows[2].cells[1].column);
        }

        /// <summary>
        /// 셀 rect이 격자 위치 그대로(하단-좌 원점)인지 고정합니다.
        /// 프레임 간 정렬 보존이 이 좌표에 달려 있으므로 알고리즘을 바꿔도 같은 값이어야 합니다.
        /// </summary>
        [Test]
        public void Detect_WhiteBackgroundSheet_CellRectsMatchGridPositions()
        {
            string path = WriteStandardSheet();

            SpriteSheetDetection detection = SpriteSheetImporter.Detect(path, true, false);

            // 위→아래 행 0은 텍스처 상단 밴드(y = 150~199).
            AssertRect(0f, 150f, CellSize, CellSize, detection.rows[0].cells[0].rect, "행0 열0");
            AssertRect(150f, 150f, CellSize, CellSize, detection.rows[0].cells[3].rect, "행0 열3");

            AssertRect(0f, 100f, CellSize, CellSize, detection.rows[1].cells[0].rect, "행1 열0");
            AssertRect(150f, 100f, CellSize, CellSize, detection.rows[1].cells[3].rect, "행1 열3");

            AssertRect(0f, 50f, CellSize, CellSize, detection.rows[2].cells[0].rect, "행2 열0");
            AssertRect(50f, 50f, CellSize, CellSize, detection.rows[2].cells[1].rect, "행2 열1");
        }

        /// <summary>
        /// 전경 비율이 낮은 셀이 "비어 보임"으로 판정돼 <c>include</c>가 자동으로 꺼지고,
        /// 나머지 셀은 포함 상태로 남는지 고정합니다. (자동 제외는 검출 단계에서 일어납니다)
        /// </summary>
        [Test]
        public void Detect_LowContentCell_IsMarkedLooksEmptyAndAutoExcluded()
        {
            string path = WriteStandardSheet();

            SpriteSheetDetection detection = SpriteSheetImporter.Detect(path, true, false);

            SpriteSheetDetectedCell tiny = detection.rows[1].cells[3];
            Assert.IsTrue(tiny.looksEmpty, "비율 0.01 < EmptyCellContentRatio(0.02) → 비어 보임");
            Assert.IsFalse(tiny.include, "비어 보이는 셀은 검출 시 자동 제외됩니다.");
            Assert.AreEqual(TinyContentRatio, tiny.contentRatio, 0.0005f);

            foreach (SpriteSheetDetectedRow row in detection.rows)
            {
                foreach (SpriteSheetDetectedCell cell in row.cells)
                {
                    if (cell == tiny)
                    {
                        continue;
                    }

                    Assert.IsFalse(cell.looksEmpty, $"행 {row.gridRow} 열 {cell.column}은 정상 프레임이어야 합니다.");
                    Assert.IsTrue(cell.include, $"행 {row.gridRow} 열 {cell.column}은 포함 상태여야 합니다.");
                    Assert.AreEqual(FullContentRatio, cell.contentRatio, 0.0005f);
                }
            }

            Assert.AreEqual(1, detection.LooksEmptyFrameCount);
            Assert.AreEqual(3, detection.IncludedRowCount, "세 행 모두 포함 프레임이 1개 이상 남습니다.");
            Assert.AreEqual(4, detection.rows[0].IncludedFrameCount);
            Assert.AreEqual(3, detection.rows[1].IncludedFrameCount, "비어 보이는 1칸이 빠집니다.");
            Assert.AreEqual(2, detection.rows[2].IncludedFrameCount);
        }

        /// <summary>
        /// 배경 제거가 알파만 지우고 RGB(격자선 색)는 보존하는지 고정합니다.
        /// 격자 검출이 배경 제거 뒤의 RGB를 그대로 읽기 때문에, 이 성질이 깨지면 격자 검출이 무너집니다.
        /// </summary>
        [Test]
        public void Detect_RemovesWhiteBackgroundAndGridLines_ButKeepsRgbAndContent()
        {
            string path = WriteStandardSheet();

            SpriteSheetDetection detection = SpriteSheetImporter.Detect(path, true, false);

            Color32[] pixels = detection.Pixels;
            Assert.IsNotNull(pixels, "적용 단계에 넘길 픽셀 버퍼가 남아 있어야 합니다.");
            Assert.AreEqual(SheetSize * SheetSize, pixels.Length);

            Color32 background = pixels[5 * SheetSize + 5];
            Assert.AreEqual(0, background.a, "셀 밖 순백 배경은 투명해져야 합니다.");

            Color32 gridLine = pixels[25 * SheetSize + 50]; // 세로 격자선 중앙(x=50), 콘텐츠 없는 밴드
            Assert.AreEqual(0, gridLine.a, "격자선도 배경으로 지워집니다.");
            Assert.AreEqual(GridLineColor.r, gridLine.r, "RGB는 보존돼야 격자 검출이 동작합니다.");
            Assert.AreEqual(GridLineColor.g, gridLine.g);
            Assert.AreEqual(GridLineColor.b, gridLine.b);

            // 행 0 / 열 0 콘텐츠 사각형의 중앙 픽셀
            int contentX = ContentInset + FullContentSize / 2;
            int contentY = (GridRowCount - 1) * CellSize + ContentInset + FullContentSize / 2;
            Color32 content = pixels[contentY * SheetSize + contentX];
            Assert.AreEqual(255, content.a, "유채색 콘텐츠는 불투명하게 보존돼야 합니다.");
            Assert.AreEqual(ContentColor.r, content.r);
            Assert.AreEqual(ContentColor.g, content.g);
            Assert.AreEqual(ContentColor.b, content.b);
        }

        /// <summary>
        /// 배경 제거를 끄면(투명 배경 모드) 불투명한 배경이 콘텐츠로 잡혀 모든 칸이 프레임이 되는,
        /// 즉 배경 모드가 결과를 바꾼다는 현재 동작을 고정합니다.
        /// </summary>
        [Test]
        public void Detect_WithoutBackgroundRemoval_TreatsEveryGridCellAsFrame()
        {
            string path = WriteStandardSheet();

            SpriteSheetDetection detection = SpriteSheetImporter.Detect(path, false, false);

            Assert.AreEqual(GridRowCount, detection.rows.Count);
            Assert.AreEqual(GridColumnCount * GridRowCount, detection.TotalFrameCount);
            Assert.AreEqual(0, detection.LooksEmptyFrameCount);
            Assert.AreEqual(CellSize, detection.cellWidth);
            Assert.AreEqual(CellSize, detection.cellHeight);
        }

        /// <summary>격자선이 없는 이미지는 원인·조치가 담긴 안내 메시지와 함께 실패하는지 고정합니다.</summary>
        [Test]
        public void Detect_SheetWithoutGridLines_ThrowsWithGuidance()
        {
            var pixels = new Color32[SheetSize * SheetSize];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = BackgroundColor;
            }

            FillCell(pixels, 0, 0, FullContentSize);
            string path = WriteSheet(pixels);

            var exception = Assert.Throws<InvalidOperationException>(
                () => SpriteSheetImporter.Detect(path, true, false));
            StringAssert.Contains("격자", exception.Message);
        }

        /// <summary>경로가 비었거나 파일이 없으면 안내 메시지와 함께 실패하는지 고정합니다.</summary>
        [Test]
        public void Detect_MissingOrBlankPath_ThrowsWithGuidance()
        {
            Assert.Throws<InvalidOperationException>(() => SpriteSheetImporter.Detect(string.Empty, true, false));
            Assert.Throws<InvalidOperationException>(() => SpriteSheetImporter.Detect("   ", true, false));

            string missing = Path
                .Combine(Path.GetTempPath(), "AIAssetPipelineMissingSheet_" + Guid.NewGuid().ToString("N") + ".png")
                .Replace('\\', '/');

            var exception = Assert.Throws<InvalidOperationException>(
                () => SpriteSheetImporter.Detect(missing, true, false));
            StringAssert.Contains(missing, exception.Message);
        }
    }
}
