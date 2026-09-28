using Uniwill_Control_Utility.Services;

namespace Uniwill_Control_Utility.Tests.Services;

public sealed class EcSmartFanWritePlanTests
{
    [Fact]
    public void Duty_update_contains_only_two_sixteen_byte_tables()
    {
        byte[] cpuDuty = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        byte[] gpuDuty = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();

        var blocks = EcSmartFanWritePlan.CreateDutyUpdate(cpuDuty, gpuDuty);

        Assert.Collection(
            blocks,
            cpu =>
            {
                Assert.Equal((ushort)0x0F20, cpu.StartAddress);
                Assert.Equal(cpuDuty, cpu.Data);
            },
            gpu =>
            {
                Assert.Equal((ushort)0x0F50, gpu.StartAddress);
                Assert.Equal(gpuDuty, gpu.Data);
            });
        Assert.Equal(32, blocks.Sum(block => block.Data.Length));
    }

    [Theory]
    [InlineData(15, 16)]
    [InlineData(16, 15)]
    [InlineData(17, 16)]
    public void Duty_update_rejects_non_table_sized_input(int cpuLength, int gpuLength)
    {
        Assert.Throws<ArgumentException>(() => EcSmartFanWritePlan.CreateDutyUpdate(
            new byte[cpuLength],
            new byte[gpuLength]));
    }
}
