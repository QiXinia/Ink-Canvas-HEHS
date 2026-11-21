using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Platform.Ai;
using Xunit;

namespace InkCanvas.Ai.Tests;

public sealed class AiTeachingTests
{
    private static AssistantSession SessionWithSteps(int count)
    {
        var session = AssistantMachine.CreateSession(AssistantMode.Solve, "teaching-1");
        session.State = AssistantState.Done;
        session.Text = "先看题目条件。";
        session.Formula = "x_1 = 2,\\ x_2 = 3";
        for (int i = 0; i < count; i++)
        {
            session.Steps.Add(new AssistantStep { Index = i + 1, Text = "步骤" + (i + 1), Latex = "" });
        }

        return session;
    }

    [Fact]
    public void Next_CyclesThroughFourModes()
    {
        Assert.Equal(AssistantTeachingMode.StepByStep, AssistantTeachingPolicy.Next(AssistantTeachingMode.Hint));
        Assert.Equal(AssistantTeachingMode.Full, AssistantTeachingPolicy.Next(AssistantTeachingMode.StepByStep));
        Assert.Equal(AssistantTeachingMode.Teacher, AssistantTeachingPolicy.Next(AssistantTeachingMode.Full));
        Assert.Equal(AssistantTeachingMode.Hint, AssistantTeachingPolicy.Next(AssistantTeachingMode.Teacher));
    }

    [Theory]
    [InlineData(AssistantTeachingMode.Hint, "hint")]
    [InlineData(AssistantTeachingMode.StepByStep, "step")]
    [InlineData(AssistantTeachingMode.Full, "full")]
    [InlineData(AssistantTeachingMode.Teacher, "teacher")]
    public void Wire_MatchesServerVocabulary(AssistantTeachingMode mode, string wire)
    {
        Assert.Equal(wire, AssistantTeachingPolicy.Wire(mode));
        Assert.Equal(mode, AssistantTeachingPolicy.Parse(wire));
    }

    [Fact]
    public void Parse_UnknownFallsBackToStepByStep()
    {
        Assert.Equal(AssistantTeachingMode.StepByStep, AssistantTeachingPolicy.Parse(""));
        Assert.Equal(AssistantTeachingMode.StepByStep, AssistantTeachingPolicy.Parse("unknown"));
        Assert.Equal(AssistantTeachingMode.StepByStep, AssistantTeachingPolicy.Parse("stepByStep"));
    }

    [Fact]
    public void Hint_HidesStepsAndAnswer()
    {
        var session = SessionWithSteps(3);
        session.TeachingMode = AssistantTeachingMode.Hint;

        Assert.Equal(0, AssistantTeachingPolicy.VisibleSteps(session.TeachingMode, 0, 3));
        Assert.False(AssistantTeachingPolicy.ShowsFinalAnswer(session.TeachingMode, 0, 3));
        Assert.False(AssistantTeachingPolicy.ShowsFinalAnswer(session.TeachingMode, 3, 3));
        Assert.False(AssistantTeachingPolicy.HasMoreSteps(session.TeachingMode, 0, 3));
    }

    [Fact]
    public void StepByStep_RevealsOneByOneAndAnswerLast()
    {
        var session = SessionWithSteps(3);
        session.TeachingMode = AssistantTeachingMode.StepByStep;
        session.RevealedSteps = 0;

        Assert.Equal(0, AssistantTeachingPolicy.VisibleSteps(session.TeachingMode, 0, 3));
        Assert.False(AssistantTeachingPolicy.ShowsFinalAnswer(session.TeachingMode, 0, 3));
        Assert.True(AssistantTeachingPolicy.HasMoreSteps(session.TeachingMode, 0, 3));

        Assert.True(AssistantMachine.RevealNextStep(session));
        Assert.Equal(1, session.RevealedSteps);
        Assert.False(AssistantTeachingPolicy.ShowsFinalAnswer(session.TeachingMode, 1, 3));

        Assert.True(AssistantMachine.RevealNextStep(session));
        Assert.True(AssistantMachine.RevealNextStep(session));
        Assert.Equal(3, session.RevealedSteps);
        Assert.True(AssistantTeachingPolicy.ShowsFinalAnswer(session.TeachingMode, 3, 3));
        Assert.False(AssistantPolicyAllowsReveal(session));
    }

    private static bool AssistantPolicyAllowsReveal(AssistantSession session)
        => AssistantTeachingPolicy.HasMoreSteps(
            session.TeachingMode, session.RevealedSteps, session.Steps.Count);

    [Fact]
    public void StepByStep_WithoutSteps_ShowsAnswer()
    {
        var session = SessionWithSteps(0);
        session.TeachingMode = AssistantTeachingMode.StepByStep;

        Assert.True(AssistantTeachingPolicy.ShowsFinalAnswer(session.TeachingMode, 0, 0));
        Assert.False(AssistantTeachingPolicy.HasMoreSteps(session.TeachingMode, 0, 0));
    }

    [Theory]
    [InlineData(AssistantTeachingMode.Full)]
    [InlineData(AssistantTeachingMode.Teacher)]
    public void FullAndTeacher_ShowEverything(AssistantTeachingMode mode)
    {
        var session = SessionWithSteps(4);
        session.TeachingMode = mode;
        session.RevealedSteps = 0;

        Assert.Equal(4, AssistantTeachingPolicy.VisibleSteps(mode, 0, 4));
        Assert.True(AssistantTeachingPolicy.ShowsFinalAnswer(mode, 0, 4));
        Assert.False(AssistantMachine.RevealNextStep(session));
    }

    [Fact]
    public void SetTeachingMode_ResetsRevealForStepByStep()
    {
        var session = SessionWithSteps(3);
        session.TeachingMode = AssistantTeachingMode.Hint;
        session.RevealedSteps = 3;

        AssistantMachine.SetTeachingMode(session, AssistantTeachingMode.StepByStep);

        Assert.Equal(AssistantTeachingMode.StepByStep, session.TeachingMode);
        Assert.Equal(0, session.RevealedSteps);
    }

    [Fact]
    public void SetTeachingMode_SameModeKeepsReveal()
    {
        var session = SessionWithSteps(3);
        session.TeachingMode = AssistantTeachingMode.StepByStep;
        session.RevealedSteps = 2;

        AssistantMachine.SetTeachingMode(session, AssistantTeachingMode.StepByStep);

        Assert.Equal(2, session.RevealedSteps);
    }

    [Fact]
    public void Payload_AppendsTeachingWithoutBreakingExistingFields()
    {
        var request = new AssistantRequest
        {
            ImageBase64 = "abc",
            Mode = AssistantMode.Solve,
            SessionId = "s-1",
            Stage = AssistantStage.WithImage,
            Teaching = AssistantTeachingMode.Hint,
        };

        string payload = AiClient.BuildPayload(request, "test-model");

        Assert.Contains("\"model\":\"test-model\"", payload);
        Assert.Contains("\"mode\":\"solve\"", payload);
        Assert.Contains("\"sessionId\":\"s-1\"", payload);
        Assert.Contains("\"imageBase64\":\"abc\"", payload);
        Assert.Contains("\"stage\":\"withImage\"", payload);
        Assert.Contains("\"teaching\":\"hint\"", payload);
    }

    [Fact]
    public void Payload_InkOnlyStageDropsImageButKeepsTeaching()
    {
        var request = new AssistantRequest
        {
            ImageBase64 = "abc",
            Stage = AssistantStage.InkOnly,
            Teaching = AssistantTeachingMode.Teacher,
        };

        string payload = AiClient.BuildPayload(request, "");

        Assert.Contains("\"imageBase64\":\"\"", payload);
        Assert.Contains("\"teaching\":\"teacher\"", payload);
    }

    [Fact]
    public void MockScript_HintOmitsFormulaAndSteps()
    {
        IReadOnlyList<AssistantEvent> hint = AiMockScript.Build(AssistantMode.Solve, AssistantTeachingMode.Hint);

        Assert.DoesNotContain(hint, ev => ev.Kind == AssistantEventKind.Step);
        Assert.DoesNotContain(hint, ev => ev.Kind == AssistantEventKind.Formula);
        Assert.Contains(hint, ev => ev.Kind == AssistantEventKind.Token);
        Assert.Equal(AssistantEventKind.Done, hint[^1].Kind);
    }

    [Fact]
    public void MockScript_FullKeepsFormulaAndSteps()
    {
        IReadOnlyList<AssistantEvent> full = AiMockScript.Build(AssistantMode.Solve, AssistantTeachingMode.Full);

        Assert.Contains(full, ev => ev.Kind == AssistantEventKind.Step);
        Assert.Contains(full, ev => ev.Kind == AssistantEventKind.Formula);
    }

    [Fact]
    public void MockClient_HintSessionNeverGetsSteps()
    {
        var session = AssistantMachine.CreateSession(AssistantMode.Solve, "mock-hint");
        using var done = new ManualResetEventSlim();
        using var client = new AiClient(new AiConfig());

        var request = new AssistantRequest
        {
            Mode = AssistantMode.Solve,
            Teaching = AssistantTeachingMode.Hint,
            SessionId = "mock-hint",
        };

        AiStreamHandle hintHandle = client.Start(
            request,
            ev => AssistantMachine.ApplyEvent(session, ev, 0),
            _ => done.Set(),
            done.Set);

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        hintHandle.Abort();
        Assert.Equal(AssistantState.Done, session.State);
        Assert.Empty(session.Steps);
        Assert.Equal("", session.Formula);
        Assert.False(string.IsNullOrEmpty(session.Text));
    }

    [Fact]
    public async Task MockClient_FullSessionGetsStepsAndFormula()
    {
        var session = AssistantMachine.CreateSession(AssistantMode.Solve, "mock-full");
        using var done = new ManualResetEventSlim();
        using var client = new AiClient(new AiConfig());

        var request = new AssistantRequest
        {
            Mode = AssistantMode.Solve,
            Teaching = AssistantTeachingMode.Full,
            SessionId = "mock-full",
        };

        AiStreamHandle fullHandle = client.Start(
            request,
            ev => AssistantMachine.ApplyEvent(session, ev, 0),
            _ => done.Set(),
            done.Set);

        await Task.Run(() => Assert.True(done.Wait(TimeSpan.FromSeconds(5))));
        fullHandle.Abort();
        Assert.Equal(3, session.Steps.Count);
        Assert.False(string.IsNullOrEmpty(session.Formula));
    }
}