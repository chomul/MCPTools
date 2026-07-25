using NUnit.Framework;

namespace MCPTools.Editor.Tests
{
    /// <summary>
    /// <see cref="SpriteSheetClipBuilder.ControllerPathForSheet"/>의 경로 규칙을 고정하는 테스트입니다.
    /// <para>
    /// 이 규칙(<c>{확정본}/Animations/{시트이름}/{시트이름}.controller</c>)은 "시트만 알면 컨트롤러를 찾을 수 있다"는
    /// 자동 연결의 전제입니다. 4단계 적용(<c>animatorControllerPath</c>)과 시트 임포트 창이 같은 규칙에 의존하므로,
    /// 규칙이 바뀌면 이미 만들어 둔 컨트롤러와의 연결이 끊깁니다.
    /// </para>
    /// <para>
    /// 이 테스트는 <see cref="MCPToolSettings.GetOrCreate"/>를 거칩니다(구현이 내부에서 호출).
    /// 설정 에셋을 읽기만 하며 새로 쓰지 않고, 설정 에셋 자체는 도구를 한 번이라도 쓰면 만들어지는
    /// 프로젝트 로컬 파일(배포·커밋 제외)입니다.
    /// </para>
    /// </summary>
    public class SpriteSheetClipBuilderTests
    {
        /// <summary>현재 설정 기준의 애니메이션 출력 루트입니다.</summary>
        private static string AnimationsDir()
        {
            return MCPToolFolders.AnimationsDir(MCPToolSettings.GetOrCreate());
        }

        /// <summary>시트 경로 → 컨트롤러 경로 규칙을 고정합니다.</summary>
        [Test]
        public void ControllerPathForSheet_UsesSheetNameFolderAndFileName()
        {
            string path = SpriteSheetClipBuilder.ControllerPathForSheet(
                "Assets/Generated/3_Confirmed/SpriteSheets/hero_sheet.png");

            Assert.AreEqual($"{AnimationsDir()}/hero_sheet/hero_sheet.controller", path);

            // 설정과 무관하게 유지돼야 하는 구조 부분 (Animations/{시트이름}/{시트이름}.controller)
            StringAssert.EndsWith("/Animations/hero_sheet/hero_sheet.controller", path);
        }

        /// <summary>역슬래시 경로와 앞뒤 공백이 정규화되어 같은 결과를 내는지 고정합니다.</summary>
        [Test]
        public void ControllerPathForSheet_NormalizesBackslashesAndTrimsWhitespace()
        {
            string expected = $"{AnimationsDir()}/hero_sheet/hero_sheet.controller";

            Assert.AreEqual(
                expected,
                SpriteSheetClipBuilder.ControllerPathForSheet(
                    @"Assets\Generated\3_Confirmed\SpriteSheets\hero_sheet.png"));
            Assert.AreEqual(
                expected,
                SpriteSheetClipBuilder.ControllerPathForSheet(
                    "  Assets/Generated/3_Confirmed/SpriteSheets/hero_sheet.png  "));
        }

        /// <summary>
        /// 시트 이름은 "마지막 확장자만" 뗀 파일명이라, 이름 중간의 점은 폴더·파일 이름에 그대로 남는지 고정합니다.
        /// </summary>
        [Test]
        public void ControllerPathForSheet_KeepsDotsInsideSheetName()
        {
            string path = SpriteSheetClipBuilder.ControllerPathForSheet("Assets/Sheets/hero.v2_sheet.png");

            Assert.AreEqual($"{AnimationsDir()}/hero.v2_sheet/hero.v2_sheet.controller", path);
        }

        /// <summary>확장자가 없는 경로도 이름 그대로 쓰는지 고정합니다.</summary>
        [Test]
        public void ControllerPathForSheet_WithoutExtension_UsesNameAsIs()
        {
            string path = SpriteSheetClipBuilder.ControllerPathForSheet("Assets/Sheets/hero_sheet");

            Assert.AreEqual($"{AnimationsDir()}/hero_sheet/hero_sheet.controller", path);
        }

        /// <summary>빈 입력(null/빈 문자열/공백)은 예외 없이 빈 문자열을 돌려주는지 고정합니다.</summary>
        [Test]
        public void ControllerPathForSheet_BlankInput_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, SpriteSheetClipBuilder.ControllerPathForSheet(null));
            Assert.AreEqual(string.Empty, SpriteSheetClipBuilder.ControllerPathForSheet(string.Empty));
            Assert.AreEqual(string.Empty, SpriteSheetClipBuilder.ControllerPathForSheet("   "));
        }
    }
}
