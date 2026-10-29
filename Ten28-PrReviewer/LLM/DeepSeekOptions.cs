namespace Ten28_PrReviewer.LLM;

internal sealed class DeepSeekOptions
{
    public int MaxOutputTokens { get; set; } = 16384;
    public int RequestTimeoutSeconds { get; set; } = 120;
    public int OverallTimeoutSeconds { get; set; } = 480;
    public int MaxRetries { get; set; } = 2;

    internal void Validate()
    {
        if (MaxOutputTokens is < 1 or > 16384 || RequestTimeoutSeconds is < 1 or > 120
            || OverallTimeoutSeconds is < 1 or > 480 || MaxRetries is < 0 or > 2)
            throw new InvalidOperationException("Invalid DeepSeek limits.");
    }
}
