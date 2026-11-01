using AzerothVR.Formats;
using Xunit;

namespace AzerothVR.Formats.Tests;

public class ProjectRulesTests
{
    [Fact]
    public void FormatsLibraryHasNoGodotDependency()
    {
        var references = typeof(ClientTarget).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, r => r.Name!.StartsWith("Godot", StringComparison.OrdinalIgnoreCase));
    }
}
