// 외부 패키지 의존이 없는 최소 JSON 파서/직렬화기입니다.
// public domain MiniJSON(Calvin Rien) 계열 구현을 본 프로젝트 규약에 맞게 정리했습니다.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace MCPTools.Editor
{
    /// <summary>
    /// 외부 의존 없는 최소 JSON 파서/직렬화기입니다.
    /// Deserialize 결과 타입: Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double, long, bool, null.
    /// </summary>
    public static class MiniJson
    {
        /// <summary>
        /// JSON 문자열을 파싱합니다.
        /// </summary>
        /// <param name="json">파싱할 JSON 문자열.</param>
        /// <returns>
        /// Dictionary&lt;string, object&gt;, List&lt;object&gt;, string, double(실수), long(정수),
        /// bool, null 중 하나. 입력이 null이면 null을 반환합니다.
        /// </returns>
        public static object Deserialize(string json)
        {
            if (json == null)
            {
                return null;
            }

            return Parser.Parse(json);
        }

        /// <summary>
        /// 객체를 JSON 문자열로 직렬화합니다.
        /// IDictionary, IList, string, bool, 숫자 타입, char, enum, null을 지원합니다.
        /// </summary>
        /// <param name="obj">직렬화할 객체.</param>
        /// <returns>JSON 문자열.</returns>
        public static string Serialize(object obj)
        {
            return Serializer.MakeJson(obj);
        }

        private sealed class Parser : IDisposable
        {
            private const string WordBreak = "{}[],:\"";

            private StringReader _json;

            private Parser(string json)
            {
                _json = new StringReader(json);
            }

            public static object Parse(string json)
            {
                using (var parser = new Parser(json))
                {
                    return parser.ParseValue();
                }
            }

            public void Dispose()
            {
                _json.Dispose();
                _json = null;
            }

            private enum Token
            {
                None,
                CurlyOpen,
                CurlyClose,
                SquaredOpen,
                SquaredClose,
                Colon,
                Comma,
                String,
                Number,
                True,
                False,
                Null
            }

            private Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>();

                // '{' 소비
                _json.Read();

                while (true)
                {
                    switch (NextToken)
                    {
                        case Token.None:
                            return null;
                        case Token.Comma:
                            continue;
                        case Token.CurlyClose:
                            return table;
                        default:
                            // 키
                            string name = ParseString();
                            if (name == null)
                            {
                                return null;
                            }

                            // ':'
                            if (NextToken != Token.Colon)
                            {
                                return null;
                            }

                            _json.Read();

                            // 값
                            table[name] = ParseValue();
                            break;
                    }
                }
            }

            private List<object> ParseArray()
            {
                var array = new List<object>();

                // '[' 소비
                _json.Read();

                bool parsing = true;
                while (parsing)
                {
                    Token nextToken = NextToken;

                    switch (nextToken)
                    {
                        case Token.None:
                            return null;
                        case Token.Comma:
                            continue;
                        case Token.SquaredClose:
                            parsing = false;
                            break;
                        default:
                            object value = ParseByToken(nextToken);
                            array.Add(value);
                            break;
                    }
                }

                return array;
            }

            private object ParseValue()
            {
                Token nextToken = NextToken;
                return ParseByToken(nextToken);
            }

            private object ParseByToken(Token token)
            {
                switch (token)
                {
                    case Token.String:
                        return ParseString();
                    case Token.Number:
                        return ParseNumber();
                    case Token.CurlyOpen:
                        return ParseObject();
                    case Token.SquaredOpen:
                        return ParseArray();
                    case Token.True:
                        return true;
                    case Token.False:
                        return false;
                    case Token.Null:
                        return null;
                    default:
                        return null;
                }
            }

            private string ParseString()
            {
                var s = new StringBuilder();

                // '"' 소비
                _json.Read();

                bool parsing = true;
                while (parsing)
                {
                    if (_json.Peek() == -1)
                    {
                        break;
                    }

                    char c = NextChar;
                    switch (c)
                    {
                        case '"':
                            parsing = false;
                            break;
                        case '\\':
                            if (_json.Peek() == -1)
                            {
                                parsing = false;
                                break;
                            }

                            c = NextChar;
                            switch (c)
                            {
                                case '"':
                                case '\\':
                                case '/':
                                    s.Append(c);
                                    break;
                                case 'b':
                                    s.Append('\b');
                                    break;
                                case 'f':
                                    s.Append('\f');
                                    break;
                                case 'n':
                                    s.Append('\n');
                                    break;
                                case 'r':
                                    s.Append('\r');
                                    break;
                                case 't':
                                    s.Append('\t');
                                    break;
                                case 'u':
                                    var hex = new char[4];
                                    for (int i = 0; i < 4; i++)
                                    {
                                        hex[i] = NextChar;
                                    }

                                    s.Append((char)Convert.ToInt32(new string(hex), 16));
                                    break;
                            }

                            break;
                        default:
                            s.Append(c);
                            break;
                    }
                }

                return s.ToString();
            }

            private object ParseNumber()
            {
                string number = NextWord;

                if (number.IndexOf('.') == -1 &&
                    number.IndexOf('e') == -1 &&
                    number.IndexOf('E') == -1)
                {
                    long parsedInt;
                    if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedInt))
                    {
                        return parsedInt;
                    }
                }

                double parsedDouble;
                double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedDouble);
                return parsedDouble;
            }

            private void EatWhitespace()
            {
                while (_json.Peek() != -1 && char.IsWhiteSpace(PeekChar))
                {
                    _json.Read();
                }
            }

            private char PeekChar
            {
                get { return Convert.ToChar(_json.Peek()); }
            }

            private char NextChar
            {
                get { return Convert.ToChar(_json.Read()); }
            }

            private string NextWord
            {
                get
                {
                    var word = new StringBuilder();

                    while (_json.Peek() != -1 && WordBreak.IndexOf(PeekChar) == -1 && !char.IsWhiteSpace(PeekChar))
                    {
                        word.Append(NextChar);
                    }

                    return word.ToString();
                }
            }

            private Token NextToken
            {
                get
                {
                    EatWhitespace();

                    if (_json.Peek() == -1)
                    {
                        return Token.None;
                    }

                    switch (PeekChar)
                    {
                        case '{':
                            return Token.CurlyOpen;
                        case '}':
                            _json.Read();
                            return Token.CurlyClose;
                        case '[':
                            return Token.SquaredOpen;
                        case ']':
                            _json.Read();
                            return Token.SquaredClose;
                        case ',':
                            _json.Read();
                            return Token.Comma;
                        case '"':
                            return Token.String;
                        case ':':
                            return Token.Colon;
                        case '0':
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                        case '-':
                            return Token.Number;
                    }

                    switch (NextWord)
                    {
                        case "false":
                            return Token.False;
                        case "true":
                            return Token.True;
                        case "null":
                            return Token.Null;
                    }

                    return Token.None;
                }
            }
        }

        private sealed class Serializer
        {
            private readonly StringBuilder _builder;

            private Serializer()
            {
                _builder = new StringBuilder();
            }

            public static string MakeJson(object obj)
            {
                var instance = new Serializer();
                instance.SerializeValue(obj);
                return instance._builder.ToString();
            }

            private void SerializeValue(object value)
            {
                if (value == null)
                {
                    _builder.Append("null");
                }
                else if (value is string asStr)
                {
                    SerializeString(asStr);
                }
                else if (value is bool asBool)
                {
                    _builder.Append(asBool ? "true" : "false");
                }
                else if (value is IDictionary asDict)
                {
                    SerializeObject(asDict);
                }
                else if (value is IList asList)
                {
                    SerializeArray(asList);
                }
                else if (value is char asChar)
                {
                    SerializeString(new string(asChar, 1));
                }
                else
                {
                    SerializeOther(value);
                }
            }

            private void SerializeObject(IDictionary obj)
            {
                bool first = true;

                _builder.Append('{');

                foreach (object key in obj.Keys)
                {
                    if (!first)
                    {
                        _builder.Append(',');
                    }

                    SerializeString(key.ToString());
                    _builder.Append(':');
                    SerializeValue(obj[key]);

                    first = false;
                }

                _builder.Append('}');
            }

            private void SerializeArray(IList array)
            {
                _builder.Append('[');

                bool first = true;
                foreach (object value in array)
                {
                    if (!first)
                    {
                        _builder.Append(',');
                    }

                    SerializeValue(value);
                    first = false;
                }

                _builder.Append(']');
            }

            private void SerializeString(string str)
            {
                _builder.Append('"');

                foreach (char c in str)
                {
                    switch (c)
                    {
                        case '"':
                            _builder.Append("\\\"");
                            break;
                        case '\\':
                            _builder.Append("\\\\");
                            break;
                        case '\b':
                            _builder.Append("\\b");
                            break;
                        case '\f':
                            _builder.Append("\\f");
                            break;
                        case '\n':
                            _builder.Append("\\n");
                            break;
                        case '\r':
                            _builder.Append("\\r");
                            break;
                        case '\t':
                            _builder.Append("\\t");
                            break;
                        default:
                            int codepoint = Convert.ToInt32(c);
                            if (codepoint >= 32 && codepoint <= 126)
                            {
                                _builder.Append(c);
                            }
                            else
                            {
                                _builder.Append("\\u");
                                _builder.Append(codepoint.ToString("x4", CultureInfo.InvariantCulture));
                            }

                            break;
                    }
                }

                _builder.Append('"');
            }

            private void SerializeOther(object value)
            {
                // float/double은 왕복 정밀도를 유지하고, 정수 계열은 그대로 출력합니다.
                if (value is float asFloat)
                {
                    _builder.Append(asFloat.ToString("R", CultureInfo.InvariantCulture));
                }
                else if (value is int || value is uint || value is long || value is sbyte ||
                         value is byte || value is short || value is ushort || value is ulong)
                {
                    _builder.Append(value);
                }
                else if (value is double || value is decimal)
                {
                    _builder.Append(Convert.ToDouble(value).ToString("R", CultureInfo.InvariantCulture));
                }
                else if (value is Enum)
                {
                    SerializeString(value.ToString());
                }
                else
                {
                    SerializeString(value.ToString());
                }
            }
        }
    }
}
