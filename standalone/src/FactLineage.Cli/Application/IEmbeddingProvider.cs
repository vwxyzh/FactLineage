namespace FactLineage.Cli.Application;

public enum EmbeddingKind
{
    Document,
    Query
}

public sealed record Embedding(string Model, float[] Vector);

public interface IEmbeddingProvider
{
    string Model { get; }

    bool IsAvailable { get; }

    Embedding? Create(string text, EmbeddingKind kind);
}

public sealed class DisabledEmbeddingProvider : IEmbeddingProvider
{
    public string Model => "disabled";

    public bool IsAvailable => false;

    public Embedding? Create(string text, EmbeddingKind kind) => null;
}