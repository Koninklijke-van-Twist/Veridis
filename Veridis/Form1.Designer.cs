namespace Veridis;

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
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Form1));
        openFileDialog1 = new OpenFileDialog();
        startButton = new Button();
        contextMenuToggle = new CheckBox();
        SuspendLayout();
        // 
        // openFileDialog1
        // 
        openFileDialog1.FileName = "openFileDialog";
        // 
        // startButton
        // 
        startButton.Location = new Point(12, 37);
        startButton.Name = "startButton";
        startButton.Size = new Size(241, 60);
        startButton.TabIndex = 0;
        startButton.Text = "Open Invoice";
        startButton.UseVisualStyleBackColor = true;
        startButton.Click += startButton_Click_1;
        // 
        // contextMenuToggle
        // 
        contextMenuToggle.AutoSize = true;
        contextMenuToggle.Location = new Point(12, 12);
        contextMenuToggle.Name = "contextMenuToggle";
        contextMenuToggle.Size = new Size(185, 19);
        contextMenuToggle.TabIndex = 1;
        contextMenuToggle.Text = "Allow Right-Clicking PDF Files";
        contextMenuToggle.UseVisualStyleBackColor = true;
        contextMenuToggle.CheckedChanged += contextMenuToggle_CheckedChanged;
        // 
        // Form1
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(267, 110);
        Controls.Add(contextMenuToggle);
        Controls.Add(startButton);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        Icon = (Icon)resources.GetObject("$this.Icon");
        MaximizeBox = false;
        Name = "Form1";
        Text = "Veridis";
        ResumeLayout(false);
        PerformLayout();
    }

    private System.Windows.Forms.Button startButton;

    private System.Windows.Forms.OpenFileDialog openFileDialog1;

    #endregion

    private CheckBox contextMenuToggle;
}