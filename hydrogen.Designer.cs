namespace hydrogen
{
    partial class hydrogen
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
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
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            处理器选择 = new TextBox();
            解码处理器 = new Label();
            点击添加暂停程序 = new Button();
            点击添加视频 = new Button();
            暂停程序列表 = new ListBox();
            RAM阈值 = new TextBox();
            VRAM阈值 = new TextBox();
            RAM = new Label();
            label2 = new Label();
            label3 = new Label();
            pictureBox1 = new PictureBox();
            pictureBox2 = new PictureBox();
            pictureBox3 = new PictureBox();
            pictureBox4 = new PictureBox();
            保存设置 = new Button();
            渲染处理器 = new Label();
            渲染卡选择 = new TextBox();
            ((System.ComponentModel.ISupportInitialize)pictureBox1).BeginInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox2).BeginInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox3).BeginInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox4).BeginInit();
            SuspendLayout();
            // 
            // 处理器选择
            // 
            处理器选择.Location = new Point(148, 31);
            处理器选择.Name = "处理器选择";
            处理器选择.Size = new Size(180, 30);
            处理器选择.TabIndex = 0;
            处理器选择.TextChanged += textBox1_TextChanged;
            // 
            // 解码处理器
            // 
            解码处理器.AutoSize = true;
            解码处理器.Location = new Point(42, 31);
            解码处理器.Name = "解码处理器";
            解码处理器.Size = new Size(100, 24);
            解码处理器.TabIndex = 1;
            解码处理器.Text = "解码处理器";
            解码处理器.Click += label1_Click;
            // 
            // 点击添加暂停程序
            // 
            点击添加暂停程序.Location = new Point(74, 530);
            点击添加暂停程序.Name = "点击添加暂停程序";
            点击添加暂停程序.Size = new Size(187, 34);
            点击添加暂停程序.TabIndex = 2;
            点击添加暂停程序.Text = "点击添加暂停程序";
            点击添加暂停程序.UseVisualStyleBackColor = true;
            点击添加暂停程序.Click += button1_Click;
            // 
            // 点击添加视频
            // 
            点击添加视频.Location = new Point(928, 37);
            点击添加视频.Name = "点击添加视频";
            点击添加视频.Size = new Size(187, 34);
            点击添加视频.TabIndex = 3;
            点击添加视频.Text = "点击添加视频";
            点击添加视频.UseVisualStyleBackColor = true;
            点击添加视频.Click += 点击添加视频_Click;
            // 
            // 暂停程序列表
            // 
            暂停程序列表.FormattingEnabled = true;
            暂停程序列表.ItemHeight = 24;
            暂停程序列表.Location = new Point(42, 137);
            暂停程序列表.Name = "暂停程序列表";
            暂停程序列表.Size = new Size(244, 364);
            暂停程序列表.TabIndex = 4;
            暂停程序列表.SelectedIndexChanged += 暂停程序列表_DoubleClick;
            // 
            // RAM阈值
            // 
            RAM阈值.Location = new Point(455, 31);
            RAM阈值.Name = "RAM阈值";
            RAM阈值.Size = new Size(150, 30);
            RAM阈值.TabIndex = 5;
            RAM阈值.TextChanged += RAM阈值_TextChanged;
            // 
            // VRAM阈值
            // 
            VRAM阈值.Location = new Point(730, 37);
            VRAM阈值.Name = "VRAM阈值";
            VRAM阈值.Size = new Size(150, 30);
            VRAM阈值.TabIndex = 6;
            VRAM阈值.TextChanged += textBox3_TextChanged;
            // 
            // RAM
            // 
            RAM.AutoSize = true;
            RAM.Location = new Point(366, 64);
            RAM.Name = "RAM";
            RAM.Size = new Size(0, 24);
            RAM.TabIndex = 7;
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(342, 37);
            label2.Name = "label2";
            label2.Size = new Size(89, 24);
            label2.TabIndex = 8;
            label2.Text = "RAM阈值";
            // 
            // label3
            // 
            label3.AutoSize = true;
            label3.Location = new Point(623, 37);
            label3.Name = "label3";
            label3.Size = new Size(101, 24);
            label3.TabIndex = 9;
            label3.Text = "VRAM阈值";
            // 
            // pictureBox1
            // 
            pictureBox1.Location = new Point(342, 118);
            pictureBox1.Name = "pictureBox1";
            pictureBox1.Size = new Size(315, 208);
            pictureBox1.TabIndex = 10;
            pictureBox1.TabStop = false;
            pictureBox1.Click += pictureBox1_Click;
            // 
            // pictureBox2
            // 
            pictureBox2.Location = new Point(836, 118);
            pictureBox2.Name = "pictureBox2";
            pictureBox2.Size = new Size(315, 208);
            pictureBox2.TabIndex = 11;
            pictureBox2.TabStop = false;
            pictureBox2.Click += pictureBox2_Click;
            // 
            // pictureBox3
            // 
            pictureBox3.Location = new Point(342, 385);
            pictureBox3.Name = "pictureBox3";
            pictureBox3.Size = new Size(315, 208);
            pictureBox3.TabIndex = 12;
            pictureBox3.TabStop = false;
            pictureBox3.Click += pictureBox3_Click;
            // 
            // pictureBox4
            // 
            pictureBox4.Location = new Point(836, 385);
            pictureBox4.Name = "pictureBox4";
            pictureBox4.Size = new Size(315, 208);
            pictureBox4.TabIndex = 13;
            pictureBox4.TabStop = false;
            pictureBox4.Click += pictureBox4_Click;
            // 
            // 保存设置
            // 
            保存设置.Location = new Point(1146, 37);
            保存设置.Name = "保存设置";
            保存设置.Size = new Size(112, 34);
            保存设置.TabIndex = 14;
            保存设置.TabStop = false;
            保存设置.Text = "保存设置";
            保存设置.UseCompatibleTextRendering = true;
            保存设置.UseVisualStyleBackColor = true;
            保存设置.Click += 保存设置_Click;
            // 
            // 渲染处理器
            // 
            渲染处理器.AutoSize = true;
            渲染处理器.Location = new Point(42, 89);
            渲染处理器.Name = "渲染处理器";
            渲染处理器.Size = new Size(100, 24);
            渲染处理器.TabIndex = 15;
            渲染处理器.Text = "渲染处理器";
            // 
            // 渲染卡选择
            // 
            渲染卡选择.Location = new Point(148, 86);
            渲染卡选择.Name = "渲染卡选择";
            渲染卡选择.Size = new Size(180, 30);
            渲染卡选择.TabIndex = 16;
            渲染卡选择.TextChanged += 渲染处理器选择_TextChanged;
            // 
            // hydrogen
            // 
            AutoScaleDimensions = new SizeF(11F, 24F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1290, 632);
            Controls.Add(渲染卡选择);
            Controls.Add(渲染处理器);
            Controls.Add(保存设置);
            Controls.Add(pictureBox4);
            Controls.Add(pictureBox3);
            Controls.Add(pictureBox2);
            Controls.Add(pictureBox1);
            Controls.Add(label3);
            Controls.Add(label2);
            Controls.Add(RAM);
            Controls.Add(VRAM阈值);
            Controls.Add(RAM阈值);
            Controls.Add(暂停程序列表);
            Controls.Add(点击添加视频);
            Controls.Add(点击添加暂停程序);
            Controls.Add(解码处理器);
            Controls.Add(处理器选择);
            Name = "hydrogen";
            Text = "hydrogen";
            Load += hydrogen_Load;
            ((System.ComponentModel.ISupportInitialize)pictureBox1).EndInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox2).EndInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox3).EndInit();
            ((System.ComponentModel.ISupportInitialize)pictureBox4).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private TextBox 处理器选择;
        private Label 解码处理器;
        private Button 点击添加暂停程序;
        private Button 点击添加视频;
        private ListBox 暂停程序列表;
        private TextBox RAM阈值;
        private TextBox VRAM阈值;
        private Label RAM;
        private Label label2;
        private Label label3;
        private PictureBox pictureBox1;
        private PictureBox pictureBox2;
        private PictureBox pictureBox3;
        private PictureBox pictureBox4;
        private Button 保存设置;
        private Label 渲染处理器;
        private TextBox 渲染卡选择;
    }
}