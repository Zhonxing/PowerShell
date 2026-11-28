// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace Microsoft.Management.UI.Internal.CommandPanel
{
    internal sealed class CommandPanelWindow : Window
    {
        private static readonly SolidColorBrush BgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1F)));
        private static readonly SolidColorBrush HeaderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27)));
        private static readonly SolidColorBrush CardBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x2D)));
        private static readonly SolidColorBrush CardHoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x32, 0x32, 0x39)));
        private static readonly SolidColorBrush InputBgBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x19)));
        private static readonly SolidColorBrush BorderDarkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x40)));
        private static readonly SolidColorBrush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)));
        private static readonly SolidColorBrush AccentHoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x8A, 0xE2)));
        private static readonly SolidColorBrush RunGreenBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43)));
        private static readonly SolidColorBrush RunGreenHoverBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0xB8, 0x52)));
        private static readonly SolidColorBrush DangerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0x3C, 0x37)));
        private static readonly SolidColorBrush TextPrimaryBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)));
        private static readonly SolidColorBrush TextMutedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA4)));
        private static readonly FontFamily UiFont = new FontFamily("Microsoft YaHei UI, Segoe UI");
        private static readonly FontFamily MonoFont = new FontFamily("Cascadia Mono, Consolas, Courier New");

        private readonly CommandConfig _config;
        private readonly DispatcherTimer _syncTimer;
        private readonly DispatcherTimer _statusTimer;

        private nint _targetHwnd;
        private nint _panelHwnd;
        private bool _isDocked = true;
        private bool _closedByUser;
        private bool _everAttachedToTerminal;
        private int _missingTerminalTicks;
        private string _selectedCategory = "全部";
        private CommandItem? _editingItem;
        private bool _isInjecting;

        private readonly TextBox _searchBox;
        private readonly WrapPanel _categoryPanel;
        private readonly StackPanel _listPanel;
        private readonly Border _editorDrawer;
        private readonly TextBlock _editorTitle;
        private readonly TextBox _nameInput;
        private readonly TextBox _categoryInput;
        private readonly WrapPanel _editorCategoryChips;
        private readonly TextBox _commandInput;
        private readonly Button _dockButton;
        private readonly TextBlock _targetLabel;
        private readonly TextBlock _statusLabel;

        internal CommandPanelWindow(nint initialTargetHwnd)
        {
            _config = CommandStore.LoadOrCreate();
            _targetHwnd = Win32TerminalBridge.FindTerminalWindow(initialTargetHwnd);
            ClearDismissMarker(_targetHwnd);

            Title = "SuperPowerShell 指令面板";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;
            ResizeMode = ResizeMode.CanResize;
            ShowInTaskbar = false;
            Background = BgBrush;
            Foreground = TextPrimaryBrush;
            FontFamily = UiFont;
            Width = _config.PanelWidth;
            Height = 640;
            MinWidth = 250;
            MinHeight = 360;

            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(10),
                GlassFrameThickness = new Thickness(0),
                ResizeBorderThickness = new Thickness(5),
                UseAeroCaptionButtons = false,
            });

            var rootBorder = new Border
            {
                BorderBrush = BorderDarkBrush,
                BorderThickness = new Thickness(0),
                Background = BgBrush,
                ClipToBounds = true,
            };

            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search & categories
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Inline add/edit drawer
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // List
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Footer

            // 1. Header bar
            var headerBorder = new Border
            {
                Background = HeaderBrush,
                BorderBrush = BorderDarkBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 10, 10, 10),
            };
            headerBorder.MouseLeftButtonDown += HeaderBorder_MouseLeftButtonDown;

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { Orientation = Orientation.Vertical };
            titleStack.Children.Add(new TextBlock
            {
                Text = "指令面板",
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush,
            });
            _targetLabel = new TextBlock
            {
                Text = "未检测到终端",
                FontSize = 10.5,
                Foreground = TextMutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            };
            titleStack.Children.Add(_targetLabel);
            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var headerButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var addButton = CreateRoundedButton("+ 新增", AccentBrush, AccentHoverBrush, "添加自定义指令", cornerRadius: 12, padding: new Thickness(10, 4, 10, 4));
            addButton.Click += (_, _) => OpenEditor(null);
            headerButtons.Children.Add(addButton);

            _dockButton = CreateRoundedButton("📌", CardBrush, CardHoverBrush, "切换吸附/浮动模式", cornerRadius: 8, padding: new Thickness(7, 4, 7, 4));
            _dockButton.Margin = new Thickness(5, 0, 0, 0);
            _dockButton.Click += (_, _) => ToggleDockMode();
            headerButtons.Children.Add(_dockButton);

            var closeButton = CreateRoundedButton("✕", CardBrush, DangerBrush, "关闭面板（终端输入 cmd-panel 可重新打开）", cornerRadius: 8, padding: new Thickness(8, 4, 8, 4));
            closeButton.Margin = new Thickness(5, 0, 0, 0);
            closeButton.Click += (_, _) => CloseByUser();
            headerButtons.Children.Add(closeButton);

            Grid.SetColumn(headerButtons, 1);
            headerGrid.Children.Add(headerButtons);
            headerBorder.Child = headerGrid;
            Grid.SetRow(headerBorder, 0);
            rootGrid.Children.Add(headerBorder);

            // 2. Search + Categories
            var filterPanel = new StackPanel { Margin = new Thickness(10, 10, 10, 4) };
            var (searchContainer, searchBox) = CreateRoundedTextBox("搜索指令名称或命令...");
            _searchBox = searchBox;
            _searchBox.TextChanged += (_, _) => RefreshCommandList();
            filterPanel.Children.Add(searchContainer);

            _categoryPanel = new WrapPanel { Margin = new Thickness(0, 8, 0, 2) };
            filterPanel.Children.Add(_categoryPanel);
            Grid.SetRow(filterPanel, 1);
            rootGrid.Children.Add(filterPanel);

            // 3. Inline Add/Edit Drawer
            _editorDrawer = new Border
            {
                Background = HeaderBrush,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(10, 4, 10, 8),
                Padding = new Thickness(10),
                Visibility = Visibility.Collapsed,
            };

            var editorStack = new StackPanel();
            _editorTitle = new TextBlock
            {
                Text = "新增指令",
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush,
                Margin = new Thickness(0, 0, 0, 8),
            };
            editorStack.Children.Add(_editorTitle);

            editorStack.Children.Add(CreateFieldLabel("名称"));
            var (nameContainer, nameInput) = CreateRoundedTextBox("例如：查看设备 IP");
            _nameInput = nameInput;
            nameContainer.Margin = new Thickness(0, 3, 0, 8);
            editorStack.Children.Add(nameContainer);

            editorStack.Children.Add(CreateFieldLabel("分组（直接输入或点选下方标签）"));
            var (catContainer, catInput) = CreateRoundedTextBox("例如：ADB 常用 / 自定义");
            _categoryInput = catInput;
            catContainer.Margin = new Thickness(0, 3, 0, 5);
            editorStack.Children.Add(catContainer);

            _editorCategoryChips = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
            editorStack.Children.Add(_editorCategoryChips);

            editorStack.Children.Add(CreateFieldLabel("指令内容（支持多条，每行一条指令）"));
            var (cmdContainer, cmdInput) = CreateRoundedTextBox("每行一条指令，例如：\nadb root\nadb remount", isMono: true, multiline: true);
            _commandInput = cmdInput;
            _commandInput.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    e.Handled = true;
                    SaveEditor();
                }
            };
            cmdContainer.Margin = new Thickness(0, 3, 0, 10);
            editorStack.Children.Add(cmdContainer);

            var editorBtns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancelBtn = CreateRoundedButton("取消", CardBrush, CardHoverBrush, "取消编辑", cornerRadius: 8, padding: new Thickness(12, 5, 12, 5));
            cancelBtn.Click += (_, _) => _editorDrawer.Visibility = Visibility.Collapsed;
            editorBtns.Children.Add(cancelBtn);

            var saveBtn = CreateRoundedButton("保存", AccentBrush, AccentHoverBrush, "保存指令", cornerRadius: 8, padding: new Thickness(14, 5, 14, 5));
            saveBtn.Margin = new Thickness(6, 0, 0, 0);
            saveBtn.Click += (_, _) => SaveEditor();
            editorBtns.Children.Add(saveBtn);
            editorStack.Children.Add(editorBtns);

            _editorDrawer.Child = editorStack;
            Grid.SetRow(_editorDrawer, 2);
            rootGrid.Children.Add(_editorDrawer);

            // 4. Command List
            _listPanel = new StackPanel { Margin = new Thickness(10, 4, 10, 8) };
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _listPanel,
            };
            Grid.SetRow(scroller, 3);
            rootGrid.Children.Add(scroller);

            // 5. Footer
            var footerBorder = new Border
            {
                Background = HeaderBrush,
                BorderBrush = BorderDarkBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                CornerRadius = new CornerRadius(0, 0, 10, 10),
                Padding = new Thickness(10, 6, 10, 6),
            };
            var footerGrid = new Grid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusLabel = new TextBlock
            {
                Text = "单击填入 · ▶ 直接执行",
                FontSize = 11,
                Foreground = TextMutedBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(_statusLabel, 0);
            footerGrid.Children.Add(_statusLabel);

            var openCfgBtn = CreateRoundedButton("配置", CardBrush, CardHoverBrush, "打开 commands.json 配置文件", cornerRadius: 8, padding: new Thickness(9, 3, 9, 3));
            openCfgBtn.FontSize = 11;
            openCfgBtn.Click += (_, _) => OpenConfigFile();
            Grid.SetColumn(openCfgBtn, 1);
            footerGrid.Children.Add(openCfgBtn);

            footerBorder.Child = footerGrid;
            Grid.SetRow(footerBorder, 4);
            rootGrid.Children.Add(footerBorder);

            rootBorder.Child = rootGrid;
            Content = rootBorder;

            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                _statusLabel.Text = "单击填入 · ▶ 直接执行";
                _statusLabel.Foreground = TextMutedBrush;
            };

            _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _syncTimer.Tick += (_, _) => SyncWithTerminal();

            SourceInitialized += (_, _) =>
            {
                _panelHwnd = new WindowInteropHelper(this).Handle;
                Win32TerminalBridge.ApplyRoundedWindowCorners(_panelHwnd);
                SyncWithTerminal(forcePosition: true);
                _syncTimer.Start();
            };

            SizeChanged += (_, _) =>
            {
                if (IsLoaded && ActualWidth >= MinWidth && ActualWidth <= 600)
                {
                    _config.PanelWidth = Math.Round(ActualWidth);
                }
            };

            Closed += (_, _) =>
            {
                _syncTimer.Stop();
                _statusTimer.Stop();
                try
                {
                    CommandStore.Save(_config);
                }
                catch
                {
                    // Best-effort save on exit.
                }
            };

            RefreshCategories();
            RefreshCommandList();
        }

        internal void BringToFrontAndRedock()
        {
            Dispatcher.Invoke(() =>
            {
                nint fg = Win32TerminalBridge.GetCurrentForegroundWindow();
                if (fg != IntPtr.Zero && Win32TerminalBridge.IsTerminalWindow(fg))
                {
                    _targetHwnd = fg;
                }
                else
                {
                    _targetHwnd = Win32TerminalBridge.FindTerminalWindow(_targetHwnd);
                }

                ClearDismissMarker(_targetHwnd);
                Visibility = Visibility.Visible;
                WindowState = WindowState.Normal;
                SyncWithTerminal(forcePosition: true);
                Activate();
            });
        }

        internal void CloseSilently()
        {
            Dispatcher.Invoke(() =>
            {
                _closedByUser = false;
                Close();
            });
        }

        private void CloseByUser()
        {
            _closedByUser = true;
            WriteDismissMarker(_targetHwnd);
            Close();
        }

        private void HeaderBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                if (_isDocked)
                {
                    _isDocked = false;
                    UpdateDockButtonVisual();
                }

                try
                {
                    DragMove();
                }
                catch
                {
                    // Ignore drag interruption.
                }
            }
        }

        private void ToggleDockMode()
        {
            _isDocked = !_isDocked;
            UpdateDockButtonVisual();
            if (_isDocked)
            {
                SyncWithTerminal(forcePosition: true);
                ShowStatus("已吸附到终端旁", AccentHoverBrush);
            }
            else
            {
                ShowStatus("已切换为自由浮动", TextMutedBrush);
            }
        }

        private void UpdateDockButtonVisual()
        {
            _dockButton.Content = _isDocked ? "📌" : "🔓";
            _dockButton.ToolTip = _isDocked ? "当前：吸附终端旁（点击切换自由浮动）" : "当前：自由浮动（点击重新吸附终端）";
        }

        private void SyncWithTerminal(bool forcePosition = false)
        {
            nint fg = Win32TerminalBridge.GetCurrentForegroundWindow();
            if (fg != IntPtr.Zero && fg != _panelHwnd && Win32TerminalBridge.IsTerminalWindow(fg))
            {
                _targetHwnd = fg;
            }

            if (!Win32TerminalBridge.IsValidWindow(_targetHwnd))
            {
                _targetHwnd = Win32TerminalBridge.FindTerminalWindow(IntPtr.Zero);
                if (_targetHwnd == IntPtr.Zero)
                {
                    _missingTerminalTicks++;
                    int threshold = _everAttachedToTerminal ? 8 : 25;
                    if (!_closedByUser && _missingTerminalTicks >= threshold)
                    {
                        Close();
                    }

                    return;
                }
            }

            _everAttachedToTerminal = true;
            _missingTerminalTicks = 0;

            string title = Win32TerminalBridge.GetWindowTitleText(_targetHwnd);
            if (!string.IsNullOrWhiteSpace(title))
            {
                _targetLabel.Text = $"目标：{title}";
            }

            if (Win32TerminalBridge.IsTargetHiddenOrCloaked(_targetHwnd))
            {
                if (Visibility == Visibility.Visible)
                {
                    Visibility = Visibility.Hidden;
                }

                return;
            }

            if (Visibility != Visibility.Visible)
            {
                Visibility = Visibility.Visible;
            }

            if (_isDocked && Win32TerminalBridge.TryGetWindowVisualRect(_targetHwnd, out NativeRect targetRect))
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
                double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

                double targetLeft = targetRect.Left / scaleX;
                double targetTop = targetRect.Top / scaleY;
                double targetRight = targetRect.Right / scaleX;
                double targetHeight = Math.Max(MinHeight, targetRect.Height / scaleY);
                double panelWidth = ActualWidth > 0 ? ActualWidth : Width;

                double desiredLeft = targetRight;
                double desiredTop = targetTop;
                double desiredHeight = targetHeight;
                if (Win32TerminalBridge.TryGetMonitorWorkArea(_targetHwnd, out NativeRect workArea))
                {
                    double workRight = workArea.Right / scaleX;
                    double workLeft = workArea.Left / scaleX;
                    double workBottom = workArea.Bottom / scaleY;

                    if (targetRight + panelWidth <= workRight + 2)
                    {
                        desiredLeft = targetRight;
                    }
                    else if (targetLeft - panelWidth >= workLeft - 2)
                    {
                        desiredLeft = targetLeft - panelWidth;
                    }
                    else
                    {
                        // Inner right dock (maximized / fullscreen): drop below WT's title/tab bar (~44 DIPs)
                        // so minimize/maximize/close buttons remain unobstructed.
                        const double titleBarOffset = 44;
                        desiredLeft = Math.Max(workLeft, targetRight - panelWidth);
                        desiredTop = targetTop + titleBarOffset;
                        desiredHeight = Math.Max(MinHeight, targetHeight - titleBarOffset);
                    }

                    if (desiredTop + desiredHeight > workBottom)
                    {
                        desiredHeight = Math.Max(MinHeight, workBottom - desiredTop);
                    }
                }

                bool positionChanged =
                    forcePosition ||
                    Math.Abs(Left - desiredLeft) > 1.5 ||
                    Math.Abs(Top - desiredTop) > 1.5 ||
                    Math.Abs(Height - desiredHeight) > 1.5;

                if (positionChanged)
                {
                    Left = desiredLeft;
                    Top = desiredTop;
                    Height = desiredHeight;
                }
            }

            bool isRelatedActive = fg == _targetHwnd || fg == _panelHwnd;
            Win32TerminalBridge.SyncPanelZOrder(_panelHwnd, _targetHwnd, fg, isRelatedActive);
        }

        private void RefreshCategories()
        {
            _categoryPanel.Children.Clear();
            var categories = new List<string> { "全部" };
            foreach (string cat in _config.Commands
                .Select(c => string.IsNullOrWhiteSpace(c.Category) ? "自定义" : c.Category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                categories.Add(cat);
            }

            if (!categories.Contains(_selectedCategory, StringComparer.OrdinalIgnoreCase))
            {
                _selectedCategory = "全部";
            }

            foreach (string cat in categories)
            {
                bool active = string.Equals(cat, _selectedCategory, StringComparison.OrdinalIgnoreCase);
                SolidColorBrush normalBg = active ? AccentBrush : CardBrush;
                SolidColorBrush hoverBg = active ? AccentHoverBrush : CardHoverBrush;
                var chip = CreateRoundedButton(cat, normalBg, hoverBg, $"筛选分组：{cat}", cornerRadius: 11, padding: new Thickness(10, 3, 10, 3));
                chip.FontSize = 11;
                chip.Margin = new Thickness(0, 0, 5, 5);
                chip.Click += (_, _) =>
                {
                    _selectedCategory = cat;
                    RefreshCategories();
                    RefreshCommandList();
                };
                _categoryPanel.Children.Add(chip);
            }
        }

        private void RefreshCommandList()
        {
            _listPanel.Children.Clear();
            string query = (_searchBox.Text ?? string.Empty).Trim();

            IEnumerable<CommandItem> filtered = _config.Commands;
            if (!string.Equals(_selectedCategory, "全部", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(c => string.Equals(
                    string.IsNullOrWhiteSpace(c.Category) ? "自定义" : c.Category.Trim(),
                    _selectedCategory,
                    StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(c =>
                    c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Command.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    c.Category.Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            List<CommandItem> items = filtered.ToList();
            if (items.Count == 0)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text = "暂无匹配指令，点击右上角「+ 新增」添加。",
                    Foreground = TextMutedBrush,
                    FontSize = 12,
                    Margin = new Thickness(4, 16, 4, 4),
                    TextWrapping = TextWrapping.Wrap,
                });
                return;
            }

            foreach (CommandItem item in items)
            {
                _listPanel.Children.Add(CreateCommandCard(item));
            }
        }

        private UIElement CreateCommandCard(CommandItem item)
        {
            var card = new Border
            {
                Background = CardBrush,
                BorderBrush = BorderDarkBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(10, 8, 8, 8),
                Cursor = Cursors.Hand,
            };

            card.MouseEnter += (_, _) => card.Background = CardHoverBrush;
            card.MouseLeave += (_, _) => card.Background = CardBrush;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            List<string> lines = CommandStore.ParseCommandLines(item.Command);
            bool isMulti = lines.Count > 1;

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var nameRow = new Grid();
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = item.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(nameBlock, 0);
            nameRow.Children.Add(nameBlock);

            if (isMulti)
            {
                var badge = new Border
                {
                    Background = HeaderBrush,
                    BorderBrush = AccentBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(5, 1, 5, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = $"{lines.Count}条",
                        FontSize = 9.5,
                        Foreground = AccentHoverBrush,
                    },
                };
                Grid.SetColumn(badge, 1);
                nameRow.Children.Add(badge);
            }

            textStack.Children.Add(nameRow);

            string previewText = isMulti
                ? string.Join("  ↵  ", lines)
                : (lines.Count == 1 ? lines[0] : item.Command);
            string tooltipText = isMulti
                ? string.Join(Environment.NewLine, lines.Select((line, idx) => $"{idx + 1}. {line}"))
                : item.Command;

            textStack.Children.Add(new TextBlock
            {
                Text = previewText,
                FontFamily = MonoFont,
                FontSize = 11,
                Foreground = TextMutedBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0),
                ToolTip = tooltipText,
            });

            card.MouseLeftButtonUp += async (_, e) =>
            {
                if (e.OriginalSource is DependencyObject dep && IsInsideButton(dep))
                {
                    return;
                }

                await TriggerInjectAsync(item.Command, execute: false);
            };

            Grid.SetColumn(textStack, 0);
            grid.Children.Add(textStack);

            var actionStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };

            string runTooltip = isMulti
                ? $"按顺序依次执行全部 {lines.Count} 条指令"
                : "填入并直接执行（带回车）";
            var runBtn = CreateRoundedButton("▶", RunGreenBrush, RunGreenHoverBrush, runTooltip, cornerRadius: 7, padding: new Thickness(7, 4, 7, 4));
            runBtn.Click += async (_, e) =>
            {
                e.Handled = true;
                await TriggerInjectAsync(item.Command, execute: true);
            };
            actionStack.Children.Add(runBtn);

            var editBtn = CreateRoundedButton("✎", HeaderBrush, AccentBrush, "编辑指令", cornerRadius: 7, padding: new Thickness(6, 4, 6, 4));
            editBtn.Margin = new Thickness(4, 0, 0, 0);
            editBtn.Click += (_, e) =>
            {
                e.Handled = true;
                OpenEditor(item);
            };
            actionStack.Children.Add(editBtn);

            var delBtn = CreateRoundedButton("🗑", HeaderBrush, DangerBrush, "删除指令", cornerRadius: 7, padding: new Thickness(6, 4, 6, 4));
            delBtn.Margin = new Thickness(4, 0, 0, 0);
            delBtn.Click += (_, e) =>
            {
                e.Handled = true;
                DeleteItem(item);
            };
            actionStack.Children.Add(delBtn);

            Grid.SetColumn(actionStack, 1);
            grid.Children.Add(actionStack);

            card.Child = grid;
            return card;
        }

        private async System.Threading.Tasks.Task TriggerInjectAsync(string command, bool execute)
        {
            if (_isInjecting)
            {
                ShowStatus("正在执行指令，请稍候...", TextMutedBrush);
                return;
            }

            if (!Win32TerminalBridge.IsValidWindow(_targetHwnd))
            {
                _targetHwnd = Win32TerminalBridge.FindTerminalWindow(IntPtr.Zero);
            }

            int lineCount = CommandStore.ParseCommandLines(command).Count;
            _isInjecting = true;
            try
            {
                if (execute && lineCount > 1)
                {
                    ShowStatus($"正在依次执行 {lineCount} 条指令...", AccentHoverBrush);
                }

                bool ok = await Win32TerminalBridge.InjectCommandAsync(_targetHwnd, command, execute);
                if (ok)
                {
                    string message = execute
                        ? (lineCount > 1 ? $"已依次执行 {lineCount} 条指令" : "已执行指令")
                        : (lineCount > 1 ? $"已填入 {lineCount} 条指令（; 分隔）" : "已填入命令行");
                    ShowStatus(message, execute ? RunGreenBrush : AccentHoverBrush);
                }
                else
                {
                    ShowStatus("未找到可输入的终端窗口", DangerBrush);
                }
            }
            finally
            {
                _isInjecting = false;
            }
        }

        private void OpenEditor(CommandItem? item)
        {
            _editingItem = item;
            _editorTitle.Text = item is null ? "新增指令" : "编辑指令";

            _editorCategoryChips.Children.Clear();
            var existingCategories = _config.Commands
                .Select(c => string.IsNullOrWhiteSpace(c.Category) ? "自定义" : c.Category.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!existingCategories.Contains("自定义", StringComparer.OrdinalIgnoreCase))
            {
                existingCategories.Add("自定义");
            }

            foreach (string cat in existingCategories)
            {
                var chip = CreateRoundedButton(cat, CardBrush, AccentBrush, $"填入分组：{cat}", cornerRadius: 10, padding: new Thickness(8, 2, 8, 2));
                chip.FontSize = 10.5;
                chip.Margin = new Thickness(0, 0, 4, 4);
                chip.Click += (_, _) => _categoryInput.Text = cat;
                _editorCategoryChips.Children.Add(chip);
            }

            if (item is null)
            {
                _nameInput.Text = string.Empty;
                _categoryInput.Text = string.Equals(_selectedCategory, "全部", StringComparison.OrdinalIgnoreCase)
                    ? "自定义"
                    : _selectedCategory;
                _commandInput.Text = string.Empty;
            }
            else
            {
                _nameInput.Text = item.Name;
                _categoryInput.Text = string.IsNullOrWhiteSpace(item.Category) ? "自定义" : item.Category;
                _commandInput.Text = item.Command.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            }

            _editorDrawer.Visibility = Visibility.Visible;
            _nameInput.Focus();
        }

        private void SaveEditor()
        {
            string name = (_nameInput.Text ?? string.Empty).Trim();
            string cmd = CommandStore.NormalizeCommand(_commandInput.Text);
            string cat = (_categoryInput.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(cat))
            {
                cat = "自定义";
            }

            if (string.IsNullOrEmpty(cmd))
            {
                ShowStatus("指令内容不能为空", DangerBrush);
                _commandInput.Focus();
                return;
            }

            if (string.IsNullOrEmpty(name))
            {
                string firstLine = CommandStore.ParseCommandLines(cmd)[0];
                name = firstLine.Length > 24 ? firstLine.Substring(0, 24) + "..." : firstLine;
            }

            if (_editingItem is null)
            {
                _config.Commands.Add(new CommandItem
                {
                    Name = name,
                    Command = cmd,
                    Category = cat,
                });
                ShowStatus("已新增指令", RunGreenBrush);
            }
            else
            {
                _editingItem.Name = name;
                _editingItem.Command = cmd;
                _editingItem.Category = cat;
                ShowStatus("已更新指令", RunGreenBrush);
            }

            CommandStore.Save(_config);
            _editorDrawer.Visibility = Visibility.Collapsed;
            _editingItem = null;
            RefreshCategories();
            RefreshCommandList();
        }

        private void DeleteItem(CommandItem item)
        {
            _config.Commands.Remove(item);
            CommandStore.Save(_config);
            if (_editingItem == item)
            {
                _editorDrawer.Visibility = Visibility.Collapsed;
                _editingItem = null;
            }

            RefreshCategories();
            RefreshCommandList();
            ShowStatus($"已删除：{item.Name}", TextMutedBrush);
        }

        private void OpenConfigFile()
        {
            try
            {
                string path = CommandStore.GetConfigPath();
                if (!File.Exists(path))
                {
                    CommandStore.Save(_config);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,
                });
            }
            catch
            {
                ShowStatus("无法打开配置文件", DangerBrush);
            }
        }

        private void ShowStatus(string text, SolidColorBrush brush)
        {
            _statusLabel.Text = text;
            _statusLabel.Foreground = brush;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private static bool IsInsideButton(DependencyObject? source)
        {
            while (source is not null)
            {
                if (source is Button)
                {
                    return true;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return false;
        }

        private static TextBlock CreateFieldLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = TextMutedBrush,
            };
        }

        private static (Border Container, TextBox Input) CreateRoundedTextBox(string placeholder, bool isMono = false, bool multiline = false)
        {
            var container = new Border
            {
                Background = InputBgBrush,
                BorderBrush = BorderDarkBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 5, 8, 5),
            };

            var grid = new Grid();
            var hint = new TextBlock
            {
                Text = placeholder,
                FontSize = 11.5,
                FontFamily = isMono ? MonoFont : UiFont,
                Foreground = TextMutedBrush,
                Opacity = 0.65,
                IsHitTestVisible = false,
                VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            };

            var input = new TextBox
            {
                Background = Brushes.Transparent,
                Foreground = TextPrimaryBrush,
                CaretBrush = TextPrimaryBrush,
                SelectionBrush = AccentBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                FontSize = 12,
                FontFamily = isMono ? MonoFont : UiFont,
                VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            };

            if (multiline)
            {
                input.MinHeight = 66;
                input.MaxHeight = 150;
            }

            input.TextChanged += (_, _) =>
            {
                hint.Visibility = string.IsNullOrEmpty(input.Text) ? Visibility.Visible : Visibility.Collapsed;
            };
            input.GotKeyboardFocus += (_, _) => container.BorderBrush = AccentBrush;
            input.LostKeyboardFocus += (_, _) => container.BorderBrush = BorderDarkBrush;

            grid.Children.Add(hint);
            grid.Children.Add(input);
            container.Child = grid;
            return (container, input);
        }

        private static Button CreateRoundedButton(
            string text,
            SolidColorBrush normalBg,
            SolidColorBrush hoverBg,
            string tooltip,
            double cornerRadius = 8,
            Thickness? padding = null)
        {
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(cornerRadius));

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);
            template.VisualTree = borderFactory;

            var btn = new Button
            {
                Content = text,
                Background = normalBg,
                Foreground = TextPrimaryBrush,
                BorderThickness = new Thickness(0),
                Padding = padding ?? new Thickness(7, 4, 7, 4),
                FontSize = 11.5,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Template = template,
            };
            btn.MouseEnter += (_, _) => btn.Background = hoverBg;
            btn.MouseLeave += (_, _) => btn.Background = normalBg;
            return btn;
        }

        private static void WriteDismissMarker(nint targetHwnd)
        {
            try
            {
                uint pid = Win32TerminalBridge.GetWindowProcessId(targetHwnd);
                if (pid != 0)
                {
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), $"SuperPowerShell.PanelDismissed.{pid}"), "1");
                }
            }
            catch
            {
                // Ignore IO failures.
            }
        }

        private static void ClearDismissMarker(nint targetHwnd)
        {
            try
            {
                uint pid = Win32TerminalBridge.GetWindowProcessId(targetHwnd);
                if (pid != 0)
                {
                    string marker = Path.Combine(Path.GetTempPath(), $"SuperPowerShell.PanelDismissed.{pid}");
                    if (File.Exists(marker))
                    {
                        File.Delete(marker);
                    }
                }
            }
            catch
            {
                // Ignore IO failures.
            }
        }

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }
    }
}
