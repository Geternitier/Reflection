using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyReaderAsync(string output)
    {
        ShowPage("reader"); await ReaderPage.InitializeAsync();
        await ReaderPage.VerifyAsync(output);
        await ReaderPage.VerifyLocalImagesAsync(output);
        await ReaderPage.VerifyFootnotesAndWikiLinksAsync();
        var extras = Enumerable.Range(0, 1000).Select(i => new CharacterCard { Name = "虚拟角色 " + i }).ToArray();
        try
        {
            foreach (var card in extras) VM.Characters.Add(card);
            ShowPage("cards"); UpdateLayout();
            var oldWidth = Width; var panel = Descendants<GalleryPanel>(CharacterGallery).Single();
            Width = 900; UpdateLayout(); var narrowColumns = panel.ColumnCount;
            Width = 1920; UpdateLayout();
            if (panel.ColumnCount <= 3 || panel.ColumnCount <= narrowColumns) throw new InvalidOperationException("Gallery columns did not follow available width");
            await Task.Delay(220); App.Capture(this, Path.Combine(output, "gallery-wide.png"));
            Width = oldWidth; UpdateLayout();
            if (Descendants<PortraitFrame>(CharacterGallery).Count() > 30) throw new InvalidOperationException("Gallery realized the complete library");
            CardsPage.ScrollToBottom(); UpdateLayout();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle); UpdateLayout();
            await File.WriteAllTextAsync(Path.Combine(output, "gallery-metrics.txt"), Descendants<GalleryPanel>(CharacterGallery).Single().Metrics);
            var realizedCards = Descendants<PortraitFrame>(CharacterGallery).Count();
            if (realizedCards is 0 or > 30 || CardsPage.VerticalOffset <= 0) throw new InvalidOperationException("Gallery virtualization/extent failed");
            await Task.Delay(220); App.Capture(this, Path.Combine(output, "virtual-gallery.png"));
        }
        finally { foreach (var card in extras) VM.Characters.Remove(card); }
        if (VM.SelectedModel is not null)
        {
            await VM.NewChatAsync(); ShowPage("chat"); VM.Draft = "navigation slow reading";
            var generation = VM.SendAsync(); await Task.Delay(60);
            ShowPage("reader"); await Task.Delay(80);
            await generation;
            if (VM.Current.Messages.Last().Incomplete || VM.Current.Messages.Last().HasError || VM.Current.Messages.Last().Stopped) throw new InvalidOperationException("Reader interrupted background generation");
        }
        await VM.NewChatAsync(VM.Characters.First()); ShowPage("chat");
    }
}

public partial class ReaderView
{
    internal async Task VerifyFootnotesAndWikiLinksAsync()
    {
        var folder = Path.Combine(Owner.VM.Store.Root, "markdown-links");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "index.md");
        var sibling = Path.Combine(folder, "相邻文档.md");
        var spacer = string.Concat(Enumerable.Repeat("这是可滚动的正文。\n\n", 160));
        await File.WriteAllTextAsync(path, "# 开始\n\n链接[^note] 与 [[相邻文档#目标 章节|查看章节]]。\n\n"
            + spacer + "[^note]: 脚注内容。\n\n" + spacer);
        await File.WriteAllTextAsync(sibling, "# 起始章节\n\n" + spacer + "## 目标 章节\n\n" + spacer);
        await OpenDocumentAsync(path);
        Pages.UpdateLayout();
        var viewer = SmoothScroll.Viewer(Pages)!;

        Hyperlink FindLink(string label) => MainWindow.Descendants<ReadingText>(Pages)
            .SelectMany(text => text.Document.Blocks.OfType<Paragraph>()
                .Concat(text.Document.Blocks.OfType<Section>().SelectMany(section => section.Blocks.OfType<Paragraph>())))
            .SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>())
            .Single(link => new TextRange(link.ContentStart, link.ContentEnd).Text == label);

        async Task ClickAsync(string label)
        {
            FindLink(label).RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            await Dispatcher.InvokeAsync(Pages.UpdateLayout, DispatcherPriority.ContextIdle);
        }

        await ClickAsync("[1]");
        if (viewer.VerticalOffset <= viewer.ViewportHeight) throw new InvalidOperationException("Reader footnote did not scroll across document pieces");
        await ClickAsync("返回引用");
        if (viewer.VerticalOffset > 20) throw new InvalidOperationException("Reader footnote backlink did not return to the reference");
        await ClickAsync("查看章节");
        for (var attempt = 0; attempt < 30 && (document?.Path != sibling || viewer.VerticalOffset <= viewer.ViewportHeight); attempt++)
        {
            await Task.Delay(25);
            Pages.UpdateLayout();
        }
        if (document?.Path != sibling || viewer.VerticalOffset <= viewer.ViewportHeight)
            throw new InvalidOperationException("Relative wiki link did not open the target document at its heading");
    }

    internal async Task VerifyAsync(string output)
    {
        var fixture = Path.Combine(Owner.VM.Store.Root, "reader-fixture"); Directory.CreateDirectory(fixture);
        var md = Path.Combine(fixture, "阅读示例.md");
        Directory.CreateDirectory(Path.Combine(fixture, ".idea"));
        Directory.CreateDirectory(Path.Combine(fixture, ".git"));
        await File.WriteAllTextAsync(Path.Combine(fixture, ".hidden.md"), "hidden");
        var hidden = Path.Combine(fixture, "hidden.md"); await File.WriteAllTextAsync(hidden, "hidden"); File.SetAttributes(hidden, FileAttributes.Hidden);
        await File.WriteAllTextAsync(md, "# 阅读示例\n\n这是一段 **加粗**、*斜体*和 `行内代码`，支持 [章节跳转](#第二节)。\n\n行内公式 \\(a^2+b^2=c^2\\)。\n\n$$\n\\frac{1}{2}+\\sqrt{x}\n$$\n\n> 阅读时保持段落、公式和表格的层次。\n\n| 项目 | 数值 |\n|:---|---:|\n| 分式 | $\\frac{a}{b}$ |\n\n## 第二节\n\n- 有序的阅读内容\n- 第二项\n\n```csharp\nvar answer = 42;\n```\n\n![本地图片占位](image.png)\n\n" + string.Concat(Enumerable.Repeat("这是第二节的正文。阅读位置应在切换排版后保持。\n\n", 60)));
        await AddPathsAsync([fixture]); await OpenDocumentAsync(md); UpdateLayout();
        if (Sources.Single().Children.Any(n => n.Label.StartsWith('.') || n.Path == hidden)) throw new InvalidOperationException("Hidden files/directories leaked into the library");
        NameFilter.Text = "阅读示例"; filterTimer.Stop(); await FilterNamesAsync();
        if (NameMatches.Items.Count != 1) throw new InvalidOperationException("Loaded filename filtering failed");
        NameFilter.Text = ""; filterTimer.Stop(); await FilterNamesAsync();
        if (document?.Outline.Count != 2 || Pages.Items.Count == 0 || Welcome.IsVisible) throw new InvalidOperationException("Reader document/outline failed");
        var text = MainWindow.Descendants<ReadingText>(Pages).ToArray();
        if (text.Sum(t => t.RenderedFormulaCount) < 3) throw new InvalidOperationException("Markdown file math did not render");
        if (text.Length == 0 || text[0].TextSize != 18 || !text.Any(t => new TextRange(t.Document.ContentStart, t.Document.ContentEnd).Text.Contains("阅读示例"))) throw new InvalidOperationException("Reader data/typography bindings failed");
        App.Capture(Owner, Path.Combine(output, "reader-markdown.png"));
        const string limits = "# 函数极限的性质\n\n1. 唯一性：$f$在点$p$的极限存在，则该极限的值唯一。\n2. 局部有界性：对任意小的正数$\\varepsilon$，总有点$p$的一个邻域使函数值在$\\mathring N_\\varepsilon(q)$中。\n   - 对应实函数的局部有界性和局部保号性。\n\n## 一元实函数极限的性质\n\n1. 运算法则：$f,g:X\\to\\mathbb{R}$，设$\\lim f(x)=A,\\lim g(x)=B$，有\n   1. $\\lim[kf(x)+lg(x)]=kA+lB$，其中$k,l$为常数。\n   2. $\\lim[f(x)g(x)]=AB$。\n   3. $\\lim\\frac{f(x)}{g(x)}=\\frac{A}{B},B\\ne0$。\n";
        var limitsPath = Path.Combine(fixture, "极限.md"); await File.WriteAllTextAsync(limitsPath, limits);
        var parsedLimits = ReaderDocuments.Parse(limitsPath, limits);
        var renderedFormulas = 0;
        foreach (var piece in parsedLimits.Pieces)
        {
            var native = new ReadingText { Piece = piece }; native.Render(); renderedFormulas += native.RenderedFormulaCount;
            if (native.FormulaError.Length > 0) throw new InvalidOperationException("Limits formula failed: " + native.FormulaError);
        }
        if (renderedFormulas != 11) throw new InvalidOperationException("Chinese-adjacent/nested formulas were not all rendered: " + renderedFormulas);
        await OpenDocumentAsync(limitsPath); UpdateLayout(); App.Capture(Owner, Path.Combine(output, "reader-limits.png"));
        var mathText = Path.Combine(fixture, "公式.txt");
        await File.WriteAllTextAsync(mathText, "第一章 公式\n\n行内 \\(\\frac{a}{b}\\) 与 $x^2+y^2$。\n\n\\[\n\\int_0^1 x^2 dx=\\frac13\n\\]\n\n```tex\n$code$\n```\n金额 $5 和 $10。\n");
        await OpenDocumentAsync(mathText); UpdateLayout();
        if (MainWindow.Descendants<ReadingText>(Pages).Sum(t => t.RenderedFormulaCount) != 3) throw new InvalidOperationException("TXT formulas/code/currency isolation failed");
        App.Capture(Owner, Path.Combine(output, "reader-txt-math.png"));
        await OpenDocumentAsync(md);
        Settings_Click(this, new()); UpdateLayout();
        if (!SettingsPanel.IsVisible || OutlinePanel.IsVisible || Grid.GetColumn(InspectorPanel) != 2) throw new InvalidOperationException("Typography did not use the outline inspector");
        SizeSlider.Value = 22; LeadingSlider.Value = 1.9; SerifCheck.IsChecked = false;
        typographyTimer.Stop(); await ApplyTypographyAsync(); UpdateLayout();
        if (MainWindow.Descendants<ReadingText>(Pages).Any(t => t.TextSize != 22 || t.Serif)) throw new InvalidOperationException("Reader typography did not propagate");
        var headings = MainWindow.Descendants<ReadingText>(Pages).SelectMany(t => t.Document.Blocks.OfType<Paragraph>()).Where(p => p.FontSize > state.FontSize).ToArray();
        if (headings.Length == 0 || headings.Any(p => Math.Abs(p.LineHeight / p.FontSize - state.LineHeight) > .01)) throw new InvalidOperationException("Heading line height ignored reader leading");
        App.Capture(Owner, Path.Combine(output, "reader-typography.png"));
        InspectorOutline_Click(this, new());
        OutlineList.SelectedIndex = 1; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background); UpdateLayout();
        if (Position().Offset < document!.Pieces[document.Outline[1].Piece].Offset) throw new InvalidOperationException("Outline navigation failed");
        var book = Path.Combine(fixture, "长篇小说.txt");
        await File.WriteAllTextAsync(book, string.Concat(Enumerable.Range(1, 500).Select(i => $"第{i}章 章节标题\n\n" + string.Concat(Enumerable.Repeat("春日的风经过窗边，纸上的文字安静而清晰。继续阅读时，位置与排版应当稳定。\n", 65)) + "\n")));
        var openingClock = Stopwatch.StartNew(); await OpenDocumentAsync(book); UpdateLayout(); openingClock.Stop();
        if (document!.Pieces.Count < 500 || Realized().Count() > 24) throw new InvalidOperationException("Long document was not virtualized");
        var viewer = SmoothScroll.Viewer(Pages)!; var samples = new List<double>();
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        Pages.RaiseEvent(wheel); Pages.UpdateLayout();
        if (!wheel.Handled || viewer.VerticalOffset <= 0) throw new InvalidOperationException("Reader wheel did not respond immediately");
        for (var input = 0; input < 6; input++)
        {
            Pages.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            await Task.Delay(32);
        }
        await Task.Delay(350); SmoothScroll.Stop(Pages);
        var motionMetrics = SmoothScroll.Diagnostics(Pages);
        await File.WriteAllTextAsync(Path.Combine(output, "scroll-callbacks.txt"), $"WPF rendering callback samples: {motionMetrics.Frames}\nMedian callback interval: {motionMetrics.MedianInterval:F2} ms\nP95 callback interval: {motionMetrics.P95Interval:F2} ms\nMaximum interval: {motionMetrics.MaximumInterval:F2} ms\nMaximum motion callback work: {motionMetrics.MaximumCallback:F3} ms\nRendering tier: {System.Windows.Media.RenderCapability.Tier >> 16}\nRemote session: {SystemParameters.IsRemoteSession}\nOffscreen regression observations; these are not display-presented FPS or a Chrome comparison.");
        for (var i = 0; i < 60; i++)
        {
            var timer = Stopwatch.StartNew(); viewer.ScrollToVerticalOffset(viewer.VerticalOffset + 145); Pages.UpdateLayout(); timer.Stop(); samples.Add(timer.Elapsed.TotalMilliseconds);
            await Task.Delay(1);
        }
        if (Realized().Count() > 24 || viewer.VerticalOffset <= 0) throw new InvalidOperationException("Reader retained offscreen document controls");
        RememberPosition(); var anchor = Position();
        await OpenDocumentAsync(book); UpdateLayout(); var restored = Position();
        if (anchor.Offset != restored.Offset || Math.Abs(anchor.Fraction - restored.Fraction) > .08) throw new InvalidOperationException($"Reader anchor drifted: {anchor} -> {restored}");
        SizeSlider.Value = 20; typographyTimer.Stop(); await ApplyTypographyAsync(); UpdateLayout();
        var resized = Position();
        if (anchor.Offset != resized.Offset || Math.Abs(anchor.Fraction - resized.Fraction) > .08) throw new InvalidOperationException($"Typography lost anchor: {anchor} -> {resized}");
        App.Capture(Owner, Path.Combine(output, "reader-long-text.png"));
        outlineOverride = null; Owner.Width = 720; Owner.Height = 620; UpdateLayout();
        App.Capture(Owner, Path.Combine(output, "reader-minimum.png"));
        if (OutlinePanel.IsVisible || Pages.ActualWidth < 300) throw new InvalidOperationException("Reader minimum viewport failed");
        Outline_Click(this, new()); UpdateLayout();
        if (!OutlinePanel.IsVisible || LibraryPanel.IsVisible || Pages.ActualWidth < 300) throw new InvalidOperationException("Narrow outline drawer failed");
        outlineOverride = null;
        Owner.Width = 1440; Owner.Height = 940; UpdateLayout();
        var moved = book + ".moved"; File.Move(book, moved, true);
        try { await OpenDocumentAsync(book, fallback: true); if (!SnapshotBanner.IsVisible) throw new InvalidOperationException("Reader snapshot fallback failed"); }
        finally { File.Move(moved, book, true); }
        await OpenDocumentAsync(md); ThemeManager.Apply("dark"); UpdateLayout();
        App.Capture(Owner, Path.Combine(output, "reader-dark.png")); ThemeManager.Apply("light");
        await VerifyHeadingLayoutAsync(fixture, output);
        await VerifyListsAndSelectionAsync(fixture, output);
        await VerifyNumberedListLayoutAsync(fixture, output);
        await File.WriteAllTextAsync(Path.Combine(output, "reader-performance.txt"), $"PASS: Markdown/TXT, six heading sizes and rendered heading spacing, nested list markers and themed text selection, native math/table, folder metadata, outline navigation, typography bindings, long-document virtualization, anchor restore and resize, snapshot fallback, narrow/dark layouts, gallery 1000-item virtualization.\nLong document: {new FileInfo(book).Length:N0} bytes\nPieces: {ReaderDocuments.Parse(book, await File.ReadAllTextAsync(book)).Pieces.Count}\nOpen including initial layout: {openingClock.Elapsed.TotalMilliseconds:F1} ms\nScroll layout median: {samples.Order().ElementAt(samples.Count / 2):F2} ms\nScroll layout p95: {samples.Order().ElementAt((int)(samples.Count * .95)):F2} ms\nScroll layout max: {samples.Max():F2} ms\nThese are offscreen WPF layout timings, not display frame-rate measurements.");
    }

    private async Task VerifyHeadingLayoutAsync(string fixture, string output)
    {
        var path = Path.Combine(fixture, "标题层级.md");
        // Blank source lines force a piece boundary between the first title and its body.
        var markdown = "引导正文。\n\n# 一级标题：用于验证窄窗口自动换行的较长中文章节名称"
            + new string('\n', ReaderDocuments.PieceCharacters + 1) + "一级标题后的正文。\n\n"
            + "二级标题\n--------\n\n二级标题后的正文。\n\n"
            + "### 三级标题\n\n三级标题后的正文。\n\n#### 四级标题\n\n四级标题后的正文。\n\n"
            + "##### 五级标题\n\n五级标题后的正文。\n\n###### 六级标题\n\n六级标题后的正文。";
        await File.WriteAllTextAsync(path, markdown);
        await OpenDocumentAsync(path); UpdateLayout();

        var paragraphs = MainWindow.Descendants<ReadingText>(Pages)
            .SelectMany(view => view.Document.Blocks.OfType<Paragraph>().Select(paragraph => (View: view, Paragraph: paragraph)))
            .ToArray();
        var titles = paragraphs.Where(item => item.Paragraph.FontSize > state.FontSize).ToArray();
        if (titles.Length != 6 || titles.Zip(titles.Skip(1)).Any(pair => pair.First.Paragraph.FontSize - pair.Second.Paragraph.FontSize < 1))
            throw new InvalidOperationException("Markdown heading sizes did not distinguish all six levels");

        double LineEdge((ReadingText View, Paragraph Paragraph) item, bool bottom)
        {
            var rect = bottom ? item.Paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward)
                : item.Paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) throw new InvalidOperationException("Reader paragraph did not lay out");
            return item.View.TranslatePoint(bottom ? rect.BottomLeft : rect.TopLeft, Pages).Y;
        }

        var minimumGap = state.FontSize * state.LineHeight * .4;
        for (var level = 0; level < 2; level++)
        {
            var index = Array.IndexOf(paragraphs, titles[level]);
            if (LineEdge(paragraphs[index], false) - LineEdge(paragraphs[index - 1], true) < minimumGap
                || LineEdge(paragraphs[index + 1], false) - LineEdge(paragraphs[index], true) < minimumGap)
                throw new InvalidOperationException($"Markdown heading {level + 1} had insufficient visible spacing");
        }
        App.Capture(Owner, Path.Combine(output, "reader-heading-levels.png"));

        Owner.Width = 720; Owner.Height = 620; ThemeManager.Apply("dark"); UpdateLayout();
        var narrowTitle = MainWindow.Descendants<ReadingText>(Pages).SelectMany(view => view.Document.Blocks.OfType<Paragraph>())
            .First(paragraph => paragraph.FontSize > state.FontSize);
        if (narrowTitle.ContentEnd.GetCharacterRect(LogicalDirection.Backward).Top <= narrowTitle.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top)
            throw new InvalidOperationException("Long reader heading did not wrap at the minimum width");
        App.Capture(Owner, Path.Combine(output, "reader-heading-minimum-dark.png"));
        Owner.Width = 1440; Owner.Height = 940; ThemeManager.Apply("light"); UpdateLayout();
    }

    private async Task VerifyListsAndSelectionAsync(string fixture, string output)
    {
        var path = Path.Combine(fixture, "列表与选区.md");
        await File.WriteAllTextAsync(path, """
            这一段正文用来验证文本选区。

            - 一级列表项目
              - 二级列表项目
                - 三级列表项目
                  - 四级列表项目

            > - 引用中的一级列表
            >   - 引用中的二级列表

            3. 从第三项开始编号
            4. 保留有序列表
            """);
        await OpenDocumentAsync(path); UpdateLayout();
        foreach (var theme in new[] { "light", "dark" })
        {
            ThemeManager.Apply(theme); UpdateLayout();
            var view = MainWindow.Descendants<ReadingText>(Pages).Single();
            var lists = view.Document.Blocks.OfType<List>().ToArray();
            var level = lists[0];
            TextMarkerStyle[] markers = [TextMarkerStyle.Box, TextMarkerStyle.Disc, TextMarkerStyle.Disc, TextMarkerStyle.Disc];
            var previousSize = double.PositiveInfinity;
            for (var i = 0; i < markers.Length; i++)
            {
                var item = level.ListItems.FirstListItem;
                var sizeMatches = i < 3 ? level.FontSize < previousSize : level.FontSize == previousSize;
                if (level.MarkerStyle != markers[i] || !sizeMatches
                    || item.Blocks.OfType<Paragraph>().Single().FontSize != view.TextSize)
                    throw new InvalidOperationException("Markdown bullet styling changed the marker hierarchy or body text size");
                previousSize = level.FontSize;
                if (i + 1 < markers.Length) level = item.Blocks.OfType<List>().Single();
            }
            var quote = view.Document.Blocks.OfType<Section>().Single().Blocks.OfType<List>().Single();
            if (quote.MarkerStyle != TextMarkerStyle.Box || lists[1].MarkerStyle != TextMarkerStyle.Decimal || lists[1].StartIndex != 3)
                throw new InvalidOperationException("Quoted or ordered Markdown list lost its marker");

            view.Selection.Select(view.Document.ContentStart, lists[0].ContentEnd);
            if (!ReferenceEquals(view.SelectionBrush, Application.Current.FindResource("ToneE2DCCE"))
                || !view.Selection.Text.Contains("三级列表项目") || !ApplicationCommands.Copy.CanExecute(null, view))
                throw new InvalidOperationException("Reader selection or copy did not follow the native themed control");
            App.Capture(Owner, Path.Combine(output, $"reader-lists-{theme}.png"));
        }
        ThemeManager.Apply("light"); UpdateLayout();
    }

    private async Task VerifyNumberedListLayoutAsync(string fixture, string output)
    {
        var path = Path.Combine(fixture, "有序列表.md");
        await File.WriteAllTextAsync(path, """
            正文起始位置。

            98. 两位数编号

                99999. 嵌套编号
                100000. 六位数编号
                100001. 这一项用较长的正文验证窄窗口中的自动换行。

            99. 连续编号
            100. 三位数编号
            """);
        Owner.Width = 720; Owner.Height = 620; ThemeManager.Apply("dark");
        SizeSlider.Value = 28; LeadingSlider.Value = 2.4; SerifCheck.IsChecked = true;
        typographyTimer.Stop(); await ApplyTypographyAsync();
        await OpenDocumentAsync(path); UpdateLayout();

        var view = MainWindow.Descendants<ReadingText>(Pages).Single();
        var root = view.Document.Blocks.OfType<List>().Single();
        var nested = root.ListItems.FirstListItem.Blocks.OfType<List>().Single();
        if (root.StartIndex != 98 || root.ListItems.Count != 3 || nested.StartIndex != 99999 || nested.ListItems.Count != 3)
            throw new InvalidOperationException("Numbered list layout fixture lost its numbering");

        double Left(Paragraph paragraph) => paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward).Left;
        void VerifyMarkerRoom(List list, string marker, double columnLeft)
        {
            var typeface = new Typeface(list.FontFamily, list.FontStyle, list.FontWeight, list.FontStretch);
            var width = new FormattedText(marker, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface,
                list.FontSize, list.Foreground, VisualTreeHelper.GetDpi(view).PixelsPerDip).Width;
            var textLeft = Left(list.ListItems.LastListItem.Blocks.OfType<Paragraph>().Single());
            if (textLeft - columnLeft < width + list.MarkerOffset)
                throw new InvalidOperationException($"Numbered Markdown list marker {marker} exceeded its gutter");
        }
        VerifyMarkerRoom(root, "100.", Left((Paragraph)view.Document.Blocks.FirstBlock));
        VerifyMarkerRoom(nested, "100001.", Left(root.ListItems.FirstListItem.Blocks.OfType<Paragraph>().Single()));
        App.Capture(Owner, Path.Combine(output, "reader-numbered-narrow.png"));
        Owner.Width = 1440; Owner.Height = 940; ThemeManager.Apply("light"); UpdateLayout();
    }
}
