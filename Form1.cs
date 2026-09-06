using System.Diagnostics;
using ICDgenerator.Excel;
using ICDgenerator.Fom;

namespace ICDgenerator
{
    public partial class Form1 : Form
    {
        FomModel? _model;

        /// <summary>Guards the subtree cascade against the AfterCheck events it raises itself.</summary>
        bool _suppressCheckCascade;

        public Form1()
        {
            InitializeComponent();
            UpdateSelectionLabel();
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "FOMファイルを選択",
                Filter = "FOM XML (*.xml)|*.xml|すべてのファイル (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                txtFomPath.Text = dialog.FileName;
            }
        }

        private async void btnLoad_Click(object sender, EventArgs e)
        {
            var fomPath = txtFomPath.Text.Trim();

            if (!File.Exists(fomPath))
            {
                MessageBox.Show(this, "FOMファイルが見つかりません。", "ICD Generator",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SetBusy(true, "読み込み中...");
            txtLog.Clear();

            try
            {
                var sw = Stopwatch.StartNew();
                var model = await Task.Run(() => FomParser.Parse(fomPath));
                var elapsed = sw.ElapsedMilliseconds;

                _model = model;
                PopulateTrees(model);

                Log($"FOM     : {fomPath}");
                Log($"パース  : {elapsed} ms");
                Log("");
                Log($"FOM名                : {model.Identification.Name}");
                Log($"バージョン           : {model.Identification.Version}");
                Log($"オブジェクトクラス数 : {model.AllObjectClasses.Count}" +
                    $" (公開可能 {model.AllObjectClasses.Count(c => c.IsPublishable)})");
                Log($"インタラクション数   : {model.AllInteractionClasses.Count}" +
                    $" (公開可能 {model.AllInteractionClasses.Count(c => c.IsPublishable)})");
                Log($"データ型数           : {model.DataTypes.Count}");
                Log($"列挙値数             : {model.DataTypes.Values.Sum(t => t.Enumerators.Count)}");
                Log($"注記数               : {model.Notes.Count}");
                Log("");
                foreach (var warning in model.Warnings) Log("警告: " + warning);
                if (model.Warnings.Count > 0) Log("");
                Log("抽出したいクラスにチェックを入れて「ICD生成」を押してください。");
                Log("チェックしなくても、全体のパース結果は常に出力されます。");

                lblStatus.Text = $"読み込み完了 ({elapsed} ms)";
            }
            catch (Exception ex)
            {
                _model = null;
                Log("エラー: " + ex.Message);
                lblStatus.Text = "エラー";
                MessageBox.Show(this, ex.Message, "ICD Generator",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
                UpdateSelectionLabel();
            }
        }

        void PopulateTrees(FomModel model)
        {
            _suppressCheckCascade = true;
            try
            {
                Fill(treeObjects, model.RootObjectClasses.Select(BuildObjectNode));
                Fill(treeInteractions, model.RootInteractionClasses.Select(BuildInteractionNode));
            }
            finally
            {
                _suppressCheckCascade = false;
            }

            static void Fill(TreeView tree, IEnumerable<TreeNode> roots)
            {
                tree.BeginUpdate();
                tree.Nodes.Clear();
                foreach (var root in roots) tree.Nodes.Add(root);
                tree.ExpandAll();
                if (tree.Nodes.Count > 0) tree.Nodes[0].EnsureVisible();
                tree.EndUpdate();
            }
        }

        static TreeNode BuildObjectNode(FomObjectClass cls)
        {
            var node = NewNode($"{cls.Name}  ({cls.AllAttributes.Count})",
                new IcdSelection(cls.FullName, IsInteraction: false), cls.IsPublishable);

            foreach (var child in cls.Children) node.Nodes.Add(BuildObjectNode(child));
            return node;
        }

        static TreeNode BuildInteractionNode(FomInteractionClass cls)
        {
            var node = NewNode($"{cls.Name}  ({cls.AllParameters.Count})",
                new IcdSelection(cls.FullName, IsInteraction: true), cls.IsPublishable);

            foreach (var child in cls.Children) node.Nodes.Add(BuildInteractionNode(child));
            return node;
        }

        static TreeNode NewNode(string text, IcdSelection selection, bool publishable)
        {
            var node = new TreeNode(text) { Tag = selection };

            // Classes that cannot be published never carry instances, so dim them as a hint. They
            // stay checkable: a reader may still want an intermediate class documented.
            if (!publishable) node.ForeColor = SystemColors.GrayText;

            return node;
        }

        private void tree_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressCheckCascade || e.Node is null) return;

            _suppressCheckCascade = true;
            try
            {
                // Checking a parent takes the whole subtree, which is how a family such as Platform
                // or the whole Warfare branch is usually picked.
                SetSubtree(e.Node, e.Node.Checked);
            }
            finally
            {
                _suppressCheckCascade = false;
            }

            UpdateSelectionLabel();
        }

        static void SetSubtree(TreeNode node, bool value)
        {
            foreach (TreeNode child in node.Nodes)
            {
                child.Checked = value;
                SetSubtree(child, value);
            }
        }

        private void btnSelectPublishable_Click(object sender, EventArgs e)
        {
            if (_model is null) return;

            var publishable = _model.AllObjectClasses.Where(c => c.IsPublishable).Select(c => c.FullName)
                .Concat(_model.AllInteractionClasses.Where(c => c.IsPublishable).Select(c => c.FullName))
                .ToHashSet(StringComparer.Ordinal);

            SetAllChecks(node => node.Tag is IcdSelection s && publishable.Contains(s.FullName));
        }

        private void btnClearSelection_Click(object sender, EventArgs e) => SetAllChecks(_ => false);

        void SetAllChecks(Func<TreeNode, bool> predicate)
        {
            _suppressCheckCascade = true;
            try
            {
                foreach (var node in AllNodes()) node.Checked = predicate(node);
            }
            finally
            {
                _suppressCheckCascade = false;
            }

            UpdateSelectionLabel();
        }

        IEnumerable<TreeNode> AllNodes()
        {
            foreach (TreeNode root in treeObjects.Nodes)
            {
                foreach (var node in Descend(root)) yield return node;
            }
            foreach (TreeNode root in treeInteractions.Nodes)
            {
                foreach (var node in Descend(root)) yield return node;
            }

            static IEnumerable<TreeNode> Descend(TreeNode node)
            {
                yield return node;
                foreach (TreeNode child in node.Nodes)
                {
                    foreach (var descendant in Descend(child)) yield return descendant;
                }
            }
        }

        List<IcdSelection> CurrentSelections() =>
            AllNodes().Where(n => n.Checked).Select(n => n.Tag).OfType<IcdSelection>().ToList();

        void UpdateSelectionLabel()
        {
            var selections = CurrentSelections();
            int objects = selections.Count(s => !s.IsInteraction);

            lblSelection.Text = selections.Count == 0
                ? "選択なし（全体のパース結果のみ出力）"
                : $"選択: オブジェクト {objects} / インタラクション {selections.Count - objects}";
        }

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            if (_model is null)
            {
                MessageBox.Show(this, "先にFOMファイルを読み込んでください。", "ICD Generator",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "ICDの保存先",
                Filter = "Excel ブック (*.xlsx)|*.xlsx",
                FileName = Path.GetFileNameWithoutExtension(txtFomPath.Text.Trim()) + "_ICD.xlsx"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var outputPath = dialog.FileName;
            var model = _model;
            var selections = CurrentSelections();

            SetBusy(true, "生成中...");
            Log("");
            Log($"出力先  : {outputPath}");

            try
            {
                var sw = Stopwatch.StartNew();
                await Task.Run(() => IcdExporter.Export(model, outputPath, selections));
                var elapsed = sw.ElapsedMilliseconds;

                Log($"Excel出力 : {elapsed} ms");
                if (selections.Count > 0)
                {
                    Log($"抽出シート: 抽出概要 + 詳細 {selections.Count} シート");
                    foreach (var selection in selections.Take(20)) Log($"    {selection.ShortName}");
                    if (selections.Count > 20) Log($"    … 他 {selections.Count - 20} 件");
                }
                Log("完了しました。");

                lblStatus.Text = $"生成完了 ({elapsed} ms)";
            }
            catch (Exception ex)
            {
                Log("エラー: " + ex.Message);
                lblStatus.Text = "エラー";
                MessageBox.Show(this, ex.Message, "ICD Generator",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy, string? status = null)
        {
            btnBrowse.Enabled = !busy;
            btnLoad.Enabled = !busy;
            btnGenerate.Enabled = !busy;
            btnSelectPublishable.Enabled = !busy;
            btnClearSelection.Enabled = !busy;
            UseWaitCursor = busy;
            if (status is not null) lblStatus.Text = status;
        }

        private void Log(string message)
        {
            txtLog.AppendText(message + Environment.NewLine);
        }
    }
}
