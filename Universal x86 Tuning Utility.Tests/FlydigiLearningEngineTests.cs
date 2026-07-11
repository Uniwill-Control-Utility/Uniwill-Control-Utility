using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Tests;

/// <summary>
/// Tests for FlydigiLearningEngine: offset learning, bias modes, stability gating, and reset.
/// </summary>
public class FlydigiLearningEngineTests
{
    #region Defaults

    [Fact]
    public void Constructor_Defaults()
    {
        var engine = new FlydigiLearningEngine();

        Assert.False(engine.IsLearningEnabled);
        Assert.Equal(0.1, engine.LearnRate);
        Assert.Equal("balanced", engine.BiasMode);
        Assert.Equal(30, engine.StableThresholdSeconds);
        Assert.Empty(engine.HeatOffsets);
        Assert.Empty(engine.CoolOffsets);
    }

    #endregion

    #region Learning Disabled

    [Fact]
    public void FeedObservation_LearningDisabled_OffsetStaysZero()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = false,
            StableThresholdSeconds = 0
        };

        engine.FeedObservation(0, 60, 50, 2000);

        Assert.Empty(engine.HeatOffsets);
        Assert.Empty(engine.CoolOffsets);
        Assert.Equal(0.0, engine.GetEffectiveOffset(0));
    }

    #endregion

    #region Single Observation

    [Fact]
    public void FeedObservation_SingleHeat_SmallOffsetApplied()
    {
        // LearnRate = 0.1, first observation initializes as delta * alpha
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 0
        };

        // observed 60, target 50 → delta = +10 (hot)
        engine.FeedObservation(0, 60, 50, 2000);

        Assert.Single(engine.HeatOffsets);
        Assert.Equal(1.0, engine.HeatOffsets[0], precision: 5); // 10 * 0.1
        Assert.Empty(engine.CoolOffsets);
    }

    [Fact]
    public void FeedObservation_SingleCool_SmallOffsetApplied()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 0
        };

        // observed 40, target 50 → delta = -10 (cool), stored as abs = 10
        engine.FeedObservation(0, 40, 50, 2000);

        Assert.Empty(engine.HeatOffsets);
        Assert.Single(engine.CoolOffsets);
        Assert.Equal(1.0, engine.CoolOffsets[0], precision: 5); // 10 * 0.1
    }

    #endregion

    #region Convergence

    [Fact]
    public void FeedObservation_MultipleObservations_OffsetConverges()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.5,
            StableThresholdSeconds = 0
        };

        // Feed delta = 10 repeatedly. With alpha=0.5 EMA,
        // the offset should converge toward 10.
        for (int i = 0; i < 20; i++)
        {
            engine.FeedObservation(0, 60, 50, 2000);
        }

        // After many iterations with alpha=0.5, offset should be very close to 10
        Assert.InRange(engine.HeatOffsets[0], 9.0, 10.0);
    }

    #endregion

    #region Separate Heat and Cool Tracking

    [Fact]
    public void FeedObservation_HeatAndCool_TrackedSeparately()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 0
        };

        // Feed a hot observation on gear 0
        engine.FeedObservation(0, 60, 50, 2000);
        // Feed a cool observation on gear 0
        engine.FeedObservation(0, 40, 50, 2000);

        Assert.Single(engine.HeatOffsets);
        Assert.Single(engine.CoolOffsets);
        Assert.Equal(1.0, engine.HeatOffsets[0], precision: 5);
        Assert.Equal(1.0, engine.CoolOffsets[0], precision: 5);
    }

    #endregion

    #region Reset

    [Fact]
    public void Reset_ClearsAllOffsets()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 0
        };

        engine.FeedObservation(0, 60, 50, 2000);
        engine.FeedObservation(1, 40, 50, 2000);

        Assert.NotEmpty(engine.HeatOffsets);
        Assert.NotEmpty(engine.CoolOffsets);

        engine.Reset();

        Assert.Empty(engine.HeatOffsets);
        Assert.Empty(engine.CoolOffsets);
        Assert.Equal(0.0, engine.GetEffectiveOffset(0));
        Assert.Equal(0.0, engine.GetEffectiveOffset(1));
    }

    #endregion

    #region Bias "cooling"

    [Fact]
    public void BiasCooling_HeatOffsetLearnsFaster()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            BiasMode = "cooling",
            StableThresholdSeconds = 0
        };

        // Heat alpha = 0.1 * 1.5 = 0.15
        engine.FeedObservation(0, 60, 50, 2000);

        // Expected: 10 * 0.15 = 1.5
        Assert.Equal(1.5, engine.HeatOffsets[0], precision: 5);
    }

    [Fact]
    public void BiasCooling_CoolOffsetUnchanged()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            BiasMode = "cooling",
            StableThresholdSeconds = 0
        };

        // Cool alpha stays at 0.1 (no bias for cooling in "cooling" mode)
        engine.FeedObservation(0, 40, 50, 2000);

        Assert.Equal(1.0, engine.CoolOffsets[0], precision: 5);
    }

    #endregion

    #region Bias "quiet"

    [Fact]
    public void BiasQuiet_CoolOffsetLearnsFaster()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            BiasMode = "quiet",
            StableThresholdSeconds = 0
        };

        // Cool alpha = 0.1 * 1.5 = 0.15
        engine.FeedObservation(0, 40, 50, 2000);

        // Expected: 10 * 0.15 = 1.5
        Assert.Equal(1.5, engine.CoolOffsets[0], precision: 5);
    }

    [Fact]
    public void BiasQuiet_HeatOffsetUnchanged()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            BiasMode = "quiet",
            StableThresholdSeconds = 0
        };

        // Heat alpha stays at 0.1 (no bias for heat in "quiet" mode)
        engine.FeedObservation(0, 60, 50, 2000);

        Assert.Equal(1.0, engine.HeatOffsets[0], precision: 5);
    }

    #endregion

    #region GetEffectiveOffset

    [Fact]
    public void GetEffectiveOffset_BothExist_ReturnsAverage()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 0
        };

        engine.FeedObservation(0, 60, 50, 2000); // heat offset = 1.0
        engine.FeedObservation(0, 40, 50, 2000); // cool offset = 1.0

        Assert.Equal(1.0, engine.GetEffectiveOffset(0), precision: 5);
    }

    [Fact]
    public void GetEffectiveOffset_OnlyHeat_ReturnsHeat()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 0
        };

        engine.FeedObservation(0, 60, 50, 2000);

        Assert.Equal(1.0, engine.GetEffectiveOffset(0), precision: 5);
    }

    [Fact]
    public void GetEffectiveOffset_OnlyCool_ReturnsCool()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 0
        };

        engine.FeedObservation(0, 40, 50, 2000);

        Assert.Equal(1.0, engine.GetEffectiveOffset(0), precision: 5);
    }

    [Fact]
    public void GetEffectiveOffset_NoData_ReturnsZero()
    {
        var engine = new FlydigiLearningEngine();

        Assert.Equal(0.0, engine.GetEffectiveOffset(0));
    }

    #endregion

    #region Stability Gating

    [Fact]
    public void FeedObservation_RpmNotStable_NoLearning()
    {
        var engine = new FlydigiLearningEngine
        {
            IsLearningEnabled = true,
            LearnRate = 0.1,
            StableThresholdSeconds = 30
        };

        // Feed with changing RPM — should not learn
        engine.FeedObservation(0, 60, 50, 2000);
        engine.FeedObservation(0, 60, 50, 2100);
        engine.FeedObservation(0, 60, 50, 2000);

        Assert.Empty(engine.HeatOffsets);
    }

    #endregion
}
