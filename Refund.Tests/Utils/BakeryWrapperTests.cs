using System.ComponentModel;
using System.Diagnostics;
using Refund.Utils;

namespace Refund.Tests.Utils;

public sealed class BakeryWrapperTests
{
    [Fact]
    public void FailedRendererPropagatesItsDiagnosticInsteadOfReportingSuccess()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BakeryWrapper.RunCommand(Shell(
            "echo 'ImportError: broken SciPy binary' >&2; exit 7")));

        Assert.Contains("exit code 7", error.Message);
        Assert.Contains("ImportError: broken SciPy binary", error.Message);
    }

    [Fact]
    public void MissingRendererDoesNotReportSuccess()
    {
        Assert.Throws<Win32Exception>(() => BakeryWrapper.RunCommand(
            new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "bakery"))));
    }

    [Fact]
    public void SuccessfulRendererCanWriteWarningsToStderr()
    {
        BakeryWrapper.RunCommand(Shell("echo 'A harmless warning' >&2; exit 0"));
    }

    private static ProcessStartInfo Shell(string command) => new("/bin/bash")
    {
        ArgumentList = { "-c", command }
    };
}
