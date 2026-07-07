# poco运维工具 v2 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在现有文件搬运工具基础上，增加进程监听和版本更新功能，重构为三合一运维工具，并新建 Launcher 启动器项目。

**Architecture:** 修改 MoveImageForm 主窗口为 TabControl 三 Tab 布局（文件搬运 + 进程监听 + 版本更新）。新建 Launcher WPF 项目负责启动时检查 SMB 共享更新、下载、替换、拉起主程序。配置文件 config.xml 由两个程序共用。

**Tech Stack:** WPF .NET Framework 4.7.2, XmlSerializer, System.Windows.Forms (NotifyIcon), SMB 网络共享, Windows Registry

**Source:** `d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\`

---

### Task 1: 提取 AppConfig 到独立文件并扩展配置模型

**Files:**
- Create: `MoveImageForm/AppConfig.cs`
- Create: `MoveImageForm/ProcessInfo.cs`
- Modify: `MoveImageForm/MainWindow.xaml.cs` — 移除底部 AppConfig 类定义
- Modify: `MoveImageForm/MoveImageForm.csproj` — 添加新文件引用

- [ ] **Step 1: 创建 ProcessInfo.cs**

```csharp
using System.Xml.Serialization;

namespace MoveImageForm
{
    public class ProcessInfo
    {
        [XmlElement]
        public string Name { get; set; } = "";

        [XmlElement]
        public string Path { get; set; } = "";

        [XmlElement]
        public bool Enabled { get; set; } = true;
    }
}
```

- [ ] **Step 2: 创建 AppConfig.cs（原有字段 + 新增字段 + WatchProcesses）**

```csharp
using System.Collections.Generic;
using System.Xml.Serialization;

namespace MoveImageForm
{
    [XmlRoot("Config")]
    public class AppConfig
    {
        // ===== 文件搬运（已有，不变） =====
        [XmlElement]
        public string SourcePath { get; set; } = "";
        [XmlElement]
        public string DestPath { get; set; } = "";
        [XmlElement]
        public string SourcePath2 { get; set; } = "";
        [XmlElement]
        public string DestPath2 { get; set; } = "";
        [XmlElement]
        public string TransferMode { get; set; } = "Cut";

        [XmlElement]
        public bool EnableTimeRule { get; set; } = true;
        [XmlElement]
        public int TimeIntervalSeconds { get; set; } = 60;

        [XmlElement]
        public bool EnableSizeRule { get; set; } = false;
        [XmlElement]
        public long SizeLimitMB { get; set; } = 100;

        [XmlElement]
        public bool EnableCountRule { get; set; } = false;
        [XmlElement]
        public int CountLimit { get; set; } = 1000;

        [XmlElement]
        public bool EnableEmptyFolderRule { get; set; } = false;
        [XmlElement]
        public double EmptyFolderHours { get; set; } = 24.0;

        // ===== 版本更新（新增） =====
        [XmlElement]
        public string CloudPath { get; set; } = "";
        [XmlElement]
        public string CloudUser { get; set; } = "";
        [XmlElement]
        public string CloudPassword { get; set; } = "";
        [XmlElement]
        public int CheckIntervalMinutes { get; set; } = 30;
        [XmlElement]
        public bool AutoUpdate { get; set; } = false;
        [XmlElement]
        public bool AutoStart { get; set; } = false;
        [XmlElement]
        public string LastCheckTime { get; set; } = "";

        // ===== 进程监听（新增） =====
        [XmlArray("WatchProcesses")]
        [XmlArrayItem("Process")]
        public List<ProcessInfo> WatchProcesses { get; set; } = new List<ProcessInfo>();
    }
}
```

- [ ] **Step 3: 从 MainWindow.xaml.cs 删除 AppConfig 类定义**

删除 MainWindow.xaml.cs 第 624-643 行的 `public class AppConfig { ... }` 整个类定义。

- [ ] **Step 4: 更新 .csproj 添加新文件**

在 MoveImageForm.csproj 的 `<ItemGroup>` 中添加：

```xml
<Compile Include="AppConfig.cs" />
<Compile Include="ProcessInfo.cs" />
```

- [ ] **Step 5: 构建验证**

Run: `msbuild "d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageForm.sln" /p:Configuration=Debug`
Expected: 编译成功

- [ ] **Step 6: 提交**

```bash
git add MoveImageForm/AppConfig.cs MoveImageForm/ProcessInfo.cs MoveImageForm/MainWindow.xaml.cs MoveImageForm/MoveImageForm.csproj
git commit -m "refactor: extract AppConfig to separate file, add new config fields for v2"
```

---

### Task 2: 主窗口改造 — TabControl 三 Tab 布局 + 文字更新

**Files:**
- Modify: `MoveImageForm/MainWindow.xaml` — 整个文件重写为 TabControl 结构
- Modify: `MoveImageForm/MainWindow.xaml.cs` — 更新托盘文字

- [ ] **Step 1: 重写 MainWindow.xaml 为 TabControl 三 Tab 布局**

```xml
<Window x:Class="MoveImageForm.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="poco运维工具" Height="620" Width="620"
        Loaded="Window_Loaded" Closing="Window_Closing">
    <TabControl Margin="5">
        <!-- ===== Tab 1: 文件搬运 ===== -->
        <TabItem Header="文件搬运">
            <ScrollViewer VerticalScrollBarVisibility="Auto">
                <Grid Margin="5">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>

                    <!-- 路径配置 -->
                    <GroupBox Header="路径配置" Grid.Row="0" Margin="0,0,0,10">
                        <Grid Margin="5">
                            <Grid.RowDefinitions>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="Auto"/>
                                <RowDefinition Height="Auto"/>
                            </Grid.RowDefinitions>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="80"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="60"/>
                            </Grid.ColumnDefinitions>

                            <TextBlock Text="监控文件夹1:" VerticalAlignment="Center" Grid.Row="0" Grid.Column="0"/>
                            <TextBox x:Name="txtSourcePath" Grid.Row="0" Grid.Column="1" Margin="5"/>
                            <Button Content="浏览..." Grid.Row="0" Grid.Column="2" Margin="5" Click="BtnBrowseSource_Click"/>

                            <TextBlock Text="目标文件夹1:" VerticalAlignment="Center" Grid.Row="1" Grid.Column="0"/>
                            <TextBox x:Name="txtDestPath" Grid.Row="1" Grid.Column="1" Margin="5"/>
                            <Button Content="浏览..." Grid.Row="1" Grid.Column="2" Margin="5" Click="BtnBrowseDest_Click"/>

                            <TextBlock Text="监控文件夹2:" VerticalAlignment="Center" Grid.Row="2" Grid.Column="0" Margin="0,5,0,0"/>
                            <TextBox x:Name="txtSourcePath2" Grid.Row="2" Grid.Column="1" Margin="5"/>
                            <Button Content="浏览..." Grid.Row="2" Grid.Column="2" Margin="5" Click="BtnBrowseSource2_Click"/>

                            <TextBlock Text="目标文件夹2:" VerticalAlignment="Center" Grid.Row="3" Grid.Column="0"/>
                            <TextBox x:Name="txtDestPath2" Grid.Row="3" Grid.Column="1" Margin="5"/>
                            <Button Content="浏览..." Grid.Row="3" Grid.Column="2" Margin="5" Click="BtnBrowseDest2_Click"/>
                        </Grid>
                    </GroupBox>

                    <!-- 搬运与清理规则 -->
                    <GroupBox Header="搬运与清理规则" Grid.Row="1" Margin="0,0,0,10">
                        <StackPanel Margin="5">
                            <TextBlock Text="处理方式:" FontWeight="Bold" Margin="0,0,0,5"/>
                            <StackPanel Orientation="Horizontal" Margin="0,5">
                                <RadioButton x:Name="rbCutMode" Content="剪切（移动到目标文件夹）" GroupName="TransferMode" VerticalAlignment="Center"/>
                                <RadioButton x:Name="rbCopyMode" Content="复制（已复制且未变化的文件会跳过）" GroupName="TransferMode" Margin="20,0,0,0" VerticalAlignment="Center"/>
                            </StackPanel>

                            <Separator Margin="0,5"/>
                            <TextBlock Text="触发条件 (满足任一勾选条件即触发搬运):" FontWeight="Bold" Margin="0,0,0,5"/>
                            <StackPanel Orientation="Horizontal" Margin="0,5">
                                <CheckBox x:Name="chkTimeRule" Content="按时间搬运: 每隔 " VerticalAlignment="Center"/>
                                <TextBox x:Name="txtTimeInterval" Width="50" Margin="5,0"/>
                                <TextBlock Text=" 秒" VerticalAlignment="Center"/>
                            </StackPanel>

                            <StackPanel Orientation="Horizontal" Margin="0,5">
                                <CheckBox x:Name="chkSizeRule" Content="按空间搬运: 达到 " VerticalAlignment="Center"/>
                                <TextBox x:Name="txtSizeLimit" Width="50" Margin="5,0"/>
                                <TextBlock Text=" MB" VerticalAlignment="Center"/>
                            </StackPanel>

                            <StackPanel Orientation="Horizontal" Margin="0,5">
                                <CheckBox x:Name="chkCountRule" Content="按文件数量搬运: 达到 " VerticalAlignment="Center"/>
                                <TextBox x:Name="txtCountLimit" Width="50" Margin="5,0"/>
                                <TextBlock Text=" 个文件" VerticalAlignment="Center"/>
                            </StackPanel>

                            <Separator Margin="0,5"/>
                            <TextBlock Text="清理条件 (每次搬运完成后执行):" FontWeight="Bold" Margin="0,5,0,5"/>
                            <StackPanel Orientation="Horizontal" Margin="0,5">
                                <CheckBox x:Name="chkEmptyFolderRule" Content="清理空文件夹: 文件夹创建超过 " VerticalAlignment="Center"/>
                                <TextBox x:Name="txtEmptyFolderHours" Width="50" Margin="5,0"/>
                                <TextBlock Text=" 小时且为空时删除" VerticalAlignment="Center"/>
                            </StackPanel>
                        </StackPanel>
                    </GroupBox>

                    <!-- 操作按钮 -->
                    <StackPanel Orientation="Horizontal" Grid.Row="2" HorizontalAlignment="Center" Margin="0,0,0,10">
                        <Button x:Name="btnStart" Content="开始搬运" Width="100" Height="30" Margin="10,0" Click="BtnStart_Click"/>
                        <Button x:Name="btnStop" Content="停止搬运" Width="100" Height="30" Margin="10,0" IsEnabled="False" Click="BtnStop_Click"/>
                    </StackPanel>

                    <!-- 运行日志 -->
                    <GroupBox Header="运行日志" Grid.Row="3">
                        <ListBox x:Name="lstLog" Margin="5"/>
                    </GroupBox>
                </Grid>
            </ScrollViewer>
        </TabItem>

        <!-- ===== Tab 2: 进程监听 ===== -->
        <TabItem Header="进程监听">
            <Grid Margin="5">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <!-- 添加栏 -->
                <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,10">
                    <TextBlock Text="进程名:" VerticalAlignment="Center"/>
                    <TextBox x:Name="txtProcessName" Width="150" Margin="5,0"/>
                    <TextBlock Text="启动路径:" VerticalAlignment="Center" Margin="10,0,0,0"/>
                    <TextBox x:Name="txtProcessPath" Width="150" Margin="5,0"/>
                    <Button x:Name="btnAddProcess" Content="添加" Width="50" Margin="10,0,0,0" Click="BtnAddProcess_Click"/>
                </StackPanel>

                <!-- 进程列表 -->
                <DataGrid x:Name="dgProcesses" Grid.Row="1" AutoGenerateColumns="False" CanUserAddRows="False" CanUserDeleteRows="False">
                    <DataGrid.Columns>
                        <DataGridTextColumn Header="进程名" Binding="{Binding Name}" Width="140"/>
                        <DataGridTextColumn Header="启动路径" Binding="{Binding Path}" Width="180"/>
                        <DataGridTemplateColumn Header="运行状态" Width="80">
                            <DataGridTemplateColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text="{Binding StatusText}" Foreground="{Binding StatusColor}"/>
                                </DataTemplate>
                            </DataGridTemplateColumn.CellTemplate>
                        </DataGridTemplateColumn>
                        <DataGridTemplateColumn Header="监控" Width="50">
                            <DataGridTemplateColumn.CellTemplate>
                                <DataTemplate>
                                    <CheckBox IsChecked="{Binding Enabled}" HorizontalAlignment="Center"
                                              Checked="ProcessEnabled_Changed" Unchecked="ProcessEnabled_Changed"/>
                                </DataTemplate>
                            </DataGridTemplateColumn.CellTemplate>
                        </DataGridTemplateColumn>
                        <DataGridTemplateColumn Header="操作" Width="50">
                            <DataGridTemplateColumn.CellTemplate>
                                <DataTemplate>
                                    <Button Content="删除" Click="BtnDeleteProcess_Click"
                                            Tag="{Binding}" Style="{StaticResource {x:Static ToolBar.ButtonStyleKey}}"/>
                                </DataTemplate>
                            </DataGridTemplateColumn.CellTemplate>
                        </DataGridTemplateColumn>
                    </DataGrid.Columns>
                </DataGrid>

                <!-- 说明 -->
                <TextBlock Grid.Row="2" Margin="0,8,0,0" FontSize="11" Foreground="#888">
                    每 5 秒轮询一次进程状态。进程不在运行中时，通过右下角气泡提醒 + 日志记录。配置保存到 config.xml 中，启动时自动加载并开始监控。
                </TextBlock>
            </Grid>
        </TabItem>

        <!-- ===== Tab 3: 版本更新 ===== -->
        <TabItem Header="版本更新">
            <Grid Margin="5">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <!-- 版本卡片 -->
                <StackPanel Grid.Row="0" Orientation="Horizontal" Margin="0,0,0,12">
                    <Border Background="#F5F5F5" Padding="20" CornerRadius="4" Width="180">
                        <StackPanel>
                            <TextBlock x:Name="txtLocalVersion" FontSize="28" FontWeight="Bold" Foreground="#0078d4" HorizontalAlignment="Center">-</TextBlock>
                            <TextBlock Text="当前本地版本" FontSize="12" Foreground="#888" HorizontalAlignment="Center" Margin="0,4,0,0"/>
                        </StackPanel>
                    </Border>
                    <Border Background="#F5F5F5" Padding="20" CornerRadius="4" Width="180" Margin="12,0,0,0">
                        <StackPanel>
                            <TextBlock x:Name="txtCloudVersion" FontSize="28" FontWeight="Bold" Foreground="#107c10" HorizontalAlignment="Center">-</TextBlock>
                            <TextBlock Text="云端最新版本" FontSize="12" Foreground="#888" HorizontalAlignment="Center" Margin="0,4,0,0"/>
                        </StackPanel>
                    </Border>
                </StackPanel>

                <!-- 新版本提示横幅 -->
                <Border x:Name="borderNewVersion" Grid.Row="1" Background="#E8F4E8" Padding="10" CornerRadius="4" Margin="0,0,0,12" Visibility="Collapsed">
                    <TextBlock x:Name="txtNewVersionInfo" FontSize="13"/>
                </Border>

                <!-- 上次检查时间 -->
                <TextBlock x:Name="txtLastCheckTime" Grid.Row="2" FontSize="12" Foreground="#888" Margin="0,0,0,12">上次检查时间: 从未检查 | 检查间隔: 30 分钟</TextBlock>

                <!-- 按钮和开关 -->
                <StackPanel Grid.Row="3" Orientation="Horizontal" Margin="0,0,0,12">
                    <Button x:Name="btnCheckUpdate" Content="立即检查" Width="90" Height="30" Click="BtnCheckUpdate_Click"
                            Style="{StaticResource {x:Static ToolBar.ButtonStyleKey}}"/>
                    <CheckBox x:Name="chkAutoCheck" Content="启用定时检查（每30分钟）" VerticalAlignment="Center" Margin="12,0,0,0"
                              Checked="ChkAutoCheck_Changed" Unchecked="ChkAutoCheck_Changed"/>
                </StackPanel>

                <!-- 开机自启动 -->
                <CheckBox x:Name="chkAutoStart" Grid.Row="4" Content="开机自动启动" VerticalAlignment="Center"
                          Checked="ChkAutoStart_Changed" Unchecked="ChkAutoStart_Changed"/>
            </Grid>
        </TabItem>
    </TabControl>
</Window>
```

- [ ] **Step 2: 更新 MainWindow.xaml.cs 中的托盘文字**

修改 InitNotifyIcon() 方法，将第 31 行的托盘文字从 `"图片搬运工具 (后台运行中)"` 改为 `"poco运维工具 (后台运行中)"`。

- [ ] **Step 3: 构建验证**

Run: `msbuild "d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageForm.sln" /p:Configuration=Debug`
Expected: 编译成功（Tab 2 和 Tab 3 的 XAML 事件处理器尚未添加代码，会有编译警告但无错误）

- [ ] **Step 4: 提交**

```bash
git add MoveImageForm/MainWindow.xaml MoveImageForm/MainWindow.xaml.cs
git commit -m "feat: add TabControl 3-tab layout, rename to poco运维工具"
```

---

### Task 3: 实现进程监听 Tab（ViewModel + 逻辑）

**Files:**
- Create: `MoveImageForm/ProcessViewModel.cs`
- Modify: `MoveImageForm/MainWindow.xaml.cs` — 添加进程监听相关代码
- Modify: `MoveImageForm/MainWindow.xaml` — DataGrid 删除按钮改用 Click 事件

- [ ] **Step 1: 创建 ProcessViewModel.cs（带 INotifyPropertyChanged 的可绑定模型）**

```csharp
using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;

namespace MoveImageForm
{
    public class ProcessViewModel : INotifyPropertyChanged
    {
        private string _name;
        private string _path;
        private bool _enabled;
        private bool _isRunning;
        private string _statusText;
        private System.Windows.Media.Brush _statusColor;
        private Action _saveCallback;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string Path
        {
            get => _path;
            set { _path = value; OnPropertyChanged(); }
        }

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); _saveCallback?.Invoke(); }
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                _isRunning = value;
                StatusText = value ? "● 运行中" : "● 未运行";
                StatusColor = value
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 128, 0))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 0, 0));
                OnPropertyChanged();
            }
        }

        private string _statusTextInternal;
        public string StatusText
        {
            get => _statusTextInternal;
            set { _statusTextInternal = value; OnPropertyChanged(); }
        }

        private System.Windows.Media.Brush _statusColorInternal;
        public System.Windows.Media.Brush StatusColor
        {
            get => _statusColorInternal;
            set { _statusColorInternal = value; OnPropertyChanged(); }
        }

        public Action SaveCallback
        {
            set => _saveCallback = value;
        }

        public ProcessViewModel()
        {
            _isRunning = false;
            _statusTextInternal = "● 未知";
            _statusColorInternal = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(136, 136, 136));
        }

        public ProcessInfo ToProcessInfo()
        {
            return new ProcessInfo { Name = this.Name, Path = this.Path, Enabled = this.Enabled };
        }

        public static ProcessViewModel FromProcessInfo(ProcessInfo info, Action saveCallback)
        {
            var vm = new ProcessViewModel
            {
                Name = info.Name,
                Path = info.Path,
                Enabled = info.Enabled,
                SaveCallback = saveCallback
            };
            return vm;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
```

- [ ] **Step 2: 在 MainWindow.xaml.cs 中添加进程监听字段和初始化**

在 `public partial class MainWindow : Window` 类的字段区域添加：

```csharp
private System.Windows.Threading.DispatcherTimer _processTimer;
private System.Collections.ObjectModel.ObservableCollection<ProcessViewModel> _processList;
```

在 `Window_Loaded` 方法末尾添加进程监听的初始化调用：

```csharp
InitProcessMonitoring();
```

- [ ] **Step 3: 添加 InitProcessMonitoring 方法和相关逻辑**

在 MainWindow.xaml.cs 中添加以下方法：

```csharp
private void InitProcessMonitoring()
{
    _processList = new System.Collections.ObjectModel.ObservableCollection<ProcessViewModel>();
    foreach (var proc in _config.WatchProcesses)
    {
        _processList.Add(ProcessViewModel.FromProcessInfo(proc, SaveProcessList));
    }
    dgProcesses.ItemsSource = _processList;

    _processTimer = new System.Windows.Threading.DispatcherTimer();
    _processTimer.Interval = TimeSpan.FromSeconds(5);
    _processTimer.Tick += ProcessTimer_Tick;
    _processTimer.Start();

    // 立即检查一次
    CheckAllProcesses();
}

private void ProcessTimer_Tick(object sender, EventArgs e)
{
    CheckAllProcesses();
}

private void CheckAllProcesses()
{
    foreach (var proc in _processList)
    {
        if (!proc.Enabled)
        {
            proc.IsRunning = false;
            continue;
        }

        try
        {
            string procName = System.IO.Path.GetFileNameWithoutExtension(proc.Name);
            var processes = System.Diagnostics.Process.GetProcessesByName(procName);
            bool wasRunning = proc.IsRunning;
            proc.IsRunning = processes.Length > 0;

            if (wasRunning && !proc.IsRunning)
            {
                string msg = $"进程 {proc.Name} 已停止运行";
                Log(msg);
                Dispatcher.InvokeAsync(() =>
                {
                    _notifyIcon.ShowBalloonTip(3000, "进程监听", msg,
                        System.Windows.Forms.ToolTipIcon.Warning);
                });
            }
        }
        catch
        {
            proc.IsRunning = false;
        }
    }
}

private void BtnAddProcess_Click(object sender, RoutedEventArgs e)
{
    string name = txtProcessName.Text.Trim();
    string path = txtProcessPath.Text.Trim();

    if (string.IsNullOrWhiteSpace(name))
    {
        System.Windows.MessageBox.Show("请输入进程名！");
        return;
    }

    // 检查是否已存在
    foreach (var p in _processList)
    {
        if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            System.Windows.MessageBox.Show($"进程 {name} 已在监控列表中！");
            return;
        }
    }

    var vm = new ProcessViewModel
    {
        Name = name,
        Path = path,
        Enabled = true,
        SaveCallback = SaveProcessList
    };
    _processList.Add(vm);
    SaveProcessList();

    txtProcessName.Clear();
    txtProcessPath.Clear();
    Log($"已添加进程监控: {name}");
}

private void ProcessEnabled_Changed(object sender, RoutedEventArgs e)
{
    // Enabled 属性绑定自动触发 SaveCallback，此处仅做日志
    Log("进程监控开关已更新");
}

private void BtnDeleteProcess_Click(object sender, RoutedEventArgs e)
{
    var button = sender as System.Windows.Controls.Button;
    var vm = button?.Tag as ProcessViewModel;
    if (vm != null)
    {
        _processList.Remove(vm);
        SaveProcessList();
        Log($"已删除进程监控: {vm.Name}");
    }
}

private void SaveProcessList()
{
    _config.WatchProcesses.Clear();
    foreach (var vm in _processList)
    {
        _config.WatchProcesses.Add(vm.ToProcessInfo());
    }
    SaveConfig();
}
```

- [ ] **Step 4: 更新 MainWindow.xaml 中 DataGrid 删除按钮的 Tag 绑定**

将 Tab 2 DataGrid 删除按钮的 `Tag="{Binding}"` 确保正确。同时更新删除按钮不使用 ToolBar.ButtonStyleKey（在 DataGrid 中可能不可用），改用普通 Button:

修改删除按钮为：
```xml
<Button Content="删除" Click="BtnDeleteProcess_Click" Tag="{Binding}" Width="40" Height="22"/>
```

- [ ] **Step 5: 构建验证**

Run: `msbuild "d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageForm.sln" /p:Configuration=Debug`
Expected: 编译成功

- [ ] **Step 6: 提交**

```bash
git add MoveImageForm/ProcessViewModel.cs MoveImageForm/MainWindow.xaml.cs MoveImageForm/MainWindow.xaml MoveImageForm/MoveImageForm.csproj
git commit -m "feat: implement process monitoring tab with 5s polling and tray alerts"
```

---

### Task 4: 实现版本更新 Tab

**Files:**
- Modify: `MoveImageForm/MainWindow.xaml.cs` — 添加版本更新相关代码
- Modify: `MoveImageForm/MoveImageForm.csproj` — 添加 System.Web.Extensions 引用（JSON 解析）

- [ ] **Step 1: 在 .csproj 中添加 System.Web.Extensions 引用**

```xml
<Reference Include="System.Web.Extensions" />
```

- [ ] **Step 2: 在 MainWindow.xaml.cs 中添加版本更新字段**

在类的字段区域添加：

```csharp
private System.Windows.Threading.DispatcherTimer _updateTimer;
private string _latestCloudVersion;
private string _latestCloudDate;
private string _latestCloudNote;
```

- [ ] **Step 3: 添加版本更新初始化方法，在 Window_Loaded 末尾调用**

在 `Window_Loaded` 方法末尾添加：

```csharp
InitVersionUpdate();
```

添加实现方法：

```csharp
private void InitVersionUpdate()
{
    // 显示本地版本
    var asm = System.Reflection.Assembly.GetExecutingAssembly();
    var ver = asm.GetName().Version;
    txtLocalVersion.Text = $"{ver.Major}.{ver.Minor}.{ver.Build}";

    // 读取上次检查时间
    if (!string.IsNullOrWhiteSpace(_config.LastCheckTime))
    {
        txtLastCheckTime.Text = $"上次检查时间: {_config.LastCheckTime} | 检查间隔: {_config.CheckIntervalMinutes} 分钟";
    }

    // 开机自启动开关
    chkAutoStart.IsChecked = _config.AutoStart;

    // 定时检查开关
    chkAutoCheck.IsChecked = _config.CheckIntervalMinutes > 0;
    if (_config.CheckIntervalMinutes > 0)
    {
        chkAutoCheck.Content = $"启用定时检查（每{_config.CheckIntervalMinutes}分钟）";
        _updateTimer = new System.Windows.Threading.DispatcherTimer();
        _updateTimer.Interval = TimeSpan.FromMinutes(_config.CheckIntervalMinutes);
        _updateTimer.Tick += (s, e) => CheckForUpdate();
        _updateTimer.Start();
    }
}

private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
{
    btnCheckUpdate.IsEnabled = false;
    btnCheckUpdate.Content = "检查中...";
    await System.Threading.Tasks.Task.Run(() => CheckForUpdate());
    btnCheckUpdate.IsEnabled = true;
    btnCheckUpdate.Content = "立即检查";
}

private void CheckForUpdate()
{
    try
    {
        string cloudPath = _config.CloudPath;
        if (string.IsNullOrWhiteSpace(cloudPath))
        {
            Dispatcher.InvokeAsync(() =>
            {
                Log("版本检查: 未配置云端路径，跳过检查");
                txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (云端路径未配置)";
            });
            _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveConfig();
            return;
        }

        // 尝试挂载 SMB
        string driveLetter = "Y:";
        if (!System.IO.Directory.Exists(driveLetter + "\\"))
        {
            string userPass = "";
            if (!string.IsNullOrWhiteSpace(_config.CloudUser))
            {
                userPass = $" /user:{_config.CloudUser}";
                if (!string.IsNullOrWhiteSpace(_config.CloudPassword))
                {
                    userPass += $" {_config.CloudPassword}";
                }
            }

            var psi = new System.Diagnostics.ProcessStartInfo("net", $"use {driveLetter} {cloudPath}{userPass}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var proc = System.Diagnostics.Process.Start(psi);
            proc.WaitForExit(10000);
        }

        // 读取 version.json
        string jsonPath = System.IO.Path.Combine(driveLetter + "\\", "version.json");
        if (!System.IO.File.Exists(jsonPath))
        {
            Dispatcher.InvokeAsync(() =>
            {
                Log("版本检查: 云端 version.json 不存在");
                txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (云端不可达)";
            });
            _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            SaveConfig();
            return;
        }

        string jsonText = System.IO.File.ReadAllText(jsonPath);
        var jss = new System.Web.Script.Serialization.JavaScriptSerializer();
        var data = jss.Deserialize<dynamic>(jsonText);
        string latestVersion = data["latest"]?.ToString() ?? "";

        var versions = data["versions"] as Dictionary<string, object>;
        string cloudDate = "";
        string cloudNote = "";
        if (versions != null && versions.ContainsKey(latestVersion))
        {
            var verInfo = versions[latestVersion] as Dictionary<string, object>;
            if (verInfo != null)
            {
                cloudDate = verInfo.ContainsKey("date") ? verInfo["date"]?.ToString() ?? "" : "";
                cloudNote = verInfo.ContainsKey("note") ? verInfo["note"]?.ToString() ?? "" : "";
            }
        }

        _latestCloudVersion = latestVersion;
        _latestCloudDate = cloudDate;
        _latestCloudNote = cloudNote;

        var localVer = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        string localVerStr = $"{localVer.Major}.{localVer.Minor}.{localVer.Build}";

        _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        SaveConfig();

        Dispatcher.InvokeAsync(() =>
        {
            txtCloudVersion.Text = latestVersion;
            txtLastCheckTime.Text = $"上次检查时间: {_config.LastCheckTime} | 检查间隔: {_config.CheckIntervalMinutes} 分钟";

            if (!string.IsNullOrWhiteSpace(latestVersion) && IsNewerVersion(latestVersion, localVerStr))
            {
                // 发现新版本
                txtNewVersionInfo.Text = $"发现新版本 {latestVersion}{(string.IsNullOrWhiteSpace(cloudDate) ? "" : $" ({cloudDate})")}\n{cloudNote}";
                borderNewVersion.Visibility = System.Windows.Visibility.Visible;
                Log($"发现新版本: {latestVersion}");

                // 弹出更新提示窗口
                ShowUpdateDialog(latestVersion, cloudDate, cloudNote);
            }
            else
            {
                borderNewVersion.Visibility = System.Windows.Visibility.Collapsed;
                Log($"版本检查: 已是最新版本 (本地 {localVerStr}, 云端 {latestVersion})");
            }
        });
    }
    catch (Exception ex)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Log($"版本检查失败: {ex.Message}");
            txtLastCheckTime.Text = $"上次检查时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss} (检查失败)";
        });
        _config.LastCheckTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        SaveConfig();
    }
}

private bool IsNewerVersion(string cloudVer, string localVer)
{
    try
    {
        var cv = new Version(cloudVer);
        var lv = new Version(localVer);
        return cv > lv;
    }
    catch
    {
        return string.Compare(cloudVer, localVer, StringComparison.OrdinalIgnoreCase) > 0;
    }
}

private void ShowUpdateDialog(string version, string date, string note)
{
    string msg = $"发现新版本 {version}";
    if (!string.IsNullOrWhiteSpace(date))
        msg += $"\n更新日期: {date}";
    if (!string.IsNullOrWhiteSpace(note))
        msg += $"\n\n{note}";
    msg += "\n\n是否立即更新？（将下载更新并重启程序）";

    var result = System.Windows.MessageBox.Show(msg, "发现新版本",
        System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);

    if (result == System.Windows.MessageBoxResult.Yes)
    {
        StartUpdateDownload(version);
    }
}

private async void StartUpdateDownload(string version)
{
    Log($"开始下载更新 {version}...");
    btnCheckUpdate.IsEnabled = false;

    await System.Threading.Tasks.Task.Run(() =>
    {
        try
        {
            string baseDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            // 向上两级到 D:\App\ (从 versions\X.X.X\ 到根目录)
            string appRoot = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(baseDir, "..", ".."));
            string tempDir = System.IO.Path.Combine(appRoot, "versions", ".temp");

            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, true);
            System.IO.Directory.CreateDirectory(tempDir);

            // 从 SMB 复制文件
            string cloudVerPath = System.IO.Path.Combine("Y:", version);
            CopyDirectory(cloudVerPath, tempDir);

            // 检查 exe 存在
            if (!System.IO.File.Exists(System.IO.Path.Combine(tempDir, "MoveImageForm.exe")))
            {
                Dispatcher.InvokeAsync(() =>
                    Log("更新下载失败: 云端版本文件不完整"));
                return;
            }

            // 生成 update.bat
            string batPath = System.IO.Path.Combine(appRoot, "update.bat");
            string batContent = GenerateUpdateBat(appRoot, version);
            System.IO.File.WriteAllText(batPath, batContent, System.Text.Encoding.UTF8);

            Dispatcher.InvokeAsync(() =>
            {
                Log($"更新已下载到 versions\\.temp\\，即将退出并执行更新...");
                // 延迟 1 秒后退出，让 bat 启动
                var timer = new System.Windows.Threading.DispatcherTimer();
                timer.Interval = TimeSpan.FromSeconds(1);
                timer.Tick += (s, args) =>
                {
                    timer.Stop();
                    // 启动 bat
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(batPath)
                    {
                        UseShellExecute = true,
                        CreateNoWindow = false,
                        WorkingDirectory = appRoot
                    });
                    // 退出程序
                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    System.Windows.Application.Current.Shutdown();
                };
                timer.Start();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.InvokeAsync(() =>
                Log($"更新下载失败: {ex.Message}"));
        }
    });
}

private string GenerateUpdateBat(string appRoot, string version)
{
    string versionsDir = System.IO.Path.Combine(appRoot, "versions");
    string newVerDir = System.IO.Path.Combine(versionsDir, version);
    string tempDir = System.IO.Path.Combine(versionsDir, ".temp");
    string backupDir = System.IO.Path.Combine(appRoot, "backup");
    string launcherPath = System.IO.Path.Combine(appRoot, "Launcher.exe");

    return $@"@echo off
chcp 65001 >nul
echo 正在更新 poco运维工具 到版本 {version}...

:: 等待主程序退出
timeout /t 2 /nobreak >nul

:: 杀进程
taskkill /f /im MoveImageForm.exe >nul 2>&1

:: 备份旧版本
if exist ""{newVerDir}"" (
    if not exist ""{backupDir}"" mkdir ""{backupDir}""
    robocopy ""{newVerDir}"" ""{backupDir}\{version}.bak"" /E /MOVE >nul 2>&1
)

:: 移动新版本
robocopy ""{tempDir}"" ""{newVerDir}"" /E /MOVE >nul 2>&1

:: 清理临时目录
rd /s /q ""{tempDir}"" 2>nul

:: 启动 Launcher
if exist ""{launcherPath}"" (
    start """" ""{launcherPath}""
) else (
    start """" ""{System.IO.Path.Combine(newVerDir, "MoveImageForm.exe")}""
)

:: 自删 bat
del ""%~f0"" & exit
";
}

private void CopyDirectory(string sourceDir, string destDir)
{
    if (!System.IO.Directory.Exists(destDir))
        System.IO.Directory.CreateDirectory(destDir);

    foreach (var file in System.IO.Directory.GetFiles(sourceDir))
    {
        string destFile = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file));
        System.IO.File.Copy(file, destFile, true);
    }

    foreach (var dir in System.IO.Directory.GetDirectories(sourceDir))
    {
        string destSubDir = System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir));
        CopyDirectory(dir, destSubDir);
    }
}

private void ChkAutoCheck_Changed(object sender, RoutedEventArgs e)
{
    if (chkAutoCheck.IsChecked == true)
    {
        _config.CheckIntervalMinutes = 30;
        if (_updateTimer == null)
        {
            _updateTimer = new System.Windows.Threading.DispatcherTimer();
            _updateTimer.Interval = TimeSpan.FromMinutes(_config.CheckIntervalMinutes);
            _updateTimer.Tick += (s, args) => CheckForUpdate();
        }
        _updateTimer.Start();
        chkAutoCheck.Content = $"启用定时检查（每{_config.CheckIntervalMinutes}分钟）";
        Log("已启用定时版本检查（每30分钟）");
    }
    else
    {
        _config.CheckIntervalMinutes = 0;
        _updateTimer?.Stop();
        chkAutoCheck.Content = "启用定时检查（每30分钟）";
        Log("已关闭定时版本检查");
    }
    SaveConfig();
}

private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
{
    _config.AutoStart = chkAutoStart.IsChecked == true;
    SetAutoStart(_config.AutoStart);
    SaveConfig();
}

private void SetAutoStart(bool enable)
{
    try
    {
        string appName = "poco运维工具";
        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        // 如果是通过 Launcher 启动的，使用 Launcher.exe 路径
        string launcherPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(exePath), "..", "..", "Launcher.exe");
        string autoStartPath = System.IO.Path.GetFullPath(launcherPath);

        var regKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", true);

        if (enable)
        {
            regKey?.SetValue(appName, $"\"{autoStartPath}\"");
            Log("已设置开机自启动");
        }
        else
        {
            regKey?.DeleteValue(appName, false);
            Log("已取消开机自启动");
        }
    }
    catch (Exception ex)
    {
        Log($"设置开机自启动失败: {ex.Message}");
    }
}
```

- [ ] **Step 4: 构建验证**

Run: `msbuild "d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageForm.sln" /p:Configuration=Debug`
Expected: 编译成功

- [ ] **Step 5: 提交**

```bash
git add MoveImageForm/MainWindow.xaml.cs MoveImageForm/MoveImageForm.csproj
git commit -m "feat: implement version update tab with SMB check, download, and update flow"
```

---

### Task 5: 新建 Launcher 启动器项目

**Files:**
- Create: `Launcher/App.xaml`
- Create: `Launcher/App.xaml.cs`
- Create: `Launcher/MainWindow.xaml`
- Create: `Launcher/MainWindow.xaml.cs`
- Create: `Launcher/Launcher.csproj`
- Create: `Launcher/Properties/AssemblyInfo.cs`

- [ ] **Step 1: 创建 Launcher.csproj**

```xml
<?xml version="1.0" encoding="utf-8"?>
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <Import Project="$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props" Condition="Exists('$(MSBuildExtensionsPath)\$(MSBuildToolsVersion)\Microsoft.Common.props')" />
  <PropertyGroup>
    <Configuration Condition=" '$(Configuration)' == '' ">Debug</Configuration>
    <Platform Condition=" '$(Platform)' == '' ">AnyCPU</Platform>
    <ProjectGuid>{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}</ProjectGuid>
    <OutputType>WinExe</OutputType>
    <RootNamespace>Launcher</RootNamespace>
    <AssemblyName>Launcher</AssemblyName>
    <TargetFrameworkVersion>v4.7.2</TargetFrameworkVersion>
    <FileAlignment>512</FileAlignment>
    <ProjectTypeGuids>{60dc8134-eba5-43b8-bcc9-bb4bc16c2548};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>
    <WarningLevel>4</WarningLevel>
    <AutoGenerateBindingRedirects>true</AutoGenerateBindingRedirects>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' ">
    <PlatformTarget>AnyCPU</PlatformTarget>
    <DebugSymbols>true</DebugSymbols>
    <DebugType>full</DebugType>
    <Optimize>false</Optimize>
    <OutputPath>bin\Debug\</OutputPath>
    <DefineConstants>DEBUG;TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <PropertyGroup Condition=" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' ">
    <PlatformTarget>AnyCPU</PlatformTarget>
    <DebugType>pdbonly</DebugType>
    <Optimize>true</Optimize>
    <OutputPath>bin\Release\</OutputPath>
    <DefineConstants>TRACE</DefineConstants>
    <ErrorReport>prompt</ErrorReport>
    <WarningLevel>4</WarningLevel>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="System" />
    <Reference Include="System.Xml" />
    <Reference Include="System.Core" />
    <Reference Include="System.Xaml">
      <RequiredTargetFramework>4.0</RequiredTargetFramework>
    </Reference>
    <Reference Include="System.Drawing" />
    <Reference Include="System.Windows.Forms" />
    <Reference Include="WindowsBase" />
    <Reference Include="PresentationCore" />
    <Reference Include="PresentationFramework" />
    <Reference Include="System.Web.Extensions" />
  </ItemGroup>
  <ItemGroup>
    <ApplicationDefinition Include="App.xaml">
      <Generator>MSBuild:Compile</Generator>
      <SubType>Designer</SubType>
    </ApplicationDefinition>
    <Page Include="MainWindow.xaml">
      <Generator>MSBuild:Compile</Generator>
      <SubType>Designer</SubType>
    </Page>
    <Compile Include="App.xaml.cs">
      <DependentUpon>App.xaml</DependentUpon>
      <SubType>Code</SubType>
    </Compile>
    <Compile Include="MainWindow.xaml.cs">
      <DependentUpon>MainWindow.xaml</DependentUpon>
      <SubType>Code</SubType>
    </Compile>
  </ItemGroup>
  <ItemGroup>
    <Compile Include="Properties\AssemblyInfo.cs">
      <SubType>Code</SubType>
    </Compile>
  </ItemGroup>
  <ItemGroup>
    <None Include="App.config" />
  </ItemGroup>
  <Import Project="$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
```

- [ ] **Step 2: 创建 Launcher/Properties/AssemblyInfo.cs**

```csharp
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

[assembly: AssemblyTitle("Launcher")]
[assembly: AssemblyDescription("poco运维工具启动器")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("")]
[assembly: AssemblyProduct("Launcher")]
[assembly: AssemblyCopyright("Copyright © 2026")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
```

- [ ] **Step 3: 创建 Launcher/App.xaml**

```xml
<Application x:Class="Launcher.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
    </Application.Resources>
</Application>
```

- [ ] **Step 4: 创建 Launcher/App.xaml.cs**

```csharp
using System.Windows;

namespace Launcher
{
    public partial class App : Application
    {
    }
}
```

- [ ] **Step 5: 创建 Launcher/MainWindow.xaml**

```xml
<Window x:Class="Launcher.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="poco运维工具 - 启动中" Height="350" Width="450"
        WindowStartupLocation="CenterScreen" Loaded="Window_Loaded">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" FontSize="18" FontWeight="Bold" Text="poco运维工具" HorizontalAlignment="Center" Margin="0,0,0,16"/>

        <TextBlock x:Name="txtStatus" Grid.Row="1" FontSize="13" HorizontalAlignment="Center" Text="正在检查更新..." Margin="0,0,0,12"/>

        <ProgressBar x:Name="progressBar" Grid.Row="2" Height="20" IsIndeterminate="False" Minimum="0" Maximum="100" Visibility="Collapsed" Margin="0,0,0,12"/>

        <StackPanel x:Name="panelUpdate" Grid.Row="3" Visibility="Collapsed" HorizontalAlignment="Center">
            <TextBlock x:Name="txtUpdateInfo" FontSize="13" Margin="0,0,0,8"/>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,8,0,0">
                <Button x:Name="btnUpdate" Content="立即更新" Width="90" Height="30" Margin="0,0,12,0" Click="BtnUpdate_Click"/>
                <Button x:Name="btnSkip" Content="稍后提醒" Width="90" Height="30" Click="BtnSkip_Click"/>
            </StackPanel>
        </StackPanel>

        <TextBlock x:Name="txtProgress" Grid.Row="4" FontSize="11" Foreground="#888" HorizontalAlignment="Center"/>
    </Grid>
</Window>
```

- [ ] **Step 6: 创建 Launcher/MainWindow.xaml.cs**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Xml;

namespace Launcher
{
    public partial class MainWindow : Window
    {
        private string _appRoot;
        private string _configPath;
        private string _statusPath;
        private string _cloudPath;
        private string _cloudUser;
        private string _cloudPassword;

        public MainWindow()
        {
            InitializeComponent();
            _appRoot = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _configPath = Path.Combine(_appRoot, "config.xml");
            _statusPath = Path.Combine(_appRoot, "update.status");
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            await Task.Run(() => StartupFlow());
        }

        private void StartupFlow()
        {
            try
            {
                // 1. 读 update.status — 崩溃恢复
                Dispatcher.InvokeAsync(() => txtStatus.Text = "正在检查更新状态...");
                string status = ReadUpdateStatus();

                if (status == "installing")
                {
                    // 上次安装未完成，检查完整性
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在恢复上次更新...");
                    if (!VerifyCurrentVersion())
                    {
                        RecoverFromBackup();
                    }
                    WriteUpdateStatus("idle");
                }
                else if (status == "ready")
                {
                    // 有已下载的更新待安装
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "有待安装的更新，正在安装...");
                    InstallUpdate();
                    return;
                }
                else if (status == "downloading")
                {
                    // 上次下载中断，清理重来
                    string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                    if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                    WriteUpdateStatus("idle");
                }

                // 2. 读 config.xml
                Dispatcher.InvokeAsync(() => txtStatus.Text = "正在读取配置...");
                LoadConfig();

                // 3. 挂载 SMB
                if (!string.IsNullOrWhiteSpace(_cloudPath))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在连接云端...");
                    MountSMB();
                }

                // 4. 检查更新
                if (!string.IsNullOrWhiteSpace(_cloudPath) && Directory.Exists("Y:\\"))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "正在检查版本更新...");
                    CheckForUpdate();
                }
                else
                {
                    // 无法连接云端，直接启动主程序
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "无法连接云端，直接启动...");
                    StartMainApp();
                }
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() =>
                    txtStatus.Text = $"启动失败: {ex.Message}\n尝试直接启动主程序...");
                StartMainApp();
            }
        }

        private string ReadUpdateStatus()
        {
            try
            {
                if (File.Exists(_statusPath))
                    return File.ReadAllText(_statusPath).Trim();
            }
            catch { }
            return "idle";
        }

        private void WriteUpdateStatus(string status)
        {
            try
            {
                File.WriteAllText(_statusPath, status);
            }
            catch { }
        }

        private void LoadConfig()
        {
            if (!File.Exists(_configPath)) return;

            var doc = new XmlDocument();
            doc.Load(_configPath);

            _cloudPath = doc.SelectSingleNode("//CloudPath")?.InnerText ?? "";
            _cloudUser = doc.SelectSingleNode("//CloudUser")?.InnerText ?? "";
            _cloudPassword = doc.SelectSingleNode("//CloudPassword")?.InnerText ?? "";
        }

        private void MountSMB()
        {
            try
            {
                string letter = "Y:";
                // 先断开
                var psi1 = new ProcessStartInfo("net", $"use {letter} /delete /y")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var p1 = Process.Start(psi1);
                p1?.WaitForExit(3000);

                // 再连接
                string userPass = "";
                if (!string.IsNullOrWhiteSpace(_cloudUser))
                {
                    userPass = $" /user:{_cloudUser}";
                    if (!string.IsNullOrWhiteSpace(_cloudPassword))
                        userPass += $" {_cloudPassword}";
                }

                var psi2 = new ProcessStartInfo("net", $"use {letter} {_cloudPath}{userPass}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var p2 = Process.Start(psi2);
                p2?.WaitForExit(10000);
            }
            catch { }
        }

        private void CheckForUpdate()
        {
            try
            {
                string jsonPath = Path.Combine("Y:", "version.json");
                if (!File.Exists(jsonPath))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "未找到云端版本信息，直接启动...");
                    StartMainApp();
                    return;
                }

                string json = File.ReadAllText(jsonPath);
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(json);
                string latestVersion = data["latest"]?.ToString() ?? "";

                var versions = data["versions"] as Dictionary<string, object>;
                string cloudDate = "";
                string cloudNote = "";
                if (versions != null && versions.ContainsKey(latestVersion))
                {
                    var verInfo = versions[latestVersion] as Dictionary<string, object>;
                    if (verInfo != null)
                    {
                        cloudDate = verInfo.ContainsKey("date") ? verInfo["date"]?.ToString() ?? "" : "";
                        cloudNote = verInfo.ContainsKey("note") ? verInfo["note"]?.ToString() ?? "" : "";
                    }
                }

                string localVer = GetLocalLatestVersion();
                if (string.IsNullOrWhiteSpace(latestVersion))
                {
                    StartMainApp();
                    return;
                }

                if (IsNewerVersion(latestVersion, localVer))
                {
                    // 发现新版本，提示用户
                    Dispatcher.InvokeAsync(() =>
                    {
                        txtStatus.Text = $"发现新版本 {latestVersion}";
                        txtUpdateInfo.Text = $"版本: {latestVersion}\n日期: {cloudDate}\n\n{cloudNote}\n\n当前本地版本: {localVer}";
                        panelUpdate.Visibility = Visibility.Visible;
                    });
                }
                else
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = $"已是最新版本 ({localVer})，正在启动...");
                    StartMainApp();
                }
            }
            catch
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = "版本检查失败，直接启动...");
                StartMainApp();
            }
        }

        private string GetLocalLatestVersion()
        {
            string versionsDir = Path.Combine(_appRoot, "versions");
            if (!Directory.Exists(versionsDir)) return "0.0.0";

            var dirs = Directory.GetDirectories(versionsDir);
            Version best = new Version(0, 0, 0);
            foreach (var dir in dirs)
            {
                string dirName = Path.GetFileName(dir);
                if (Version.TryParse(dirName, out Version ver) && ver > best)
                    best = ver;
            }
            return $"{best.Major}.{best.Minor}.{best.Build}";
        }

        private bool IsNewerVersion(string cloudVer, string localVer)
        {
            try
            {
                var cv = new Version(cloudVer);
                var lv = new Version(localVer);
                return cv > lv;
            }
            catch
            {
                return string.Compare(cloudVer, localVer, StringComparison.OrdinalIgnoreCase) > 0;
            }
        }

        private async void BtnUpdate_Click(object sender, RoutedEventArgs e)
        {
            panelUpdate.Visibility = Visibility.Collapsed;
            btnUpdate.IsEnabled = false;
            btnSkip.IsEnabled = false;

            await Task.Run(() => DownloadAndInstall());
        }

        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            StartMainApp();
        }

        private void DownloadAndInstall()
        {
            try
            {
                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "正在下载更新...";
                    progressBar.Visibility = Visibility.Visible;
                    progressBar.IsIndeterminate = true;
                });

                WriteUpdateStatus("downloading");

                // 读云端信息
                string json = File.ReadAllText(Path.Combine("Y:", "version.json"));
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(json);
                string latestVersion = data["latest"]?.ToString() ?? "";

                string tempDir = Path.Combine(_appRoot, "versions", ".temp");
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                Directory.CreateDirectory(tempDir);

                Dispatcher.InvokeAsync(() => txtProgress.Text = "正在从云端复制文件...");
                CopyDirectory(Path.Combine("Y:", latestVersion), tempDir);

                // 验证
                if (!File.Exists(Path.Combine(tempDir, "MoveImageForm.exe")))
                {
                    Dispatcher.InvokeAsync(() => txtStatus.Text = "更新下载失败: 版本文件不完整");
                    WriteUpdateStatus("idle");
                    return;
                }

                WriteUpdateStatus("ready");

                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "下载完成，正在安装更新...";
                    progressBar.IsIndeterminate = false;
                    progressBar.Value = 100;
                });

                InstallUpdate();
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = $"更新失败: {ex.Message}");
                WriteUpdateStatus("idle");
            }
        }

        private void InstallUpdate()
        {
            try
            {
                WriteUpdateStatus("installing");

                string json = File.ReadAllText(Path.Combine("Y:", "version.json"));
                var jss = new JavaScriptSerializer();
                var data = jss.Deserialize<dynamic>(json);
                string latestVersion = data["latest"]?.ToString() ?? "";

                string batPath = Path.Combine(_appRoot, "update.bat");
                string batContent = GenerateUpdateBat(latestVersion);
                File.WriteAllText(batPath, batContent, System.Text.Encoding.UTF8);

                Dispatcher.InvokeAsync(() =>
                {
                    txtStatus.Text = "即将重启完成更新...";
                });

                // 启动 bat 并退出
                Task.Delay(500).ContinueWith(_ =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        Process.Start(new ProcessStartInfo(batPath)
                        {
                            UseShellExecute = true,
                            CreateNoWindow = false,
                            WorkingDirectory = _appRoot
                        });
                        Application.Current.Shutdown();
                    });
                });
            }
            catch (Exception ex)
            {
                Dispatcher.InvokeAsync(() => txtStatus.Text = $"安装更新失败: {ex.Message}");
                WriteUpdateStatus("idle");
            }
        }

        private string GenerateUpdateBat(string version)
        {
            string versionsDir = Path.Combine(_appRoot, "versions");
            string newVerDir = Path.Combine(versionsDir, version);
            string tempDir = Path.Combine(versionsDir, ".temp");
            string backupDir = Path.Combine(_appRoot, "backup");
            string launcherPath = Path.Combine(_appRoot, "Launcher.exe");

            return $@"@echo off
chcp 65001 >nul
echo 正在更新 poco运维工具 到版本 {version}...

:: 等待 Launcher 退出
timeout /t 2 /nobreak >nul

:: 杀进程
taskkill /f /im MoveImageForm.exe >nul 2>&1

:: 备份旧版本
if exist ""{newVerDir}"" (
    if not exist ""{backupDir}"" mkdir ""{backupDir}""
    robocopy ""{newVerDir}"" ""{backupDir}\{version}.bak"" /E /MOVE >nul 2>&1
)

:: 移动新版本
robocopy ""{tempDir}"" ""{newVerDir}"" /E /MOVE >nul 2>&1

:: 清理临时目录
rd /s /q ""{tempDir}"" 2>nul

:: 更新状态
echo idle> ""{Path.Combine(_appRoot, "update.status")}""

:: 启动 Launcher
if exist ""{launcherPath}"" (
    start """" ""{launcherPath}""
) else (
    start """" ""{Path.Combine(newVerDir, "MoveImageForm.exe")}""
)

:: 自删 bat
del ""%~f0"" & exit
";
        }

        private void CopyDirectory(string sourceDir, string destDir)
        {
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, true);
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                string destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, destSubDir);
            }
        }

        private bool VerifyCurrentVersion()
        {
            string localVer = GetLocalLatestVersion();
            string versionsDir = Path.Combine(_appRoot, "versions");
            string verDir = Path.Combine(versionsDir, localVer);
            return File.Exists(Path.Combine(verDir, "MoveImageForm.exe"));
        }

        private void RecoverFromBackup()
        {
            string backupDir = Path.Combine(_appRoot, "backup");
            if (!Directory.Exists(backupDir)) return;

            var dirs = Directory.GetDirectories(backupDir);
            Version best = new Version(0, 0, 0);
            string bestDir = "";
            foreach (var dir in dirs)
            {
                string dirName = Path.GetFileName(dir);
                // dirName is like "1.0.0.bak"
                string verStr = dirName.Replace(".bak", "");
                if (Version.TryParse(verStr, out Version ver) && ver > best)
                {
                    best = ver;
                    bestDir = dir;
                }
            }

            if (!string.IsNullOrWhiteSpace(bestDir))
            {
                string versionsDir = Path.Combine(_appRoot, "versions");
                string restoreDir = Path.Combine(versionsDir, $"{best.Major}.{best.Minor}.{best.Build}");
                if (Directory.Exists(restoreDir)) Directory.Delete(restoreDir, true);
                CopyDirectory(bestDir, restoreDir);

                Dispatcher.InvokeAsync(() =>
                    txtStatus.Text = "已从备份恢复，正在启动...");
            }
        }

        private void StartMainApp()
        {
            string localVer = GetLocalLatestVersion();
            string versionsDir = Path.Combine(_appRoot, "versions");
            string exePath = Path.Combine(versionsDir, localVer, "MoveImageForm.exe");

            if (!File.Exists(exePath))
            {
                // 可能是开发环境，尝试从当前目录启动
                exePath = Path.Combine(_appRoot, "MoveImageForm.exe");
                if (!File.Exists(exePath))
                {
                    Dispatcher.InvokeAsync(() =>
                        txtStatus.Text = $"错误: 找不到主程序文件。\n搜索路径: {exePath}");
                    return;
                }
            }

            Dispatcher.InvokeAsync(() =>
                txtStatus.Text = "正在启动主程序...");

            Process.Start(new ProcessStartInfo(exePath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            });

            // Launcher 退出
            Task.Delay(500).ContinueWith(_ =>
            {
                Dispatcher.InvokeAsync(() => Application.Current.Shutdown());
            });
        }
    }
}
```

- [ ] **Step 7: 创建 Launcher/App.config**

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
    <startup>
        <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.7.2"/>
    </startup>
</configuration>
```

- [ ] **Step 8: 构建 Launcher 项目验证**

Run: `msbuild "d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\Launcher\Launcher.csproj" /p:Configuration=Debug`
Expected: 编译成功

- [ ] **Step 9: 提交**

```bash
git add Launcher/
git commit -m "feat: create Launcher project with SMB update check, download, and bat-based install flow"
```

---

### Task 6: 更新解决方案文件 + 最终集成

**Files:**
- Modify: `MoveImageForm.sln` — 添加 Launcher 项目引用
- Modify: `MoveImageForm/Properties/AssemblyInfo.cs` — 更新版本号和标题信息

- [ ] **Step 1: 更新 MoveImageForm.sln 添加 Launcher 项目**

在 `MoveImageForm.sln` 中的 EndProject 之后，Global 之前插入：

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Launcher", "Launcher\Launcher.csproj", "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"
EndProject
```

在 GlobalSection(ProjectConfigurationPlatforms) 中添加：

```
{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Debug|Any CPU.Build.0 = Debug|Any CPU
{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Release|Any CPU.ActiveCfg = Release|Any CPU
{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}.Release|Any CPU.Build.0 = Release|Any CPU
```

- [ ] **Step 2: 更新 AssemblyInfo.cs 程序集信息**

将 MoveImageForm 的 AssemblyInfo.cs 中：
- `[assembly: AssemblyTitle("MoveImageForm")]` → `[assembly: AssemblyTitle("poco运维工具")]`
- `[assembly: AssemblyProduct("MoveImageForm")]` → `[assembly: AssemblyProduct("poco运维工具")]`

- [ ] **Step 3: 完整解决方案构建验证**

Run: `msbuild "d:\1111wyj\QZ\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageFormTool-master-bfd4a3b393880fdd9922bbf2a431b3c857931bd9\MoveImageForm.sln" /p:Configuration=Debug`
Expected: 两个项目均编译成功，无错误

- [ ] **Step 4: 提交**

```bash
git add MoveImageForm.sln MoveImageForm/Properties/AssemblyInfo.cs
git commit -m "feat: add Launcher to solution, update assembly info to poco运维工具"
```

---

## 验证清单

完成所有任务后，手动验证以下功能：

1. **编译**: `msbuild MoveImageForm.sln /p:Configuration=Debug` 两个项目均成功
2. **文件搬运 Tab**: 界面布局与其他 Tab 一致，搬运功能正常
3. **进程监听 Tab**: 
   - 添加/删除进程
   - 监控开关
   - 进程挂掉后托盘气泡提醒
4. **版本更新 Tab**: 
   - 显示本地版本号
   - 「立即检查」按钮响应
   - 定时检查开关
   - 开机自启动开关
5. **Launcher**: 
   - 启动时检查更新
   - 发现新版本弹窗
   - 下载进度显示
   - update.bat 生成
6. **配置文件**: config.xml 正确保存和加载所有新增字段
