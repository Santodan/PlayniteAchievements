using System;
using System.Collections.Generic;
using System.Text;

namespace PlayniteAchievements.Providers.Hoyoverse
{
    internal static class HoyoverseLzStringUtf16Codec
    {
        public static string DecompressFromUtf16(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return string.Empty;
            }

            return Decompress(input.Length, 16384, index => input[index] - 32) ?? string.Empty;
        }

        public static string CompressToUtf16(string input)
        {
            if (input == null)
            {
                return string.Empty;
            }

            return Compress(input, 15, value => (char)(value + 32)) + " ";
        }

        private static string Compress(string uncompressed, int bitsPerChar, Func<int, char> getCharFromInt)
        {
            if (uncompressed == null)
            {
                return string.Empty;
            }

            var dictionary = new Dictionary<string, int>();
            var dictionaryToCreate = new HashSet<string>();
            var contextW = string.Empty;
            var enlargeIn = 2;
            var dictSize = 3;
            var numBits = 2;
            var data = new StringBuilder();
            var dataVal = 0;
            var dataPosition = 0;

            Action<int> writeBit = value =>
            {
                dataVal = (dataVal << 1) | value;
                if (dataPosition == bitsPerChar - 1)
                {
                    dataPosition = 0;
                    data.Append(getCharFromInt(dataVal));
                    dataVal = 0;
                }
                else
                {
                    dataPosition++;
                }
            };

            Action<int, int> writeBits = (count, value) =>
            {
                for (var i = 0; i < count; i++)
                {
                    writeBit(value & 1);
                    value >>= 1;
                }
            };

            for (var ii = 0; ii < uncompressed.Length; ii++)
            {
                var c = uncompressed[ii].ToString();
                if (!dictionary.ContainsKey(c))
                {
                    dictionary[c] = dictSize++;
                    dictionaryToCreate.Add(c);
                }

                var wc = contextW + c;
                if (dictionary.ContainsKey(wc))
                {
                    contextW = wc;
                    continue;
                }

                if (WriteDictionaryEntry(contextW, dictionary, dictionaryToCreate, numBits, writeBits))
                {
                    enlargeIn--;
                    if (enlargeIn == 0)
                    {
                        enlargeIn = 1 << numBits;
                        numBits++;
                    }
                }

                enlargeIn--;
                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }

                dictionary[wc] = dictSize++;
                contextW = c;
            }

            if (!string.IsNullOrEmpty(contextW))
            {
                if (WriteDictionaryEntry(contextW, dictionary, dictionaryToCreate, numBits, writeBits))
                {
                    enlargeIn--;
                    if (enlargeIn == 0)
                    {
                        enlargeIn = 1 << numBits;
                        numBits++;
                    }
                }

                enlargeIn--;
                if (enlargeIn == 0)
                {
                    numBits++;
                }
            }

            writeBits(numBits, 2);

            while (true)
            {
                dataVal <<= 1;
                if (dataPosition == bitsPerChar - 1)
                {
                    data.Append(getCharFromInt(dataVal));
                    break;
                }

                dataPosition++;
            }

            return data.ToString();
        }

        private static bool WriteDictionaryEntry(
            string value,
            IDictionary<string, int> dictionary,
            ISet<string> dictionaryToCreate,
            int numBits,
            Action<int, int> writeBits)
        {
            if (dictionaryToCreate.Contains(value))
            {
                var code = value[0];
                if (code < 256)
                {
                    writeBits(numBits, 0);
                    writeBits(8, code);
                }
                else
                {
                    writeBits(numBits, 1);
                    writeBits(16, code);
                }

                dictionaryToCreate.Remove(value);
                return true;
            }

            writeBits(numBits, dictionary[value]);
            return false;
        }

        private static string Decompress(int length, int resetValue, Func<int, int> getNextValue)
        {
            var dictionary = new List<string> { "0", "1", "2" };
            var enlargeIn = 4;
            var dictSize = 4;
            var numBits = 3;
            var data = new DataState
            {
                Value = getNextValue(0),
                Position = resetValue,
                Index = 1
            };

            var next = ReadBits(2, data, resetValue, getNextValue);
            string c;
            switch (next)
            {
                case 0:
                    c = ((char)ReadBits(8, data, resetValue, getNextValue)).ToString();
                    break;
                case 1:
                    c = ((char)ReadBits(16, data, resetValue, getNextValue)).ToString();
                    break;
                case 2:
                    return string.Empty;
                default:
                    return null;
            }

            dictionary.Add(c);
            var w = c;
            var result = new StringBuilder(c);

            while (true)
            {
                if (data.Index > length)
                {
                    return string.Empty;
                }

                var cc = ReadBits(numBits, data, resetValue, getNextValue);
                string entry;
                switch (cc)
                {
                    case 0:
                        entry = ((char)ReadBits(8, data, resetValue, getNextValue)).ToString();
                        dictionary.Add(entry);
                        cc = dictSize++;
                        enlargeIn--;
                        break;
                    case 1:
                        entry = ((char)ReadBits(16, data, resetValue, getNextValue)).ToString();
                        dictionary.Add(entry);
                        cc = dictSize++;
                        enlargeIn--;
                        break;
                    case 2:
                        return result.ToString();
                    default:
                        if (cc < dictionary.Count && dictionary[cc] != null)
                        {
                            entry = dictionary[cc];
                        }
                        else if (cc == dictSize)
                        {
                            entry = w + w[0];
                        }
                        else
                        {
                            return null;
                        }

                        break;
                }

                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }

                result.Append(entry);
                dictionary.Add(w + entry[0]);
                dictSize++;
                enlargeIn--;
                w = entry;

                if (enlargeIn == 0)
                {
                    enlargeIn = 1 << numBits;
                    numBits++;
                }
            }
        }

        private static int ReadBits(int count, DataState data, int resetValue, Func<int, int> getNextValue)
        {
            var bits = 0;
            var maxPower = 1 << count;
            var power = 1;

            while (power != maxPower)
            {
                var resb = data.Value & data.Position;
                data.Position >>= 1;
                if (data.Position == 0)
                {
                    data.Position = resetValue;
                    data.Value = getNextValue(data.Index++);
                }

                if (resb > 0)
                {
                    bits |= power;
                }

                power <<= 1;
            }

            return bits;
        }

        private sealed class DataState
        {
            public int Value { get; set; }
            public int Position { get; set; }
            public int Index { get; set; }
        }
    }
}
