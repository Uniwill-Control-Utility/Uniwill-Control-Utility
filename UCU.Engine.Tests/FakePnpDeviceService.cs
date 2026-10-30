using UCU.Hardware.Devices;

namespace UCU.Engine.Tests;

/// <summary>
/// The scripted device tree the device verbs run against. Devices are keyed by
/// the verb's filter class ("Camera", "Display"), each with a running flag and a
/// present flag; SetEnabled moves the running flag and reports the verification
/// the production service performs by re-reading (the scripted tree answers
/// instantly, so verification is "the tree is now in the requested state").
/// </summary>
public sealed class FakePnpDeviceService : IPnpDeviceService
{
    private sealed record FakeDevice(string InstanceId, bool Present, bool Running);

    private readonly Dictionary<string, FakeDevice> _devices =
        new(StringComparer.OrdinalIgnoreCase);

    public List<(string Class, bool Enabled)> Mutations { get; } = new();

    public FakePnpDeviceService With(string deviceClass, string instanceId, bool present = true, bool running = true)
    {
        _devices[deviceClass] = new FakeDevice(instanceId, present, running);
        return this;
    }

    public Task<PnpDeviceState?> GetStateAsync(PnpDeviceFilter filter, CancellationToken cancellationToken = default)
    {
        if (!_devices.TryGetValue(filter.DeviceClass, out FakeDevice? device) || !device.Present)
            return Task.FromResult<PnpDeviceState?>(null);

        return Task.FromResult<PnpDeviceState?>(new PnpDeviceState
        {
            InstanceId = device.InstanceId,
            Status = device.Running ? "OK" : "Disable",
            Present = true,
        });
    }

    public Task<bool> SetEnabledAsync(PnpDeviceFilter filter, bool enabled, CancellationToken cancellationToken = default)
    {
        Mutations.Add((filter.DeviceClass, enabled));

        if (!_devices.TryGetValue(filter.DeviceClass, out FakeDevice? device) || !device.Present)
            return Task.FromResult(false);

        _devices[filter.DeviceClass] = device with { Running = enabled };
        return Task.FromResult(true);
    }
}
