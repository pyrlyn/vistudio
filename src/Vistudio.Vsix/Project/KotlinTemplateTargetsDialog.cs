using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Vistudio.Core.Templates;

namespace Vistudio
{
    /// <summary>Asks which Kotlin Multiplatform targets a new project from the Multiplatform template declares.</summary>
    internal sealed class KotlinTemplateTargetsDialog : DialogWindow
    {
        private readonly List<(string Target, CheckBox Box)> boxes = new List<(string, CheckBox)>();

        private KotlinTemplateTargetsDialog(string projectName)
        {
            Title = "Kotlin Multiplatform targets";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            var panel = new StackPanel { Margin = new Thickness(16), MinWidth = 340 };
            panel.Children.Add(new TextBlock { Text = "Targets for " + projectName + ":", Margin = new Thickness(0, 0, 0, 8) });
            var ok = new Button { Content = "Create", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            foreach (var target in KotlinProjectTemplate.MultiplatformTargets)
            {
                var box = new CheckBox
                {
                    Content = target + " — " + KotlinProjectTemplate.PlatformDisplayName(target),
                    IsChecked = true,
                    Margin = new Thickness(0, 2, 0, 2),
                };
                box.Checked += (_, __) => ok.IsEnabled = true;
                box.Unchecked += (_, __) => ok.IsEnabled = boxes.Any(b => b.Box.IsChecked == true);
                boxes.Add((target, box));
                panel.Children.Add(box);
            }
            panel.Children.Add(new TextBlock
            {
                Text = "The first build downloads Node.js for js/wasmJs and the Kotlin/Native toolchain for mingwX64.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
                Margin = new Thickness(0, 8, 0, 0),
            });
            ok.Click += (_, __) => DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
            panel.Children.Add(buttons);
            Content = panel;
        }

        /// <summary>The chosen targets in canonical order, or null when canceled.</summary>
        public static IReadOnlyList<string>? Choose(string projectName)
        {
            var dialog = new KotlinTemplateTargetsDialog(projectName);
            return dialog.ShowModal() == true ? dialog.boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Target).ToArray() : null;
        }
    }
}
