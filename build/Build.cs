using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
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
    OnCronSchedule = "0 13 * * 1,4",
    ImportGitHubTokenAs = nameof(GitHubToken),
    ImportSecrets =
        new[]
        {
            nameof(TwitterConsumerKey),
            nameof(TwitterConsumerSecret),
            nameof(TwitterAccessToken),
            nameof(TwitterAccessTokenSecret)
        },
    InvokedTargets = new[] { nameof(Foo) })]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.Foo);

    [Parameter] readonly string GitHubToken;

    [Parameter] readonly string TwitterConsumerKey;
    [Parameter] readonly string TwitterConsumerSecret;
    [Parameter] readonly string TwitterAccessToken;
    [Parameter] readonly string TwitterAccessTokenSecret;

    Target Foo => _ => _
        .Executes(() =>
        {
            Console.WriteLine(DateTime.Now);
        });

    Target Tweet => _ => _
        .Executes(async () =>
        {
            var client = new TwitterClient(
                new TwitterCredentials(
                    TwitterConsumerKey,
                    TwitterConsumerSecret,
                    TwitterAccessToken,
                    TwitterAccessTokenSecret));

            var directory = RootDirectory / "src" / "shell-completion";
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
