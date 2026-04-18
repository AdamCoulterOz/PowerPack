using PowerPack.Options;

namespace PowerPack.Tests;

public sealed class PowerPackOptionsTests
{
    [Fact]
    public void StorageOptions_Defaults_TableNames_For_PowerPack_Index()
    {
        var options = new StorageOptions();

        Assert.Equal("solutionindex", options.SolutionIndexTableName);
        Assert.Equal("dependencyindex", options.DependencyIndexTableName);
        Assert.Equal("packages", options.PackageContainerName);
    }

    [Fact]
    public void StorageOptions_CreateTableServiceClient_Fails_Loudly_When_No_Storage_Config_Is_Provided()
    {
        var options = new StorageOptions();

        var exception = Assert.Throws<InvalidOperationException>(() => options.CreateTableServiceClient());

        Assert.Contains("PowerPack storage configuration is invalid", exception.Message);
    }
}
