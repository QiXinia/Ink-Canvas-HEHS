using System;
using System.Collections.Generic;
using InkCanvas.Core.AiAssistant;
using Xunit;

namespace InkCanvas.Ai.Tests;

/// <summary>圈选 → 定点替换 → 其余逐字不变的端到端纯逻辑流程。</summary>
public sealed class LocalCorrectionFlowTests
{
    private static readonly AssistantRect Anchor = new(0, 0, 100, 100);

    [Fact]
    public void Formula_MiddleSelection_ReplacesOnlySelectedRange()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), "AB");

        Assert.Equal("Formula", result.Field);
        Assert.Equal(4, result.Start);
        Assert.Equal(2, result.Length);
        Assert.Equal("0123AB6789", result.Session.Formula);
        Assert.Equal("0123", result.Session.Formula.Substring(0, result.Start));
        Assert.Equal("6789", result.Session.Formula.Substring(result.Start + result.Length));
        Assert.Equal("正文", result.Session.Text);
        AssertStepsAndAnnotationsPreserved(session, result.Session);
    }

    [Fact]
    public void Formula_ReplacementWithDifferentLength_KeepsPrefixAndSuffixVerbatim()
    {
        AssistantSession session = NewSession("正文", "abcdefghij");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(30, 0, 50, 100), "[替换]");

        Assert.Equal("abc", result.Session.Formula.Substring(0, 3));
        Assert.Equal("fghij", result.Session.Formula.Substring(3 + "[替换]".Length));
        Assert.Equal("abc[替换]fghij", result.Session.Formula);
        Assert.Equal(session.Text, result.Session.Text);
    }

    [Fact]
    public void Text_WhenFormulaEmpty_ReplacesTextRangeOnly()
    {
        AssistantSession session = NewSession("面积 = 长 × 宽", "");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), "高");

        Assert.Equal("Text", result.Field);
        Assert.Equal(4, result.Start);
        Assert.Equal(2, result.Length);
        Assert.Equal("面积 =高 × 宽", result.Session.Text);
        Assert.Equal("", result.Session.Formula);
        AssertStepsAndAnnotationsPreserved(session, result.Session);
    }

    [Fact]
    public void Text_WhenFormulaIsWhitespace_FallsBackToTextTarget()
    {
        AssistantSession session = NewSession("0123456789", "   ");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), "XY");

        Assert.Equal("Text", result.Field);
        Assert.Equal("0123XY6789", result.Session.Text);
        Assert.Equal("   ", result.Session.Formula);
    }

    [Fact]
    public void RegionFullyLeftOfAnchor_LeavesContentUnchanged()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(-50, 0, -10, 100), "X");

        Assert.Equal("Formula", result.Field);
        Assert.Equal(0, result.Start);
        Assert.Equal(0, result.Length);
        Assert.Equal("0123456789", result.Session.Formula);
        Assert.NotSame(session, result.Session);
    }

    [Fact]
    public void RegionFullyRightOfAnchor_ReplacesOnlyLastCharacter()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(120, 0, 150, 100), "X");

        Assert.Equal(9, result.Start);
        Assert.Equal(1, result.Length);
        Assert.Equal("012345678X", result.Session.Formula);
        Assert.Equal("正文", result.Session.Text);
    }

    [Fact]
    public void EmptyReplacement_LeavesContentUnchangedButReportsTarget()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), "");

        Assert.Equal("Formula", result.Field);
        Assert.Equal(4, result.Start);
        Assert.Equal(2, result.Length);
        Assert.Equal("0123456789", result.Session.Formula);
        Assert.Equal("正文", result.Session.Text);
        Assert.NotSame(session, result.Session);
    }

    [Fact]
    public void NullReplacement_LeavesContentUnchanged()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), null);

        Assert.Equal("Formula", result.Field);
        Assert.Equal("0123456789", result.Session.Formula);
    }

    [Fact]
    public void ZeroMapping_LeavesContentUnchanged()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, new AssistantRect(10, 10, 10, 20), new AssistantRect(0, 0, 50, 20), "X");

        Assert.Equal("Formula", result.Field);
        Assert.Equal(0, result.Start);
        Assert.Equal(0, result.Length);
        Assert.Equal("0123456789", result.Session.Formula);
    }

    [Fact]
    public void EmptyTarget_ReturnsZeroRangeWithoutThrowing()
    {
        AssistantSession session = NewSession("", "");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), "X");

        Assert.Equal("Text", result.Field);
        Assert.Equal(0, result.Start);
        Assert.Equal(0, result.Length);
        Assert.Equal("", result.Session.Text);
        Assert.Equal("", result.Session.Formula);
    }

    [Fact]
    public void RepeatedApply_AccumulatesChangesAndPreservesRest()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult first = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), "AB");
        LocalCorrectionResult second = LocalCorrectionFlow.Apply(
            first.Session, Anchor, new AssistantRect(0, 0, 20, 100), "X");

        Assert.Equal("0123AB6789", first.Session.Formula);
        Assert.Equal("X23AB6789", second.Session.Formula);
        Assert.Equal("正文", second.Session.Text);
        Assert.Equal("0123456789", session.Formula);
        AssertStepsAndAnnotationsPreserved(session, second.Session);
        AssertRegionsPreserved(session, second.Session);
        AssertMetadataPreserved(session, second.Session);
    }

    [Fact]
    public void Apply_ReturnsNewSession_LeavesOriginalUntouched()
    {
        AssistantSession session = NewSession("正文", "0123456789");
        string originalFormula = session.Formula;
        string originalText = session.Text;
        int originalStepCount = session.Steps.Count;
        int originalAnnotationCount = session.Annotations.Count;
        int originalRegionCount = session.Regions.Count;

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(40, 0, 60, 100), "AB");

        Assert.NotSame(session, result.Session);
        Assert.NotSame(session.Steps, result.Session.Steps);
        Assert.NotSame(session.Annotations, result.Session.Annotations);
        Assert.NotSame(session.Regions, result.Session.Regions);
        Assert.Equal(originalFormula, session.Formula);
        Assert.Equal(originalText, session.Text);
        Assert.Equal(originalStepCount, session.Steps.Count);
        Assert.Equal(originalAnnotationCount, session.Annotations.Count);
        Assert.Equal(originalRegionCount, session.Regions.Count);

        result.Session.Formula = "changed";
        Assert.Equal(originalFormula, session.Formula);
    }

    [Fact]
    public void Apply_PreservesMetadataAndCollections()
    {
        AssistantSession session = NewSession("正文", "0123456789");

        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            session, Anchor, new AssistantRect(0, 0, 30, 100), "00");

        AssertMetadataPreserved(session, result.Session);
        AssertRegionsPreserved(session, result.Session);
        Assert.Equal("003456789", result.Session.Formula);
        Assert.Equal("00", result.Session.Formula.Substring(0, result.Start + result.Length - 1));
        Assert.Equal("456789", result.Session.Formula.Substring(result.Start + result.Length));
    }

    [Fact]
    public void Apply_NullSession_ReturnsZeroRangeWithoutThrowing()
    {
        LocalCorrectionResult result = LocalCorrectionFlow.Apply(
            null, Anchor, new AssistantRect(40, 0, 60, 100), "X");

        Assert.Null(result.Session);
        Assert.Equal("Text", result.Field);
        Assert.Equal(0, result.Start);
        Assert.Equal(0, result.Length);
    }

    private static AssistantSession NewSession(string text, string formula)
    {
        return new AssistantSession
        {
            State = AssistantState.Done,
            Mode = AssistantMode.Solve,
            SessionId = "session-1",
            Text = text,
            Formula = formula,
            Steps = new List<AssistantStep>
            {
                new() { Index = 1, Text = "步骤一", Latex = "a+b" },
                new() { Index = 2, Text = "步骤二", Latex = "c-d" },
            },
            Annotations = new List<IReadOnlyList<IReadOnlyList<double>>>
            {
                new List<IReadOnlyList<double>> { new List<double> { 1, 2, 3 } },
            },
            Regions = new List<AssistantRegion>
            {
                new() { Rect = new AssistantRect(1, 2, 3, 4), TextStart = 1, TextLength = 2, Source = "seed" },
            },
            Confidence = 0.75,
            Verified = true,
            LocalHint = "提示",
            Error = "",
            TokenCount = 42,
            StartedAtMs = 100,
            FirstTokenAtMs = 200,
        };
    }

    private static void AssertStepsAndAnnotationsPreserved(AssistantSession original, AssistantSession updated)
    {
        Assert.Equal(original.Steps.Count, updated.Steps.Count);
        for (int i = 0; i < original.Steps.Count; i++)
        {
            Assert.Equal(original.Steps[i].Index, updated.Steps[i].Index);
            Assert.Equal(original.Steps[i].Text, updated.Steps[i].Text);
            Assert.Equal(original.Steps[i].Latex, updated.Steps[i].Latex);
        }

        Assert.Equal(original.Annotations.Count, updated.Annotations.Count);
        for (int i = 0; i < original.Annotations.Count; i++)
        {
            Assert.Equal(original.Annotations[i], updated.Annotations[i]);
        }
    }

    private static void AssertRegionsPreserved(AssistantSession original, AssistantSession updated)
    {
        Assert.Equal(original.Regions.Count, updated.Regions.Count);
        for (int i = 0; i < original.Regions.Count; i++)
        {
            Assert.Equal(original.Regions[i].Rect, updated.Regions[i].Rect);
            Assert.Equal(original.Regions[i].TextStart, updated.Regions[i].TextStart);
            Assert.Equal(original.Regions[i].TextLength, updated.Regions[i].TextLength);
            Assert.Equal(original.Regions[i].Source, updated.Regions[i].Source);
        }
    }

    private static void AssertMetadataPreserved(AssistantSession original, AssistantSession updated)
    {
        Assert.Equal(original.State, updated.State);
        Assert.Equal(original.Mode, updated.Mode);
        Assert.Equal(original.SessionId, updated.SessionId);
        Assert.Equal(original.Confidence, updated.Confidence);
        Assert.Equal(original.Verified, updated.Verified);
        Assert.Equal(original.LocalHint, updated.LocalHint);
        Assert.Equal(original.Error, updated.Error);
        Assert.Equal(original.TokenCount, updated.TokenCount);
        Assert.Equal(original.StartedAtMs, updated.StartedAtMs);
        Assert.Equal(original.FirstTokenAtMs, updated.FirstTokenAtMs);
    }
}