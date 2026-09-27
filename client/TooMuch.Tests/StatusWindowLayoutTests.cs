using System.Drawing;
using TooMuch.Client;
using Xunit;

namespace TooMuch.Tests;

public class StatusWindowLayoutTests
{
    [Fact]
    public void ClampToWorkingArea_MovesWindowUpWhenItWouldOverlapBottomEdge()
    {
        var workingArea = new Rectangle(0, 0, 1366, 700);
        var bounds = new Rectangle(100, 600, 340, 230);

        var visible = StatusWindow.ClampToWorkingArea(bounds, workingArea);

        Assert.Equal(new Rectangle(100, 470, 340, 230), visible);
    }
}
