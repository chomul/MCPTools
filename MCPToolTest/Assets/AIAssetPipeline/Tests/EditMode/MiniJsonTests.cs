using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NUnit.Framework;

namespace AIAssetPipeline.Editor.Tests
{
    /// <summary>
    /// <see cref="MiniJson"/>의 직렬화/역직렬화 계약을 고정하는 테스트입니다.
    /// <para>
    /// Task 8 S16(파서 교체)에서 MiniJson을 다른 구현으로 바꾸더라도, 여기 적힌 결과가 그대로 유지돼야 합니다.
    /// 특히 (1) 숫자 타입 매핑(정수→long / 실수→double), (2) 소수점의 로케일 무관 표기,
    /// (3) 비ASCII 문자의 \uXXXX 이스케이프는 저장된 1·2단계 JSON 문서의 호환성과 직결됩니다.
    /// </para>
    /// </summary>
    public class MiniJsonTests
    {
        private CultureInfo _originalCulture;
        private CultureInfo _originalUiCulture;

        [SetUp]
        public void SetUp()
        {
            _originalCulture = Thread.CurrentThread.CurrentCulture;
            _originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        }

        [TearDown]
        public void TearDown()
        {
            // 로케일 테스트가 다른 테스트/에디터 세션에 새지 않도록 반드시 되돌린다.
            Thread.CurrentThread.CurrentCulture = _originalCulture;
            Thread.CurrentThread.CurrentUICulture = _originalUiCulture;
        }

        /// <summary>값 1개를 Serialize → Deserialize로 왕복시켜 복원된 값을 돌려줍니다.</summary>
        private static object RoundTrip(object value)
        {
            var source = new Dictionary<string, object> { { "v", value } };
            var parsed = MiniJson.Deserialize(MiniJson.Serialize(source)) as Dictionary<string, object>;
            Assert.IsNotNull(parsed, "루트가 객체로 파싱되지 않았습니다.");
            return parsed["v"];
        }

        /// <summary>소수점 자리 표기가 CurrentCulture와 무관하게 항상 '.'인지 고정합니다.</summary>
        private static CultureInfo CommaDecimalCulture()
        {
            // 실제 로케일 이름("de-DE" 등)에 의존하면 ICU가 없는 환경에서 깨질 수 있으므로
            // InvariantCulture를 복제해 소수점만 ','로 바꾼 가짜 로케일을 쓴다.
            var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            culture.NumberFormat.NumberDecimalSeparator = ",";
            culture.NumberFormat.NumberGroupSeparator = ".";
            return culture;
        }

        /// <summary>중첩 객체·배열과 모든 값 타입(문자열/정수/실수/bool/null)이 왕복에서 보존되는지 고정합니다.</summary>
        [Test]
        public void Serialize_Deserialize_PreservesNestedStructureAndValueTypes()
        {
            var source = new Dictionary<string, object>
            {
                { "str", "hello" },
                { "num", 42L },
                { "real", 3.5d },
                { "flagTrue", true },
                { "flagFalse", false },
                { "nil", null },
                {
                    "arr", new List<object>
                    {
                        1L,
                        "two",
                        false,
                        null,
                        new Dictionary<string, object> { { "deep", "값" } }
                    }
                },
                {
                    "obj", new Dictionary<string, object>
                    {
                        { "inner", new List<object> { 1L, 2L, 3L } }
                    }
                }
            };

            var parsed = MiniJson.Deserialize(MiniJson.Serialize(source)) as Dictionary<string, object>;

            Assert.IsNotNull(parsed);
            Assert.AreEqual("hello", parsed["str"]);
            Assert.AreEqual(42L, parsed["num"]);
            Assert.AreEqual(3.5d, parsed["real"]);
            Assert.AreEqual(true, parsed["flagTrue"]);
            Assert.AreEqual(false, parsed["flagFalse"]);
            Assert.IsTrue(parsed.ContainsKey("nil"), "null 값이라도 키는 남아야 합니다.");
            Assert.IsNull(parsed["nil"]);

            var arr = parsed["arr"] as List<object>;
            Assert.IsNotNull(arr, "배열은 List<object>로 복원돼야 합니다.");
            Assert.AreEqual(5, arr.Count);
            Assert.AreEqual(1L, arr[0]);
            Assert.AreEqual("two", arr[1]);
            Assert.AreEqual(false, arr[2]);
            Assert.IsNull(arr[3]);

            var deep = arr[4] as Dictionary<string, object>;
            Assert.IsNotNull(deep, "배열 안의 객체는 Dictionary<string, object>로 복원돼야 합니다.");
            Assert.AreEqual("값", deep["deep"]);

            var obj = parsed["obj"] as Dictionary<string, object>;
            Assert.IsNotNull(obj);
            var inner = obj["inner"] as List<object>;
            Assert.IsNotNull(inner);
            CollectionAssert.AreEqual(new List<object> { 1L, 2L, 3L }, inner);
        }

        /// <summary>
        /// 비ASCII(한글)는 \uXXXX로 이스케이프되어 순수 ASCII JSON이 되고, 그대로 왕복되는지 고정합니다.
        /// (저장된 문서 파일의 인코딩과 무관하게 읽히게 하는 현재 동작)
        /// </summary>
        [Test]
        public void Serialize_EscapesNonAsciiAsUnicodeEscape_AndRoundTrips()
        {
            const string korean = "한글 테스트 · 混";
            string json = MiniJson.Serialize(new Dictionary<string, object> { { "v", korean } });

            StringAssert.Contains("\\u", json);
            foreach (char c in json)
            {
                Assert.Less((int)c, 128, $"직렬화 결과에 비ASCII 문자가 남았습니다: '{c}'");
            }

            Assert.AreEqual(korean, RoundTrip(korean));
        }

        /// <summary>따옴표·역슬래시·개행·탭 등 제어 문자의 이스케이프와 왕복을 고정합니다.</summary>
        [Test]
        public void Serialize_EscapesQuoteBackslashAndControlChars_AndRoundTrips()
        {
            const string raw = "a\"b\\c\nd\te\rf\bg\fh/";
            string json = MiniJson.Serialize(new Dictionary<string, object> { { "v", raw } });

            StringAssert.Contains("\\\"", json); // "  → \"
            StringAssert.Contains("\\\\", json); // \  → \\
            StringAssert.Contains("\\n", json);
            StringAssert.Contains("\\t", json);
            StringAssert.Contains("\\r", json);
            StringAssert.Contains("\\b", json);
            StringAssert.Contains("\\f", json);
            StringAssert.Contains("/", json);    // '/'는 이스케이프하지 않는다

            Assert.AreEqual(raw, RoundTrip(raw));
        }

        /// <summary>
        /// 소수점이 CurrentCulture와 무관하게 항상 '.'로 직렬화/파싱되는지 고정합니다.
        /// (현재 구현이 CultureInfo.InvariantCulture를 쓰는 것을 못박는 것이 목적 — 파서를 교체해도 유지돼야 함)
        /// </summary>
        [Test]
        public void Serialize_And_Deserialize_UseInvariantDecimalPoint_UnderCommaDecimalCulture()
        {
            Thread.CurrentThread.CurrentCulture = CommaDecimalCulture();

            Assert.AreEqual("{\"v\":1234.5}", MiniJson.Serialize(new Dictionary<string, object> { { "v", 1234.5d } }));
            Assert.AreEqual("{\"v\":0.25}", MiniJson.Serialize(new Dictionary<string, object> { { "v", 0.25f } }));
            Assert.AreEqual("{\"v\":-0.125}", MiniJson.Serialize(new Dictionary<string, object> { { "v", -0.125d } }));

            var parsed = MiniJson.Deserialize("{\"v\":1234.5}") as Dictionary<string, object>;
            Assert.IsNotNull(parsed);
            Assert.AreEqual(1234.5d, parsed["v"]);
        }

        /// <summary>
        /// 정수는 long으로, 소수점/지수가 있는 값은 double로 복원되는 타입 매핑을 고정합니다.
        /// 2^53를 넘는 정수도 정밀도 손실 없이 왕복돼야 합니다(double 기반 파서로 바꾸면 깨지는 지점).
        /// </summary>
        [Test]
        public void Deserialize_MapsIntegersToLong_AndFractionalToDouble()
        {
            Assert.IsInstanceOf<long>(RoundTrip(7));            // int도 long으로 돌아온다
            Assert.AreEqual(7L, RoundTrip(7));

            Assert.IsInstanceOf<long>(RoundTrip(-9L));
            Assert.AreEqual(-9L, RoundTrip(-9L));

            const long beyondDouble = 9007199254740993L;        // 2^53 + 1
            Assert.IsInstanceOf<long>(RoundTrip(beyondDouble));
            Assert.AreEqual(beyondDouble, RoundTrip(beyondDouble));

            Assert.IsInstanceOf<double>(RoundTrip(3.5d));
            Assert.AreEqual(3.5d, RoundTrip(3.5d));

            Assert.IsInstanceOf<double>(RoundTrip(1e-7d));
            Assert.AreEqual(1e-7d, (double)RoundTrip(1e-7d), 1e-20d);
        }

        /// <summary>
        /// 소수부가 없는 double(2.0)은 "2"로 직렬화되어 long으로 되돌아오는 현재 동작을 고정합니다.
        /// (직관과 다르지만 저장된 문서가 이 형태이므로, 파서를 교체해도 같은 결과여야 합니다)
        /// </summary>
        [Test]
        public void Serialize_WholeDouble_WritesWithoutFraction_AndComesBackAsLong()
        {
            Assert.AreEqual("{\"v\":2}", MiniJson.Serialize(new Dictionary<string, object> { { "v", 2.0d } }));

            object parsed = RoundTrip(2.0d);
            Assert.IsInstanceOf<long>(parsed);
            Assert.AreEqual(2L, parsed);
        }

        /// <summary>입력이 null이면 파서가 예외 없이 null을 돌려주는지 고정합니다.</summary>
        [Test]
        public void Deserialize_Null_ReturnsNull()
        {
            Assert.IsNull(MiniJson.Deserialize(null));
        }

        /// <summary>bool·null 값이 JSON 리터럴로 직렬화되는지 고정합니다.</summary>
        [Test]
        public void Serialize_BoolAndNull_UseJsonLiterals()
        {
            Assert.AreEqual("{\"v\":true}", MiniJson.Serialize(new Dictionary<string, object> { { "v", true } }));
            Assert.AreEqual("{\"v\":false}", MiniJson.Serialize(new Dictionary<string, object> { { "v", false } }));
            Assert.AreEqual("{\"v\":null}", MiniJson.Serialize(new Dictionary<string, object> { { "v", null } }));
        }
    }
}
