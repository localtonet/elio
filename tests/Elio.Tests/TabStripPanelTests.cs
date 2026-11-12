using Elio.UI;

namespace Elio.Tests;

/// <summary>Only the sharing out of widths, which is plain arithmetic: nothing here measures or draws a control.</summary>
public sealed class TabStripPanelTests
{
    private const double Smallest = 44;

    private static double[] Shrink(double[] widths, double room)
    {
        TabStripPanel.Shrink(widths, room, Smallest);
        return widths;
    }

    [Fact]
    public void Shrink_WhenEverythingFits_ChangesNothing() =>
        Assert.Equal([120, 200, 150], Shrink([120, 200, 150], 470));

    [Fact]
    public void Shrink_EqualTabs_ShareTheRoomEqually() =>
        Assert.Equal([100, 100, 100], Shrink([120, 120, 120], 300));

    [Fact]
    public void Shrink_WidestGiveWayFirst_NarrowOnesKeepTheirWidth() =>
        Assert.Equal([120, 160, 120], Shrink([120, 300, 120], 400));

    [Fact]
    public void Shrink_SeveralWideTabs_EndUpEquallyWide() =>
        Assert.Equal([150, 100, 150], Shrink([300, 100, 200], 400));

    [Fact]
    public void Shrink_FillsExactlyTheRoomThereIs() =>
        Assert.Equal(500, Shrink([120, 340, 180, 260], 500).Sum(), precision: 6);

    [Fact]
    public void Shrink_NeverGoesBelowTheSmallestWidth_EvenWithoutRoom() =>
        Assert.Equal([Smallest, Smallest, Smallest, Smallest], Shrink([120, 120, 120, 120], 100));

    [Fact]
    public void Shrink_KeepsTheOrderOfTheTabs() =>
        Assert.Equal([60, 170, 170], Shrink([60, 400, 200], 400));
}
