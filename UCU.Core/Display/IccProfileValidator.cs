using System;

namespace UCU.Core.Display
{
    /// <summary>
    /// The ICC profile gate (story U-205): an imported file must be a real
    /// profile before anything associates it with a display. The check is the
    /// header facts every conforming profile carries and nothing that needs a
    /// colour engine:
    ///
    /// - the first four bytes are the profile's own size, big-endian;
    /// - offset 36 carries the 'acsp' file signature (the one field that
    ///   separates an ICC profile from any other 128-byte blob);
    /// - the major version byte is 2..4 (v2/v4 are what Windows ships and
    ///   what displays consume; v5 is not in the wild);
    /// - the tag table at offset 128 declares a count whose 12-byte entries
    ///   fit inside the declared size.
    ///
    /// Pure by design (bytes in, verdict out): the app reads the file, Core
    /// decides, and the caller refuses to associate garbage.
    /// </summary>
    public static class IccProfileValidator
    {
        /// <summary>Header (128) + tag count (4).</summary>
        public const int MinimumLength = 132;

        public const int HeaderLength = 128;

        public sealed record Result(bool Valid, string? Reason)
        {
            public static Result Ok() => new(true, null);

            public static Result Fail(string reason) => new(false, reason);
        }

        public static Result Validate(ReadOnlySpan<byte> profile)
        {
            if (profile.Length < MinimumLength)
                return Result.Fail($"file is {profile.Length} bytes; an ICC profile needs at least {MinimumLength}");

            uint declared = ReadUInt32BigEndian(profile[..4]);
            if (declared < MinimumLength)
                return Result.Fail($"header declares {declared} bytes; an ICC profile needs at least {MinimumLength}");

            if (declared > profile.Length)
                return Result.Fail($"header declares {declared} bytes but the file is {profile.Length}");

            // 'acsp' at offset 36: the signature every ICC profile carries.
            if (profile[36] != (byte)'a' || profile[37] != (byte)'c' ||
                profile[38] != (byte)'s' || profile[39] != (byte)'p')
            {
                return Result.Fail("missing the 'acsp' signature (not an ICC profile)");
            }

            byte major = profile[8];
            if (major is < 2 or > 4)
                return Result.Fail($"unsupported ICC version {major}.x");

            uint tags = ReadUInt32BigEndian(profile.Slice(HeaderLength, 4));
            long tableEnd = HeaderLength + 4L + (long)tags * 12L;
            if (tableEnd > declared)
                return Result.Fail($"tag table ({tags} entries) runs past the declared size");

            return Result.Ok();
        }

        private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes) =>
            ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}
