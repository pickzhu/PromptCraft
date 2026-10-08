[![netstandard2.0](https://img.shields.io/badge/netstandard-2.0-blue.svg)](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
[![Avalonia](https://img.shields.io/badge/Avalonia-11-blue.svg)](https://avaloniaui.net/)
[![License](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)
[![GitHub issues](https://img.shields.io/github/issues/DearVa/LiveMarkdown.Avalonia.svg)](https://github.com/DearVa/LiveMarkdown.Avalonia/issues)
[![NuGet](https://img.shields.io/nuget/v/LiveMarkdown.Avalonia.svg)](https://www.nuget.org/packages/LiveMarkdown.Avalonia/)

![demo.gif](https://raw.githubusercontent.com/DearVa/LiveMarkdown.Avalonia/main/img/demo.gif)

## 👋 Introduction

`LiveMarkdown.Avalonia` is a High-performance Markdown viewer for Avalonia applications.
It supports **real-time rendering** of Markdown content, so it's ideal for applications that require dynamic text updating, **especially when streaming large model outputs**.

## ⭐ Features

- 🚀 **High-performance rendering powered by [Markdig](https://github.com/xoofx/markdig)**
- 🔄 **Real-time updates**: Automatically re-renders changes in Markdown content
- 🎨 **Customizable styles**: Easily style Markdown elements using Avalonia's powerful styling system
- 🔗 **Hyperlink support**: Clickable links with customizable behavior
- 📊 **Table support**: Render tables with proper formatting
- 📜 **Code block syntax highlighting**: Supports multiple languages with [ColorCode](https://github.com/CommunityToolkit/ColorCode-Universal)
- 🖼️ **Image support**: Load online, local even `avares` images asynchronously
- ✍️ **Selectable text**: Text can be selected across different Markdown elements

> [!NOTE]
> This library currently only supports `Append` and `Clear` operations on the Markdown content, which is enough for LLM streaming scenarios.

## ✈️ Roadmap

- [x] Basic Markdown rendering
- [x] Real-time updates
- [x] Hyperlink support
- [x] Table support
- [x] Code block syntax highlighting
- [x] Image support
  - [x] Bitmap
  - [x] SVG
  - [x] Online images
  - [x] Local images
  - [x] `avares` images
- [x] Selectable text across elements
- [ ] LaTeX support
- [ ] HTML rendering

## 🚀 Getting Started

### 1. Install the NuGet package

You can install the latest version from NuGet CLI:

```bash
dotnet add package LiveMarkdown.Avalonia
```

or use the NuGet Package Manager in your IDE.

### 2. Register the Markdown styles in your Avalonia application

```xml
<Application
  x:Class="YourAppClass" xmlns="https://github.com/avaloniaui"
  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" RequestedThemeVariant="Default">

  <Application.Styles>
    <!-- Your other styles here -->
    <StyleInclude Source="avares://LiveMarkdown.Avalonia/Styles.axaml"/>
  </Application.Styles>

  <Application.Resources>
    <!-- Your other resources here -->
    <Color x:Key="BorderColor">#3DFFFFFF</Color>
    <Color x:Key="ForegroundColor">#FFFFFF</Color>
    <Color x:Key="CardBackgroundColor">#15000000</Color>
    <Color x:Key="SecondaryCardBackgroundColor">#99000000</Color>
  </Application.Resources>
</Application>
```

### 3. Use the `MarkdownRenderer` control in your XAML

Add the `MarkdownRenderer` control to your `.axaml` file:
```xml
<YourControl
  xmlns:md="clr-namespace:LiveMarkdown.Avalonia;assembly=LiveMarkdown.Avalonia">
  <md:MarkdownRenderer x:Name="MarkdownRenderer"/>
</YourControl>
```

Then you can manage the Markdown content in your code-behind:

```csharp
// ObservableStringBuilder is used for efficient string updates
var markdownBuilder = new ObservableStringBuilder();
MarkdownRenderer.MarkdownBuilder = markdownBuilder;

// Append Markdown content, this will trigger re-rendering
markdownBuilder.Append("# Hello, Markdown!");
markdownBuilder.Append("\n\nThis is a **live** Markdown viewer for Avalonia applications.");

// Clear the content
markdownBuilder.Clear();
```

If you want to load local images with relative paths, you can set the `MarkdownRenderer.ImageBasePath` property.

## 🪄 Style Customization

Markdown elements can be styled using Avalonia's powerful styling system. You can override the [default styles](https://github.com/DearVa/LiveMarkdown.Avalonia/blob/main/src/LiveMarkdown.Avalonia/Styles.axaml) by defining your own styles in your application styles.

Avalonia Styling Docs: 
- [Avalonia Styles](https://docs.avaloniaui.net/docs/styling)
- [Style selector syntax](https://docs.avaloniaui.net/docs/reference/styles/style-selector-syntax)

## 🤔 FAQ

- Q: Why some emojis not rendered correctly (rendered in single color)?
- A: This is a known issue caused by Skia (the render backend of Avalonia). You can upgrade SkiaSharp version (e.g. >= 3.117.0) to fix this. [Related issue](https://github.com/AvaloniaUI/Avalonia/issues/18677)

## 🤝 Contributing

We welcome issues, feature ideas, and PRs! See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

## 📄 License

Distributed under the Apache 2.0 License. See [LICENSE](LICENSE) for more information.

### Third-Party Licenses

- **markdig** - [BSD-2-Clause License](https://github.com/xoofx/markdig/blob/master/license.txt)
    - Markdown parser for Everywhere.Markdown rendering
    - Source repo: https://github.com/xoofx/markdig
- **Svg.Skia** - [MIT License](https://github.com/wieslawsoltes/Svg.Skia/blob/master/LICENSE.TXT)
    - Svg rendering for images
    - Source repo: https://github.com/wieslawsoltes/Svg.Skia
- **TextMateSharp** - [MIT License](https://github.com/danipen/TextMateSharp/blob/master/LICENSE.md)
    - Syntax highlighting for code blocks
    - Source repo: https://github.com/danipen/TextMateSharp

