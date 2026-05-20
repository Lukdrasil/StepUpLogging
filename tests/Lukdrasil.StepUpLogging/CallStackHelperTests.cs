using Serilog.Core;

namespace Lukdrasil.StepUpLogging.Tests;

public class CallStackHelperTests
{
    [Fact]
    public void CreateCallStackEnricher_DoesNotThrow()
    {
        var ex = Record.Exception(() => CallStackHelper.CreateCallStackEnricher());
        Assert.Null(ex);
    }

    [Fact]
    public void CreateCallStackEnricher_ReturnsNullWhenPackageNotLoaded()
    {
        // No Serilog.Enrichers.CallStack assembly referenced in the test project → null
        var result = CallStackHelper.CreateCallStackEnricher();
        Assert.Null(result);
    }
}
