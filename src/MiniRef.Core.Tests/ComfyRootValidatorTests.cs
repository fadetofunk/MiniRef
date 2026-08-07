using MiniRef.Core.Services;
using Xunit;

namespace MiniRef.Core.Tests;

public class ComfyRootValidatorTests
{
    [Fact]
    public void LooksValid_TrueOnlyWhenBothInputAndModelsSubfoldersExist()
    {
        var root = Path.Combine(Path.GetTempPath(), "miniref-validator-test-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(ComfyRootValidator.LooksValid(root));

            Directory.CreateDirectory(Path.Combine(root, "input"));
            Assert.False(ComfyRootValidator.LooksValid(root));

            Directory.CreateDirectory(Path.Combine(root, "models"));
            Assert.True(ComfyRootValidator.LooksValid(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LooksValid_FalseForNullOrBlank(string? path) => Assert.False(ComfyRootValidator.LooksValid(path));

    [Fact]
    public void LooksValid_FalseForNonexistentFolder() =>
        Assert.False(ComfyRootValidator.LooksValid(@"Z:\definitely\does\not\exist\anywhere"));
}
