using System.Collections;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupSemanticEvaluationTests
{
    private static readonly GroupScope Scope = new(Id(1), Id(2), Id(3));
    private static readonly Guid CorpusId = Id(4);
    private static readonly Guid CaseId = Id(5);
    private static readonly Guid GoldA = Id(6);
    private static readonly Guid GoldB = Id(7);

    [Fact]
    public void MaximumCardinality_ReassignsFirstPredictionInsteadOfGreedyCredit()
    {
        var result = Reduce(Case(GoldA, GoldB), Observation(2, [new(0, GoldA), new(0, GoldB), new(1, GoldA)]));
        Assert.Equal(2, result.TruePositives);
        Assert.Equal(0, result.FalsePositives);
        Assert.Equal(0, result.FalseNegatives);
        Assert.Equal(1.0, result.Precision);
        Assert.Equal(1.0, result.Recall);
        Assert.True(result.SuppliedAccountingComplete);
        Assert.False(result.ReleaseQualified);
    }

    [Fact]
    public void MaximumCardinality_AllThreeByThreeGraphsAgreeWithExhaustiveAssignmentAndReversedOrder()
    {
        var gold = new[] { GoldA, GoldB, Id(8) };
        for (var mask = 0; mask < 512; mask++)
        {
            var edges = (from p in Enumerable.Range(0, 3)
                         from g in Enumerable.Range(0, 3)
                         where (mask & (1 << (p * 3 + g))) != 0
                         select new GroupSemanticMatchEdge(p, gold[g])).ToArray();
            var expected = BruteForce(0, []);
            var forward = Reduce(Case(gold), Observation(3, edges));
            var reversed = Reduce(Case(gold.Reverse().ToArray()), Observation(3, edges.Reverse().ToArray()));
            Assert.Equal(expected, forward.TruePositives);
            Assert.Equal(forward, reversed);

            int BruteForce(int prediction, HashSet<Guid> used)
            {
                if (prediction == 3) return 0;
                var best = BruteForce(prediction + 1, used);
                foreach (var edge in edges.Where(x => x.PredictionIndex == prediction && !used.Contains(x.GoldTopicId)))
                {
                    var next = new HashSet<Guid>(used) { edge.GoldTopicId };
                    best = Math.Max(best, 1 + BruteForce(prediction + 1, next));
                }
                return best;
            }
        }
    }

    [Fact]
    public void DuplicateSemanticPredictionsRemainFalsePositivesAndMergedPredictionCannotCountTwice()
    {
        var duplicate = Reduce(Case(GoldA), Observation(2, [new(0, GoldA), new(1, GoldA)]));
        Assert.Equal(1, duplicate.TruePositives);
        Assert.Equal(1, duplicate.FalsePositives);
        var merged = Reduce(Case(GoldA, GoldB), Observation(1, [new(0, GoldA), new(0, GoldB)]));
        Assert.Equal(1, merged.TruePositives);
        Assert.Equal(1, merged.FalseNegatives);
    }

    [Fact]
    public void KnownRejectedRawCountsAreAllFalsePositiveAndEveryGoldRemainsMissed()
    {
        var result = Reduce(Case(GoldA, GoldB), Rejected(3));
        Assert.Equal(0, result.TruePositives);
        Assert.Equal(3, result.FalsePositives);
        Assert.Equal(2, result.FalseNegatives);
        Assert.Equal(0.0, result.Precision);
        Assert.Equal(0.0, result.Recall);
        Assert.Equal(1, result.AttemptedWindows);
        Assert.Equal(1, result.RejectedWindows);
        Assert.True(result.HasBlockingWindowFailures);
    }

    [Fact]
    public void UnknownRejectedRawCardinalityNullsWholeAggregateWithoutDroppingKnownCountsOrGold()
    {
        var other = new GroupSemanticEvaluationCase(Id(9), GroupSemanticEligibility.ModelEligible, [GoldB]);
        var accepted = Observation(2, [new(0, GoldB)], caseId: other.CaseId);
        var result = GroupSemanticEvaluation.Reduce(Corpus(Case(GoldA), other), [Rejected(null), accepted]);
        Assert.Equal(2, result.AttemptedWindows);
        Assert.Equal(1, result.TruePositives);
        Assert.Equal(1, result.FalseNegatives);
        Assert.Equal(2, result.KnownRawPredictions);
        Assert.Equal(1, result.KnownFalsePositives);
        Assert.Equal(1, result.UnknownCardinalityWindows);
        Assert.Null(result.RawPredictions);
        Assert.Null(result.FalsePositives);
        Assert.Null(result.Precision);
        Assert.Null(result.Recall);
        Assert.False(result.SuppliedAccountingComplete);
    }

    [Fact]
    public void MissingEligibleCaseIsExplicitAndRetainsGoldDenominator()
    {
        var result = GroupSemanticEvaluation.Reduce(Corpus(Case(GoldA, GoldB)), []);
        Assert.Equal(1, result.EligibleWindows);
        Assert.Equal(0, result.AttemptedWindows);
        Assert.Equal(1, result.MissingWindows);
        Assert.Equal(2, result.FalseNegatives);
        Assert.Equal(0.0, result.Recall);
        Assert.Null(result.Precision);
        Assert.True(result.HasBlockingWindowFailures);
        Assert.False(result.SuppliedAccountingComplete);
    }

    [Fact]
    public void HostOnlyCasesAreSeparatelyExcludedBeforeOutputAndCannotReceiveAttemptCredit()
    {
        var host = new GroupSemanticEvaluationCase(Id(9), GroupSemanticEligibility.HostOnly, []);
        var result = GroupSemanticEvaluation.Reduce(Corpus(Case(GoldA), host), [Observation(1, [new(0, GoldA)])]);
        Assert.Equal(1, result.HostOnlyWindows);
        Assert.Equal(1, result.EligibleWindows);
        Assert.Equal(1, result.AttemptedWindows);
        Refused(() => GroupSemanticEvaluation.Reduce(Corpus(host), [Observation(0, [], caseId: host.CaseId)]));
        Refused(() => new GroupSemanticEvaluationCase(Id(9), GroupSemanticEligibility.HostOnly, [GoldA]));
    }

    [Fact]
    public void HostOnlyCorpusHasNoWorkMetricOrModelQualification()
    {
        var result = GroupSemanticEvaluation.Reduce(Corpus(new GroupSemanticEvaluationCase(CaseId, GroupSemanticEligibility.HostOnly, [])), []);
        Assert.Equal(0, result.EligibleWindows);
        Assert.Null(result.Precision);
        Assert.Null(result.Recall);
        Assert.False(result.ReleaseQualified);
    }

    [Fact]
    public void CompleteValidZeroWorkPassesOnlyItsWindowAndLeavesBothMetricDenominatorsUndefined()
    {
        var result = Reduce(Case(), Observation(0, []));
        Assert.Equal(1, result.PassedNoWorkWindows);
        Assert.Equal(0, result.FailedNoWorkWindows);
        Assert.Null(result.Precision);
        Assert.Null(result.Recall);
        Assert.False(result.ReleaseQualified);
    }

    [Fact]
    public void ScorableFalsePositiveOnNoWorkIsFailedWindowWithoutPhantomGold()
    {
        var result = Reduce(Case(), Observation(1, []));
        Assert.Equal(1, result.FailedNoWorkWindows);
        Assert.Equal(1, result.FalsePositives);
        Assert.Equal(0, result.GoldTopics);
        Assert.Equal(0, result.FalseNegatives);
        Assert.True(result.HasBlockingWindowFailures);
    }

    [Theory]
    [InlineData(GroupSemanticOutcome.RejectedOutput, GroupSemanticRefusal.InvalidDisposition, 0)]
    [InlineData(GroupSemanticOutcome.RejectedOutput, GroupSemanticRefusal.ParserValidation, -1)]
    [InlineData(GroupSemanticOutcome.Unscorable, GroupSemanticRefusal.IncompleteAdjudication, 0)]
    [InlineData(GroupSemanticOutcome.Failed, GroupSemanticRefusal.Deadline, -1)]
    [InlineData(GroupSemanticOutcome.Failed, GroupSemanticRefusal.Cancellation, 0)]
    [InlineData(GroupSemanticOutcome.Failed, GroupSemanticRefusal.ExecutionFailure, 0)]
    public void EveryRejectedUnscorableFailedNoWorkBlocksEvenWithoutObservablePredictions(
        GroupSemanticOutcome outcome, GroupSemanticRefusal reason, int count)
    {
        var observation = new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, outcome, reason,
            count < 0 ? null : count, GroupSemanticAdjudication.Unavailable, [], GroupSemanticCriticalInspection.Complete, []);
        var result = Reduce(Case(), observation);
        Assert.Equal(1, result.AttemptedWindows);
        Assert.Equal(1, result.FailedNoWorkWindows);
        Assert.Equal(0, result.PassedNoWorkWindows);
        Assert.True(result.HasBlockingWindowFailures);
        Assert.Null(result.Precision);
        Assert.Null(result.Recall);
    }

    [Fact]
    public void MissingNoWorkIsExplicitWithoutPhantomPredictionOrGold()
    {
        var result = GroupSemanticEvaluation.Reduce(Corpus(Case()), []);
        Assert.Equal(1, result.MissingNoWorkWindows);
        Assert.Equal(0, result.FalseNegatives);
        Assert.Equal(0, result.FalsePositives);
        Assert.True(result.HasBlockingWindowFailures);
    }

    [Fact]
    public void UnavailableAdjudicationCannotInferEmptyEdgesAreComplete()
    {
        var result = Reduce(Case(GoldA), Observation(1, [], adjudication: GroupSemanticAdjudication.Unavailable));
        Assert.Equal(0, result.TruePositives);
        Assert.Equal(1, result.KnownFalsePositives);
        Assert.Equal(1, result.FalseNegatives);
        Assert.Equal(1, result.UnscorableWindows);
        Assert.Null(result.Precision);
        Assert.Null(result.Recall);
        Refused(() => Observation(1, [new(0, GoldA)], adjudication: GroupSemanticAdjudication.Unavailable));
    }

    [Fact]
    public void EmptyCompleteEdgeSetIsScorableOrdinaryMismatchNotUnscorable()
    {
        var result = Reduce(Case(GoldA), Observation(1, []));
        Assert.Equal(0, result.UnscorableWindows);
        Assert.Equal(1, result.FalsePositives);
        Assert.Equal(1, result.FalseNegatives);
        Assert.Equal(0.0, result.Precision);
        Assert.Equal(0.0, result.Recall);
        Assert.False(result.HasBlockingWindowFailures);
        Assert.False(result.ReleaseQualified);
    }

    [Fact]
    public void RejectedRawCriticalFindingsAreRetainedAndUninspectedRawCannotPassNoWork()
    {
        var rejected = new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, GroupSemanticOutcome.RejectedOutput,
            GroupSemanticRefusal.ParserValidation, 2, GroupSemanticAdjudication.Unavailable, [],
            GroupSemanticCriticalInspection.Complete, [new(GroupSemanticCriticalCategory.InventedErpExecution, 1)]);
        var result = Reduce(Case(GoldA), rejected);
        Assert.Equal(1, result.CriticalWindows);
        Assert.Equal(1, result.CriticalFindings);
        Assert.Equal(2, result.FalsePositives);
        var uninspected = Reduce(Case(), Observation(0, [], criticalInspection: GroupSemanticCriticalInspection.Unavailable));
        Assert.Equal(1, uninspected.UninspectedCriticalWindows);
        Assert.Equal(1, uninspected.FailedNoWorkWindows);
        Assert.False(uninspected.SuppliedAccountingComplete);
        Assert.True(uninspected.HasBlockingWindowFailures);
    }

    [Fact]
    public void CriticalFindingsAreNotSemanticMatchesAndCannotGrantReleaseWithPerfectArithmetic()
    {
        var observation = Observation(1, [new(0, GoldA)], critical: [new(GroupSemanticCriticalCategory.InventedSla, 2)]);
        var result = Reduce(Case(GoldA), observation);
        Assert.Equal(1.0, result.Precision);
        Assert.Equal(1.0, result.Recall);
        Assert.Equal(2, result.CriticalFindings);
        Assert.True(result.HasBlockingWindowFailures);
        Assert.False(result.ReleaseQualified);
        Assert.Equal("UNQUALIFIED_SUPPLIED_SEMANTIC_ACCOUNTING_ONLY", result.Status);
    }

    [Fact]
    public void ReducerRefusesDuplicateUnknownAndWrongCorpusObservations()
    {
        var corpus = Corpus(Case(GoldA));
        var observation = Observation(1, [new(0, GoldA)]);
        Refused(() => GroupSemanticEvaluation.Reduce(corpus, [observation, observation]));
        Refused(() => GroupSemanticEvaluation.Reduce(corpus, [Observation(0, [], caseId: Id(80))]));
        Refused(() => GroupSemanticEvaluation.Reduce(corpus, [Observation(0, [], corpusId: Id(81))]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryScopeComponentMustMatch(int component)
    {
        var scope = component switch
        {
            0 => Scope with { TenantId = Id(80) },
            1 => Scope with { CompanyId = Id(80) },
            _ => Scope with { SourceBindingId = Id(80) }
        };
        Refused(() => GroupSemanticEvaluation.Reduce(Corpus(Case()), [Observation(0, [], scope: scope)]));
    }

    [Fact]
    public void IdenticalCaseIdInDifferentCorpusDoesNotAuthorizeObservation()
    {
        var observation = Observation(0, []);
        Refused(() => GroupSemanticEvaluation.Reduce(new(Scope, Id(82), [Case()]), [observation]));
    }

    [Fact]
    public void GoldAndCorpusIdentityMustBeUniqueNonemptyAndBounded()
    {
        Refused(() => new GroupSemanticEvaluationCase(Guid.Empty, GroupSemanticEligibility.ModelEligible, []));
        Refused(() => Case(Guid.Empty));
        Refused(() => Case(GoldA, GoldA));
        Refused(() => Case(Enumerable.Range(0, 21).Select(x => Id(x + 100)).ToArray()));
        Refused(() => Corpus());
        Refused(() => Corpus(Case(), Case()));
        Refused(() => new GroupSemanticEvaluationCorpus(Scope, Guid.Empty, [Case()]));
        Refused(() => new GroupSemanticEvaluationCorpus(Scope with { TenantId = Guid.Empty }, CorpusId, [Case()]));
        Refused(() => new GroupSemanticEvaluationCase(CaseId, (GroupSemanticEligibility)99, []));
    }

    [Fact]
    public void EmptyAndOutOfCaseGoldEdgesOrRepeatedExactEdgesRefuse()
    {
        Refused(() => Observation(1, [new(0, Guid.Empty)]));
        Refused(() => Observation(1, [new(-1, GoldA)]));
        Refused(() => Observation(1, [new(1, GoldA)]));
        Refused(() => Observation(1, [new(0, GoldA), new(0, GoldA)]));
        Refused(() => Reduce(Case(GoldA), Observation(1, [new(0, GoldB)])));
        Refused(() => Reduce(Case(), Observation(1, [new(0, GoldA)])));
    }

    [Fact]
    public void RejectedOrUnscorableEdgesCanNeverClaimTopicCredit()
    {
        foreach (var outcome in new[] { GroupSemanticOutcome.RejectedOutput, GroupSemanticOutcome.Unscorable, GroupSemanticOutcome.Failed })
        {
            var refusal = outcome == GroupSemanticOutcome.RejectedOutput ? GroupSemanticRefusal.HostValidation :
                outcome == GroupSemanticOutcome.Unscorable ? GroupSemanticRefusal.IncompleteAdjudication : GroupSemanticRefusal.ExecutionFailure;
            Refused(() => new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, outcome, refusal, 1,
                GroupSemanticAdjudication.Unavailable, [new(0, GoldA)], GroupSemanticCriticalInspection.Complete, []));
        }
    }

    [Fact]
    public void ClosedStatusesAndReasonsRefuseContradictoryOrUnknownStates()
    {
        Refused(() => new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, (GroupSemanticOutcome)90,
            GroupSemanticRefusal.None, 0, GroupSemanticAdjudication.Complete, [], GroupSemanticCriticalInspection.Complete, []));
        Refused(() => new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, GroupSemanticOutcome.ValidOutput,
            (GroupSemanticRefusal)90, 0, GroupSemanticAdjudication.Complete, [], GroupSemanticCriticalInspection.Complete, []));
        Refused(() => Observation(0, [], adjudication: (GroupSemanticAdjudication)90));
        Refused(() => Observation(0, [], criticalInspection: (GroupSemanticCriticalInspection)90));
        Refused(() => new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, GroupSemanticOutcome.ValidOutput,
            GroupSemanticRefusal.Deadline, 0, GroupSemanticAdjudication.Complete, [], GroupSemanticCriticalInspection.Complete, []));
        Refused(() => new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, GroupSemanticOutcome.Failed,
            GroupSemanticRefusal.None, 0, GroupSemanticAdjudication.Unavailable, [], GroupSemanticCriticalInspection.Complete, []));
        Refused(() => new GroupSemanticEvaluationObservation(Scope, CorpusId, CaseId, GroupSemanticOutcome.Unscorable,
            GroupSemanticRefusal.UnknownCardinality, 0, GroupSemanticAdjudication.Unavailable, [], GroupSemanticCriticalInspection.Complete, []));
    }

    [Fact]
    public void RawCountBoundsPreserveRejectedOverflowAndUnknownInsteadOfInventingZero()
    {
        Refused(() => Observation(-1, []));
        Refused(() => Observation(21, []));
        Refused(() => Rejected(101));
        var rejected = Reduce(Case(GoldA), Rejected(100));
        Assert.Equal(100, rejected.FalsePositives);
        Assert.Equal(100, rejected.RawPredictions);
        var unknown = Reduce(Case(), Rejected(null));
        Assert.Null(unknown.FalsePositives);
        Assert.Equal(1, unknown.FailedNoWorkWindows);
    }

    [Fact]
    public void CriticalCategoriesRequireKnownPositiveUniqueBoundedCountsAndCompleteInspection()
    {
        Refused(() => Observation(0, [], critical: [new((GroupSemanticCriticalCategory)90, 1)]));
        Refused(() => Observation(0, [], critical: [new(GroupSemanticCriticalCategory.CrossScope, 0)]));
        Refused(() => Observation(0, [], critical: [new(GroupSemanticCriticalCategory.CrossScope, 101)]));
        Refused(() => Observation(0, [], critical: [new(GroupSemanticCriticalCategory.CrossScope, 1), new(GroupSemanticCriticalCategory.CrossScope, 2)]));
        Refused(() => Observation(0, [], critical: [new(GroupSemanticCriticalCategory.CrossScope, 1)],
            criticalInspection: GroupSemanticCriticalInspection.Unavailable));
    }

    [Fact]
    public void CarriersDefensivelyCopyAllCollectionsAndExposeReadOnlyViews()
    {
        var gold = new[] { GoldA };
        var testCase = Case(gold);
        var cases = new[] { testCase };
        var corpus = Corpus(cases);
        var edges = new[] { new GroupSemanticMatchEdge(0, GoldA) };
        var critical = new[] { new GroupSemanticCriticalFinding(GroupSemanticCriticalCategory.CrossScope, 1) };
        var observation = Observation(1, edges, critical: critical);
        gold[0] = GoldB; cases[0] = Case(GoldB); edges[0] = new(0, GoldB); critical[0] = new(GroupSemanticCriticalCategory.InventedSla, 2);
        Assert.Equal(GoldA, testCase.GoldTopicIds.Single());
        Assert.Same(testCase, corpus.Cases.Single());
        Assert.Equal(GoldA, observation.Edges.Single().GoldTopicId);
        Assert.Equal(GroupSemanticCriticalCategory.CrossScope, observation.CriticalFindings.Single().Category);
        Assert.Throws<NotSupportedException>(() => ((IList<Guid>)testCase.GoldTopicIds)[0] = GoldB);
        Assert.Throws<NotSupportedException>(() => ((IList<GroupSemanticMatchEdge>)observation.Edges)[0] = new(0, GoldB));
        Assert.Equal(1, GroupSemanticEvaluation.Reduce(corpus, [observation]).TruePositives);
    }

    [Fact]
    public void OversizedListsAreRejectedBeforeIndexingOrEnumeration()
    {
        Refused(() => new GroupSemanticEvaluationCorpus(Scope, CorpusId, new OversizedList<GroupSemanticEvaluationCase>(1001)));
        Refused(() => GroupSemanticEvaluation.Reduce(Corpus(Case()), new OversizedList<GroupSemanticEvaluationObservation>(1001)));
        Refused(() => new GroupSemanticEvaluationCase(CaseId, GroupSemanticEligibility.ModelEligible, new OversizedList<Guid>(21)));
        Refused(() => Observation(1, new OversizedList<GroupSemanticMatchEdge>(2001)));
        Refused(() => Observation(0, [], critical: new OversizedList<GroupSemanticCriticalFinding>(9)));
    }

    [Fact]
    public void BoundedDenseMatchingAndMaximumCorpusCountsStayExact()
    {
        var gold = Enumerable.Range(100, 20).Select(Id).ToArray();
        var edges = (from p in Enumerable.Range(0, 20) from g in gold select new GroupSemanticMatchEdge(p, g)).ToArray();
        Assert.Equal(20, Reduce(Case(gold), Observation(20, edges)).TruePositives);
        var cases = Enumerable.Range(1000, 1000).Select(x => new GroupSemanticEvaluationCase(Id(x), GroupSemanticEligibility.ModelEligible, [GoldA])).ToArray();
        var observations = cases.Select(x => Rejected(100, caseId: x.CaseId)).ToArray();
        var result = GroupSemanticEvaluation.Reduce(Corpus(cases), observations);
        Assert.Equal(1000, result.AttemptedWindows);
        Assert.Equal(100000, result.FalsePositives);
        Assert.Equal(1000, result.FalseNegatives);
    }

    [Fact]
    public void CaseAndObservationOrderingDoesNotChangeAggregate()
    {
        var other = new GroupSemanticEvaluationCase(Id(90), GroupSemanticEligibility.ModelEligible, [GoldB]);
        var cases = new[] { Case(GoldA), other };
        var observations = new[] { Observation(1, [new(0, GoldA)]), Rejected(2, caseId: other.CaseId) };
        Assert.Equal(GroupSemanticEvaluation.Reduce(Corpus(cases), observations),
            GroupSemanticEvaluation.Reduce(Corpus(cases.Reverse().ToArray()), observations.Reverse().ToArray()));
    }

    [Fact]
    public void NullInputsAreFixedRefusalsAndReportContainsOnlyCountsAndFixedStatus()
    {
        Refused(() => Case(null!));
        Refused(() => Corpus(null!));
        Refused(() => GroupSemanticEvaluation.Reduce(null!, []));
        Refused(() => GroupSemanticEvaluation.Reduce(Corpus(Case()), null!));
        Refused(() => new GroupSemanticEvaluationCorpus(null!, CorpusId, [Case()]));
        Refused(() => new GroupSemanticEvaluationCorpus(Scope, CorpusId, [null!]));
        Refused(() => GroupSemanticEvaluation.Reduce(Corpus(Case()), [null!]));
        var report = Reduce(Case(), Observation(0, []));
        Assert.Equal("UNQUALIFIED_SUPPLIED_SEMANTIC_ACCOUNTING_ONLY", report.Status);
        Assert.DoesNotContain(CaseId.ToString(), report.ToString());
        Assert.False(report.ReleaseQualified);
    }

    private static GroupSemanticEvaluationCase Case(params Guid[] gold) => new(CaseId, GroupSemanticEligibility.ModelEligible, gold);
    private static GroupSemanticEvaluationCorpus Corpus(params GroupSemanticEvaluationCase[] cases) => new(Scope, CorpusId, cases);
    private static GroupSemanticEvaluationStatistics Reduce(GroupSemanticEvaluationCase testCase, GroupSemanticEvaluationObservation observation) =>
        GroupSemanticEvaluation.Reduce(Corpus(testCase), [observation]);
    private static GroupSemanticEvaluationObservation Observation(int count, IReadOnlyList<GroupSemanticMatchEdge> edges,
        GroupSemanticAdjudication adjudication = GroupSemanticAdjudication.Complete, Guid? caseId = null, Guid? corpusId = null,
        GroupScope? scope = null, GroupSemanticCriticalInspection criticalInspection = GroupSemanticCriticalInspection.Complete,
        IReadOnlyList<GroupSemanticCriticalFinding>? critical = null) =>
        new(scope ?? Scope, corpusId ?? CorpusId, caseId ?? CaseId, GroupSemanticOutcome.ValidOutput, GroupSemanticRefusal.None,
            count, adjudication, edges, criticalInspection, critical ?? []);
    private static GroupSemanticEvaluationObservation Rejected(int? count, Guid? caseId = null) =>
        new(Scope, CorpusId, caseId ?? CaseId, GroupSemanticOutcome.RejectedOutput, GroupSemanticRefusal.ParserValidation,
            count, GroupSemanticAdjudication.Unavailable, [], GroupSemanticCriticalInspection.Complete, []);
    private static Guid Id(int id) => new(id, 0, 0, new byte[8]);
    private static void Refused(Action action) =>
        Assert.Equal(GroupSemanticEvaluation.InvalidInputReason, Assert.Throws<InvalidOperationException>(action).Message);

    private sealed class OversizedList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Index must not be accessed.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Enumeration must not start.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
