using System;
using System.Collections.Generic;

namespace Uniwill_Control_Utility.Services;

public readonly record struct EcRegisterBlock(ushort StartAddress, byte[] Data);

/// <summary>
/// Builds the minimal recurring EC write plan for smart fan duty updates.
/// Temperature tables are configured separately when the loop starts.
/// </summary>
public static class EcSmartFanWritePlan
{
    private const int TableSize = 16;

    public static IReadOnlyList<EcRegisterBlock> CreateDutyUpdate(byte[] cpuDuty, byte[] gpuDuty)
    {
        ArgumentNullException.ThrowIfNull(cpuDuty);
        ArgumentNullException.ThrowIfNull(gpuDuty);

        if (cpuDuty.Length != TableSize || gpuDuty.Length != TableSize)
            throw new ArgumentException("Smart fan duty tables must have exactly 16 entries.");

        return new[]
        {
            new EcRegisterBlock(UniwillECService.REG_CPU_FAN_SPEED_TABLE, (byte[])cpuDuty.Clone()),
            new EcRegisterBlock(UniwillECService.REG_GPU_FAN_SPEED_TABLE, (byte[])gpuDuty.Clone())
        };
    }
}
