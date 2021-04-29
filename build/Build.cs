using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using CsvHelper.Configuration;
using JetBrains.Annotations;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Tweetinvi;
using Tweetinvi.Models;
using Tweetinvi.Parameters;
using static Nuke.Common.ControlFlow;
using static Nuke.Common.IO.TextTasks;
using static Nuke.Common.Logger;
using static Nuke.Common.Tools.Git.GitTasks;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
[GitHubActions(
    "dispatch",
    GitHubActionsImage.UbuntuLatest,
    OnWorkflowDispatchRequiredInputs = new[] {nameof(TweetBaseName)},
    ImportGitHubTokenAs = nameof(GitHubToken),
    ImportSecrets =
        new[]
        {
            nameof(TwitterConsumerKey),
            nameof(TwitterConsumerSecret),
            nameof(TwitterAccessToken),
            nameof(TwitterAccessTokenSecret)
        },
    InvokedTargets = new[] {nameof(SendTweet)})]
partial class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode
    public static int Main() => Execute<Build>(x => x.SendTweet);

    [Parameter] readonly string TwitterConsumerKey;
    [Parameter] readonly string TwitterConsumerSecret;
    [Parameter] readonly string TwitterAccessToken;
    [Parameter] readonly string TwitterAccessTokenSecret;

    AbsolutePath TweetDirectory => RootDirectory / "tweets";
    AbsolutePath TweetStatisticsFile => RootDirectory / "tweet-statistics.csv";
    List<TweetData> TweetStatistics = new List<TweetData>();

    Target LoadTweetStatistics => _ => _
        .OnlyWhenStatic(() => File.Exists(TweetStatisticsFile))
        .Executes(() =>
        {
            using var reader = new StreamReader(TweetStatisticsFile);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            csv.Context.RegisterClassMap<TweetDataMap>();
            TweetStatistics = csv.GetRecords<TweetData>().ToList();
        });

    Target UpdateTweetStatistics => _ => _
        .DependsOn(LoadTweetStatistics)
        .Requires(() => TwitterConsumerKey)
        .Requires(() => TwitterConsumerSecret)
        .Requires(() => TwitterAccessToken)
        .Requires(() => TwitterAccessTokenSecret)
        .Executes(() =>
        {
            var client = new TwitterClient(
                new TwitterCredentials(
                    TwitterConsumerKey,
                    TwitterConsumerSecret,
                    TwitterAccessToken,
                    TwitterAccessTokenSecret));

            TweetStatistics.Where(x => !x.FavoriteCount.HasValue)
                .ForEach(x =>
                {
                    var tweet = client.Tweets.GetTweetAsync(x.Id).GetAwaiter().GetResult();
                    x.FavoriteCount = tweet.FavoriteCount;
                    x.RetweetCount = tweet.RetweetCount;
                    x.ReplyCount = tweet.ReplyCount;
                });
        });

    [Parameter] readonly string TweetBaseName;

    Target SendTweet => _ => _
        .DependsOn(LoadTweetStatistics)
        .DependsOn(UpdateTweetStatistics)
        .Triggers(SaveTweetStatistics)
        .Requires(() => TwitterConsumerKey)
        .Requires(() => TwitterConsumerSecret)
        .Requires(() => TwitterAccessToken)
        .Requires(() => TwitterAccessTokenSecret)
        .Executes(async () =>
        {
            string GetTweetBaseName()
                => TweetDirectory
                    .GlobFiles("*.md")
                    .Select(x => Path.GetFileNameWithoutExtension(x))
                    .Select(x => x.ReplaceRegex("\\d+.*", x => string.Empty))
                    .Distinct()
                    .OrderBy(x => TweetStatistics.FindIndex(y => y.Name == x))
                    .ThenBy(x => x).ToList().First();

            var tweetBaseName = TweetBaseName ?? GetTweetBaseName();
            var tweetFiles = TweetDirectory
                .GlobFiles($"{tweetBaseName}*.md")
                .Select(x => x.ToString())
                .OrderBy(x => x).ToList();
            Assert(tweetFiles.Count > 0, "tweetFiles.Count > 0");

            var client = new TwitterClient(
                new TwitterCredentials(
                    TwitterConsumerKey,
                    TwitterConsumerSecret,
                    TwitterAccessToken,
                    TwitterAccessTokenSecret));

            foreach (var tweetFile in tweetFiles)
            {
                var tweetName = Path.GetFileNameWithoutExtension(tweetFile);
                var text = ReadAllText(tweetFile);
                var media = TweetDirectory
                    .GlobFiles($"{tweetName}*.png", $"{tweetName}*.jpeg", $"{tweetName}*.jpg", $"{tweetName}*.gif")
                    .Select(async x => await client.Upload.UploadTweetImageAsync(
                        new UploadTweetImageParameters(ReadAllBytes(x))
                        {
                            MediaCategory = x.ToString().EndsWithOrdinalIgnoreCase("gif")
                                ? MediaCategory.Gif
                                : MediaCategory.Image
                        }))
                    .Select(x => x.Result).ToList();
                var parameters = new PublishTweetParameters
                {
                    InReplyToTweetId = TweetStatistics.FirstOrDefault(x => x.FavoriteCount == null)?.Id,
                    Text = text,
                    Medias = media
                };

                var tweet = await client.Tweets.PublishTweetAsync(parameters);
                Info($"Sent tweet: {tweetName} [{tweet.Url}]");
                TweetStatistics.Insert(index: 0, new TweetData
                {
                    Id = tweet.Id,
                    DateTime = DateTime.Now,
                    Name = tweetName,
                    Url = tweet.Url
                });
            }
        });

    [CI] readonly GitHubActions Actions;
    [Parameter] readonly string GitHubToken;
    [GitRepository] readonly GitRepository Repository;

    Target SaveTweetStatistics => _ => _
        .DependsOn(LoadTweetStatistics)
        .Triggers(CommitStatistics)
        .Executes(() =>
        {
            using var writer = new StreamWriter(TweetStatisticsFile);
            using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
            csv.Context.RegisterClassMap<TweetDataMap>();
            csv.WriteRecords(TweetStatistics);
        });

    string CommitterName => "Matthias Koch";
    string CommitterEmail => "ithrowexceptions@gmail.com";

    Target CommitStatistics => _ => _
        .Executes(() =>
        {
            // https://github.community/t/how-does-one-commit-from-an-action/16127
            // https://github.com/eine/actions/blob/3f0701c2f20780984590bd955839a38b75c96668/.github/workflows/push.yml#L33-L48
            var remote = $"https://{Actions.GitHubActor}:{GitHubToken}@github.com/{Actions.GitHubRepository}";
            Git($"remote set-url origin {remote.DoubleQuote()}");
            Git($"config user.name {CommitterName.DoubleQuote()}");
            Git($"config user.email {CommitterEmail.DoubleQuote()}");
            Git($"add {TweetStatisticsFile}");
            Git($"commit -m {"Update statistics".DoubleQuote()}");
            Git($"push origin HEAD:{Repository.Branch}");
        });

    class TweetData
    {
        public string Name;
        public DateTime DateTime;
        public string Url;
        public long Id;
        public int? FavoriteCount;
        public int? RetweetCount;
        public int? ReplyCount;
    }

    [UsedImplicitly]
    class TweetDataMap : ClassMap<TweetData>
    {
        [SuppressMessage("ReSharper", "VirtualMemberCallInConstructor")]
        public TweetDataMap()
        {
            Map(x => x.Name);
            Map(x => x.DateTime);
            Map(x => x.Url);
            Map(x => x.Id);
            Map(x => x.FavoriteCount);
            Map(x => x.RetweetCount);
            Map(x => x.ReplyCount);
        }
    }
}
