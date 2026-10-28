using System.Globalization;
using UwPower.Core;
using UwPower.Core.Config;
using UwPower.Core.Idle;
using UwPower.Core.Scheduling;

namespace UwPower.Tray;

/// <summary>Mode buttons, idle settings, schedule editor and startup option.</summary>
internal sealed class SettingsForm : Form
{
    private readonly TrayContext _tray;
    private readonly Label _modeLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
    private readonly Label _reasonLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Dictionary<PowerMode, Button> _modeButtons = [];

    private readonly CheckBox _automation = new() { Text = "Switch automatically (schedule and idle)", AutoSize = true };
    private readonly CheckBox _idleEnabled = new() { Text = "When I'm away, switch to", AutoSize = true };
    private readonly ComboBox _idleMode = ModeCombo();
    private readonly NumericUpDown _idleMinutes = new() { Minimum = 1, Maximum = 1440, Width = 60 };
    private readonly CheckBox _respectRequests = new()
    {
        Text = "Not while apps keep the PC awake (videos, games, calls, music) - same rule Windows uses for sleep",
        AutoSize = true,
    };
    private readonly CheckBox _fullscreen = new() { Text = "Not while a fullscreen app or game is in front", AutoSize = true };
    private readonly Label _elevationNote = new() { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(620, 0) };

    private readonly DataGridView _grid = new()
    {
        AllowUserToAddRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        Height = 170,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.FixedSingle,
    };

    private readonly CheckBox _hotkeysEnabled = new() { Text = "Switch modes with global keyboard shortcuts", AutoSize = true };
    private readonly Dictionary<PowerMode, HotkeyBox> _hotkeyBoxes = PowerModes.All.ToDictionary(m => m, _ => new HotkeyBox());
    private readonly Label _hotkeyProblems = new() { AutoSize = true, ForeColor = Color.DarkOrange, MaximumSize = new Size(620, 0) };

    private readonly CheckBox _autostart = new()
    {
        Text = "Start at logon (as administrator, so videos and games are detected)",
        AutoSize = true,
    };

    public SettingsForm(TrayContext tray)
    {
        _tray = tray;
        Text = "Uniwill Power Switch";
        Icon = tray.Icons.For(PowerMode.Beast);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        root.Controls.Add(BuildModeGroup());
        root.Controls.Add(BuildIdleGroup());
        root.Controls.Add(BuildScheduleGroup());
        root.Controls.Add(BuildHotkeyGroup());
        root.Controls.Add(BuildStartupGroup());
        root.Controls.Add(BuildButtons());
        Controls.Add(root);

        LoadSettings(_tray.Controller.Config);
        RefreshState();
    }

    /// <summary>Updates the live parts (current mode, reason). Called from the tray on state changes.</summary>
    public void RefreshState()
    {
        var controller = _tray.Controller;
        var mode = controller.CurrentMode;
        _modeLabel.Text = controller.Connected ? $"Current mode: {mode}" : "Not connected to the Control Center service";
        _modeLabel.ForeColor = controller.Connected && mode is { } m ? ModeIcons.Color(m) : SystemColors.GrayText;
        _reasonLabel.Text = controller.Connected
            ? controller.Config.AutomationEnabled ? $"Why: {controller.LastDecision?.Reason ?? "starting"}" : "Automation paused"
            : controller.LastError ?? "";
        foreach (var (buttonMode, button) in _modeButtons)
        {
            button.Enabled = controller.Connected;
            button.Font = new Font(Font, buttonMode == mode ? FontStyle.Bold : FontStyle.Regular);
        }
        Icon = _tray.Icons.For(controller.Connected ? mode : null);

        var problems = _tray.Hotkeys.Problems;
        _hotkeyProblems.Text = string.Join("\n", problems.Select(p => $"{p.Key}: {p.Value}"));
        _hotkeyProblems.Visible = problems.Count > 0;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tray.Hotkeys.Suspended = false;
        base.OnFormClosed(e);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Give every section the width of the widest one.
        var groups = Controls[0].Controls.OfType<GroupBox>().ToList();
        var width = groups.Max(g => g.Width);
        foreach (var group in groups)
        {
            group.MinimumSize = new Size(width, 0);
            foreach (var label in group.Controls[0].Controls.OfType<Label>().Where(l => l.MaximumSize.Width > 0))
                label.MaximumSize = new Size(width - 30, 0);
        }
        _grid.Width = width - 30;
        Controls[0].Controls.OfType<FlowLayoutPanel>().Last().Width = width;
    }

    private GroupBox BuildModeGroup()
    {
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
        foreach (var mode in PowerModes.All)
        {
            var button = new Button
            {
                Text = mode.ToString(),
                AutoSize = true,
                MinimumSize = new Size(130, 34),
                Padding = new Padding(4, 0, 4, 0),
                Image = _tray.Icons.For(mode).ToBitmap().GetThumbnailImage(16, 16, null, IntPtr.Zero),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
            };
            button.Click += async (_, _) => await SwitchAsync(mode);
            _modeButtons[mode] = button;
            buttons.Controls.Add(button);
        }

        return Group("Power mode", _modeLabel, _reasonLabel, buttons, _automation);
    }

    private GroupBox BuildIdleGroup()
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(_idleEnabled);
        row.Controls.Add(_idleMode);
        row.Controls.Add(new Label { Text = "after", AutoSize = true, Margin = new Padding(6, 6, 3, 0) });
        row.Controls.Add(_idleMinutes);
        row.Controls.Add(new Label { Text = "minutes without keyboard or mouse input", AutoSize = true, Margin = new Padding(3, 6, 0, 0) });

        var check = new Button { Text = "Check now", AutoSize = true };
        check.Click += (_, _) => ShowIdleCheck();

        _idleEnabled.CheckedChanged += (_, _) => UpdateIdleControls();
        _respectRequests.Margin = _fullscreen.Margin = new Padding(20, 3, 3, 3);
        return Group("When idle", row, _respectRequests, _fullscreen, _elevationNote, check);
    }

    private GroupBox BuildScheduleGroup()
    {
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "On", FillWeight = 12 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Days", HeaderText = "Days", FillWeight = 40 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "From", HeaderText = "From", FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "To", HeaderText = "To", FillWeight = 20 });
        var modeColumn = new DataGridViewComboBoxColumn { Name = "Mode", HeaderText = "Mode", FillWeight = 30, FlatStyle = FlatStyle.Flat };
        modeColumn.Items.AddRange(PowerModes.All.Select(m => m.ToString()).ToArray<object>());
        _grid.Columns.Add(modeColumn);
        _grid.Width = 620;

        var add = new Button { Text = "Add", AutoSize = true };
        add.Click += (_, _) => _grid.Rows.Add(true, "Weekdays", "09:00", "17:00", PowerMode.Enthusiast.ToString());
        var remove = new Button { Text = "Remove", AutoSize = true };
        remove.Click += (_, _) =>
        {
            foreach (DataGridViewRow row in _grid.SelectedRows)
                _grid.Rows.Remove(row);
        };
        var buttons = new FlowLayoutPanel { AutoSize = true };
        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);

        var help = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Days: Daily, Weekdays, Weekends, Mon-Fri, Sat,Sun ...  Times are 24-hour HH:mm; an end time " +
                   "before the start runs past midnight. Lower rows win when rules overlap. Picking a mode by hand " +
                   "during a rule keeps your choice until that rule ends.",
        };
        return Group("Schedule", _grid, buttons, help);
    }

    private GroupBox BuildHotkeyGroup()
    {
        var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Margin = new Padding(20, 3, 3, 3) };
        foreach (var (mode, box) in _hotkeyBoxes)
        {
            table.Controls.Add(new Label { Text = mode.ToString(), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 0) });
            table.Controls.Add(box);
            // While recording, the current shortcuts must not fire (they'd swallow the keys).
            box.Enter += (_, _) => _tray.Hotkeys.Suspended = true;
            box.Leave += (_, _) => _tray.Hotkeys.Suspended = false;
        }
        _hotkeysEnabled.CheckedChanged += (_, _) => table.Enabled = _hotkeysEnabled.Checked;

        var help = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            ForeColor = SystemColors.GrayText,
            Text = "Click a box and press the shortcut you want (it must include Ctrl, Alt or Win). Backspace clears it.",
        };
        return Group("Keyboard shortcuts", _hotkeysEnabled, table, help, _hotkeyProblems);
    }

    private GroupBox BuildStartupGroup() => Group("Startup", _autostart);

    private FlowLayoutPanel BuildButtons()
    {
        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(80, 0) };
        save.Click += (_, _) => Save();
        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(80, 0), DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        AcceptButton = save;
        CancelButton = close;
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Width = 640, Margin = new Padding(0, 6, 0, 0) };
        panel.Controls.Add(close);
        panel.Controls.Add(save);
        return panel;
    }

    private void LoadSettings(AppConfig config)
    {
        _automation.Checked = config.AutomationEnabled;
        _idleEnabled.Checked = config.Idle.Enabled;
        _idleMode.SelectedItem = config.Idle.Mode.ToString();
        _idleMinutes.Value = (decimal)Math.Clamp(Math.Round(config.Idle.Minutes), 1, 1440);
        _respectRequests.Checked = config.Idle.RespectPowerRequests;
        _fullscreen.Checked = config.Idle.FullscreenCountsAsActive;

        _grid.Rows.Clear();
        foreach (var rule in config.Schedule)
        {
            var index = _grid.Rows.Add(rule.Enabled, rule.Days, rule.From, rule.To, rule.Mode.ToString());
            _grid.Rows[index].Tag = rule.Id;
        }

        _hotkeysEnabled.Checked = config.Hotkeys.Enabled;
        foreach (var (mode, box) in _hotkeyBoxes)
            box.Shortcut = Hotkey.TryParse(config.Hotkeys.For(mode), out var hotkey, out _)
                ? hotkey.ToString()
                : config.Hotkeys.For(mode);

        _autostart.Checked = Autostart.IsEnabled();
        _elevationNote.Text = PowerRequests.CanQuery
            ? ""
            : "Running without administrator rights: videos, games and calls can't be detected yet. " +
              "Enable \"Start at logon\" below and restart the app to fix this.";
        _elevationNote.Visible = !PowerRequests.CanQuery;
        UpdateIdleControls();
    }

    private void UpdateIdleControls()
    {
        _idleMode.Enabled = _idleMinutes.Enabled = _respectRequests.Enabled = _fullscreen.Enabled = _idleEnabled.Checked;
    }

    private void Save()
    {
        var rules = new List<ScheduleRule>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var rule = new ScheduleRule
            {
                Id = row.Tag as string ?? ScheduleRule.NewId(),
                Enabled = row.Cells["On"].Value is true,
                Days = (row.Cells["Days"].Value as string ?? "").Trim(),
                From = (row.Cells["From"].Value as string ?? "").Trim(),
                To = (row.Cells["To"].Value as string ?? "").Trim(),
                Mode = PowerModes.TryParse(row.Cells["Mode"].Value as string, out var m) ? m : PowerMode.Balanced,
            };
            if (ScheduleEvaluator.Validate(rule) is { } error)
            {
                _grid.CurrentCell = row.Cells["Days"];
                MessageBox.Show(this, $"Schedule row {row.Index + 1}: {error}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DaySets.TryParse(rule.Days, out var days, out _);
            rule.Days = DaySets.Format(days);
            rules.Add(rule);
        }

        var hotkeys = new HotkeySettings { Enabled = _hotkeysEnabled.Checked };
        foreach (var (mode, box) in _hotkeyBoxes)
            hotkeys.Set(mode, box.Shortcut);
        if (hotkeys.Validate() is { } hotkeyError)
        {
            MessageBox.Show(this, hotkeyError, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ConfigStore.Update(c =>
        {
            c.Hotkeys = hotkeys;
            c.AutomationEnabled = _automation.Checked;
            c.Idle.Enabled = _idleEnabled.Checked;
            c.Idle.Mode = Enum.Parse<PowerMode>((string)_idleMode.SelectedItem!);
            c.Idle.Minutes = (double)_idleMinutes.Value;
            c.Idle.RespectPowerRequests = _respectRequests.Checked;
            c.Idle.FullscreenCountsAsActive = _fullscreen.Checked;
            c.Schedule = rules;
        });

        if (_autostart.Checked != Autostart.IsEnabled() && !UpdateAutostart(_autostart.Checked))
            _autostart.Checked = Autostart.IsEnabled();

        _tray.Controller.ReloadConfig();
        LoadSettings(_tray.Controller.Config);
        RefreshState();
    }

    private bool UpdateAutostart(bool enable)
    {
        try
        {
            var trayExe = Environment.ProcessPath!;
            if (PowerRequests.CanQuery)
            {
                if (enable)
                    Autostart.Enable(trayExe);
                else
                    Autostart.Disable();
                return true;
            }

            var cli = Path.Combine(AppContext.BaseDirectory, "uwpower.exe");
            if (!File.Exists(cli))
                throw new FileNotFoundException("uwpower.exe was not found next to UwPowerTray.exe.");
            if (Autostart.RunElevated(cli, $"autostart {(enable ? "enable" : "disable")}") != 0)
                throw new InvalidOperationException("Updating the logon task failed.");
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Start at logon", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private async Task SwitchAsync(PowerMode mode)
    {
        try
        {
            await _tray.Controller.SetModeManuallyAsync(mode);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not switch mode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowIdleCheck()
    {
        var settings = new IdleSettings
        {
            Minutes = (double)_idleMinutes.Value,
            RespectPowerRequests = _respectRequests.Checked,
            FullscreenCountsAsActive = _fullscreen.Checked,
            IgnoredRequests = _tray.Controller.Config.Idle.IgnoredRequests,
        };
        var s = new IdleDetector().Evaluate(settings, checkBlockersAlways: true);
        var text = $"No keyboard/mouse input for {(int)s.InputIdle.TotalSeconds} s " +
                   $"(threshold {s.Threshold.TotalMinutes.ToString("0.#", CultureInfo.CurrentCulture)} min).\n\n" +
                   (s.Blockers.Count == 0
                       ? "Nothing else is keeping the PC active."
                       : "Counted as activity:\n" + string.Join("\n", s.Blockers.Select(b => "  • " + b))) +
                   (s.Note is null ? "" : $"\n\n{s.Note}");
        MessageBox.Show(this, text, "Idle check", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static ComboBox ModeCombo()
    {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
        combo.Items.AddRange(PowerModes.All.Select(m => m.ToString()).ToArray<object>());
        return combo;
    }

    private static GroupBox Group(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(6, 4, 6, 4),
        };
        flow.Controls.AddRange(controls);
        var group = new GroupBox
        {
            Text = title,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(650, 0),
            Margin = new Padding(0, 0, 0, 8),
        };
        group.Controls.Add(flow);
        return group;
    }
}
