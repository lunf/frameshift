namespace FrameShift;

internal sealed class InputDialog : Form
{
    private readonly TextBox input;
    public string Value => input.Text.Trim();

    public InputDialog(string title, string description, string value, bool secret = false)
    {
        Text = title;
        Font = new Font("Segoe UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(470, secret ? 290 : 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(248, 249, 251);
        var label = new Label { Text = description, Location = new Point(22, 18), Size = new Size(426, secret ? 148 : 58) };
        input = new TextBox { Text = value, Location = new Point(24, secret ? 177 : 85), Width = 422,
            UseSystemPasswordChar = secret, MaxLength = secret ? 256 : 200 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(90, 32),
            Location = new Point(256, ClientSize.Height - 52) };
        var accept = new Button { Text = secret ? "Use key" : "Apply topic", DialogResult = DialogResult.OK,
            Size = new Size(96, 32), Location = new Point(352, ClientSize.Height - 52) };
        Controls.AddRange([label, input, cancel, accept]);
        AcceptButton = accept;
        CancelButton = cancel;
        Shown += (_, _) => { Activate(); input.Focus(); input.SelectAll(); };
    }
}
