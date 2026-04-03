// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Terminal emulator for the UWP XAML UI.
// Provides a text-based terminal display that bridges Linux process
// stdout/stderr output and stdin input through the UWP TextBlock/TextBox
// controls. Designed for Xbox One gamepad navigation.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace LinuxBinaryTranslator.Terminal
{
    /// <summary>
    /// Event args for terminal output.
    /// </summary>
    public sealed class TerminalOutputEventArgs : EventArgs
    {
        public string Text { get; }
        public TerminalOutputEventArgs(string text) => Text = text;
    }

    /// <summary>
    /// Terminal emulator that buffers I/O between the Linux process and
    /// the UWP UI. Handles ANSI escape sequence stripping, line buffering,
    /// and input queuing for the translated process.
    ///
    /// Xbox One compatible: no dependency on Win32 console APIs.
    /// </summary>
    public sealed class TerminalEmulator
    {
        private readonly StringBuilder _outputBuffer = new StringBuilder();
        private readonly Queue<byte> _inputQueue = new Queue<byte>();
        private readonly ManualResetEventSlim _inputAvailable = new ManualResetEventSlim(false);
        private readonly object _inputLock = new object();
        private readonly object _outputLock = new object();

        // Terminal dimensions (default 80x24)
        private int _columns = 80;
        private int _rows = 24;

        // Maximum output buffer size to prevent unbounded growth
        private const int MaxOutputBufferSize = 1024 * 1024; // 1 MB

        /// <summary>
        /// Fired when new output text is available for display.
        /// </summary>
        public event EventHandler<TerminalOutputEventArgs>? OutputReceived;

        /// <summary>
        /// Terminal width in columns.
        /// </summary>
        public int Columns
        {
            get => _columns;
            set => _columns = Math.Max(1, value);
        }

        /// <summary>
        /// Terminal height in rows.
        /// </summary>
        public int Rows
        {
            get => _rows;
            set => _rows = Math.Max(1, value);
        }

        /// <summary>
        /// Get all buffered output text and clear the buffer.
        /// </summary>
        public string FlushOutput()
        {
            lock (_outputLock)
            {
                string text = _outputBuffer.ToString();
                _outputBuffer.Clear();
                return text;
            }
        }

        /// <summary>
        /// Write handler for stdout/stderr from the translated process.
        /// Called by the VFS ConsoleDevice.
        /// </summary>
        public void WriteOutput(byte[] data, int offset, int count)
        {
            string text = Encoding.UTF8.GetString(data, offset, count);
            // Strip common ANSI escape sequences for clean display
            text = StripAnsiEscapes(text);

            lock (_outputLock)
            {
                _outputBuffer.Append(text);

                // Trim buffer if too large (keep the tail)
                if (_outputBuffer.Length > MaxOutputBufferSize)
                {
                    int removeLen = _outputBuffer.Length - MaxOutputBufferSize / 2;
                    _outputBuffer.Remove(0, removeLen);
                }
            }

            OutputReceived?.Invoke(this, new TerminalOutputEventArgs(text));
        }

        /// <summary>
        /// Read handler for stdin for the translated process.
        /// Blocks until input is available (or returns 0 for non-blocking).
        /// Called by the VFS ConsoleDevice.
        /// </summary>
        public int ReadInput(byte[] buffer, int offset, int count)
        {
            // Wait for input to be available
            _inputAvailable.Wait(TimeSpan.FromMilliseconds(100));

            int bytesRead = 0;
            lock (_inputLock)
            {
                while (bytesRead < count && _inputQueue.Count > 0)
                {
                    buffer[offset + bytesRead] = _inputQueue.Dequeue();
                    bytesRead++;
                }

                if (_inputQueue.Count == 0)
                    _inputAvailable.Reset();
            }

            return bytesRead;
        }

        /// <summary>
        /// Send input text to the process (simulates keyboard input).
        /// Called from the UWP UI when the user types text.
        /// </summary>
        public void SendInput(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            lock (_inputLock)
            {
                foreach (byte b in data)
                    _inputQueue.Enqueue(b);
                _inputAvailable.Set();
            }
        }

        /// <summary>
        /// Send a single line of input followed by a newline.
        /// </summary>
        public void SendLine(string line)
        {
            SendInput(line + "\n");
        }

        /// <summary>
        /// Send a special key (for Xbox gamepad button mapping).
        /// </summary>
        public void SendKey(TerminalKey key)
        {
            string sequence = key switch
            {
                TerminalKey.Enter => "\n",
                TerminalKey.Tab => "\t",
                TerminalKey.Backspace => "\x7F",
                TerminalKey.Escape => "\x1B",
                TerminalKey.Up => "\x1B[A",
                TerminalKey.Down => "\x1B[B",
                TerminalKey.Right => "\x1B[C",
                TerminalKey.Left => "\x1B[D",
                TerminalKey.Home => "\x1B[H",
                TerminalKey.End => "\x1B[F",
                TerminalKey.Delete => "\x1B[3~",
                TerminalKey.CtrlC => "\x03",
                TerminalKey.CtrlD => "\x04",
                TerminalKey.CtrlZ => "\x1A",
                TerminalKey.CtrlL => "\x0C",
                _ => "",
            };
            if (sequence.Length > 0)
                SendInput(sequence);
        }

        /// <summary>
        /// Strip ANSI escape sequences from text for clean display.
        /// </summary>
        private static string StripAnsiEscapes(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == '\x1B' && i + 1 < text.Length && text[i + 1] == '[')
                {
                    // Skip CSI sequence: ESC [ ... (letter)
                    i += 2;
                    while (i < text.Length && !char.IsLetter(text[i]) && text[i] != '@')
                        i++;
                    if (i < text.Length)
                        i++; // Skip the terminating letter
                }
                else if (text[i] == '\x1B')
                {
                    // Skip other ESC sequences (2 chars)
                    i += 2;
                }
                else if (text[i] == '\r')
                {
                    // Convert \r\n to \n, skip bare \r
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        sb.Append('\n');
                        i += 2;
                    }
                    else
                    {
                        i++;
                    }
                }
                else
                {
                    sb.Append(text[i]);
                    i++;
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Terminal special key identifiers.
    /// Mapped to Xbox One gamepad buttons in the UI layer.
    /// </summary>
    public enum TerminalKey
    {
        Enter,
        Tab,
        Backspace,
        Escape,
        Up,
        Down,
        Left,
        Right,
        Home,
        End,
        Delete,
        CtrlC,
        CtrlD,
        CtrlZ,
        CtrlL,
    }
}
