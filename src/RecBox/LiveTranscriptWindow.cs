using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using RecBox.Core.Transcription;
using RecBox.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RecBox;

/// <summary>
/// The Live text screen: what is said while recording, "You" (mic) and "Others" (system audio) with times,
/// auto-scroll that pauses when you scroll up, Copy all, Translate to English and the current lag.
/// Opens from the main window with one click and can stay open while recording. Bound to MainViewModel.
/// </summary>
public sealed class LiveTranscriptWindow : FluentWindow
{
    private const string Layout = """
        <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto" />
                <RowDefinition Height="Auto" />
                <RowDefinition Height="*" />
            </Grid.RowDefinitions>

            <ui:TitleBar Grid.Row="0" Title="Live text" CloseWindowByDoubleClickOnIcon="False" />

            <StackPanel Grid.Row="1" Margin="20,0,20,10">
                <Grid>
                    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                        <Ellipse Width="8" Height="8" Margin="2,0,8,0" Fill="{StaticResource RecBrush}"
                                 Visibility="{Binding IsRecording, Converter={StaticResource BoolToVis}}" />
                        <TextBlock x:Name="LagText" Text="{Binding LiveLag}" FontSize="14" FontWeight="SemiBold"
                                   Foreground="{DynamicResource TextFillColorPrimaryBrush}" />
                    </StackPanel>
                    <ui:Button HorizontalAlignment="Right" Content="Copy all" Icon="{ui:SymbolIcon Copy24}"
                               Command="{Binding CopyLiveCommand}" AutomationProperties.Name="Copy all live text" />
                </Grid>
                <TextBlock Text="{Binding LiveStatus}" Style="{StaticResource Caption}" Margin="0,6,0,0" />
                <Grid Margin="0,10,0,0">
                    <TextBlock Text="Live text while recording" Style="{StaticResource Body}" VerticalAlignment="Center" Margin="0,0,64,0" />
                    <ui:ToggleSwitch HorizontalAlignment="Right" IsChecked="{Binding LiveEnabled}"
                                     AutomationProperties.Name="Live text while recording" />
                </Grid>
                <Grid Margin="0,8,0,0">
                    <TextBlock Text="Translate to English" Style="{StaticResource Body}" VerticalAlignment="Center" Margin="0,0,64,0" />
                    <ui:ToggleSwitch HorizontalAlignment="Right" IsChecked="{Binding LiveTranslate}"
                                     AutomationProperties.Name="Translate live text to English" />
                </Grid>
            </StackPanel>

            <Border Grid.Row="2" Style="{StaticResource Panel}" Margin="20,0,20,16">
                <Grid>
                    <ScrollViewer x:Name="LiveScroll" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled"
                                  Padding="14,10" Focusable="False">
                        <ItemsControl ItemsSource="{Binding LiveLines}">
                            <ItemsControl.ItemTemplate>
                                <DataTemplate>
                                    <Grid Margin="0,0,0,8">
                                        <Grid.ColumnDefinitions>
                                            <ColumnDefinition Width="56" />
                                            <ColumnDefinition Width="*" />
                                        </Grid.ColumnDefinitions>
                                        <TextBlock Text="{Binding Time}" Style="{StaticResource Caption}" Margin="0,2,0,0"
                                                   Typography.NumeralAlignment="Tabular" />
                                        <TextBlock Grid.Column="1" FontSize="14" TextWrapping="Wrap"
                                                   Foreground="{DynamicResource TextFillColorPrimaryBrush}">
                                            <TextBlock.Style>
                                                <Style TargetType="TextBlock">
                                                    <Style.Triggers>
                                                        <DataTrigger Binding="{Binding IsNote}" Value="True">
                                                            <Setter Property="FontStyle" Value="Italic" />
                                                            <Setter Property="Opacity" Value="0.7" />
                                                        </DataTrigger>
                                                    </Style.Triggers>
                                                </Style>
                                            </TextBlock.Style>
                                            <Run Text="{Binding Label, Mode=OneWay}" FontWeight="SemiBold">
                                                <Run.Style>
                                                    <Style TargetType="Run">
                                                        <Style.Triggers>
                                                            <DataTrigger Binding="{Binding IsYou}" Value="True">
                                                                <Setter Property="Foreground" Value="{DynamicResource AccentTextFillColorPrimaryBrush}" />
                                                            </DataTrigger>
                                                        </Style.Triggers>
                                                    </Style>
                                                </Run.Style>
                                            </Run><Run Text="{Binding Text, Mode=OneWay}" />
                                        </TextBlock>
                                    </Grid>
                                </DataTemplate>
                            </ItemsControl.ItemTemplate>
                        </ItemsControl>
                    </ScrollViewer>
                    <TextBlock Text="Live text appears here while you record. You = your microphone, Others = the system audio (the other side of a call)."
                               Style="{StaticResource Caption}" Margin="14,10"
                               Visibility="{Binding LiveLines.Count, Converter={StaticResource ZeroToVis}}" />
                    <ui:Button x:Name="JumpButton" Content="Jump to latest" Icon="{ui:SymbolIcon ArrowDown24}" Appearance="Primary"
                               HorizontalAlignment="Center" VerticalAlignment="Bottom" Margin="0,0,0,10" Visibility="Collapsed"
                               AutomationProperties.Name="Jump to the latest live text" />
                </Grid>
            </Border>
        </Grid>
        """;

    private readonly LiveScrollFollow _follow = new();
    private readonly ScrollViewer _scroll;
    private readonly FrameworkElement _jump;

    public LiveTranscriptWindow(MainViewModel vm)
    {
        DataContext = vm;
        Title = "RecBox live text";
        Width = 520;
        Height = 640;
        MinWidth = 360;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowCornerPreference = WindowCornerPreference.Round;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/recbox.ico")); } catch { }

        var root = BuildContent();
        Content = root;
        _scroll = (ScrollViewer)root.FindName("LiveScroll");
        _jump = (FrameworkElement)root.FindName("JumpButton");
        _scroll.ScrollChanged += OnScrollChanged;
        ((System.Windows.Controls.Button)_jump).Click += (_, _) =>
        {
            _follow.JumpToLatest();
            _scroll.ScrollToEnd();
            _jump.Visibility = Visibility.Collapsed;
        };
        Loaded += (_, _) =>
        {
            ApplicationThemeManager.ApplySystemTheme();
            SystemThemeWatcher.Watch(this);
            _scroll.ScrollToEnd();
        };
    }

    /// <summary>The window content alone (also used to lay it out off-screen without showing a window).</summary>
    public static FrameworkElement BuildContent() => (FrameworkElement)XamlReader.Parse(Layout);

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_follow.OnScrollChanged(_scroll.VerticalOffset, _scroll.ScrollableHeight, e.ExtentHeightChange)) _scroll.ScrollToEnd();
        _jump.Visibility = _follow.Following ? Visibility.Collapsed : Visibility.Visible;
    }
}
