using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using PromptCraft.UILib.Markdown;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Runtime.CompilerServices;
using AvaloniaDocs = Avalonia.Controls.Documents;

namespace LiveMarkdown.Avalonia;

public abstract class MarkdownNode
{
    /// <summary>
    /// Gets the text block associated with this node, if any.
    /// </summary>
    protected virtual MarkdownTextBlock? TextBlock => null;

    /// <summary>
    /// records the source span of the block in the Markdown document.
    /// </summary>
    private SourceSpan span;

    private bool IsDirty(MarkdownObject markdownObject, in ObservableStringBuilderChangedEventArgs change)
    {
        return !span.Equals(markdownObject.Span) || span.End >= change.StartIndex && change.StartIndex + change.Length > span.Start;
    }

    public bool Update(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        // type check
        if (!IsCompatible(markdownObject))
        {
            return false;
        }

        if (!IsDirty(markdownObject, change))
        {
            // No need to update, the change does not affect this node
            return true;
        }

        var result = UpdateCore(documentNode, markdownObject, change, cancellationToken);
        span = markdownObject.Span;

        if (TextBlock is { } textBlock)
        {
            if (result)
            {
                textBlock.SourceSpan = span;
                documentNode.textBlocks.Add(textBlock);
            }
            else
            {
                documentNode.textBlocks.Remove(textBlock);
            }
        }

        MarkdownRenderer.VerboseLogger?.Log(
            this,
            "Updated {NodeName} {Result} {Span}",
            markdownObject.GetType().Name,
            result,
            markdownObject.Span);
        return result;
    }

    protected abstract bool IsCompatible(MarkdownObject markdownObject);

    /// <summary>
    /// Updates the block with the given inlines and change information.
    /// </summary>
    /// <param name="documentNode"></param>
    /// <param name="markdownObject"></param>
    /// <param name="change"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>true if the block was updated successfully, false if it needs to be removed</returns>
    protected abstract bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken);
}

public abstract class InlineNode : MarkdownNode
{
    public abstract AvaloniaDocs.Inline Inline { get; }

    public Classes Classes => Inline.Classes;

    protected static InlineNode CreateInlineNode(
        DocumentNode documentNode,
        Inline inline,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        InlineNode node = inline switch
        {
            LiteralInline => new FuncInlineNode<LiteralInline, AvaloniaDocs.Run>((literal, run) =>
            {
                run.Text = literal.Content.ToString();
                return true;
            })
            {
                Classes = { "Literal" }
            },
            LineBreakInline => new FuncInlineNode<LineBreakInline, AvaloniaDocs.LineBreak>((_, _) => true)
            {
                Classes = { "LineBreak" }
            },
            AutolinkInline => new FuncInlineNode<AutolinkInline, InlineHyperlink>((autolink, inlineHyperlink) =>
            {
                Uri.TryCreate(autolink.Url, UriKind.RelativeOrAbsolute, out var uri);
                inlineHyperlink.HRef = uri;

                if (inlineHyperlink.Inlines is [AvaloniaDocs.Run run]) run.Text = autolink.Url;
                else
                {
                    inlineHyperlink.Inlines.Clear();
                    inlineHyperlink.Inlines.Add(
                        new AvaloniaDocs.Run
                        {
                            Classes = { "Autolink" },
                            Text = autolink.Url
                        });
                }

                return true;
            })
            {
                Classes = { "AutoLink" }
            },
            DelimiterInline => new FuncInlineNode<DelimiterInline, AvaloniaDocs.Run>((delimiter, run) =>
            {
                run.Text = delimiter.ToLiteral();
                return true;
            })
            {
                Classes = { "Delimiter" }
            },
            TaskList => new FuncInlineNode<TaskList, AvaloniaDocs.InlineUIContainer>((taskList, inlineUIContainer) =>
            {
                if (inlineUIContainer.Child is not CheckBox checkBox)
                {
                    inlineUIContainer.Child = checkBox = new CheckBox
                    {
                        Classes = { "TaskList" },
                        IsEnabled = false
                    };
                }
                checkBox.IsChecked = taskList.Checked;
                return true;
            })
            {
                Classes = { "TaskList" }
            },
            HtmlEntityInline => new FuncInlineNode<HtmlEntityInline, AvaloniaDocs.Run>((htmlEntity, run) =>
            {
                run.Text = htmlEntity.Transcoded.ToString();
                return true;
            })
            {
                Classes = { "HtmlEntity" }
            },
            CodeInline => new CodeInlineNode(),
            LinkInline => new LinkInlineNode(),
            EmphasisInline => new EmphasisInlineNode(),
            ContainerInline => new ContainerInlineNode(),
            _ => new NotImplementedInlineNode(inline)
        };
        node.Update(documentNode, inline, change, cancellationToken);
        return node;
    }
}

public class FuncInlineNode<TInline, TAvaloniaInline>(Func<TInline, TAvaloniaInline, bool> updater) : InlineNode
    where TInline : Inline
    where TAvaloniaInline : AvaloniaDocs.Inline, new()
{
    public override AvaloniaDocs.Inline Inline => inline;

    private readonly TAvaloniaInline inline = new();

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject is TInline;
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return updater((TInline)markdownObject, inline);
    }
}

public class InlinesNode : InlineNode
{
    protected override MarkdownTextBlock? TextBlock { get; }

    public override AvaloniaDocs.Inline Inline { get; }

    public AvaloniaDocs.InlineCollection Inlines { get; }

    private readonly MarkdownRenderer.InlinesProxy proxy;

    public InlinesNode(AvaloniaDocs.Span span) : this(span, span.Inlines) { }

    protected InlinesNode(InlineHyperlink inlineHyperlink) : this(inlineHyperlink, inlineHyperlink.Inlines)
    {
        TextBlock = inlineHyperlink.TextBlock;
    }

    private InlinesNode(AvaloniaDocs.Inline inline, AvaloniaDocs.InlineCollection inlines)
    {
        Inline = inline;
        Inlines = inlines;
        proxy = new MarkdownRenderer.InlinesProxy(inlines);
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject is IEnumerable<Inline>;
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var i = -1;
        foreach (var inline in (IEnumerable<Inline>)markdownObject)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Add new inline
            if (proxy.Count > ++i)
            {
                // Update existing inline
                var oldInlineNode = proxy[i];

                // if Update returned true, it means the block was updated successfully
                if (oldInlineNode.Update(documentNode, inline, change, cancellationToken)) continue;

                // else, remove the old node and create a new one
                var newInlineNode = CreateInlineNode(documentNode, inline, change, cancellationToken);
                proxy[i] = newInlineNode;
            }
            else
            {
                var newInlineNode = CreateInlineNode(documentNode, inline, change, cancellationToken);
                proxy.Add(newInlineNode);
            }
        }

        while (proxy.Count > i + 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            proxy.RemoveAt(proxy.Count - 1);
        }

        return i >= 0; // Return true if at least one inline was processed
    }
}

public class CodeInlineNode : InlineNode
{
    protected override MarkdownTextBlock TextBlock => textBlock;

    public override AvaloniaDocs.Inline Inline => inlineUIContainer;

    private readonly AvaloniaDocs.InlineUIContainer inlineUIContainer;
    private readonly MarkdownTextBlock textBlock;

    public CodeInlineNode()
    {
        inlineUIContainer = new AvaloniaDocs.InlineUIContainer
        {
            Classes = { "Code" },
            Child = new Border
            {
                Classes = { "Code" },
                Child = textBlock = new MarkdownTextBlock
                {
                    Classes = { "Code" }
                }
            }
        };
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(CodeInline);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var code = Unsafe.As<CodeInline>(markdownObject);
        textBlock.Text = code.Content;
        return true;
    }
}

public class LinkInlineNode() : InlinesNode(
    new InlineHyperlink
    {
        Classes = { "Link" }
    })
{
    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(LinkInline);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var linkInline = Unsafe.As<LinkInline>(markdownObject);
        if (linkInline.Url == null) return false;

        var inlineHyperlink = Unsafe.As<InlineHyperlink>(Inline);
        Uri.TryCreate(linkInline.Url, UriKind.RelativeOrAbsolute, out var uri);

        if (linkInline.IsImage)
        {
            Image img;
            if (inlineHyperlink.Image is { } image)
            {
                img = image;
            }
            else
            {
                inlineHyperlink.Image = img = new Image
                {
                    Classes = { "Link" },
                };
            }

            if (uri is { IsAbsoluteUri: false })
            {
                if (documentNode.Owner.ImageBasePath is { } imageBasePath)
                {
                    // If the URL is a relative path, combine it with the base path
                    Uri.TryCreate(Path.GetFullPath(Path.Combine(imageBasePath, linkInline.Url)), UriKind.Absolute, out uri);
                }
                else
                {
                    // If no base path is set, set the URI to null, preventing unexpected behavior
                    uri = null;
                }
            }

            inlineHyperlink.HRef = uri;
            AsyncImageLoader.SetSource(img, uri?.ToString());
        }
        else
        {
            inlineHyperlink.HRef = uri;
            inlineHyperlink.Image = null;
            base.UpdateCore(documentNode, markdownObject, change, cancellationToken);
        }

        return true;
    }
}

public class EmphasisInlineNode : ContainerInlineNode
{
    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(EmphasisInline);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var emphasisInline = Unsafe.As<EmphasisInline>(markdownObject);
        var span = (AvaloniaDocs.Span)Inline;
        switch (emphasisInline.DelimiterChar)
        {
            case '*' when emphasisInline.DelimiterCount == 2: // bold
            case '_' when emphasisInline.DelimiterCount == 2: // bold
                span.FontWeight = FontWeight.Bold;
                break;
            case '*': // italic
            case '_': // italic
                span.FontStyle = FontStyle.Italic;
                break;
            case '~': // 2x strike through, 1x subscript
                if (emphasisInline.DelimiterCount == 2)
                    span.TextDecorations = TextDecorations.Strikethrough;
                else
                    span.BaselineAlignment = BaselineAlignment.Subscript;
                break;
            case '^': // 1x superscript
                span.BaselineAlignment = BaselineAlignment.Superscript;
                break;
            case '+': // 2x underline
                span.TextDecorations = TextDecorations.Underline;
                break;
            case '=': // 2x Marked
                // documentNode: Implement Marked
                break;
        }

        return base.UpdateCore(documentNode, markdownObject, in change, cancellationToken);
    }
}

public class ContainerInlineNode() : InlinesNode(new AvaloniaDocs.Span())
{
    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject is ContainerInline and not EmphasisInline; // EmphasisInline is handled separately
    }
}

public class NotImplementedInlineNode(Inline markdownInline) : InlineNode
{
    public override AvaloniaDocs.Inline Inline { get; } = new AvaloniaDocs.Run
    {
        Classes = { "NotImplementedInline" }
    };

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == markdownInline.GetType();
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        return true;
    }
}

public abstract class BlockNode : MarkdownNode
{
    public abstract Control Control { get; }

    public Classes Classes => Control.Classes;

    protected static BlockNode CreateBlockNode(
        DocumentNode documentNode,
        Block block,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        BlockNode node = block switch
        {
            Table => new TableNode(),
            TableCell => new TableCellNode(),
            ListBlock => new ListBlockNode(),
            Markdig.Syntax.CodeBlock => new CodeBlockNode(),
            QuoteBlock => new QuoteBlockNode(),
            HeadingBlock => new HeadingBlockNode(),
            ParagraphBlock => new ParagraphBlockNode(),
            ContainerBlock => new ContainerBlockNode(),
            ThematicBreakBlock => new ThematicBreakBlockNode(),
            _ => new NotImplementedBlockNode(block)
        };
        node.Update(documentNode, block, change, cancellationToken);
        return node;
    }
}

/// <summary>
/// Works as <see cref="MarkdownTextBlock"/>
/// </summary>
public class InlineCollectionNode : BlockNode
{
    protected override MarkdownTextBlock TextBlock => textBlock;

    public override Control Control => textBlock;

    private readonly InlinesNode inlinesNode;
    private readonly MarkdownTextBlock textBlock;

    public InlineCollectionNode()
    {
        inlinesNode = new InlinesNode(new AvaloniaDocs.Span());
        textBlock = new MarkdownTextBlock
        {
            Inlines = inlinesNode.Inlines
        };
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject is IEnumerable<Inline>;
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        return inlinesNode.Update(
            documentNode,
            markdownObject,
            change,
            cancellationToken);
    }
}

public class TableNode : BlockNode
{
    public override Control Control { get; }

    private readonly Grid container;
    private readonly MarkdownRenderer.BlocksProxy proxy;

    public TableNode()
    {
        container = new Grid();
        proxy = new MarkdownRenderer.BlocksProxy(container.Children);
        Control = new ScrollViewer
        {
            Classes = { "Table" },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border
            {
                Classes = { "Table" },
                Child = container
            }
        };
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(Table);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var table = Unsafe.As<Table>(markdownObject);
        if (table.ColumnDefinitions.Count == 0) return false;

        while (table.ColumnDefinitions.Count < container.ColumnDefinitions.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            container.ColumnDefinitions.RemoveAt(container.ColumnDefinitions.Count - 1);
        }
        while (table.ColumnDefinitions.Count > container.ColumnDefinitions.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var rowIndex = 0;
        var cellIndex = 0;
        foreach (var row in table.OfType<TableRow>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rowIndex >= container.RowDefinitions.Count)
            {
                container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            foreach (var (cell, columnIndex) in row.OfType<TableCell>().Select((c, i) => (c, i)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                Control cellControl;
                do
                {
                    if (proxy.Count > cellIndex)
                    {
                        // existing item block node, update it
                        var oldCellBlockNode = proxy[cellIndex];
                        cellControl = oldCellBlockNode.Control;

                        // if Update returned true; it means the block was updated successfully
                        if (oldCellBlockNode.Update(documentNode, cell, change, cancellationToken)) break;

                        // else, remove the old node and create a new one
                        var newCellBlockNode = CreateBlockNode(documentNode, cell, change, cancellationToken);
                        proxy[cellIndex] = newCellBlockNode;
                        cellControl = newCellBlockNode.Control;
                    }
                    else
                    {
                        var newCellBlockNode = CreateBlockNode(documentNode, cell, change, cancellationToken);
                        proxy.Add(newCellBlockNode);
                        cellControl = newCellBlockNode.Control;
                    }
                }
                while (false);

                cellIndex++;
                Grid.SetRow(cellControl, rowIndex);
                Grid.SetColumn(cellControl, columnIndex);

                if (row.IsHeader)
                {
                    if (!cellControl.Classes.Contains("Header"))
                    {
                        cellControl.Classes.Add("Header");
                    }
                }
                else
                {
                    cellControl.Classes.Remove("Header");
                }

                if (columnIndex >= table.ColumnDefinitions.Count) continue;
                if (cellControl is not Border { Child: { } child }) continue;
                var columnDefinition = table.ColumnDefinitions[columnIndex];
                child.HorizontalAlignment = columnDefinition.Alignment switch
                {
                    TableColumnAlign.Left => HorizontalAlignment.Left,
                    TableColumnAlign.Center => HorizontalAlignment.Center,
                    TableColumnAlign.Right => HorizontalAlignment.Right,
                    _ => HorizontalAlignment.Stretch
                };
            }

            rowIndex++;
        }

        var columnCount = table.ColumnDefinitions.Count;
        var cellCount = rowIndex * columnCount;
        while (proxy.Count > cellCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            proxy.RemoveAt(proxy.Count - 1);
        }

        if (rowIndex == 0 || columnCount == 0)
        {
            return false;
        }

        while (rowIndex < container.RowDefinitions.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            container.RowDefinitions.RemoveAt(container.RowDefinitions.Count - 1);
        }

        return true;
    }
}

public class TableCellNode : ContainerBlockNode
{
    public TableCellNode()
    {
        Classes.Add("TableCell");
        control = new Border
        {
            Classes = { "TableCell" },
            Child = base.Control
        };
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(TableCell);
    }
}

public class ListBlockNode : BlockNode
{
    public override Control Control => grid;

    private readonly Grid grid;
    private readonly MarkdownRenderer.BlocksProxy proxy;

    public ListBlockNode()
    {
        grid = new Grid
        {
            Classes = { "ListBlock" },
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition()
            }
        };
        proxy = new MarkdownRenderer.BlocksProxy(grid.Children);
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(ListBlock);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var listBlock = Unsafe.As<ListBlock>(markdownObject);
        if (listBlock.Count == 0) return false;

        var number = 1;
        for (var i = 0; i < listBlock.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (grid.RowDefinitions.Count <= i)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            var itemBlock = listBlock[i];
            var numberIndex = i * 2;

            // number part
            if (listBlock.IsOrdered)
            {
                // number is no need to be selected
                TextBlock numberControl;
                if (proxy.Count > numberIndex && proxy[numberIndex].Control is TextBlock existingNumberControl)
                {
                    // existing number block node, update it
                    numberControl = existingNumberControl;
                }
                else
                {
                    // create a new number block node
                    numberControl = new TextBlock
                    {
                        Classes = { "ListBlockNumber" },
                    };

                    if (proxy.Count > numberIndex)
                    {
                        // replace the existing number block node
                        proxy.SetControlAt(numberIndex, numberControl);
                    }
                    else
                    {
                        // add a new number block node
                        proxy.Add(numberControl);
                    }
                }

                Grid.SetRow(numberControl, i);
                Grid.SetColumn(numberControl, 0);
                numberControl.Text = $"{number++}.";
            }
            else
            {
                Border bulletIcon;
                if (proxy.Count > numberIndex && proxy[numberIndex].Control is Border existingBulletIcon)
                {
                    // existing bullet block node, update it
                    bulletIcon = existingBulletIcon;
                }
                else
                {
                    // create a new bullet block node
                    bulletIcon = new Border
                    {
                        Classes = { "ListBlockBullet" }
                    };

                    if (proxy.Count > numberIndex)
                    {
                        proxy.SetControlAt(numberIndex, bulletIcon);
                    }
                    else
                    {
                        proxy.Add(bulletIcon);
                    }
                }

                Grid.SetRow(bulletIcon, i);
                Grid.SetColumn(bulletIcon, 0);
                bulletIcon.Classes.EnsureClassName("Level", (listBlock.Column / 2) % 4);
            }

            // item part
            var itemIndex = i * 2 + 1;
            if (proxy.Count > itemIndex)
            {
                // existing item block node, update it
                var oldItemBlockNode = proxy[itemIndex];

                // if Update returned true, it means the block was updated successfully
                if (oldItemBlockNode.Update(documentNode, itemBlock, change, cancellationToken)) continue;

                // else, remove the old node and create a new one
                var newItemBlockNode = CreateBlockNode(documentNode, itemBlock, change, cancellationToken);
                proxy[itemIndex] = newItemBlockNode;
                Grid.SetRow(newItemBlockNode.Control, i);
                Grid.SetColumn(newItemBlockNode.Control, 1);
            }
            else
            {
                var newItemBlockNode = CreateBlockNode(documentNode, itemBlock, change, cancellationToken);
                proxy.Add(newItemBlockNode);
                Grid.SetRow(newItemBlockNode.Control, i);
                Grid.SetColumn(newItemBlockNode.Control, 1);
            }
        }

        while (proxy.Count > listBlock.Count * 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            proxy.RemoveAt(proxy.Count - 1);
        }

        while (grid.RowDefinitions.Count > listBlock.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            grid.RowDefinitions.RemoveAt(grid.RowDefinitions.Count - 1);
        }

        return true;
    }
}

public class CodeBlockNode : BlockNode
{
    protected override MarkdownTextBlock? TextBlock => _codeBlock.CodeTextBlock;

    public override Control Control { get; }

    private readonly CodeBlock _codeBlock;
    private SyntaxHighlighting? syntaxHighlighting;

    private string? _oldLanguage = string.Empty;

    public CodeBlockNode()
    {
        Control = _codeBlock = new CodeBlock
        {
            Classes = { "CodeBlock" }
        };
        _codeBlock.ApplyTemplate(); // Ensure the template is applied to initialize the CodeTextBlock
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject is Markdig.Syntax.CodeBlock;
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var codeBlock = Unsafe.As<Markdig.Syntax.CodeBlock>(markdownObject);
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (codeBlock.Lines.Lines is null) return false;

        var inlines = _codeBlock.Inlines;
        foreach (var (slice, lineIndex) in codeBlock.Lines.Lines.Take(codeBlock.Lines.Count).Select((l, i) => (l.Slice, i)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var inlineIndex = lineIndex * 2;

            // Skip if the slice is completely outside the change range
            //if (inlines.Count > inlineIndex &&
            //    (slice.End < change.StartIndex || change.StartIndex + change.Length <= slice.Start)) continue;

            if (inlines.Count <= inlineIndex)
            {
                if (inlines.Count % 2 == 1)
                {
                    // we need to add a LineBreak before the new Run
                    inlines.Add(new AvaloniaDocs.LineBreak());
                }

                inlines.Add(new AvaloniaDocs.Run(slice.ToString()));
            }
            else if (inlines[inlineIndex] is AvaloniaDocs.Run run)
            {
                // Update existing run
                run.Text = slice.ToString();
            }
            else
            {
                // Replace it with a new run if it's not a Run
                inlines[inlineIndex] = new AvaloniaDocs.Run(slice.ToString());
            }

            if (lineIndex < codeBlock.Lines.Count - 1)
            {
                // Add a line break after each line except the last one
                if (inlines.Count <= inlineIndex + 1)
                {
                    inlines.Add(new AvaloniaDocs.LineBreak());
                }
                else if (inlines[inlineIndex + 1] is not AvaloniaDocs.LineBreak)
                {
                    // Replace it with a LineBreak if it's not a LineBreak
                    inlines[inlineIndex + 1] = new AvaloniaDocs.LineBreak();
                }
            }
        }

        while (inlines.Count > codeBlock.Lines.Count * 2 - 1)
        {
            // Remove excess inlines
            inlines.RemoveAt(inlines.Count - 1);
        }

        // Highlighting only works for closed FencedCodeBlock with Info
        if (codeBlock is not FencedCodeBlock fencedCodeBlock) return true;
        _codeBlock.Language = fencedCodeBlock.Info;

        if (fencedCodeBlock is not { Info.Length: > 0 }) return true;
        cancellationToken.ThrowIfCancellationRequested();

        // FencedCodeBlock with Info, use syntax highlighting
        var languageName = fencedCodeBlock.Info.TrimEnd().ToLower();
        languageName = MarkdownLanguage.GetLanguage(languageName);
        if (_oldLanguage == languageName)
            syntaxHighlighting ??= new SyntaxHighlighting(languageName);
        else
        {
            syntaxHighlighting = new SyntaxHighlighting(languageName);
            _oldLanguage = languageName;
        }
        syntaxHighlighting.FormatInlines(inlines);
        return true;
    }
}

public class QuoteBlockNode : ContainerBlockNode
{
    public QuoteBlockNode()
    {
        Classes.Add("QuoteBlock");
        control = new Border
        {
            Classes = { "QuoteBlock" },
            Child = base.Control
        };
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(QuoteBlock);
    }
}

public class HeadingBlockNode : BlockNode
{
    public override Control Control { get; }

    private readonly InlineCollectionNode headingInlines;

    public HeadingBlockNode()
    {
        headingInlines = new InlineCollectionNode();
        Control = new Border
        {
            Classes = { "HeadingBlock" },
            Child = headingInlines.Control
        };
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(HeadingBlock);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var headingBlock = Unsafe.As<HeadingBlock>(markdownObject);
        if (headingBlock.Inline is null) return false;

        if (!headingInlines.Update(documentNode, headingBlock.Inline, change, cancellationToken)) return false;

        cancellationToken.ThrowIfCancellationRequested();
        headingInlines.Classes.EnsureClassName("Heading", headingBlock.Level);
        return true;
    }
}

public class ParagraphBlockNode : InlineCollectionNode
{
    public ParagraphBlockNode()
    {
        Classes.Add("ParagraphBlock");
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(ParagraphBlock);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var paragraphBlock = Unsafe.As<ParagraphBlock>(markdownObject);
        return paragraphBlock.Inline is not null && base.UpdateCore(documentNode, paragraphBlock.Inline, change, cancellationToken);
    }
}

public class ContainerBlockNode : BlockNode
{
    public override Control Control => control;

    protected Control control;

    protected readonly MarkdownRenderer.BlocksProxy proxy;

    public ContainerBlockNode()
    {
        var container = new StackPanel
        {
            Orientation = Orientation.Vertical
        };
        control = container;
        proxy = new MarkdownRenderer.BlocksProxy(container.Children);
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject is ContainerBlock and
            not MarkdownDocument and
            not QuoteBlock and
            not Table and
            not TableCell and
            not ListBlock;
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var containerBlock = Unsafe.As<ContainerBlock>(markdownObject);
        if (containerBlock.Count == 0) return false;

        var i = 0;
        for (; i < containerBlock.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = containerBlock[i];

            if (i < proxy.Count)
            {
                var oldNode = proxy[i];
                if (oldNode.Update(documentNode, block, change, cancellationToken)) continue;

                // if Update returned false, it means the block needs to be removed
                var newNode = CreateBlockNode(documentNode, block, change, cancellationToken);
                proxy[i] = newNode;
            }
            else
            {
                var newNode = CreateBlockNode(documentNode, block, change, cancellationToken);
                proxy.Add(newNode);
            }
        }

        for (var j = proxy.Count - 1; j >= i; j--)
        {
            cancellationToken.ThrowIfCancellationRequested();

            proxy.RemoveAt(j);
        }

        return true;
    }
}

public class ThematicBreakBlockNode : BlockNode
{
    public override Control Control { get; } = new Border
    {
        Classes = { "ThematicBreak" }
    };

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(ThematicBreakBlock);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        return true;
    }
}

public class NotImplementedBlockNode(Block markdownBlock) : BlockNode
{
    public override Control Control { get; } = new()
    {
        Classes = { "NotImplementedBlock" }
    };

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == markdownBlock.GetType();
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        return true;
    }
}

public class DocumentNode : ContainerBlockNode
{
    public MarkdownRenderer Owner { get; }

    public IReadOnlyCollection<MarkdownTextBlock> TextBlocks => textBlocks;

    internal readonly HashSet<MarkdownTextBlock> textBlocks = [];

    public DocumentNode(MarkdownRenderer owner)
    {
        Owner = owner;
        Classes.Add("MarkdownDocument");
    }

    protected override bool IsCompatible(MarkdownObject markdownObject)
    {
        return markdownObject.GetType() == typeof(MarkdownDocument);
    }

    protected override bool UpdateCore(
        DocumentNode documentNode,
        MarkdownObject markdownObject,
        in ObservableStringBuilderChangedEventArgs change,
        CancellationToken cancellationToken)
    {
        var result = base.UpdateCore(documentNode, markdownObject, in change, cancellationToken);

        // (#1) DocumentNode is the outest node, so if it has no children, we clear the proxy
        if (!result) proxy.Clear();

        return result;
    }
}