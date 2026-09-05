using System.Diagnostics;
using ICDgenerator.Excel;
using ICDgenerator.Fom;

namespace ICDgenerator
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
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

        private async void btnGenerate_Click(object sender, EventArgs e)
        {
            var fomPath = txtFomPath.Text.Trim();

            if (!File.Exists(fomPath))
            {
                MessageBox.Show(this, "FOMファイルが見つかりません。", "ICD Generator",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var dialog = new SaveFileDialog
            {
                Title = "ICDの保存先",
                Filter = "Excel ブック (*.xlsx)|*.xlsx",
                FileName = Path.GetFileNameWithoutExtension(fomPath) + "_ICD.xlsx"
            };

            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            var outputPath = dialog.FileName;

            SetBusy(true);
            txtLog.Clear();
            Log($"FOM     : {fomPath}");
            Log($"出力先  : {outputPath}");
            Log("");

            try
            {
                var result = await Task.Run(() => Generate(fomPath, outputPath));

                Log($"パース      : {result.ParseMs} ms");
                Log($"Excel出力   : {result.ExportMs} ms");
                Log("");
                Log($"FOM名           : {result.Model.Identification.Name}");
                Log($"バージョン      : {result.Model.Identification.Version}");
                Log($"オブジェクトクラス数 : {result.Model.AllObjectClasses.Count}");
                Log($"リーフクラス数       : {result.Model.AllObjectClasses.Count(c => c.IsLeaf)}");
                Log($"最大階層             : {result.Model.AllObjectClasses.Max(c => c.Level)}");
                Log($"継承後の最大属性数   : {result.Model.AllObjectClasses.Max(c => c.AllAttributes.Count)}");
                Log("");
                Log("完了しました。");

                lblStatus.Text = $"完了 ({result.ParseMs + result.ExportMs} ms)";
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

        private static GenerateResult Generate(string fomPath, string outputPath)
        {
            var sw = Stopwatch.StartNew();
            var model = FomParser.Parse(fomPath);
            var parseMs = sw.ElapsedMilliseconds;

            sw.Restart();
            IcdExporter.Export(model, outputPath);
            var exportMs = sw.ElapsedMilliseconds;

            return new GenerateResult(model, parseMs, exportMs);
        }

        private void SetBusy(bool busy)
        {
            btnBrowse.Enabled = !busy;
            btnGenerate.Enabled = !busy;
            UseWaitCursor = busy;
            if (busy) lblStatus.Text = "生成中...";
        }

        private void Log(string message)
        {
            txtLog.AppendText(message + Environment.NewLine);
        }

        private sealed record GenerateResult(FomModel Model, long ParseMs, long ExportMs);
    }
}
