using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Emby.Naming.Common;
using Emby.Naming.TV;
using MediaBrowser.Model.IO;

namespace Emby.Naming.Video
{
    /// <summary>
    /// Resolves alternative versions and extras from list of video files.
    /// </summary>
    public static class VideoListResolver
    {
        /// <summary>
        /// Resolves alternative versions and extras from list of video files.
        /// </summary>
        /// <param name="videoInfos">List of related video files.</param>
        /// <param name="namingOptions">The naming options.</param>
        /// <param name="supportMultiVersion">Indication we should consider multi-versions of content.</param>
        /// <param name="parseName">Whether to parse the name or use the filename.</param>
        /// <returns>Returns enumerable of <see cref="VideoInfo"/> which groups files together when related.</returns>
        public static IReadOnlyList<VideoInfo> Resolve(IReadOnlyList<VideoFileInfo> videoInfos, NamingOptions namingOptions, bool supportMultiVersion = true, bool parseName = true)
        {
            // Filter out all extras, otherwise they could cause stacks to not be resolved
            // See the unit test TestStackedWithTrailer
            var nonExtras = videoInfos
                .Where(i => i.ExtraType == null)
                .Select(i => new FileSystemMetadata { FullName = i.Path, IsDirectory = i.IsDirectory });

            var stackResult = StackResolver.Resolve(nonExtras, namingOptions).ToList();

            var remainingFiles = new List<VideoFileInfo>();
            var standaloneMedia = new List<VideoFileInfo>();

            for (var i = 0; i < videoInfos.Count; i++)
            {
                var current = videoInfos[i];
                if (stackResult.Any(s => s.ContainsFile(current.Path, current.IsDirectory)))
                {
                    continue;
                }

                if (current.ExtraType == null)
                {
                    standaloneMedia.Add(current);
                }
                else
                {
                    remainingFiles.Add(current);
                }
            }

            var list = new List<VideoInfo>();

            foreach (var stack in stackResult)
            {
                var info = new VideoInfo(stack.Name)
                {
                    Files = stack.Files.Select(i => VideoResolver.Resolve(i, stack.IsDirectoryStack, namingOptions, parseName))
                        .OfType<VideoFileInfo>()
                        .ToList()
                };

                info.Year = info.Files[0].Year;
                list.Add(info);
            }

            foreach (var media in standaloneMedia)
            {
                var info = new VideoInfo(media.Name) { Files = new[] { media } };

                info.Year = info.Files[0].Year;
                list.Add(info);
            }

            if (supportMultiVersion)
            {
                list = GetVideosGroupedByVersion(list, namingOptions);
            }

            // Whatever files are left, just add them
            list.AddRange(remainingFiles.Select(i => new VideoInfo(i.Name)
            {
                Files = new[] { i },
                Year = i.Year,
                ExtraType = i.ExtraType
            }));

            return list;
        }

        private static List<VideoInfo> GetVideosGroupedByVersion(List<VideoInfo> videos, NamingOptions namingOptions)
        {
            if (videos.Count == 0)
            {
                return videos;
            }

            var folderName = Path.GetFileName(Path.GetDirectoryName(videos[0].Files[0].Path.AsSpan()));

            if (folderName.Length <= 1 || !HaveSameYear(videos))
            {
                return videos;
            }

            var mergeable = new List<VideoInfo>();
            var notMergeable = new List<VideoInfo>();
            // Cannot use Span inside local functions and delegates thus we cannot use LINQ here nor merge with the above [if]
            for (var i = 0; i < videos.Count; i++)
            {
                var video = videos[i];
                if (video.ExtraType != null)
                {
                    continue;
                }

                if (IsEligibleForMultiVersion(folderName, video.Files[0].Path, namingOptions))
                {
                    mergeable.Add(video);
                }
                else
                {
                    notMergeable.Add(video);
                }
            }

            var groupedList = mergeable.GroupBy(x=> Grouper(x.Name)).ToList();
            // take grouping list and make List<VideoInfo>
            var list = new List<VideoInfo>();
            foreach (var grouping in groupedList)
            {
                var video = grouping.First();
                list.Add(video);

                var alternateVersionsLen = grouping.Count() - 1;
                var alternateVersions = new VideoFileInfo[alternateVersionsLen];
                for (int i = 0; i < alternateVersionsLen; i++)
                {
                    var alt = grouping.ElementAt(i + 1);
                    alternateVersions[i] = alt.Files[0];
                }
                if (alternateVersionsLen > 0)
                {
                    video.AlternateVersions = alternateVersions;
                    video.Name = grouping.Key;
                }
            }

            // add non mergeables back in
            list.AddRange(notMergeable);
            list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.Ordinal));

            return list;
        }

        private static string Grouper(string? s)
        {
            s ??= string.Empty;
            var idx = s.LastIndexOf(" -"); // name doesn't come in with trailing space
            return idx == -1 ? s : s[..idx];
        }

        private static bool HaveSameYear(IReadOnlyList<VideoInfo> videos)
        {
            if (videos.Count == 1)
            {
                return true;
            }

            var firstYear = videos[0].Year ?? -1;
            for (var i = 1; i < videos.Count; i++)
            {
                if ((videos[i].Year ?? -1) != firstYear)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsEligibleForMultiVersion(ReadOnlySpan<char> folderName, string testFilePath, NamingOptions namingOptions)
        {
            var testFilename = Path.GetFileNameWithoutExtension(testFilePath.AsSpan());

            // Test if filename is formatted like an episode
            var resolver = new EpisodeResolver(namingOptions);
            var episodeInfo = resolver.Resolve(testFilePath, false);

            if (episodeInfo is not null)
            {
                // if an episode, is eligible unless grouper == seriesname == folder
                var g = Grouper(testFilename.ToString());
                return !(g.Equals(episodeInfo.SeriesName, StringComparison.OrdinalIgnoreCase) && g.AsSpan().Equals(folderName, StringComparison.OrdinalIgnoreCase));
            }

            if (!testFilename.StartsWith(folderName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Remove the folder name before cleaning as we don't care about cleaning that part
            if (folderName.Length <= testFilename.Length)
            {
                testFilename = testFilename[folderName.Length..].Trim();
            }

            // There are no span overloads for regex unfortunately
            var tmpTestFilename = testFilename.ToString();
            if (CleanStringParser.TryClean(tmpTestFilename, namingOptions.CleanStringRegexes, out var cleanName))
            {
                tmpTestFilename = cleanName.Trim();
            }

            // The CleanStringParser should have removed common keywords etc.
            return string.IsNullOrEmpty(tmpTestFilename)
                    || testFilename[0] == '-'
                    || Regex.IsMatch(tmpTestFilename, @"^\[([^]]*)\]", RegexOptions.Compiled)
            ;
        }
    }
}
