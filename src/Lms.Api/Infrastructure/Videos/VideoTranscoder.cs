using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Lms.Api.Domain.Videos;

namespace Lms.Api.Infrastructure.Videos;

/// <summary>One quality of a converted video: its picture height and how many pieces it has.</summary>
public sealed record Rendition(int Height, int Segments);

public sealed record TranscodeResult(IReadOnlyList<Rendition> Renditions, bool HasPoster, int? DurationSeconds);

/// <summary>
/// Turns an uploaded video into streaming pieces. Work happens in a folder holding the upload as <c>input.bin</c>;
/// results are written beside it: <c>master.m3u8</c>, <c>v0-index.m3u8</c>, <c>v0-seg00000.ts</c>… (v0 is the best quality), <c>poster.jpg</c> and <c>audio.mp3</c>.
/// </summary>
public interface IVideoTranscoder
{
    /// <summary>False when no FFmpeg is set up; uploads are then played as they are.</summary>
    bool Enabled { get; }
    Task<TranscodeResult> PackageAsync(string workDirectory, CancellationToken cancellationToken);
    /// <summary>Makes a small mono recording of the sound (audio.mp3) for speech-to-text.</summary>
    Task ExtractAudioAsync(string workDirectory, CancellationToken cancellationToken);
}

public static class VideoFiles
{
    public const string Master = "master.m3u8";
    /// <summary>The single playlist of videos converted before there were several qualities.</summary>
    public const string Playlist = "index.m3u8";
    public const string Poster = "poster.jpg";
    public const string Input = "input.bin";
    public const string Audio = "audio.mp3";
    public static readonly Regex AllowedName = new(@"^(master\.m3u8|index\.m3u8|poster\.jpg|seg\d{5}\.ts|v\d-index\.m3u8|v\d-seg\d{5}\.ts)$", RegexOptions.Compiled);

    public static string Segment(int index) => $"seg{index:D5}.ts";
    public static string RenditionPlaylist(int rendition) => $"v{rendition}-index.m3u8";
    public static string RenditionSegment(int rendition, int index) => $"v{rendition}-seg{index:D5}.ts";
    public static string Key(Guid tenantId, Guid courseId, Guid videoId, string name) => $"{tenantId:D}/{courseId:D}/video-{videoId:D}/{name}";
    public static string ContentType(string name) => name.EndsWith(".m3u8", StringComparison.Ordinal) ? "application/vnd.apple.mpegurl" : name.EndsWith(".jpg", StringComparison.Ordinal) ? "image/jpeg" : "video/mp2t";

    /// <summary>"720:12,360:12" for a 720p and a 360p version of 12 pieces each, best first.</summary>
    public static string FormatLayout(IEnumerable<Rendition> renditions) => string.Join(',', renditions.Select(item => $"{item.Height}:{item.Segments}"));

    public static List<Rendition> ParseLayout(string? layout)
    {
        var result = new List<Rendition>();
        foreach (var part in (layout ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split(':');
            if (pair.Length == 2 && int.TryParse(pair[0], out var height) && int.TryParse(pair[1], out var segments)) result.Add(new Rendition(height, segments));
        }
        return result;
    }

    public static bool HasStreaming(Video video) => video.HlsSegmentCount is > 0;

    /// <summary>The playlist a player starts from: the master list that offers every quality, or the single one of older conversions.</summary>
    public static string StartPlaylist(Video video) => video.HlsLayout is { Length: > 0 } ? Master : Playlist;

    /// <summary>Every file made from the video (and nothing else), so a request can only ask for what exists.</summary>
    public static IEnumerable<string> FilesOf(Video video)
    {
        if (video.HlsLayout is { Length: > 0 })
        {
            yield return Master;
            foreach (var (item, rendition) in ParseLayout(video.HlsLayout).Select((item, index) => (item, index)))
            {
                yield return RenditionPlaylist(rendition);
                for (var n = 0; n < item.Segments; n++) yield return RenditionSegment(rendition, n);
            }
        }
        else if (video.HlsSegmentCount is > 0)
        {
            yield return Playlist;
            for (var n = 0; n < video.HlsSegmentCount; n++) yield return Segment(n);
        }
        yield return Poster;
    }

    public static bool Exists(Video video, string name)
    {
        if (name == Poster) return video.HasPoster;
        if (!AllowedName.IsMatch(name)) return false;
        if (name == Master) return video.HlsLayout is { Length: > 0 };
        if (name == Playlist) return video.HlsLayout is not { Length: > 0 } && video.HlsSegmentCount is > 0;
        if (name[0] == 's') return video.HlsLayout is not { Length: > 0 } && int.Parse(name[3..8]) < (video.HlsSegmentCount ?? 0);
        var renditions = ParseLayout(video.HlsLayout);
        var which = name[1] - '0';
        if (which >= renditions.Count) return false;
        return name.EndsWith("-index.m3u8", StringComparison.Ordinal) || int.Parse(name[6..11]) < renditions[which].Segments;
    }
}

/// <summary>
/// Runs FFmpeg: the program on this machine (<c>Videos:Ffmpeg:Command</c>, default "ffmpeg"), or a container
/// (<c>Videos:Ffmpeg:DockerImage</c>, e.g. linuxserver/ffmpeg) so nothing needs installing.
/// Enabled with <c>Videos:Processing:Enabled</c>.
/// </summary>
public sealed partial class FfmpegVideoTranscoder(IConfiguration configuration, ILogger<FfmpegVideoTranscoder> logger) : IVideoTranscoder
{
    /// <summary>The qualities offered, best first, with the picture bit rate (kbit/s) used for each. Only those no taller than the video are made.</summary>
    public static readonly (int Height, int Kbps)[] Ladder = [(1080, 5000), (720, 2800), (480, 1400), (360, 800), (240, 400)];

    public bool Enabled => configuration.GetValue("Videos:Processing:Enabled", false);

    public async Task<TranscodeResult> PackageAsync(string workDirectory, CancellationToken cancellationToken)
    {
        // Looking at the file first tells us its size, whether it has sound, and how long it is.
        var probe = await RunAsync(workDirectory, ["-hide_banner", "-i", VideoFiles.Input], cancellationToken, allowFailure: true);
        var info = ParseProbe(probe) ?? throw new InvalidOperationException("The file could not be read as a video.");

        var heights = ChooseHeights(info.Height);
        await RunAsync(workDirectory, BuildHlsArguments(heights, info.HasAudio), cancellationToken);

        var renditions = new List<Rendition>();
        for (var i = 0; i < heights.Count; i++)
        {
            var segments = Directory.EnumerateFiles(workDirectory, $"v{i}-seg*.ts").Count();
            if (segments == 0 || !File.Exists(Path.Combine(workDirectory, VideoFiles.RenditionPlaylist(i)))) throw new InvalidOperationException("The video could not be converted.");
            renditions.Add(new Rendition(heights[i], segments));
        }
        if (!File.Exists(Path.Combine(workDirectory, VideoFiles.Master))) throw new InvalidOperationException("The video could not be converted.");

        var poster = false;
        foreach (var second in new[] { "1", "0" })   // a one-second clip has no frame at 1s
        {
            try { await RunAsync(workDirectory, ["-y", "-ss", second, "-i", VideoFiles.Input, "-frames:v", "1", "-vf", "scale=640:-2", VideoFiles.Poster], cancellationToken); } catch (InvalidOperationException) { }
            poster = File.Exists(Path.Combine(workDirectory, VideoFiles.Poster)) && new FileInfo(Path.Combine(workDirectory, VideoFiles.Poster)).Length > 0;
            if (poster) break;
        }
        return new TranscodeResult(renditions, poster, info.DurationSeconds);
    }

    public async Task ExtractAudioAsync(string workDirectory, CancellationToken cancellationToken)
    {
        await RunAsync(workDirectory, ["-y", "-i", VideoFiles.Input, "-vn", "-ac", "1", "-ar", "16000", "-b:a", "32k", VideoFiles.Audio], cancellationToken);
        if (!File.Exists(Path.Combine(workDirectory, VideoFiles.Audio))) throw new InvalidOperationException("The video has no sound to transcribe.");
    }

    /// <summary>The qualities to make for a video of this height: every rung of the ladder that fits, or just the video's own height when it is smaller than all of them.</summary>
    public static List<int> ChooseHeights(int sourceHeight)
    {
        var fits = Ladder.Where(rung => rung.Height <= sourceHeight).Select(rung => rung.Height).ToList();
        return fits.Count > 0 ? fits : [Math.Max(2, sourceHeight / 2 * 2)];
    }

    public static int KbpsFor(int height) => Ladder.Where(rung => rung.Height >= height).Select(rung => rung.Kbps).DefaultIfEmpty(400).Last();

    /// <summary>The FFmpeg arguments that make every quality at once, with a master playlist offering them all.</summary>
    public static List<string> BuildHlsArguments(IReadOnlyList<int> heights, bool hasAudio)
    {
        var count = heights.Count;
        var filter = count == 1
            ? $"[0:v:0]scale=-2:{heights[0]}[o0]"
            : $"[0:v:0]split={count}{string.Concat(Enumerable.Range(0, count).Select(i => $"[s{i}]"))};" + string.Join(';', heights.Select((height, i) => $"[s{i}]scale=-2:{height}[o{i}]"));
        var args = new List<string> { "-y", "-i", VideoFiles.Input, "-filter_complex", filter };
        for (var i = 0; i < count; i++) { args.Add("-map"); args.Add($"[o{i}]"); }
        if (hasAudio) for (var i = 0; i < count; i++) { args.Add("-map"); args.Add("0:a:0"); }
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-pix_fmt", "yuv420p", "-sc_threshold", "0", "-force_key_frames", "expr:gte(t,n_forced*6)"]);
        for (var i = 0; i < count; i++)
        {
            var rate = KbpsFor(heights[i]);
            args.AddRange([$"-b:v:{i}", $"{rate}k", $"-maxrate:v:{i}", $"{rate * 12 / 10}k", $"-bufsize:v:{i}", $"{rate * 2}k"]);
        }
        if (hasAudio) args.AddRange(["-c:a", "aac", "-b:a", "128k"]);
        args.AddRange(["-f", "hls", "-hls_time", "6", "-hls_playlist_type", "vod", "-hls_flags", "independent_segments", "-master_pl_name", VideoFiles.Master,
            "-var_stream_map", string.Join(' ', Enumerable.Range(0, count).Select(i => hasAudio ? $"v:{i},a:{i}" : $"v:{i}")),
            "-hls_segment_filename", "v%v-seg%05d.ts", "v%v-index.m3u8"]);
        return args;
    }

    public sealed record Probe(int Width, int Height, bool HasAudio, int? DurationSeconds);

    public static Probe? ParseProbe(string log)
    {
        var video = VideoStream().Match(log);
        if (!video.Success) return null;
        return new Probe(int.Parse(video.Groups["w"].Value, CultureInfo.InvariantCulture), int.Parse(video.Groups["h"].Value, CultureInfo.InvariantCulture), log.Contains("Audio:", StringComparison.Ordinal), ParseDuration(log));
    }

    private async Task<string> RunAsync(string workDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken, bool allowFailure = false)
    {
        var image = configuration["Videos:Ffmpeg:DockerImage"]?.Trim();
        var info = new ProcessStartInfo { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDirectory };
        if (string.IsNullOrEmpty(image)) info.FileName = configuration["Videos:Ffmpeg:Command"]?.Trim() is { Length: > 0 } command ? command : "ffmpeg";
        else
        {
            info.FileName = "docker";
            foreach (var part in new[] { "run", "--rm", "-v", $"{workDirectory}:/work", "-w", "/work", image }) info.ArgumentList.Add(part);
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("FFmpeg could not be started. Install it or set Videos:Ffmpeg:DockerImage.", exception);
        }
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Videos:Processing:TimeoutMinutes", 60))));
        try { await process.WaitForExitAsync(limit.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw cancellationToken.IsCancellationRequested ? new OperationCanceledException(cancellationToken) : new InvalidOperationException("Converting the video took too long.");
        }
        var text = await errors + await output;
        if (process.ExitCode != 0 && !allowFailure)
        {
            logger.LogWarning("FFmpeg exited with {Code}: {Tail}", process.ExitCode, Tail(text));
            throw new InvalidOperationException("The file could not be read as a video. " + Tail(text, 200));
        }
        return text;
    }

    private static string Tail(string text, int length = 600) => text.Length <= length ? text.Trim() : text[^length..].Trim();

    public static int? ParseDuration(string log)
    {
        var match = DurationPattern().Match(log);
        if (!match.Success) return null;
        var seconds = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60
            + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        return seconds > 0 ? (int)Math.Ceiling(seconds) : null;
    }

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationPattern();

    [GeneratedRegex(@"Video:.*?, (?<w>\d{2,5})x(?<h>\d{2,5})[ ,\[]")]
    private static partial Regex VideoStream();
}
