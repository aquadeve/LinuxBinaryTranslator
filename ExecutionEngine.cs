// Copyright (c) Linux Binary Translator contributors.
// Licensed under the GPLv3+ license.
//
// Execution engine: orchestrates loading, translation, and running of
// Linux ELF binaries. This is the top-level coordinator that connects
// the ELF loader, block translator, syscall handler, and VFS.

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LinuxBinaryTranslator.Cpu;
using LinuxBinaryTranslator.Cpu.Translation;
using LinuxBinaryTranslator.Elf;
using LinuxBinaryTranslator.FileSystem;
using LinuxBinaryTranslator.Memory;
using LinuxBinaryTranslator.Syscall;

namespace LinuxBinaryTranslator
{
    /// <summary>
    /// Result of a completed binary execution.
    /// </summary>
    public sealed class ExecutionResult
    {
        public int ExitCode { get; set; }
        public long InstructionBlocksExecuted { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Request to execute a new binary via execve.
    /// Set by the syscall handler when execve is called.
    /// </summary>
    public sealed class ExecveRequest
    {
        public byte[] ElfData { get; set; } = Array.Empty<byte>();
        public string[] Argv { get; set; } = Array.Empty<string>();
        public string[] Envp { get; set; } = Array.Empty<string>();
        public string Path { get; set; } = "";
    }

    /// <summary>
    /// Orchestrates the loading and execution of a Linux ELF binary.
    /// Connects all subsystems: ELF loader → block translator → syscall handler → VFS.
    /// Supports rootfs-based execution for running Linux distribution shells.
    /// </summary>
    public sealed class ExecutionEngine
    {
        private VirtualMemoryManager _memory;
        private readonly VirtualFileSystem _vfs;
        private BlockTranslator _translator;
        private SyscallHandler _syscallHandler;
        private CpuState _cpu;
        private readonly Action<string> _logger;
        private readonly Func<byte[], int, int, int>? _stdinRead;
        private readonly Action<byte[], int, int>? _stdoutWrite;
        private readonly Action<byte[], int, int>? _stderrWrite;

        // Stack configuration
        private const ulong StackBase = 0x7FFFFFFFE000UL;
        private const ulong StackSize = 8UL * 1024 * 1024; // 8 MB stack

        public CpuState Cpu => _cpu;
        public VirtualMemoryManager Memory => _memory;
        public VirtualFileSystem Vfs => _vfs;

        public ExecutionEngine(
            Func<byte[], int, int, int>? stdinRead,
            Action<byte[], int, int>? stdoutWrite,
            Action<byte[], int, int>? stderrWrite,
            Action<string>? logger = null)
        {
            _logger = logger ?? (_ => { });
            _stdinRead = stdinRead;
            _stdoutWrite = stdoutWrite;
            _stderrWrite = stderrWrite;
            _memory = new VirtualMemoryManager();
            _vfs = new VirtualFileSystem(stdinRead, stdoutWrite, stderrWrite);
            _cpu = new CpuState();
            _translator = new BlockTranslator(_memory);
            _syscallHandler = new SyscallHandler(_memory, _vfs, _logger);
            _syscallHandler.SetExecutionEngine(this);
            _translator.SyscallHandler = _syscallHandler.Dispatch;
        }

        /// <summary>
        /// Load an ELF binary into memory and prepare it for execution.
        /// If the binary requires a dynamic linker (PT_INTERP), the interpreter
        /// is loaded from the rootfs and execution starts at the interpreter's
        /// entry point instead of the binary's.
        /// </summary>
        public ElfLoadResult LoadBinary(byte[] elfData, string[]? argv = null, string[]? envp = null)
        {
            var loader = new ElfLoader(_memory);
            string programPath = argv != null && argv.Length > 0 ? argv[0] : "program";
            ElfLoadResult loadResult;
            try
            {
                loadResult = loader.Load(elfData);
            }
            catch (ElfLoadException ex)
            {
                _logger($"ELF load failed for '{programPath}': {ex.Message}");
                ExceptionDispatchInfo.Capture(ex).Throw();
                throw;
            }

            _logger($"ELF loaded: entry=0x{loadResult.EntryPoint:X16}, " +
                    $"base=0x{loadResult.BaseAddress:X16}, " +
                    $"brk=0x{loadResult.BrkAddress:X16}, " +
                    $"segments={loadResult.Segments.Count}" +
                    (loadResult.InterpreterPath != null ? $", interp={loadResult.InterpreterPath}" : ""));

            // If the binary needs a dynamic linker, load it
            ulong interpBase = 0;
            if (loadResult.InterpreterPath != null)
            {
                byte[]? interpData = _vfs.ReadFileBytes(loadResult.InterpreterPath);
                if (interpData != null)
                {
                    // Load interpreter above the main binary
                    ulong interpLoadAddr = ((loadResult.BrkAddress + 0x100000UL) & ~0xFFFUL);
                    ElfLoadResult interpResult;
                    try
                    {
                        interpResult = loader.LoadInterpreter(interpData, interpLoadAddr);
                    }
                    catch (ElfLoadException ex)
                    {
                        _logger($"Interpreter load failed for '{loadResult.InterpreterPath}' (program '{programPath}'): {ex.Message}");
                        ExceptionDispatchInfo.Capture(ex).Throw();
                        throw;
                    }
                    interpBase = interpResult.InterpreterBase;

                    _logger($"Interpreter loaded: entry=0x{interpResult.EntryPoint:X16}, " +
                            $"base=0x{interpBase:X16}");

                    // Execution starts at the interpreter's entry point
                    // The interpreter will then jump to the main binary's entry
                    loadResult.InterpreterBase = interpBase;

                    // Update brk to be above both binaries
                    if (interpResult.BrkAddress > loadResult.BrkAddress)
                        loadResult.BrkAddress = interpResult.BrkAddress;

                    // Save the real entry point — the interpreter needs it via AT_ENTRY
                    ulong realEntry = loadResult.EntryPoint;
                    loadResult.EntryPoint = interpResult.EntryPoint;

                    // Initialize the program break
                    _memory.InitializeBrk(loadResult.BrkAddress);

                    // Set up the stack with AT_BASE pointing to interpreter
                    SetupStackWithInterpreter(loadResult, realEntry, interpBase,
                        argv ?? new[] { "program" }, envp ?? GetDefaultEnvironment());

                    _cpu.RIP = loadResult.EntryPoint;
                    return loadResult;
                }
                else
                {
                    _logger($"Warning: interpreter not found: {loadResult.InterpreterPath}, " +
                            "attempting direct execution");
                }
            }

            // Initialize the program break
            _memory.InitializeBrk(loadResult.BrkAddress);

            // Set up the stack
            SetupStack(loadResult, argv ?? new[] { "program" }, envp ?? GetDefaultEnvironment());

            // Set the instruction pointer to the ELF entry point
            _cpu.RIP = loadResult.EntryPoint;

            return loadResult;
        }

        /// <summary>
        /// Execute the loaded binary until it exits or an error occurs.
        /// Supports execve — when a process calls execve, the engine resets
        /// and loads the new binary, continuing execution transparently.
        /// </summary>
        public async Task<ExecutionResult> ExecuteAsync(CancellationToken cancellationToken = default)
        {
            var startTime = DateTime.UtcNow;
            long blocksExecuted = 0;
            string? error = null;
            var recentBlocks = new Queue<ulong>();

            try
            {
                await Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        // Check for pending execve request
                        ExecveRequest? execReq = _syscallHandler.PendingExecve;
                        if (execReq != null)
                        {
                            _syscallHandler.PendingExecve = null;
                            HandleExecve(execReq);
                            continue; // Start executing the new binary
                        }

                        if (_cpu.Halted)
                            break;

                        // Check for pending signals at safe points
                        if (_syscallHandler.DeliverPendingSignal(_cpu, _memory))
                        {
                            // Signal handler was set up — continue execution at new RIP
                            if (_cpu.Halted) break;
                            continue;
                        }

                        if (!_memory.IsMapped(_cpu.RIP))
                        {
                            // Try to deliver SIGSEGV before terminating
                            _logger($"Execution fault: RIP=0x{_cpu.RIP:X16} is not in mapped memory");
                            _logger($"Registers: RSP=0x{_cpu.RSP:X16}, RBP=0x{_cpu.RBP:X16}, RAX=0x{_cpu.RAX:X16}, RBX=0x{_cpu.RBX:X16}, RCX=0x{_cpu.RCX:X16}, RDX=0x{_cpu.RDX:X16}, RSI=0x{_cpu.RSI:X16}, RDI=0x{_cpu.RDI:X16}");
                            _syscallHandler.QueueSignal(11); // SIGSEGV
                            if (_syscallHandler.DeliverPendingSignal(_cpu, _memory))
                            {
                                if (_cpu.Halted) break;
                                continue; // Handler was set up
                            }
                            _cpu.Halted = true;
                            _cpu.ExitCode = 139; // SIGSEGV
                            break;
                        }

                        var block = _translator.GetBlock(_cpu.RIP);
                        recentBlocks.Enqueue(block.Address);
                        while (recentBlocks.Count > 8)
                            recentBlocks.Dequeue();

                        block.ExecutionCount++;
                        blocksExecuted++;

                        ulong nextAddr = block.Execute(_cpu, _memory);

                        if (!_cpu.Halted && nextAddr != 0 && !_memory.IsMapped(nextAddr))
                        {
                            _logger($"Control transfer fault: block 0x{block.Address:X16} -> 0x{nextAddr:X16}");
                            _logger($"Block bytes: {FormatBytes(_memory.Read(block.Address, 16))}");
                            _logger($"Decoded block: {_translator.DescribeBlock(block.Address)}");
                            _logger($"Recent blocks: {FormatRecentBlocks(recentBlocks)}");
                            _logger($"Decoded recent blocks: {DescribeRecentBlocks(recentBlocks)}");
                        }

                        if (!_cpu.Halted && nextAddr != 0)
                            _cpu.RIP = nextAddr;

                        // Safety: check for infinite loops with a yield point
                        if (blocksExecuted % 100000 == 0)
                            await Task.Yield();
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                error = "Execution cancelled";
            }
            catch (Exception ex)
            {
                error = $"Execution error: {ex.Message}";
                _logger(error);
            }

            return new ExecutionResult
            {
                ExitCode = _cpu.ExitCode,
                InstructionBlocksExecuted = blocksExecuted,
                ElapsedTime = DateTime.UtcNow - startTime,
                Error = error,
            };
        }

        /// <summary>
        /// Handle an execve request: reset the CPU state, memory, and block cache,
        /// then load the new binary and prepare it for execution.
        /// This replaces the current process image — just like the real execve.
        /// </summary>
        private void HandleExecve(ExecveRequest request)
        {
            _logger($"execve: loading {request.Path} with {request.Argv.Length} args");

            // Reset memory manager (clears all mappings)
            _memory = new VirtualMemoryManager();

            // Reset CPU state
            _cpu = new CpuState();

            // Reset block translator cache (old translated code is invalid)
            _translator = new BlockTranslator(_memory);

            // Reconnect syscall handler with new memory
            _syscallHandler = new SyscallHandler(_memory, _vfs, _logger);
            _syscallHandler.SetExecutionEngine(this);
            _translator.SyscallHandler = _syscallHandler.Dispatch;

            try
            {
                // Keep /proc/self in sync with the new process image.
                _vfs.SetProcessInfo(request.Path, request.Argv);

                // Reuse the normal binary loading path so PT_INTERP binaries
                // also load their dynamic linker during execve.
                var loadResult = LoadBinary(request.ElfData, request.Argv, request.Envp);
                _logger($"execve: entry=0x{loadResult.EntryPoint:X16}, " +
                        $"segments={loadResult.Segments.Count}");
            }
            catch (Exception ex)
            {
                _logger($"execve failed: {ex.Message}");
                _cpu.Halted = true;
                _cpu.ExitCode = 126; // Cannot execute
            }
        }

        /// <summary>
        /// Boot a Linux distribution from a rootfs.
        /// Loads the rootfs into the VFS and launches the default shell.
        /// </summary>
        public Elf.ElfLoadResult BootRootfs(FileSystem.RootfsManager rootfs, string? shellOverride = null, string[]? extraArgs = null)
        {
            var info = rootfs.Info;
            if (info == null)
                throw new InvalidOperationException("Rootfs not loaded");

            string shellPath = shellOverride ?? info.ShellPath;

            // Read the shell binary from rootfs
            byte[]? shellData = rootfs.ReadFile(shellPath);
            if (shellData == null)
                throw new Elf.ElfLoadException($"Shell not found in rootfs: {shellPath}");

            _logger($"Booting {info.DistroName} {info.Version}: {shellPath}");

            // Build argv: [shell, --login] or [shell, ...extraArgs]
            var argv = new List<string> { shellPath };
            if (extraArgs != null && extraArgs.Length > 0)
                argv.AddRange(extraArgs);
            else
                argv.Add("--login");

            // Get environment from rootfs
            string[] envp = rootfs.GetDefaultEnvironment();

            // Update /proc/self entries for the shell
            _vfs.SetProcessInfo(shellPath, argv);

            // Load and prepare the shell binary
            return LoadBinary(shellData, argv.ToArray(), envp);
        }

        /// <summary>
        /// Set up the process stack with argc, argv, envp, and auxiliary vectors.
        /// This mirrors the Linux kernel's stack layout for ELF executables,
        /// as described in the kernel source (fs/binfmt_elf.c: create_elf_tables).
        ///
        /// Stack layout (growing downward):
        ///   [padding/alignment]
        ///   [auxv strings]
        ///   [envp strings]
        ///   [argv strings]
        ///   [NULL]
        ///   [auxv entries]
        ///   [NULL]
        ///   [envp pointers]
        ///   [NULL]
        ///   [argv pointers]
        ///   [argc]           ← RSP points here
        /// </summary>
        private void SetupStack(ElfLoadResult loadResult, string[] argv, string[] envp)
        {
            ulong stackTop = StackBase;
            ulong stackBottom = stackTop - StackSize;

            // Map the stack
            _memory.Map(stackBottom, StackSize, MemoryProtection.ReadWrite);

            ulong sp = stackTop;

            // Write string data at top of stack and collect pointers
            ulong[] argvPtrs = new ulong[argv.Length];
            for (int i = 0; i < argv.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(argv[i] + "\0");
                sp -= (ulong)bytes.Length;
                _memory.Write(sp, bytes);
                argvPtrs[i] = sp;
            }

            ulong[] envpPtrs = new ulong[envp.Length];
            for (int i = 0; i < envp.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(envp[i] + "\0");
                sp -= (ulong)bytes.Length;
                _memory.Write(sp, bytes);
                envpPtrs[i] = sp;
            }

            // Write 16 bytes of random data for AT_RANDOM
            sp -= 16;
            ulong randomAddr = sp;
            var rng = new Random();
            byte[] randomBytes = new byte[16];
            rng.NextBytes(randomBytes);
            _memory.Write(randomAddr, randomBytes);

            // Write "x86_64" platform string for AT_PLATFORM
            byte[] platformStr = Encoding.UTF8.GetBytes("x86_64\0");
            sp -= (ulong)platformStr.Length;
            ulong platformAddr = sp;
            _memory.Write(platformAddr, platformStr);

            // Align to 16 bytes
            sp &= ~0xFUL;
            ulong execfnAddr = argvPtrs.Length > 0 ? argvPtrs[0] : 0;

            // Auxiliary vector (from bottom up)
            // We'll build from top down, then copy
            // AT_HWCAP: x86_64 baseline hardware capabilities from CPUID.01H:EDX.
            // ld.so uses this for ifunc (indirect function) resolution to select
            // optimized implementations based on CPU features.
            const ulong X86_64_HWCAP = 0x078bfbffUL; // FPU+SSE+SSE2+CMOV+FXSR+MMX+...

            var auxv = new (ulong type, ulong value)[]
            {
                (ElfConstants.AT_PHDR, loadResult.ProgramHeaderAddress),
                (ElfConstants.AT_PHENT, loadResult.ProgramHeaderEntrySize),
                (ElfConstants.AT_PHNUM, loadResult.ProgramHeaderCount),
                (ElfConstants.AT_PAGESZ, 4096),
                (ElfConstants.AT_BASE, 0),
                (ElfConstants.AT_FLAGS, 0),
                (ElfConstants.AT_ENTRY, loadResult.EntryPoint),
                (ElfConstants.AT_HWCAP, X86_64_HWCAP),
                (ElfConstants.AT_UID, 1000),
                (ElfConstants.AT_EUID, 1000),
                (ElfConstants.AT_GID, 1000),
                (ElfConstants.AT_EGID, 1000),
                (ElfConstants.AT_CLKTCK, 100),
                (ElfConstants.AT_SECURE, 0),
                (ElfConstants.AT_PLATFORM, platformAddr),
                (ElfConstants.AT_RANDOM, randomAddr),
                (ElfConstants.AT_EXECFN, execfnAddr),
                (ElfConstants.AT_NULL, 0),
            };

            // Calculate total size needed below current sp
            int totalEntries = 1 + argv.Length + 1 + envp.Length + 1 + (auxv.Length * 2);
            sp -= (ulong)(totalEntries * 8);
            sp &= ~0xFUL; // Align to 16 bytes

            _cpu.RSP = sp;
            ulong ptr = sp;

            // argc
            _memory.WriteUInt64(ptr, (ulong)argv.Length);
            ptr += 8;

            // argv pointers
            for (int i = 0; i < argvPtrs.Length; i++)
            {
                _memory.WriteUInt64(ptr, argvPtrs[i]);
                ptr += 8;
            }
            _memory.WriteUInt64(ptr, 0); // NULL terminator
            ptr += 8;

            // envp pointers
            for (int i = 0; i < envpPtrs.Length; i++)
            {
                _memory.WriteUInt64(ptr, envpPtrs[i]);
                ptr += 8;
            }
            _memory.WriteUInt64(ptr, 0); // NULL terminator
            ptr += 8;

            // Auxiliary vectors
            foreach (var (type, value) in auxv)
            {
                _memory.WriteUInt64(ptr, type);
                ptr += 8;
                _memory.WriteUInt64(ptr, value);
                ptr += 8;
            }

            _logger($"Stack set up: RSP=0x{_cpu.RSP:X16}, argc={argv.Length}");
        }

        /// <summary>
        /// Set up the stack for a dynamically-linked binary with an interpreter.
        /// The key difference from SetupStack is that AT_ENTRY points to the
        /// original binary's entry point (not the interpreter's), and AT_BASE
        /// points to the interpreter's load base address.
        /// </summary>
        private void SetupStackWithInterpreter(ElfLoadResult loadResult, ulong realEntry,
                                                ulong interpBase, string[] argv, string[] envp)
        {
            ulong stackTop = StackBase;
            ulong stackBottom = stackTop - StackSize;

            _memory.Map(stackBottom, StackSize, MemoryProtection.ReadWrite);

            ulong sp = stackTop;

            ulong[] argvPtrs = new ulong[argv.Length];
            for (int i = 0; i < argv.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(argv[i] + "\0");
                sp -= (ulong)bytes.Length;
                _memory.Write(sp, bytes);
                argvPtrs[i] = sp;
            }

            ulong[] envpPtrs = new ulong[envp.Length];
            for (int i = 0; i < envp.Length; i++)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(envp[i] + "\0");
                sp -= (ulong)bytes.Length;
                _memory.Write(sp, bytes);
                envpPtrs[i] = sp;
            }

            sp -= 16;
            ulong randomAddr = sp;
            var rng = new Random();
            byte[] randomBytes = new byte[16];
            rng.NextBytes(randomBytes);
            _memory.Write(randomAddr, randomBytes);

            byte[] platformStr = Encoding.UTF8.GetBytes("x86_64\0");
            sp -= (ulong)platformStr.Length;
            ulong platformAddr = sp;
            _memory.Write(platformAddr, platformStr);

            sp &= ~0xFUL;
            ulong execfnAddr = argvPtrs.Length > 0 ? argvPtrs[0] : 0;

            // AT_HWCAP: x86_64 baseline hardware capabilities from CPUID.01H:EDX.
            // ld.so uses this for ifunc (indirect function) resolution to select
            // optimized implementations based on CPU features.
            const ulong X86_64_HWCAP = 0x078bfbffUL; // FPU+SSE+SSE2+CMOV+FXSR+MMX+...

            // Auxiliary vector — AT_ENTRY is the real binary entry, AT_BASE is interpreter base
            var auxv = new (ulong type, ulong value)[]
            {
                (ElfConstants.AT_PHDR, loadResult.ProgramHeaderAddress),
                (ElfConstants.AT_PHENT, loadResult.ProgramHeaderEntrySize),
                (ElfConstants.AT_PHNUM, loadResult.ProgramHeaderCount),
                (ElfConstants.AT_PAGESZ, 4096),
                (ElfConstants.AT_BASE, interpBase),
                (ElfConstants.AT_FLAGS, 0),
                (ElfConstants.AT_ENTRY, realEntry),
                (ElfConstants.AT_HWCAP, X86_64_HWCAP),
                (ElfConstants.AT_UID, 1000),
                (ElfConstants.AT_EUID, 1000),
                (ElfConstants.AT_GID, 1000),
                (ElfConstants.AT_EGID, 1000),
                (ElfConstants.AT_CLKTCK, 100),
                (ElfConstants.AT_SECURE, 0),
                (ElfConstants.AT_PLATFORM, platformAddr),
                (ElfConstants.AT_RANDOM, randomAddr),
                (ElfConstants.AT_EXECFN, execfnAddr),
                (ElfConstants.AT_NULL, 0),
            };

            int totalEntries = 1 + argv.Length + 1 + envp.Length + 1 + (auxv.Length * 2);
            sp -= (ulong)(totalEntries * 8);
            sp &= ~0xFUL;

            _cpu.RSP = sp;
            ulong ptr = sp;

            _memory.WriteUInt64(ptr, (ulong)argv.Length);
            ptr += 8;

            for (int i = 0; i < argvPtrs.Length; i++)
            {
                _memory.WriteUInt64(ptr, argvPtrs[i]);
                ptr += 8;
            }
            _memory.WriteUInt64(ptr, 0);
            ptr += 8;

            for (int i = 0; i < envpPtrs.Length; i++)
            {
                _memory.WriteUInt64(ptr, envpPtrs[i]);
                ptr += 8;
            }
            _memory.WriteUInt64(ptr, 0);
            ptr += 8;

            foreach (var (type, value) in auxv)
            {
                _memory.WriteUInt64(ptr, type);
                ptr += 8;
                _memory.WriteUInt64(ptr, value);
                ptr += 8;
            }

            _logger($"Stack set up (with interp): RSP=0x{_cpu.RSP:X16}, " +
                    $"argc={argv.Length}, AT_ENTRY=0x{realEntry:X16}, AT_BASE=0x{interpBase:X16}, " +
                    $"AT_PHDR=0x{loadResult.ProgramHeaderAddress:X16}, AT_EXECFN=0x{execfnAddr:X16}");
        }

        private static string[] GetDefaultEnvironment()
        {
            return new[]
            {
                "PATH=/usr/local/bin:/usr/bin:/bin",
                "HOME=/root",
                "TERM=xterm-256color",
                "LANG=en_US.UTF-8",
                "USER=user",
                "SHELL=/bin/sh",
                "COLUMNS=80",
                "LINES=24",
            };
        }

        private static string FormatBytes(byte[] data)
        {
            if (data.Length == 0)
                return "(none)";

            int count = Math.Min(data.Length, 16);
            var parts = new string[count];
            for (int i = 0; i < count; i++)
                parts[i] = data[i].ToString("X2");
            return string.Join(" ", parts);
        }

        private static string FormatRecentBlocks(IEnumerable<ulong> addresses)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (ulong address in addresses)
            {
                if (!first)
                    sb.Append(" | ");
                first = false;
                sb.Append($"0x{address:X16}");
            }
            return sb.ToString();
        }

        private string DescribeRecentBlocks(IEnumerable<ulong> addresses)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (ulong address in addresses)
            {
                if (!first)
                    sb.Append(" || ");
                first = false;
                sb.Append(_translator.DescribeBlock(address, 6));
            }
            return sb.ToString();
        }
    }
}
