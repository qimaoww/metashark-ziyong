// <copyright file="NameParser.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace Jellyfin.Plugin.MetaShark.Core
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Emby.Naming.TV;
    using Jellyfin.Plugin.MetaShark.Model;

    public static class NameParser
    {
        private static readonly Regex YearReg = new Regex(@"[12][890][0-9][0-9]", RegexOptions.Compiled);
        private static readonly Regex SeasonSuffixReg = new Regex(@"[ .]S\d{1,2}$", RegexOptions.Compiled);
        private static readonly Regex UnusedReg = new Regex(@"\[.+?\]|\(.+?\)|【.+?】", RegexOptions.Compiled);

        private static readonly Regex FixSeasonNumberReg = new Regex(@"(\[|\.)S(\d{1,2})(\]|\.)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex StartWithHyphenCharReg = new Regex(@"^[-～~]", RegexOptions.Compiled);

        private static readonly Regex ChineseIndexNumberReg = new Regex(@"第\s*?([0-9零一二三四五六七八九]+?)\s*?(集|章|话|話|期)", RegexOptions.Compiled);

        private static readonly Regex NormalizeNameReg = new Regex(@"第\s*?([0-9零一二三四五六七八九]+?)\s*?(集|章|话|話|期)", RegexOptions.Compiled);

        private static readonly Regex SpecialIndexNumberReg = new Regex(@"ep(\d{1,2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex ResolutionReg = new Regex(@"\d{3,4}x\d{3,4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex EpisodePatternReg = new Regex(@"S\d{1,2}E\d{1,3}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] ExtraKeywords =
        {
            "MENU",
            "NCED",
            "NCOP",
            "DRAMA",
            "PV",
            "BONUS",
            "EXTRA",
        };

        public static ParseNameResult Parse(string fileName, bool isEpisode = false)
        {
            fileName = NormalizeFileName(fileName ?? string.Empty);

            var parseResult = new ParseNameResult();
            var anitomyResult = ParseAnitomy(fileName, isEpisode);
            var isAnime = IsAnime(fileName);
            foreach (var item in anitomyResult)
            {
                switch (item.Category)
                {
                    case AnitomySharp.Element.ElementCategory.ElementAnimeTitle:
                        // 处理混合中英文的标题，中文一般在最前面，如V字仇杀队.V.for.Vendetta
                        char[] delimiters = { ' ', '.' };
                        var firstSpaceIndex = item.Value.IndexOfAny(delimiters);
                        if (firstSpaceIndex > 0)
                        {
                            var firstString = item.Value.Substring(0, firstSpaceIndex);
                            var lastString = item.Value.Substring(firstSpaceIndex + 1);
                            if (firstString.HasChinese() && !lastString.HasChinese() && !StartWithHyphenCharReg.IsMatch(lastString))
                            {
                                parseResult.ChineseName = CleanName(firstString);
                                parseResult.Name = CleanName(lastString);
                            }
                            else
                            {
                                parseResult.Name = CleanName(item.Value);
                            }
                        }
                        else
                        {
                            parseResult.Name = CleanName(item.Value);
                        }

                        break;
                    case AnitomySharp.Element.ElementCategory.ElementEpisodeTitle:
                        parseResult.EpisodeName = item.Value;
                        break;
                    case AnitomySharp.Element.ElementCategory.ElementAnimeSeason:
                        var seasonNumber = item.Value.ToInt();
                        if (seasonNumber > 0)
                        {
                            parseResult.ParentIndexNumber = seasonNumber;
                        }

                        break;
                    case AnitomySharp.Element.ElementCategory.ElementEpisodeNumber:
                        var year = ParseYear(item.Value);
                        if (year > 0)
                        {
                            parseResult.Year = year;
                        }
                        else
                        {
                            var episodeNumber = item.Value.ToInt();
                            if (episodeNumber > 0)
                            {
                                parseResult.IndexNumber = episodeNumber;
                            }
                        }

                        break;
                    case AnitomySharp.Element.ElementCategory.ElementAnimeType:
                        parseResult.AnimeType = item.Value;
                        break;
                    case AnitomySharp.Element.ElementCategory.ElementAnimeYear:
                        parseResult.Year = item.Value.ToInt();
                        break;
                    default:
                        break;
                }
            }

            // 修正动画季信息特殊情况，格式：[SXX]
            if (!parseResult.ParentIndexNumber.HasValue && isAnime)
            {
                var match = FixSeasonNumberReg.Match(fileName);
                if (match.Success && match.Groups.Count > 2)
                {
                    parseResult.ParentIndexNumber = match.Groups[2].Value.ToInt();
                }
            }

            // 假如 Anitomy 解析不到 year，尝试使用 jellyfin 默认 parser，看能不能解析成功
            if (parseResult.Year == null && !isAnime)
            {
                var nativeParseResult = ParseMovieByDefault(fileName);
                if (nativeParseResult.Year != null)
                {
                    parseResult = nativeParseResult;
                }
            }

            // 假如 Anitomy 解析不到集数，尝试从 volume 中获取
            if (parseResult.IndexNumber is null && isEpisode)
            {
                var volume = anitomyResult?.FirstOrDefault(e => e.Category == AnitomySharp.Element.ElementCategory.ElementVolumeNumber);
                if (volume != null && volume.Value.ToInt() > 0)
                {
                    parseResult.IndexNumber = volume.Value.ToInt();
                }
            }

            // 假如 Anitomy 解析不到集数，判断 name 是否是数字集号
            if (parseResult.IndexNumber is null && isEpisode)
            {
                if (!string.IsNullOrEmpty(parseResult.Name) && parseResult.Name.IsNumericString())
                {
                    parseResult.IndexNumber = parseResult.Name.ToInt();
                }
            }

            // 修复纯中文集数/特殊标识集数
            if (parseResult.IndexNumber is null)
            {
                parseResult.IndexNumber = ParseChineseOrSpecialIndexNumber(fileName);
            }

            // 解析不到 title 时，或解析出多个 title 时，使用默认名
            if (string.IsNullOrEmpty(parseResult.Name))
            {
                parseResult.Name = fileName;
            }

            TrySetExtraType(parseResult, fileName, isEpisode);

            return parseResult;
        }

        private static List<AnitomySharp.Element> ParseAnitomy(string fileName, bool isEpisode)
        {
            try
            {
                return AnitomySharp.AnitomySharp.Parse(fileName, new AnitomySharp.Options(title: isEpisode)).ToList();
            }
            catch (IndexOutOfRangeException)
            {
                return new List<AnitomySharp.Element>();
            }
            catch (ArgumentOutOfRangeException)
            {
                return new List<AnitomySharp.Element>();
            }
        }

        public static ParseNameResult ParseEpisode(string fileName)
        {
            return Parse(fileName, true);
        }

        /// <summary>
        /// emby原始电影解析.
        /// </summary>
        /// <returns></returns>
        public static ParseNameResult ParseMovieByDefault(string fileName)
        {
            // 默认解析器会错误把分辨率当年份，先删除
            fileName = ResolutionReg.Replace(fileName, string.Empty);

            var parseResult = new ParseNameResult();
            var nameOptions = new Emby.Naming.Common.NamingOptions();
            var result = Emby.Naming.Video.VideoResolver.CleanDateTime(fileName, nameOptions);
            if (Emby.Naming.Video.VideoResolver.TryCleanString(result.Name, nameOptions, out var cleanName))
            {
                parseResult.Name = CleanName(cleanName);
                parseResult.Year = result.Year;
            }
            else
            {
                parseResult.Name = CleanName(result.Name);
                parseResult.Year = result.Year;
            }

            return parseResult;
        }

        /// <summary>
        /// emby原始剧集解析.
        /// </summary>
        /// <returns></returns>
        public static EpisodePathParserResult ParseEpisodeByDefault(string fileName)
        {
            // EpisodePathParser需要路径信息， 这里添加一个分隔符模拟路径
            var path = Path.DirectorySeparatorChar + fileName;
            var nameOptions = new Emby.Naming.Common.NamingOptions();
            return new EpisodePathParser(nameOptions)
                .Parse(path, false);
        }

        public static bool IsSpecialDirectory(string path, bool isDirectory = false)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(path))?.ToUpperInvariant() ?? string.Empty;
            if (isDirectory)
            {
                folder = Path.GetFileName(path)?.ToUpperInvariant() ?? string.Empty;
            }

            return folder == "SP" || folder == "SPS" || folder == "SPECIALS" || folder.Contains("特典", StringComparison.Ordinal);
        }

        public static bool IsExtraDirectory(string path, bool isDirectory = false)
        {
            var folder = Path.GetFileName(Path.GetDirectoryName(path))?.ToUpperInvariant() ?? string.Empty;
            if (isDirectory)
            {
                folder = Path.GetFileName(path)?.ToUpperInvariant() ?? string.Empty;
            }

            return folder == "EXTRA"
            || folder == "MENU"
            || folder == "MENUS"
            || folder == "PV"
            || folder == "PV&CM"
            || folder == "CM"
            || folder == "BONUS"
            || folder.Contains("OPED", StringComparison.Ordinal)
            || folder.Contains("NCED", StringComparison.Ordinal)
            || folder.Contains("花絮", StringComparison.Ordinal);
        }

        // 判断是否为动漫
        // https://github.com/jxxghp/nas-tools/blob/f549c924558fd49e183333285bc6a804af1a2cb7/app/media/meta/metainfo.py#L51
        public static bool IsAnime(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            if (Regex.Match(name, @"【[+0-9XVPI-]+】\s*【", RegexOptions.IgnoreCase).Success)
            {
                return true;
            }

            if (Regex.Match(name, @"\s+-\s+[\dv]{1,4}\s+", RegexOptions.IgnoreCase).Success)
            {
                return true;
            }

            if (Regex.Match(name, @"S\d{2}\s*-\s*S\d{2}|S\d{2}|\s+S\d{1,2}|EP?\d{2,4}\s*-\s*EP?\d{2,4}|EP?\d{2,4}|\s+EP?\d{1,4}", RegexOptions.IgnoreCase).Success)
            {
                return true;
            }

            if (Regex.Match(name, @"\[[+0-9XVPI-]+]\s*\[", RegexOptions.IgnoreCase).Success)
            {
                return true;
            }

            if (Regex.Match(name, @"\[.+\].*?\[.+?\]", RegexOptions.IgnoreCase).Success)
            {
                return true;
            }

            return false;
        }

        private static string CleanName(string name)
        {
            // 电视剧名称后紧跟季信息时，会附加到名称中，需要去掉
            name = SeasonSuffixReg.Replace(name, string.Empty);

            // 删除多余的[]/()附加信息
            name = UnusedReg.Replace(name, string.Empty);

            return name.Replace(".", " ", StringComparison.Ordinal).Trim();
        }

        private static int ParseYear(string val)
        {
            var match = YearReg.Match(val);
            if (match.Success && match.Groups.Count > 0)
            {
                return match.Groups[0].Value.ToInt();
            }

            return 0;
        }

        private static string NormalizeFileName(string fileName)
        {
            // 去掉中文集数之间的空格（要不然Anitomy解析不正确）
            fileName = NormalizeNameReg.Replace(fileName, m => m.Value.Replace(" ", string.Empty, StringComparison.Ordinal));

            return fileName;
        }

        private static int? ParseChineseOrSpecialIndexNumber(string fileName)
        {
            var match = ChineseIndexNumberReg.Match(fileName);
            if (match.Success && match.Groups.Count > 1)
            {
                if (int.TryParse(match.Groups[1].Value, out var indexNumber))
                {
                    return indexNumber;
                }

                var number = Utils.ChineseNumberToInt(match.Groups[1].Value);
                if (number.HasValue)
                {
                    return number;
                }
            }
            else
            {
                match = SpecialIndexNumberReg.Match(fileName);
                if (match.Success && match.Groups.Count > 1)
                {
                    if (int.TryParse(match.Groups[1].Value, out var indexNumber))
                    {
                        return indexNumber;
                    }
                }
            }

            return null;
        }

        private static void TrySetExtraType(ParseNameResult parseResult, string fileName, bool isEpisode)
        {
            if (!string.IsNullOrEmpty(parseResult.AnimeType))
            {
                return;
            }

            if (!isEpisode && EpisodePatternReg.IsMatch(fileName))
            {
                parseResult.AnimeType = "EXTRA";
                return;
            }

            var upperFileName = fileName.ToUpperInvariant();
            if (upperFileName.Contains("VOICE MESSAGE", StringComparison.Ordinal)
                || upperFileName.Contains("MESSAGE", StringComparison.Ordinal))
            {
                parseResult.AnimeType = "MESSAGE";
                return;
            }

            foreach (var keyword in ExtraKeywords)
            {
                if (upperFileName.Contains(keyword, StringComparison.Ordinal))
                {
                    parseResult.AnimeType = keyword;
                    return;
                }
            }
        }
    }
}
