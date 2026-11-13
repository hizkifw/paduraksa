using Paduraksa.Application.Config;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

using static Paduraksa.Application.Config.ConfigErrorCodes;
using static Paduraksa.Tests.Unit.Config.ConfigSamples;

namespace Paduraksa.Tests.Unit.Config;

/// <summary>The compression minimum at the layer that sets it.</summary>
public sealed class CompressionValidationTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(CompressionBounds.MaxMinimumSize, true)]
    [InlineData(-1, false)]
    [InlineData(CompressionBounds.MaxMinimumSize + 1, false)]
    public void TheMinimumIsZeroToOneMebibyte(int minimum, bool valid)
    {
        var document = Valid();
        document = document with
        {
            Settings = document.Settings with { Defaults = new HostSettings { Compression = new CompressionSettings { Enabled = true, MinimumSize = minimum } } },
        };

        var errors = ConfigValidator.Validate(document).Select(e => (e.Path, e.Code));

        Assert.Equal(valid ? [] : [("/settings/defaults/compression/minimumSize", CompressionMinimumSizeInvalid)], errors);
    }
}
