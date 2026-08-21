namespace FactLineage.Cli.Infrastructure;

public sealed class EmbeddingModelStore(string modelDirectory)
{
    private const string ModelUrl = "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main/onnx/model.onnx";
    private const string TokenizerUrl = "https://huggingface.co/intfloat/multilingual-e5-small/resolve/main/sentencepiece.bpe.model";

    public string ModelDirectory { get; } = modelDirectory;

    public bool IsReady => File.Exists(Path.Combine(ModelDirectory, "model.onnx")) && File.Exists(Path.Combine(ModelDirectory, "sentencepiece.bpe.model"));

    public void Download()
    {
        Directory.CreateDirectory(ModelDirectory);
        DownloadFile(ModelUrl, Path.Combine(ModelDirectory, "model.onnx"));
        DownloadFile(TokenizerUrl, Path.Combine(ModelDirectory, "sentencepiece.bpe.model"));
    }

    private static void DownloadFile(string url, string destination)
    {
        if (File.Exists(destination))
        {
            return;
        }

        var temporaryPath = destination + ".download";
        try
        {
            using var client = new HttpClient();
            using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using (var source = response.Content.ReadAsStream())
            using (var target = File.Create(temporaryPath))
            {
                source.CopyTo(target);
            }

            File.Move(temporaryPath, destination);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}