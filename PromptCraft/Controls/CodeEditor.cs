using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using System;
using TextMateSharp.Grammars;

namespace PromptCraft.Controls;

public class CodeEditor : TextEditor
{
    public static readonly StyledProperty<string?> LanguageProperty =
        AvaloniaProperty.Register<CodeEditor, string?>(nameof(Language));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TextEditor, string?>(nameof(Text));
    private bool _isFirst = true;
    protected override void OnInitialized()
    {
        if (!string.IsNullOrEmpty(Text))
            Document.Text = Text;
        _isFirst = false;
        base.OnInitialized();
        UpdateEditorTheme();
    }

    public new string? Text
    {
        get => GetValue(TextProperty);
        set
        {
            SetValue(TextProperty, value);
        }
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);
    public string? Language
    {
        get => GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }
    public CodeEditor()
    {
        ShowLineNumbers = true;
        FontFamily = FontFamily.Parse("Consolas");
        FlowDirection = FlowDirection.LeftToRight;

        ActualThemeVariantChanged += (_, _) => UpdateEditorTheme();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == LanguageProperty)
        {
            UpdateEditorTheme();
        }
        else if (change.Property == TextProperty)
        {
            // 避免死循环：只有当外部绑定写入时才更新 Document
            if (Document != null && Document.Text != Text)
            {
                Document.Text = Text ?? string.Empty;
            }
        }

        base.OnPropertyChanged(change);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        if (_isFirst) return;
        base.OnTextChanged(e);
        SetValue(TextProperty, Document.Text);
    }

    private void UpdateEditorTheme()
    {
        var languageId = Language;

        if (string.IsNullOrEmpty(languageId))
        {
            return;
        }

        var theme = ActualThemeVariant == ThemeVariant.Light
                    ? ThemeName.LightPlus
                    : ThemeName.DarkPlus;

        var options = new RegistryOptions(theme);

        var installation = this.InstallTextMate(options);

        installation.SetGrammar(options.GetScopeByLanguageId(languageId));
    }
}
