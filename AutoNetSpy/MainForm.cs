using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AutoNetSpy;

public sealed class MainForm : Form
{
    private readonly TextBox _sourceBox = new();
    private readonly TextBox _outputBox = new();
    private readonly Button _browseSrcBtn = new() { Text = "浏览…" };
    private readonly Button _browseOutBtn = new() { Text = "浏览…" };
    private readonly Button _scanBtn = new() { Text = "扫描" };
    private readonly Button _startBtn = new() { Text = "开始反编译", Enabled = false };
    private readonly Button _cancelBtn = new() { Text = "取消", Enabled = false };
    private readonly Button _selectAllBtn = new() { Text = "全选" };
    private readonly Button _selectNoneBtn = new() { Text = "反选" };
    private readonly TreeView _tree = new() { CheckBoxes = true };
    private readonly CheckBox _chkCreateProject = new() { Text = "生成 .csproj 项目", Checked = true };
    private readonly CheckBox _chkRemoveCg = new() { Text = "清理编译器生成代码", Checked = true };
    private readonly CheckBox _chkSkipResources = new() { Text = "跳过资源" };
    private readonly CheckBox _chkSkipExisting = new() { Text = "跳过已反编译", Checked = true };
    private readonly TextBox _skipPrefixesBox = new()
    {
        Text = DecompileOptions.DefaultSkipNamePrefixText,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        AcceptsReturn = true,
        WordWrap = false,
        Height = 72,
    };
    private readonly NumericUpDown _minSize = new() { Minimum = 0, Maximum = 1_000_000, Value = 0 };
    private readonly NumericUpDown _parallelism = new() { Minimum = 1, Maximum = 64, Value = Environment.ProcessorCount };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Continuous };
    private readonly Label _status = new() { Text = "就绪", AutoEllipsis = true };
    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new System.Drawing.Font("Consolas", 9f),
    };

    private CancellationTokenSource? _cts;
    private bool _suppressTreeCheck;

    public MainForm()
    {
        Text = "AutoNetSpy - 批量反编译 (.NET via ilspycmd)";
        Width = 1100;
        Height = 760;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new System.Drawing.Size(900, 650);

        BuildLayout();

        _browseSrcBtn.Click += (_, _) => Browse(_sourceBox);
        _browseOutBtn.Click += (_, _) => Browse(_outputBox);
        _scanBtn.Click += async (_, _) => await ScanAsync();
        _startBtn.Click += async (_, _) => await StartAsync();
        _cancelBtn.Click += (_, _) => _cts?.Cancel();
        _selectAllBtn.Click += (_, _) => SetAllChecked(true);
        _selectNoneBtn.Click += (_, _) => SetAllChecked(false);
        _tree.AfterCheck += OnTreeAfterCheck;
    }

    private void BuildLayout()
    {
        var rootTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 65f));
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        rootTable.RowStyles.Add(new RowStyle(SizeType.Percent, 35f));
        rootTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        rootTable.Controls.Add(BuildPathRow("源目录:", _sourceBox, _browseSrcBtn, _scanBtn), 0, 0);
        rootTable.Controls.Add(BuildPathRow("输出目录:", _outputBox, _browseOutBtn, null), 0, 1);

        var treeGroup = new GroupBox { Text = "程序集列表", Dock = DockStyle.Fill };
        var treePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        treePanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        treePanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        var btnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        btnRow.Controls.AddRange(new Control[] { _selectAllBtn, _selectNoneBtn });
        treePanel.Controls.Add(btnRow, 0, 0);
        _tree.Dock = DockStyle.Fill;
        treePanel.Controls.Add(_tree, 0, 1);
        treeGroup.Controls.Add(treePanel);
        rootTable.Controls.Add(treeGroup, 0, 2);

        var optGroup = new GroupBox
        {
            Text = "反编译选项",
            Dock = DockStyle.Fill,
            AutoSize = true,
            MinimumSize = new System.Drawing.Size(0, 125),
        };
        var optPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        optPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        optPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var optionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        optionRow.Controls.AddRange(new Control[]
        {
            _chkCreateProject, _chkRemoveCg, _chkSkipResources, _chkSkipExisting,
            LabeledNumeric("最小尺寸(KB):", _minSize),
            LabeledNumeric("并行度:", _parallelism),
        });

        optPanel.Controls.Add(optionRow, 0, 0);
        optPanel.Controls.Add(LabeledText("跳过名称前缀(;或换行):", _skipPrefixesBox), 0, 1);
        optGroup.Controls.Add(optPanel);
        rootTable.Controls.Add(optGroup, 0, 3);

        var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, AutoSize = true };
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _progress.Dock = DockStyle.Fill;
        _progress.Height = 22;
        actionRow.Controls.Add(_progress, 0, 0);
        actionRow.Controls.Add(_startBtn, 1, 0);
        actionRow.Controls.Add(_cancelBtn, 2, 0);
        rootTable.Controls.Add(actionRow, 0, 4);

        var logGroup = new GroupBox { Text = "日志", Dock = DockStyle.Fill };
        _log.Dock = DockStyle.Fill;
        var logPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        logPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        logPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        logPanel.Controls.Add(_log, 0, 0);
        _status.Dock = DockStyle.Fill;
        logPanel.Controls.Add(_status, 0, 1);
        logGroup.Controls.Add(logPanel);
        rootTable.Controls.Add(logGroup, 0, 5);

        Controls.Add(rootTable);
    }

    private static Control BuildPathRow(string label, TextBox box, Button browse, Button? extra)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = extra is null ? 3 : 4, AutoSize = true };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (extra != null) panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 6, 0) };
        box.Dock = DockStyle.Fill;
        panel.Controls.Add(lbl, 0, 0);
        panel.Controls.Add(box, 1, 0);
        panel.Controls.Add(browse, 2, 0);
        if (extra != null) panel.Controls.Add(extra, 3, 0);
        return panel;
    }

    private static Control LabeledNumeric(string label, NumericUpDown nud)
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(8, 3, 8, 3) };
        p.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        nud.Width = 70;
        p.Controls.Add(nud);
        return p;
    }

    private static Control LabeledText(string label, TextBox box)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(8, 3, 8, 3) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, box.Height + 6));

        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Padding = new Padding(0, 6, 4, 0) };
        box.Dock = DockStyle.Fill;
        panel.Controls.Add(lbl, 0, 0);
        panel.Controls.Add(box, 1, 0);
        return panel;
    }

    private static void Browse(TextBox target)
    {
        using var dlg = new FolderBrowserDialog();
        if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text;
        if (dlg.ShowDialog() == DialogResult.OK)
            target.Text = dlg.SelectedPath;
    }

    private async Task ScanAsync()
    {
        var path = _sourceBox.Text.Trim();
        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, "源目录不存在", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _scanBtn.Enabled = false;
        _startBtn.Enabled = false;
        _tree.Nodes.Clear();
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 30;
        AppendLog($"扫描: {path}");
        SetStatus("枚举文件中…");

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                if (p.Phase == ScanPhase.Enumerating)
                {
                    SetStatus($"枚举中… 已发现 {p.Total} 个候选");
                }
                else
                {
                    if (_progress.Style != ProgressBarStyle.Continuous)
                    {
                        _progress.Style = ProgressBarStyle.Continuous;
                        _progress.Minimum = 0;
                        _progress.Maximum = Math.Max(1, p.Total);
                    }
                    SetProgressValue(p.Done, p.Total);
                    SetStatus($"鉴别程序集… {p.Done}/{p.Total}  (托管: {p.FoundAssemblies})");
                }
            });

            var root = await Task.Run(() => AssemblyScanner.Scan(path, progress));
            PopulateTree(root);
            _startBtn.Enabled = root.Subdirs.Count + root.Assemblies.Count > 0;
            SetProgressValue(_progress.Maximum, _progress.Maximum);
            SetStatus($"扫描完成: {CountAssemblies(root)} 个托管程序集");
        }
        catch (Exception ex)
        {
            AppendLog("扫描失败: " + ex.Message);
            SetStatus("扫描失败");
        }
        finally
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.MarqueeAnimationSpeed = 0;
            _scanBtn.Enabled = true;
        }
    }

    /// <summary>
    /// Sets progress bar value reliably, bypassing the WinForms animation lag
    /// (which makes the bar visually trail behind, especially near completion).
    /// </summary>
    private void SetProgressValue(int value, int max)
    {
        if (InvokeRequired) { BeginInvoke(() => SetProgressValue(value, max)); return; }
        if (max <= 0) return;
        if (_progress.Maximum != max + 1) _progress.Maximum = max + 1;
        value = Math.Clamp(value, 0, max);
        // Trick: jump to value+1 then back to value to defeat the slide animation.
        _progress.Value = Math.Min(value + 1, _progress.Maximum);
        _progress.Value = value;
    }

    private static int CountAssemblies(DirNode n) =>
        n.Assemblies.Count + n.Subdirs.Sum(CountAssemblies);

    private void PopulateTree(DirNode root)
    {
        _tree.BeginUpdate();
        try
        {
            var node = BuildNode(root);
            node.Expand();
            _tree.Nodes.Add(node);
        }
        finally { _tree.EndUpdate(); }
    }

    private static TreeNode BuildNode(DirNode dir)
    {
        var node = new TreeNode($"📁 {dir.Name}") { Tag = dir };
        foreach (var sub in dir.Subdirs)
            node.Nodes.Add(BuildNode(sub));
        foreach (var asm in dir.Assemblies)
        {
            var label = $"{Path.GetFileName(asm.FullPath)}  ({(string.IsNullOrEmpty(asm.TargetFramework) ? "?" : asm.TargetFramework)}, {FormatSize(asm.SizeBytes)})";
            node.Nodes.Add(new TreeNode(label) { Tag = asm });
        }
        return node;
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024:F1} MB" : $"{bytes / 1024.0:F1} KB";

    private void OnTreeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suppressTreeCheck || e.Node is null) return;
        _suppressTreeCheck = true;
        try
        {
            foreach (TreeNode child in e.Node.Nodes)
                SetCheckedRecursive(child, e.Node.Checked);
        }
        finally { _suppressTreeCheck = false; }
    }

    private static void SetCheckedRecursive(TreeNode node, bool value)
    {
        node.Checked = value;
        foreach (TreeNode c in node.Nodes) SetCheckedRecursive(c, value);
    }

    private void SetAllChecked(bool value)
    {
        _suppressTreeCheck = true;
        try
        {
            foreach (TreeNode n in _tree.Nodes) SetCheckedRecursive(n, value);
        }
        finally { _suppressTreeCheck = false; }
    }

    private IEnumerable<AssemblyNode> GatherSelected()
    {
        foreach (var n in EnumerateAllNodes(_tree.Nodes))
            if (n.Checked && n.Tag is AssemblyNode a)
                yield return a;
    }

    private static IEnumerable<TreeNode> EnumerateAllNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes)
        {
            yield return n;
            foreach (var c in EnumerateAllNodes(n.Nodes)) yield return c;
        }
    }

    private async Task StartAsync()
    {
        var outDir = _outputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outDir))
        {
            MessageBox.Show(this, "请选择输出目录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var ilspy = IlspyCmdLocator.Find();
        if (ilspy is null)
        {
            if (MessageBox.Show(this,
                "未检测到 ilspycmd，是否立即通过 dotnet 全局安装？",
                "ilspycmd 缺失", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            SetStatus("正在安装 ilspycmd…");
            var (ok, log) = await IlspyCmdLocator.InstallAsync();
            AppendLog(log);
            if (!ok) { MessageBox.Show(this, "安装失败，详见日志", "错误"); return; }
            ilspy = IlspyCmdLocator.Find();
            if (ilspy is null) { MessageBox.Show(this, "安装后仍未找到 ilspycmd，请重启程序"); return; }
        }

        var selected = GatherSelected().ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请勾选要反编译的程序集", "提示");
            return;
        }

        var options = new DecompileOptions
        {
            OutputDirectory = outDir,
            CreateProject = _chkCreateProject.Checked,
            RemoveCompilerGenerated = _chkRemoveCg.Checked,
            SkipResources = _chkSkipResources.Checked,
            SkipAlreadyDecompiled = _chkSkipExisting.Checked,
            SkipNamePrefixes = DecompileOptions.ParseSkipNamePrefixes(_skipPrefixesBox.Text),
            MinSizeKb = (int)_minSize.Value,
            MaxParallelism = (int)_parallelism.Value,
        };

        _cts = new CancellationTokenSource();
        SetRunning(true);
        SetProgressValue(0, selected.Count);

        var svc = new DecompileService(ilspy, options);
        var progress = new Progress<(int done, int total, string current, bool skipped)>(p =>
        {
            SetProgressValue(p.done, p.total);
            var icon = p.skipped ? "⏭" : "✓";
            SetStatus($"[{p.done}/{p.total}] {p.current}");
            AppendLog($"{icon} ({p.done}/{p.total}) {Path.GetFileName(p.current)}");
        });

        var sw = Stopwatch.StartNew();
        try
        {
            var results = await svc.RunAsync(selected, progress, _cts.Token);
            sw.Stop();
            var skipped = results.Count(r => r.Skipped);
            var ok = results.Count(r => r.Success && !r.Skipped);
            var fail = results.Count(r => !r.Success && !r.Skipped);
            SetProgressValue(selected.Count, selected.Count);
            AppendLog($"完成: 成功 {ok}, 跳过 {skipped}, 失败 {fail}, 耗时 {sw.Elapsed:mm\\:ss}");
            SetStatus($"完成 — 成功 {ok} / 跳过 {skipped} / 失败 {fail}");
            if (MessageBox.Show(this, $"完成。成功 {ok}，跳过 {skipped}，失败 {fail}。是否打开输出目录？",
                "完成", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Process.Start("explorer.exe", outDir);
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("已取消");
            SetStatus("已取消");
        }
        catch (Exception ex)
        {
            AppendLog("错误: " + ex);
            SetStatus("出错");
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void SetRunning(bool running)
    {
        _startBtn.Enabled = !running;
        _cancelBtn.Enabled = running;
        _scanBtn.Enabled = !running;
        _browseSrcBtn.Enabled = !running;
        _browseOutBtn.Enabled = !running;
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => _status.Text = text); return; }
        _status.Text = text;
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(line)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
    }
}
