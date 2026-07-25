using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace MCPTools.Editor.Tests
{
    /// <summary>
    /// <see cref="SpriteSheetPromptBuilder"/>의 입력 파싱·이름 정규화 계약을 고정하는 테스트입니다.
    /// <para>
    /// <see cref="SpriteSheetPromptBuilder.SanitizeActionName"/>의 결과는 슬라이스 이름
    /// (<c>{동작}_{번호}</c>)과 클립·컨트롤러 파일 이름에 그대로 쓰이므로,
    /// 값이 바뀌면 이미 만들어 둔 에셋과의 연결이 끊깁니다.
    /// 여기 적힌 값은 "바람직한 동작"이 아니라 <b>현재 구현의 실제 동작</b>을 그대로 옮긴 것입니다.
    /// </para>
    /// </summary>
    public class SpriteSheetPromptBuilderTests
    {
        /// <summary>행당 최대 프레임 수 상한값을 고정합니다. (ParseRows 경계 검사의 기준값)</summary>
        [Test]
        public void MaxFrameCount_Is10()
        {
            Assert.AreEqual(10, SpriteSheetPromptBuilder.MaxFrameCount);
        }

        // ──────────────────── ParseRows ────────────────────

        /// <summary>정상 형식("walk:8,run:8")이 순서대로 파싱되는지 고정합니다.</summary>
        [Test]
        public void ParseRows_NormalFormat_ParsesInOrder()
        {
            List<SpriteSheetRowDef> rows = SpriteSheetPromptBuilder.ParseRows("walk:8,run:8,attack:8,death:10");

            Assert.AreEqual(4, rows.Count);
            Assert.AreEqual("walk", rows[0].action);
            Assert.AreEqual(8, rows[0].frameCount);
            Assert.AreEqual("run", rows[1].action);
            Assert.AreEqual("attack", rows[2].action);
            Assert.AreEqual("death", rows[3].action);
            Assert.AreEqual(10, rows[3].frameCount);
        }

        /// <summary>
        /// 항목·콜론 주변 공백이 허용되고 동작명이 소문자로 정규화되는지 고정합니다.
        /// (동작명은 ToLowerInvariant만 적용되고 SanitizeActionName은 거치지 않습니다)
        /// </summary>
        [Test]
        public void ParseRows_AllowsSurroundingWhitespace_AndLowercasesAction()
        {
            List<SpriteSheetRowDef> rows = SpriteSheetPromptBuilder.ParseRows("  WALK : 8 ,\tRun:10  ");

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("walk", rows[0].action);
            Assert.AreEqual(8, rows[0].frameCount);
            Assert.AreEqual("run", rows[1].action);
            Assert.AreEqual(10, rows[1].frameCount);
        }

        /// <summary>빈 항목(",,")은 예외 없이 건너뛰는지 고정합니다.</summary>
        [Test]
        public void ParseRows_SkipsEmptyTokens()
        {
            List<SpriteSheetRowDef> rows = SpriteSheetPromptBuilder.ParseRows("walk:8,,run:8,");

            Assert.AreEqual(2, rows.Count);
            Assert.AreEqual("walk", rows[0].action);
            Assert.AreEqual("run", rows[1].action);
        }

        /// <summary>프레임 수 경계값 1과 MaxFrameCount는 허용되는지 고정합니다.</summary>
        [Test]
        public void ParseRows_AcceptsFrameCountBoundaries()
        {
            Assert.AreEqual(1, SpriteSheetPromptBuilder.ParseRows("walk:1")[0].frameCount);
            Assert.AreEqual(
                SpriteSheetPromptBuilder.MaxFrameCount,
                SpriteSheetPromptBuilder.ParseRows($"walk:{SpriteSheetPromptBuilder.MaxFrameCount}")[0].frameCount);
        }

        /// <summary>빈 입력은 ArgumentException으로 거부되는지 고정합니다.</summary>
        [Test]
        public void ParseRows_NullOrBlank_Throws()
        {
            Assert.Throws<ArgumentException>(() => SpriteSheetPromptBuilder.ParseRows(null));
            Assert.Throws<ArgumentException>(() => SpriteSheetPromptBuilder.ParseRows(string.Empty));
            Assert.Throws<ArgumentException>(() => SpriteSheetPromptBuilder.ParseRows("   "));
        }

        /// <summary>형식이 잘못된 입력이 ArgumentException으로 거부되는지 고정합니다.</summary>
        [TestCase("walk", TestName = "ParseRows_Invalid_NoColon")]
        [TestCase("walk:8:9", TestName = "ParseRows_Invalid_TooManyColons")]
        [TestCase(":8", TestName = "ParseRows_Invalid_EmptyAction")]
        [TestCase("walk:abc", TestName = "ParseRows_Invalid_NonNumericFrameCount")]
        [TestCase("walk:0", TestName = "ParseRows_Invalid_FrameCountBelowMin")]
        [TestCase("walk:11", TestName = "ParseRows_Invalid_FrameCountAboveMax")]
        [TestCase("walk:-1", TestName = "ParseRows_Invalid_NegativeFrameCount")]
        [TestCase("walk:8,run:0", TestName = "ParseRows_Invalid_SecondRowRejectsWholeInput")]
        public void ParseRows_InvalidFormat_Throws(string rows)
        {
            Assert.Throws<ArgumentException>(() => SpriteSheetPromptBuilder.ParseRows(rows));
        }

        /// <summary>구분자만 있어 유효한 행이 하나도 없으면 ArgumentException인지 고정합니다.</summary>
        [Test]
        public void ParseRows_OnlySeparators_Throws()
        {
            Assert.Throws<ArgumentException>(() => SpriteSheetPromptBuilder.ParseRows(",,,"));
        }

        // ──────────────────── SanitizeActionName ────────────────────

        /// <summary>
        /// 동작명 정규화의 실제 동작을 고정합니다:
        /// 앞뒤 공백 제거 → 소문자화 → 영숫자가 아닌 문자는 모두 '_'로 치환 → 앞뒤 '_' 제거.
        /// 중간 '_'는 개수까지 그대로 남습니다.
        /// </summary>
        [TestCase("walk", "walk")]
        [TestCase("WALK", "walk")]
        [TestCase("  Attack  ", "attack")]
        [TestCase("attack combo", "attack_combo")]
        [TestCase("Attack-Combo!", "attack_combo")]
        [TestCase("_walk_", "walk")]
        [TestCase("__walk__", "walk")]
        [TestCase("walk__01", "walk__01")]
        [TestCase("Idle 01", "idle_01")]
        [TestCase("a.b/c", "a_b_c")]
        [TestCase("2단계", "2단계")]
        public void SanitizeActionName_NormalizesAsCurrentlyImplemented(string input, string expected)
        {
            Assert.AreEqual(expected, SpriteSheetPromptBuilder.SanitizeActionName(input));
        }

        /// <summary>
        /// 한글 동작명은 <c>char.IsLetterOrDigit</c>가 true라서 그대로 남는 현재 동작을 고정합니다.
        /// (바람직한지와 무관하게, 이미 이 이름으로 슬라이스된 시트가 있을 수 있으므로 바꾸면 회귀입니다)
        /// </summary>
        [Test]
        public void SanitizeActionName_KeepsHangulLetters()
        {
            Assert.AreEqual("걷기", SpriteSheetPromptBuilder.SanitizeActionName("걷기"));
            Assert.AreEqual("걷기_2", SpriteSheetPromptBuilder.SanitizeActionName(" 걷기 2 "));
        }

        /// <summary>정규화 후 남는 글자가 없으면 "action"으로 대체되는지 고정합니다.</summary>
        [Test]
        public void SanitizeActionName_EmptyResult_FallsBackToAction()
        {
            Assert.AreEqual("action", SpriteSheetPromptBuilder.SanitizeActionName(null));
            Assert.AreEqual("action", SpriteSheetPromptBuilder.SanitizeActionName(string.Empty));
            Assert.AreEqual("action", SpriteSheetPromptBuilder.SanitizeActionName("   "));
            Assert.AreEqual("action", SpriteSheetPromptBuilder.SanitizeActionName("!!!"));
            Assert.AreEqual("action", SpriteSheetPromptBuilder.SanitizeActionName("___"));
        }
    }
}
