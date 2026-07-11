using Universal_x86_Tuning_Utility.Models;

namespace Universal_x86_Tuning_Utility.Tests;

/// <summary>
/// Tests for FlydigiFanCurveProfile interpolation, presets, serialization, and cloning.
/// </summary>
public class FlydigiFanCurveTests
{
    #region Interpolation Tests

    [Fact]
    public void GetRpmForTemperature_InterpolatesBetweenTwoPoints()
    {
        // Arrange: simple two-point curve 0°C→1300, 100°C→4000
        var profile = new FlydigiFanCurveProfile
        {
            Points = new List<FanCurvePoint>
            {
                new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                new FanCurvePoint { Temperature = 100, Rpm = 4000 },
            }
        };

        // Act: at 50°C we expect (1300 + (4000-1300)*0.5) = 2650
        var rpm = profile.GetRpmForTemperature(50);

        // Assert
        Assert.Equal((ushort)2650, rpm);
    }

    [Fact]
    public void GetRpmForTemperature_ClampsToMinRpm()
    {
        // Arrange: curve that would produce RPM below 1300
        var profile = new FlydigiFanCurveProfile
        {
            Points = new List<FanCurvePoint>
            {
                new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                new FanCurvePoint { Temperature = 100, Rpm = 1300 },
            }
        };

        // Act
        var rpm = profile.GetRpmForTemperature(50);

        // Assert: should return 1300 (the min clamp)
        Assert.Equal((ushort)1300, rpm);
    }

    [Fact]
    public void GetRpmForTemperature_ClampsToMaxRpm()
    {
        // Arrange: curve with RPM above 4000
        var profile = new FlydigiFanCurveProfile
        {
            Points = new List<FanCurvePoint>
            {
                new FanCurvePoint { Temperature = 0, Rpm = 4000 },
                new FanCurvePoint { Temperature = 100, Rpm = 4000 },
            }
        };

        // Act
        var rpm = profile.GetRpmForTemperature(50);

        // Assert: should return 4000 (the max clamp)
        Assert.Equal((ushort)4000, rpm);
    }

    [Fact]
    public void GetRpmForTemperature_BelowFirstPoint_ReturnsFirstPointRpm()
    {
        // Arrange
        var profile = new FlydigiFanCurveProfile
        {
            Points = new List<FanCurvePoint>
            {
                new FanCurvePoint { Temperature = 40, Rpm = 1300 },
                new FanCurvePoint { Temperature = 80, Rpm = 4000 },
            }
        };

        // Act
        var rpm = profile.GetRpmForTemperature(20);

        // Assert: below first point, returns first point's RPM
        Assert.Equal((ushort)1300, rpm);
    }

    [Fact]
    public void GetRpmForTemperature_AboveLastPoint_ReturnsLastPointRpm()
    {
        // Arrange
        var profile = new FlydigiFanCurveProfile
        {
            Points = new List<FanCurvePoint>
            {
                new FanCurvePoint { Temperature = 0, Rpm = 1300 },
                new FanCurvePoint { Temperature = 80, Rpm = 4000 },
            }
        };

        // Act
        var rpm = profile.GetRpmForTemperature(100);

        // Assert: above last point, returns last point's RPM
        Assert.Equal((ushort)4000, rpm);
    }

    #endregion

    #region Built-in Profile Tests

    [Fact]
    public void CreateSilent_HasCorrectDefaultPoints()
    {
        // Act
        var profile = FlydigiFanCurveProfile.CreateSilent();

        // Assert
        Assert.Equal("Silent", profile.Name);
        Assert.NotNull(profile.Id);
        Assert.Equal(4, profile.Points.Count);
        Assert.Equal(0, profile.Points[0].Temperature);
        Assert.Equal((ushort)1300, profile.Points[0].Rpm);
        Assert.Equal(65, profile.Points[1].Temperature);
        Assert.Equal((ushort)1300, profile.Points[1].Rpm);
        Assert.Equal(90, profile.Points[2].Temperature);
        Assert.Equal((ushort)3000, profile.Points[2].Rpm);
        Assert.Equal(100, profile.Points[3].Temperature);
        Assert.Equal((ushort)3000, profile.Points[3].Rpm);
    }

    [Fact]
    public void CreateBalanced_HasCorrectDefaultPoints()
    {
        // Act
        var profile = FlydigiFanCurveProfile.CreateBalanced();

        // Assert
        Assert.Equal("Balanced", profile.Name);
        Assert.NotNull(profile.Id);
        Assert.Equal(4, profile.Points.Count);
        Assert.Equal(0, profile.Points[0].Temperature);
        Assert.Equal((ushort)1300, profile.Points[0].Rpm);
        Assert.Equal(50, profile.Points[1].Temperature);
        Assert.Equal((ushort)1300, profile.Points[1].Rpm);
        Assert.Equal(85, profile.Points[2].Temperature);
        Assert.Equal((ushort)3500, profile.Points[2].Rpm);
        Assert.Equal(100, profile.Points[3].Temperature);
        Assert.Equal((ushort)3500, profile.Points[3].Rpm);
    }

    [Fact]
    public void CreatePerformance_HasCorrectDefaultPoints()
    {
        // Act
        var profile = FlydigiFanCurveProfile.CreatePerformance();

        // Assert
        Assert.Equal("Performance", profile.Name);
        Assert.NotNull(profile.Id);
        Assert.Equal(4, profile.Points.Count);
        Assert.Equal(0, profile.Points[0].Temperature);
        Assert.Equal((ushort)1300, profile.Points[0].Rpm);
        Assert.Equal(40, profile.Points[1].Temperature);
        Assert.Equal((ushort)1300, profile.Points[1].Rpm);
        Assert.Equal(80, profile.Points[2].Temperature);
        Assert.Equal((ushort)4000, profile.Points[2].Rpm);
        Assert.Equal(100, profile.Points[3].Temperature);
        Assert.Equal((ushort)4000, profile.Points[3].Rpm);
    }

    #endregion

    #region Clone Tests

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        // Arrange
        var original = FlydigiFanCurveProfile.CreateBalanced();

        // Act
        var clone = original.Clone();

        // Assert
        Assert.Equal(original.Name, clone.Name);
        Assert.NotEqual(original.Id, clone.Id); // new GUID
        Assert.Equal(original.Points.Count, clone.Points.Count);
        for (int i = 0; i < original.Points.Count; i++)
        {
            Assert.Equal(original.Points[i].Temperature, clone.Points[i].Temperature);
            Assert.Equal(original.Points[i].Rpm, clone.Points[i].Rpm);
        }

        // Mutate original and verify clone is independent
        original.Points[0] = new FanCurvePoint { Temperature = 0, Rpm = 2000 };
        Assert.Equal((ushort)1300, clone.Points[0].Rpm);
    }

    #endregion

    #region Serialization Tests

    [Fact]
    public void ToJSON_FromJSON_RoundTrip_PreservesData()
    {
        // Arrange
        var original = FlydigiFanCurveProfile.CreatePerformance();
        original.Name = "Custom Profile";
        original.Id = "test-id-123";

        // Act
        string json = original.ToJSON();
        var deserialized = FlydigiFanCurveProfile.FromJSON(json);

        // Assert
        Assert.Equal(original.Name, deserialized.Name);
        Assert.Equal(original.Id, deserialized.Id);
        Assert.Equal(original.Points.Count, deserialized.Points.Count);
        for (int i = 0; i < original.Points.Count; i++)
        {
            Assert.Equal(original.Points[i].Temperature, deserialized.Points[i].Temperature);
            Assert.Equal(original.Points[i].Rpm, deserialized.Points[i].Rpm);
        }
    }

    #endregion
}
