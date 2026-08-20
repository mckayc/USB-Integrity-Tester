using UsbIntegrityTester.Core.Testing;

namespace UsbIntegrityTester.Tests;

public class CapacityVerifierTests
{
    [Fact]
    public void FillDeterministicBlock_IsReproducible_ForSameSeedAndOffset()
    {
        var a = new byte[64];
        var b = new byte[64];

        CapacityVerifier.FillDeterministicBlock(a, seed: 12345, blockOffset: 999);
        CapacityVerifier.FillDeterministicBlock(b, seed: 12345, blockOffset: 999);

        Assert.Equal(a, b);
    }

    [Fact]
    public void FillDeterministicBlock_DiffersAcrossOffsets()
    {
        var a = new byte[64];
        var b = new byte[64];

        CapacityVerifier.FillDeterministicBlock(a, seed: 1, blockOffset: 0);
        CapacityVerifier.FillDeterministicBlock(b, seed: 1, blockOffset: 1);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FillDeterministicBlock_DiffersAcrossSeeds()
    {
        var a = new byte[64];
        var b = new byte[64];

        CapacityVerifier.FillDeterministicBlock(a, seed: 1, blockOffset: 0);
        CapacityVerifier.FillDeterministicBlock(b, seed: 2, blockOffset: 0);

        Assert.NotEqual(a, b);
    }
}
