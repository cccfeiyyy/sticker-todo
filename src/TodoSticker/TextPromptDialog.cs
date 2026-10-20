using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TodoSticker;

/// <summary>轻量输入对话框（用于新建/重命名分类）。返回 null 表示取消。</summary>
public static class TextPromptDialog
{
    public static string? Show(Window? owner, string title, string label, string initialValue, int maxLength = 20)
    {
        Window dialog = new()
        {
            Title = title,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            SizeToContent = SizeToContent.Height,
            Width = 340,
        };

        TextBox input = new()
        {
            Text = initialValue,
            MaxLength = maxLength,
            Margin = new Thickness(0, 10, 0, 0),
            Padding = new Thickness(10, 7, 10, 7),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
            Foreground = (Brush?)Application.Current.TryFindResource("TextPrimaryBrush") ?? Brushes.Black,
            Background = Brushes.White,
            BorderBrush = (Brush?)Application.Current.TryFindResource("DividerBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        TextBlock hint = new()
        {
            Text = label,
            Margin = new Thickness(0, 4, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 11,
            Foreground = (Brush?)Application.Current.TryFindResource("TextMutedBrush") ?? Brushes.Gray,
        };

        TextBlock heading = new()
        {
            Text = title,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush?)Application.Current.TryFindResource("TextPrimaryBrush") ?? Brushes.Black,
        };

        Button cancel = CreateButton("取消", isPrimary: false);
        Button confirm = CreateButton("确定", isPrimary: true);

        bool accepted = false;

        cancel.Click += (_, _) =>
        {
            accepted = false;
            dialog.Close();
        };

        confirm.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                accepted = true;
                dialog.Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                accepted = false;
                dialog.Close();
                e.Handled = true;
            }
        };

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        StackPanel content = new();
        content.Children.Add(heading);
        content.Children.Add(hint);
        content.Children.Add(input);
        content.Children.Add(buttons);

        Border card = new()
        {
            Background = (Brush?)Application.Current.TryFindResource("CardBgBrush") ?? Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20, 16, 20, 16),
            Child = content,
        };

        dialog.Content = new Grid { Margin = new Thickness(16), Children = { card } };
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        dialog.ShowDialog();
        if (!accepted)
        {
            return null;
        }

        string value = input.Text.Trim();
        return value.Length == 0 ? null : value;
    }

    private static Button CreateButton(string text, bool isPrimary)
    {
        return new Button
        {
            Content = text,
            Width = 76,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 12,
            Cursor = Cursors.Hand,
            Foreground = isPrimary ? Brushes.White : ((Brush?)Application.Current.TryFindResource("TextSecondaryBrush") ?? Brushes.Gray),
            BorderThickness = new Thickness(isPrimary ? 0 : 1),
            BorderBrush = (Brush?)Application.Current.TryFindResource("DividerBrush") ?? Brushes.Gray,
            Background = isPrimary
                ? ((Brush?)Application.Current.TryFindResource("AccentBrush") ?? Brushes.DodgerBlue)
                : ((Brush?)Application.Current.TryFindResource("HoverBrush") ?? Brushes.Transparent),
        };
    }
}