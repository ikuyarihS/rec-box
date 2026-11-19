using System;
using System.IO;
using RecBox.Core;
using RecBox.Core.Transcription;
using Xunit;

namespace RecBox.Tests;

public class DeviceNameAndHomeTests
{
    [Theory]
    [InlineData("Vulkan1 = NVIDIA GeForce RTX 3060 Laptop GPU (discrete, 5.9 GB own memory)", "NVIDIA GeForce RTX 3060 Laptop GPU")]
    [InlineData("Vulkan0 = Intel(R) Iris(R) Xe Graphics (integrated, 0.1 GB own memory)", "Intel(R) Iris(R) Xe Graphics")]
    [InlineData("NVIDIA GeForce GTX 860M", "NVIDIA GeForce GTX 860M")]
    [InlineData("Vulkan", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Friendly_DropsTheVulkanIdAndDetails(string? raw, string? expected) =>
        Assert.Equal(expected, DeviceName.Friendly(raw));

    [Fact]
    public void Where_NeverSaysVulkan()
    {
        Assert.Equal("the NVIDIA GeForce RTX 3060 Laptop GPU", DeviceName.Where("Vulkan1 = NVIDIA GeForce RTX 3060 Laptop GPU (discrete, 5.9 GB own memory)", true));
        Assert.Equal("the graphics card", DeviceName.Where(null, true));
        Assert.Equal("the processor", DeviceName.Where("Vulkan1 = X", false));
    }

    [Fact]
    public void Tests_NeverWriteTheOwnersRealLog()
    {
        var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecBox");
        Assert.False(Log.FilePath.StartsWith(real, StringComparison.OrdinalIgnoreCase), Log.FilePath);
        Assert.False(AppPaths.LogsDir.StartsWith(real, StringComparison.OrdinalIgnoreCase), AppPaths.LogsDir);
    }
}
