using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using static Lms.Api.Tests.TestWorld;

namespace Lms.Api.Tests;

/// <summary>Uploading a video in pieces: starting, sending, carrying on after a break, refusing what is wrong, and joining the pieces into the video.</summary>
public sealed class VideoUploadTests
{
    private const string Uploads = "/api/v1/tenant/videos/uploads";
    private const int Piece = 1024 * 1024;

    private sealed record Setup(TestWorld World, LmsApiFactory Factory, Tenant Tenant, Person Teacher, Person Other, Person Ada, CourseInfo Course);

    private static async Task<Setup> NewSetupAsync(Dictionary<string, string?>? more = null)
    {
        var settings = new Dictionary<string, string?> { ["Videos:ChunkMegabytes"] = "1", ["Videos:MaxMegabytes"] = "8" };
        foreach (var (key, value) in more ?? []) settings[key] = value;
        var factory = new LmsApiFactory { ExtraSettings = settings, Transcoder = new FakeVideoTranscoder { Enabled = false } };
        var world = new TestWorld(factory);
        var t = await world.NewTenantAsync();
        var teacher = await world.AddPersonAsync(t, "Tara", "TEACHER");
        var other = await world.AddPersonAsync(t, "Otto", "TEACHER");
        var ada = await world.AddLearnerAsync(t, "Ada");
        var course = await world.NewCourseAsync(t, "UP-1");
        Assert.True((await EnrollAsync(ada, course)).IsSuccessStatusCode);
        return new Setup(world, factory, t, teacher, other, ada, course);
    }

    /// <summary>A pretend MP4 of the given size: a real file signature up front, then filler that differs from piece to piece so a mix-up shows.</summary>
    private static byte[] Mp4(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i / Piece * 40 + i % 251);
        new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'m', (byte)'p', (byte)'4', (byte)'2' }.CopyTo(bytes, 0);
        return bytes;
    }

    private static object Start(Setup s, int size, string? contentType = "video/mp4", string title = "Limits", string? fileName = "limits.mp4")
        => new { courseId = s.Course.Id, title, description = "About limits", fileName, contentType, sizeBytes = size, durationSeconds = 90 };

    private static async Task<JsonElement> CreateAsync(Setup s, int size)
    {
        var response = await s.Teacher.Client.PostAsJsonAsync(Uploads, Start(s, size));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync(response);
    }

    private static Task<HttpResponseMessage> SendAsync(Person who, Guid id, int index, byte[] file)
    {
        var from = index * Piece;
        var content = new ByteArrayContent(file[from..Math.Min(file.Length, from + Piece)]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return who.Client.PutAsync($"{Uploads}/{id}/chunks/{index}", content);
    }

    private static Task<HttpResponseMessage> JoinAsync(Person who, Guid id) => who.Client.PostAsync($"{Uploads}/{id}/complete", null);

    [Fact]
    public async Task Starting_an_upload_says_how_big_the_pieces_are_and_how_many_there_will_be()
    {
        var s = await NewSetupAsync();
        var upload = await CreateAsync(s, Piece * 2 + 10);
        Assert.Equal((Piece, 3), (upload.GetProperty("chunkSize").GetInt32(), upload.GetProperty("totalChunks").GetInt32()));
        Assert.Empty(upload.GetProperty("received").EnumerateArray());
    }

    [Fact]
    public async Task Only_staff_who_manage_courses_can_upload_and_what_is_wrong_is_refused_before_anything_is_sent()
    {
        var s = await NewSetupAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.PostAsJsonAsync(Uploads, Start(s, 100))).StatusCode);

        var tooBig = await s.Teacher.Client.PostAsJsonAsync(Uploads, Start(s, 9 * Piece));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooBig.StatusCode);
        Assert.Contains("limit of 8 MB", await tooBig.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync(Uploads, Start(s, 100, contentType: "application/pdf"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync(Uploads, Start(s, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync(Uploads, Start(s, 100, title: " "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PostAsJsonAsync(Uploads, new { courseId = Guid.NewGuid(), title = "x", fileName = "a.mp4", contentType = "video/mp4", sizeBytes = 100 })).StatusCode);
        await s.World.WithDbAsync(s.Tenant.Slug, async db => Assert.Empty(await db.VideoUploads.ToListAsync()));
    }

    [Fact]
    public async Task The_pieces_are_joined_into_a_video_with_the_whole_file_and_nothing_is_left_behind()
    {
        var s = await NewSetupAsync();
        var file = Mp4(Piece * 2 + 777);
        var id = (await CreateAsync(s, file.Length)).GetProperty("id").GetGuid();
        for (var index = 0; index < 3; index++)
        {
            var sent = await SendAsync(s.Teacher, id, index, file);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            Assert.Equal(index + 1, (await ReadAsync(sent)).GetProperty("received").GetArrayLength());
        }
        var joined = await JoinAsync(s.Teacher, id);
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        var video = await ReadAsync(joined);
        Assert.Equal(("Limits", "Uploaded", "Ready", (long)file.Length, 90), (video.GetProperty("title").GetString(), video.GetProperty("type").GetString(), video.GetProperty("status").GetString(), video.GetProperty("sizeBytes").GetInt64(), video.GetProperty("durationSeconds").GetInt32()));
        Assert.Equal(s.Course.Id, video.GetProperty("courseId").GetGuid());

        // What plays back is the file that was sent, byte for byte.
        var link = await ReadAsync(await s.Teacher.Client.GetAsync($"/api/v1/tenant/videos/{video.GetProperty("id").GetGuid()}/link"));
        var played = await s.Factory.CreateClient().GetByteArrayAsync(link.GetProperty("url").GetString());
        Assert.Equal(file, played);

        await s.World.WithDbAsync(s.Tenant.Slug, async db => Assert.Empty(await db.VideoUploads.ToListAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync($"{Uploads}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await JoinAsync(s.Teacher, id)).StatusCode);                  // it cannot be joined twice
    }

    [Fact]
    public async Task An_upload_can_be_carried_on_after_a_break_from_the_pieces_that_arrived()
    {
        var s = await NewSetupAsync();
        var file = Mp4(Piece * 3);
        var id = (await CreateAsync(s, file.Length)).GetProperty("id").GetGuid();
        await SendAsync(s.Teacher, id, 0, file);
        await SendAsync(s.Teacher, id, 2, file);

        var state = await ReadAsync(await s.Teacher.Client.GetAsync($"{Uploads}/{id}"));
        Assert.Equal([0, 2], state.GetProperty("received").EnumerateArray().Select(item => item.GetInt32()).ToArray());

        var early = await JoinAsync(s.Teacher, id);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("1 piece of the video has not arrived", await early.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await SendAsync(s.Teacher, id, 1, file)).StatusCode);
        await SendAsync(s.Teacher, id, 1, file);                                                              // a piece sent twice does no harm
        Assert.Equal(HttpStatusCode.Created, (await JoinAsync(s.Teacher, id)).StatusCode);
    }

    [Fact]
    public async Task A_piece_of_the_wrong_size_or_number_is_refused_and_the_upload_stays_as_it_was()
    {
        var s = await NewSetupAsync();
        var file = Mp4(Piece + 500);
        var id = (await CreateAsync(s, file.Length)).GetProperty("id").GetGuid();

        var content = new ByteArrayContent(new byte[Piece - 1]);
        var shortPiece = await s.Teacher.Client.PutAsync($"{Uploads}/{id}/chunks/0", content);
        Assert.Equal(HttpStatusCode.BadRequest, shortPiece.StatusCode);
        Assert.Contains($"should be {Piece} bytes", await shortPiece.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PutAsync($"{Uploads}/{id}/chunks/2", new ByteArrayContent(new byte[500]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Teacher.Client.PutAsync($"{Uploads}/{id}/chunks/-1", new ByteArrayContent(new byte[500]))).StatusCode);
        Assert.Empty((await ReadAsync(await s.Teacher.Client.GetAsync($"{Uploads}/{id}"))).GetProperty("received").EnumerateArray());
    }

    [Fact]
    public async Task A_file_that_is_not_really_a_video_is_refused_at_the_first_piece_and_the_upload_is_dropped()
    {
        var s = await NewSetupAsync();
        var file = new byte[Piece + 10];
        new byte[] { (byte)'%', (byte)'P', (byte)'D', (byte)'F', (byte)'-', (byte)'1', (byte)'.', (byte)'7' }.CopyTo(file, 0);
        var id = (await CreateAsync(s, file.Length)).GetProperty("id").GetGuid();
        var refused = await SendAsync(s.Teacher, id, 0, file);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync($"{Uploads}/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_upload_belongs_to_the_person_who_started_it_and_can_be_cancelled()
    {
        var s = await NewSetupAsync();
        var file = Mp4(Piece);
        var id = (await CreateAsync(s, file.Length)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await s.Other.Client.GetAsync($"{Uploads}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(s.Other, id, 0, file)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await JoinAsync(s.Other, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Other.Client.DeleteAsync($"{Uploads}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await s.Ada.Client.GetAsync($"{Uploads}/{id}")).StatusCode);

        await SendAsync(s.Teacher, id, 0, file);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Teacher.Client.DeleteAsync($"{Uploads}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync($"{Uploads}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Uploads_left_unfinished_for_too_long_are_cleared_when_the_next_one_starts()
    {
        var s = await NewSetupAsync();
        var stale = (await CreateAsync(s, Piece)).GetProperty("id").GetGuid();
        var recent = (await CreateAsync(s, Piece)).GetProperty("id").GetGuid();
        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var old = await db.VideoUploads.SingleAsync(item => item.Id == stale); old.UpdatedAtUtc = DateTimeOffset.UtcNow.AddDays(-3); await db.SaveChangesAsync(); });
        await CreateAsync(s, Piece);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync($"{Uploads}/{stale}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Teacher.Client.GetAsync($"{Uploads}/{recent}")).StatusCode);
    }

    [Fact]
    public async Task An_upload_into_a_course_archived_meanwhile_is_refused_when_joining()
    {
        var s = await NewSetupAsync();
        var file = Mp4(Piece);
        var id = (await CreateAsync(s, file.Length)).GetProperty("id").GetGuid();
        await SendAsync(s.Teacher, id, 0, file);
        await s.World.WithDbAsync(s.Tenant.Slug, async db => { var course = await db.Courses.SingleAsync(item => item.Id == s.Course.Id); course.Status = Lms.Api.Domain.Courses.CourseStatus.Archived; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.BadRequest, (await JoinAsync(s.Teacher, id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Teacher.Client.GetAsync($"{Uploads}/{id}")).StatusCode);
    }
}
