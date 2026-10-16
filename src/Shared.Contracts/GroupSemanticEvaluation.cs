using System.Diagnostics.CodeAnalysis;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Shared.Contracts;

public enum GroupSemanticEligibility { ModelEligible = 1, HostOnly = 2 }
public enum GroupSemanticOutcome { ValidOutput = 1, RejectedOutput = 2, Unscorable = 3, Failed = 4 }
public enum GroupSemanticAdjudication { Complete = 1, Unavailable = 2 }
public enum GroupSemanticCriticalInspection { Complete = 1, Unavailable = 2 }
public enum GroupSemanticRefusal
{
    None = 0,
    HostValidation = 1,
    ParserValidation = 2,
    InvalidDisposition = 3,
    IncompleteAdjudication = 4,
    UnknownCardinality = 5,
    Deadline = 6,
    Cancellation = 7,
    ExecutionFailure = 8
}
public enum GroupSemanticCriticalCategory
{
    CrossScope = 1,
    PrivateHistoryDisclosure = 2,
    InventedAssignee = 3,
    InventedSla = 4,
    InventedItResolution = 5,
    InventedDestination = 6,
    InventedPermission = 7,
    InventedErpExecution = 8
}

public readonly record struct GroupSemanticMatchEdge(int PredictionIndex, Guid GoldTopicId);
public readonly record struct GroupSemanticCriticalFinding(GroupSemanticCriticalCategory Category, int Count);

/// <summary>Opaque pre-output labels only; this carrier cannot authenticate eligibility or label provenance.</summary>
public sealed class GroupSemanticEvaluationCase
{
    public GroupSemanticEvaluationCase(Guid caseId, GroupSemanticEligibility eligibility,
        IReadOnlyList<Guid> goldTopicIds)
    {
        GroupSemanticEvaluation.Require(caseId != Guid.Empty && Enum.IsDefined(eligibility));
        var gold = GroupSemanticEvaluation.Copy(goldTopicIds, GroupSemanticEvaluation.MaximumGoldTopicsPerCase);
        GroupSemanticEvaluation.Require(gold.All(x => x != Guid.Empty) && gold.Distinct().Count() == gold.Length &&
            (eligibility != GroupSemanticEligibility.HostOnly || gold.Length == 0));
        CaseId = caseId;
        Eligibility = eligibility;
        GoldTopicIds = Array.AsReadOnly(gold);
    }

    public Guid CaseId { get; }
    public GroupSemanticEligibility Eligibility { get; }
    public IReadOnlyList<Guid> GoldTopicIds { get; }
}

/// <summary>Frozen case set and scope identity; no source text, model identity or release authority.</summary>
public sealed class GroupSemanticEvaluationCorpus
{
    public GroupSemanticEvaluationCorpus(GroupScope scope, Guid corpusId,
        IReadOnlyList<GroupSemanticEvaluationCase> cases)
    {
        GroupSemanticEvaluation.RequireScope(scope);
        GroupSemanticEvaluation.Require(corpusId != Guid.Empty);
        var copy = GroupSemanticEvaluation.Copy(cases, GroupSemanticEvaluation.MaximumCases);
        GroupSemanticEvaluation.Require(copy.Length != 0 && copy.All(x => x is not null) &&
            copy.Select(x => x.CaseId).Distinct().Count() == copy.Length);
        Scope = scope;
        CorpusId = corpusId;
        Cases = Array.AsReadOnly(copy);
    }

    public GroupScope Scope { get; }
    public Guid CorpusId { get; }
    public IReadOnlyList<GroupSemanticEvaluationCase> Cases { get; }
}

/// <summary>
/// One attempted eligible window. Complete means the caller supplied the entire adjudicated edge set,
/// including the decision that omitted edges are non-matches; this assertion is not independent review.
/// Raw cardinality counts all predictions before host rejection, never only a parser-accepted subset.
/// Critical findings come from separate raw inspection, including rejected outputs.
/// </summary>
public sealed class GroupSemanticEvaluationObservation
{
    public GroupSemanticEvaluationObservation(GroupScope scope, Guid corpusId, Guid caseId,
        GroupSemanticOutcome outcome, GroupSemanticRefusal refusal, int? rawPredictionCount,
        GroupSemanticAdjudication adjudication, IReadOnlyList<GroupSemanticMatchEdge> edges,
        GroupSemanticCriticalInspection criticalInspection, IReadOnlyList<GroupSemanticCriticalFinding> criticalFindings)
    {
        GroupSemanticEvaluation.RequireScope(scope);
        GroupSemanticEvaluation.Require(corpusId != Guid.Empty && caseId != Guid.Empty &&
            Enum.IsDefined(outcome) && Enum.IsDefined(refusal) && Enum.IsDefined(adjudication) &&
            Enum.IsDefined(criticalInspection) &&
            (rawPredictionCount is null || rawPredictionCount is >= 0 and <= GroupSemanticEvaluation.MaximumRawPredictionsPerCase));
        var edgeCopy = GroupSemanticEvaluation.Copy(edges, GroupSemanticEvaluation.MaximumEdgesPerCase);
        var criticalCopy = GroupSemanticEvaluation.Copy(criticalFindings, GroupSemanticEvaluation.MaximumCriticalCategories);
        GroupSemanticEvaluation.Require(edgeCopy.Distinct().Count() == edgeCopy.Length &&
            edgeCopy.All(x => x.GoldTopicId != Guid.Empty && x.PredictionIndex >= 0 && x.PredictionIndex < rawPredictionCount) &&
            criticalCopy.All(x => Enum.IsDefined(x.Category) && x.Count is > 0 and <= GroupSemanticEvaluation.MaximumCriticalFindingsPerCategory) &&
            criticalCopy.Select(x => x.Category).Distinct().Count() == criticalCopy.Length &&
            (criticalInspection == GroupSemanticCriticalInspection.Complete || criticalCopy.Length == 0));
        var reasonFits = outcome switch
        {
            GroupSemanticOutcome.ValidOutput => refusal == GroupSemanticRefusal.None &&
                rawPredictionCount is >= 0 and <= GroupSemanticEvaluation.MaximumAcceptedPredictionsPerCase,
            GroupSemanticOutcome.RejectedOutput => refusal is GroupSemanticRefusal.HostValidation or
                GroupSemanticRefusal.ParserValidation or GroupSemanticRefusal.InvalidDisposition,
            GroupSemanticOutcome.Unscorable => refusal is GroupSemanticRefusal.IncompleteAdjudication or GroupSemanticRefusal.UnknownCardinality,
            GroupSemanticOutcome.Failed => refusal is GroupSemanticRefusal.Deadline or GroupSemanticRefusal.Cancellation or GroupSemanticRefusal.ExecutionFailure,
            _ => false
        };
        GroupSemanticEvaluation.Require(reasonFits &&
            (outcome == GroupSemanticOutcome.ValidOutput || adjudication == GroupSemanticAdjudication.Unavailable) &&
            (outcome == GroupSemanticOutcome.ValidOutput && adjudication == GroupSemanticAdjudication.Complete || edgeCopy.Length == 0) &&
            (refusal != GroupSemanticRefusal.UnknownCardinality || rawPredictionCount is null));
        Scope = scope;
        CorpusId = corpusId;
        CaseId = caseId;
        Outcome = outcome;
        Refusal = refusal;
        RawPredictionCount = rawPredictionCount;
        Adjudication = adjudication;
        Edges = Array.AsReadOnly(edgeCopy);
        CriticalInspection = criticalInspection;
        CriticalFindings = Array.AsReadOnly(criticalCopy);
    }

    public GroupScope Scope { get; }
    public Guid CorpusId { get; }
    public Guid CaseId { get; }
    public GroupSemanticOutcome Outcome { get; }
    public GroupSemanticRefusal Refusal { get; }
    public int? RawPredictionCount { get; }
    public GroupSemanticAdjudication Adjudication { get; }
    public IReadOnlyList<GroupSemanticMatchEdge> Edges { get; }
    public GroupSemanticCriticalInspection CriticalInspection { get; }
    public IReadOnlyList<GroupSemanticCriticalFinding> CriticalFindings { get; }
}

/// <summary>
/// Structural arithmetic from supplied labels/observations only. Nullable prediction totals retain
/// unknown cardinality; KnownRawPredictions/KnownFalsePositives are partial counts, not denominators.
/// No status, metric or caller assertion can qualify a model, corpus, route or release.
/// </summary>
public sealed record GroupSemanticEvaluationStatistics
{
    internal GroupSemanticEvaluationStatistics() { }
    public int EligibleWindows { get; internal init; }
    public int HostOnlyWindows { get; internal init; }
    public int AttemptedWindows { get; internal init; }
    public int MissingWindows { get; internal init; }
    public int RejectedWindows { get; internal init; }
    public int UnscorableWindows { get; internal init; }
    public int FailedWindows { get; internal init; }
    public int FailedNoWorkWindows { get; internal init; }
    public int PassedNoWorkWindows { get; internal init; }
    public int MissingNoWorkWindows { get; internal init; }
    public int GoldTopics { get; internal init; }
    public int TruePositives { get; internal init; }
    public int FalseNegatives { get; internal init; }
    public int KnownRawPredictions { get; internal init; }
    public int KnownFalsePositives { get; internal init; }
    public int UnknownCardinalityWindows { get; internal init; }
    public int? RawPredictions { get; internal init; }
    public int? FalsePositives { get; internal init; }
    public int CriticalWindows { get; internal init; }
    public int CriticalFindings { get; internal init; }
    public int UninspectedCriticalWindows { get; internal init; }
    public double? Precision { get; internal init; }
    public double? Recall { get; internal init; }
    public bool SuppliedAccountingComplete { get; internal init; }
    public bool HasBlockingWindowFailures { get; internal init; }
    public bool ReleaseQualified => false;
    public string Status => "UNQUALIFIED_SUPPLIED_SEMANTIC_ACCOUNTING_ONLY";
}

/// <summary>Pure bounded reducer. It performs no parsing, semantic judging, provenance validation or effects.</summary>
public static class GroupSemanticEvaluation
{
    public const int MaximumCases = 1000;
    public const int MaximumGoldTopicsPerCase = 20;
    public const int MaximumAcceptedPredictionsPerCase = 20;
    public const int MaximumRawPredictionsPerCase = 100;
    public const int MaximumEdgesPerCase = MaximumGoldTopicsPerCase * MaximumRawPredictionsPerCase;
    public const int MaximumCriticalCategories = 8;
    public const int MaximumCriticalFindingsPerCategory = 100;
    public const string InvalidInputReason = "Group semantic evaluation input is invalid.";

    public static GroupSemanticEvaluationStatistics Reduce(GroupSemanticEvaluationCorpus corpus,
        IReadOnlyList<GroupSemanticEvaluationObservation> observations)
    {
        Require(corpus is not null);
        var supplied = Copy(observations, MaximumCases);
        var cases = corpus.Cases.ToDictionary(x => x.CaseId);
        var byCase = new Dictionary<Guid, GroupSemanticEvaluationObservation>();
        foreach (var observation in supplied)
        {
            Require(observation is not null && observation.Scope == corpus.Scope && observation.CorpusId == corpus.CorpusId &&
                cases.TryGetValue(observation.CaseId, out var testCase) && testCase.Eligibility == GroupSemanticEligibility.ModelEligible);
            Require(byCase.TryAdd(observation.CaseId, observation));
            var gold = cases[observation.CaseId].GoldTopicIds.ToHashSet();
            Require(observation.Edges.All(x => gold.Contains(x.GoldTopicId)));
        }

        var eligible = 0; var hostOnly = 0; var missing = 0; var rejected = 0; var unscorable = 0; var failed = 0;
        var failedNoWork = 0; var passedNoWork = 0; var missingNoWork = 0; var goldTotal = 0; var tp = 0;
        var raw = 0; var unknown = 0; var criticalWindows = 0; var criticalTotal = 0; var uninspected = 0;
        foreach (var testCase in corpus.Cases)
        {
            if (testCase.Eligibility == GroupSemanticEligibility.HostOnly) { hostOnly++; continue; }
            eligible++;
            goldTotal += testCase.GoldTopicIds.Count;
            if (!byCase.TryGetValue(testCase.CaseId, out var observation))
            {
                missing++;
                if (testCase.GoldTopicIds.Count == 0) missingNoWork++;
                continue;
            }
            if (observation.RawPredictionCount is int count) raw += count;
            else unknown++;
            if (observation.CriticalInspection == GroupSemanticCriticalInspection.Unavailable) uninspected++;
            if (observation.CriticalFindings.Count != 0) criticalWindows++;
            criticalTotal += observation.CriticalFindings.Sum(x => x.Count);
            if (observation.Outcome == GroupSemanticOutcome.RejectedOutput) rejected++;
            if (observation.Outcome == GroupSemanticOutcome.Failed) failed++;
            var unresolved = observation.Outcome == GroupSemanticOutcome.Unscorable ||
                observation.Outcome == GroupSemanticOutcome.ValidOutput && observation.Adjudication != GroupSemanticAdjudication.Complete;
            if (unresolved || observation.RawPredictionCount is null) unscorable++;
            var valid = observation.Outcome == GroupSemanticOutcome.ValidOutput && !unresolved;
            if (valid) tp += MaximumMatching(testCase.GoldTopicIds, observation.Edges, observation.RawPredictionCount!.Value);
            if (testCase.GoldTopicIds.Count == 0)
            {
                if (valid && observation.RawPredictionCount == 0 && observation.CriticalInspection == GroupSemanticCriticalInspection.Complete &&
                    observation.CriticalFindings.Count == 0) passedNoWork++;
                else failedNoWork++;
            }
        }
        var blocking = missing != 0 || rejected != 0 || unscorable != 0 || failed != 0 || failedNoWork != 0 ||
            criticalWindows != 0 || uninspected != 0;
        // Rejected/failed windows have known zero matches. Unresolved adjudication cannot supply definitive metrics.
        return new GroupSemanticEvaluationStatistics
        {
            EligibleWindows = eligible,
            HostOnlyWindows = hostOnly,
            AttemptedWindows = byCase.Count,
            MissingWindows = missing,
            RejectedWindows = rejected,
            UnscorableWindows = unscorable,
            FailedWindows = failed,
            FailedNoWorkWindows = failedNoWork,
            PassedNoWorkWindows = passedNoWork,
            MissingNoWorkWindows = missingNoWork,
            GoldTopics = goldTotal,
            TruePositives = tp,
            FalseNegatives = goldTotal - tp,
            KnownRawPredictions = raw,
            KnownFalsePositives = raw - tp,
            UnknownCardinalityWindows = unknown,
            RawPredictions = unknown == 0 ? raw : null,
            FalsePositives = unknown == 0 ? raw - tp : null,
            CriticalWindows = criticalWindows,
            CriticalFindings = criticalTotal,
            UninspectedCriticalWindows = uninspected,
            Precision = unknown == 0 && unscorable == 0 && raw != 0 ? (double)tp / raw : null,
            Recall = unscorable == 0 && goldTotal != 0 ? (double)tp / goldTotal : null,
            SuppliedAccountingComplete = missing == 0 && unscorable == 0 && unknown == 0 && uninspected == 0,
            HasBlockingWindowFailures = blocking
        };
    }

    private static int MaximumMatching(IReadOnlyList<Guid> gold, IReadOnlyList<GroupSemanticMatchEdge> edges, int predictions)
    {
        var goldIndex = gold.Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        var adjacency = Enumerable.Range(0, predictions).Select(_ => new List<int>()).ToArray();
        foreach (var edge in edges) adjacency[edge.PredictionIndex].Add(goldIndex[edge.GoldTopicId]);
        var matchedPrediction = Enumerable.Repeat(-1, gold.Count).ToArray();
        var matched = 0;
        for (var prediction = 0; prediction < predictions; prediction++)
            if (Augment(prediction, new bool[gold.Count])) matched++;
        return matched;

        bool Augment(int prediction, bool[] seen)
        {
            foreach (var topic in adjacency[prediction])
            {
                if (seen[topic]) continue;
                seen[topic] = true;
                if (matchedPrediction[topic] < 0 || Augment(matchedPrediction[topic], seen))
                {
                    matchedPrediction[topic] = prediction;
                    return true;
                }
            }
            return false;
        }
    }

    internal static T[] Copy<T>(IReadOnlyList<T> values, int maximum)
    {
        Require(values is not null);
        var count = values.Count;
        Require(count >= 0 && count <= maximum);
        var copy = new T[count];
        for (var index = 0; index < count; index++) copy[index] = values[index];
        return copy;
    }

    internal static void RequireScope(GroupScope scope) => Require(scope is not null &&
        scope.TenantId != Guid.Empty && scope.CompanyId != Guid.Empty && scope.SourceBindingId != Guid.Empty);

    internal static void Require([DoesNotReturnIf(false)] bool value)
    {
        if (!value) throw new InvalidOperationException(InvalidInputReason);
    }
}
