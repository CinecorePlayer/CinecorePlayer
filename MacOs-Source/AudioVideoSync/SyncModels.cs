using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace Cinecore.AudioVideoSync;

internal enum ConfidenceLevel
{
    Low,
    Medium,
    High
}

internal sealed record WindowEvidence(
    double StartSeconds,
    int DetectedTargetDelayMs,
    double Correlation,
    double PeakProminence,
    bool SupportsConsensus);

internal sealed record SyncAnalysisResult
{
    public required string Input { get; init; }
    public required int ReferenceAudioOrdinal { get; init; }
    public required int TargetAudioOrdinal { get; init; }
    public required int DetectedTargetDelayMs { get; init; }
    public required int RecommendedCorrectionMs { get; init; }
    public required double Confidence { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ConfidenceLevel ConfidenceLevel { get; init; }
    public required int SupportingWindows { get; init; }
    public required int AnalyzedWindows { get; init; }
    public required double AnalysisStartSeconds { get; init; }
    public required double AnalysisDurationSeconds { get; init; }
    public required string Method { get; init; }
    public required IReadOnlyList<WindowEvidence> Windows { get; init; }
    public string? AppliedOutput { get; init; }
}

internal sealed record AnalysisOptions(
    string Input,
    int ReferenceAudioOrdinal,
    int TargetAudioOrdinal,
    double StartSeconds,
    double DurationSeconds,
    double MaximumOffsetSeconds,
    string? FfmpegPath,
    string? JsonOutput,
    string? ApplyOutput,
    string? MkvMergePath,
    bool ForceLowConfidence);
