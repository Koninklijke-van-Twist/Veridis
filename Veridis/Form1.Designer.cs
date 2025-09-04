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
        openFileDialog1 = new OpenFileDialog();
        startButton = new Button();
        SuspendLayout();
        // 
        // openFileDialog1
        // 
        openFileDialog1.FileName = "openFileDialog";
        // 
        // startButton
        // 
        startButton.Location = new Point(212, 158);
        startButton.Name = "startButton";
        startButton.Size = new Size(75, 23);
        startButton.TabIndex = 0;
        startButton.Text = "Open Invoice";
        startButton.UseVisualStyleBackColor = true;
        startButton.Click += startButton_Click_1;
        // 
        // Form1
        // 
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(800, 450);
        Controls.Add(startButton);
        Name = "Form1";
        Text = "Form1";
        ResumeLayout(false);
    }

    private System.Windows.Forms.Button startButton;

    private System.Windows.Forms.OpenFileDialog openFileDialog1;

    #endregion
}