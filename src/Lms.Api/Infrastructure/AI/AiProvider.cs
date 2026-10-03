using System.Text.RegularExpressions;
using Lms.Api.Domain.AI;

namespace Lms.Api.Infrastructure.AI;

public sealed record AiContextItem(Guid Id, string Title, string Content);

public sealed record AiGenerationRequest(
    AiFeatureType Feature,
    string Instruction,
    string OutputLanguage,
    string CourseTitle,
    IReadOnlyList<AiContextItem> Sources);

public sealed record AiCitationResult(Guid SourceId, string SourceTitle, string? Locator, string? Excerpt);

public sealed record AiGenerationResult(
    string Provider,
    string Model,
    string Title,
    string Content,
    string? StructuredJson,
    IReadOnlyList<AiCitationResult> Citations);

public interface IAiProvider
{
    Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Safe local provider used for development and automated tests. A hosted model can be
/// added behind IAiProvider without changing the job, review, or tenant boundaries.
/// </summary>
public sealed class LocalAiProvider : IAiProvider
{
    public Task<AiGenerationResult> GenerateAsync(AiGenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sources = request.Sources.Take(8).ToArray();
        var sourceText = string.Join(" ", sources.Select(item => item.Content)).Trim();
        var citations = sources.Select(item => new AiCitationResult(
            item.Id, item.Title, "published-course-content", Truncate(item.Content, 280))).ToArray();
        var title = request.Feature switch
        {
            AiFeatureType.LessonSummary => $"Summary: {request.CourseTitle}",
            AiFeatureType.QuestionDraft => $"Question draft: {request.CourseTitle}",
            AiFeatureType.FlashcardDraft => $"Flashcards: {request.CourseTitle}",
            AiFeatureType.TranslationDraft => $"Translation draft ({request.OutputLanguage}): {request.CourseTitle}",
            _ => $"Tutor explanation: {request.CourseTitle}"
        };

        var content = request.Feature switch
        {
            AiFeatureType.LessonSummary => BuildSummary(request, sourceText, citations),
            AiFeatureType.QuestionDraft => BuildQuestionDraft(request, sourceText),
            AiFeatureType.FlashcardDraft => BuildFlashcards(request, sources),
            AiFeatureType.TranslationDraft => BuildTranslation(request, sourceText),
            _ => BuildTutorExplanation(request, sourceText, citations)
        };

        return Task.FromResult(new AiGenerationResult(
            "local", "local-template-v1", title, content, null, citations));
    }

    private static string BuildSummary(AiGenerationRequest request, string sourceText, IReadOnlyList<AiCitationResult> citations)
    {
        var summary = string.IsNullOrWhiteSpace(sourceText)
            ? "No published lesson content was available for this draft."
            : Truncate(sourceText, 1200);
        return $"AI DRAFT — human review required\n\n{summary}\n\nFocus requested: {request.Instruction}\nSources: {string.Join(", ", citations.Select((item, index) => $"[{index + 1}] {item.SourceTitle}"))}";
    }

    private static string BuildQuestionDraft(AiGenerationRequest request, string sourceText)
    {
        var basis = string.IsNullOrWhiteSpace(sourceText) ? request.Instruction : Truncate(sourceText, 500);
        return $"AI DRAFT — human review required\n\nQuestion: Based on the published course content, what is the most important idea to remember?\n\nOptions:\n- {Truncate(basis, 160)}\n- A detail unrelated to the lesson\n- An unsupported conclusion\n- None of the above\n\nSuggested answer: Review the published lesson content and confirm the correct option before publishing.\n\nAuthor instruction: {request.Instruction}";
    }

    private static string BuildFlashcards(AiGenerationRequest request, IReadOnlyList<AiContextItem> sources)
    {
        var cards = sources.Take(5).Select((item, index) =>
            $"{index + 1}. Front: What should a learner remember from “{item.Title}”?\n   Back: {Truncate(item.Content, 260)}");
        return $"AI DRAFT — human review required\n\n{string.Join("\n\n", cards)}\n\nAuthor instruction: {request.Instruction}";
    }

    private static string BuildTranslation(AiGenerationRequest request, string sourceText)
        => $"AI DRAFT — human review required\n\nTarget language: {request.OutputLanguage}\nTranslation source:\n{Truncate(sourceText, 1400)}\n\nTranslation instruction: {request.Instruction}\n\nA language reviewer must verify terminology and meaning before publication.";

    private static string BuildTutorExplanation(AiGenerationRequest request, string sourceText, IReadOnlyList<AiCitationResult> citations)
        => $"AI DRAFT — grounded only in published course content\n\n{Truncate(sourceText, 1400)}\n\nLearner question: {request.Instruction}\n\nIf the published content does not answer the question, say so and ask the teacher for clarification. Sources: {string.Join(", ", citations.Select((item, index) => $"[{index + 1}] {item.SourceTitle}"))}";

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd() + "…";
}

public static class AiTextSafety
{
    private static readonly Regex HtmlScript = new("<script\\b[^<]*(?:(?!</script>)<[^<]*)*</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Email = new(@"\\b[\\w.+-]+@[\\w.-]+\\.[A-Za-z]{2,}\\b", RegexOptions.Compiled);
    private static readonly Regex Phone = new(@"(?<!\\w)(?:\\+?\\d[\\d ()-]{7,}\\d)(?!\\w)", RegexOptions.Compiled);

    public static string RedactAndLimit(string? value, int maxLength)
    {
        var text = HtmlScript.Replace(value ?? string.Empty, " ");
        text = Email.Replace(text, "[redacted-email]");
        text = Phone.Replace(text, "[redacted-phone]");
        text = Regex.Replace(text, @"\\s+", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd() + "…";
    }
}
