using FactLineage.Cli.Application;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace FactLineage.Cli.Infrastructure;

public sealed class OnnxEmbeddingProvider(string modelDirectory) : IEmbeddingProvider, IDisposable
{
    private const int MaxTokens = 512;
    private const int BeginTokenId = 0;
    private const int EndTokenId = 2;
    private readonly string _modelPath = Path.Combine(modelDirectory, "model.onnx");
    private readonly string _tokenizerPath = Path.Combine(modelDirectory, "sentencepiece.bpe.model");
    private InferenceSession? _session;
    private SentencePieceTokenizer? _tokenizer;

    public string Model => "multilingual-e5-small:384";

    public bool IsAvailable => File.Exists(_modelPath) && File.Exists(_tokenizerPath);

    public Embedding? Create(string text, EmbeddingKind kind)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        _session ??= new InferenceSession(_modelPath);
        _tokenizer ??= CreateTokenizer();
        var tokenIds = Encode(kind == EmbeddingKind.Query ? $"query: {text}" : $"passage: {text}");
        var inputIds = new DenseTensor<long>(new[] { 1, tokenIds.Count });
        var attentionMask = new DenseTensor<long>(new[] { 1, tokenIds.Count });
        var tokenTypeIds = new DenseTensor<long>(new[] { 1, tokenIds.Count });
        for (var index = 0; index < tokenIds.Count; index++)
        {
            inputIds[0, index] = tokenIds[index];
            attentionMask[0, index] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMask)
        };
        if (_session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIds));
        }

        using var output = _session.Run(inputs);
        var hiddenStates = output.First().AsTensor<float>();
        var vector = new float[hiddenStates.Dimensions[2]];
        for (var tokenIndex = 0; tokenIndex < tokenIds.Count; tokenIndex++)
        {
            for (var dimension = 0; dimension < vector.Length; dimension++)
            {
                vector[dimension] += hiddenStates[0, tokenIndex, dimension];
            }
        }

        var squaredSum = 0d;
        for (var dimension = 0; dimension < vector.Length; dimension++)
        {
            vector[dimension] /= tokenIds.Count;
            squaredSum += vector[dimension] * vector[dimension];
        }

        var norm = Math.Sqrt(squaredSum);
        if (norm > 0)
        {
            for (var dimension = 0; dimension < vector.Length; dimension++)
            {
                vector[dimension] = (float)(vector[dimension] / norm);
            }
        }

        return new Embedding(Model, vector);
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    private SentencePieceTokenizer CreateTokenizer()
    {
        using var stream = File.OpenRead(_tokenizerPath);
        return SentencePieceTokenizer.Create(stream, false, false, new Dictionary<string, int>());
    }

    private IReadOnlyList<int> Encode(string text)
    {
        var content = _tokenizer!.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false)
            .Take(MaxTokens - 2)
            .ToList();
        content.Insert(0, BeginTokenId);
        content.Add(EndTokenId);
        return content;
    }
}