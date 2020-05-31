using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.Utilities;
using Tweetinvi;
using Tweetinvi.Models;
using Tweetinvi.Parameters;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.IO.TextTasks;
using static Nuke.Common.Logger;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
[GitHubActions(
    "scheduled",
    GitHubActionsImage.UbuntuLatest,
    OnCronSchedule = "0 13 * * 2",
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
        .Executes(async () =>
        {
            var client = new TwitterClient(
                new TwitterCredentials(
                    TwitterConsumerKey,
                    TwitterConsumerSecret,
                    TwitterAccessToken,
                    TwitterAccessTokenSecret));

            var tweets = TweetDirectory.GlobDirectories("*").OrderBy(x => (string) x).ToList();
            var directory = tweets.ElementAt((int) (DateTime.Now.Ticks / TimeSpan.FromDays(7).Ticks) % tweets.Count);

            var text = ReadAllText(directory.GlobFiles("*.md").Single());
            var media = directory.GlobFiles("*.png", "*.jpeg", "*.jpg", "*.gif")
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
                Text = text,
                Medias = media
            };

            var tweet = await client.Tweets.PublishTweet(tweetParameters);

            Info($"Sent tweet: {tweet.Url}");
        });
}
