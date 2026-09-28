using System.Reflection;
using System.Text.Json;
using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class StartupProgressTests
{
    [Fact]
    public void Timeline_IsStrictlyIncreasing_AndCompletesAtOneHundredPercent()
    {
        var states = StartupProgress.Timeline;

        Assert.NotEmpty(states);
        Assert.True(states.Count >= 12, "The startup loader should expose detailed progress, not only broad phases.");
        Assert.Equal(100, states[^1].Percent);
        Assert.All(states, state => Assert.InRange(state.Percent, 0, 100));

        for (var index = 1; index < states.Count; index++)
        {
            Assert.True(states[index].Percent > states[index - 1].Percent);
        }
    }

    [Fact]
    public void EverySupportedCatalogue_ContainsEveryStartupStatus()
    {
        var assembly = typeof(LocalizationService).Assembly;

        foreach (var language in LocalizationService.SupportedLanguages)
        {
            var resourceName = assembly.GetManifestResourceNames()
                .Single(name => name.EndsWith($"Localization.{language.CultureName}.json", StringComparison.Ordinal));
            using var stream = assembly.GetManifestResourceStream(resourceName);
            using var document = JsonDocument.Parse(Assert.IsAssignableFrom<Stream>(stream));

            foreach (var statusKey in StartupProgress.Timeline.Select(state => state.StatusKey).Distinct())
            {
                Assert.True(
                    document.RootElement.TryGetProperty(statusKey, out var translation) &&
                    !string.IsNullOrWhiteSpace(translation.GetString()),
                    $"{language.CultureName} is missing startup status '{statusKey}'.");
            }
        }
    }

    [Theory]
    [InlineData(7.5, 18)]
    [InlineData(49.25, 66)]
    public void AnimationTransition_ContinuesFromCurrentDisplayedValue(double currentValue, int targetValue)
    {
        var transition = StartupProgressAnimation.Create(currentValue, targetValue);

        Assert.Equal(currentValue, transition.From);
        Assert.Equal(targetValue, transition.To);
        Assert.True(transition.Duration < StartupProgressAnimation.StageDisplayDuration);
    }

    [Fact]
    public async Task Reporter_ForwardsHostStagesInOrder()
    {
        var reported = new List<StartupStage>();
        var reporter = new StartupProgressReporter(stage =>
        {
            reported.Add(stage);
            return Task.CompletedTask;
        });

        await reporter.ReportAsync(StartupStage.StartingServices);
        await reporter.ReportAsync(StartupStage.PreparingInterface);
        await reporter.ReportAsync(StartupStage.OpeningDashboard);

        Assert.Equal(
            new[]
            {
                StartupStage.StartingServices,
                StartupStage.PreparingInterface,
                StartupStage.OpeningDashboard
            },
            reported);
    }
}
