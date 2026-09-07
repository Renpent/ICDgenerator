namespace ICDgenerator
{
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.lblFomPath = new System.Windows.Forms.Label();
            this.txtFomPath = new System.Windows.Forms.TextBox();
            this.btnBrowse = new System.Windows.Forms.Button();
            this.btnLoad = new System.Windows.Forms.Button();
            this.lblStatus = new System.Windows.Forms.Label();
            this.splitTrees = new System.Windows.Forms.SplitContainer();
            this.grpObjects = new System.Windows.Forms.GroupBox();
            this.treeObjects = new System.Windows.Forms.TreeView();
            this.grpInteractions = new System.Windows.Forms.GroupBox();
            this.treeInteractions = new System.Windows.Forms.TreeView();
            this.btnSelectPublishable = new System.Windows.Forms.Button();
            this.btnClearSelection = new System.Windows.Forms.Button();
            this.lblSelection = new System.Windows.Forms.Label();
            this.btnGenerate = new System.Windows.Forms.Button();
            this.txtLog = new System.Windows.Forms.TextBox();
            ((System.ComponentModel.ISupportInitialize)(this.splitTrees)).BeginInit();
            this.splitTrees.Panel1.SuspendLayout();
            this.splitTrees.Panel2.SuspendLayout();
            this.splitTrees.SuspendLayout();
            this.grpObjects.SuspendLayout();
            this.grpInteractions.SuspendLayout();
            this.SuspendLayout();
            //
            // lblFomPath
            //
            this.lblFomPath.AutoSize = true;
            this.lblFomPath.Location = new System.Drawing.Point(12, 16);
            this.lblFomPath.Name = "lblFomPath";
            this.lblFomPath.Size = new System.Drawing.Size(84, 15);
            this.lblFomPath.TabIndex = 0;
            this.lblFomPath.Text = "FOMファイル:";
            //
            // txtFomPath
            //
            this.txtFomPath.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtFomPath.Location = new System.Drawing.Point(108, 12);
            this.txtFomPath.Name = "txtFomPath";
            this.txtFomPath.Size = new System.Drawing.Size(624, 23);
            this.txtFomPath.TabIndex = 1;
            //
            // btnBrowse
            //
            this.btnBrowse.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnBrowse.Location = new System.Drawing.Point(738, 11);
            this.btnBrowse.Name = "btnBrowse";
            this.btnBrowse.Size = new System.Drawing.Size(90, 25);
            this.btnBrowse.TabIndex = 2;
            this.btnBrowse.Text = "参照...";
            this.btnBrowse.UseVisualStyleBackColor = true;
            this.btnBrowse.Click += new System.EventHandler(this.btnBrowse_Click);
            //
            // btnLoad
            //
            this.btnLoad.Location = new System.Drawing.Point(108, 45);
            this.btnLoad.Name = "btnLoad";
            this.btnLoad.Size = new System.Drawing.Size(120, 30);
            this.btnLoad.TabIndex = 3;
            this.btnLoad.Text = "読み込み";
            this.btnLoad.UseVisualStyleBackColor = true;
            this.btnLoad.Click += new System.EventHandler(this.btnLoad_Click);
            //
            // lblStatus
            //
            this.lblStatus.AutoSize = true;
            this.lblStatus.Location = new System.Drawing.Point(246, 53);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(43, 15);
            this.lblStatus.TabIndex = 4;
            this.lblStatus.Text = "待機中";
            //
            // splitTrees
            //
            this.splitTrees.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.splitTrees.Location = new System.Drawing.Point(12, 85);
            this.splitTrees.Name = "splitTrees";
            this.splitTrees.Panel1.Controls.Add(this.grpObjects);
            this.splitTrees.Panel2.Controls.Add(this.grpInteractions);
            this.splitTrees.Size = new System.Drawing.Size(816, 330);
            this.splitTrees.SplitterDistance = 404;
            this.splitTrees.TabIndex = 5;
            //
            // grpObjects
            //
            this.grpObjects.Controls.Add(this.treeObjects);
            this.grpObjects.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpObjects.Location = new System.Drawing.Point(0, 0);
            this.grpObjects.Name = "grpObjects";
            this.grpObjects.Size = new System.Drawing.Size(404, 330);
            this.grpObjects.TabIndex = 0;
            this.grpObjects.TabStop = false;
            this.grpObjects.Text = "オブジェクトクラス";
            //
            // treeObjects
            //
            this.treeObjects.CheckBoxes = true;
            this.treeObjects.Dock = System.Windows.Forms.DockStyle.Fill;
            this.treeObjects.Location = new System.Drawing.Point(3, 19);
            this.treeObjects.Name = "treeObjects";
            this.treeObjects.Size = new System.Drawing.Size(398, 308);
            this.treeObjects.TabIndex = 0;
            this.treeObjects.AfterCheck += new System.Windows.Forms.TreeViewEventHandler(this.tree_AfterCheck);
            //
            // grpInteractions
            //
            this.grpInteractions.Controls.Add(this.treeInteractions);
            this.grpInteractions.Dock = System.Windows.Forms.DockStyle.Fill;
            this.grpInteractions.Location = new System.Drawing.Point(0, 0);
            this.grpInteractions.Name = "grpInteractions";
            this.grpInteractions.Size = new System.Drawing.Size(408, 330);
            this.grpInteractions.TabIndex = 0;
            this.grpInteractions.TabStop = false;
            this.grpInteractions.Text = "インタラクション";
            //
            // treeInteractions
            //
            this.treeInteractions.CheckBoxes = true;
            this.treeInteractions.Dock = System.Windows.Forms.DockStyle.Fill;
            this.treeInteractions.Location = new System.Drawing.Point(3, 19);
            this.treeInteractions.Name = "treeInteractions";
            this.treeInteractions.Size = new System.Drawing.Size(402, 308);
            this.treeInteractions.TabIndex = 0;
            this.treeInteractions.AfterCheck += new System.Windows.Forms.TreeViewEventHandler(this.tree_AfterCheck);
            //
            // btnSelectPublishable
            //
            this.btnSelectPublishable.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnSelectPublishable.Location = new System.Drawing.Point(12, 425);
            this.btnSelectPublishable.Name = "btnSelectPublishable";
            this.btnSelectPublishable.Size = new System.Drawing.Size(140, 26);
            this.btnSelectPublishable.TabIndex = 6;
            this.btnSelectPublishable.Text = "公開可能を全選択";
            this.btnSelectPublishable.UseVisualStyleBackColor = true;
            this.btnSelectPublishable.Click += new System.EventHandler(this.btnSelectPublishable_Click);
            //
            // btnClearSelection
            //
            this.btnClearSelection.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.btnClearSelection.Location = new System.Drawing.Point(158, 425);
            this.btnClearSelection.Name = "btnClearSelection";
            this.btnClearSelection.Size = new System.Drawing.Size(90, 26);
            this.btnClearSelection.TabIndex = 7;
            this.btnClearSelection.Text = "全解除";
            this.btnClearSelection.UseVisualStyleBackColor = true;
            this.btnClearSelection.Click += new System.EventHandler(this.btnClearSelection_Click);
            //
            // lblSelection
            //
            this.lblSelection.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)));
            this.lblSelection.AutoSize = true;
            this.lblSelection.Location = new System.Drawing.Point(258, 431);
            this.lblSelection.Name = "lblSelection";
            this.lblSelection.Size = new System.Drawing.Size(60, 15);
            this.lblSelection.TabIndex = 8;
            this.lblSelection.Text = "選択なし";
            //
            // btnGenerate
            //
            this.btnGenerate.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnGenerate.Location = new System.Drawing.Point(688, 423);
            this.btnGenerate.Name = "btnGenerate";
            this.btnGenerate.Size = new System.Drawing.Size(140, 30);
            this.btnGenerate.TabIndex = 9;
            this.btnGenerate.Text = "ICD生成";
            this.btnGenerate.UseVisualStyleBackColor = true;
            this.btnGenerate.Click += new System.EventHandler(this.btnGenerate_Click);
            //
            // txtLog
            //
            this.txtLog.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.txtLog.Location = new System.Drawing.Point(12, 461);
            this.txtLog.Multiline = true;
            this.txtLog.Name = "txtLog";
            this.txtLog.ReadOnly = true;
            this.txtLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtLog.Size = new System.Drawing.Size(816, 160);
            this.txtLog.TabIndex = 10;
            //
            // Form1
            //
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(840, 633);
            this.Controls.Add(this.txtLog);
            this.Controls.Add(this.btnGenerate);
            this.Controls.Add(this.lblSelection);
            this.Controls.Add(this.btnClearSelection);
            this.Controls.Add(this.btnSelectPublishable);
            this.Controls.Add(this.splitTrees);
            this.Controls.Add(this.lblStatus);
            this.Controls.Add(this.btnLoad);
            this.Controls.Add(this.btnBrowse);
            this.Controls.Add(this.txtFomPath);
            this.Controls.Add(this.lblFomPath);
            this.MinimumSize = new System.Drawing.Size(760, 560);
            this.Name = "Form1";
            this.Text = "ICDgenerator";
            this.splitTrees.Panel1.ResumeLayout(false);
            this.splitTrees.Panel2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.splitTrees)).EndInit();
            this.splitTrees.ResumeLayout(false);
            this.grpObjects.ResumeLayout(false);
            this.grpInteractions.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Label lblFomPath;
        private System.Windows.Forms.TextBox txtFomPath;
        private System.Windows.Forms.Button btnBrowse;
        private System.Windows.Forms.Button btnLoad;
        private System.Windows.Forms.Label lblStatus;
        private System.Windows.Forms.SplitContainer splitTrees;
        private System.Windows.Forms.GroupBox grpObjects;
        private System.Windows.Forms.TreeView treeObjects;
        private System.Windows.Forms.GroupBox grpInteractions;
        private System.Windows.Forms.TreeView treeInteractions;
        private System.Windows.Forms.Button btnSelectPublishable;
        private System.Windows.Forms.Button btnClearSelection;
        private System.Windows.Forms.Label lblSelection;
        private System.Windows.Forms.Button btnGenerate;
        private System.Windows.Forms.TextBox txtLog;
    }
}
