// <copyright file="StringExtension.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.Eventing.Reader;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using StringMetric;

    public static class StringExtension
    {
        private static readonly Regex ChineseOnlyRegex = new Regex(@"[\u4e00-\u9fa5：]{1,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex HasChineseRegex = new Regex(@"[\u4e00-\u9fa5]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static long ToLong(this string s)
        {
            long val;
            if (long.TryParse(s, out val))
            {
                return val;
            }

            return 0;
        }

        public static int ToInt(this string s)
        {
            int val;
            if (int.TryParse(s, out val))
            {
                return val;
            }

            return 0;
        }

        public static float ToFloat(this string s)
        {
            float val;
            if (float.TryParse(s, out val))
            {
                return val;
            }

            return 0.0f;
        }

        public static bool IsChinese(this string s)
        {
            ArgumentNullException.ThrowIfNull(s);
            return ChineseOnlyRegex.IsMatch(s.Replace(" ", string.Empty, StringComparison.Ordinal).Trim());
        }

        public static bool HasChinese(this string s)
        {
            return HasChineseRegex.IsMatch(s);
        }

        public static bool IsSameLanguage(this string s1, string s2)
        {
            return s1.IsChinese() == s2.IsChinese();
        }

        public static double Distance(this string s1, string s2)
        {
            var jw = new JaroWinkler();

            return jw.Similarity(s1, s2);
        }

        public static string GetMatchGroup(this string text, Regex reg)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(reg);
            var match = reg.Match(text);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value.Trim();
            }

            return string.Empty;
        }

        public static bool IsNumericString(this string str)
        {
            return str.All(char.IsDigit);
        }
    }
}
