// <copyright file="Utils.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    public static class Utils
    {
        private static readonly Dictionary<char, char> ChineseNumberMap = new Dictionary<char, char>()
        {
            { '一', '1' },
            { '二', '2' },
            { '三', '3' },
            { '四', '4' },
            { '五', '5' },
            { '六', '6' },
            { '七', '7' },
            { '八', '8' },
            { '九', '9' },
            { '零', '0' },
        };

        private static readonly Dictionary<char, char> AsciiNumberMap = new Dictionary<char, char>()
        {
            { '1', '一' },
            { '2', '二' },
            { '3', '三' },
            { '4', '四' },
            { '5', '五' },
            { '6', '六' },
            { '7', '七' },
            { '8', '八' },
            { '9', '九' },
            { '0', '零' },
        };

        public static DateTime UnixTimeStampToDateTime(long unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dateTime = dateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dateTime;
        }

        /// <summary>
        /// 转换数字.
        /// </summary>
        /// <returns></returns>
        public static int? ChineseNumberToInt(string str)
        {
            if (string.IsNullOrEmpty(str))
            {
                return null;
            }

            var numberArr = str.ToCharArray().Select(x => ChineseNumberMap.TryGetValue(x, out var mapped) ? mapped : x).ToArray();
            if (int.TryParse(new string(numberArr), out var number))
            {
                return number;
            }

            return null;
        }

        /// <summary>
        /// 转换中文数字.
        /// </summary>
        /// <returns></returns>
        public static string? ToChineseNumber(int? number)
        {
            if (number is null)
            {
                return null;
            }

            var numberArr = $"{number}".ToCharArray().Select(x => AsciiNumberMap.TryGetValue(x, out var mapped) ? mapped : x).ToArray();
            return new string(numberArr);
        }
    }
}
