using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Animation;

namespace Minecraft.Tests;

public sealed class PeerNoticeFadeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CancelledFadeCompletionCannotReenterNativeClose(bool immediate)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var theme = WindowCanvasTests.LoadWindow();
            var notice = new PeerNoticeWindow(theme, new PeerNotice("Друг", "Зашёл в сборку"))
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
            };
            var closing = 0;
            var closed = 0;
            Exception? completionFailure = null;
            AnimationClock? fade = null;
            CancelEventHandler deliverLateCompletion = (_, _) =>
            {
                closing++;
                // Deliver the real WPF clock's already queued callback while
                // native closing is on the stack, without a timing-sensitive wait.
                completionFailure = Record.Exception(() =>
                {
                    if (fade is not null) DeliverCompletion(fade);
                    else notice.Dismiss();
                });
            };
            notice.Closed += (_, _) => closed++;
            try
            {
                notice.Show();
                if (SystemParameters.ClientAreaAnimation)
                {
                    notice.Dismiss();
                    fade = OpacityClock(notice);
                }
                notice.Closing += deliverLateCompletion;
                var failure = Record.Exception(() =>
                {
                    if (immediate) notice.CloseNow();
                    else notice.Close();
                });
                Assert.Null(failure);
                Assert.Null(completionFailure);
                Assert.Equal(1, closing);
                Assert.Equal(1, closed);
                if (fade is not null) Assert.Null(Record.Exception(() => DeliverCompletion(fade)));
                Assert.Null(Record.Exception(notice.CloseNow));
                Assert.Equal(1, closed);
                // An uncancelled completion must still dismiss its window.
                var normal = new PeerNoticeWindow(theme, new PeerNotice("Друг", "Обычное уведомление"))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -32000, Top = -32000, ShowActivated = false, ShowInTaskbar = false
                };
                var normalClosed = 0;
                normal.Closed += (_, _) => normalClosed++;
                try
                {
                    normal.Show();
                    normal.Dismiss();
                    if (SystemParameters.ClientAreaAnimation) DeliverCompletion(OpacityClock(normal));
                    Assert.Equal(1, normalClosed);
                }
                finally { normal.CloseNow(); }
                return true;
            }
            finally
            {
                notice.Closing -= deliverLateCompletion;
                if (closed == 0) notice.CloseNow();
                theme.Close();
            }
        });
    }

    private static AnimationClock OpacityClock(PeerNoticeWindow notice)
    {
        // Retain the real clock before CloseNow detaches it. WPF can still
        // deliver Completed from its accumulated event queue after detachment.
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic;
        var storageType = typeof(AnimationClock).Assembly.GetType("System.Windows.Media.Animation.AnimationStorage", true)!;
        var storage = storageType.GetMethod("GetStorage", flags | BindingFlags.Static)!
            .Invoke(null, [notice, UIElement.OpacityProperty]);
        Assert.NotNull(storage);
        var clocks = storageType.GetField("_animationClocks", flags | BindingFlags.Instance)!.GetValue(storage);
        Assert.NotNull(clocks);
        Assert.Equal(1, clocks.GetType().GetProperty("Count")!.GetValue(clocks));
        return Assert.IsType<AnimationClock>(clocks.GetType().GetProperty("Item")!.GetValue(clocks, [0]));
    }

    private static void DeliverCompletion(AnimationClock clock) => typeof(Clock)
        .GetMethod("FireCompletedEvent", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(clock, null);
}
