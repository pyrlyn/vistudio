using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Vistudio.Core.Packages;

namespace Vistudio.Packages
{
    /// <summary>Asks for the package to add: name, version or constraint, scope, and a source URL where the manager needs one.</summary>
    internal sealed class AddPackageDialog : DialogWindow
    {
        private readonly TextBox name = new TextBox { MinWidth = 320 };
        private readonly TextBox version = new TextBox();
        private readonly TextBox source = new TextBox();
        private readonly ComboBox scope = new ComboBox();
        private readonly TextBlock error = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
        private PackageRequest? request;

        private AddPackageDialog(IPackageManager manager, string manifest)
        {
            Title = "Add Package (" + manager.DisplayName + ")";
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false;
            HasMaximizeButton = false;
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = "Adds to " + manifest, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Margin = new Thickness(0, 0, 0, 8) });
            Field(panel, NameLabel(manager.Ecosystem), name);
            Field(panel, "Version or constraint (empty: the manager picks the newest):", version);
            foreach (var value in Enum.GetNames(typeof(PackageScope))) scope.Items.Add(value);
            scope.SelectedIndex = 0;
            Field(panel, "Scope (Runtime, Development = tests/dev tools, Build = build-time):", scope);
            if (manager.Ecosystem == PackageEcosystem.Zig) Field(panel, "Source URL (zig fetch):", source);
            panel.Children.Add(error);
            var ok = new Button { Content = "Add", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (_, __) =>
            {
                try
                {
                    request = new PackageRequest(name.Text, version.Text, (PackageScope)Enum.Parse(typeof(PackageScope), (string)scope.SelectedItem), source.Text);
                    DialogResult = true;
                }
                catch (ArgumentException e) { error.Text = e.Message; }
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 });
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, __) => name.Focus();
        }

        private static string NameLabel(PackageEcosystem ecosystem)
        {
            switch (ecosystem)
            {
                case PackageEcosystem.Gradle: return "Module (group:artifact):";
                case PackageEcosystem.Conan: return "Reference name (for example fmt):";
                case PackageEcosystem.Zig: return "Dependency name in build.zig.zon:";
                case PackageEcosystem.Wkg: return "Package (namespace:name):";
                default: return "Package name:";
            }
        }

        private static void Field(Panel panel, string label, Control control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
            panel.Children.Add(control);
        }

        /// <summary>The validated request, or null when canceled.</summary>
        public static PackageRequest? Ask(IPackageManager manager, string manifest)
        {
            var dialog = new AddPackageDialog(manager, manifest);
            return dialog.ShowModal() == true ? dialog.request : null;
        }
    }
}
