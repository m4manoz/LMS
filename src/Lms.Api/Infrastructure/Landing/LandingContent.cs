using System.Text.Json;

namespace Lms.Api.Infrastructure.Landing;

public sealed record LandingHero(string Title, string Subtitle, string PrimaryLabel, string PrimaryLink, string SearchPlaceholder);
public sealed record LandingBanner(string Id, string Title, string Text, string ButtonLabel, string Link, string Theme);
public sealed record LandingLink(string Label, string Link);
public sealed record LandingIntents(string Title, List<LandingLink> Items);
/// <param name="Mode">newest, popular, category (uses CategoryId) or manual (uses CourseIds).</param>
public sealed record LandingRow(string Id, string Title, string? Subtitle, string Mode, Guid? CategoryId, List<Guid> CourseIds, int Limit);
public sealed record LandingFeature(string Title, string Text, string Icon);
public sealed record LandingStat(string Value, string Label);
public sealed record LandingTestimonial(string Name, string? Role, string Quote);
public sealed record LandingFaq(string Question, string Answer);
public sealed record LandingFooterLink(string Label, string Url);
public sealed record LandingFooterGroup(string Title, List<LandingFooterLink> Links);

/// <summary>Everything an organization can change on its public page.</summary>
public sealed record LandingContent(
    LandingHero Hero, List<LandingBanner> Banners, LandingIntents Intents, List<LandingRow> Rows,
    bool ShowCategories, string CategoriesTitle,
    string FeaturesTitle, List<LandingFeature> Features, List<LandingStat> Stats,
    string TestimonialsTitle, List<LandingTestimonial> Testimonials,
    string FaqTitle, List<LandingFaq> Faq,
    string FooterAbout, List<LandingFooterGroup> FooterGroups, string Copyright);

public static class LandingDefaults
{
    /// <summary>A sensible page for an organization that has not written its own yet. It says nothing that is not true of every organization.</summary>
    public static LandingContent Create(string organization) => new(
        new LandingHero("Learn without limits", $"Courses and live classes from {organization}. Find a course, apply, and start learning.", "Explore courses", "#courses", "What do you want to learn?"),
        [
            new("welcome", "Apply for a course and learn with live classes", "Pick a course, tell us a little about yourself, and we will invite you to start.", "Browse courses", "#courses", "blue"),
            new("live", "Learn together, live", "Join classes with your teacher, ask questions in chat and watch recordings later.", "How it works", "#faq", "green"),
            new("certificates", "Finish a course, keep the proof", "Complete a course and receive a certificate that you can share.", "See categories", "#categories", "purple"),
        ],
        new("What brings you here today?", [new("Start my career", "#courses"), new("Change my career", "#courses"), new("Grow in my current role", "#courses"), new("Explore topics outside of work", "#categories")]),
        [new("popular", "Most popular", "What other learners are taking", "popular", null, [], 8), new("new", "New and noteworthy", "Recently published", "newest", null, [], 8)],
        true, "Explore categories",
        "Why learn with us",
        [
            new("Live classes", "Join your teacher in a live room, chat, vote in polls and revisit recordings.", "video"),
            new("Learn at your pace", "Lessons, videos and quizzes you can come back to any time.", "clock"),
            new("Certificates", "Complete a course and earn a certificate you can share.", "award"),
            new("Support from teachers", "Ask questions, get feedback on assignments and see your progress.", "users"),
        ],
        [],
        "What learners say", [],
        "Frequently asked questions",
        [
            new("How do I apply for a course?", "Open a course, press Apply, and tell us your name and email. We review your application and send an invitation with a link to create your account and join the course."),
            new("Does it cost anything to apply?", "Applying is free. If a course has a fee, the details are shown on the course."),
            new("How do the live classes work?", "Once you are enrolled, the live classes of your course appear in the course and under Classroom. Press Join class and allow your camera and microphone."),
            new("Can I watch a class later?", "When a teacher records a class, the recording is added to the video library, with search and captions."),
            new("How do I know if I passed?", "Quizzes are graded automatically and teachers grade assignments. Your results and progress are always visible in your account."),
        ],
        $"{organization} helps you learn with courses, live classes and certificates.",
        [
            new("Learn", [new("Courses", "#courses"), new("Categories", "#categories"), new("Questions", "#faq")]),
            new("Account", [new("Log in", "#login"), new("I have an invitation", "#join")]),
        ],
        $"© {DateTime.UtcNow.Year} {organization}");
}

public static class LandingContentRules
{
    public static readonly string[] Themes = ["blue", "green", "purple", "amber", "dark"];
    public static readonly string[] Modes = ["newest", "popular", "category", "manual"];
    public static readonly string[] Icons = ["video", "book", "award", "users", "clock", "shield", "globe", "laptop", "check", "star"];
    public const int MaxJsonCharacters = 120_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize(LandingContent content) => JsonSerializer.Serialize(content, Json);

    /// <summary>The stored content, tidied; or the defaults when nothing usable is stored.</summary>
    public static LandingContent Read(string? json, string organization)
    {
        try
        {
            var stored = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<LandingContent>(json, Json);
            if (stored is null) return LandingDefaults.Create(organization);
            return Normalize(stored, out _) ?? LandingDefaults.Create(organization);
        }
        catch (JsonException) { return LandingDefaults.Create(organization); }
    }

    /// <summary>A link that is safe to put in a button: this page's own sections (#...), a path in this app (/...), a mail link or an https address.</summary>
    public static bool IsSafeLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.Length > 500 || text.Any(char.IsControl)) return false;
        if (text.StartsWith('#')) return text.Length > 1;
        if (text.StartsWith('/')) return !text.StartsWith("//") && !text.Contains('\\');
        if (text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return text.Length > 7;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host);
    }

    private static string Clean(string? value, int max) { var text = (value ?? string.Empty).Trim(); return text.Length > max ? text[..max] : text; }
    private static string? CleanOrNull(string? value, int max) { var text = Clean(value, max); return text.Length == 0 ? null : text; }

    /// <summary>Tidies the content (trims, drops empty items, enforces limits) or returns null with the first problem in <paramref name="error"/>.</summary>
    public static LandingContent? Normalize(LandingContent input, out string? error)
    {
        error = null;
        var hero = input.Hero ?? LandingDefaults.Create("").Hero;
        if (Clean(hero.Title, 200).Length == 0) { error = "The main heading cannot be empty."; return null; }
        if (hero.PrimaryLabel is { Length: > 0 } && !IsSafeLink(hero.PrimaryLink)) { error = "The main button's link must start with #, / or https://."; return null; }

        var banners = new List<LandingBanner>();
        foreach (var banner in input.Banners ?? [])
        {
            if (Clean(banner.Title, 200).Length == 0) continue;
            if (Clean(banner.ButtonLabel, 60).Length > 0 && !IsSafeLink(banner.Link)) { error = $"The link of the banner “{Clean(banner.Title, 40)}” must start with #, / or https://."; return null; }
            banners.Add(new(Clean(banner.Id, 40) is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N")[..8], Clean(banner.Title, 200), Clean(banner.Text, 400), Clean(banner.ButtonLabel, 60), Clean(banner.ButtonLabel, 60).Length > 0 ? banner.Link.Trim() : "", Themes.Contains(banner.Theme) ? banner.Theme : "blue"));
        }
        if (banners.Count > 6) { error = "A page can have at most 6 banners."; return null; }

        var intentItems = new List<LandingLink>();
        foreach (var item in input.Intents?.Items ?? [])
        {
            if (Clean(item.Label, 80).Length == 0) continue;
            if (!IsSafeLink(item.Link)) { error = $"The link of “{Clean(item.Label, 40)}” must start with #, / or https://."; return null; }
            intentItems.Add(new(Clean(item.Label, 80), item.Link.Trim()));
        }
        if (intentItems.Count > 8) { error = "There can be at most 8 choices under “What brings you here”."; return null; }

        var rows = new List<LandingRow>();
        foreach (var row in input.Rows ?? [])
        {
            if (Clean(row.Title, 120).Length == 0) continue;
            var mode = Modes.Contains(row.Mode) ? row.Mode : "newest";
            if (mode == "category" && row.CategoryId is null) { error = $"Choose a category for the row “{Clean(row.Title, 40)}”."; return null; }
            var ids = (row.CourseIds ?? []).Distinct().Take(24).ToList();
            if (mode == "manual" && ids.Count == 0) { error = $"Choose at least one course for the row “{Clean(row.Title, 40)}”."; return null; }
            rows.Add(new(Clean(row.Id, 40) is { Length: > 0 } id ? id : Guid.NewGuid().ToString("N")[..8], Clean(row.Title, 120), CleanOrNull(row.Subtitle, 200), mode, mode == "category" ? row.CategoryId : null, mode == "manual" ? ids : [], Math.Clamp(row.Limit <= 0 ? 8 : row.Limit, 1, 24)));
        }
        if (rows.Count > 8) { error = "A page can have at most 8 course rows."; return null; }

        var features = (input.Features ?? []).Where(item => Clean(item.Title, 100).Length > 0).Select(item => new LandingFeature(Clean(item.Title, 100), Clean(item.Text, 300), Icons.Contains(item.Icon) ? item.Icon : "star")).ToList();
        if (features.Count > 8) { error = "There can be at most 8 reasons to learn with you."; return null; }
        var stats = (input.Stats ?? []).Where(item => Clean(item.Value, 20).Length > 0 && Clean(item.Label, 80).Length > 0).Select(item => new LandingStat(Clean(item.Value, 20), Clean(item.Label, 80))).ToList();
        if (stats.Count > 4) { error = "There can be at most 4 numbers."; return null; }
        var testimonials = (input.Testimonials ?? []).Where(item => Clean(item.Quote, 600).Length > 0 && Clean(item.Name, 80).Length > 0).Select(item => new LandingTestimonial(Clean(item.Name, 80), CleanOrNull(item.Role, 120), Clean(item.Quote, 600))).ToList();
        if (testimonials.Count > 8) { error = "There can be at most 8 testimonials."; return null; }
        var faq = (input.Faq ?? []).Where(item => Clean(item.Question, 200).Length > 0 && Clean(item.Answer, 1500).Length > 0).Select(item => new LandingFaq(Clean(item.Question, 200), Clean(item.Answer, 1500))).ToList();
        if (faq.Count > 12) { error = "There can be at most 12 questions."; return null; }

        var groups = new List<LandingFooterGroup>();
        foreach (var group in input.FooterGroups ?? [])
        {
            if (Clean(group.Title, 80).Length == 0) continue;
            var links = new List<LandingFooterLink>();
            foreach (var link in group.Links ?? [])
            {
                if (Clean(link.Label, 80).Length == 0) continue;
                if (!IsSafeLink(link.Url)) { error = $"The link “{Clean(link.Label, 40)}” in the footer must start with #, / or https://."; return null; }
                links.Add(new(Clean(link.Label, 80), link.Url.Trim()));
            }
            if (links.Count > 10) { error = "A footer column can have at most 10 links."; return null; }
            groups.Add(new(Clean(group.Title, 80), links));
        }
        if (groups.Count > 5) { error = "The footer can have at most 5 columns."; return null; }

        var content = new LandingContent(
            new(Clean(hero.Title, 200), Clean(hero.Subtitle, 400), Clean(hero.PrimaryLabel, 60), Clean(hero.PrimaryLabel, 60).Length > 0 ? hero.PrimaryLink.Trim() : "", Clean(hero.SearchPlaceholder, 80)),
            banners, new(Clean(input.Intents?.Title, 120), intentItems), rows,
            input.ShowCategories, Clean(input.CategoriesTitle, 120), Clean(input.FeaturesTitle, 120), features, stats,
            Clean(input.TestimonialsTitle, 120), testimonials, Clean(input.FaqTitle, 120), faq,
            Clean(input.FooterAbout, 400), groups, Clean(input.Copyright, 200));
        if (Serialize(content).Length > MaxJsonCharacters) { error = "The page is too large."; return null; }
        return content;
    }
}
