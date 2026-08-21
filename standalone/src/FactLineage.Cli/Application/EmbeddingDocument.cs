using System.Text.Json;
using FactLineage.Cli.Domain;

namespace FactLineage.Cli.Application;

public static class EmbeddingDocument
{
    public static string Create(string title, string summary, object? details, IReadOnlyList<CodeReference> references) =>
        Create(title, summary, JsonSerializer.Serialize(details), JsonSerializer.Serialize(references));

    public static string Create(string title, string summary, string detailsJson, string referencesJson) => $"""
        Title: {title}
        Summary: {summary}
        Details: {detailsJson}
        Code references: {referencesJson}
        """;
}