using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using ImageMagick;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Tweetinvi;
using Tweetinvi.Models;
using Tweetinvi.Parameters;
using static Nuke.Common.IO.CompressionTasks;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.HttpTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.TextTasks;
using static Nuke.Common.Logger;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
[GitHubActions(
    "scheduled",
    GitHubActionsImage.UbuntuLatest,
    OnCronSchedule = "0 13 * * 3",
    ImportGitHubTokenAs = nameof(GitHubToken),
    ImportSecrets =
        new[]
        {
            nameof(TwitterConsumerKey),
            nameof(TwitterConsumerSecret),
            nameof(TwitterAccessToken),
            nameof(TwitterAccessTokenSecret)
        },
    InvokedTargets = new[] {nameof(Tweet)})]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Tweet);

    [Parameter] readonly string GitHubToken;

    [Parameter] readonly string TwitterConsumerKey;
    [Parameter] readonly string TwitterConsumerSecret;
    [Parameter] readonly string TwitterAccessToken;
    [Parameter] readonly string TwitterAccessTokenSecret;

    AbsolutePath TweetDirectory => RootDirectory / "tweets";

    Target Tweet => _ => _
        .Requires(() => TwitterConsumerKey)
        .Requires(() => TwitterConsumerSecret)
        .Requires(() => TwitterAccessToken)
        .Requires(() => TwitterAccessTokenSecret)
        .Executes(async () =>
        {
            var client = new TwitterClient(
                new TwitterCredentials(
                    TwitterConsumerKey,
                    TwitterConsumerSecret,
                    TwitterAccessToken,
                    TwitterAccessTokenSecret));

            var tweetDirectories = TweetDirectory.GlobDirectories("*").OrderBy(x => (string) x).ToList();
            var index = (int) (DateTime.Now.Ticks / TimeSpan.FromDays(7).Ticks) %  tweetDirectories.Count;
            var tweetDirectory = tweetDirectories.Last();

            var sentTweets = new List<ITweet>();
            var sortedTweets = tweetDirectory.GlobFiles("*.md").Select(x => x.ToString()).OrderBy(x => x);
            foreach (var tweetFile in sortedTweets)
            {
                var part = Path.GetFileNameWithoutExtension(tweetFile);
                var text = ReadAllText(tweetFile);
                var media = tweetDirectory.GlobFiles($"{part}*.png", $"{part}*.jpeg", $"{part}*.jpg", $"{part}*.gif")
                    .Select(async x => await client.Upload.UploadTweetImage(
                        new UploadTweetImageParameters(ReadAllBytes(x))
                        {
                            MediaCategory = x.ToString().EndsWithOrdinalIgnoreCase("gif")
                                ? MediaCategory.Gif
                                : MediaCategory.Image
                        }))
                    .Select(x => x.Result).ToList();

                var tweetParameters = new PublishTweetParameters
                {
                    InReplyToTweetId = sentTweets.LastOrDefault()?.Id,
                    Text = text,
                    Medias = media
                };

                var tweet = await client.Tweets.PublishTweet(tweetParameters);
                sentTweets.Add(tweet);
            }

            Info($"Sent tweet: {sentTweets.First().Url}");
        });
}
