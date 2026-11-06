using UCU.Hardware.Cpu;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted SMU command table the cpu verbs run against. The production
/// service drives real CPU power telemetry registers through PawnIO, which is
/// exactly what no test may touch. The fake mirrors the production surface's
/// semantics: a known command answers its scripted status code, an unknown
/// command throws ArgumentOutOfRangeException (the dispatcher's InvalidValue),
/// and a scripted fault type reproduces what the mailbox protocol throws
/// (InvalidOperationException for PawnIO init failure / SMU refusal →
/// HardwareFault).
/// </summary>
public sealed class FakeAmdSmuService : IAmdSmuService
{
    private readonly Dictionary<string, int> _statuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _faults = new(StringComparer.Ordinal);

    public List<(string Name, uint Value)> Calls { get; } = new();

    /// <summary>Register a command with the status the SMU should answer (0x01 = OK).</summary>
    public FakeAmdSmuService Answers(string commandName, int status = 0x01)
    {
        _statuses[commandName] = status;
        return this;
    }

    /// <summary>Register a command whose execution throws (the mailbox protocol's
    /// failure shapes: InvalidOperationException for init failure / refusal).</summary>
    public FakeAmdSmuService Fails(string commandName, Exception fault)
    {
        _faults[commandName] = fault;
        return this;
    }

    public int Execute(string commandName, uint value, CancellationToken cancellationToken = default)
    {
        Calls.Add((commandName, value));

        if (_faults.TryGetValue(commandName, out Exception? fault))
            throw fault;

        if (!_statuses.TryGetValue(commandName, out int status))
            throw new ArgumentOutOfRangeException($"Command '{commandName}' not found");

        return status;
    }
}
