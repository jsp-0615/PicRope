using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace ScreenLine
{
    internal sealed class CaptureShortcutWindow : Window
    {
        static readonly ResourceDictionary styles = (ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Style x:Key='Modifier' TargetType='CheckBox'>
    <Setter Property='Foreground' Value='#536172'/><Setter Property='Cursor' Value='Hand'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
      <Border x:Name='surface' Background='#F6F8FB' BorderBrush='#E1E7EE' BorderThickness='1' CornerRadius='8' Padding='14,8'>
        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
      </Border>
      <ControlTemplate.Triggers>
        <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='surface' Property='Background' Value='#EDF3FA'/></Trigger>
        <Trigger Property='IsChecked' Value='True'><Setter Property='Foreground' Value='#2463AE'/><Setter Property='FontWeight' Value='SemiBold'/><Setter TargetName='surface' Property='Background' Value='#EAF3FF'/><Setter TargetName='surface' Property='BorderBrush' Value='#90B5DD'/></Trigger>
        <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='surface' Property='BorderBrush' Value='#4F86C3'/></Trigger>
      </ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='KeyItem' TargetType='ComboBoxItem'>
    <Setter Property='Padding' Value='12,7'/><Setter Property='Foreground' Value='#283343'/><Setter Property='HorizontalContentAlignment' Value='Stretch'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBoxItem'>
      <Border x:Name='surface' Background='Transparent' CornerRadius='5' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border>
      <ControlTemplate.Triggers><Trigger Property='IsHighlighted' Value='True'><Setter TargetName='surface' Property='Background' Value='#EDF4FD'/></Trigger><Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='#2463AE'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
  <Style x:Key='KeyBox' TargetType='ComboBox'>
    <Setter Property='Height' Value='42'/><Setter Property='Foreground' Value='#283343'/><Setter Property='FocusVisualStyle' Value='{x:Null}'/><Setter Property='Cursor' Value='Hand'/><Setter Property='ScrollViewer.CanContentScroll' Value='True'/>
    <Setter Property='ItemContainerStyle' Value='{StaticResource KeyItem}'/>
    <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ComboBox'>
      <Grid>
        <ToggleButton x:Name='toggle' Focusable='False' IsChecked='{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}' ClickMode='Press'>
          <ToggleButton.Template><ControlTemplate TargetType='ToggleButton'><Border x:Name='surface' Background='White' BorderBrush='#DCE3EB' BorderThickness='1' CornerRadius='8'/><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='surface' Property='BorderBrush' Value='#8EADD0'/></Trigger><Trigger Property='IsChecked' Value='True'><Setter TargetName='surface' Property='BorderBrush' Value='#6E97CB'/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
        </ToggleButton>
        <ContentPresenter Margin='14,0,38,0' VerticalAlignment='Center' HorizontalAlignment='Left' Content='{TemplateBinding SelectionBoxItem}' IsHitTestVisible='False'/>
        <Path Data='M0,0 L5,5 L10,0' Stroke='#718092' StrokeThickness='1.6' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round' Width='10' Height='5' HorizontalAlignment='Right' Margin='0,0,16,0' VerticalAlignment='Center' IsHitTestVisible='False'/>
        <Popup x:Name='PART_Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
          <Border MinWidth='{TemplateBinding ActualWidth}' MaxHeight='220' Background='White' BorderBrush='#DCE3EB' BorderThickness='1' CornerRadius='8' Padding='4' Margin='0,4,0,0'>
            <ScrollViewer CanContentScroll='True'><ItemsPresenter/></ScrollViewer>
          </Border>
        </Popup>
      </Grid>
      <ControlTemplate.Triggers><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter Property='Foreground' Value='#2463AE'/></Trigger></ControlTemplate.Triggers>
    </ControlTemplate></Setter.Value></Setter>
  </Style>
</ResourceDictionary>");
        readonly ComboBox keys = new ComboBox();
        readonly CheckBox control = new CheckBox { Content = "Ctrl" };
        readonly CheckBox alt = new CheckBox { Content = "Alt" };
        readonly CheckBox shift = new CheckBox { Content = "Shift" };
        readonly CheckBox win = new CheckBox { Content = "Win" };
        readonly TextBlock preview = new TextBlock();
        readonly TextBlock status = new TextBlock { Foreground = LightTheme.Brush("#BD3A33"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        readonly Func<int, int, string> save;

        public CaptureShortcutWindow(int key, int modifiers, Func<int, int, string> save)
        {
            this.save = save;
            Title = "截图快捷键"; Width = 500; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; Topmost = true;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; ShowInTaskbar = false; FontFamily = new FontFamily("Segoe UI");
            AutomationProperties.SetAutomationId(this, "CaptureShortcutSettings");
            var body = new StackPanel();
            var heading = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            heading.Children.Add(new TextBlock { Text = "截图快捷键", FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#283343"), VerticalAlignment = VerticalAlignment.Center });
            var close = LightTheme.Button("", Close, "quiet");
            close.Content = LightTheme.CloseIcon(); close.Width = close.Height = 30; close.MinWidth = 0; close.Padding = new Thickness(0); close.HorizontalAlignment = HorizontalAlignment.Right;
            AutomationProperties.SetName(close, "关闭快捷键设置"); heading.Children.Add(close); body.Children.Add(heading);
            body.Children.Add(new TextBlock { Text = "设置一个顺手的组合，随时开始区域截图。", FontSize = 12, Foreground = LightTheme.Brush("#718092"), Margin = new Thickness(0, 0, 0, 20) });

            var previewRow = new Grid();
            previewRow.ColumnDefinitions.Add(new ColumnDefinition()); previewRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var previewCaption = new StackPanel();
            previewCaption.Children.Add(new TextBlock { Text = "快捷键预览", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#344459") });
            previewCaption.Children.Add(new TextBlock { Text = "按下后开始截图", FontSize = 11, Foreground = LightTheme.Brush("#8190A0"), Margin = new Thickness(0, 4, 0, 0) });
            previewRow.Children.Add(previewCaption);
            preview.FontSize = 15; preview.FontWeight = FontWeights.SemiBold; preview.Foreground = LightTheme.Brush("#2463AE"); preview.VerticalAlignment = VerticalAlignment.Center;
            previewRow.Children.Add(preview); Grid.SetColumn(preview, 1);
            body.Children.Add(new Border { Background = LightTheme.Brush("#F2F7FD"), BorderBrush = LightTheme.Brush("#E4EFFA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(15, 12, 15, 12), Child = previewRow });

            body.Children.Add(Label("组合键", "可选", new Thickness(0, 20, 0, 9)));
            var flags = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var modifier in new[] { control, alt, shift, win })
            {
                modifier.Style = (Style)styles["Modifier"]; modifier.Margin = new Thickness(0, 0, 8, 0); modifier.MinWidth = 66;
                modifier.Checked += Changed; modifier.Unchecked += Changed; flags.Children.Add(modifier);
            }
            body.Children.Add(flags);
            body.Children.Add(Label("主按键", null, new Thickness(0, 19, 0, 9)));
            keys.Style = (Style)styles["KeyBox"]; AutomationProperties.SetName(keys, "截图主按键");
            for (int i = 112; i <= 135; i++) keys.Items.Add(new KeyOption(i, "F" + (i - 111)));
            for (int i = 65; i <= 90; i++) keys.Items.Add(new KeyOption(i, ((char)i).ToString()));
            for (int i = 48; i <= 57; i++) keys.Items.Add(new KeyOption(i, ((char)i).ToString()));
            foreach (KeyOption option in keys.Items) if (option.Key == key) keys.SelectedItem = option;
            if (keys.SelectedItem == null) keys.SelectedIndex = 0;
            control.IsChecked = (modifiers & 2) != 0; alt.IsChecked = (modifiers & 1) != 0;
            shift.IsChecked = (modifiers & 4) != 0; win.IsChecked = (modifiers & 8) != 0;
            keys.SelectionChanged += delegate { UpdatePreview(); };
            body.Children.Add(keys); body.Children.Add(status); UpdatePreview();

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            var cancel = LightTheme.Button("取消", Close); cancel.Margin = new Thickness(0, 0, 8, 0);
            var submit = LightTheme.Button("保存快捷键", Apply, "primary"); submit.MinWidth = 112;
            actions.Children.Add(cancel); actions.Children.Add(submit); body.Children.Add(actions);
            Content = LightTheme.Card(body);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            PreviewKeyDown += delegate(object sender, KeyEventArgs args) { if (args.Key == Key.Escape && !keys.IsDropDownOpen) Close(); };
        }
        static UIElement Label(string title, string detail, Thickness margin)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = margin };
            line.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = LightTheme.Brush("#435365") });
            if (detail != null) line.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = LightTheme.Brush("#95A1AE"), Margin = new Thickness(7, 0, 0, 0) });
            return line;
        }
        void Changed(object sender, RoutedEventArgs args) { UpdatePreview(); }
        int Modifiers() { return (alt.IsChecked == true ? 1 : 0) | (control.IsChecked == true ? 2 : 0) | (shift.IsChecked == true ? 4 : 0) | (win.IsChecked == true ? 8 : 0); }
        void UpdatePreview()
        {
            var selected = keys.SelectedItem as KeyOption;
            if (selected != null) preview.Text = Format(selected.Key, Modifiers());
            status.Visibility = Visibility.Collapsed;
        }
        void Apply()
        {
            int modifiers = Modifiers(); int key = ((KeyOption)keys.SelectedItem).Key;
            string error = key < 112 && modifiers == 0 ? "字母或数字需要搭配至少一个组合键。" : save(key, modifiers);
            if (error == null) Close();
            else { status.Text = error; status.Visibility = Visibility.Visible; }
        }
        sealed class KeyOption
        {
            public readonly int Key; readonly string name;
            public KeyOption(int key, string name) { Key = key; this.name = name; }
            public override string ToString() { return name; }
        }
        internal static bool Valid(int key, int modifiers)
        {
            return (modifiers & ~15) == 0 && ((key >= 112 && key <= 135) || (modifiers != 0 && ((key >= 65 && key <= 90) || (key >= 48 && key <= 57))));
        }
        internal static string Format(int key, int modifiers)
        {
            return ((modifiers & 2) != 0 ? "Ctrl+" : "") + ((modifiers & 1) != 0 ? "Alt+" : "") + ((modifiers & 4) != 0 ? "Shift+" : "") + ((modifiers & 8) != 0 ? "Win+" : "") + (key >= 112 && key <= 135 ? "F" + (key - 111) : ((char)key).ToString());
        }
    }
}
