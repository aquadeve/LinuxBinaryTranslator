// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Main page for the Linux Binary Translator UWP app.
// Provides a terminal-style UI with gamepad support for Xbox One.
// Handles ELF binary loading via file picker, execution control,
// and terminal I/O bridging.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinuxBinaryTranslator.FileSystem;
using LinuxBinaryTranslator.Terminal;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace LinuxBinaryTranslator
{
    /// <summary>
    /// Main page hosting the terminal emulator UI.
    /// Designed for both desktop and Xbox One gamepad interaction.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private TerminalEmulator? _terminal;
        private ExecutionEngine? _engine;
        private CancellationTokenSource? _cts;
        private readonly StringBuilder _displayBuffer = new StringBuilder();

        // Rate-limit UI updates for performance once the visual tree is ready.
        private DispatcherTimer? _uiUpdateTimer;
        private bool _outputDirty;
        private bool _viewReady;

        public MainPage()
        {
            Debug.WriteLine("[LBT] MainPage ctor: begin");

            try
            {
                InitializeComponent();
                Debug.WriteLine("[LBT] MainPage ctor: InitializeComponent complete");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LBT] MainPage ctor: InitializeComponent failed: " + ex);
                Content = new TextBlock
                {
                    Text = "Linux Binary Translator failed to initialize its UI.\n\n" + ex,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(24)
                };
                return;
            }

            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;
            Debug.WriteLine("[LBT] MainPage ctor: complete");
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[LBT] MainPage Loaded");
            _viewReady = true;

            if (_uiUpdateTimer == null)
            {
                _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            }

            _uiUpdateTimer.Start();
            UpdateDisplay();
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("[LBT] MainPage Unloaded");
            _viewReady = false;
            _uiUpdateTimer?.Stop();
        }

        /// <summary>
        /// Load an ELF binary from the file picker.
        /// </summary>
        private async void LoadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                };
                picker.FileTypeFilter.Add("*");

                StorageFile file = await picker.PickSingleFileAsync();
                if (file == null)
                {
                    return;
                }

                await LoadAndRunAsync(file);
            }
            catch (Exception ex)
            {
                AppendOutput($"Load failed: {ex.Message}\n");
                UpdateStatus("Load failed");
                LogMessage("LoadButton_Click failed: " + ex);
            }
        }

        /// <summary>
        /// Load and execute an ELF binary file.
        /// </summary>
        private async Task LoadAndRunAsync(StorageFile file)
        {
            StopExecution();

            var buffer = await FileIO.ReadBufferAsync(file);
            byte[] elfData = new byte[buffer.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
            {
                reader.ReadBytes(elfData);
            }

            _displayBuffer.Clear();
            _displayBuffer.AppendLine($"Loading: {file.Name} ({elfData.Length} bytes)");
            UpdateDisplay();

            _terminal = new TerminalEmulator();
            _terminal.OutputReceived += Terminal_OutputReceived;

            _engine = new ExecutionEngine(
                stdinRead: _terminal.ReadInput,
                stdoutWrite: _terminal.WriteOutput,
                stderrWrite: _terminal.WriteOutput,
                logger: LogMessage);

            try
            {
                var loadResult = _engine.LoadBinary(elfData, new[] { file.Name });
                AppendOutput($"Entry point: 0x{loadResult.EntryPoint:X16}\n");
                AppendOutput($"Base address: 0x{loadResult.BaseAddress:X16}\n");
                AppendOutput($"Program headers: 0x{loadResult.ProgramHeaderAddress:X16} ({loadResult.ProgramHeaderCount} x {loadResult.ProgramHeaderEntrySize})\n");
                if (!string.IsNullOrEmpty(loadResult.InterpreterPath))
                {
                    AppendOutput($"Interpreter: {loadResult.InterpreterPath}\n");
                    AppendOutput($"Interpreter base: 0x{loadResult.InterpreterBase:X16}\n");
                }
                AppendOutput($"Segments: {loadResult.Segments.Count}\n");
                AppendOutput($"Machine: {(loadResult.Machine == Elf.ElfConstants.EM_X86_64 ? "x86_64" : $"0x{loadResult.Machine:X}")}\n");
                AppendOutput("--- Execution started ---\n");

                UpdateStatus("Running");
                LoadButton.IsEnabled = false;
                StopButton.IsEnabled = true;

                _cts = new CancellationTokenSource();
                var result = await _engine.ExecuteAsync(_cts.Token);

                AppendOutput("\n--- Execution finished ---\n");
                AppendOutput($"Exit code: {result.ExitCode}\n");
                AppendOutput($"Blocks executed: {result.InstructionBlocksExecuted:N0}\n");
                AppendOutput($"Elapsed: {result.ElapsedTime.TotalMilliseconds:F1} ms\n");
                if (result.Error != null)
                {
                    AppendOutput($"Error: {result.Error}\n");
                }

                UpdateStatus($"Exited ({result.ExitCode})");
                UpdatePerformance($"{result.InstructionBlocksExecuted:N0} blocks in {result.ElapsedTime.TotalMilliseconds:F0}ms");
            }
            catch (Elf.ElfLoadException ex)
            {
                AppendOutput($"ELF load error: {ex.Message}\n");
                UpdateStatus("Load failed");
                LogMessage("LoadAndRunAsync ELF load failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}\n");
                UpdateStatus("Error");
                LogMessage("LoadAndRunAsync failed: " + ex);
            }
            finally
            {
                LoadButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// Boot a Linux distribution from a rootfs folder.
        /// The rootfs folder should be in app local storage or picked via folder picker.
        /// </summary>
        private async void BootRootfsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker
                {
                    ViewMode = PickerViewMode.List,
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                };
                picker.FileTypeFilter.Add("*");

                StorageFolder folder = await picker.PickSingleFolderAsync();
                if (folder == null)
                {
                    return;
                }

                await BootRootfsAsync(folder);
            }
            catch (Exception ex)
            {
                AppendOutput($"Rootfs boot failed: {ex.Message}\n");
                UpdateStatus("Boot failed");
                LogMessage("BootRootfsButton_Click failed: " + ex);
            }
        }

        /// <summary>
        /// Load a rootfs from a folder and boot its default shell.
        /// </summary>
        private async Task BootRootfsAsync(StorageFolder rootfsFolder)
        {
            StopExecution();

            _displayBuffer.Clear();
            _displayBuffer.AppendLine($"Loading rootfs from: {rootfsFolder.Name}");
            _displayBuffer.AppendLine("Scanning files...");
            UpdateDisplay();

            _terminal = new TerminalEmulator();
            _terminal.OutputReceived += Terminal_OutputReceived;

            _engine = new ExecutionEngine(
                stdinRead: _terminal.ReadInput,
                stdoutWrite: _terminal.WriteOutput,
                stderrWrite: _terminal.WriteOutput,
                logger: LogMessage);

            try
            {
                var fileMap = new Dictionary<string, byte[]>();
                int fileCount = 0;
                long totalBytes = 0;

                await ScanFolderTreeAsync(rootfsFolder, fileMap);

                foreach (var kvp in fileMap)
                {
                    fileCount++;
                    totalBytes += kvp.Value.Length;
                }

                AppendOutput($"Found {fileCount} files ({totalBytes / 1024}KB)\n");
                AppendOutput("Mounting rootfs...\n");

                var rootfs = new RootfsManager(_engine.Vfs, LogMessage);
                var info = rootfs.LoadFromFileMap(fileMap);

                AppendOutput($"Distro: {info.DistroName} {info.Version}\n");
                AppendOutput($"Shell: {info.ShellPath}\n");
                AppendOutput($"Files: {info.FileCount}, Size: {info.TotalSize / 1024}KB\n");
                AppendOutput("--- Booting ---\n\n");

                UpdateStatus($"Booting {info.DistroName}");
                LoadButton.IsEnabled = false;
                BootRootfsButton.IsEnabled = false;
                StopButton.IsEnabled = true;

                var loadResult = _engine.BootRootfs(rootfs);
                AppendOutput($"Entry point: 0x{loadResult.EntryPoint:X16}\n");
                AppendOutput($"Base address: 0x{loadResult.BaseAddress:X16}\n");
                AppendOutput($"Program headers: 0x{loadResult.ProgramHeaderAddress:X16} ({loadResult.ProgramHeaderCount} x {loadResult.ProgramHeaderEntrySize})\n");
                if (!string.IsNullOrEmpty(loadResult.InterpreterPath))
                {
                    AppendOutput($"Interpreter: {loadResult.InterpreterPath}\n");
                    AppendOutput($"Interpreter base: 0x{loadResult.InterpreterBase:X16}\n");
                }

                _cts = new CancellationTokenSource();
                var result = await _engine.ExecuteAsync(_cts.Token);

                AppendOutput("\n--- Session ended ---\n");
                AppendOutput($"Exit code: {result.ExitCode}\n");
                AppendOutput($"Blocks executed: {result.InstructionBlocksExecuted:N0}\n");
                AppendOutput($"Elapsed: {result.ElapsedTime.TotalMilliseconds:F1} ms\n");
                if (result.Error != null)
                {
                    AppendOutput($"Error: {result.Error}\n");
                }

                UpdateStatus($"Exited ({result.ExitCode})");
                UpdatePerformance($"{result.InstructionBlocksExecuted:N0} blocks in {result.ElapsedTime.TotalMilliseconds:F0}ms");
            }
            catch (Elf.ElfLoadException ex)
            {
                AppendOutput($"ELF load error: {ex.Message}\n");
                UpdateStatus("Boot failed");
                LogMessage("BootRootfsAsync ELF load failed: " + ex.Message);
            }
            catch (Exception ex)
            {
                AppendOutput($"Error: {ex.Message}\n");
                UpdateStatus("Error");
                LogMessage("BootRootfsAsync failed: " + ex);
            }
            finally
            {
                LoadButton.IsEnabled = true;
                BootRootfsButton.IsEnabled = true;
                StopButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// Iteratively scan a folder and load all files into a path to data map.
        /// Paths are relative to the rootfs root (for example, /bin/bash).
        /// </summary>
        private async Task ScanFolderTreeAsync(StorageFolder rootFolder, Dictionary<string, byte[]> fileMap)
        {
            var pending = new Queue<Tuple<StorageFolder, string>>();
            pending.Enqueue(Tuple.Create(rootFolder, ""));

            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                StorageFolder folder = current.Item1;
                string prefix = current.Item2;

                IReadOnlyList<StorageFile> files;
                try
                {
                    files = await folder.GetFilesAsync();
                }
                catch (Exception ex)
                {
                    LogMessage($"Skipping files in '{folder.Name}': {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    try
                    {
                        var buffer = await FileIO.ReadBufferAsync(file);
                        byte[] data = new byte[buffer.Length];
                        using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer))
                        {
                            reader.ReadBytes(data);
                        }

                        string path = prefix + "/" + file.Name;
                        fileMap[path] = data;
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"Skipping file '{file.Name}': {ex.Message}");
                    }
                }

                IReadOnlyList<StorageFolder> subFolders;
                try
                {
                    subFolders = await folder.GetFoldersAsync();
                }
                catch (Exception ex)
                {
                    LogMessage($"Skipping folders in '{folder.Name}': {ex.Message}");
                    continue;
                }

                foreach (var sub in subFolders)
                {
                    string subPrefix = prefix + "/" + sub.Name;
                    pending.Enqueue(Tuple.Create(sub, subPrefix));
                }
            }
        }

        /// <summary>
        /// Stop the currently running binary.
        /// </summary>
        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopExecution();
        }

        private void StopExecution()
        {
            _cts?.Cancel();
            _cts = null;
            if (_engine != null)
            {
                _engine.Cpu.Halted = true;
                _engine = null;
            }
        }

        /// <summary>
        /// Handle input box Enter key to send input to the process.
        /// </summary>
        private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                SendInput();
                e.Handled = true;
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendInput();
        }

        private void SendInput()
        {
            string text = InputBox.Text;
            if (_terminal != null)
            {
                _terminal.SendLine(text);
                AppendOutput($"$ {text}\n");
            }

            InputBox.Text = "";
        }

        /// <summary>
        /// Handle page-level key events for Xbox gamepad button mapping.
        /// </summary>
        private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_terminal == null)
            {
                return;
            }

            switch (e.Key)
            {
                case VirtualKey.Escape:
                case VirtualKey.GamepadB:
                    _terminal.SendKey(TerminalKey.CtrlC);
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadX:
                    LoadButton_Click(sender, e);
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadY:
                    _displayBuffer.Clear();
                    UpdateDisplay();
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadA:
                    SendInput();
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadDPadUp:
                    _terminal.SendKey(TerminalKey.Up);
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadDPadDown:
                    _terminal.SendKey(TerminalKey.Down);
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadDPadLeft:
                    _terminal.SendKey(TerminalKey.Left);
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadDPadRight:
                    _terminal.SendKey(TerminalKey.Right);
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadLeftShoulder:
                    _terminal.SendKey(TerminalKey.Tab);
                    e.Handled = true;
                    break;

                case VirtualKey.GamepadRightShoulder:
                    _terminal.SendKey(TerminalKey.Backspace);
                    e.Handled = true;
                    break;
            }
        }

        private void Terminal_OutputReceived(object? sender, TerminalOutputEventArgs e)
        {
            _outputDirty = true;
        }

        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (!_outputDirty || _terminal == null)
            {
                return;
            }

            _outputDirty = false;

            string newText = _terminal.FlushOutput();
            if (newText.Length > 0)
            {
                _displayBuffer.Append(newText);

                if (_displayBuffer.Length > 512 * 1024)
                {
                    _displayBuffer.Remove(0, _displayBuffer.Length - 256 * 1024);
                }

                UpdateDisplay();
            }
        }

        private void UpdateDisplay()
        {
            if (!_viewReady || OutputTextBlock == null)
            {
                return;
            }

            OutputTextBlock.Text = _displayBuffer.ToString();
            if (OutputScrollViewer == null)
            {
                return;
            }

            try
            {
                OutputScrollViewer.ChangeView(null, OutputScrollViewer.ScrollableHeight, null);
            }
            catch (Exception ex)
            {
                LogMessage("ChangeView failed: " + ex.Message);
            }
        }

        private void AppendOutput(string text)
        {
            _displayBuffer.Append(text);
            _outputDirty = true;
        }

        private void UpdateStatus(string status)
        {
            if (StatusText != null)
            {
                StatusText.Text = status;
            }
        }

        private void UpdatePerformance(string text)
        {
            if (PerformanceText != null)
            {
                PerformanceText.Text = text;
            }
        }

        private void LogMessage(string message)
        {
            Debug.WriteLine($"[LBT] {message}");
            AppendOutput($"[LBT] {message}\n");
        }
    }
}
