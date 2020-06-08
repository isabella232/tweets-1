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

    string[] FontDownloadUrls =>
        new[]
        {
            "https://github.com/googlefonts/roboto/releases/latest/download/roboto-unhinted.zip",
            "https://github.com/JetBrains/JetBrainsMono/releases/download/v1.0.6/JetBrainsMono-1.0.6.zip"
        };

    AbsolutePath FontDirectory => TemporaryDirectory / "fonts";
    IReadOnlyCollection<AbsolutePath> FontArchives => FontDirectory.GlobFiles("*.*");

    Target DownloadFonts => _ => _
        .OnlyWhenDynamic(() => FontDownloadUrls.Length != FontArchives.Count)
        .Executes(() =>
        {
            FontDownloadUrls.ForEach(x => HttpDownloadFile(x, FontDirectory / new Uri(x).Segments.Last()));
            FontArchives.ForEach(x => Uncompress(x, FontDirectory / Path.GetFileNameWithoutExtension(x)));
        });

    readonly FontCollection FontCollection = new FontCollection();
    IReadOnlyCollection<AbsolutePath> FontFiles => FontDirectory.GlobFiles("**/[!\\.]*.ttf");

    Target InstallFonts => _ => _
        .DependsOn(DownloadFonts)
        .Executes(() =>
        {
            FontFiles.ForEach(x => FontCollection.Install(x));
            FontCollection.Families.ForEach(x => Normal($"Installed font {x.Name.SingleQuote()}"));
        });

    AbsolutePath WatermarkImageFile => RootDirectory / ".." / ".." / "images" / "logo-watermark.png";
    AbsolutePath ReleaseImageFile => TemporaryDirectory / "release-image.png";

    Target ReleaseImage => _ => _
        .DependsOn(InstallFonts)
        .Executes(() =>
        {
            const float logoScaling = 0.37f;
            var logo = Image.Load(WatermarkImageFile);
            logo.Mutate(x => x.Resize((int) (logo.Width * logoScaling), (int) (logo.Height * logoScaling)));

            var robotoFont = FontCollection.Families.Single(x => x.Name == "Roboto Black");
            var graphicsOptions =
                new TextGraphicsOptions
                {
                    TextOptions = new TextOptions
                    {
                        HorizontalAlignment = HorizontalAlignment.Center,
                    }
                };

            ReleaseImageFile.Parent.GlobFiles("*.png").ForEach(DeleteFile);

            using (var collection = new MagickImageCollection())
            {
                var count = 120;
                for (var i = 0; i < count; i++)
                {
                    var image = GetImage(logo, robotoFont, i, count, graphicsOptions);
                    collection.Add(new MagickImage(image)
                    {
                        AnimationIterations = 0,
                        AnimationDelay = 5
                    });
                }

                collection.Last().AnimationDelay = 50;

                var settings = new QuantizeSettings {Colors = 256};
                collection.Quantize(settings);
                collection.Optimize();
                collection.Write(BuildProjectDirectory / "release-image.gif");
            }
        });

    const int width = 1200;
    const int height = 675;

    int releaseFontSize = 2;
    int versionFontSize = 4;

    static Stream GetImage(Image logo, FontFamily robotoFont, int i, int count, TextGraphicsOptions graphicsOptions)
    {
        PointF m = PointF.Empty;
        float fontSize;
        var quarter = count / 4;
        if (i < quarter)
        {
            fontSize = 1 + (float) Math.Pow(i / (1f * quarter), 10) * 150;
        }

        else if (i < quarter * 3)
        {
            fontSize = 151 + (i - quarter) / (2f * quarter) * 100;
        }

        else
        {
            var pow = (float) Math.Pow((i - 3f * quarter) / quarter, 4);
            fontSize = 251 + pow * 12000;
            m = new PointF(-1300 * pow, -100 * pow);
        }

        Console.WriteLine(fontSize);

        var image = new Image<Rgba64>(width: width, height: height);
        image.Mutate(x =>
        {
            x
                .BackgroundColor(Color.FromRgb(r: 25, g: 25, b: 25))
                .DrawImage(
                    logo,
                    location: new Point(image.Width / 2 - logo.Width / 2, image.Height / 2 - logo.Height / 2),
                    opacity: 0.025f)
                .DrawText(
                    text: "0.25.0",
                    font: robotoFont.CreateFont((float) fontSize),
                    color: Color.WhiteSmoke,
                    location: new PointF(
                        image.Width / 2f,
                        image.Height / 2f - fontSize * 0.75f) + m,
                    options: graphicsOptions)
                ;
        });

        var memoryStream = new MemoryStream();
        image.SaveAsJpeg(memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);
        return memoryStream;
    }
}
