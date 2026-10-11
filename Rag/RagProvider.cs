namespace BimSAgentApp.Rag;

public static class RagProvider
{
    public static bool IsLocal => Environment.GetEnvironmentVariable("BIMS_LLM_PROVIDER")?.Trim().ToLowerInvariant() switch
    {
        null or "" or "openai" => false,
        "ollama" => true,
        _ => throw new ArgumentException("BIMS_LLM_PROVIDER: допустимы openai и ollama.")
    };
    // Even an injected root keeps local data in a child directory, never beside cloud vectors.
    public static string DirectoryFor(string? root, bool local) => local
        ? Path.Combine(root ?? Environment.GetEnvironmentVariable("BIMS_LOCAL_RAG_DIRECTORY") ?? Path.GetFullPath("rag-data"), "ollama-nomic-v1.5")
        : root ?? RagDefaults.DataDirectory;
    public static HttpClient CreateHttp(bool local) => new(new HttpClientHandler
        { AllowAutoRedirect = false, UseProxy = !local }) { Timeout = TimeSpan.FromMinutes(local ? 5 : 2) };
}
