namespace PolinMegatranscriber.Core;

public sealed record MediaToolPaths(string FFmpegPath, string FFprobePath);

public interface IMediaToolLocator
{
    MediaToolPaths Locate();
}

public sealed class FixedMediaToolLocator : IMediaToolLocator
{
    private readonly MediaToolPaths paths;

    public FixedMediaToolLocator(string ffmpegPath, string ffprobePath)
    {
        paths = new MediaToolPaths(ffmpegPath, ffprobePath);
    }

    public MediaToolPaths Locate() => paths;
}
