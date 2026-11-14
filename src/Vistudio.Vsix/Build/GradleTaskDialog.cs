using System;
using System.Linq;
using System.Windows.Forms;
using Vistudio.Core;

namespace Vistudio
{
    internal sealed class GradleTaskDialog : Form
    {
        private readonly ListBox list = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
        public string[] SelectedTasks => list.SelectedItems.Cast<GradleTaskInfo>().Select(x => x.Path).ToArray();

        public GradleTaskDialog(GradleTaskInfo[] tasks)
        {
            Text = "Kotlin: Gradle targets and tasks";
            Width = 1000; Height = 600; MinimizeBox = false; MaximizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            var search = new TextBox { Dock = DockStyle.Top, AccessibleName = "Filter tasks by target, name or description" };
            var hint = new Label { Dock = DockStyle.Top, Height = 44, Text = "Filter by target (jvm, mingw, js, wasm) or operation (build, test, run). Select tasks, then Run.\r\nOnly targets configured in this Gradle project are available. Ctrl+click selects multiple tasks." };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
            var run = new Button { Text = "Run selected", DialogResult = DialogResult.OK, AutoSize = true, Enabled = false };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(run); buttons.Controls.Add(cancel);
            Controls.Add(list); Controls.Add(search); Controls.Add(hint); Controls.Add(buttons);
            AcceptButton = run; CancelButton = cancel;
            list.AccessibleName = "Gradle tasks";
            list.SelectedIndexChanged += (_, __) => run.Enabled = list.SelectedItems.Count > 0;
            void Filter()
            {
                list.BeginUpdate(); list.Items.Clear();
                list.Items.AddRange(tasks.Where(x => x.ToString().IndexOf(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).Cast<object>().ToArray());
                list.EndUpdate();
            }
            search.TextChanged += (_, __) => Filter();
            Filter();
        }
    }
}
