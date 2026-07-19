using FluentAssertions;
using ticolinea.stream.service.Helpers;
using Xunit;

namespace Ticolinea.Streaming.Middleware.Tests;

// The gate is static (node-wide by design). Tests within one class run
// sequentially under xUnit, and every test releases in finally, so the
// static state never leaks between them.
public class MassOperationGateTests
{
    [Fact]
    public void First_entry_acquires_the_gate()
    {
        MassOperationGate.TryEnter().Should().BeTrue();
        try
        {
            MassOperationGate.IsHeld.Should().BeTrue();
        }
        finally { MassOperationGate.Exit(); }
    }

    [Fact]
    public void Second_entry_is_rejected_while_held()
    {
        MassOperationGate.TryEnter().Should().BeTrue();
        try
        {
            // This is the 409 path in AdminController.MassOperation.
            MassOperationGate.TryEnter().Should().BeFalse();
            MassOperationGate.TryEnter().Should().BeFalse();
        }
        finally { MassOperationGate.Exit(); }
    }

    [Fact]
    public void Gate_is_reacquirable_after_exit()
    {
        MassOperationGate.TryEnter().Should().BeTrue();
        MassOperationGate.Exit();

        MassOperationGate.IsHeld.Should().BeFalse();
        MassOperationGate.TryEnter().Should().BeTrue();
        try
        {
            MassOperationGate.TryEnter().Should().BeFalse();
        }
        finally { MassOperationGate.Exit(); }
    }
}
