using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Minecraft;

namespace Minecraft.Tests;

public sealed class ChatComposerTests
{
    [Fact]
    public void UnavailableAssistant_KeepsTheDraftEditableAndExplainsTheDisabledAction()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var composer = Element<ChatComposer>(window, "ChatComposerControl");
            var input = Element<TextBox>(window, "ChatMessageTextBox");
            var action = Element<Button>(window, "ChatSendButton");
            input.Text = "Мой вопрос о сборке";
            Settle(window, new Size(968, 633));
            Assert.True(input.IsEnabled);
            Assert.False(input.IsReadOnly);
            Assert.Equal(input.Text, composer.Prompt);
            Assert.False(composer.IsServiceAvailable);
            Assert.False(composer.IsGenerating);
            Assert.False(action.IsEnabled);
            Assert.False(composer.InvokeAction());
            Assert.Equal("Отправить", AutomationProperties.GetName(action));
            Assert.Equal("Помощник пока не подключён", action.ToolTip);
            Assert.Equal("Подключение помощника пока недоступно", Element<TextBlock>(window, "ChatAvailabilityText").Text);
            return true;
        });
    }

    [Fact]
    public void TheSameAction_SendsThenCancelsAControlledOperationAndRestoresItsArrow()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var composer = Element<ChatComposer>(window, "ChatComposerControl");
            var input = Element<TextBox>(window, "ChatMessageTextBox");
            var action = Element<Button>(window, "ChatSendButton");
            var size = new Size(968, 633);
            composer.IsServiceAvailable = true;
            input.Text = "   ";
            Settle(window, size);
            Assert.False(action.IsEnabled);
            input.Text = "Проверить сборку";
            input.Select(2, 5);
            Settle(window, size);
            var request = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sends = 0;
            var cancellations = 0;
            window.AddHandler(ChatComposer.SendRequestedEvent, new RoutedEventHandler((_, e) =>
            {
                Assert.Same(composer, e.OriginalSource);
                sends++;
                composer.IsGenerating = true;
            }));
            window.AddHandler(ChatComposer.CancelRequestedEvent, new RoutedEventHandler((_, e) =>
            {
                Assert.Same(composer, e.OriginalSource);
                cancellations++;
                request.TrySetCanceled();
            }));
            Assert.True(action.IsEnabled);
            ChatComposer.ActionCommand.Execute(null, composer);
            Settle(window, size);
            Assert.Equal(1, sends);
            Assert.True(composer.IsGenerating);
            Assert.False(request.Task.IsCompleted);
            Assert.Equal(true, action.Tag);
            Assert.Equal("Остановить", AutomationProperties.GetName(action));
            Assert.Equal(Visibility.Visible, Glyph(action, "StopGlyph").Visibility);
            Assert.Equal(Visibility.Collapsed, Glyph(action, "SendGlyph").Visibility);
            Assert.Equal("Проверить сборку", input.Text);
            Assert.Equal(2, input.SelectionStart);
            Assert.Equal(5, input.SelectionLength);
            ChatComposer.ActionCommand.Execute(null, composer);
            Settle(window, size);
            Assert.Equal(1, cancellations);
            Assert.True(request.Task.IsCanceled);
            Assert.True(composer.IsCancellationPending);
            Assert.False(action.IsEnabled);
            Assert.False(composer.InvokeAction());
            Assert.Equal("Остановка…", AutomationProperties.GetName(action));
            // A real operation's completion is supplied by the owner, not simulated by the view.
            composer.IsGenerating = false;
            Settle(window, size);
            Assert.False(composer.IsCancellationPending);
            Assert.Equal("Отправить", AutomationProperties.GetName(action));
            Assert.Equal(Visibility.Visible, Glyph(action, "SendGlyph").Visibility);
            Assert.Equal(Visibility.Collapsed, Glyph(action, "StopGlyph").Visibility);
            Assert.True(action.IsEnabled);
            Assert.Equal("Проверить сборку", input.Text);
            Assert.Equal(2, input.SelectionStart);
            Assert.Equal(5, input.SelectionLength);
            return true;
        });
    }

    [Theory]
    [InlineData(968, 633, 1)]
    [InlineData(968, 633, 1.25)]
    [InlineData(968, 633, 1.5)]
    [InlineData(968, 633, 2)]
    [InlineData(1904, 1001, 1)]
    [InlineData(1904, 1001, 1.5)]
    [InlineData(1904, 1001, 2)]
    public void LongDraft_ScrollsInsideTheFieldWithoutMovingTheSidebarOrItsBottomAnchor(double width, double height, double scale)
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var size = new Size(width, height);
            Settle(window, size);
            var dpi = new DpiScale(scale, scale);
            VisualTreeHelper.SetRootDpi(window, dpi);
            VisualTreeHelper.SetRootDpi((FrameworkElement)window.Content, dpi);
            Settle(window, size);
            var input = Element<TextBox>(window, "ChatMessageTextBox");
            var composer = Element<Border>(window, "ChatComposerFrame");
            var card = Element<Border>(window, "SidePanelCard");
            var action = Element<Button>(window, "ChatSendButton");
            var root = Element<Grid>(window, "RootGrid");
            input.Text = "Короткий вопрос";
            Settle(window, size);
            var scroll = Assert.IsType<ScrollViewer>(input.Template.FindName("PART_ContentHost", input));
            Assert.Equal(scale, VisualTreeHelper.GetDpi(input).DpiScaleY, 6);
            Assert.Equal(Visibility.Collapsed, scroll.ComputedVerticalScrollBarVisibility);
            var composerHeight = composer.ActualHeight;
            var cardHeight = card.ActualHeight;
            var caption = Bounds(Element<TextBlock>(window, "ChatAvailabilityText"), root);
            Assert.True(caption.Bottom <= Bounds(composer, root).Top + 0.5);
            input.Text = string.Concat(Enumerable.Repeat("Очень длинный запрос о Minecraft и сборке. ", 100));
            Settle(window, size);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            Assert.True(input.ExtentHeight > input.ViewportHeight);
            Assert.Equal((double)window.FindResource("Size.ChatPromptHeight"), input.ActualHeight, 3);
            Assert.Equal(composerHeight, composer.ActualHeight, 3);
            Assert.Equal(cardHeight, card.ActualHeight, 3);
            Assert.True(scroll.ScrollableWidth <= 0.5);
            input.ScrollToEnd();
            Settle(window, size);
            Assert.True(input.VerticalOffset > 0);
            var actionBounds = Bounds(action, composer);
            var inputBounds = Bounds(input, composer);
            Assert.True(actionBounds.Top >= inputBounds.Bottom + 7.5);
            Assert.True(actionBounds.Right <= composer.ActualWidth - composer.Padding.Right + 0.5);
            Assert.True(actionBounds.Bottom <= composer.ActualHeight - composer.Padding.Bottom + 0.5);
            Assert.Equal(Bounds(Element<ProgressBar>(window, "UpdateProgressBar"), root).Bottom, Bounds(card, root).Bottom, 3);
            return true;
        });
    }

    [Fact]
    public void ResizingSwitchingFacesAndChangingActionState_PreserveTheDraftAndSelection()
    {
        WindowCanvasTests.OnAStaThread(() =>
        {
            var window = WindowCanvasTests.LoadWindow();
            var input = Element<TextBox>(window, "ChatMessageTextBox");
            var composer = Element<ChatComposer>(window, "ChatComposerControl");
            input.Text = "Черновик сохраняется при изменении размера и переключении страницы";
            input.Select(5, 12);
            var draft = input.Text;
            foreach (var size in new[] { new Size(968, 633), new Size(1904, 1001), new Size(968, 633) })
            {
                Element<Grid>(window, "ChatPanel").Visibility = Visibility.Collapsed;
                Element<Grid>(window, "ChangelogPanel").Visibility = Visibility.Visible;
                composer.IsGenerating = true;
                Settle(window, size);
                Element<Grid>(window, "ChatPanel").Visibility = Visibility.Visible;
                Element<Grid>(window, "ChangelogPanel").Visibility = Visibility.Collapsed;
                composer.IsGenerating = false;
                Settle(window, size);
                Assert.Equal(draft, input.Text);
                Assert.Equal(5, input.SelectionStart);
                Assert.Equal(12, input.SelectionLength);
                Assert.Same(input, window.FindName("ChatMessageTextBox"));
            }
            return true;
        });
    }

    private static FrameworkElement Glyph(Button action, string name) =>
        Assert.IsAssignableFrom<FrameworkElement>(action.Template.FindName(name, action));
    private static T Element<T>(Window window, string name) where T : FrameworkElement => Assert.IsType<T>(window.FindName(name));
    private static Rect Bounds(FrameworkElement element, Visual parent) =>
        element.TransformToAncestor(parent).TransformBounds(new Rect(new Point(), element.RenderSize));
    private static void Settle(Window window, Size size)
    {
        WindowCanvasTests.LayoutWindow(window, size);
        Dispatcher.CurrentDispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);
        WindowCanvasTests.LayoutWindow(window, size);
    }
}
