// <copyright file="ChineseLocalePolicy.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;

    public enum ChineseScriptBucket
    {
        Unknown = 0,
        Hans = 1,
        Hant = 2,
    }

    public static class ChineseLocalePolicy
    {
        private static readonly HashSet<char> HansDistinctiveCharacters = new HashSet<char>();
        private static readonly HashSet<char> HantDistinctiveCharacters = new HashSet<char>();

        private static readonly HashSet<string> StrictZhCnAllowedLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "zh-CN",
        };

        private static readonly HashSet<string> HansLanguageTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "zh-CN",
            "zh-SG",
            "zh-Hans",
        };

        private static readonly HashSet<string> HantLanguageTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "zh-TW",
            "zh-HK",
            "zh-MO",
            "zh-Hant",
        };

        private static readonly (char Hans, char Hant)[] DistinctiveChineseCharacterPairs =
        {
            ('个', '個'),
            ('么', '麼'),
            ('乐', '樂'),
            ('习', '習'),
            ('书', '書'),
            ('亲', '親'),
            ('众', '眾'),
            ('优', '優'),
            ('伤', '傷'),
            ('儿', '兒'),
            ('这', '這'),
            ('来', '來'),
            ('为', '為'),
            ('们', '們'),
            ('让', '讓'),
            ('带', '帶'),
            ('开', '開'),
            ('车', '車'),
            ('辆', '輛'),
            ('两', '兩'),
            ('厉', '厲'),
            ('讲', '講'),
            ('较', '較'),
            ('听', '聽'),
            ('说', '說'),
            ('点', '點'),
            ('体', '體'),
            ('与', '與'),
            ('无', '無'),
            ('龙', '龍'),
            ('猫', '貓'),
            ('坏', '壞'),
            ('关', '關'),
            ('级', '級'),
            ('评', '評'),
            ('论', '論'),
            ('丰', '豐'),
            ('围', '圍'),
            ('绕', '繞'),
            ('争', '爭'),
            ('夺', '奪'),
            ('复', '複'),
            ('选', '選'),
            ('战', '戰'),
            ('遗', '遺'),
            ('嘱', '囑'),
            ('发', '發'),
            ('间', '間'),
            ('医', '醫'),
            ('会', '會'),
            ('现', '現'),
            ('导', '導'),
            ('经', '經'),
            ('过', '過'),
            ('国', '國'),
            ('际', '際'),
            ('组', '組'),
            ('织', '織'),
            ('怀', '懷'),
            ('惊', '驚'),
            ('计', '計'),
            ('画', '畫'),
            ('实', '實'),
            ('验', '驗'),
            ('档', '檔'),
            ('录', '錄'),
            ('历', '歷'),
            ('样', '樣'),
            ('欢', '歡'),
            ('觉', '覺'),
            ('观', '觀'),
            ('记', '記'),
            ('议', '議'),
            ('语', '語'),
            ('误', '誤'),
            ('读', '讀'),
            ('轻', '輕'),
            ('还', '還'),
            ('迟', '遲'),
            ('释', '釋'),
            ('难', '難'),
            ('顺', '順'),
            ('须', '須'),
            ('顾', '顧'),
            ('顿', '頓'),
            ('预', '預'),
            ('领', '領'),
            ('题', '題'),
            ('额', '額'),
            ('颜', '顏'),
            ('风', '風'),
            ('飞', '飛'),
            ('宫', '宮'),
            ('归', '歸'),
            ('马', '馬'),
            ('讶', '訝'),
        };

        static ChineseLocalePolicy()
        {
            foreach (var pair in DistinctiveChineseCharacterPairs)
            {
                HansDistinctiveCharacters.Add(pair.Hans);
                HantDistinctiveCharacters.Add(pair.Hant);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Language tag canonicalization intentionally lowercases language and variant subtags while preserving region/script casing semantics.")]
        public static string? CanonicalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return language;
            }

            var trimmed = language.Trim();
            if (trimmed.Contains('_', StringComparison.Ordinal))
            {
                return trimmed;
            }

            var parts = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return trimmed;
            }

            parts[0] = parts[0].ToLowerInvariant();
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 2)
                {
                    parts[i] = parts[i].ToUpperInvariant();
                }
                else if (parts[i].Length == 4)
                {
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..].ToLowerInvariant();
                }
                else
                {
                    parts[i] = parts[i].ToLowerInvariant();
                }
            }

            return string.Join('-', parts);
        }

        public static bool IsChineseRequest(string? language)
        {
            var canonicalLanguage = CanonicalizeLanguage(language);
            return !string.IsNullOrEmpty(canonicalLanguage)
                && (canonicalLanguage.Equals("zh", StringComparison.OrdinalIgnoreCase)
                    || canonicalLanguage.StartsWith("zh-", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsAllowedForStrictZhCn(string? language)
        {
            var canonicalLanguage = CanonicalizeLanguage(language);
            return !string.IsNullOrEmpty(canonicalLanguage) && StrictZhCnAllowedLanguages.Contains(canonicalLanguage);
        }

        public static ChineseScriptBucket GetLanguageScriptBucket(string? language)
        {
            var canonicalLanguage = CanonicalizeLanguage(language);
            if (string.IsNullOrEmpty(canonicalLanguage))
            {
                return ChineseScriptBucket.Unknown;
            }

            if (HansLanguageTags.Contains(canonicalLanguage) || canonicalLanguage.Contains("-Hans", StringComparison.OrdinalIgnoreCase))
            {
                return ChineseScriptBucket.Hans;
            }

            if (HantLanguageTags.Contains(canonicalLanguage) || canonicalLanguage.Contains("-Hant", StringComparison.OrdinalIgnoreCase))
            {
                return ChineseScriptBucket.Hant;
            }

            return ChineseScriptBucket.Unknown;
        }

        public static bool TryGetPreferredPeopleLocalization<T>(IEnumerable<T>? localizedValues, Func<T, string?> languageSelector, Func<T, string?> valueSelector, string? explicitFallback, out string? value, out string? sourceLanguage)
        {
            ArgumentNullException.ThrowIfNull(languageSelector);
            ArgumentNullException.ThrowIfNull(valueSelector);

            value = null;
            sourceLanguage = null;

            if (localizedValues != null)
            {
                foreach (var localizedValue in localizedValues)
                {
                    var candidateLanguage = CanonicalizeLanguage(languageSelector(localizedValue));
                    if (!string.Equals(candidateLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var candidateValue = GetTrimmedNonEmptyValue(valueSelector(localizedValue));
                    if (candidateValue == null)
                    {
                        continue;
                    }

                    value = candidateValue;
                    sourceLanguage = candidateLanguage;
                    return true;
                }
            }

            _ = explicitFallback;
            return false;
        }

        public static bool IsTextAllowedForStrictZhCn(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.HasChinese())
            {
                return false;
            }

            var hasHansEvidence = false;
            var hasHantEvidence = false;

            foreach (var character in text)
            {
                if (HansDistinctiveCharacters.Contains(character))
                {
                    hasHansEvidence = true;
                }

                if (HantDistinctiveCharacters.Contains(character))
                {
                    hasHantEvidence = true;
                }

                if (hasHansEvidence && hasHantEvidence)
                {
                    return false;
                }
            }

            return hasHansEvidence && !hasHantEvidence;
        }

        private static string? GetTrimmedNonEmptyValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
