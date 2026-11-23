// SPDX-License-Identifier: GPL-3.0-or-later
using Notchly.Core.Notch;

namespace Notchly.Core.Tests;

public class NotchMotionTests
{
    [Fact]
    public void SpringStartsAtRestAndSettlesAtOne()
    {
        foreach (var spring in new[] { NotchMotion.GrowingWidth, NotchMotion.ShrinkingHeight, NotchMotion.SteadyWidth })
        {
            Assert.Equal(0, spring.Progress(0));
            Assert.Equal(1, spring.Progress(3), 3);
        }
    }

    [Fact]
    public void BouncySpringOvershootsByItsAnalyticShare()
    {
        var spring = NotchMotion.GrowingWidth;
        var peak = Enumerable.Range(1, 2400).Select(i => spring.Progress(i / 2400.0)).Max();
        Assert.Equal(1 + spring.Overshoot, peak, 3);
        Assert.Equal(0, NotchMotion.ShrinkingWidth.Overshoot);
    }

    [Fact]
    public void LongTravelIsLimitedToTwelvePointsOfSwing()
    {
        var limited = NotchMotion.SpringFor(24, 370, width: false);
        Assert.True((370 - 24) * limited.Overshoot <= NotchMotion.OvershootLimit + 1e-6);
        var size = Enumerable.Range(1, 960).Select(i => NotchMotion.SizeAt(i / 480.0, new SizeD(135, 24), new SizeD(560, 370)));
        Assert.True(size.Max(s => s.Width) <= 560 + NotchMotion.OvershootLimit + 1e-6);
        Assert.True(size.Max(s => s.Height) <= 370 + NotchMotion.OvershootLimit + 1e-6);
    }

    [Fact]
    public void ShrinkingNeverPassesItsTarget()
    {
        var sizes = Enumerable.Range(1, 480).Select(i => NotchMotion.SizeAt(i / 240.0, new SizeD(560, 370), new SizeD(135, 24)));
        Assert.True(sizes.Min(s => s.Width) >= 135 - 1e-6);
        Assert.True(sizes.Min(s => s.Height) >= 24 - 1e-6);
    }

    [Fact]
    public void OpeningSettlesWithinASecond()
    {
        var time = NotchMotion.SettlingTime(new SizeD(135, 24), new SizeD(560, 328));
        Assert.InRange(time, 0.2, 1.0);
    }

    [Fact]
    public void TimelineContinuesInTheSameDirectionAndRestartsWhenTurningBack()
    {
        var timeline = new NotchMotionTimeline(new SizeD(135, 24), new SizeD(300, 100), begin: 0);
        var further = timeline.Continued(new SizeD(560, 328), 0.1);
        Assert.NotNull(further);
        Assert.Equal(new SizeD(560, 328), further!.Target);
        Assert.Null(timeline.Continued(new SizeD(100, 20), 0.1));
        Assert.True(further.SizeAt(3).Width is > 559.5 and < 560.5);
        Assert.True(further.IsSettled(3));
        Assert.False(further.IsSettled(0.15));
    }
}
