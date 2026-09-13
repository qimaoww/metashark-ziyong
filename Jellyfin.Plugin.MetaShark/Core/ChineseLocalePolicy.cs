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
        public const string TmdbChineseLocaleZhCn = "zh-CN";
        public const string TmdbChineseLocaleZhSg = "zh-SG";
        public const string TmdbChineseLocaleZhTw = "zh-TW";
        public const string TmdbChineseLocaleZhHk = "zh-HK";

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

        private static readonly HashSet<string> ChineseMetadataLanguages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            TmdbChineseLocaleZhCn,
            TmdbChineseLocaleZhSg,
            TmdbChineseLocaleZhTw,
            TmdbChineseLocaleZhHk,
        };

        private static readonly HashSet<string> HantLanguageTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "zh-TW",
            "zh-HK",
            "zh-MO",
            "zh-Hant",
        };

        /// <summary>
        /// Jellyfin 12 的语言下拉使用显示名（如 Chinese / Chinese (Traditional)）而不是 BCP-47 代码，
        /// 库、服务器或条目的 PreferredMetadataLanguage 可能直接存这些值；
        /// 若不先归一化，IsChineseRequest 会判定为非中文，导致 TMDb 请求语言无法识别、
        /// 中文标题来源与繁简判定失效（表现为标题不被正确覆盖）。
        /// </summary>
        private static readonly Dictionary<string, string> LanguageAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Chinese"] = "zh",
            ["Chinese (Simplified)"] = "zh-CN",
            ["Chinese (China)"] = "zh-CN",
            ["Chinese (Traditional)"] = "zh-TW",
            ["Chinese (Taiwan)"] = "zh-TW",
            ["Chinese (Hong Kong)"] = "zh-HK",
            ["Chinese (Singapore)"] = "zh-SG",
            ["Chinese (Macao)"] = "zh-MO",
            ["Chinese (Macau)"] = "zh-MO",

            // 「中英双语」按通用中文处理，具体变体仍由地区与默认中文地区决定。
            // Jellyfin 12 为它使用自定义两字母代码 ze（见 iso6392.txt 的变体条目）。
            ["Chinese (Bilingual)"] = "zh",
            ["ze"] = "zh",

            // ISO 639-2/T 与 639-2/B 的三字母代码。
            ["zho"] = "zh",
            ["chi"] = "zh",
        };

        // 地区关键词比字体关键词更具体，必须优先匹配，
        // 否则 Chinese (Traditional, Hong Kong) 会被 Traditional 提前判成 zh-TW。
        private static readonly (string Keyword, string Language)[] ChineseDisplayNameKeywords =
        {
            ("Hong Kong", TmdbChineseLocaleZhHk),
            ("Macao", TmdbChineseLocaleZhHk),
            ("Macau", TmdbChineseLocaleZhHk),
            ("Singapore", TmdbChineseLocaleZhSg),
            ("Taiwan", TmdbChineseLocaleZhTw),
            ("Traditional", TmdbChineseLocaleZhTw),
            ("Simplified", TmdbChineseLocaleZhCn),
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

            // 先把 Jellyfin 的显示名/三字母代码映射成 BCP-47，再走下面的常规规范化。
            if (LanguageAliases.TryGetValue(trimmed, out var aliasedLanguage))
            {
                trimmed = aliasedLanguage;
            }
            else if (trimmed.StartsWith("Chinese", StringComparison.OrdinalIgnoreCase))
            {
                // 兜底：Jellyfin 的语言名可能新增未登记的变体（例如 Chinese (Classical)），
                // 只要仍以 Chinese 开头就按中文处理，按关键词决定繁体变体，其余按通用中文。
                trimmed = ResolveChineseDisplayNameFallback(trimmed);
            }

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

        public static string NormalizeDefaultChineseMetadataLocale(string? value)
        {
            return CanonicalizeLanguage(value) switch
            {
                TmdbChineseLocaleZhCn => TmdbChineseLocaleZhCn,
                TmdbChineseLocaleZhSg => TmdbChineseLocaleZhSg,
                TmdbChineseLocaleZhTw => TmdbChineseLocaleZhTw,
                TmdbChineseLocaleZhHk => TmdbChineseLocaleZhHk,
                _ => TmdbChineseLocaleZhCn,
            };
        }

        public static string? ResolveTmdbMetadataLanguage(string? language, string? countryCode, string? defaultChineseLocale)
        {
            var canonicalLanguage = CanonicalizeLanguage(language);
            if (string.IsNullOrWhiteSpace(canonicalLanguage) || !IsChineseRequest(canonicalLanguage))
            {
                return canonicalLanguage;
            }

            var normalizedCountry = string.IsNullOrWhiteSpace(countryCode)
                ? null
                : countryCode.Trim().ToUpperInvariant();
            var normalizedDefault = NormalizeDefaultChineseMetadataLocale(defaultChineseLocale);

            return canonicalLanguage switch
            {
                TmdbChineseLocaleZhCn => TmdbChineseLocaleZhCn,
                TmdbChineseLocaleZhSg => TmdbChineseLocaleZhSg,
                TmdbChineseLocaleZhTw => TmdbChineseLocaleZhTw,
                TmdbChineseLocaleZhHk => TmdbChineseLocaleZhHk,
                "zh-MO" => TmdbChineseLocaleZhHk,
                "zh-Hans" => string.Equals(normalizedCountry, "SG", StringComparison.Ordinal)
                    ? TmdbChineseLocaleZhSg
                    : TmdbChineseLocaleZhCn,
                "zh-Hant" => string.Equals(normalizedCountry, "HK", StringComparison.Ordinal)
                    || string.Equals(normalizedCountry, "MO", StringComparison.Ordinal)
                        ? TmdbChineseLocaleZhHk
                        : TmdbChineseLocaleZhTw,
                "zh" => ResolveGenericChineseLocale(normalizedCountry, normalizedDefault),
                _ => normalizedDefault,
            };
        }

        public static string? GetTmdbChineseRegionCode(string? resolvedLanguage)
        {
            return CanonicalizeLanguage(resolvedLanguage) switch
            {
                TmdbChineseLocaleZhCn => "CN",
                TmdbChineseLocaleZhSg => "SG",
                TmdbChineseLocaleZhTw => "TW",
                TmdbChineseLocaleZhHk => "HK",
                _ => null,
            };
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

            var (hasHansEvidence, hasHantEvidence) = CollectChineseScriptEvidence(text);
            return hasHansEvidence && !hasHantEvidence;
        }

        /// <summary>
        /// 判断语言是否为可用于中文元数据的中文变体（简中 / 新加坡 / 台湾 / 香港）。
        /// 与 <see cref="IsAllowedForStrictZhCn"/> 不同，这里接受繁体变体，
        /// 用于跟随用户在 Jellyfin 中明确设置的中文语言（例如 zh-TW、zh-HK）。
        /// </summary>
        /// <param name="language">语言标签或 Jellyfin 语言名.</param>
        public static bool IsChineseMetadataLanguage(string? language)
        {
            var canonicalLanguage = CanonicalizeLanguage(language);
            return !string.IsNullOrEmpty(canonicalLanguage) && ChineseMetadataLanguages.Contains(canonicalLanguage);
        }

        /// <summary>
        /// 按语言变体的脚本要求校验文本：简中变体要求简体、繁中变体要求繁体，
        /// 语言无法判断脚本或文本没有明确繁简证据时放行。
        /// </summary>
        /// <param name="text">待校验文本.</param>
        /// <param name="language">文本所属语言.</param>
        public static bool IsTextAllowedForChineseMetadataLanguage(string? text, string? language)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.HasChinese())
            {
                return false;
            }

            var scriptBucket = GetLanguageScriptBucket(language);
            if (scriptBucket == ChineseScriptBucket.Unknown)
            {
                return true;
            }

            var (hasHansEvidence, hasHantEvidence) = CollectChineseScriptEvidence(text);
            if (hasHansEvidence && hasHantEvidence)
            {
                // 简繁混用视为不可信。
                return false;
            }

            // 注意：与 IsTextAllowedForStrictZhCn 不同，这里允许没有繁简差异证据的纯中文标题
            // （例如「勇者的肋骨」），只拒绝与目标变体冲突的证据。
            return scriptBucket == ChineseScriptBucket.Hans ? !hasHantEvidence : !hasHansEvidence;
        }

        private static (bool HasHans, bool HasHant) CollectChineseScriptEvidence(string text)
        {
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
                    break;
                }
            }

            return (hasHansEvidence, hasHantEvidence);
        }

        private static string? GetTrimmedNonEmptyValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string ResolveChineseDisplayNameFallback(string displayName)
        {
            foreach (var (keyword, language) in ChineseDisplayNameKeywords)
            {
                if (displayName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return language;
                }
            }

            // Chinese / Chinese (Simplified) / Chinese (Bilingual) 等一律按通用中文处理，
            // 具体变体由地区与默认中文地区决定。
            return "zh";
        }

        private static string ResolveGenericChineseLocale(string? countryCode, string defaultLocale)
        {
            return countryCode switch
            {
                "CN" => TmdbChineseLocaleZhCn,
                "SG" => TmdbChineseLocaleZhSg,
                "TW" => TmdbChineseLocaleZhTw,
                "HK" => TmdbChineseLocaleZhHk,
                "MO" => TmdbChineseLocaleZhHk,
                _ => defaultLocale,
            };
        }
    }
}
