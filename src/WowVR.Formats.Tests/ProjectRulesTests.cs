using WowVR.Formats;
using Xunit;

namespace WowVR.Formats.Tests;

public class ProjectRulesTests
{
    [Fact]
    public void FormatsLibraryHasNoGodotDependency()
    {
        var references = typeof(ClientTarget).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, r => r.Name!.StartsWith("Godot", StringComparison.OrdinalIgnoreCase));
    }
}
