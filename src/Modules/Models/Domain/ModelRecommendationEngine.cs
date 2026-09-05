namespace LocalAgentPlatform.Modules.Models.Domain;

/// <summary>
/// Pure recommendation logic: given available RAM and a set of candidate models,
/// decide which is a safe default and which are merely "possible but slower".
/// No I/O, no infrastructure dependency — easy to unit test in isolation.
/// </summary>
public static class ModelRecommendationEngine
{
    /// <summary>
    /// Rule of thumb used across local-inference tooling: a quantized model's resident
    /// memory footprint is roughly its on-disk (file) size, plus headroom for KV cache
    /// and OS/runtime overhead. We require the file size to leave at least this fraction
    /// of available RAM free after loading.
    /// </summary>
    private const double SafetyHeadroomFactor = 1.35;

    public static ModelRecommendationResult Recommend(
        long? availableRamBytes,
        IReadOnlyList<CandidateModel> candidates)
    {
        if (candidates.Count == 0)
        {
            return new ModelRecommendationResult(null, "No models registered.", candidates
                .Select(c => new CandidateVerdict(c.Id, ModelFitness.Unknown, "No candidates available."))
                .ToList());
        }

        var verdicts = new List<CandidateVerdict>();
        CandidateModel? recommended = null;

        // Prefer the largest model that still comfortably fits, since bigger generally
        // means stronger reasoning/coding capability at a given quantization level.
        foreach (var c in candidates.OrderByDescending(c => c.EstimatedRamBytes ?? 0))
        {
            if (availableRamBytes is null || c.EstimatedRamBytes is null)
            {
                verdicts.Add(new CandidateVerdict(c.Id, ModelFitness.Unknown,
                    "Cannot evaluate — RAM usage or availability unknown on this host."));
                continue;
            }

            var requiredBytes = (long)(c.EstimatedRamBytes.Value * SafetyHeadroomFactor);

            if (requiredBytes <= availableRamBytes.Value)
            {
                if (recommended is null)
                {
                    recommended = c;
                    verdicts.Add(new CandidateVerdict(c.Id, ModelFitness.Recommended,
                        "Fits comfortably within available RAM."));
                }
                else
                {
                    verdicts.Add(new CandidateVerdict(c.Id, ModelFitness.Fits,
                        "Also fits, but a larger recommended model was preferred."));
                }
            }
            else
            {
                var fitsWithoutHeadroom = c.EstimatedRamBytes.Value <= availableRamBytes.Value;
                verdicts.Add(new CandidateVerdict(
                    c.Id,
                    fitsWithoutHeadroom ? ModelFitness.PossibleButSlower : ModelFitness.TooLarge,
                    fitsWithoutHeadroom
                        ? "Possible but slower — leaves little headroom for KV cache/other processes."
                        : "Estimated footprint exceeds available RAM; likely to fail or swap heavily."));
            }
        }

        var message = recommended is not null
            ? $"Recommended: {recommended.Id}"
            : "No candidate fits safely within available RAM; showing best-effort options.";

        return new ModelRecommendationResult(recommended?.Id, message, verdicts);
    }
}

public sealed record CandidateModel(string Id, long? EstimatedRamBytes);

public enum ModelFitness { Recommended, Fits, PossibleButSlower, TooLarge, Unknown }

public sealed record CandidateVerdict(string ModelId, ModelFitness Fitness, string Reason);

public sealed record ModelRecommendationResult(
    string? RecommendedModelId,
    string Message,
    IReadOnlyList<CandidateVerdict> Verdicts);
