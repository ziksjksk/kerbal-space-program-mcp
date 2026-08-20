Import-Clixml: Unexpected end of file has occurred. The following elements are not closed: En, DCT, Obj, En, DCT, Obj, En, DCT, Obj,
Objs. Line 965, position 23.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace KspMcp
{
    // A small JSON implementation keeps the plugin independent of Json.NET and
    // Unity's JsonUtility, neither of which can represent arbitrary MCP
    // objects and arrays on every KSP 1.x installation.
    internal static class McpJson
    {
        public static object Deserialize(string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException("json");
            }
            return new Parser(json).ParseDocument();
        }

        public static string Serialize(object value)
        {
            var builder = new StringBuilder();
            Serializer.SerializeValue(value, builder);
            return builder.ToString();
        }

        private sealed class Parser
        {
            private readonly string _json;
            private int _index;

            public Parser(string json)
            {
                _json = json;
            }

            public object ParseDocument()
            {
                object value = ParseValue();
                EatWhitespace();
                if (_index != _json.Length)
                {
                    throw new FormatException("Unexpected characters after JSON value");
                }
                return value;
            }

            public object ParseValue()
            {
                EatWhitespace();
                if (_index >= _json.Length)
                {
                    throw new FormatException("Unexpected end of JSON");
                }

                char character = _json[_index];
                if (character == '{') return ParseObject();
                if (character == '[') return ParseArray();
                if (character == '"') return ParseString();
                if (character == 't' || character == 'f') return ParseBoolean();
                if (character == 'n') return ParseNull();
                return ParseNumber();
            }

            private Dictionary<string, object> ParseObject()
            {
                var table = new Dictionary<string, object>(StringComparer.Ordinal);
                Expect('{');
                EatWhitespace();
                if (TryConsume('}')) return table;

                while (true)
                {
                    EatWhitespace();
                    if (_index >= _json.Length || _json[_index] != '"')
                    {
                        throw new FormatException("JSON object key must be a string");
                    }
                    string key = ParseString();
                    EatWhitespace();
                    Expect(':');
                    table[key] = ParseValue();
                    EatWhitespace();
                    if (TryConsume('}')) return table;
                    Expect(',');
                }
            }

            private List<object> ParseArray()
            {
                var list = new List<object>();
                Expect('[');
                EatWhitespace();
                if (TryConsume(']')) return list;

                while (true)
                {
                    list.Add(ParseValue());
                    EatWhitespace();
                    if (TryConsume(']')) return list;
                    Expect(',');
                }
            }

            private string ParseString()
            {
                Expect('"');
                var builder = new StringBuilder();
                while (_index < _json.Length)
                {
                    char character = _json[_index++];
                    if (character == '"') return builder.ToString();
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }

                    if (_index >= _json.Length) throw new FormatException("Unterminated JSON escape");
                    char escaped = _json[_index++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (_index + 4 > _json.Length) throw new FormatException("Invalid unicode escape");
                            string hex = _json.Substring(_index, 4);
                            builder.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            _index += 4;
                            break;
                        default: throw new FormatException("Invalid JSON escape: \\" + escaped);
                    }
                }
                throw new FormatException("Unterminated JSON string");
            }

            private object ParseBoolean()
            {
                if (Match("true")) return true;
                if (Match("false")) return false;
                throw new FormatException("Invalid JSON boolean");
            }

            private object ParseNull()
            {
                if (!Match("null")) throw new FormatException("Invalid JSON null");
                return null;
            }

            private object ParseNumber()
            {
                int start = _index;
                if (_json[_index] == '-') _index++;
                while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                if (_index < _json.Length && _json[_index] == '.')
                {
                    _index++;
                    while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                }
                if (_index < _json.Length && (_json[_index] == 'e' || _json[_index] == 'E'))
                {
                    _index++;
                    if (_index < _json.Length && (_json[_index] == '+' || _json[_index] == '-')) _index++;
                    while (_index < _json.Length && char.IsDigit(_json[_index])) _index++;
                }

                string text = _json.Substring(start, _index - start);
                double number;
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                {
                    throw new FormatException("Invalid JSON number: " + text);
                }
                return number;
            }

            private void EatWhitespace()
            {
                while (_index < _json.Length && char.IsWhiteSpace(_json[_index])) _index++;
            }

            private bool Match(string text)
            {
                if (_index + text.Length > _json.Length || string.CompareOrdinal(_json, _index, text, 0, text.Length) != 0)
                {
                    return false;
                }
                _index += text.Length;
                return true;
            }

            private void Expect(char character)
            {
                EatWhitespace();
                if (_index >= _json.Length || _json[_index] != character)
                {
                    throw new FormatException("Expected JSON character '" + character + "'");
                }
                _index++;
            }

            private bool TryConsume(char character)
            {
                EatWhitespace();
                if (_index < _json.Length && _json[_index] == character)
                {
                    _index++;
                    return true;
                }
                return false;
            }
        }

        private static class Serializer
        {
            public static void SerializeValue(object value, StringBuilder builder)
            {
                if (value == null)
                {
                    builder.Append("null");
                    return;
                }
                if (value is string)
                {
                    SerializeString((string)value, builder);
                    return;
                }
                if (value is bool)
                {
                    builder.Append((bool)value ? "true" : "false");
                    return;
                }
                if (value is IDictionary)
                {
                    SerializeObject((IDictionary)value, builder);
                    return;
                }
                if (value is IList)
                {
                    SerializeArray((IList)value, builder);
                    return;
                }
                if (value is char)
                {
                    SerializeString(value.ToString(), builder);
                    return;
                }

                IFormattable formattable = value as IFormattable;
                builder.Append(formattable == null
                    ? value.ToString()
                    : formattable.ToString(null, CultureInfo.InvariantCulture));
            }

            private static void SerializeObject(IDictionary table, StringBuilder builder)
            {
                bool first = true;
                builder.Append('{');
                foreach (DictionaryEntry entry in table)
                {
                    if (!first) builder.Append(',');
                    SerializeString(Convert.ToString(entry.Key, CultureInfo.InvariantCulture), builder);
                    builder.Append(':');
                    SerializeValue(entry.Value, builder);
                    first = false;
                }
                builder.Append('}');
            }

            private static void SerializeArray(IList list, StringBuilder builder)
            {
                builder.Append('[');
                for (int index = 0; index < list.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    SerializeValue(list[index], builder);
                }
                builder.Append(']');
            }

            private static void SerializeString(string value, StringBuilder builder)
            {
                builder.Append('"');
                if (value != null)
                {
                    foreach (char character in value)
                    {
                        switch (character)
                        {
                            case '"': builder.Append("\\\""); break;
                            case '\\': builder.Append("\\\\"); break;
                            case '\b': builder.Append("\\b"); break;
                            case '\f': builder.Append("\\f"); break;
                            case '\n': builder.Append("\\n"); break;
                            case '\r': builder.Append("\\r"); break;
                            case '\t': builder.Append("\\t"); break;
                            default:
                                if (character < 32)
                                {
                                    builder.Append("\\u");
                                    builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                                }
                                else
                                {
                                    builder.Append(character);
                                }
                                break;
                        }
                    }
                }
                builder.Append('"');
            }
        }
    }

    internal static class JsonUtil
    {
        public static Dictionary<string, object> Object(object value)
        {
            return value as Dictionary<string, object>;
        }

        public static object Get(Dictionary<string, object> source, string key)
        {
            object value;
            return source != null && source.TryGetValue(key, out value) ? value : null;
        }

        public static bool Has(Dictionary<string, object> source, string key)
        {
            return source != null && source.ContainsKey(key) && source[key] != null;
        }

        public static string String(Dictionary<string, object> source, string key, string fallback)
        {
            object value = Get(source, key);
            return value is string ? (string)value : fallback;
        }

        public static string RequiredString(Dictionary<string, object> source, string key)
        {
            string value = String(source, key, null);
            if (string.IsNullOrEmpty(value)) throw new ArgumentException("Missing required string: " + key);
            return value;
        }

        public static double Number(Dictionary<string, object> source, string key, double fallback)
        {
            object value = Get(source, key);
            if (value is double) return (double)value;
            if (value is float) return (float)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            return fallback;
        }

        public static int Integer(Dictionary<string, object> source, string key, int fallback)
        {
            return (int)Math.Round(Number(source, key, fallback));
        }

        public static bool Boolean(Dictionary<string, object> source, string key, bool fallback)
        {
            object value = Get(source, key);
            return value is bool ? (bool)value : fallback;
        }

        public static List<object> Array(Dictionary<string, object> source, string key)
        {
            return Get(source, key) as List<object>;
        }

        public static Vector3 Vector3(Dictionary<string, object> source, string key, Vector3 fallback)
        {
            List<object> values = Array(source, key);
            if (values == null || values.Count != 3) return fallback;
            return new UnityEngine.Vector3(ToFloat(values[0]), ToFloat(values[1]), ToFloat(values[2]));
        }

        public static Quaternion Quaternion(Dictionary<string, object> source, string key, Quaternion fallback)
        {
            List<object> values = Array(source, key);
            if (values == null || values.Count != 4) return fallback;
            return new UnityEngine.Quaternion(ToFloat(values[0]), ToFloat(values[1]), ToFloat(values[2]), ToFloat(values[3]));
        }

        public static float ToFloat(object value)
        {
            if (value is double) return (float)(double)value;
            if (value is float) return (float)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            return 0f;
        }

        public static Dictionary<string, object> Vector3Object(UnityEngine.Vector3 value)
        {
            return new Dictionary<string, object>
            {
                { "x", value.x }, { "y", value.y }, { "z", value.z }
            };
        }

        public static Dictionary<string, object> Vector3dObject(Vector3d value)
        {
            return new Dictionary<string, object>
            {
                { "x", value.x }, { "y", value.y }, { "z", value.z }
            };
        }

        public static Dictionary<string, object> QuaternionObject(UnityEngine.Quaternion value)
        {
            return new Dictionary<string, object>
            {
                { "x", value.x }, { "y", value.y }, { "z", value.z }, { "w", value.w }
            };
        }
    }
}

