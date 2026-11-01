using UCU.Core.Display;

namespace UCU.Core.Tests.Display;

/// <summary>
/// The ICC import gate (U-205). The point is that a wrong file must be refused
/// BEFORE anything associates it with a display: Windows happily accepts the
/// association registry write for a non-profile, and the panel then renders
/// through a broken transform. Every rejection reason is pinned, and the
/// acceptance case is a byte-exact minimal v4 header.
/// </summary>
public sealed class IccProfileValidatorTests
{
    private static byte[] Profile(uint size = 132, byte major = 4, uint tags = 0, string signature = "acsp")
    {
        var bytes = new byte[Math.Max((int)size, 132)];
        WriteUInt32BigEndian(bytes, 0, size);
        bytes[8] = major;
        bytes[36] = (byte)signature[0];
        bytes[37] = (byte)signature[1];
        bytes[38] = (byte)signature[2];
        bytes[39] = (byte)signature[3];
        WriteUInt32BigEndian(bytes, 128, tags);
        return bytes;
    }

    private static void WriteUInt32BigEndian(byte[] bytes, int offset, uint value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    [Fact]
    public void MinimalV4ProfileWithNoTags_IsAccepted()
    {
        IccProfileValidator.Result result = IccProfileValidator.Validate(Profile());

        Assert.True(result.Valid, result.Reason);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void VersionsTwoThroughFour_AreAccepted(byte major)
    {
        Assert.True(IccProfileValidator.Validate(Profile(major: major)).Valid);
    }

    [Fact]
    public void TruncatedFile_IsRefused()
    {
        IccProfileValidator.Result result = IccProfileValidator.Validate(new byte[100]);

        Assert.False(result.Valid);
        Assert.Contains("at least 132", result.Reason);
    }

    [Fact]
    public void DeclaredSizeLargerThanTheFile_IsRefused()
    {
        // Declare 4096 in a 132-byte file.
        byte[] bytes = Profile();
        WriteUInt32BigEndian(bytes, 0, 4096);

        IccProfileValidator.Result result = IccProfileValidator.Validate(bytes);

        Assert.False(result.Valid);
        Assert.Contains("header declares 4096", result.Reason);
    }

    [Fact]
    public void MissingAcspSignature_IsRefused()
    {
        IccProfileValidator.Result result = IccProfileValidator.Validate(Profile(signature: "acpx"));

        Assert.False(result.Valid);
        Assert.Contains("acsp", result.Reason);
    }

    [Fact]
    public void UnknownMajorVersion_IsRefused()
    {
        IccProfileValidator.Result result = IccProfileValidator.Validate(Profile(major: 7));

        Assert.False(result.Valid);
        Assert.Contains("version 7", result.Reason);
    }

    [Fact]
    public void TagTableRunningPastTheDeclaredSize_IsRefused()
    {
        // 5 entries need 132 + 60 = 192 bytes; declare 132.
        IccProfileValidator.Result result = IccProfileValidator.Validate(Profile(tags: 5));

        Assert.False(result.Valid);
        Assert.Contains("tag table", result.Reason);
    }

    [Fact]
    public void TagTableThatFits_IsAccepted()
    {
        // 5 entries inside a 192-byte profile.
        Assert.True(IccProfileValidator.Validate(Profile(size: 192, tags: 5)).Valid);
    }
}
