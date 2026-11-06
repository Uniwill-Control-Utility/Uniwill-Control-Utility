using System;
using System.Collections.Generic;
using UCU.Contracts;
using UCU.Core.Ec;

namespace Uniwill_Control_Utility.Services;

/// <summary>
/// Builds the minimal recurring EC write plan for smart fan duty updates, as the
/// <c>fan.write_register_blocks</c> payload: the plan crosses the RPC as data and the
/// engine writes the blocks in the order given, so the sequencing stays one call.
/// Temperature tables are configured separately when the loop starts.
/// </summary>
public static class EcSmartFanWritePlan
{
    private const int TableSize = 16;

    public static IReadOnlyList<EcRegisterBlockDto> CreateDutyUpdate(byte[] cpuDuty, byte[] gpuDuty)
    {
        ArgumentNullException.ThrowIfNull(cpuDuty);
        ArgumentNullException.ThrowIfNull(gpuDuty);

        if (cpuDuty.Length != TableSize || gpuDuty.Length != TableSize)
            throw new ArgumentException("Smart fan duty tables must have exactly 16 entries.");

        return new[]
        {
            new EcRegisterBlockDto { Address = EcFanRegisters.CpuFanSpeedTable, Data = (byte[])cpuDuty.Clone() },
            new EcRegisterBlockDto { Address = EcFanRegisters.GpuFanSpeedTable, Data = (byte[])gpuDuty.Clone() }
        };
    }
}
