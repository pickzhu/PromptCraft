using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;

namespace LiveMarkdown.Avalonia;

/// <summary>
/// Represents a code block in a Markdown document.
/// This control is used to display code snippets with optional syntax highlighting.
/// </summary>
[TemplatePart(CodeTextBlockName, typeof(MarkdownTextBlock), IsRequired = true)]
[TemplatePart(ScrollViewerName, typeof(ScrollViewer), IsRequired = false)]
[TemplatePart(LanguageTextBlockName, typeof(TextBlock), IsRequired = false)]
[TemplatePart(ToggleTextWrapButtonName, typeof(ToggleButton), IsRequired = false)]
[TemplatePart(CopyButtonName, typeof(Button), IsRequired = false)]
public class CodeBlock : TemplatedControl
{
    private const string ScrollViewerName = "PART_ScrollViewer";
    private const string CodeTextBlockName = "PART_CodeTextBlock";
    private const string LanguageTextBlockName = "PART_LanguageTextBlock";
    private const string ToggleTextWrapButtonName = "PART_ToggleTextWrapButton";
    private const string CopyButtonName = "PART_CopyButton";

    /// <summary>
    /// Defines the <see cref="Language"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeBlock, string?>(nameof(Language));

    /// <summary>
    /// Gets or sets the programming language of the code block.
    /// </summary>
    public string? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    public InlineCollection Inlines { get; } = new();

    /// <summary>
    /// Defines the <see cref="CopyingToClipboard"/> event.
    /// Handle this event to perform custom actions when the code block is copying to clipboard and prevent the default behavior by setting <see cref="RoutedEventArgs.Handled"/> to true.
    /// </summary>
    public static readonly RoutedEvent<RoutedEventArgs> CopyingToClipboardEvent =
        RoutedEvent.Register<TextBox, RoutedEventArgs>(nameof(CopyingToClipboard), RoutingStrategies.Bubble);

    /// <summary>
    /// Raised when the code block is copying to clipboard.
    /// </summary>
    public event EventHandler<RoutedEventArgs>? CopyingToClipboard
    {
        add => AddHandler(CopyingToClipboardEvent, value);
        remove => RemoveHandler(CopyingToClipboardEvent, value);
    }

    internal MarkdownTextBlock? CodeTextBlock { get; private set; }

    private ScrollViewer? _scrollViewer;
    private IDisposable? _toggleTextWrapButtonIsCheckedChangedSubscription;
    private IDisposable? _copyButtonClickedSubscription;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        if (_toggleTextWrapButtonIsCheckedChangedSubscription is not null)
        {
            _toggleTextWrapButtonIsCheckedChangedSubscription.Dispose();
            _toggleTextWrapButtonIsCheckedChangedSubscription = null;
        }

        if (_copyButtonClickedSubscription is not null)
        {
            _copyButtonClickedSubscription.Dispose();
            _copyButtonClickedSubscription = null;
        }

        CodeTextBlock = e.NameScope.Find<MarkdownTextBlock>(CodeTextBlockName);
        if (CodeTextBlock is null)
        {
            throw new InvalidOperationException($"{CodeTextBlockName} is not found in the template.");
        }

        CodeTextBlock.Inlines = Inlines;

        _scrollViewer = e.NameScope.Find<ScrollViewer>(ScrollViewerName);

        if (e.NameScope.Find<ToggleButton>(ToggleTextWrapButtonName) is { } toggleTextWrapButton)
        {
            _toggleTextWrapButtonIsCheckedChangedSubscription = toggleTextWrapButton.AddDisposableHandler(
                ToggleButton.IsCheckedChangedEvent,
                HandleToggleTextWrapButtonIsCheckedChanged,
                RoutingStrategies.Bubble,
                true);
        }

        if (e.NameScope.Find<Button>(CopyButtonName) is { } copyButton)
        {
            _copyButtonClickedSubscription = copyButton.AddDisposableHandler(
                Button.ClickEvent,
                HandleCopyButtonClick,
                RoutingStrategies.Bubble,
                true);
        }
    }

    private void HandleToggleTextWrapButtonIsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (CodeTextBlock is null) return;

        var isChecked = (sender as ToggleButton)?.IsChecked ?? false;
        CodeTextBlock.TextWrapping = isChecked ? TextWrapping.Wrap : TextWrapping.NoWrap;
        if (_scrollViewer is not null)
        {
            _scrollViewer.HorizontalScrollBarVisibility = isChecked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            CodeTextBlock.Width = _scrollViewer.Viewport.Width;
            CodeTextBlock.UpdateLayout(); // fix bug that the text block does not resize correctly
            CodeTextBlock.Width = double.NaN;
        }
    }

    private void HandleCopyButtonClick(object? sender, RoutedEventArgs e)
    {
        var copyEventArgs = new RoutedEventArgs(CopyingToClipboardEvent);
        RaiseEvent(copyEventArgs);
        if (copyEventArgs.Handled) return;

        var text = Inlines.Text;
        if (string.IsNullOrEmpty(text)) return;

        TopLevel.GetTopLevel(this)?.Clipboard?.SetValueAsync(DataFormat.Text, text);
    }
}