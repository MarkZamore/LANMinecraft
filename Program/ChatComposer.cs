using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Minecraft;

/// <summary>A draft editor's send/cancel state, independent of an assistant transport.</summary>
public sealed class ChatComposer : ContentControl
{
    public static readonly DependencyProperty PromptProperty = DependencyProperty.Register(
        nameof(Prompt), typeof(string), typeof(ChatComposer),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, StateChanged));
    public static readonly DependencyProperty IsServiceAvailableProperty = DependencyProperty.Register(
        nameof(IsServiceAvailable), typeof(bool), typeof(ChatComposer), new PropertyMetadata(false, StateChanged));
    public static readonly DependencyProperty IsGeneratingProperty = DependencyProperty.Register(
        nameof(IsGenerating), typeof(bool), typeof(ChatComposer), new PropertyMetadata(false, GenerationChanged));

    private static readonly DependencyPropertyKey IsActionEnabledPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsActionEnabled), typeof(bool), typeof(ChatComposer), new PropertyMetadata(false));
    public static readonly DependencyProperty IsActionEnabledProperty = IsActionEnabledPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey IsCancellationPendingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsCancellationPending), typeof(bool), typeof(ChatComposer), new PropertyMetadata(false));
    public static readonly DependencyProperty IsCancellationPendingProperty = IsCancellationPendingPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey ActionLabelPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ActionLabel), typeof(string), typeof(ChatComposer), new PropertyMetadata("Отправить"));
    public static readonly DependencyProperty ActionLabelProperty = ActionLabelPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey ActionToolTipPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ActionToolTip), typeof(string), typeof(ChatComposer), new PropertyMetadata("Помощник пока не подключён"));
    public static readonly DependencyProperty ActionToolTipProperty = ActionToolTipPropertyKey.DependencyProperty;

    public static readonly RoutedEvent SendRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(SendRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ChatComposer));
    public static readonly RoutedEvent CancelRequestedEvent = EventManager.RegisterRoutedEvent(
        nameof(CancelRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ChatComposer));
    public static readonly RoutedCommand ActionCommand = new(nameof(ActionCommand), typeof(ChatComposer));

    public ChatComposer()
    {
        Focusable = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        CommandBindings.Add(new CommandBinding(ActionCommand, (_, e) =>
        {
            InvokeAction();
            e.Handled = true;
        }, (_, e) =>
        {
            e.CanExecute = IsActionEnabled;
            e.Handled = true;
        }));
    }

    public string Prompt { get => (string)GetValue(PromptProperty); set => SetValue(PromptProperty, value); }
    public bool IsServiceAvailable { get => (bool)GetValue(IsServiceAvailableProperty); set => SetValue(IsServiceAvailableProperty, value); }
    public bool IsGenerating { get => (bool)GetValue(IsGeneratingProperty); set => SetValue(IsGeneratingProperty, value); }
    public bool IsActionEnabled => (bool)GetValue(IsActionEnabledProperty);
    public bool IsCancellationPending => (bool)GetValue(IsCancellationPendingProperty);
    public string ActionLabel => (string)GetValue(ActionLabelProperty);
    public string ActionToolTip => (string)GetValue(ActionToolTipProperty);

    public event RoutedEventHandler SendRequested { add => AddHandler(SendRequestedEvent, value); remove => RemoveHandler(SendRequestedEvent, value); }
    public event RoutedEventHandler CancelRequested { add => AddHandler(CancelRequestedEvent, value); remove => RemoveHandler(CancelRequestedEvent, value); }

    /// <summary>The owner starts/cancels its real operation; this view never fabricates a response.</summary>
    public bool InvokeAction()
    {
        if (!IsActionEnabled) return false;
        if (IsGenerating)
        {
            SetValue(IsCancellationPendingPropertyKey, true);
            UpdateAction();
            RaiseEvent(new RoutedEventArgs(CancelRequestedEvent, this));
        }
        else
        {
            RaiseEvent(new RoutedEventArgs(SendRequestedEvent, this));
        }
        return true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.OriginalSource is TextBox && e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!IsGenerating) InvokeAction();
            e.Handled = true;
        }
    }

    private static void StateChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args) =>
        ((ChatComposer)owner).UpdateAction();

    private static void GenerationChanged(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        var composer = (ChatComposer)owner;
        composer.SetValue(IsCancellationPendingPropertyKey, false);
        composer.UpdateAction();
    }

    private void UpdateAction()
    {
        SetValue(IsActionEnabledPropertyKey, IsGenerating ? !IsCancellationPending : IsServiceAvailable && !string.IsNullOrWhiteSpace(Prompt));
        SetValue(ActionLabelPropertyKey, IsGenerating ? IsCancellationPending ? "Остановка…" : "Остановить" : "Отправить");
        SetValue(ActionToolTipPropertyKey, IsGenerating ? ActionLabel : !IsServiceAvailable ? "Помощник пока не подключён" : "Отправить сообщение");
        CommandManager.InvalidateRequerySuggested();
    }
}
